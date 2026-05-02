# syntax=docker/dockerfile:1
# The fake OpenID provider of spec 0011, for the end-to-end stack only (tests/Auth.FakeOidc/compose.yml builds it). Never shipped.
# Build from the repository root; this folder's Dockerfile.dockerignore replaces the root .dockerignore, which leaves tests/ out:
#   docker build -f tests/Auth.FakeOidc/Dockerfile -t auth-core-fake-oidc:local .
#
# The base images are the server's (src/Auth.Server/Dockerfile), pinned by the same digests: update them together.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY tests/Auth.FakeOidc/Auth.FakeOidc.csproj tests/Auth.FakeOidc/
RUN dotnet restore tests/Auth.FakeOidc/Auth.FakeOidc.csproj

COPY tests/Auth.FakeOidc/ tests/Auth.FakeOidc/
RUN dotnet publish tests/Auth.FakeOidc/Auth.FakeOidc.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8099
EXPOSE 8099

# Chiseled images ship a non-root "app" user; run as it explicitly.
USER $APP_UID
ENTRYPOINT ["dotnet", "Auth.FakeOidc.dll"]
