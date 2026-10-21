namespace RealEstate.Api.Contracts.Overview;

/// <summary>Osoba ve workspace (vlastník + členové s účtem), která může hlasovat.</summary>
public sealed record PersonDto(Guid UserId, string Name, bool IsMe, bool CanVote);

/// <summary>Hlas jedné osoby k domu; <see cref="BeforePriceChange"/> = od hlasu se změnila cena.</summary>
public sealed record VoteDto(Guid UserId, string Name, string Value, string? Note, DateTime VotedAt, bool BeforePriceChange);

public sealed record VoteRequestDto(string Value, string? Note);

public sealed record RankItemDto(Guid ListingId, string Bucket, int Position);

/// <summary>Karta domu pro žebříček kandidátů – vše za celou skupinu duplicit.</summary>
public sealed class HouseCardDto
{
    public Guid ListingId { get; set; }
    public Guid GroupId { get; set; }
    public string Title { get; set; } = "";
    public string? Municipality { get; set; }
    public string? LocationText { get; set; }
    public string? PhotoUrl { get; set; }
    public string? Url { get; set; }
    public string SourceCode { get; set; } = "";
    public bool IsActive { get; set; }
    public string UserStatus { get; set; } = "New";
    public string? Notes { get; set; }

    public decimal? Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public double? PriceChangePct { get; set; }
    public DateTime? LastPriceChangeAt { get; set; }
    public int DaysOnMarket { get; set; }
    /// <summary>60+ dní na trhu bez zlevnění.</summary>
    public bool NegotiationRoom { get; set; }
    /// <summary>Nad 7,5 mil. Kč.</summary>
    public bool WaitForDiscount { get; set; }

    /// <summary>fits | partial | no</summary>
    public string Match { get; set; } = "partial";
    public int CriteriaMet { get; set; }
    public int CriteriaTotal { get; set; }
    public List<string> CriteriaMissing { get; set; } = [];
    public string? HouseType { get; set; }
    public string? HabitableFloor { get; set; }
    public string? Verdict { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> Pluses { get; set; } = [];
    public List<string> Minuses { get; set; } = [];
    public List<string> VerifyOnSite { get; set; } = [];
    public string? Variant { get; set; }
    public string? Trainer { get; set; }
    public double? GardenM2 { get; set; }
    public string? Heating { get; set; }
    public string? RoomsNote { get; set; }
    public string? ExtraSpace { get; set; }
    public string? Garage { get; set; }
    public Guid? LastAnalysisId { get; set; }
    public DateTime? LastAnalysisAt { get; set; }
    public string? LastAnalysisTitle { get; set; }

    /// <summary>visit | watch | second</summary>
    public string Bucket { get; set; } = "watch";
    public int Position { get; set; }
    public List<VoteDto> Votes { get; set; } = [];
    /// <summary>Všichni hlasovali „Jet se podívat".</summary>
    public bool Agreed { get; set; }
    /// <summary>Někdo řekl „Ne".</summary>
    public bool Vetoed { get; set; }
    public Guid? UpcomingVisitId { get; set; }
    public DateTime? UpcomingVisitAt { get; set; }
}

public sealed record BucketDto(string Key, string Label, List<HouseCardDto> Houses);

public sealed record CandidatesDto(List<PersonDto> People, List<BucketDto> Buckets, DateTime LoadedAt);

public sealed record ChecklistItemDto(string Text, bool Done);

public sealed class VisitDto
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string Title { get; set; } = "";
    public string? Municipality { get; set; }
    public string? LocationText { get; set; }
    public string? PhotoUrl { get; set; }
    public string? Url { get; set; }
    public decimal? Price { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public string? AgentName { get; set; }
    public string? AgentPhone { get; set; }
    public string? Address { get; set; }
    public List<ChecklistItemDto> Checklist { get; set; } = [];
    public string? Note { get; set; }
    public string Status { get; set; } = "planned";
    public DateTime UpdatedAt { get; set; }
}

public sealed record VisitUpsertDto(
    Guid? ListingId,
    DateTime? ScheduledAt,
    string? AgentName,
    string? AgentPhone,
    string? Address,
    List<ChecklistItemDto>? Checklist,
    string? Note,
    string? Status);

public sealed record DigestItemDto(string Type, Guid? ListingId, string Title, string? Text, string? Link);

public sealed record DailyDigestDto(DateOnly Date, DateTime GeneratedAt, string Summary, List<DigestItemDto> Items, bool IsToday);

public sealed record SaveDigestDto(DateOnly? Date, string Summary, List<DigestItemDto> Items);
