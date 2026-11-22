/*==============================================================================
  Nazad POS — 25 · Access assignment, commission rules and bill templates

  The last tables without a way in. Each already exists; none had an endpoint,
  so branch access, permission overrides, commission rules and receipt layouts
  could only be changed by hand in SSMS.
==============================================================================*/

USE POSMAIN;
GO

/*=========================================================== branch access */

/*
  Which branches a person may work in.

  Replaced wholesale rather than added to. A grant-and-revoke pair drifts: the
  screen shows a set, and applying a diff means two round trips that can half
  succeed, leaving access nobody chose.
*/
CREATE OR ALTER PROCEDURE core.usp_SetUserBranches
    @UserId    NVARCHAR(40),
    @BranchIds NVARCHAR(MAX),        -- comma separated
    @Blocked   NVARCHAR(400) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @Blocked = NULL;

    IF NOT EXISTS (SELECT 1 FROM core.AppUser WHERE UserId = @UserId)
        THROW 60000, N'No such user.', 1;

    DECLARE @Wanted TABLE (BranchId NVARCHAR(40) PRIMARY KEY);

    INSERT INTO @Wanted (BranchId)
    SELECT DISTINCT LTRIM(RTRIM(value))
    FROM STRING_SPLIT(ISNULL(@BranchIds, N''), N',')
    WHERE LTRIM(RTRIM(value)) <> N'';

    /* A branch that does not exist would be accepted silently and then never
       appear anywhere — access that looks granted and is not. */
    IF EXISTS (SELECT 1 FROM @Wanted AS w
               WHERE NOT EXISTS (SELECT 1 FROM core.Branch AS b WHERE b.BranchId = w.BranchId))
    BEGIN
        SET @Blocked = N'One of those branches does not exist.';
        RETURN;
    END

    /* Nobody works nowhere. An empty set would leave the person able to sign in
       and do nothing, which reads as a broken account rather than a decision. */
    IF NOT EXISTS (SELECT 1 FROM @Wanted)
    BEGIN
        SET @Blocked = N'A user needs at least one branch.';
        RETURN;
    END

    BEGIN TRAN;

        DELETE FROM core.UserBranch WHERE UserId = @UserId;

        INSERT INTO core.UserBranch (UserId, BranchId)
        SELECT @UserId, BranchId FROM @Wanted;

    COMMIT;
END
GO

/*====================================================== permission overrides */

/*
  Grants and denials on top of a role.

  Also replaced wholesale, for the same reason. The effective permission view
  already resolves the precedence — denial beats grant — so this only records
  what was chosen.
*/
CREATE OR ALTER PROCEDURE core.usp_SetUserOverrides
    @UserId  NVARCHAR(40),
    @Grants  NVARCHAR(MAX) = NULL,   -- comma separated permission keys
    @Denies  NVARCHAR(MAX) = NULL,
    @Blocked NVARCHAR(400) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @Blocked = NULL;

    IF NOT EXISTS (SELECT 1 FROM core.AppUser WHERE UserId = @UserId)
        THROW 60010, N'No such user.', 1;

    DECLARE @Wanted TABLE (PermissionKey NVARCHAR(60), Effect NVARCHAR(10));

    INSERT INTO @Wanted (PermissionKey, Effect)
    SELECT DISTINCT LTRIM(RTRIM(value)), N'grant'
    FROM STRING_SPLIT(ISNULL(@Grants, N''), N',')
    WHERE LTRIM(RTRIM(value)) <> N'';

    INSERT INTO @Wanted (PermissionKey, Effect)
    SELECT DISTINCT LTRIM(RTRIM(value)), N'deny'
    FROM STRING_SPLIT(ISNULL(@Denies, N''), N',')
    WHERE LTRIM(RTRIM(value)) <> N'';

    IF EXISTS (SELECT 1 FROM @Wanted AS w
               WHERE NOT EXISTS (SELECT 1 FROM core.Permission AS p
                                 WHERE p.PermissionKey = w.PermissionKey))
    BEGIN
        SET @Blocked = N'One of those permissions does not exist.';
        RETURN;
    END

    /* Granting and denying the same key is not a preference, it is a mistake.
       The view would resolve it to denied and the screen would show it granted. */
    IF EXISTS (SELECT PermissionKey FROM @Wanted
               GROUP BY PermissionKey HAVING COUNT(*) > 1)
    BEGIN
        SET @Blocked = N'A permission cannot be granted and denied at once.';
        RETURN;
    END

    /*
      An owner cannot be denied the ability to manage users.

      Otherwise the last person able to grant permissions can remove their own
      ability to do so, and nobody can undo it without a database session.
    */
    IF EXISTS (SELECT 1 FROM @Wanted AS w
               JOIN core.AppUser AS u ON u.UserId = @UserId
               WHERE w.Effect = N'deny'
                 AND w.PermissionKey IN (N'users.edit', N'users.create')
                 AND u.RoleId = N'rol_owner')
    BEGIN
        SET @Blocked = N'An owner cannot be denied user management.';
        RETURN;
    END

    BEGIN TRAN;

        DELETE FROM core.UserPermissionOverride WHERE UserId = @UserId;

        INSERT INTO core.UserPermissionOverride (UserId, PermissionKey, Effect)
        SELECT @UserId, PermissionKey, Effect FROM @Wanted;

    COMMIT;
