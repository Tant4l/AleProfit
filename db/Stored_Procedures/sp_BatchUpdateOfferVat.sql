CREATE OR ALTER PROCEDURE sp_BatchUpdateOfferVat
    @ClientId UNIQUEIDENTIFIER,
    @VatDataJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE md
    SET md.VatRateValue = ISNULL(src.VatRate, ISNULL(src.VatRate2, md.VatRateValue)),
        md.ProductName = ISNULL(src.ProductName, ISNULL(src.ProductName2, md.ProductName)),
        md.IsVatSynced = 1,
        md.UpdatedAt = SYSDATETIMEOFFSET()
    FROM OfferMasterData md
    JOIN OPENJSON(@VatDataJson)
    WITH (
        OfferId NVARCHAR(50) '$.OfferId', OfferId2 NVARCHAR(50) '$.offerId',
        ProductName NVARCHAR(255) '$.ProductName', ProductName2 NVARCHAR(255) '$.productName',
        VatRate DECIMAL(5,2) '$.VatRate', VatRate2 DECIMAL(5,2) '$.vatRate'
    ) src ON md.AllegroOfferId = ISNULL(src.OfferId, src.OfferId2)
    WHERE md.ClientId = @ClientId;
END;
GO
