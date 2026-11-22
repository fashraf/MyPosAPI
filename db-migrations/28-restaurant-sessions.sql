/*==============================================================================
  Nazad POS — 28 · Restaurant business sessions, tax settings, order routing

  1. rest.RestaurantSession        the business day: open → closed, one open per
                                   branch register (filtered unique index)
  2. rest.CashMovement             cash in/out of the drawer during a session
  3. rest.SessionAuditLog          append-only trail of every sensitive action
  4. sales.Sale.SessionId          every order belongs to exactly one session;
                                   stamped by a trigger that REFUSES the insert
                                   when no session is open (error 52090)
  5. core.TaxSettings              per-business tax setup (VAT / GST / off)
  6. core.BranchSettings routing   which order types go to the kitchen

  Totals are never stored while a session is open — they are derived from
  sales, payments and credit notes. Closing freezes a JSON snapshot as the
  permanent record. Money is BIGINT minor units (halalas / paise / fils).

  Errors: 52090–52099 (order guard), 56000–56099 (session rules).
==============================================================================*/

USE POSMAIN;
GO

IF SCHEMA_ID(N'rest') IS NULL EXEC(N'CREATE SCHEMA rest');
GO

/*------------------------------------------------------------------ sessions */

IF OBJECT_ID(N'rest.RestaurantSession') IS NULL
CREATE TABLE rest.RestaurantSession
(
    SessionId           NVARCHAR(40)   NOT NULL CONSTRAINT PK_RestaurantSession PRIMARY KEY,
    SessionNumber       NVARCHAR(20)   NOT NULL CONSTRAINT UQ_RestaurantSession_Number UNIQUE,
    BranchId            NVARCHAR(40)   NOT NULL
        CONSTRAINT FK_RestaurantSession_Branch REFERENCES core.Branch (BranchId),
    RegisterId          NVARCHAR(40)   NOT NULL CONSTRAINT DF_RestaurantSession_Register DEFAULT (N'reg_main'),

    /* Set at opening, in the restaurant's time zone (Asia/Riyadh), never moved.
       An order at 01:30 belongs to the day the restaurant opened. */
    BusinessDate        DATE           NOT NULL,
    Status              NVARCHAR(10)   NOT NULL CONSTRAINT DF_RestaurantSession_Status DEFAULT (N'open'),

    OpenedAtUtc         DATETIME2(3)   NOT NULL,
    OpenedByUserId      NVARCHAR(40)   NULL,
    OpenedByName        NVARCHAR(160)  NOT NULL,
    OpeningBalanceH     BIGINT         NOT NULL,
    OpeningNotes        NVARCHAR(500)  NULL,

    ClosedAtUtc         DATETIME2(3)   NULL,
    ClosedByUserId      NVARCHAR(40)   NULL,
    ClosedByName        NVARCHAR(160)  NULL,
    ExpectedClosingH    BIGINT         NULL,
    ActualClosingH      BIGINT         NULL,
    DifferenceH         AS (ActualClosingH - ExpectedClosingH) PERSISTED,
    ClosingNotes        NVARCHAR(500)  NULL,
    DiscrepancyReason   NVARCHAR(500)  NULL,
    DiscrepancyApprovedBy NVARCHAR(160) NULL,
    PendingOverrideBy   NVARCHAR(160)  NULL,
    PendingOverrideReason NVARCHAR(300) NULL,
    PendingOverrideCount INT           NULL,
    /* The full financial picture at closing (sales, methods, cash breakdown). */
    SummaryJson         NVARCHAR(MAX)  NULL,
    ReopenCount         INT            NOT NULL CONSTRAINT DF_RestaurantSession_Reopen DEFAULT (0),

    /* Optimistic concurrency: two managers closing at once — one wins. */
    RowVer              ROWVERSION,
    CreatedAtUtc        DATETIME2(3)   NOT NULL CONSTRAINT DF_RestaurantSession_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc        DATETIME2(3)   NOT NULL CONSTRAINT DF_RestaurantSession_Updated DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT CK_RestaurantSession_Status   CHECK (Status IN (N'open', N'closed')),
    CONSTRAINT CK_RestaurantSession_Opening  CHECK (OpeningBalanceH >= 0),
    CONSTRAINT CK_RestaurantSession_Actual   CHECK (ActualClosingH IS NULL OR ActualClosingH >= 0),
    CONSTRAINT CK_RestaurantSession_Closed   CHECK (
        (Status = N'open'   AND ClosedAtUtc IS NULL) OR
        (Status = N'closed' AND ClosedAtUtc IS NOT NULL AND ActualClosingH IS NOT NULL AND ExpectedClosingH IS NOT NULL)),
    /* A difference must be explained. */
    CONSTRAINT CK_RestaurantSession_Reason   CHECK (
        Status = N'open' OR ActualClosingH = ExpectedClosingH OR LEN(ISNULL(DiscrepancyReason, N'')) > 0)
);
GO

