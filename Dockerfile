# syntax=docker/dockerfile:1
FROM oven/bun:1.3.14-debian AS web-build
WORKDIR /src/FactorioManager.Web
COPY src/FactorioManager.Web/package.json src/FactorioManager.Web/bun.lock ./
RUN bun install --frozen-lockfile
COPY src/FactorioManager.Web/ ./
RUN bun run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
ARG APP_VERSION=0.1.0
WORKDIR /src
COPY Directory.Build.props FactorioServerManager.sln ./
COPY src/FactorioManager.Api/FactorioManager.Api.csproj src/FactorioManager.Api/
RUN dotnet restore src/FactorioManager.Api/FactorioManager.Api.csproj
COPY src/FactorioManager.Api/ src/FactorioManager.Api/
RUN dotnet publish src/FactorioManager.Api/FactorioManager.Api.csproj -c Release -o /app/publish --no-restore \
    -p:VersionPrefix=$APP_VERSION -p:Version=$APP_VERSION -p:InformationalVersion=$APP_VERSION
COPY --from=web-build /src/FactorioManager.Api/wwwroot /app/publish/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl tar xz-utils && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --create-home factorio
WORKDIR /app
COPY --from=api-build /app/publish ./
RUN mkdir -p /data && chown factorio:factorio /data
USER factorio
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataRoot=/data
EXPOSE 8080 34197/udp
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl --fail http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "FactorioManager.Api.dll"]
