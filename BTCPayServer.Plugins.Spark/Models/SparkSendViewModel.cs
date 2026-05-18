using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Spark.Models;

public enum SparkSendStep
{
    /// <summary>Paste / scan destination. Initial state.</summary>
    Destination = 0,
    /// <summary>Enter amount. Skipped automatically for BOLT11 invoices with an embedded amount.</summary>
    Amount = 1,
    /// <summary>Review parsed destination + amount + fetched fee, confirm to execute.</summary>
    Confirm = 2,
    /// <summary>Send executed; show result.</summary>
    Done = 3,
}

public enum SparkSendKind
{
    Unknown = 0,
    /// <summary>BOLT11 Lightning invoice.</summary>
    Bolt11 = 1,
    /// <summary><c>user@domain</c> Lightning address (LNURL-pay).</summary>
    LightningAddress = 2,
    /// <summary><c>spark1…</c> or <c>sparkrt1…</c> Spark-to-Spark transfer.</summary>
    SparkAddress = 3,
    /// <summary>On-chain Bitcoin address — cooperative exit.</summary>
    OnChain = 4,
}

/// <summary>
/// Multi-step send wizard backing model. The same view component is re-rendered on each POST;
/// <see cref="Step"/> selects which slice of the view is visible. All non-result fields are
/// designed to be carried across steps as hidden inputs so the back button works without the
/// server holding session state.
/// </summary>
public class SparkSendViewModel
{
    public SparkSendStep Step { get; set; } = SparkSendStep.Destination;
    public SparkSendKind Kind { get; set; } = SparkSendKind.Unknown;

    [Display(Name = "Destination")]
    public string? Destination { get; set; }

    /// <summary>Amount in sats. For BOLT11 with an embedded amount, this is locked at parse time.</summary>
    [Display(Name = "Amount")]
    public long? AmountSats { get; set; }

    /// <summary>True when the amount was extracted from the destination (BOLT11) and the user can't change it.</summary>
    public bool AmountLocked { get; set; }

    /// <summary>
    /// "Empty the wallet" mode for on-chain withdrawals only. When set, the controller queries
    /// the current balance, builds a fee quote against ALL available leaves, and treats the
    /// quote step's amount as <c>balance − fee</c> (what the recipient receives). The execute
    /// step then passes the full balance to <c>WithdrawAsync</c> so every leaf is swept.
    /// Ignored for Lightning / Spark-transfer kinds — those have no "withdraw all" semantics.
    /// </summary>
    public bool SwipeAll { get; set; }

    /// <summary>Optional ceiling on Lightning routing fees (Lightning kinds only).</summary>
    [Display(Name = "Max fee (sats)")]
    public long? MaxFeeSats { get; set; }

    /// <summary>Best-available SSP/SO fee estimate at quote time, in sats. Null for Spark transfers (no fee).</summary>
    public long? FeeEstimateSats { get; set; }

    /// <summary>
    /// For Lightning addresses: the concrete BOLT11 returned by LNURL-pay at quote time. We
    /// surface the resolved invoice on the Confirm step so the fee shown is the fee for THAT
    /// exact invoice — and pay that exact invoice on confirm. Re-resolving at execute time
    /// could legitimately return a different invoice (one-shot LNURL endpoints), so we lock it
    /// here. Null for non-Lightning-address kinds.
    /// </summary>
    public string? ResolvedBolt11 { get; set; }

    /// <summary>Optional human-readable note pulled from the destination (e.g. BOLT11 description). Display-only.</summary>
    public string? Memo { get; set; }

    /// <summary>Spendable balance at quote time. Used to show "Max" and pre-empt insufficient-funds errors.</summary>
    public long? AvailableSats { get; set; }

    /// <summary>Populated on <see cref="SparkSendStep.Done"/>.</summary>
    public SparkSendResultViewModel? Result { get; set; }
}

public class SparkSendResultViewModel
{
    public SparkSendKind Kind { get; set; }
    public string Destination { get; set; } = "";
    public long AmountSats { get; set; }
    public long? FeeSats { get; set; }
    public string? TxId { get; set; }              // on-chain withdraw
    public string? SspRequestId { get; set; }      // Lightning send
    public string? SparkTransferId { get; set; }   // Spark-to-Spark
}
