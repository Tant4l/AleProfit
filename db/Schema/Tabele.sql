CREATE TABLE Clients (
    ClientId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    CompanyName NVARCHAR(255) NOT NULL,
    TaxId NVARCHAR(50) NOT NULL, -- NIP
    LastOrderSyncAt DATETIMEOFFSET NULL, -- Delta Sync state for Orders
    LastBillingSyncAt DATETIMEOFFSET NULL, -- Delta Sync state for Ledger
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE TABLE ClientAllegroTokens (
    ClientId UNIQUEIDENTIFIER PRIMARY KEY FOREIGN KEY REFERENCES Clients(ClientId),
    AccessToken NVARCHAR(MAX) NOT NULL,
    RefreshToken NVARCHAR(MAX) NOT NULL,
    ExpiresAt DATETIMEOFFSET NOT NULL,
    UpdatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE TABLE OfferMasterData (
    AllegroOfferId NVARCHAR(50) PRIMARY KEY,
    ClientId UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES Clients(ClientId),
    ProductName NVARCHAR(255) NOT NULL,
    DefaultPurchasePriceNet DECIMAL(12,2) DEFAULT 0.00, -- COGS (Cena hurtowa)
    DefaultPackagingCostNet DECIMAL(12,2) DEFAULT 0.00, -- Karton, taśma
    VatRate DECIMAL(5,2) NOT NULL DEFAULT 23.00, -- Synced from /sale/offers
    UpdatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET(),
    IsVatSynced BIT DEFAULT 0
);

CREATE TABLE AllegroOrders (
    InternalId INT IDENTITY(1,1) PRIMARY KEY,
    ClientId UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES Clients(ClientId),
    AllegroOrderId UNIQUEIDENTIFIER NOT NULL,
    BuyerLogin NVARCHAR(100) NOT NULL,
    BuyerNip NVARCHAR(20) NULL, -- Identifies B2B transactions
    IsB2b AS (CASE WHEN BuyerNip IS NOT NULL THEN 1 ELSE 0 END), -- Computed Column
    KsefReferenceNumber NVARCHAR(50) NULL, -- 2026 Mandatory KSeF Readiness
    OrderDate DATETIMEOFFSET NOT NULL,
    TotalGrossAmount DECIMAL(12,2) NOT NULL,
    ShippingRevenueGross DECIMAL(12,2) DEFAULT 0.00, -- What buyer paid
    IsDeleted BIT DEFAULT 0, -- Soft delete for pre-fulfillment cancellations
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET(),
    OrderStatus NVARCHAR(50) DEFAULT 'UNKNOWN',
    CONSTRAINT UQ_Client_Order UNIQUE (ClientId, AllegroOrderId)
);

CREATE TABLE OrderLineItems (
    LineItemId INT IDENTITY(1,1) PRIMARY KEY,
    InternalOrderId INT NOT NULL FOREIGN KEY REFERENCES AllegroOrders(InternalId),
    AllegroOfferId NVARCHAR(50) NOT NULL FOREIGN KEY REFERENCES OfferMasterData(AllegroOfferId),
    Quantity INT NOT NULL CHECK (Quantity > 0),
    UnitPriceGross DECIMAL(12,2) NOT NULL,
    AllegroLineItemId UNIQUEIDENTIFIER
);

CREATE TABLE AllegroBillingEntries (
    BillingEntryId UNIQUEIDENTIFIER PRIMARY KEY,
    ClientId UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES Clients(ClientId),
    AllegroOrderId UNIQUEIDENTIFIER NULL,
    FeeType NVARCHAR(100) NOT NULL, -- 'SUCCES_FEE', 'DELIVERY_FEE', 'WZA_FEE'
    Amount DECIMAL(12,2) NOT NULL, -- Negative for costs, positive for refunds
    OccurredAt DATETIMEOFFSET NOT NULL
);

CREATE TABLE OrderRefunds (
    RefundId UNIQUEIDENTIFIER PRIMARY KEY,
    InternalOrderId INT NOT NULL FOREIGN KEY REFERENCES AllegroOrders(InternalId),
    RefundDate DATETIMEOFFSET NOT NULL, -- Dictates the tax month for the correction
    RefundAmountGross DECIMAL(12,2) NOT NULL CHECK (RefundAmountGross >= 0),
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE NONCLUSTERED INDEX IX_Orders_ClientId_Date ON AllegroOrders(ClientId, OrderDate);
CREATE NONCLUSTERED INDEX IX_Billing_OrderId ON AllegroBillingEntries(AllegroOrderId);
CREATE NONCLUSTERED INDEX IX_LineItems_OrderId ON OrderLineItems(InternalOrderId);

GO

CREATE OR ALTER VIEW vw_OrderProfitability_Detailed AS
SELECT
    o.AllegroOrderId,
    o.ClientId,
    -- Timezone conversion for Polish Accounting (CET/CEST)
    o.OrderDate AT TIME ZONE 'UTC' AT TIME ZONE 'Central European Standard Time' AS OrderDatePL,
    o.IsB2b,

    o.TotalGrossAmount AS RevenueGross,
    ISNULL(SUM(CAST(li.UnitPriceGross / (1 + (md.VatRate / 100.0)) * li.Quantity AS DECIMAL(12,2))), 0)
        + CAST(o.ShippingRevenueGross / 1.23 AS DECIMAL(12,2)) AS RevenueNet,

    ISNULL(SUM(md.DefaultPurchasePriceNet * li.Quantity), 0) AS TotalCogsNet,
    ISNULL(SUM(md.DefaultPackagingCostNet * li.Quantity), 0) AS TotalPackagingNet,

    ISNULL((SELECT CAST(SUM(ABS(Amount)) / 1.23 AS DECIMAL(12,2))
            FROM AllegroBillingEntries
            WHERE AllegroOrderId = o.AllegroOrderId AND FeeType NOT IN ('WZA_FEE', 'DELIVERY_FEE')), 0) AS CommissionsNet,

    ISNULL((SELECT CAST(SUM(ABS(Amount)) / 1.23 AS DECIMAL(12,2))
            FROM AllegroBillingEntries
            WHERE AllegroOrderId = o.AllegroOrderId AND FeeType IN ('WZA_FEE', 'DELIVERY_FEE')), 0) AS CourierCostsNet

FROM AllegroOrders o
LEFT JOIN OrderLineItems li ON o.InternalId = li.InternalOrderId
LEFT JOIN OfferMasterData md ON li.AllegroOfferId = md.AllegroOfferId
WHERE o.IsDeleted = 0
GROUP BY
    o.AllegroOrderId, o.ClientId, o.OrderDate, o.IsB2b, o.TotalGrossAmount, o.ShippingRevenueGross;
GO

CREATE OR ALTER PROCEDURE sp_GetDashboardAggregates
    @ClientId UNIQUEIDENTIFIER,
    @StartDatePL DATETIMEOFFSET,
    @EndDatePL DATETIMEOFFSET,
    @IncomeTaxRate DECIMAL(5,2) -- e.g., 19.00 for Liniowy
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        COUNT(AllegroOrderId) AS TotalOrdersProcessed,
        SUM(RevenueGross) AS GrandTotalRevenueGross,
        SUM(RevenueNet) AS GrandTotalRevenueNet,

        -- Deductions
        SUM(TotalCogsNet) AS TotalCOGS,
        SUM(TotalPackagingNet) AS TotalPackaging,
        SUM(CommissionsNet) AS TotalAllegroCommissions,
        SUM(CourierCostsNet) AS TotalCourierCosts,

        -- PRE-TAX PROFIT (Dochód)
        SUM(RevenueNet)
            - SUM(TotalCogsNet)
            - SUM(TotalPackagingNet)
            - SUM(CommissionsNet)
            - SUM(CourierCostsNet) AS IncomeBeforeTax,

        -- INCOME TAX (PIT/CIT)
        CAST((SUM(RevenueNet) - SUM(TotalCogsNet) - SUM(TotalPackagingNet) - SUM(CommissionsNet) - SUM(CourierCostsNet)) * (@IncomeTaxRate / 100.0) AS DECIMAL(12,2)) AS EstimatedIncomeTax,

        -- PURE PROFIT (Zysk Netto)
        CAST((SUM(RevenueNet) - SUM(TotalCogsNet) - SUM(TotalPackagingNet) - SUM(CommissionsNet) - SUM(CourierCostsNet)) * (1 - (@IncomeTaxRate / 100.0)) AS DECIMAL(12,2)) AS PureProfitAfterTax

    FROM vw_OrderProfitability_Detailed
    WHERE ClientId = @ClientId
      AND OrderDatePL >= @StartDatePL
      AND OrderDatePL <= @EndDatePL;
END;
GO
