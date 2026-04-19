using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using AllegroRecruitment.Models;

namespace AllegroRecruitment
{
    public class GetOrderDetails
    {
        private readonly ILogger<GetOrderDetails> _logger;

        public GetOrderDetails(ILogger<GetOrderDetails> logger)
        {
            _logger = logger;
        }

        [Function("GetOrderDetails")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!Guid.TryParse(query["clientId"], out Guid clientId))
            {
                var err = req.CreateResponse(HttpStatusCode.BadRequest);
                await err.WriteStringAsync("Missing or invalid clientId.");
                return err;
            }

            // 1. Timezone and Date Range Logic
            TimeZoneInfo polishTime;
            try { polishTime = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"); }
            catch (TimeZoneNotFoundException)
            {
                try { polishTime = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time"); }
                catch (TimeZoneNotFoundException ex)
                {
                    _logger.LogError(ex, "Polish time zone not available on host.");
                    return req.CreateResponse(HttpStatusCode.InternalServerError);
                }
            }

            DateTimeOffset nowInPoland = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, polishTime);

            DateTimeOffset startDate = DateTimeOffset.TryParse(query["startDate"], out var sDate)
                ? sDate
                : new DateTimeOffset(nowInPoland.Year, nowInPoland.Month, 1, 0, 0, 0, polishTime.GetUtcOffset(nowInPoland));

            DateTimeOffset endDate = DateTimeOffset.TryParse(query["endDate"], out var eDate)
                ? eDate
                : startDate.AddMonths(1).AddTicks(-1);

            decimal taxRate = decimal.TryParse(query["taxRate"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tRate)
                ? tRate
                : 23.90m;

            int offset = int.TryParse(query["offset"], out int o) ? Math.Max(0, o) : 0;
            int limit = int.TryParse(query["limit"], out int l) ? l : 50;
            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            string connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing SqlConnectionString");

            var orders = new List<OrderProfitabilityDto>();

            try
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync();

                    string sqlQuery = @"
                        SELECT
                            AllegroOrderId,
                            OrderDatePL,
                            InternalStatus,
                            IsB2b,
                            ProductSummary,
                            RevenueGross,
                            RevenueNet,
                            TotalCogsNet,
                            TotalPackagingNet,
                            CommissionsNet,
                            CourierCostsNet,
                            IncomeBeforeTax,
                            dbo.fn_GetEstimatedTax(IncomeBeforeTax, RevenueNet, @TaxRate) AS EstimatedTax,
                            (IncomeBeforeTax - dbo.fn_GetEstimatedTax(IncomeBeforeTax, RevenueNet, @TaxRate)) AS PureProfitAfterTax
                        FROM vw_OrderProfitability_Detailed
                        WHERE ClientId = @ClientId
                        AND OrderDatePL >= @StartDate
                        AND OrderDatePL <= @EndDate
                        ORDER BY OrderDatePL DESC
                        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;";

                    using (SqlCommand cmd = new SqlCommand(sqlQuery, conn))
                    {
                        cmd.Parameters.Add("@ClientId", System.Data.SqlDbType.UniqueIdentifier).Value = clientId;
                        cmd.Parameters.Add("@StartDate", System.Data.SqlDbType.DateTimeOffset).Value = startDate;
                        cmd.Parameters.Add("@EndDate", System.Data.SqlDbType.DateTimeOffset).Value = endDate;
                        cmd.Parameters.Add("@TaxRate", System.Data.SqlDbType.Decimal).Value = taxRate;
                        cmd.Parameters.Add("@Offset", System.Data.SqlDbType.Int).Value = offset;
                        cmd.Parameters.Add("@Limit", System.Data.SqlDbType.Int).Value = limit;

                        using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                        {
                            decimal Dec(int i) => reader.IsDBNull(i) ? 0m : reader.GetDecimal(i);
                            string Str(int i) => reader.IsDBNull(i) ? string.Empty : reader.GetString(i);

                            while (await reader.ReadAsync())
                            {
                                orders.Add(new OrderProfitabilityDto(
                                    reader.GetGuid(0).ToString(),
                                    reader.GetDateTimeOffset(1),
                                    Str(2),
                                    !reader.IsDBNull(3) && reader.GetBoolean(3),
                                    Str(4),
                                    Dec(5),
                                    Dec(6),
                                    Dec(7),
                                    Dec(8),
                                    Dec(9),
                                    Dec(10),
                                    Dec(11),
                                    Dec(12),
                                    Dec(13)
                                ));
                            }
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "SQL Error fetching order details for client {ClientId}", clientId);
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(orders);
            return response;
        }
    }
}