/* The rule that matters most: one open session per branch register. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RestaurantSession_OneOpen')
CREATE UNIQUE INDEX UX_RestaurantSession_OneOpen
    ON rest.RestaurantSession (BranchId, RegisterId) WHERE Status = N'open';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RestaurantSession_History')
CREATE INDEX IX_RestaurantSession_History
    ON rest.RestaurantSession (BranchId, BusinessDate DESC) INCLUDE (Status, DifferenceH, OpenedByName, ClosedByName);
GO

IF OBJECT_ID(N'rest.CashMovement') IS NULL
CREATE TABLE rest.CashMovement
(
    MovementId      NVARCHAR(40)  NOT NULL CONSTRAINT PK_CashMovement PRIMARY KEY,
    SessionId       NVARCHAR(40)  NOT NULL
        CONSTRAINT FK_CashMovement_Session REFERENCES rest.RestaurantSession (SessionId),
    MovementType    NVARCHAR(20)  NOT NULL,
    AmountH         BIGINT        NOT NULL,
    Description     NVARCHAR(200) NOT NULL,
    CreatedByUserId NVARCHAR(40)  NULL,
    CreatedByName   NVARCHAR(160) NOT NULL,
    CreatedAtUtc    DATETIME2(3)  NOT NULL CONSTRAINT DF_CashMovement_Created DEFAULT (SYSUTCDATETIME()),
    /* CASH_SALE / CASH_REFUND are derived from sales and credit notes, never
       stored here — storing them would duplicate the sales records. */
    CONSTRAINT CK_CashMovement_Type   CHECK (MovementType IN (N'CASH_ADDITION', N'CASH_EXPENSE', N'CASH_WITHDRAWAL', N'CASH_TRANSFER')),
    CONSTRAINT CK_CashMovement_Amount CHECK (AmountH > 0)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CashMovement_Session')
CREATE INDEX IX_CashMovement_Session ON rest.CashMovement (SessionId, CreatedAtUtc);
GO

IF OBJECT_ID(N'rest.SessionAuditLog') IS NULL
CREATE TABLE rest.SessionAuditLog
(
    AuditId         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SessionAuditLog PRIMARY KEY,
    SessionId       NVARCHAR(40)  NULL,
    Action          NVARCHAR(40)  NOT NULL,
    Description     NVARCHAR(500) NOT NULL,
    PreviousValue   NVARCHAR(MAX) NULL,
    NewValue        NVARCHAR(MAX) NULL,
    UserId          NVARCHAR(40)  NULL,
    UserName        NVARCHAR(160) NOT NULL,
    AtUtc           DATETIME2(3)  NOT NULL CONSTRAINT DF_SessionAuditLog_At DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_SessionAuditLog_Action CHECK (Action IN (
        N'opened', N'cash_movement', N'closed', N'reopened', N'override_pending', N'discrepancy_approved', N'settings_changed'))
);
GO

/* Append-only: an audit trail that can be edited is not an audit trail. */
CREATE OR ALTER TRIGGER rest.trg_SessionAuditLog_NoChange
ON rest.SessionAuditLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 56090, N'The session audit log cannot be changed.', 1;
END;
GO

