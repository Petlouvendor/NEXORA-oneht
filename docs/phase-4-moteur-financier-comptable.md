# NEXORA — PHASE 4 — MOTEUR FINANCIER & COMPTABLE

**Spécification fonctionnelle du cœur financier de NEXORA**
**Version 1.0**

> Document de référence. Implémentation : module `Finance` / `Accounting` du backend
> (voir [`phase-4-implementation.md`](phase-4-implementation.md)).

---

## 1. Objectif

Définir le moteur qui transforme les opérations NEXORA en conséquences financières et comptables cohérentes. Le moteur doit fonctionner pour une petite entreprise comme pour un groupe multi-entreprises et rester indépendant de l'interface POS.

### Principe directeur

Une transaction validée est une source de vérité. Les soldes, états financiers et rapports sont dérivés des transactions financières et écritures validées, avec des mécanismes de rapprochement.

## 2. Périmètre

Caisse et trésorerie · banques et comptes financiers · revenus et dépenses · créances clients · dettes fournisseurs · paiements et allocations · devises et taux de change · plan comptable · journaux et écritures · périodes et clôtures · comptabilité analytique · intercompany · consolidation · rapprochements · prévisions de trésorerie · audit financier.

## 3. Architecture du moteur

```
OPÉRATION MÉTIER
      ↓
ÉVÉNEMENT FINANCIER
      ↓
RÈGLE FINANCIÈRE / COMPTABLE
      ↓
TRANSACTION ATOMIQUE
      ├── Créance / Dette
      ├── Caisse / Banque
      ├── Stock (si concerné)
      └── Écriture comptable
      ↓
RAPPORTS / TRÉSORERIE / CONSOLIDATION
```

## 4. Principes comptables fondamentaux

- Comptabilité en partie double.
- Chaque écriture validée doit avoir au moins une ligne débit et une ligne crédit.
- Total débit = total crédit.
- Chaque écriture possède une date, un journal, une période et une source.
- Les comptes doivent être correctement typés.
- Les périodes clôturées sont protégées.
- Une écriture validée n'est pas modifiée directement.
- Les corrections utilisent une procédure d'extourne/correction traçable.
- Les règles comptables sont configurables par entreprise selon son référentiel et le cadre applicable.

## 5. Plan comptable

Créer/importer un plan comptable · numéro et libellé · type de compte · compte parent · compte collectif ou auxiliaire · compte postable ou non postable · statut actif/inactif · version du plan comptable · **mapping des comptes vers les événements métier**.

Familles : Actifs · Passifs · Capitaux propres · Produits · Charges.

Le plan comptable réel doit être paramétrable et adapté au cadre comptable applicable ; NEXORA ne doit pas imposer un plan unique.

## 6. Journaux

Ventes · achats · caisse · banque · opérations diverses · salaires (si activé) · intercompany · autres configurables. Chaque entreprise active/désactive ses journaux.

## 7. Écriture comptable

```
JOURNAL ENTRY
├── Entreprise · Journal · Période · Date · Numéro
├── Source métier · Description
└── Lignes
     ├── Compte · Débit · Crédit · Devise · Analytique
```

- Équilibrée avant validation.
- Numéro unique dans son périmètre.
- Source métier conservée.
- Statut : brouillon → validée → éventuellement extournée/corrigée.

## 8. Événements financiers

Vente · achat · paiement client · paiement fournisseur · dépense · encaissement · décaissement · transfert financier · ajustement · frais bancaires · différence de caisse · différence de change · intercompany.

Chaque événement est associé à une règle de génération financière et, lorsque nécessaire, comptable.

## 9. Vente comptant

```
VENTE 100 000
→ CA +100 000
→ Caisse/Banque +100 000
→ Création des écritures
→ Stock et coût des marchandises selon règles
```

Le paiement est lié à la vente. Le moyen de paiement détermine le compte financier concerné. La marge utilise le coût historique conservé avec les lignes de vente.

