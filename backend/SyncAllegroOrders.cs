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
        }

        [Function("SyncAllegroOrders")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("Initiating Allegro Order Sync Pipeline.");

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
                            await unauthResponse.WriteStringAsync("No valid Allegro tokens found.");
                            return unauthResponse;
                        }
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
            int totalOrdersProcessed = 0;

            while (hasMoreData)
            {
                string apiUrl = $"https://api.allegro.pl.allegrosandbox.pl/order/checkout-forms?offset={offset}&limit={limit}";
                var ordersRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                ordersRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                ordersRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));

                var ordersResponse = await _httpClient.SendAsync(ordersRequest);
                string ordersJson = await ordersResponse.Content.ReadAsStringAsync();

                if (!ordersResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Allegro API Sync Failed: {Response}", ordersJson);
                    var errResponse = req.CreateResponse(System.Net.HttpStatusCode.BadGateway);
                    await errResponse.WriteStringAsync("Allegro API Error.");
                    return errResponse;
                }

                using JsonDocument doc = JsonDocument.Parse(ordersJson);
                var checkoutForms = doc.RootElement.GetProperty("checkoutForms");
                int currentBatchCount = checkoutForms.GetArrayLength();

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
                            cmd.Parameters.AddWithValue("@ClientId", clientId);
                            cmd.Parameters.AddWithValue("@OrdersJson", ordersJson);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    totalOrdersProcessed += currentBatchCount;
                    offset += limit; // Increment offset to fetch next page
                }
                catch (SqlException sqlEx)
                {
                    _logger.LogCritical(sqlEx, "SQL Parsing failed for payload: {OrdersJson}", ordersJson);
                    var errResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                    await errResponse.WriteStringAsync("Database schema violation during JSON shredding.");
                    return errResponse;
                }
            }

            var unsyncedOffers = new System.Collections.Generic.List<string>();

            using (SqlConnection conn = new SqlConnection(sqlConnectionString))
            {
                await conn.OpenAsync();
                using (SqlCommand cmd = new SqlCommand("SELECT AllegroOfferId FROM OfferMasterData WHERE IsVatSynced = 0 AND ClientId = @ClientId", conn))
                {
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            unsyncedOffers.Add(reader.GetString(0));
                        }
                    }
                }
            }

            foreach (var offerId in unsyncedOffers)
            {
                try
                {
                    var offerRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.allegro.pl.allegrosandbox.pl/sale/offers/{offerId}");
                    offerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    offerRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));

                    var offerResponse = await _httpClient.SendAsync(offerRequest);
                    if (offerResponse.IsSuccessStatusCode)
                    {
                        using JsonDocument offerDoc = JsonDocument.Parse(await offerResponse.Content.ReadAsStringAsync());

                        decimal realVatRate = 23.00m; // Fallback

                        if (offerDoc.RootElement.TryGetProperty("tax", out var taxElement) &&
                            taxElement.TryGetProperty("rate", out var rateElement))
                        {
                            string rateString = rateElement.GetString() ?? "23.00";
                            if (rateString.Contains("EXEMPT", StringComparison.OrdinalIgnoreCase)) {
                                realVatRate = 0.00m;
                            } else {
                                decimal.TryParse(rateString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out realVatRate);
                            }
                        }

                        using (SqlConnection conn = new SqlConnection(sqlConnectionString))
                        {
                            await conn.OpenAsync();
                            using (SqlCommand updateCmd = new SqlCommand("UPDATE OfferMasterData SET VatRate = @VatRate, IsVatSynced = 1, UpdatedAt = SYSDATETIMEOFFSET() WHERE AllegroOfferId = @OfferId", conn))
                            {
                                updateCmd.Parameters.AddWithValue("@VatRate", realVatRate);
                                updateCmd.Parameters.AddWithValue("@OfferId", offerId);
                                await updateCmd.ExecuteNonQueryAsync();
                            }
                        }
                        _logger.LogInformation("Successfully synced exact VAT Rate ({Rate}%) for Offer {OfferId}.", realVatRate, offerId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync VAT for Offer {OfferId}.", offerId);
                }
            }

            var successResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await successResponse.WriteStringAsync($"Sync Complete. Successfully processed {totalOrdersProcessed} orders.");
            return successResponse;
        }
    }
}
