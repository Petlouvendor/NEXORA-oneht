# NEXORA ONE — DESIGN SYSTEM

**Identité visuelle premium et système d'interface — THE NEXT GROUP**
**Version 2.0** (NEXORA ONE, 27/09/2026 — voir [brief NEXORA ONE](nexora-one-experience-intelligente.md) §3-4 ; v1.0 = Phase 1)

> Document de référence. À conserver au même titre que le [cahier des charges](cahier-des-charges.md).

---

## 1. Vision de marque

NEXORA doit être perçue comme le **centre de pilotage de l'entreprise** : moderne, fiable, intelligent, précis et évolutif. L'interface doit être premium sans sacrifier la rapidité du POS.

## 2. Direction artistique

**Dark Premium + Electric Turquoise + Glass / depth subtil + Data Visualization + Micro-interactions + Intelligence cards.**

Futurisme **fonctionnel** : chaque élément visuel sert l'information, la navigation ou la décision. À éviter : l'apparence d'un logiciel de caisse traditionnel, les interfaces surchargées, le glassmorphism excessif, le « gadget sci-fi ».

## 3. Palette

Palette v2 (brief NEXORA ONE §3), neutres froids. Tokens dans `frontend/nexora-web/src/shared/theme.css`.

| Rôle | Token | Sombre (défaut) |
|---|---|---|
| Marque (turquoise électrique) | `--nx-brand` | `#00F5D4` |
| Fond | `--nx-bg` | `#05080A` |
| Surface | `--nx-surface` | `#0B1114` |
| Surface secondaire | `--nx-surface-2` | `#10191D` |
| Bordure | `--nx-border` | `rgba(255,255,255,0.08)` |
| Texte | `--nx-text` | `#F4F7F7` |
| Texte secondaire | `--nx-text-2` | `#8A9A9D` |
| Succès (vert) | `--nx-success` | `#3DDC97` |
| Attention (ambre) | `--nx-warning` | `#FFB547` |
| Danger (rouge contrôlé) | `--nx-error` | `#F2566F` |
| Information | `--nx-info` | `#6CB6FF` |

Le turquoise est une **couleur d'accent technologique** et ne doit pas envahir l'interface : liserés, états actifs, pastilles `--nx-brand-soft` / `--nx-brand-line`, jamais de grands aplats. Le mode clair, le contraste élevé et la densité compacte redéfinissent ces mêmes tokens.

### 3.1 Profondeur, verre, grille

| Groupe | Tokens | Usage |
|---|---|---|
| Élévation | `--nx-elev-1` · `--nx-elev-2` · `--nx-elev-3` · `--nx-glow` | carte posée · carte d'intelligence / popover · palette, panneau NEXORA AI · focus d'un élément IA |
| Verre | `--nx-glass` + `--nx-glass-blur` | barre supérieure, barre latérale, palette Ctrl+K, panneau NEXORA AI — seulement sur ce qui flotte au-dessus du contenu |
| Signature IA | `--nx-ai-gradient` | fond des cartes de recommandation et des réponses NEXORA AI (un liseré lumineux, jamais un aplat) |
| Formes | `--nx-radius-sm/-/lg/xl` | 7 · 10 · 16 · 22 px |
| Grille | `.nx-grid` 12 colonnes, `--nx-gap` 16 px ; `.nx-col-3/4/6/8/12` | toutes les pages ; repli à 6 puis 12 colonnes sous 1100 / 720 px |

## 4. Logo

Créer un **monogramme « N » abstrait** évoquant connexion, flux et progression. Le symbole doit fonctionner seul pour favicon, application, POS, mobile et avatar. Prévoir versions **sombre, claire et monochrome**.

## 5. Typographie

- **Interface** : Inter
- **Titres** (si souhaité) : Geist ou Manrope
- **Tableaux financiers** : chiffres tabulaires
- Hiérarchie forte et lisible.

## 6. Principes UI

