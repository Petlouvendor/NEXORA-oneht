# NEXORA — Frontend (React + TypeScript)

Vite + React 19 + React Router 7. Structure conforme à la Phase 3 §7.

```bash
cd frontend/nexora-web
npm install
npm run dev        # http://localhost:5173  (proxy /api -> http://localhost:5080)
npm run build
npm run typecheck
npm test           # Vitest (jsdom)
```

## Structure (`src/`)

| Dossier | Rôle |
|---|---|
| `app/` | Bootstrap, routeur, `AppLayout` (sidebar premium + sélecteur d'entreprise) |
| `core/` | `apiClient` (bearer, `X-Company-Id`, `X-Correlation-Id`, erreurs RFC 7807), `config` |
| `auth/` | `AuthContext`, `ProtectedRoute`, `LoginPage`, types |
| `organization/` | Contexte / API entreprise |
| `dashboard/` | Command Center (squelette) |
| `pos/`, `catalog/`, `inventory/`, `purchasing/`, `sales/`, `finance/`, `accounting/`, `reports/`, `notifications/`, `settings/` | Modules métier (structure en place) |
| `shared/` | Tokens du Design System (`theme.css`), composants communs |

## Principes appliqués

- **Routes protégées** : `<ProtectedRoute permission="…">` — redirection `/login`, blocage par permission.
- **Contexte d'entreprise actif** : `AuthContext.activeCompanyId` persisté (localStorage), envoyé en
  `X-Company-Id` sur chaque requête — l'autorisation reste côté serveur (§12).
- **Validation serveur obligatoire** : le client valide l'UX, jamais la sécurité.
- **Design System** : couleurs / typo via les variables CSS de `shared/theme.css`
  (`--nx-brand: #00F5D4`, fond `#07100F`…).
