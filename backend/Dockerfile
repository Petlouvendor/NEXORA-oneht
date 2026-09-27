# NEXORA — image API (Phase 3 §2 : conteneurisation recommandée)
# Contexte de build : racine du dépôt — l'API embarque database/migrations pour l'assistant
# d'installation (résumé fonctionnel §20).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/Directory.Build.props backend/Directory.Packages.props backend/Nexora.sln ./backend/
COPY backend/src/ ./backend/src/
COPY backend/tests/ ./backend/tests/
COPY database/migrations/ ./database/migrations/
RUN dotnet restore backend/src/Nexora.Api/Nexora.Api.csproj
RUN dotnet publish backend/src/Nexora.Api/Nexora.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Utilisateur non-root (§13, §14) ; seul /var/lib/nexora est inscriptible (configuration écrite
# par l'assistant d'installation quand aucune base n'est fournie par l'environnement).
RUN adduser --system --group --no-create-home nexora \
 && mkdir -p /var/lib/nexora && chown nexora:nexora /var/lib/nexora
USER nexora
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    Install__ConfigPath=/var/lib/nexora/nexora.install.json
VOLUME /var/lib/nexora
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Nexora.Api.dll"]
