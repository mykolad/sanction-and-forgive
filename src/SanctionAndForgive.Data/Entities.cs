namespace SanctionAndForgive.Data;

/// <summary>A list the app answers for: a public feed (with a URL) or a manual source (without one). Its
/// <see cref="ActiveImportId"/> is the import readers use; a new import becomes active in one transaction.</summary>
public sealed class Source
{
    public int Id { get; set; }

    public required string Code { get; set; }

    public required string Name { get; set; }

    /// <summary>Null for a manual source.</summary>
    public string? Url { get; set; }

    /// <summary>How long after its last successful check the list may still be searched. Null: never stale (manual).</summary>
    public int? MaxAgeHours { get; set; }

    public int? ActiveImportId { get; set; }

    public Import? ActiveImport { get; set; }

    /// <summary>The last successful check of the feed, changed or not. Staleness counts from here.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    public List<Import> Imports { get; } = [];
}

public enum ImportStatus
{
    Running,
    Completed,

    /// <summary>The download or the parse failed. Its subjects (if any) are deleted.</summary>
    Failed,

    /// <summary>The sanity guard refused it: zero records, or a drop of more than 20%. Kept for diagnosis only.</summary>
    Rejected,
}

/// <summary>One import of one source: its own complete copy of the subjects.</summary>
public sealed class Import
{
    public int Id { get; set; }

    public int SourceId { get; set; }

    public Source? Source { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>SHA-256 of the canonical content (sorted external ids plus record content), so a file whose only
    /// change is its generation date counts as unchanged.</summary>
    public string? ContentHash { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int RecordCount { get; set; }

    public ImportStatus Status { get; set; }

    /// <summary>Why a failed or rejected import didn't become active. No input data: lists only.</summary>
    public string? Message { get; set; }
}

public enum SubjectType : byte
{
    Individual,
    Entity,
    Unknown,
}

public enum NameScript : byte
{
    Latin,
    Cyrillic,
    Other,
}

public sealed class Subject
{
    public int Id { get; set; }

    public int ImportId { get; set; }

    public int SourceId { get; set; }

    public required string ExternalId { get; set; }

    public SubjectType Type { get; set; }

    public required string PrimaryName { get; set; }

    public string? Programmes { get; set; }

    public DateOnly? ListedOn { get; set; }

    public string? Remarks { get; set; }

    public string? SourceUrl { get; set; }

    public List<SubjectName> Names { get; } = [];

    public List<SubjectAddress> Addresses { get; } = [];
}

public sealed class SubjectName
{
    public int Id { get; set; }

    public int SubjectId { get; set; }

    public required string FullName { get; set; }

    public bool IsWeak { get; set; }

    public NameScript Script { get; set; }
}

public sealed class SubjectAddress
{
    public int Id { get; set; }

    public int SubjectId { get; set; }

    public required string Raw { get; set; }

    public string? CountryCode { get; set; }
}
