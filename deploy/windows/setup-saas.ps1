# Switch one environment of this server to SaaS mode: one API for every client, each client with its own database,
# clients at <name>.<domain> (pos1.wateeq.com) and the super admin app (POSSA) at admin.<domain>.
#
#   powershell -ExecutionPolicy Bypass -File C:\Project\POS\bin\setup-saas.ps1 -Env production -Domain wateeq.com -AdminEmail you@example.com
#
# Before: GoDaddy DNS -> add two A records pointing at this server: "*" (every client) and "admin".
# Safe to run again.
param(
  [Parameter(Mandatory)][ValidateSet('staging', 'production')][string]$Env,
  [Parameter(Mandatory)][string]$Domain,
  [Parameter(Mandatory)][string]$AdminEmail,
  [string]$ControlDatabase = 'MYPOS_CONTROL'
)
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
$Domain = $Domain.Trim().ToLower()
$adminHost = "admin.$Domain"
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

Step "1/6 Control database $ControlDatabase (list of clients; never their sales)"
$psql = Join-Path $c.pgBin 'psql.exe'
$env:PGPASSWORD = $c.dbPassword
$exists = & $psql -h localhost -U $c.dbUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$ControlDatabase'"
if (-not "$exists".Trim()) {
  & $psql -h localhost -U $c.dbUser -d postgres -c "CREATE DATABASE `"$ControlDatabase`""
  if ($LASTEXITCODE -ne 0) { throw "Could not create $ControlDatabase. In pgAdmin run: CREATE DATABASE `"$ControlDatabase`" OWNER $($c.dbUser);" }
}
Remove-Item Env:\PGPASSWORD
Write-Host "   OK"

Step "2/6 Settings in config\mypos.json"
$cfgFile = Join-Path $c.base 'config\mypos.json'
$json = Get-Content $cfgFile -Raw | ConvertFrom-Json
$secrets = if ($json.PSObject.Properties['saas'] -and $json.saas.secretsKey) { $json.saas.secretsKey } else { -join ((48..57) + (97..122) | Get-Random -Count 48 | ForEach-Object { [char]$_ }) }
$json | Add-Member -NotePropertyName saas -NotePropertyValue ([pscustomobject]@{
  controlDatabase = $ControlDatabase; rootDomain = $Domain; adminHost = $adminHost; secretsKey = $secrets }) -Force
$json | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 $cfgFile
Write-Host "   OK (keep secretsKey safe: it encrypts every client's database password)" -ForegroundColor Yellow

Step "3/6 IIS: every client at *.$Domain on site $($c.site)"
if (-not (Get-WebBinding -Name $c.site -Protocol http -Port 80 -HostHeader "*.$Domain")) {
  New-WebBinding -Name $c.site -Protocol http -Port 80 -HostHeader "*.$Domain"
}
Write-Host "   OK"

Step "4/6 IIS: POSSA (super admin) at $adminHost"
$adminSite = "MyPOS-admin"
$adminRoot = Join-Path $Root 'POSAdmin'
New-Item -ItemType Directory -Force (Join-Path $adminRoot 'current') | Out-Null
if (-not (Get-Website -Name $adminSite)) {
  if (-not (Test-Path "IIS:\AppPools\$adminSite")) { New-WebAppPool -Name $adminSite | Out-Null; Set-ItemProperty "IIS:\AppPools\$adminSite" managedRuntimeVersion '' }
  New-Website -Name $adminSite -Port 80 -HostHeader $adminHost -PhysicalPath (Join-Path $adminRoot 'current') -ApplicationPool $adminSite | Out-Null
}
# Same API as the clients: /api/sa on the admin host.
Write-WebConfig $c (Join-Path $adminRoot 'current')
if (-not (Test-Path (Join-Path $adminRoot 'current\index.html'))) {
  Set-Content -Encoding UTF8 (Join-Path $adminRoot 'current\index.html') '<!doctype html><title>POSSA</title><p>POSSA will appear here after its first deploy.</p>'
}
Write-Host "   OK: $adminSite -> $adminRoot\current"

Step "5/6 API restart with SaaS settings and control database"
$apiCurrent = Join-Path $c.base 'api\current'
Write-ApiConfig $c $apiCurrent (Get-Current $c 'api')
Push-Location $apiCurrent
$env:Database__Provider = 'postgres'; $env:Tenancy__Mode = 'multi'; $env:ConnectionStrings__Control = Get-ControlConnection $c; $env:Secrets__Key = $secrets
& dotnet .\PosApi.dll --migrate
Step "6/6 Super admin account"
& dotnet .\PosApi.dll --create-admin $AdminEmail "Super admin"
foreach ($v in 'Database__Provider', 'Tenancy__Mode', 'ConnectionStrings__Control', 'Secrets__Key') { Remove-Item "Env:\$v" -ErrorAction SilentlyContinue }
Pop-Location
Restart-WebAppPool -Name "$($c.site)-api"

Write-Host "`nDONE." -ForegroundColor Green
Write-Host "  POSSA:   http://$adminHost   (sign in with $AdminEmail and the one-time password above, then change it)"
Write-Host "  Clients: http://<name>.$Domain once created in POSSA."
Write-Host "  HTTPS:   run add-https-host.ps1 -Address for $adminHost and for each client name (or a wildcard certificate)."
