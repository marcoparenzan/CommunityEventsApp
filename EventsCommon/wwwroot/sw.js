// Service worker minimo, condiviso dalle due app (ognuna lo serve dal proprio indirizzo).
// L'app è una Blazor Server: senza connessione non può funzionare. Il service worker serve a rendere l'app installabile,
// a velocizzare i file statici e a mostrare una pagina chiara quando manca la rete. Non memorizza mai pagine,
// risposte delle API o dati degli utenti.
const VERSION = "v1";
const STATIC_CACHE = `1nn0va-static-${VERSION}`;
const OFFLINE_URL = "/offline.html";

self.addEventListener("install", event => {
    event.waitUntil(
        caches.open(STATIC_CACHE)
            .then(cache => cache.addAll([OFFLINE_URL, "/img/1nn0va.png"]))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener("activate", event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(keys.filter(key => key !== STATIC_CACHE).map(key => caches.delete(key))))
            .then(() => self.clients.claim())
    );
});

// Percorsi che non vanno mai toccati: connessione Blazor, API, file dinamici.
const NEVER_CACHE = [/^\/_blazor/, /^\/_framework/, /^\/api\//, /^\/media\//, /^\/admin\//, /^\/checkin\//];

self.addEventListener("fetch", event => {
    const request = event.request;
    if (request.method !== "GET") {
        return;
    }

    const url = new URL(request.url);
    if (url.origin !== self.location.origin || NEVER_CACHE.some(pattern => pattern.test(url.pathname))) {
        return;
    }

    // Pagine: sempre dalla rete; senza rete, la pagina "offline".
    if (request.mode === "navigate") {
        event.respondWith(
            fetch(request).catch(() => caches.match(OFFLINE_URL))
        );
        return;
    }

    // File statici (stili, script, font, immagini): dalla cache e aggiornati in sottofondo.
    if (["style", "script", "font", "image"].includes(request.destination)) {
        event.respondWith(
            caches.open(STATIC_CACHE).then(cache =>
                cache.match(request).then(cached => {
                    const network = fetch(request)
                        .then(response => {
                            if (response.ok) {
                                cache.put(request, response.clone());
                            }
                            return response;
                        })
                        .catch(() => cached);
                    return cached || network;
                })
            )
        );
    }
});
