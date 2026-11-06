# NEXORA ONE — image de l'interface (finalisation phase 18, §39 « Docker, HTTPS »)
# Contexte de build : racine du dépôt (le Caddyfile est dans infrastructure/caddy).
#   docker build -f frontend/nexora-web/Dockerfile -t nexora-web .
# Construit l'application (tsc + vite) puis la sert avec Caddy, qui termine TLS et relaie /api vers l'API.
# Étiquettes exactes (audit 2026-10 éd. 2) : « 22-alpine » et « 2-alpine » flottaient, deux constructions du même
# commit pouvaient différer. Mettre à jour ces versions volontairement.
FROM node:22.20.0-alpine3.22 AS build
WORKDIR /web
COPY frontend/nexora-web/package.json frontend/nexora-web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/nexora-web/ ./
RUN npm run build

FROM caddy:2.10.2-alpine
ARG NEXORA_VERSION=dev
ARG NEXORA_COMMIT=
LABEL org.opencontainers.image.title="nexora-web" \
      org.opencontainers.image.version="${NEXORA_VERSION}" \
      org.opencontainers.image.revision="${NEXORA_COMMIT}"
COPY infrastructure/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /web/dist /srv
