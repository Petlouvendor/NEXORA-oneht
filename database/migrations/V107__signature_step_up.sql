-- =============================================================================
-- NEXORA — V107 — Confirmation forte à la signature interne (audit 2026-10 éd. 2, lot 7)
--
-- La signature interne d'un document GED reposait sur la seule session : un poste resté ouvert suffisait à signer.
-- Nouveau motif de confirmation forte « signature » (StepUpPurposes.Signature).
-- Idempotente.
-- =============================================================================

ALTER TABLE nexora.step_up_challenge DROP CONSTRAINT IF EXISTS step_up_challenge_purpose_chk;
ALTER TABLE nexora.step_up_challenge ADD CONSTRAINT step_up_challenge_purpose_chk CHECK (purpose IN (
    'payment','cancellation','accounting_validation','permission_change','deletion','sensitive_data',
    'period_close','period_reopen','financial_exceptional','consolidation_publish','device_revoke','sensitive_export',
    'signature','other'));
