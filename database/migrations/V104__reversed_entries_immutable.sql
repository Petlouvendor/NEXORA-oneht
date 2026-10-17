-- =============================================================================
-- NEXORA — V104 — Écritures extournées immuables (audit 2026-10 éd. 2, A-043)
--
-- L'extourne passe l'écriture d'origine en « reversed » et crée son miroir en « posted ». Les états ne comptaient
-- que « posted » : l'origine disparaissait et le miroir restait, l'effet net d'une écriture extournée devenait
-- l'opposé de l'écriture. L'application compte désormais « posted » et « reversed » (LedgerStatuses.InLedger) ; une
-- écriture extournée fait donc partie du grand livre et doit être aussi intouchable qu'une écriture validée.
--
-- Avant : une écriture « reversed » pouvait changer de date, de période, de journal ou de statut, et ses lignes
-- pouvaient être modifiées ou supprimées ; une écriture « posted » pouvait passer en « cancelled » (sortie du grand
-- livre sans extourne — transition qu'aucun code n'utilise).
-- Après : « posted » → « reversed » seulement ; « reversed » n'évolue plus ; lignes figées dans les deux cas.
-- Idempotente (CREATE OR REPLACE ; les déclencheurs existants de V019 appellent ces fonctions).
-- =============================================================================

CREATE OR REPLACE FUNCTION nexora.journal_entry_immutability()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status IN ('posted','reversed') AND NOT nexora.flag('allow_hard_delete') THEN
            RAISE EXCEPTION 'Écriture % validée : suppression interdite (extourne requise)', OLD.id
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN OLD;
    END IF;

    IF OLD.status IN ('posted','reversed') THEN
        -- seule transition autorisée : posted → reversed (extourne) ; une écriture extournée n'évolue plus
        IF NEW.status <> OLD.status AND NOT (OLD.status = 'posted' AND NEW.status = 'reversed') THEN
            RAISE EXCEPTION 'Transition de statut interdite pour une écriture validée : % → %', OLD.status, NEW.status
                USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.company_id            <> OLD.company_id
        OR NEW.journal_id            <> OLD.journal_id
        OR NEW.accounting_period_id  <> OLD.accounting_period_id
        OR NEW.entry_number          <> OLD.entry_number
        OR NEW.entry_date            <> OLD.entry_date THEN
            RAISE EXCEPTION 'Champs d''une écriture validée non modifiables (extourne requise)'
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION nexora.journal_entry_line_immutability()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
    SELECT status INTO v_status FROM nexora.journal_entry
    WHERE id = coalesce(NEW.journal_entry_id, OLD.journal_entry_id);

    IF v_status IN ('posted','reversed') AND NOT nexora.flag('allow_hard_delete') THEN
        RAISE EXCEPTION 'Lignes d''une écriture validée non modifiables (extourne requise)'
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN coalesce(NEW, OLD);
END;
$$;
