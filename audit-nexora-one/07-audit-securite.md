# 07 — Audit de sécurité et d'isolation des entreprises (édition 2)

> Lecture du code, requêtes de catalogue PostgreSQL et requêtes HTTP contre l'API locale de HEAD `6a47c6d` (port 5099,
> base jetable `nexora_audit_ed2`, supprimée ensuite), les 2026-10-16 et 2026-10-17. **Aucun test intrusif, aucun
> environnement de production, aucun scanner dynamique.** Édition 1 : historique Git.

## 1. Synthèse

| Domaine | Constat éd. 2 | Verdict | Anomalies |
| --- | --- | --- | --- |
| Authentification | Hachage factice contre l'énumération, verrouillage, jetons de 15 min, rafraîchissement HttpOnly avec rotation, MFA TOTP avec anti-rejeu (V102) | Solide | A-011 (résidu FAIBLE) |
| Confirmation forte | 41 actions, liée à la session ; utilisée et vérifiée sur l'extourne | **Vérifié** (exécuté) | absente sur le versement d'avance (FAIBLE) |
| Autorisation | deny-by-default ; permission vérifiée sur l'entreprise active | Défauts de périmètre | A-033, A-034, A-045, A-048 |
| Isolation par requête | `X-Company-Id` hors jeton → 403 | Vérifié (tests d'API) | — |
| Isolation en base | RLS forcée 251/291 ; contexte réappliqué après portée système | Vérifié (catalogue + lecture) | A-009 (résidu) |
| Console d'exploitation | garde plateforme sur 17 opérations | **Corrigé** (A-031) | — |
| Webhook de facturation plateforme | anonyme, jeton par défaut public accepté en production | **Défaut confirmé** | **A-049** |
| Webhooks d'intégration | signature HMAC vérifiée ; SSRF bloquée ; pas de limite de débit | Solide | FAIBLE |
| Liens publics (GED, vérification de document) | jetons aléatoires 192 / 80 bits, expiration, mot de passe BCrypt en POST, limite de débit | Solide | — |
| Injection SQL | SQL brut paramétré ; identifiants dynamiques en listes fermées | Aucune trouvée | — |
| XSS | aucun `dangerouslySetInnerHTML`/`eval` ; un `innerHTML` à texte fixe ; e-mails HTML avec variables non échappées si un gabarit HTML est créé | Risque faible | FAIBLE |
| CSV / Excel | formules neutralisées en CSV ; ClosedXML en valeurs | Solide | — |
| Secrets | garde de démarrage (JWT, MFA, connexion) ; pas pour `PlatformBilling:WebhookToken` ; clé de données = clé MFA par défaut | Lacune | A-049 |
| Pile d'exécution | renvoyée seulement en Development (`ExceptionHandlingMiddleware.cs:43`) | Vérifié | — |
| Dépendances | scan bloquant en CI : `System.Security.Cryptography.Pkcs` 10.0.0 vulnérable | **CI rouge** | A-036 |
| Journal d'audit | immuable ; privilèges réduits (V101) | Vérifié | — |

## 2. Contrôles d'accès : défauts trouvés

1. **A-045** (exécuté) : routes `/finance/*` à montants libres ; un comptable seul paie une dépense sans approbation.
2. **A-048** (exécuté) : `intelligence.view` donne aux rôles stock et achats les prévisions de CA et de trésorerie et les
   créances nominatives, refusées par `/forecasts`.
3. **A-033** (lecture) : `admin.manage_users` contrôlée sur l'entreprise active, action sur toute entreprise
   d'appartenance.
4. **A-034** (lecture) : audit de sécurité, audit RH, clonage de configuration lisent ou écrivent dans toutes les
   entreprises du jeton après un contrôle sur l'entreprise active.
5. **A-035** (lecture) : accès d'urgence marqué révoqué sans l'être.
6. Recherche globale : employés de toute l'entreprise visibles d'un Manager restreint à son équipe (FAIBLE).

## 3. Données fournies par le client

| Donnée | Constat |
| --- | --- |
| `SoldAt` | ignoré en ligne (A-003 corrigé) ; **accepté par `/sync`** depuis un appareil auto-enrôlé, jusque dans une session close (exécuté, résidu ÉLEVÉ) |
| `TaxId` de ligne | **accepté sans contrôle** (A-040, exécuté) |
| Prix forcé, remise | contrôlés par le garde-fou de tarification |
| Date de versement d'avance, date limite de purge | libres (FAIBLE) |

## 4. Fraude métier

| Scénario | Résultat éd. 2 |
| --- | --- |
| Vente dans une caisse déjà comptée | **Possible** par `/sync` (exécuté) |
| Taxe ramenée à 0 % sans remise déclarée | **Possible** (exécuté) |
| Stock créé par réceptions simultanées | **Possible** (exécuté) |
| Dépense payée sans second approbateur | **Possible** par `/finance/expenses` (exécuté) |
| Facture plateforme soldée par un tiers anonyme | **Possible** si la plateforme est utilisée (exécuté) |
| Remboursement espèces hors session, coupon multiplié | corrigés (A-002, A-012) |

## 5. Recommandations (ordre)

1. A-049 : clé obligatoire au démarrage, signature HMAC, limite de débit (ou route désactivée hors SaaS).
2. A-040, A-003 (résidu) : taxe imposée par le serveur ; enrôlement d'appareil approuvé ; refus des sessions closes.
3. A-045, A-048, A-033, A-034 : aligner les contrôles sur ceux des services métier et sur l'entreprise des données lues.
4. A-036 : épingler une version corrigée de `System.Security.Cryptography.Pkcs`.
5. A-009 (résidu), A-035, A-011 (résidu) et constats FAIBLES.
6. Tests d'autorisation par rôle sur les routes ci-dessus ; test d'intrusion externe (non fait).
