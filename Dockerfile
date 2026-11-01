# CORE24 as a container image. Build:  docker build -t core24 .
# Two stages: a big image with the SDK compiles the app; a small image with only the runtime runs it.

# ---- 1. Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, with only the project files: Docker caches this layer, so changing code
# doesn't download every NuGet package again.
COPY Core24.slnx ./
COPY src/Core24.Web/Core24.Web.csproj src/Core24.Web/
RUN dotnet restore src/Core24.Web/Core24.Web.csproj

COPY src/ src/
RUN dotnet publish src/Core24.Web/Core24.Web.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---- 2. Run ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
COPY --from=build /app .

# Everything the app writes (database, files, keys, logs) lives in /app/App_Data.
# docker-compose.yml mounts a volume there, so it survives a new image; back that volume up.
# The image runs as the non-root user "app" (built into Microsoft's images): it must own the folder.
RUN mkdir -p /app/App_Data && chown -R $APP_UID /app/App_Data
USER $APP_UID

# Plain HTTP inside the container. HTTPS is the reverse proxy's job (Caddy, see docker-compose.yml).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "Core24.Web.dll"]
