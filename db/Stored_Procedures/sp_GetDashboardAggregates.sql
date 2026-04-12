CREATE OR ALTER PROCEDURE sp_GetDashboardAggregates
    @ClientId UNIQUEIDENTIFIER,
    @StartDatePL DATETIMEOFFSET,
    @EndDatePL DATETIMEOFFSET,
    @IncomeTaxRate DECIMAL(5,2)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        COALESCE(Agg.GrandTotalRevenueNet, 0) AS GrandTotalRevenueNet,
        COALESCE(Agg.TotalCOGS, 0) AS TotalCOGS,
        COALESCE(Agg.TotalPackaging, 0) AS TotalPackaging,
        COALESCE(Agg.TotalAllegroCommissions, 0) AS TotalAllegroCommissions,
        COALESCE(Agg.TotalCourierCosts, 0) AS TotalCourierCosts,
        Tax.EstimatedIncomeTax,
        COALESCE(Agg.TotalIncomeBeforeTax, 0) - Tax.EstimatedIncomeTax AS PureProfitAfterTax
    FROM (
        SELECT
            SUM(RevenueNet) AS GrandTotalRevenueNet,
            SUM(TotalCogsNet) AS TotalCOGS,
            SUM(TotalPackagingNet) AS TotalPackaging,
            SUM(CommissionsNet) AS TotalAllegroCommissions,
            SUM(CourierCostsNet) AS TotalCourierCosts,
            SUM(IncomeBeforeTax) AS TotalIncomeBeforeTax
        FROM vw_OrderProfitability_Detailed
        WHERE ClientId = @ClientId AND OrderDatePL >= @StartDatePL AND OrderDatePL <= @EndDatePL
    ) AS Agg
    CROSS APPLY (
        SELECT dbo.fn_GetEstimatedTax(Agg.TotalIncomeBeforeTax, Agg.GrandTotalRevenueNet, @IncomeTaxRate) AS EstimatedIncomeTax
    ) AS Tax;
END;
GO
