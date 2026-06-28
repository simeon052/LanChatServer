using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LanChatServer.Models;

namespace LanChatServer.Services;

/// <summary>Anthropic Messages API の低レベルHTTPクライアント。SSEストリーミング対応。</summary>
public sealed class AnthropicClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private const string ApiBase = "https://api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async IAsyncEnumerable<string> StreamAsync(
        string apiKey,
        string model,
        string? systemPrompt,
        IReadOnlyList<ChatMessage> messages,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_ANTHROPIC_API_KEY")
        {
            yield return "[ERROR] appsettings.json の Claude.ApiKey を設定してください。";
            yield break;
        }

        var body = new
        {
            model,
            max_tokens = maxTokens,
            system = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", ApiVersion);

        HttpResponseMessage? res = null;
        string? connectError = null;
        try
        {
            res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) { connectError = ex.Message; }

        if (connectError is not null)
        {
            yield return $"[ERROR] Anthropic API 接続失敗: {connectError}";
            yield break;
        }

        if (!res!.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            yield return $"[ERROR {(int)res.StatusCode}] {err}";
            res.Dispose();
            yield break;
        }

        using (res)
        {
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                JsonElement doc;
                try { doc = JsonSerializer.Deserialize<JsonElement>(data); }
                catch { continue; }

                if (!doc.TryGetProperty("type", out var typeEl)) continue;
                if (typeEl.GetString() != "content_block_delta") continue;

                if (doc.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("type", out var dt) &&
                    dt.GetString() == "text_delta" &&
                    delta.TryGetProperty("text", out var text) &&
                    text.GetString() is { Length: > 0 } chunk)
                {
                    yield return chunk;
                }
            }
        }
    }
}
