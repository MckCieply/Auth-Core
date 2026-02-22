# syntax=docker/dockerfile:1
# Build from the repository root (the version and the commit come in as build arguments, because .git is not in the build
# context; the release uses the tag and `git rev-parse HEAD`):
#   docker build -f src/Auth.Server/Dockerfile \
#     --build-arg VERSION=0.1.0 --build-arg REVISION="$(git rev-parse HEAD)" \
#     -t ghcr.io/mckcieply/auth-core:0.1.0 .
#
# Both base images are pinned by digest: the tag says which release, the digest says which bytes, so the image cannot change under
# the tag. Updating them is a deliberate commit (docker buildx imagetools inspect <image:tag> prints the index digest).

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

# Restore first, from project files only, so the package layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Auth.Infrastructure/Auth.Infrastructure.csproj src/Auth.Infrastructure/
COPY src/Auth.Server/Auth.Server.csproj src/Auth.Server/
RUN dotnet restore src/Auth.Server/Auth.Server.csproj

COPY src/ src/
RUN dotnet publish src/Auth.Server/Auth.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/MckCieply/Auth-Core" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Chiseled images ship a non-root "app" user; run as it explicitly.
USER $APP_UID
ENTRYPOINT ["dotnet", "Auth.Server.dll"]
