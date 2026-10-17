# StreamZH container image
# Build from the repository root:  docker build -f docker/Dockerfile -t streamzh:dev .

FROM node:24-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY --from=frontend /src/src/StreamZH.Host/wwwroot src/StreamZH.Host/wwwroot
RUN dotnet publish src/StreamZH.Host -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# par2 for verify/repair, 7-Zip for unpacking (RAR, 7z, ZIP)
RUN apt-get update \
    && apt-get install -y --no-install-recommends par2 7zip \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /config \
    && chown "$APP_UID" /config
ENV STREAMZH_DATA=/config
VOLUME /config
EXPOSE 9797
WORKDIR /app
COPY --from=backend /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "StreamZH.dll"]
