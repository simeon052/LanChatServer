using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LanChatServer.Config;

namespace LanChatServer.Services;

public sealed class HermesSession : IAsyncDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    private readonly Process _process;
    // stdout + stderr を統合するチャネル
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });
    // 同時に1会話のみ許可
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool IsRunning => !_process.HasExited;

    public HermesSession(Process process)
    {
        _process = process;
        _ = PipeAsync(process.StandardOutput);
        _ = PipeAsync(process.StandardError);
        _ = WatchExitAsync();
    }

    private async Task PipeAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                _output.Writer.TryWrite(line);
        }
        catch { }
    }

    private async Task WatchExitAsync()
    {
        try { await _process.WaitForExitAsync(); } catch { }
        _output.Writer.TryComplete();
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string message,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // 以前の残留出力をクリア
            while (_output.Reader.TryRead(out _)) { }

            await _process.StandardInput.WriteLineAsync(message.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);

            // タイムアウトベースで応答を収集: timeoutMs 間無音 → 応答完了と判断
            while (!ct.IsCancellationRequested)
            {
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                bool hasData;
                try
                {
                    hasData = await _output.Reader.WaitToReadAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    break; // タイムアウト or ユーザーキャンセル
                }

                if (!hasData) break; // プロセス終了でチャネル完了

                while (_output.Reader.TryRead(out var line))
                    yield return line + "\n";
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch { }
        _output.Writer.TryComplete();
        Gate.Dispose();
        _process.Dispose();
    }
}

public sealed class HermesService : IAsyncDisposable
{
    private readonly HermesConfig _config;
    private readonly ConcurrentDictionary<string, HermesSession> _sessions = new();

    public HermesService(HermesConfig config) => _config = config;

    public async Task<(HermesSession? Session, string? Error)> CreateSessionAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            Arguments = _config.Arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("プロセスの起動に失敗しました。");
        }
        catch (Exception ex)
        {
            return (null, $"Hermes の起動に失敗しました: {ex.Message}");
        }

        var session = new HermesSession(process);
        _sessions[session.Id] = session;

        // 起動確認のため少し待つ
        await Task.Delay(600, ct);
        if (!session.IsRunning)
        {
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync();
            return (null, "Hermes プロセスが即座に終了しました。Command パスを確認してください。");
        }

        return (session, null);
    }

    public IReadOnlyList<HermesSession> ListSessions() =>
        [.. _sessions.Values.OrderByDescending(s => s.CreatedAt)];

    public HermesSession? GetSession(string id) => _sessions.GetValueOrDefault(id);

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string sessionId,
        string message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            yield return "[ERROR] セッションが見つかりません";
            yield break;
        }
        if (!session.IsRunning)
        {
            yield return "[ERROR] Hermes プロセスが終了しています。セッションを作り直してください。";
            yield break;
        }

        await foreach (var chunk in session.StreamResponseAsync(message, _config.ResponseTimeoutMs, ct))
            yield return chunk;
    }

    public async Task<bool> DeleteSessionAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return false;
        await session.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
    }
}
