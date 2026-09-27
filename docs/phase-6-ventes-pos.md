# NEXORA — PHASE 6 — VENTES & POS

**Spécification fonctionnelle de la caisse, des ventes et du parcours d'encaissement**
**Version 1.0**

> Document de référence. Implémentation : module `Sales` du backend + POS React
> (voir [`phase-6-implementation.md`](phase-6-implementation.md)).

---

## 1. Objectif

Construire le moteur de vente et l'interface POS de NEXORA pour offrir une expérience extrêmement rapide, fiable et adaptable à une boutique, un supermarché, une pharmacie, un restaurant, un grossiste ou un groupe multi-sites.

## 2. Principes

- Une vente est saisie une seule fois et devient la source des conséquences stock, caisse, finance et comptabilité.
- Le POS doit rester utilisable même sous forte pression opérationnelle.
- Le tactile et le clavier sont tous deux prioritaires.
- Le mode offline doit permettre les opérations essentielles lorsqu'il est activé.
- Les permissions contrôlent chaque action sensible.
- Aucun secteur ne doit nécessiter une version séparée du POS.

## 3. Architecture du parcours

```
PRODUIT → PANIER → CLIENT → TARIFS / PROMOTION → TOTAL
                       ↓
              PAIEMENT OU CRÉDIT
                       ↓
              VALIDATION DE VENTE
                       ↓
       STOCK ↓ + CAISSE/BANQUE ↑ OU CRÉANCE ↑
                       ↓
                  COMPTABILITÉ
                       ↓
                REÇU / RAPPORT
```

## 4. Écran POS

Zone recherche/scanner · panier central · panneau client · totaux permanents · raccourcis produits/catégories configurables · bouton paiement accessible · indicateur caisse/session/utilisateur · indicateur réseau/offline · alertes non bloquantes lorsque possible.

## 5. Recherche produit

Code-barres · SKU · nom · catégorie · marque · recherche partielle · recherche clavier rapide · favoris · récemment utilisés · recherche phonétique/tolérante si activée.

## 6. Scanner

Scanner USB · scanner réseau/périphérique compatible · lecture rapide · quantité répétée · gestion d'un code inconnu · message d'erreur clair · saisie manuelle possible.

## 7. Panier

Ajouter/supprimer ligne · modifier quantité · modifier prix si permission · remise ligne · remise globale · taxe · commentaire · produit pesé · produit composé · réservation de stock selon politique.

## 8. Client

Vente anonyme · sélection client · création rapide selon permission · historique · fidélité · prix spécifique · crédit disponible · solde dû · limite de crédit · alerte retard.

## 9. Tarification

Prix standard · par quantité · par groupe client · promotionnel · établissement/magasin selon config · devise · règles d'arrondi · période de validité · contrôle des modifications.

## 10. Promotions

Remise % · remise montant · achetez X obtenez Y · prix par quantité · produit gratuit · coupon · par catégorie · par période · par client/groupe · limites et règles de cumul.

Les règles de promotion doivent être **déterministes** et **enregistrer le résultat appliqué** à la transaction afin que l'historique ne change pas ultérieurement.

## 11. Taxes

Taxe par produit · par ligne · exonération si applicable · arrondis configurables · affichage avant paiement · montant de taxe conservé dans la vente.

## 12. Calcul du total

```
SOUS-TOTAL → REMISES → TAXES → FRAIS ÉVENTUELS → TOTAL
```

Calcul déterministe · précision monétaire · contrôle des arrondis · total affiché clairement · **recalcul serveur obligatoire avant validation**.

## 13. Paiements

Espèces · carte · virement · chèque · Mobile Money selon intégration · crédit client · paiements multiples · paiement exact · paiement supérieur avec rendu monnaie · paiement partiel si autorisé · annulation avant validation selon workflow.

## 14. Paiement multiple

```
TOTAL 100 000
ESPÈCES 40 000 + CARTE 35 000 + MOBILE 25 000 = 100 000
```

Plusieurs moyens sur une même vente · validation uniquement lorsque le total payé couvre le montant requis (sauf crédit autorisé) · chaque moyen reste identifié · remboursement traçable par moyen lorsque possible.

