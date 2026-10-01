using System.Collections.Concurrent;

namespace RealEstate.Api.Services.Jobs;

/// <summary>Stav úlohy na pozadí – vrací ho GET /api/jobs/{id}.</summary>
public sealed record BackgroundJobDto(
    Guid Id,
    string Kind,
    Guid? ListingId,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Error,
    object? Result)
{
    public bool IsFinished => Status is "Succeeded" or "Failed";
}

/// <summary>
/// Dlouhé akce vyvolané z UI (klasifikace fotek, export na Drive, lokální analýza) běží
/// nezávisle na HTTP požadavku. Dřív byly svázané s otevřenou stránkou: návrat na seznam
/// zrušil CancellationToken požadavku a API rozdělanou práci zahodilo.
/// </summary>
public interface IBackgroundJobService
{
    /// <summary>Zařadí úlohu; stejný druh pro stejný inzerát se nespouští dvakrát (vrátí běžící).</summary>
    Guid Enqueue(string kind, Guid? listingId, Func<IServiceProvider, CancellationToken, Task<object?>> work);

    BackgroundJobDto? Get(Guid id);

    IReadOnlyList<BackgroundJobDto> List(Guid? listingId, bool activeOnly);

    /// <summary>Počká na dokončení (čekání zruší jen volající, úloha běží dál). Výjimka úlohy se vyhodí znovu.</summary>
    Task<BackgroundJobDto> WaitAsync(Guid id, TimeSpan timeout, CancellationToken ct);
}

public sealed class BackgroundJobService(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<BackgroundJobService> logger) : IBackgroundJobService
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(6);

    private sealed class Job
    {
        public required Guid Id { get; init; }
        public required string Kind { get; init; }
        public Guid? ListingId { get; init; }
        public string Status { get; set; } = "Queued";
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? Error { get; set; }
        public object? Result { get; set; }
        public Exception? Exception { get; set; }
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BackgroundJobDto ToDto() => new(Id, Kind, ListingId, Status, CreatedAt, StartedAt, FinishedAt, Error, Result);
    }

    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    public Guid Enqueue(string kind, Guid? listingId, Func<IServiceProvider, CancellationToken, Task<object?>> work)
    {
        Prune();

        if (listingId is not null)
        {
            var running = _jobs.Values.FirstOrDefault(j => j.Kind == kind && j.ListingId == listingId && j.Status is "Queued" or "Running");
            if (running is not null)
                return running.Id;
        }

        var job = new Job { Id = Guid.NewGuid(), Kind = kind, ListingId = listingId };
        _jobs[job.Id] = job;

        _ = Task.Run(() => RunAsync(job, work), CancellationToken.None);
        return job.Id;
    }

    private async Task RunAsync(Job job, Func<IServiceProvider, CancellationToken, Task<object?>> work)
    {
        job.Status = "Running";
        job.StartedAt = DateTime.UtcNow;
        logger.LogInformation("Job {Kind} {Id} pro {ListingId} spuštěn", job.Kind, job.Id, job.ListingId);
        try
        {
            // Zrušení jen při zastavení aplikace – nikdy z HTTP požadavku, který úlohu založil
            await using var scope = scopeFactory.CreateAsyncScope();
            job.Result = await work(scope.ServiceProvider, lifetime.ApplicationStopping);
            job.Status = "Succeeded";
            logger.LogInformation("Job {Kind} {Id} hotov za {Seconds:F0} s", job.Kind, job.Id, (DateTime.UtcNow - job.StartedAt.Value).TotalSeconds);
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.Error = ex.Message;
            job.Exception = ex;
            logger.LogError(ex, "Job {Kind} {Id} pro {ListingId} selhal", job.Kind, job.Id, job.ListingId);
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
            job.Done.TrySetResult(true);
        }
    }

    public BackgroundJobDto? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job.ToDto() : null;

    public IReadOnlyList<BackgroundJobDto> List(Guid? listingId, bool activeOnly)
        => _jobs.Values
            .Where(j => listingId is null || j.ListingId == listingId)
            .Where(j => !activeOnly || j.Status is "Queued" or "Running")
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.ToDto())
            .ToList();

    public async Task<BackgroundJobDto> WaitAsync(Guid id, TimeSpan timeout, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(id, out var job))
            throw new KeyNotFoundException($"Úloha {id} neexistuje.");

        await job.Done.Task.WaitAsync(timeout, ct);
        if (job.Exception is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(job.Exception).Throw();
        return job.ToDto();
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - KeepFinished;
        foreach (var (id, job) in _jobs)
            if (job.FinishedAt is { } f && f < cutoff)
                _jobs.TryRemove(id, out _);
    }
}
