# Finalisation produit — Phase 19 : E2E global

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 19 (§41 Tests, §42 Tests métier
> obligatoires, §43 Règle de cohérence financière). Branche `feature/e2e-global`. Date : 2026-10-06.

## 1. Travail effectué

**État trouvé** : les parcours au navigateur des phases précédentes étaient des scripts jetables, hors du dépôt. Aucune
suite E2E versionnée, aucune dans la CI, et aucun des cinq scénarios obligatoires du §42 n'était automatisé de bout
en bout.

**Réalisé** : une suite **Playwright versionnée** (`frontend/nexora-web/e2e/`) :

- chaque scénario agit dans l'interface comme un utilisateur (libellés et rôles accessibles) ;
- elle tourne contre une **installation neuve** (pile de production de la phase 18, en HTTP sur localhost) ;
- les effets croisés (stock, caisse, créances, dettes, comptabilité) sont vérifiés par l'API ;
- trois personnes interviennent : la direction, un caissier, un responsable de magasin ;
- la suite est intégrée à la CI.

| Scénario | Résultat |
|---|---|
| 1 — produit → fournisseur → commande → réception → stock → vente → caisse → client → comptabilité | réussi |
| 2 — client → vente à crédit → créance → versements → solde | réussi |
| 3 — inventaire → écart → validation → mouvement → audit (+ écriture d'écart) | réussi |
| 4 — caisse hors ligne → coupure → vente → reconnexion → synchronisation → **rejeu sans doublon** | réussi |
| 5 — commande → réception partielle → facture → rapprochement à trois voies → paiement | réussi |
| 6 — cohérence globale : balance, écritures, créances = 4111, dettes = 4011 | réussi |
| 6 — stock valorisé = 3110 | **défaut connu**, déclaré (`test.fixme`), voir §9 |

## 2. Fichiers créés

- `frontend/nexora-web/playwright.config.ts`
- `frontend/nexora-web/e2e/` :
  - `support.ts` (connexions, vérification API, gestes métier communs) et `global-setup.ts` ;
  - `scenario1-achat-vente`, `scenario2-vente-credit`, `scenario3-inventaire`, `scenario4-caisse-hors-ligne`,
    `scenario5-achat-rapprochement`, `scenario6-coherence-globale` (`.spec.ts`).
- `frontend/nexora-web/.gitignore`, `src/inventory/locationSelect.test.tsx`
- `infrastructure/docker-compose.e2e.yml`
- `docs/exploitation/tests-e2e.md`, ce document

## 3. Fichiers modifiés

- `src/inventory/stockShared.tsx` (`LocationSelect`), `src/purchasing/purchasingShared.tsx` et `InvoicesPanel.tsx`
  (statuts de rapprochement).
- `vite.config.ts` (rapports E2E hors surveillance), `package.json` (`@playwright/test`, script `e2e`).
- `backend/src/Nexora.Api/Program.cs` (limite de connexion réglable), `ProductionConfiguration.cs` (contrôle
  `login_rate_limit`) et son test.
- `.github/workflows/ci.yml` (job `e2e`).

## 4. APIs utilisées

Interface : catalogue, fournisseurs, clients, commandes, réceptions, factures, rapprochement, dettes et paiements,
caisse (ouverture, encaissement, clôture), inventaires, synchronisation hors ligne. Vérifications :
`/stock/balances`, `/stock/movements`, `/accounting-entries`, `/trial-balance`, `/customers/{id}/account`,
`/payables`, `/purchasing/invoices/{id}`, `/security/audit`, `/pos/sessions/current`, `/sync/batch` (rejeu).

Nouveau réglage : `Security:LoginRateLimitPerMinute`, 10 par défaut, signalé « bloquant » en production au-delà.

## 5. Défauts révélés et corrigés

1. **Réception sur une installation neuve** : le seul emplacement (« Magasin ») n'était pas présélectionné. Corrigé
   dans le composant partagé : un emplacement obligatoire, seul choix possible, est présélectionné (réceptions,
   inventaires, ajustements, transferts).
2. **Rapprochement des factures fournisseur affiché en anglais brut** (« unmatched ») : la table de libellés
   traduisait deux statuts inexistants et en ignorait trois réels (`unmatched`, `blocked`, `waived`). Corrigé, avec les
   couleurs.
3. **Limite de connexion figée** : réglable pour un environnement de test, contrôlée en production.

## 6. Tests ajoutés

- E2E : 6 fichiers, 10 tests, dont 1 défaut connu déclaré.
- Frontend (+3) : présélection de l'emplacement unique, aucun choix imposé sinon, libellé de chaque statut réel.
- Backend (+1) : une limite de connexion élargie est bloquante en production.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| E2E sur installation neuve (images construites) | 9 / 9 + 1 défaut connu, **trois passages consécutifs** sur la même base |
| Unitaires backend | 59 / 59 |
| Intégration backend | 484 / 484 |
| API backend | 194 / 194 |
| Frontend Vitest | 270 / 270 (59 fichiers), types valides |

## 8. Résultats

Les cinq scénarios métier obligatoires du plan fonctionnent de bout en bout sur une installation neuve, menés par trois
personnes distinctes, sans boîte native ni erreur serveur. Les écritures sont équilibrées, et créances et dettes
concordent avec le grand livre.

## 9. Problèmes rencontrés

- **Défaut de cohérence stock / comptabilité (§43), à décider.** Une réception augmente le stock valorisé sans aucune
  écriture : le stock n'entre en comptabilité qu'à la facture fournisseur, qui débite directement 3110. Les réceptions
  non encore facturées manquent donc au compte 3110. Le décalage est rattrapé à la facture, mais il fausse le stock du
  grand livre en fin de période ; il est **définitif pour une réception jamais facturée** (réception sans commande ni
  facture). Correction proposée : compte « fournisseurs — factures non parvenues » (4081) crédité à la réception et
  soldé par la facture, avec l'écart de prix et les retours. C'est un changement de modèle comptable, pas une
  correction d'écran : **décision demandée**. Le test correspondant redeviendra bloquant ensuite.
- Règle des quatre yeux : la direction ne peut pas valider la clôture de sa propre caisse. La suite a donc été conçue
  avec un caissier et un responsable distincts.
- Limite de 10 connexions par minute et par adresse atteinte par la suite : connexions API mises en cache, limite
  réglable en E2E seulement.
- Vite rechargeait la page à chaque écriture de rapport Playwright, et OneDrive verrouillait les traces : artefacts
  placés hors du dépôt.

## 10. Dette technique restante

- Cohérence stock / grand livre : voir §9 (décision).
- Les scénarios couvrent le cœur commerce, achats et stock. Restaurant, terrain, paie, groupe, GED et signatures ont
  leurs tests d'intégration et d'API, mais pas encore de scénario E2E.
- Navigateur Chromium seulement.

## 11. Prochaine phase recommandée

Phase 20 — UX, performance et finitions, dont les 40 boîtes natives restantes, puis le rapport final
« NEXORA ONE — FINAL READINESS REPORT ».
