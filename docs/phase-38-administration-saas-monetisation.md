# NEXORA — PHASE 38 — ADMINISTRATION SAAS & MONÉTISATION

*Plateforme NEXORA, abonnements, licences, plans, quotas, facturation & gouvernance — V1.0*

**Version 1.0**

## 1. Objectif

Définir le fonctionnement de NEXORA comme une plateforme capable d'héberger et d'administrer plusieurs entreprises clientes, avec une gestion commerciale des abonnements, plans, licences, quotas, facturation, essais, renouvellements, suspension et gouvernance de la plateforme.

## 2. Principe

```
PLATEFORME NEXORA
       ↓
CLIENT / COMPTE ORGANISATION
       ↓
ABONNEMENT
       ↓
PLAN + OPTIONS
       ↓
LICENCES / QUOTAS
       ↓
ENTREPRISES
       ↓
UTILISATEURS / TERMINAUX
       ↓
FACTURATION NEXORA
```

- La facturation de NEXORA est distincte de la comptabilité interne des entreprises clientes.
- Une entreprise cliente peut gérer plusieurs sociétés dans son organisation selon le modèle commercial retenu.
- Les données métier des clients restent isolées.
- Les plans contrôlent principalement l'offre commerciale et les limites ; ils ne doivent pas casser les données existantes.

## 3. Modèle commercial

- Essai gratuit.
- Abonnement mensuel.
- Abonnement annuel.
- Plan personnalisé entreprise.
- Options/add-ons.
- Tarification par entreprise.
- Tarification par utilisateur lorsque pertinente.
- Tarification par terminal lorsque pertinente.
- Tarification hybride lorsque nécessaire.

## 4. Plans

- Plan Starter.
- Plan Business.
- Plan Professional.
- Plan Enterprise.
- Plan sur mesure.

Les noms et prix définitifs sont commerciaux et pourront être modifiés sans changer l'architecture.

## 5. Paramètres d'un plan

- Prix.
- Devise.
- Périodicité.
- Nombre entreprises.
- Nombre utilisateurs.
- Nombre établissements.
- Nombre terminaux.
- Stockage.
- Transactions.
- API.
- Connecteurs.
- Support.
- Fonctionnalités incluses.
- Options.
- SLA selon offre.

## 6. Fonctionnalités par plan

```
PLAN
 ↓
MODULES DISPONIBLES
 ↓
QUOTAS
 ↓
OPTIONS
 ↓
POLITIQUES
```

- Un module peut être inclus, optionnel ou indisponible.
- Une fonctionnalité déjà utilisée doit faire l'objet d'une politique de downgrade explicite.

## 7. Compte client

- Organisation cliente.
- Contact principal.
- Contacts secondaires.
- Adresse.
- Pays.
- Devise de facturation.
- Statut.
- Date création.
- Conditions commerciales.
- Identifiant client.

## 8. Tenant / organisation

- Isolation des données.
- Configuration.
- Entreprises.
- Utilisateurs.
- Abonnements.
- Factures NEXORA.
- Paiements.
- Usage.
- Support.
- Audit.

## 9. Entreprise cliente

- Une organisation peut posséder une ou plusieurs entreprises selon son offre.
- Chaque entreprise possède ses propres données, utilisateurs et configuration.
- Une personne peut administrer plusieurs entreprises si son rôle l'autorise.

## 10. Abonnement

- Actif.
- En essai.
- En attente paiement.
- En retard.
- Suspendu.
- Annulé.
- Expiré.
- Renouvellement programmé.

## 11. Cycle abonnement

```
ESSAI
 ↓
ABONNEMENT
 ↓
RENOUVELLEMENT
 ↓
PAIEMENT
 ↓
ACTIF
 ↓
RENOUVELLEMENT
      │
      └→ ÉCHEC → GRÂCE → SUSPENSION
```

## 12. Essai gratuit

- Durée configurable.
- Fonctionnalités d'essai.
- Limites.
- Date début.
- Date fin.
- Conversion.
- Rappels.
- Expiration.
- Suppression ou conservation selon politique.

## 13. Facturation

