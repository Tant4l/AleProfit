CREATE OR ALTER PROCEDURE sp_UpsertAllegroOrdersFromJSON
    @ClientId UNIQUEIDENTIFIER,
    @OrdersJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @OrdersJson IS NULL OR ISJSON(@OrdersJson) = 0
    BEGIN
        RAISERROR('Invalid JSON input for sp_UpsertAllegroOrdersFromJSON', 16, 1);
        RETURN;
    END

    BEGIN TRY
        BEGIN TRANSACTION;

        CREATE TABLE #TempOrders (
            AllegroOrderId UNIQUEIDENTIFIER PRIMARY KEY,
            BuyerLogin NVARCHAR(100),
            BuyerNip NVARCHAR(20),
            OrderStatus NVARCHAR(50),
            FulfillmentStatus NVARCHAR(50),
            OrderDate DATETIMEOFFSET,
            TotalGrossAmount DECIMAL(12,2),
            ShippingRevenueGross DECIMAL(12,2)
        );

        INSERT INTO #TempOrders (
            AllegroOrderId, BuyerLogin, BuyerNip, OrderStatus,
            FulfillmentStatus, OrderDate, TotalGrossAmount, ShippingRevenueGross
        )
        SELECT
            AllegroOrderId, BuyerLogin, BuyerNip, OrderStatus,
            FulfillmentStatus, OrderDate, TotalGrossAmount, ShippingRevenueGross
        FROM OPENJSON(@OrdersJson, '$.checkoutForms')
        WITH (
            AllegroOrderId UNIQUEIDENTIFIER '$.id',
            BuyerLogin NVARCHAR(100) '$.buyer.login',
            OrderStatus NVARCHAR(50) '$.status',
            FulfillmentStatus NVARCHAR(50) '$.fulfillment.status',
            OrderDate DATETIMEOFFSET '$.updatedAt',
            TotalGrossAmount DECIMAL(12,2) '$.summary.totalToPay.amount',
            ShippingRevenueGross DECIMAL(12,2) '$.delivery.cost.amount',
            BuyerNip NVARCHAR(20) '$.invoice.address.company.taxId'
        );

        MERGE INTO AllegroOrders WITH (UPDLOCK, HOLDLOCK) AS Target
        USING #TempOrders AS Source
        ON Target.ClientId = @ClientId AND Target.AllegroOrderId = Source.AllegroOrderId
        WHEN MATCHED THEN
            UPDATE SET
                InternalStatus = CASE
                    WHEN Source.OrderStatus = 'CANCELLED' OR Source.FulfillmentStatus = 'CANCELLED' THEN 'CANCELLED'
                    ELSE COALESCE(Source.FulfillmentStatus, Source.OrderStatus)
                END,
                TotalGrossAmount = Source.TotalGrossAmount,
                ShippingRevenueGross = COALESCE(Source.ShippingRevenueGross, 0),
                BuyerNip = Source.BuyerNip,
                CreatedAt = Target.CreatedAt
        WHEN NOT MATCHED THEN
            INSERT (ClientId, AllegroOrderId, BuyerLogin, BuyerNip, InternalStatus, OrderDate, TotalGrossAmount, ShippingRevenueGross)
            VALUES (@ClientId, Source.AllegroOrderId, Source.BuyerLogin, Source.BuyerNip,
                CASE
                    WHEN Source.OrderStatus = 'CANCELLED' OR Source.FulfillmentStatus = 'CANCELLED' THEN 'CANCELLED'
                    ELSE COALESCE(Source.FulfillmentStatus, Source.OrderStatus)
                END,
                Source.OrderDate, Source.TotalGrossAmount, COALESCE(Source.ShippingRevenueGross, 0));

        CREATE TABLE #TempItems (
            InternalOrderId INT,
            AllegroLineItemId UNIQUEIDENTIFIER PRIMARY KEY,
            AllegroOfferId NVARCHAR(50),
            OfferName NVARCHAR(255),
            Quantity INT,
            UnitPriceGross DECIMAL(12,2)
        );

        INSERT INTO #TempItems (InternalOrderId, AllegroLineItemId, AllegroOfferId, OfferName, Quantity, UnitPriceGross)
        SELECT
            o.InternalId, items.AllegroLineItemId, items.AllegroOfferId, items.OfferName, items.Quantity, items.UnitPriceGross
        FROM OPENJSON(@OrdersJson, '$.checkoutForms')
        WITH (
            AllegroOrderId UNIQUEIDENTIFIER '$.id',
            LineItemsJson NVARCHAR(MAX) '$.lineItems' AS JSON
        ) f
        CROSS APPLY OPENJSON(f.LineItemsJson)
        WITH (
            AllegroLineItemId UNIQUEIDENTIFIER '$.id',
            AllegroOfferId NVARCHAR(50) '$.offer.id',
            OfferName NVARCHAR(255) '$.offer.name',
            Quantity INT '$.quantity',
            UnitPriceGross DECIMAL(12,2) '$.price.amount'
        ) items
        JOIN AllegroOrders o ON o.AllegroOrderId = f.AllegroOrderId AND o.ClientId = @ClientId;

        MERGE INTO OfferMasterData WITH (UPDLOCK, HOLDLOCK) AS Target
        USING (SELECT DISTINCT AllegroOfferId, OfferName FROM #TempItems) AS Source
        ON Target.AllegroOfferId = Source.AllegroOfferId AND Target.ClientId = @ClientId
        WHEN MATCHED AND Target.ProductName = 'TEMP_PENDING_SYNC' THEN
            UPDATE SET
                ProductName = COALESCE(Source.OfferName, 'Unknown Product'),
                UpdatedAt = SYSDATETIMEOFFSET()
        WHEN NOT MATCHED THEN
            INSERT (AllegroOfferId, ClientId, ProductName)
            VALUES (Source.AllegroOfferId, @ClientId, COALESCE(Source.OfferName, 'Unknown Product'));

        MERGE INTO OrderLineItems WITH (UPDLOCK, HOLDLOCK) AS Target
        USING #TempItems AS Source ON Target.AllegroLineItemId = Source.AllegroLineItemId
        WHEN MATCHED THEN
            UPDATE SET
                Quantity = Source.Quantity,
                UnitPriceGross = Source.UnitPriceGross
        WHEN NOT MATCHED THEN
            INSERT (InternalOrderId, AllegroOfferId, Quantity, UnitPriceGross, AllegroLineItemId)
            VALUES (Source.InternalOrderId, Source.AllegroOfferId, Source.Quantity, Source.UnitPriceGross, Source.AllegroLineItemId);

        UPDATE o
        SET ShippingVatRate = COALESCE((
            SELECT MAX(md.VatRateValue)
            FROM OrderLineItems li
            JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId AND md.ClientId = o.ClientId
            WHERE li.InternalOrderId = o.InternalId
        ), 23.00)
        FROM AllegroOrders o
        JOIN #TempOrders t ON o.AllegroOrderId = t.AllegroOrderId
        WHERE o.ClientId = @ClientId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