/*------------------------------------------------------- sales: session link */

IF COL_LENGTH(N'sales.Sale', N'SessionId') IS NULL
    ALTER TABLE sales.Sale ADD SessionId NVARCHAR(40) NULL
        CONSTRAINT FK_Sale_Session REFERENCES rest.RestaurantSession (SessionId);
IF COL_LENGTH(N'sales.Sale', N'VoidSessionId') IS NULL
    ALTER TABLE sales.Sale ADD VoidSessionId NVARCHAR(40) NULL;
IF COL_LENGTH(N'sales.Sale', N'OrderType') IS NULL
    ALTER TABLE sales.Sale ADD OrderType NVARCHAR(20) NULL
        CONSTRAINT CK_Sale_OrderType CHECK (OrderType IN (N'dine_in', N'takeaway', N'delivery', N'drive_thru', N'other'));
/* How the order was taxed: {"enabled":true,"inclusive":true,"name":"VAT","lines":[…]} */
IF COL_LENGTH(N'sales.Sale', N'TaxSnapshotJson') IS NULL
    ALTER TABLE sales.Sale ADD TaxSnapshotJson NVARCHAR(2000) NULL;
IF COL_LENGTH(N'sales.SaleReturn', N'SessionId') IS NULL
    ALTER TABLE sales.SaleReturn ADD SessionId NVARCHAR(40) NULL;
IF COL_LENGTH(N'sales.SaleReturn', N'RefundMethod') IS NULL
    ALTER TABLE sales.SaleReturn ADD RefundMethod NVARCHAR(20) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sale_Session')
CREATE INDEX IX_Sale_Session ON sales.Sale (SessionId) INCLUDE (TotalH, TaxH, DiscountH, Status) WHERE SessionId IS NOT NULL;
GO

/*
  The order guard.

  Runs inside usp_CommitSale's transaction, so a refusal rolls back the whole
  sale — lines, payments, stock and ledger. UPDLOCK + HOLDLOCK on the session
  row means a close that is committing at the same instant either finishes
  first (and this insert is refused) or waits for this sale to land (and the
  close counts it). There is no gap for an order to slip into.
*/
CREATE OR ALTER TRIGGER sales.trg_Sale_RequireSession
ON sales.Sale
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE s
       SET s.SessionId = rs.SessionId
      FROM sales.Sale AS s
      JOIN inserted   AS i ON i.SaleId = s.SaleId
     CROSS APPLY (
        SELECT TOP (1) r.SessionId
          FROM rest.RestaurantSession AS r WITH (UPDLOCK, HOLDLOCK)
         WHERE r.Status = N'open' AND r.BranchId = i.BranchId
         ORDER BY r.OpenedAtUtc DESC) AS rs
     WHERE s.SessionId IS NULL;

    IF EXISTS (SELECT 1 FROM sales.Sale AS s JOIN inserted AS i ON i.SaleId = s.SaleId WHERE s.SessionId IS NULL)
        THROW 52090, N'Restaurant is currently closed. Open a business session before creating orders.', 1;
END;
GO

/*----------------------------------------------------------- live summary */

/* What a session has taken so far, by method. Split payments count once per
   method; cancelled-within-session orders count for nothing. */
CREATE OR ALTER VIEW rest.vw_SessionMethodTotals
AS
SELECT  s.SessionId,
        p.Method,
        COUNT(DISTINCT s.SaleId) AS Orders,
        SUM(p.AmountH)           AS AmountH
FROM    sales.Sale        AS s
JOIN    sales.SalePayment AS p ON p.SaleId = s.SaleId
WHERE   s.SessionId IS NOT NULL
  AND   NOT (s.Status = N'voided' AND ISNULL(s.VoidSessionId, s.SessionId) = s.SessionId)
GROUP BY s.SessionId, p.Method;
GO

