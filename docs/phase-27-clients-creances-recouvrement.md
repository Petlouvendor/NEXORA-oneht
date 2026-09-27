# NEXORA — PHASE 27 — CLIENTS, CRÉANCES & RECOUVREMENT

**Gestion du crédit client et du recouvrement NEXORA V1.0**
**Version 1.0**

---

## 1. Objectif

Construire le moteur de gestion client et de crédit permettant de gérer les clients comptant ou à crédit, les limites, échéances, créances, paiements partiels, retards, relances, litiges, recouvrements et analyses. Le système doit fonctionner pour une boutique, un restaurant, une pharmacie, un grossiste, une entreprise de services ou un groupe multi-entreprises.

## 2. Cycle client

```
CLIENT → CONDITIONS DE CRÉDIT → COMMANDE / VENTE → CRÉANCE → ÉCHÉANCE → RELANCE
      → PAIEMENT PARTIEL / TOTAL → RAPPROCHEMENT → SOLDE → COMPTABILITÉ
```

## 3. Fiche client

Identifiant · Code client · Nom/prénom ou raison sociale · Type particulier/entreprise · Téléphone · Email · Adresse · Contacts · Identifiants fiscaux selon besoin · Devise · Statut · Notes · Documents.

## 4. Clients particuliers et entreprises

Client individuel · Entreprise cliente · Compte professionnel · Contacts multiples · Adresses multiples · Conditions commerciales différentes · Facturation centralisée lorsque nécessaire.

## 5. Client multi-entreprises

- Même client utilisable par plusieurs entreprises du groupe.
- Conditions de crédit propres à chaque entreprise · Solde par entreprise · Solde groupe en lecture consolidée selon permission · Paiements affectés à l'entreprise concernée · Historique séparé et consolidé.

## 6. Compte client

```
CLIENT
 ├── CONDITIONS
 ├── CRÉDIT
 ├── FACTURES
 ├── PAIEMENTS
 ├── AVOIRS
 ├── RELANCES
 └── LITIGES
```

## 7. Conditions de paiement

Comptant · À réception · 7 jours · 15 jours · 30 jours · 60 jours · Personnalisé · Date fixe mensuelle · Acompte · Paiement échelonné.

## 8. Profil crédit

Limite crédit · Devise · Conditions · Durée maximale · Solde autorisé · Pourcentage acompte · Blocage automatique · Niveau de risque · Date de révision · Responsable.

## 9. Éligibilité crédit

```
CLIENT → STATUT → LIMITE → SOLDE ACTUEL → CRÉDIT DISPONIBLE → ÉCHÉANCE → AUTORISATION
```

Vérification avant vente · Vérification avant commande · Blocage dépassement · Autorisation exceptionnelle · Motif · Audit.

## 10. Crédit disponible

```
CRÉDIT DISPONIBLE = LIMITE − SOLDE DÛ − ENGAGEMENTS
```

La formule exacte doit tenir compte des règles de réservation et des montants en attente définis par l'entreprise.

## 11. Vente à crédit

Client obligatoire · Document source · Montant · Acompte · Montant crédité · Échéance · Conditions · Statut · Audit.

## 12. Créance

```
VENTE À CRÉDIT → CRÉANCE
 ├── montant original
 ├── payé
 ├── solde
 ├── échéance
 └── statut
```

- Une créance est liée à une transaction source.
- Les paiements ne modifient pas l'historique du montant original.
- Le solde est recalculable et réconciliable.

## 13. Statuts créance

À venir · Échue · Partiellement payée · Payée · En retard · En litige · Radiation approuvée · Annulée selon workflow.

## 14. Échéancier

Date facture · Date échéance · Montant · Payé · Solde · Jours avant échéance · Jours de retard · Priorité · Statut.

## 15. Aging des créances

```
COURANT · 0–30 JOURS · 31–60 JOURS · 61–90 JOURS · 91–120 JOURS · 120+ JOURS
```

Montant par tranche · Pourcentage du total · Par client · Par entreprise · Par commercial · Par secteur · Évolution.

## 16. Paiements clients

Espèces · Carte · Mobile Money · Virement · Chèque si retenu · Paiement multiple · Paiement partiel · Affectation à facture · Affectation à plusieurs factures · Acompte · Avoir.

## 17. Paiement partiel

```
CRÉANCE 100 000 → PAIEMENT 30 000 → SOLDE 70 000 → PAIEMENT 70 000 → SOLDE 0
```

Historique chaque paiement · Référence · Date · Moyen · Compte financier · Utilisateur · Rapprochement.

## 18. Affectation paiement

