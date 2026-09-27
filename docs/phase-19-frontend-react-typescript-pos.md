# NEXORA — PHASE 19 — FRONTEND REACT / TYPESCRIPT & POS

**Architecture de l'application web, de la caisse et du client offline**
Version 1.0

## 1. Objectif

Définir l'architecture frontend de NEXORA afin de fournir une expérience rapide, premium et
cohérente sur dashboard, gestion, finance, comptabilité et POS. Le frontend doit consommer l'API
.NET, respecter les scopes de sécurité et pouvoir fonctionner avec un mode offline contrôlé.

## 2. Stack de référence

- TypeScript.
- React.
- React Router ou équivalent pour la navigation.
- Client HTTP typé.
- Gestion d'état serveur et cache adaptés.
- Store local pour le POS/offline.
- Design system NEXORA.
- Tests unitaires et end-to-end.
- PWA ou mécanisme équivalent si retenu pour certaines fonctions offline.

Les bibliothèques exactes seront figées lors de la préparation du projet ; aucune dépendance ne
doit être choisie uniquement pour sa popularité.

## 3. Principes frontend

- Le serveur reste la source d'autorité pour les règles métier.
- Le frontend ne doit pas contourner les permissions.
- Les écrans sont organisés par domaine métier.
- Le POS est optimisé pour la vitesse.
- Les opérations critiques affichent leur état réel.
- Le mode offline ne simule jamais une confirmation externe non reçue.
- Les composants communs proviennent du Design System.
- Accessibilité intégrée dès le départ.

## 4. Structure du projet

```
src/
├── app/
│   ├── router/
│   ├── providers/
│   ├── permissions/
│   └── config/
├── features/
│   ├── auth/
│   ├── organization/
│   ├── catalog/
│   ├── inventory/
│   ├── sales/
│   ├── pos/
│   ├── purchases/
│   ├── customers/
│   ├── suppliers/
│   ├── payments/
│   ├── finance/
│   ├── accounting/
│   ├── reporting/
│   ├── consolidation/
│   ├── workflows/
│   ├── integrations/
│   ├── documents/
│   ├── notifications/
│   ├── audit/
│   └── sync/
├── components/
├── layouts/
├── services/
├── stores/
├── hooks/
├── utils/
├── styles/
└── types/
```

## 5. Architecture par feature

- Chaque feature possède ses pages, composants métier, hooks, services API et types lorsque
  pertinent.
- Les composants génériques restent dans components.
- Les règles financières restent côté backend.
- Les features ne doivent pas créer des dépendances circulaires.

## 6. Layouts

- Public layout.
- Authentication layout.
- Application layout.
- POS layout.
- Reporting layout.
- Administration layout.

## 7. Navigation

```
APP
├── Dashboard
├── POS
├── Ventes
├── Produits
├── Stocks
├── Achats
├── Clients
├── Fournisseurs
├── Finance
├── Comptabilité
├── Rapports
├── Groupe
├── Intégrations
└── Administration
```

- Menu selon permissions.
- Contexte entreprise permanent.
- Recherche globale.
- Favoris.
- Raccourcis.
- Breadcrumbs lorsque utiles.

## 8. Contexte de travail

```
GROUPE → ENTREPRISE → ÉTABLISSEMENT → CAISSE
```

- Context selector.
- Contexte affiché dans le header.
- Avertissement lors d'un changement.
- Rafraîchissement des données dépendantes.
- Aucune donnée d'un ancien contexte ne doit rester visible par erreur.

## 9. Gestion des permissions

- Permissions récupérées depuis le backend.
- Guards de route.
- Guards d'action.
- Masquage des actions non autorisées.
- Ne jamais considérer le masquage UI comme sécurité suffisante.
- Gestion des réponses 401/403.

## 10. Authentification

- Login.
- MFA lorsque activé.
- Session.
- Refresh selon stratégie retenue.
- Logout.
- Expiration.
- Réauthentification action sensible.
- Gestion appareil.
- Écran session expirée.

