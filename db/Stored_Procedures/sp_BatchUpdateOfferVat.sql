CREATE OR ALTER PROCEDURE sp_BatchUpdateOfferVat
    @ClientId UNIQUEIDENTIFIER,
    @VatDataJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE md
    SET md.VatRateValue = src.VatRate,
        md.IsVatSynced = 1,
        md.UpdatedAt = SYSDATETIMEOFFSET()
    FROM OfferMasterData md
    JOIN OPENJSON(@VatDataJson)
    WITH (
        OfferId NVARCHAR(50) '$.OfferId',
        VatRate DECIMAL(5,2) '$.VatRate'
    ) src ON md.AllegroOfferId = src.OfferId
    WHERE md.ClientId = @ClientId;
END;