Facture précise · Plusieurs factures · Plus ancienne créance selon règle · Acompte non affecté · Affectation manuelle contrôlée · Réaffectation auditée.

## 19. Acomptes clients

Acompte avant vente · Solde acompte · Affectation à commande · Affectation à facture · Remboursement · Comptabilité.

## 20. Avoir client

Retour · Correction · Remise exceptionnelle · Avoir commercial · Montant · Solde · Utilisation partielle · Remboursement si autorisé · Expiration selon politique.

## 21. Relances

```
ÉCHÉANCE → AVANT ÉCHÉANCE → ÉCHUE → RELANCE 1 → RELANCE 2 → RELANCE 3 → RECOUVREMENT
```

Relance automatique · Relance manuelle · Email · SMS/notification selon intégration · Message configurable · Historique · Responsable · Prochaine action.

## 22. Scénarios de relance

7 jours avant · Jour échéance · 1–7 jours retard · 8–30 jours · 31–60 jours · 60+ jours · Scénarios personnalisables.

## 23. Recouvrement

Dossier recouvrement · Responsable · Montant · Client · Créances concernées · Historique contacts · Promesse paiement · Échéance promesse · Résultat · Escalade.

## 24. Promesse de paiement

Montant promis · Date · Créances concernées · Canal · Responsable · Statut tenue/non tenue · Nouvelle action · Audit.

## 25. Litiges

Client · Facture · Montant contesté · Motif · Documents · Responsable · Statut · Résolution · Impact solde · Audit.

## 26. Blocage client

Blocage crédit · Blocage vente · Blocage livraison · Blocage compte · Motif · Date début · Date fin · Autorisation exceptionnelle · Audit.

## 27. Déblocage

Utilisateur autorisé · Motif · Conditions · Durée · Audit · Réévaluation crédit.

## 28. Risque client

Retards · Montant dû · Concentration · Historique paiement · Litiges · Limite utilisée · Promesses non tenues · Score interne optionnel.

Un score ne doit pas être présenté comme une décision automatique irrévocable ; les règles de crédit restent explicables et contrôlables.

## 29. Analyse portefeuille

Total créances · Créances courantes · Créances échues · DSO selon données disponibles · Top débiteurs · Retards · Taux recouvrement · Aging · Évolution · Risque.

## 30. DSO

NEXORA peut calculer le Days Sales Outstanding lorsque les données de ventes et de créances sont suffisamment complètes. La méthode et la période doivent être explicites.

## 31. Recouvrement multi-entreprises

```
GROUPE
 ├── ENTREPRISE A → CRÉANCES A
 ├── ENTREPRISE B → CRÉANCES B
 └── ENTREPRISE C → CRÉANCES C
          ↓
   VUE GROUPE AUTORISÉE
```

- Comptable en chef : plusieurs entreprises selon scope.
- Vue consolidée sans mélange des soldes sources · Paiements affectés à la bonne entreprise · Audit.

## 32. Commercial / vendeur

Portefeuille clients · Ventes créditées · Créances · Relances · Objectifs · Commission optionnelle · Accès limité au scope.

## 33. Commissions

Base commission · Vente payée ou facturée selon politique · Taux · Période · Commercial · Annulation/reprise · Audit.

## 34. Documents

Facture · Reçu · Avoir · Relevé client · État de compte · Échéancier · Preuve paiement · Lettre de relance.

## 35. Relevé client

```
SOLDE INITIAL + FACTURES − PAIEMENTS − AVOIRS ± AJUSTEMENTS AUTORISÉS = SOLDE FINAL
```

## 36. Rapprochement

Paiement bancaire · Paiement caisse · Mobile Money · Référence transaction · Affectation facture · Écart · Suspense · Résolution.

## 37. Radiation créance

Motif · Montant · Créance source · Approbation · Compte comptable · Impact financier · Audit · Interdiction suppression historique.

## 38. Rééchelonnement

Nouvelle échéance · Plan de paiement · Nombre d'échéances · Montants · Acompte · Approbation · Historique ancien plan · Audit.

## 39. Plans de paiement

```
100 000 → 20 000 maintenant → 20 000 J+30 → 20 000 J+60 → 20 000 J+90 → 20 000 J+120
```

## 40. Paiement en devise

Devise paiement · Devise facture · Taux · Date taux · Gain/perte change · Solde résiduel · Règles comptables.

## 41. Intégration POS

```
POS → CRÉDIT CLIENT → CRÉANCE → COMPTE CLIENT → RELANCE / PAIEMENT
```

## 42. Intégration finance

Paiement client augmente caisse/banque/Mobile Money · Créance augmente à la vente crédit · Acompte · Remboursement · Avoir · Écart de change.