CREATE OR ALTER FUNCTION rest.fn_ExpectedCash (@SessionId NVARCHAR(40))
RETURNS TABLE
AS
RETURN
WITH sess AS (
    SELECT SessionId, OpeningBalanceH FROM rest.RestaurantSession WHERE SessionId = @SessionId
),
cash_sales AS (
    SELECT ISNULL(SUM(AmountH), 0) AS H FROM rest.vw_SessionMethodTotals WHERE SessionId = @SessionId AND Method = N'cash'
),
cash_refunds AS (
    SELECT ISNULL(SUM(r.RefundH), 0) AS H FROM sales.SaleReturn AS r WHERE r.SessionId = @SessionId AND r.RefundMethod = N'cash'
),
late_voids AS (   /* sold in an earlier session, cancelled in this one */
    SELECT ISNULL(SUM(p.AmountH), 0) AS H
      FROM sales.Sale AS s JOIN sales.SalePayment AS p ON p.SaleId = s.SaleId
     WHERE s.Status = N'voided' AND s.VoidSessionId = @SessionId AND s.SessionId <> @SessionId AND p.Method = N'cash'
),
mv AS (
    SELECT
        ISNULL(SUM(CASE WHEN MovementType = N'CASH_ADDITION'   THEN AmountH END), 0) AS AdditionsH,
        ISNULL(SUM(CASE WHEN MovementType = N'CASH_EXPENSE'    THEN AmountH END), 0) AS ExpensesH,
        ISNULL(SUM(CASE WHEN MovementType = N'CASH_WITHDRAWAL' THEN AmountH END), 0) AS WithdrawalsH,
        ISNULL(SUM(CASE WHEN MovementType = N'CASH_TRANSFER'   THEN AmountH END), 0) AS TransfersH
    FROM rest.CashMovement WHERE SessionId = @SessionId
)
SELECT  sess.OpeningBalanceH            AS OpeningH,
        cash_sales.H                    AS CashSalesH,
        mv.AdditionsH,
        cash_refunds.H + late_voids.H   AS CashRefundsH,
        mv.ExpensesH,
        mv.WithdrawalsH,
        mv.TransfersH,
        sess.OpeningBalanceH + cash_sales.H + mv.AdditionsH
          - (cash_refunds.H + late_voids.H) - mv.ExpensesH - mv.WithdrawalsH - mv.TransfersH AS ExpectedH
FROM sess CROSS JOIN cash_sales CROSS JOIN cash_refunds CROSS JOIN late_voids CROSS JOIN mv;
GO

/*------------------------------------------------------------- procedures */

