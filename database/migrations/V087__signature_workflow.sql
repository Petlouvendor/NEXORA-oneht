-- =============================================================================
-- NEXORA — V087 — Workflow de signature GED (finalisation produit, phase 10)
--
-- Document → demande de signature → destinataire → signature / refus → archivage.
--  * La demande fige la VERSION à signer : une signature porte toujours sur la version demandée.
--  * Qui a refusé / annulé, et qui a recueilli la signature d'un signataire externe (signature recueillie
--    en présence ou sur papier : NEXORA n'envoie pas de lien de signature externe).
--  * Une seule demande en cours par document.
--  * Modèles de notification : le destinataire est prévenu quand c'est son tour ; le demandeur à l'issue.
-- Idempotente.
-- =============================================================================
ALTER TABLE nexora.document_signature_request
    ADD COLUMN IF NOT EXISTS version_id    uuid REFERENCES nexora.document_version(id),
    ADD COLUMN IF NOT EXISTS message       text,
    ADD COLUMN IF NOT EXISTS cancelled_by  uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS cancelled_at  timestamptz;

ALTER TABLE nexora.document_signer
    ADD COLUMN IF NOT EXISTS recorded_by uuid REFERENCES nexora.app_user(id),
    ADD COLUMN IF NOT EXISTS declined_at timestamptz;

-- Demandes existantes : version courante du document au moment de la migration.
UPDATE nexora.document_signature_request r SET version_id = d.current_version_id
FROM nexora.document d WHERE d.id = r.document_id AND r.version_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS document_signature_request_one_pending_uk
    ON nexora.document_signature_request (document_id) WHERE status = 'pending';

INSERT INTO nexora.message_template (company_id, key, channel, locale, subject, body, description, variables) VALUES
(NULL, 'ged.signature_requested', 'in_app', 'fr',
 'Signature demandée — {{document.name}}',
 '{{requester.name}} vous demande de signer « {{document.name}} » (version {{document.version}}).{{request.message}}',
 'GED — c''est au tour du destinataire de signer',
 '["document.name","document.version","requester.name","request.message"]'::jsonb),
(NULL, 'ged.signature_outcome', 'in_app', 'fr',
 '{{outcome.title}} — {{document.name}}',
 '{{outcome.detail}}',
 'GED — demande de signature signée par tous, refusée ou expirée',
 '["outcome.title","document.name","outcome.detail"]'::jsonb)
ON CONFLICT ON CONSTRAINT message_template_key_uk DO NOTHING;
