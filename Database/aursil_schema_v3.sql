-- AurSil v3: admin role, vendors, stock inventory and stock purchases
-- Run: sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -i Database\aursil_schema_v3.sql

USE aursil;
GO

IF COL_LENGTH('dbo.Users', 'Role') IS NULL
    ALTER TABLE dbo.Users ADD Role NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_Role DEFAULT 'Customer';
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE dbo.Users ADD CONSTRAINT CK_Users_Role CHECK (Role IN ('Customer', 'Admin'));
GO

IF OBJECT_ID('dbo.Vendors') IS NULL
CREATE TABLE dbo.Vendors
(
    Id         INT IDENTITY(1,1) PRIMARY KEY,
    Name       NVARCHAR(150) NOT NULL,
    Phone      NVARCHAR(20)  NULL,
    Email      NVARCHAR(256) NULL,
    Address    NVARCHAR(300) NULL,
    IsActive   BIT           NOT NULL DEFAULT 1,
    CreatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Vendors_Name UNIQUE (Name)
);
GO

-- One row per metal: grams currently available to sell to customers. Never negative.
IF OBJECT_ID('dbo.StockInventory') IS NULL
CREATE TABLE dbo.StockInventory
(
    Metal      NVARCHAR(10)  NOT NULL PRIMARY KEY,     -- gold | silver | platinum
    Grams      DECIMAL(18,4) NOT NULL DEFAULT 0,
    UpdatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_StockInventory_Grams CHECK (Grams >= 0)
);
GO
INSERT INTO dbo.StockInventory (Metal)
SELECT m.Metal FROM (VALUES ('gold'), ('silver'), ('platinum')) AS m(Metal)
WHERE NOT EXISTS (SELECT 1 FROM dbo.StockInventory s WHERE s.Metal = m.Metal);
GO

-- Ledger of stock the admin bought from vendors
IF OBJECT_ID('dbo.StockPurchases') IS NULL
CREATE TABLE dbo.StockPurchases
(
    Id           INT IDENTITY(1,1) PRIMARY KEY,
    VendorId     INT           NOT NULL REFERENCES dbo.Vendors(Id),
    Metal        NVARCHAR(10)  NOT NULL,
    Grams        DECIMAL(18,4) NOT NULL,
    CostPerGram  DECIMAL(18,2) NOT NULL,
    TotalCost    DECIMAL(18,2) NOT NULL,
    PurchasedBy  NVARCHAR(50)  NOT NULL REFERENCES dbo.Users(UserId),
    PurchasedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_StockPurchases_Grams CHECK (Grams > 0),
    CONSTRAINT CK_StockPurchases_Metal CHECK (Metal IN ('gold', 'silver', 'platinum'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockPurchases_Vendor')
    CREATE INDEX IX_StockPurchases_Vendor ON dbo.StockPurchases (VendorId, PurchasedAt DESC);
GO
