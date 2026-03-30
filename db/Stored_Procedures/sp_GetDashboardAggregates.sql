CREATE   PROCEDURE sp_GetDashboardAggregates
    @ClientId UNIQUEIDENTIFIER,
    @StartDatePL DATETIMEOFFSET,
    @EndDatePL DATETIMEOFFSET,
    @IncomeTaxRate DECIMAL(5,2) -- e.g., 19.00 for Liniowy
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        COUNT(AllegroOrderId) AS TotalOrdersProcessed,
        SUM(RevenueGross) AS GrandTotalRevenueGross,
        SUM(RevenueNet) AS GrandTotalRevenueNet,

        -- Deductions
        SUM(TotalCogsNet) AS TotalCOGS,
        SUM(TotalPackagingNet) AS TotalPackaging,
        SUM(CommissionsNet) AS TotalAllegroCommissions,
        SUM(CourierCostsNet) AS TotalCourierCosts,

        SUM(RevenueNet)
            - SUM(TotalCogsNet)
            - SUM(TotalPackagingNet)
            - SUM(CommissionsNet)
            - SUM(CourierCostsNet) AS IncomeBeforeTax,

        CAST((SUM(RevenueNet) - SUM(TotalCogsNet) - SUM(TotalPackagingNet) - SUM(CommissionsNet) - SUM(CourierCostsNet)) * (@IncomeTaxRate / 100.0) AS DECIMAL(12,2)) AS EstimatedIncomeTax,

        CAST((SUM(RevenueNet) - SUM(TotalCogsNet) - SUM(TotalPackagingNet) - SUM(CommissionsNet) - SUM(CourierCostsNet)) * (1 - (@IncomeTaxRate / 100.0)) AS DECIMAL(12,2)) AS PureProfitAfterTax

    FROM vw_OrderProfitability_Detailed
    WHERE ClientId = @ClientId
      AND OrderDatePL >= @StartDatePL
      AND OrderDatePL <= @EndDatePL;
END;
