using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AllegroRecruitment.Services
{
    public interface IAllegroTokenService
    {
        Task<string> GetValidTokenAsync(Guid clientId);
    }

    public class AllegroTokenService : IAllegroTokenService
    {
        private readonly HttpClient _httpClient;

        public AllegroTokenService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        private string GetConfig(string key) => 
            Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"CRITICAL: Missing environment variable '{key}'.");

        public async Task<string> GetValidTokenAsync(Guid clientId)
        {
            string sqlConn = GetConfig("SqlConnectionString");
            string authBaseUrl = Environment.GetEnvironmentVariable("Allegro_AuthBaseUrl") ?? "https://allegro.pl";
            string appClientId = GetConfig("Allegro_ClientId");
            string appClientSecret = GetConfig("Allegro_ClientSecret");

            string accessToken = string.Empty;
            string refreshToken = string.Empty;
            bool needsRefresh = false;

            using (var conn = new SqlConnection(sqlConn))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("sp_GetValidToken", conn) { CommandType = System.Data.CommandType.StoredProcedure };
                cmd.Parameters.AddWithValue("@ClientId", clientId);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    accessToken = reader.GetString(0);
                    refreshToken = reader.GetString(1);
                    needsRefresh = reader.GetInt32(2) == 1; 
                }
                else 
                {
                    throw new UnauthorizedAccessException($"No OAuth identity found for ClientId: {clientId}. Manual authorization required.");
                }
            }

            if (needsRefresh)
            {
                var refreshRequest = new HttpRequestMessage(HttpMethod.Post, $"{authBaseUrl}/auth/oauth/token");
                
                var authHeaderValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{appClientId}:{appClientSecret}"));
                refreshRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeaderValue);
                
                refreshRequest.Content = new StringContent(
                    $"grant_type=refresh_token&refresh_token={refreshToken}", 
                    Encoding.UTF8, "application/x-www-form-urlencoded");

                var response = await _httpClient.SendAsync(refreshRequest);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Allegro OAuth Refresh Failed. Status: {response.StatusCode}, Response: {responseBody}");
                }

                // Parse standard OAuth2 response
                using var doc = JsonDocument.Parse(responseBody);
                accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
                string newRefreshToken = doc.RootElement.GetProperty("refresh_token").GetString()!;
                int expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

                using (var conn = new SqlConnection(sqlConn))
                {
                    await conn.OpenAsync();
                    using var cmd = new SqlCommand("sp_UpsertClientToken", conn) { CommandType = System.Data.CommandType.StoredProcedure };
                    cmd.Parameters.AddWithValue("@ClientId", clientId);
                    cmd.Parameters.AddWithValue("@AccessToken", accessToken);
                    cmd.Parameters.AddWithValue("@RefreshToken", newRefreshToken);
                    cmd.Parameters.AddWithValue("@ExpiresInSeconds", expiresIn);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            return accessToken;
        }
    }
}