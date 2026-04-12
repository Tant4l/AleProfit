CREATE OR ALTER PROCEDURE sp_GetValidToken
    @ClientId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP 1
        AccessToken,
        RefreshToken,
        CASE
            WHEN ExpiresAt < DATEADD(second, 300, SYSUTCDATETIME()) THEN 1
            ELSE 0
        END AS NeedsRefresh
    FROM ClientAllegroTokens WITH (NOLOCK)
    WHERE ClientId = @ClientId
    ORDER BY UpdatedAt DESC;
END;
GO
