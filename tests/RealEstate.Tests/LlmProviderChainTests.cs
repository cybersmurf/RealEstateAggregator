using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Řetězec chat poskytovatelů a dočasné vyřazení po 401/402/403
//  (1. 10. 2026: Mistral vracel 402, 40 tisíc chyb za den, stál cenový signál i shrnutí)
// ─────────────────────────────────────────────────────────────────
public class LlmProviderCooldownTests
{
    [Fact]
    public void NewProvider_IsAvailable()
    {
        var cooldown = new LlmProviderCooldown(new FakeTimeProvider());

        Assert.True(cooldown.IsAvailable("Mistral"));
        Assert.Null(cooldown.SuspendedUntil("Mistral"));
    }

    [Fact]
    public void Suspend_BlocksUntilCooldownElapses()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero));
        var cooldown = new LlmProviderCooldown(time);

        cooldown.Suspend("Mistral");

        Assert.False(cooldown.IsAvailable("Mistral"));
        Assert.True(cooldown.IsAvailable("OpenRouter"));
        time.Advance(LlmProviderCooldown.DefaultCooldown - TimeSpan.FromSeconds(1));
        Assert.False(cooldown.IsAvailable("Mistral"));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(cooldown.IsAvailable("Mistral"));
    }

    [Fact]
    public void ProviderName_IsCaseInsensitive()
    {
        var cooldown = new LlmProviderCooldown(new FakeTimeProvider());
        cooldown.Suspend("mistral");

        Assert.False(cooldown.IsAvailable("Mistral"));
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(402, true)]
    [InlineData(403, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(404, false)]
    public void IsAccountProblem_OnlyAuthAndPayment(int status, bool expected)
        => Assert.Equal(expected, LlmProviderCooldown.IsAccountProblem(status));
}

public class LlmChatProviderChainTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void Order_MistralOpenRouterGroqOllamaCloud_OnlyWithKeys()
    {
        var providers = OllamaEmbeddingService.BuildChatProviders(Config(
            ("Mistral:ApiKey", "m"), ("OpenRouter:ApiKey", "o"), ("Groq:ApiKey", "g"), ("OllamaCloud:ApiKey", "c")));

        Assert.Equal(["Mistral", "OpenRouter", "Groq", "OllamaCloud"], providers.Select(p => p.Name));
        Assert.Equal("google/gemini-3.1-flash-lite", providers[1].Model);
        Assert.Equal("openai/gpt-oss-120b", providers[2].Model);
        Assert.Equal("https://ollama.com/v1/chat/completions", providers[3].Url);
    }

    [Fact]
    public void MissingKeys_ProviderSkipped()
    {
        var providers = OllamaEmbeddingService.BuildChatProviders(Config(("Groq:ApiKey", "g"), ("Mistral:ApiKey", "")));

        Assert.Single(providers);
        Assert.Equal("Groq", providers[0].Name);
    }

    [Fact]
    public void ModelsAndBaseUrls_ComeFromConfig()
    {
        var providers = OllamaEmbeddingService.BuildChatProviders(Config(
            ("OpenRouter:ApiKey", "o"), ("OpenRouter:ChatModel", "x/y"), ("OpenRouter:BaseUrl", "https://proxy.example/v1/"),
            ("OpenRouter:Referer", "https://realestate.sudata.eu")));

        var p = Assert.Single(providers);
        Assert.Equal("x/y", p.Model);
        Assert.Equal("https://proxy.example/v1/chat/completions", p.Url);
        Assert.Equal("https://realestate.sudata.eu", p.ExtraHeaders!["HTTP-Referer"]);
    }

    [Fact]
    public void NoKeys_EmptyChain()
        => Assert.Empty(OllamaEmbeddingService.BuildChatProviders(Config()));
}