CREATE OR ALTER PROCEDURE rest.usp_OpenSession
    @BranchId        NVARCHAR(40),
    @RegisterId      NVARCHAR(40) = N'reg_main',
    @OpeningBalanceH BIGINT,
    @OpenedAtUtc     DATETIME2(3) = NULL,
    @BusinessDate    DATE = NULL,
    @Notes           NVARCHAR(500) = NULL,
    @UserId          NVARCHAR(40),
    @UserName        NVARCHAR(160),
    @SessionId       NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF @OpeningBalanceH IS NULL OR @OpeningBalanceH < 0
        THROW 56000, N'Opening cash must be zero or more.', 1;

    SET @OpenedAtUtc = ISNULL(@OpenedAtUtc, SYSUTCDATETIME());
    IF @OpenedAtUtc > DATEADD(MINUTE, 5, SYSUTCDATETIME())
        THROW 56002, N'Opening time cannot be in the future.', 1;
    /* Business date in Riyadh time unless given. */
    SET @BusinessDate = ISNULL(@BusinessDate,
        CAST(CAST(@OpenedAtUtc AS DATETIMEOFFSET) AT TIME ZONE 'Arab Standard Time' AS DATE));

    BEGIN TRAN;
        IF EXISTS (SELECT 1 FROM rest.RestaurantSession WITH (UPDLOCK, HOLDLOCK)
                   WHERE BranchId = @BranchId AND RegisterId = @RegisterId AND Status = N'open')
            THROW 56001, N'This restaurant already has an open business session.', 1;

        DECLARE @PrevClosed DATETIME2(3) = (SELECT MAX(ClosedAtUtc) FROM rest.RestaurantSession
                                            WHERE BranchId = @BranchId AND RegisterId = @RegisterId);
        IF @PrevClosed IS NOT NULL AND @OpenedAtUtc < @PrevClosed
            SET @OpenedAtUtc = CASE WHEN DATEDIFF(MINUTE, @OpenedAtUtc, @PrevClosed) < 1 THEN @PrevClosed
                                    ELSE NULL END;
        IF @OpenedAtUtc IS NULL
            THROW 56003, N'Opening time must be after the previous session closed.', 1;

        DECLARE @Seq INT = (SELECT COUNT(*) + 1 FROM rest.RestaurantSession);
        SET @SessionId = CONCAT(N'rss_', REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N''));

        INSERT rest.RestaurantSession (SessionId, SessionNumber, BranchId, RegisterId, BusinessDate, Status,
                                       OpenedAtUtc, OpenedByUserId, OpenedByName, OpeningBalanceH, OpeningNotes)
        VALUES (@SessionId, CONCAT(N'S-', RIGHT(CONCAT(N'0000', @Seq), 4)), @BranchId, @RegisterId, @BusinessDate, N'open',
                @OpenedAtUtc, @UserId, @UserName, @OpeningBalanceH, NULLIF(LTRIM(RTRIM(@Notes)), N''));

        INSERT rest.SessionAuditLog (SessionId, Action, Description, NewValue, UserId, UserName)
        VALUES (@SessionId, N'opened', CONCAT(N'Restaurant opened for business date ', CONVERT(NVARCHAR(10), @BusinessDate, 23)),
                CONVERT(NVARCHAR(30), @OpeningBalanceH), @UserId, @UserName);
    COMMIT;
END;
GO

CREATE OR ALTER PROCEDURE rest.usp_AddCashMovement
    @SessionId   NVARCHAR(40),
    @Type        NVARCHAR(20),
    @AmountH     BIGINT,
    @Description NVARCHAR(200),
    @UserId      NVARCHAR(40),
    @UserName    NVARCHAR(160),
    @MovementId  NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF @AmountH IS NULL OR @AmountH <= 0 THROW 56010, N'Enter an amount greater than zero.', 1;
    IF LEN(LTRIM(RTRIM(ISNULL(@Description, N'')))) = 0 THROW 56011, N'Say what the cash was for.', 1;

    BEGIN TRAN;
        IF NOT EXISTS (SELECT 1 FROM rest.RestaurantSession WITH (UPDLOCK) WHERE SessionId = @SessionId AND Status = N'open')
            THROW 56012, N'This session is closed. Cash movements can only be recorded while it is open.', 1;
        IF @Type <> N'CASH_ADDITION' AND @AmountH > (SELECT ExpectedH FROM rest.fn_ExpectedCash(@SessionId))
            THROW 56013, N'There is not that much cash expected in the drawer.', 1;

        SET @MovementId = CONCAT(N'rcm_', REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N''));
        INSERT rest.CashMovement (MovementId, SessionId, MovementType, AmountH, Description, CreatedByUserId, CreatedByName)
        VALUES (@MovementId, @SessionId, @Type, @AmountH, LTRIM(RTRIM(@Description)), @UserId, @UserName);

        INSERT rest.SessionAuditLog (SessionId, Action, Description, NewValue, UserId, UserName)
        VALUES (@SessionId, N'cash_movement', CONCAT(@Type, N': ', @Description), CONVERT(NVARCHAR(30), @AmountH), @UserId, @UserName);
    COMMIT;
END;
GO

