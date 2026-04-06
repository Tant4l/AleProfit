CREATE OR ALTER PROCEDURE sp_UpsertAllegroBillingFromJSON
    @ClientId UNIQUEIDENTIFIER,
    @BillingJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * INTO #RawBilling FROM OPENJSON(@BillingJson, '$.billingEntries')
    WITH (
        BillingEntryId UNIQUEIDENTIFIER '$.id',
        FeeType NVARCHAR(100) '$.type.id',
        Amount DECIMAL(12,2) '$.value.amount',
        VatRate DECIMAL(5,2) '$.tax.percentage',
        OccurredAt DATETIMEOFFSET '$.occurredAt',
        OrderId UNIQUEIDENTIFIER '$.order.id'
    );

    BEGIN TRANSACTION;
        MERGE INTO AllegroBillingEntries WITH (UPDLOCK, HOLDLOCK) AS Target
        USING #RawBilling AS Source ON Target.BillingEntryId = Source.BillingEntryId
        WHEN NOT MATCHED THEN
            INSERT (BillingEntryId, ClientId, AllegroOrderId, FeeType, Amount, VatRate, OccurredAt)
            VALUES (Source.BillingEntryId, @ClientId, Source.OrderId, Source.FeeType, Source.Amount, ISNULL(Source.VatRate, 23.00), Source.OccurredAt);
    COMMIT TRANSACTION;
END;
