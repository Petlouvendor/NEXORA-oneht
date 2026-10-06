# NEXORA — image API (Phase 3 §2 ; finalisation phase 18 §39 « Docker, migrations »)
# Contexte de build : racine du dépôt — l'API embarque database/migrations (assistant d'installation §20,
# commande « migrate »).
#   docker build -f backend/Dockerfile --build-arg NEXORA_VERSION=1.2.0 --build-arg NEXORA_COMMIT=$(git rev-parse HEAD) -t nexora-api .
# Le même binaire sert :
#   dotnet Nexora.Api.dll               → l'API (port 8080)
#   dotnet Nexora.Api.dll migrate       → applique les migrations livrées (compte propriétaire), puis s'arrête
#   dotnet Nexora.Api.dll healthcheck   → sonde de vivacité (l'image n'a ni curl ni wget)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/Directory.Build.props backend/Directory.Packages.props ./backend/
COPY backend/src/ ./backend/src/
COPY database/migrations/ ./database/migrations/
RUN dotnet restore backend/src/Nexora.Api/Nexora.Api.csproj
ARG NEXORA_VERSION=1.0.0
RUN dotnet publish backend/src/Nexora.Api/Nexora.Api.csproj -c Release -o /app --no-restore \
      -p:Version=${NEXORA_VERSION%%-*} -p:InformationalVersion=${NEXORA_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Utilisateur non-root (§13, §14) : l'image .NET fournit « app » ($APP_UID) — elle n'a pas « adduser », ce qui
# empêchait jusqu'ici toute construction de l'image (finalisation phase 18). Seul /var/lib/nexora est inscriptible
# (configuration écrite par l'assistant d'installation quand aucune base n'est fournie par l'environnement).
RUN mkdir -p /var/lib/nexora && chown "$APP_UID" /var/lib/nexora
USER $APP_UID
COPY --from=build /app ./
ARG NEXORA_VERSION=1.0.0
ARG NEXORA_COMMIT=
LABEL org.opencontainers.image.title="nexora-api" \
      org.opencontainers.image.version="${NEXORA_VERSION}" \
      org.opencontainers.image.revision="${NEXORA_COMMIT}"
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Install__ConfigPath=/var/lib/nexora/nexora.install.json \
    NEXORA_VERSION=${NEXORA_VERSION} \
    NEXORA_COMMIT=${NEXORA_COMMIT} \
    DOTNET_gcServer=0
VOLUME /var/lib/nexora
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=6s --start-period=20s --retries=3 \
  CMD ["dotnet", "Nexora.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Nexora.Api.dll"]
