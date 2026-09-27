# NEXORA — PHASE 26 — ACHATS, FOURNISSEURS, CRÉDIT & DETTES

**Moteur d'approvisionnement et de dettes fournisseurs NEXORA V1.0**
**Version 1.0**

---

## 1. Objectif

Construire le cycle d'achat universel de NEXORA, depuis la demande et la commande fournisseur jusqu'à la réception, la facture, l'achat à crédit, la dette, le paiement et la comptabilisation. Le module doit convenir aux commerces, pharmacies, restaurants, grossistes, entreprises de services et structures multi-entreprises.

## 2. Cycle complet

```
BESOIN → DEMANDE D'ACHAT → APPROBATION → COMMANDE FOURNISSEUR → RÉCEPTION → CONTRÔLE
      → FACTURE → PAIEMENT OU CRÉDIT → DETTE FOURNISSEUR → COMPTABILITÉ
```

## 3. Principe

- Une commande n'est pas une dette.
- Une réception n'est pas automatiquement une facture.
- Une facture à crédit crée une dette selon les règles de l'entreprise.
- Un paiement réduit la dette.
- Les documents et mouvements doivent rester traçables.
- Les montants historiques sont conservés.

## 4. Fournisseur

Identité · Code · Nom légal · Nom commercial · Contacts · Adresse · Pays · Devise · Conditions de paiement · Limite éventuelle · Délai moyen · Statut · Compte fournisseur · Documents · Notes.

## 5. Fournisseurs multi-entreprises

- Un fournisseur peut être utilisé par plusieurs entreprises.
- Conditions différentes par entreprise · Prix différents · Devise différente si autorisée · Compte comptable différent selon mapping · Historique séparé par entreprise.

## 6. Catalogue fournisseur

Produit interne · Référence fournisseur · Description fournisseur · Coût · Unité achat · MOQ · Conditionnement · Délai · Fournisseur principal · Fournisseurs alternatifs.

## 7. Demande d'achat

Créateur · Entreprise · Établissement · Articles · Quantité · Motif · Budget · Priorité · Date souhaitée · Statut · Approbation.

## 8. Workflow demande

```
BROUILLON → SOUMISE → À APPROUVER → APPROUVÉE / REFUSÉE → COMMANDE
```

Seuils financiers · Approbateur · Délégation · Motif refus · Audit.

## 9. Commande fournisseur

Fournisseur · Entreprise · Établissement · Date · Lignes · Quantités · Prix négociés · Taxes · Remises · Devise · Conditions · Date livraison · Référence fournisseur · Statut.

## 10. Statuts commande

Brouillon · Soumise · Approuvée · Envoyée · Partiellement reçue · Reçue · Annulée · Clôturée.

## 11. Commande partielle

Quantité commandée · Quantité reçue · Quantité restante · Réceptions multiples · Backorder · Annulation reliquat · Écart.

## 12. Réception

```
COMMANDE → RÉCEPTION → CONTRÔLE QUANTITÉ → CONTRÔLE QUALITÉ → STOCK
```

Réception complète/partielle · Quantité reçue · Quantité rejetée · Lot · Expiration · Emplacement · Écart · Bon de réception.

## 13. Réception sans commande

Autorisation configurable · Motif · Fournisseur · Produit · Quantité · Prix · Approbation selon politique · Audit.

## 14. Réception pharmacie

Lot obligatoire lorsque requis · Expiration · Numéro lot fournisseur · Quantité par lot · Quarantaine · Blocage produit/lot · Traçabilité.

## 15. Réception restaurant

Ingrédients · Unité · Poids · Qualité · Température si l'entreprise la renseigne · Pertes · Lot/expiration lorsque pertinent · Stock cuisine.

## 16. Réception supermarché

Code-barres · Cartons · Unités · Poids · Lots · Expiration · Écart livraison · Stock magasin/réserve.

## 17. Réception grossiste

Palettes · Cartons · Unités · Lots · Entrepôt · Réception partielle · Écarts.

## 18. Contrôle réception

Commande vs reçu · Bon fournisseur · Quantité · Prix · Taxe · Lot · Expiration · Qualité · Anomalies · Validation.

## 19. Facture fournisseur

Numéro facture · Fournisseur · Date facture · Date échéance · Lignes · Quantités · Prix · Taxes · Remises · Frais · Devise · Total · Référence commande · Référence réception.

## 20. Rapprochement 3-way

```
COMMANDE ↕ RÉCEPTION ↕ FACTURE → MATCH / ÉCART
```