- Facture NEXORA.
- Avoir.
- Reçu.
- Échéance.
- Taxe applicable.
- Remise commerciale.
- Prorata.
- Crédit client NEXORA.
- Numérotation.
- PDF.
- Historique.

## 14. Prorata

- Upgrade en cours de période.
- Downgrade selon politique.
- Ajout utilisateur.
- Ajout entreprise.
- Ajout option.
- Calcul transparent.
- Ligne d'ajustement.

## 15. Paiements NEXORA

- Carte lorsque disponible.
- Virement bancaire.
- Mobile Money lorsque disponible.
- Passerelle locale/internationale.
- Paiement manuel validé par administrateur.
- Référence externe.
- Rapprochement.

Les moyens de paiement dépendent des fournisseurs disponibles et du pays. NEXORA doit conserver une couche de paiement interchangeable.

## 16. Échec paiement

- Tentative échouée.
- Nouvelle tentative.
- Notification.
- Période de grâce.
- Restrictions progressives.
- Suspension.
- Réactivation après paiement.

## 17. Période de grâce

- Durée configurable.
- Services maintenus ou limités selon politique.
- Notifications.
- Date limite.
- Suspension automatique.

## 18. Downgrade

- Vérification quotas.
- Fonctionnalités affectées.
- Prévisualisation impact.
- Confirmation.
- Plan inférieur.
- Données conservées.
- Fonctionnalités excédentaires rendues inactives sans suppression des données.

## 19. Upgrade

- Nouveau plan.
- Activation immédiate lorsque paiement confirmé.
- Prorata.
- Nouveaux quotas.
- Nouvelles fonctionnalités.
- Facture d'ajustement.

## 20. Add-ons

- Utilisateurs supplémentaires.
- Entreprises supplémentaires.
- Stockage.
- API.
- Connecteurs.
- Terminaux.
- Support premium.
- Fonctionnalités spécialisées.

## 21. Quotas

- Utilisateurs.
- Entreprises.
- Établissements.
- Terminaux.
- Transactions.
- Stockage.
- API requests.
- Webhooks.
- Exports.
- Documents.
- Automatisations.

## 22. Usage

- Consommation actuelle.
- Limite.
- Pourcentage.
- Prévision.
- Historique.
- Alertes.
- Dépassement.
- Top consommateurs.

## 23. Alertes commerciales

- 80 % quota.
- 90 %.
- 100 %.
- Essai bientôt terminé.
- Paiement proche échéance.
- Paiement échoué.
- Carte expirante lorsque fournisseur le permet.
- Abonnement à renouveler.
- Downgrade programmé.

## 24. Facturation par usage

- Compteur.
- Unité.
- Prix unitaire.
- Période.
- Seuil.
- Plafond.
- Arrondi.
- Facture détaillée.
- Correction contrôlée.

## 25. Free / Starter

- Onboarding simple.
- Limites claires.
- Paiement simplifié.
- Support standard.
- Upgrade rapide.

## 26. Enterprise

- Contrat.
- Tarification personnalisée.
- Facturation sur facture.
- Multi-entreprises.
- SSO.
- Politiques avancées.
- Support prioritaire.
- SLA selon contrat.

## 27. Licence

- Licence organisation.
- Licence entreprise.
- Licence utilisateur.
- Licence terminal.
- Licence module.
- Expiration.
- Activation.
- Révocation.
- Renouvellement.

## 28. Licence offline

- Jeton de licence signé.
- Durée de validité limitée.
- Renouvellement périodique.
- Grace period.
- Vérification locale.
- Protection contre modification.
- Pas de dépendance permanente à Internet pour les opérations autorisées.

## 29. Activation

```
ABONNEMENT PAYÉ
 ↓
LICENCE ACTIVE
 ↓
MODULES / QUOTAS
 ↓
ENTREPRISES
 ↓
UTILISATION
```

## 30. Suspension

- Motif.
- Date.
- Périmètre.
- Avertissement.
- Accès limité.
- Lecture seule optionnelle.
- Blocage opérations selon politique.
- Réactivation.

## 31. Annulation

- Annulation immédiate ou fin de période.
- Date effective.
- Remboursement selon politique.
- Export données.
- Période de conservation.
- Désactivation.
- Suppression ultérieure selon politique.

