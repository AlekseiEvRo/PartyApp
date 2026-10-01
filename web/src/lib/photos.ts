import { getToken } from './api';

/** Максимальная сторона при сжатии: больше для телефона и не нужно. */
const MAX_DIMENSION = 2048;
const JPEG_QUALITY = 0.85;

export interface UploadedPhoto {
    id: string;
    status: string;
}

/**
 * Сжимает фото на клиенте и загружает его на сервер.
 * Сжатие экономит трафик и избавляет сервер от тяжёлых файлов.
 */
export async function uploadPhoto(file: File): Promise<UploadedPhoto> {
    const blob = await compressImage(file);
    const token = getToken();

    const form = new FormData();
    form.append('file', blob, 'photo.jpg');

    const res = await fetch('/api/photos', {
        method: 'POST',
        headers: token ? { Authorization: `Bearer ${token}` } : {},
        body: form
    });

    if (!res.ok) {
        const err = await res.json().catch(() => ({}) as { error?: string });
        throw new Error(err.error || `HTTP ${res.status}`);
    }

    return res.json();
}

/**
 * Забирает картинку с авторизацией и возвращает blob-URL.
 * <img> не умеет слать Authorization, поэтому качаем через fetch.
 */
const urlCache = new Map<string, string>();

export async function loadPhotoUrl(photoId: string): Promise<string> {
    const cached = urlCache.get(photoId);
    if (cached) return cached;

    const token = getToken();
    const res = await fetch(`/api/photos/${photoId}/content`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {}
    });

    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const url = URL.createObjectURL(await res.blob());
    urlCache.set(photoId, url);
    return url;
}

/**
 * Сбрасывает кэш картинки: при повторном появлении (например, фото
 * отклонили, а потом снова одобрили) она скачается заново.
 */
export function invalidatePhotoUrl(photoId: string): void {
    const url = urlCache.get(photoId);
    if (url) {
        URL.revokeObjectURL(url);
        urlCache.delete(photoId);
    }
}

export function releasePhotoUrls(): void {
    for (const url of urlCache.values()) {
        URL.revokeObjectURL(url);
    }
    urlCache.clear();
}

async function compressImage(file: File): Promise<Blob> {
    const image = await loadImage(file);

    const scale = Math.min(1, MAX_DIMENSION / Math.max(image.width, image.height));
    const width = Math.max(1, Math.round(image.width * scale));
    const height = Math.max(1, Math.round(image.height * scale));

    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;

    const context = canvas.getContext('2d');
    if (!context) throw new Error('Браузер не поддерживает обработку изображений');

    // Браузеры сами применяют EXIF-поворот при отрисовке изображения
    context.drawImage(image, 0, 0, width, height);

    return new Promise<Blob>((resolve, reject) => {
        canvas.toBlob(
            (blob) => (blob ? resolve(blob) : reject(new Error('Не удалось сжать фото'))),
            'image/jpeg',
            JPEG_QUALITY
        );
    });
}

function loadImage(file: File): Promise<HTMLImageElement> {
    return new Promise((resolve, reject) => {
        const url = URL.createObjectURL(file);
        const image = new Image();

        image.onload = () => {
            URL.revokeObjectURL(url);
            resolve(image);
        };

        image.onerror = () => {
            URL.revokeObjectURL(url);
            reject(new Error('Не удалось прочитать изображение'));
        };

        image.src = url;
    });
}