# Container image of this fork: the server from this repository and the web client from the jellyfin-web fork,
# on top of the official Jellyfin image, which provides jellyfin-ffmpeg, fonts and the runtime libraries.
#
# Build from the root of this repository, with the web client checked out in ./jellyfin-web-src:
#   docker buildx build -f deployment/fork/Dockerfile --platform linux/amd64,linux/arm64 .
#
# The build stages run natively on the build machine and cross-compile, so building for another
# architecture does not need emulation.

ARG BASE_IMAGE=jellyfin/jellyfin:unstable

FROM --platform=$BUILDPLATFORM node:24-bookworm AS web
WORKDIR /src
COPY jellyfin-web-src/package.json jellyfin-web-src/package-lock.json ./
RUN npm ci --no-audit --no-fund --ignore-scripts
COPY jellyfin-web-src/ ./
RUN npm run build:production

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS server
ARG TARGETARCH
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in \
        amd64) rid=linux-x64 ;; \
        arm64) rid=linux-arm64 ;; \
        *) echo "unsupported architecture $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
        --configuration Release \
        --runtime "$rid" \
        --self-contained \
        --output /server \
        -p:DebugSymbols=false \
        -p:DebugType=none

FROM ${BASE_IMAGE}
# Installed next to, not over, the official server, so no file of another version is left behind.
COPY --from=server /server /jellyfin-fork
COPY --from=web /src/dist /jellyfin-fork/jellyfin-web
# The base image sets the data, cache, config and log directories and the ffmpeg path (JELLYFIN_* variables),
# its health check and volumes. Only the web client location differs.
ENV JELLYFIN_WEB_DIR=/jellyfin-fork/jellyfin-web
ENTRYPOINT ["/jellyfin-fork/jellyfin"]
