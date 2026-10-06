#!/usr/bin/env bash
# NEXORA ONE — génération des secrets de production (finalisation phase 18, §39 « gestion secrets »).
#
# Crée infrastructure/secrets/ (dossier 700, hors Git) avec des valeurs aléatoires fortes. Ne remplace JAMAIS un
# secret existant : changer la clé MFA ou la clé des données rendrait illisibles les secrets déjà chiffrés (voir la
# rotation dans docs/exploitation/runbook-production.md).
#
# Usage : infrastructure/scripts/generate-secrets.sh
set -euo pipefail

DIR="$(cd "$(dirname "$0")/.." && pwd)/secrets"
mkdir -p "$DIR"
chmod 700 "$DIR"

rand_b64() { openssl rand -base64 "$1" | tr -d '\r\n'; }   # \r : openssl sous Windows
rand_alnum() { openssl rand -base64 96 | tr -dc 'A-Za-z0-9' | cut -c1-"$1"; }

# Fichier lisible par le conteneur (utilisateur non-root) ; c'est le dossier 700 qui protège sur l'hôte.
put() {
  local name="$1" value="$2"
  if [ -s "$DIR/$name" ]; then
    echo "conservé  $name (existe déjà)"
    return
  fi
  printf '%s' "$value" > "$DIR/$name"
  chmod 644 "$DIR/$name"
  echo "créé      $name"
}

PG_PASSWORD="$(cat "$DIR/postgres_password" 2>/dev/null || rand_alnum 40)"
APP_PASSWORD="$(cat "$DIR/app_role_password" 2>/dev/null || rand_alnum 40)"

put postgres_password "$PG_PASSWORD"
put app_role_password "$APP_PASSWORD"
put migration_connection "Host=postgres;Port=5432;Database=nexora;Username=nexora_owner;Password=$PG_PASSWORD;Search Path=nexora,public;GSS Encryption Mode=Disable"
put app_connection "Host=postgres;Port=5432;Database=nexora;Username=nexora_app;Password=$APP_PASSWORD;Search Path=nexora,public;Maximum Pool Size=50;GSS Encryption Mode=Disable"
put jwt_signing_key "$(rand_alnum 64)"
put mfa_encryption_key "$(rand_b64 32)"
put data_encryption_key "$(rand_b64 32)"
# Clé d'API de la sauvegarde (scope continuity.manage) : à créer dans l'application puis à coller ici ; vide = la
# sauvegarde reste locale, sans enregistrement au registre de continuité.
for optional in backup_api_key smtp_password; do
  [ -e "$DIR/$optional" ] || { : > "$DIR/$optional"; chmod 644 "$DIR/$optional"; echo "créé      $optional (vide, à renseigner si utilisé)"; }
done

echo ">> secrets dans $DIR — à sauvegarder hors du serveur (coffre), jamais dans Git."
