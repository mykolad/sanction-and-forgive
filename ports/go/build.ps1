<#
.SYNOPSIS
  Builds the Go implementation: copies the shared dictionaries and pages from the .NET projects (their one source of
  truth), then vets, tests and builds. docs/conformance.md runs the result against the conformance suite.
#>
param([switch] $SkipTests)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
Push-Location $PSScriptRoot
try {
    Remove-Item -Recurse -Force matching/data, api/wwwroot -ErrorAction SilentlyContinue
    Copy-Item -Recurse (Join-Path $root 'src/SanctionAndForgive.Matching/Data') matching/data
    if (Test-Path api) { Copy-Item -Recurse (Join-Path $root 'src/SanctionAndForgive.Api/wwwroot') api/wwwroot }
    go vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'go vet failed.' }
    if (-not $SkipTests) {
        go test ./...
        if ($LASTEXITCODE -ne 0) { throw 'go test failed.' }
    }
    New-Item -ItemType Directory -Force bin | Out-Null
    go build -o bin/ ./cmd/...
    if ($LASTEXITCODE -ne 0) { throw 'go build failed.' }
}
finally {
    Pop-Location
}
