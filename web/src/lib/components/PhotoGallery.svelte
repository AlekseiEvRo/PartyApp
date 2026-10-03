<script lang="ts">
    import { onDestroy } from 'svelte';
    import { api } from '../api';
    import { photosVersion, photoRemovedId, showToast } from '../stores';
    import { loadPhotoUrl, invalidatePhotoUrl, releasePhotoUrls, uploadPhoto } from '../photos';

    interface Photo {
        id: string;
        uploadedByName: string;
        uploadedAt: string;
        caption: string | null;
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
    const maxCaptionLength = 200;

    let photos: Photo[] = [];
    let urls: Record<string, string> = {};
    let total = 0;
    let loading = true;
    let uploading = false;
    let error = '';
    let viewer: Photo | null = null;
    let lastVersion = -1;
    let lastRemovedId: string | null = null;

    // Фото и подпись до подтверждения отправки
    let pendingFile: File | null = null;
    let pendingPreview = '';
    let caption = '';

    $: if ($photosVersion !== lastVersion) {
        lastVersion = $photosVersion;
        void load();
    }

    // Фото удалили (или отозвали одобрение) — убираем из списка без перезагрузки
    $: if ($photoRemovedId && $photoRemovedId !== lastRemovedId) {
        lastRemovedId = $photoRemovedId;
        removeLocally($photoRemovedId);
    }

    onDestroy(() => {
        releasePreview();
        releasePhotoUrls();
    });

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

    function onFileChange(event: Event) {
        const input = event.currentTarget as HTMLInputElement;
        const file = input.files?.[0];
        input.value = '';

        if (!file || uploading) return;

        releasePreview();
        pendingFile = file;
        pendingPreview = URL.createObjectURL(file);
        caption = '';
    }

    function releasePreview() {
        if (pendingPreview) {
            URL.revokeObjectURL(pendingPreview);
            pendingPreview = '';
        }
    }

    function cancelUpload() {
        if (uploading) return;
        releasePreview();
        pendingFile = null;
    }

    async function confirmUpload() {
        if (!pendingFile || uploading) return;

        uploading = true;

        try {
            const { status } = await uploadPhoto(pendingFile, caption);
            showToast(status === 'Pending' ? '📸 Фото отправлено на модерацию' : '📸 Фото добавлено!');
            releasePreview();
            pendingFile = null;
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
                <figure class="tile" class:has-caption={!!photo.caption}>
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

                    <figcaption class="meta">
                        {#if photo.caption}
                            <span class="caption" title={photo.caption}>💬 {photo.caption}</span>
                        {/if}
                        <span>{photo.uploadedByName} · {formatTime(photo.uploadedAt)}</span>
                    </figcaption>
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
        {#if viewer.caption}
            <p class="viewer-caption">💬 {viewer.caption}</p>
        {/if}
        <button class="viewer-close" on:click={() => (viewer = null)} aria-label="Закрыть">✕</button>
    </div>
{/if}

{#if pendingFile}
    <div class="upload-overlay" role="presentation">
        <div class="upload-card" role="dialog" aria-label="Подпись к фото">
            <img src={pendingPreview} alt="Превью фото" />

            <input
                class="caption-input"
                type="text"
                maxlength={maxCaptionLength}
                placeholder="Подпись (необязательно)"
                bind:value={caption}
                on:keydown={(event) => {
                    if (event.key === 'Enter') void confirmUpload();
                }}
            />

            <span class="caption-counter">{caption.length}/{maxCaptionLength}</span>

            <div class="upload-actions">
                <button class="cancel" on:click={cancelUpload} disabled={uploading}>Отмена</button>
                <button class="send" on:click={confirmUpload} disabled={uploading}>
                    {uploading ? 'Отправляем…' : 'Отправить'}
                </button>
            </div>
        </div>
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

    /* Подпись добавляет вторую строку в подвал — поднимаем кнопки */
    .tile.has-caption .tile-actions { bottom: 44px; }

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
        display: flex;
        flex-direction: column;
        gap: 1px;
    }

    .meta .caption {
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

    .viewer-caption {
        position: absolute;
        left: 16px;
        right: 16px;
        bottom: calc(16px + env(safe-area-inset-bottom, 0px));
        margin: 0;
        text-align: center;
        color: #fff;
        font-size: 16px;
        text-shadow: 0 1px 6px rgba(0, 0, 0, 0.9);
    }

    .upload-overlay {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.85);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 210;
        padding: 16px;
    }

    .upload-card {
        width: min(420px, 100%);
        background: var(--bg-soft, #12122e);
        border-radius: 14px;
        padding: 14px;
        display: flex;
        flex-direction: column;
        gap: 10px;
    }

    .upload-card img {
        width: 100%;
        max-height: 45vh;
        object-fit: contain;
        border-radius: 10px;
        background: #000;
    }

    .caption-input {
        background: #1d1d40;
        border: 1px solid #2a2a5e;
        border-radius: 8px;
        color: inherit;
        padding: 10px 12px;
        font-size: 15px;
    }

    .caption-counter {
        align-self: flex-end;
        margin-top: -6px;
        font-size: 11px;
        color: var(--muted, #aaa);
    }

    .upload-actions {
        display: flex;
        gap: 8px;
        justify-content: flex-end;
    }

    .upload-actions button {
        border: none;
        border-radius: 8px;
        padding: 9px 14px;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
    }

    .upload-actions .cancel { background: #2a2a5e; color: #ddd; }
    .upload-actions .send { background: var(--accent, #f5a623); color: #12122e; }
    .upload-actions button:disabled { opacity: 0.6; cursor: default; }
</style>
