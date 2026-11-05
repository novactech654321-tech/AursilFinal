-- AurSil v4: MCP servers each user connected to the AI chat
-- Run: sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -i Database\aursil_schema_v4.sql

USE aursil;
GO

IF OBJECT_ID('dbo.McpServers') IS NULL
CREATE TABLE dbo.McpServers
(
    Id         INT IDENTITY(1,1) PRIMARY KEY,
    UserId     NVARCHAR(50)  NOT NULL REFERENCES dbo.Users(UserId),
    Name       NVARCHAR(40)  NOT NULL,
    Url        NVARCHAR(500) NOT NULL,
    AuthToken  NVARCHAR(2000) NULL,                    -- encrypted with ASP.NET Data Protection, never plain text
    CreatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_McpServers_User_Name UNIQUE (UserId, Name)
);
GO
