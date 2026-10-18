# StreamZH container image
# Build from the repository root:  docker build -f docker/Dockerfile -t streamzh:dev .
# Run:  docker run -d --name streamzh -p 9797:9797 -e PUID=1000 -e PGID=1000 -v /opt/streamzh:/config streamzh:dev

FROM node:24-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY global.json Directory.Build.props NuGet.Config ./
COPY src/ src/
COPY --from=frontend /src/src/StreamZH.Host/wwwroot src/StreamZH.Host/wwwroot
RUN dotnet publish src/StreamZH.Host -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# par2 for repair, 7-Zip for unpacking (RAR, 7z, ZIP). Found on PATH, so StreamZH does not install its own copy here.
RUN apt-get update \
    && apt-get install -y --no-install-recommends par2 7zip \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /config \
    && chown "$APP_UID:$APP_UID" /config
# Data in /config. The container starts as root only to give /config to PUID:PGID (default: the image's "app" user
# 1654) and then runs StreamZH as that user; with --user it runs as that user directly. UMASK like linuxserver.io.
ENV STREAMZH_DATA=/config \
    PUID= \
    PGID= \
    UMASK=
VOLUME /config
EXPOSE 9797
WORKDIR /app
COPY --from=backend /app .
COPY --chmod=0755 docker/entrypoint.sh /entrypoint.sh
# /health needs no login. Port 9797 inside the container (change the published port with -p, not in Settings).
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/9797 && printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["/entrypoint.sh"]
