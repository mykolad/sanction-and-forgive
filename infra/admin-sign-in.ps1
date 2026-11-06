<#
.SYNOPSIS
Sets up the admin sign-in once: a Microsoft Entra app registration with a "Lists administrator" role, assigned to you.

.DESCRIPTION
The owner runs this (it writes to the tenant, outside sanction-and-forgive-rg), signed in with `az login` as an
account that may create app registrations. It's safe to run again: it finds the registration by name and only adds
what's missing.

It creates:
- an app registration for this tenant only, with the redirect URI of the deployed app's built-in sign-in and ID tokens
  on (no client secret: the ID token carries the role);
- the app role Lists.Admin, which the admin page requires for reviewing updates;
- its service principal with "assignment required", so only accounts given the role can sign in at all;
- the role, assigned to you.

Then set the repository variable it prints; the next deploy turns sign-in on (infra/app.bicep).

.EXAMPLE
./infra/admin-sign-in.ps1
#>
param(
    [string] $ResourceGroup = 'sanction-and-forgive-rg',
    [string] $App = 'sanction-and-forgive',
    [string] $DisplayName = 'sanction-and-forgive admin sign-in'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$fqdn = az containerapp show -g $ResourceGroup -n $App --query properties.configuration.ingress.fqdn -o tsv
$redirect = "https://$fqdn/.auth/login/aad/callback"
Write-Host "App: https://$fqdn (sign-in callback $redirect)"

$appId = az ad app list --display-name $DisplayName --query '[0].appId' -o tsv
if (-not $appId) {
    $roles = Join-Path ([IO.Path]::GetTempPath()) "admin-sign-in-roles-$([guid]::NewGuid()).json"
    ConvertTo-Json -Depth 5 -InputObject @(
        [ordered]@{
            allowedMemberTypes = @('User')
            description        = 'Confirms or rejects list updates held for review, and edits the allow-list.'
            displayName        = 'Lists administrator'
            id                 = [guid]::NewGuid().ToString()
            isEnabled          = $true
            value              = 'Lists.Admin'
        }) | Set-Content -Path $roles -Encoding utf8
    try {
        $appId = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg `
            --web-redirect-uris $redirect --enable-id-token-issuance true --app-roles "@$roles" --query appId -o tsv
    }
    finally {
        Remove-Item $roles -ErrorAction SilentlyContinue
    }
    Write-Host "Created the app registration $appId."
}
else {
    # Already there: make sure the redirect URI and ID tokens are as needed (the app's address may have changed).
    az ad app update --id $appId --web-redirect-uris $redirect --enable-id-token-issuance true
    Write-Host "Found the app registration $appId."
}

$roleId = az ad app show --id $appId --query "appRoles[?value=='Lists.Admin'].id | [0]" -o tsv
if (-not $roleId) { throw "The app registration $appId has no Lists.Admin role; delete it and run this again." }

$spId = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
if (-not $spId) {
    $spId = az ad sp create --id $appId --query id -o tsv
}
az ad sp update --id $spId --set appRoleAssignmentRequired=true

$me = az ad signed-in-user show --query id -o tsv
$assigned = az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo" `
    --query "length(value[?principalId=='$me' && appRoleId=='$roleId'])" -o tsv
if ($assigned -eq '0') {
    $body = Join-Path ([IO.Path]::GetTempPath()) "admin-sign-in-assignment-$([guid]::NewGuid()).json"
    ConvertTo-Json -InputObject ([ordered]@{ principalId = $me; resourceId = $spId; appRoleId = $roleId }) | Set-Content -Path $body -Encoding utf8
    try {
        az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo" `
            --headers 'Content-Type=application/json' --body "@$body" -o none
    }
    finally {
        Remove-Item $body -ErrorAction SilentlyContinue
    }
    Write-Host 'Gave you the Lists administrator role.'
}
else {
    Write-Host 'You already have the Lists administrator role.'
}

Write-Host ''
Write-Host 'Last step: set the repository variable, then deploy (or re-run the last deploy):'
Write-Host "  gh variable set ADMIN_CLIENT_ID --body $appId"
Write-Host 'To give someone else the role: Entra admin center > Enterprise applications >'
Write-Host "  '$DisplayName' > Users and groups > Add user/group."
