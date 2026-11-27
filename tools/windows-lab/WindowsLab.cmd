@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0WindowsLab.ps1" %*
exit /b %errorlevel%
