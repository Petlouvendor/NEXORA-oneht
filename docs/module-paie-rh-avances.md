# NEXORA ONE — MODULE — PAIE, RH & AVANCES SUR SALAIRE

Employés • Contrats • Présences • Salaires • Avances • Retenues • Paiements • Historique

Document fonctionnel et technique destiné à l'équipe de développement — THE NEXT GROUP.

> Spécification transmise par le propriétaire du produit le 2026-09-24, reproduite telle quelle.
> Mise en œuvre et correspondances : [module-paie-rh-avances-implementation.md](module-paie-rh-avances-implementation.md).

## 1. Objet et vision

Ce module permet à NEXORA ONE de gérer le cycle complet de la paie et des informations RH d'une entreprise : employés, postes, contrats, rémunération, périodes de paie, éléments variables, absences, heures supplémentaires, primes, retenues, avances sur salaire, acomptes, prêts éventuels, validation, paiement, bulletins et historique.

Le module doit fonctionner comme un sous-système financier contrôlé : une paie validée produit des écritures et des obligations financières traçables, sans permettre à un utilisateur non autorisé de consulter ou modifier les salaires.

## 2. Objectifs

- Centraliser les dossiers employés nécessaires à la paie.
- Gérer les contrats et la rémunération de chaque employé.
- Créer des périodes de paie mensuelles ou selon la fréquence configurée.
- Calculer le salaire brut, les primes, heures supplémentaires, retenues et net à payer.
- Gérer les avances sur salaire avec remboursement automatique ou manuel.
- Gérer plusieurs avances simultanément si la politique de l'entreprise l'autorise.
- Conserver l'historique complet de chaque avance, remboursement et retenue.
- Gérer absences, congés et autres éléments ayant un impact sur la paie.
- Permettre la validation hiérarchique avant paiement.
- Produire un bulletin de paie et des rapports.
- Intégrer les paiements à la trésorerie/finance de NEXORA ONE.
- Assurer audit, permissions, confidentialité et isolation par entreprise.
- Préparer le module à l'intégration de règles fiscales et sociales configurables selon la juridiction.

## 3. Principe général du cycle de paie

Flux recommandé :

EMPLOYÉ → CONTRAT → PÉRIODE DE PAIE → PRÉSENCES/VARIABLES → BRUT → RETENUES → AVANCES → NET → VALIDATION → PAIEMENT → BULLETIN → JOURNAL/AUDIT

## 4. Périmètre fonctionnel

| Domaine | Fonctions |
|---|---|
| Employés | Dossier, statut, poste, département, coordonnées, date d'entrée. |
| Contrats | Type, dates, salaire, fréquence, avantages, conditions. |
| Rémunération | Salaire de base, taux horaires, primes, commissions, avantages. |
| Temps | Présences, absences, congés, heures supplémentaires. |
| Paie | Périodes, calcul brut/net, retenues, variables. |
| Avances | Demande, approbation, versement, remboursement, solde. |
| Retenues | Impôts, cotisations, avances, prêts, autres retenues configurées. |
| Paiement | Cash, banque, virement ou autre moyen configuré. |
| Bulletins | Génération, consultation, impression, export selon permission. |
| Rapports | Masse salariale, net, retenues, avances, coûts. |
| Audit | Historique complet des actions sensibles. |

## 5. Sécurité et confidentialité

Les données de paie sont des données hautement confidentielles. Le système doit appliquer un contrôle strict côté backend.

- Le caissier POS n'a aucun accès aux salaires des employés.
- Un employé ne peut voir que ses propres informations autorisées.
- Le responsable RH peut gérer les dossiers et éléments autorisés.
- Le comptable peut traiter les éléments financiers selon ses permissions.
- Le Manager peut valider certaines demandes selon la politique.
- La Direction/Administrateur autorisé peut consulter les rapports globaux.
- Les permissions sont vérifiées côté API ; cacher un menu dans React ne constitue pas une sécurité.
- Les accès à un autre tenant ou à un autre employé doivent être refusés côté serveur.
- Les bulletins et données sensibles doivent être protégés contre l'accès direct par identifiant.

## 6. Rôles proposés

| Rôle | Accès principal |
|---|---|
| Administrateur système | Configuration technique, sans accès automatique au contenu RH sensible sauf permission explicite. |
| Administrateur entreprise | Paramètres RH/paie, utilisateurs et permissions. |
| RH / Responsable RH | Employés, contrats, variables RH, demandes et processus autorisés. |
| Manager | Validation de certaines demandes, absences, avances et variables selon permission. |
| Comptable / Chef comptable | Calcul/contrôle paie, retenues, paiements et intégration finance. |
| Direction / Propriétaire | Vue globale, validation finale et rapports selon politique. |
| Employé | Consultation de ses données et bulletins, demandes autorisées. |
| Auditeur | Lecture et audit, sans modification. |

