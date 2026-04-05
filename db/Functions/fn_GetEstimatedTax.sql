CREATE OR ALTER FUNCTION dbo.fn_GetEstimatedTax(
    @IncomeBeforeTax DECIMAL(12,2),
    @RevenueNet DECIMAL(12,2),
    @TaxRate DECIMAL(5,2)
)
RETURNS DECIMAL(12,2)
AS
BEGIN
    RETURN CAST(
        CASE
            WHEN @TaxRate = 8.50 THEN @RevenueNet * 0.085 -- Ryczałt (Revenue based)
            WHEN @TaxRate = 12.00 THEN CASE WHEN @IncomeBeforeTax > 0 THEN @IncomeBeforeTax * 0.21 ELSE 0 END -- Skala (12+9)
            WHEN @TaxRate = 19.00 THEN CASE WHEN @IncomeBeforeTax > 0 THEN @IncomeBeforeTax * 0.239 ELSE 0 END -- Liniowy (19+4.9)
            ELSE CASE WHEN @IncomeBeforeTax > 0 THEN @IncomeBeforeTax * (@TaxRate / 100.0) ELSE 0 END
        END
    AS DECIMAL(12,2));
END;
GO
