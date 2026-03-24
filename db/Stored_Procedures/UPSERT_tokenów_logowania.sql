CREATE PROCEDURE sp_UpdateClientOAuthToken
    @ClientId UNIQUEIDENTIFIER,
    @AccessToken NVARCHAR(MAX),
    @RefreshToken NVARCHAR(MAX),
    @ExpiresInSeconds INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CalculatedExpiry DATETIMEOFFSET = DATEADD(second, @ExpiresInSeconds, SYSDATETIMEOFFSET());

    IF EXISTS (SELECT 1 FROM ClientAllegroTokens WHERE ClientId = @ClientId)
    BEGIN
        UPDATE ClientAllegroTokens
        SET AccessToken = @AccessToken,
            RefreshToken = @RefreshToken,
            ExpiresAt = @CalculatedExpiry,
            UpdatedAt = SYSDATETIMEOFFSET()
        WHERE ClientId = @ClientId;
    END
    ELSE
    BEGIN
        INSERT INTO ClientAllegroTokens (ClientId, AccessToken, RefreshToken, ExpiresAt)
        VALUES (@ClientId, @AccessToken, @RefreshToken, @CalculatedExpiry);
    END
END;
