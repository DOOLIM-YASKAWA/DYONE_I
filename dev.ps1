# DY ONE 내부용 개발 환경 실행 — 한 번에 켜고, 코드를 고치면 알아서 다시 시작한다
#   .\dev.ps1            API + Worker + 웹 개발 서버 + 연계 프로그램(SmartLab · SRS Analyzer · 공정 관리)
#   .\dev.ps1 -Desktop   위 + Windows 앱
#   .\dev.ps1 -NoApps    연계 프로그램은 켜지 않음
#   .\dev.ps1 -AppsOnly  연계 프로그램만 켜기(API · Worker는 건드리지 않음)
#   .\dev.ps1 -Stop      API · Worker · Windows 앱 · 연계 프로그램 끄기
# API·Worker는 dotnet watch로 띄운다: 소스를 저장하면 자동으로 다시 빌드·재시작한다(창을 닫고 다시 켤 필요 없음).
# 이미 켜져 있는 API·Worker·Windows 앱은 먼저 끈다(실행 파일이 잠겨 빌드가 실패하는 것을 막는다).
# 웹 개발 서버와 연계 프로그램은 켜져 있으면 그대로 둔다(웹은 응답이 없으면 다시 켠다).
# 연계 프로그램은 이 저장소 옆 폴더(E:\dy-one-smartlab, dy-one-srs-analyzer, dy-one-process)에 있을 때만 켠다.
# DB: 기존 dyone DB(Docker 컨테이너 dyone_db, PostgreSQL 18, 포트 5433). 연결 정보는 user-secrets.
# 이 파일은 UTF-8(BOM)로 저장한다 — Windows PowerShell 5에서도 한글이 깨지지 않게.
param([switch]$Desktop, [switch]$Stop, [switch]$NoApps, [switch]$AppsOnly)

$root = $PSScriptRoot
$parent = Split-Path $root -Parent

function Stop-DyOne([switch]$WithApps) {
    # DY ONE 프로세스만 고른다: 실행 파일 이름, 또는 명령줄에 이 저장소의 API·Worker·Windows 앱 프로젝트가 들어 있는 dotnet
    $targets = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in 'DyOne.Api.exe', 'DyOne.Worker.exe', 'DyOneDesktop.exe' -or
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'DyOne\.(Api|Worker)|dy-one-internal\\desktop') -or
        ($WithApps -and $_.Name -eq 'node.exe' -and $_.CommandLine -match 'dy-one-(smartlab|srs-analyzer|process)')
    }
    foreach ($p in $targets) {
        Write-Host "  끄기: $($p.Name) ($($p.ProcessId))" -ForegroundColor DarkGray
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Start-Window($title, $dir, $command) {
    Start-Process pwsh -ArgumentList '-NoExit', '-Command', "`$Host.UI.RawUI.WindowTitle = '$title'; Set-Location '$dir'; $command"
}

function Test-Port($port) {
    [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

function Start-LinkedApps {
    # SSO 공유 시크릿(개발값)은 API 설정(appsettings.Development.json)과 같은 값을 쓴다
    $cfg = Get-Content "$root\server\src\DyOne.Api\appsettings.Development.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    $apps = @(
        @{ Name = 'SmartLab'; Dir = "$parent\dy-one-smartlab"; WebPort = 4174; ApiPort = 4000; Secret = $cfg.SmartLabSso.Secret
           Api = "Start-Process node -ArgumentList 'index.js' -WorkingDirectory server -NoNewWindow" },
        @{ Name = 'SRS Analyzer'; Dir = "$parent\dy-one-srs-analyzer"; WebPort = 4175; ApiPort = 4101; Secret = $cfg.SrsAnalyzerSso.Secret
           Api = "Start-Process node -ArgumentList 'server/index.js' -NoNewWindow" },
        @{ Name = '공정 관리'; Dir = "$parent\dy-one-process"; WebPort = 4176; ApiPort = $null; Secret = $null; Api = $null }
    )
    foreach ($a in $apps) {
        if (-not (Test-Path $a.Dir)) { Write-Host "  $($a.Name): 폴더 없음($($a.Dir)) — 건너뜀" -ForegroundColor DarkGray; continue }
        $parts = @()
        if ($a.Secret) { $parts += "`$env:DYONE_SSO_SECRET = '$($a.Secret)'" }
        if ($a.Api -and -not (Test-Port $a.ApiPort)) { $parts += $a.Api }
        if (-not (Test-Port $a.WebPort)) { $parts += 'npm run dev' }
        if ($parts.Count -le [int][bool]$a.Secret) {
            Write-Host "  $($a.Name)는 이미 실행 중 (http://localhost:$($a.WebPort))" -ForegroundColor DarkGray
            continue
        }
        Write-Host "$($a.Name) (http://localhost:$($a.WebPort))" -ForegroundColor Cyan
        Start-Window "DY ONE · $($a.Name)" $a.Dir ($parts -join '; ')
    }
}

if (-not $AppsOnly) {
    Write-Host "실행 중인 DY ONE API · Worker · Windows 앱 정리" -ForegroundColor Cyan
    Stop-DyOne -WithApps:$Stop
    if ($Stop) {
        Write-Host "모두 껐습니다. (웹 개발 서버 창은 직접 닫으세요)" -ForegroundColor Green
        return
    }
}

# DB(Docker dyone_db)가 없으면 API·Worker·연계 프로그램이 동작하지 않으므로 먼저 확인한다. Docker Desktop이 꺼져 있으면 켜고 기다린다.
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker Desktop이 꺼져 있습니다. 켜는 중... (최대 2분)" -ForegroundColor Yellow
    Start-Process "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"
    $deadline = (Get-Date).AddMinutes(2)
    do {
        Start-Sleep -Seconds 3
        docker info *> $null
    } while ($LASTEXITCODE -ne 0 -and (Get-Date) -lt $deadline)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Docker Desktop이 켜지지 않았습니다. Docker Desktop을 직접 실행한 뒤 다시 .\dev.ps1 을 실행하세요." -ForegroundColor Red
        return
    }
}
$state = docker inspect -f '{{.State.Running}}' dyone_db 2>$null
if ($state -ne 'true') {
    Write-Host "dyone_db 컨테이너를 시작합니다." -ForegroundColor Yellow
    docker start dyone_db | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "dyone_db를 시작하지 못했습니다. Docker Desktop에서 컨테이너 상태를 확인하세요." -ForegroundColor Red
        return
    }
}

