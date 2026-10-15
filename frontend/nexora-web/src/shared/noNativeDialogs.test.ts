import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";

/** Plan maître §45 : jamais de window.prompt / confirm / alert — des fenêtres de l'application (finalisation phase 20). */
describe("aucune boîte native du navigateur dans l'interface", () => {
  it("aucun appel à prompt(), confirm() ou alert() dans src/", () => {
    const root = resolve(process.cwd(), "src");
    const offenders: string[] = [];
    const walk = (dir: string) => {
      for (const name of readdirSync(dir)) {
        const path = join(dir, name);
        if (statSync(path).isDirectory()) { walk(path); continue; }
        if (!/\.(ts|tsx)$/.test(name) || /\.test\.tsx?$/.test(name)) continue;
        readFileSync(path, "utf8").split("\n").forEach((line, i) => {
          if (/(^|[^.\w])(prompt|confirm|alert)\(/.test(line) || /window\.(prompt|confirm|alert)\(/.test(line))
            offenders.push(`${relative(root, path)}:${i + 1}`);
        });
      }
    };
    walk(root);
    expect(offenders).toEqual([]);
  }, 30_000);   // lecture de tout src/ (OneDrive) : 0,9 s seul, plus de 5 s quand la suite complète tourne en parallèle
});