/*
  Close.

  @CanApprove = 1 when the caller holds restaurant.session.approve_discrepancy.
  The API decides that from the token; the procedure enforces what it means.
*/
CREATE OR ALTER PROCEDURE rest.usp_CloseSession
    @SessionId             NVARCHAR(40),
    @ActualClosingH        BIGINT,
    @ClosingNotes          NVARCHAR(500) = NULL,
    @DiscrepancyReason     NVARCHAR(500) = NULL,
    @PendingOverrideReason NVARCHAR(300) = NULL,
    @CanApprove            BIT,
    @ThresholdH            BIGINT = 10000,
    @SummaryJson           NVARCHAR(MAX) = NULL,
    @UserId                NVARCHAR(40),
    @UserName              NVARCHAR(160)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF @ActualClosingH IS NULL OR @ActualClosingH < 0 THROW 56020, N'Enter the cash you counted (zero or more).', 1;

    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRAN;
        DECLARE @Status NVARCHAR(10), @BranchId NVARCHAR(40), @OpenedAt DATETIME2(3), @ClosedBy NVARCHAR(160);
        SELECT @Status = Status, @BranchId = BranchId, @OpenedAt = OpenedAtUtc, @ClosedBy = ClosedByName
          FROM rest.RestaurantSession WITH (UPDLOCK, HOLDLOCK) WHERE SessionId = @SessionId;

        IF @Status IS NULL      THROW 56021, N'That session does not exist.', 1;
        IF @Status <> N'open'   THROW 56022, N'This session was already closed.', 1;

        DECLARE @Pending INT = (
            SELECT COUNT(*) FROM sales.PreparationOrder AS po
             WHERE po.Status IN (N'open', N'preparing', N'ready')
               AND (po.SaleId IN (SELECT SaleId FROM sales.Sale WHERE SessionId = @SessionId)
                    OR (po.BranchId = @BranchId AND po.CreatedAtUtc >= @OpenedAt)));
        /* Open kitchen orders do not block closing; a reason is required instead. */
        IF @Pending > 0 AND LEN(LTRIM(RTRIM(ISNULL(@PendingOverrideReason, N'')))) = 0
            THROW 56024, N'Give a reason for closing with orders still open.', 1;

        DECLARE @Expected BIGINT = (SELECT ExpectedH FROM rest.fn_ExpectedCash(@SessionId));
        DECLARE @Diff BIGINT = @ActualClosingH - @Expected;
        IF @Diff <> 0 AND LEN(LTRIM(RTRIM(ISNULL(@DiscrepancyReason, N'')))) = 0
            THROW 56025, N'Explain the cash difference before closing.', 1;
        IF ABS(@Diff) > @ThresholdH AND @CanApprove = 0
            THROW 56026, N'The difference is above the limit you can sign off. A manager must close this session.', 1;

        UPDATE rest.RestaurantSession
           SET Status = N'closed', ClosedAtUtc = SYSUTCDATETIME(), ClosedByUserId = @UserId, ClosedByName = @UserName,
               ExpectedClosingH = @Expected, ActualClosingH = @ActualClosingH,
               ClosingNotes = NULLIF(LTRIM(RTRIM(@ClosingNotes)), N''),
               DiscrepancyReason = CASE WHEN @Diff <> 0 THEN LTRIM(RTRIM(@DiscrepancyReason)) END,
               DiscrepancyApprovedBy = CASE WHEN ABS(@Diff) > @ThresholdH THEN @UserName END,
               PendingOverrideBy = CASE WHEN @Pending > 0 THEN @UserName END,
               PendingOverrideReason = CASE WHEN @Pending > 0 THEN LTRIM(RTRIM(@PendingOverrideReason)) END,
               PendingOverrideCount = NULLIF(@Pending, 0),
               SummaryJson = @SummaryJson, UpdatedAtUtc = SYSUTCDATETIME()
         WHERE SessionId = @SessionId AND Status = N'open';

        IF @Pending > 0
            INSERT rest.SessionAuditLog (SessionId, Action, Description, UserId, UserName)
            VALUES (@SessionId, N'override_pending', CONCAT(N'Closed with ', @Pending, N' open kitchen order(s): ', @PendingOverrideReason), @UserId, @UserName);
        IF ABS(@Diff) > @ThresholdH
            INSERT rest.SessionAuditLog (SessionId, Action, Description, NewValue, UserId, UserName)
            VALUES (@SessionId, N'discrepancy_approved', CONCAT(N'Difference above limit approved: ', @DiscrepancyReason), CONVERT(NVARCHAR(30), @Diff), @UserId, @UserName);
        INSERT rest.SessionAuditLog (SessionId, Action, Description, PreviousValue, NewValue, UserId, UserName)
        VALUES (@SessionId, N'closed', CONCAT(N'Closed. Expected ', @Expected, N', counted ', @ActualClosingH, N', difference ', @Diff, N'.'),
                CONVERT(NVARCHAR(30), @Expected), CONVERT(NVARCHAR(30), @ActualClosingH), @UserId, @UserName);
    COMMIT;

    SELECT * FROM rest.RestaurantSession WHERE SessionId = @SessionId;
