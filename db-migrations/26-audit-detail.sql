/*==============================================================================
  Nazad POS — 26 · Audit detail

  The trail recorded that a call happened, not what it changed. "Someone updated
  an item at 14:32" is a timestamp. A person needs the price before and after,
  the table it landed in, and the row.
==============================================================================*/

USE POSMAIN;
GO

/* A separate table, not more columns: one call writes several tables, and a
   JSON blob cannot be filtered. "Everything that touched inv.StockLevel last
   week" is the question this exists to answer. */
CREATE TABLE audit.AuditChange
(
    ChangeId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditChange PRIMARY KEY,
    AuditId       BIGINT        NOT NULL,
    SchemaName    NVARCHAR(40)  NOT NULL,
    TableName     NVARCHAR(80)  NOT NULL,
    Operation     NVARCHAR(10)  NOT NULL,
    PrimaryKey    NVARCHAR(200) NULL,
    /* The row this one points at, when it explains the change. */
    ForeignKey    NVARCHAR(200) NULL,
    ForeignTable  NVARCHAR(120) NULL,
    /* One row per field that moved, not per row written. */
    ColumnName    NVARCHAR(80)  NULL,
    OldValue      NVARCHAR(400) NULL,
    NewValue      NVARCHAR(400) NULL,
    RowsAffected  INT           NULL,

    CONSTRAINT FK_AuditChange_Entry FOREIGN KEY (AuditId)
        REFERENCES audit.AuditEntry (AuditId) ON DELETE CASCADE,
    CONSTRAINT CK_AuditChange_Op CHECK (Operation IN (N'INSERT', N'UPDATE', N'DELETE'))
);
GO

CREATE INDEX IX_AuditChange_Entry ON audit.AuditChange (AuditId);
CREATE INDEX IX_AuditChange_Table ON audit.AuditChange (SchemaName, TableName, ChangeId DESC);
GO

CREATE VIEW audit.vw_AuditTables
AS
SELECT c.AuditId,
       c.SchemaName + N'.' + c.TableName AS FullName,
       c.Operation,
       COUNT(*)            AS Changes,
       MAX(c.RowsAffected) AS RowsAffected
FROM audit.AuditChange AS c
GROUP BY c.AuditId, c.SchemaName, c.TableName, c.Operation;
GO

PRINT N'26 · Audit detail ready.';
GO
