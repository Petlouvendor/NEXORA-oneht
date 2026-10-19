-- =============================================================================
-- NEXORA — V108 — Intégrité : constats faibles de l'audit (audit 2026-10 éd. 2, lot 7)
--
-- * Rapprochement bancaire : un mouvement de trésorerie n'est rapproché que d'une seule ligne de relevé. Seul un
--   contrôle préalable, sans verrou, l'empêchait : deux rapprochements simultanés du même mouvement passaient.
--   Vérifié avant création : aucun doublon dans nexora, nexora_recette, nexora_api_it (2026-10-18).
-- * Catalogue des indicateurs : les formules décrivaient des sommes sans conversion, alors que les montants sont
--   convertis en devise de l'entreprise depuis le lot 4 (A-046).
-- * Suspens de trésorerie : un passage en perte ou profit référence l'écriture validée qui le constate.
-- Idempotente.
-- =============================================================================

DROP INDEX IF EXISTS nexora.bank_statement_line_matched_transaction_id_fk_idx;
CREATE UNIQUE INDEX IF NOT EXISTS bank_statement_line_matched_transaction_uk
    ON nexora.bank_statement_line (matched_transaction_id) WHERE matched_transaction_id IS NOT NULL;

UPDATE nexora.kpi_definition SET formula = 'Σ (subtotal − discount_total) − retours nets, convertis en devise de l''entreprise au taux de la date de vente' WHERE key = 'revenue';
UPDATE nexora.kpi_definition SET formula = 'Σ amount (période, statut approuvé/payé), converti en devise de l''entreprise au taux de la date de la dépense' WHERE key = 'expenses';
UPDATE nexora.kpi_definition SET formula = 'Σ current_balance (comptes actifs caisse/banque/mobile), convertis en devise de l''entreprise au taux du jour' WHERE key = 'cash';
UPDATE nexora.kpi_definition SET formula = 'Σ remaining_amount (OPEN/PARTIALLY_PAID/OVERDUE), convertis en devise de l''entreprise au taux du jour' WHERE key = 'open_receivables';
UPDATE nexora.kpi_definition SET formula = 'Σ remaining_amount (OPEN/PARTIALLY_PAID/OVERDUE), convertis en devise de l''entreprise au taux du jour' WHERE key = 'open_payables';
UPDATE nexora.kpi_definition SET formula = 'Σ amount (période), converti en devise de l''entreprise au taux de la date du paiement' WHERE key = 'cash_inflows';
UPDATE nexora.kpi_definition SET formula = 'Σ amount (période), converti en devise de l''entreprise au taux de la date du paiement' WHERE key = 'cash_outflows';

ALTER TABLE nexora.treasury_suspense_item
    ADD COLUMN IF NOT EXISTS journal_entry_id uuid REFERENCES nexora.journal_entry(id);
CREATE INDEX IF NOT EXISTS treasury_suspense_item_journal_entry_id_fk_idx
    ON nexora.treasury_suspense_item (journal_entry_id) WHERE journal_entry_id IS NOT NULL;
