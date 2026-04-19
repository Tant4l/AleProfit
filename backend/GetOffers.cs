using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AllegroRecruitment
{
    public class GetOffers
    {
        private readonly ILogger<GetOffers> _logger;

        public GetOffers(ILogger<GetOffers> logger)
        {
            _logger = logger;
        }

        [Function("GetOffers")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!Guid.TryParse(query["clientId"], out Guid clientId))
            {
                return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            }

            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing SqlConnectionString");
            var offers = new List<object>();

            try
            {
                using SqlConnection conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                string sql = @"SELECT AllegroOfferId, ProductName, DefaultPurchasePriceNet,
                                     DefaultPackagingCostNet, VatRateValue
                               FROM OfferMasterData
                               WHERE ClientId = @ClientId
                               ORDER BY UpdatedAt DESC";

                using SqlCommand cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@ClientId", System.Data.SqlDbType.UniqueIdentifier).Value = clientId;
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    offers.Add(new
                    {
                        offerId = r.GetString(0),
                        name = r.IsDBNull(1) ? null : r.GetString(1),
                        cogs = r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2),
                        pkg = r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3),
                        vat = r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4)
                    });
                }
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "Failed to fetch offers for {ClientId}", clientId);
                return req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(offers);
            return response;
        }
    }
}