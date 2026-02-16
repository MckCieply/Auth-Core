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

FROM caddy:2
COPY --from=build /app/dist/notes-web/browser /srv
