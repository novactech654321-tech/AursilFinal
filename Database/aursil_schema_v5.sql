-- AurSil v5: personal access tokens so external AI apps can use AurSil over MCP
-- Run: sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -i Database\aursil_schema_v5.sql

USE aursil;
GO

IF OBJECT_ID('dbo.ApiTokens') IS NULL
CREATE TABLE dbo.ApiTokens
(
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    UserId      NVARCHAR(50)  NOT NULL REFERENCES dbo.Users(UserId),
    Name        NVARCHAR(60)  NOT NULL,
    TokenHash   NVARCHAR(64)  NOT NULL,                -- SHA-256 of the token; the token itself is shown once and never stored
    CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    LastUsedAt  DATETIME2     NULL,
    CONSTRAINT UQ_ApiTokens_Hash UNIQUE (TokenHash)
);
GO