if ($AppsOnly) {
    Start-LinkedApps
    return
}

Write-Host "API 서버 (http://localhost:5080) — 코드 저장 시 자동 재시작" -ForegroundColor Cyan
Start-Window 'DY ONE API' "$root\server" 'dotnet watch run --non-interactive --project src/DyOne.Api --launch-profile http'

Write-Host "Worker (그룹웨어 동기화 · 변경 알림) — 코드 저장 시 자동 재시작" -ForegroundColor Cyan
Start-Window 'DY ONE Worker' "$root\server" 'dotnet watch run --non-interactive --project src/DyOne.Worker'

$web = Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($web) {
    # 포트는 열려 있는데 응답이 없으면(콘솔 창에서 글자를 선택해 멈춘 경우 등) 끄고 새로 켠다
    try { Invoke-WebRequest -Uri 'http://localhost:5173/' -TimeoutSec 3 -UseBasicParsing | Out-Null; $alive = $true } catch { $alive = $false }
    if (-not $alive) {
        Write-Host "  웹 개발 서버가 응답하지 않아 다시 켭니다." -ForegroundColor Yellow
        Stop-Process -Id $web.OwningProcess -Force -ErrorAction SilentlyContinue
        $web = $null
    }
}
if ($web) {
    Write-Host "웹 개발 서버는 이미 실행 중 (http://localhost:5173)" -ForegroundColor DarkGray
} else {
    Write-Host "웹 개발 서버 (http://localhost:5173)" -ForegroundColor Cyan
    Start-Window 'DY ONE Web' "$root\web" 'npm run dev'
}

if (-not $NoApps) { Start-LinkedApps }

if ($Desktop) {
    Write-Host "Windows 앱 실행" -ForegroundColor Cyan
    Start-Window 'DY ONE Desktop' "$root\desktop" 'dotnet run'
}

Write-Host "브라우저에서 http://localhost:5173 을 여세요." -ForegroundColor Green