## 32. Portabilité des données

- Export des données métier.
- Documents.
- Clients.
- Produits.
- Stock.
- Finance.
- Comptabilité.
- Configuration.
- Format documenté.
- Fenêtre d'export après résiliation.

## 33. Réactivation

- Paiement confirmé.
- Validation abonnement.
- Réactivation licence.
- Restauration accès.
- Vérification intégrité.
- Notification.
- Audit.

## 34. Gestion plateforme

- Dashboard SaaS.
- Clients.
- Abonnements.
- Paiements.
- Factures.
- Usage.
- Quotas.
- Incidents.
- Support.
- Connecteurs.
- Licences.
- Sécurité.

## 35. Rôles NEXORA

- Super administrateur plateforme.
- Administrateur billing.
- Support.
- Finance NEXORA.
- Sécurité.
- Opérateur technique.
- Lecture seule.
- Administrateur client.

## 36. Séparation des responsabilités

- Support ne modifie pas les paiements sans permission.
- Billing ne gère pas les données métier client.
- Sécurité ne reçoit pas automatiquement accès aux données métier.
- Actions sensibles à double contrôle lorsque nécessaire.

## 37. Support

- Tickets.
- Priorité.
- SLA selon contrat.
- Client.
- Entreprise concernée.
- Abonnement.
- Historique.
- Pièces jointes.
- Escalade.
- Notes internes.

## 38. Support assisté

- Demande d'accès temporaire.
- Consentement/autorisation.
- Périmètre limité.
- Durée limitée.
- Bannière visible.
- Audit complet.
- Révocation automatique.

## 39. Catalogue

- Plans.
- Modules.
- Add-ons.
- Connecteurs.
- Services.
- Support.
- Offres promotionnelles.
- Codes promotionnels.

## 40. Promotions

- Coupon.
- Réduction %.
- Montant fixe.
- Durée.
- Premier cycle.
- Nombre utilisations.
- Client éligible.
- Expiration.
- Audit.

## 41. Taxes et facturation

- Adresse de facturation.
- Pays.
- Devise.
- Taxe applicable.
- Numéro fiscal lorsque nécessaire.
- Exonération selon règles applicables.
- Lignes facture.
- Historique.

La fiscalité de NEXORA doit être configurée selon le pays d'exploitation et validée avec les obligations locales applicables.

## 42. Devise

- Devise de prix.
- Devise facture.
- Devise paiement.
- Taux si conversion.
- Arrondi.
- Historique taux.

## 43. Comptabilité NEXORA

- Créances clients NEXORA.
- Produits.
- Taxes.
- Paiements.
- Remboursements.
- Avoirs.
- Revenue recognition lorsque nécessaire.
- Rapprochement.

## 44. Revenue / usage

Pour les offres complexes, les événements de facturation doivent être séparés des écritures comptables afin de permettre corrections, prorata, avoirs et rapprochements sans modifier silencieusement l'historique.

## 45. Portail client

- Abonnement.
- Plan.
- Factures.
- Paiements.
- Méthodes paiement.
- Utilisateurs.
- Entreprises.
- Quotas.
- Usage.
- Support.
- Exports.
- Sécurité.

## 46. Self-service

- Upgrade.
- Ajout utilisateur.
- Ajout entreprise selon quota.
- Ajout option.
- Téléchargement facture.
- Paiement.
- Renouvellement.
- Annulation.
- Gestion contacts.

## 47. Notifications SaaS

- Bienvenue.
- Essai commencé.
- Essai bientôt terminé.
- Facture créée.
- Paiement réussi.
- Paiement échoué.
- Échéance.
- Quota.
- Suspension.
- Réactivation.
- Maintenance.
- Incident.

## 48. Audit billing

- Plan changé.
- Prix changé.
- Coupon.
- Facture.
- Paiement.
- Remboursement.
- Avoir.
- Suspension.
- Réactivation.
- Quota.
- Licence.
- Accès support.

## 49. Anti-fraude

- Détection paiements suspects selon provider.
- Tentatives répétées.
- Usage anormal.
- Création massive comptes.
- Abus coupons.
- API abuse.
- Blocage contrôlé.
- Revue manuelle.

