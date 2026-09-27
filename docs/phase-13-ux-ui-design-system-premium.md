# NEXORA — PHASE 13 — UX/UI, DESIGN SYSTEM & EXPÉRIENCE PREMIUM

**Identité visuelle, ergonomie et expérience utilisateur de la plateforme NEXORA**
Version 1.0

## 1. Objectif

Créer une expérience NEXORA reconnaissable, moderne, premium et efficace, capable de servir
aussi bien une petite entreprise qu'un grand groupe multi-entreprises. L'interface doit réduire
le temps d'apprentissage, accélérer les opérations de caisse et rendre les informations
financières compréhensibles.

## 2. Principes de design

- Clarté avant décoration.
- Rapidité avant complexité.
- Hiérarchie visuelle forte.
- Même logique dans toute l'application.
- Interface adaptée au rôle.
- Densité configurable : opérationnelle ou analytique.
- Actions critiques toujours explicites.
- Les données financières importantes doivent être lisibles sans surcharge.
- Le design doit fonctionner sur écran tactile, ordinateur portable et grand écran.

## 3. Positionnement visuel

NEXORA doit se positionner comme un logiciel de gestion professionnel de nouvelle génération,
et non comme une simple caisse. L'identité doit évoquer : confiance, précision, intelligence,
performance et maîtrise.

- Premium SaaS.
- Technologie moderne.
- Élégance professionnelle.
- Identité forte.
- Interface sobre mais distinctive.
- Animations discrètes et utiles.

## 4. Identité visuelle

- Nom : NEXORA.
- Logo : symbole + logotype utilisables séparément.
- Système d'icônes propriétaire.
- Palette cohérente.
- Typographie moderne et très lisible.
- Grille et espacements constants.
- Illustrations limitées aux contextes utiles.
- Style reconnaissable même sans logo.

## 5. Palette de couleurs

La palette définitive devra être validée avec les maquettes, mais elle doit distinguer
clairement identité, action, information, succès, avertissement et erreur.

- Couleur de marque NEXORA : couleur signature forte et reconnaissable.
- Surface principale : neutre et reposante.
- Surface secondaire : différenciation légère.
- Texte principal : contraste élevé.
- Texte secondaire : contraste suffisant.
- Succès : état positif.
- Avertissement : attention.
- Erreur : danger.
- Information : état informatif.

La couleur ne doit jamais être l'unique moyen d'indiquer un état : icône, texte ou forme
doivent compléter l'information.

## 6. Mode clair et sombre

- Mode clair premium.
- Mode sombre premium.
- Préférence utilisateur.
- Détection du système.
- Contraste WCAG visé.
- Conserver la hiérarchie visuelle dans les deux modes.
- Éviter les noirs absolus lorsque cela nuit au contraste ou au confort.

## 7. Typographie

- Une famille principale.
- Poids distincts pour titre, section, corps, chiffres et annotations.
- Chiffres financiers particulièrement lisibles.
- Tabular numerals pour tableaux et KPI lorsque possible.
- Taille minimale adaptée au POS et aux utilisateurs professionnels.
- Support des accents et langues utilisées.

## 8. Grille et espacements

- Système d'espacement cohérent.
- Cartes alignées.
- Marges respirantes dans les dashboards.
- Densité plus élevée dans les écrans opérationnels.
- Zones tactiles suffisamment grandes.
- Alignement strict des montants et colonnes.

## 9. Navigation globale

```
SIDEBAR
├── Accueil
├── Ventes / POS
├── Produits
├── Stocks
├── Achats
├── Clients
├── Fournisseurs
├── Finance
├── Comptabilité
├── Rapports
├── Entreprises / Groupe
├── Intégrations
└── Administration
```

- Menu adapté au rôle.
- Éléments non autorisés masqués ou désactivés selon contexte.
- Recherche globale.
- Favoris.
- Historique récent.
- Contexte entreprise toujours visible.

## 10. Contexte entreprise

Le contexte de travail doit être impossible à confondre.

- Nom de l'entreprise.
- Logo éventuel.
- Établissement.
- Magasin/entrepôt.
- Caisse si applicable.
- Statut online/offline.
- Sélecteur d'entreprise.
- Indication vue groupe lorsqu'activée.

```
NEXORA / GROUPE → ENTREPRISE → ÉTABLISSEMENT → CAISSE
```

## 11. Dashboard premium

- KPI prioritaires en haut.
- Tendance visible.
- Comparaison période.
- Alertes.
- Actions rapides.
- Graphiques simples.
- Drill-down.
- Personnalisation par rôle.
- Possibilité de réorganiser certains widgets.
- Mode résumé et mode détaillé.

## 12. Command Center

- CA.
- Marge.
- Bénéfice.
- Trésorerie.
- Créances.
- Dettes.
- Stock.
- Objectifs.
- Alertes critiques.
- Performance des entreprises.

Le Command Center doit répondre en quelques secondes à : « Comment va mon entreprise ? »

## 13. POS premium

Le POS doit privilégier la vitesse, la lisibilité et le nombre minimal d'étapes.