- Data First
- Clarté avant décoration
- Une action principale identifiable
- Profondeur subtile
- Espacement généreux
- États et permissions visibles
- Langage visuel commun à tous les modules

## 7. Navigation

**Sidebar premium compacte** avec symbole NEXORA.

Sections (v2, brief NEXORA ONE §5) : **Dashboard** · Commerce (Ventes & commandes · POS · Factures & documents · Clients · Fournisseurs) · Opérations (Stock · Catalogue · Achats · Terrain · GED) · Finance · RH · Pilotage (Rapports · Intelligence · Automatisation) · Paramètres.

Navigation **adaptative au rôle** : chaque entrée porte la permission de son écran (la même que la route) — un caissier ne voit pas les modules d'un directeur ; un groupe vide disparaît.

Sélecteur d'entreprise accessible en permanence. **Ctrl+K** (recherche/commande universelle) et **Ctrl+J** (NEXORA AI) disponibles partout.

## 8. Quick Switch

Passage rapide entre une entreprise, plusieurs entreprises et la vue groupe :

```
ABC GROUP → Pharmacie ABC → Restaurant ABC → Supermarché ABC → Vue Groupe
```

## 9. Command Center

Dashboard directionnel (v2, brief §6) qui répond d'abord à : *que se passe-t-il ? qu'est-ce qui est important ? que dois-je faire ? qu'est-ce qui risque d'arriver ?* — accueil personnalisé (« Bonjour, … ») → 4 KPI héros (CA, profit, cash flow net, valeur du stock) avec variation vs période précédente de même durée → **NEXORA Intelligence** (compteurs + 3 cartes de recommandation) → **Top performance** (produits, clients, meilleures journées) → **Risques** (stock, finance, clients, opérations) → **Activité** (timeline) → **Prévisions** (CA et trésorerie, avec confiance). Chaque bloc ne s'affiche qu'avec sa permission.

Ajouter **« Santé de l'entreprise » /100** avec explication.

## 10. POS

Interface dédiée, tactile, rapide et minimaliste. Recherche, catégories, panier, total, paiements et validation immédiatement accessibles. Ne pas reproduire la complexité du back-office.

## 11. Finance & comptabilité

Cartes financières sobres, tableaux lisibles, graphiques simples et filtres rapides. Mettre en évidence trésorerie, créances, dettes, échéances et flux prévisionnels.

## 12. Crédit

Présenter créances clients et dettes fournisseurs avec **total, payé, restant et échéance**.

Exemple : `500 000 HTG → 400 000 HTG payé → 100 000 HTG restant`.

## 13. Micro-interactions

Transitions courtes et fonctionnelles pour : validation, paiement, synchronisation, stock faible, sauvegarde et erreur. Les animations ne doivent jamais ralentir l'utilisation.

## 14. NEXORA AI (ex-Ask Nexora)

Panneau latéral global (bouton « NEXORA AI » de la barre, **Ctrl+J**, ou « Demander à NEXORA AI » depuis Ctrl+K). Questions comme :
- « Pourquoi mes bénéfices ont diminué ? » · « Quels produits dois-je commander ? » · « Quels produits se vendent le mieux ? »
- « Pourquoi mon stock diminue ? » · « Quels clients ont des paiements en retard ? » · « Montre-moi les dépenses qui ont augmenté. »

Chaque réponse montre : l'explication (phrase principale + causes numérotées), les **données** (graphique en barres de l'unité dominante + tuiles), les **sources** cliquables vers l'écran d'origine, les **recommandations** ouvertes liées, et « Voir l'analyse ». Les réponses sont calculées de façon déterministe côté serveur, dans la limite des droits de l'utilisateur ; une estimation est toujours signalée comme telle.

## 15. Responsive

Cohérence desktop, tablette et tactile. POS optimisé pour écrans tactiles ; back-office optimisé pour écrans larges et tableaux.

## 16. Accessibilité

Contrastes suffisants, tailles lisibles, navigation clavier, focus visibles, erreurs compréhensibles, et **aucune information transmise uniquement par la couleur**.

