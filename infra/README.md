# Infrastructure

Everything in Azure comes from the Bicep here, in one resource group, `sanction-and-forgive-rg` (Sweden Central),
reproducible from an empty group. Nothing here belongs to or touches lazydad.

| File | Scope | Deployed by | What |
|---|---|---|---|
| `bootstrap.bicep` | Subscription | The owner, once | Resource group; `sanction-and-forgive-github-cd` (the identity GitHub Actions deploys as), trusted from this repository's `production` environment only, Contributor on the group only |
| `platform.bicep` | Resource group | The deploy workflow (first time: by hand) | Log Analytics (30 days, daily cap), Container Apps environment `sanction-and-forgive-cae`, app identity `sanction-and-forgive-app`, SQL server `sanction-and-forgive-sql` (Entra-only), database `sanction-and-forgive-db` (Basic, 5 DTU, 2 GB), the Azure services firewall rule; alerts to the subscription's Owner role: database storage above 80%, a failed or rejected import, and a $20 monthly budget (80% spent, 100% forecast) |
| `app.bicep` | Resource group | The deploy workflow | Container App `sanction-and-forgive` (0.5 vCPU / 1 GiB since 2026-11-07, min = max = 1, port 8080, liveness `/healthz`, readiness `/readyz`, public ingress unless `allowedCidrs`) and the job `sanction-and-forgive-import` (daily 03:30 UTC, `import --source all`, 0.5 vCPU / 1 GiB, 30 minutes), both from `ghcr.io/mykolad/sanction-and-forgive:<sha>` as identity `sanction-and-forgive-app` |
| `sql/grant-identities.sql` | Database | The owner, once (or after re-creating the database) | Database users for the app and deploy identities: what Bicep can't do |
| `admin-sign-in.ps1` | Tenant | The owner, once | The admin sign-in's Entra app registration (role `Lists.Admin`, assignment required, assigned to the owner); `app.bicep` then turns the Container App's built-in sign-in on |

Nothing personal lives in this public repository: the SQL admin's object ID and login come from environment variables
(`platform.bicepparam`), set as repository variables for the workflows. Alerts go to whoever holds Owner directly on the
subscription, so no email address is stored anywhere.

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
az deployment group what-if -g sanction-and-forgive-rg --parameters infra/platform.bicepparam
az deployment group create  -g sanction-and-forgive-rg -n platform --parameters infra/platform.bicepparam