## 10. Vente à crédit

```
VENTE 100 000 → CA +100 000 → CRÉANCE CLIENT +100 000 → Écriture comptable
PAIEMENT 40 000 → Caisse/Banque +40 000 → CRÉANCE -40 000
SOLDE = 60 000
```

Vérifier la limite de crédit · échéance · paiements partiels · paiements multiples · alertes de retard · historique complet.

## 11. Achat comptant

```
ACHAT 500 000 → Stock/charge approprié → Caisse/Banque -500 000 → Écriture comptable
```

## 12. Achat à crédit

```
ACHAT 500 000 → Stock/charge → DETTE FOURNISSEUR +500 000 → Écriture comptable
PAIEMENT 200 000 → Caisse/Banque -200 000 → DETTE -200 000
SOLDE = 300 000
```

Échéance · conditions · paiements partiels · retards · relevé fournisseur · alertes · historique.

## 13. Créances clients

Montant initial · payé · solde · devise · émission · échéance · statut · client · source · historique des allocations.

```
SOLDE CRÉANCE = MONTANT ORIGINAL − SOMME DES PAIEMENTS AFFECTÉS
```

## 14. Dettes fournisseurs

Montant initial · payé · solde · devise · date facture · échéance · statut · fournisseur · source · historique des allocations.

```
SOLDE DETTE = MONTANT ORIGINAL − SOMME DES PAIEMENTS AFFECTÉS
```

## 15. Paiements

Unique · partiel · multiple sur plusieurs factures · affectation créance · affectation dette · non affecté si autorisé · référence · date effective · compte financier · devise et taux.

**Règle** : le montant affecté ne peut dépasser le solde disponible du paiement ni le solde ouvert de la créance/dette.

## 16. Caisse

Ouverture · fond initial · encaissements · décaissements autorisés · transferts · retraits/dépôts · comptage · solde théorique · solde réel · écart · clôture · validation et audit.

```
SOLDE THÉORIQUE = FOND INITIAL + ENTRÉES − SORTIES
```

## 17. Écarts de caisse

Calcul automatique · justification obligatoire selon seuil · approbation si nécessaire · enregistrement financier/comptable selon règle · historique utilisateur et session.

## 18. Banques et comptes financiers

Compte bancaire · caisse · Mobile Money · autre · solde · devise · référence · statut.

**Rapprochement bancaire** : importer/saisir relevé · associer transactions · identifier transactions manquantes · doublons · écarts · valider · conserver période et responsable.

## 19. Dépenses

Catégorie · fournisseur éventuel · montant · taxe · devise · date · compte financier · description · pièce justificative · approbation · écriture comptable.

## 20. Budget

Par entreprise · période · compte/catégorie · centre de coût · montant prévu · réalisé · écart · % de consommation · révision contrôlée.

## 21. Trésorerie prévisionnelle

```
TRÉSORERIE FUTURE = SOLDE ACTUEL + ENCAISSEMENTS PRÉVUS − DÉCAISSEMENTS PRÉVUS
```

Inclure créances/dettes avec échéances · budgets/dépenses prévues selon config · scénarios prudent/normal/optimiste si activé · distinction réel vs prévisionnel.

## 22. Multi-devises

Devise fonctionnelle de l'entreprise · devise de transaction · taux utilisé · date/heure du taux · montant devise transaction · montant converti · différences de change · taux historiques conservés.

Une transaction historique ne doit pas être recalculée avec un nouveau taux simplement parce que le taux courant a changé.

## 23. Taxes

Configurable par entreprise · taux et type · compte comptable associé · application par produit ou transaction · historique du taux utilisé · exonération selon config et cadre applicable.

## 24. Comptabilité analytique

Centres de coûts · départements · établissements · activités · projets (si activés) · répartition en % ou montant · rapports analytiques.

## 25. Intercompany

