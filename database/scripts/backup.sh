#!/usr/bin/env bash
# Sauvegarde logique de la base Nexora One (Phase 36 §6, §26, §31).
# Produit un dump compressé + son empreinte SHA-256, puis (optionnellement) enregistre la
# sauvegarde dans le registre applicatif via l'API (`POST /api/v1/backups`).
#
# Usage :
#   PGPASSWORD=... ./backup.sh [dbname] [dest-dir]
#
# Variables d'environnement facultatives :
#   NEXORA_API_URL      ex. http://localhost:5080   (déclenche l'enregistrement au registre)
#   NEXORA_API_KEY      clé d'API avec le scope `continuity.manage`
#   NEXORA_BACKUP_KIND  full | incremental | config | documents | snapshot   (défaut : full)
set -euo pipefail

DB="${1:-nexora}"
DEST="${2:-./backups}"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
KIND="${NEXORA_BACKUP_KIND:-full}"

PGDUMP_BIN="${PGDUMP_BIN:-pg_dump}"
if ! command -v "$PGDUMP_BIN" >/dev/null 2>&1; then
  for c in "/c/Program Files/PostgreSQL/18/bin/pg_dump.exe" "/c/Program Files/PostgreSQL/16/bin/pg_dump.exe"; do
    [ -x "$c" ] && PGDUMP_BIN="$c" && break
  done
fi

mkdir -p "$DEST"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
COVERS_UP_TO="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
FILE="$DEST/nexora_${DB}_${KIND}_${STAMP}.dump"

echo ">> pg_dump $DB → $FILE"
"$PGDUMP_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" \
  --format=custom --compress=9 --no-owner --no-privileges --file "$FILE"

SIZE=$(wc -c < "$FILE" | tr -d ' ')
CHECKSUM=$(sha256sum "$FILE" | cut -d' ' -f1)
echo ">> taille=${SIZE}o  sha256=${CHECKSUM}"

# Nombre d'objets applicatifs (proxy de cohérence — cf. IContinuityService.LiveObjectCountAsync)
PSQL_BIN="${PSQL_BIN:-psql}"
command -v "$PSQL_BIN" >/dev/null 2>&1 || PSQL_BIN="/c/Program Files/PostgreSQL/18/bin/psql.exe"
OBJECTS=$("$PSQL_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -qtAc \
  "SELECT (SELECT count(*) FROM nexora.sale) + (SELECT count(*) FROM nexora.journal_entry)
        + (SELECT count(*) FROM nexora.product) + (SELECT count(*) FROM nexora.customer)
        + (SELECT count(*) FROM nexora.issued_document)" 2>/dev/null || echo "")

echo "$CHECKSUM  $(basename "$FILE")" > "$FILE.sha256"

if [ -n "${NEXORA_API_URL:-}" ] && [ -n "${NEXORA_API_KEY:-}" ]; then
  echo ">> enregistrement au registre applicatif : $NEXORA_API_URL/api/v1/backups"
  curl -fsS -X POST "$NEXORA_API_URL/api/v1/backups" \
    -H "X-Api-Key: $NEXORA_API_KEY" -H "Content-Type: application/json" \
    -d "{\"kind\":\"$KIND\",\"coversUpTo\":\"$COVERS_UP_TO\",\"sizeBytes\":$SIZE,\"checksum\":\"$CHECKSUM\",\"objectCount\":${OBJECTS:-null},\"locationRef\":\"$(basename "$FILE")\",\"encrypted\":false}" \
    && echo " (enregistré)" || echo " (échec d'enregistrement — la sauvegarde locale reste valide)"
fi

echo ">> terminé : $FILE"
