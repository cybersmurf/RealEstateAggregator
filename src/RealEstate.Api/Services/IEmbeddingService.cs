namespace RealEstate.Api.Services;

public interface IEmbeddingService
{
    /// <summary>Vygeneruje embedding pro text. Vrátí null pokud není OpenAI klíč nebo dojde k chybě.</summary>
    Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct = default);

    /// <summary>Pošle systémový prompt + uživatelský dotaz do OpenAI Chat API a vrátí odpověď.</summary>
    Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    bool IsConfigured { get; }
}
