#!/usr/bin/env bash
# Recrée entièrement la base nexora (DROP + CREATE) puis applique toutes les migrations.
# DESTRUCTIF — développement uniquement.
#
# Usage : PGPASSWORD=... ./rebuild.sh [dbname]
set -euo pipefail

DB="${1:-nexora}"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
HERE="$(cd "$(dirname "$0")" && pwd)"

PSQL_BIN="${PSQL_BIN:-psql}"
if ! command -v "$PSQL_BIN" >/dev/null 2>&1; then
  for c in "/c/Program Files/PostgreSQL/18/bin/psql.exe" "/c/Program Files/PostgreSQL/16/bin/psql.exe"; do
    [ -x "$c" ] && PSQL_BIN="$c" && break
  done
fi

echo ">> DROP & CREATE DATABASE $DB"
"$PSQL_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d postgres -v ON_ERROR_STOP=1 -q \
  -c "DROP DATABASE IF EXISTS $DB WITH (FORCE);" \
  -c "CREATE DATABASE $DB WITH ENCODING 'UTF8' TEMPLATE template0;"

PSQL_BIN="$PSQL_BIN" PGHOST="$PGHOST" PGPORT="$PGPORT" PGUSER="$PGUSER" \
  bash "$HERE/apply.sh" "$DB"

"$PSQL_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -qtAc \
  "SELECT 'tables=' || count(*) FROM information_schema.tables
   WHERE table_schema='nexora' AND table_type='BASE TABLE';"
