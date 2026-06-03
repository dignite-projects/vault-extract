FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Issue #112: copy the whole source tree before restore. The previous Slice-0
# pattern of per-csproj COPY directives existed only to keep the restore-cache
# layer stable across pure source edits, but it silently broke the moment
# Slice 1+ added projects (TextExtraction*, Ocr.PaddleOcr,
# Contracts.*) and moved the Slice-0 per-tier props files into
# the consolidated root common.props + Directory.Packages.props. Trading the
# restore-cache for a build that doesn't need a Dockerfile audit on every
# project add — the Host image is rebuilt by CI per push, not in tight inner
# loops, so the lost cache cost is negligible.
COPY common.props Directory.Packages.props Dignite.Paperbase.slnx ./
COPY core/ core/
COPY host/ host/

RUN dotnet restore "host/src/Dignite.Paperbase.Host.csproj"
RUN dotnet build "host/src/Dignite.Paperbase.Host.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "host/src/Dignite.Paperbase.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Dignite.Paperbase.Host.dll"]
