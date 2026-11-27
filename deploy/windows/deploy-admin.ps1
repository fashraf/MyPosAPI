# deploy-admin.ps1 -Env production -Tag sha-xxxxxxx -Zip C:\Project\POS\artifacts\possa-sha-xxxxxxx.zip [-Port 9090]
# Puts a POSSA (super admin) build live on site MyPOS-admin: new release folder, /api proxied to the API,
# switch, check, switch back if the page does not answer.
param([Parameter(Mandatory)][string]$Env, [Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$Zip, [int]$Port = 9090)
. "$PSScriptRoot\lib.ps1"
Import-Module WebAdministration
$c = Get-Cfg $Env
$site = 'MyPOS-admin'
if (-not (Get-Website -Name $site)) { throw "Site $site is missing. Run setup-saas.ps1 first." }
if (-not (Test-Path $Zip)) { throw "Package not found: $Zip" }
$adminRoot = Join-Path $Root 'POSAdmin'
$rel = Join-Path $adminRoot "releases\$Tag"
if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
Expand-Archive -Path $Zip -DestinationPath $rel -Force
Write-WebConfig $c $rel
$prev = (Get-Website -Name $site).physicalPath
Set-ItemProperty "IIS:\Sites\$site" -Name physicalPath -Value $rel
Log "POSSA $Env -> $Tag (was $prev)"
$ok = $false
foreach ($i in 1..10) {
  try { if ((Invoke-WebRequest "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200) { $ok = $true; break } } catch { Start-Sleep 3 }
}
if (-not $ok) { Set-ItemProperty "IIS:\Sites\$site" -Name physicalPath -Value $prev; Log "POSSA not answering - back to $prev"; exit 1 }
Get-ChildItem (Join-Path $adminRoot 'releases') -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Log "OK: POSSA $Tag live at http://<server IP>:$Port"
