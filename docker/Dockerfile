# syntax=docker/dockerfile:1
# Build from the repository root (docs/deploy/docker.md):
#   docker build -f docker/Dockerfile -t telrang .
#   docker buildx build -f docker/Dockerfile --platform linux/amd64,linux/arm64 .   # both release architectures
# or: docker compose -f docker/compose.yml up -d --build

# ---- build ----------------------------------------------------------------------------------
# Runs on the build machine's architecture for every target: the output is framework-dependent IL (native SQLite for
# every architecture included), so no emulation is needed. The final stage has no RUN step for the same reason.
# Base images pinned by digest (pentest R3-DEP-12e): a rebuild uses the same bytes; update the digests on purpose
# (docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0, .../aspnet:10.0).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
ARG VERSION=0.0.0
ARG COMMIT=unknown
WORKDIR /src

# Restore first, from project files only, so the layer is cached across source edits.
COPY Directory.Build.props ./
COPY src/Telrang.Core/Telrang.Core.csproj src/Telrang.Core/
COPY src/Telrang.Web/Telrang.Web.csproj src/Telrang.Web/
RUN dotnet restore src/Telrang.Web/Telrang.Web.csproj

COPY src/ src/
# Restores again (quick: packages are cached above): the framework's Blazor scripts (_framework/blazor.web.js) come
# from a pack the SDK only adds once it sees the components, which the project-files-only restore does not.
RUN dotnet publish src/Telrang.Web/Telrang.Web.csproj \
        -c Release -o /app/publish /p:UseAppHost=false /p:ContinuousIntegrationBuild=true \
        /p:Version=$VERSION /p:InformationalVersion=$VERSION+$COMMIT \
    && mkdir -p /app/data/keys

# ---- runtime --------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS final
ARG VERSION=0.0.0
ARG COMMIT=unknown
LABEL org.opencontainers.image.title="Telrang" \
      org.opencontainers.image.description="Self-hosted voting app: ranked, points and dates polls" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$COMMIT
WORKDIR /app

# /data holds app.db, keys/ (Data Protection) and optionally cert.pfx. Owned by the image's built-in non-root "app"
# user (UID/GID 1654) and closed to everyone else (0700, DESIGN.md §29), so a fresh named volume starts that way.
COPY --from=build --chown=1654:1654 --chmod=700 /app/data /data
COPY --from=build /app/publish .

# DOTNET_EnableDiagnostics=0: no diagnostics port (debugger, profiler, event pipe) into the running app.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    App__DataDir=/data \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080 8443
VOLUME /data

USER $APP_UID
# The app asks itself (the image has no curl): GET /healthz on the first http address of ASPNETCORE_URLS.
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD ["dotnet", "Telrang.Web.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Telrang.Web.dll"]
