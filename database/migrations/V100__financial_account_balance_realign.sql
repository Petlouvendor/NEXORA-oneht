-- =============================================================================
-- NEXORA — V100 — Réalignement des soldes des comptes financiers (audit 2026-10, A-018)
--
-- Le solde stocké (financial_account.current_balance) était mis à jour par lecture, ajout en mémoire puis écriture :
-- deux opérations simultanées sur le même compte pouvaient perdre une mise à jour, sans erreur. L'application ajoute
-- désormais le montant en base (current_balance = current_balance + montant). Cette migration remet d'aplomb les
-- comptes qui auraient déjà dérivé : solde = solde d'ouverture + somme des transactions de trésorerie, la seule
-- définition du solde dans NEXORA (les transactions, elles, n'ont jamais été perdues).
-- Ne touche que les comptes en écart. Idempotente.
-- =============================================================================
SELECT set_config('nexora.bypass_rls', 'on', true);

UPDATE nexora.financial_account fa
   SET current_balance = fa.opening_balance + t.total,
       updated_at      = now()
  FROM (SELECT a.id, COALESCE(SUM(tx.amount), 0) AS total
          FROM nexora.financial_account a
          LEFT JOIN nexora.financial_transaction tx ON tx.financial_account_id = a.id
         GROUP BY a.id) t
 WHERE t.id = fa.id
   AND fa.current_balance <> fa.opening_balance + t.total;
