/*==============================================================================
  Nazad POS — 27 · Creating and editing branches

  Sixteen tables reference core.Branch. Most fill in as the branch is used —
  the first sale, the first delivery. Three things cannot wait:

    core.Branch          the branch itself
    core.BranchSettings  its VAT rate and stock rules — a sale reads these
    core.UserBranch      someone allowed to work there

  Without the settings, the till falls back to defaults it should not be
  guessing. Without the access, the branch exists and nobody can select it —
  which looks like a failed save rather than a missing grant.

  All three in one transaction, or none.
==============================================================================*/

USE POSMAIN;
GO

CREATE OR ALTER PROCEDURE core.usp_SaveBranch
    @BranchId          NVARCHAR(40) = NULL,   -- null creates
    @Code              NVARCHAR(20),
    @NameAr            NVARCHAR(200),
    @NameEn            NVARCHAR(200),
    @Phone             NVARCHAR(40)  = NULL,
    @AddressAr         NVARCHAR(400) = NULL,
    @AddressEn         NVARCHAR(400) = NULL,
    @IsActive          BIT = 1,

    /* Settings. Defaults are Saudi retail's, not guesses. */
    @VatRatePercent    DECIMAL(5,2) = 15.00,
    @PricesIncludeVat  BIT = 1,
    @AllowNegativeStock BIT = 0,

    @Actor             NVARCHAR(160),
    @ActorUserId       NVARCHAR(40),
    @SavedId           NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF LTRIM(RTRIM(ISNULL(@NameAr, N''))) = N'' OR LTRIM(RTRIM(ISNULL(@NameEn, N''))) = N''
        THROW 61000, N'Both names are required.', 1;

    IF LTRIM(RTRIM(ISNULL(@Code, N''))) = N''
        THROW 61001, N'A branch code is required.', 1;

    /* The code prints on invoices and prefixes device ids. Two branches sharing
       one would make every document ambiguous about where it came from. */
    IF EXISTS (SELECT 1 FROM core.Branch
               WHERE Code = @Code AND (@BranchId IS NULL OR BranchId <> @BranchId))
        THROW 61002, N'That branch code is already used.', 1;

    IF @VatRatePercent < 0 OR @VatRatePercent > 100
        THROW 61003, N'The VAT rate must be between 0 and 100.', 1;

    /* The last active branch cannot be switched off from here either — the
       till would have nowhere to sell. Same rule as usp_SetActive. */
    IF @BranchId IS NOT NULL AND @IsActive = 0
       AND EXISTS (SELECT 1 FROM core.Branch WHERE BranchId = @BranchId AND IsActive = 1)
       AND (SELECT COUNT(*) FROM core.Branch WHERE IsActive = 1) <= 1
        THROW 61004, N'This is the only active branch. At least one must remain.', 1;

    BEGIN TRAN;

        IF @BranchId IS NULL
        BEGIN
            SET @SavedId = CONCAT(N'brn_',
                LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));

            INSERT INTO core.Branch
                (BranchId, Code, NameAr, NameEn, Phone, AddressAr, AddressEn, IsActive)
            VALUES
                (@SavedId, @Code, @NameAr, @NameEn, @Phone, @AddressAr, @AddressEn, @IsActive);

            /* Settings, in the same transaction. A branch without them is a
               branch whose VAT rate the till has to guess. */
            INSERT INTO core.BranchSettings
                (BranchId, VatRatePercent, PricesIncludeVat, AllowNegativeStock, UpdatedBy)
            VALUES
                (@SavedId, @VatRatePercent, @PricesIncludeVat, @AllowNegativeStock, @Actor);

            /*
              Access for everyone who could already see every branch.

              An owner who creates a branch and cannot then select it has been
              handed a broken result. Granting it to every active owner — not
              only the creator — means a second owner does not have to go and
              grant it to themselves.
            */
            INSERT INTO core.UserBranch (UserId, BranchId)
            SELECT u.UserId, @SavedId
            FROM core.AppUser AS u
            WHERE u.RoleId = N'rol_owner' AND u.Status = N'active'
              AND NOT EXISTS (SELECT 1 FROM core.UserBranch AS ub
                              WHERE ub.UserId = u.UserId AND ub.BranchId = @SavedId);

            /* And the person creating it, whatever their role. */
            IF NOT EXISTS (SELECT 1 FROM core.UserBranch
                           WHERE UserId = @ActorUserId AND BranchId = @SavedId)
               AND EXISTS (SELECT 1 FROM core.AppUser WHERE UserId = @ActorUserId)
            BEGIN
                INSERT INTO core.UserBranch (UserId, BranchId)
                VALUES (@ActorUserId, @SavedId);
            END
        END
        ELSE
        BEGIN
            SET @SavedId = @BranchId;

            UPDATE core.Branch
            SET Code = @Code, NameAr = @NameAr, NameEn = @NameEn, Phone = @Phone,
                AddressAr = @AddressAr, AddressEn = @AddressEn, IsActive = @IsActive,
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE BranchId = @BranchId;

            IF @@ROWCOUNT = 0 THROW 61005, N'No such branch.', 1;

            /* Settings may be missing for a branch created before this
               procedure existed. Create them rather than fail the edit. */
            IF EXISTS (SELECT 1 FROM core.BranchSettings WHERE BranchId = @BranchId)
            BEGIN
                UPDATE core.BranchSettings
                SET VatRatePercent = @VatRatePercent,
                    PricesIncludeVat = @PricesIncludeVat,
                    AllowNegativeStock = @AllowNegativeStock,
                    UpdatedAtUtc = SYSUTCDATETIME(),
                    UpdatedBy = @Actor
                WHERE BranchId = @BranchId;
            END
            ELSE
            BEGIN
                INSERT INTO core.BranchSettings
                    (BranchId, VatRatePercent, PricesIncludeVat, AllowNegativeStock, UpdatedBy)
                VALUES
                    (@BranchId, @VatRatePercent, @PricesIncludeVat, @AllowNegativeStock, @Actor);
            END
        END

    COMMIT;
