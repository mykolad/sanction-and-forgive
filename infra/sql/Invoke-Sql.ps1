<#
.SYNOPSIS
Runs a T-SQL file against the app's database as your `az login` (the server's Entra admin).

.DESCRIPTION
Opens the SQL firewall for this machine's public IP, runs the file in one batch with an Entra access token, and
closes the firewall again, also on failure. Needs PowerShell 7 (System.Data.SqlClient ships with it) and az.

.EXAMPLE
./infra/sql/Invoke-Sql.ps1 -File infra/sql/grant-identities.sql
#>
param(
    [Parameter(Mandatory)] [string] $File,
    [string] $ResourceGroup = 'sanction-and-forgive-rg',
    [string] $Server = 'sanction-and-forgive-sql',
    [string] $Database = 'sanction-and-forgive-db'
)

$ErrorActionPreference = 'Stop'
$sql = Get-Content -Raw -Path $File
$ip = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
$rule = 'invoke-sql-' + [Environment]::MachineName.ToLowerInvariant() + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)

az sql server firewall-rule create -g $ResourceGroup -s $Server -n $rule --start-ip-address $ip --end-ip-address $ip -o none
if ($LASTEXITCODE -ne 0) { throw 'Could not open the SQL firewall.' }
try {
    $token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0) { throw 'Could not get a database token from az.' }

    $connection = [System.Data.SqlClient.SqlConnection]::new(
        "Server=tcp:$Server.database.windows.net,1433;Database=$Database;Encrypt=True;Connect Timeout=60")
    $connection.AccessToken = $token
    # A new firewall rule can take a few seconds to apply.
    for ($attempt = 1; ; $attempt++) {
        try { $connection.Open(); break }
        catch { if ($attempt -ge 6) { throw }; Start-Sleep -Seconds 10 }
    }
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        [void] $command.ExecuteNonQuery()
        Write-Host "Ran $File on $Database."
    }
    finally { $connection.Dispose() }
}
finally {
    # A native command's exit code isn't an exception, even with ErrorActionPreference Stop. Warn rather than throw
    # here, so a failure above keeps its own error, and fail the script below.
    az sql server firewall-rule delete -g $ResourceGroup -s $Server -n $rule -o none
    $cleanupFailed = $LASTEXITCODE -ne 0
    if ($cleanupFailed) {
        Write-Warning "Could not remove the firewall rule '$rule' ($ip). Remove it by hand: az sql server firewall-rule delete -g $ResourceGroup -s $Server -n $rule"
    }
}
if ($cleanupFailed) { exit 1 }
