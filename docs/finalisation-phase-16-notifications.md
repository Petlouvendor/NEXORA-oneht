# Finalisation produit — Phase 16 : Notifications

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 16 (§32 Notifications).
> Branche `feature/notifications`. Date : 2026-10-05.

Exigences du plan : préparer proprement e-mail, SMS et push avec **fournisseur, configuration, statut, relances,
journal, erreurs, modèles** ; si le fournisseur n'est pas configuré, **ne pas prétendre que le message a été
envoyé** et afficher « fournisseur non configuré ».

## 1. Travail effectué

Les fournisseurs réels existaient (SMTP via MailKit, Twilio, Firebase Cloud Messaging), mais :

- le canal **webhook de notification** renvoyait **« envoyé »** sans rien envoyer ;
- un canal non configuré envoyait chaque message en **rejet définitif** (« dead ») avec une **alerte à
  l'administrateur à chaque message** : un serveur sans SMTP noyait les administrateurs ;
- une adresse absente était traitée comme une panne passagère (relances jusqu'au rejet, puis alerte) ;
- le **planificateur est arrêté par défaut** : les e-mails et SMS restaient en file sans que personne ne le sache ;
- aucun écran ne montrait les fournisseurs, la file, le journal ni les modèles ; aucun test de connexion ;
- push : la **priorité Android était inversée** (les messages les moins importants passaient en priorité haute).

**Désormais** :

| Élément | Comportement |
|---|---|
| Fournisseur non configuré | état **« fournisseur non configuré »** : rien n'est parti, aucune relance, aucune alerte ; relance groupée une fois le fournisseur configuré |
| Test de connexion | **réel**, sans envoyer de message : SMTP (connexion + authentification), Twilio (lecture du compte), Firebase (envoi « à blanc ») → CONNECTED, AUTHENTICATION_FAILED, TIMEOUT, UNAVAILABLE, NOT_CONFIGURED |
| Message de test | vrai message remis au fournisseur, résultat inscrit au journal |
| Journal | chaque tentative : fournisseur, résultat, erreur, durée ; adresses masquées |
| Relances | automatiques (délai croissant) ; manuelles pour un envoi en échec ou abandonné ; « traiter la file maintenant » quand le planificateur est arrêté (signalé) |
| Préférences | l'utilisateur est averti qu'un canal choisi n'enverra rien tant que son fournisseur n'est pas configuré |
| Modèles | liste (système / entreprise), modification, variables détectées, aperçu |

**Interface** : la page Notifications gagne les onglets **Envois** (permission `notifications.manage_delivery`) et
**Modèles** (`notifications.manage_templates`).

## 2. Fichiers créés

- `database/migrations/V092__notification_delivery.sql`
- `backend/src/Nexora.Infrastructure/Notifications/NotificationDeliveryAdmin.cs`
- `backend/src/Nexora.Api/Controllers/NotificationDeliveryController.cs`
- `frontend/nexora-web/src/notifications/DeliveryAdmin.tsx`, `deliveryApi.test.ts`

## 3. Fichiers modifiés

- Backend : `SmtpEmailChannel.cs`, `TwilioSmsChannel.cs`, `FcmPushChannel.cs`, `NotificationDispatcher.cs`,
  `Application/Notifications/INotifications.cs`, `Contracts/Notifications/NotificationContracts.cs`,
  `Application/Security/PermissionCatalog.cs`, `Api/Controllers/NotificationsController.cs`,
  `Infrastructure/DependencyInjection.cs`
- Tests : `NotificationEngineTests.cs`, `RecetteCorrectionsTests.cs`
- Frontend : `notifications/NotificationsPage.tsx`, `notificationsApi.ts`

## 4. APIs

Nouvelles (`/notification-delivery`, permission `notifications.manage_delivery`) : `GET providers`,
`POST providers/{canal}/test`, `POST providers/{canal}/requeue`, `POST test-message`, `GET summary`,
`GET outbox?status=&channel=`, `GET outbox/{id}/log`, `POST outbox/{id}/retry`, `POST dispatch`.
`GET /notification-preferences/channels` (tout utilisateur : canal → fournisseur présent ou non, sans détail).
Utilisées pour la première fois par l'interface : `GET/POST /message-templates`, `POST /message-templates/render`.

### Base de données — V092
État `not_configured` ; fournisseur et heure de la dernière tentative ; table `notification_delivery_log`
(cloisonnée par entreprise) ; table `notification_channel_test` (dernier test par canal) ; permission
`notifications.manage_delivery` accordée aux rôles qui géraient déjà les modèles ; audit des modèles et des tests.
Appliquée sur `nexora` et `nexora_recette`.

## 5. Défauts corrigés

1. **Faux « envoyé »** du canal webhook de notification (et de tout canal sans fournisseur).
2. **Alerte administrateur à chaque message** quand un fournisseur n'est pas configuré.
3. Adresse absente relancée comme une panne passagère → échec définitif explicite.
4. **Priorité Android inversée** pour les notifications push.
5. Aucun test de connexion des fournisseurs → test réel par canal.
6. File invisible et **planificateur arrêté sans avertissement** → état de la file, alerte, traitement à la demande.
7. Aucun journal des tentatives, aucune relance manuelle.
8. Modèles non administrables depuis l'interface ; canal « interne » non libellé.
9. Préférences permettant de choisir un canal sans fournisseur sans le dire.
10. En cas de surcharge d'un canal (tests, extensions), le répartiteur prenait le premier enregistré au lieu du dernier.

## 6. Tests ajoutés

- Intégration : sans fournisseur rien n'est « envoyé » ni escaladé (e-mail, SMS, push, webhook), états, journal,
  relances refusées, adresse masquée, message de test ; **canal e-mail contre un vrai dialogue SMTP** (serveur local :
  authentification refusée, connexion, message réellement transmis, port fermé) ; relances avec délai puis abandon,
  journalisées (canal de test en panne).
- Frontend (+3) : règles de relance, état « non configuré » distinct d'« envoyé », routes.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 477 / 477 |
| API backend | 187 / 187 |
| Frontend Vitest | 259 / 259 (56 fichiers) |
| Scénario API (direction, gérant) avec serveur SMTP local | 25 / 25 — **un vrai e-mail transmis et reçu** |
| Navigateur : avertissement dans les préférences (gérant) ; fournisseurs, test réel, message de test, file, journal, détail, modèles et aperçu (direction) ; téléphone | réussi, 0 erreur hors l'adresse invalide refusée (422 attendu), 0 boîte native |

## 8. Résultats

Un administrateur voit, canal par canal, quel fournisseur est branché, le teste réellement, envoie un message de
test et suit chaque envoi jusqu'à sa remise ou son échec motivé. Sans fournisseur, l'application le dit au lieu
d'annoncer un envoi fictif ou de multiplier les alertes.

## 9. Problèmes rencontrés

- Aucun compte SMTP, Twilio ni Firebase réel n'est disponible : la QA utilise un **serveur SMTP local** qui parle le
  vrai protocole (le canal e-mail a réellement transmis le message). SMS et push sont vérifiés en « non configuré ».
- Le parcours navigateur a d'abord visé la carte englobante au lieu de celle du canal, puis cherché des envois d'une
  autre entreprise que l'entreprise active : scripts corrigés, écran conforme.

## 10. Dette technique restante

- Fournisseurs configurés **au niveau du serveur** (une configuration pour toute l'installation), pas par entreprise
  depuis l'interface.
- WhatsApp et webhook de notification : aucun fournisseur intégré (« non configuré »).
- Heures calmes évaluées en UTC (limitation connue de la phase 34) ; résumés quotidiens / hebdomadaires non envoyés.
- Pour un SMS, « envoyé » signifie accepté par Twilio, pas encore remis au téléphone (pas de rappel de statut).
- L'aperçu d'un modèle montre la version enregistrée, pas le brouillon en cours.

## 11. Prochaine phase recommandée

Phase 17 — Sécurité.
