using BTCPayServer.Plugins.Spark.Helpers;
using BTCPayServer.Plugins.Spark.Models;
using Microsoft.Extensions.Logging;
using NSpark.Models;
using NSpark.Services;

namespace BTCPayServer.Plugins.Spark.Services;

/// <summary>
/// Pulls the authoritative "all activity" feed for a Spark wallet straight from the Signing
/// Operators via <c>SparkWallet.GetTransfersAsync</c>. Each transfer arrives with a
/// <see cref="SparkTransfer.Type"/> tag (e.g. <c>PREIMAGE_SWAP</c>, <c>COOPERATIVE_EXIT</c>,
/// <c>UTXO_SWAP</c>, <c>TRANSFER</c>) — this service maps those raw proto enum values into a
/// presentational <see cref="SparkActivityRow"/> that the UI can render without any business logic.
/// </summary>
/// <remarks>
/// Why a separate service: the local DbContext-backed feeds (LightningInvoices, OutgoingPayments,
/// WalletDeposits, WalletWithdrawals) only cover events that originated through THIS plugin's
/// code paths. The wallet might have transfers from other clients (e.g. a recovery via the
/// Spark mobile app), and only the SO-side history sees those. Querying the SOs directly gives
/// a complete view.
/// </remarks>
public interface ISparkTransferActivityService
{
    /// <summary>
    /// Fetch one page of transfers for the given <paramref name="walletId"/>, mapped to UI rows.
    /// <paramref name="hideInternalSwaps"/> filters out the leaf-rebalancing types
    /// (<c>SWAP</c>, <c>COUNTER_SWAP</c>, <c>PRIMARY_SWAP_V3</c>, <c>COUNTER_SWAP_V3</c>) that
    /// are protocol mechanics rather than user-visible activity. Defaults to true.
    /// </summary>
    Task<SparkActivityPage> GetActivityPageAsync(
        string walletId,
        int take = 25,
        long offset = 0,
        bool hideInternalSwaps = true,
        CancellationToken ct = default);
}

