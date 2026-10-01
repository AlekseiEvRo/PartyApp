import { registerSW } from 'virtual:pwa-register';

let reloading = false;

/**
 * Регистрирует service worker и следит за обновлениями.
 * Обновления применяются автоматически: новый SW активируется, страница
 * перезагружается. Проверка запускается при старте и далее раз в минуту,
 * так что открытая админка подтягивает новую версию сама.
 */
export function initPwa(): void {
    if (!import.meta.env.PROD) return;

    const hadController = 'serviceWorker' in navigator && navigator.serviceWorker.controller !== null;

    registerSW({
        immediate: true,
        onRegisteredSW(_swUrl, registration) {
            if (!registration) return;

            void registration.update();
            setInterval(() => void registration.update(), 60_000);
        }
    });

    // Страховка: если управление перешло новому SW, а Workbox не перезагрузил —
    // перезагружаемся сами (один раз, без цикла). На первом запуске не трогаем.
    navigator.serviceWorker?.addEventListener('controllerchange', () => {
        if (!hadController || reloading) return;

        reloading = true;
        window.location.reload();
    });
}

/**
 * Жёсткий сброс: снимает service worker, чистит все кэши и перезагружает
 * страницу. Нужен, если приложение «залипло» на старой версии (например,
 * в PWA на iOS, где нет DevTools).
 */
export async function forceRefreshApp(): Promise<void> {
    try {
        if ('serviceWorker' in navigator) {
            const registrations = await navigator.serviceWorker.getRegistrations();
            await Promise.all(registrations.map((registration) => registration.unregister()));
        }

        if ('caches' in window) {
            const keys = await caches.keys();
            await Promise.all(keys.map((key) => caches.delete(key)));
        }
    } catch (e) {
        console.warn('Не удалось полностью очистить кэш приложения', e);
    } finally {
        window.location.reload();
    }
}