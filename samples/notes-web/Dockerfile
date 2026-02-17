# syntax=docker/dockerfile:1
# The "notes" frontend: node builds the app, Caddy serves it. The compose overlay builds it with this directory as the
# context. The image holds the build only: the Caddyfile is mounted by the overlay (not copied in), so run it through the
# overlay. By hand, `docker build -f samples/notes-web/Dockerfile samples/notes-web` only checks that the app builds.

# The tag says which release, the digest which bytes (the index of all platforms, as the registry gave it on 2026-10-06).
FROM node:24-alpine@sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1 AS build
ENV NG_CLI_ANALYTICS=false
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY angular.json tsconfig.json tsconfig.app.json tsconfig.spec.json ./
COPY public public
COPY src src
RUN npx ng build --configuration production

# Same pinned image as the proxy of samples/notes-api/compose.yml (the tag says which release, the digest which bytes).
# Runs as 10002:10002 (USER below, and again in the overlay) on a read-only root filesystem with /data and /config in memory
# (the overlay): the files below only need to be readable by everyone, and nothing in the Caddyfile writes anywhere else (tls
# internal keeps its CA in /data, which a hand `docker run` has to make writable for that user).
FROM caddy:2.11.7@sha256:f2a1290d0463aad60660d4ec134943f183ee2a5f6c3eb7bf32dd984f2f020772
COPY --from=build --chmod=u=rwX,go=rX /app/dist/notes-web/browser /srv
USER 10002:10002
