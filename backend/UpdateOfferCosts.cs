using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AllegroRecruitment
{
    public class UpdateOfferCosts
    {
        private readonly ILogger<UpdateOfferCosts> _logger;

        public UpdateOfferCosts(ILogger<UpdateOfferCosts> logger)
        {
            _logger = logger;
        }

        public record OfferUpdateDto(Guid ClientId, string OfferId, decimal Cogs, decimal Pkg, decimal VatRate);

        [Function("UpdateOfferCosts")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
        {
            var options = new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            };

            OfferUpdateDto? dto;
            try
            {
                dto = await JsonSerializer.DeserializeAsync<OfferUpdateDto>(req.Body, options);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Malformed UpdateOfferCosts payload.");
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (dto == null || string.IsNullOrWhiteSpace(dto.OfferId) || dto.ClientId == Guid.Empty
                || dto.Cogs < 0 || dto.Pkg < 0 || dto.VatRate < 0)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing SqlConnectionString");

            int rowsAffected;
            try
            {
                using SqlConnection conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                string sql = @"UPDATE OfferMasterData
                               SET DefaultPurchasePriceNet = @Cogs,
                                   DefaultPackagingCostNet = @Pkg,
                                   VatRateValue = @VatRate,
                                   IsVatSynced = 1,
                                   UpdatedAt = SYSDATETIMEOFFSET()
                               WHERE AllegroOfferId = @OfferId AND ClientId = @ClientId";

                using SqlCommand cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@Cogs", System.Data.SqlDbType.Decimal, 9).Value = dto.Cogs;
                cmd.Parameters["@Cogs"].Precision = 18; cmd.Parameters["@Cogs"].Scale = 4;
                cmd.Parameters.Add("@Pkg", System.Data.SqlDbType.Decimal).Value = dto.Pkg;
                cmd.Parameters["@Pkg"].Precision = 18; cmd.Parameters["@Pkg"].Scale = 4;
                cmd.Parameters.Add("@VatRate", System.Data.SqlDbType.Decimal).Value = dto.VatRate;
                cmd.Parameters["@VatRate"].Precision = 18; cmd.Parameters["@VatRate"].Scale = 4;
                cmd.Parameters.Add("@OfferId", System.Data.SqlDbType.NVarChar, 64).Value = dto.OfferId;
                cmd.Parameters.Add("@ClientId", System.Data.SqlDbType.UniqueIdentifier).Value = dto.ClientId;

                rowsAffected = await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "Failed to update offer costs for {ClientId}/{OfferId}", dto.ClientId, dto.OfferId);
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            return req.CreateResponse(rowsAffected == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        }
    }
}