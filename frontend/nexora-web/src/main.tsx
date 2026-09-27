import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router-dom";
import { AuthProvider } from "@/auth/AuthContext";
import { PreferencesProvider } from "@/shared/PreferencesContext";
import { ToastProvider } from "@/shared/Toast";
import { router } from "@/app/router";
import "@/shared/theme.css";

/* Applique le thème mis en cache avant la première peinture (évite le flash — §41). */
try {
  const cached = JSON.parse(localStorage.getItem("nexora.prefs") ?? "{}");
  const root = document.documentElement;
  root.dataset.theme = cached.theme ?? "system";
  root.dataset.density = cached.density ?? "comfortable";
  root.dataset.motion = cached.reducedMotion ? "reduced" : "full";
} catch {
  /* premières visites : valeurs par défaut du CSS */
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <AuthProvider>
      <PreferencesProvider>
        <ToastProvider>
          <RouterProvider router={router} />
        </ToastProvider>
      </PreferencesProvider>
    </AuthProvider>
  </StrictMode>,
);
