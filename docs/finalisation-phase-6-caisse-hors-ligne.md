# Finalisation produit — Phase 6 : Caisse hors ligne, application installable

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 6.
> Branche `feature/pos-offline`. Date : 2026-09-28.

## 1. Travail effectué

Avant cette phase, la caisse « hors ligne » ne tenait que si l'onglet restait ouvert : un rechargement sans réseau
affichait une page d'erreur, la recherche d'article échouait (aucun catalogue local), la session était fermée
(retour à la connexion), et la synchronisation exigeait de **saisir à la main l'identifiant technique de
l'appareil**. Désormais :

| Besoin | Réalisé |
|---|---|
| Rouvrir la caisse sans réseau | Application installable (manifeste, icônes) + service worker : la coque de l'application est servie depuis le cache ; l'API n'est **jamais** mise en cache. |
| Rester connecté sans réseau | Le jeton et le profil sont conservés chiffrés ; une coupure réseau ne déconnecte plus — seul un refus du serveur (401/403) ferme la session. |
| Vendre sans réseau | Profil hors ligne téléchargé en ligne toutes les 10 min (catalogue vendable de l'emplacement, **prix de vente seuls, jamais de coût**, politique hors ligne, session, moyens d'encaissement), gardé **chiffré** et lié à l'utilisateur et à l'entreprise. Recherche et scan locaux (nom sans accents, SKU, code-barres, PLU). |
| Synchroniser | Le navigateur de caisse **s'enregistre lui-même** comme appareil (plus de saisie manuelle) ; un responsable peut le suspendre ou le révoquer. Rejeu automatique au retour du réseau ; le motif d'un échec est affiché sous la vente en attente. |
| Politique hors ligne | Appliquée **avant** la mise en file (la caisse refuse ce qui partirait en conflit) et **réappliquée par le serveur** : moyens autorisés (espèces seules par défaut — la carte n'est même pas proposée), remises, crédit et plafond, durée maximale sans contact avec le serveur. |
| Arbitrer un conflit | « Le poste a raison » **enregistre réellement la vente** (rejeu immédiat) ; si elle reste impossible (stock, crédit client), le conflit est clos et un nouveau est ouvert avec la raison. |

## 2. Défauts corrigés

1. **La caisse encaissait le prix hors taxe** (en ligne comme hors ligne). 46 articles sur 48 de la recette ont une TVA
   de 10 % non incluse : la caisse annonçait 390, le serveur calculait 429. Vente refusée (« client obligatoire pour
   une vente à crédit ») pour un client de passage, ou **crédit silencieux du montant de la taxe** pour un client.
2. **La caisse ignorait les promotions** : pour une bière en promotion (−10 %), le serveur enregistrait un total
   inférieur au montant encaissé et comptait la différence en « monnaie rendue » jamais rendue — écart de caisse
   assuré à la clôture (mesuré : 192,50 encaissés, vente de 173,25, 19,25 de rendu fictif).
   La caisse affiche maintenant le montant calculé par le serveur (prix, promotions, taxes) et bloque l'encaissement
   tant qu'il n'est pas connu.
3. Rechargement de la caisse sans réseau → page d'erreur du navigateur.
4. Coupure réseau au démarrage → **déconnexion** (le moindre échec de `/me` fermait la session).
5. Connexion puis rechargement immédiat → **session perdue** (le jeton était écrit en arrière-plan).
6. Recherche et scan impossibles sans réseau.
7. Synchronisation impossible sans saisir à la main un identifiant d'appareil technique.
8. Politique hors ligne partiellement ignorée à la synchronisation (moyens de paiement, remises, durée hors ligne).
9. « Le poste a raison » ne faisait que remettre l'opération « à rejouer » : le poste ne renvoyant jamais une
   opération en conflit, la vente n'était **jamais enregistrée**.
10. Appareil suspendu : synchronisation acceptée (seul « révoqué » était refusé).
11. Échec de synchronisation invisible pour le caissier (la vente restait « pending » sans explication).

## 3. Base de données

Aucune migration : `sync_device`, `offline_policy`, `sync_operation`, `sync_conflict` existaient (phase 10). Les
conflits de politique utilisent le type existant `validation`.

## 4. APIs

Nouvelles : `POST /pos/device` (enregistrement de l'appareil, idempotent, refusé si révoqué/suspendu),
`GET /pos/offline-profile`, `POST /pos/cart/preview` (montant exact, rien n'est écrit, jamais de coût).
Modifiées : `GET /pos/products/search|scan` renvoient le taux de taxe et si le prix est TTC ;
`POST /sync/batch` applique toute la politique hors ligne ; `POST /sync/conflicts/{id}/resolve` rejoue la vente.

## 5. Règles garanties

- Une vente hors ligne n'est **jamais** présentée comme confirmée avant la synchronisation ; identifiant d'opération
  unique (aucune double vente au rejeu).
- Catalogue et profil hors ligne chiffrés (AES-GCM, clé non exportable) ; sans contexte sécurisé, rien n'est
  conservé et la caisse l'indique.
- Le serveur recalcule tout (prix, promotions, taxes, stock, crédit) à la synchronisation.

## 6. Tests

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend (dont 4 nouveaux `PosOfflineTests`) | 449 / 449 |
| API backend | 186 / 186 |
| Frontend Vitest (dont 6 nouveaux), fuseau `America/Port-au-Prince` | 224 / 224 |
| Scénario API (clone `nexora_qa`) : appareil, profil, révocation | 7 / 7 |
| Navigateur, **build de production** : connexion, coupure réseau, rechargement, vente, reconnexion | réussi : page rouverte hors ligne, catalogue du coffre, vente en file puis **synchronisée** |
| Navigateur, en ligne : article taxé en promotion | affiché 346,50 = enregistré 346,50, rendu 0 |

## 7. Dette technique restante

- **Promotions hors ligne** : non calculées sans réseau (la caisse le signale). Une vente hors ligne d'un article en
  promotion est enregistrée au prix promotionnel à la synchronisation ; l'écart apparaît en excédent de caisse à la
  clôture. Correctif possible : embarquer les promotions actives dans le profil hors ligne.
- Retours et versements clients hors ligne : non proposés à l'écran (le serveur sait rejouer un versement).
- Un seul paquet JavaScript de 1,15 Mo : tout est disponible hors ligne après la première visite, mais le premier
  chargement est lourd (découpage à prévoir, phase 20).
- Le service worker ne s'active qu'en production (HTTPS ou localhost) ; en HTTP simple, pas de hors ligne.