END
GO

/*======================================================== commission rules */

CREATE OR ALTER PROCEDURE fin.usp_SaveCommissionRule
    @RuleId   NVARCHAR(40) = NULL,
    @Basis    NVARCHAR(20),
    @Value    DECIMAL(18,3),
    @IsActive BIT = 1,
    @SavedId  NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @Basis NOT IN (N'percent_of_sale', N'percent_of_margin', N'fixed_per_sale')
        THROW 60020, N'That commission basis is not recognised.', 1;

    IF @Value <= 0
        THROW 60021, N'A commission value must be above zero.', 1;

    /* A percentage above 100 pays out more than the sale brought in. */
    IF @Basis LIKE N'percent%' AND @Value > 100
        THROW 60022, N'A commission percentage cannot exceed 100.', 1;

    IF @RuleId IS NULL
    BEGIN
        SET @SavedId = CONCAT(N'crl_',
            LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));

        INSERT INTO fin.CommissionRule (RuleId, Basis, Value, IsActive)
        VALUES (@SavedId, @Basis, @Value, @IsActive);
    END
    ELSE
    BEGIN
        SET @SavedId = @RuleId;

        /*
          Editing a rule does not change commission already earned.

          fin.CommissionEntry records the amount at the moment of each sale, so
          past entries keep the rate that applied then. Recalculating them would
          rewrite what someone was told they had earned.
        */
        UPDATE fin.CommissionRule
        SET Basis = @Basis, Value = @Value, IsActive = @IsActive,
            UpdatedAtUtc = SYSUTCDATETIME()
        WHERE RuleId = @RuleId;

        IF @@ROWCOUNT = 0 THROW 60023, N'No such commission rule.', 1;
    END
END
GO

/*========================================================== bill templates */

