CREATE OR ALTER VIEW vw_OrderProfitability_Detailed AS
SELECT
    o.AllegroOrderId,
    o.ClientId,
    o.OrderDate AT TIME ZONE 'UTC' AT TIME ZONE 'Central European Standard Time' AS OrderDatePL,
    o.IsB2b,

    o.TotalGrossAmount AS RevenueGross,
    ISNULL(SUM(CAST(li.UnitPriceGross / (1 + (md.VatRate / 100.0)) * li.Quantity AS DECIMAL(12,2))), 0)
        + CAST(o.ShippingRevenueGross / 1.23 AS DECIMAL(12,2)) AS RevenueNet,

    ISNULL(SUM(md.DefaultPurchasePriceNet * li.Quantity), 0) AS TotalCogsNet,
    ISNULL(SUM(md.DefaultPackagingCostNet * li.Quantity), 0) AS TotalPackagingNet,

    ISNULL((SELECT CAST((SUM(Amount) * -1) / 1.23 AS DECIMAL(12,2))
            FROM AllegroBillingEntries
            WHERE AllegroOrderId = o.AllegroOrderId
              AND FeeType NOT IN ('WZA_FEE', 'DELIVERY_FEE', 'PAD')), 0) AS CommissionsNet,

    ISNULL((SELECT CAST((SUM(Amount) * -1) / 1.23 AS DECIMAL(12,2))
            FROM AllegroBillingEntries
            WHERE AllegroOrderId = o.AllegroOrderId
              AND FeeType IN ('WZA_FEE', 'DELIVERY_FEE')), 0) AS CourierCostsNet

FROM AllegroOrders o
LEFT JOIN OrderLineItems li ON o.InternalId = li.InternalOrderId
LEFT JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId
WHERE o.IsDeleted = 0
GROUP BY
    o.AllegroOrderId, o.ClientId, o.OrderDate, o.IsB2b, o.TotalGrossAmount, o.ShippingRevenueGross;
GO
