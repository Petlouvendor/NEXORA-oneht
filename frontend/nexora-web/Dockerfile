# NEXORA ONE — image de l'interface (finalisation phase 18, §39 « Docker, HTTPS »)
# Contexte de build : racine du dépôt (le Caddyfile est dans infrastructure/caddy).
#   docker build -f frontend/nexora-web/Dockerfile -t nexora-web .
# Construit l'application (tsc + vite) puis la sert avec Caddy, qui termine TLS et relaie /api vers l'API.
FROM node:22-alpine AS build
WORKDIR /web
COPY frontend/nexora-web/package.json frontend/nexora-web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/nexora-web/ ./
RUN npm run build

FROM caddy:2-alpine
ARG NEXORA_VERSION=dev
ARG NEXORA_COMMIT=
LABEL org.opencontainers.image.title="nexora-web" \
      org.opencontainers.image.version="${NEXORA_VERSION}" \
      org.opencontainers.image.revision="${NEXORA_COMMIT}"
COPY infrastructure/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /web/dist /srv
