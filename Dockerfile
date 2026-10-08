# syntax=docker/dockerfile:1
#
# apiary-keeper — the ApiaryCopilot.Api image (ghcr.io/<owner>/apiary-keeper, the repository owner in lower case), carrying the built family PWA (src/pwa) in wwwroot (D-5). Tracked by SemVer tags only: :X.Y.Z, plus the moving :staging and :production.
# VERSION and REVISION are passed by the workflows, which also set the org.opencontainers.image.source label from the
# repository they run in (GitHub reads it to link the package to its repository); a local build carries none, so
# there is no repository URL in this file to go stale when the repository moves or is renamed.
#   docker build -t api:dev -f Dockerfile .
#   docker build --target test -f Dockerfile .

# Matches global.json so the container builds with the same SDK as developers and CI.
ARG DOTNET_SDK_VERSION=10.0.301

# The family PWA (src/pwa): installed exactly from its lock and built to static files. Its lint, type-check and
# tests are not part of this build: run them with npm run lint, npm run typecheck and npm test.
FROM node:24-alpine AS pwa
WORKDIR /repo/src/pwa
COPY src/pwa/package.json src/pwa/package-lock.json ./
RUN npm ci
COPY src/pwa/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /repo

# Restore from project files first, so the layer is cached until a dependency changes.
# .editorconfig too: its analyzer severities (CA1707 off for tests) must match the CI build's.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/*.slnx src/
COPY src/ApiaryCopilot.Api/ApiaryCopilot.Api.csproj src/ApiaryCopilot.Api/
COPY src/ApiaryCopilot.Domain/ApiaryCopilot.Domain.csproj src/ApiaryCopilot.Domain/
COPY src/ApiaryCopilot.Infrastructure/ApiaryCopilot.Infrastructure.csproj src/ApiaryCopilot.Infrastructure/
COPY src/tests/ApiaryCopilot.UnitTests/ApiaryCopilot.UnitTests.csproj src/tests/ApiaryCopilot.UnitTests/
COPY src/tests/ApiaryCopilot.IntegrationTests/ApiaryCopilot.IntegrationTests.csproj src/tests/ApiaryCopilot.IntegrationTests/
RUN dotnet restore src/ApiaryCopilot.Api/ApiaryCopilot.Api.csproj \
 && dotnet restore src/tests/ApiaryCopilot.UnitTests/ApiaryCopilot.UnitTests.csproj

COPY src/ src/
RUN dotnet publish src/ApiaryCopilot.Api/ApiaryCopilot.Api.csproj -c Release --no-restore -o /app

# Unit tier (CONVENTIONS.md §8.1): `docker build --target test .`
FROM build AS test
RUN dotnet test src/tests/ApiaryCopilot.UnitTests/ApiaryCopilot.UnitTests.csproj -c Release --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="apiary-keeper" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=build /app ./
# Served by the API host (FrontendExtensions.UseFrontend).
COPY --from=pwa /repo/src/pwa/dist ./wwwroot
# RAM is the main cost on Railway (NFR-7): workstation GC, and a 256 MiB managed-heap hard limit (hexadecimal, as the
# runtime reads it). An environment variable of the same name overrides either (DEPLOYMENT.md §3).
ENV DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimit=0x10000000
# The base image's non-root user.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ApiaryCopilot.Api.dll"]
