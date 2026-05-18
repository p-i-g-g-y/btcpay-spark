using BTCPayServer.Plugins.Spark.Data.Entities;

namespace BTCPayServer.Plugins.Spark.Helpers;

/// <summary>Display helpers used by Razor views — status badge classes, friendly date formatting, etc.</summary>
public static class SparkViewHelpers
{
    public static string BadgeClassFor(SparkLightningInvoiceStatus status) => status switch
    {
        SparkLightningInvoiceStatus.Paid => "bg-success",
        SparkLightningInvoiceStatus.Pending => "bg-info",
        SparkLightningInvoiceStatus.Expired => "bg-secondary",
        SparkLightningInvoiceStatus.Cancelled => "bg-warning",
        _ => "bg-secondary",
    };

    public static string BadgeClassFor(SparkWalletDepositStatus status) => status switch
    {
        SparkWalletDepositStatus.Settled => "bg-success",
        SparkWalletDepositStatus.ClaimedToTransfer => "bg-info",
        SparkWalletDepositStatus.Discovered => "bg-secondary",
        SparkWalletDepositStatus.Failed => "bg-danger",
        _ => "bg-secondary",
    };

    public static string BadgeClassFor(SparkWalletWithdrawalStatus status) => status switch
    {
        SparkWalletWithdrawalStatus.Sent => "bg-success",
        SparkWalletWithdrawalStatus.Pending => "bg-info",
        SparkWalletWithdrawalStatus.Failed => "bg-danger",
        _ => "bg-secondary",
    };

    public static string BadgeClassFor(SparkOutgoingPaymentStatus status) => status switch
    {
        SparkOutgoingPaymentStatus.Succeeded => "bg-success",
        SparkOutgoingPaymentStatus.Pending => "bg-info",
        SparkOutgoingPaymentStatus.Failed => "bg-danger",
        _ => "bg-secondary",
    };

    public static string RelativeTime(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        return when.ToString("yyyy-MM-dd");
    }

    /// <summary>Direction arrow with conventional colour: ↘ green (in), ↗ red (out), ↔ grey (internal).</summary>
    public static string DirectionArrow(bool outgoing, bool incoming) => (outgoing, incoming) switch
    {
        (true, false) => "↗",
        (false, true) => "↘",
        _ => "↔",
    };

    public static string DirectionClass(bool outgoing, bool incoming) => (outgoing, incoming) switch
    {
        (true, false) => "text-danger",
        (false, true) => "text-success",
        _ => "text-secondary",
    };
}
