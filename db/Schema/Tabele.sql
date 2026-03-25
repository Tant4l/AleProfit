CREATE TABLE Clients (
    ClientId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    CompanyName NVARCHAR(255) NOT NULL,
    TaxId NVARCHAR(50), -- NIP (Crucial for Polish bookkeeping)
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE TABLE ClientAllegroTokens (
    ClientId UNIQUEIDENTIFIER PRIMARY KEY FOREIGN KEY REFERENCES Clients(ClientId),
    AccessToken NVARCHAR(MAX) NOT NULL,
    RefreshToken NVARCHAR(MAX) NOT NULL,
    ExpiresAt DATETIMEOFFSET NOT NULL,
    UpdatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);

CREATE TABLE OAuthStateTracker (
    StateId UNIQUEIDENTIFIER PRIMARY KEY,
    ClientId UNIQUEIDENTIFIER FOREIGN KEY REFERENCES Clients(ClientId),
    CreatedAt DATETIMEOFFSET DEFAULT SYSDATETIMEOFFSET()
);


CREATE TABLE AllegroOrders (
    InternalId INT IDENTITY(1,1) PRIMARY KEY,
    ClientId UNIQUEIDENTIFIER NOT NULL FOREIGN KEY REFERENCES Clients(ClientId),
    AllegroOrderId UNIQUEIDENTIFIER NOT NULL,
    BuyerLogin NVARCHAR(100) NOT NULL,
    OrderDate DATETIMEOFFSET NOT NULL,
    TotalGrossAmount DECIMAL(12,2) NOT NULL CHECK (TotalGrossAmount >= 0),
    Currency NVARCHAR(3) DEFAULT 'PLN',
    -- Ensures an order is unique PER client, not globally
    CONSTRAINT UQ_Client_AllegroOrder UNIQUE (ClientId, AllegroOrderId)
);

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

CREATE TABLE AllegroBillingEntries (
    BillingEntryId UNIQUEIDENTIFIER PRIMARY KEY,
    AllegroOrderId UNIQUEIDENTIFIER NULL, -- Links to your AllegroOrders table
    FeeType NVARCHAR(100) NOT NULL,       -- e.g., 'SUCCES_FEE', 'SMART_DELIVERY_FEE'
    Amount DECIMAL(12,2) NOT NULL,        -- Will be a negative number for charges
    Currency NVARCHAR(3) DEFAULT 'PLN',
    OccurredAt DATETIMEOFFSET NOT NULL
);

CREATE NONCLUSTERED INDEX IX_Billing_OrderId ON AllegroBillingEntries(AllegroOrderId);

CREATE NONCLUSTERED INDEX IX_AllegroOrders_AllegroOrderId
ON AllegroOrders(AllegroOrderId);
