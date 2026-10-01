<script lang="ts">
    import { onDestroy } from 'svelte';
    import { api } from '../api';
    import { photosVersion, photoRemovedId, showToast } from '../stores';
    import { loadPhotoUrl, invalidatePhotoUrl, releasePhotoUrls, uploadPhoto } from '../photos';

    interface Photo {
        id: string;
        uploadedByName: string;
        uploadedAt: string;
        status: string;
        likesCount: number;
        likedByMe: boolean;
        isMine: boolean;
    }

    interface PhotoPage {
        items: Photo[];
        total: number;
    }

    const pageSize = 24;

    let photos: Photo[] = [];
    let urls: Record<string, string> = {};
    let total = 0;
    let loading = true;
    let uploading = false;
    let error = '';
    let viewer: Photo | null = null;
    let lastVersion = -1;
    let lastRemovedId: string | null = null;

    $: if ($photosVersion !== lastVersion) {
        lastVersion = $photosVersion;
        void load();
    }

    // Фото удалили (или отозвали одобрение) — убираем из списка без перезагрузки
    $: if ($photoRemovedId && $photoRemovedId !== lastRemovedId) {
        lastRemovedId = $photoRemovedId;
        removeLocally($photoRemovedId);
    }

    onDestroy(releasePhotoUrls);

    async function load() {
        loading = true;
        error = '';

        try {
            const page = await api<PhotoPage>(`/api/photos?limit=${pageSize}&offset=0`);
            photos = page.items;
            total = page.total;
            await loadUrls(page.items);
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить фото';
        } finally {
            loading = false;
        }
    }

    async function loadMore() {
        loading = true;

        try {
            const page = await api<PhotoPage>(
                `/api/photos?limit=${pageSize}&offset=${photos.length}`
            );
            photos = [...photos, ...page.items];
            total = page.total;
            await loadUrls(page.items);
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось загрузить фото', 'error');
        } finally {
            loading = false;
        }
    }

    async function loadUrls(items: Photo[]) {
        const pairs = await Promise.all(
            items.map(async (photo) => {
                const url = await loadPhotoUrl(photo.id).catch(() => '');
                return [photo.id, url] as const;
            })
        );

        const next = { ...urls };
        for (const [id, url] of pairs) {
            if (url) next[id] = url;
        }
        urls = next;
    }

    async function onFileChange(event: Event) {
        const input = event.currentTarget as HTMLInputElement;
        const file = input.files?.[0];
        input.value = '';

        if (!file || uploading) return;

        uploading = true;

        try {
            const { status } = await uploadPhoto(file);
            showToast(status === 'Pending' ? '📸 Фото отправлено на модерацию' : '📸 Фото добавлено!');
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось загрузить фото', 'error');
        } finally {
            uploading = false;
        }
    }

    async function toggleLike(photo: Photo) {
        try {
            const result = await api<{ liked: boolean; likesCount: number }>(
                `/api/photos/${photo.id}/like`,
                'POST'
            );
            photos = photos.map((p) =>
                p.id === photo.id
                    ? { ...p, likedByMe: result.liked, likesCount: result.likesCount }
                    : p
            );
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось поставить лайк', 'error');
        }
    }

    async function remove(photo: Photo) {
        if (!confirm('Удалить фото?')) return;

        try {
            await api(`/api/photos/${photo.id}`, 'DELETE');
            removeLocally(photo.id);
            showToast('Фото удалено', 'info');
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось удалить фото', 'error');
        }
    }

    /** Убирает фото из открытого списка; повторный вызов (после SignalR) безопасен. */
    function removeLocally(id: string) {
        const existed = photos.some((p) => p.id === id);
        photos = photos.filter((p) => p.id !== id);

        if (existed) total = Math.max(0, total - 1);
        if (viewer?.id === id) viewer = null;
        invalidatePhotoUrl(id);
    }

    function formatTime(value: string): string {
        return new Date(value).toLocaleString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    }
</script>

