namespace RealEstate.Api.Services;

public interface IEmbeddingService
{
    /// <summary>Vygeneruje embedding pro text. Vrátí null pokud není OpenAI klíč nebo dojde k chybě.</summary>
    Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct = default);

    /// <summary>Pošle systémový prompt + uživatelský dotaz do chat API a vrátí odpověď.</summary>
    /// <param name="jsonMode">Pokud true, vynutí JSON-only výstup (Ollama format=json / OpenAI json_object).</param>
    Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct = default, bool jsonMode = false);

    bool IsConfigured { get; }
}
