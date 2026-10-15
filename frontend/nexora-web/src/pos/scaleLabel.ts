/**
 * Étiquette de balance à poids intégré (audit 2026-10, A-015) — même décodage que le serveur (`ScaleLabel.cs`) :
 * EAN-13 commençant par 2, les 7 premiers chiffres désignent l'article (code-barres de l'article marqué « poids
 * intégré »), les 5 suivants le poids en grammes, le dernier la clé de contrôle.
 */
export function decodeScaleLabel(code: string): { prefix: string; kilograms: number } | null {
  const c = code.trim();
  if (!/^2\d{12}$/.test(c)) return null;
  let sum = 0;
  for (let i = 0; i < 12; i++) sum += Number(c[i]) * (i % 2 === 0 ? 1 : 3);
  if ((10 - (sum % 10)) % 10 !== Number(c[12])) return null;
  const grams = Number(c.slice(7, 12));
  return grams > 0 ? { prefix: c.slice(0, 7), kilograms: grams / 1000 } : null;
}
