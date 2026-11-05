-- AurSil database schema (SQL Server)
-- Run: sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -i Database\aursil_schema.sql

IF DB_ID('aursil') IS NULL
    CREATE DATABASE aursil;
GO

USE aursil;
GO

IF OBJECT_ID('dbo.Users') IS NULL
CREATE TABLE dbo.Users
(
    UserId        NVARCHAR(50)  NOT NULL PRIMARY KEY,
    Name          NVARCHAR(100) NOT NULL,
    Email         NVARCHAR(256) NOT NULL,
    Phone         NVARCHAR(15)  NOT NULL,
    PasswordHash  NVARCHAR(256) NOT NULL,              -- PBKDF2, never plain text
    Status        NVARCHAR(20)  NOT NULL DEFAULT 'Pending', -- Pending | Active | Blocked
    CreatedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    LastLoginAt   DATETIME2     NULL,
    CONSTRAINT UQ_Users_Email UNIQUE (Email),
    CONSTRAINT UQ_Users_Phone UNIQUE (Phone),
    CONSTRAINT CK_Users_Status CHECK (Status IN ('Pending', 'Active', 'Blocked'))
);
GO

IF OBJECT_ID('dbo.OtpCodes') IS NULL
CREATE TABLE dbo.OtpCodes
(
    Id         INT IDENTITY(1,1) PRIMARY KEY,
    Email      NVARCHAR(256) NOT NULL,
    CodeHash   NVARCHAR(128) NOT NULL,                 -- SHA-256 of the 6-digit code
    Purpose    NVARCHAR(20)  NOT NULL,                 -- Register | ResetPassword
    ExpiresAt  DATETIME2     NOT NULL,
    Attempts   INT           NOT NULL DEFAULT 0,
    IsUsed     BIT           NOT NULL DEFAULT 0,
    CreatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OtpCodes_Email_Purpose')
    CREATE INDEX IX_OtpCodes_Email_Purpose ON dbo.OtpCodes (Email, Purpose, IsUsed);
GO
