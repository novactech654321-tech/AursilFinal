-- AurSil v7: invoices. Every completed order gets a sequential invoice number.
-- Run: sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -I -i Database\aursil_schema_v7.sql

USE aursil;
GO
SET QUOTED_IDENTIFIER ON;  -- needed for the filtered index below
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'InvoiceSeq')
    CREATE SEQUENCE dbo.InvoiceSeq AS INT START WITH 1 INCREMENT BY 1;
GO

IF COL_LENGTH('dbo.Orders', 'InvoiceNo') IS NULL
    ALTER TABLE dbo.Orders ADD InvoiceNo NVARCHAR(30) NULL;      -- e.g. INV-2026-000001, set when the order completes
GO

-- Give invoice numbers to orders that completed before invoices existed (oldest first)
DECLARE @id INT;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT Id FROM dbo.Orders WHERE Status = 'Completed' AND InvoiceNo IS NULL ORDER BY CompletedAt, Id;
OPEN c; FETCH NEXT FROM c INTO @id;
WHILE @@FETCH_STATUS = 0
BEGIN
    UPDATE dbo.Orders
    SET InvoiceNo = 'INV-' + CAST(YEAR(ISNULL(CompletedAt, CreatedAt)) AS VARCHAR(4)) + '-' + RIGHT('000000' + CAST(NEXT VALUE FOR dbo.InvoiceSeq AS VARCHAR(10)), 6)
    WHERE Id = @id;
    FETCH NEXT FROM c INTO @id;
END
CLOSE c; DEALLOCATE c;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Orders_InvoiceNo')
    CREATE UNIQUE INDEX UX_Orders_InvoiceNo ON dbo.Orders (InvoiceNo) WHERE InvoiceNo IS NOT NULL;
GO
