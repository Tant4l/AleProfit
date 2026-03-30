CREATE OR ALTER PROCEDURE sp_UpsertAllegroBillingFromJSON
    @ClientId UNIQUEIDENTIFIER,
    @BillingJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT * INTO #TempBilling
        FROM OPENJSON(@BillingJson, '$.billingEntries')
        WITH (
            BillingEntryId UNIQUEIDENTIFIER '$.id',
            OccurredAt DATETIMEOFFSET '$.occurredAt',
            FeeType NVARCHAR(100) '$.type.id',
            Amount DECIMAL(12,2) '$.value.amount',
            AllegroOrderId UNIQUEIDENTIFIER '$.order.id'
        );

        MERGE INTO AllegroBillingEntries WITH (HOLDLOCK, SERIALIZABLE) AS Target
        USING #TempBilling AS Source
        ON Target.BillingEntryId = Source.BillingEntryId
        WHEN MATCHED THEN
            UPDATE SET
                FeeType = Source.FeeType,
                Amount = Source.Amount,
                AllegroOrderId = Source.AllegroOrderId
        WHEN NOT MATCHED THEN
            INSERT (BillingEntryId, ClientId, AllegroOrderId, FeeType, Amount, OccurredAt)
            VALUES (Source.BillingEntryId, @ClientId, Source.AllegroOrderId, Source.FeeType, Source.Amount, Source.OccurredAt);

        DECLARE @MaxOccurredAt DATETIMEOFFSET = (SELECT MAX(OccurredAt) FROM #TempBilling);

        IF @MaxOccurredAt IS NOT NULL
        BEGIN
            UPDATE Clients
            SET LastBillingSyncAt =
                CASE
                    WHEN LastBillingSyncAt IS NULL THEN @MaxOccurredAt
                    WHEN @MaxOccurredAt > LastBillingSyncAt THEN @MaxOccurredAt
                    ELSE LastBillingSyncAt
                END
            WHERE ClientId = @ClientId;
        END

        DROP TABLE #TempBilling;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
