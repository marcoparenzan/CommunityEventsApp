// Registra il service worker (serve l'HTTPS, o localhost in sviluppo). Senza, il sito funziona comunque.
if ("serviceWorker" in navigator && (location.protocol === "https:" || location.hostname === "localhost")) {
    window.addEventListener("load", () => {
        navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(() => { /* niente PWA, nessun problema */ });
    });
}
