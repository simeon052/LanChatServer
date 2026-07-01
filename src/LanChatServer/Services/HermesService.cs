using System.Collections.Concurrent;
using System.Net.WebSockets;
using LanChatServer.Config;

namespace LanChatServer.Services;

/// <summary>
/// ConPTY 経由で Hermes Agent を起動し、WebSocket ターミナルとして公開する。
/// prompt_toolkit の NoConsoleScreenBufferError を回避するため stdin/stdout リダイレクトは使わない。
/// </summary>
public sealed class HermesSession : IAsyncDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    private readonly ConPtyProcess _pty;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WebSocket> _clients = [];
    private readonly SemaphoreSlim _clientsLock = new(1, 1);
    private readonly Task _broadcastTask;

    public bool IsRunning => !_pty.HasExited;

    public HermesSession(ConPtyProcess pty)
    {
        _pty = pty;
        _broadcastTask = BroadcastLoopAsync(_cts.Token);
    }

    /// <summary>WebSocket クライアントのターミナルセッションを処理する。</summary>
    public async Task HandleWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        await _clientsLock.WaitAsync(ct);
        _clients.Add(ws);
        _clientsLock.Release();

        var buf = new byte[1024];
        try
        {
            // WS からのキー入力を ConPTY へ流す
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try { result = await ws.ReceiveAsync(buf, ct); }
                catch { break; }

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0)
                {
                    try { await _pty.Input.WriteAsync(buf.AsMemory(0, result.Count), ct); }
                    catch { break; }
                }
            }
        }
        finally
        {
            await _clientsLock.WaitAsync(CancellationToken.None);
            _clients.Remove(ws);
            _clientsLock.Release();
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
    }

    /// <summary>ConPTY 出力を全 WebSocket クライアントにブロードキャストする。</summary>
    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try { n = await _pty.Output.ReadAsync(buf, ct); }
                catch { break; }
                if (n == 0) break;

                var data = new ArraySegment<byte>(buf, 0, n);

                await _clientsLock.WaitAsync(CancellationToken.None);
                var snapshot = _clients.ToList();
                _clientsLock.Release();

                foreach (var ws in snapshot)
                {
                    if (ws.State != WebSocketState.Open) continue;
                    try { await ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None); }
                    catch { }
                }
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _broadcastTask; } catch { }

        await _clientsLock.WaitAsync(CancellationToken.None);
        var snapshot = _clients.ToList();
        _clients.Clear();
        _clientsLock.Release();

        foreach (var ws in snapshot)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { }
        }

        await _pty.DisposeAsync();
        _cts.Dispose();
        _clientsLock.Dispose();
    }
}

public sealed class HermesService : IAsyncDisposable
{
    private readonly HermesConfig _config;
    private readonly ConcurrentDictionary<string, HermesSession> _sessions = new();

    public HermesService(HermesConfig config) => _config = config;

    public Task<(HermesSession? Session, string? Error)> CreateSessionAsync(CancellationToken ct = default)
    {
        ConPtyProcess pty;
        try
        {
            pty = ConPtyProcess.Start(_config.Command);
        }
        catch (Exception ex)
        {
            return Task.FromResult<(HermesSession?, string?)>((null, $"Hermes の起動に失敗しました: {ex.Message}"));
        }

        var session = new HermesSession(pty);
        _sessions[session.Id] = session;

        // 起動直後にプロセスが即死していないか確認
        Task.Delay(800, ct).ContinueWith(t =>
        {
            if (!session.IsRunning)
                _sessions.TryRemove(session.Id, out var removed);
        }, TaskContinuationOptions.ExecuteSynchronously);

        return Task.FromResult<(HermesSession?, string?)>((session, null));
    }

    public IReadOnlyList<HermesSession> ListSessions() =>
        [.. _sessions.Values.OrderByDescending(s => s.CreatedAt)];

    public HermesSession? GetSession(string id) => _sessions.GetValueOrDefault(id);

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
