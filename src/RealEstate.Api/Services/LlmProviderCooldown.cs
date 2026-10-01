namespace RealEstate.Api.Services;

/// <summary>
/// Dočasné vyřazení LLM poskytovatele, který vrátil 401/402/403 (došel kredit, neplatný klíč).
/// Bez toho šlo každé volání nejdřív na mrtvý Mistral (1. 10. 2026: 40 tisíc chyb za den
/// z nočního generování shrnutí) a teprve pak na zálohu.
/// </summary>
public sealed class LlmProviderCooldown(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Stavové kódy, po kterých nemá smysl poskytovatele chvíli zkoušet.</summary>
    public static bool IsAccountProblem(int statusCode) => statusCode is 401 or 402 or 403;

    public bool IsAvailable(string provider)
    {
        lock (_lock)
        {
            if (!_until.TryGetValue(provider, out var until)) return true;
            if (until > _time.GetUtcNow()) return false;
            _until.Remove(provider);
            return true;
        }
    }

    public void Suspend(string provider, TimeSpan? duration = null)
    {
        lock (_lock)
        {
            _until[provider] = _time.GetUtcNow() + (duration ?? DefaultCooldown);
        }
    }

    public DateTimeOffset? SuspendedUntil(string provider)
    {
        lock (_lock)
        {
            return _until.TryGetValue(provider, out var until) && until > _time.GetUtcNow() ? until : null;
        }
    }
}

/// <summary>Všichni nakonfigurovaní poskytovatelé chatu selhali nebo jsou dočasně vyřazení.</summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
