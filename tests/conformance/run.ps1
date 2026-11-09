<#
.SYNOPSIS
  Runs the conformance suite (docs/conformance.md) against an implementation's container image: a fresh database, the
  API started with the conformance settings, the CLI run in containers from the same image.

.EXAMPLE
  # The .NET implementation, built from this repository, against the local SQL Server container (CLAUDE.md):
  ./tests/conformance/run.ps1 -Build

.EXAMPLE
  # The Go implementation (ports/go), its CLI at /app/cli in the image:
  ./tests/conformance/run.ps1 -Build -Image sanction-and-forgive-go:conformance -Dockerfile ports/go/Dockerfile -CliCommand '--entrypoint /app/cli {image}'

.EXAMPLE
  # Rewrite the golden files from the reference implementation after a deliberate change (review the diff):
  ./tests/conformance/run.ps1 -Build -UpdateGolden
#>
param(
    # The image to test. Its entry point serves the API on 8080; -CliCommand runs its CLI.
    [string] $Image = 'sanction-and-forgive:conformance',
    # Build $Image from this repository first, with -Dockerfile (the .NET implementation's by default).
    [switch] $Build,
    [string] $Dockerfile = 'Dockerfile',
    # How the CLI is started inside the image (the .NET image: dotnet SanctionAndForgive.Cli.dll).
    [string] $CliCommand = '--entrypoint dotnet {image} SanctionAndForgive.Cli.dll',
    # SQL Server as this machine reaches it, and as the containers do.
    [string] $SqlServer = 'localhost,14333',
    [string] $ContainerSqlServer = 'host.docker.internal,14333',
    # The throwaway container's password (CLAUDE.md, "Local SQL Server"): it protects nothing else.
    [string] $SqlPassword = 'Throwaway-container-2026!',
    [string] $Database = 'conformance',
    [int] $Port = 8089,
    [switch] $UpdateGolden,
    # Extra arguments for dotnet test (a filter, say).
    [string[]] $TestArguments = @()
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$work = Join-Path ([IO.Path]::GetTempPath()) 'sanction-and-forgive-conformance'
$feeds = Join-Path $work 'feeds'
$log = Join-Path $work 'api.log'
$name = 'sanction-and-forgive-conformance-api'
New-Item -ItemType Directory -Force $feeds | Out-Null

$hostConnection = "Server=$SqlServer;Database=$Database;User ID=sa;Password=$SqlPassword;TrustServerCertificate=True"
$containerConnection = "Server=$ContainerSqlServer;Database=$Database;User ID=sa;Password=$SqlPassword;TrustServerCertificate=True"

if ($Build) {
    docker build -f (Join-Path $root $Dockerfile) -t $Image --build-arg VERSION=conformance $root
    if ($LASTEXITCODE -ne 0) { throw 'docker build failed.' }
}

# A fresh database with the shared schema (the EF migrations are its source of truth for every implementation).
Push-Location $root
try {
    dotnet tool restore | Out-Null
    # dotnet ef needs the projects restored and built (a fresh checkout has neither).
    dotnet build src/SanctionAndForgive.Api -v q
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    $env:ConnectionStrings__Lists = $hostConnection
    dotnet ef database drop --force --project src/SanctionAndForgive.Data --startup-project src/SanctionAndForgive.Api
    dotnet ef database update --project src/SanctionAndForgive.Data --startup-project src/SanctionAndForgive.Api
    if ($LASTEXITCODE -ne 0) { throw 'The migrations failed.' }
}
finally {
    Pop-Location
}

docker rm -f $name 2>$null | Out-Null
$settings = @(
    '-e', "ConnectionStrings__Lists=$containerConnection",
    '-e', 'Lists__RefreshMinutes=0.05',
    '-e', 'RateLimiting__PerMinute=0',
    '-e', 'Admin__SignInEnabled=true',
    '-e', 'Screening__StreamMaxRecords=1000'
)
docker run -d --name $name --add-host=host.docker.internal:host-gateway -p "${Port}:8080" @settings $Image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The API container did not start.' }
$logs = Start-Process docker -ArgumentList 'logs', '-f', $name -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow

try {
    $deadline = (Get-Date).AddMinutes(2)
    do {
        Start-Sleep -Seconds 1
        try { $ready = (Invoke-WebRequest "http://localhost:$Port/readyz" -SkipHttpErrorCheck -TimeoutSec 5).StatusCode -eq 200 } catch { $ready = $false }
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw "The API isn't ready after 2 minutes: docker logs $name" }

    # The suite splits this on spaces, keeping "quoted parts" whole (the connection string has "User ID").
    $cli = "docker run --rm --add-host=host.docker.internal:host-gateway -e `"ConnectionStrings__Lists=$containerConnection`" -v `"${feeds}:/feeds`" " + $CliCommand.Replace('{image}', $Image)
    $env:CONFORMANCE_BASE_URL = "http://localhost:$Port"
    $env:CONFORMANCE_SQL = $hostConnection
    $env:CONFORMANCE_CLI = $cli
    $env:CONFORMANCE_FEEDS = $feeds
    $env:CONFORMANCE_CLI_FEEDS = '/feeds'
    $env:CONFORMANCE_API_LOG = "$log;$log.err"
    $env:CONFORMANCE_UPDATE_GOLDEN = if ($UpdateGolden) { '1' } else { '' }
    dotnet test (Join-Path $root 'tests/SanctionAndForgive.Conformance') --logger 'console;verbosity=normal' @TestArguments
    $result = $LASTEXITCODE
}
finally {
    Stop-Process -Id $logs.Id -ErrorAction SilentlyContinue
    docker rm -f $name 2>$null | Out-Null
}

exit $result
