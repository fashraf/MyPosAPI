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
6. Business numbers are numeric (BIGINT/INT): invoice, daily order no., session no., credit note, receipt, employee no.
   Assigned only on the server, inside the transaction.
7. Secrets never go in git: connection strings and JWT keys live in `appsettings.Production.json` on the server only.

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
