CREATE VIEW vw_OrderProfitability AS
SELECT
    o.AllegroOrderId,
    o.TotalGrossAmount AS Revenue,
    ISNULL(SUM(b.Amount), 0) AS TotalAllegroFees,
    (o.TotalGrossAmount + ISNULL(SUM(b.Amount), 0)) AS NetProfit BeforeTax
FROM AllegroOrders o
LEFT JOIN AllegroBillingEntries b ON o.AllegroOrderId = b.AllegroOrderId
GROUP BY o.AllegroOrderId, o.TotalGrossAmount;