END
GO

PRINT N'27 · Branch maintenance ready.';
GO

/*----------------------------------------------------------------- repair */

/*
  Any existing branch missing its settings gets them now.

  A branch created by hand or by an older seed may have none, and the till has
  been falling back to defaults for it. Filling them in is the same fix the
  procedure applies on edit, done once for everything already there.
*/
INSERT INTO core.BranchSettings (BranchId, VatRatePercent, PricesIncludeVat,
                                 AllowNegativeStock, UpdatedBy)
SELECT b.BranchId, 15.00, 1, 0, N'migration-27'
FROM core.Branch AS b
WHERE NOT EXISTS (SELECT 1 FROM core.BranchSettings AS s WHERE s.BranchId = b.BranchId);
GO

/*------------------------------------------------------------- permission */

INSERT INTO core.Permission (PermissionKey, Module, Action, Sensitivity)
SELECT v.k, N'branches', v.a, N'general'
FROM (VALUES (N'branches.create', N'create'), (N'branches.edit', N'edit')) AS v (k, a)
WHERE NOT EXISTS (SELECT 1 FROM core.Permission WHERE PermissionKey = v.k);
GO

/* The owner only. A branch is a new place money moves through. */
INSERT INTO core.RolePermission (RoleId, PermissionKey)
SELECT N'rol_owner', p.PermissionKey
FROM core.Permission AS p
WHERE p.PermissionKey IN (N'branches.create', N'branches.edit')
  AND NOT EXISTS (SELECT 1 FROM core.RolePermission AS rp
                  WHERE rp.RoleId = N'rol_owner' AND rp.PermissionKey = p.PermissionKey);
GO

SELECT
    CASE WHEN NOT EXISTS (SELECT 1 FROM core.Branch AS b
                          WHERE NOT EXISTS (SELECT 1 FROM core.BranchSettings AS s
                                            WHERE s.BranchId = b.BranchId))
         THEN N'PASS  Every branch has settings'
         ELSE N'FAIL  A branch is still missing settings'
    END AS Result;
GO
