# syntax=docker/dockerfile:1
# Build from the repository root:
#   docker build -f src/Auth.Server/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so the package layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Auth.Infrastructure/Auth.Infrastructure.csproj src/Auth.Infrastructure/
COPY src/Auth.Server/Auth.Server.csproj src/Auth.Server/
RUN dotnet restore src/Auth.Server/Auth.Server.csproj

COPY src/ src/
RUN dotnet publish src/Auth.Server/Auth.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Chiseled images ship a non-root "app" user; run as it explicitly.
USER $APP_UID
ENTRYPOINT ["dotnet", "Auth.Server.dll"]