Tolérance quantité · Tolérance prix · Écart taxe · Écart total · Blocage paiement · Validation manuelle · Audit.

## 21. Facture sans commande

Autorisation · Validation · Compte analytique si nécessaire · Pièce justificative · Workflow · Audit.

## 22. Achats comptant

```
FACTURE → PAIEMENT IMMÉDIAT → FOURNISSEUR RÉGLÉ
```

## 23. Achats à crédit

```
FACTURE → DETTE FOURNISSEUR → ÉCHÉANCE → PAIEMENT PARTIEL / TOTAL → SOLDE
```

Conditions crédit · Échéance · Montant dû · Paiements · Solde · Retard · Blocage achats éventuel · Relances.

## 24. Dette fournisseur

Fournisseur · Facture source · Montant original · Montant payé · Solde · Devise · Date facture · Échéance · Jours retard · Statut.

## 25. Statuts dette

Ouverte · Partiellement payée · Payée · En retard · Litige · Annulée selon workflow · Crédit fournisseur.

## 26. Paiement fournisseur

Espèces · Banque · Mobile Money lorsque pertinent · Virement · Paiement multiple · Paiement partiel · Référence · Date valeur · Compte financier · Pièce justificative.

## 27. Paiement multiple

```
DETTE 100 000
├── BANQUE 60 000
└── MOBILE 40 000
= 100 000
```

Somme contrôlée · Statut de chaque moyen · Idempotence · Rapprochement.

## 28. Acompte fournisseur

Demande · Paiement avant réception · Solde d'acompte · Imputation à facture · Rapprochement · Comptabilité.

## 29. Crédit fournisseur

Note de crédit · Retour fournisseur · Montant crédit · Application à dette · Remboursement éventuel · Solde fournisseur · Audit.

## 30. Retours fournisseurs

```
FACTURE / RÉCEPTION → RETOUR → SORTIE STOCK → AVOIR / CRÉDIT → DETTE AJUSTÉE
```

Retour total/partiel · Motif · Produit · Lot · Quantité · Document · Impact stock · Impact financier.

## 31. Frais d'achat

Transport · Douane · Assurance · Manutention · Autres frais · Répartition sur articles selon méthode · Compte financier/comptable · Audit.

## 32. Coût d'acquisition

Prix achat · Remise · Frais directement attribuables selon règles · Taxes récupérables/non récupérables selon configuration · Coût stock final · Méthode documentée.

## 33. Taxes achats

Taxe récupérable · Taxe non récupérable · Exonération · Taux · Compte taxe · Date · Juridiction.

## 34. Devise

Devise facture · Devise entreprise · Taux · Date taux · Montant original · Montant fonctionnel · Écart de change selon règles.

## 35. Échéances

Date échéance · Conditions 7/15/30/60 jours · Calendrier · Retard · Alerte · Priorité paiement · Prévision trésorerie.

## 36. Calendrier fournisseurs

À payer aujourd'hui · 7 jours · 30 jours · En retard · Montant par fournisseur · Montant par entreprise · Devise · Priorité.

## 37. Budget achat

Budget par entreprise · Catégorie · Département · Période · Engagé · Réalisé · Disponible · Dépassement · Approbation.

## 38. Engagement

```
COMMANDE APPROUVÉE → BUDGET ENGAGÉ → FACTURE → BUDGET RÉALISÉ
```

## 39. Workflow paiement

```
FACTURE → RAPPROCHEMENT → APPROBATION → PROGRAMMATION → PAIEMENT → RAPPROCHEMENT BANQUE
```

## 40. Blocage paiement

Écart commande/réception/facture · Facture en litige · Fournisseur bloqué · Budget insuffisant · Permission manquante · Données bancaires modifiées récemment · Contrôle anti-fraude.

## 41. Fournisseur en litige

Motif · Montant contesté · Lignes · Responsable · Statut · Résolution · Impact dette · Audit.

## 42. Performance fournisseur

Délai moyen · Taux livraison complète · Écarts · Prix moyen · Qualité · Retours · Litiges · Dépendance fournisseur · Historique.

## 43. Comparaison fournisseurs

Prix · Délai · MOQ · Qualité · Historique · Conditions crédit · Performance · Disponibilité.

## 44. Réapprovisionnement

Le moteur de stock peut proposer une commande fournisseur à partir des seuils, ventes, commandes clients, stock disponible, délai fournisseur et historique.

```
STOCK → BESOIN → FOURNISSEUR → SUGGESTION COMMANDE
```

## 45. Achats multi-entreprises