END;
GO

CREATE OR ALTER PROCEDURE rest.usp_ReopenSession
    @SessionId NVARCHAR(40),
    @Reason    NVARCHAR(500),
    @UserId    NVARCHAR(40),
    @UserName  NVARCHAR(160)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF LEN(LTRIM(RTRIM(ISNULL(@Reason, N'')))) = 0 THROW 56030, N'Say why the session is being reopened.', 1;

    BEGIN TRAN;
        DECLARE @BranchId NVARCHAR(40), @RegisterId NVARCHAR(40), @Status NVARCHAR(10), @Prev NVARCHAR(MAX);
        SELECT @BranchId = BranchId, @RegisterId = RegisterId, @Status = Status,
               @Prev = (SELECT ClosedAtUtc, ClosedByName, ExpectedClosingH, ActualClosingH, DifferenceH, DiscrepancyReason
                          FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
          FROM rest.RestaurantSession WITH (UPDLOCK, HOLDLOCK) WHERE SessionId = @SessionId;

        IF @Status IS NULL     THROW 56021, N'That session does not exist.', 1;
        IF @Status <> N'closed' THROW 56031, N'Only a closed session can be reopened.', 1;
        IF EXISTS (SELECT 1 FROM rest.RestaurantSession WHERE BranchId = @BranchId AND RegisterId = @RegisterId
                   AND OpenedAtUtc > (SELECT OpenedAtUtc FROM rest.RestaurantSession WHERE SessionId = @SessionId))
            THROW 56032, N'Only the most recent session at a branch can be reopened.', 1;

        UPDATE rest.RestaurantSession
           SET Status = N'open', ClosedAtUtc = NULL, ClosedByUserId = NULL, ClosedByName = NULL,
               ExpectedClosingH = NULL, ActualClosingH = NULL, DiscrepancyReason = NULL, DiscrepancyApprovedBy = NULL,
               PendingOverrideBy = NULL, PendingOverrideReason = NULL, PendingOverrideCount = NULL,
               SummaryJson = NULL, ReopenCount = ReopenCount + 1, UpdatedAtUtc = SYSUTCDATETIME()
         WHERE SessionId = @SessionId;   -- UX_RestaurantSession_OneOpen refuses this if another is open

        INSERT rest.SessionAuditLog (SessionId, Action, Description, PreviousValue, UserId, UserName)
        VALUES (@SessionId, N'reopened', CONCAT(N'Reopened: ', @Reason), @Prev, @UserId, @UserName);
    COMMIT;
END;
GO

/*----------------------------------------------------------------- settings */

IF OBJECT_ID(N'rest.SessionSettings') IS NULL
CREATE TABLE rest.SessionSettings
(
    SettingsId            INT           NOT NULL CONSTRAINT PK_SessionSettings PRIMARY KEY CONSTRAINT CK_SessionSettings_One CHECK (SettingsId = 1),
    LongSessionHours      INT           NOT NULL CONSTRAINT DF_SessionSettings_Long DEFAULT (18),
    DiscrepancyThresholdH BIGINT        NOT NULL CONSTRAINT DF_SessionSettings_Threshold DEFAULT (10000),
    UpdatedAtUtc          DATETIME2(3)  NOT NULL CONSTRAINT DF_SessionSettings_Updated DEFAULT (SYSUTCDATETIME()),
    UpdatedBy             NVARCHAR(160) NULL
);
IF NOT EXISTS (SELECT 1 FROM rest.SessionSettings) INSERT rest.SessionSettings (SettingsId) VALUES (1);
GO

/* Tax: one row per business. The full setup is JSON (rates and components vary
   by country); the columns pulled out are the ones queries filter on. */
IF OBJECT_ID(N'core.TaxSettings') IS NULL
CREATE TABLE core.TaxSettings
(
    SettingsId        INT            NOT NULL CONSTRAINT PK_TaxSettings PRIMARY KEY CONSTRAINT CK_TaxSettings_One CHECK (SettingsId = 1),
    Enabled           BIT            NOT NULL CONSTRAINT DF_TaxSettings_Enabled DEFAULT (1),
    Country           NVARCHAR(10)   NOT NULL CONSTRAINT DF_TaxSettings_Country DEFAULT (N'SA'),
    Regime            NVARCHAR(20)   NOT NULL CONSTRAINT DF_TaxSettings_Regime DEFAULT (N'vat'),
    PricesIncludeTax  BIT            NOT NULL CONSTRAINT DF_TaxSettings_Inclusive DEFAULT (1),
    RegistrationNumber NVARCHAR(40)  NULL,
    SettingsJson      NVARCHAR(MAX)  NOT NULL,
    UpdatedAtUtc      DATETIME2(3)   NOT NULL CONSTRAINT DF_TaxSettings_Updated DEFAULT (SYSUTCDATETIME()),
    UpdatedBy         NVARCHAR(160)  NULL,
    CONSTRAINT CK_TaxSettings_Json   CHECK (ISJSON(SettingsJson) = 1),
    CONSTRAINT CK_TaxSettings_Regime CHECK (Regime IN (N'vat', N'gst', N'sales_tax'))
);
IF NOT EXISTS (SELECT 1 FROM core.TaxSettings)
    INSERT core.TaxSettings (SettingsId, SettingsJson)
    VALUES (1, N'{"enabled":true,"country":"SA","regime":"vat","taxName":"VAT","registrationLabel":"VAT number","registrationNumber":"","pricesIncludeTax":true,"rates":[{"id":"std","name":"Standard 15%","kind":"standard","percent":15,"components":[]},{"id":"zero","name":"Zero-rated 0%","kind":"zero","percent":0,"components":[]},{"id":"exempt","name":"Exempt","kind":"exempt","percent":0,"components":[]}],"defaultRateId":"std","gstSupply":"intra","gstState":"","showBreakdown":false,"zatca":true}');
GO

IF COL_LENGTH(N'core.CatalogItem', N'TaxRateId') IS NULL
    ALTER TABLE core.CatalogItem ADD TaxRateId NVARCHAR(40) NULL;
GO

/* Order types and kitchen routing, per branch. */
IF OBJECT_ID(N'core.BranchSettings') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'core.BranchSettings', N'EnabledOrderTypes') IS NULL
        ALTER TABLE core.BranchSettings ADD EnabledOrderTypes NVARCHAR(200) NOT NULL
            CONSTRAINT DF_BranchSettings_OrderTypes DEFAULT (N'dine_in,takeaway,delivery');
    IF COL_LENGTH(N'core.BranchSettings', N'DefaultOrderType') IS NULL
        ALTER TABLE core.BranchSettings ADD DefaultOrderType NVARCHAR(20) NOT NULL
            CONSTRAINT DF_BranchSettings_DefaultType DEFAULT (N'takeaway');
    /* {"dine_in":"kitchen","takeaway":"kitchen","delivery":"none",…} — kitchen | collection | none */
    IF COL_LENGTH(N'core.BranchSettings', N'KitchenRoutingJson') IS NULL
        ALTER TABLE core.BranchSettings ADD KitchenRoutingJson NVARCHAR(400) NULL;
END;
GO

PRINT N'28-restaurant-sessions applied.';
GO
