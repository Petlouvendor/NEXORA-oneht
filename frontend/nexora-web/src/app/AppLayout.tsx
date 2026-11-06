import { useEffect, useState, Suspense } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { ScreenErrorBoundary } from "@/shared/ScreenErrorBoundary";
import { useAuth } from "@/auth/AuthContext";
import { usePreferences } from "@/shared/PreferencesContext";
import { NAV_GROUPS, ALL_NAV } from "@/app/nav";
import { CommandPalette } from "@/app/CommandPalette";
import { NexoraAiButton, NexoraAiPanel } from "@/ai/NexoraAiPanel";
import { Icon } from "@/shared/icons";
import { TableToolsContext, useClickOutside } from "@/shared/ui";
import { PageExportButton, useTableExportRenderer } from "@/shared/ExportMenu";
import { useOnline } from "@/shared/useOnline";
import { useOutbox } from "@/shared/useOutbox";
import { unreadCount } from "@/notifications/notificationsApi";
import { StepUpHost } from "@/auth/StepUpDialog";

function NavRow({ to, label, icon, end, onNavigate }: {
  to: string; label: string; icon: keyof typeof Icon; end?: boolean; onNavigate: () => void;
}) {
  const I = Icon[icon];
  return (
    <NavLink to={to} end={end} onClick={onNavigate}
             className={({ isActive }) => `nx-navlink${isActive ? " active" : ""}`}>
      <I size={17} /> <span>{label}</span>
    </NavLink>
  );
}

