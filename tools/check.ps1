param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$IncludeUi
)

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $workspacePath
try {
    dotnet restore Kino.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet build Kino.slnx -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    & (Join-Path $workspacePath "tests\Kino.Tests\bin\$Configuration\net48\Kino.Tests.exe")
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    if ($IncludeUi) {
        & (Join-Path $workspacePath "tests\Kino.UiSmoke\bin\$Configuration\net48\Kino.UiSmoke.exe") (Join-Path $workspacePath 'artifacts\previews')
        if ($LASTEXITCODE -ne 0) { throw 'UI smoke checks failed.' }
    }
    Write-Output ('PASS build and core tests' + $(if ($IncludeUi) { ', plus both UI themes.' } else { '.' }))
} finally {
    Pop-Location
}
