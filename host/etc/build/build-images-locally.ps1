param ($version='latest')

$currentFolder = $PSScriptRoot
$slnFolder = Join-Path $currentFolder "../../"
$appFolder = Join-Path $slnFolder "src"

$angularAppFolder = Join-Path $slnFolder "angular"

Write-Host "********* BUILDING Angular Application *********" -ForegroundColor Green
Set-Location $angularAppFolder
npm install
npm run build:prod
docker build -f Dockerfile.local -t dignite/extract-web:$version .

Write-Host "********* BUILDING Api.Host Application *********" -ForegroundColor Green
Set-Location $appFolder
dotnet publish -c Release
docker build -f Dockerfile.local -t dignite/extract-api:$version .


### ALL COMPLETED
Write-Host "********* COMPLETED *********" -ForegroundColor Green
Set-Location $currentFolder
exit $LASTEXITCODE