## 15. Vente comptant

```
PANIER → TOTAL → PAIEMENT → VALIDATION → STOCK ↓ → CAISSE/BANQUE ↑ → COMPTABILITÉ
```

Créer transaction · vérifier stock selon politique · enregistrer paiement · générer mouvements · générer conséquence comptable · émettre reçu.

## 16. Vente à crédit

```
PANIER → CLIENT → CONTRÔLE CRÉDIT → VALIDATION
→ STOCK ↓ → CRÉANCE ↑ → COMPTABILITÉ
PAIEMENT FUTUR → CRÉANCE ↓ → CAISSE/BANQUE ↑
```

Client obligatoire · vérification limite · échéance · paiement initial possible · paiements futurs partiels · solde affiché · historique · relances et alertes.

## 17. Contrôle du crédit

Limite globale · limite par devise si configurée · solde actuel · montant nouvelle vente · encours projeté · retards existants · autorisation exceptionnelle par responsable · motif et audit pour dépassement autorisé.

## 18. Réservations et disponibilité stock

Vérifier disponibilité · réserver pendant le processus si nécessaire · libérer si abandon · décrémenter définitivement à la validation · gérer la concurrence entre caisses · signaler rupture avec alternative si configurée.

## 19. Finalisation de vente

Recalcul serveur · validation permissions · validation stock · validation paiement/crédit · **transaction atomique** · numéro de document · date/heure · utilisateur · caisse/terminal · entreprise/établissement · mouvement stock · finance/comptabilité · audit.

```
AUCUNE VENTE VALIDÉE = AUCUNE MISE À JOUR PARTIELLE
```

## 20. Reçu

Numéro · entreprise · établissement/magasin · date · caissier · produits · quantités · prix · remises · taxes · total · paiements · solde crédit si applicable · informations légales/configurées · QR code ou identifiant de vérification si activé.

## 21. Impression et formats

Imprimante thermique · PDF · email si activé · partage selon plateforme · réimpression contrôlée · format configurable par entreprise · copie client / copie interne si nécessaire.

## 22. Retours

Total ou partiel · recherche par reçu/facture · produit et quantité · motif · contrôle délai · contrôle état produit · lot si applicable · validation selon permission · réintégration stock si applicable · avoir/remboursement · **écriture de correction** · audit.

## 23. Remboursements

Espèces · vers moyen de paiement lorsque possible · avoir · partiel · validation selon seuil · lien obligatoire avec la vente source sauf exception autorisée · traçabilité.

## 24. Annulation

Avant validation · après validation via **procédure de correction** contrôlée · motif obligatoire pour opérations sensibles · autorisation selon rôle · audit · **aucune suppression silencieuse d'une vente financière validée**.

## 25. Caisse et sessions

Ouverture · fond initial · utilisateur assigné · ventes · encaissements · décaissements autorisés · retraits/dépôts · transferts · fermeture · comptage · solde théorique · solde réel · écart · validation.

```
THÉORIQUE = OUVERTURE + ENTRÉES − SORTIES
```

## 26. Multi-caisses

Plusieurs terminaux par magasin · session par caisse · utilisateur affecté · permissions · rapport par terminal · rapport magasin · consolidation établissement/entreprise.

## 27. Gestion des caissiers

Connexion individuelle · ouverture de session · affectation à une caisse · droits limités · transfert de session selon règle · clôture · rapport personnel · audit.

## 28. Mode offline

Scanner et panier locaux · catalogue/cache local nécessaire · validation locale selon politique · **identifiant unique de transaction** · queue de synchronisation · retry · détection des doublons · statut synchronisation · gestion des conflits · synchronisation des paiements selon intégration.

Les fonctions nécessitant une vérification serveur temps réel peuvent être désactivées ou soumises à une politique spécifique lorsque le terminal est hors ligne.

## 29. Fonctionnalités supermarché

Scanner très rapide · produits au poids · balances compatibles · promotions complexes · multiples caisses · ouverture rapide · inventaire · prix affichés · raccourcis clavier · gestion de files si intégration prévue.

## 30. Fonctionnalités pharmacie

