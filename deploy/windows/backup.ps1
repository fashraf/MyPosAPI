# backup.ps1 -Env staging|production [-Label nightly] — compressed PostgreSQL backup into <env>\backups (keeps 14 per database).
# SaaS mode: the control database and every client database on this server (same database user).
param([Parameter(Mandatory)][string]$Env, [string]$Label = 'manual')
. "$PSScriptRoot\lib.ps1"
$c = Get-Cfg $Env
$psql = Join-Path $c.pgBin 'psql.exe'
$env:PGPASSWORD = $c.dbPassword

$databases = @($c.database)
if (Test-Saas $c) {
  $databases = @($c.saas.controlDatabase)
  $local = & $psql -h localhost -U $c.dbUser -d $c.saas.controlDatabase -tAc "SELECT db_name FROM control.tenant WHERE lower(db_host) IN ('localhost','127.0.0.1','::1') ORDER BY tenant_id" 2>$null
  $databases += @($local | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
  $remote = & $psql -h localhost -U $c.dbUser -d $c.saas.controlDatabase -tAc "SELECT slug || ' (' || db_host || ')' FROM control.tenant WHERE lower(db_host) NOT IN ('localhost','127.0.0.1','::1')" 2>$null
  if ("$remote".Trim()) { Log "Not backed up here (database on another server; back it up there): $($remote -join ', ')" }
}

$failed = 0
foreach ($db in $databases) {
  $file = Join-Path $c.base ("backups\{0}-{1:yyyyMMdd-HHmmss}-{2}.dump" -f $db, (Get-Date), ($Label -replace '[^A-Za-z0-9._-]', '_'))
  & (Join-Path $c.pgBin 'pg_dump.exe') -h localhost -U $c.dbUser -d $db -Fc -f $file
  if ($LASTEXITCODE -ne 0) { $failed++; Log "Backup FAILED: $db"; continue }
  Get-ChildItem (Join-Path $c.base 'backups') -Filter "$db-*.dump" | Sort-Object LastWriteTime -Descending | Select-Object -Skip 14 | Remove-Item -Force
  Log "Backup OK: $(Split-Path $file -Leaf) ($([math]::Round((Get-Item $file).Length / 1MB, 1)) MB)"
}
Remove-Item Env:\PGPASSWORD
if ($failed -gt 0) { throw "pg_dump failed for $failed database(s)" }
