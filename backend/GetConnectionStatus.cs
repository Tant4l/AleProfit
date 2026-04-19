using System;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AllegroRecruitment
{
    public class GetConnectionStatus
    {
        private readonly ILogger<GetConnectionStatus> _logger;

        public GetConnectionStatus(ILogger<GetConnectionStatus> logger)
        {
            _logger = logger;
        }

        [Function("GetConnectionStatus")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!Guid.TryParse(query["clientId"], out Guid clientId)) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing SqlConnectionString");

            bool isConnected;
            try
            {
                using SqlConnection conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                using SqlCommand cmd = new SqlCommand("SELECT COUNT(1) FROM ClientAllegroTokens WHERE ClientId = @ClientId", conn);
                cmd.Parameters.Add("@ClientId", System.Data.SqlDbType.UniqueIdentifier).Value = clientId;
                var result = await cmd.ExecuteScalarAsync();
                isConnected = result != null && result != DBNull.Value && (int)result > 0;
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "Failed to query connection status for {ClientId}", clientId);
                return req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { connected = isConnected });
            return response;
        }
    }
}