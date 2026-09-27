# Phase 30 — Facturation & documents commerciaux — implémentation

**Statut : livré et vérifié.** Migration `V043`. 7 tests d'intégration
(`CommercialDocumentsTests`), 2 tests frontend (`documentsApi.test.ts`). **196 tests backend**
verts (16 unit + 7 API + 173 intégration), Debug **et** Release, 0 warning. 29 tests frontend.

Le socle documentaire existe déjà et est **validé** par cette phase :

- `document_sequence` + `nexora.next_document_number()` — numérotation atomique contrôlée par
  entreprise / type / établissement / année, préfixe / année / séparateur / padding configurables (§19-20) — V015
- `issued_document` — registre générique de documents émis (type, statut, `reference_type/id`,
  `party_type/id`, `payload` jsonb, `valid_until`, `total`) — V015, **jusqu'ici inutilisé**
- `sale` / `sale_line` (facture / ticket §8), `sale_return` (avoir §12), `payment` (reçu §13),
  `sales_order` + `OrderService` (commande client §7, acompte §10) — Phases 6 / 25
- `purchase_order` / `goods_receipt` / `supplier_invoice` / `supplier_return` (documents fournisseurs §16) — Phase 26
- Relevé client (`ICreditPortfolioQueries.CustomerStatementAsync` §17) / relevé fournisseur (§18) — Phases 27 / 26
- `company_branding` (logo, pied de page, `default_locale`, `date_format`, `number_locale` §21-23) — Phase 13
- Connecteurs & liens de paiement, webhooks HMAC (§39), notifications & relances (§37-38) — Phases 12 / 14
- `CsvExportService` + `export_log` (§35) — Phase 7

La Phase 30 ajoute le **moteur documentaire** : devis (le grand manque §4-5), émission d'un
document figé depuis une source métier, vérification publique, rendu, envoi, annulation.

---

## Correspondance cahier des charges → implémentation

