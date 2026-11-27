# MyPosAPI — rules for Claude

.NET 8 minimal APIs + Dapper over SQL Server (`MyPosDB`). Front end lives in `fashraf/MyPOSFO`.

## How work flows
1. Branch from `develop`: `feature/<name>` or `fix/<issue-id>-<name>`. PR to `develop`, never push to `main`/`develop`.
2. Bugs: root cause first, then the fix, then a test.
3. **Database changes = a new migration file**, numbered after the last one in `db-migrations/`
   (e.g. `30-numbering.sql`). Never edit a migration that has already run on staging or production.
4. Every migration must:
   - be safe to run twice (`IF COL_LENGTH(...) IS NULL`, `IF OBJECT_ID(...) IS NULL`, `CREATE OR ALTER`)
   - contain no `USE <database>` line (the deploy tool picks the database)
   - keep the previous API version working (add first; drop/rename only in a later release)
5. Run before every push: `dotnet build src/PosApi/PosApi.csproj -c Release` and `python3 validate-api.py`.
6. Business numbers are numeric (BIGINT/INT): invoice (from 1), daily order no. (1 each business day), session no., credit note, receipt, employee no.
   Primary keys are integers (branch 1, item 1, sale = invoice no.); text keys only for fixed codes (role, permission, series).
   Assigned only on the server, inside the transaction.
7. Secrets never go in git: connection strings and JWT keys live in `appsettings.Production.json` on the server only.

## Database layer (engine-neutral) — read before touching data access
- Engine comes from configuration only: `Database:Provider` (postgres | sqlserver) + `ConnectionStrings:PosMain`.
  Never branch on the engine in feature code.
- Layers: Endpoint → Service (business rules, transactions, numbering — C#, no stored procedures)
  → Repository (one per aggregate, interface + one implementation) → `IDb` (connection, transaction, SqlKata query builder).
- Write queries with SqlKata (`db.Query(conn).Query("sale")...`) or plain ANSI SQL. The only engine-specific SQL lives in
  `Data/Providers/*Provider.cs` (`ISqlDialect`) — keep it tiny.
- Schema = FluentMigrator classes in `Data/Migrations/` (one source for every engine). `dotnet PosApi.dll --migrate` runs them.
  Names: lower_snake_case tables/columns. Never edit an applied migration; add a new one.
- Business numbers via `INumberSeries.NextAsync(conn, tx, "invoice")` inside the transaction.
- Legacy: `ISqlConnectionFactory` + `db-migrations/*.sql` (T-SQL) are the old SQL Server path; features move to `IDb` one by one.

## Deploy kit (`deploy/`)
- Docker Compose per environment on any Linux server: `db` (SQL Server Express), `api`, `web` (nginx, proxies /api and /health).
- Scripts in `deploy/bin/` (copied to `/opt/mypos/bin` on every deploy): `deploy.sh` (backup → migrate → swap → health → auto-rollback),
  `rollback.sh` (previous release, < 1 min), `migrate.sh` (runs new files from `db-migrations/` baked into the image; journal `dbo.__Migrations`),
  `backup.sh`, `restore.sh`, `setup.sh` (fresh server). Keep them shellcheck-clean (CI checks).
- The API image must contain `db-migrations/`; migration order = base script, then numbered files.

## Environments
- Staging: merge to `develop` → deploy + migrate staging DB → smoke tests.
- Production: release tag on `main` → Fahad approves in GitHub → backup → migrate → deploy → smoke → auto-rollback on failure.
- Server: any Linux box with Docker; its IP is only in the repo secrets `DEPLOY_HOST_STAGING` / `DEPLOY_HOST_PRODUCTION`.
  Databases `MyPosDB` (prod) and `MyPosDB_Staging` (staging).
