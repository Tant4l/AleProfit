using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace AllegroRecruitment
{
    public class GetOffers
    {
        [Function("GetOffers")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!Guid.TryParse(query["clientId"], out Guid clientId))
            {
                return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            }

            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString")!;
            var offers = new List<object>();

            using (SqlConnection conn = new SqlConnection(connStr))
            {
                await conn.OpenAsync();
                string sql = @"SELECT AllegroOfferId, ProductName, DefaultPurchasePriceNet, 
                                     DefaultPackagingCostNet, VatRateValue 
                               FROM OfferMasterData 
                               WHERE ClientId = @ClientId 
                               ORDER BY UpdatedAt DESC";
                               
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            offers.Add(new { 
                                offerId = r.GetString(0), 
                                name = r.GetString(1), 
                                cogs = r.GetDecimal(2), 
                                pkg = r.GetDecimal(3), 
                                vat = r.GetDecimal(4) 
                            });
                        }
                    }
                }
            }
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(offers);
            return response;
        }
    }
}