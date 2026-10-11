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

## Étape 2 — Compte 4081 « Fournisseurs — factures non parvenues » (décision du 2026-10-09)

**Défaut.** Une réception augmentait le stock sans écriture ; le stock n'entrait en comptabilité qu'à la facture. Le
compte 3110 restait en retard sur la valeur du stock tant que les factures n'étaient pas saisies (définitivement pour
une réception jamais facturée). Le contrôle E2E « stock = 3110 » était ignoré depuis la phase 19.

**Correction (option retenue par la direction).**

| Opération | Écriture |
|---|---|
| Réception d'articles stockés | D 3110 Stock / C 4081, au coût de réception — même transaction que l'entrée en stock |
| Facture (ou achat comptant) | D 4081 au coût de réception des articles facturés, D/C écart de prix en charge (`purchase_price_variance`, 6800 par défaut), D 4456 TVA / C 4011 (ou trésorerie) |
| Lignes de frais, services, articles non stockés | Inchangé : stock ou achats selon « capitaliser en stock » |
| Retour avant facture | D 4081 / C 3110 au coût de sortie du stock |
| Retour après facture | D 4011 (ou attente) / C 3110 au coût de sortie, C 4456, écart de prix en charge |

Coût de référence d'une ligne facturée : coût moyen des réceptions comptabilisées (la réception citée, sinon celles de
la commande) ; pas encore reçu : prix de la commande, à défaut prix facturé — la réception créditera ensuite le 4081.

**Reprise de l'existant.** V096 crée le 4081 et sa correspondance pour chaque entreprise, l'ajoute à l'initialisation
des nouvelles entreprises et le rattache au compte de groupe du 4011. Aucune écriture passée n'est modifiée : une
facture dont les réceptions sont antérieures au 4081 (sans écriture de réception) garde l'ancien traitement, pour ne
pas laisser de solde débiteur artificiel au 4081.

**Limites connues.** Une facture saisie avant la réception solde le 4081 au prix de la commande ; si le coût saisi à la
réception diffère ensuite de ce prix, l'écart reste au 4081 (à solder par une écriture manuelle). Le retour avant facture est valorisé au coût moyen du stock
au moment du retour, qui peut différer légèrement du coût de la réception.

**Tests.** `GoodsReceivedNotInvoicedTests` : réception → 3110/4081 ; facture au coût de réception → 4081 soldé, dette
créée, 3110 inchangé ; écart de prix → 6800 ; retour avant facture → 4081 / 3110. Contrôle E2E « stock = 3110 »
réactivé, sur installation neuve.

## Étape 3 — Permissions sans effet (§6.2)

**Défaut.** 102 des 316 permissions proposées dans l'administration n'étaient vérifiées nulle part : les retirer d'un
rôle ne restreignait rien. C'étaient les restes d'une matrice générique créer / modifier / supprimer / exporter des
premières migrations (`crm.*`, `analytics.*`, `cash.*`, `payments.*`, `synchronization.*`…) ; les actions réelles
sont protégées par d'autres permissions (`purchasing.approve_order`, `reporting.export`…). Aucun écran ne s'en servait.

**Correction.**
- `pos.reprint` correspond à une vraie action et est désormais vérifiée : réimprimer un ticket de vente déjà sorti
  (duplicata) l'exige. Les rôles caissier, caissier principal, responsable, direction et administrateurs la portent
  déjà ; l'auditeur et le vendeur, non (conception d'origine des rôles). Refus : `print.reprint_forbidden`.
- V097 supprime les 101 autres ; leurs attributions aux rôles disparaissent avec elles (clé en cascade, tracée par
  l'audit des rôles).

**Garde-fou.** `PermissionCatalogTests` : toute permission en base est déclarée par le code (`Permissions`), et toute
permission déclarée existe en base. Une permission décorative ne peut plus réapparaître sans faire échouer la suite.

## Étape 4 — Formulaires en fenêtre ouverts jusqu'à la réponse du serveur (§8.5)

**Défaut.** `useFormDialog` et `usePrompt` rendaient la saisie puis se fermaient ; l'écran appelait le serveur
ensuite. Un refus (règle métier, droit, période close) s'affichait sur la page, mais la saisie était perdue — un
post-mortem d'incident ou une ligne de budget était à retaper. Le test E2E « formulaires » relisait l'incident avant la
fin de son enregistrement et échouait par intermittence.

**Correction.** Option `onSubmit` sur les deux fenêtres : l'enregistrement se fait dans la fenêtre, bouton occupé et
« Annuler » désactivé pendant l'appel ; un refus s'affiche dans la fenêtre, saisie intacte ; la fenêtre ne se ferme
qu'après succès. Les 23 fenêtres de l'application (continuité, BI, intelligence, exploitation, maintenance, plateforme,
facturation) enregistrent ainsi ; l'écran ne fait plus qu'afficher le message de réussite et recharger.

**Tests.** `formDialog.test.tsx` : refus → fenêtre ouverte, motif affiché, saisie conservée ; nouvel essai réussi →
fenêtre fermée avec les valeurs. Le test E2E « formulaires » attend désormais la vraie fin de l'enregistrement.

## Étape 5 — Partage public de documents et points d'accès anonymes (§6.4)

**Défaut.** Le mot de passe d'un lien de partage GED était haché en SHA-256 sans sel, comparé par `!=` (temps
variable), transmis dans l'adresse (`?password=`, conservé par l'historique et les journaux des proxys), sans limite
de tentatives. La vérification publique de document et de licence n'avait pas non plus de limite.

**Correction.**
- Lien protégé : sans mot de passe, le lien affiche une page minimale (sans script, `no-store`, CSP propre) qui
  demande le mot de passe et l'envoie en **POST** ; le paramètre `?password=` n'est plus lu. Un client d'API peut
  envoyer `{ "password": … }` en JSON.
- Nouveaux partages hachés en BCrypt (salé, lent) ; les partages existants gardent leur empreinte SHA-256, comparée
  en temps constant — aucun lien déjà envoyé n'est cassé.
- Politique de débit `public` (30 requêtes par minute et par adresse, `Security:PublicRateLimitPerMinute`) sur le
  partage GED, la vérification de document et la vérification de licence.

**Tests.** API : page de saisie même avec `?password=` dans l'adresse, mauvais mot de passe → 403, bon mot de passe
en formulaire ou en JSON → fichier. Intégration : empreinte BCrypt, `password_required`, ancien partage SHA-256
toujours ouvrable.
