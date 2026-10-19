// ESLint (analyse exhaustive 2026-10, §7) : règles des hooks React, que le code croyait déjà vérifiées
// (directives « eslint-disable » sans effet faute d'ESLint installé). Le typage reste vérifié par tsc.
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";

export default tseslint.config(
  { ignores: ["dist/**", "coverage/**", "node_modules/**", "playwright-report/**", "test-results/**"] },
  {
    files: ["src/**/*.{ts,tsx}"],
    // Analyse typée (audit 2026-10 éd. 2) : une promesse ni attendue ni gérée perd son erreur en silence (envoi qui
    // échoue sans message, état jamais remis à jour).
    languageOptions: {
      parser: tseslint.parser,
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    plugins: { "react-hooks": reactHooks, "@typescript-eslint": tseslint.plugin },
    linterOptions: { reportUnusedDisableDirectives: "error" },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "error",
      "@typescript-eslint/no-floating-promises": "error",
    },
  },
  {
    // Scénarios Playwright : hors du tsconfig de l'application, sans analyse typée.
    files: ["e2e/**/*.ts"],
    languageOptions: { parser: tseslint.parser },
    plugins: { "react-hooks": reactHooks },
    linterOptions: { reportUnusedDisableDirectives: "error" },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "error",
    },
  },
);
