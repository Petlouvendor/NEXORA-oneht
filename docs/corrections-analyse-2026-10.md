# Corrections issues de l'analyse exhaustive (2026-10)

> Suivi, étape par étape, des corrections demandées après `docs/analyse-exhaustive-2026-10.md`.
> Pour l'équipe technique et la personne qui validera la recette.

## Étape 1 — Date métier dans le fuseau de l'entreprise (§5.5 de l'analyse)

**Défaut.** La date d'une opération était calculée de trois façons : en UTC par le code C# (49 endroits, dont toutes
les règles comptables), dans le fuseau du serveur PostgreSQL par le SQL (`current_date`), et dans l'heure du poste par
l'interface. Après 20 h en Haïti (19 h en hiver), l'UTC est déjà au lendemain : ventes, encaissements, règlements et
écarts d'inventaire étaient datés du lendemain, alors que les pièces saisies à l'écran gardaient la date du jour. Le
dernier soir du mois, les ventes de la soirée partaient dans le mois suivant.

**Correction.**
- `IBusinessCalendar` (Application) : jour civil de l'entreprise active, à partir de l'horloge testable
  `IDateTimeProvider` et de `company.timezone` (gardé 5 minutes en cache). Toutes les dates « du jour » et toutes les
  dates comptables passent par lui ; les règles comptables reçoivent le fuseau par `PostingContext.DateOf`.
- Session PostgreSQL : à l'ouverture de chaque connexion, `TimeZone` prend le fuseau de l'entreprise active
  (`RlsConnectionInterceptor`). `current_date`, les conversions horodatage → date et les valeurs par défaut des colonnes
  date donnent ainsi le même jour que l'application. Le pool remet la session à zéro au retour.
- Dates saisies (date de facture, date de comptage importée, taux de change d'une écriture manuelle) : converties en
  instant à **midi UTC** (`BusinessDate.Noon`), qui reste le même jour dans tout fuseau de UTC−11 à UTC+11. Minuit UTC
  tombait la veille au soir en Haïti.
- Numéros de pièces : l'année vient de la base (`next_document_number` sans année explicite), donc de l'année locale.
  Une facture du 31 décembre à 21 h n'est plus numérotée sur l'année suivante.
- V095 : un fuseau inconnu est refusé à l'écriture sur `company` (il ferait échouer chaque connexion de l'entreprise) ;
  les valeurs existantes inconnues sont ramenées sur Haïti.

**Laissé en UTC, volontairement.** Le groupe et l'intercompagnie (comparaison de sociétés entre elles, lectures
`AT TIME ZONE 'UTC'`), la facturation de la plateforme SaaS, les fenêtres de détection de fraude et les filtres de
dates du journal d'audit.

**Tests.** `BusinessDateTests` : horloge figée au 30 avril, 22 h heure d'Haïti — jour métier = 30 avril, écriture d'une
dépense du soir datée du 30 avril (reste dans son mois), session PostgreSQL au fuseau de l'entreprise et `current_date`
égal au jour applicatif, fuseau inconnu refusé. Les trois tests qui échouaient chaque soir passent désormais à toute
heure ; les tests qui calculaient leur « aujourd'hui » en UTC utilisent `TestDates.Today`.
