-- =============================================================================
-- NEXORA — V103 — Ouverture de l'exercice suivant (audit 2026-10 éd. 2, A-042)
--
-- Seul l'amorçage (V072) créait un exercice : au premier jour de l'année suivante, toute écriture — donc toute vente —
-- était refusée faute de période. L'application ouvre désormais l'exercice suivant de trois façons : à la demande
-- (écran des périodes), par le planificateur dans le mois qui précède la fin de l'exercice, et en dernier recours au
-- moment où une opération du jour tombe après le dernier exercice. Cette migration déclare le job planifié.
-- Idempotente.
-- =============================================================================

INSERT INTO nexora.job_definition (key, name, category, description, owner) VALUES
    ('accounting.open_next_year', 'Ouverture de l''exercice suivant', 'business',
     'Ouvre l''exercice comptable suivant quand le dernier se termine dans moins de 31 jours, pour qu''aucune écriture ne soit refusée au changement d''année.',
     'scheduler')
ON CONFLICT (key) DO NOTHING;
