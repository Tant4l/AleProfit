namespace AllegroRecruitment.Models
{
    public record OrderProfitabilityDto(
        string AllegroOrderId,
        DateTimeOffset OrderDatePL,
        string InternalStatus,
        bool IsB2b,
        decimal RevenueGross,
        decimal RevenueNet,
        decimal TotalCogsNet,
        decimal TotalPackagingNet,
        decimal CommissionsNet,
        decimal CourierCostsNet,
        decimal IncomeBeforeTax
    );
}