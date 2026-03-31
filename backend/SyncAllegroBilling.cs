using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using AllegroRecruitment.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AllegroRecruitment
{
    public class SyncAllegroBilling
    {
        private readonly HttpClient _httpClient;
        private readonly IAllegroTokenService _tokenService;
        private readonly ILogger<SyncAllegroBilling> _logger;

        public SyncAllegroBilling(
            ILogger<SyncAllegroBilling> logger, 
            IAllegroTokenService tokenService, 
            HttpClient httpClient)
        {
            _logger = logger;
            _tokenService = tokenService;
            _httpClient = httpClient;
        }

        [Function("SyncAllegroBilling")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("Initiating Allegro Financial Ledger Sync.");

            var queryDictionary = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!Guid.TryParse(queryDictionary["clientId"], out Guid clientId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Valid clientId parameter is required.");
                return badResponse;
            }

            string sqlConnectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing SqlConnectionString");
            string apiBaseUrl = Environment.GetEnvironmentVariable("Allegro_ApiBaseUrl")
                ?? throw new InvalidOperationException("Missing Allegro_ApiBaseUrl");

            string accessToken;
            string lastSyncDate = string.Empty;

            try
            {
                accessToken = await _tokenService.GetValidTokenAsync(clientId);

                using (SqlConnection conn = new SqlConnection(sqlConnectionString))
                {
                    await conn.OpenAsync();
                    using (SqlCommand cmdDate = new SqlCommand("SELECT LastBillingSyncAt FROM Clients WHERE ClientId = @ClientId", conn))
                    {
                        cmdDate.Parameters.AddWithValue("@ClientId", clientId);
                        var result = await cmdDate.ExecuteScalarAsync();
                        if (result != DBNull.Value && result != null)
                        {
                            // Allegro strictly requires ISO 8601 with ms precision: yyyy-MM-ddTHH:mm:ss.fffZ
                            lastSyncDate = ((DateTimeOffset)result).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                var unauthResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthResponse.WriteStringAsync("No valid Allegro tokens found for this client.");
                return unauthResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pre-sync preparation failed for client {ClientId}", clientId);
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            int offset = 0;
            const int limit = 100;
            bool hasMoreData = true;
            int totalEntriesProcessed = 0;

            while (hasMoreData)
            {
                string apiUrl = $"{apiBaseUrl}/billing/billing-entries?offset={offset}&limit={limit}";
                if (!string.IsNullOrEmpty(lastSyncDate))
                {
                    apiUrl += $"&occurredAt.gte={lastSyncDate}";
                }

                using var billingRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                billingRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                
                // FIXED: Multi-Accept Header Strategy
                billingRequest.Headers.Accept.Clear();
                billingRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));
                billingRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.8));

                using var billingResponse = await _httpClient.SendAsync(billingRequest, HttpCompletionOption.ResponseContentRead);
                string billingJson = await billingResponse.Content.ReadAsStringAsync();

                if (!billingResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Allegro Billing API Sync Failed. Status: {Status}, Response: {Body}", 
                        (int)billingResponse.StatusCode, billingJson);
                    
                    var errResponse = req.CreateResponse(HttpStatusCode.BadGateway);
                    await errResponse.WriteStringAsync($"Allegro API Error: {billingResponse.ReasonPhrase}");
                    return errResponse;
                }

                using JsonDocument doc = JsonDocument.Parse(billingJson);
                if (!doc.RootElement.TryGetProperty("billingEntries", out JsonElement billingEntries))
                {
                    hasMoreData = false;
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

                    // Safety break if limit is reached to prevent infinite loops in dev
                    if (offset > 10000) break; 
                }
                catch (SqlException sqlEx)
                {
                    _logger.LogCritical(sqlEx, "Database schema violation during billing sync for client {ClientId}", clientId);
                    var errResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await errResponse.WriteStringAsync("Database persistence failed.");
                    return errResponse;
                }
            }

            // 5. Update Tenant High-Water Mark to prevent re-syncing old data
            using (SqlConnection conn = new SqlConnection(sqlConnectionString))
            {
                await conn.OpenAsync();
                string updateSql = "UPDATE Clients SET LastBillingSyncAt = SYSDATETIMEOFFSET() WHERE ClientId = @ClientId";
                using (SqlCommand cmd = new SqlCommand(updateSql, conn))
                {
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            var successResponse = req.CreateResponse(HttpStatusCode.OK);
            await successResponse.WriteStringAsync($"Ledger Sync Complete. Processed {totalEntriesProcessed} operations.");
            return successResponse;
        }
    }
}