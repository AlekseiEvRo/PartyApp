import { getToken } from './api';

/** Максимальная сторона аватара после сжатия: больше на телефоне не нужно. */
const AVATAR_SIZE = 512;
const JPEG_QUALITY = 0.9;

const urlCache = new Map<string, string>();

/**
 * Забирает аватар с авторизацией и возвращает blob-URL.
 * Ключ кэша включает версию профиля, поэтому старые картинки не залипают.
 */
export async function loadAvatarUrl(userId: string, version?: string | null): Promise<string | null> {
    const key = `${userId}:${version ?? ''}`;
    const cached = urlCache.get(key);
    if (cached) return cached;

    const token = getToken();
    const res = await fetch(`/api/users/${userId}/avatar`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {}
    });

    if (!res.ok) return null;

    // Старые версии аватара этого же игрока больше не нужны
    for (const [existingKey, url] of urlCache) {
        if (existingKey.startsWith(`${userId}:`) && existingKey !== key) {
            URL.revokeObjectURL(url);
            urlCache.delete(existingKey);
        }
    }

    const url = URL.createObjectURL(await res.blob());
    urlCache.set(key, url);
    return url;
}

/** Сжимает фото в квадрат и загружает как аватар. */
export async function uploadAvatar(file: File): Promise<void> {
    const blob = await resizeToSquare(file, AVATAR_SIZE);

    const token = getToken();
    const form = new FormData();
    form.append('file', blob, 'avatar.jpg');

    const res = await fetch('/api/profile/avatar', {
        method: 'POST',
        headers: token ? { Authorization: `Bearer ${token}` } : {},
        body: form
    });

    if (!res.ok) {
        const err = await res.json().catch(() => ({}) as { error?: string });
        throw new Error(err.error || `HTTP ${res.status}`);
    }
}

export async function deleteAvatar(): Promise<void> {
    const token = getToken();
    const res = await fetch('/api/profile/avatar', {
        method: 'DELETE',
        headers: token ? { Authorization: `Bearer ${token}` } : {}
    });

    if (!res.ok) throw new Error(`HTTP ${res.status}`);
}

export function releaseAvatarUrls(): void {
    for (const url of urlCache.values()) {
        URL.revokeObjectURL(url);
    }
    urlCache.clear();
}

function resizeToSquare(file: File, size: number): Promise<Blob> {
    return new Promise<Blob>((resolve, reject) => {
        const url = URL.createObjectURL(file);
        const image = new Image();

        image.onload = () => {
            URL.revokeObjectURL(url);

            const side = Math.min(image.width, image.height);
            const sourceX = Math.round((image.width - side) / 2);
            const sourceY = Math.round((image.height - side) / 2);

            const canvas = document.createElement('canvas');
            canvas.width = size;
            canvas.height = size;

            const context = canvas.getContext('2d');
            if (!context) {
                reject(new Error('Браузер не поддерживает обработку изображений'));
                return;
            }

            // Браузеры сами применяют EXIF-поворот при отрисовке
            context.drawImage(image, sourceX, sourceY, side, side, 0, 0, size, size);

            canvas.toBlob(
                (blob) => (blob ? resolve(blob) : reject(new Error('Не удалось сжать аватар'))),
                'image/jpeg',
                JPEG_QUALITY
            );
        };

        image.onerror = () => {
            URL.revokeObjectURL(url);
            reject(new Error('Не удалось прочитать изображение'));
        };

        image.src = url;
    });
}
