CREATE OR ALTER VIEW vw_OrderProfitability_Detailed AS
WITH OrderVatContext AS (
    SELECT
        li.InternalOrderId,
        SUM(li.UnitPriceGross * li.Quantity) as TotalGross,
        SUM(CAST(li.UnitPriceGross / (1 + (md.VatRateValue / 100.0)) * li.Quantity AS DECIMAL(12,2))) as TotalNet
    FROM OrderLineItems li
    JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId
    GROUP BY li.InternalOrderId
),
ItemAggregates AS (
    SELECT
        li.InternalOrderId,
        ISNULL(STRING_AGG(CAST(md.ProductName AS NVARCHAR(MAX)), ' | '), 'Unknown Items') AS ProductSummary,
        SUM(md.DefaultPurchasePriceNet * li.Quantity) AS TotalCogsNet,
        SUM(md.DefaultPackagingCostNet * li.Quantity) AS TotalPackagingNet
    FROM OrderLineItems li
    LEFT JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId
    GROUP BY li.InternalOrderId
),
BillingAggregates AS (
    SELECT
        b.AllegroOrderId,
        SUM(CASE WHEN ISNULL(cat.CategoryGroup, 'COMMISSION') = 'COMMISSION'
                 THEN CAST(ABS(b.Amount) / (1 + (b.VatRate / 100.0)) AS DECIMAL(12,2)) ELSE 0 END) as CommissionsNet,
        SUM(CASE WHEN cat.CategoryGroup = 'LOGISTICS'
                 THEN CAST(ABS(b.Amount) / (1 + (b.VatRate / 100.0)) AS DECIMAL(12,2)) ELSE 0 END) as CourierCostsNet
    FROM AllegroBillingEntries b
    LEFT JOIN AllegroFeeCategories cat ON b.FeeType = cat.FeeType
    WHERE b.AllegroOrderId IS NOT NULL
    GROUP BY b.AllegroOrderId
),
RefundAggregates AS (
    SELECT
        r.InternalOrderId,
        SUM(r.RefundAmountGross) AS TotalRefundedGross,
        SUM(CASE WHEN r.RefundAmountNet > 0 THEN r.RefundAmountNet
                 ELSE CAST(r.RefundAmountGross * (ISNULL(ovc.TotalNet, 0) / NULLIF(ovc.TotalGross, 0)) AS DECIMAL(12,2))
            END) AS TotalRefundedNet
    FROM OrderRefunds r
    JOIN OrderVatContext ovc ON r.InternalOrderId = ovc.InternalOrderId
    GROUP BY r.InternalOrderId
)
SELECT
    o.AllegroOrderId,
    o.ClientId,
    o.OrderDate AT TIME ZONE 'UTC' AT TIME ZONE 'Central European Standard Time' AS OrderDatePL,
    o.InternalStatus,
    CAST(CASE WHEN o.BuyerNip IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsB2b,
    ia.ProductSummary,

    CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0
         ELSE (o.TotalGrossAmount - ISNULL(ref.TotalRefundedGross, 0)) END AS RevenueGross,

    CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0
         ELSE (ISNULL(ovc.TotalNet, 0) + CAST(o.ShippingRevenueGross / (1 + (o.ShippingVatRate / 100.0)) AS DECIMAL(12,2)) - ISNULL(ref.TotalRefundedNet, 0)) END AS RevenueNet,

    CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0 ELSE ISNULL(ia.TotalCogsNet, 0) END AS TotalCogsNet,
    CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0 ELSE ISNULL(ia.TotalPackagingNet, 0) END AS TotalPackagingNet,

    CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0 ELSE ISNULL(ba.CommissionsNet, 0) END AS CommissionsNet,

    CASE
        WHEN o.InternalStatus = 'CANCELLED' THEN 0
        WHEN ISNULL(ba.CourierCostsNet, 0) > 0 THEN ba.CourierCostsNet
        ELSE CAST(o.ShippingRevenueGross / (1 + (o.ShippingVatRate / 100.0)) AS DECIMAL(12,2))
    END AS CourierCostsNet,

    (
        (CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0 ELSE (ISNULL(ovc.TotalNet, 0) + CAST(o.ShippingRevenueGross / (1 + (o.ShippingVatRate / 100.0)) AS DECIMAL(12,2)) - ISNULL(ref.TotalRefundedNet, 0)) END)
        - (CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0 ELSE (ISNULL(ia.TotalCogsNet, 0) + ISNULL(ia.TotalPackagingNet, 0)) END)

        - (CASE WHEN o.InternalStatus = 'CANCELLED' THEN 0 ELSE ISNULL(ba.CommissionsNet, 0) END)
        - (CASE
            WHEN o.InternalStatus = 'CANCELLED' THEN 0
            WHEN ISNULL(ba.CourierCostsNet, 0) > 0 THEN ba.CourierCostsNet
            ELSE CAST(o.ShippingRevenueGross / (1 + (o.ShippingVatRate / 100.0)) AS DECIMAL(12,2))
          END)
    ) AS IncomeBeforeTax

FROM AllegroOrders o
LEFT JOIN OrderVatContext ovc ON o.InternalId = ovc.InternalOrderId
LEFT JOIN ItemAggregates ia ON o.InternalId = ia.InternalOrderId
LEFT JOIN BillingAggregates ba ON o.AllegroOrderId = ba.AllegroOrderId
LEFT JOIN RefundAggregates ref ON o.InternalId = ref.InternalOrderId;
GO
