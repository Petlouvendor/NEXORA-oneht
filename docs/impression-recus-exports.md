# Impression des reçus et export des rapports (Excel, PDF)

Date : 2026-09-24. Demande du propriétaire : « je dois pouvoir imprimer les différents reçus du client
(total versé, reste à verser…), n'importe quel type de reçu pour les fournisseurs, et exporter n'importe
quel rapport en Excel ou en PDF » — « ça doit être de vrais fichiers Excel ».

Aucune migration : les reçus sont reconstruits à la demande depuis les tables existantes, et chaque
impression ou export est tracé dans `export_log` (V026).

## 1. Ce qui est livré

| Besoin | Réalisation |
|---|---|
| Vrais fichiers Excel | Classeurs `.xlsx` (ClosedXML, MIT) : montants et quantités en **nombres** (`#,##0.00`), dates en **dates**, pourcentages en %, en-tête de l'entreprise, ligne d'en-tête figée, filtre automatique, ligne de totaux, mise en page d'impression ajustée à la largeur ; une feuille par tableau (+ « Synthèse »). |
| Vrais PDF | PDF (PDFsharp-MigraDoc, MIT) en **A4** (paysage automatique au-delà de 7 colonnes, en-tête de colonnes répété à chaque page, « Page n / N ») ou en **ticket 80 mm** pour imprimante thermique, à la hauteur exacte du contenu (aucun papier blanc débité). Police **Inter** embarquée (SIL OFL, `Printing/Fonts`) : rendu identique sous Windows et dans l'image Linux de l'API, qui n'a aucune police. |
| Reçus client | Reçu de **versement** (montant, crédits réglés, reste dû par crédit, **total des crédits / total versé / reste à verser**), **relevé de compte** (mouvements, totaux, reste à verser, en retard, avoir), **crédit** (reconnaissance de dette, règlements, échéancier, signature du client), **avoir**, **remboursement**, **note de dépense imputée**, **ticket de caisse** (articles, paiements, rendu, part payée par avoir, part à crédit, reste à verser du compte). |
| Reçus fournisseur | Reçu de **paiement fournisseur** (factures réglées, reste dû par facture, **total facturé / total payé / reste à payer**), **facture fournisseur** (lignes, paiements, reste à payer), **bon de commande** (livrer à, signatures), **bon de réception** (lots, péremption, valeur), **relevé fournisseur**. |
| N'importe quel rapport | ① **« Exporter ce tableau »** sur chaque tableau de données de l'application (Excel, PDF, CSV) ; ② **« Exporter la page »** dans la barre du haut : tous les tableaux et indicateurs de la page, une feuille Excel par tableau ; ③ exports complets calculés par le serveur (journal des ventes, valorisation du stock, balances âgées clients / fournisseurs) ; ④ rapports clients (Excel, PDF, CSV). |

## 2. Règles de confiance

