-- 1. Table for OAuth Token Management
-- Proves you know how to handle state in a stateless serverless architecture.
CREATE TABLE AppConfiguration (
    ConfigKey NVARCHAR(50) PRIMARY KEY,
    ConfigValue NVARCHAR(MAX) NOT NULL,
    UpdatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

-- 2. Core Orders Table
CREATE TABLE AllegroOrders (
    InternalId INT IDENTITY(1,1) PRIMARY KEY,
    AllegroOrderId UNIQUEIDENTIFIER NOT NULL UNIQUE, -- Allegro uses UUIDs
    BuyerLogin NVARCHAR(100) NOT NULL,
    OrderDate DATETIMEOFFSET NOT NULL,
    TotalGrossAmount DECIMAL(12,2) NOT NULL CHECK (TotalGrossAmount >= 0),
    Currency NVARCHAR(3) DEFAULT 'PLN',
    PaymentStatus NVARCHAR(50),
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

-- 3. Order Line Items (1:N Relationship)
CREATE TABLE OrderLineItems (
    LineItemId INT IDENTITY(1,1) PRIMARY KEY,
    InternalOrderId INT NOT NULL FOREIGN KEY REFERENCES AllegroOrders(InternalId),
    AllegroOfferId NVARCHAR(50) NOT NULL,
    ProductName NVARCHAR(255) NOT NULL,
    Quantity INT NOT NULL CHECK (Quantity > 0),
    UnitPriceGross DECIMAL(12,2) NOT NULL,
    VatRate DECIMAL(5,2), -- Essential for a tax firm
    CommissionAmount DECIMAL(12,2) DEFAULT 0.00,
    BaseCost DECIMAL(12,2) DEFAULT 0.00 -- COGS (Cost of Goods Sold)
);

-- 4. Indexing for API UPSERT Operations
-- Crucial for a DB maintenance role. This speeds up checking if an order exists.
CREATE NONCLUSTERED INDEX IX_AllegroOrders_AllegroOrderId
ON AllegroOrders(AllegroOrderId);
