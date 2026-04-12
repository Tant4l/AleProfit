CREATE OR ALTER PROCEDURE sp_UpsertAllegroBillingFromJSON
    @ClientId UNIQUEIDENTIFIER,
    @BillingJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BillingJson IS NULL OR ISJSON(@BillingJson) = 0
    BEGIN
        RAISERROR('Invalid JSON input for sp_UpsertAllegroBillingFromJSON', 16, 1);
        RETURN;
    END

    BEGIN TRY
        BEGIN TRANSACTION;

        CREATE TABLE #RawBilling (
            BillingEntryId UNIQUEIDENTIFIER PRIMARY KEY,
            FeeType NVARCHAR(100),
            Amount DECIMAL(12,2),
            VatRate DECIMAL(5,2),
            OccurredAt DATETIMEOFFSET,
            OrderId UNIQUEIDENTIFIER
        );

        INSERT INTO #RawBilling (BillingEntryId, FeeType, Amount, VatRate, OccurredAt, OrderId)
        SELECT
            BillingEntryId, FeeType, Amount, VatRate, OccurredAt, OrderId
        FROM OPENJSON(@BillingJson, '$.billingEntries')
        WITH (
            BillingEntryId UNIQUEIDENTIFIER '$.id',
            FeeType NVARCHAR(100) '$.type.id',
            Amount DECIMAL(12,2) '$.value.amount',
            VatRate DECIMAL(5,2) '$.tax.percentage',
            OccurredAt DATETIMEOFFSET '$.occurredAt',
            OrderId UNIQUEIDENTIFIER '$.order.id'
        );

        MERGE INTO AllegroBillingEntries WITH (UPDLOCK, HOLDLOCK) AS Target
        USING #RawBilling AS Source ON Target.BillingEntryId = Source.BillingEntryId
        WHEN NOT MATCHED THEN
            INSERT (BillingEntryId, ClientId, AllegroOrderId, FeeType, Amount, VatRate, OccurredAt)
            VALUES (Source.BillingEntryId, @ClientId, Source.OrderId, Source.FeeType, Source.Amount, COALESCE(Source.VatRate, 23.00), Source.OccurredAt);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