CREATE OR ALTER PROCEDURE core.usp_SaveBillTemplate
    @BillId        NVARCHAR(40) = NULL,
    @Name          NVARCHAR(120),
    @Audience      NVARCHAR(20),
    @PrintGroupId  NVARCHAR(40) = NULL,
    @IsDefault     BIT = 0,
    @PaperSize     NVARCHAR(10) = N'80mm',
    @BillLanguage  NVARCHAR(10) = N'both',
    @ShowLogo      BIT = 1,
    @ShowVatNumber BIT = 1,
    @ShowCrNumber  BIT = 0,
    @ShowUnitPrice BIT = 1,
    @ShowLineTotal BIT = 1,
    @ShowSubtotal  BIT = 1,
    @ShowDiscount  BIT = 1,
    @ShowVat       BIT = 1,
    @ShowGrandTotal BIT = 1,
    @ShowZatcaQr   BIT = 1,
    @FooterAr      NVARCHAR(400) = NULL,
    @FooterEn      NVARCHAR(400) = NULL,
    @IsActive      BIT = 1,
    @SavedId       NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF LTRIM(RTRIM(ISNULL(@Name, N''))) = N''
        THROW 60030, N'A template needs a name.', 1;

    IF @PaperSize NOT IN (N'58mm', N'80mm', N'A4')
        THROW 60031, N'Paper size must be 58mm, 80mm or A4.', 1;

    /*
      A customer receipt must show the total.

      Everything else is a layout preference; a receipt without a total is not a
      receipt, and a customer cannot check what they were charged.
    */
    IF @Audience = N'customer' AND @ShowGrandTotal = 0
        THROW 60032, N'A customer receipt must show the total.', 1;

    BEGIN TRAN;

        IF @BillId IS NULL
        BEGIN
            SET @SavedId = CONCAT(N'bil_',
                LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));

            INSERT INTO core.BillTemplate
                (BillId, Name, Audience, PrintGroupId, IsDefault, PaperSize, BillLanguage,
                 ShowLogo, ShowVatNumber, ShowCrNumber, ShowUnitPrice, ShowLineTotal,
                 ShowSubtotal, ShowDiscount, ShowVat, ShowGrandTotal, ShowZatcaQr,
                 FooterAr, FooterEn, IsActive)
            VALUES
                (@SavedId, @Name, @Audience, @PrintGroupId, @IsDefault, @PaperSize, @BillLanguage,
                 @ShowLogo, @ShowVatNumber, @ShowCrNumber, @ShowUnitPrice, @ShowLineTotal,
                 @ShowSubtotal, @ShowDiscount, @ShowVat, @ShowGrandTotal, @ShowZatcaQr,
                 @FooterAr, @FooterEn, @IsActive);
        END
        ELSE
        BEGIN
            SET @SavedId = @BillId;

            UPDATE core.BillTemplate
            SET Name = @Name, Audience = @Audience, PrintGroupId = @PrintGroupId,
                IsDefault = @IsDefault, PaperSize = @PaperSize, BillLanguage = @BillLanguage,
                ShowLogo = @ShowLogo, ShowVatNumber = @ShowVatNumber,
                ShowCrNumber = @ShowCrNumber, ShowUnitPrice = @ShowUnitPrice,
                ShowLineTotal = @ShowLineTotal, ShowSubtotal = @ShowSubtotal,
                ShowDiscount = @ShowDiscount, ShowVat = @ShowVat,
                ShowGrandTotal = @ShowGrandTotal, ShowZatcaQr = @ShowZatcaQr,
                FooterAr = @FooterAr, FooterEn = @FooterEn, IsActive = @IsActive,
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE BillId = @BillId;

            IF @@ROWCOUNT = 0 THROW 60033, N'No such template.', 1;
        END

        /* One default per audience. Two would make which one prints a matter of
           row order, and a receipt layout that changes at random is worse than
           the wrong layout consistently. */
        IF @IsDefault = 1
        BEGIN
            UPDATE core.BillTemplate
            SET IsDefault = 0, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE Audience = @Audience AND BillId <> @SavedId AND IsDefault = 1;
        END

    COMMIT;
END
GO

PRINT N'25 · Access, commission and templates ready.';
GO

/*------------------------------------------------------------- permissions */

INSERT INTO core.Permission (PermissionKey, Module, Action, Sensitivity)
SELECT v.k, v.m, v.a, N'general'
FROM (VALUES
    (N'users.assign_branches', N'users',   N'assign_branches'),
    (N'users.override',        N'users',   N'override'),
    (N'finance.commission_rules', N'finance', N'commission_rules'),
    (N'bill_builder.edit',     N'bill_builder', N'edit')
) AS v (k, m, a)
WHERE NOT EXISTS (SELECT 1 FROM core.Permission WHERE PermissionKey = v.k);
GO

INSERT INTO core.RolePermission (RoleId, PermissionKey)
SELECT N'rol_owner', p.PermissionKey
FROM core.Permission AS p
WHERE p.PermissionKey IN (N'users.assign_branches', N'users.override',
                          N'finance.commission_rules', N'bill_builder.edit')
  AND NOT EXISTS (SELECT 1 FROM core.RolePermission AS rp
                  WHERE rp.RoleId = N'rol_owner' AND rp.PermissionKey = p.PermissionKey);

/* A manager may lay out receipts. Access and commission stay with the owner —
   both decide what people are paid or may see. */
INSERT INTO core.RolePermission (RoleId, PermissionKey)
SELECT N'rol_manager', N'bill_builder.edit'
WHERE NOT EXISTS (SELECT 1 FROM core.RolePermission
                  WHERE RoleId = N'rol_manager' AND PermissionKey = N'bill_builder.edit');
GO

SELECT CASE WHEN (SELECT COUNT(*) FROM core.Permission
                  WHERE PermissionKey IN (N'users.assign_branches', N'users.override',
                        N'finance.commission_rules', N'bill_builder.edit')) = 4
            THEN N'PASS  Access and templates ready'
            ELSE N'FAIL  Permissions incomplete'
       END AS Result;
GO
