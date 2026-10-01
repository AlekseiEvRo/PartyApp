import { api } from './api';

export type PushUnavailableReason =
    | 'insecure'
    | 'no-service-worker'
    | 'no-notification'
    | 'no-push-manager'
    | 'ios-not-installed';

export interface PushSupport {
    supported: boolean;
    reason?: PushUnavailableReason;
    ios: boolean;
    standalone: boolean;
}

export interface PushActionResult {
    ok: boolean;
    error?: string;
}

export interface PushReport {
    sent: number;
    failed: number;
    removed: number;
}

export function isIos(): boolean {
    return /iPad|iPhone|iPod/.test(navigator.userAgent)
        || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

export function isStandalone(): boolean {
    return window.matchMedia('(display-mode: standalone)').matches
        || (window.navigator as unknown as { standalone?: boolean }).standalone === true;
}

export function getPushSupport(): PushSupport {
    const ios = isIos();
    const standalone = isStandalone();

    if (!window.isSecureContext) {
        return { supported: false, reason: 'insecure', ios, standalone };
    }

    if (!('serviceWorker' in navigator)) {
        return { supported: false, reason: 'no-service-worker', ios, standalone };
    }

    if (!('Notification' in window)) {
        return { supported: false, reason: 'no-notification', ios, standalone };
    }

    if (!('PushManager' in window)) {
        return { supported: false, reason: 'no-push-manager', ios, standalone };
    }

    // На iOS push работает только в приложении, добавленном на экран «Домой»
    if (ios && !standalone) {
        return { supported: false, reason: 'ios-not-installed', ios, standalone };
    }

    return { supported: true, ios, standalone };
}

export function describeUnavailable(reason?: PushUnavailableReason): string {
    switch (reason) {
        case 'insecure':
            return 'Уведомления работают только по HTTPS — открой сайт по защищённому адресу.';
        case 'no-service-worker':
            return 'Браузер не поддерживает service worker.';
        case 'no-notification':
            return 'Браузер не поддерживает уведомления.';
        case 'no-push-manager':
            return 'Браузер не поддерживает push-уведомления.';
        case 'ios-not-installed':
            return 'На iPhone уведомления приходят только приложению, добавленному на экран «Домой».';
        default:
            return 'Уведомления недоступны.';
    }
}

export function getPermission(): NotificationPermission | 'unsupported' {
    return 'Notification' in window ? Notification.permission : 'unsupported';
}

let cachedPublicKey: string | null = null;

export async function getVapidPublicKey(): Promise<string | null> {
    if (cachedPublicKey) return cachedPublicKey;

    try {
        const res = await fetch('/api/push/vapid-public-key');
        if (!res.ok) return null;

        const data = await res.json() as { publicKey?: string };
        cachedPublicKey = data.publicKey ?? null;
        return cachedPublicKey;
    } catch {
        return null;
    }
}

export async function getExistingSubscription(): Promise<PushSubscription | null> {
    const registration = await getReadyRegistration(2000);
    if (!registration) return null;

    try {
        return await registration.pushManager.getSubscription();
    } catch {
        return null;
    }
}

/**
 * Включает уведомления: запрашивает разрешение, создаёт подписку и сохраняет её на сервере.
 * Вызывать из обработчика клика — iOS требует жест пользователя.
 */
export async function enablePush(): Promise<PushActionResult> {
    const support = getPushSupport();
    if (!support.supported) {
        return { ok: false, error: describeUnavailable(support.reason) };
    }

    const permission = await Notification.requestPermission();
    if (permission !== 'granted') {
        return { ok: false, error: 'Разрешение на уведомления не выдано' };
    }

    const registration = await getReadyRegistration();
    if (!registration) {
        return { ok: false, error: 'Service worker ещё не готов — перезапусти приложение с иконки' };
    }

    const publicKey = await getVapidPublicKey();
    if (!publicKey) {
        return { ok: false, error: 'Push-уведомления не настроены на сервере' };
    }

    try {
        let subscription = await registration.pushManager.getSubscription();
        if (!subscription) {
            subscription = await registration.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: urlBase64ToUint8Array(publicKey)
            });
        }

        await saveSubscription(subscription);
        return { ok: true };
    } catch (e) {
        return { ok: false, error: e instanceof Error ? e.message : 'Не удалось включить уведомления' };
    }
}

/**
 * Привязывает подписку устройства к текущему пользователю.
 * Вызывается при входе: телефон один, а логиниться могут разные люди.
 */
export async function syncPushSubscription(): Promise<void> {
    const support = getPushSupport();
    if (!support.supported || Notification.permission !== 'granted') return;

    try {
        const registration = await getReadyRegistration(5000);
        if (!registration) return;

        let subscription = await registration.pushManager.getSubscription();

        // Разрешение есть, а подписки нет (её могла сбросить ОС) — создаём заново
        if (!subscription) {
            const publicKey = await getVapidPublicKey();
            if (!publicKey) return;

            subscription = await registration.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: urlBase64ToUint8Array(publicKey)
            });
        }

        await saveSubscription(subscription);
    } catch (e) {
        console.warn('Не удалось синхронизировать push-подписку', e);
    }
}

export async function disablePush(): Promise<PushActionResult> {
    const registration = await getReadyRegistration(3000);

    try {
        const subscription = registration ? await registration.pushManager.getSubscription() : null;

        if (subscription) {
            await api('/api/push/unsubscribe', 'POST', { endpoint: subscription.endpoint }).catch(() => undefined);
            await subscription.unsubscribe();
        }

        return { ok: true };
    } catch {
        return { ok: false, error: 'Не удалось отключить уведомления' };
    }
}

/**
 * Отвязывает подписку устройства от текущего пользователя на сервере,
 * не удаляя её в браузере. Вызывается при выходе: телефон один,
 * а логиниться могут разные люди, чужие уведомления приходить не должны.
 */
export async function detachPushSubscription(): Promise<void> {
    if (getPermission() !== 'granted') return;

    try {
        const subscription = await getExistingSubscription();
        if (!subscription) return;

        await api('/api/push/unsubscribe', 'POST', { endpoint: subscription.endpoint })
            .catch(() => undefined);
    } catch {
        // Выход не должен блокироваться проблемами с push-подпиской
    }
}

export async function sendTestPush(): Promise<PushReport> {
    return api<PushReport>('/api/push/test', 'POST');
}

function saveSubscription(subscription: PushSubscription): Promise<unknown> {
    const json = subscription.toJSON();

    return api('/api/push/subscribe', 'POST', {
        endpoint: subscription.endpoint,
        p256dh: json.keys?.p256dh ?? '',
        auth: json.keys?.auth ?? '',
        userAgent: navigator.userAgent
    });
}

async function getReadyRegistration(timeoutMs = 8000): Promise<ServiceWorkerRegistration | null> {
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

function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
    const padding = '='.repeat((4 - (base64.length % 4)) % 4);
    const normalized = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(normalized);

    const bytes = new Uint8Array(new ArrayBuffer(raw.length));
    for (let i = 0; i < raw.length; i++) {
        bytes[i] = raw.charCodeAt(i);
    }

    return bytes;
}