Recherche rapide nom/code · lots et expiration · FEFO · traçabilité · produits réglementés selon cadre applicable · contrôles supplémentaires · historique client si légalement autorisé · alertes d'expiration.

## 31. Fonctionnalités restaurant

Tables · prise de commande · transfert de table · division d'addition · cuisine/KDS · extras/modificateurs · pourboire · livraison · paiement partiel/partagé · recettes et consommation stock.

## 32. Fonctionnalités grossiste

Commande client · tarifs par quantité · tarifs professionnels · crédit · livraison · bon de livraison · facturation différée · représentant · stock réservé.

## 33. Fidélité

Points · niveaux client · récompenses · coupons · historique · expiration des points · règles par entreprise · activation/désactivation.

## 34. Ventes en attente

Mettre panier en attente · identifier · récupérer · limiter selon caisse · expiration configurable · **ne pas considérer comme vente validée** · audit si suppression.

## 35. Ventes suspendues et restauration

Le caissier doit pouvoir interrompre une vente sans perdre son panier, puis le reprendre rapidement (client cherche un produit, change de mode de paiement).

## 36. Raccourcis opérationnels

Nouvelle vente · recherche produit · scanner · ajouter client · remise autorisée · suspendre · reprendre · paiement · annuler panier · imprimer reçu · retour · clôture caisse. Configurables et documentés.

## 37. Sécurité POS

Session individuelle · verrouillage automatique · réauthentification pour actions sensibles · permissions remise / remboursement / annulation / modification de prix · limites par montant · journalisation · protection contre doublons.

## 38. Contrôles anti-fraude

Remises anormales · annulations répétées · remboursements inhabituels · ouvertures/clôtures irrégulières · écarts de caisse · ventes sans client lorsque obligatoire · modification de prix · activité inhabituelle par utilisateur · alertes direction.

## 39. Intégration Finance & Comptabilité

```
VENTE COMPTANT   POS → SALE → PAYMENT → CASH/BANK → JOURNAL
VENTE CRÉDIT     POS → SALE → RECEIVABLE → JOURNAL
RETOUR           RETURN → STOCK IN → REFUND/CREDIT → CORRECTION JOURNAL
```

Le POS **déclenche les services métier ; il ne crée pas directement les écritures comptables.**

## 40. Performance

Réponse très courte pour recherche et ajout panier · préchargement des données utiles · cache catalogue local · éviter les appels réseau inutiles · validation finale serveur · traitements non bloquants pour impression/notifications · plusieurs caisses simultanées.

## 41. Permissions POS

Accéder à la caisse · vendre · modifier quantité · modifier prix · appliquer remise · vendre à crédit · voir solde client · rembourser · retourner · annuler · réimprimer · ouvrir caisse · clôturer caisse · voir rapports · opérations sensibles.

## 42. Scénarios de validation

Vente simple avec scan · plusieurs produits · avec remise · avec taxe · multi-paiement · à crédit sans paiement initial · à crédit avec paiement initial · dépassement de limite avec autorisation · rendu monnaie · produit au poids · retour partiel · remboursement · annulation contrôlée · ouverture et clôture de caisse · écart de caisse · deux caisses vendant simultanément le même stock · vente offline puis synchronisation · doublon de synchronisation · contrôle d'accès d'un caissier · contrôle anti-fraude.

## 43. Critères de sortie

- Parcours de vente complet défini.
- Paiements multiples définis.
- Crédit client défini.
- Retours et remboursements définis.
- Caisse et sessions définies.
- Multi-caisses défini.
- Offline défini.
- Périphériques POS définis.
- Fonctions sectorielles identifiées.
- Sécurité et anti-fraude définies.
- Intégration Stock/Finance/Comptabilité définie.
- Scénarios critiques testables.

## 44. Étape suivante

Passer à la **Phase 7 — Dashboard, Rapports & Pilotage**. Elle transformera les données validées de NEXORA en indicateurs opérationnels et financiers pour le caissier, manager, comptable, direction et groupe.

---

**NEXORA — Phase 6 validée → Phase 7 : Dashboard, Rapports & Pilotage**