## 7. Dossier employé

| Bloc | Données |
|---|---|
| Identité | ID employé, matricule, nom, prénom, date de naissance si nécessaire. |
| Contact | Téléphone, email, adresse, contact d'urgence selon politique. |
| Emploi | Poste, département, responsable, lieu de travail, statut. |
| Contrat | Type, date début, date fin, période d'essai, statut. |
| Rémunération | Salaire de base, fréquence, devise, taux horaire, avantages. |
| Banque | Informations de paiement autorisées et protégées. |
| Fiscal/social | Identifiants ou paramètres requis par la réglementation applicable. |
| Documents | Contrat, justificatifs, pièces RH selon politique. |
| Audit | Créateur, dates, modifications et historique. |

## 8. Contrats et historique de rémunération

Une modification de salaire ne doit pas écraser l'ancien historique.

- Conserver la date d'effet de chaque rémunération.
- Conserver l'ancien et le nouveau salaire.
- Enregistrer l'utilisateur et le motif du changement.
- Permettre de reconstituer la rémunération applicable à une période donnée.
- Un contrat terminé ne doit pas être supprimé physiquement.

## 9. Périodes de paie

| Élément | Exemple |
|---|---|
| Fréquence | Mensuelle, bihebdomadaire, hebdomadaire selon configuration. |
| Période | 01/09/2026 → 30/09/2026. |
| Date de clôture | Date après laquelle les variables sont verrouillées. |
| Date de paiement | Date prévue de versement. |
| Statut | Brouillon, calculée, en validation, approuvée, payée, clôturée, annulée selon workflow. |

Une période clôturée ne doit plus être modifiable librement. Une correction doit passer par un mécanisme de régularisation auditable.

## 10. Composition de la paie

| Composant | Exemples |
|---|---|
| Salaire de base | Rémunération contractuelle. |
| Heures supplémentaires | Heures × taux × coefficient configuré. |
| Primes | Performance, transport, logement, etc. |
| Commissions | Selon règles de l'entreprise. |
| Avantages | Avantages en nature selon configuration. |
| Absences | Déduction selon politique et réglementation. |
| Retenues légales | Taxes/cotisations selon règles configurées. |
| Avance salaire | Montant à récupérer. |
| Prêt employé | Échéance du prêt si module activé. |
| Autres retenues | Éléments autorisés et documentés. |

## 11. Calcul général

Le moteur doit séparer les composants afin de rendre le calcul explicable.

Brut = Salaire de base proratisé + heures supplémentaires + primes + commissions + avantages imposables/assimilés selon règles

Total retenues = retenues légales + absences/déductions + avances + prêts + autres retenues autorisées

Net à payer = Brut − Total retenues

Les règles fiscales et sociales ne doivent pas être codées en dur sans possibilité de configuration/versionnement. Elles doivent être adaptées à la juridiction applicable et validées par le responsable compétent.

## 12. Avances sur salaire

Fonction centrale demandée : lorsqu'une entreprise avance une partie du salaire à un employé, NEXORA ONE doit créer une dette/avance distincte et suivre son remboursement.

| Champ | Description |
|---|---|
| advance_id | Référence unique. |
| employee_id | Employé. |
| request_date | Date de demande. |
| amount_requested | Montant demandé. |
| amount_approved | Montant approuvé. |
| amount_paid | Montant effectivement versé. |
| balance | Solde restant à récupérer. |
| reason | Motif si requis. |
| approval | Utilisateur et date de validation. |
| payment | Date, caisse/compte, moyen et référence. |
| recovery_method | Retenue sur prochaine paie ou échéancier. |
| status | Demandée, approuvée, payée, en remboursement, soldée, rejetée, annulée. |

## 13. Workflow d'une avance

1. Employé ou RH crée une demande d'avance.
2. Le système vérifie les règles : salaire, avances existantes, plafond et période.
3. Le Manager/RH/Direction autorisé valide ou refuse.
4. Le montant approuvé est enregistré.
5. Le paiement de l'avance est effectué et relié à la trésorerie/caisse.
6. Une créance d'avance est créée sur l'employé.
7. Le système programme sa récupération selon la règle choisie.
8. Chaque retenue de paie diminue le solde de l'avance.
9. Une fois le solde à zéro, l'avance passe à 'soldée'.
10. Toutes les étapes restent dans l'audit.

## 14. Exemple d'avance

