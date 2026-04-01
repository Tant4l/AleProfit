using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace AllegroRecruitment
{
    public class UpdateOfferCosts
    {
        public record OfferUpdateDto(Guid ClientId, string OfferId, decimal Cogs, decimal Pkg, decimal VatRate);

        [Function("UpdateOfferCosts")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
        {
            var options = new JsonSerializerOptions { 
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString 
            };
            
            var dto = await JsonSerializer.DeserializeAsync<OfferUpdateDto>(req.Body, options);

            if (dto == null || string.IsNullOrWhiteSpace(dto.OfferId) || dto.ClientId == Guid.Empty)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString")!;

            try 
            {
                using (SqlConnection conn = new SqlConnection(connStr))
                {
                    await conn.OpenAsync();
                    string sql = @"UPDATE OfferMasterData 
                                   SET DefaultPurchasePriceNet = @Cogs, 
                                       DefaultPackagingCostNet = @Pkg, 
                                       VatRateValue = @VatRate,
                                       IsVatSynced = 1,
                                       UpdatedAt = SYSDATETIMEOFFSET() 
                                   WHERE AllegroOfferId = @OfferId AND ClientId = @ClientId";
                                   
                    using (SqlCommand cmd = new SqlCommand(sql, conn))
                    {
                        cmd.Parameters.Add("@Cogs", System.Data.SqlDbType.Decimal).Value = dto.Cogs;
                        cmd.Parameters.Add("@Pkg", System.Data.SqlDbType.Decimal).Value = dto.Pkg;
                        cmd.Parameters.Add("@VatRate", System.Data.SqlDbType.Decimal).Value = dto.VatRate;
                        cmd.Parameters.AddWithValue("@OfferId", dto.OfferId);
                        cmd.Parameters.AddWithValue("@ClientId", dto.ClientId);
                        
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (SqlException) { return req.CreateResponse(HttpStatusCode.InternalServerError); }

            return req.CreateResponse(HttpStatusCode.OK);
        }
    }
}