```
ENTREPRISE A → vente/transfert → TRANSACTION INTERCOMPANY → ENTREPRISE B → achat/réception
→ RAPPROCHEMENT → ÉLIMINATION À LA CONSOLIDATION
```

Identifier les deux entreprises · références croisées · rapprocher les montants · détecter les écarts · empêcher la suppression silencieuse · préparer les écritures d'élimination.

## 26. Consolidation

Choisir les entreprises · période · convertir les devises · agrégation des comptes · élimination intercompany · ajustements autorisés · génération des états · traçabilité des ajustements.

La consolidation ne modifie pas les livres comptables individuels.

## 27. Clôture comptable

Vérifier : écritures équilibrées · comptes et périodes · rapprochements · créances/dettes · caisse et banques. Lancer clôture · bloquer modifications normales · réouverture uniquement avec permission et audit.

## 28. Extournes et corrections

Identifier l'écriture source · créer une écriture de correction/extourne · conserver lien source → correction · exiger motif · tracer utilisateur et date · ne pas effacer l'historique.

## 29. Rapprochement financier

Caisse vs POS · banque vs transactions financières · créances vs paiements · dettes vs paiements fournisseurs · comptabilité vs sous-livres · intercompany A vs B · rapports vs écritures sources.

## 30. Audit financier

Création · validation · modification autorisée · annulation · extourne · paiement · affectation · clôture · réouverture · ajustement · approbation.

## 31. Permissions financières

Voir finance · créer transaction · modifier brouillon · valider · annuler · effectuer paiement · affecter paiement · gérer caisse · rapprocher banque · gérer plan comptable · valider écritures · clôturer période · réouvrir période · consolider · exporter.

Chaque permission doit être combinée au périmètre groupe/entreprise/établissement/caisse nécessaire.

## 32. Règles de précision et cohérence

- `DECIMAL`/`NUMERIC` pour les montants ; jamais de flottants binaires.
- Précision monétaire définie par devise.
- Contrôler arrondis et différences.
- Conserver les montants originaux et convertis lorsqu'une conversion intervient.

## 33. Idempotence et transactions

- Une commande financière critique doit pouvoir être rejouée sans créer de doublon (identifiant unique d'opération).
- Vente + paiement/créance + écritures critiques cohérents dans une transaction atomique lorsque le contexte le permet.
- Les traitements asynchrones disposent d'un mécanisme de reprise sûr.

## 34. Rapports financiers

Journal général · grand livre · balance · bilan · compte de résultat · flux de trésorerie · créances âgées · dettes âgées · journal de caisse · rapprochement bancaire · budget vs réalisé · marge · rentabilité · trésorerie prévisionnelle · rapports groupe et consolidation.

## 35. Scénarios de validation

Vente comptant simple · vente avec paiement multiple · vente à crédit avec paiement partiel · vente à crédit avec paiement final · achat comptant · achat à crédit avec paiement partiel · paiement fournisseur final · dépense approuvée · écart de caisse · rapprochement bancaire · transaction multi-devises · différence de change · transaction intercompany · consolidation de trois entreprises · clôture puis tentative de modification · extourne d'une écriture validée.

## 36. Critères de sortie

- Toutes les opérations financières critiques ont une règle définie.
- Créances et dettes fonctionnent avec paiements partiels.
- Les écritures sont équilibrées.
- Les périodes sont contrôlées.
- Les devises et taux sont historisés.
- Les rapprochements sont définis.
- Intercompany et consolidation sont définis.
- Les permissions financières sont définies.
- Les scénarios critiques sont testables.
- Le moteur peut être utilisé par POS, Achats et autres modules sans logique financière dupliquée.

## 37. Livrable suivant

Après validation, passer à la **Phase 5 — Produits, stocks et achats**. Le module devra consommer le moteur financier pour transformer les commandes, réceptions, factures, achats comptants et achats à crédit en mouvements de stock, dettes, paiements et écritures.

---

**NEXORA — Phase 4 validée → Phase 5 : Produits, Stocks & Achats**
