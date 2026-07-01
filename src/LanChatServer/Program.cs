using System.Net;
using System.Text;
using System.Text.Json;
using LanChatServer.Config;
using LanChatServer.Models;
using LanChatServer.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var serverCfg = builder.Configuration.GetSection("Server").Get<ServerConfig>() ?? new();
var claudeCfg = builder.Configuration.GetSection("Claude").Get<ClaudeConfig>() ?? new();
var hermesCfg = builder.Configuration.GetSection("Hermes").Get<HermesConfig>() ?? new();

builder.WebHost.UseUrls($"http://{serverCfg.Host}:{serverCfg.Port}");

builder.Services.AddSingleton(claudeCfg);
builder.Services.AddSingleton(hermesCfg);
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<HermesService>();
builder.Services.AddSingleton<ClaudeCodeImporter>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

// コンソール出力ヘルパー
static void Log(string level, string msg)
{
    var color = level switch
    {
        "INFO"  => ConsoleColor.Cyan,
        "OK"    => ConsoleColor.Green,
        "WARN"  => ConsoleColor.Yellow,
        "ERROR" => ConsoleColor.Red,
        _       => ConsoleColor.Gray,
    };
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
    Console.ForegroundColor = color;
    Console.Write($"[{level}] ");
    Console.ResetColor();
    Console.WriteLine(msg);
}

static string Clip(string? s, int max = 50) =>
    s is null ? "" : s.Length <= max ? s : s[..max] + "…";

// ---- ヘルスチェック ----
app.MapGet("/healthz", (HttpContext ctx) =>
{
    Log("INFO", $"Healthz from {ctx.Connection.RemoteIpAddress}");
    return Results.Ok(new { status = "ok", time = DateTimeOffset.Now });
});

// ---- セッション一覧 ----
app.MapGet("/api/sessions", (ClaudeService claude, HermesService hermes) =>
{
    var list = new List<SessionInfoDto>();
    foreach (var s in claude.ListSessions())
        list.Add(new SessionInfoDto { Id = s.Id, Backend = "claude", Name = $"Claude ({s.Model})", CreatedAt = s.CreatedAt.ToString("o"), MessageCount = s.Messages.Count, IsActive = true });
    foreach (var s in hermes.ListSessions())
        list.Add(new SessionInfoDto { Id = s.Id, Backend = "hermes", Name = "Hermes Agent", CreatedAt = s.CreatedAt.ToString("o"), MessageCount = 0, IsActive = s.IsRunning });
    list.Sort((a, b) => string.Compare(b.CreatedAt, a.CreatedAt, StringComparison.Ordinal));
    return Results.Json(list, jsonOpts);
});

// ---- セッション作成 ----
app.MapPost("/api/sessions", async (CreateSessionRequest req, ClaudeService claude, HermesService hermes,
    HttpContext ctx, CancellationToken ct) =>
{
    if (req.Backend == "hermes")
    {
        Log("INFO", $"Hermes セッション作成 (from {ctx.Connection.RemoteIpAddress})");
        var (session, error) = await hermes.CreateSessionAsync(ct);
        if (session is null)
        {
            Log("ERROR", $"Hermes 起動失敗: {error}");
            return Results.Json(new { error }, jsonOpts, statusCode: 500);
        }
        Log("OK", $"Hermes セッション開始 [{session.Id}]");
        return Results.Json(new SessionInfoDto { Id = session.Id, Backend = "hermes", Name = "Hermes Agent", CreatedAt = session.CreatedAt.ToString("o"), IsActive = true }, jsonOpts, statusCode: 201);
    }
    else
    {
        var session = claude.CreateSession(req.Model, req.SystemPrompt);
        Log("OK", $"Claude セッション作成 [{session.Id}] model={session.Model} (from {ctx.Connection.RemoteIpAddress})");
        return Results.Json(new SessionDetailDto { Id = session.Id, Backend = "claude", Name = $"Claude ({session.Model})", CreatedAt = session.CreatedAt.ToString("o"), IsActive = true, SystemPrompt = session.SystemPrompt, Messages = [] }, jsonOpts, statusCode: 201);
    }
});

// ---- セッション詳細 ----
app.MapGet("/api/sessions/{id}", (string id, ClaudeService claude, HermesService hermes) =>
{
    var cs = claude.GetSession(id);
    if (cs is not null)
        return Results.Json(new SessionDetailDto { Id = cs.Id, Backend = "claude", Name = $"Claude ({cs.Model})", CreatedAt = cs.CreatedAt.ToString("o"), MessageCount = cs.Messages.Count, IsActive = true, SystemPrompt = cs.SystemPrompt, Messages = cs.Messages }, jsonOpts);
    var hs = hermes.GetSession(id);
    if (hs is not null)
        return Results.Json(new SessionDetailDto { Id = hs.Id, Backend = "hermes", Name = "Hermes Agent", CreatedAt = hs.CreatedAt.ToString("o"), IsActive = hs.IsRunning, Messages = [] }, jsonOpts);
    return Results.NotFound(new { error = "セッションが見つかりません" });
});

// ---- セッション削除 ----
app.MapDelete("/api/sessions/{id}", async (string id, ClaudeService claude, HermesService hermes, HttpContext ctx) =>
{
    if (claude.DeleteSession(id))
    {
        Log("OK", $"Claude セッション削除 [{id}] (from {ctx.Connection.RemoteIpAddress})");
        return Results.Ok(new { status = "ok" });
    }
    if (await hermes.DeleteSessionAsync(id))
    {
        Log("OK", $"Hermes セッション削除 [{id}] (from {ctx.Connection.RemoteIpAddress})");
        return Results.Ok(new { status = "ok" });
    }
    return Results.NotFound(new { error = "セッションが見つかりません" });
});

