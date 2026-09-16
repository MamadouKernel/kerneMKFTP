// Service Worker kernelMK — reçoit les notifications Web Push envoyées par le serveur (même onglet fermé)
// et gère le clic dessus pour ramener l'utilisateur sur l'application. Servi depuis la racine de wwwroot
// (pas /js/) pour avoir la portée ("scope") la plus large possible sur le site.

self.addEventListener('install', function (event) {
    self.skipWaiting();
});

self.addEventListener('activate', function (event) {
    event.waitUntil(self.clients.claim());
});

self.addEventListener('push', function (event) {
    if (!event.data) return;

    let payload;
    try {
        payload = event.data.json();
    } catch {
        payload = { title: 'kernelMK', body: event.data.text() };
    }

    const title = payload.title || 'kernelMK';
    const options = {
        body: payload.body || '',
        icon: '/favicon.png',
        badge: '/favicon.png',
        tag: payload.tag || 'kernelmk-notification',
        data: { url: payload.url || '/' }
    };

    event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    const targetUrl = (event.notification.data && event.notification.data.url) || '/';

    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clientList) {
            for (const client of clientList) {
                if (client.url.includes(self.location.origin) && 'focus' in client) {
                    client.navigate(targetUrl);
                    return client.focus();
                }
            }
            if (self.clients.openWindow) {
                return self.clients.openWindow(targetUrl);
            }
        })
    );
});
