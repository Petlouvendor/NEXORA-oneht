#!/usr/bin/env bash
# Construit la base de RECETTE (Phase 55) : schéma complet + démo ABC GROUP + Supermarché
# opérationnel + jeu de données de recette généré par les services métier.
#
# Refuse de cibler `nexora` : cette base sert de modèle (TEMPLATE) aux tests d'intégration ;
# y verser des données de recette ferait échouer les tests (anomalie A3, Phase 49).
#
# Usage : PGPASSWORD=... ./recette.sh [dbname]        (défaut : nexora_recette)
# Ensuite : Database__ConnectionString="Host=localhost;Database=nexora_recette;Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public" \
#           dotnet run --project backend/src/Nexora.Api
set -euo pipefail

DB="${1:-nexora_recette}"
if [ "$DB" = "nexora" ]; then
  echo "REFUS : 'nexora' est la base modèle des tests d'intégration. Utilisez une base dédiée (ex. nexora_recette)." >&2
  exit 1
fi
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
: "${PGPASSWORD:?PGPASSWORD (superutilisateur) requis}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

PSQL_BIN="${PSQL_BIN:-psql}"
if ! command -v "$PSQL_BIN" >/dev/null 2>&1; then
  for c in "/c/Program Files/PostgreSQL/18/bin/psql.exe" "/c/Program Files/PostgreSQL/16/bin/psql.exe"; do
    [ -x "$c" ] && PSQL_BIN="$c" && break
  done
fi
run_sql() { "$PSQL_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -q -f "$1"; }

echo ">> 1/4 schéma ($DB)"
PSQL_BIN="$PSQL_BIN" PGHOST="$PGHOST" PGPORT="$PGPORT" PGUSER="$PGUSER" bash "$HERE/rebuild.sh" "$DB" | tail -1

echo ">> 2/4 démo ABC GROUP"
run_sql "$ROOT/database/seeds/demo_abc_group.sql" | tail -1

echo ">> 3/4 Supermarché opérationnel"
run_sql "$ROOT/database/seeds/recette/01_supermarche_config.sql"

echo ">> 4/4 jeu de recette (services métier)"
CONN="Host=$PGHOST;Port=$PGPORT;Database=$DB;Username=$PGUSER;Password=$PGPASSWORD;Search Path=nexora,public;Include Error Detail=true"
dotnet run --project "$ROOT/backend/tools/Nexora.RecetteSeed" -c Release -- "$CONN"
