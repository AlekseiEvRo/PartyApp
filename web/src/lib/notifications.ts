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

export function showBrowserNotification(title: string, options?: NotificationOptions) {
    if (!isNotificationSupported()) return;
    if (Notification.permission !== 'granted') return;

    try {
        const notification = new Notification(title, {
            icon: '/icon-192.png',
            badge: '/icon-192.png',
            ...options
        });

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