- **Chiffres du moment de l'opération.** Un reçu de versement donne la situation du compte arrêtée à
  l'écriture de ce versement dans le journal client (`customer_ledger_entry`, immuable) ; le reste dû
  par crédit suit la même règle que `recompute_customer_receivable` (versements, avoirs imputés,
  radiations actifs à cet instant). Réimprimé un mois plus tard, il redonne les mêmes montants. Idem
  côté fournisseur (paiements et dettes à l'instant du paiement) et pour le ticket d'une vente à crédit.
- **DUPLICATA.** Toute nouvelle sortie d'une pièce déjà imprimée (quel que soit le format) porte
  « DUPLICATA — copie n° k » ; les pièces annulées portent « VERSEMENT ANNULÉ », « VENTE ANNULÉE »…
- **Construits par le serveur.** Les reçus viennent de la base, jamais du navigateur. L'export d'un
  écran, lui, reprend le tableau affiché : il est marqué « document non contractuel », sans tampon ni
  signature, pour ne jamais passer pour un reçu.
- **Journalisé.** Chaque impression / export écrit une ligne `export_log` (qui, quoi, format, lignes).

## 3. Droits

| Document | Accès complet | Accès limité |
|---|---|---|
| Reçu de versement | `credit.view_portfolio` | `credit.receive_payment` : seulement **ses propres** versements (module clients §19) |
| Crédit, relevé client | `credit.view_portfolio` | — |
| Avoir, remboursement | `credit.view_portfolio` ou `credit.refund` | — |
| Note de dépense | `credit.view_portfolio` | `customers.create_expense` : ses propres dépenses |
| Ticket de caisse | `pos.view` | — |
| Paiement fournisseur | `purchasing.view_payables` ou `purchasing.pay` | — |
| Facture fournisseur | `purchasing.view_payables` ou `purchasing.invoice` | — |
| Bon de commande / de réception | `purchasing.view` (ou `purchasing.order` / `purchasing.receive`) | — |
| Relevé fournisseur | `purchasing.view_payables` | — |
| Export d'un écran | `reporting.export` | — |
| Exports complets (ventes, stock, balances) | `reporting.export` + `reporting.export_financial` | — |
| Rapports clients | `credit.view_portfolio` + `customers.export` | — |

Une pièce d'une autre entreprise, ou hors périmètre pour l'accès limité, répond **404** (jamais la donnée).

## 4. API

| Méthode | Route | Rôle |
|---|---|---|
| GET | `/print/{type}/{id}?format=pdf\|ticket\|xlsx\|csv` | types : `customer-payment`, `customer-credit`, `credit-memo`, `customer-refund`, `customer-expense`, `sale`, `supplier-payment`, `supplier-invoice`, `purchase-order`, `goods-receipt` |
| GET | `/print/customers/{id}/statement?from&to&currency&format` | relevé de compte client |
| GET | `/print/suppliers/{id}/statement?from&to&format` | relevé fournisseur |
| GET | `/print/suppliers/{id}/documents` | pièces imprimables d'un fournisseur (limitées aux droits) |
| POST | `/exports/screen?format=xlsx\|pdf\|csv` | export d'un tableau affiché (`reporting.export`, 50 000 lignes au plus) |
| POST | `/reports/export` | `format` = `csv`, `xlsx` ou `pdf` (auparavant CSV seulement) |
| GET | `/customer-reports/{key}/export?format=` | idem |

## 5. Où imprimer dans l'application

- **Fiche client** : bouton « Relevé de compte » (imprimer, PDF, Excel) ; icône d'impression sur chaque
  versement (A4 ou ticket), crédit, avoir, remboursement, dépense et achat (ticket de caisse).
- **Après un versement** (fiche client ou caisse) : le dialogue propose aussitôt « Imprimer le reçu (A4) »
  et « Imprimer le ticket » (le ticket est mis en avant à la caisse).
- **Caisse** : « Imprimer le ticket » imprime le vrai ticket 80 mm de la vente (« Format A4 » en option).
- **Achats** : reçu proposé juste après un paiement fournisseur ; icône d'impression sur chaque facture et
  bon de commande ; « Pièces & relevé » par fournisseur (paiements, réceptions, factures, commandes, relevé).
- **Rapports** : « Exports complets (Excel, PDF) » ; **partout** : « Exporter ce tableau » et « Exporter la page ».

## 6. Code

- Backend : `Nexora.Contracts/Printing` (modèle `PrintDocument`), `Nexora.Application/Printing`
  (`IDocumentRenderer`, `IPrintService`, `IReceiptService`, `PrintAccess`, `PrintTableInference`),
  `Nexora.Infrastructure/Printing` (`DocumentRenderer`, `PdfDocumentWriter`, `XlsxDocumentWriter`,
  `InterFontResolver`, `SqlPrintService`, `SqlReceiptService`), `PrintController` + `ExportsController` ;
  `ReportExportService` remplace `CsvExportService`.
- Frontend : `shared/printApi.ts`, `shared/tableExport.ts` (relecture typée d'un tableau affiché : montants
  fr-FR, dates, identifiants gardés en texte), `shared/ExportMenu.tsx` (menus Imprimer / Exporter),
  `TableToolsContext` dans `shared/ui.tsx` (fourni par `AppLayout` selon `reporting.export`),
  `purchasing/SupplierDocuments.tsx`.
- Paquets ajoutés : `ClosedXML` 0.105.0, `PDFsharp-MigraDoc` 6.2.4 (licences MIT).

## 7. Vérification

- Tests unitaires `DocumentRendererTests` (10) : les `.xlsx` et PDF produits sont **relus** (ClosedXML,
  PDFsharp) — cellules numériques et dates typées, en-tête figé, filtre, plusieurs pages, ticket de
  80 mm, paysage automatique, CSV (BOM, formules neutralisées), export JSON d'écran typé.
- Tests d'intégration `PrintingTests` (5) : total versé / reste à verser figés au jour du versement
  (600 puis 400 après un 2ᵉ versement, réimpression inchangée et « DUPLICATA — copie n° 2 »), journal des
  impressions, relevé, crédit, ticket de vente, droits de l'encaisseur (ses reçus seulement, 404 sinon),
  documents fournisseur (reste 3 000 sur une facture de 5 000), exports de pilotage Excel et PDF.
- Tests API `PrintEndpointsTests` (10) : vrais fichiers (`%PDF-`, archive `PK`), refus au caissier,
  404 hors entreprise, format invalide refusé, export d'écran refusé sans `reporting.export`.
- Frontend : `tableExport.test.ts` (6), `printApi.test.tsx` (6).
- Suites complètes : **28 unitaires, 388 intégration, 145 API, 158 frontend — 0 échec.**
- Recette navigateur (nexora_recette, gérant) : reçus, relevés, crédit, pièces fournisseur et tableaux
  téléchargés en vrais PDF et `.xlsx` avec leur nom, impression ouverte dans un onglet, exports financiers
  masqués sans le droit (voir §8 bis pour la correction des PDF vides).
- Relecture visuelle des PDF produits (reçu A4, ticket, relevé, paiement fournisseur).

## 8. Compléments du 2026-09-24 (« fais-le »)

- **Paiement d'une dette fournisseur** : l'identifiant du compte n'est plus saisi à la main. Le dialogue
  « Payer » propose les comptes actifs dans la devise de la facture (`GET /payables/payment-accounts`,
  `purchasing.pay`, nom / type / devise, **aucun solde**), limite le montant au solde, garde la même clé
  d'idempotence pour un double clic, puis propose le reçu. Le rapprochement 3-way se lance depuis une
  liste des factures ouvertes au lieu d'un identifiant à saisir.
- **Numéros clients / fournisseurs en double** : le numéro automatique (« plus grand + 1 ») était calculé
  sans verrou ; deux créations simultanées (deux caisses) obtenaient le même numéro et la seconde échouait
  (« le numéro client existe déjà »). C'est ce défaut qui faisait échouer la suite de tests lancée en une
  seule commande. Désormais : verrou par entreprise (`pg_advisory_xact_lock`) le temps de la transaction qui
  calcule le numéro et crée la fiche. Test : 12 clients et 12 fournisseurs créés en même temps → numéros
  tous distincts ; un numéro saisi déjà pris reste refusé proprement.
- Vérification : `dotnet test Nexora.sln` en une commande → **28 unitaires, 389 intégration, 146 API, 0 échec** ;
  frontend **160** ; recette navigateur du dialogue (comptes HTG listés sans solde, montant excessif refusé,
  fermeture sans écriture, liste des factures à rapprocher, aucune fenêtre de saisie).

## 8 bis. PDF téléchargés vides (0 octet) — corrigé le 2026-09-24

Constat du propriétaire : « après le téléchargement je n'arrive pas à ouvrir les fichiers PDF ». Le fichier
`releve-client.pdf` trouvé dans son dossier Téléchargements faisait **0 octet** (nom de secours de
l'application), alors que les `.xlsx` étaient corrects. Le navigateur (réglage « télécharger les PDF » ou
gestionnaire de téléchargements) intercepte les réponses PDF, et la page n'en reçoit qu'un corps vide —
le même comportement que le navigateur de test, que j'avais d'abord pris à tort pour une limite de l'outil.

Correction :
- l'application web demande les fichiers dans une **enveloppe JSON** (`Accept: application/vnd.nexora.file+json`
  → `{ fileName, contentType, size, base64 }`) : une réponse JSON n'est jamais interceptée ; le fichier est
  reconstitué dans la page, octet pour octet. Les autres clients de l'API reçoivent toujours le fichier brut ;
