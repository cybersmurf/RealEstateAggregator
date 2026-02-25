using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealEstate.Api.Services;

/// <summary>
/// Embedding + chat service nad lokální Ollama instancí.
/// Embedding: nomic-embed-text (768 dim) – nebo libovolný embed model z Ollama.
/// Chat: qwen2.5:14b nebo libovolný chat model.
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly string _embedModel;
    private readonly string _chatModel;
    private readonly int _dimensions;
    private readonly ILogger<OllamaEmbeddingService> _logger;

    public bool IsConfigured { get; }

    public OllamaEmbeddingService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<OllamaEmbeddingService> logger)
    {
        _logger = logger;
        _embedModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        _chatModel = config["Ollama:ChatModel"] ?? "qwen2.5:14b";
        _dimensions = int.TryParse(config["Embedding:VectorDimensions"], out var d) ? d : 768;

        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http = httpFactory.CreateClient("Ollama");
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(5);

        IsConfigured = true;
        logger.LogInformation(
            "Ollama service configured (base={Base}, embed={Embed}, chat={Chat}, dim={Dim})",
            baseUrl, _embedModel, _chatModel, _dimensions);
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

    // ─── Chat ─────────────────────────────────────────────────────────────────

    public async Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        try
        {
            var request = new
            {
                model = _chatModel,
                stream = false,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userMessage  }
                }
            };

            var resp = await _http.PostAsJsonAsync("api/chat", request, ct);
            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<OllamaChatResponse>(
                cancellationToken: ct);

            return result?.Message?.Content
                ?? "[Ollama vrátilo prázdnou odpověď]";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama chat failed (model={Model})", _chatModel);
            throw;
        }
    }

    // ─── Response DTOs ────────────────────────────────────────────────────────

    private sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; set; }
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
