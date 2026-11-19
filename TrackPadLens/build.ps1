<#
.SYNOPSIS
    Build, debug-stage, test, and package TrackPadLens for Windows x64 (.NET 8).
.EXAMPLE
    .\build.ps1 -Mode Debug     # Builds Debug x64 with PDB symbols and stages runnable TrackPadLens.exe in root
    .\build.ps1 -Mode Release   # Builds Release x64 and stages runnable TrackPadLens.exe in root
    .\build.ps1 -Mode Publish   # Publishes self-contained single-file win-x64 TrackPadLens.exe to publish\ and root
    .\build.ps1 -Mode Test      # Runs the full xUnit test suite
#>
param(
    [ValidateSet("Debug", "Release", "Publish", "Test")]
    [string]$Mode = "Debug"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csproj = Join-Path $ProjectRoot "TrackPadLens.csproj"
$TestCsproj = Join-Path $ProjectRoot "Tests\TrackPadLens.Tests.csproj"
$RootExe = Join-Path $ProjectRoot "TrackPadLens.exe"

Write-Host "=== TrackPadLens Build ($Mode) ===" -ForegroundColor Cyan

function Stage-FrameworkDependentToRoot([string]$OutDir) {
    $BuiltExe = Join-Path $OutDir "TrackPadLens.exe"
    if (-not (Test-Path $BuiltExe)) {
        Write-Error "Expected executable not found at $BuiltExe"
        exit 1
    }

    # Copy apphost + managed assembly + runtimeconfig + deps + PDB symbols so root TrackPadLens.exe launches & debugs cleanly
    $RequiredArtifacts = @(
        "TrackPadLens.exe",
        "TrackPadLens.dll",
        "TrackPadLens.deps.json",
        "TrackPadLens.runtimeconfig.json",
        "TrackPadLens.pdb"
    )

    foreach ($artifact in $RequiredArtifacts) {
        $src = Join-Path $OutDir $artifact
        if (Test-Path $src) {
            Copy-Item -Path $src -Destination (Join-Path $ProjectRoot $artifact) -Force
        }
    }
}

if ($Mode -eq "Test") {
    dotnet test $TestCsproj -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "All unit tests passed." -ForegroundColor Green
    exit 0
}

if ($Mode -eq "Debug") {
    dotnet build $Csproj -c Debug -p:Platform=x64 -r win-x64 --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Debug build failed. Existing root executable was not modified."
        exit $LASTEXITCODE
    }
    $OutDir = Join-Path $ProjectRoot "bin\x64\Debug\net8.0-windows\win-x64"
    Stage-FrameworkDependentToRoot $OutDir
    Write-Host "Debug build ready and staged at root: $RootExe (with TrackPadLens.pdb)" -ForegroundColor Green
    exit 0
}

if ($Mode -eq "Release") {
    dotnet build $Csproj -c Release -p:Platform=x64 -r win-x64 --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Release build failed. Existing root executable was not modified."
        exit $LASTEXITCODE
    }
    $OutDir = Join-Path $ProjectRoot "bin\x64\Release\net8.0-windows\win-x64"
    Stage-FrameworkDependentToRoot $OutDir
    Write-Host "Release build ready and staged at root: $RootExe" -ForegroundColor Green
    exit 0
}

if ($Mode -eq "Publish") {
    $PublishDir = Join-Path $ProjectRoot "publish"
    dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Standalone publish failed. Existing root executable was not modified."
        exit $LASTEXITCODE
    }

    $PublishedExe = Join-Path $PublishDir "TrackPadLens.exe"
    if (Test-Path $PublishedExe) {
        # Remove stale framework-dependent sidecars if upgrading root to standalone single-file exe
        foreach ($sidecar in @("TrackPadLens.dll", "TrackPadLens.deps.json", "TrackPadLens.runtimeconfig.json", "TrackPadLens.pdb")) {
            $p = Join-Path $ProjectRoot $sidecar
            if (Test-Path $p) { Remove-Item $p -Force }
        }
        Copy-Item -Path $PublishedExe -Destination $RootExe -Force
        Write-Host "Standalone single-file executable copied to root: $RootExe" -ForegroundColor Green
    } else {
        Write-Error "Expected published executable not found at $PublishedExe"
        exit 1
    }
}
