using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Spark.Data.Entities;
using NBitcoin;

namespace BTCPayServer.Plugins.Spark.Lightning;

/// <summary>
/// Translates between our persisted <see cref="SparkLightningInvoiceEntity"/> and BTCPay's
/// <see cref="LightningInvoice"/> contract.
/// </summary>
public static class SparkLightningInvoiceMapper
{
    public static LightningInvoice ToBtcPay(SparkLightningInvoiceEntity entity, Network network)
    {
        var status = entity.Status switch
        {
            SparkLightningInvoiceStatus.Paid => LightningInvoiceStatus.Paid,
            SparkLightningInvoiceStatus.Expired => LightningInvoiceStatus.Expired,
            SparkLightningInvoiceStatus.Cancelled => LightningInvoiceStatus.Expired,
            _ => LightningInvoiceStatus.Unpaid,
        };

        LightMoney amount;
        DateTimeOffset expires;
        try
        {
            var pr = BOLT11PaymentRequest.Parse(entity.Bolt11, network);
            amount = pr.MinimumAmount ?? LightMoney.Satoshis(entity.AmountSats);
            expires = pr.ExpiryDate;
        }
        catch
        {
            amount = LightMoney.Satoshis(entity.AmountSats);
            expires = entity.ExpiresAt;
        }

        return new LightningInvoice
        {
            Id = entity.RequestId,
            Amount = amount,
            Status = status,
            ExpiresAt = expires,
            BOLT11 = entity.Bolt11,
            PaymentHash = entity.PaymentHash,
            PaidAt = entity.PaidAt?.ToUniversalTime(),
        };
    }
}
