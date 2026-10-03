// Обработчик push-уведомлений.
// Подключается в сгенерированный service worker (workbox.importScripts в vite.config.ts).

self.addEventListener('push', (event) => {
    event.waitUntil(handlePush(event));
});

async function handlePush(event) {
    // Если приложение открыто на экране, оно само покажет событие — не дублируем
    const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const hasVisibleClient = clients.some((client) => client.visibilityState === 'visible');
    if (hasVisibleClient) return;

    let data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch {
        data = { title: 'PartyApp', body: event.data ? event.data.text() : '' };
    }

    await self.registration.showNotification(data.title || 'PartyApp', {
        body: data.body || undefined,
        icon: '/icon-192.png',
        badge: '/icon-monochrome.png',
        tag: data.tag || undefined,
        data: { url: data.url || '/' }
    });
}

self.addEventListener('notificationclick', (event) => {
    event.notification.close();

    const targetUrl = (event.notification.data && event.notification.data.url) || '/';

    event.waitUntil((async () => {
        const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });

        // Если приложение уже открыто — фокусируем его, иначе открываем новое окно
        const existing = clients.find((client) => client.url.startsWith(self.location.origin));
        if (existing) {
            await existing.focus();
            return;
        }

        await self.clients.openWindow(targetUrl);
    })());
});
