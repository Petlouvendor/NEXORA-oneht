# NEXORA ONE — Grille de recette (Phase 55) — résultats

Grille officielle de la Phase 55 exécutée ligne par ligne. Chaque statut renvoie à une preuve
**qui existe dans le dépôt ou qui a été exécutée pendant la recette**. Aucun statut PASS n'a été
attribué sur la seule lecture du code.

## 4. Environnement de recette

| Élément | Valeur |
|---|---|
| Version NEXORA ONE | V1.0 RC — base `V001`–`V063` |
| Date | 2026-09-22 |
| Testeur | Claude (agent de développement), à contre-signer par la QA |
| Développeur | Claude (agent de développement) |
| Environnement | DEV (poste local) |
| Frontend | React 19 + TypeScript (Vite), `http://localhost:5173` |
| Backend | ASP.NET Core .NET 10, `http://localhost:5080` |
| Base | PostgreSQL 18.6, `localhost:5432` |
| OS | Windows 11 Pro 26200 |
| Navigateur | Chromium sans interface (Playwright / patchright) |
| Mode | On-premise (poste unique) — cloud et hybride **non testés** |
| Version API | `v1` |
| Version DB | `V063` — 251 tables, 212 sous RLS, 63 migrations |

## Statuts utilisés

`PASS` preuve disponible · `FAIL` absent, cassé ou incorrect · `PARTIAL` partiellement implémenté,
**ou implémenté sans preuve automatisée** (la règle §1 interdit alors PASS) · `BLOCKED` prérequis
manquant · `N/A` justifié · `RETEST → PASS` défaut trouvé par cette recette, corrigé puis rejoué.

Emplacement des preuves : `IT` = `backend/tests/Nexora.IntegrationTests` (base PostgreSQL réelle,
clonée par exécution) · `API` = `backend/tests/Nexora.ApiTests` (HTTP réel) · `FE` =
`frontend/nexora-web/src/**/*.test.ts` · `NAV` = parcours navigateur exécuté le 2026-09-22 ·
`MAN` = opération manuelle exécutée et tracée dans ce document.

## 5. Jeu de données obligatoire

| Exigence | État | Commentaire |
|---|---|---|
| 2 entreprises minimum | PASS | Démo ABC GROUP : 3 entreprises (pharmacie, restaurant, supermarché) |
| 3 rôles minimum | PASS | 12 rôles système amorcés ; 4 comptes démo (direction, comptable, caissier, plateforme) |
| Utilisateurs aux permissions différentes | PASS | direction (tout), caissier (pharmacie seule, sans `pos.change_price`) |
| 30 produits variés | RETEST → PASS | jeu de recette : 32 produits au Supermarché, 16 à la Pharmacie (rotations, marges, TVA, pesés, lots) |
| Fournisseurs et délais | RETEST → PASS | 8 fournisseurs, délais de 2 à 14 jours, dont 1 sans historique |
| Clients normaux / crédit / retard | RETEST → PASS | 14 clients : comptant, bon payeur, en retard, proche de la limite, bloqué, inactif, gros compte, sans historique |
| Ventes sur plusieurs périodes | RETEST → PASS | ~100 jours de ventes datées, tendances contrastées (hausse / baisse) |
| Achats, réceptions, retours, paiements, dépenses | PASS | jeu de recette : 3 vagues d'achats datées, réceptions partielles, factures en écart, retours, règlements, dépenses (BUG-016 : registre des dépenses vide) |
| Données provoquant des alertes / Intelligence | RETEST → PASS | stock bas, lots expirés / proches, dormants, créances échues, hausse fournisseur, hausse / baisse des ventes, client inactif |
| Jeu conservé pour les régressions | RETEST → PASS | **BUG-015 corrigé** : `database/scripts/recette.sh` reconstruit à l'identique la base `nexora_recette` (graine fixe) — voir [`phase-55-jeu-de-donnees-recette.md`](phase-55-jeu-de-donnees-recette.md) |

Les suites automatisées ne dépendent pas de ce jeu : chaque test prépare ses données sur une base
clonée. Le jeu sert à la recette **manuelle**, aux démonstrations et aux régressions ; sa génération
par les services réels a révélé cinq défauts (BUG-016 à BUG-020).

## 6. Authentification et sessions

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| AUTH-001 | PASS | | API `SmokeTests`, tous les tests API (connexion réelle) ; NAV connexion |
| AUTH-002 | PASS | | IT `MfaAuthenticationTests.Five_bad_passwords_lock_the_account_temporarily` |
| AUTH-003 | PARTIAL | Moyenne | `LoginHandler` renvoie le même message générique que pour un mauvais mot de passe ; pas de test dédié |
| AUTH-004 | RETEST → PASS | Haute | **BUG-003 corrigé.** API `RecetteEndpointsTests.Logout_revokes_this_token_immediately_but_not_other_sessions` ; NAV : 200 avant, 401 après déconnexion |
| AUTH-005 | PASS | | API `RecetteEndpointsTests.New_routes_require_authentication`, `OperationsEndpointsTests.System_status_requires_authentication` |
| AUTH-006 | PARTIAL | Moyenne | `ValidateLifetime = true`, jeton 60 min, tolérance 30 s (Program.cs) ; aucun test avec jeton expiré |
| AUTH-007 | RETEST → PASS | Haute | **BUG-004 corrigé.** IT `RecetteCorrectionsTests.Changing_the_password_requires_the_current_one_and_ends_every_session` |
| AUTH-008 | RETEST → PASS | Haute | **BUG-004 corrigé.** IT `…Password_reset_is_emailed_single_use_and_never_reveals_whether_an_account_exists`, `…Reset_requests_are_throttled_and_expired_codes_are_refused` ; API `…Forgot_password_answers_the_same_for_an_unknown_account` ; NAV lien `/reset-password?token=` |
| AUTH-009 | PASS | | IT `IdentityAccessTests` (suspension → accès retiré) ; middleware refuse tout jeton d'un compte non actif |
| AUTH-010 | RETEST → PASS | Haute | Verrouillage par compte : IT `Five_bad_passwords_lock…`. **BUG-006 corrigé** (débit par IP) : API `…Anonymous_auth_endpoints_are_rate_limited_per_client` (10 → 429) |

