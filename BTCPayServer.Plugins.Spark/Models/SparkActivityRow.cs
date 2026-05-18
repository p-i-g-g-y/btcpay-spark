namespace BTCPayServer.Plugins.Spark.Models;

/// <summary>
/// Canonical presentational shape for one row in any Spark activity table — the SO-authoritative
/// "All" feed, the local-DB Lightning invoices / Lightning payments / on-chain deposits / on-chain
/// withdrawals tabs, the dashboard widget, and the recent-activity slice on the overview page.
/// </summary>
/// <remarks>
/// One projection function per entity (in <c>SparkActivityRowFactory</c>) is the only place that
/// knows how to convert a domain entity into this shape; everything else is the
/// <c>_SparkActivityRow</c> partial which renders it identically across surfaces. That keeps the
/// History tabs visually consistent and lets us add details (mempool links, retry actions,
/// preimages) in one place without touching every view.
/// </remarks>
public sealed record SparkActivityRow
{
    /// <summary>Kind label shown in the leftmost badge (e.g. "Lightning in", "On-chain out").</summary>
    public required string Kind { get; init; }

    /// <summary>Bootstrap class applied to the kind badge.</summary>
    public required string KindBadgeClass { get; init; }

    public SparkActivityDirection Direction { get; init; } = SparkActivityDirection.Internal;

    /// <summary>Amount in sats. Set <see cref="AmountIsApproximate"/> when this is a quoted figure rather than the final number.</summary>
    public long AmountSats { get; init; }
    public bool AmountIsApproximate { get; init; }

    /// <summary>Optional fee, when distinct from the amount (Lightning routing, coop-exit). Null hides the fee row.</summary>
    public long? FeeSats { get; init; }

    public required string StatusLabel { get; init; }
    public required string StatusBadgeClass { get; init; }
    /// <summary>Raw status string from the SDK / DB — used as the hover-tooltip on the status badge for forensics.</summary>
    public string? StatusRaw { get; init; }

    /// <summary>When the row was created (or first seen, for deposits).</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// One-line secondary detail rendered below the amount row. Free-form HTML-escaped string
    /// describing the most identifying piece of context for this row — e.g. payment hash, txid,
    /// destination address, memo, BTCPay invoice id. The partial wraps it in <c>small text-secondary</c>.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Optional fully-qualified URL the detail string should link to (mempool tx/address page, BTCPay invoice page).</summary>
    public string? DetailLink { get; init; }

    /// <summary>Click-to-copy payload for the row's primary id (transfer id, txid, payment hash). Optional.</summary>
    public string? CopyText { get; init; }

    /// <summary>
    /// Last-error tooltip for the status badge. When set, the status badge gets a danger outline
    /// and the error is the badge's <c>title</c>. Avoids dedicating a column to errors.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Optional inline action button (Retry, View invoice, etc.). Rendered at the end of the row.</summary>
    public RowAction? Action { get; init; }
}

public enum SparkActivityDirection
{
    /// <summary>Sender == receiver == us (or N/A). Renders ↔.</summary>
    Internal = 0,

    /// <summary>This wallet was the sender — sats left. Renders ↗ red.</summary>
    Outgoing = 1,

    /// <summary>This wallet was the receiver — sats arrived. Renders ↘ green.</summary>
    Incoming = 2,
}

/// <summary>Inline action button on an activity row — a small form-post with a label.</summary>
public sealed record RowAction(string Label, string Controller, string Action, IDictionary<string, string> RouteValues, string? Title = null);

/// <summary>
/// SO-authoritative transfer page. The <c>SparkTransferActivityService</c> produces these for
/// the "All" tab. Pre-projected to <see cref="SparkActivityRow"/> via the same factory other
/// tabs use, so layout stays identical.
/// </summary>
public sealed record SparkActivityPage(
    IReadOnlyList<SparkActivityRow> Rows,
    long? NextOffset,
    int Take,
    long Offset,
    string? Error);
