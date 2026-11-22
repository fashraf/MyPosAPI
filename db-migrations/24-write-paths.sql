/*==============================================================================
  Nazad POS — 24 · The missing write paths

  Four endpoint groups were read-only, so four screens saved into memory and
  lost the change on reload. These are the procedures behind them.

  Every column below was read from this file's own CREATE TABLE statements
  rather than assumed — the last two migrations were corrected for exactly that
  mistake.
==============================================================================*/

USE POSMAIN;
GO

/*================================================================== catalog */

CREATE OR ALTER PROCEDURE core.usp_SaveCatalogItem
    @ItemId              NVARCHAR(40)  = NULL,   -- null creates
    @Kind                NVARCHAR(20),
    @NameAr              NVARCHAR(200),
    @NameEn              NVARCHAR(200),
    @DescriptionAr       NVARCHAR(600) = NULL,
    @DescriptionEn       NVARCHAR(600) = NULL,
    @Sku                 NVARCHAR(60)  = NULL,
    @Barcode             NVARCHAR(60)  = NULL,
    @CategoryId          NVARCHAR(40)  = NULL,
    @VendorId            NVARCHAR(40)  = NULL,
    @PrintGroupId        NVARCHAR(40)  = NULL,
    @PriceH              BIGINT,
    @CostH               BIGINT = 0,
    @TracksStock         BIT = 1,
    @ReorderLevel        DECIMAL(18,3) = 0,
    @RequiresPreparation BIT = 0,
    @IsActive            BIT = 1,
    @Actor               NVARCHAR(160),
    @SavedItemId         NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF LTRIM(RTRIM(ISNULL(@NameAr, N''))) = N'' OR LTRIM(RTRIM(ISNULL(@NameEn, N''))) = N''
        THROW 59000, N'Both names are required.', 1;

    IF @PriceH < 0 THROW 59001, N'A price cannot be negative.', 1;
    IF @CostH  < 0 THROW 59002, N'A cost cannot be negative.', 1;

    /* A service has nothing to count. Letting it track stock would produce
       movements against a thing that does not exist. */
    IF @Kind = N'service' SET @TracksStock = 0;

    /* Barcodes are scanned. Two items sharing one means the till picks the
       wrong product, silently. */
    IF @Barcode IS NOT NULL AND EXISTS (
        SELECT 1 FROM core.CatalogItem
        WHERE Barcode = @Barcode AND (@ItemId IS NULL OR ItemId <> @ItemId))
        THROW 59003, N'That barcode is already used by another item.', 1;

    IF @Sku IS NOT NULL AND EXISTS (
        SELECT 1 FROM core.CatalogItem
        WHERE Sku = @Sku AND (@ItemId IS NULL OR ItemId <> @ItemId))
        THROW 59004, N'That SKU is already used by another item.', 1;

    IF @ItemId IS NULL
    BEGIN
        SET @SavedItemId = CONCAT(N'itm_',
            LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));

        INSERT INTO core.CatalogItem
            (ItemId, Kind, NameAr, NameEn, DescriptionAr, DescriptionEn, Sku, Barcode,
             CategoryId, VendorId, PrintGroupId, PriceH, CostH, TracksStock,
             ReorderLevel, RequiresPreparation, IsActive, CreatedBy)
        VALUES
            (@SavedItemId, @Kind, @NameAr, @NameEn, @DescriptionAr, @DescriptionEn,
             @Sku, @Barcode, @CategoryId, @VendorId, @PrintGroupId, @PriceH, @CostH,
             @TracksStock, @ReorderLevel, @RequiresPreparation, @IsActive, @Actor);
    END
    ELSE
    BEGIN
        SET @SavedItemId = @ItemId;

        /*
          Cost is not editable here.

          It is a weighted average maintained by receiving, and overwriting it
          from an edit form would make every past margin wrong. Correcting a
          cost is an inventory adjustment, not a product edit.
        */
        UPDATE core.CatalogItem
        SET Kind                = @Kind,
            NameAr              = @NameAr,
            NameEn              = @NameEn,
            DescriptionAr       = @DescriptionAr,
            DescriptionEn       = @DescriptionEn,
            Sku                 = @Sku,
            Barcode             = @Barcode,
            CategoryId          = @CategoryId,
            VendorId            = @VendorId,
            PrintGroupId        = @PrintGroupId,
            PriceH              = @PriceH,
            TracksStock         = @TracksStock,
            ReorderLevel        = @ReorderLevel,
            RequiresPreparation = @RequiresPreparation,
            IsActive            = @IsActive,
            UpdatedAtUtc        = SYSUTCDATETIME(),
            UpdatedBy           = @Actor
        WHERE ItemId = @ItemId;

        IF @@ROWCOUNT = 0 THROW 59005, N'No such item.', 1;
    END