## 7. Rôles et permissions

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| RBAC-001 | RETEST → PASS | Haute | Rôle : IT `AdministrationTests.Custom_role_is_created_and_previews_its_permissions`. **BUG-005 corrigé** (création d'utilisateur) : IT `RecetteCorrectionsTests.A_user_is_created_with_a_first_assignment…` ; NAV onglet Utilisateurs |
| RBAC-002 | PASS | | IT `IdentityAccessTests.Temporary_role_assignment_expires_on_its_own` |
| RBAC-003 | PARTIAL | Moyenne | L'auditeur n'a que des permissions `*.view` et chaque écriture exige une permission serveur ; pas de test « auditeur tente d'écrire » |
| RBAC-004 | PASS | | API `SecurityGovernanceEndpointsTests.Platform_only_security_registries_are_reserved_to_platform_staff` (403) |
| RBAC-005 | PASS | | IT `DataModelIntegrityTests.Validated_stock_movement_cannot_be_hard_deleted`, `SecurityAuditTests.Audit_log_cannot_be_modified` |
| RBAC-006 | PASS | | IT `IdentityAccessTests` (affectation puis retrait → droits recalculés) |
| RBAC-007 | PASS | | API 403 ci-dessus ; IT `…Creating_a_user_more_powerful_than_oneself_is_refused_and_leaves_no_account` |
| RBAC-008 | PASS | | IT `SecurityAuditTests.Refund_above_threshold_requires_two_distinct_approvers`, `…Step_up_challenge_issues_one_time_token` |

## 8. Multi-entreprise / isolation

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| TENANT-001 | PASS | | IT `GroupConsolidationTests` (création d'entreprise dans le groupe) |
| TENANT-002 | PASS | | idem ; IT `DataModelIntegrityTests.Independent_company_needs_no_group` |
| TENANT-003 | PASS | | API `MultiTenantIsolationTests` (lecture dans sa propre entreprise) |
| TENANT-004 | PASS | | API `MultiTenantIsolationTests` (liste, recherche, incidents) ; API `RecetteEndpointsTests.Customer_card_is_readable_in_its_company_and_not_found_from_another` |
| TENANT-005 | PASS | | API `…A_product_readable_in_its_own_company_returns_not_found_under_another_companys_scope` (404) ; IT `RecetteCorrectionsTests.A_customer_of_one_company_is_invisible_from_another` |
| TENANT-006 | PASS | | IT `DashboardReportingTests.Group_dashboard_excludes_unauthorized_company` |
| TENANT-007 | PASS | | même jeton, entreprise active changée par `X-Company-Id` ; entreprise non autorisée → 403 `company_not_authorized` |
| TENANT-008 | PASS | | IT `SecurityAuditTests.Audit_records_price_change_with_actor_company_and_before_after` |

Signal complémentaire : point de checklist `multi_tenant_isolation` (le rôle `nexora_app` n'a ni
`BYPASSRLS` ni superutilisateur — vérifié en base le 2026-09-22).

## 9. Produits et catalogue

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| PROD-001 | PASS | | IT `CatalogPricingTests.Category_and_price_list_creation_are_scoped_and_unique` |
| PROD-002 | PASS | | IT `CatalogInventoryTests.Create_product_with_sku_and_barcode` |
| PROD-003 | PASS | | IT `CatalogInventoryTests` (`catalog.sku_exists`) |
| PROD-004 | RETEST → PASS | Haute | **BUG-007 corrigé.** IT `RecetteCorrectionsTests.Product_details_can_be_updated_but_the_sku_never_changes` ; UI « Modifier la fiche » |
| PROD-005 | RETEST → PASS | Haute | IT `CatalogPricingTests.Base_price_and_quantity_tier_are_explained`, `…Historical_price_is_preserved_after_a_revision` ; **BUG-017 corrigé** (un prix USD fermait le prix HTG) : IT `RecetteDatasetFindingsTests.A_price_in_another_currency_does_not_close_the_existing_price` |
| PROD-006 | PARTIAL | Moyenne | Transitions testées (`Product_workflow_enforces_allowed_transitions`) ; « produit inactif non vendable à la caisse » non testé |
| PROD-007 | PASS | | IT `CatalogPricingTests.Search_is_tolerant_and_scan_returns_one_exact_match` |
| PROD-008 | PARTIAL | Basse | filtre `categoryId` présent sur `GET /catalog/products`, non testé |
| PROD-009 | PASS | | IT `MigrationImportTests.Product_import_autocreates_category_and_unit` (+ erreurs ligne par ligne) |
| PROD-010 | **FAIL** | Moyenne | **BUG-011** — aucun export du catalogue |

## 10. Stock et inventaire

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| STOCK-001 | PASS | | IT `MigrationImportTests.Opening_stock_posts_an_opening_movement` |
| STOCK-002 | PASS | | IT `PosSalesTests.Cash_sale_decrements_stock_moves_cash_and_posts_balanced_entry` |
| STOCK-003 | PASS | | IT `CatalogInventoryTests.Receive_goods_updates_stock_and_average_cost` |
| STOCK-004 | PASS | | IT `PosSalesTests.Partial_return_restocks_and_posts_correction_entry` |
| STOCK-005 | PASS | | IT `CatalogInventoryTests.Supplier_return_reduces_stock_and_posts_entry` |
| STOCK-006 | PASS | | IT `CatalogInventoryTests.Loss_writes_off_stock_and_posts_balanced_entry` (pas d'ajustement libre : toute correction passe par perte ou inventaire, par conception) |
| STOCK-007 | PASS | | IT `CatalogInventoryTests.Inventory_count_variance_posts_adjustment_and_balanced_revaluation` |
| STOCK-008 | PASS | | idem |
| STOCK-009 | PASS | | IT `StockLifecycleTests.Negative_stock_is_refused_by_default_and_allowed_by_setting` |
| STOCK-010 | PASS | | IT `StockLifecycleTests.Stock_alerts_flag_low_stock_and_near_expiry` |
| STOCK-011 | PASS | | IT `CatalogInventoryTests.Reorder_suggestion_appears_when_stock_below_point` |
| STOCK-012 | PARTIAL | Moyenne | vélocité × délai fournisseur dans la règle `stock_reorder` ; aucune formule de stock de sécurité documentée ni testée |
| STOCK-013 | PARTIAL | Moyenne | score de stock explicable (`Scoring_computes_explainable_stock_and_credit_scores`) ; KPI de rotation non testé |
| STOCK-014 | PASS | | IT `IntelligenceEngineTests.Generation_stock_reorder_and_dormant_explain_confidence_and_match_scenario` |
| STOCK-015 | PASS | | idem (règle produit dormant / surstock) |
| STOCK-016 | PASS | | IT `StockLifecycleTests.Stock_movements_query_filters_and_paginates` ; mouvements non supprimables |

## 11. POS / ventes / caisse

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| POS-001 | PASS | | IT `PosSalesTests.Session_open_sell_close_with_variance` |
| POS-002 | RETEST → PASS | Haute | IT `PosSalesTests.Cash_sale_…` ; le serveur recalcule chaque ligne ; **BUG-019 corrigé** (article sans prix vendu à 0) : `sale.no_price`, IT `RecetteDatasetFindingsTests.An_article_without_a_price_is_refused_…` |
| POS-003 | PARTIAL | Basse | panier géré par l'écran ; total recalculé par le serveur à l'encaissement ; pas de test d'écran |
| POS-004 | PARTIAL | Basse | idem |
| POS-005 | PASS | | IT `PosSalesTests.Sale_with_line_discount_reduces_total` |
| POS-006 | RETEST → PASS | **Critique** | **BUG-001 corrigé.** IT `PosSalesTests.A_cashier_cannot_force_a_price_but_can_discount_within_the_cap`, `…Discounts_beyond_the_configured_cap_or_the_line_amount_are_refused_for_everyone` |
| POS-007 | PASS | | IT `PosSalesTests.Sale_with_tax_records_tax_amount` |
| POS-008 | PASS | | IT `PosSalesTests.Cash_sale_…` |
| POS-009 | PARTIAL | Moyenne | encaissement sur compte bancaire / moyen « carte » testé (`Multi_tender_sale_with_change`) ; aucun TPE réel |
| POS-010 | PASS | | IT `PosSalesTests.Multi_tender_sale_with_change` |
| POS-011 | PARTIAL | Moyenne | le reste dû devient une créance si client + crédit autorisé (`Credit_sale_creates_receivable_with_optional_initial_payment`) ; cas « espèces insuffisantes sans client » non testé |
| POS-012 | PASS | | IT `PosSalesTests.Multi_tender_sale_with_change` |
| POS-013 | PASS | | IT `CommercialDocumentsTests.Rendered_document_is_html_with_the_number` (format A4 / ticket) |
| POS-014 | PARTIAL | Haute | route `POST /pos/sales/{id}/void` (permission `pos.void_sale`) : seule une vente **en attente** s'annule, une vente validée est refusée (`pos.use_full_return`, §24) ; le jeu de recette contient les deux cas (MAN), **aucun test automatisé** (BUG-014) |
| POS-015 | PASS | | IT `PosSalesTests.Partial_return_restocks_and_posts_correction_entry` |
| POS-016 | RETEST → PASS | Haute | le retour total **multi-lignes** échouait (même cause que BUG-018) ; IT `RecetteDatasetFindingsTests.An_article_without_a_price_is_refused_and_a_multi_line_sale_can_be_fully_returned` |
| POS-017 | PASS | | IT `PosSalesTests.Session_open_sell_close_with_variance` |
| POS-018 | PASS | | IT `FinancialEngineTests.CashCount_shortage_posts_variance` |
| POS-019 | PASS | | IT `PosSalesTests.Credit_sale_creates_receivable_with_optional_initial_payment` |
| POS-020 | PASS | | IT `DashboardReportingTests.Drill_down_from_revenue_reaches_source_sale_lines` ; API `SmokeTests` |

## 12. Hors ligne et synchronisation

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| OFF-001 | PARTIAL | Moyenne | `useOnline` + bandeau ; pas de test d'écran |
| OFF-002 | PASS | | FE `offlineQueue.test.ts` ; IT `OfflineSyncTests.Offline_batch_syncs_and_creates_real_sales` |
| OFF-003 | PASS | | IT `OfflineSyncTests.Sync_center_summarizes_queue` |
| OFF-004 | PASS | | IT `OfflineSyncTests.Offline_batch_syncs_and_creates_real_sales` |
| OFF-005 | PASS | | IT `OfflineSyncTests.Duplicate_push_is_idempotent` |
| OFF-006 | PASS | | même idempotence par `operation_uuid` ; `Failed_operation_is_never_confirmed_as_a_sale` |
| OFF-007 | PASS | | IT `OfflineSyncTests.Stock_conflict_is_recorded_not_overwritten`, `…Resolve_conflict_records_decision` |
| OFF-008 | RETEST → PASS | Moyenne | **BUG-012 corrigé** (guide fonctionnel partie 2, étape G) : jeton et file hors ligne chiffrés AES-256-GCM, clé non exportable ; FE `secureStore.test.ts`, `offlineQueue.test.ts` ; recette navigateur |

Note : depuis la correction de BUG-001, une vente hors ligne dont la remise dépasse le plafond est
refusée à la synchronisation (opération `FAILED`, jamais confirmée comme vente).

## 13. Achats et fournisseurs

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| ACH-001 | RETEST → PASS | Haute | **BUG-002 corrigé.** IT `RecetteCorrectionsTests.Supplier_is_created_numbered_and_updated` ; UI onglet Fournisseurs |
| ACH-002 | PASS | | IT `PurchaseThreeWayMatchTests.Purchase_order_lifecycle_enforces_transitions` |
| ACH-003 | RETEST → PASS | **Critique** | **BUG-018 corrigé** — toute réception de plus d'une ligne échouait (UUID d'opération dupliqué sur les mouvements) ; les tests ne recevaient qu'une ligne. IT `RecetteDatasetFindingsTests.A_multi_line_receipt_is_posted_and_completes_the_order` ; **BUG-020 corrigé** (statut de commande rétrogradé) : `…A_receipt_not_imputed_to_order_lines_never_downgrades_the_order` |
| ACH-004 | PASS | | IT `CatalogInventoryTests.Partial_receipt_against_purchase_order` |
| ACH-005 | PASS | | IT `CatalogInventoryTests.Receive_goods_updates_stock_and_average_cost` |
| ACH-006 | PASS | | IT `PurchaseThreeWayMatchTests.Quantity_variance_is_flagged` |
| ACH-007 | PASS | | IT `CatalogInventoryTests.Supplier_return_reduces_stock_and_posts_entry` |
| ACH-008 | PASS | | IT `PurchaseThreeWayMatchTests.Three_way_match_within_tolerance_passes` |
| ACH-009 | PASS | | IT `CatalogInventoryTests.Credit_purchase_invoice_creates_payable_and_cash_purchase_moves_cash` |
| ACH-010 | PASS | | IT `PurchaseThreeWayMatchTests.Full_purchase_to_debt_settlement` |
| ACH-011 | PASS | | IT `PurchaseThreeWayMatchTests.Supplier_statement_lists_invoices_and_payments` |

## 14. Clients et crédits

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| CLI-001 | RETEST → PASS | Haute | **BUG-002 corrigé.** IT `RecetteCorrectionsTests.Customer_is_created_numbered_found_by_search_and_updated` ; NAV écran Clients |
| CLI-002 | PASS | | IT `CustomerCollectionsTests.Customer_statement_lists_charges_and_payments` |
| CLI-003 | PASS | | IT `PosSalesTests.Credit_sale_creates_receivable…` |
| CLI-004 | PASS | | IT `FinancialEngineTests.CreditSale_then_partial_then_final_payment_settles_receivable` |
| CLI-005 | PASS | | idem |
| CLI-006 | PASS | | IT `CustomerCollectionsTests.Collections_and_aging_reflect_overdue_receivables` |
| CLI-007 | PASS | | IT `PosSalesTests.Credit_over_limit_needs_override` ; IT `RecetteCorrectionsTests.A_cashier_can_create_a_customer_but_never_open_credit` |
| CLI-008 | PASS | | IT `Credit_over_limit_needs_override`, `CustomerCollectionsTests.Blocked_customer_cannot_buy_on_credit` |
| CLI-009 | PASS | | IT `DashboardReportingTests.Aging_receivables_reconciles_with_open_receivable_subledger` |
| CLI-010 | PASS | | IT `IntelligenceEngineTests.Generation_customer_inactive_flags_a_previously_active_customer_only` |

## 15. Finance et comptabilité

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| FIN-001 | PARTIAL | Moyenne | écriture et trésorerie justes (IT `FinancialEngineTests.Expense_reduces_cash_and_posts_balanced`) mais **BUG-016** : aucune ligne n'est créée dans le registre `expense` — les dépenses sont invisibles des rapports de dépenses |
| FIN-002 | PASS | | IT `FinancialEngineTests.CashSale_posts_balanced_entry_and_moves_cash` |
| FIN-003 | PASS | | IT `DashboardReportingTests.Command_center_revenue_reconciles_with_sales_journal` |
| FIN-004 | PARTIAL | Haute | KPI de marge présent (Phase 7) ; formule non vérifiée par un test dédié |
| FIN-005 | PARTIAL | Haute | résultat clôturé testé (`GeneralAccountingTests.Year_end_close_moves_the_result_to_equity`) ; bénéfice de période non vérifié indépendamment |
| FIN-006 | PASS | | IT `Aging_receivables_reconciles…` ; `ReportingTrustTests.Reconciliation_matches_a_credit_sale_on_a_private_period` |
| FIN-007 | PASS | | IT `FinancialEngineTests.SupplierInvoice_then_payments_settle_payable` |
| FIN-008 | PASS | | IT `FinancialEngineTests.CreditSale_then_partial_then_final…` |
| FIN-009 | PARTIAL | Moyenne | période close refusée (`Posting_into_a_closed_period_is_rejected`) ; filtres de période des rapports non testés |
| FIN-010 | PASS | | IT `FinancialEngineTests.CashSale_in_usd_is_converted_to_functional_currency`, `…Fx_gain_recognised…` |
| FIN-011 | PASS | | IT `CatalogPricingTests.Tax_inclusive_price_is_split_deterministically` (arrondi bancaire constant) |
| FIN-012 | PASS | | IT `DashboardReportingTests.Export_sales_journal_is_csv_financial_and_logged` |
| FIN-013 | PASS | | IT `FinancialEngineTests.Reversal_creates_mirror_entry_and_marks_source_reversed` |
| FIN-014 | RETEST → PASS | **Critique** | **BUG-001 corrigé** — prix forcé et remise contrôlés côté serveur : IT `PosSalesTests.No_price_can_go_below_the_floor_price_even_with_the_right_permission` + tests POS-006 |

## 16. Dashboard, rapports et exports

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| REP-001 | PASS | | IT `DashboardReportingTests.Command_center_revenue_reconciles_with_sales_journal` |
| REP-002 | PARTIAL | Moyenne | paramètres de période présents ; non testés |
| REP-003 | PASS | | IT `DashboardReportingTests.Group_dashboard_excludes_unauthorized_company` |
| REP-004 | PASS | | IT `Command_center_revenue_reconciles…` |
| REP-005 | PASS | | IT `ReportingTrustTests` (stock rapporté = grand livre de stock) |
| REP-006 | PARTIAL | Moyenne | relevé fournisseur testé ; pas de rapport d'achats réconcilié |
| REP-007 | PASS | | IT `Aging_receivables_reconciles…` |
| REP-008 | PASS | | IT `ReportingTrustTests.Reconciliation_matches_a_credit_sale_on_a_private_period` |
| REP-009 | PARTIAL | Moyenne | CSV : PASS (`Export_sales_journal_is_csv…`) ; Excel : non disponible |
| REP-010 | PARTIAL | Basse | route `GET /documents/{id}/pdf` ; rendu HTML testé, PDF non testé |
| REP-011 | PASS | | IT `StockLifecycleTests.Stock_movements_query_filters_and_paginates` ; pagination par curseur (`MarketplaceEcosystemTests`) |
| REP-012 | PARTIAL | Basse | recherche testée ; tri non testé |

## 17. Notifications et alertes

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| NOTIF-001 | PASS | | IT `StockLifecycleTests.Stock_alerts_flag_low_stock_and_near_expiry` ; `DashboardReportingTests.Alert_center_reports_only_validated_conditions` |
| NOTIF-002 | PARTIAL | Moyenne | priorité 1-5 portée par la notification ; priorisation de la rupture non testée |
| NOTIF-003 | PASS | | IT `TreasuryCockpitTests.Liquidity_flags_a_gap_when_commitments_exceed_available_cash` |
| NOTIF-004 | PASS | | IT `NotificationEngineTests.Marking_read_and_the_per_user_scope` |
| NOTIF-005 | PASS | | idem |
| NOTIF-006 | PARTIAL | Basse | filtres présents ; non testés |
| NOTIF-007 | PARTIAL | Basse | lien d'action stocké ; ouverture du bon objet non testée |
| NOTIF-008 | PASS | | IT `NotificationEngineTests.Marking_read_and_the_per_user_scope` |

## 18. Intelligence — recommandations

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| INT-001 | PASS | | IT `IntelligenceEngineTests.Generation_stock_reorder_and_dormant…` |
| INT-002 | PASS | | idem (vélocité + délai fournisseur) |
| INT-003 | PASS | | idem |
| INT-004 | PASS | | IT `…Generation_sales_spike_is_an_opportunity_not_a_risk` |
| INT-005 | PASS | | IT `…Generation_sales_decline_flags_a_risk_with_high_priority` |
| INT-006 | PARTIAL | Moyenne | détectée comme anomalie (`BiReportingTests.Insight_scan_detects_anomalies…`), pas comme recommandation |
| INT-007 | PARTIAL | Moyenne | idem (règle d'insight « hausse de dépenses ») ; ne peut pas se déclencher en usage réel tant que BUG-016 est ouvert (le jeu de recette le montre) |
| INT-008 | PASS | | IT `…Generation_receivable_priority_ranks_overdue_customer_without_credit_decision` |
| INT-009 | PASS | | IT `…Dashboard_counts_new_recommendations_by_priority_level` |
| INT-010 | PASS | | IT `…Generation_stock_reorder_and_dormant_explain_confidence…` |
| INT-011 | PASS | | idem (`evidence`) |
| INT-012 | PASS | | IT `…Recommendation_lifecycle_accept_creates_task_dismiss_snooze_and_resolve_outcome` |
| INT-013 | PASS | | IT `…Modify_keeps_the_original_proposal…` |
| INT-014 | PARTIAL | Basse | statut nommé `dismissed`, pas `IGNORED` (choix Phase 54 pour ne rien casser) |
| INT-015 | PASS | | IT `…Modify_keeps_the_original_proposal_and_expired_recommendations_leave_new_status` |
| INT-016 | PASS | | IT `…accept_creates_task…` |
| INT-017 | PASS | | `decided_by` / `decided_at` + déclencheur d'audit sur `recommendation` |
| INT-018 | PASS | | contrainte unique de déduplication ; IT `BiReportingTests.Insight_scan_detects_anomalies_then_dismiss_assign_and_dedup` |

## 19. Intelligence — analytics, prévisions, anomalies

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| ANL-001 | PASS | | IT `DashboardReportingTests.Sales_breakdown_covers_every_dimension` |
| ANL-002 | PASS | | idem |
| ANL-003 | PASS | | IT `…Generation_sales_spike…` (évolution exposée dans `evidence`) |
| ANL-004 | PARTIAL | Moyenne | pas de test de formule de rotation |
| ANL-005 | PARTIAL | Moyenne | jours de couverture non testés |
| ANL-006 | PARTIAL | Moyenne | voir STOCK-012 |
| ANL-007 | PASS | | IT `BiReportingTests.Forecast_generate_for_revenue_and_cash_and_rejects_invalid_input` |
| ANL-008 | PASS | | idem |
| ANL-009 | PASS | | idem (niveau de confiance porté par la prévision) |
| ANL-010 | PASS | | IT `BiReportingTests.Insight_scan_detects_anomalies…` |
| ANL-011 | PASS | | idem |
| ANL-012 | PASS | | IT `ReportingTrustTests.Negative_stock_is_flagged` |
| ANL-013 | PASS | | IT `DashboardReportingTests.Ask_nexora_answers_with_sources_and_no_fabricated_forecast` |

## 20. Assistant NEXORA

L'assistant est **déterministe** (aucun modèle de langage) : il répond à partir des tableaux de bord
du moteur métier.

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| AI-001 | PASS | | IT `Ask_nexora_answers_with_sources_and_no_fabricated_forecast` |
| AI-002 | PARTIAL | Moyenne | pas de test de question stock |
| AI-003 | PASS | | IT `IntelligenceEngineTests.Assistant_briefing_reuses_dashboards_per_role_and_query_is_logged` |
| AI-004 | PARTIAL | Moyenne | idem AI-002 pour les clients |
| AI-005 | PASS | | IT `…no_fabricated_forecast` |
| AI-006 | PARTIAL | Haute | briefing filtré par rôle ; pas de test « question sur une donnée interdite » |
| AI-007 | PASS | | réutilise les tableaux de bord Phase 7 (aucun calcul propre) |
| AI-008 | PASS | | l'explication d'une recommandation est son `why` / `evidence` stocké |
| AI-009 | PARTIAL | Basse | pas de test du message de limite |

## 21. API / backend

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| API-001 | PASS | | Swagger généré (`/swagger`) |
| API-002 | PASS | | API `RecetteEndpointsTests.Invalid_payloads_are_refused_without_writing_anything` |
| API-003 | PASS | | 401 / 403 / 404 / 409 / 422 / 429 vérifiés dans les tests API |
| API-004 | PASS | | API `New_routes_require_authentication` |
| API-005 | PASS | | API 403 (RBAC-004) |
| API-006 | PASS | | voir REP-011 |
| API-007 | PASS | | IT `Stock_movements_query_filters_and_paginates` |
| API-008 | PASS | | format RFC 7807 (`title`, `detail`, `correlationId`) vérifié dans les tests API |
| API-009 | PASS | | API `IdempotencyFilterTests` ; IT `PosSalesTests.Duplicate_checkout_operation_is_not_double_posted` |
| API-010 | PASS | | IT `FinancialEngineTests.Same_operation_uuid_is_not_double_posted` ; transaction unique par vente |
| API-011 | PARTIAL | Moyenne | journaux structurés + identifiant de corrélation ; absence de secret non vérifiée par un test |
| API-012 | PASS | | API `OperationsEndpointsTests.Ready_is_anonymous_and_reports_healthy` |

## 22. PostgreSQL / intégrité

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| DB-001 | PASS | | MAN 2026-09-22 : base vierge `nexora_recette_vide`, 63 migrations appliquées en 45 s, 251 tables |
| DB-002 | PASS | | MAN : second passage → 63 migrations ignorées, aucune modification |
| DB-003 | PASS | | IT `PurchaseThreeWayMatchTests.Duplicate_supplier_invoice_reference_is_rejected` ; `customer.number_exists` |
| DB-004 | PASS | | IT `DataModelIntegrityTests.Sale_line_cannot_cross_company_boundary` |
| DB-005 | PASS | | IT `DataModelIntegrityTests.Journal_entry_without_period_or_journal_is_rejected` |
| DB-006 | PASS | | IT `PosSalesTests.Two_registers_selling_same_stock_never_oversell` ; ventes en une transaction |
| DB-007 | PARTIAL | Moyenne | ~360 index ; aucun plan d'exécution mesuré sur volume |
| DB-008 | PARTIAL | Basse | `timestamptz` partout ; fuseau utilisateur (Phase 40) non testé bout-en-bout |
| DB-009 | PASS | | domaine `money_amount numeric(19,4)` ; tests de conversion et d'arrondi |
| DB-010 | PASS | | MAN : `pg_dump` format custom, 1,56 Mo, empreinte SHA-256 relevée |
| DB-011 | PASS | | MAN : `pg_restore` dans `nexora_restore_test` en 14 s — comptages identiques sur 8 tables clés, 0 écriture déséquilibrée, 212 politiques RLS restaurées |

## 23. Sécurité applicative

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| SEC-001 | PASS | | API `RecetteEndpointsTests.Sql_injection_strings_in_filters_are_treated_as_plain_text` ; SQL 100 % paramétré |
| SEC-002 | PARTIAL | Haute | React échappe l'affichage, aucun `dangerouslySetInnerHTML` ; rendu HTML des documents côté serveur non sondé |
| SEC-003 | PASS | | voir TENANT-005 |
| SEC-004 | PARTIAL | Moyenne | clé JWT et mots de passe **de développement** dans `appsettings.json` / tests ; la checklist `production_secrets` bloque le Go-Live tant qu'ils ne sont pas remplacés |
| SEC-005 | PASS | | IT `…A_user_is_created…` (bcrypt, jamais en clair) ; secret MFA chiffré AES-256-GCM (Phase 47) |
| SEC-006 | BLOCKED | Haute | pas d'environnement de production |
| SEC-007 | PARTIAL | Moyenne | origines listées par configuration (`Cors:Origins`) ; non testé |
| SEC-008 | PARTIAL | Moyenne | voir API-011 |
| SEC-009 | PASS | | MAN : `nexora_app` sans superutilisateur, `BYPASSRLS`, `CREATEROLE`, `CREATEDB` |
| SEC-010 | RETEST → PASS | Haute | voir AUTH-004 et AUTH-006 |
| SEC-011 | RETEST → PASS | Haute | **BUG-006 corrigé** — voir AUTH-010 |

## 24. Performance et charge

| ID | Statut | Preuve / notes |
|---|---|---|
| PERF-001 → PERF-008 | BLOCKED | Aucun outil de charge, aucun volume cible défini, aucun environnement représentatif. Seuls repères : restauration 14 s et migrations 45 s sur la base de démo — sans valeur pour la production |

## 25. UX / responsive / accessibilité

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| UX-001 | PARTIAL | Moyenne | NAV : `/login`, `/reset-password`, `/customers`, `/admin` sans erreur console ; les ~30 autres routes non parcourues |
| UX-002 | PARTIAL | Moyenne | messages serveur restitués dans les formulaires |
| UX-003 | PARTIAL | Basse | messages en français, non audités |
| UX-004 | PARTIAL | Basse | squelettes / « Chargement… » présents |
| UX-005 | PARTIAL | Basse | `EmptyState` utilisé ; non audité |
| UX-006 | PARTIAL | Moyenne | voir OFF-001 |
| UX-007 | PARTIAL | Basse | NAV bureau sur 4 écrans |
| UX-008 | BLOCKED | | aucun appareil tactile |
| UX-009 | PARTIAL | Moyenne | non audité |
| UX-010 | PARTIAL | Moyenne | thème contraste élevé (Phase 40) ; mesure WCAG non faite |
| UX-011 | PARTIAL | Moyenne | focus 3 px en contraste élevé ; non audité |
| UX-012 | RETEST → PASS | Moyenne | **BUG-008 corrigé** — chaque `Field` relie désormais son libellé à son champ ; NAV : champs retrouvés par leur libellé |

## 26. Audit et traçabilité

| ID | Statut | Sév. | Preuve / notes |
|---|---|---|---|
| AUDIT-001 | PASS | | IT `SecurityAuditTests.Audit_records_price_change_with_actor_company_and_before_after` |
| AUDIT-002 | PASS | | idem (avant / après) |
| AUDIT-003 | PASS | | déclencheur `audit_row` sur DELETE ; suppression physique interdite (RBAC-005) |
| AUDIT-004 | PASS | | écriture comptable systématique (`FinancialEngineTests`) |
| AUDIT-005 | PARTIAL | Moyenne | affectations tracées (`granted_by`, `revoked_by`) ; audit d'une modification de permissions d'un rôle non testé |
| AUDIT-006 | PASS | | événements `login.*`, `logout`, `password.*` dans `audit_log` ; IT `…Changing_the_password…` vérifie `password.changed` |
| AUDIT-007 | PASS | | voir INT-017 |
| AUDIT-008 | PASS | | voir INT-016 |

## 27. Intégration entre modules

| ID | Statut | Preuve |
|---|---|---|
| INTG-001 | PASS | IT `PosSalesTests.Cash_sale_decrements_stock…` |
| INTG-002 | PASS | idem (écriture équilibrée) |
| INTG-003 | PASS | IT `Credit_sale_creates_receivable…` |
| INTG-004 | PASS | IT `Receive_goods_updates_stock_and_average_cost` |
| INTG-005 | PASS | IT `Credit_purchase_invoice_creates_payable…` |
| INTG-006 | PASS | IT `CreditSale_then_partial_then_final_payment_settles_receivable` |
| INTG-007 | PASS | IT `SupplierInvoice_then_payments_settle_payable` |
| INTG-008 | PASS | IT `Inventory_count_variance_posts_adjustment_and_balanced_revaluation` |
| INTG-009 | PASS | IT `Generation_stock_reorder_and_dormant…` |
| INTG-010 | PASS | IT `Generation_sales_spike…` |
| INTG-011 | PASS | IT `Generation_cashflow_risk_fires_once_rule_threshold_is_overridden` |
| INTG-012 | PASS | IT `Group_dashboard_excludes_unauthorized_company` ; `Export_sales_journal_is_csv_financial_and_logged` |

## 28. Scénarios de régression bout-en-bout

| # | Scénario | Statut | Preuve |
|---|---|---|---|
| 1 | Connexion → dashboard → navigation | PASS | API `SmokeTests` ; NAV |
| 2 | Produit → stock initial → vente → stock final | PASS | IT `PosSalesTests` (produit, réception, vente, stock) ; API `SmokeTests` |
| 3 | Fournisseur → commande → réception → stock → facture → paiement | PASS | IT `PurchaseThreeWayMatchTests.Full_purchase_to_debt_settlement` + `RecetteCorrectionsTests` (fournisseur) |
| 4 | Client → vente à crédit → paiement partiel → total | PASS | IT `CreditSale_then_partial_then_final…` + `RecetteCorrectionsTests` (client) |
| 5 | Caisse → ventes → clôture → comparaison | PASS | IT `Session_open_sell_close_with_variance` |
| 6 | Stock faible → recommandation → modification → validation → audit | PASS | IT `IntelligenceEngineTests` (génération, modification, acceptation) |
| 7 | Entreprise A → accès aux données de B → refus | PASS | API `MultiTenantIsolationTests` |
| 8 | Vente hors ligne → reconnexion → synchronisation → pas de doublon | PASS | IT `OfflineSyncTests` |
| 9 | Sauvegarde → restauration → intégrité | PASS | MAN DB-010 / DB-011 |
| 10 | Correction → rejouer module + intégration | PASS | après chaque correction de cette recette : 18 + 64 + 323 tests backend, 118 frontend |

## 29. Tests extrêmes

| Cas | Statut | Preuve / notes |
|---|---|---|
| Quantité 0 ou négative | RETEST → PASS | refusée (`sale.quantity`) — `Discounts_beyond_the_configured_cap…` |
| Prix 0 ou négatif | RETEST → PASS | négatif refusé (`sale.price_negative`) ; prix forcé soumis à `pos.change_price` et au plancher |
| Remise 100 % ou plus | RETEST → PASS | au-delà du plafond ou de la ligne : refusée |
| Très grand panier | BLOCKED | voir PERF |
| Très grand volume | BLOCKED | voir PERF |
| Stock 0 / négatif | PASS | STOCK-009 ; lots FEFO jamais négatifs |
| Montants très élevés | PARTIAL | `numeric(19,4)` ; non testé |
| Décimales nombreuses | PASS | FIN-011 |
| Client sans historique | PASS | CLI-001 (encours 0) |
| Produit sans fournisseur | PASS | `SmokeTests` et `RecetteCorrectionsTests` vendent / modifient des produits sans lien fournisseur |
| Double clic paiement | PASS | API-009 (clé d'idempotence obligatoire sur la vente) |
| Deux utilisateurs sur la même donnée | PASS | `Two_registers_selling_same_stock_never_oversell` |
| Coupure réseau pendant paiement | PASS | OFF-005 / OFF-006 |
| Serveur indisponible | PARTIAL | FE `apiClient.test.ts` (erreur réseau) ; non testé bout-en-bout |
| Requête API répétée | PASS | API-009 |

## 30. Réconciliation financière indépendante

| Contrôle | Statut | Preuve |
|---|---|---|
| CA | PASS | `Command_center_revenue_reconciles_with_sales_journal` |
| Stock | PASS | `ReportingTrustTests` (stock rapporté = grand livre de stock) |
| Créances | PASS | `Aging_receivables_reconciles_with_open_receivable_subledger` |
| Dettes | PASS | `SupplierInvoice_then_payments_settle_payable` |
| Caisse | PASS | `Session_open_sell_close_with_variance`, `CashCount_shortage_posts_variance` |
| Marge | PARTIAL | voir FIN-004 |
| Bénéfice | PARTIAL | voir FIN-005 |
| Inventaire | PASS | `Inventory_count_variance_posts_adjustment_and_balanced_revaluation` |

## 31. Vérification contre le cahier des charges

La correspondance exigence → test est tenue dans
[`phase-49-matrice-tracabilite.md`](phase-49-matrice-tracabilite.md) (38 familles) et, pour chaque
phase, dans son `docs/phase-NN-implementation.md`. La présente grille en est la vue ligne par ligne.

## 32. Registre des anomalies

| ID | Module | Description | Sévérité | Correction | Retest | Statut |
|---|---|---|---|---|---|---|
| BUG-001 | Ventes | Le prix forcé et la remise envoyés par le client étaient acceptés sans permission, sans plafond (`pos.max_discount_percent` jamais lu), sans prix plancher, et une remise supérieure à la ligne donnait un total négatif | **Critique** | `ISalePricingGuard` sur vente, commande et devis | 3 tests IT + 42 tests ventes / commandes / devis / hors ligne / restaurant | Fermé |
| BUG-002 | Tiers | Aucune API de création ni de modification de client ou de fournisseur (import ou SQL uniquement) | Haute | `/customers`, `POST/PUT /suppliers` + écran | 4 IT, 2 API, NAV | Fermé |
| BUG-003 | Auth | La déconnexion ne révoquait pas la session serveur ; révoquer une session ne coupait pas son jeton | Haute | `jti` lié à la session, `POST /me/logout`, contrôle dans le middleware | API, NAV | Fermé |
| BUG-004 | Auth | Ni changement ni réinitialisation de mot de passe | Haute | `POST /me/password`, `/auth/password/forgot|reset` (V063) + écrans | 3 IT, 2 API, NAV | Fermé |
| BUG-005 | Identité | Aucune création d'utilisateur (prévue en Phase 22, jamais branchée) ni écran d'administration des utilisateurs | Haute | `POST /users` + onglet Utilisateurs | 3 IT, NAV | Fermé |
| BUG-006 | Sécurité | Politique de débit `auth` définie mais attachée à aucun endpoint | Haute | `[EnableRateLimiting("auth")]` | API (429) | Fermé |
| BUG-007 | Catalogue | Fiche produit non modifiable | Haute | `PUT /catalog/products/{id}` + formulaire | IT | Fermé |
| BUG-008 | UX / a11y | Libellés de formulaire reliés à aucun champ, dans toute l'application | Moyenne | `Field` relie libellé, aide et erreur | NAV | Fermé |
| BUG-009 | Frontend | `apiFetch` échouait sur une réponse de succès sans corps (202) | Moyenne | lecture tolérante | FE | Fermé |
| BUG-010 | Catalogue | Conflit de suivi EF lors d'une modification après création dans la même requête (trouvé avant livraison) | Basse | recopie sur l'entité suivie | IT | Fermé |
| BUG-011 | Catalogue | Aucun export du catalogue | Moyenne | — | — | Ouvert |
| BUG-012 | Sécurité | Jeton d'accès et file hors ligne en clair dans le stockage du navigateur | Moyenne | — | — | Corrigé (guide fonctionnel partie 2, étape G) |
| BUG-013 | Navigation | L'écran « Clients & fournisseurs » est masqué à un acheteur sans `customers.view` (il ne peut pas y créer de fournisseur) | Basse | — | — | Ouvert |
| BUG-014 | POS | Annulation de vente (`void`) jamais testée | Haute (test manquant) | — | — | Ouvert |
| BUG-015 | Recette | Pas de jeu de données de recette dédié et figé (§5) | Moyenne | `recette.sh` + générateur `Nexora.RecetteSeed` (base `nexora_recette`) | `verif_jeu_recette.sql` | Fermé |
| BUG-016 | Finance | `POST /finance/expenses` ne crée que l'écriture : aucune ligne dans le registre `expense` (rapports de dépenses et insight « hausse de dépenses » inopérants) | Moyenne | — | — | Ouvert |
| BUG-017 | Catalogue | Poser un prix dans une autre devise fermait le prix de la devise principale | Haute | clôture du prix précédent filtrée par devise | IT | Fermé |
| BUG-018 | Achats / POS | Toute réception fournisseur de plus d'une ligne échouait, ainsi que tout retour client multi-lignes (UUID d'opération dupliqué sur les mouvements de stock) | **Critique** | seul le 1er mouvement porte l'UUID (ancre d'idempotence) | 2 IT | Fermé |
| BUG-019 | Ventes | Un article sans prix en vigueur passait en caisse à 0 | Haute | refus `sale.no_price` | IT | Fermé |
| BUG-020 | Achats | Une réception sans ligne de commande rétrogradait la commande « envoyée » en « approuvée » | Basse | statut jamais rétrogradé | IT | Fermé |

## 36. Tableau de synthèse

Les lignes `RETEST → PASS` sont comptées PASS.

| Domaine | Tests | PASS | FAIL | PARTIAL | BLOCKED | Taux |
|---|---|---|---|---|---|---|
| Authentification | 10 | 8 | 0 | 2 | 0 | 80 % |
| Permissions | 8 | 7 | 0 | 1 | 0 | 88 % |
| Multi-entreprise | 8 | 8 | 0 | 0 | 0 | 100 % |
| Produits | 10 | 7 | 1 | 2 | 0 | 70 % |
| Stock | 16 | 14 | 0 | 2 | 0 | 88 % |
| POS | 20 | 15 | 0 | 5 | 0 | 75 % |
| Hors ligne | 8 | 6 | 1 | 1 | 0 | 75 % |
| Achats | 11 | 11 | 0 | 0 | 0 | 100 % |
| Clients | 10 | 10 | 0 | 0 | 0 | 100 % |
| Finance | 14 | 10 | 0 | 4 | 0 | 71 % |
| Rapports | 12 | 7 | 0 | 5 | 0 | 58 % |
| Notifications | 8 | 5 | 0 | 3 | 0 | 63 % |
| Intelligence (recommandations + analytics) | 31 | 25 | 0 | 6 | 0 | 81 % |
| Assistant | 9 | 5 | 0 | 4 | 0 | 56 % |
| API | 12 | 11 | 0 | 1 | 0 | 92 % |
| Base de données | 11 | 9 | 0 | 2 | 0 | 82 % |
| Sécurité | 11 | 6 | 0 | 4 | 1 | 55 % |
| Performance | 8 | 0 | 0 | 0 | 8 | 0 % |
| UX / A11Y | 12 | 1 | 0 | 10 | 1 | 8 % |
| Audit | 8 | 7 | 0 | 1 | 0 | 88 % |
| Intégration modules | 12 | 12 | 0 | 0 | 0 | 100 % |
| **Total** | **249** | **184** | **2** | **53** | **10** | **74 %** |

## 38. Décision GO / NO-GO

| Critère | Résultat | Condition |
|---|---|---|
| Fonctions critiques | ✅ POS, stock, achats, crédits, isolation PASS ; ⚠️ annulation de vente non testée (BUG-014) | 100 % PASS |
| Sécurité critique / haute | ✅ 0 ouverte — les 4 trouvées (BUG-001, 003, 004, 006) sont corrigées ; ⚠️ SEC-002 et SEC-006 non prouvées | 0 ouverte |
| Intégrité financière | ⚠️ CA, stock, créances, dettes, caisse, inventaire PASS ; marge et bénéfice PARTIAL | 100 % validée |
| Isolation multi-tenant | ✅ | 100 % validée |
| Sauvegarde / restauration | ✅ exercée le 2026-09-22 | test réussi |
| Régression | ✅ 409 backend + 118 frontend | PASS |
| Performance | ❌ jamais mesurée | seuils acceptés |
| Documentation | ⚠️ technique complète ; guides utilisateur et administrateur absents | complète |
| Bugs critiques | ✅ 0 ouvert (BUG-001 et BUG-018 corrigés) | 0 |
| Bugs hauts | ⚠️ 1 ouvert (BUG-014, test manquant) | 0 ou dérogation formelle |

**Recommandation : NO-GO pour la production.** Les défauts les plus graves trouvés par cette recette
sont corrigés, mais quatre conditions de sortie ne sont pas remplies : performance jamais mesurée,
annulation de vente non prouvée, marge et bénéfice non réconciliés indépendamment, documentation
utilisateur absente. Une Release Candidate sur `staging` est défendable **avec ces réserves écrites**.

## 39. Signatures

| Rôle | Nom | Signature | Date | Décision |
|---|---|---|---|---|
| Développeur responsable | | | | |
| QA / Testeur | | | | |
| Responsable produit | | | | |
| Administrateur / DevOps | | | | |
| Client / Direction | | | | |

La grille est préparée ; les signatures reviennent aux personnes désignées, pas à l'agent qui l'a
exécutée.
