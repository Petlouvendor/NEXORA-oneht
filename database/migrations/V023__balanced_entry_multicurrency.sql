-- =============================================================================
-- NEXORA — V023 — Équilibre des écritures multi-devises (Phase 4 §4, §22)
-- L'équilibre réglementaire porte sur la devise de référence (base_debit/base_credit).
-- L'équilibre en devise de transaction n'est exigé que pour une écriture mono-devise
-- (une écriture de règlement avec différence de change mélange légitimement les devises).
-- =============================================================================
SET search_path TO nexora, public;

CREATE OR REPLACE FUNCTION nexora.assert_entry_balanced(p_entry uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_status     text;
    v_debit      nexora.money_amount;
    v_credit     nexora.money_amount;
    v_bdebit     nexora.money_amount;
    v_bcredit    nexora.money_amount;
    v_lines      integer;
    v_currencies integer;
BEGIN
    SELECT status INTO v_status FROM nexora.journal_entry WHERE id = p_entry;
    IF NOT FOUND OR v_status <> 'posted' THEN
        RETURN;
    END IF;

    SELECT coalesce(sum(debit),0), coalesce(sum(credit),0),
           coalesce(sum(base_debit),0), coalesce(sum(base_credit),0),
           count(*), count(DISTINCT currency_id)
    INTO v_debit, v_credit, v_bdebit, v_bcredit, v_lines, v_currencies
    FROM nexora.journal_entry_line WHERE journal_entry_id = p_entry;

    IF v_lines < 2 THEN
        RAISE EXCEPTION 'Écriture % : au moins 2 lignes requises', p_entry
            USING ERRCODE = 'check_violation';
    END IF;

    IF v_bdebit <> v_bcredit THEN
        RAISE EXCEPTION 'Écriture % non équilibrée en devise de référence : débit % <> crédit %',
            p_entry, v_bdebit, v_bcredit
            USING ERRCODE = 'check_violation';
    END IF;

    IF v_currencies = 1 AND v_debit <> v_credit THEN
        RAISE EXCEPTION 'Écriture % non équilibrée : débit % <> crédit %', p_entry, v_debit, v_credit
            USING ERRCODE = 'check_violation';
    END IF;
END;
$$;