| Élément | Montant |
|---|---|
| Salaire mensuel | 60 000 HTG |
| Avance accordée | 20 000 HTG |
| Récupération paie 1 | 10 000 HTG |
| Récupération paie 2 | 10 000 HTG |
| Solde avance | 0 HTG |

Le système ne doit pas considérer l'avance comme une nouvelle charge salariale si elle constitue simplement une avance récupérable sur salaire. La comptabilisation exacte doit suivre le plan comptable et les règles de l'entreprise.

## 15. Remboursement d'une avance

- Remboursement total ou fractionné.
- Possibilité de définir un nombre d'échéances.
- Montant fixe ou pourcentage du net selon politique.
- Prévention d'une retenue supérieure au solde.
- Prévention d'une retenue qui rendrait le net négatif, sauf règle légale explicitement applicable.
- Modification de l'échéancier soumise à permission.
- Historique de chaque récupération.

| Échéance | Montant |
|---|---|
| Paie septembre | 10 000 HTG |
| Paie octobre | 10 000 HTG |
| Solde | 0 HTG |

## 16. Plusieurs avances

Si l'entreprise autorise plusieurs avances, elles doivent rester séparées.

- Chaque avance possède sa référence.
- Chaque avance possède son solde.
- Le système conserve son échéancier.
- Les retenues sont affectées explicitement à une ou plusieurs avances.
- Le système applique un plafond d'endettement configurable.
- Une nouvelle avance peut être bloquée si les avances précédentes sont trop importantes.

## 17. Autres prêts au personnel

Le module peut distinguer une avance sur salaire d'un prêt personnel.

| Type | Logique |
|---|---|
| Avance salaire | Récupération généralement sur une ou plusieurs paies proches. |
| Prêt employé | Échéancier potentiellement plus long, avec conditions propres. |
| Acompte | Paiement anticipé d'une rémunération déjà due selon la politique. |
| Remboursement de frais | Dette de l'entreprise envers l'employé, donc sens financier inverse. |

## 18. Présences, absences et congés

- Importer ou saisir les présences selon la solution retenue.
- Gérer absences justifiées/non justifiées.
- Gérer congés et jours payés/non payés.
- Calculer les impacts selon règles configurées.
- Permettre validation Manager/RH.
- Verrouiller les données après clôture de la période.

## 19. Heures supplémentaires

- Enregistrer date, nombre d'heures, type et taux.
- Demander validation lorsque nécessaire.
- Appliquer coefficient/règle configurée.
- Intégrer automatiquement le montant validé dans la paie.
- Conserver la preuve de l'approbation.

## 20. Primes, commissions et avantages

Les éléments variables doivent être configurables et traçables.

| Élément | Exemple de configuration |
|---|---|
| Prime fixe | 25 000 HTG ponctuelle. |
| Prime récurrente | 10 000 HTG par mois. |
| Commission | Pourcentage ou montant selon ventes. |
| Transport | Allocation configurée. |
| Logement | Avantage selon politique. |
| Performance | Montant validé par Manager. |

## 21. Retenues

- Retenues légales/sociales configurables.
- Retenues pour avance.
- Retenues pour prêt.
- Retenues disciplinaires uniquement si légalement et contractuellement autorisées.
- Autres retenues documentées.
- Chaque retenue doit avoir une source, un montant et une règle.

## 22. Validation de la paie

Workflow recommandé :

BROUILLON → CALCUL → CONTRÔLE RH/COMPTABLE → VALIDATION → APPROBATION FINALE → PAIEMENT → BULLETINS → CLÔTURE

- Une paie validée ne doit plus être modifiable directement.
- Une modification post-validation passe par une régularisation.
- Le système doit afficher les anomalies avant approbation.
- Les approbateurs doivent être identifiés dans l'audit.

## 23. Contrôles avant validation

- Employé actif pendant la période.
- Contrat valide.
- Salaire applicable correct.
- Absences validées.
- Variables approuvées.
- Avances existantes correctement récupérées.
- Retenues cohérentes.
- Net à payer non incohérent.
- Doublons absents.
- Compte bancaire/moyen de paiement valide si paiement électronique.
- Écart inhabituel signalé.

## 24. Paiement des salaires

| Moyen | Fonction |
|---|---|
| Virement bancaire | Paiement individuel ou groupé. |
| Cash | Paiement en espèces selon politique et contrôle de caisse. |
| Mobile money | Si intégré/configuré. |
| Autre | Moyen autorisé par l'entreprise. |

Chaque paiement doit avoir une référence, un montant, une date, un bénéficiaire, un moyen, un statut et un lien avec la paie.

