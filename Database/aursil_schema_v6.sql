-- AurSil v6: autopay (daily recurring buy plans) and their run history
-- Run: sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -i Database\aursil_schema_v6.sql

USE aursil;
GO
SET QUOTED_IDENTIFIER ON;  -- needed for the filtered index below
GO

IF OBJECT_ID('dbo.AutopayPlans') IS NULL
CREATE TABLE dbo.AutopayPlans
(
    Id             INT IDENTITY(1,1) PRIMARY KEY,
    UserId         NVARCHAR(50)  NOT NULL REFERENCES dbo.Users(UserId),
    Metal          NVARCHAR(10)  NOT NULL,              -- gold | silver | platinum
    AmountInr      DECIMAL(18,2) NOT NULL,              -- rupees bought every day (GST included)
    TotalLimitInr  DECIMAL(18,2) NULL,                  -- optional cap on the total spent by this plan
    TotalSpent     DECIMAL(18,2) NOT NULL DEFAULT 0,
    RunHour        TINYINT       NOT NULL,              -- hour of day (India time, 0-23) the plan runs
    Status         NVARCHAR(12)  NOT NULL DEFAULT 'Active',
    NextRunAt      DATETIME2     NOT NULL,              -- UTC
    LastRunAt      DATETIME2     NULL,
    CreatedAt      DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_AutopayPlans_Status CHECK (Status IN ('Active', 'Paused', 'Completed')),
    CONSTRAINT CK_AutopayPlans_Metal  CHECK (Metal IN ('gold', 'silver', 'platinum')),
    CONSTRAINT CK_AutopayPlans_Hour   CHECK (RunHour BETWEEN 0 AND 23),
    CONSTRAINT CK_AutopayPlans_Amount CHECK (AmountInr > 0)
);
GO
-- One live plan per user and metal
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_AutopayPlans_User_Metal')
    CREATE UNIQUE INDEX UX_AutopayPlans_User_Metal ON dbo.AutopayPlans (UserId, Metal) WHERE Status IN ('Active', 'Paused');
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AutopayPlans_Due')
    CREATE INDEX IX_AutopayPlans_Due ON dbo.AutopayPlans (Status, NextRunAt);
GO

IF OBJECT_ID('dbo.AutopayRuns') IS NULL
CREATE TABLE dbo.AutopayRuns
(
    Id        INT IDENTITY(1,1) PRIMARY KEY,
    PlanId    INT           NOT NULL REFERENCES dbo.AutopayPlans(Id) ON DELETE CASCADE,
    OrderNo   NVARCHAR(30)  NULL,
    Amount    DECIMAL(18,2) NOT NULL,
    Status    NVARCHAR(20)  NOT NULL,                   -- Success | Failed | AwaitingPayment
    Message   NVARCHAR(300) NULL,
    RunAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_AutopayRuns_Status CHECK (Status IN ('Success', 'Failed', 'AwaitingPayment'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AutopayRuns_Plan')
    CREATE INDEX IX_AutopayRuns_Plan ON dbo.AutopayRuns (PlanId, RunAt DESC);
GO
