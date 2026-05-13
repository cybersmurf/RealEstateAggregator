using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealEstate.Api.Services;

/// <summary>
/// Embedding + chat service.
/// Embedding: nomic-embed-text (768 dim) přes lokální Ollama – beze změny.
/// Chat: mistral-small-2506 přes Mistral API (dříve qwen2.5:14b přes Ollama).
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _embedModel;
    private readonly string _mistralChatModel;
    private readonly string _mistralApiKey;
    private readonly int _dimensions;
    private readonly ILogger<OllamaEmbeddingService> _logger;

    public bool IsConfigured { get; }

    public OllamaEmbeddingService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<OllamaEmbeddingService> logger)
    {
        _logger = logger;
        _httpFactory = httpFactory;
        _embedModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        _mistralChatModel = config["Mistral:ChatModel"] ?? "mistral-small-2506";
        _mistralApiKey = config["Mistral:ApiKey"] ?? string.Empty;
        _dimensions = int.TryParse(config["Embedding:VectorDimensions"], out var d) ? d : 768;

        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http = httpFactory.CreateClient("Ollama");
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(5);

        IsConfigured = true;
        logger.LogInformation(
            "OllamaEmbeddingService configured (base={Base}, embed={Embed}, mistral-chat={Chat}, dim={Dim})",
            baseUrl, _embedModel, _mistralChatModel, _dimensions);
    }

    // ─── Embedding ────────────────────────────────────────────────────────────

    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var truncated = text.Length > 8000 ? text[..8000] : text;

            // Ollama /api/embed (0.5+) – single call, returns array of embeddings
            var request = new { model = _embedModel, input = truncated };
            var resp = await _http.PostAsJsonAsync("api/embed", request, ct);
            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<OllamaEmbedResponse>(
                cancellationToken: ct);

            var embedding = result?.Embeddings?.FirstOrDefault();
            if (embedding is null)
            {
                _logger.LogWarning("Ollama returned empty embedding for text len={Len}", text.Length);
                return null;
            }

            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama embedding failed (model={Model})", _embedModel);
            return null;
        }
    }

    // ─── Chat (Mistral API) ───────────────────────────────────────────────────

    public async Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct = default, bool jsonMode = false)
    {
        try
        {
            var requestNode = new System.Text.Json.Nodes.JsonObject
            {
                ["model"]  = _mistralChatModel,
                ["messages"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                    new System.Text.Json.Nodes.JsonObject { ["role"] = "user",   ["content"] = userMessage  }
                )
            };
            if (jsonMode)
                requestNode["response_format"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "json_object" };

            using var http = _httpFactory.CreateClient("MistralChat");
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _mistralApiKey);

            using var body = new StringContent(
                requestNode.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(
                "https://api.mistral.ai/v1/chat/completions", body, ct);
            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<MistralChatResponse>(
                cancellationToken: ct);

            return result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim()
                ?? "[Mistral vrátilo prázdnou odpověď]";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mistral chat failed (model={Model})", _mistralChatModel);
            throw;
        }
    }

    // ─── Response DTOs ────────────────────────────────────────────────────────

    private sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; set; }
    }

    // Mistral chat/completions response (stejný formát jako OpenAI)
    private sealed class MistralChatResponse
    {
        [JsonPropertyName("choices")]
        public List<MistralChoice>? Choices { get; set; }
    }

    private sealed class MistralChoice
    {
        [JsonPropertyName("message")]
        public MistralMessage? Message { get; set; }
    }

    private sealed class MistralMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