## 17. États

Chaque composant doit prévoir : normal, survol, actif, focus, désactivé, chargement, succès, avertissement, erreur et **état vide**.

## 18. Composants

Boutons · champs · sélecteurs · KPI · tableaux · filtres · onglets · modales · tiroirs · badges · alertes · notifications · graphiques · sélecteur d'entreprise · calendrier · timeline · recherche · panier POS · clavier numérique · paiement.

### 18.1 Composants d'intelligence (v2 — `src/shared/intel.tsx`, `src/ai/`)

Construits une fois, réutilisés par le Dashboard, NEXORA INTELLIGENCE et NEXORA AI — aucun écran ne redessine sa propre carte.

| Composant | Rôle |
|---|---|
| `SectionHeader` | surtitre (catégorie, turquoise pour l'intelligence) + titre + action : hiérarchie forte |
| `HeroKpi` + `Sparkline` + `Delta` | KPI du dashboard : valeur, variation signée (flèche + couleur + valeur), micro-tendance |
| `SignalCount` | compteur de signaux (recommandations, alertes, opportunités) |
| `RecommendationCard` | carte **QUOI / POURQUOI / IMPACT / ACTION** : titre, métriques de preuve (libellés métier), pourquoi, impact, « NEXORA recommande », puis [Voir l'analyse] [Modifier] [Commander/Relancer/…] [Ignorer] — un bouton sans permission n'apparaît pas |
| `ActivityTimeline` | opérations récentes : quoi, montant, quand, lien vers l'objet |
| `BarList` | classements (top performance) et données des réponses NEXORA AI ; négatif en rouge |
| `ForecastChart` | historique plein + projection pointillée, toujours accompagné du niveau de confiance |
| `CommandPalette` v2 | Ctrl+K : produits, clients, fournisseurs, employés, factures, ventes (serveur, filtré par permission), pages, actions, paramètres, question à NEXORA AI ; raccourcis Produit → Stock → Ventes → Achats → Recommandations |
| `NexoraAiPanel` / `AiConversation` | conversation NEXORA AI (§14) |

Version publiée par `GET /api/v1/design-system/version` : **2.0.0** (30 composants).

## 19. Règles financières (UI)

- Réserver les accents aux variations et alertes.
- Toujours afficher devise et précision pertinentes.
- Les montants importants doivent être immédiatement lisibles.
- Les informations critiques doivent avoir un libellé explicite.

## 20. À éviter

Interface type Excel · menus interminables · couleurs sans fonction · animations décoratives · excès de cartes KPI · texte minuscule · icônes ambiguës · tableaux sans filtres · surcharge visuelle.

## 21. Expérience premium

Le premium vient de la **cohérence, de la précision, de la vitesse et des détails** : alignements, espaces réguliers, transitions discrètes, états clairs, graphiques propres et langage uniforme.

## 22. Cohérence des modules

POS, finance, comptabilité, stock, pharmacie et restaurant utilisent les **mêmes tokens, composants et règles**. Les écrans spécialisés doivent rester immédiatement identifiables comme NEXORA.

## 23. Onboarding

Assistant de configuration : type d'entreprise, activités, établissements, caisses, utilisateurs, devise, finance/comptabilité et mode local/hybride/cloud. L'environnement est configuré **sans créer une autre version du produit**.

## 24. Signature de marque

- **Nom** : NEXORA ONE — une plateforme de **THE NEXT GROUP**
- **Slogan du groupe** : « Connecter les visions, concrétiser l’avenir. »
- **Positionnement** : NEXORA ONE = Business Management + Intelligence + Automation + Decision Support — le système d'exploitation intelligent de l'entreprise (DATA → INFORMATION → INSIGHT → RECOMMENDATION → ACTION).
- Code, base et identifiants techniques restent « Nexora » / `nexora` / `nxk_`.

## 25. Principe directeur

> « NEXORA ne doit pas seulement afficher les données de l'entreprise. Il doit aider à la comprendre et à la piloter. »