END
GO

CREATE OR ALTER PROCEDURE core.usp_SaveCategory
    @CategoryId  NVARCHAR(40) = NULL,
    @NameAr      NVARCHAR(200),
    @NameEn      NVARCHAR(200),
    @SortOrder   INT = 0,
    @IsActive    BIT = 1,
    @SavedId     NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF LTRIM(RTRIM(ISNULL(@NameAr, N''))) = N'' OR LTRIM(RTRIM(ISNULL(@NameEn, N''))) = N''
        THROW 59010, N'Both names are required.', 1;

    IF @CategoryId IS NULL
    BEGIN
        SET @SavedId = CONCAT(N'cat_',
            LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));

        INSERT INTO core.Category (CategoryId, NameAr, NameEn, SortOrder, IsActive)
        VALUES (@SavedId, @NameAr, @NameEn, @SortOrder, @IsActive);
    END
    ELSE
    BEGIN
        SET @SavedId = @CategoryId;

        UPDATE core.Category
        SET NameAr = @NameAr, NameEn = @NameEn, SortOrder = @SortOrder,
            IsActive = @IsActive, UpdatedAtUtc = SYSUTCDATETIME()
        WHERE CategoryId = @CategoryId;

        IF @@ROWCOUNT = 0 THROW 59011, N'No such category.', 1;
    END
END
GO

/*================================================================ discounts */

CREATE OR ALTER PROCEDURE sales.usp_SaveDiscount
    @DiscountId     NVARCHAR(40) = NULL,
    @NameAr         NVARCHAR(200),
    @NameEn         NVARCHAR(200),
    @DiscountType   NVARCHAR(20),
    @Value          DECIMAL(18,3),
    @MaxDiscountH   BIGINT = NULL,
    @MinSubtotalH   BIGINT = 0,
    @ApprovalAboveH BIGINT = NULL,
    @AppliesTo      NVARCHAR(20) = N'sale',
    @IsAutomatic    BIT = 0,
    @AllowStacking  BIT = 0,
    @ActiveDays     NVARCHAR(20) = NULL,
    @StartsOn       DATE = NULL,
    @EndsOn         DATE = NULL,
    @IsActive       BIT = 1,
    @SavedId        NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @DiscountType NOT IN (N'percent', N'amount')
        THROW 59020, N'A discount is either a percentage or an amount.', 1;

    /* A percentage above 100 would pay the customer to take the goods. */
    IF @DiscountType = N'percent' AND (@Value <= 0 OR @Value > 100)
        THROW 59021, N'A percentage must be above zero and at most 100.', 1;

    IF @DiscountType = N'amount' AND @Value <= 0
        THROW 59022, N'An amount must be above zero.', 1;

    IF @EndsOn IS NOT NULL AND @StartsOn IS NOT NULL AND @EndsOn < @StartsOn
        THROW 59023, N'The end date cannot be before the start date.', 1;

    IF @DiscountId IS NULL
    BEGIN
        SET @SavedId = CONCAT(N'dsc_',
            LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));

        INSERT INTO sales.Discount
            (DiscountId, NameAr, NameEn, DiscountType, Value, MaxDiscountH,
             MinSubtotalH, ApprovalAboveH, AppliesTo, IsAutomatic, AllowStacking,
             ActiveDays, StartsOn, EndsOn, IsActive)
        VALUES
            (@SavedId, @NameAr, @NameEn, @DiscountType, @Value, @MaxDiscountH,
             @MinSubtotalH, @ApprovalAboveH, @AppliesTo, @IsAutomatic, @AllowStacking,
             @ActiveDays, @StartsOn, @EndsOn, @IsActive);
    END
    ELSE
    BEGIN
        SET @SavedId = @DiscountId;

        /* UsageCount is not touched. It records how often this discount has
           been applied; an edit must not reset that history. */
        UPDATE sales.Discount
        SET NameAr = @NameAr, NameEn = @NameEn, DiscountType = @DiscountType,
            Value = @Value, MaxDiscountH = @MaxDiscountH, MinSubtotalH = @MinSubtotalH,
            ApprovalAboveH = @ApprovalAboveH, AppliesTo = @AppliesTo,
            IsAutomatic = @IsAutomatic, AllowStacking = @AllowStacking,
            ActiveDays = @ActiveDays, StartsOn = @StartsOn, EndsOn = @EndsOn,
            IsActive = @IsActive, UpdatedAtUtc = SYSUTCDATETIME()
        WHERE DiscountId = @DiscountId;

        IF @@ROWCOUNT = 0 THROW 59024, N'No such discount.', 1;
    END
