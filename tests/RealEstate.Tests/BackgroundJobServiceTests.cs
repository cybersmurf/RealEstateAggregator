using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RealEstate.Api.Services.Jobs;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Úlohy na pozadí – klasifikace, export na Drive a analýza nesmí umřít s HTTP požadavkem
//  (1. 10. 2026: návrat na seznam přerušil klasifikaci i export).
// ─────────────────────────────────────────────────────────────────
public class BackgroundJobServiceTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static BackgroundJobService Create()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return new BackgroundJobService(sp.GetRequiredService<IServiceScopeFactory>(), new Lifetime(), NullLogger<BackgroundJobService>.Instance);
    }

    [Fact]
    public async Task Job_RunsToCompletion_AndKeepsResult()
    {
        var jobs = Create();
        var id = jobs.Enqueue("photo-classify", Guid.NewGuid(), async (_, _) => { await Task.Delay(20); return "done"; });

        var job = await jobs.WaitAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Succeeded", job.Status);
        Assert.Equal("done", job.Result);
        Assert.NotNull(jobs.Get(id));
        Assert.Empty(jobs.List(null, activeOnly: true));
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelTheJob()
    {
        var jobs = Create();
        var finished = new TaskCompletionSource<bool>();
        var id = jobs.Enqueue("drive-export", Guid.NewGuid(), async (_, ct) =>
        {
            await Task.Delay(150, ct);   // ct je token aplikace, ne volajícího
            finished.TrySetResult(true);
            return 1;
        });

        using var callerCts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobs.WaitAsync(id, TimeSpan.FromSeconds(5), callerCts.Token));

        Assert.True(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Succeeded", (await jobs.WaitAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task SameKindForSameListing_IsNotStartedTwice()
    {
        var jobs = Create();
        var listing = Guid.NewGuid();
        var gate = new TaskCompletionSource<bool>();
        var first = jobs.Enqueue("photo-classify", listing, async (_, _) => { await gate.Task; return null; });
        var second = jobs.Enqueue("photo-classify", listing, async (_, _) => { await gate.Task; return null; });
        var other = jobs.Enqueue("photo-classify", Guid.NewGuid(), async (_, _) => { await gate.Task; return null; });

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(2, jobs.List(null, activeOnly: true).Count);
        gate.SetResult(true);
        await jobs.WaitAsync(first, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task FailedJob_RethrowsOnWait_AndReportsError()
    {
        var jobs = Create();
        var id = jobs.Enqueue("local-analysis", Guid.NewGuid(), (_, _) => throw new KeyNotFoundException("Inzerát nenalezen"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => jobs.WaitAsync(id, TimeSpan.FromSeconds(5), CancellationToken.None));
        var job = jobs.Get(id)!;
        Assert.Equal("Failed", job.Status);
        Assert.Equal("Inzerát nenalezen", job.Error);
    }
}
