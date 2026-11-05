-- AurSil v2: investments, payments, bank accounts, price history
-- Run: sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -i Database\aursil_schema_v2.sql

USE aursil;
GO

IF OBJECT_ID('dbo.PriceHistory') IS NULL
CREATE TABLE dbo.PriceHistory
(
    Id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    Metal         NVARCHAR(10)  NOT NULL,              -- gold | silver | platinum
    SpotUsdPerOz  DECIMAL(18,4) NOT NULL,
    UsdInr        DECIMAL(18,4) NOT NULL,
    BuyRate       DECIMAL(18,2) NOT NULL,              -- INR per gram, before GST
    SellRate      DECIMAL(18,2) NOT NULL,              -- INR per gram
    RecordedAt    DATETIME2     NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PriceHistory_Metal_Time')
    CREATE INDEX IX_PriceHistory_Metal_Time ON dbo.PriceHistory (Metal, RecordedAt);
GO

IF OBJECT_ID('dbo.Holdings') IS NULL
CREATE TABLE dbo.Holdings
(
    UserId          NVARCHAR(50)  NOT NULL REFERENCES dbo.Users(UserId),
    Metal           NVARCHAR(10)  NOT NULL,
    Grams           DECIMAL(18,4) NOT NULL DEFAULT 0,
    InvestedAmount  DECIMAL(18,2) NOT NULL DEFAULT 0,  -- total paid incl. GST
    UpdatedAt       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_Holdings PRIMARY KEY (UserId, Metal),
    CONSTRAINT CK_Holdings_Grams CHECK (Grams >= 0)
);
GO

IF OBJECT_ID('dbo.BankAccounts') IS NULL
CREATE TABLE dbo.BankAccounts
(
    Id             INT IDENTITY(1,1) PRIMARY KEY,
    UserId         NVARCHAR(50)  NOT NULL UNIQUE REFERENCES dbo.Users(UserId),
    HolderName     NVARCHAR(100) NOT NULL,
    AccountNumber  NVARCHAR(20)  NOT NULL,
    Ifsc           NVARCHAR(11)  NOT NULL,
    BankName       NVARCHAR(100) NOT NULL,
    Branch         NVARCHAR(150) NULL,
    CreatedAt      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('dbo.Orders') IS NULL
CREATE TABLE dbo.Orders
(
    Id                INT IDENTITY(1,1) PRIMARY KEY,
    OrderNo           NVARCHAR(30)  NOT NULL UNIQUE,
    UserId            NVARCHAR(50)  NOT NULL REFERENCES dbo.Users(UserId),
    Metal             NVARCHAR(10)  NOT NULL,
    Side              NVARCHAR(4)   NOT NULL,          -- Buy | Sell
    Grams             DECIMAL(18,4) NOT NULL,
    RatePerGram       DECIMAL(18,2) NOT NULL,
    Amount            DECIMAL(18,2) NOT NULL,          -- metal value
    Gst               DECIMAL(18,2) NOT NULL DEFAULT 0,
    Total             DECIMAL(18,2) NOT NULL,          -- paid (buy) or paid out (sell)
    Status            NVARCHAR(20)  NOT NULL,          -- Created | Completed | Failed | Processing
    PaymentGateway    NVARCHAR(20)  NULL,              -- razorpay | simulated | bank
    GatewayOrderId    NVARCHAR(50)  NULL,
    GatewayPaymentId  NVARCHAR(50)  NULL,
    BankAccountId     INT           NULL REFERENCES dbo.BankAccounts(Id),
    CreatedAt         DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt       DATETIME2     NULL,
    CONSTRAINT CK_Orders_Side CHECK (Side IN ('Buy', 'Sell')),
    CONSTRAINT CK_Orders_Status CHECK (Status IN ('Created', 'Completed', 'Failed', 'Processing'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Orders_User')
    CREATE INDEX IX_Orders_User ON dbo.Orders (UserId, CreatedAt DESC);
GO
