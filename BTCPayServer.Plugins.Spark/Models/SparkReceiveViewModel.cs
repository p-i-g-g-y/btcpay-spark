using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Spark.Models;

/// <summary>
/// Backs the three-tab Receive page: Spark address (off-chain Spark-to-Spark), on-chain
/// deposit address (UTXOs auto-claim after 3 confirmations), and an ad-hoc Lightning
/// invoice generator. The Lightning section is the only one that requires user input;
/// the two addresses are stable per wallet and rendered immediately on GET.
/// </summary>
public class SparkReceiveViewModel
{
    public string StoreId { get; set; } = "";

    /// <summary>Stable Spark address (<c>spark1...</c> or <c>sprt1...</c>). Always present.</summary>
    public string SparkAddress { get; set; } = "";

    /// <summary>P2TR mainnet/regtest address for on-chain funding. Null only if not derived yet.</summary>
    public string? StaticDepositAddress { get; set; }

    // ── Invoice generator inputs ──

    [Display(Name = "Amount (sats)")]
    [Range(1, 100_000_000, ErrorMessage = "Amount must be between 1 sat and 1 BTC.")]
    public long? InvoiceAmountSats { get; set; }

    [Display(Name = "Memo (optional)")]
    [StringLength(640, ErrorMessage = "Memo is too long.")]
    public string? InvoiceMemo { get; set; }

    [Display(Name = "Expiry (minutes)")]
    [Range(1, 1440, ErrorMessage = "Expiry must be between 1 minute and 24 hours.")]
    public int InvoiceExpiryMinutes { get; set; } = 60;

    // ── Invoice generator result (populated on POST) ──

    public string? GeneratedInvoice { get; set; }
    public string? GeneratedInvoicePaymentHash { get; set; }
    public long? GeneratedInvoiceAmountSats { get; set; }
    public DateTimeOffset? GeneratedInvoiceExpiresAt { get; set; }

    /// <summary>Which tab to open first. Defaults to "lightning" — the most common receive
    /// channel — matching the tab order on the page. Reset to "lightning" after a successful
    /// invoice POST so the user sees the freshly generated BOLT11.</summary>
    public string ActiveTab { get; set; } = "lightning";
}
