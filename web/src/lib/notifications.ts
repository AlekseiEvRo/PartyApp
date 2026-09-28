export function isNotificationSupported(): boolean {
    return 'Notification' in window;
}

export function isSecureContext(): boolean {
    return window.isSecureContext;
}

export function canRequestPermission(): boolean {
    return isNotificationSupported() && isSecureContext();
}

export async function requestNotificationPermission(): Promise<NotificationPermission | 'unsupported'> {
    if (!isNotificationSupported()) return 'unsupported';

    if (!isSecureContext()) {
        console.warn('Уведомления требуют HTTPS');
        return 'denied';
    }

    return Notification.requestPermission();
}

/**
 * Показывает уведомление средствами service worker (на iOS обычный `new Notification` не работает).
 * Вызывается только когда приложение на экране: в фоне уведомление придёт через push.
 */
export async function showBrowserNotification(title: string, options?: NotificationOptions): Promise<void> {
    if (!isNotificationSupported()) return;
    if (Notification.permission !== 'granted') return;
    if (document.visibilityState !== 'visible') return;

    const notificationOptions: NotificationOptions = {
        icon: '/icon-192.png',
        badge: '/icon-192.png',
        ...options
    };

    const registration = await getRegistration();

    if (registration) {
        try {
            await registration.showNotification(title, notificationOptions);
            return;
        } catch (e) {
            console.warn('Service worker не смог показать уведомление:', e);
        }
    }

    // Запасной вариант для браузеров без service worker
    try {
        const notification = new Notification(title, notificationOptions);

        notification.onclick = () => {
            window.focus();
            notification.close();
        };

        // Автоматически закрываем через 5 секунд
        setTimeout(() => notification.close(), 5000);
    } catch (e) {
        console.error('Не удалось показать уведомление:', e);
    }
}

async function getRegistration(timeoutMs = 1500): Promise<ServiceWorkerRegistration | null> {
    if (!('serviceWorker' in navigator)) return null;

    try {
        return await Promise.race([
            navigator.serviceWorker.ready,
            new Promise<null>((resolve) => setTimeout(() => resolve(null), timeoutMs))
        ]);
    } catch {
        return null;
    }
}