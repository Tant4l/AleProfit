CREATE OR ALTER PROCEDURE sp_UpsertAllegroOrdersFromJSON
    @ClientId UNIQUEIDENTIFIER,
    @OrdersJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        -- FIX: Added FulfillmentStatus extraction
        SELECT * INTO #TempOrders FROM OPENJSON(@OrdersJson, '$.checkoutForms')
        WITH (
            AllegroOrderId UNIQUEIDENTIFIER '$.id', BuyerLogin NVARCHAR(100) '$.buyer.login',
            OrderStatus NVARCHAR(50) '$.status',
            FulfillmentStatus NVARCHAR(50) '$.fulfillment.status',
            OrderDate DATETIMEOFFSET '$.updatedAt',
            TotalGrossAmount DECIMAL(12,2) '$.summary.totalToPay.amount',
            ShippingRevenueGross DECIMAL(12,2) '$.delivery.cost.amount', BuyerNip NVARCHAR(20) '$.invoice.address.company.taxId'
        );

        MERGE INTO AllegroOrders WITH (SERIALIZABLE) AS Target
        USING #TempOrders AS Source ON Target.ClientId = @ClientId AND Target.AllegroOrderId = Source.AllegroOrderId
        WHEN MATCHED THEN
            UPDATE SET
                -- FIX: Prioritize CANCELLED from either status. Otherwise fallback to fulfillment status.
                InternalStatus = CASE
                    WHEN Source.OrderStatus = 'CANCELLED' OR Source.FulfillmentStatus = 'CANCELLED' THEN 'CANCELLED'
                    ELSE ISNULL(Source.FulfillmentStatus, Source.OrderStatus)
                END,
                TotalGrossAmount = Source.TotalGrossAmount, ShippingRevenueGross = ISNULL(Source.ShippingRevenueGross, 0), BuyerNip = Source.BuyerNip
        WHEN NOT MATCHED THEN
            INSERT (ClientId, AllegroOrderId, BuyerLogin, BuyerNip, InternalStatus, OrderDate, TotalGrossAmount, ShippingRevenueGross)
            VALUES (@ClientId, Source.AllegroOrderId, Source.BuyerLogin, Source.BuyerNip,
                CASE
                    WHEN Source.OrderStatus = 'CANCELLED' OR Source.FulfillmentStatus = 'CANCELLED' THEN 'CANCELLED'
                    ELSE ISNULL(Source.FulfillmentStatus, Source.OrderStatus)
                END,
                Source.OrderDate, Source.TotalGrossAmount, ISNULL(Source.ShippingRevenueGross, 0));

        SELECT o.InternalId, items.* INTO #TempItems
        FROM OPENJSON(@OrdersJson, '$.checkoutForms') WITH (AllegroOrderId UNIQUEIDENTIFIER '$.id', LineItemsJson NVARCHAR(MAX) '$.lineItems' AS JSON) f
        CROSS APPLY OPENJSON(f.LineItemsJson) WITH (
            AllegroLineItemId UNIQUEIDENTIFIER '$.id', AllegroOfferId NVARCHAR(50) '$.offer.id',
            OfferName NVARCHAR(255) '$.offer.name',
            Quantity INT '$.quantity', UnitPriceGross DECIMAL(12,2) '$.price.amount'
        ) items
        JOIN AllegroOrders o ON o.AllegroOrderId = f.AllegroOrderId AND o.ClientId = @ClientId;

        MERGE INTO OfferMasterData AS Target
        USING (SELECT DISTINCT AllegroOfferId, OfferName FROM #TempItems) AS Source
        ON Target.AllegroOfferId = Source.AllegroOfferId AND Target.ClientId = @ClientId
        WHEN MATCHED AND Target.ProductName = 'TEMP_PENDING_SYNC' THEN
            UPDATE SET ProductName = ISNULL(Source.OfferName, 'Unknown Product')
        WHEN NOT MATCHED THEN
            INSERT (AllegroOfferId, ClientId, ProductName)
            VALUES (Source.AllegroOfferId, @ClientId, ISNULL(Source.OfferName, 'Unknown Product'));

        MERGE INTO OrderLineItems AS Target
        USING #TempItems AS Source ON Target.AllegroLineItemId = Source.AllegroLineItemId
        WHEN NOT MATCHED THEN
            INSERT (InternalOrderId, AllegroOfferId, Quantity, UnitPriceGross, AllegroLineItemId)
            VALUES (Source.InternalId, Source.AllegroOfferId, Source.Quantity, Source.UnitPriceGross, Source.AllegroLineItemId)
        WHEN MATCHED THEN
            UPDATE SET Quantity = Source.Quantity, UnitPriceGross = Source.UnitPriceGross;

        UPDATE o SET ShippingVatRate = ISNULL((SELECT MAX(md.VatRateValue) FROM OrderLineItems li JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId WHERE li.InternalOrderId = o.InternalId), 23.00)
        FROM AllegroOrders o WHERE o.ClientId = @ClientId AND o.AllegroOrderId IN (SELECT AllegroOrderId FROM #TempOrders);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
