import { Component, type ErrorInfo, type ReactNode } from "react";

interface State { error: Error | null }

/**
 * Barrière d'erreur d'un écran (finalisation phase 20) : un écran qui ne se charge pas (hors ligne alors qu'il n'a
 * jamais été ouvert sur ce poste, déploiement en cours) ou qui échoue au rendu n'emporte plus toute l'application —
 * le menu reste utilisable et l'utilisateur peut réessayer. `resetKey` (l'adresse) efface l'erreur quand on change
 * d'écran.
 */
export class ScreenErrorBoundary extends Component<{ children: ReactNode; resetKey?: string }, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error("Écran en erreur :", error, info.componentStack);
  }

  componentDidUpdate(previous: { resetKey?: string }): void {
    if (this.state.error && previous.resetKey !== this.props.resetKey) this.setState({ error: null });
  }

  render(): ReactNode {
    if (!this.state.error) return this.props.children;
    const offline = typeof navigator !== "undefined" && !navigator.onLine;
    return (
      <div className="nx-card" role="alert" style={{ display: "grid", gap: 10, maxWidth: 640 }}>
        <h1 style={{ margin: 0, fontSize: "var(--nx-fs-lg, 20px)" }}>Cet écran n'a pas pu s'afficher</h1>
        <p style={{ margin: 0, color: "var(--nx-text-2)" }}>
          {offline
            ? "Vous êtes hors ligne et cet écran n'a pas encore été ouvert sur ce poste. La caisse reste disponible ; réessayez une fois la connexion revenue."
            : "Une erreur est survenue pendant son chargement. Réessayez ; si le problème persiste, notez l'heure et prévenez l'administrateur."}
        </p>
        <div>
          <button type="button" className="nx-btn" onClick={() => window.location.reload()}>Réessayer</button>
        </div>
      </div>
    );
  }
}
