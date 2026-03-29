CREATE OR ALTER PROCEDURE sp_UpsertClientToken
    @ClientId UNIQUEIDENTIFIER,
    @AccessToken NVARCHAR(MAX),
    @RefreshToken NVARCHAR(MAX),
    @ExpiresInSeconds INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CalculatedExpiry DATETIMEOFFSET = DATEADD(second, @ExpiresInSeconds, SYSUTCDATETIME());

    MERGE INTO ClientAllegroTokens WITH (HOLDLOCK) AS Target
    USING (SELECT @ClientId AS ClientId) AS Source
    ON Target.ClientId = Source.ClientId
    WHEN MATCHED THEN
        UPDATE SET
            AccessToken = @AccessToken,
            RefreshToken = @RefreshToken,
            ExpiresAt = @CalculatedExpiry,
            UpdatedAt = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (ClientId, AccessToken, RefreshToken, ExpiresAt)
        VALUES (@ClientId, @AccessToken, @RefreshToken, @CalculatedExpiry);
END;
GO
