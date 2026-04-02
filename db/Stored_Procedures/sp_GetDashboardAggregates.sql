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

        CAST(
            CASE
                WHEN @IncomeTaxRate = 8.50 THEN SUM(RevenueNet) * (@IncomeTaxRate / 100.0)
                ELSE CASE WHEN SUM(IncomeBeforeTax) > 0 THEN SUM(IncomeBeforeTax) * (@IncomeTaxRate / 100.0) ELSE 0 END
            END
        AS DECIMAL(12,2)) AS EstimatedIncomeTax,

        CAST(
            SUM(IncomeBeforeTax) -
            CASE
                WHEN @IncomeTaxRate = 8.50 THEN SUM(RevenueNet) * (@IncomeTaxRate / 100.0)
                ELSE CASE WHEN SUM(IncomeBeforeTax) > 0 THEN SUM(IncomeBeforeTax) * (@IncomeTaxRate / 100.0) ELSE 0 END
            END
        AS DECIMAL(12,2)) AS PureProfitAfterTax

    FROM vw_OrderProfitability_Detailed
    WHERE ClientId = @ClientId AND OrderDatePL >= @StartDatePL AND OrderDatePL <= @EndDatePL;
END;
