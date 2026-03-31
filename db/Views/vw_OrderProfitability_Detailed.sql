CREATE OR ALTER VIEW vw_OrderProfitability_Detailed AS
WITH OrderVatContext AS (
    SELECT
        InternalOrderId,
        SUM(UnitPriceGross * Quantity) as TotalGross,
        SUM(CAST(UnitPriceGross / (1 + (md.VatRateValue / 100.0)) * Quantity AS DECIMAL(12,2))) as TotalNet
    FROM OrderLineItems li
    JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId
    GROUP BY InternalOrderId
),
BillingAggregates AS (
    -- Dynamically calculate Net based on the specific VatRate of each fee
    SELECT
        AllegroOrderId,
        SUM(CASE WHEN cat.CategoryGroup = 'COMMISSION' THEN CAST(ABS(b.Amount) / (1 + (b.VatRate / 100.0)) AS DECIMAL(12,2)) ELSE 0 END) as CommissionsNet,
        SUM(CASE WHEN cat.CategoryGroup = 'LOGISTICS' THEN CAST(ABS(b.Amount) / (1 + (b.VatRate / 100.0)) AS DECIMAL(12,2)) ELSE 0 END) as CourierCostsNet
    FROM AllegroBillingEntries b
    JOIN AllegroFeeCategories cat ON b.FeeType = cat.FeeType
    GROUP BY AllegroOrderId
)
SELECT
    o.AllegroOrderId,
    o.ClientId,
    o.OrderDate AT TIME ZONE 'UTC' AT TIME ZONE 'Central European Standard Time' AS OrderDatePL,
    o.InternalStatus,
    CASE WHEN o.BuyerNip IS NOT NULL THEN 1 ELSE 0 END AS IsB2b,

    (o.TotalGrossAmount - ISNULL(ref.TotalRefundedGross, 0)) AS RevenueGross,

    -- Improved Net Revenue Calculation
    (ovc.TotalNet + CAST(o.ShippingRevenueGross / (1 + (o.ShippingVatRate / 100.0)) AS DECIMAL(12,2)) - ISNULL(ref.TotalRefundedNet, 0)) AS RevenueNet,

    ISNULL(SUM(md.DefaultPurchasePriceNet * li.Quantity), 0) AS TotalCogsNet,
    ISNULL(SUM(md.DefaultPackagingCostNet * li.Quantity), 0) AS TotalPackagingNet,

    ISNULL(ba.CommissionsNet, 0) AS CommissionsNet,
    ISNULL(ba.CourierCostsNet, 0) AS CourierCostsNet,

    -- Final Profitability (Income Before Tax)
    (
        (ovc.TotalNet + CAST(o.ShippingRevenueGross / (1 + (o.ShippingVatRate / 100.0)) AS DECIMAL(12,2)) - ISNULL(ref.TotalRefundedNet, 0))
        - (ISNULL(SUM(md.DefaultPurchasePriceNet * li.Quantity), 0) + ISNULL(SUM(md.DefaultPackagingCostNet * li.Quantity), 0))
        - (ISNULL(ba.CommissionsNet, 0) + ISNULL(ba.CourierCostsNet, 0))
    ) AS IncomeBeforeTax

FROM AllegroOrders o
LEFT JOIN OrderLineItems li ON o.InternalId = li.InternalOrderId
LEFT JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId
LEFT JOIN OrderVatContext ovc ON o.InternalId = ovc.InternalOrderId
LEFT JOIN BillingAggregates ba ON o.AllegroOrderId = ba.AllegroOrderId
LEFT JOIN (
    -- Better refund logic: Use stored RefundAmountNet if available, else weighted context
    SELECT
        r.InternalOrderId,
        SUM(r.RefundAmountGross) AS TotalRefundedGross,
        SUM(CASE WHEN r.RefundAmountNet > 0 THEN r.RefundAmountNet
                 ELSE CAST(r.RefundAmountGross * (ovc.TotalNet / NULLIF(ovc.TotalGross, 0)) AS DECIMAL(12,2))
            END) AS TotalRefundedNet
    FROM OrderRefunds r
    JOIN OrderVatContext ovc ON r.InternalOrderId = ovc.InternalOrderId
    GROUP BY r.InternalOrderId
) ref ON o.InternalId = ref.InternalOrderId
WHERE o.InternalStatus <> 'CANCELLED'
GROUP BY
    o.AllegroOrderId, o.ClientId, o.OrderDate, o.BuyerNip, o.TotalGrossAmount,
    o.ShippingRevenueGross, o.ShippingVatRate, o.InternalStatus,
    ref.TotalRefundedGross, ref.TotalRefundedNet, ovc.TotalNet, ba.CommissionsNet, ba.CourierCostsNet;
GO
