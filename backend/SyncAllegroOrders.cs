using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AllegroRecruitment.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AllegroRecruitment
{
    public record VatSyncEntry(string OfferId, string? ProductName, decimal VatRate);

    public class SyncAllegroOrders
    {
        private readonly HttpClient _httpClient;
        private readonly IAllegroTokenService _tokenService;
        private readonly ILogger<SyncAllegroOrders> _logger;

        public SyncAllegroOrders(
            ILogger<SyncAllegroOrders> logger, 
            IAllegroTokenService tokenService, 
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _tokenService = tokenService;
            _httpClient = httpClientFactory.CreateClient("AllegroClient");
        }

        [Function("SyncAllegroOrders")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("Initiating Allegro Order Sync Pipeline.");

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
            try
            {
                accessToken = await _tokenService.GetValidTokenAsync(clientId);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "No valid Allegro token for client {ClientId}", clientId);
                var unauthResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthResponse.WriteStringAsync("Authentication failed: No valid tokens available.");
                return unauthResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token retrieval failed for client {ClientId}", clientId);
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            int offset = 0;
            const int limit = 100;
            bool hasMoreData = true;
            int totalOrdersProcessed = 0;

            while (hasMoreData)
            {
                string apiUrl = $"{apiBaseUrl}/order/checkout-forms?offset={offset}&limit={limit}";
                
                using var ordersRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                ordersRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                
                // Prioritize Allegro vendor-specific JSON, fallback to standard JSON for errors
                ordersRequest.Headers.Accept.Clear();
                ordersRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));
                ordersRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.8));

                using var ordersResponse = await _httpClient.SendAsync(ordersRequest, HttpCompletionOption.ResponseContentRead);
                string ordersJson = await ordersResponse.Content.ReadAsStringAsync();

                if (!ordersResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Allegro API Sync Failed: {StatusCode} - {Response}", (int)ordersResponse.StatusCode, ordersJson);
                    var errResponse = req.CreateResponse(HttpStatusCode.BadGateway);
                    await errResponse.WriteStringAsync("Allegro API Error during order fetch.");
                    return errResponse;
                }

                int currentBatchCount;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(ordersJson);
                    if (!doc.RootElement.TryGetProperty("checkoutForms", out var checkoutForms))
                    {
                        hasMoreData = false;
                        break;
                    }
                    currentBatchCount = checkoutForms.GetArrayLength();
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Malformed orders response from Allegro.");
                    return req.CreateResponse(HttpStatusCode.BadGateway);
                }

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
                        using (SqlCommand cmd = new SqlCommand("sp_UpsertAllegroOrdersFromJSON", conn))
                        {
                            cmd.CommandType = System.Data.CommandType.StoredProcedure;
                            cmd.Parameters.Add("@ClientId", System.Data.SqlDbType.UniqueIdentifier).Value = clientId;
                            cmd.Parameters.Add("@OrdersJson", System.Data.SqlDbType.NVarChar, -1).Value = ordersJson;
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    totalOrdersProcessed += currentBatchCount;
                    offset += limit;

                    if (currentBatchCount < limit) hasMoreData = false;
                    if (offset > 10000)
                    {
                        _logger.LogWarning("Order sync safety cap hit for client {ClientId}.", clientId);
                        break;
                    }
                }
                catch (SqlException sqlEx)
                {
                    _logger.LogCritical(sqlEx, "SQL Error during Order JSON processing for client {ClientId}", clientId);
                    var errResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await errResponse.WriteStringAsync("Database persistence failed.");
                    return errResponse;
                }
            }

            var unsyncedOffers = new List<string>();
            using (SqlConnection conn = new SqlConnection(sqlConnectionString))
            {
                await conn.OpenAsync();
                string query = "SELECT AllegroOfferId FROM OfferMasterData WHERE IsVatSynced = 0 AND ClientId = @ClientId";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.Add("@ClientId", System.Data.SqlDbType.UniqueIdentifier).Value = clientId;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync()) unsyncedOffers.Add(reader.GetString(0));
                    }
                }
            }

            var vatResults = new ConcurrentBag<VatSyncEntry>();
            using var semaphore = new SemaphoreSlim(10); 

            var tasks = unsyncedOffers.Select(async offerId => {
                await semaphore.WaitAsync();
                try {
                    using var offerRequest = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}/sale/offers/{Uri.EscapeDataString(offerId)}");
                    offerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    offerRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));

                    using var offerResponse = await _httpClient.SendAsync(offerRequest);
                    if (offerResponse.IsSuccessStatusCode) {
                        using JsonDocument offerDoc = JsonDocument.Parse(await offerResponse.Content.ReadAsStringAsync());

                        string realName = offerDoc.RootElement.TryGetProperty("name", out var nameProp)
                            ? nameProp.GetString() ?? "Unknown Product"
                            : "Unknown Product";

                        decimal realVatRate = 23.00m;
                        if (offerDoc.RootElement.TryGetProperty("tax", out var tax) && tax.TryGetProperty("rate", out var rate)) {
                            string rateStr = rate.GetString() ?? "23.00";
                            if (rateStr.Contains("EXEMPT"))
                            {
                                realVatRate = 0.00m;
                            }
                            else if (!decimal.TryParse(rateStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out realVatRate))
                            {
                                realVatRate = 23.00m;
                            }
                        }

                        vatResults.Add(new VatSyncEntry(offerId, realName, realVatRate));
                    }
                    else
                    {
                        // FIX: If API fails (e.g. 404 for archived Sandbox offers),
                        // force sync completion with fallback data to prevent infinite retry loop.
                        vatResults.Add(new VatSyncEntry(offerId, null, 23.00m));
                    }
                }
                catch (Exception ex) {
                    _logger.LogWarning("Failed to enrich metadata for offer {OfferId}: {Msg}", offerId, ex.Message);
                    vatResults.Add(new VatSyncEntry(offerId, null, 23.00m));
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);

            if (!vatResults.IsEmpty) {
                try 
                {
                    using (SqlConnection conn = new SqlConnection(sqlConnectionString)) {
                        await conn.OpenAsync();
                        using (SqlCommand batchCmd = new SqlCommand("sp_BatchUpdateOfferVat", conn)) {
                            batchCmd.CommandType = System.Data.CommandType.StoredProcedure;
                            batchCmd.Parameters.Add("@ClientId", System.Data.SqlDbType.UniqueIdentifier).Value = clientId;

                            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = null };
                            batchCmd.Parameters.Add("@VatDataJson", System.Data.SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(vatResults, jsonOptions);
                            
                            await batchCmd.ExecuteNonQueryAsync();
                        }
                    }
                }
                catch (SqlException sqlEx)
                {
                    _logger.LogError(sqlEx, "Failed to batch update VAT rates for client {ClientId}", clientId);
                }
            }

            var successResponse = req.CreateResponse(HttpStatusCode.OK);
            await successResponse.WriteStringAsync($"Sync Complete. Processed {totalOrdersProcessed} orders. Updated {vatResults.Count} VAT rates.");
            return successResponse;
        }
    }
}