# 3. Database users (opens the firewall for your IP, runs the script as you, closes it)
./infra/sql/Invoke-Sql.ps1 -File infra/sql/grant-identities.sql
```

The GitHub subject prefix is the immutable form, `repo:<owner>@<owner id>/<repo>@<repo id>`, so a renamed or
re-created repository can't inherit the trust. It isn't a secret.

### GitHub settings

- **Repository variables** (no secrets anywhere): `AZURE_CLIENT_ID` (the deploy identity's client ID, a bootstrap
  output), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `SQL_ADMIN_OBJECT_ID`, `SQL_ADMIN_LOGIN`, and (optional)
  `ADMIN_CLIENT_ID`, the admin sign-in's app registration.
- **Environment `production`**: deployments from `master` only. The deploy identity trusts only this environment.
- **Rulesets** on `master`: PR with `build-and-test` passing and up to date, review threads resolved; no force-push or
  deletion; Copilot code review on every PR and push.

## Admin sign-in (the owner, once)

The admin page (`/admin`: confirm or reject updates held for review) uses Container Apps' built-in Microsoft Entra ID
sign-in. Screening never needs it. The app registration lives in the tenant, outside the resource group, so the owner
creates it:

```powershell
./infra/admin-sign-in.ps1                        # creates (or updates) it and gives you the Lists.Admin role
gh variable set ADMIN_CLIENT_ID --body <app id>  # as printed; the next deploy turns sign-in on
gh workflow run deploy.yml                       # or wait for the next push to master
```

The deploy turns the app's trust in the principal header on only after it has checked that the Container App's
auth config is in place for that client id (`ADMIN_SIGN_IN_READY`, checked on every deploy): the first deploy creates
the auth config with the app still ignoring the header, then deploys again with it on. A failed provisioning never
leaves the app trusting a header anyone could send. If the auth config is ever removed by hand, deploy again (or set
`ADMIN_CLIENT_ID` empty) so the app stops trusting it too.

Until `ADMIN_CLIENT_ID` is set, sign-in is off and the app ignores any principal header: the admin page says so and
held updates are reviewed with `tools/lists.ps1 review|confirm|reject`. Others get the role in the Entra admin center
(Enterprise applications, the registration, Users and groups). No client secret: the ID token carries the role.

## Deploying

`.github/workflows/deploy.yml` (**Deploy**) runs after Build and Test passes on a push to `master`, or by hand. By
hand, its input **implementation** chooses what runs: `dotnet` (the reference, the default, and always after a push),
`go` or `rust` (the ports, `ports/go` and `ports/rust`; docs/conformance.md):

```powershell
gh workflow run deploy.yml --ref master -f implementation=go   # and -f implementation=dotnet to go back
```

1. **build**: the image `ghcr.io/mykolad/sanction-and-forgive:<sha>` (a port's: the same package's tag
   `go-<sha>` or `rust-<sha>`, version `go-<short sha>` or `rust-<short sha>`; version metadata baked in) with the job's own token, and the EF migration
   bundle (the schema is the same for both).
2. **deploy** (environment `production`, OIDC): `platform.bicep` what-if (logged), then deployed; the SQL firewall
   opened for the runner, the migration bundle as the deploy identity, the firewall closed (always); `app.bicep`
   with the commit's image; the smoke tests (`tests/SanctionAndForgive.SmokeTests`) against the new revision.

**The image must be public** (no registry, no pull role, no secret). GitHub creates the package private on the first
push: once, open https://github.com/users/mykolad/packages/container/sanction-and-forgive/settings, *Change visibility*
to Public, then re-run the deploy. Nothing in the image is secret: it's this public repository's code.

**First deploy:** the app starts with no lists (every row Not checked). Run the import once instead of waiting for 03:30:

```powershell
az containerapp job start -g sanction-and-forgive-rg -n sanction-and-forgive-import
az containerapp job execution list -g sanction-and-forgive-rg -n sanction-and-forgive-import -o table
```

**Load tests** (Phase 5): deploy with the rate limit off, then deploy again without it. `app.bicepparam` also reads
`APP_IMPLEMENTATION` (`dotnet`, `go` or `rust`; a port's image tag `<implementation>-<sha>` must exist), `APP_CPU` and `APP_MEMORY` (0.5 and
1Gi by default) for a run at another size:

```powershell
$env:IMAGE_TAG = '<the running sha>'; $env:RATE_LIMIT_PER_MINUTE = '0'
# $env:APP_IMPLEMENTATION = 'go'; $env:APP_CPU = '0.25'; $env:APP_MEMORY = '0.5Gi'
az deployment group create -g sanction-and-forgive-rg -n app --parameters infra/app.bicepparam
Remove-Item Env:RATE_LIMIT_PER_MINUTE, Env:APP_IMPLEMENTATION, Env:APP_CPU, Env:APP_MEMORY -ErrorAction Ignore   # then the same create again
```

**What-if on a deployed environment** lists a few changes that aren't changes, a what-if limitation: values read from
other resources at deploy time (the connection string, the identity's client ID, the Log Analytics ID) show as
`[reference(…)]` against their current values, and the read-only `runningStatus` as removed. Anything else in a
what-if is a real change. Bicep files are LF everywhere (`.gitattributes`): the alert query is a multi-line
string, and a CRLF checkout would deploy a different one from CI's.

## Demo-day checklist

1. `/api/v1/status`: every list `Current`, and today's date on the daily ones.
2. Run each "Try an example" button.
3. Run `ТОВ Ромашка`-style and `Шевченко Т.Г.`-style inputs against real entries chosen beforehand.
4. Confirm `rateLimitPerMinute` is back to normal after any load test.
