$currentFolder = $PSScriptRoot

$hostFolder = Join-Path $currentFolder "../../"
$angularFolder = Join-Path $currentFolder "../../../angular"
$certsFolder = Join-Path $currentFolder "certs"

# 1. Local development certificate (Kestrel HTTPS). The password must match
#    Kestrel__Certificates__Default__Password in docker-compose.yml.
If(!(Test-Path -Path $certsFolder))
{
    New-Item -ItemType Directory -Force -Path $certsFolder
    if(!(Test-Path -Path (Join-Path $certsFolder "localhost.pfx") -PathType Leaf)){
        Set-Location $certsFolder
        dotnet dev-certs https -v -ep localhost.pfx -p 91f91912-5ab0-49df-8166-23377efaf3cc -t
    }
}

# 2. Prebuild backend output -> host/src/bin/Release/net10.0/publish/
#    src/Dockerfile.local only does lightweight packaging (COPY pre-published output);
#    it does not build inside the container.
Set-Location $hostFolder
dotnet publish "src/Dignite.Vault.Extract.Host.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 3. Prebuild frontend output -> angular/dist/host/browser/
#    apps/host/Dockerfile.local only packages it with nginx.
Set-Location $angularFolder
npx nx build host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. Rebuild images and start them. --build ensures Docker uses the newly produced
#    artifacts above instead of stale image layers.
Set-Location $currentFolder
docker-compose up -d --build
exit $LASTEXITCODE
