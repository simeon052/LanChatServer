namespace LanChatServer.Config;

public sealed class ServerConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5050;
}

public sealed class ClaudeConfig
{
    public string ApiKey { get; set; } = "";
    public string DefaultModel { get; set; } = "claude-sonnet-4-6";
    public int MaxTokens { get; set; } = 8192;
    public string DefaultSystemPrompt { get; set; } = "You are a helpful assistant.";
}

public sealed class HermesConfig
{
    public string Command { get; set; } = "hermes";
    public string Arguments { get; set; } = "";
    public int ResponseTimeoutMs { get; set; } = 1500;
}
