/**
 * QR-код сгенерирован как ссылка `/?qr=КОД`. При открытии приложения код
 * перехватывается, убирается из адресной строки и живёт в sessionStorage,
 * пока Party.svelte не проверит активные ивенты: во время ивента «Охота
 * за QR-кодами» код активируется, вне ивента — сбрасывается (активация
 * возможна только во время ивента).
 */

const STORAGE_KEY = 'party_pending_qr';

/** Коды сервера: 6 символов без похожих (0/O, 1/I), но допускаем запас. */
const CODE_PATTERN = /^[A-Z0-9]{4,12}$/;

/** Читает `?qr=КОД`, запоминает и очищает параметр в адресной строке. */
export function captureQrParam(): void {
    try {
        const params = new URLSearchParams(window.location.search);
        const raw = params.get('qr');
        if (!raw) return;

        const code = raw.trim().toUpperCase();
        if (CODE_PATTERN.test(code)) {
            sessionStorage.setItem(STORAGE_KEY, code);
        }

        // Параметр больше не нужен: обычное обновление страницы не должно
        // повторять активацию
        params.delete('qr');
        const query = params.toString();
        window.history.replaceState(
            null,
            '',
            `${window.location.pathname}${query ? `?${query}` : ''}${window.location.hash}`
        );
    } catch {
        // sessionStorage может быть недоступен (приватный режим) — молча пропускаем
    }
}

/** Код, ожидающий активации, либо null. */
export function getPendingQrCode(): string | null {
    try {
        return sessionStorage.getItem(STORAGE_KEY);
    } catch {
        return null;
    }
}

/** Убирает ожидающий код после успешной активации или осмысленной ошибки. */
export function clearPendingQrCode(): void {
    try {
        sessionStorage.removeItem(STORAGE_KEY);
    } catch {
        // ignore
    }
}