## 11. Client API

- Instance HTTP centralisée.
- Headers d'authentification.
- Correlation ID.
- Idempotency key lorsque nécessaire.
- Gestion erreurs.
- Retry seulement pour opérations sûres.
- Cancellation.
- Parsing DTO.
- Gestion 401/403.

## 12. Gestion d'état

- État serveur : cache/query layer.
- État UI : composants/hooks.
- État session : contexte sécurisé.
- Panier POS : store local.
- Offline queue : stockage persistant.
- Éviter de placer toutes les données dans un store global.

## 13. Dashboard

- KPI.
- Graphiques.
- Alertes.
- Tendance.
- Comparaisons.
- Actions rapides.
- Widgets configurables.
- Filtres période/entreprise.
- Drill-down.

## 14. Dashboard direction

- CA.
- Marge.
- Bénéfice.
- Trésorerie.
- Créances.
- Dettes.
- Stock.
- Performance entreprises.
- Consolidation.
- Alertes majeures.

## 15. Dashboard comptable

- Périodes.
- Écritures en attente.
- Rapprochements.
- Créances.
- Dettes.
- Anomalies.
- Clôtures.
- Tâches.
- Vue entreprise/groupe selon permission.

## 16. Dashboard manager

- Ventes du jour.
- Marge.
- Stock critique.
- Caisse.
- Crédits.
- Dépenses.
- Objectifs.
- Anomalies.

## 17. POS — objectifs UX

- Vitesse.
- Lisibilité.
- Tactile.
- Clavier.
- Scanner.
- Faible nombre de clics.
- Tolérance aux erreurs.
- Feedback immédiat.
- Accessibilité.

## 18. POS — écran principal

```
┌─────────────────────────────────────────────┐
│ RECHERCHE / SCANNER / CLIENT / CONTEXTE    │
├──────────────────────┬──────────────────────┤
│ CATÉGORIES            │ PANIER               │
│ PRODUITS RAPIDES      │ ARTICLES             │
│ FAVORIS               │ REMISES              │
│                       │ SOUS-TOTAL           │
│                       │ TAXES                │
│                       │ TOTAL                │
├──────────────────────┴──────────────────────┤
│ PAIMEMENT / SUSPENDRE / RETOUR / ACTIONS   │
└─────────────────────────────────────────────┘
```

## 19. Recherche POS

- Code-barres.
- Nom.
- SKU.
- Référence.
- Recherche tolérante.
- Résultats rapides.
- Produits favoris.
- Recherche client.
- Recherche lot si nécessaire.

## 20. Panier

- Ajouter.
- Modifier quantité.
- Supprimer.
- Remise autorisée.
- Taxe.
- Lot lorsque nécessaire.
- Note.
- Client.
- Suspension.
- Restauration.

## 21. Paiement POS

- Espèces.
- Carte.
- Mobile Money.
- QR/lien.
- Paiement multiple.
- Crédit client.
- Montant reçu.
- Rendu monnaie.
- Statut fournisseur.
- Reçu.

## 22. Crédit POS

```
CLIENT → ÉLIGIBILITÉ → LIMITE → VENTE → CRÉANCE → CONFIRMATION
```

- Afficher limite disponible.
- Afficher solde.
- Afficher échéance.
- Refuser si règle non satisfaite.
- Autorisation exceptionnelle selon permission.
- Audit.

## 23. Vente suspendue

- Nom/session.
- Panier conservé.
- Client.
- Utilisateur.
- Horodatage.
- Restauration contrôlée.
- Expiration selon politique.

## 24. Retours

- Scanner/chercher document source.
- Sélection lignes.
- Quantité.
- Motif.
- Impact stock.
- Impact paiement.
- Remboursement.
- Confirmation.
- Audit.

## 25. Clôture caisse

- Résumé ventes.
- Moyens de paiement.
- Attendu.
- Compté.
- Écart.
- Motif écart.
- Validation.
- Reçu de clôture.
- Audit.

## 26. POS offline

