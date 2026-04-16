@echo off
setlocal
powershell -ExecutionPolicy Bypass -File "%~dp0start-codex-contexthub.ps1" %*
exit /b %ERRORLEVEL%
