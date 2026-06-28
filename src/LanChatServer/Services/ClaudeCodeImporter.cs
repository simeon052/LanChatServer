using System.Text;
using System.Text.Json;
using LanChatServer.Models;

namespace LanChatServer.Services;

/// <summary>~/.claude/projects/ の JSONL ファイルを読み込んで LanChatServer セッションに変換する。</summary>
public sealed class ClaudeCodeImporter
{
    private static readonly string ProjectsRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public sealed record ClaudeCodeSession(
        string Id,          // UUIDのみ (拡張子なし)
        string FilePath,
        string ProjectDir,  // ~/.claude/projects/ 直下のフォルダ名
        string Title,       // 最初のユーザーメッセージ(先頭60文字)
        DateTimeOffset LastModified,
        int MessageCount);

    /// <summary>全プロジェクトの Claude Code セッション一覧。</summary>
    public IReadOnlyList<ClaudeCodeSession> ListSessions()
    {
        if (!Directory.Exists(ProjectsRoot)) return [];

        var result = new List<ClaudeCodeSession>();
        foreach (var projectDir in Directory.EnumerateDirectories(ProjectsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var modified = File.GetLastWriteTime(file);
                var (title, count) = PeekSession(file);
                if (count == 0) continue; // 空セッションはスキップ
                result.Add(new ClaudeCodeSession(
                    Id: id,
                    FilePath: file,
                    ProjectDir: Path.GetFileName(projectDir),
                    Title: title,
                    LastModified: modified,
                    MessageCount: count));
            }
        }
        result.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
        return result;
    }

    /// <summary>セッションをタイトルと会話数だけ取得（全行を読まない簡易版）。</summary>
    private static (string Title, int Count) PeekSession(string path)
    {
        string title = "";
        int count = 0;
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement doc;
                try { doc = JsonSerializer.Deserialize<JsonElement>(line); } catch { continue; }
                if (!doc.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                if (type is not ("user" or "assistant")) continue;
                count++;
                if (title == "" && type == "user")
                    title = ExtractUserText(doc)?[..Math.Min(ExtractUserText(doc)?.Length ?? 0, 60)] ?? "";
            }
        }
        catch { }
        return (title, count);
    }

    /// <summary>JSONL ファイルを解析して ChatMessage リストを返す。</summary>
    public List<ChatMessage> ImportMessages(string sessionId)
    {
        var file = FindFile(sessionId)
            ?? throw new FileNotFoundException($"Session {sessionId} not found");
        return ParseMessages(file);
    }

    private string? FindFile(string sessionId)
    {
        if (!Directory.Exists(ProjectsRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
        {
            var path = Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static List<ChatMessage> ParseMessages(string path)
    {
        var messages = new List<ChatMessage>();
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement doc;
                try { doc = JsonSerializer.Deserialize<JsonElement>(line); } catch { continue; }
                if (!doc.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();

                if (type == "user")
                {
                    var text = ExtractUserText(doc);
                    if (!string.IsNullOrWhiteSpace(text))
                        messages.Add(new ChatMessage("user", text));
                }
                else if (type == "assistant")
                {
                    var text = ExtractAssistantText(doc);
                    if (!string.IsNullOrWhiteSpace(text))
                        messages.Add(new ChatMessage("assistant", text));
                }
            }
        }
        catch { }
        return messages;
    }

    private static string? ExtractUserText(JsonElement doc)
    {
        if (!doc.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        // content は文字列の場合と配列の場合がある
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());
            }
            return sb.ToString();
        }
        return null;
    }

    private static string? ExtractAssistantText(JsonElement doc)
    {
        if (!doc.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            // text ブロックのみ抽出。thinking / tool_use / tool_result はスキップ
            if (!block.TryGetProperty("type", out var t)) continue;
            if (t.GetString() != "text") continue;
            if (block.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } text)
                sb.Append(text);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
