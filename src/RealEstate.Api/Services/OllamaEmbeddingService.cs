using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RealEstate.Api.Services;

/// <summary>
/// Embedding + chat service.
/// Embedding: nomic-embed-text (768 dim) přes lokální Ollama – beze změny.
/// Chat: OpenAI-kompatibilní API s řetězcem záloh Mistral → OpenRouter → Groq → Ollama Cloud (dříve jen Mistral).
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _embedModel;
    private readonly int _dimensions;
    private readonly ILogger<OllamaEmbeddingService> _logger;
    private readonly IReadOnlyList<ChatProvider> _chatProviders;
    private readonly LlmProviderCooldown _cooldown;

    /// <summary>OpenAI-kompatibilní chat endpoint. Pořadí v seznamu = pořadí záloh.</summary>
    public sealed record ChatProvider(string Name, string Url, string ApiKey, string Model, IReadOnlyDictionary<string, string>? ExtraHeaders = null);

    public bool IsConfigured { get; }

    public OllamaEmbeddingService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<OllamaEmbeddingService> logger,
        LlmProviderCooldown? cooldown = null)
    {
        _logger = logger;
        _httpFactory = httpFactory;
        _cooldown = cooldown ?? new LlmProviderCooldown();
        _embedModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        _chatProviders = BuildChatProviders(config);
        _dimensions = int.TryParse(config["Embedding:VectorDimensions"], out var d) ? d : 768;

        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http = httpFactory.CreateClient("Ollama");
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(5);

        IsConfigured = true;
        logger.LogInformation(
            "OllamaEmbeddingService configured (base={Base}, embed={Embed}, chat={Chat}, dim={Dim})",
            baseUrl, _embedModel, string.Join(" → ", _chatProviders.Select(p => $"{p.Name}:{p.Model}")), _dimensions);
    }

    /// <summary>
    /// Řetězec chat poskytovatelů: Mistral → OpenRouter → Groq → Ollama Cloud. Každý jen když má klíč.
    /// Mistral 1. 10. 2026 vracel 402 (vypršelo předplatné) a bez zálohy stála shrnutí, štítky,
    /// normalizace i cenový signál.
    /// </summary>
    public static IReadOnlyList<ChatProvider> BuildChatProviders(IConfiguration config)
    {
        var providers = new List<ChatProvider>();

        void Add(string name, string? apiKey, string url, string model, IReadOnlyDictionary<string, string>? headers = null)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                providers.Add(new ChatProvider(name, url, apiKey, model, headers));
        }

        Add("Mistral", config["Mistral:ApiKey"],
            "https://api.mistral.ai/v1/chat/completions",
            config["Mistral:ChatModel"] ?? "mistral-small-2506");

        var openRouterBase = (config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1").TrimEnd('/');
        Add("OpenRouter", config["OpenRouter:ApiKey"],
            $"{openRouterBase}/chat/completions",
            config["OpenRouter:ChatModel"] ?? "google/gemini-3.1-flash-lite",
            new Dictionary<string, string>
            {
                ["HTTP-Referer"] = config["OpenRouter:Referer"] ?? Environment.GetEnvironmentVariable("PUBLIC_API_URL") ?? "http://localhost:5001",
                ["X-Title"] = "RealEstateAggregator",
            });

        Add("Groq", config["Groq:ApiKey"],
            "https://api.groq.com/openai/v1/chat/completions",
            config["Groq:ChatModel"] ?? "openai/gpt-oss-120b");

        var ollamaCloudBase = (config["OllamaCloud:BaseUrl"] ?? "https://ollama.com/v1").TrimEnd('/');
        Add("OllamaCloud", config["OllamaCloud:ApiKey"],
            $"{ollamaCloudBase}/chat/completions",
            config["OllamaCloud:ChatModel"] ?? "gpt-oss:120b");

        return providers;
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

    // ─── Chat (OpenAI-kompatibilní API s řetězcem záloh) ─────────────────────

    public async Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct = default, bool jsonMode = false)
    {
        if (_chatProviders.Count == 0)
            throw new LlmUnavailableException("Není nakonfigurovaný žádný chat poskytovatel (Mistral/OpenRouter/Groq/OllamaCloud ApiKey).");

        Exception? last = null;
        var skipped = new List<string>();

        foreach (var provider in _chatProviders)
        {
            if (!_cooldown.IsAvailable(provider.Name))
            {
                skipped.Add(provider.Name);
                continue;
            }

            try
            {
                var answer = await ChatWithProviderAsync(provider, systemPrompt, userMessage, jsonMode, ct);
                if (skipped.Count > 0 || !ReferenceEquals(provider, _chatProviders[0]))
                    _logger.LogInformation("LLM chat: odpověděl {Provider} ({Model}), přeskočeno: {Skipped}",
                        provider.Name, provider.Model, string.Join(", ", skipped));
                return answer;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } code && LlmProviderCooldown.IsAccountProblem((int)code))
            {
                _cooldown.Suspend(provider.Name);
                _logger.LogWarning("LLM chat: {Provider} vrátil {Status} (klíč/kredit) – vyřazen na {Minutes} min, zkouším zálohu",
                    provider.Name, (int)code, LlmProviderCooldown.DefaultCooldown.TotalMinutes);
                last = ex;
                skipped.Add(provider.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM chat: {Provider} ({Model}) selhal, zkouším zálohu", provider.Name, provider.Model);
                last = ex;
                skipped.Add(provider.Name);
            }
        }

        throw new LlmUnavailableException(
            $"Žádný chat poskytovatel neodpověděl ({string.Join(", ", skipped)}).", last);
    }

    private async Task<string> ChatWithProviderAsync(ChatProvider provider, string systemPrompt, string userMessage, bool jsonMode, CancellationToken ct)
    {
        var requestNode = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = provider.Model,
            ["messages"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new System.Text.Json.Nodes.JsonObject { ["role"] = "user",   ["content"] = userMessage  }
            )
        };
        if (jsonMode)
            requestNode["response_format"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "json_object" };

        using var http = _httpFactory.CreateClient("MistralChat");
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.Url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        if (provider.ExtraHeaders is not null)
            foreach (var (key, value) in provider.ExtraHeaders)
                request.Headers.TryAddWithoutValidation(key, value);
        request.Content = new StringContent(requestNode.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"{provider.Name} HTTP {(int)resp.StatusCode}: {(detail.Length > 300 ? detail[..300] : detail)}",
                null, resp.StatusCode);
        }

        var result = await resp.Content.ReadFromJsonAsync<MistralChatResponse>(cancellationToken: ct);
        var content = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrEmpty(content))
            throw new InvalidOperationException($"{provider.Name} ({provider.Model}) vrátil prázdnou odpověď");
        return content;
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
