namespace Telrang.Web.Data;

/// <summary>
/// A network an admin blocked on purpose (DESIGN.md §17). The CIDR is typed in by an admin, never taken from
/// a request: client addresses are still never written to the database. Expired rows are purged on load.
/// </summary>
public class BlockedRange
{
    public const int MaxNoteLength = 200;

    public Guid Id { get; set; }

    /// <summary>Canonical CIDR (network address and prefix), e.g. <c>203.0.113.0/24</c> or <c>2001:db8:1::/48</c>.</summary>
    public required string Cidr { get; set; }

    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>Null = until removed.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
