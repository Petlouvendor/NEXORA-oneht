import { useAsync } from "@/shared/useAsync";
import { dateTime } from "@/shared/format";
import { Badge, Button, Card, SectionLabel } from "@/shared/ui";
import { productionReadiness, type ProductionCheck, type ProductionReadiness } from "@/operations/operationsApi";

const CHECK_TONE = { pass: "success", warn: "warning", fail: "error" } as const;
const CHECK_LABEL = { pass: "conforme", warn: "à surveiller", fail: "bloquant" } as const;
const PROBE_TONE = { Healthy: "success", Degraded: "warning", Unhealthy: "error" } as const;
const PROBE_LABEL = { Healthy: "disponible", Degraded: "dégradé", Unhealthy: "indisponible" } as const;
const CATEGORY_LABEL: Record<string, string> = { security: "Sécurité", monitoring: "Surveillance" };
const muted = { fontSize: 12, color: "var(--nx-text-2)" } as const;

function uptime(seconds: number): string {
  const d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
  return d > 0 ? `${d} j ${h} h` : h > 0 ? `${h} h ${m} min` : `${m} min`;
}

/**
 * État de production (finalisation phase 18) : ce qui tourne réellement — version et commit de l'image, sondes de
 * disponibilité, écart entre les migrations livrées et la base, contrôles de configuration (secrets, HTTPS, proxy,
 * traces et métriques, journaux, tâches, e-mail). Lecture seule ; aucun secret n'est jamais affiché.
 */
export function ProductionSection() {
  const state = useAsync(productionReadiness);
  const p = state.data;
  if (!p) return <Card>{state.error ? <div role="alert" className="nx-error-text">{state.error}</div> : "Chargement…"}</Card>;

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      <Card>
        <SectionLabel action={<Button size="sm" variant="ghost" disabled={state.loading} onClick={state.reload}>Actualiser</Button>}>
          Version en service
        </SectionLabel>
        <div style={{ display: "flex", flexWrap: "wrap", gap: "8px 24px", alignItems: "baseline" }}>
          <span style={{ fontSize: 20, fontWeight: 800 }}>{p.version}</span>
          <Badge tone={CHECK_TONE[p.overall]}>{p.overall === "pass" ? "prête pour la production" : p.overall === "warn" ? "points à surveiller" : "bloquants à corriger"}</Badge>
          <span style={muted}>environnement <b>{p.environment}</b></span>
          {p.commit && <span style={muted}>commit <code>{p.commit.slice(0, 12)}</code></span>}
          <span style={muted}>démarrée {dateTime(p.startedAt)} · en service depuis {uptime(p.uptimeSeconds)}</span>
        </div>
      </Card>

      <Card>
        <SectionLabel>Sondes de disponibilité</SectionLabel>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(min(240px, 100%), 1fr))", gap: 10 }}>
          {p.probes.map((probe) => (
            <div key={probe.name} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: "8px 10px", display: "grid", gap: 4 }}>
              <div style={{ display: "flex", justifyContent: "space-between", gap: 8 }}>
                <b>{probe.name === "database" ? "Base de données" : probe.name === "migrations" ? "Schéma (migrations)" : probe.name}</b>
                <Badge tone={PROBE_TONE[probe.status]}>{PROBE_LABEL[probe.status]}</Badge>
              </div>
              <div style={muted}>{probe.description ?? "—"} · {probe.durationMs} ms</div>
            </div>
          ))}
        </div>
        <div style={{ ...muted, marginTop: 8 }}>
          Sondes publiques pour l'orchestrateur : <code>/health/live</code> (le processus répond) et <code>/health/ready</code> (base
          joignable et schéma conforme).
        </div>
      </Card>

      <MigrationsCard p={p} />

      <Card>
        <SectionLabel>Configuration de production</SectionLabel>
        <ChecksTable checks={p.checks} />
      </Card>
    </div>
  );
}

function MigrationsCard({ p }: { p: ProductionReadiness }) {
  const m = p.migrations;
  if (!m) return <Card><SectionLabel>Migrations</SectionLabel><div className="nx-error-text">État des migrations illisible (base injoignable).</div></Card>;
  const ok = m.pending.length === 0 && m.drifted.length === 0 && m.unknown.length === 0;
  return (
    <Card>
      <SectionLabel>Migrations</SectionLabel>
      <div style={{ display: "flex", flexWrap: "wrap", gap: "6px 24px", alignItems: "baseline" }}>
        <Badge tone={m.pending.length > 0 ? "error" : ok ? "success" : "warning"}>
          {m.pending.length > 0 ? `${m.pending.length} en attente` : ok ? "à jour" : "écart à examiner"}
        </Badge>
        <span>{m.applied} appliquée(s), dernière <b>{m.latestApplied ?? "—"}</b></span>
        <span style={muted}>{m.embedded} livrée(s) avec cette version, dernière {m.latestEmbedded ?? "—"}</span>
      </div>
      {m.pending.length > 0 && (
        <div style={{ marginTop: 8 }}>
          <div className="nx-error-text">En attente — l'instance n'est pas prête : lancer <code>migrate</code> (runbook, déploiement).</div>
          <ul style={{ margin: "4px 0 0 18px" }}>{m.pending.map((f) => <li key={f}><code>{f}</code></li>)}</ul>
        </div>
      )}
      {m.drifted.length > 0 && (
        <div style={{ marginTop: 8 }}>
          <div style={{ color: "var(--nx-warning)" }}>Modifiée(s) depuis leur application — une migration appliquée ne se modifie jamais :</div>
          <ul style={{ margin: "4px 0 0 18px" }}>{m.drifted.map((f) => <li key={f}><code>{f}</code></li>)}</ul>
        </div>
      )}
      {m.unknown.length > 0 && (
        <div style={{ marginTop: 8, color: "var(--nx-warning)" }}>
          Base plus récente que l'application (retour arrière ?) : {m.unknown.join(", ")}
        </div>
      )}
    </Card>
  );
}

function ChecksTable({ checks }: { checks: ProductionCheck[] }) {
  const order = { fail: 0, warn: 1, pass: 2 } as const;
  const sorted = [...checks].sort((a, b) => order[a.status] - order[b.status]);
  return (
    <div style={{ display: "grid", gap: 8 }}>
      {sorted.map((c) => (
        <div key={c.key} style={{ display: "grid", gridTemplateColumns: "auto minmax(0, 1fr)", gap: "2px 10px", alignItems: "baseline",
          borderTop: "1px solid var(--nx-surface-2)", paddingTop: 8 }}>
          <Badge tone={CHECK_TONE[c.status]}>{CHECK_LABEL[c.status]}</Badge>
          <div>
            <b>{c.label}</b> <span style={muted}>· {CATEGORY_LABEL[c.category] ?? c.category}</span>
            <div style={{ ...muted, overflowWrap: "anywhere" }}>{c.detail}</div>
          </div>
        </div>
      ))}
    </div>
  );
}
