# DY ONE 내부용 개발 환경 실행
#   .\dev.ps1          API + 웹 개발 서버
#   .\dev.ps1 -Desktop 위 + Windows 앱
# DB: 기존 dyone DB(Docker 컨테이너 dyone_db, PostgreSQL 18, 포트 5433)를 읽기 전용으로 사용한다.
param([switch]$Desktop)

$root = $PSScriptRoot

$state = docker inspect -f '{{.State.Running}}' dyone_db 2>$null
if ($state -ne 'true') {
    Write-Host "dyone_db 컨테이너가 실행 중이 아닙니다. 시작합니다." -ForegroundColor Yellow
    docker start dyone_db | Out-Null
}

Write-Host "API 서버 (http://localhost:5080)" -ForegroundColor Cyan
Start-Process pwsh -ArgumentList '-NoExit', '-Command', "Set-Location '$root\server'; dotnet run --project src/DyOne.Api --launch-profile http"

Write-Host "웹 개발 서버 (http://localhost:5173)" -ForegroundColor Cyan
Start-Process pwsh -ArgumentList '-NoExit', '-Command', "Set-Location '$root\web'; npm run dev"

if ($Desktop) {
    Write-Host "Windows 앱 실행" -ForegroundColor Cyan
    Start-Process pwsh -ArgumentList '-NoExit', '-Command', "Set-Location '$root\desktop'; dotnet run"
}

Write-Host "브라우저에서 http://localhost:5173 을 여세요. (개발용 로그인: 직원 선택)" -ForegroundColor Green