public class SparkTransferActivityService(
    IStoreSparkWalletProvider walletProvider,
    ILogger<SparkTransferActivityService> logger) : ISparkTransferActivityService
{
    public async Task<SparkActivityPage> GetActivityPageAsync(
        string walletId,
        int take = 25,
        long offset = 0,
        bool hideInternalSwaps = true,
        CancellationToken ct = default)
    {
        if (take <= 0) take = 25;
        if (take > 200) take = 200;            // soft cap; SOs accept higher but UI doesn't need it
        if (offset < 0) offset = 0;

        var wallet = await walletProvider.GetByWalletIdAsync(walletId, ct);
        if (wallet is null)
            return new SparkActivityPage([], NextOffset: null, Take: take, Offset: offset, Error: "Wallet not available.");

        // Pull a slightly larger window when filtering so the post-filter page size still matches
        // the requested take. 2× is enough in practice — leaf swaps are rarely more than half of
        // history.
        var rawTake = hideInternalSwaps ? Math.Min(take * 2, 200) : take;

        TransferPage page;
        try
        {
            page = await wallet.GetTransfersAsync(limit: rawTake, offset: offset, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetTransfersAsync failed for wallet {WalletId}", walletId);
            return new SparkActivityPage([], NextOffset: null, Take: take, Offset: offset, Error: ex.Message);
        }

        var ownIdentityHex = wallet.IdentityPublicKeyHex ?? string.Empty;

        var rows = page.Transfers
            .Select(t => Map(t, ownIdentityHex))
            .Where(p => !hideInternalSwaps || !p.IsInternal)
            .Select(p => p.Row)
            .Take(take)
            .ToList();

        // SOs return the next-offset cursor even when we paged with our own filter. Pass it
        // through so the caller can ask for the next page; null means "no more".
        long? nextOffset = page.Transfers.Count == rawTake ? page.Offset : null;

        return new SparkActivityPage(rows, nextOffset, take, offset, Error: null);
    }

    /// <summary>
    /// Maps a raw <see cref="SparkTransfer"/> into a presentational row. Centralised so both the
    /// Overview "All" tab and the History page share identical formatting.
    /// </summary>
    private static (SparkActivityRow Row, bool IsInternal) Map(SparkTransfer t, string ownIdentityHex)
    {
        // Direction: compare against the wallet's own identity pubkey.
        var senderIsSelf = string.Equals(t.SenderIdentityPublicKey, ownIdentityHex, StringComparison.OrdinalIgnoreCase);
        var receiverIsSelf = string.Equals(t.ReceiverIdentityPublicKey, ownIdentityHex, StringComparison.OrdinalIgnoreCase);
        var direction = (senderIsSelf, receiverIsSelf) switch
        {
            (true, true) => SparkActivityDirection.Internal,
            (true, false) => SparkActivityDirection.Outgoing,
            (false, true) => SparkActivityDirection.Incoming,
            _ => SparkActivityDirection.Internal,
        };

        var (kind, kindBadge, isInternal) = ClassifyKind(t.Type);
        var (statusLabel, statusBadge) = ClassifyStatus(t.Status);

        // The detail string surfaces the counterparty for completeness, falling back to the
        // transfer id when we're on both sides (internal rebalance).
        var counterparty = senderIsSelf ? t.ReceiverIdentityPublicKey : t.SenderIdentityPublicKey;
        var detail = direction == SparkActivityDirection.Internal
            ? $"transfer {SparkLinkHelper.ShortId(t.Id)}"
            : $"to {SparkLinkHelper.ShortId(counterparty)}";

        var row = new SparkActivityRow
        {
            Kind = kind,
            KindBadgeClass = kindBadge,
            Direction = direction,
            AmountSats = t.TotalValueSats,
            StatusLabel = statusLabel,
            StatusBadgeClass = statusBadge,
            StatusRaw = t.Status,
            At = t.CreatedAt,
            Detail = detail,
            CopyText = t.Id,
        };
        return (row, isInternal);
    }

    private static (string Kind, string Badge, bool IsInternal) ClassifyKind(string? type)
    {
        // NSpark's MapTransfer uses proto's <c>t.Type.ToString()</c>, which returns the C# enum
        // member name (PascalCase) — e.g. "PreimageSwap", "CounterSwapV3" — NOT the proto
        // SCREAMING_SNAKE_CASE source name. We normalise both forms (strip underscores, lowercase)
        // so the matching is robust if either NSpark or the proto generator ever changes its
        // toString convention.
        return Normalize(type) switch
        {
            // Direction is already conveyed by the ↗/↘ arrow next to the badge, so the kind label
            // stays direction-agnostic — keeps the row narrow and matches BTCPay's stock idiom.
            "preimageswap" => ("Lightning payment", "bg-info text-dark", false),
            "cooperativeexit" => ("On-chain withdrawal", "bg-secondary", false),
            "utxoswap" => ("On-chain deposit", "bg-success", false),
            "transfer" => ("Spark transfer", "bg-primary", false),

            // Leaf-rebalancing operations — protocol mechanics, not user activity. Marked internal
            // so the default view hides them; advanced users can opt in via the "Show leaf swaps"
            // toggle on the History page.
            "swap" or "counterswap" or "primaryswapv3" or "counterswapv3" =>
                ("Internal rebalance", "bg-light text-dark border", true),

            "" => ("Transfer", "bg-light text-dark border", false),
            _ => (type ?? "Transfer", "bg-light text-dark border", false),
        };
    }

    private static (string Label, string Badge) ClassifyStatus(string? status)
    {
        // Same normalization story as ClassifyKind: NSpark stringifies proto's TransferStatus to
        // its C# PascalCase form ("Completed", "Expired", "Returned", "SenderInitiated", …).
        // Anything we don't recognise as terminal stays "Pending" so the user sees a friendly word
        // instead of an internal state name like "SenderKeyTweaked".
        return Normalize(status) switch
        {
            "completed" => ("Completed", "bg-success"),
            "expired" => ("Expired", "bg-warning text-dark"),
            "returned" => ("Returned to sender", "bg-warning text-dark"),
            "" => ("Unknown", "bg-secondary"),
            _ => ("Pending", "bg-info text-dark"),
        };
    }

    /// <summary>Strip underscores and lowercase, so SCREAMING_SNAKE_CASE and PascalCase compare equal.</summary>
    private static string Normalize(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Replace("_", "").ToLowerInvariant();
}
