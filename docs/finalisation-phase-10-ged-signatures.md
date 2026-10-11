# Finalisation produit — Phase 10 : GED / signatures

> Plan maître « NEXORA ONE — Finalisation complète du produit », phase 10 (§25 GED).
> Branche `feature/ged`. Date : 2026-10-03.

Cycle visé par le plan : **document → demande de signature → destinataire → signature / refus → archivage**.

## 1. Travail effectué

La GED (phase 45) avait un serveur complet, mais l'écran n'en exposait qu'une partie : aucune demande de signature
ne pouvait être créée depuis l'application, les signataires apparaissaient sous forme d'identifiants techniques,
on ne pouvait ni télécharger un document ni voir ses versions, et onze actions passaient par `prompt()` / `alert()`.

| Onglet / écran | Après |
|---|---|
| Bibliothèque | recherche (texte, type, statut), **import** (nom, type, catégorie, fichier contrôlé), fiche du document |
| Fiche document | **téléchargement** de la version courante ou de n'importe quelle version, **nouvelle version** motivée, **historique des versions** (date, motif, taille, empreinte), **demande de signature**, revue, verrouillage motivé, partage temporaire (lien à copier, mot de passe facultatif), archivage / restauration, conservation légale, demande de suppression, extraction de texte, **historique (audit) du document** |
| Signatures | **« À signer par moi »** (tour en cours), toutes les demandes avec avancement et signataire attendu, signature / refus / annulation |
| Revue | documents en attente, approbation / rejet motivé (le déposant ne peut pas approuver) |
| Rétention & suppression | politiques, documents dont la rétention est échue, suppression à deux personnes |
| Audit | journal GED lisible (date, action, élément) |

Le lien de la notification (`/ged?tab=signatures`) ouvre directement l'onglet Signatures.

## 2. Fichiers créés

- `database/migrations/V087__signature_workflow.sql`
- `frontend/nexora-web/src/ged/GedDialogs.tsx`, `gedSignature.test.ts`

## 3. Fichiers modifiés

- Backend : `SignatureService.cs` (réécrit), `GedService.cs`, `GedContracts.cs`, `IGed.cs`, `GedController.cs`,
  tests `GedDocumentsTests.cs`
- Frontend : `ged/gedApi.ts`, `ged/GedSections.tsx`, `ged/GedPage.tsx` (réécrits)
- `docs/finalisation-phase-9-credit-recouvrement.md` (rectificatif, voir §9)

## 4. APIs utilisées

Nouvelle : `GET /signature-candidates` (utilisateurs habilités à signer).
Étendues : `GET /signature-requests` (`mine`, `documentId`) ; une demande renvoie désormais le document, la version
signée, le demandeur, le message, le signataire attendu et « à moi de répondre » ; `POST /ged-documents/{id}/signature`
accepte un message.
Utilisées pour la première fois par l'interface : `POST /ged-documents/{id}/signature`, `GET /ged-documents/{id}/versions`,
`GET /ged-documents/{id}/content`, `POST /ged-documents/{id}/versions`, `POST /signature-requests/{id}/cancel`,
`POST /ged-documents/{id}/cancel-deletion`, `GET /document-audit?documentId=`.

### Base de données — V087
- La demande fige la **version** à signer ; message du demandeur ; auteur et date de l'annulation.
- Signataire : qui a recueilli la réponse d'un externe, date du refus.
- **Une seule demande en cours par document** (index unique partiel).
- Modèles de notification `ged.signature_requested` et `ged.signature_outcome`.
Appliquée sur `nexora` et `nexora_recette`.

## 5. Fonctionnalités connectées — défauts corrigés

1. **N'importe quel utilisateur habilité à signer pouvait signer à la place d'un signataire externe**, et refuser
   ou annuler la demande de n'importe qui. La réponse d'un externe est désormais **recueillie** par le demandeur ou un
   gestionnaire GED, et la preuve l'indique (« recueillie par … ») ; seul le signataire désigné répond pour lui-même ;
   seul le demandeur ou un gestionnaire annule.
2. **Refus sans contrôle d'ordre** : un signataire pouvait refuser avant son tour, et le refus était possible sur
   une demande déjà close.
3. **Document bloqué pour toujours après un refus, une annulation ou une expiration** : le verrou de signature
   n'était jamais levé, et personne ne pouvait corriger le document. Il est maintenant libéré (un verrou posé pour
   une autre raison, par exemple un contrôle fiscal, reste en place).
4. **Un document signé pouvait être déverrouillé**, puis recevoir une nouvelle version : le contenu signé était
   remplacé. Refusé désormais ; un document en cours de signature ne se déverrouille pas et ne s'archive pas non plus.
5. **La signature ne portait pas sur une version précise** : l'empreinte enregistrée était celle de la version
   courante au moment de signer. La demande fige la version ; si le document change, la signature est refusée.
