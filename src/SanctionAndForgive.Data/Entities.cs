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

    /// <summary>When an administrator switched the list off: it isn't searched, but it's still imported, shown and
    /// browsable. Null: on (<see cref="SourceSwitch"/>).</summary>
    public DateTimeOffset? SwitchedOffAt { get; set; }

    /// <summary>Who switched it off (never public).</summary>
    public string? SwitchedOffBy { get; set; }

    /// <summary>Why, in the administrator's words: shown with every result while it's off.</summary>
    public string? SwitchedOffReason { get; set; }

    public List<Import> Imports { get; } = [];
}

public enum ImportStatus
{
    Running,
    Completed,

    /// <summary>The download or the parse failed. Its subjects (if any) are deleted.</summary>
    Failed,

    /// <summary>Refused: zero records, refused on review, or superseded while held. Its records (if any) are
    /// deleted; the row stays in the history with the reason.</summary>
    Rejected,

    /// <summary>A drop of more than 20%: written in full but not active, until someone confirms or rejects it. The
    /// active list keeps serving meanwhile (and ages out as usual). Superseded if the feed returns to the active list
    /// or a later update needs no review.</summary>
    PendingReview,
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

    /// <summary>Whether its subject rows still exist. Every import's row stays as the list's history; the records
    /// are kept only for the active import, the one before it, and one held for review.</summary>
    public bool HasData { get; set; }

    /// <summary>For an import held for review: the active import it was compared against. Confirming it is refused if
    /// the source has moved on from that list since, so a stale hold can never roll a list back.</summary>
    public int? BasedOnImportId { get; set; }

    /// <summary>When a held import was confirmed or rejected, and by whom (the reviewer's sign-in name). Not shown
    /// on the public pages.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewedBy { get; set; }

    /// <summary>What changed against the list it was compared with (the active list when it was written; for the first
    /// import, everything is added), by external id: records new, gone, and kept with different content (any field,
    /// name or address). Null where it wasn't recorded (an import from before these counts whose records are gone, or
    /// a count that failed: an import never fails for its counts).</summary>
    public int? AddedCount { get; set; }

    public int? RemovedCount { get; set; }

    public int? ChangedCount { get; set; }
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

/// <summary>An allow-list entry (owner's decision, 2026-11-06): a name known to be a legitimate party, whose possible
/// matches are shown as allowed. Never deleted: a removal sets <see cref="RemovedAt"/>, so the list keeps its history.
/// Written by administrators only; it holds no screening input.</summary>
public sealed class AllowListItem
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string Reason { get; set; }

    public required string AddedBy { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public string? RemovedBy { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>A destination under broad restrictions (owner's decisions on #67, 2026-11-18; read by the Rust port only so
/// far): a country, a region or a settlement, with every name it is recognised by in an address, and whether it is in a
/// restricted area, partly (a region whose occupied part an address can't tell), or outside one (a settlement inside a
/// partly restricted region). Curated by administrators from the official sources; never deleted: a removal sets
/// <see cref="RemovedAt"/>, and a change is a new row with the same <see cref="Code"/> that removes the old one. It holds
/// no screening input.</summary>
public sealed class RestrictedDestination
{
    public int Id { get; set; }

    /// <summary>Stable across changes ("ua-crimea"): a settlement names its region by it.</summary>
    public required string Code { get; set; }

    /// <summary><c>Country</c>, <c>Region</c> or <c>Settlement</c>.</summary>
    public required string Kind { get; set; }

    public required string Name { get; set; }

    /// <summary>Every other name it is recognised by, one per line.</summary>
    public required string OtherNames { get; set; }

    /// <summary>ISO 3166-1 alpha-2: the country itself for a <c>Country</c>, the one it lies in otherwise.</summary>
    public string? CountryCode { get; set; }

    /// <summary>A settlement's region (its <see cref="Code"/>).</summary>
    public string? ParentCode { get; set; }

    /// <summary>Recognised only when the address names its region or no other one (a name that repeats).</summary>
    public bool NeedsRegion { get; set; }

    /// <summary><c>Restricted</c>, <c>Partly</c> or <c>Outside</c>.</summary>
    public required string Status { get; set; }

    /// <summary>Who restricts it ("EU, UK, US").</summary>
    public required string RestrictedBy { get; set; }

    /// <summary>The legal acts it comes from.</summary>
    public required string LegalSource { get; set; }

    public DateOnly AsOf { get; set; }

    public DateOnly ReviewBy { get; set; }

    public required string AddedBy { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public string? RemovedBy { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }
}