## 25. Bulletin de paie

- Identité employé.
- Employeur.
- Période.
- Poste/département selon besoin.
- Salaire de base.
- Primes/variables.
- Brut.
- Retenues détaillées.
- Avances récupérées.
- Net à payer.
- Date de paiement.
- Référence du paiement.
- Mentions légales configurées.
- Identifiant unique du bulletin.

## 26. Historique financier de l'employé

La fiche employé doit fournir une vue chronologique et financière des opérations autorisées :

- Salaires calculés.
- Salaires payés.
- Avances accordées.
- Paiements d'avances.
- Récupérations par paie.
- Prêts et échéances.
- Remboursements de frais.
- Régularisations.
- Bulletins générés.
- Modifications importantes.

## 27. Rapports

- Masse salariale par période.
- Net à payer par période.
- Brut par département.
- Retenues par catégorie.
- Avances en cours.
- Avances soldées.
- Avances par employé.
- Dette totale du personnel envers l'entreprise.
- Paies impayées.
- Écarts entre périodes.
- Coût employeur selon règles configurées.
- Export PDF/Excel/CSV selon permissions.

## 28. Dashboard RH/Paie

| Indicateur | Description |
|---|---|
| Employés actifs | Nombre actuel. |
| Masse salariale | Total brut/net selon vue. |
| Paie à payer | Montant restant avant paiement. |
| Avances en cours | Nombre + solde. |
| Retenues | Total de la période. |
| Anomalies | Éléments nécessitant contrôle. |
| Paies en validation | Périodes/dossiers en attente. |
| Paiements effectués | Montant et volume. |

## 29. API indicative

| Méthode | Endpoint | Fonction |
|---|---|---|
| GET | /api/employees | Liste/recherche autorisée. |
| POST | /api/employees | Créer employé. |
| GET | /api/employees/{id} | Dossier autorisé. |
| POST | /api/employees/{id}/contracts | Créer contrat. |
| GET | /api/payroll/periods | Périodes de paie. |
| POST | /api/payroll/periods | Créer période. |
| POST | /api/payroll/periods/{id}/calculate | Calculer paie. |
| POST | /api/payroll/{id}/approve | Approuver. |
| POST | /api/payroll/{id}/pay | Enregistrer paiement. |
| POST | /api/employees/{id}/salary-advances | Demander avance. |
| POST | /api/salary-advances/{id}/approve | Approuver avance. |
| POST | /api/salary-advances/{id}/pay | Verser avance. |
| GET | /api/salary-advances/{id}/history | Historique. |
| POST | /api/salary-advances/{id}/repay | Enregistrer récupération. |
| GET | /api/employees/{id}/payslips | Bulletins. |

## 30. Modèle PostgreSQL proposé

| Table | Rôle |
|---|---|
| employees | Dossier employé. |
| employee_contracts | Contrats et historique. |
| salary_history | Historique de rémunération. |
| payroll_periods | Périodes. |
| payroll_runs | Exécutions de paie. |
| payroll_items | Lignes détaillées par employé. |
| payroll_earnings | Primes, heures, commissions, avantages. |
| payroll_deductions | Retenues. |
| employee_advances | Avances. |
| advance_repayments | Récupérations. |
| employee_loans | Prêts si activés. |
| attendance_records | Présences. |
| leave_records | Congés/absences. |
| overtime_records | Heures supplémentaires. |
| payslips | Bulletins. |
| salary_payments | Paiements. |
| payroll_adjustments | Régularisations. |
| employee_documents | Documents RH. |
| audit_logs | Audit. |

## 31. Contraintes de données et calcul

- Utiliser des types numériques exacts pour les montants monétaires.
- Stocker explicitement la devise.
- Utiliser transactions PostgreSQL pour calcul/validation/paiement.
- Versionner les règles de calcul lorsque nécessaire.
- Ne pas modifier rétroactivement une paie clôturée sans régularisation.
- Utiliser des clés uniques pour éviter double paiement.
- Conserver les références entre paie, paiement, avance et écriture financière.

## 32. Intégration Finance

La paie doit être intégrée au module Finance :

- Création des obligations salariales selon le modèle comptable.
- Paiement générant une sortie de trésorerie.
- Avance créant une créance sur employé jusqu'à récupération.
- Récupération diminuant la créance d'avance.
- Remboursement de frais créant une dette de l'entreprise envers l'employé.
- Chaque écriture financière doit être réconciliable avec le dossier RH.

## 33. NEXORA ONE Intelligence

