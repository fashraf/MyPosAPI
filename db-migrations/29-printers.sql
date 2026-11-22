/*==============================================================================
  Nazad POS — 29 · Printers and cloud print jobs

  Printers are configured per branch. A phone on mobile data cannot reach a LAN
  printer, so "cloud" printers pull their jobs from this server instead:
  Star CloudPRNT and Epson Server Direct Print both poll an HTTPS URL. A job is
  the ESC/POS bytes the POS rendered (text mode ≈ 1 KB, image mode ≈ 30–60 KB).
==============================================================================*/

USE POSMAIN;
GO

IF SCHEMA_ID(N'prn') IS NULL EXEC(N'CREATE SCHEMA prn');
GO

IF OBJECT_ID(N'prn.Printer') IS NULL
CREATE TABLE prn.Printer
(
    PrinterId      NVARCHAR(40)  NOT NULL CONSTRAINT PK_Printer PRIMARY KEY,
    BranchId       NVARCHAR(40)  NULL CONSTRAINT FK_Printer_Branch REFERENCES core.Branch (BranchId),
    Name           NVARCHAR(120) NOT NULL,
    Connection     NVARCHAR(20)  NOT NULL,
    PaperWidth     INT           NOT NULL CONSTRAINT DF_Printer_Paper DEFAULT (80),
    RenderMode     NVARCHAR(10)  NOT NULL CONSTRAINT DF_Printer_Mode DEFAULT (N'auto'),
    DataSaver      BIT           NOT NULL CONSTRAINT DF_Printer_Saver DEFAULT (0),
    Copies         INT           NOT NULL CONSTRAINT DF_Printer_Copies DEFAULT (1),
    Documents      NVARCHAR(100) NOT NULL,           -- receipt,gift,kitchen,a4
    DefaultFor     NVARCHAR(100) NOT NULL CONSTRAINT DF_Printer_Default DEFAULT (N''),
    Host           NVARCHAR(120) NULL,
    Port           INT           NULL,
    Protocol       NVARCHAR(20)  NULL,
    Url            NVARCHAR(400) NULL,
    /* Cloud: the printer identifies itself by MAC/serial when it polls. */
    CloudId        NVARCHAR(60)  NULL,
    Status         NVARCHAR(10)  NOT NULL CONSTRAINT DF_Printer_Status DEFAULT (N'active'),
    LastSeenAtUtc  DATETIME2(0)  NULL,
    UpdatedAtUtc   DATETIME2(3)  NOT NULL CONSTRAINT DF_Printer_Updated DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_Printer_Connection CHECK (Connection IN (N'bluetooth', N'network', N'cloud', N'system', N'preview')),
    CONSTRAINT CK_Printer_Paper      CHECK (PaperWidth IN (58, 80)),
    CONSTRAINT CK_Printer_Copies     CHECK (Copies BETWEEN 1 AND 5)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Printer_CloudId')
CREATE UNIQUE INDEX UX_Printer_CloudId ON prn.Printer (CloudId) WHERE CloudId IS NOT NULL;
GO

IF OBJECT_ID(N'prn.PrintJob') IS NULL
CREATE TABLE prn.PrintJob
(
    JobId          NVARCHAR(40)    NOT NULL CONSTRAINT PK_PrintJob PRIMARY KEY,
    PrinterId      NVARCHAR(40)    NOT NULL CONSTRAINT FK_PrintJob_Printer REFERENCES prn.Printer (PrinterId),
    Document       NVARCHAR(20)    NOT NULL,
    Reference      NVARCHAR(60)    NOT NULL,
    Copies         INT             NOT NULL CONSTRAINT DF_PrintJob_Copies DEFAULT (1),
    Payload        VARBINARY(MAX)  NOT NULL,
    Status         NVARCHAR(10)    NOT NULL CONSTRAINT DF_PrintJob_Status DEFAULT (N'queued'),
    Attempts       INT             NOT NULL CONSTRAINT DF_PrintJob_Attempts DEFAULT (0),
    CreatedBy      NVARCHAR(160)   NULL,
    CreatedAtUtc   DATETIME2(3)    NOT NULL CONSTRAINT DF_PrintJob_Created DEFAULT (SYSUTCDATETIME()),
    PrintedAtUtc   DATETIME2(3)    NULL,
    Error          NVARCHAR(400)   NULL,
    CONSTRAINT CK_PrintJob_Status CHECK (Status IN (N'queued', N'printing', N'printed', N'failed'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrintJob_Queue')
CREATE INDEX IX_PrintJob_Queue ON prn.PrintJob (PrinterId, Status, CreatedAtUtc) WHERE Status IN (N'queued', N'printing');
GO

/* Jobs stuck "printing" for 2 minutes go back to the queue (printer lost power). */
CREATE OR ALTER PROCEDURE prn.usp_NextJob
    @CloudId NVARCHAR(60)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @PrinterId NVARCHAR(40) = (SELECT PrinterId FROM prn.Printer WHERE CloudId = @CloudId AND Status = N'active');
    IF @PrinterId IS NULL RETURN;

    UPDATE prn.Printer SET LastSeenAtUtc = SYSUTCDATETIME() WHERE PrinterId = @PrinterId;
    UPDATE prn.PrintJob SET Status = N'queued'
     WHERE PrinterId = @PrinterId AND Status = N'printing' AND CreatedAtUtc < DATEADD(MINUTE, -2, SYSUTCDATETIME()) AND Attempts < 5;

    UPDATE TOP (1) j
       SET Status = N'printing', Attempts = Attempts + 1
    OUTPUT inserted.JobId, inserted.Payload, inserted.Copies
      FROM prn.PrintJob AS j WITH (UPDLOCK, READPAST, ROWLOCK)
     WHERE j.JobId = (SELECT TOP (1) JobId FROM prn.PrintJob WITH (UPDLOCK, READPAST)
                       WHERE PrinterId = @PrinterId AND Status = N'queued' ORDER BY CreatedAtUtc);
END;
GO

PRINT N'29-printers applied.';
GO
