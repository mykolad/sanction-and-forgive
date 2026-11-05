# Infrastructure

Everything in Azure comes from the Bicep here, in one resource group, `sanction-and-forgive-rg` (Sweden Central),
reproducible from an empty group. Nothing here belongs to or touches lazydad.

| File | Scope | Deployed by | What |
|---|---|---|---|
| `bootstrap.bicep` | Subscription | The owner, once | Resource group; `sanction-and-forgive-github-cd` (the identity GitHub Actions deploys as), trusted from this repository's `production` environment only, Contributor on the group only |
| `platform.bicep` | Resource group | The deploy workflow (first time: by hand) | Log Analytics (30 days, daily cap), Container Apps environment `sanction-and-forgive-cae`, app identity `sanction-and-forgive-app`, SQL server `sanction-and-forgive-sql` (Entra-only), database `sanction-and-forgive-db` (Basic, 5 DTU, 2 GB), the Azure services firewall rule, a storage alert at 80% |
| `app.bicep` | Resource group | The deploy workflow | Container App and the daily import job (Phase 4) |
| `sql/grant-identities.sql` | Database | The owner, once (or after re-creating the database) | Database users for the app and deploy identities: what Bicep can't do |

Nothing personal lives in this public repository: the SQL admin's object ID and login and the alert email come from
environment variables (`platform.bicepparam`), set as repository variables for the workflows.

## Bootstrap (done 2026-11-06)

PowerShell 7, signed in with `az login` (Owner on the subscription) and `gh auth login`:

```powershell
# 1. Resource group, deploy identity, its GitHub trust and role
$prefix = gh api repos/mykolad/sanction-and-forgive/actions/oidc/customization/sub --jq .sub_claim_prefix
az deployment sub create --name sanction-and-forgive-bootstrap --location swedencentral `
  --template-file infra/bootstrap.bicep --parameters githubSubjectPrefix=$prefix

# 2. Platform (later deploys: the deploy workflow)
$env:SQL_ADMIN_OBJECT_ID = az ad signed-in-user show --query id -o tsv
$env:SQL_ADMIN_LOGIN = '<your sign-in name>'
$env:ALERT_EMAIL = '<where alerts go>'
az deployment group what-if -g sanction-and-forgive-rg --parameters infra/platform.bicepparam
az deployment group create  -g sanction-and-forgive-rg -n platform --parameters infra/platform.bicepparam

# 3. Database users (opens the firewall for your IP, runs the script as you, closes it)
./infra/sql/Invoke-Sql.ps1 -File infra/sql/grant-identities.sql
```

The GitHub subject prefix is the immutable form, `repo:<owner>@<owner id>/<repo>@<repo id>`, so a renamed or
re-created repository can't inherit the trust. It isn't a secret.

### GitHub settings

- **Repository variables** (no secrets anywhere): `AZURE_CLIENT_ID` (the deploy identity's client ID, a bootstrap
  output), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `SQL_ADMIN_OBJECT_ID`, `SQL_ADMIN_LOGIN`, `ALERT_EMAIL`.
- **Environment `production`**: deployments from `master` only. The deploy identity trusts only this environment.
- **Rulesets** on `master`: PR with `build-and-test` passing and up to date, review threads resolved; no force-push or
  deletion; Copilot code review on every PR and push.

## Demo-day checklist

1. `/api/v1/status`: every list `Current`, and today's date on the daily ones.
2. Run each "Try an example" button.
3. Run `ТОВ Ромашка`-style and `Шевченко Т.Г.`-style inputs against real entries chosen beforehand.
4. Confirm `rateLimitPerMinute` is back to normal after any load test.
