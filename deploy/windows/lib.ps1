# Shared helpers for the MyPOS Windows deploy scripts. Every script takes -Env staging|production.
$ErrorActionPreference = 'Stop'
$Root = 'C:\Project\POS'
$Folders = @{ production = 'POSprod'; staging = 'POSStageing' }

function Get-Cfg([string]$Env) {
  if (-not $Folders.ContainsKey($Env)) { throw "Env must be staging or production" }
  $f = Join-Path $Root "$($Folders[$Env])\config\mypos.json"
  if (-not (Test-Path $f)) { throw "Missing $f - run setup.ps1 first" }
  $c = Get-Content $f -Raw | ConvertFrom-Json
  $c | Add-Member -NotePropertyName base -NotePropertyValue (Join-Path $Root $Folders[$Env]) -Force
  $c.database = Resolve-DbName $c
  return $c
}

# PostgreSQL names are case-sensitive when quoted: pgAdmin keeps "POSMAIN_STAGING", psql makes posmain_staging.
# Find the real name whatever its case, so the config never has to match it exactly.
function Resolve-DbName($c) {
  $psql = Join-Path $c.pgBin 'psql.exe'
  if (-not (Test-Path $psql)) { return $c.database }
  $env:PGPASSWORD = $c.dbPassword
  $names = & $psql -h localhost -U $c.dbUser -d postgres -tAc "SELECT datname FROM pg_database WHERE lower(datname) = lower('$($c.database)')" 2>&1
  $code = $LASTEXITCODE; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
  if ($code -ne 0) { throw "Cannot connect to PostgreSQL as $($c.dbUser): $names" }
  $found = @($names | Where-Object { "$_".Trim() }) | Select-Object -First 1
  if (-not $found) {
    throw ("Database '$($c.database)' does not exist. Fix: in pgAdmin run  CREATE DATABASE `"$($c.database)`" OWNER mypos_app;  " +
           "then re-run the deploy in GitHub Actions.")
  }
  return "$found".Trim()
}
function Log($t) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $t) }

# Current release tags, from the folders the "current" links point to.
function Get-Current($c, [string]$App) {
  $cur = Join-Path $c.base "$App\current"
  if (-not (Test-Path $cur)) { return 'none' }
  $target = (Get-Item $cur).Target
  if ($target -is [array]) { $target = $target[0] }
  $name = Split-Path $target -Leaf
  if ($name -eq 'initial') { return 'none' } else { return $name }
}
function Set-Current($c, [string]$App, [string]$Tag) {
  $cur = Join-Path $c.base "$App\current"
  $rel = Join-Path $c.base "$App\releases\$(if ($Tag -eq 'none') { 'initial' } else { $Tag })"
  if (-not (Test-Path $rel)) { throw "Release folder not found: $rel" }
  if (Test-Path $cur) { cmd /c rmdir "$cur" | Out-Null }
  cmd /c mklink /J "$cur" "$rel" | Out-Null
}

function Test-Url([string]$Url) {
  try { $r = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 5; return ($r.StatusCode -eq 200) } catch { return $false }
}
# Healthy = the site answers, and (when an API is deployed) /health and /health/db answer through the site.
function Test-Health($c, [int]$Wait = 120) {
  $end = (Get-Date).AddSeconds($Wait)
  $api = (Get-Current $c 'api') -ne 'none'
  while ((Get-Date) -lt $end) {
    $ok = Test-Url "http://localhost:$($c.webPort)/"
    if ($ok -and $api) { $ok = (Test-Url "http://localhost:$($c.webPort)/health") -and (Test-Url "http://localhost:$($c.webPort)/health/db") }
    if ($ok) { return $true }
    Start-Sleep -Seconds 3
  }
  return $false
}

function Add-Release($c, [string]$By) {
  $line = "{0:yyyy-MM-ddTHH:mm:ssZ} web={1} api={2} by={3}" -f (Get-Date).ToUniversalTime(), (Get-Current $c 'web'), (Get-Current $c 'api'), $By
  Add-Content -Path (Join-Path $c.base 'releases.log') -Value $line
}

# Keep the newest KeepReleases folders per app (never the ones in use).
function Remove-OldReleases($c, [string]$App) {
  $keep = [int]$c.keepReleases; if ($keep -lt 2) { $keep = 5 }
  $inUse = Get-Current $c $App
  Get-ChildItem (Join-Path $c.base "$App\releases") -Directory | Where-Object { $_.Name -notin @('initial', $inUse) } |
    Sort-Object LastWriteTime -Descending | Select-Object -Skip $keep | ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
}

# web.config for the site: SPA fallback + /api, /health, /cloudprnt forwarded to this environment's API.
function Write-WebConfig($c, [string]$Folder) {
  # "httpsHost" in config\mypos.json (set by setup-https.ps1): http://<that host> is sent to https://. The IP address
  # (ESP32 order light, health checks) and the certificate check (/.well-known) keep working over http.
  $https = ''
  if ($c.PSObject.Properties.Name -contains 'httpsHost' -and $c.httpsHost) {
    $https = @"
        <rule name="https" stopProcessing="true">
          <match url="(.*)" />
          <conditions logicalGrouping="MatchAll">
            <add input="{HTTPS}" pattern="^OFF$" />
            <add input="{HTTP_HOST}" pattern="^$([regex]::Escape($c.httpsHost))$" />
            <add input="{REQUEST_URI}" pattern="^/\.well-known/" negate="true" />
          </conditions>
          <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
        </rule>
"@
  }
  @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
$https        <rule name="api" stopProcessing="true">
          <match url="^(api|health|cloudprnt)(/.*)?$" />
          <action type="Rewrite" url="http://127.0.0.1:$($c.apiPort)/{R:0}" />
        </rule>
        <rule name="spa" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
          </conditions>
          <action type="Rewrite" url="/index.html" />
        </rule>
      </rules>
    </rewrite>
    <staticContent>
      <remove fileExtension=".webmanifest" />
      <mimeMap fileExtension=".webmanifest" mimeType="application/manifest+json" />
    </staticContent>
    <httpProtocol><customHeaders><add name="Cache-Control" value="no-cache" /></customHeaders></httpProtocol>
  </system.webServer>
  <location path="assets"><system.webServer><httpProtocol><customHeaders>
    <remove name="Cache-Control" /><add name="Cache-Control" value="public, max-age=31536000, immutable" />
  </customHeaders></httpProtocol></system.webServer></location>
</configuration>
"@ | Set-Content -Encoding UTF8 (Join-Path $Folder 'web.config')
}

# web.config for the API: ASP.NET Core Module + every environment setting the API needs (nothing in git).
function Write-ApiConfig($c, [string]$Folder, [string]$Tag) {
  $aspnet = if ($c.environment -eq 'production') { 'Production' } else { 'Staging' }
  $cs = "Host=localhost;Port=5432;Database=$($c.database);Username=$($c.dbUser);Password=$($c.dbPassword)"
  $vars = [ordered]@{
    ASPNETCORE_ENVIRONMENT = $aspnet; APP_VERSION = $Tag
    Database__Provider = 'postgres'; ConnectionStrings__PosMain = $cs
    Jwt__SigningKey = $c.jwtSigningKey; Cors__AllowedOrigins__0 = $c.publicUrl
    Auth__LoginMode = 'TwoStep'
    # Staging only: fixed one-time code (no SMS/e-mail sender yet). Production refuses to start with it.
    Auth__UseFixedDevOtp = $(if ($c.environment -eq 'staging') { 'true' } else { 'false' })
    # Staging: the QA suite signs in many times from one address.
    RateLimiting__AuthPermitLimit = $(if ($c.environment -eq 'staging') { '60' } else { '10' })
    # Order light (ESP32): device key and poll interval, from config\mypos.json ("orderLightKey", "orderLightPollSeconds").
    OrderLight__Key = $(if ($c.PSObject.Properties['orderLightKey']) { $c.orderLightKey } else { '' })
    OrderLight__PollSeconds = $(if ($c.PSObject.Properties['orderLightPollSeconds']) { "$($c.orderLightPollSeconds)" } else { '10' })
  }
  $envXml = ($vars.GetEnumerator() | ForEach-Object { "        <environmentVariable name=`"$($_.Key)`" value=`"$([Security.SecurityElement]::Escape([string]$_.Value))`" />" }) -join "`n"
  @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers>
      <aspNetCore processPath="dotnet" arguments=".\PosApi.dll" stdoutLogEnabled="true" stdoutLogFile="$($c.base)\logs\api" hostingModel="inprocess">
        <environmentVariables>
$envXml
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
"@ | Set-Content -Encoding UTF8 (Join-Path $Folder 'web.config')
}
