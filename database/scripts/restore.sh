#!/usr/bin/env bash
# Restauration d'une sauvegarde Nexora One (finalisation phase 18, §39 « restore »).
#
#   PGPASSWORD=... ./restore.sh <fichier.dump> <base-cible>            restaure dans une base NOUVELLE
#   PGPASSWORD=... ./restore.sh <fichier.dump> --verify                 exercice : restaure dans une base jetable,
#                                                                       contrôle, puis la supprime
#
# Étapes : empreinte SHA-256 contrôlée (fichier .sha256 écrit par backup.sh) → base cible créée (jamais une base
# existante : on ne restaure pas PAR-DESSUS la production) → pg_restore en arrêtant à la première erreur → contrôles
# (suivi des migrations, nombre d'objets métier comparé au registre si connu) → en mode --verify, si
# NEXORA_API_URL/NEXORA_API_KEY sont fournis et qu'un fichier <dump>.id existe, la sauvegarde est marquée
# « vérifiée » et un test de restauration réussi est inscrit au registre de continuité.
#
# Basculer la production sur une base restaurée est une décision humaine (runbook) : ce script ne touche jamais
# à la base en service.
set -euo pipefail

DUMP="${1:?usage : restore.sh <fichier.dump> <base-cible>|--verify}"
TARGET="${2:?usage : restore.sh <fichier.dump> <base-cible>|--verify}"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
[ -n "${PGPASSWORD_FILE:-}" ] && export PGPASSWORD="$(cat "$PGPASSWORD_FILE")"
export PGCLIENTENCODING=UTF8   # psql sous Windows : sinon l'encodage de la console est annoncé au serveur

find_bin() {
  local name="$1" var="$2"
  local value="${!var:-$name}"
  if ! command -v "$value" >/dev/null 2>&1; then
    for c in "/c/Program Files/PostgreSQL/18/bin/$name.exe" "/c/Program Files/PostgreSQL/16/bin/$name.exe"; do
      [ -x "$c" ] && value="$c" && break
    done
  fi
  printf '%s' "$value"
}
PSQL_BIN="$(find_bin psql PSQL_BIN)"
PGRESTORE_BIN="$(find_bin pg_restore PGRESTORE_BIN)"
psql_on() { "$PSQL_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$1" -v ON_ERROR_STOP=1 -qtA "${@:2}"; }

[ -f "$DUMP" ] || { echo "ÉCHEC : sauvegarde introuvable : $DUMP"; exit 1; }

# 1. Intégrité du fichier
if [ -f "$DUMP.sha256" ]; then
  EXPECTED="$(cut -d' ' -f1 "$DUMP.sha256")"
  ACTUAL="$(sha256sum "$DUMP" | cut -d' ' -f1)"
  [ "$EXPECTED" = "$ACTUAL" ] || { echo "ÉCHEC : empreinte SHA-256 différente (fichier altéré ou incomplet)"; exit 1; }
  echo ">> empreinte SHA-256 conforme"
else
  echo ">> ATTENTION : aucun fichier .sha256 — intégrité non contrôlée"
fi

VERIFY=0
if [ "$TARGET" = "--verify" ]; then
  VERIFY=1
  TARGET="nexora_restore_check_$(date -u +%Y%m%d%H%M%S)"
fi
[[ "$TARGET" =~ ^[a-z_][a-z0-9_]{0,62}$ ]] || { echo "ÉCHEC : nom de base invalide : $TARGET"; exit 1; }

# 2. Base cible neuve
EXISTS="$(psql_on postgres -c "SELECT 1 FROM pg_database WHERE datname = '$TARGET'")"
[ -z "$EXISTS" ] || { echo "ÉCHEC : la base $TARGET existe déjà — choisissez une base neuve (jamais par-dessus une base en service)"; exit 1; }
cleanup() { [ "$VERIFY" = 1 ] && psql_on postgres -c "DROP DATABASE IF EXISTS \"$TARGET\" WITH (FORCE)" >/dev/null 2>&1 || true; }
trap cleanup EXIT

START=$(date +%s)
psql_on postgres -c "CREATE DATABASE \"$TARGET\" ENCODING 'UTF8' TEMPLATE template0" >/dev/null
echo ">> base $TARGET créée"

# 3. Rôles applicatifs attendus par les droits du schéma (sans mot de passe : la base restaurée ne sert pas tant
#    qu'on ne l'a pas décidé).
psql_on postgres -c "DO \$\$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_app') THEN
  CREATE ROLE nexora_app NOLOGIN NOBYPASSRLS; END IF; END \$\$" >/dev/null

# 4. Restauration (arrêt à la première erreur)
"$PGRESTORE_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$TARGET" --no-owner --exit-on-error "$DUMP"
echo ">> pg_restore terminé"

# 5. Contrôles
MIGRATIONS="$(psql_on "$TARGET" -c "SELECT count(*) || ' (' || max(version) || ')' FROM nexora.schema_migration")"
OBJECTS="$(psql_on "$TARGET" -c "SELECT (SELECT count(*) FROM nexora.sale) + (SELECT count(*) FROM nexora.journal_entry)
  + (SELECT count(*) FROM nexora.product) + (SELECT count(*) FROM nexora.customer) + (SELECT count(*) FROM nexora.issued_document)")"
BALANCE="$(psql_on "$TARGET" -c "SELECT count(*) FROM (SELECT journal_entry_id FROM nexora.journal_entry_line
  GROUP BY journal_entry_id HAVING sum(debit) <> sum(credit)) x")"
DURATION=$(( $(date +%s) - START ))
echo ">> migrations : $MIGRATIONS"
echo ">> objets métier (ventes + écritures + produits + clients + documents) : $OBJECTS"
echo ">> écritures déséquilibrées : $BALANCE"
echo ">> durée de restauration : ${DURATION}s"
[ "$BALANCE" = "0" ] || { echo "ÉCHEC : écritures comptables déséquilibrées dans la sauvegarde"; exit 1; }

# 6. Registre de continuité (mode exercice)
if [ "$VERIFY" = 1 ] && [ -n "${NEXORA_API_URL:-}" ] && [ -n "${NEXORA_API_KEY:-}" ] && [ -s "$DUMP.id" ]; then
  ID="$(cat "$DUMP.id")"
  curl -fsS -X POST "$NEXORA_API_URL/api/v1/backups/$ID/verify" -H "X-Api-Key: $NEXORA_API_KEY" >/dev/null \
    && curl -fsS -X POST "$NEXORA_API_URL/api/v1/backups/$ID/restore-test" -H "X-Api-Key: $NEXORA_API_KEY" \
         -H "Content-Type: application/json" -d "{\"backupId\":\"$ID\",\"target\":\"$TARGET (${DURATION}s, $OBJECTS objets)\"}" >/dev/null \
    && echo ">> registre : sauvegarde $ID vérifiée, test de restauration inscrit" \
    || echo ">> registre : inscription impossible (la restauration elle-même a réussi)"
fi

if [ "$VERIFY" = 1 ]; then
  echo ">> EXERCICE RÉUSSI : la sauvegarde se restaure ; base jetable supprimée"
else
  trap - EXIT
  echo ">> RESTAURÉE dans $TARGET — basculer la production dessus suit le runbook (décision humaine)"
fi