Commande dans une entreprise · Fournisseur partagé · Conditions propres · Paiement propre · Dette propre · Rapport groupe · Consolidation comptable selon règles.

## 46. API principales

```
GET/POST   /api/v1/suppliers
GET/PATCH  /api/v1/suppliers/{id}
GET/POST   /api/v1/purchase-requests
GET/POST   /api/v1/purchase-orders
POST       /api/v1/purchase-orders/{id}/approve
POST       /api/v1/purchase-orders/{id}/send
POST       /api/v1/purchase-orders/{id}/receive
GET/POST   /api/v1/purchase-invoices
POST       /api/v1/purchase-invoices/{id}/match
POST       /api/v1/purchase-invoices/{id}/approve
GET        /api/v1/payables
POST       /api/v1/payables/{id}/payments
POST       /api/v1/supplier-returns
GET        /api/v1/supplier-statements
```

## 47. Événements métier

```
PurchaseRequested · PurchaseApproved · PurchaseOrderCreated · PurchaseReceived
PurchaseInvoiceCreated · PurchaseInvoiceMatched · PayableCreated
SupplierPaymentCreated · SupplierPaymentConfirmed · SupplierReturnCreated · SupplierCreditCreated
```

## 48. Intégration stock

Réception augmente stock · Retour fournisseur diminue stock · Acompte n'affecte pas stock · Facture seule n'affecte pas stock si réception déjà enregistrée · Corrections suivent un mouvement contrôlé.

## 49. Intégration finance

Facture à crédit crée dette · Paiement réduit dette · Acompte crée un actif/solde fournisseur selon mapping · Frais d'achat impactent coût/finance selon règles · Écarts de change selon configuration.

## 50. Intégration comptabilité

```
ACHAT → STOCK / CHARGE → TAXE → FOURNISSEUR → PAIEMENT → BANQUE / CAISSE
```

Écritures équilibrées · Mapping comptes · Période ouverte · Journal achats · Journal banque/caisse · Audit source.

## 51. Permissions

Créer fournisseur · Créer demande · Approuver demande · Créer commande · Approuver commande · Réceptionner · Modifier prix · Créer facture · Approuver facture · Programmer paiement · Payer fournisseur · Retourner · Voir dettes · Modifier conditions crédit.

## 52. Audit

Création fournisseur · Modification coordonnées bancaires · Commande · Approbation · Réception · Écart · Facture · Validation · Paiement · Retour · Avoir · Modification échéance · Annulation.

## 53. Anti-fraude

Fournisseur nouvellement créé + paiement rapide · Changement compte bancaire + paiement · Facture dupliquée · Numéro facture déjà utilisé · Montant inhabituel · Écart commande/facture · Paiements fractionnés · Dépassement seuil · Retour inhabituel.

## 54. Contrôles doublons

Fournisseur + numéro facture · Montant/date · Référence fournisseur · Hash document si disponible · Alerte doublon · Blocage selon politique.

## 55. Tests

Commande simple · Commande partielle · Réception · Réception sans commande · Lot/expiration · Facture · 3-way match · Écart prix · Écart quantité · Achat comptant · Achat crédit · Paiement partiel · Paiement total · Acompte · Avoir · Retour · Frais · Devise · Budget · Multi-entreprises · Anti-doublon.

## 56. Scénario complet

```
FOURNISSEUR → COMMANDE 100 UNITÉS À 500 → RÉCEPTION 100 → FACTURE 50 000 → CRÉDIT 30 JOURS
           → DETTE 50 000 → PAIEMENT 20 000 → SOLDE 30 000 → PAIEMENT 30 000 → DETTE 0
           → COMPTABILITÉ + AUDIT
```

## 57. Critères de sortie

Fournisseurs · Demandes d'achat · Commandes · Réceptions · Lots · Factures · 3-way match · Achats comptant · Achats à crédit · Dettes · Échéances · Paiements partiels · Acomptes · Avoirs · Retours · Frais · Taxes · Devises · Budgets · Performance fournisseur · Multi-entreprises · Stock · Finance · Comptabilité · Permissions · Audit · Anti-fraude · API · Tests.

## 58. Étape suivante

Passer à la **Phase 27 — Clients, comptes clients, crédit, créances, paiements, échéances et recouvrement**. Elle complétera le cycle du crédit côté ventes et préparera la gestion complète du BFR, de la trésorerie et de la comptabilité.

---

**NEXORA V1.0 — PHASE 26 VALIDÉE → PHASE 27 : CLIENTS, CRÉANCES & RECOUVREMENT**
