/**
 * Internationalisation minimale (Phase 13 §40). Français en priorité ; l'architecture est
 * prête pour d'autres langues — ajouter un dictionnaire et le référencer dans `DICTS`.
 * Les textes non traduits retombent sur la clé française.
 */

const fr = {
  "nav.home": "Accueil",
  "nav.pos": "Ventes / POS",
  "nav.products": "Produits",
  "nav.inventory": "Stocks",
  "nav.purchasing": "Achats",
  "nav.customers": "Clients",
  "nav.suppliers": "Fournisseurs",
  "nav.finance": "Finance",
  "nav.accounting": "Comptabilité",
  "nav.reports": "Rapports",
  "nav.group": "Entreprises / Groupe",
  "nav.sync": "Synchronisation",
  "nav.integrations": "Intégrations",
  "nav.sectors": "Secteurs & modules",
  "nav.security": "Sécurité",
  "nav.admin": "Administration",
  "shell.search": "Rechercher…",
  "shell.online": "En ligne",
  "shell.offline": "Hors ligne",
  "shell.syncing": "Synchronisation",
  "shell.logout": "Déconnexion",
  "shell.theme": "Thème",
  "common.save": "Enregistrer",
  "common.cancel": "Annuler",
  "common.confirm": "Confirmer",
  "common.loading": "Chargement…",
  "common.empty": "Rien à afficher",
} as const;

export type MessageKey = keyof typeof fr;

const DICTS: Record<string, Partial<Record<MessageKey, string>>> = { fr, "fr-FR": fr };

let dict: Partial<Record<MessageKey, string>> = fr;

export function setLocale(locale: string | null | undefined) {
  const key = locale ?? "fr-FR";
  dict = DICTS[key] ?? DICTS[key.split("-")[0]] ?? fr;
}

export function t(key: MessageKey): string {
  return dict[key] ?? fr[key] ?? key;
}