END
GO

/*==================================================================== users */

CREATE OR ALTER PROCEDURE core.usp_SaveUser
    @UserId          NVARCHAR(40) = NULL,
    @Username        NVARCHAR(80),
    @NameAr          NVARCHAR(200),
    @NameEn          NVARCHAR(200),
    @Email           NVARCHAR(200) = NULL,
    @Phone           NVARCHAR(40)  = NULL,
    @RoleId          NVARCHAR(40),
    @Status          NVARCHAR(20)  = N'active',
    @IsExternal      BIT = 0,
    @AccessExpiresOn DATE = NULL,
    @BaseSalaryH     BIGINT = 0,
    @Actor           NVARCHAR(160),
    @SavedId         NVARCHAR(40) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF LTRIM(RTRIM(ISNULL(@Username, N''))) = N''
        THROW 59030, N'A username is required.', 1;

    IF EXISTS (SELECT 1 FROM core.AppUser
               WHERE Username = @Username AND (@UserId IS NULL OR UserId <> @UserId))
        THROW 59031, N'That username is already taken.', 1;

    /* Sign-in matches on email or phone, so a duplicate would make which
       account you reach a matter of row order. */
    IF @Email IS NOT NULL AND EXISTS (
        SELECT 1 FROM core.AppUser
        WHERE Email = @Email AND (@UserId IS NULL OR UserId <> @UserId))
        THROW 59032, N'That email is already used by another account.', 1;

    IF @Phone IS NOT NULL AND EXISTS (
        SELECT 1 FROM core.AppUser
        WHERE Phone = @Phone AND (@UserId IS NULL OR UserId <> @UserId))
        THROW 59033, N'That mobile number is already used by another account.', 1;

    IF NOT EXISTS (SELECT 1 FROM core.Role WHERE RoleId = @RoleId)
        THROW 59034, N'No such role.', 1;

    /* An external account without an expiry never expires, which defeats the
       reason for marking it external. */
    IF @IsExternal = 1 AND @AccessExpiresOn IS NULL
        THROW 59035, N'An external account needs an access expiry date.', 1;

    IF @UserId IS NULL
    BEGIN
        SET @SavedId = CONCAT(N'usr_',
            LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')));

        /* No password is set here. It arrives through the sign-in flow, so a
           password never passes through an admin form or an audit row. */
        INSERT INTO core.AppUser
            (UserId, Username, NameAr, NameEn, Email, Phone, RoleId, Status,
             IsExternal, AccessExpiresOn, BaseSalaryH, CreatedBy)
        VALUES
            (@SavedId, @Username, @NameAr, @NameEn, @Email, @Phone, @RoleId, @Status,
             @IsExternal, @AccessExpiresOn, @BaseSalaryH, @Actor);
    END
    ELSE
    BEGIN
        SET @SavedId = @UserId;

        /* The last active owner cannot be suspended or demoted: someone has to
           be able to grant permissions back. */
        IF (@Status <> N'active' OR @RoleId <> N'rol_owner')
           AND EXISTS (SELECT 1 FROM core.AppUser
                       WHERE UserId = @UserId AND RoleId = N'rol_owner' AND Status = N'active')
           AND (SELECT COUNT(*) FROM core.AppUser
                WHERE RoleId = N'rol_owner' AND Status = N'active') <= 1
            THROW 59036, N'This is the only active owner. Appoint another first.', 1;

        UPDATE core.AppUser
        SET Username = @Username, NameAr = @NameAr, NameEn = @NameEn,
            Email = @Email, Phone = @Phone, RoleId = @RoleId, Status = @Status,
            IsExternal = @IsExternal, AccessExpiresOn = @AccessExpiresOn,
            BaseSalaryH = @BaseSalaryH,
            UpdatedAtUtc = SYSUTCDATETIME(), UpdatedBy = @Actor
        WHERE UserId = @UserId;

        IF @@ROWCOUNT = 0 THROW 59037, N'No such user.', 1;
    END
END
GO

/*================================================================== company */

CREATE OR ALTER PROCEDURE core.usp_SaveCompany
    @NameAr        NVARCHAR(200),
    @NameEn        NVARCHAR(200),
    @DescriptionAr NVARCHAR(600) = NULL,
    @DescriptionEn NVARCHAR(600) = NULL,
    @LogoUrl       NVARCHAR(400) = NULL,
    @CrNumber      NVARCHAR(40)  = NULL,
    @VatNumber     NVARCHAR(40)  = NULL,
    @Phone         NVARCHAR(40)  = NULL,
    @Email         NVARCHAR(200) = NULL,
    @Website       NVARCHAR(200) = NULL,
    @Street        NVARCHAR(200) = NULL,
    @District      NVARCHAR(120) = NULL,
    @City          NVARCHAR(120) = NULL,
    @PostalCode    NVARCHAR(20)  = NULL,
    @Country       NVARCHAR(80)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    /* A Saudi VAT number is fifteen digits. A wrong one prints on every
       invoice, so it is checked here rather than discovered by an auditor. */
    IF @VatNumber IS NOT NULL AND @VatNumber <> N''
       AND (LEN(@VatNumber) <> 15 OR @VatNumber LIKE N'%[^0-9]%')
        THROW 59040, N'A VAT number must be exactly 15 digits.', 1;

    UPDATE core.Company
    SET NameAr = @NameAr, NameEn = @NameEn,
        DescriptionAr = @DescriptionAr, DescriptionEn = @DescriptionEn,
        LogoUrl = @LogoUrl, CrNumber = @CrNumber, VatNumber = @VatNumber,
        Phone = @Phone, Email = @Email, Website = @Website,
        Street = @Street, District = @District, City = @City,
        PostalCode = @PostalCode, Country = @Country,
        UpdatedAtUtc = SYSUTCDATETIME();

    IF @@ROWCOUNT = 0 THROW 59041, N'No company record exists.', 1;
END
GO

PRINT N'24 · Write paths ready.';
GO

/*------------------------------------------------------------- permissions */

INSERT INTO core.Permission (PermissionKey, Module, Action, Sensitivity)
SELECT v.k, v.m, v.a, N'general'
FROM (VALUES
    (N'products.create', N'products', N'create'),
    (N'products.edit',   N'products', N'edit'),
    (N'discounts.create',N'discounts',N'create'),
    (N'discounts.edit',  N'discounts',N'edit'),
    (N'users.create',    N'users',    N'create'),
    (N'users.edit',      N'users',    N'edit'),
    (N'company.edit',    N'company',  N'edit')
) AS v (k, m, a)
WHERE NOT EXISTS (SELECT 1 FROM core.Permission WHERE PermissionKey = v.k);
GO

INSERT INTO core.RolePermission (RoleId, PermissionKey)
SELECT N'rol_owner', p.PermissionKey
FROM core.Permission AS p
WHERE p.PermissionKey IN (N'products.create', N'products.edit', N'discounts.create',
                          N'discounts.edit', N'users.create', N'users.edit', N'company.edit')
  AND NOT EXISTS (SELECT 1 FROM core.RolePermission AS rp
                  WHERE rp.RoleId = N'rol_owner' AND rp.PermissionKey = p.PermissionKey);

/* A manager runs the shop's catalogue and promotions. Staff accounts and the
   company's own details stay with the owner. */
INSERT INTO core.RolePermission (RoleId, PermissionKey)
SELECT N'rol_manager', p.PermissionKey
FROM core.Permission AS p
WHERE p.PermissionKey IN (N'products.create', N'products.edit',
                          N'discounts.create', N'discounts.edit')
  AND NOT EXISTS (SELECT 1 FROM core.RolePermission AS rp
                  WHERE rp.RoleId = N'rol_manager' AND rp.PermissionKey = p.PermissionKey);
GO

SELECT CASE WHEN (SELECT COUNT(*) FROM core.Permission
                  WHERE PermissionKey IN (N'products.create', N'products.edit',
                        N'discounts.create', N'discounts.edit', N'users.create',
                        N'users.edit', N'company.edit')) = 7
            THEN N'PASS  Write paths ready'
            ELSE N'FAIL  Permissions incomplete'
       END AS Result;
GO
