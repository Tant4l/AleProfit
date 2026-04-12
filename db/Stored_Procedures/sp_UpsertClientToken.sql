CREATE OR ALTER PROCEDURE sp_UpsertClientToken
    @ClientId UNIQUEIDENTIFIER,
    @AccessToken NVARCHAR(MAX),
    @RefreshToken NVARCHAR(MAX),
    @ExpiresInSeconds INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @CalculatedExpiry DATETIMEOFFSET = DATEADD(second, @ExpiresInSeconds, SYSDATETIMEOFFSET());

        MERGE INTO ClientAllegroTokens WITH (UPDLOCK, HOLDLOCK) AS Target
        USING (SELECT @ClientId AS ClientId) AS Source
        ON Target.ClientId = Source.ClientId
        WHEN MATCHED THEN
            UPDATE SET
                AccessToken = @AccessToken,
                RefreshToken = @RefreshToken,
                ExpiresAt = @CalculatedExpiry,
                UpdatedAt = SYSDATETIMEOFFSET()
        WHEN NOT MATCHED THEN
            INSERT (ClientId, AccessToken, RefreshToken, ExpiresAt, UpdatedAt)
            VALUES (@ClientId, @AccessToken, @RefreshToken, @CalculatedExpiry, SYSDATETIMEOFFSET());

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
