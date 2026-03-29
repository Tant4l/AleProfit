using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AllegroRecruitment
{
    public class SyncAllegroOrders
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly ILogger<SyncAllegroOrders> _logger;

        public SyncAllegroOrders(ILogger<SyncAllegroOrders> logger)
        {
            _logger = logger;
        }[Function("SyncAllegroOrders")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("Initiating Allegro Order Sync.");

            var queryDictionary = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            string? clientIdString = queryDictionary["clientId"];

            if (!Guid.TryParse(clientIdString, out Guid clientId))
            {
                var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Valid clientId parameter is required.");
                return badResponse;
            }

            string sqlConnectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing SqlConnectionString");

            string accessToken = string.Empty;
            string refreshToken = string.Empty;
            bool needsRefresh = false;

            // 1. Retrieve Token from Database
            using (SqlConnection conn = new SqlConnection(sqlConnectionString))
            {
                await conn.OpenAsync();
                using (SqlCommand cmd = new SqlCommand("sp_GetValidToken", conn))
                {
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            accessToken = reader.GetString(0);
                            refreshToken = reader.GetString(1);
                            needsRefresh = reader.GetInt32(2) == 1;
                        }
                        else
                        {
                            var unauthResponse = req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);
                            await unauthResponse.WriteStringAsync("No valid Allegro tokens found for this ClientId.");
                            return unauthResponse;
                        }
                    }
                }
            }

            if (needsRefresh)
            {
                _logger.LogInformation("Access Token expired. Negotiating refresh with Allegro.");
                string allegroClientId = Environment.GetEnvironmentVariable("Allegro_ClientId") ?? throw new Exception("Missing ClientId");
                string allegroClientSecret = Environment.GetEnvironmentVariable("Allegro_ClientSecret") ?? throw new Exception("Missing Secret");

                var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "https://allegro.pl.allegrosandbox.pl/auth/oauth/token");
                var authBytes = Encoding.ASCII.GetBytes($"{allegroClientId}:{allegroClientSecret}");
                refreshRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
                refreshRequest.Content = new StringContent($"grant_type=refresh_token&refresh_token={refreshToken}", Encoding.UTF8, "application/x-www-form-urlencoded");

                var refreshResponse = await _httpClient.SendAsync(refreshRequest);
                if (!refreshResponse.IsSuccessStatusCode) throw new Exception("Failed to refresh Allegro token.");

                using JsonDocument doc = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
                accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
                string newRefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? string.Empty;
                int expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

                using (SqlConnection conn = new SqlConnection(sqlConnectionString))
                {
                    await conn.OpenAsync();
                    using (SqlCommand cmd = new SqlCommand("sp_UpsertClientToken", conn))
                    {
                        cmd.CommandType = System.Data.CommandType.StoredProcedure;
                        cmd.Parameters.AddWithValue("@ClientId", clientId);
                        cmd.Parameters.AddWithValue("@AccessToken", accessToken);
                        cmd.Parameters.AddWithValue("@RefreshToken", newRefreshToken);
                        cmd.Parameters.AddWithValue("@ExpiresInSeconds", expiresIn);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }

            var ordersRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.allegro.pl.allegrosandbox.pl/order/checkout-forms");
            ordersRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            // Allegro API requires this specific Accept header
            ordersRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));

            var ordersResponse = await _httpClient.SendAsync(ordersRequest);
            string ordersJson = await ordersResponse.Content.ReadAsStringAsync();

            if (!ordersResponse.IsSuccessStatusCode)
            {
                var errResponse = req.CreateResponse(System.Net.HttpStatusCode.BadGateway);
                await errResponse.WriteStringAsync($"Allegro API Error: {ordersJson}");
                return errResponse;
            }

            using (SqlConnection conn = new SqlConnection(sqlConnectionString))
            {
                await conn.OpenAsync();
                using (SqlCommand cmd = new SqlCommand("sp_UpsertAllegroOrdersFromJSON", conn))
                {
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    cmd.Parameters.AddWithValue("@OrdersJson", ordersJson);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            var successResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await successResponse.WriteStringAsync("Orders successfully synchronized and shredded into database.");
            return successResponse;
        }
    }
}
