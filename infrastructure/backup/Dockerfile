# NEXORA ONE — image de sauvegarde (finalisation phase 18) : outils PostgreSQL 18 (pg_dump / pg_restore de la même
# version que le serveur), curl pour l'enregistrement au registre de continuité, scripts de sauvegarde/restauration.
# Contexte de build : racine du dépôt.
FROM postgres:18
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY database/scripts/backup.sh database/scripts/backup-loop.sh database/scripts/restore.sh /scripts/
ENTRYPOINT ["bash", "/scripts/backup-loop.sh"]