```
┌──────────── PRODUITS / RECHERCHE ────────────┐
│                                               │
│                    PANIER                     │
│                                               │
├───────────────────────────────┬───────────────┤
│ Produits / catégories         │ TOTAL         │
│ raccourcis                    │ PAIEMENT      │
└───────────────────────────────┴───────────────┘
```

- Ajout en un geste.
- Scanner immédiat.
- Panier toujours visible.
- Total très visible.
- Paiement accessible sans navigation inutile.
- Raccourcis clavier.
- Support tactile.
- Mode sombre particulièrement adapté aux caisses.
- Indicateur offline visible mais discret.

## 14. Paiement POS

- Écran de paiement simplifié.
- Montant dû énorme et lisible.
- Moyens de paiement sous forme de boutons.
- Paiement multiple clair.
- Rendu monnaie évident.
- Crédit clairement identifié.
- Confirmation finale avant transaction irréversible.

## 15. Finance

- Vue trésorerie.
- Encaissements.
- Décaissements.
- Créances.
- Dettes.
- Banques.
- Caisses.
- Budgets.
- Prévisions.
- Alertes.

Les écrans financiers doivent privilégier la compréhension avant la densité.

## 16. Comptabilité

- Journal.
- Grand livre.
- Balance.
- Bilan.
- Résultat.
- Rapprochements.
- Clôture.
- Écritures.
- Pièces justificatives.
- Audit.
- Filtres puissants.
- Recherche.
- Drill-down.
- Lien vers transaction source.
- Statut clair : brouillon, validé, clôturé.

## 17. Interface comptable en chef

- Sélecteur multi-entreprises.
- Vue portefeuille.
- Entreprises à clôturer.
- Anomalies.
- Rapprochements.
- Intercompany.
- Consolidation.
- Tâches.
- Alertes.

## 18. Tables de données

- Colonnes configurables.
- Tri.
- Filtre.
- Recherche.
- Pagination.
- Sélection multiple.
- Actions en masse contrôlées.
- Colonnes numériques alignées.
- Totaux.
- Export selon permissions.
- État vide explicatif.

## 19. Formulaires

- Sections logiques.
- Champs obligatoires clairement indiqués.
- Validation immédiate.
- Messages d'erreur précis.
- Sauvegarde brouillon lorsque pertinente.
- Prévisualisation avant action sensible.
- Auto-complétion.
- Recherche d'entités.

## 20. Actions sensibles

- Couleur/état distinct.
- Confirmation explicite.
- Résumé de l'opération.
- Motif si requis.
- Information sur impact financier/stock.
- Possibilité d'annuler uniquement si métier le permet.
- Audit automatique.

## 21. Notifications

- Centre de notifications.
- Priorité.
- Critique.
- Avertissement.
- Information.
- Lu/non lu.
- Filtre.
- Lien direct vers l'action.
- Éviter les notifications inutiles.
- Regrouper les événements répétitifs.

## 22. Alertes intelligentes

- Rupture stock.
- Créance échue.
- Dette échue.
- Écart caisse.
- Anomalie financière.
- Expiration.
- Erreur synchronisation.
- Intégration indisponible.
- Anomalie anti-fraude.

## 23. Recherche globale

- Produits.
- Clients.
- Fournisseurs.
- Ventes.
- Achats.
- Factures.
- Paiements.
- Écritures.
- Entreprises.
- Utilisateurs selon permission.
- Numéros de documents.

La recherche doit respecter strictement les permissions et le périmètre.

## 24. Expérience multi-entreprises

- Changement d'entreprise rapide.
- Contexte persistant.
- Indicateurs de secteur.
- Vue groupe.
- Comparaison.
- Retour facile à l'entreprise précédente.
- Aucun mélange visuel de données.

## 25. Expérience offline

- Badge clair : En ligne / Hors ligne / Synchronisation.
- Nombre de transactions en attente.
- Aucune fausse confirmation.
- Centre de synchronisation accessible.
- Messages simples pour l'utilisateur.

## 26. Responsive

- Desktop prioritaire.
- Laptop.
- Tablette pour certains usages.
- POS tactile.
- Grand écran pour dashboards.
- Pas de réduction illisible des tableaux.
- Adaptation des menus et panneaux.

## 27. Accessibilité

- Contraste suffisant.
- Navigation clavier.
- Focus visible.
- Labels explicites.
- Tailles tactiles suffisantes.
- Messages d'erreur accessibles.
- Ne pas dépendre uniquement de la couleur.
- Support zoom.
- Ordre logique de tabulation.

## 28. Micro-interactions

- Feedback instantané.
- Animation courte après validation.
- Chargement skeleton lorsque pertinent.
- Transition discrète.
- Confirmation visuelle.
- Aucune animation qui ralentit la caisse.
- Respect de `prefers-reduced-motion`.

## 29. Design system

Tous les écrans doivent utiliser un système de composants partagé.

