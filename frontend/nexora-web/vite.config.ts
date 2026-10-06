/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: {
    port: 5173,
    // Suite E2E (finalisation phase 19) : ses rapports et traces ne doivent pas recharger la page pendant les tests.
    watch: { ignored: ["**/e2e/**", "**/e2e-report/**", "**/e2e-results/**"] },
    proxy: {
      // NEXORA_API_PROXY : viser une autre instance de l'API (ex. assistant d'installation en recette).
      "/api": { target: process.env.NEXORA_API_PROXY ?? "http://localhost:5080", changeOrigin: true },
    },
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
    restoreMocks: true,
  },
});
