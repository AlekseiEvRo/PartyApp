/**
 * Синхронизация времени с сервером.
 *
 * Часы на телефонах могут расходиться на секунды, из-за чего таймеры
 * («Кто быстрее», тост, аукцион) показывали разное на телефонах и на
 * большом экране. Все отсчёты считаем через serverNow().
 */

let clockOffsetMs = 0;

/** Текущее серверное время в миллисекундах (локальные часы + поправка). */
export function serverNow(): number {
    return Date.now() + clockOffsetMs;
}

/** Спрашивает у сервера время и запоминает поправку к локальным часам. */
export async function syncServerTime(): Promise<void> {
    try {
        const res = await fetch('/api/time');
        if (!res.ok) return;

        const data = await res.json() as { serverTimeUtc: string };
        clockOffsetMs = new Date(data.serverTimeUtc).getTime() - Date.now();
    } catch {
        // Сеть недоступна — остаёмся на локальных часах
    }
}
