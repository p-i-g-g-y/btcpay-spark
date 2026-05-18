namespace BTCPayServer.Plugins.Spark.Data.Entities;

public enum SparkLightningInvoiceStatus
{
    Pending = 0,
    Paid = 1,
    Expired = 2,
    Cancelled = 3,
}

public enum SparkWalletDepositStatus
{
    /// <summary>UTXO discovered at the static deposit address; not yet claimed.</summary>
    Discovered = 0,

    /// <summary><c>ClaimStaticDepositAsync</c> succeeded; pending transfer issued by SSP.</summary>
    ClaimedToTransfer = 1,

    /// <summary><c>ClaimPendingTransfersAsync</c> succeeded; sats are spendable.</summary>
    Settled = 2,

    /// <summary>Permanently failed after exhausting retries.</summary>
    Failed = 3,
}

public enum SparkWalletWithdrawalStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
}

public enum SparkOutgoingPaymentStatus
{
    /// <summary>Submitted to the SSP; awaiting settlement on the receiving Lightning node.</summary>
    Pending = 0,

    /// <summary>SSP reported the payment settled. <c>Preimage</c> and <c>ActualFeeSats</c> are populated.</summary>
    Succeeded = 1,

    /// <summary>SSP reported the payment failed. Funds are returned to the wallet on HTLC timeout if not already.</summary>
    Failed = 2,
}
