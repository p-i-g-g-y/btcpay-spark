using BTCPayServer.Plugins.Spark.Data.Entities;
using BTCPayServer.Plugins.Spark.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.Spark.Helpers;

/// <summary>
/// Single source of truth for turning local-DB activity entities into
/// <see cref="SparkActivityRow"/>s. Mirrors what <c>SparkTransferActivityService</c> does for
/// the SO-authoritative "All" feed, but for our own bookkeeping tables. Centralising the
/// projection here keeps the History / Overview / Dashboard widgets visually consistent — a UX
/// tweak to one row style propagates everywhere.
/// </summary>
public static class SparkActivityRowFactory
{
    /// <summary>Incoming Lightning invoice (we receive sats from the payer).</summary>
    public static SparkActivityRow FromInvoice(SparkLightningInvoiceEntity i)
    {
        var (label, badge) = i.Status switch
        {
            SparkLightningInvoiceStatus.Paid => ("Paid", "bg-success"),
            SparkLightningInvoiceStatus.Pending => ("Pending", "bg-info text-dark"),
            SparkLightningInvoiceStatus.Expired => ("Expired", "bg-secondary"),
            SparkLightningInvoiceStatus.Cancelled => ("Cancelled", "bg-warning text-dark"),
            _ => (i.Status.ToString(), "bg-secondary"),
        };

        var detail = !string.IsNullOrEmpty(i.Memo)
            ? i.Memo
            : !string.IsNullOrEmpty(i.BtcPayInvoiceId)
                ? $"BTCPay invoice {i.BtcPayInvoiceId}"
                : $"hash {SparkLinkHelper.ShortId(i.PaymentHash)}";

        return new SparkActivityRow
        {
            Kind = "Lightning in",
            KindBadgeClass = "bg-info text-dark",
            Direction = SparkActivityDirection.Incoming,
            AmountSats = i.AmountSats,
            StatusLabel = label,
            StatusBadgeClass = badge,
            StatusRaw = i.Status.ToString(),
            At = i.PaidAt ?? i.CreatedAt,
            Detail = detail,
            CopyText = i.PaymentHash,
        };
    }

    /// <summary>Outgoing Lightning payment (we paid an invoice).</summary>
    public static SparkActivityRow FromOutgoingPayment(SparkOutgoingPaymentEntity p)
    {
        var (label, badge) = p.Status switch
        {
            SparkOutgoingPaymentStatus.Succeeded => ("Succeeded", "bg-success"),
            SparkOutgoingPaymentStatus.Pending => ("Pending", "bg-info text-dark"),
            SparkOutgoingPaymentStatus.Failed => ("Failed", "bg-danger"),
            _ => (p.Status.ToString(), "bg-secondary"),
        };

        var detail = !string.IsNullOrEmpty(p.Memo)
            ? p.Memo
            : $"hash {SparkLinkHelper.ShortId(p.PaymentHash)}";

        return new SparkActivityRow
        {
            Kind = "Lightning out",
            KindBadgeClass = "bg-info text-dark",
            Direction = SparkActivityDirection.Outgoing,
            AmountSats = p.AmountSats ?? 0,
            AmountIsApproximate = p.AmountSats is null,
            FeeSats = p.ActualFeeSats ?? p.MaxFeeSats,
            StatusLabel = label,
            StatusBadgeClass = badge,
            StatusRaw = p.Status.ToString(),
            At = p.CompletedAt ?? p.CreatedAt,
            Detail = detail,
            CopyText = p.PaymentHash,
            Error = p.Status == SparkOutgoingPaymentStatus.Failed ? p.LastError : null,
        };
    }

    /// <summary>On-chain deposit (UTXO sent to our static deposit address, possibly being claimed).</summary>
    public static SparkActivityRow FromDeposit(SparkWalletDepositEntity d, Network network)
    {
        var (label, badge) = d.Status switch
        {
            SparkWalletDepositStatus.Settled => ("Confirmed", "bg-success"),
            SparkWalletDepositStatus.ClaimedToTransfer => ("Claiming", "bg-info text-dark"),
            SparkWalletDepositStatus.Discovered => ("Pending claim", "bg-info text-dark"),
            SparkWalletDepositStatus.Failed => ("Failed", "bg-danger"),
            _ => (d.Status.ToString(), "bg-secondary"),
        };

        var detail = $"txid {SparkLinkHelper.ShortOutpoint(d.TxId, d.Vout)}";

        RowAction? action = null;
        if (d.Status != SparkWalletDepositStatus.Settled)
        {
            action = new RowAction(
                Label: "Retry",
                Controller: "Spark",
                Action: "RetryDeposit",
                RouteValues: new Dictionary<string, string> { ["depositId"] = d.Id },
                Title: "Re-queue this deposit; the auto-claimer will attempt it on the next sweep.");
        }

        return new SparkActivityRow
        {
            Kind = "On-chain in",
            KindBadgeClass = "bg-success",
            Direction = SparkActivityDirection.Incoming,
            AmountSats = d.AmountSats ?? 0,
            AmountIsApproximate = d.AmountSats is null,
            StatusLabel = label,
            StatusBadgeClass = badge,
            StatusRaw = d.Status.ToString(),
            At = d.SettledAt ?? d.SeenAt ?? d.CreatedAt,
            Detail = detail,
            DetailLink = string.IsNullOrEmpty(d.TxId) ? null : SparkLinkHelper.GetTransactionLink(network, d.TxId),
            CopyText = d.TxId,
            Error = d.Status == SparkWalletDepositStatus.Failed ? d.LastError : null,
            Action = action,
        };
    }

    /// <summary>On-chain withdrawal (cooperative exit to an external Bitcoin address).</summary>
    public static SparkActivityRow FromWithdrawal(SparkWalletWithdrawalEntity w, Network network)
    {
        var (label, badge) = w.Status switch
        {
            SparkWalletWithdrawalStatus.Sent => ("Broadcast", "bg-success"),
            SparkWalletWithdrawalStatus.Pending => ("Pending", "bg-info text-dark"),
            SparkWalletWithdrawalStatus.Failed => ("Failed", "bg-danger"),
            _ => (w.Status.ToString(), "bg-secondary"),
        };

        var detail = $"to {SparkLinkHelper.ShortId(w.DestinationAddress)}";

        return new SparkActivityRow
        {
            Kind = "On-chain out",
            KindBadgeClass = "bg-secondary",
            Direction = SparkActivityDirection.Outgoing,
            AmountSats = w.AmountSats,
            StatusLabel = label,
            StatusBadgeClass = badge,
            StatusRaw = w.Status.ToString(),
            At = w.CompletedAt ?? w.CreatedAt,
            Detail = detail,
            // Prefer the tx link (most useful), fall back to the address page when the tx isn't
            // visible yet (shouldn't really happen for Sent withdrawals, but be defensive).
            DetailLink = !string.IsNullOrEmpty(w.TxId)
                ? SparkLinkHelper.GetTransactionLink(network, w.TxId)
                : SparkLinkHelper.GetAddressLink(network, w.DestinationAddress),
            CopyText = w.TxId ?? w.DestinationAddress,
        };
    }
}
