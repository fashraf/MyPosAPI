# deploy.ps1 -Env staging|production -App web|api -Tag sha-xxxxxxx|vX.Y.Z -Zip C:\Project\POS\artifacts\web-sha-xxxxxxx.zip [-By name]
# Unpacks into a new release folder, (API: backup + migrations), switches "current" to it, checks health,
# and switches back automatically if the new version is not healthy.
param([Parameter(Mandatory)][string]$Env, [Parameter(Mandatory)][ValidateSet('web', 'api')][string]$App,
      [Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$Zip, [string]$By = 'manual')
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
$sw = [Diagnostics.Stopwatch]::StartNew()
$prev = Get-Current $c $App
Log "Deploying $Env ${App}: $prev -> $Tag"
if (-not (Test-Path $Zip)) { throw "Package not found: $Zip" }

$rel = Join-Path $c.base "$App\releases\$Tag"
$same = ($Tag -eq $prev)
if ($same) {
  # Re-running the version that is already live: its files are in use, so keep them (same build) and only
  # refresh settings, run migrations and restart.
  Log "$Tag is already live - reusing its files."
} else {
  if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
  Expand-Archive -Path $Zip -DestinationPath $rel -Force
}
$pool = if ($App -eq 'web') { $c.site } else { "$($c.site)-api" }

if ($App -eq 'web') {
  Write-WebConfig $c $rel
} else {
  Write-ApiConfig $c $rel $Tag
  & "$PSScriptRoot\backup.ps1" -Env $Env -Label "pre-$Tag"
  # Database migrations: the API package migrates the database itself (provider-independent) when asked to.
  $env:Database__Provider = 'postgres'
  $env:ConnectionStrings__PosMain = "Host=localhost;Port=5432;Database=$($c.database);Username=$($c.dbUser);Password=$($c.dbPassword)"
  if (Test-Saas $c) {
    # SaaS: the control database, then every client's database (one failing does not stop the others).
    $env:Tenancy__Mode = 'multi'; $env:ConnectionStrings__Control = Get-ControlConnection $c; $env:Secrets__Key = $c.saas.secretsKey
    $env:Tenancy__RootDomain = $c.saas.rootDomain
  }
  Push-Location $rel
  & dotnet .\PosApi.dll --migrate
  $code = $LASTEXITCODE
  # Setup data (branches, users, roles, items, combos). Only adds what is missing; never touches activity.
  # Staging always; production only when "seedOnEmpty": true is set in config\mypos.json.
  if ($code -eq 0 -and -not (Test-Saas $c) -and ($c.environment -eq 'staging' -or $c.seedOnEmpty) -and (Test-Path .\seed\tenant-alnoor.json)) {
    & dotnet .\PosApi.dll --seed .\seed\tenant-alnoor.json
    $code = $LASTEXITCODE
  }
  Pop-Location
  Remove-Item Env:\ConnectionStrings__PosMain
  foreach ($v in 'Tenancy__Mode', 'ConnectionStrings__Control', 'Secrets__Key', 'Tenancy__RootDomain') { Remove-Item "Env:\$v" -ErrorAction SilentlyContinue }
  if ($code -ne 0) {
    Log "Migration failed - old version keeps running."
    if (-not $same) { Remove-Item $rel -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
  }
  'Updating, back in a moment.' | Set-Content (Join-Path $c.base 'api\current\app_offline.htm') -ErrorAction SilentlyContinue
}

Set-Current $c $App $Tag
Restart-WebAppPool -Name $pool -ErrorAction SilentlyContinue
Remove-Item (Join-Path $c.base 'api\current\app_offline.htm') -ErrorAction SilentlyContinue

if (Test-Health $c 150) {
  Add-Release $c $By
  Remove-OldReleases $c $App
  Copy-Item (Join-Path $PSScriptRoot '*.ps1') "$Root\bin\" -ErrorAction SilentlyContinue
  Log ("OK: $Env is healthy on web={0} api={1} ({2:N0}s)." -f (Get-Current $c 'web'), (Get-Current $c 'api'), $sw.Elapsed.TotalSeconds)
} else {
  Log "New version is NOT healthy - rolling back to $App=$prev."
  # Why: each check's answer, then the last lines the API wrote (start-up errors land here).
  foreach ($u in '/', '/health', '/health/db') {
    try { $r = Invoke-WebRequest "http://localhost:$($c.webPort)$u" -UseBasicParsing -TimeoutSec 10; Log "  $u -> $($r.StatusCode)" }
    catch { Log "  $u -> $($_.Exception.Message) $(try { (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd().Substring(0, 300) } catch { '' })" }
  }
  $last = Get-ChildItem (Join-Path $c.base 'logs') -Filter 'api*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($last) { Log "  --- $($last.Name) (last 40 lines) ---"; Get-Content $last.FullName -Tail 40 | ForEach-Object { Log "  $_" } }
  Set-Current $c $App $prev
  Restart-WebAppPool -Name $pool -ErrorAction SilentlyContinue
  if (Test-Health $c 90) { Log 'Rolled back, previous version healthy.' } else { Log 'WARNING: previous version is not healthy either.' }
  exit 1
}
