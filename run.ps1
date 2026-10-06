# run.ps1 — собирает SPA и запускает PartyApp единым приложением на http://localhost:5000
# Использование: powershell -ExecutionPolicy Bypass -File run.ps1 [-NoBuild]
# Двойной клик по run.cmd делает то же самое.

[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$webDir = Join-Path $root 'web'
$apiProject = Join-Path $root 'src\PartyApp.Api'

function Find-CommandPath {
    param([string[]]$Names)

    foreach ($name in $Names) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }

    return $null
}

# npm.cmd предпочтительнее npm.ps1: вызов не зависит от ExecutionPolicy
$npm = Find-CommandPath -Names @('npm.cmd', 'npm')
$dotnet = Find-CommandPath -Names @('dotnet')

if (-not $dotnet) {
    Write-Host 'Ошибка: не найден dotnet. Установите .NET SDK 9 (версия зафиксирована в global.json).' -ForegroundColor Red
    exit 1
}

if (-not $npm) {
    Write-Host 'Ошибка: не найден npm. Установите Node.js 22+.' -ForegroundColor Red
    exit 1
}

Push-Location $root
try {
    if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
        Write-Host '==> Установка зависимостей фронтенда (npm ci)...' -ForegroundColor Cyan
        Push-Location $webDir
        try {
            & $npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci завершился с кодом $LASTEXITCODE" }
        }
        finally {
            Pop-Location
        }
    }

    if (-not $NoBuild) {
        Write-Host '==> Сборка SPA (npm run build -> src\PartyApp.Api\wwwroot)...' -ForegroundColor Cyan
        Push-Location $webDir
        try {
            & $npm run build
            if ($LASTEXITCODE -ne 0) { throw "npm run build завершился с кодом $LASTEXITCODE" }
        }
        finally {
            Pop-Location
        }
    }
    else {
        Write-Host '==> Сборка SPA пропущена (-NoBuild).' -ForegroundColor DarkGray
    }

    Write-Host ''
    Write-Host '==> Запуск PartyApp на http://localhost:5000' -ForegroundColor Green
    Write-Host '    Игрок:      http://localhost:5000/' -ForegroundColor Gray
    Write-Host '    Админка:    http://localhost:5000/admin' -ForegroundColor Gray
    Write-Host '    Экран (TV): http://localhost:5000/screen' -ForegroundColor Gray
    Write-Host '    Swagger:    http://localhost:5000/swagger' -ForegroundColor Gray
    Write-Host '    Остановка:  Ctrl+C' -ForegroundColor Gray
    Write-Host ''

    & $dotnet run --project $apiProject
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
