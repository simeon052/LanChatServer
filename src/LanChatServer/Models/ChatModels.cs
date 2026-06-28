namespace LanChatServer.Models;

public sealed record ChatMessage(
    [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
    [property: System.Text.Json.Serialization.JsonPropertyName("content")] string Content);

// ---- Request / Response DTOs ----

public sealed class CreateSessionRequest
{
    public string Backend { get; set; } = "claude";
    public string? Model { get; set; }
    public string? SystemPrompt { get; set; }
}

public sealed class SendMessageRequest
{
    public string Content { get; set; } = "";
}

public class SessionInfoDto
{
    public string Id { get; set; } = "";
    public string Backend { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int MessageCount { get; set; }
    public bool IsActive { get; set; }
}

public sealed class SessionDetailDto : SessionInfoDto
{
    public List<ChatMessage> Messages { get; set; } = [];
    public string SystemPrompt { get; set; } = "";
}
