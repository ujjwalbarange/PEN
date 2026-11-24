<#
.SYNOPSIS
    Build, debug-stage, test, and package TrackPadLens for Windows x64 (.NET 8).
.EXAMPLE
    .\build.ps1 -Mode Debug     # Builds Debug x64 with PDB symbols and stages runnable TrackPadLens.exe in root
    .\build.ps1 -Mode Release   # Builds Release x64 and stages runnable TrackPadLens.exe in root
    .\build.ps1 -Mode Publish   # Publishes self-contained single-file win-x64 TrackPadLens.exe to publish\ and root
    .\build.ps1 -Mode Installer # Publishes self-contained win-x64 payload and builds dist\TrackPadLens-Setup-x64.exe (Inno Setup 6)
    .\build.ps1 -Mode Test      # Runs the full xUnit test suite
#>
param(
    [ValidateSet("Debug", "Release", "Publish", "Installer", "Test")]
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

function Resolve-BuildOutputDir([string]$Configuration) {
    $Candidates = @(
        (Join-Path $ProjectRoot "bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64"),
        (Join-Path $ProjectRoot "bin\$Configuration\net8.0-windows10.0.19041.0\win-x64"),
        (Join-Path $ProjectRoot "bin\x64\$Configuration\net8.0-windows\win-x64"),
        (Join-Path $ProjectRoot "bin\$Configuration\net8.0-windows\win-x64")
    )
    foreach ($dir in $Candidates) {
        if (Test-Path (Join-Path $dir "TrackPadLens.exe")) {
            return $dir
        }
    }
    return $Candidates[0]
}

if ($Mode -eq "Debug") {
    dotnet build $Csproj -c Debug -p:Platform=x64 -r win-x64 --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Debug build failed. Existing root executable was not modified."
        exit $LASTEXITCODE
    }
    $OutDir = Resolve-BuildOutputDir "Debug"
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
    $OutDir = Resolve-BuildOutputDir "Release"
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
    exit 0
}

if ($Mode -eq "Installer") {
    $PayloadDir = Join-Path $ProjectRoot "publish-installer"
    $DistDir = Join-Path $ProjectRoot "dist"
    $IssScript = Join-Path $ProjectRoot "Installer\TrackPadLens.iss"

    if (Test-Path $PayloadDir) { Remove-Item $PayloadDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $PayloadDir | Out-Null
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

    Write-Host "Publishing self-contained win-x64 application payload..." -ForegroundColor Cyan
    dotnet publish $Csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -o $PayloadDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Self-contained publish for installer failed."
        exit $LASTEXITCODE
    }

    $IsccCandidates = @(
        (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }

    $Iscc = $IsccCandidates | Select-Object -First 1
    if (-not $Iscc) {
        Write-Error "Inno Setup 6 (ISCC.exe) was not found. Install it once via: winget install JRSoftware.InnoSetup"
        exit 1
    }

    [xml]$projXml = Get-Content $Csproj
    $AppVersion = ($projXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $AppVersion) { $AppVersion = "1.0.0" }

    Write-Host "Compiling Windows Setup Installer (v$AppVersion) using $Iscc..." -ForegroundColor Cyan
    & $Iscc "/DAppVersion=$AppVersion" "/DPayloadDir=$PayloadDir" "/DOutputDir=$DistDir" $IssScript
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup compilation failed."
        exit $LASTEXITCODE
    }

    $SetupExe = Join-Path $DistDir "TrackPadLens-Setup-x64.exe"
    if (-not (Test-Path $SetupExe)) {
        Write-Error "Expected installer not found at $SetupExe"
        exit 1
    }
    Write-Host "Windows Setup Installer ready: $SetupExe" -ForegroundColor Green
}
