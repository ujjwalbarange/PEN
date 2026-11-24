$Root = Split-Path -Parent $PSScriptRoot
& (Join-Path $Root "build.ps1") -Mode Release
exit $LASTEXITCODE
