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
    public class SyncAllegroBilling
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly ILogger<SyncAllegroBilling> _logger;

        public SyncAllegroBilling(ILogger<SyncAllegroBilling> logger)
        {
            _logger = logger;
        }

        [Function("SyncAllegroBilling")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("Initiating Allegro Financial Ledger Sync.");

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
            string lastSyncDate = string.Empty;

            using (SqlConnection conn = new SqlConnection(sqlConnectionString))
            {
                await conn.OpenAsync();

                using (SqlCommand cmdToken = new SqlCommand("sp_GetValidToken", conn))
                {
                    cmdToken.CommandType = System.Data.CommandType.StoredProcedure;
                    cmdToken.Parameters.AddWithValue("@ClientId", clientId);
                    using (var reader = await cmdToken.ExecuteReaderAsync())
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
                            await unauthResponse.WriteStringAsync("No valid Allegro tokens found.");
                            return unauthResponse;
                        }
                    }
                }

                using (SqlCommand cmdDate = new SqlCommand("SELECT LastBillingSyncAt FROM Clients WHERE ClientId = @ClientId", conn))
                {
                    cmdDate.Parameters.AddWithValue("@ClientId", clientId);
                    var result = await cmdDate.ExecuteScalarAsync();
                    if (result != DBNull.Value && result != null)
                    {
                        lastSyncDate = ((DateTimeOffset)result).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    }
                }
            }

            if (needsRefresh)
            {
                _logger.LogInformation("Token expired. Executing refresh flow.");
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

            int offset = 0;
            int limit = 100;
            bool hasMoreData = true;
            int totalEntriesProcessed = 0;

            while (hasMoreData)
            {
                string apiUrl = $"https://api.allegro.pl.allegrosandbox.pl/billing/billing-entries?offset={offset}&limit={limit}";
                if (!string.IsNullOrEmpty(lastSyncDate))
                {
                    apiUrl += $"&occurredAt.gte={lastSyncDate}";
                }

                var billingRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                billingRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                billingRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));

                var billingResponse = await _httpClient.SendAsync(billingRequest);
                string billingJson = await billingResponse.Content.ReadAsStringAsync();

                if (!billingResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Allegro Billing API Sync Failed: {Response}", billingJson);
                    var errResponse = req.CreateResponse(System.Net.HttpStatusCode.BadGateway);
                    await errResponse.WriteStringAsync("Allegro API Error.");
                    return errResponse;
                }

                using JsonDocument doc = JsonDocument.Parse(billingJson);

                if (!doc.RootElement.TryGetProperty("billingEntries", out JsonElement billingEntries))
                {
                    _logger.LogWarning("Unexpected JSON payload missing 'billingEntries'. Payload: {Payload}", billingJson);
                    break;
                }

                int currentBatchCount = billingEntries.GetArrayLength();

                if (currentBatchCount == 0)
                {
                    hasMoreData = false;
                    continue;
                }

                try
                {
                    using (SqlConnection conn = new SqlConnection(sqlConnectionString))
                    {
                        await conn.OpenAsync();
                        using (SqlCommand cmd = new SqlCommand("sp_UpsertAllegroBillingFromJSON", conn))
                        {
                            cmd.CommandType = System.Data.CommandType.StoredProcedure;
                            cmd.Parameters.AddWithValue("@ClientId", clientId);
                            cmd.Parameters.AddWithValue("@BillingJson", billingJson);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    totalEntriesProcessed += currentBatchCount;
                    offset += limit;
                }
                catch (SqlException sqlEx)
                {
                    _logger.LogCritical(sqlEx, "SQL Parsing failed for Billing Payload: {Payload}", billingJson);
                    var errResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                    await errResponse.WriteStringAsync("Database schema violation during JSON shredding.");
                    return errResponse;
                }
            }

            var successResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await successResponse.WriteStringAsync($"Ledger Sync Complete. Processed {totalEntriesProcessed} financial operations.");
            return successResponse;
        }
    }
}
