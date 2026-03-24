CREATE FUNCTION fn_GetValidAccessToken (@ClientId UNIQUEIDENTIFIER)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @Token NVARCHAR(MAX);
    SELECT @Token = AccessToken
    FROM ClientAllegroTokens
    WHERE ClientId = @ClientId AND ExpiresAt > DATEADD(second, 300, SYSUTCDATETIME());

    RETURN @Token;
END;
