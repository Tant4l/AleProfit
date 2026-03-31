using System;
using System.Net;
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
    public class AllegroAuthCallback
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AllegroAuthCallback> _logger;

        public AllegroAuthCallback(ILogger<AllegroAuthCallback> logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        [Function("AllegroAuthCallback")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            _logger.LogInformation("Processing Allegro OAuth Callback.");

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            string? code = query["code"];
            string? state = query["state"]; // Expected to be the ClientId GUID

            if (string.IsNullOrEmpty(code) || !Guid.TryParse(state, out Guid clientId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Invalid code or state parameter.");
                return badResponse;
            }

            // Environment Configuration
            string authBaseUrl = Environment.GetEnvironmentVariable("Allegro_AuthBaseUrl") ?? "https://allegro.pl.allegrosandbox.pl";
            string appClientId = Environment.GetEnvironmentVariable("Allegro_ClientId") ?? throw new Exception("Missing Allegro_ClientId");
            string appClientSecret = Environment.GetEnvironmentVariable("Allegro_ClientSecret") ?? throw new Exception("Missing Allegro_ClientSecret");
            string redirectUri = Environment.GetEnvironmentVariable("Allegro_RedirectUri") ?? throw new Exception("Missing Allegro_RedirectUri");
            string sqlConn = Environment.GetEnvironmentVariable("SqlConnectionString") ?? throw new Exception("Missing SqlConnectionString");

            // 1. Token Exchange Request
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"{authBaseUrl}/auth/oauth/token");
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{appClientId}:{appClientSecret}"));
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var postData = $"grant_type=authorization_code&code={code}&redirect_uri={Uri.EscapeDataString(redirectUri)}";
            tokenRequest.Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _httpClient.SendAsync(tokenRequest);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token Exchange Failed: {Body}", responseBody);
                return req.CreateResponse(HttpStatusCode.BadGateway);
            }

            // 2. Parse and Persist
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            string access = doc.RootElement.GetProperty("access_token").GetString()!;
            string refresh = doc.RootElement.GetProperty("refresh_token").GetString()!;
            int expires = doc.RootElement.GetProperty("expires_in").GetInt32();

            try
            {
                using (var conn = new SqlConnection(sqlConn))
                {
                    await conn.OpenAsync();
                    using (var cmd = new SqlCommand("sp_UpsertClientToken", conn))
                    {
                        cmd.CommandType = System.Data.CommandType.StoredProcedure;
                        cmd.Parameters.AddWithValue("@ClientId", clientId);
                        cmd.Parameters.AddWithValue("@AccessToken", access);
                        cmd.Parameters.AddWithValue("@RefreshToken", refresh);
                        cmd.Parameters.AddWithValue("@ExpiresInSeconds", expires);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (SqlException ex)
            {
                _logger.LogCritical(ex, "Database persistence failed for client {ClientId}", clientId);
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            var successResponse = req.CreateResponse(HttpStatusCode.OK);
            await successResponse.WriteStringAsync("Authentication successful. Tokens secured.");
            return successResponse;
        }
    }
}