## 43. Intégration comptabilité

```
VENTE CRÉDIT → CLIENT / CRÉANCE → REVENUS → TAXES
PAIEMENT → BANQUE/CAISSE → CLIENT
AVOIR / RETOUR → CLIENT / REVENUS / TAXES SELON RÈGLES
```

## 44. API principales

```
GET/POST  /api/v1/customers
GET/PATCH /api/v1/customers/{id}
GET       /api/v1/customers/{id}/credit
PATCH     /api/v1/customers/{id}/credit-terms
GET       /api/v1/receivables
GET       /api/v1/receivables/{id}
POST      /api/v1/customer-payments
POST      /api/v1/customer-payments/{id}/allocate
POST      /api/v1/customer-credits
POST      /api/v1/customer-promises
POST      /api/v1/customer-disputes
POST      /api/v1/customer-blocks
GET       /api/v1/collections
GET       /api/v1/customer-statements
```

## 45. Événements métier

```
CustomerCreated · CreditLimitChanged · CreditSaleCreated · ReceivableCreated
CustomerPaymentCreated · CustomerPaymentAllocated · CustomerPaymentConfirmed
CustomerPromiseCreated · CustomerDisputeCreated · CustomerBlocked · ReceivableWrittenOff
```

## 46. Permissions

Lire clients · Créer client · Modifier client · Définir crédit · Vendre à crédit · Modifier limite · Enregistrer paiement · Affecter paiement · Rembourser · Créer relance · Lancer recouvrement · Rééchelonner · Bloquer · Débloquer · Radier créance · Voir portefeuille groupe.

## 47. Séparation des responsabilités

- Un caissier peut enregistrer un paiement sans pouvoir modifier la limite.
- Un commercial peut créer une demande de crédit sans l'approuver.
- Un comptable peut rapprocher un paiement selon permission.
- Une radiation importante peut exiger une approbation supérieure.

## 48. Audit

Création client · Modification crédit · Modification limite · Vente crédit · Paiement · Affectation · Réaffectation · Remboursement · Relance · Promesse · Blocage · Déblocage · Rééchelonnement · Radiation · Litige.

## 49. Anti-fraude

Paiement annulé/réaffecté fréquemment · Modification limite juste avant grosse vente · Crédit dépassé autorisé manuellement · Remboursements inhabituels · Radiations répétées · Paiements sans référence · Affectations inhabituelles · Comptes clients dupliqués.

## 50. Doublons clients

Téléphone · Email · Identifiant entreprise · Nom + adresse · Code client · Alerte avant création · Fusion contrôlée · Audit fusion.

## 51. Fusion clients

Détection · Prévisualisation impact · Choix compte maître · Déplacement historique contrôlé · Conservation références · Interdiction si contraintes bloquantes · Audit.

## 52. Notifications

Échéance proche · Retard · Limite presque atteinte · Crédit bloqué · Promesse échue · Litige mis à jour · Paiement reçu · Anomalie.

## 53. Tests

Client comptant · Client crédit · Limite · Dépassement · Vente crédit · Acompte · Paiement partiel · Paiement total · Paiement multi-factures · Avoir · Retour · Relance · Promesse · Rééchelonnement · Litige · Blocage · Déblocage · Radiation · Devise · Multi-entreprises · Isolation scope.

## 54. Scénario complet

```
LIMITE CLIENT = 500 000 → VENTE = 300 000 À CRÉDIT → CRÉANCE = 300 000 → PAIEMENT = 100 000
→ SOLDE = 200 000 → RETARD → RELANCE → PROMESSE = 100 000 → PAIEMENT = 100 000
→ SOLDE = 100 000 → PAIEMENT FINAL → SOLDE = 0
```

## 55. Critères de sortie

Fiche client · Comptes clients · Conditions crédit · Limites · Vente crédit · Créances · Échéancier · Aging · Paiements · Affectations · Acomptes · Avoirs · Relances · Recouvrement · Promesses · Litiges · Blocage/déblocage · Risque · Portefeuille · Rééchelonnement · Radiation contrôlée · Multi-devises · Multi-entreprises · POS · Finance · Comptabilité · Permissions · Audit · Anti-fraude · API · Tests.

## 56. Étape suivante

Passer à la **Phase 28 — Finance, trésorerie, caisses, banques, Mobile Money, dépenses, transferts, budgets et rapprochements**. Cette phase unifiera les flux monétaires provenant des ventes, achats, créances et dettes.

---

**NEXORA V1.0 — PHASE 27 VALIDÉE → PHASE 28 : FINANCE & TRÉSORERIE**
