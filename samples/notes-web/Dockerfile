# syntax=docker/dockerfile:1
# The "notes" frontend: node builds the app, Caddy serves it. The compose overlay builds it with this directory as the
# context. By hand:  docker build -f samples/notes-web/Dockerfile samples/notes-web

FROM node:24-alpine AS build
ENV NG_CLI_ANALYTICS=false
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY angular.json tsconfig.json tsconfig.app.json tsconfig.spec.json ./
COPY public public
COPY src src
RUN npx ng build --configuration production

# Same pinned image as the proxy of samples/notes-api/compose.yml (the tag says which release, the digest which bytes).
# The overlay runs it as 10002:10002 on a read-only root filesystem with /data and /config in memory: the files below only
# need to be readable by everyone, and nothing in the Caddyfile writes anywhere else (tls internal keeps its CA in /data).
FROM caddy:2.11.7@sha256:f2a1290d0463aad60660d4ec134943f183ee2a5f6c3eb7bf32dd984f2f020772
COPY --from=build --chmod=u=rwX,go=rX /app/dist/notes-web/browser /srv
