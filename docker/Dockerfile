# StreamZH container image (linux/amd64 and linux/arm64)
# Build from the repository root:  docker build -f docker/Dockerfile -t streamzh:dev .
# Run:  docker run -d --name streamzh -p 9797:9797 -e PUID=1000 -e PGID=1000 -v /srv/streamzh:/config streamzh:dev
# Released images: ghcr.io/streamzh/streamzh (see docs/PACKAGING.md).

# The web UI and the .NET build run on the build machine's platform; their output is the same for every target
# (the backend is published framework-dependent and portable: managed code plus native SQLite for each platform).
FROM --platform=$BUILDPLATFORM node:26-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY global.json Directory.Build.props NuGet.Config ./
COPY src/ src/
COPY --from=frontend /src/src/StreamZH.Host/wwwroot src/StreamZH.Host/wwwroot
RUN dotnet publish src/StreamZH.Host -c Release -o /app -p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=dev
LABEL org.opencontainers.image.title="StreamZH" \
      org.opencontainers.image.description="One app for your whole media pipeline: library, indexers, downloads, requests." \
      org.opencontainers.image.source="https://github.com/streamzh/streamzh" \
      org.opencontainers.image.url="https://github.com/streamzh/streamzh" \
      org.opencontainers.image.documentation="https://github.com/streamzh/streamzh/blob/main/docs/INSTALL.md" \
      org.opencontainers.image.licenses="GPL-3.0-only" \
      org.opencontainers.image.vendor="StreamZH contributors" \
      org.opencontainers.image.version="$VERSION"
# par2 for repair, 7-Zip for unpacking (RAR, 7z, ZIP), ffmpeg for ffprobe (media info). Found on PATH, so StreamZH does
# not install its own copies here.
RUN apt-get update \
    && apt-get install -y --no-install-recommends par2 7zip ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /config \
    && chown "$APP_UID:$APP_UID" /config
# Data in /config. The container starts as root only to give /config to PUID:PGID (default: the image's "app" user
# 1654) and then runs StreamZH as that user; with --user it runs as that user directly. UMASK like linuxserver.io
# (default 027: group may read new files, others not).
# ASPNETCORE_HTTP_PORTS from the base image (8080) is cleared: StreamZH listens on 9797 itself.
ENV STREAMZH_DATA=/config \
    ASPNETCORE_HTTP_PORTS= \
    PUID= \
    PGID= \
    UMASK=
VOLUME /config
# 9797: web UI and API. 51414: the built-in torrent engine's listening port for peers (TCP and UDP), when it is on.
EXPOSE 9797 51414/tcp 51414/udp
WORKDIR /app
COPY --from=backend /app .
COPY --chmod=0755 docker/entrypoint.sh /entrypoint.sh
# /health needs no login. Port 9797 inside the container (change the published port with -p, not in Settings).
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/9797 && printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["/entrypoint.sh"]
