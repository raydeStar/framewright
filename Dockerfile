# syntax=docker/dockerfile:1.7

FROM node:24-bookworm-slim AS web
WORKDIR /source/src/storyboard-studio-web
COPY src/storyboard-studio-web/package.json src/storyboard-studio-web/package-lock.json ./
RUN npm ci
COPY src/storyboard-studio-web/ ./
RUN npm run build

FROM node:24-bookworm-slim AS codex
RUN npm install --global @openai/codex@0.145.0

FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS build
ARG FRAMEWRIGHT_VERSION=development
ARG FRAMEWRIGHT_COMMIT=unknown
ARG FRAMEWRIGHT_BUILT_AT_UTC=unknown
ARG FRAMEWRIGHT_CHANNEL=development
WORKDIR /source
COPY global.json Directory.Build.props Framewright.slnx ./
COPY src/StoryboardStudio.Core/StoryboardStudio.Core.csproj src/StoryboardStudio.Core/
COPY src/StoryboardStudio.Core/packages.lock.json src/StoryboardStudio.Core/
COPY src/StoryboardStudio.Api/StoryboardStudio.Api.csproj src/StoryboardStudio.Api/
COPY src/StoryboardStudio.Api/packages.lock.json src/StoryboardStudio.Api/
RUN dotnet restore src/StoryboardStudio.Api/StoryboardStudio.Api.csproj --locked-mode
COPY src/ ./src/
COPY workflows/ ./workflows/
COPY tools/voice/ ./tools/voice/
COPY scripts/setup-voice-worker.ps1 scripts/start-voice-worker.ps1 scripts/start-installed.ps1 ./scripts/
COPY --from=web /source/src/StoryboardStudio.Api/wwwroot/ ./src/StoryboardStudio.Api/wwwroot/
RUN dotnet publish src/StoryboardStudio.Api/StoryboardStudio.Api.csproj --configuration Release --no-restore --output /out \
    -p:FramewrightVersion=${FRAMEWRIGHT_VERSION} \
    -p:FramewrightCommit=${FRAMEWRIGHT_COMMIT} \
    -p:FramewrightBuiltAtUtc=${FRAMEWRIGHT_BUILT_AT_UTC} \
    -p:FramewrightChannel=${FRAMEWRIGHT_CHANNEL}

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3 AS runtime
ARG FRAMEWRIGHT_VERSION=development
ARG FRAMEWRIGHT_COMMIT=unknown
ARG FRAMEWRIGHT_BUILT_AT_UTC=unknown
ARG FRAMEWRIGHT_CHANNEL=development
LABEL org.opencontainers.image.title="Framewright" \
      org.opencontainers.image.version="${FRAMEWRIGHT_VERSION}" \
      org.opencontainers.image.revision="${FRAMEWRIGHT_COMMIT}" \
      org.opencontainers.image.created="${FRAMEWRIGHT_BUILT_AT_UTC}" \
      org.opencontainers.image.ref.name="${FRAMEWRIGHT_CHANNEL}"
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl ffmpeg gosu \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /codex-home/skills/.system /codex-seed/skills /home/app \
    && chown -R $APP_UID:$APP_UID /codex-home /codex-seed /home/app
COPY --from=codex /usr/local/bin/node /usr/local/bin/node
COPY --from=codex /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/@openai/codex/bin/codex.js /usr/local/bin/codex
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
COPY --chmod=755 --chown=$APP_UID:$APP_UID scripts/docker-entrypoint.sh /usr/local/bin/framewright-entrypoint
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Studio__DataRoot=/data \
    Studio__WorkflowRoot=/app/workflows \
    HOME=/home/app \
    CODEX_HOME=/codex-home
EXPOSE 8080
# Docker restarts only on process liveness. Operational readiness (database,
# writable storage, workflows, workers, and backups) is intentionally reported
# by /health/ready without turning a recoverable workstation problem into a
# restart loop.
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=4 CMD curl --fail --silent http://127.0.0.1:8080/health/live || exit 1

# The entrypoint needs a brief root bootstrap to repair ownership on bind-mounted
# directories. It drops permanently to APP_UID before Framewright starts.
USER root
ENTRYPOINT ["framewright-entrypoint"]