- Buttons.
- Inputs.
- Selects.
- Autocomplete.
- Tables.
- Cards.
- Badges.
- Tabs.
- Modals.
- Drawers.
- Dropdowns.
- Toasts.
- Tooltips.
- Charts.
- Date pickers.
- Currency fields.
- KPI cards.
- Empty states.
- Loading states.
- Error states.

## 30. États des composants

- Default.
- Hover.
- Focus.
- Active.
- Disabled.
- Loading.
- Success.
- Warning.
- Error.
- Offline.
- Read-only.

## 31. Iconographie

- Icônes cohérentes.
- Style unique.
- Taille standardisée.
- Libellé lorsque ambigu.
- Icônes financières compréhensibles.
- Icônes sectorielles distinctes.
- Pas d'icône décorative inutile.

## 32. Graphiques

- Peu de types mais bien utilisés.
- Courbes pour tendances.
- Barres pour comparaisons.
- Aires avec modération.
- Camemberts limités.
- KPI avec variation.
- Légendes claires.
- Valeurs exactes accessibles.
- Filtres synchronisés.

## 33. Visualisation financière

- Montants alignés.
- Devise visible.
- Positif/négatif explicite.
- Réel vs prévisionnel distinct.
- Variation avec valeur et pourcentage.
- Période toujours affichée.
- Possibilité de voir la source.

## 34. Documents et impression

- Facture professionnelle.
- Reçu POS.
- Bon de livraison.
- Bon de commande.
- Rapport.
- États financiers.
- Mise en page cohérente.
- Logo entreprise configurable.
- Numérotation visible.
- QR code lorsque prévu.

## 35. Personnalisation entreprise

- Logo.
- Nom affiché.
- Couleur d'accent dans limites du système.
- Format reçu.
- Documents.
- Devise.
- Langue.
- Terminologie.
- Widgets.
- Règles d'affichage.

La personnalisation ne doit pas dégrader la lisibilité ou les exigences d'accessibilité.

## 36. Personnalisation utilisateur

- Mode clair/sombre.
- Langue.
- Densité.
- Widgets.
- Colonnes.
- Favoris.
- Raccourcis.
- Préférences de notifications.

## 37. Expérience par rôle

- Caissier : vitesse et caisse.
- Manager : performance et alertes.
- Stock : mouvements et inventaire.
- Comptable : contrôle et écritures.
- Comptable en chef : portefeuille et consolidation.
- Direction : vision et décisions.
- Administrateur : configuration et sécurité.

## 38. Onboarding

```
CRÉER ENTREPRISE
→ CHOISIR ACTIVITÉ
→ CONFIGURER
→ IMPORTER / AJOUTER PRODUITS
→ CRÉER UTILISATEURS
→ CONFIGURER CAISSE
→ TESTER
→ PREMIÈRE VENTE
```

- Assistant de configuration.
- Progression visible.
- Valeurs par défaut raisonnables.
- Conseils contextuels.
- Checklist de démarrage.
- Possibilité de terminer plus tard.

## 39. Aide contextuelle

- Tooltips.
- Guides courts.
- Aide sur champ.
- Glossaire comptable.
- Explications KPI.
- Raccourcis clavier.
- Centre d'aide.

## 40. Internationalisation

- Français en priorité.
- Architecture prête pour d'autres langues.
- Formats date configurables.
- Formats monétaires.
- Séparateurs numériques.
- Fuseaux horaires.
- Textes traduisibles.
- Langue par utilisateur.

## 41. Performance perçue

- Interface réactive.
- Feedback immédiat.
- Chargements progressifs.
- Cache des données utiles.
- Préchargement du POS.
- Éviter les écrans blancs.
- Indiquer clairement les traitements longs.

## 42. Design et sécurité

- Ne jamais afficher de secrets.
- Masquer données sensibles selon rôle.
- Confirmation avant actions critiques.
- Déconnexion visible.
- Indicateur de session.
- Alerte si contexte entreprise changé.
- Protection contre click accidentel sur actions irréversibles.

## 43. Tests UX

- Test caisse avec utilisateur novice.
- Test caisse avec utilisateur expérimenté.
- Test comptable.
- Test comptable en chef multi-entreprises.
- Test direction.
- Test mobile/tablette lorsque concerné.
- Test clavier.
- Test tactile.
- Test mode sombre.
- Test accessibilité.
- Test offline.
- Test avec gros volume de données.

## 44. Critères de sortie

- Identité visuelle définie.
- Palette définie.
- Typographie définie.
- Grille définie.
- Navigation définie.
- POS défini.
- Dashboards définis.
- Finance définie.
- Comptabilité définie.
- Multi-entreprises défini.
- Design system défini.
- États des composants définis.
- Responsive défini.
- Accessibilité définie.
- Onboarding défini.
- Internationalisation définie.
- Tests UX définis.

## 45. Étape suivante

Passer à la **Phase 14 — Administration, Paramétrage & Automatisation**. Elle définira le centre
d'administration permettant à chaque entreprise et au groupe d'adapter NEXORA à son
fonctionnement sans intervention d'un développeur.

---

**NEXORA — Phase 13 validée → Phase 14 : Administration, Paramétrage & Automatisation**
