#!/usr/bin/env bash
# Applique les migrations NEXORA non encore appliquées sur une base existante.
# Chaque migration est jouée dans une transaction et enregistrée dans
# nexora.schema_migration. Idempotent : relancer ne rejoue rien.
#
# Usage : PGPASSWORD=... ./apply.sh [dbname]
set -euo pipefail

DB="${1:-nexora}"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
MIG_DIR="$(cd "$(dirname "$0")/../migrations" && pwd)"

PSQL_BIN="${PSQL_BIN:-psql}"
if ! command -v "$PSQL_BIN" >/dev/null 2>&1; then
  for c in "/c/Program Files/PostgreSQL/18/bin/psql.exe" "/c/Program Files/PostgreSQL/16/bin/psql.exe"; do
    [ -x "$c" ] && PSQL_BIN="$c" && break
  done
fi
psql_run() { "$PSQL_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" "$@"; }

# La table de suivi n'existe pas tant que V001 n'a pas tourné.
HAS_TABLE=$(psql_run -qtAc \
  "SELECT to_regclass('nexora.schema_migration') IS NOT NULL" 2>/dev/null || echo "f")

applied_versions=""
if [ "$HAS_TABLE" = "t" ]; then
  applied_versions=$(psql_run -qtAc "SELECT version FROM nexora.schema_migration")
fi

for f in "$MIG_DIR"/V*.sql; do
  name="$(basename "$f")"
  version="${name%%__*}"
  if grep -qxF "$version" <<< "$applied_versions"; then
    echo "skip  $name (déjà appliqué)"
    continue
  fi
  sum=$(sha256sum "$f" | cut -d' ' -f1)
  # -1 : tout le fichier dans une seule transaction ; ON_ERROR_STOP : rollback au 1er échec
  if psql_run -v ON_ERROR_STOP=1 -q -1 \
        -f "$f" \
        -c "INSERT INTO nexora.schema_migration (version, filename, checksum)
            VALUES ('$version', '$name', '$sum')
            ON CONFLICT (version) DO NOTHING;" >/tmp/nexora_apply.out 2>&1; then
    echo "apply $name"
  else
    echo "FAILED $name"; cat /tmp/nexora_apply.out; exit 1
  fi
done

echo ">> $DB à jour"
