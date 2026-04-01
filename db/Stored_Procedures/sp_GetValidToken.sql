CREATE OR ALTER PROCEDURE sp_GetValidToken
    @ClientId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Return the token and a flag indicating if it needs refreshing
    SELECT TOP 1
        AccessToken,
        RefreshToken,
        CASE
            -- Buffer of 5 minutes (300 seconds) to prevent failing during network transit
            WHEN ExpiresAt < DATEADD(second, 300, SYSUTCDATETIME()) THEN 1
            ELSE 0
        END AS NeedsRefresh
    FROM ClientAllegroTokens WITH (NOLOCK)
    WHERE ClientId = @ClientId;
END;
GO
