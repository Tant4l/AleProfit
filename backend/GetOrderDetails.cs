using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

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
                var err = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await err.WriteStringAsync("Missing or invalid clientId.");
                return err;
            }

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

            var orders = new List<Dictionary<string, object>>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();

                string sqlQuery = @"
                    SELECT *
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
                            var row = new Dictionary<string, object>();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            orders.Add(row);
                        }
                    }
                }
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(orders);
            return response;
        }
    }
}
