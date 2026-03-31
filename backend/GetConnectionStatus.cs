using System;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace AllegroRecruitment
{
    public class GetConnectionStatus
    {
        [Function("GetConnectionStatus")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!Guid.TryParse(query["clientId"], out Guid clientId)) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

            bool isConnected = false;
            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString")!;

            using (SqlConnection conn = new SqlConnection(connStr))
            {
                await conn.OpenAsync();
                using (SqlCommand cmd = new SqlCommand("SELECT COUNT(1) FROM ClientAllegroTokens WHERE ClientId = @ClientId", conn))
                {
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    isConnected = (int)await cmd.ExecuteScalarAsync() > 0;
                }
            }

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { connected = isConnected });
            return response;
        }
    }
}