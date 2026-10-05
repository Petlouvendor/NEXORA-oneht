# Finalisation produit — Phase 15 : Paiements réels / connecteurs

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 15 (§31 Paiements, §33 Connecteurs).
> Branche `feature/payments`. Date : 2026-10-05.

Exigences du plan : abstraction `IPaymentProvider` avec fournisseurs simulé, MonCash et carte ; affichage
clair **SIMULATION / RÉEL** ; MonCash préparé proprement **sans inventer d'API** ; « Tester la connexion » qui teste
réellement, avec les résultats CONNECTED, AUTHENTICATION_FAILED, TIMEOUT, UNAVAILABLE, NOT_CONFIGURED.

## 1. Travail effectué

L'inspection a montré que les paiements en ligne étaient entièrement simulés sans le dire :

- « Tester la connexion » répondait **toujours « OK »** (« réponse simulée OK »), quel que soit le connecteur ;
- toute intention de paiement recevait une référence `SIM-…`, quel que soit le fournisseur, et rien ne l'indiquait ;
- le « rappel fournisseur » manuel permettait à un utilisateur de **confirmer n'importe quel paiement**, ce qui
  créait un vrai versement client et un mouvement de trésorerie, même sur un connecteur de production ;
- le **remboursement ne faisait que changer un statut** : aucun argent ne sortait, aucune écriture n'était passée ;
- « Synchroniser » renvoyait un test de connexion présenté comme une synchronisation.

**Fournisseurs** (`IPaymentProvider`, `IPaymentProviderRegistry`) :

| Fournisseur | Mode | Comportement |
|---|---|---|
| Simulation | SIMULATION | n'appelle aucun service, le dit à chaque test ; interdite en production ; confirmation à la main (test uniquement) ; remboursement total = annulation réelle du versement |
| MonCash | RÉEL | API REST publiée par Digicel (« Rest API MonCash Documentation », 2019) : `oauth/token` (Basic client_id:secret), `v1/CreatePayment` → lien `…/Payment/Redirect?token=…`, `v1/RetrieveOrderPayment` ; bac à sable / production ; HTG seulement ; **aucune route de remboursement n'est publiée** → remboursement refusé avec la marche à suivre |
| Carte bancaire | RÉEL | aucun acquéreur intégré : « non configuré », n'appelle rien, ne prétend rien |

