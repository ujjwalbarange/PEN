@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Mode Installer
if errorlevel 1 exit /b %errorlevel%
