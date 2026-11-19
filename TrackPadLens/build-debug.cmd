@echo off
setlocal
echo === Building TrackPadLens (Debug x64 with PDB Symbols) ===
powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Mode Debug
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Debug build failed. Existing root TrackPadLens.exe was left untouched.
    exit /b %ERRORLEVEL%
)
echo.
echo [OK] Debug executable ready at: %~dp0TrackPadLens.exe
echo      Debug symbols (PDB) ready at: %~dp0TrackPadLens.pdb
echo      To launch directly: TrackPadLens.exe
exit /b 0
