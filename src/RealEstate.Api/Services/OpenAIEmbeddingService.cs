using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace RealEstate.Api.Services;

/// <summary>
/// Obaluje OpenAI API: generování embeddingů (text-embedding-3-small) a chat (gpt-4o-mini).
/// Pokud OpenAI:ApiKey není nakonfigurován, je IsConfigured=false a metody vracejí null/"[not configured]".
/// </summary>
public sealed class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly OpenAIClient? _client;
    private readonly string _embeddingModel;
    private readonly string _chatModel;
    private readonly ILogger<OpenAIEmbeddingService> _logger;

    public bool IsConfigured { get; }

    public OpenAIEmbeddingService(IConfiguration config, ILogger<OpenAIEmbeddingService> logger)
    {
        _logger = logger;
        _embeddingModel = config["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";
        _chatModel = config["OpenAI:ChatModel"] ?? "gpt-4o-mini";

        var apiKey = config["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new OpenAIClient(apiKey);
            IsConfigured = true;
            logger.LogInformation("OpenAI service configured (embedding={Model}, chat={Chat})", _embeddingModel, _chatModel);
        }
        else
        {
            IsConfigured = false;
            logger.LogWarning("OpenAI:ApiKey not set – embedding/RAG features disabled");
        }
    }

    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (_client is null) return null;

        try
        {
            // Truncate to ~8000 chars to stay within token limit
            var truncated = text.Length > 8000 ? text[..8000] : text;
            var embClient = _client.GetEmbeddingClient(_embeddingModel);
            var result = await embClient.GenerateEmbeddingAsync(truncated, cancellationToken: ct);
            return result.Value.ToFloats().ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding (text length={Len})", text.Length);
            return null;
        }
    }

    public async Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        if (_client is null)
            return "[RAG není dostupný – OpenAI API klíč není nakonfigurován.]";

        try
        {
            var chatClient = _client.GetChatClient(_chatModel);
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userMessage)
            };
            var response = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI chat failed");
            throw;
        }
    }
}
