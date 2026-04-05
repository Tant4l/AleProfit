using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AllegroRecruitment
{
    public class GetDashboardSummary
    {
        private readonly ILogger<GetDashboardSummary> _logger;

        public GetDashboardSummary(ILogger<GetDashboardSummary> logger)
        {
            _logger = logger;
        }

        [Function("GetDashboardSummary")]
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
            catch { polishTime = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time"); } // Windows fallback

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

            string connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing SqlConnectionString");

            var result = new Dictionary<string, object>();

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();
                using (SqlCommand cmd = new SqlCommand("sp_GetDashboardAggregates", conn))
                {
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    cmd.Parameters.AddWithValue("@StartDatePL", startDate);
                    cmd.Parameters.AddWithValue("@EndDatePL", endDate);
                    cmd.Parameters.AddWithValue("@IncomeTaxRate", taxRate);

                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                result.Add(reader.GetName(i), reader.IsDBNull(i) ? 0.00m : reader.GetValue(i));
                            }
                        }
                    }
                }
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
    }
}
