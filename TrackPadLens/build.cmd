@echo off
setlocal
set MODE=%1
if "%MODE%"=="" set MODE=Publish
powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Mode %MODE%
exit /b %ERRORLEVEL%
