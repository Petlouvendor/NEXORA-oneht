#!/usr/bin/env bash
# Sauvegarde quotidienne en continu (finalisation phase 18) — service « backup » de docker-compose.prod.yml.
# Une sauvegarde au démarrage si aucune n'a moins de 24 h, puis une par jour à NEXORA_BACKUP_HOUR_UTC ;
# suppression des sauvegardes plus anciennes que NEXORA_BACKUP_RETENTION_DAYS.
set -euo pipefail

[ -n "${PGPASSWORD_FILE:-}" ] && export PGPASSWORD="$(cat "$PGPASSWORD_FILE")"
if [ -n "${NEXORA_API_KEY_FILE:-}" ] && [ -s "$NEXORA_API_KEY_FILE" ]; then
  export NEXORA_API_KEY="$(cat "$NEXORA_API_KEY_FILE")"
fi
DB="${NEXORA_DB:-nexora}"
DIR="${NEXORA_BACKUP_DIR:-/backups}"
RETENTION="${NEXORA_BACKUP_RETENTION_DAYS:-14}"
HOUR="${NEXORA_BACKUP_HOUR_UTC:-6}"
SCRIPTS="$(cd "$(dirname "$0")" && pwd)"

run_once() {
  if bash "$SCRIPTS/backup.sh" "$DB" "$DIR"; then
    find "$DIR" -maxdepth 1 -name "nexora_${DB}_*" -mtime "+$RETENTION" -print -delete | sed 's/^/>> purgé (rétention) : /'
  else
    echo ">> ÉCHEC de la sauvegarde $(date -u +%FT%TZ) — nouvelle tentative au prochain passage" >&2
  fi
}

mkdir -p "$DIR"
if [ -z "$(find "$DIR" -maxdepth 1 -name "nexora_${DB}_*.dump" -mmin -1440 -print -quit)" ]; then
  run_once
fi
while true; do
  NOW=$(date -u +%s)
  NEXT=$(date -u -d "today ${HOUR}:00" +%s)
  [ "$NEXT" -le "$NOW" ] && NEXT=$(date -u -d "tomorrow ${HOUR}:00" +%s)
  echo ">> prochaine sauvegarde : $(date -u -d "@$NEXT" +%FT%TZ)"
  sleep $(( NEXT - NOW ))
  run_once
done
