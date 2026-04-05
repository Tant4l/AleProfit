CREATE OR ALTER PROCEDURE sp_GetDashboardAggregates
    @ClientId UNIQUEIDENTIFIER,
    @StartDatePL DATETIMEOFFSET,
    @EndDatePL DATETIMEOFFSET,
    @IncomeTaxRate DECIMAL(5,2)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        SUM(RevenueNet) AS GrandTotalRevenueNet,
        SUM(TotalCogsNet) AS TotalCOGS,
        SUM(TotalPackagingNet) AS TotalPackaging,
        SUM(CommissionsNet) AS TotalAllegroCommissions,
        SUM(CourierCostsNet) AS TotalCourierCosts,

        dbo.fn_GetEstimatedTax(SUM(IncomeBeforeTax), SUM(RevenueNet), @IncomeTaxRate) AS EstimatedIncomeTax,

        SUM(IncomeBeforeTax) - dbo.fn_GetEstimatedTax(SUM(IncomeBeforeTax), SUM(RevenueNet), @IncomeTaxRate) AS PureProfitAfterTax

    FROM vw_OrderProfitability_Detailed
    WHERE ClientId = @ClientId AND OrderDatePL >= @StartDatePL AND OrderDatePL <= @EndDatePL;
END;
GO
