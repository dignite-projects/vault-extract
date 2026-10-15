# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# The default OCR provider (VisionLlm) rasterizes PDF pages through PDFtoImage using
# PDFium + SkiaSharp. SkiaSharp needs libfontconfig1 on Linux to render vector/text
# content inside PDFs. See docs/en/text-extraction/ocr-vision-llm.md.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Issue #112: copy the whole source tree before restore. The previous Slice-0
# pattern of per-csproj COPY directives existed only to keep the restore-cache
# layer stable across pure source edits, but it silently broke the moment
# Slice 1+ added projects (Parse*, Ocr.PaddleOcr,
# Contracts.*) and moved the Slice-0 per-tier props files into
# the consolidated root common.props + Directory.Packages.props. Trading the
# restore-cache for a build that doesn't need a Dockerfile audit on every
# project add — the Host image is rebuilt by CI per push, not in tight inner
# loops, so the lost cache cost is negligible.
#
# NuGet.Config must be copied alongside them: each restored project resolves its OWN NuGet.Config by
# walking up from ITS OWN directory, not from the entry-point project's directory. Without the root
# file here, core/*'s ancestor chain (/src/core/... -> /src -> filesystem root) never reaches a config
# that knows about the `github` source below - it silently falls back to nuget.org-only for every
# core/* project, while host/NuGet.Config (host/'s own nearest file) governs only the host project's
# own direct references. Verified by reproducing this exact gap locally (NU1102, nuget.org only found
# up to Dignite.Abp.FlexFields.* 10.0.0-rc.15) before this line existed.
COPY common.props Directory.Packages.props Dignite.Vault.Extract.slnx NuGet.Config ./
COPY core/ core/
COPY host/ host/

# GITHUB_PACKAGES_TOKEN is unused since abp-modules 10.7.0 (NuGet.Config is nuget.org-only); the
# optional secret mount stays so a pre-release pin that needs the GitHub feed can come back.
# BuildKit secret mount keeps it out of the image layers/history - never bake it in via ENV/ARG.
RUN --mount=type=secret,id=github_packages_token \
    GITHUB_PACKAGES_TOKEN="$(cat /run/secrets/github_packages_token)" dotnet restore "host/src/Dignite.Vault.Extract.Host.csproj"
RUN dotnet build "host/src/Dignite.Vault.Extract.Host.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "host/src/Dignite.Vault.Extract.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Dignite.Vault.Extract.Host.dll"]