// ---- メッセージ送信(SSEストリーミング) ----
app.MapPost("/api/sessions/{id}/messages", async (string id, SendMessageRequest req, HttpContext ctx,
    ClaudeService claude, HermesService hermes) =>
{
    if (string.IsNullOrWhiteSpace(req.Content))
    {
        await Results.Json(new { error = "content が必要です" }, jsonOpts, statusCode: 400).ExecuteAsync(ctx);
        return;
    }

    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    async Task WriteSseAsync(string text, bool done = false)
    {
        var payload = done
            ? JsonSerializer.Serialize(new { done = true }, jsonOpts)
            : JsonSerializer.Serialize(new { text }, jsonOpts);
        await ctx.Response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8);
        await ctx.Response.Body.FlushAsync();
    }

    var ct = ctx.RequestAborted;
    var from = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    var cs = claude.GetSession(id);
    if (cs is not null)
    {
        Log("INFO", $"Claude [{id}] ← \"{Clip(req.Content)}\" (from {from})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int chars = 0;
        await foreach (var chunk in claude.ChatStreamAsync(id, req.Content, ct))
        {
            chars += chunk.Length;
            await WriteSseAsync(chunk);
        }
        await WriteSseAsync("", done: true);
        Log("OK", $"Claude [{id}] → {chars}文字 ({sw.ElapsedMilliseconds}ms)");
        return;
    }

    var hs = hermes.GetSession(id);
    if (hs is not null)
    {
        await Results.Json(new { error = "Hermes はターミナル(WebSocket)でのみ操作できます。/terminal エンドポイントを使用してください。" }, jsonOpts, statusCode: 400).ExecuteAsync(ctx);
        return;
    }

    await Results.Json(new { error = "セッションが見つかりません" }, jsonOpts, statusCode: 404).ExecuteAsync(ctx);
});

// ---- Hermes ターミナル (WebSocket) ----
app.Map("/api/sessions/{id}/terminal", async (string id, HermesService hermes, HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket 接続が必要です");
        return;
    }

    var session = hermes.GetSession(id);
    if (session is null)
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    var from = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    Log("INFO", $"Hermes WS 接続 [{id}] (from {from})");

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await session.HandleWebSocketAsync(ws, ctx.RequestAborted);

    Log("OK", $"Hermes WS 切断 [{id}] (from {from})");
});

// ---- Claude Code セッション一覧 ----
app.MapGet("/api/claude-code/sessions", (ClaudeCodeImporter importer, HttpContext ctx) =>
{
    Log("INFO", $"Claude Code セッション一覧取得 (from {ctx.Connection.RemoteIpAddress})");
    var sessions = importer.ListSessions().Select(s => new
    {
        id = s.Id,
        projectDir = s.ProjectDir,
        title = s.Title,
        lastModified = s.LastModified.ToString("o"),
        messageCount = s.MessageCount,
    });
    return Results.Json(sessions, jsonOpts);
});

// ---- Claude Code セッションをインポート ----
app.MapPost("/api/claude-code/sessions/{id}/import", (string id, ClaudeCodeImporter importer, ClaudeService claude, HttpContext ctx) =>
{
    Log("INFO", $"Claude Code インポート [{id}] (from {ctx.Connection.RemoteIpAddress})");
    List<ChatMessage> messages;
    try { messages = importer.ImportMessages(id); }
    catch (FileNotFoundException)
    {
        Log("ERROR", $"セッションファイルが見つかりません: {id}");
        return Results.NotFound(new { error = "セッションファイルが見つかりません" });
    }

    if (messages.Count == 0)
    {
        Log("WARN", $"空のセッション: {id}");
        return Results.Json(new { error = "会話が空のセッションです" }, jsonOpts, statusCode: 400);
    }

    var session = claude.ImportFromClaudeCode(messages);
    Log("OK", $"インポート完了 [{id}] → LanChat [{session.Id}] ({messages.Count}件)");
    return Results.Json(new SessionDetailDto
    {
        Id = session.Id,
        Backend = "claude",
        Name = $"[CC] {Clip(session.Messages.FirstOrDefault()?.Content, 40)}…",
        CreatedAt = session.CreatedAt.ToString("o"),
        MessageCount = session.Messages.Count,
        IsActive = true,
        SystemPrompt = session.SystemPrompt,
        Messages = session.Messages,
    }, jsonOpts, statusCode: 201);
});

// ---- 起動メッセージ ----
var localIp = GetLocalIp();
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║         LanChatServer 起動           ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine($"  PC:     http://localhost:{serverCfg.Port}");
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"  スマホ: http://{localIp}:{serverCfg.Port}");
Console.ResetColor();
Console.WriteLine($"  Claude: {(string.IsNullOrWhiteSpace(claudeCfg.ApiKey) || claudeCfg.ApiKey == "YOUR_ANTHROPIC_API_KEY" ? "⚠ APIキー未設定" : $"✓ {claudeCfg.DefaultModel}")}");
Console.WriteLine($"  Hermes: {hermesCfg.Command}");
Console.WriteLine("  Ctrl+C で終了");
Console.WriteLine();

app.Run();

static string GetLocalIp()
{
    try
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        return host.AddressList
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?.ToString() ?? "0.0.0.0";
    }
    catch { return "0.0.0.0"; }
}
