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
    public class AllegroAuthCallback
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly ILogger<AllegroAuthCallback> _logger;

        public AllegroAuthCallback(ILogger<AllegroAuthCallback> logger)
        {
            _logger = logger;
        }

        [Function("AllegroAuthCallback")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            _logger.LogInformation("Processing Allegro OAuth Callback.");

            var queryDictionary = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

            string? code = queryDictionary["code"];
            string? state = queryDictionary["state"];

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                _logger.LogWarning("Callback failed: Missing code or state.");
                var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Missing code or state parameter.");
                return badResponse;
            }

            if (!Guid.TryParse(state, out Guid parsedClientId))
            {
                _logger.LogWarning("Callback failed: Invalid state format. Expected GUID, received: {State}", state);
                var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Malformed state parameter.");
                return badResponse;
            }

            string clientId = Environment.GetEnvironmentVariable("Allegro_ClientId")
                ?? throw new InvalidOperationException("Missing Env Var: Allegro_ClientId");
            string clientSecret = Environment.GetEnvironmentVariable("Allegro_ClientSecret")
                ?? throw new InvalidOperationException("Missing Env Var: Allegro_ClientSecret");
            string sqlConnectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
                ?? throw new InvalidOperationException("Missing Env Var: SqlConnectionString");
            string redirectUri = "http://localhost:7071/api/AllegroAuthCallback";

            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://allegro.pl.allegrosandbox.pl/auth/oauth/token");
            var authBytes = Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}");
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var content = new StringContent(
                $"grant_type=authorization_code&code={code}&redirect_uri={redirectUri}",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");
            tokenRequest.Content = content;

            var response = await _httpClient.SendAsync(tokenRequest);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Allegro API Token Exchange Failed. Status: {Status}, Response: {Response}", response.StatusCode, responseString);
                var errResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await errResponse.WriteStringAsync("Failed to negotiate with Allegro API.");
                return errResponse;
            }

            using JsonDocument doc = JsonDocument.Parse(responseString);

            string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
            string refreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? string.Empty;
            int expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

            try
            {
                using (SqlConnection conn = new SqlConnection(sqlConnectionString))
                {
                    await conn.OpenAsync();
                    using (SqlCommand cmd = new SqlCommand("sp_UpsertClientToken", conn))
                    {
                        cmd.CommandType = System.Data.CommandType.StoredProcedure;
                        cmd.Parameters.AddWithValue("@ClientId", parsedClientId);
                        cmd.Parameters.AddWithValue("@AccessToken", accessToken);
                        cmd.Parameters.AddWithValue("@RefreshToken", refreshToken);
                        cmd.Parameters.AddWithValue("@ExpiresInSeconds", expiresIn);

                        await cmd.ExecuteNonQueryAsync();
                        _logger.LogInformation("Successfully secured token in database for Client: {ClientId}", parsedClientId);
                    }
                }
            }
            catch (SqlException sqlEx)
            {
                _logger.LogCritical(sqlEx, "Database operation failed while saving tokens.");
                var errResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await errResponse.WriteStringAsync("Database persistence failed.");
                return errResponse;
            }

            var successResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await successResponse.WriteStringAsync("Authentication successful! Token secured in database. You may close this window.");
            return successResponse;
        }
    }
}
