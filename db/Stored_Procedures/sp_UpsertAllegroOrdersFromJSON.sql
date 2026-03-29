CREATE OR ALTER PROCEDURE sp_UpsertAllegroOrdersFromJSON
    @ClientId UNIQUEIDENTIFIER,
    @OrdersJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT * INTO #TempOrders
        FROM OPENJSON(@OrdersJson, '$.checkoutForms')
        WITH (
            AllegroOrderId UNIQUEIDENTIFIER '$.id',
            BuyerLogin NVARCHAR(100) '$.buyer.login',
            OrderStatus NVARCHAR(50) '$.status', -- Extracted Status
            OrderDate DATETIMEOFFSET '$.updatedAt',
            TotalGrossAmount DECIMAL(12,2) '$.summary.totalToPay.amount',
            ShippingRevenueGross DECIMAL(12,2) '$.delivery.cost.amount',
            BuyerNip NVARCHAR(20) '$.invoice.address.company.taxId'
        );

        MERGE INTO AllegroOrders WITH (HOLDLOCK, SERIALIZABLE) AS Target
        USING #TempOrders AS Source
        ON Target.ClientId = @ClientId AND Target.AllegroOrderId = Source.AllegroOrderId
        WHEN MATCHED THEN
            UPDATE SET
                BuyerLogin = Source.BuyerLogin,
                OrderStatus = Source.OrderStatus,
                -- If Allegro says it's cancelled, soft-delete it from our tax calculations
                IsDeleted = CASE WHEN Source.OrderStatus = 'CANCELLED' THEN 1 ELSE 0 END,
                TotalGrossAmount = Source.TotalGrossAmount,
                ShippingRevenueGross = ISNULL(Source.ShippingRevenueGross, 0),
                BuyerNip = Source.BuyerNip
        WHEN NOT MATCHED THEN
            INSERT (ClientId, AllegroOrderId, BuyerLogin, BuyerNip, OrderStatus, IsDeleted, OrderDate, TotalGrossAmount, ShippingRevenueGross)
            VALUES (@ClientId, Source.AllegroOrderId, Source.BuyerLogin, Source.BuyerNip, Source.OrderStatus,
                    CASE WHEN Source.OrderStatus = 'CANCELLED' THEN 1 ELSE 0 END,
                    Source.OrderDate, Source.TotalGrossAmount, ISNULL(Source.ShippingRevenueGross, 0));

        SELECT
            o.InternalId AS InternalOrderId,
            items.AllegroLineItemId,
            items.AllegroOfferId,
            items.ProductName,
            items.Quantity,
            items.UnitPriceGross
        INTO #TempLineItems
        FROM OPENJSON(@OrdersJson, '$.checkoutForms')
        WITH (
            AllegroOrderId UNIQUEIDENTIFIER '$.id',
            LineItemsJson NVARCHAR(MAX) '$.lineItems' AS JSON
        ) form
        CROSS APPLY OPENJSON(form.LineItemsJson)
        WITH (
            AllegroLineItemId UNIQUEIDENTIFIER '$.id', -- Unique ID per cart entry
            AllegroOfferId NVARCHAR(50) '$.offer.id',
            ProductName NVARCHAR(255) '$.offer.name',
            Quantity INT '$.quantity',
            UnitPriceGross DECIMAL(12,2) '$.price.amount'
        ) items
        INNER JOIN AllegroOrders o ON o.AllegroOrderId = form.AllegroOrderId
        WHERE o.ClientId = @ClientId;

        INSERT INTO OfferMasterData (AllegroOfferId, ClientId, ProductName, DefaultPurchasePriceNet, DefaultPackagingCostNet, VatRate, IsVatSynced)
        SELECT DISTINCT t.AllegroOfferId, @ClientId, t.ProductName, 0.00, 0.00, 23.00, 0
        FROM #TempLineItems t
        WHERE NOT EXISTS (SELECT 1 FROM OfferMasterData m WHERE m.AllegroOfferId = t.AllegroOfferId);

        MERGE INTO OrderLineItems WITH (HOLDLOCK, SERIALIZABLE) AS Target
        USING #TempLineItems AS Source
        ON Target.AllegroLineItemId = Source.AllegroLineItemId
        WHEN MATCHED THEN
            UPDATE SET
                Quantity = Source.Quantity,
                UnitPriceGross = Source.UnitPriceGross
        WHEN NOT MATCHED THEN
            INSERT (InternalOrderId, AllegroLineItemId, AllegroOfferId, Quantity, UnitPriceGross)
            VALUES (Source.InternalOrderId, Source.AllegroLineItemId, Source.AllegroOfferId, Source.Quantity, Source.UnitPriceGross);

        DROP TABLE #TempOrders;
        DROP TABLE #TempLineItems;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
