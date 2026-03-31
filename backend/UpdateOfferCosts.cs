using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace AllegroRecruitment
{
    public class UpdateOfferCosts
    {
        public record OfferUpdateDto(Guid ClientId, string OfferId, decimal Cogs, decimal Pkg);

        [Function("UpdateOfferCosts")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
        {
            var dto = await JsonSerializer.DeserializeAsync<OfferUpdateDto>(req.Body, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (dto == null || string.IsNullOrWhiteSpace(dto.OfferId) || dto.ClientId == Guid.Empty)
            {
                var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                await badReq.WriteStringAsync("Invalid request payload: Missing ClientId or OfferId.");
                return badReq;
            }

            if (dto.Cogs < 0 || dto.Pkg < 0)
            {
                var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                await badReq.WriteStringAsync("Financial values (COGS/Packaging) cannot be negative.");
                return badReq;
            }

            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString") 
                ?? throw new InvalidOperationException("Missing SqlConnectionString");

            try 
            {
                using (SqlConnection conn = new SqlConnection(connStr))
                {
                    await conn.OpenAsync();
                    string sql = @"UPDATE OfferMasterData 
                                   SET DefaultPurchasePriceNet = @Cogs, 
                                       DefaultPackagingCostNet = @Pkg, 
                                       UpdatedAt = SYSDATETIMEOFFSET() 
                                   WHERE AllegroOfferId = @OfferId AND ClientId = @ClientId";
                                   
                    using (SqlCommand cmd = new SqlCommand(sql, conn))
                    {
                        // Explicitly defining SqlDbType to match schema DECIMAL(12,2)
                        cmd.Parameters.Add("@Cogs", System.Data.SqlDbType.Decimal).Value = dto.Cogs;
                        cmd.Parameters.Add("@Pkg", System.Data.SqlDbType.Decimal).Value = dto.Pkg;
                        cmd.Parameters.AddWithValue("@OfferId", dto.OfferId);
                        cmd.Parameters.AddWithValue("@ClientId", dto.ClientId);
                        
                        int rows = await cmd.ExecuteNonQueryAsync();
                        if (rows == 0) return req.CreateResponse(HttpStatusCode.NotFound);
                    }
                }
            }
            catch (SqlException)
            {
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            return req.CreateResponse(HttpStatusCode.OK);
        }
    }
}