## 50. API principales

```
GET/POST /api/v1/platform/customers
GET/POST /api/v1/platform/plans
GET/POST /api/v1/platform/addons
GET/POST /api/v1/subscriptions
POST     /api/v1/subscriptions/{id}/upgrade
POST     /api/v1/subscriptions/{id}/downgrade
POST     /api/v1/subscriptions/{id}/cancel
POST     /api/v1/subscriptions/{id}/reactivate
GET      /api/v1/billing/invoices
GET      /api/v1/billing/payments
POST     /api/v1/billing/payment-methods
GET      /api/v1/usage
GET      /api/v1/quotas
GET      /api/v1/licenses
POST     /api/v1/licenses/{id}/revoke
GET      /api/v1/support/tickets
POST     /api/v1/support/tickets
```

## 51. Événements

```
TrialStarted
TrialEndingSoon
TrialExpired
SubscriptionCreated
SubscriptionUpgraded
SubscriptionDowngraded
PaymentSucceeded
PaymentFailed
InvoiceIssued
InvoicePaid
InvoiceOverdue
SubscriptionSuspended
SubscriptionCancelled
SubscriptionReactivated
QuotaThresholdReached
LicenseActivated
LicenseRevoked
```

## 52. Tests

- Essai.
- Conversion.
- Upgrade.
- Downgrade.
- Prorata.
- Paiement réussi.
- Paiement échoué.
- Grace period.
- Suspension.
- Réactivation.
- Annulation.
- Avoir.
- Remboursement.
- Quota.
- Usage.
- Licence.
- Offline licence.
- Portail client.
- Self-service.
- Multi-entreprises.
- Isolation.
- Support.
- Audit.
- Taxes.
- Devises.
- Facturation usage.

## 53. Sécurité

- Isolation stricte des clients.
- RBAC plateforme.
- MFA administrateurs.
- Secrets paiement protégés.
- Tokenisation lorsque fournie par PSP.
- Aucune donnée carte sensible inutilement stockée.
- Audit.
- Double contrôle actions critiques.
- Protection API.
- Rate limiting.

## 54. Résilience billing

- Webhook paiement idempotent.
- Réconciliation quotidienne.
- Retry.
- Dead-letter.
- État paiement explicite.
- Aucune suspension sur un événement ambigu.
- Possibilité de revue manuelle.

## 55. Métriques SaaS

- MRR.
- ARR.
- Churn.
- Activation.
- Conversion essai.
- ARPU.
- LTV lorsque calculable.
- Usage.
- Dépassement quota.
- Taux paiement réussi.
- DSO.
- Tickets.
- Disponibilité.

Les KPI commerciaux sont destinés à la gestion de la plateforme et ne doivent pas être confondus avec les KPI financiers des entreprises clientes.

## 56. Critères de sortie

- Comptes clients.
- Tenant.
- Plans.
- Abonnements.
- Essai.
- Facturation.
- Prorata.
- Paiements.
- Échecs.
- Grace period.
- Upgrade.
- Downgrade.
- Add-ons.
- Quotas.
- Usage.
- Licences.
- Offline licence.
- Suspension.
- Annulation.
- Portabilité.
- Réactivation.
- Administration.
- Rôles plateforme.
- Séparation responsabilités.
- Support.
- Catalogue.
- Promotions.
- Taxes.
- Devises.
- Comptabilité NEXORA.
- Portail client.
- Self-service.
- Notifications.
- Audit.
- Anti-fraude.
- API.
- Événements.
- Tests.
- Sécurité.
- Résilience.
- Métriques SaaS.

## 57. Étape suivante

Passer à la Phase 39 — Migration, importation initiale et onboarding avancé. Elle permettra à une entreprise existante de passer à NEXORA sans repartir de zéro : import clients, fournisseurs, produits, stocks, soldes, historiques et configuration, avec validation, prévisualisation, mapping et contrôle des erreurs.

---

**NEXORA V1.0 — PHASE 38 VALIDÉE → PHASE 39 : MIGRATION & ONBOARDING AVANCÉ**