6. **Plusieurs demandes simultanées** sur le même document ; demande possible sur un document en revue, archivé,
   déjà signé, ou en cours de suppression.
7. **Signataires non vérifiés** : un utilisateur sans droit de signer, ou de l'entreprise d'à côté, pouvait être
   désigné ; doublons acceptés ; échéance dans le passé acceptée.
8. **Restaurer un document signé et archivé** le faisait revenir « approuvé » : il perdait son statut signé.
9. **Personne n'était prévenu** : le destinataire est maintenant notifié quand c'est son tour, le demandeur à l'issue
   (signé, refusé, expiré).

## 6. Tests ajoutés

- Intégration (`GedDocumentsTests`, +2) : règles de demande (signataires habilités, doublon, échéance, une demande
  en cours), tours et version figée, recueil de la signature d'un externe, document signé non déverrouillable,
  restauration qui garde le statut signé ; libération du document après refus, annulation et expiration (en
  conservant un verrou étranger), demande refusée sur un document en revue.
- Frontend (+3) : validation des signataires, envoi de la demande / filtre « à moi » / annulation motivée,
  nom du fichier téléchargé.

## 7. Tests exécutés

| Suite | Résultat |
|---|---|
| Unitaires backend | 50 / 50 |
| Intégration backend | 459 / 459 (dont 2 nouveaux) |
| API backend | 186 / 186 |
| Frontend Vitest | 239 / 239 |
| Scénario API (gérant, comptable, auditeur de recette) | 37 / 37 |
| Navigateur : import (champs obligatoires), verrou motivé, demande à 2 signataires (interne + externe), partage (lien vérifié), signature du comptable, signature de l'externe recueillie par le gérant, téléchargement, archivage | réussi, 0 erreur console, 0 requête en échec, 0 boîte native |

## 8. Résultats

Le cycle du plan est complet de bout en bout : le gérant importe un contrat et demande deux signatures ; le
comptable est notifié, le voit dans « À signer par moi », signe ; le gérant recueille la signature du prestataire
externe ; le document passe « Signé », reste figé, se télécharge, s'archive et se restaure sans perdre son statut.
Un refus clôt la demande, prévient le demandeur et libère le document, qui peut être corrigé (nouvelle version) et
de nouveau soumis. Rôles vérifiés : le comptable signe mais ne recueille ni n'annule ; l'auditeur lit sans agir.

## 9. Problèmes rencontrés

- **Rectificatif de la phase 9** : son rapport affirmait « plus aucun `window.prompt` dans toute l'application ».
  La vérification ne cherchait que `window.prompt`, pas `prompt(`, `alert(` ni `confirm(`. Rapport corrigé.
  Inventaire réel après cette phase : la GED n'en a plus ; **68 appels restent dans 10 fichiers**, chacun rattaché
  à sa phase du plan :

  | Fichier | Appels | Phase |
  |---|---|---|
  | `admin/AdminCenterPage.tsx` | 1 | 11 Administration |
  | `mobile/FieldOpsSections.tsx` | 6 | 12 Terrain |
  | `billing/BillingPortalPage.tsx`, `platform/PlatformConsolePage.tsx` | 3 | 15 Paiements / plateforme |
  | `security/SecurityPage.tsx`, `continuity/ContinuityPage.tsx` | 25 | 17 Sécurité |
  | `operations/MaintenanceSections.tsx`, `operations/OperationsPage.tsx` | 11 | 18 Production readiness |
  | `reports/BiSections.tsx`, `intelligence/IntelligenceSections.tsx` | 22 | 20 UX |

- Deux échecs du parcours navigateur venaient du script (sélecteurs ambigus, mauvais signataire choisi) ; vérifiés
  en base avant correction du script, aucun ne venait de l'application.

## 10. Dette technique restante

- **Pas d'invitation externe** : NEXORA n'envoie pas de lien de signature aux externes ; leur signature est recueillie
  par un utilisateur (en présence ou sur papier). Signature électronique **simple** uniquement (identité de session,
  date, empreinte) — aucune valeur avancée ou qualifiée revendiquée.
- Pas d'aperçu intégré (le document se télécharge) ni de document signé « tamponné » (PDF avec cartouche de signature).
- Pas de relance automatique des signataires avant l'échéance (l'expiration est gérée par le planificateur, désactivé
  par défaut comme en phase 9).
- Extraction de texte limitée au CSV (pas de vrai OCR) ; pas d'antivirus ni de vérification du type réel du fichier.
- Le lien de partage pointe sur l'adresse de l'interface (`/api/v1/shared/…`) : à vérifier derrière le reverse proxy
  de production (phase 18).

## 11. Prochaine phase recommandée

Phase 11 — Administration / rôles / workflows.