```
ONLINE
 ↓
API
 ↓
CONFIRMED

OFFLINE
 ↓
LOCAL QUEUE
 ↓
PENDING SYNC
 ↓
SERVER
 ↓
CONFIRMED / CONFLICT / FAILED
```

- Badge offline.
- Compteur en attente.
- État de chaque opération.
- Synchronisation automatique.
- Retry.
- Résolution conflits.
- Aucune double vente.

## 27. Stock frontend

- Stock disponible.
- Mouvements.
- Lots.
- Expiration.
- Inventaires.
- Transferts.
- Ajustements.
- Scanner.
- Filtres.

## 28. Achats frontend

- Commandes.
- Réceptions.
- Factures.
- Achats crédit.
- Dettes.
- Paiements.
- Retours.
- Approbations.

## 29. Clients frontend

- Fiche.
- Historique.
- Crédit.
- Limite.
- Solde.
- Échéances.
- Paiements.
- Documents.

## 30. Fournisseurs frontend

- Fiche.
- Historique achats.
- Conditions.
- Factures.
- Dettes.
- Paiements.
- Documents.

## 31. Finance frontend

- Vue trésorerie.
- Caisses.
- Banques.
- Mobile Money.
- Dépenses.
- Transferts.
- Budgets.
- Prévisions.
- Rapprochement.

## 32. Comptabilité frontend

- Plan comptable.
- Journaux.
- Écritures.
- Grand livre.
- Balance.
- Bilan.
- Résultat.
- Périodes.
- Clôture.
- Rapprochement.
- Pièces justificatives.

## 33. Consolidation frontend

- Sélection groupe.
- Période.
- Entreprises.
- Préparation.
- Anomalies.
- Intercompany.
- Ajustements.
- Éliminations.
- Validation.
- Publication.
- Rapports.

## 34. Administration frontend

- Entreprise.
- Établissements.
- Caisses.
- Utilisateurs.
- Rôles.
- Permissions.
- Taxes.
- Numérotation.
- Documents.
- Workflows.
- Automatisations.
- Intégrations.
- Sécurité.
- Configuration.

## 35. Recherche globale

- Résultats groupés par domaine.
- Raccourci clavier.
- Recherche récente.
- Filtres.
- Permissions respectées.
- Navigation directe.

## 36. Notifications

- Centre.
- Priorités.
- Lu/non lu.
- Actions.
- Lien vers ressource.
- Préférences.
- Notifications critiques persistantes jusqu'à traitement.

## 37. Documents

- Prévisualisation.
- Génération.
- Téléchargement selon permission.
- Impression.
- Partage lorsque autorisé.
- Historique.
- Statut génération.

## 38. Formulaires

- Validation immédiate.
- Autosave pour brouillons appropriés.
- Champs obligatoires.
- Aide contextuelle.
- Erreur par champ.
- Résumé avant action critique.

## 39. Tables

- Pagination.
- Tri.
- Filtre.
- Colonnes configurables.
- Export.
- Actions en masse contrôlées.
- Totaux.
- Sticky headers lorsque utile.
- Virtualisation pour gros volumes.

## 40. Performance frontend

- Code splitting.
- Lazy loading.
- Cache.
- Debounce recherche.
- Virtualisation.
- Préchargement POS.
- Optimisation images.
- Éviter re-render inutile.
- Budgets de bundle.

## 41. Offline storage

- IndexedDB ou mécanisme adapté.
- Données minimales nécessaires.
- Chiffrement/protection selon menace.
- Outbox.
- Inbox.
- Version de schéma local.
- Migration du store.
- Nettoyage contrôlé.

## 42. Synchronisation UI

- État : pending/syncing/synced/failed/conflict.
- Progression.
- Dernière synchronisation.
- Erreur explicite.
- Retry manuel.
- Résolution assistée lorsque nécessaire.

## 43. Périphériques POS

- Scanner code-barres.
- Imprimante ticket.
- Tiroir.
- Balance.
- Afficheur.
- Terminal paiement.
- Abstraction matériel côté application.
- Statut périphérique.