| § | Exigence | Où |
|---|---|---|
| 2 | Contenu historique figé ; modèles sans toucher aux données sources ; document reconstituable ; numérotation contrôlée par entreprise / type | À l'émission, `SqlDocumentService.IssueAsync` **fige un snapshot complet** (`issued_document.content` jsonb : émetteur, tiers, lignes, totaux, devise, dates) + **empreinte SHA-256** (`content_hash`). Le rendu part du snapshot, jamais de la source. Numéro via `next_document_number` (§19) |
| 3 | 18 types de documents | `issued_document.document_type` CHECK étendu (`V043`) : quote / proforma / invoice / deposit_invoice / final_invoice / delivery_note / goods_receipt / purchase_order / credit_note / debit_note / customer_statement / supplier_statement / receipt / other |
| 4-5 | **Devis** (client, validité, lignes, prix, remises, taxes, conditions, statut, version) + cycle brouillon → envoyé → accepté / refusé / expiré → commande | **`quote` / `quote_line`** (`V043`) + **`QuoteService`** : `CreateAsync` (lignes chiffrées par `ISalesPricing.PriceCartAsync`, figées §7), `SendAsync` / `AcceptAsync` / `RejectAsync` (transitions `QuoteStatus.CanTransition`), `ConvertToOrderAsync` (**réutilise `OrderService.CreateOrderAsync`** — lignes au prix du devis §9), `ExpireOverdueAsync` (branchable au planificateur). **`GET/POST /api/v1/quotes`** + `/{id}/{send,accept,reject,convert}`. Tests `Quote_is_created_priced_sent_and_converted_to_order` |
| 6 | Proforma non comptable | `document_type = 'proforma'` — émissible depuis `sale` / `sales_order` / `quote`, statut explicite, aucune écriture |
| 7 | Commande client (prix snapshot, acompte, crédit, réservation, statut) | `sales_order` + `OrderService` (Phase 25) — inchangé ; le devis y déverse ses lignes au prix figé |
| 8 | **Facture** (numéro, émetteur, client, dates, lignes, totaux HT / taxe / TTC, payé, solde, devise) | Document `invoice` émis depuis une `sale` : `SqlDocumentService.LoadSaleAsync` reprend totaux, lignes, client. **`GET /api/v1/invoices`**, **`POST /api/v1/invoices/{saleId}/issue`**, **`POST /api/v1/invoices/{id}/cancel``. Le total / les créances / les écritures restent portés par la vente (§45). Test `Invoice_is_issued_from_a_sale_with_a_frozen_snapshot` |
| 9 | Facture à crédit → créance → échéance → relevé / relance | `sale_type = 'credit'` → `customer_receivable` (Phase 6) ; relances `dunning_policy` (Phase 14) ; relevé (Phase 27) — inchangé |
| 10-11 | Facture d'acompte / facture finale (imputation acomptes) | Types `deposit_invoice` (source `sales_order`) / `final_invoice` (source `sale`) émissibles ; l'acompte lui-même est un `payment` non alloué (`OrderService.RecordDepositAsync`, Phase 25). Déduction automatique acompte ↔ facture finale : **backlog** (le solde commande est déjà exact côté `sales_order`) |
| 12 | **Avoir** (facture source, motif, lignes, montant, taxes, remboursement / crédit, numéro, audit) | Document `credit_note` émis depuis un `sale_return` (`LoadReturnAsync`). **`POST /api/v1/credit-notes`**. Test `Credit_note_and_receipt_are_issued_with_distinct_series` |
| 13 | **Reçu** (paiement source, tiers, montant, moyen, date, solde après, signature / QR optionnels) | Document `receipt` émis depuis un `payment` (`LoadPaymentAsync` : moyen, tiers client / fournisseur). **`POST /api/v1/receipts`** |
| 14-15 | Bon de livraison / bon de réception (lignes, lots, transporteur, signature) | `delivery_note` émis depuis `sales_order` / `sale` ; `goods_receipt` existe (Phase 26). Lots / signature / preuve de livraison : **backlog** |
| 16 | Documents fournisseurs | `purchase_order` / `goods_receipt` / `supplier_invoice` / `supplier_return` (Phase 26) — types présents dans le CHECK, émission depuis ces sources : **backlog** |
| 17-18 | Relevé client / état de compte fournisseur (solde initial + factures − paiements − avoirs) | `CustomerStatementAsync` (Phase 27) / relevé fournisseur (Phase 26) — inchangé ; type `customer_statement` / `supplier_statement` dans le registre |
| 19 | **Numérotation** (séquence par entreprise / type, préfixe, année, sans doublon, numéro définitif à l'émission, annulation contrôlée) | `next_document_number` (V015). **Numéro provisoire → définitif à l'émission** : `IssueAsync` appelle `next_document_number($"document:{type}")` ; le devis reçoit son numéro à la création (`quote` prefix `DEV`). Traçabilité des trous : `document_sequence.current_number` monotone, jamais réutilisé. **`GET /api/v1/document-sequences`**. Test `Credit_note_and_receipt_are_issued_with_distinct_series` (numéros distincts) |
| 20 | Séries (INV- / CRN- / RCV- …) par établissement / type / entreprise | `document_sequence` par `(company, document_type, establishment_id, year)` ; démo seed : préfixes `INV` / `AVR` / `RCU` / `BL` / `PRO` / `DEV` pour l'année 2026. **Correctif** : les lignes seed portent désormais `year = 2026` (l'app passe toujours l'année courante à `next_document_number` — une ligne `year IS NULL` était ignorée) |
| 21-23 | Modèles multi-entreprises / multi-langues (logo, coordonnées, pied de page, mentions, langue) | `company` (name, legal_name, tax_identifier) + `company_branding` (`document_footer`, `default_locale`) repris dans le snapshot et le rendu. `issued_document.language` (`V043`, défaut `company_branding.default_locale`). Modèle par établissement / groupe, traductions de libellés : **backlog** |
| 24 | Multi-devises (devise document, taux, affichage clair) | `issued_document.currency_id` = devise de la source ; totaux dans cette devise. Conversion fonctionnelle : le moteur financier (Phase 4) |
| 25 | **PDF** (génération serveur, aperçu, A4 / ticket, numéro, métadonnées) | `SqlDocumentService.RenderAsync` → **HTML déterministe** construit à partir du snapshot (`format = a4` par défaut ou `ticket` compact). **`GET /api/v1/documents/{id}/pdf`** (`text/html`). PDF binaire (bibliothèque dédiée) : **backlog assumé** — l'HTML est imprimable et archivable. Test `Rendered_document_is_html_with_the_number` |
| 26 | Impression POS (ticket thermique, réimpression selon permission, queue) | `RenderAsync(format: "ticket")` (largeur 300 px) ; le ticket POS `receipt_format` (Phase 13) est déjà rendu au checkout ; queue imprimante : **backlog** |
| 27-28 | **QR codes / vérification** (lien, identifiant, statut, données publiques minimales, accès protégé si confidentiel) | `issued_document.verification_code` (`V043`, jeton opaque aléatoire, index unique). **`GET /api/v1/documents/verify/{code}` — `[AllowAnonymous]`** : renvoie uniquement type / numéro / statut / date / entreprise / total (`DocumentVerificationDto`, `SqlDocumentService.VerifyAsync` sous `BeginSystemScope()`). États : valide / annulé / remplacé / introuvable. Tests `Public_verification_returns_minimal_data`, `Cancelled_document_stays_visible` |
| 29 | Signature électronique (interne / client / fournisseur, horodatage, identité, niveaux) | **Backlog** — `issued_document.content` peut porter des blocs signature ; aucun niveau juridique présenté par défaut (§29) |
| 30 | Documents versionnés (brouillon v1 → modification → v suivante ; validé figé) | `quote.version` ; un document `issued` est **immuable** (snapshot figé) — la correction passe par annulation + nouveau document (§32) |
| 31 | **Annulation** (motif obligatoire, utilisateur, date, reste visible, référence corrective, audit) | `SqlDocumentService.CancelAsync` : motif obligatoire, `cancelled_at` / `cancelled_by` / `cancel_reason`, statut `cancelled` (ou `replaced` si `replacedByDocumentId`). **Le document reste consultable** (`GET /api/v1/documents/{id}`). `trg_audit_issued_document` (`V043`). **`POST /api/v1/documents/{id}/cancel`**. Test `Cancelled_document_stays_visible` |
| 32 | Correction facture (avoir / annulation → nouvelle facture ; ne pas écraser ; lien ; audit) | `issued_document` figé + `replaced_by_document_id` ; un nouveau `IssueAsync` sur la même source est refusé tant que le précédent n'est pas annulé (`document.already_issued`) |
| 33 | Archivage (métadonnées, type, numéro, tiers, date, hash, conservation, accès contrôlé) | `issued_document` = l'archive (snapshot + `content_hash`) ; accès via `documents.view` + RLS. Durée de conservation configurable : **backlog** |
| 34 | Recherche documentaire (numéro, tiers, date, montant, type, état, référence) | **`GET /api/v1/documents`** (`DocumentSearchRequest` : type, statut, numéro `ILIKE`, `partyId`, plage de dates). `IDocumentQueries.SearchAsync`. Test `Search_and_send_log` |
| 35 | Exports (PDF, CSV, ZIP, comptables, fiscaux) | `CsvExportService` (Phase 7) ; ZIP documentaire : **backlog** |
| 36 | **Envoi** (email / lien / impression / lien sécurisé, journal d'envoi, statut) | **`document_send`** (`V043`) + `SqlDocumentService.SendAsync` : journalise canal / destinataire / statut. email / sms → `queued` (livraison réelle = connecteur Phase 12) ; lien / impression / téléchargement → `sent`. Passe le document `issued → sent`. **`POST /api/v1/documents/{id}/send`**, **`GET /api/v1/documents/{id}/sends`**. Test `Search_and_send_log` |
| 37-38 | Templates & relances documentaires | `notification` + `dunning_policy` (Phase 14) ; templates email dédiés : **backlog** |
| 39 | Paiement intégré au document (lien, expiration, aucune confirmation implicite) | `payment_intent` + webhooks signés (Phase 12) — inchangé ; un lien de paiement peut être joint au snapshot |
| 40-44 | Documents sectoriels (restaurant / pharmacie / supermarché / grossiste / services) | Mêmes types génériques ; le rendu / ticket suit `company.main_activity_type` + `company_branding.receipt_format` (Phase 8 / 13) |
| 45 | Comptabilité : document lié, écriture non modifiable par édition du PDF | `issued_document.reference_type/id` pointe la source ; le rendu ne touche jamais `journal_entry` (§2) |
| 46 | Audit | `trg_audit_{issued_document, document_send, quote, quote_line}` (`V043`) |
| 47 | Permissions | `V043` : `documents.{view, create_quote, issue, cancel, send, manage_sequence}` ; un caissier émet (`documents.issue`) sans créer de devis ni annuler |
| 48 | API principales | `/api/v1/quotes` (+ `/{id}/{send,accept,reject,convert}`), `/api/v1/invoices` (+ `/{saleId}/issue`, `/{id}/cancel`), `/api/v1/documents` (recherche, `/{id}`, `/{id}/pdf`, `/{id}/send`, `/{id}/sends`, `/{id}/cancel`, `verify/{code}`), `/api/v1/{credit-notes,receipts,delivery-notes}` (POST), `/api/v1/document-sequences`. **Écart §48** : `/orders` déjà servi par Phase 25 ; `/document-templates` = **backlog** (`company_branding` couvre l'essentiel) |
| 49 | Événements | `InvoiceIssued` / `CreditNoteIssued` / `ReceiptIssued` / `DocumentSent` = insertions `issued_document` / `document_send` auditées ; `QuoteAccepted` = transition auditée. Bus d'événements dédié : **backlog** |
| 50 | Tests | `CommercialDocumentsTests` (7) : devis chiffré / envoyé / converti ; facture figée depuis vente (hash + code) ; vérification publique (minimale + code inconnu) ; annulation → reste visible ; avoir + reçu → séries distinctes ; rendu HTML avec numéro ; recherche par type + journal d'envoi |
| 51 | Scénario complet (devis → commande → acompte → livraison → facture − acompte → paiement → reçu → créance 0) | Couvert par la combinaison `Quote_…_converted_to_order` + `OrderService` (Phase 25) + `Invoice_…` + `Receipt` |

## `V043` — contenu

- `quote` (`document_number`, `status` `draft/sent/accepted/rejected/expired/converted/cancelled`,
  `version`, `valid_until`, totaux, `terms`, `notes`, `converted_order_id`) + `quote_line`
- `issued_document` += `content` jsonb (snapshot), `content_hash`, `total_net`, `total_tax`,
  `language`, `issued_at`/`issued_by`, `cancelled_at`/`cancel_reason`/`cancelled_by`,
  `replaced_by_document_id`, `verification_code` (index unique) ; CHECK `document_type`/`status`
  étendus ; clé composite `issued_document_company_id_uk (id, company_id)`
- `document_send` (`channel`, `recipient`, `subject`, `status` `queued/sent/failed`, `sent_at`/`sent_by`)
- RLS `company_isolation` + `trg_audit_*` sur `quote` / `quote_line` / `document_send` / `issued_document`
- 6 permissions `documents.*`
- **174 tables (+3), 162 en RLS (+3)**, 43 migrations (V001–V043)

## Frontend

`src/documents/` — `documentsApi.ts` + **`DocumentsPage.tsx`** : KPI (documents émis / annulés /
devis / devis en attente) ; onglets **Documents** (recherche par type, lien PDF, envoyer,
annuler), **Devis** (envoyer / accepter / convertir en commande), **Vérifier** (saisie du code
→ statut + données publiques). Route `/documents`, nav « Documents » (`documents.view`).
Vitest `documentsApi.test.ts` (2) → 29 tests frontend.

## Manques assumés (backlog)

- PDF binaire (bibliothèque dédiée) — l'HTML rendu est imprimable / archivable
- Déduction automatique acompte ↔ facture finale (§10-11)
- Émission de documents fournisseurs depuis `purchase_order` / `goods_receipt` / `supplier_invoice` (§16)
- Signature électronique multi-niveaux (§29)
- Modèles de documents par établissement / groupe + traductions de libellés (§21-23) — `document_template`
- Templates email dédiés (§37) ; ZIP documentaire (§35) ; queue d'impression POS (§26)
- Lots / transporteur / preuve de livraison sur le bon de livraison (§14)
