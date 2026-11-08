param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $workspacePath 'src\Kino\Kino.csproj'
dotnet build $projectPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$projectXml = [xml][System.IO.File]::ReadAllText($projectPath, [System.Text.Encoding]::UTF8)
$version = [string]$projectXml.Project.PropertyGroup.Version
$outputPath = Join-Path $workspacePath "artifacts\KINO-$version-windows"
$buildPath = Join-Path $workspacePath "src\Kino\bin\$Configuration\net48"
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
foreach ($name in @('KINO.exe', 'KINO.exe.config')) {
    $sourcePath = Join-Path $buildPath $name
    $destinationPath = Join-Path $outputPath $name
    if ((Test-Path -LiteralPath $destinationPath) -and
        (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -eq
        (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash) {
        continue
    }
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}
# SkiaSharp and HarfBuzz load native DLLs from their architecture subdirectory.
# Managed dependencies remain embedded by Costura; do not copy macOS assets.
foreach ($architecture in @('x86', 'x64', 'arm64')) {
    $nativeOutputPath = Join-Path $outputPath $architecture
    New-Item -ItemType Directory -Path $nativeOutputPath -Force | Out-Null
    foreach ($name in @('libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
        $sourcePath = Join-Path (Join-Path $buildPath $architecture) $name
        $destinationPath = Join-Path $nativeOutputPath $name
        if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Missing native chart dependency: $sourcePath" }
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash) {
            throw "Native dependency copy verification failed: $destinationPath"
        }
    }
}
$readmeOutputPath = Join-Path $outputPath 'README.md'
Copy-Item -LiteralPath (Join-Path $workspacePath 'README.md') -Destination $readmeOutputPath -Force
if (Test-Path -LiteralPath (Join-Path $workspacePath 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $workspacePath 'LICENSE') -Destination $outputPath -Force
}
Copy-Item -LiteralPath (Join-Path $workspacePath 'licenses') -Destination $outputPath -Recurse -Force
$imagesOutputPath = Join-Path $outputPath 'docs'
New-Item -ItemType Directory -Path $imagesOutputPath -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspacePath 'docs\images') -Destination $imagesOutputPath -Recurse -Force
# Reusing a version's output folder must not retain removed documentation.
foreach ($document in @(Get-ChildItem -LiteralPath $outputPath -File -Filter '*.md' -Recurse)) {
    if ($document.FullName -ne $readmeOutputPath) { Remove-Item -LiteralPath $document.FullName -Force }
}
$archivePath = "$outputPath.zip"
Compress-Archive -LiteralPath $outputPath -DestinationPath $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText("$archivePath.sha256", "$hash  $([System.IO.Path]::GetFileName($archivePath))`n", [System.Text.UTF8Encoding]::new($false))
Write-Output $outputPath
Write-Output $archivePath
