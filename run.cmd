@echo off
rem Запуск PartyApp единым приложением: сборка SPA + API на http://localhost:5000
rem Эквивалент: powershell -ExecutionPolicy Bypass -File run.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
if errorlevel 1 pause