- un fichier vide n'est **jamais** enregistré : message d'erreur à la place ;
- « Imprimer » ouvre le PDF dans un **nouvel onglet** (visionneuse du navigateur : Imprimer ou Ctrl+P, A4
  ou ticket) au lieu d'un cadre invisible ; l'onglet est réservé pendant le clic (pas de blocage de fenêtre
  surgissante) ; fenêtres bloquées → le PDF complet est enregistré.

Vérification : dans le navigateur qui reproduisait la panne, reçu, relevés, crédit, paiement fournisseur
→ PDF de 62 à 68 Ko signés `%PDF-` avec leur vrai nom ; impressions → 3 onglets PDF ouverts, 0 erreur.
Tests : enveloppe décodée octet pour octet, refus d'un fichier vide, onglet réservé avant l'appel réseau,
repli en téléchargement, test API de l'enveloppe.

## 8 ter. Relevé client sur l'imprimante de caisse — 2026-09-24

Demande : « le relevé des clients doit être imprimé sur des imprimantes comme des reçus de caisse ».
- `GET /print/customers/{id}/statement?format=ticket` produit un relevé **ticket 80 mm** dédié : client,
  **solde reporté**, mouvements en 4 colonnes (date, opération + référence, montant — positif = dette,
  négatif = versement / avoir —, solde), puis versé sur la période, avoir, retard et **reste à verser** en
  évidence. Sans période demandée : les **90 derniers jours** (fuseau de l'entreprise), l'historique plus
  ancien étant repris dans le solde reporté (le relevé complet reste disponible en A4).
- Où : fiche client → « Relevé de compte » → « Imprimer le relevé (ticket 80 mm) » ; caisse → client choisi →
  « Relevé (ticket) » (détenteurs de `credit.view_portfolio`).
- Colonnes de date élargies sur le ticket (une date ne déborde plus sur la colonne voisine).
- Vérifié : ticket d'une seule bande de 80 mm (test d'intégration), relu visuellement ; fiche et caisse ouvrent
  le ticket dans un onglet (recette navigateur).

## 9. Points ouverts

- Les documents commerciaux émis (Phase 30, `/documents/{id}/pdf`) restent rendus en HTML.
- Une fois (2026-09-24), toute la suite d'intégration a échoué à l'initialisation lors d'un lancement
  groupé (clonage de la base modèle `nexora` pendant que les tests API y étaient connectés) ; non reproduit
  depuis — la fixture coupe les connexions et réessaie. En cas de récidive : lancer les projets un par un.
