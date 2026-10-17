# Runs Aethera on Windows (or any machine with PowerShell 7 and Docker Desktop): builds the images, starts PostgreSQL, Redis, the control plane and an agent.
#
#   From a checkout:   pwsh deploy/install.ps1
#   Without one:       irm https://raw.githubusercontent.com/MihaiM21/Aethera-Forge/master/deploy/install.ps1 | iex
#
# Parameters (when saved as a file): -Dir <path> (default ~/aethera when not run from a checkout), -Ref <branch or tag> (default master),
# -HttpPort 8080, -GrpcPort 9443, -PublicHost localhost, -NoOpen. Running it again updates in place; the generated secrets in .env are kept.
# For a Linux server use deploy/install.sh instead (it also installs Docker and sets up updates).
[CmdletBinding()]
param(
  [string]$Dir = "",
  [string]$Ref = "master",
  [int]$HttpPort = 8080,
  [int]$GrpcPort = 9443,
  [string]$PublicHost = "localhost",
  [switch]$NoOpen
)
$ErrorActionPreference = "Stop"
$Repo = "MihaiM21/Aethera-Forge"

function Say($m) { Write-Host "==> $m" }
function Die($m) { Write-Host "install: $m" -ForegroundColor Red; exit 1 }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die "Docker is not installed. Install Docker Desktop (https://docs.docker.com/desktop/) and run this again." }
docker info *> $null
if ($LASTEXITCODE -ne 0) { Die "Docker is installed but not running. Start Docker Desktop, wait until it says 'running', and run this again." }
docker compose version *> $null
if ($LASTEXITCODE -ne 0) { Die "Docker Compose v2 is required (included in Docker Desktop)." }

# Where the sources are: this checkout, or a downloaded copy.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { "" }
if ($here -and (Test-Path (Join-Path $here "docker-compose.yml")) -and (Test-Path (Join-Path $here "docker/Dockerfile.control-plane"))) {
  $deploy = $here
  if (-not $Dir) { $Dir = $deploy }
} else {
  if (-not $Dir) { $Dir = Join-Path $HOME "aethera" }
  $src = Join-Path $Dir "src"
  Say "Downloading the source ($Ref)"
  $tmp = Join-Path ([IO.Path]::GetTempPath()) ("aethera-" + [guid]::NewGuid().ToString("N"))
  New-Item -ItemType Directory -Path $tmp | Out-Null
  try {
    Invoke-WebRequest "https://codeload.github.com/$Repo/zip/$Ref" -OutFile (Join-Path $tmp "src.zip") -UseBasicParsing
    Expand-Archive (Join-Path $tmp "src.zip") -DestinationPath $tmp
    if (Test-Path $src) { Remove-Item $src -Recurse -Force }
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Move-Item (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName $src
  } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
  $deploy = Join-Path $src "deploy"
}

# The .env keeps the generated secrets; an existing one is never rotated (that would lock the installation out of its data).
$envFile = Join-Path $deploy ".env"
$values = [ordered]@{}
if (Test-Path $envFile) {
  foreach ($line in Get-Content $envFile) { if ($line -match '^([A-Za-z0-9_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] } }
}
function Random-Bytes($n) { $b = New-Object byte[] $n; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $b }
if (-not $values["AETHERA_DB_PASSWORD"] -or $values["AETHERA_DB_PASSWORD"] -eq "change-me") {
  $values["AETHERA_DB_PASSWORD"] = ([Convert]::ToBase64String((Random-Bytes 24))) -replace '[/+=]', ''
}
if (-not $values["AETHERA_MASTER_KEY"]) {
  $values["AETHERA_MASTER_KEY"] = [Convert]::ToBase64String((Random-Bytes 32))
  Say "Generated the master key in $envFile. It encrypts every stored secret: back this file up."
}
$values["AETHERA_PUBLIC_HOST"] = $PublicHost
$values["AETHERA_HTTP_PORT"] = "$HttpPort"
$values["AETHERA_GRPC_PORT"] = "$GrpcPort"
# LF line endings: the file is also read by tools inside Linux containers.
[IO.File]::WriteAllText($envFile, (($values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "`n") + "`n")

Say "Building and starting Aethera (several minutes the first time)"
docker compose -f (Join-Path $deploy "docker-compose.yml") --env-file $envFile up -d --build
if ($LASTEXITCODE -ne 0) { Die "docker compose failed (see the output above)." }

$base = "http://localhost:$HttpPort"
Say "Waiting for the control plane"
$ready = $false
for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
  try { $ready = (Invoke-WebRequest "$base/ready" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { Start-Sleep -Seconds 2 }
}
if (-not $ready) { Die "the control plane did not become ready: docker compose -f `"$deploy/docker-compose.yml`" logs aethera" }

Write-Host ""
Say "Aethera is running."
Write-Host "    URL:       $base   (the first account you register becomes the owner)"
Write-Host "    Server:    open Servers > 'Use this machine' to deploy applications on this computer"
Write-Host "    Files:     $deploy  (.env holds the generated secrets: back it up)"
Write-Host "    Logs:      docker compose -f `"$deploy/docker-compose.yml`" --env-file `"$envFile`" logs -f"
Write-Host "    Update:    run this script again"
if (-not $NoOpen) { Start-Process $base }