export function AppLayout() {
  const { user, activeCompanyId, setActiveCompany, logout, hasPermission } = useAuth();
  const tableTools = useTableExportRenderer();
  const { prefs, branding, toggleTheme, isFavorite } = usePreferences();
  const online = useOnline();
  const { pending: queued } = useOutbox();
  const location = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);
  const [navOpen, setNavOpen] = useState(false);
  const userMenuRef = useClickOutside<HTMLDivElement>(() => setMenuOpen(false));

  const [unread, setUnread] = useState(0);
  // Finalisation phase 20 (§49) : le compteur était rechargé à chaque changement de page (un appel par navigation) et
  // deux fois au démarrage. Il l'est désormais au changement d'entreprise, toutes les 60 s, et en quittant l'écran
  // Notifications (où l'on vient de lire).
  const onNotifications = location.pathname.startsWith("/notifications");
  useEffect(() => {
    if (!activeCompanyId) return;
    let alive = true;
    const poll = () => unreadCount().then((r) => { if (alive) setUnread(r.unread); }).catch(() => {});
    void poll();
    const t = setInterval(poll, 60_000);
    return () => { alive = false; clearInterval(t); };
  }, [activeCompanyId, onNotifications]);

  const company = user?.companies.find((c) => c.companyId === activeCompanyId);
  const companyLabel = branding?.displayName || company?.companyName || "—";
  const inGroup = Boolean(company?.groupId);

  const favorites = ALL_NAV.filter((i) => isFavorite(i.to) && (!i.permission || hasPermission(i.permission)));
  const closeNav = () => setNavOpen(false);

  return (
    <div className="nx-shell">
      <a href="#nx-main" className="nx-skip">Aller au contenu</a>
      <StepUpHost />

      <aside className={`nx-sidebar${navOpen ? " open" : ""}`}>
        <div className="nx-brandmark">
          <span style={{ color: "var(--nx-brand)" }}><Icon.Monogram size={20} /></span>
          <span style={{ display: "grid", lineHeight: 1.15 }}>
            <span>NEXORA ONE</span>
            <span style={{ fontSize: 10, fontWeight: 600, letterSpacing: "0.14em", color: "var(--nx-text-3)" }}>THE NEXT GROUP</span>
          </span>
        </div>

        {user && user.companies.length > 1 && (
          <select
            className="nx-select" aria-label="Entreprise active"
            value={activeCompanyId ?? ""} onChange={(e) => setActiveCompany(e.target.value)}
            style={{ marginBottom: 6 }}
          >
            {user.companies.map((c) => <option key={c.companyId} value={c.companyId}>{c.companyName}</option>)}
          </select>
        )}

        {favorites.length > 0 && (
          <>
            <div className="nx-navgroup">Favoris</div>
            {favorites.map((i) => <NavRow key={i.to} {...i} onNavigate={closeNav} />)}
          </>
        )}

        {NAV_GROUPS.map((g) => {
          const items = g.items.filter((i) => !i.permission || hasPermission(i.permission));
          if (items.length === 0) return null;
          return (
            <div key={g.title}>
              <div className="nx-navgroup">{g.title}</div>
              {items.map((i) => <NavRow key={i.to} {...i} onNavigate={closeNav} />)}
            </div>
          );
        })}

        <div style={{ marginTop: "auto", paddingTop: 12, color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)", display: "grid", gap: 2 }}>
          <span>NEXORA ONE · {prefs.density === "compact" ? "dense" : "confort"}</span>
          <span style={{ fontStyle: "italic" }}>« Connecter les visions, concrétiser l’avenir. »</span>
        </div>
      </aside>

      <div className="nx-main">
        <header className="nx-topbar">
          <button className="nx-iconbtn nx-menu-toggle" aria-label="Menu" onClick={() => setNavOpen((v) => !v)}>
            <Icon.Layers />
          </button>

          <div className="nx-context" title="Contexte de travail">
            <span style={{ color: "var(--nx-brand)" }}><Icon.Monogram size={15} /></span>
            {inGroup && <><b>{"Groupe"}</b><span className="sep">→</span></>}
            <b style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", maxWidth: 240 }}>{companyLabel}</b>
          </div>

          <div style={{ flex: 1 }} />

          <span
            className="nx-badge"
            style={{ color: online ? "var(--nx-success)" : "var(--nx-warning)" }}
            title={online ? "Connexion active" : "Hors ligne — les ventes sont mises en file"}
          >
            {online ? <Icon.Check size={13} /> : <Icon.Offline size={13} />}
            <span className="nx-hide-sm">{online ? "En ligne" : "Hors ligne"}</span>
            {queued > 0 && (
              <span style={{ color: "var(--nx-info)" }} title={`${queued} opération(s) en file de synchronisation`}>
                · {queued} en file
              </span>
            )}
          </span>

          <PageExportButton />
          <button className="nx-iconbtn" aria-label="Recherche globale (Ctrl+K)" title="Rechercher partout (Ctrl+K)"
                  onClick={() => window.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ctrlKey: true }))}
                  style={{ width: "auto", padding: "0 10px", gap: 8, border: "1px solid var(--nx-border)" }}>
            <Icon.Search size={16} />
            <span className="nx-kbd nx-hide-sm">Ctrl K</span>
          </button>
          <NexoraAiButton />

          <NavLink to="/notifications" className="nx-iconbtn" aria-label="Notifications"
                   style={{ position: "relative" }} title={unread > 0 ? `${unread} non lue(s)` : "Notifications"}>
            <Icon.Bell />
            {unread > 0 && (
              <span style={{
                position: "absolute", top: 2, right: 2, minWidth: 15, height: 15, padding: "0 3px",
                borderRadius: 8, background: "var(--nx-error)", color: "#fff", fontSize: 10, fontWeight: 700,
                display: "grid", placeItems: "center", lineHeight: 1,
              }}>
                {unread > 99 ? "99+" : unread}
              </span>
            )}
          </NavLink>

          <button className="nx-iconbtn" aria-label="Changer de thème"
                  title={prefs.theme === "dark" ? "Passer en clair" : "Passer en sombre"} onClick={toggleTheme}>
            {prefs.theme === "dark" ? <Icon.Sun /> : <Icon.Moon />}
          </button>

          <div ref={userMenuRef} style={{ position: "relative" }}>
            <button className="nx-iconbtn" aria-haspopup="menu" aria-expanded={menuOpen}
                    aria-label="Compte" onClick={() => setMenuOpen((v) => !v)}
                    style={{ width: "auto", padding: "0 8px", gap: 6 }}>
              <span style={{
                width: 26, height: 26, borderRadius: "50%", background: "var(--nx-surface-3)",
                display: "grid", placeItems: "center", fontSize: 12, fontWeight: 700,
              }}>
                {(user?.fullName || user?.email || "?").slice(0, 1).toUpperCase()}
              </span>
              <Icon.ChevronDown size={14} />
            </button>
            {menuOpen && (
              <div role="menu" className="nx-card" style={{
                position: "absolute", right: 0, top: "calc(100% + 6px)", width: 240, padding: 8, zIndex: 30,
                boxShadow: "var(--nx-shadow)",
              }}>
                <div style={{ padding: "8px 10px" }}>
                  <div style={{ fontWeight: 700 }}>{user?.fullName || user?.email || "Utilisateur"}</div>
                  <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-xs)" }}>Session active</div>
                </div>
                <NavLink to="/settings" className="nx-navlink" onClick={() => setMenuOpen(false)}>
                  <Icon.Sliders size={16} /> Mes préférences
                </NavLink>
                <button className="nx-navlink" style={{ width: "100%", background: "transparent", border: "none", cursor: "pointer", color: "var(--nx-error)" }}
                        onClick={logout}>
                  <Icon.Logout size={16} /> Déconnexion
                </button>
              </div>
            )}
          </div>
        </header>

        <main id="nx-main" className="nx-content" key={location.pathname}>
          {/* Export Excel / PDF de n'importe quel tableau affiché (reporting.export). */}
          <TableToolsContext.Provider value={tableTools}>
            {/* Finalisation phase 20 : écrans chargés à la demande — le cadre reste affiché pendant le chargement. */}
            <ScreenErrorBoundary resetKey={location.pathname}>
              <Suspense fallback={<div className="nx-page-loading" role="status" aria-live="polite">Chargement…</div>}>
                <Outlet />
              </Suspense>
            </ScreenErrorBoundary>
          </TableToolsContext.Provider>
        </main>
      </div>

      <CommandPalette />
      <NexoraAiPanel />
    </div>
  );
}
