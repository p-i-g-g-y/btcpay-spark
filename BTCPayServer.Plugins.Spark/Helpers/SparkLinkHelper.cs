using NBitcoin;

namespace BTCPayServer.Plugins.Spark.Helpers;

/// <summary>
/// Builders for outgoing links / URIs (BIP-21 fund URI, mempool explorer, etc.) and short-form
/// display helpers for txids and outpoints.
/// </summary>
public static class SparkLinkHelper
{
    /// <summary>
    /// Build a <c>bitcoin:</c> URI suitable for displaying a fund-the-wallet QR code.
    /// Amount is in BTC (per BIP-21).
    /// </summary>
    public static string BuildBip21FundUri(string address, long? amountSats = null, string? label = null)
    {
        var qs = new List<string>();
        if (amountSats is > 0)
            qs.Add($"amount={SparkAmountConverter.FormatBtc(amountSats.Value)}");
        if (!string.IsNullOrWhiteSpace(label))
            qs.Add($"label={Uri.EscapeDataString(label)}");
        return qs.Count == 0 ? $"bitcoin:{address}" : $"bitcoin:{address}?{string.Join('&', qs)}";
    }

    public static string BuildLightningUri(string bolt11) => $"lightning:{bolt11}";

    /// <summary>Block-explorer transaction link; falls back to mempool.space for the active network.</summary>
    public static string GetTransactionLink(Network network, string txId)
    {
        var baseUrl = network.ChainName == ChainName.Mainnet
            ? "https://mempool.space"
            : network.ChainName == ChainName.Regtest
                ? "https://mempool.space/signet" // regtest has no public explorer; signet is closest
                : "https://mempool.space/testnet";
        return $"{baseUrl}/tx/{txId}";
    }

    public static string GetAddressLink(Network network, string address)
    {
        var baseUrl = network.ChainName == ChainName.Mainnet
            ? "https://mempool.space"
            : "https://mempool.space/testnet";
        return $"{baseUrl}/address/{address}";
    }

    /// <summary>Short-form id display (first 8 ... last 8).</summary>
    public static string ShortId(string? id)
        => string.IsNullOrEmpty(id) || id.Length <= 20 ? id ?? "" : $"{id[..8]}...{id[^8..]}";

    public static string FormatOutpoint(string txId, int vout) => $"{txId}:{vout}";

    public static string ShortOutpoint(string txId, int vout) => $"{ShortId(txId)}:{vout}";

    /// <summary>
    /// Reverses the byte order of a hex-encoded Bitcoin txid. Bitcoin's display form
    /// (mempool.space, RPCs, most explorers) is the byte-reverse of the internal/wire form
    /// used in raw transactions and some protobuf messages. NSpark's
    /// <c>GetUtxosForDepositAddressAsync</c> empirically returns the txid in internal-byte
    /// order even though its comment claims otherwise; we call this on ingest to normalise to
    /// display-byte order before persisting and using for further SSP calls.
    /// </summary>
    public static string ReverseHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return hex;
        var bytes = Convert.FromHexString(hex);
        Array.Reverse(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
