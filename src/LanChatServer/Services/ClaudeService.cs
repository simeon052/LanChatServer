using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanChatServer.Config;
using LanChatServer.Models;

namespace LanChatServer.Services;

public sealed class ClaudeSession
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; init; } = [];
}

public sealed class ClaudeService
{
    private readonly ClaudeConfig _config;
    private readonly AnthropicClient _client = new();
    private readonly ConcurrentDictionary<string, ClaudeSession> _sessions = new();
    private readonly string _storePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ClaudeService(ClaudeConfig config)
    {
        _config = config;
        _storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanChatServer", "sessions.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var sessions = JsonSerializer.Deserialize<List<ClaudeSession>>(
                File.ReadAllText(_storePath), JsonOpts) ?? [];
            foreach (var s in sessions)
                _sessions[s.Id] = s;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath,
                JsonSerializer.Serialize(_sessions.Values.ToList(), JsonOpts));
        }
        catch { }
    }

    public ClaudeSession CreateSession(string? model, string? systemPrompt)
    {
        var s = new ClaudeSession
        {
            Model = model ?? _config.DefaultModel,
            SystemPrompt = systemPrompt ?? _config.DefaultSystemPrompt,
        };
        _sessions[s.Id] = s;
        Save();
        return s;
    }

    public IReadOnlyList<ClaudeSession> ListSessions() =>
        [.. _sessions.Values.OrderByDescending(s => s.CreatedAt)];

    public ClaudeSession? GetSession(string id) => _sessions.GetValueOrDefault(id);

    public bool DeleteSession(string id)
    {
        var removed = _sessions.TryRemove(id, out _);
        if (removed) Save();
        return removed;
    }

    /// <summary>Claude Code セッションの会話履歴をインポートして新しいセッションを作成。</summary>
    public ClaudeSession ImportFromClaudeCode(List<ChatMessage> messages, string? model = null, string? systemPrompt = null)
    {
        var s = new ClaudeSession
        {
            Model = model ?? _config.DefaultModel,
            SystemPrompt = systemPrompt ?? _config.DefaultSystemPrompt,
        };
        s.Messages.AddRange(messages);
        _sessions[s.Id] = s;
        Save();
        return s;
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string sessionId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            yield return "[ERROR] セッションが見つかりません";
            yield break;
        }

        session.Messages.Add(new ChatMessage("user", userMessage));
        var sb = new System.Text.StringBuilder();

        await foreach (var chunk in _client.StreamAsync(
            _config.ApiKey, session.Model, session.SystemPrompt,
            session.Messages, _config.MaxTokens, ct))
        {
            sb.Append(chunk);
            yield return chunk;
        }

        if (sb.Length > 0)
        {
            session.Messages.Add(new ChatMessage("assistant", sb.ToString()));
            Save();
        }
    }
}
