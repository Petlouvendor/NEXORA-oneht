# Finalisation produit — Phase 7 : Finance et trésorerie

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 7.
> Branche `feature/finance`. Date : 2026-10-02.

## 1. Travail effectué

Avant cette phase, la page Finance était un tableau de bord en lecture, avec quatre actions passant par
`window.prompt`. Aucun compte de trésorerie ne pouvait être créé, aucun transfert, aucune dépense, aucun
rapprochement bancaire depuis l'application.

| Onglet | Après |
|---|---|
| Position | inchangé (soldes par compte, en devise et en devise de l'entreprise) |
| Comptes | création (caisse, banque, mobile money, monétique…), n° masqué, compte comptable, modification, désactivation / clôture motivées ; **historique des mouvements** (date, opération, compte opposé, n° d'écriture, auteur, filtre de dates) ; **transfert entre comptes** ; **frais bancaires** |
| Dépenses | saisie (catégorie, fournisseur, HT, taxe récupérable, référence), brouillon ou soumission, **approbation par un autre que le demandeur**, refus et annulation motivés, **paiement** sur un compte avec écriture comptable ; catégories liées à un compte de charge |
| Rapprochement bancaire | **import d'un relevé CSV** (séparateur, dates, décimales françaises ; lignes illisibles signalées une à une), contrôle ouverture + mouvements = clôture, rapprochement automatique puis manuel (mouvements candidats proposés), défaire, validation |
| Prévision, Suspens, Clôture | suspens ouverts et résolus par formulaire (nature, issue, note), clôture forcée avec motif — plus aucun `window.prompt` dans la finance |

## 2. Défauts corrigés

1. **Aucun transfert entre comptes ne fonctionnait** : les deux mouvements portaient le même identifiant d'opération,
   que la base imposait unique. Toute tentative échouait (« duplicate key »).
2. Transferts acceptés entre devises différentes, vers le même compte, ou au-delà du solde d'une caisse.
3. **Rapprochement manuel sans contrôle** : n'importe quel mouvement de l'entreprise (autre compte, autre montant,
   déjà rapproché) pouvait être rattaché à une ligne de relevé.
4. **Validation d'un relevé plein d'écarts** possible : il était déclaré « rapproché » sans condition.
5. **Doublons de relevé jamais détectés d'un relevé à l'autre** : la base comparait « 500.0000 », l'application « 500 ».
6. Relevé incohérent (ouverture + lignes ≠ clôture) accepté.
7. Table des dépenses présente depuis l'origine mais jamais utilisée : aucune dépense ne pouvait être saisie.
8. Une dépense enregistrée directement par le moteur financier n'était pas reliée à son écriture comptable : le lien
   est désormais posé après comptabilisation, quel que soit le chemin.
9. Le comptable ne pouvait pas charger la liste des devises ni des fournisseurs dans ses formulaires (403) :
   une liste de référence minimale lui est fournie sans ouvrir le catalogue ni les achats.

## 3. Base de données — V084

- Unicité des mouvements : (identifiant d'opération, compte) au lieu de l'identifiant seul.
- Catégorie de dépense → compte de charge ; dépense : écriture, dates de soumission / approbation, payeur, motif,
  référence ; contraintes (motif obligatoire si refus ou annulation, payée ⇒ compte + écriture), fournisseur lié.
- Audit étendu aux comptes financiers, catégories de dépense, relevés et lignes de relevé.
Appliquée sur `nexora` et `nexora_recette`.

## 4. APIs

Nouvelles : `GET /financial-accounts/manage`, `POST /financial-accounts`, `PUT /financial-accounts/{id}`,
`POST /financial-accounts/{id}/status`, `GET /financial-accounts/{id}/transactions`, `GET /ledger-options`,
`GET /finance-lookups`, `POST /treasury/transfers`, `POST /treasury/charges`, `GET|POST|PUT /expense-categories`,
`GET|POST /expenses`, `GET|PUT /expenses/{id}`, `POST /expenses/{id}/submit|approve|reject|cancel|pay`,
`GET /integrations/bank/lines/{id}/candidates`, `POST /integrations/bank/lines/{id}/unmatch`.
Renforcées : import, rapprochement manuel et validation des relevés.

## 5. Règles garanties

- Aucun solde ne s'écrit sans écriture : un nouveau compte part à zéro ; type, devise et compte comptable sont figés
  dès le premier mouvement ; un compte ne se clôture qu'à solde nul et sans session de caisse ouverte.
- Transfert, frais et paiement de dépense : idempotents (un rejeu ne déplace jamais l'argent deux fois).
- Dépense : quatre yeux, payée une seule fois, écriture Dr charge / Dr taxe récupérable / Cr trésorerie.
- Permissions : `finance.update` (paramétrage), `finance.transfer`, `finance.reconcile`, `finance.create`, `finance.pay`,
  `integrations.bank` (vérifié : caissier → 403).

## 6. Tests

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend (dont 3 nouveaux `TreasuryManagementTests`) | 452 / 452 |
| API backend | 186 / 186 |
| Frontend Vitest (dont 3 nouveaux), fuseau `America/Port-au-Prince` | 227 / 227 |
| Scénario API (clone `nexora_qa`, compte comptable de recette) | 19 / 19 |
| Navigateur : compte, transfert, mouvements, catégorie, dépense, import CSV, suspens, clôture | réussi, 0 erreur console |

## 7. Dette technique restante

- Transfert entre devises (opération de change) non pris en charge : refusé explicitement.
- Banque : pas de contrôle de solde (le découvert est possible) ; seules caisses et porte-monnaie sont bornés.
- Import de relevé : CSV uniquement (pas d'OFX / MT940) ; pas de connexion bancaire directe (phase 15).
- Le relevé entièrement rapproché à l'import est validé automatiquement (comportement existant conservé).
- Il reste des `window.prompt` hors finance : comptabilité, recouvrement, compte client, RH, centre de stock
  (traités dans leurs phases respectives).
