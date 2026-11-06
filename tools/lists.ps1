<#
.SYNOPSIS
Manages the sanctions lists in the app's database, so you never need to remember dotnet arguments.

.DESCRIPTION
Runs SanctionAndForgive.Cli against the Azure SQL database as your `az login` (Entra ID only: no password), after
opening the SQL firewall for this machine's IP; the firewall rule is removed again afterwards, also on failure.
Set $env:ConnectionStrings__Lists first to use another database (a local SQL Server, say): then the firewall isn't
touched.

.EXAMPLE
./tools/lists.ps1 status                                  # sources, record counts, last import, age
./tools/lists.ps1 import -Source all                      # every active feed, or any single feed code
./tools/lists.ps1 import -Source eu -File .\eu.xml        # import a file already downloaded
./tools/lists.ps1 import -Source un -Force                # import even if unchanged, or past the sanity guard
./tools/lists.ps1 source-add -Code ee-mfa -Name "Estonia MFA national list"
./tools/lists.ps1 add -Source ee-mfa -File .\records.csv  # upsert by external_id
./tools/lists.ps1 remove -Source ee-mfa -ExternalId EE-12
./tools/lists.ps1 probe                                   # download and parse every feed; writes nothing
./tools/lists.ps1 load-inputs -File tests/load/inputs.csv # the load test's inputs, from the active lists
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('status', 'import', 'source-add', 'add', 'remove', 'probe', 'load-inputs')]
    [string] $Command,
    [string] $Source,
    [string] $File,
    [switch] $Force,
    [string] $Code,
    [string] $Name,
    [string] $ExternalId,
    [string] $ResourceGroup = 'sanction-and-forgive-rg',
    [string] $Server = 'sanction-and-forgive-sql',
    [string] $Database = 'sanction-and-forgive-db'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/SanctionAndForgive.Cli'

$cliArgs = @($Command)
if ($Source) { $cliArgs += @('--source', $Source) }
# Relative to PowerShell's current location (not the process's), and allowed not to exist yet (load-inputs writes it).
if ($File) { $cliArgs += @('--file', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($File)) }
if ($Force) { $cliArgs += '--force' }
if ($Code) { $cliArgs += @('--code', $Code) }
if ($Name) { $cliArgs += @('--name', $Name) }
if ($ExternalId) { $cliArgs += @('--external-id', $ExternalId) }

function Invoke-Cli {
    dotnet run --project $project -c Release -- @cliArgs
    if ($LASTEXITCODE -ne 0) { throw "The CLI exited with $LASTEXITCODE." }
}

if ($Command -eq 'probe' -or $env:ConnectionStrings__Lists) {
    Invoke-Cli
    return
}

$ip = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
$rule = 'lists-ps1-' + [Environment]::MachineName.ToLowerInvariant() + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
az sql server firewall-rule create -g $ResourceGroup -s $Server -n $rule --start-ip-address $ip --end-ip-address $ip -o none
if ($LASTEXITCODE -ne 0) { throw 'Could not open the SQL firewall (are you signed in with az login?).' }
try {
    $env:ConnectionStrings__Lists = "Server=tcp:$Server.database.windows.net,1433;Database=$Database;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60"
    Invoke-Cli
}
finally {
    Remove-Item Env:ConnectionStrings__Lists -ErrorAction SilentlyContinue
    az sql server firewall-rule delete -g $ResourceGroup -s $Server -n $rule -o none
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not remove the firewall rule '$rule' ($ip). Remove it by hand: az sql server firewall-rule delete -g $ResourceGroup -s $Server -n $rule"
    }
}
