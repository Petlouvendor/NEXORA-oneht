/*
 * Service worker de Nexora One (finalisation, phase 6) — application installable qui se rouvre sans réseau.
 *
 * - Pages (navigation) : réseau d'abord, repli sur la coque de l'application mise en cache.
 * - Fichiers de l'application (/assets/* horodatés, icônes, manifeste) : cache d'abord, complété au fil de l'eau.
 * - API (/api/*) : JAMAIS mise en cache. Les données hors ligne passent par le coffre chiffré du navigateur et
 *   la file d'attente de la caisse ; une réponse d'API servie depuis un cache serait une donnée périmée
 *   présentée comme actuelle.
 */
const CACHE = "nexora-shell-v1";
const SHELL = ["/", "/index.html", "/manifest.webmanifest", "/icons/icon-192.png", "/icons/icon-512.png"];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin || url.pathname.startsWith("/api/")) return;

  if (req.mode === "navigate") {
    event.respondWith(
      fetch(req)
        .then((res) => {
          if (res.ok) {
            const copy = res.clone();
            caches.open(CACHE).then((c) => c.put("/index.html", copy));
          }
          return res;
        })
        .catch(() => caches.match("/index.html").then((r) => r ?? Response.error())),
    );
    return;
  }

  event.respondWith(
    caches.match(req).then((cached) => cached ?? fetch(req).then((res) => {
      if (res.ok && (url.pathname.startsWith("/assets/") || url.pathname.startsWith("/icons/") || url.pathname === "/manifest.webmanifest")) {
        const copy = res.clone();
        caches.open(CACHE).then((c) => c.put(req, copy));
      }
      return res;
    })),
  );
});