- Détecter une variation anormale de masse salariale.
- Identifier une augmentation inhabituelle des heures supplémentaires.
- Repérer des avances fréquentes ou atypiques selon règles.
- Signaler les avances proches d'une limite configurée.
- Identifier des doublons ou anomalies de paiement.
- Comparer les coûts de paie entre périodes.
- Répondre à des questions comme : « Combien reste-t-il d'avances à récupérer ? » ou « Quelle est la masse salariale de ce mois ? ».

Les calculs financiers restent déterministes côté backend. L'IA peut analyser et expliquer, mais ne doit pas inventer ou modifier les montants.

## 34. Notifications

- Demande d'avance à valider.
- Avance approuvée/refusée.
- Avance versée.
- Échéance d'avance proche.
- Paie en attente de validation.
- Paie approuvée.
- Paiement effectué.
- Anomalie détectée.
- Document/bulletin disponible.

## 35. Audit

- Création/modification employé.
- Modification contrat/salaire.
- Création et validation avance.
- Paiement d'avance.
- Modification d'échéancier.
- Calcul/validation de paie.
- Modification post-validation.
- Paiement salaire.
- Génération bulletin.
- Accès ou export de données sensibles selon politique d'audit.

Chaque événement doit conserver utilisateur, date/heure, tenant, action, objet, ancienne/nouvelle valeur si applicable, montant, référence et motif.

## 36. Mode correction et régularisation

Une paie déjà approuvée ou payée ne doit pas être silencieusement modifiée. Toute correction doit créer une régularisation reliée à la paie originale, avec justification et validation selon permission.

## 37. Tests d'acceptation obligatoires

(Numérotation d'origine du document conservée : 11 à 30.)

11. Créer un employé avec contrat et salaire.
12. Modifier le salaire avec date d'effet et vérifier l'historique.
13. Créer une période de paie.
14. Calculer une paie simple.
15. Ajouter une prime et vérifier le brut/net.
16. Ajouter une absence et vérifier son impact.
17. Ajouter des heures supplémentaires validées.
18. Créer une avance de salaire.
19. Approuver puis payer l'avance.
20. Récupérer l'avance sur deux paies et vérifier le solde.
21. Tester plusieurs avances séparées.
22. Empêcher une récupération supérieure au solde.
23. Tester les permissions d'un employé sur ses propres données.
24. Tester qu'un caissier ne peut pas consulter les salaires.
25. Tester l'isolation entre deux entreprises.
26. Tester le double envoi d'un paiement.
27. Valider une paie et vérifier qu'elle devient verrouillée.
28. Créer une régularisation après clôture.
29. Générer et vérifier un bulletin.
30. Réconcilier paiement RH et Finance.

## 38. Definition of Done

- Employés et contrats opérationnels.
- Historique des rémunérations opérationnel.
- Calcul de paie testé.
- Avances entièrement gérées.
- Récupération automatique/contrôlée opérationnelle.
- Retenues et variables testées.
- Validation et clôture opérationnelles.
- Paiements intégrés à Finance.
- Bulletins générés.
- Permissions et confidentialité testées.
- Audit complet.
- Tests unitaires, intégration et sécurité.
- Documentation API et règles métier.
- Aucun bug critique ouvert.

## 39. Règles que le développeur ne doit pas violer

- Ne jamais exposer les salaires à un rôle non autorisé.
- Ne jamais faire confiance à un montant envoyé par le frontend.
- Ne jamais supprimer une paie ou un paiement finalisé.
- Ne jamais modifier silencieusement une paie clôturée.
- Ne jamais confondre une avance récupérable avec une nouvelle charge salariale.
- Ne jamais créer deux paiements pour une même instruction.
- Ne jamais mélanger les employés de deux tenants.
- Ne jamais permettre à un utilisateur de s'auto-approuver une opération si la séparation des tâches l'interdit.
- Ne jamais laisser l'IA être l'autorité du calcul financier.

## 40. Critère final de qualité

Un responsable autorisé doit pouvoir ouvrir le dossier d'un employé et comprendre son contrat, sa rémunération applicable, chaque période de paie, chaque élément du calcul, ses avances, les récupérations déjà effectuées, le solde restant, ses paiements et ses bulletins, avec une traçabilité complète.

## 41. Flux global final

EMPLOYÉ → CONTRAT → RÉMUNÉRATION → PRÉSENCES/VARIABLES → PAIE → RETENUES/AVANCES → VALIDATION → PAIEMENT → BULLETIN → FINANCE → AUDIT → ANALYSE

Document de référence — NEXORA ONE / THE NEXT GROUP. Les paramètres fiscaux, sociaux et légaux doivent être configurables et validés selon la juridiction et la politique de l'entreprise avant mise en production.
