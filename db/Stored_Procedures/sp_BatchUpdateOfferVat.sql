CREATE OR ALTER PROCEDURE sp_BatchUpdateOfferVat
    @ClientId UNIQUEIDENTIFIER,
    @VatDataJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @VatDataJson IS NULL OR ISJSON(@VatDataJson) = 0
    BEGIN
        RAISERROR('Invalid JSON', 16, 1);
        RETURN;
    END

    DECLARE @UpdatedRows INT = 0;

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE md
        SET
            md.VatRateValue = COALESCE(src.VatRate, src.VatRate2, md.VatRateValue),
            md.ProductName  = COALESCE(src.ProductName, src.ProductName2, md.ProductName),
            md.IsVatSynced  = 1,
            md.UpdatedAt    = SYSDATETIMEOFFSET()
        FROM OfferMasterData md
        JOIN OPENJSON(@VatDataJson)
        WITH (
            OfferId     NVARCHAR(50)  '$.OfferId',     OfferId2     NVARCHAR(50)  '$.offerId',
            ProductName NVARCHAR(255) '$.ProductName', ProductName2 NVARCHAR(255) '$.productName',
            VatRate     DECIMAL(5,2)  '$.VatRate',     VatRate2     DECIMAL(5,2)  '$.vatRate'
        ) src ON md.AllegroOfferId = COALESCE(src.OfferId, src.OfferId2)
        WHERE md.ClientId = @ClientId;

        SET @UpdatedRows = @@ROWCOUNT;

        COMMIT TRANSACTION;

        SELECT @UpdatedRows AS UpdatedRows;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
