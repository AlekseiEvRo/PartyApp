import { writable } from 'svelte/store';
import { registerSW } from 'virtual:pwa-register';

/** Стала доступна новая версия приложения — показываем баннер. */
export const needRefresh = writable(false);

let updateServiceWorker: ((reloadPage?: boolean) => Promise<void>) | null = null;

/**
 * Регистрирует service worker и следит за обновлениями.
 * Раз в минуту просит браузер проверить новую версию, а когда она найдена —
 * поднимает флаг needRefresh (баннер предложит обновиться).
 */
export function initPwa(): void {
    if (!import.meta.env.PROD) return;

    updateServiceWorker = registerSW({
        immediate: true,
        onNeedRefresh() {
            needRefresh.set(true);
        },
        onRegisteredSW(_swUrl, registration) {
            if (!registration) return;

            setInterval(() => void registration.update(), 60_000);
        }
    });
}

/** Плавное обновление: новый SW вступает в силу, страница перезагружается. */
export async function updateApp(): Promise<void> {
    needRefresh.set(false);

    if (updateServiceWorker) {
        await updateServiceWorker(true);
        return;
    }

    await forceRefreshApp();
}

export function dismissUpdate(): void {
    needRefresh.set(false);
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