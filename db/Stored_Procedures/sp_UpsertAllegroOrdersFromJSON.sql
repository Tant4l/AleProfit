CREATE OR ALTER PROCEDURE sp_UpsertAllegroOrdersFromJSON
    @ClientId UNIQUEIDENTIFIER,
    @OrdersJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        MERGE INTO AllegroOrders WITH (HOLDLOCK) AS Target
        USING (
            SELECT
                TRY_CAST(JSON_VALUE(value, '$.id') AS UNIQUEIDENTIFIER) AS AllegroOrderId,
                JSON_VALUE(value, '$.buyer.login') AS BuyerLogin,
                TRY_CAST(JSON_VALUE(value, '$.updatedAt') AS DATETIMEOFFSET) AS OrderDate,
                TRY_CAST(JSON_VALUE(value, '$.summary.totalToPay.amount') AS DECIMAL(12,2)) AS TotalGrossAmount,
                TRY_CAST(JSON_VALUE(value, '$.delivery.cost.amount') AS DECIMAL(12,2)) AS ShippingRevenueGross
            FROM OPENJSON(@OrdersJson, '$.checkoutForms')
        ) AS Source
        ON Target.ClientId = @ClientId AND Target.AllegroOrderId = Source.AllegroOrderId
        WHEN MATCHED THEN
            UPDATE SET
                BuyerLogin = Source.BuyerLogin,
                TotalGrossAmount = Source.TotalGrossAmount,
                ShippingRevenueGross = ISNULL(Source.ShippingRevenueGross, 0)
        WHEN NOT MATCHED THEN
            INSERT (ClientId, AllegroOrderId, BuyerLogin, OrderDate, TotalGrossAmount, ShippingRevenueGross)
            VALUES (@ClientId, Source.AllegroOrderId, Source.BuyerLogin, Source.OrderDate, Source.TotalGrossAmount, ISNULL(Source.ShippingRevenueGross, 0));

        SELECT
            o.InternalId AS InternalOrderId,
            JSON_VALUE(items.value, '$.offer.id') AS AllegroOfferId,
            JSON_VALUE(items.value, '$.offer.name') AS ProductName,
            TRY_CAST(JSON_VALUE(items.value, '$.quantity') AS INT) AS Quantity,
            TRY_CAST(JSON_VALUE(items.value, '$.price.amount') AS DECIMAL(12,2)) AS UnitPriceGross
        INTO #TempLineItems
        FROM OPENJSON(@OrdersJson, '$.checkoutForms') form
        CROSS APPLY OPENJSON(form.value, '$.lineItems') items
        INNER JOIN AllegroOrders o ON o.AllegroOrderId = TRY_CAST(JSON_VALUE(form.value, '$.id') AS UNIQUEIDENTIFIER)
        WHERE o.ClientId = @ClientId;

        INSERT INTO OfferMasterData (AllegroOfferId, ClientId, ProductName, DefaultPurchasePriceNet, DefaultPackagingCostNet, VatRate)
        SELECT DISTINCT t.AllegroOfferId, @ClientId, t.ProductName, 0.00, 0.00, 23.00
        FROM #TempLineItems t
        WHERE NOT EXISTS (SELECT 1 FROM OfferMasterData m WHERE m.AllegroOfferId = t.AllegroOfferId);

        DELETE FROM OrderLineItems
        WHERE InternalOrderId IN (SELECT DISTINCT InternalOrderId FROM #TempLineItems);

        INSERT INTO OrderLineItems (InternalOrderId, AllegroOfferId, Quantity, UnitPriceGross)
        SELECT InternalOrderId, AllegroOfferId, Quantity, UnitPriceGross
        FROM #TempLineItems;

        DROP TABLE #TempLineItems;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
