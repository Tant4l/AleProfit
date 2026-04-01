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
            catch { polishTime = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time"); }

            DateTimeOffset nowInPoland = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, polishTime);

            DateTimeOffset startDate = DateTimeOffset.TryParse(query["startDate"], out var sDate)
                ? sDate
                : new DateTimeOffset(nowInPoland.Year, nowInPoland.Month, 1, 0, 0, 0, polishTime.GetUtcOffset(nowInPoland));

            DateTimeOffset endDate = DateTimeOffset.TryParse(query["endDate"], out var eDate)
                ? eDate
                : startDate.AddMonths(1).AddTicks(-1);

            int offset = int.TryParse(query["offset"], out int o) ? o : 0;
            int limit = int.TryParse(query["limit"], out int l) ? l : 50;
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
                            ProductSummary, -- Index 4
                            RevenueGross, 
                            RevenueNet, 
                            TotalCogsNet, 
                            TotalPackagingNet, 
                            CommissionsNet, 
                            CourierCostsNet, 
                            IncomeBeforeTax
                        FROM vw_OrderProfitability_Detailed
                        WHERE ClientId = @ClientId
                        AND OrderDatePL >= @StartDate
                        AND OrderDatePL <= @EndDate
                        ORDER BY OrderDatePL DESC
                        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;";

                    using (SqlCommand cmd = new SqlCommand(sqlQuery, conn))
                    {
                        cmd.Parameters.AddWithValue("@ClientId", clientId);
                        cmd.Parameters.AddWithValue("@StartDate", startDate);
                        cmd.Parameters.AddWithValue("@EndDate", endDate);
                        cmd.Parameters.AddWithValue("@Offset", offset);
                        cmd.Parameters.AddWithValue("@Limit", limit);

                        using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                orders.Add(new OrderProfitabilityDto(
                                    reader.GetGuid(0).ToString(),      
                                    reader.GetDateTimeOffset(1),       
                                    reader.GetString(2),               
                                    reader.GetBoolean(3),              
                                    reader.GetString(4),               // ProductSummary
                                    reader.GetDecimal(5),              // RevenueGross
                                    reader.GetDecimal(6),              // RevenueNet
                                    reader.GetDecimal(7),              // TotalCogsNet
                                    reader.GetDecimal(8),              // TotalPackagingNet
                                    reader.GetDecimal(9),              // CommissionsNet
                                    reader.GetDecimal(10),             // CourierCostsNet
                                    reader.GetDecimal(11)              // IncomeBeforeTax
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