## 44. Responsive

- Desktop pour gestion.
- Tablette pour certains workflows.
- POS tactile.
- Grand écran pour dashboards.
- Tables adaptées sans devenir illisibles.

## 45. Accessibilité

- Navigation clavier.
- Focus visible.
- Contraste.
- Labels.
- ARIA lorsque nécessaire.
- Taille tactile.
- Messages d'erreur accessibles.
- Réduction animations.
- Lecteurs d'écran pour écrans de gestion.

## 46. Design system technique

- Tokens.
- Button.
- Input.
- Select.
- Combobox.
- Table.
- Modal.
- Drawer.
- Toast.
- Badge.
- Tabs.
- KPI.
- Charts.
- Date picker.
- Money input.
- Empty state.
- Loading state.
- Error state.

## 47. États d'interface

- Initial.
- Loading.
- Success.
- Empty.
- Error.
- Offline.
- Pending.
- Conflict.
- Permission denied.
- Read-only.

## 48. Gestion des erreurs

- Erreur réseau.
- Erreur métier.
- Permission.
- Session expirée.
- Conflit.
- Timeout.
- Paiement en attente.
- Erreur intégration.
- Message compréhensible.
- Détails techniques réservés aux diagnostics.

## 49. Sécurité frontend

- Pas de secrets dans le bundle.
- Pas de décision d'autorisation uniquement côté client.
- Protection XSS via bonnes pratiques framework.
- Gestion tokens/session conforme architecture backend.
- Nettoyage données sensibles.
- Auto-lock POS selon politique.

## 50. Tests frontend

- Unit components.
- Hooks.
- Services API.
- Permissions.
- Routing.
- POS.
- Paiement.
- Crédit.
- Offline.
- Synchronisation.
- Accessibilité.
- Responsive.
- End-to-end.

## 51. Tests POS critiques

- Scanner produit.
- Produit inconnu.
- Produit épuisé.
- Remise interdite.
- Paiement espèces.
- Paiement externe pending.
- Crédit dépassant limite.
- Vente offline.
- Synchronisation doublon.
- Retour.
- Clôture caisse.
- Perte imprimante.
- Perte réseau.

## 52. Tests UX par rôle

- Caissier novice.
- Caissier expérimenté.
- Manager.
- Stock.
- Comptable.
- Comptable en chef.
- Direction.
- Administrateur.

## 53. PWA / installation locale

- Installation navigateur lorsque adaptée.
- Cache des assets.
- Versioning assets.
- Mise à jour contrôlée.
- Indication nouvelle version.
- Fonctions offline limitées aux capacités validées.

## 54. Internationalisation

- Français.
- Préparation anglais et autres langues.
- Formats monétaires.
- Dates.
- Nombres.
- Fuseaux.
- Traductions externalisées.

## 55. Analytics produit

- Temps de vente.
- Fonctions utilisées.
- Erreurs UX.
- Taux d'abandon.
- Temps recherche produit.
- Temps paiement.
- Synchronisations.
- Performance.
- Données anonymisées/agrégées selon politique.

## 56. Critères de sortie

- Structure React définie.
- Features définies.
- Routing défini.
- Permissions définies.
- Client API défini.
- State management défini.
- POS défini.
- Offline défini.
- Finance définie.
- Comptabilité définie.
- Consolidation définie.
- Administration définie.
- Design system défini.
- Accessibilité définie.
- Performance définie.
- Tests définis.
- Périphériques définis.

## 57. Étape suivante

Passer à la **Phase 20 — Backlog de développement & découpage en sprints**. Cette phase
transformera les architectures PostgreSQL, .NET et React en lots de travail ordonnés, avec
priorités, dépendances, critères d'acceptation et séquence de construction de NEXORA V1.0.

---

**NEXORA V1.0 — PHASE 19 VALIDÉE → PHASE 20 : BACKLOG & PLAN DE DÉVELOPPEMENT**
