param(
    [int]$Port = 8080
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Find-Ngrok {
    $command = Get-Command ngrok -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $commonPaths = @(
        "C:\ngrok\ngrok.exe",
        "$env:USERPROFILE\Desktop\ngrok\ngrok.exe",
        "$env:USERPROFILE\ngrok\ngrok.exe",
        "$env:LOCALAPPDATA\ngrok\ngrok.exe"
    )

    foreach ($path in $commonPaths) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    return $null
}

function Get-NgrokHttpsUrl {
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $response = Invoke-RestMethod -Uri "http://127.0.0.1:4040/api/tunnels" -TimeoutSec 2
            $tunnel = $response.tunnels |
                Where-Object { $_.public_url -like "https://*" } |
                Select-Object -First 1

            if ($tunnel) {
                return $tunnel.public_url.TrimEnd("/")
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Get-NgrokApiSnapshot {
    try {
        return (Invoke-RestMethod -Uri "http://127.0.0.1:4040/api/tunnels" -TimeoutSec 2 |
            ConvertTo-Json -Depth 10)
    }
    catch {
        return "ngrok API недоступен: $($_.Exception.Message)"
    }
}

function Get-ConfiguredNgrokEndpoint($settingsPath) {
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return $null
    }

    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $url = [string]$settings.WebAppUrl

        if ([string]::IsNullOrWhiteSpace($url)) {
            return $null
        }

        $uri = [Uri]$url
        if ($uri.Host -notlike "*.ngrok-*") {
            return $null
        }

        return $uri.Host
    }
    catch {
        return $null
    }
}

function Clear-ProxyEnvironment {
    foreach ($name in @(
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "GIT_HTTP_PROXY",
        "GIT_HTTPS_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy"
    )) {
        Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
    }
}

function Stop-PreviousDevProcesses {
    Get-Process -Name "TelegramStudentBot", "ngrok" -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -ne $PID } |
        Stop-Process -Force
}

function Update-Settings($settingsPath, $webAppUrl, $port) {
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    }
    else {
        $settings = [ordered]@{
            BotToken = ""
            OpenRouterApiKey = ""
            WebAppPort = $port
            WebAppUrl = ""
            WebAppStopUrl = ""
        }
    }

    if (-not ($settings.PSObject.Properties.Name -contains "BotToken")) {
        $settings | Add-Member -NotePropertyName "BotToken" -NotePropertyValue ""
    }
    if (-not ($settings.PSObject.Properties.Name -contains "OpenRouterApiKey")) {
        $settings | Add-Member -NotePropertyName "OpenRouterApiKey" -NotePropertyValue ""
    }
    if (-not ($settings.PSObject.Properties.Name -contains "WebAppPort")) {
        $settings | Add-Member -NotePropertyName "WebAppPort" -NotePropertyValue $port
    }
    if (-not ($settings.PSObject.Properties.Name -contains "WebAppUrl")) {
        $settings | Add-Member -NotePropertyName "WebAppUrl" -NotePropertyValue ""
    }
    if (-not ($settings.PSObject.Properties.Name -contains "WebAppStopUrl")) {
        $settings | Add-Member -NotePropertyName "WebAppStopUrl" -NotePropertyValue ""
    }

    $settings.WebAppPort = $port
    $settings.WebAppUrl = $webAppUrl
    $settings.WebAppStopUrl = ""

    $settings |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $settingsPath -Encoding UTF8
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$settingsPath = Join-Path $repoRoot "appsettings.Development.json"
$ngrokLogPath = Join-Path $repoRoot "ngrok-dev.log"
$ngrokErrorPath = Join-Path $repoRoot "ngrok-dev.err.log"
$ngrokPath = Find-Ngrok

Stop-PreviousDevProcesses

if (-not $ngrokPath) {
    Write-Host ""
    Write-Host "ngrok не найден." -ForegroundColor Yellow
    Write-Host "Установи его один раз и положи ngrok.exe, например, в C:\ngrok\ngrok.exe"
    Write-Host "После этого запускай этот скрипт снова: .\run-dev.ps1"
    exit 1
}

Remove-Item -LiteralPath $ngrokLogPath, $ngrokErrorPath -Force -ErrorAction SilentlyContinue

$configuredEndpoint = Get-ConfiguredNgrokEndpoint -settingsPath $settingsPath
$ngrokArgs = @("http", "http://127.0.0.1:$Port", "--log=stdout")
if ($configuredEndpoint) {
    $ngrokArgs += "--url=$configuredEndpoint"
    Write-Host "Запускаю ngrok для порта $Port на домене $configuredEndpoint..." -ForegroundColor Cyan
}
else {
    Write-Host "Запускаю ngrok для порта $Port..." -ForegroundColor Cyan
}

Clear-ProxyEnvironment
$ngrokProcess = Start-Process `
    -FilePath $ngrokPath `
    -ArgumentList $ngrokArgs `
    -RedirectStandardOutput $ngrokLogPath `
    -RedirectStandardError $ngrokErrorPath `
    -PassThru `
    -WindowStyle Hidden

try {
    $publicUrl = Get-NgrokHttpsUrl
    if (-not $publicUrl) {
        $logText = ""
        if (Test-Path -LiteralPath $ngrokLogPath) {
            $logText += (Get-Content -LiteralPath $ngrokLogPath -Tail 20 -ErrorAction SilentlyContinue) -join "`n"
        }
        if (Test-Path -LiteralPath $ngrokErrorPath) {
            $errorText = (Get-Content -LiteralPath $ngrokErrorPath -Tail 20 -ErrorAction SilentlyContinue) -join "`n"
            if (-not [string]::IsNullOrWhiteSpace($errorText)) {
                $logText += "`n$errorText"
            }
        }

        if ([string]::IsNullOrWhiteSpace($logText)) {
            $logText = "Лог ngrok пуст. Попробуй запустить вручную: $ngrokPath http http://127.0.0.1:$Port"
        }

        $apiSnapshot = Get-NgrokApiSnapshot

        throw "Не удалось получить HTTPS URL из ngrok API на http://127.0.0.1:4040/api/tunnels`n`nОтвет ngrok API:`n$apiSnapshot`n`nПоследние строки ngrok:`n$logText"
    }

    Write-Host "Mini App URL: $publicUrl" -ForegroundColor Green
    Update-Settings -settingsPath $settingsPath -webAppUrl $publicUrl -port $Port
    Write-Host "Обновил appsettings.Development.json" -ForegroundColor Green
    Write-Host "Запускаю бота..." -ForegroundColor Cyan
    Write-Host ""

    dotnet run
}
finally {
    if ($ngrokProcess -and -not $ngrokProcess.HasExited) {
        Write-Host ""
        Write-Host "Останавливаю ngrok..." -ForegroundColor Cyan
        Stop-Process -Id $ngrokProcess.Id -Force
    }
}