**Règles** : un fournisseur réel ne s'active qu'après un **test de connexion réussi** ; changer de fournisseur,
d'environnement ou d'identifiants repasse le connecteur inactif ; un paiement réel n'est confirmé **que par le
fournisseur** (« Vérifier auprès du fournisseur », ou webhook suivi d'une vérification) et au montant attendu ;
les identifiants MonCash sont **chiffrés au repos** (AES-256-GCM, clé de production déjà exigée au démarrage) et
jamais relus.

**Interface** (Centre des intégrations, section refaite) : connecteurs avec badge **SIMULATION / RÉEL**, dernier test
(statut, message, date), création d'un connecteur de paiement, saisie des identifiants, test réel, activation ;
paiements en ligne avec badge de mode, **demande de paiement** (client, montant, compte d'encaissement), lien de
paiement MonCash, vérification auprès du fournisseur, boutons « Simuler : payé / refusé » réservés à la simulation,
remboursement motivé.

## 2. Fichiers créés

- `database/migrations/V091__payment_providers.sql`
- `backend/src/Nexora.Application/Integrations/PaymentProviders.cs`
- `backend/src/Nexora.Infrastructure/Integrations/PaymentProviders.cs`
- `frontend/nexora-web/src/integrations/PaymentsSection.tsx`, `paymentsApi.test.ts`

## 3. Fichiers modifiés

- Backend : `IntegrationRegistryService.cs`, `PaymentGatewayService.cs`, `WebhookServices.cs`,
  `Application/Integrations/Abstractions.cs`, `Contracts/Integrations/IntegrationContracts.cs`,
  `Api/Controllers/IntegrationsController.cs`, `Infrastructure/DependencyInjection.cs`
- Tests : `IntegrationsEcosystemTests.cs` (4 adaptés, 3 ajoutés), `FinanceEngineFixture.cs` (point d'extension)
- Frontend : `integrations/IntegrationsCenterPage.tsx`, `integrationsApi.ts`, `shared/theme.css` (fenêtres)

## 4. APIs

Nouvelles : `GET /integrations/payment-providers`, `PUT /integrations/connectors/{id}/credentials` (écriture seule),
`POST /integrations/payments/intents/{id}/verify`.
Modifiées : `POST /integrations/connectors/{id}/test` (test réel, statut parmi les cinq du plan, mode),
`POST /integrations/payments/intents/{id}/callback` (simulation seulement), `…/refund` (règles ci-dessus),
`POST /integrations/{id}/sync` (refus explicite), connecteurs et intentions (mode, identifiants configurés, dernier
test, lien de paiement, numéro de commande).

### Base de données — V091
Identifiants chiffrés et indice, résultat du dernier test (contrôlé), mode de chaque paiement (les paiements
existants, tous simulés, sont marqués « simulation »), lien de paiement et numéro de commande fournisseur (unique par
connecteur). Le connecteur de démonstration `moncash-sim` est reclassé fournisseur « simulation ».
Appliquée sur `nexora` et `nexora_recette`.

## 5. Défauts corrigés

1. Test de connexion toujours « OK » → test réel, cinq statuts, message compréhensible, résultat conservé.
2. Paiements simulés indiscernables des paiements réels → mode affiché partout ; simulation interdite en production.
3. **Confirmation manuelle d'un paiement réel** (argent jamais reçu enregistré en caisse) → réservée à la simulation.
4. **Remboursement fictif** (statut seul) → annulation réelle du versement en simulation ; refus motivé chez MonCash.
5. Webhook « payé » accepté sur parole → pour un paiement réel, vérification auprès du fournisseur.
6. Fournisseur de paiement en texte libre (« simpay », « flashpay » acceptés) → seuls les fournisseurs disponibles.
7. Activation d'un connecteur réel jamais testé → refusée.
8. « Synchroniser » présentait un test comme une synchronisation → refus explicite.
9. **Fenêtres de dialogue trop étroites** (toute l'application) : la fenêtre faisait 560 px alors que de nombreux
   formulaires en demandent 520 à 900 ; les champs débordaient à droite. La fenêtre s'élargit désormais à la largeur
   minimale de son contenu, dans la limite de l'écran.
10. Page des intégrations débordant de 768 px sur téléphone.

## 6. Tests ajoutés

- Intégration (+3, 4 adaptés) : tests honnêtes (simulation annoncée, MonCash / carte / sans adaptateur
  « non configuré », activation sans test refusée, simulation en production refusée) ; **faux serveur MonCash conforme
  à la documentation** : authentification refusée, indisponibilité, délai dépassé, connexion, identifiants chiffrés
  et jamais relus ; paiement réel : devise, lien de paiement, requêtes conformes, confirmation manuelle refusée,
  vérification (en attente, payé, idempotente), remboursement refusé, montant différent → aucun paiement ;
  remboursement de simulation qui annule réellement le versement.
- Frontend (+3) : actions selon le mode, identifiants, création idempotente et vérification.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 475 / 475 (exécutée de jour : aucun test de date en échec) |
| API backend | 187 / 187 |
| Frontend Vitest | 256 / 256 (55 fichiers) |
| Scénario API (direction, gérant) — dont **un vrai appel au bac à sable MonCash** | 28 / 28 |
| Navigateur : connecteur de simulation créé, testé, activé ; connecteur MonCash, identifiants, test réel (« Authentification refusée ») ; paiement de simulation demandé, confirmé, remboursé ; téléphone | réussi, 0 erreur hors le refus d'activation attendu (409), 0 boîte native |

## 8. Résultats

Plus aucun paiement simulé ne peut passer pour réel, et plus aucun test ne répond « OK » par défaut. MonCash est prêt :
il suffit de saisir les identifiants du portail MonCash Business, de tester la connexion, puis d'activer. Le test
contre le bac à sable réel a répondu « authentification refusée » en 0,6 s avec de faux identifiants, ce qui prouve
que l'appel part bien chez Digicel et que la réponse est interprétée honnêtement.

## 9. Problèmes rencontrés

- La documentation MonCash n'est publiée qu'en PDF : texte extrait avec `pypdf` (installé pour l'occasion) afin de
  s'en tenir exactement aux routes et champs documentés.
- MonCash ne documente ni webhook de notification ni remboursement : la confirmation passe par la vérification
  `RetrieveOrderPayment`, et le remboursement est refusé avec la procédure manuelle.
- Le parcours navigateur a d'abord cherché un client d'une autre entreprise que l'entreprise active ; l'écran affiche
  maintenant « Aucun client trouvé dans cette entreprise ».

## 10. Dette technique restante

- **Aucun identifiant MonCash réel** n'est disponible : le parcours de paiement réel est prouvé contre un faux serveur
  conforme à la documentation et par un appel d'authentification réel, pas par un vrai paiement.
- Pas de vérification automatique périodique des paiements MonCash en attente (vérification à la demande).
- Paiement par **carte** : aucun acquéreur intégré.
- Remboursement partiel non pris en charge ; remboursement MonCash manuel (portail + annulation du versement).
- Les connecteurs non « paiement » (banque, e-commerce, messagerie…) n'ont pas d'adaptateur : ils sont « non
  configurés » (les notifications sont l'objet de la phase 16).

## 11. Prochaine phase recommandée

Phase 16 — Notifications.