<section class="gallery">
    <div class="section-header">
        <h2>📸 Фотоальбом</h2>

        <label class="upload-btn" class:busy={uploading}>
            {uploading ? 'Загружаем…' : '+ Добавить фото'}
            <input
                type="file"
                accept="image/*"
                on:change={onFileChange}
                disabled={uploading}
                hidden
            />
        </label>
    </div>

    {#if error}
        <p class="message error">{error}</p>
    {:else if loading && photos.length === 0}
        <p class="muted">Загружаем фото…</p>
    {:else if photos.length === 0}
        <p class="muted">Пока нет фото — добавь первое!</p>
    {:else}
        <div class="grid">
            {#each photos as photo (photo.id)}
                <figure class="tile">
                    {#if urls[photo.id]}
                        <button class="image-button" on:click={() => (viewer = photo)} aria-label="Открыть фото">
                            <img src={urls[photo.id]} alt="Фото от {photo.uploadedByName}" loading="lazy" />
                        </button>
                    {:else}
                        <div class="placeholder">…</div>
                    {/if}

                    {#if photo.status !== 'Approved'}
                        <span class="badge" class:pending={photo.status === 'Pending'}>
                            {photo.status === 'Pending' ? 'на модерации' : 'отклонено'}
                        </span>
                    {/if}

                    <div class="tile-actions">
                        <button
                            class="like"
                            class:liked={photo.likedByMe}
                            on:click={() => toggleLike(photo)}
                            title="Нравится"
                        >
                            {photo.likedByMe ? '❤️' : '🤍'} {photo.likesCount}
                        </button>

                        {#if photo.isMine}
                            <button class="delete" on:click={() => remove(photo)} title="Удалить фото">🗑</button>
                        {/if}
                    </div>

                    <figcaption class="meta">{photo.uploadedByName} · {formatTime(photo.uploadedAt)}</figcaption>
                </figure>
            {/each}
        </div>

        {#if photos.length < total}
            <button class="more" on:click={loadMore} disabled={loading}>
                {loading ? 'Загружаем…' : `Показать ещё (${total - photos.length})`}
            </button>
        {/if}
    {/if}
</section>

{#if viewer && urls[viewer.id]}
    <div class="viewer" on:click={() => (viewer = null)} role="presentation">
        <img src={urls[viewer.id]} alt="Фото от {viewer.uploadedByName}" />
        <button class="viewer-close" on:click={() => (viewer = null)} aria-label="Закрыть">✕</button>
    </div>
{/if}

<style>
    .gallery {
        margin-bottom: 24px;
    }

    .section-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        margin-bottom: 12px;
        flex-wrap: wrap;
    }

    .section-header h2 { margin: 0; }

    .upload-btn {
        background: var(--accent, #f5a623);
        color: #12122e;
        padding: 10px 16px;
        border-radius: 10px;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
        white-space: nowrap;
    }

    .upload-btn.busy { opacity: 0.6; cursor: default; }

    .grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
        gap: 10px;
    }

    .tile {
        position: relative;
        margin: 0;
        background: var(--bg-soft, #12122e);
        border-radius: 12px;
        overflow: hidden;
    }

    .image-button {
        display: block;
        width: 100%;
        padding: 0;
        border: none;
        background: none;
        cursor: pointer;
    }

    .tile img {
        display: block;
        width: 100%;
        aspect-ratio: 1;
        object-fit: cover;
    }

    .placeholder {
        width: 100%;
        aspect-ratio: 1;
        display: flex;
        align-items: center;
        justify-content: center;
        color: var(--muted, #aaa);
    }

    .badge {
        position: absolute;
        top: 8px;
        left: 8px;
        background: rgba(231, 76, 60, 0.9);
        color: #fff;
        font-size: 11px;
        padding: 2px 8px;
        border-radius: 10px;
    }

    .badge.pending { background: rgba(245, 166, 35, 0.9); color: #12122e; }

    .tile-actions {
        position: absolute;
        right: 6px;
        bottom: 26px;
        display: flex;
        gap: 4px;
    }

    .like, .delete {
        border: none;
        background: rgba(15, 15, 35, 0.75);
        color: #fff;
        border-radius: 8px;
        padding: 4px 8px;
        font-size: 13px;
        cursor: pointer;
    }

    .like.liked { background: rgba(231, 76, 60, 0.85); }

    .meta {
        position: absolute;
        left: 0;
        right: 0;
        bottom: 0;
        padding: 3px 8px;
        font-size: 11px;
        color: #ddd;
        background: rgba(15, 15, 35, 0.75);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .more {
        width: 100%;
        margin-top: 12px;
        padding: 10px;
        border: none;
        border-radius: 10px;
        background: #2a2a5a;
        color: #ddd;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
    }

    .more:disabled { opacity: 0.6; cursor: default; }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }

    .viewer {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.92);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 200;
        padding: 16px;
    }

    .viewer img {
        max-width: 100%;
        max-height: 100%;
        border-radius: 8px;
        object-fit: contain;
    }

    .viewer-close {
        position: absolute;
        top: calc(12px + env(safe-area-inset-top, 0px));
        right: 12px;
        background: rgba(255, 255, 255, 0.15);
        border: none;
        color: #fff;
        font-size: 20px;
        width: 40px;
        height: 40px;
        border-radius: 50%;
        cursor: pointer;
    }
</style>
