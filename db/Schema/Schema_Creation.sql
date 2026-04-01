CREATE TABLE Clients (
    ClientId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    CompanyName NVARCHAR(255) NOT NULL,
    TaxId NVARCHAR(50) NOT NULL UNIQUE,
    LastOrderSyncAt DATETIMEOFFSET NULL,
    LastBillingSyncAt DATETIMEOFFSET NULL,
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE TABLE ClientAllegroTokens (
    ClientId UNIQUEIDENTIFIER PRIMARY KEY FOREIGN KEY REFERENCES Clients(ClientId),
    AccessToken NVARCHAR(MAX) NOT NULL,
    RefreshToken NVARCHAR(MAX) NOT NULL,
    ExpiresAt DATETIMEOFFSET NOT NULL,
    UpdatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE TABLE AllegroFeeCategories (
    FeeType NVARCHAR(100) PRIMARY KEY,
    CategoryGroup NVARCHAR(50) NOT NULL,
    IsDeductibleCost BIT DEFAULT 1
);

INSERT INTO AllegroFeeCategories (FeeType, CategoryGroup, IsDeductibleCost) VALUES
('SUCCESS_FEE', 'COMMISSION', 1), ('COMMISSION_REFUND', 'COMMISSION', 1),
('WZA_FEE', 'LOGISTICS', 1), ('DELIVERY_FEE', 'LOGISTICS', 1),
('AD_FEE', 'MARKETING', 1), ('SUBSCRIPTION_FEE', 'MARKETING', 1),
('PAD', 'ADJUSTMENT', 0),
('SUBSCRIBTION_MAINTENANCE_FEE', 'MARKETING', 1),
('OFFER_MAINTENANCE_FEE', 'COMMISSION', 1),
('AD_FEE_EXTENDED', 'MARKETING', 1),
('COMMISSION_REFUND', 'COMMISSION', 1);

CREATE TABLE OfferMasterData (
    AllegroOfferId NVARCHAR(50) PRIMARY KEY,
    ClientId UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES Clients(ClientId),
    ProductName NVARCHAR(255) NOT NULL,
    DefaultPurchasePriceNet DECIMAL(12,2) DEFAULT 0.00,
    DefaultPackagingCostNet DECIMAL(12,2) DEFAULT 0.00,
    VatRateCode NVARCHAR(10) NOT NULL DEFAULT '23',
    VatRateValue DECIMAL(5,2) NOT NULL DEFAULT 23.00,
    IsVatSynced BIT DEFAULT 0,
    UpdatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE TABLE AllegroOrders (
    InternalId INT IDENTITY(1,1) PRIMARY KEY,
    ClientId UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES Clients(ClientId),
    AllegroOrderId UNIQUEIDENTIFIER NOT NULL,
    BuyerLogin NVARCHAR(100) NOT NULL,
    BuyerNip NVARCHAR(20) NULL,
    InternalStatus NVARCHAR(50) DEFAULT 'PAID',
    OrderDate DATETIMEOFFSET NOT NULL,
    TotalGrossAmount DECIMAL(12,2) NOT NULL,
    ShippingRevenueGross DECIMAL(12,2) DEFAULT 0.00,
    ShippingVatRate DECIMAL(5,2) DEFAULT 23.00,
    KsefReferenceNumber NVARCHAR(100) NULL,
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT UQ_Client_Order UNIQUE (ClientId, AllegroOrderId)
);

CREATE TABLE OrderLineItems (
    LineItemId INT IDENTITY(1,1) PRIMARY KEY,
    InternalOrderId INT NOT NULL FOREIGN KEY REFERENCES AllegroOrders(InternalId),
    AllegroOfferId NVARCHAR(50) NOT NULL FOREIGN KEY REFERENCES OfferMasterData(AllegroOfferId),
    Quantity INT NOT NULL CHECK (Quantity > 0),
    UnitPriceGross DECIMAL(12,2) NOT NULL,
    AllegroLineItemId UNIQUEIDENTIFIER NOT NULL UNIQUE
);

CREATE TABLE AllegroBillingEntries (
    BillingEntryId UNIQUEIDENTIFIER PRIMARY KEY NONCLUSTERED,
    ClientId UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES Clients(ClientId),
    AllegroOrderId UNIQUEIDENTIFIER NULL,
    FeeType NVARCHAR(100) NOT NULL,
    Amount DECIMAL(12,2) NOT NULL,
    OccurredAt DATETIMEOFFSET NOT NULL,
    VatRate DECIMAL(5,2) NOT NULL DEFAULT 23.00
);
CREATE CLUSTERED INDEX CIX_Billing_Date ON AllegroBillingEntries(OccurredAt);

CREATE TABLE OrderRefunds (
    RefundId UNIQUEIDENTIFIER PRIMARY KEY NONCLUSTERED,
    InternalOrderId INT NOT NULL FOREIGN KEY REFERENCES AllegroOrders(InternalId),
    RefundDate DATETIMEOFFSET NOT NULL,
    RefundAmountGross DECIMAL(12,2) NOT NULL CHECK (RefundAmountGross > 0),
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET(),
    RefundAmountNet DECIMAL(12,2) NOT NULL DEFAULT 0.00
);
CREATE CLUSTERED INDEX CIX_Refunds_Date ON OrderRefunds(RefundDate);

CREATE NONCLUSTERED INDEX IX_AllegroOrders_ClientId_OrderDate
ON AllegroOrders (ClientId, OrderDate)
INCLUDE (InternalStatus, TotalGrossAmount, ShippingRevenueGross, ShippingVatRate);

CREATE NONCLUSTERED INDEX IX_AllegroBillingEntries_AllegroOrderId_FeeType
ON AllegroBillingEntries (AllegroOrderId, FeeType)
INCLUDE (Amount);

CREATE NONCLUSTERED INDEX IX_OrderLineItems_InternalOrderId
ON OrderLineItems (InternalOrderId)
INCLUDE (AllegroOfferId, Quantity, UnitPriceGross);

GO
