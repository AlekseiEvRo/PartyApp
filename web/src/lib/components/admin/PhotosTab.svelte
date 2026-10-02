<script lang="ts">
    import { onDestroy, onMount } from 'svelte';
    import { api } from '../../api';
    import { loadPhotoUrl, releasePhotoUrls } from '../../photos';
    import { moderationVersion, showToast } from '../../stores';

    interface Photo {
        id: string;
        uploadedByName: string;
        uploadedAt: string;
        status: string;
        likesCount: number;
    }

    interface PhotoPage {
        items: Photo[];
        total: number;
    }

    type Filter = 'Pending' | 'Rejected' | 'all';

    const filters: { id: Filter; label: string }[] = [
        { id: 'Pending', label: 'На модерации' },
        { id: 'Rejected', label: 'Отклонённые' },
        { id: 'all', label: 'Все' }
    ];

    let photos: Photo[] = [];
    let urls: Record<string, string> = {};
    let filter: Filter = 'Pending';
    let loading = true;
    let error = '';
    let lastModerationVersion = -1;

    // Награда за одобренные фото
    let photoPoints = 5;
    let savingSettings = false;

    onMount(() => void loadSettings());

    $: void load(filter);

    // Новое фото на модерации — обновляем очередь, если админ как раз смотрит её
    $: if ($moderationVersion !== lastModerationVersion) {
        lastModerationVersion = $moderationVersion;
        void load(filter);
    }

    onDestroy(releasePhotoUrls);

    async function load(current: Filter) {
        loading = true;
        error = '';

        try {
            const page = await api<PhotoPage>('/api/photos?limit=100');
            photos = current === 'all'
                ? page.items
                : page.items.filter((p) => p.status === current);

            const pairs = await Promise.all(
                photos.map(async (photo) => {
                    const url = await loadPhotoUrl(photo.id).catch(() => '');
                    return [photo.id, url] as const;
                })
            );

            const next: Record<string, string> = {};
            for (const [id, url] of pairs) {
                if (url) next[id] = url;
            }
            urls = next;
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить фото';
        } finally {
            loading = false;
        }
    }

    async function moderate(photo: Photo, action: 'approve' | 'reject') {
        try {
            await api(`/api/photos/${photo.id}/${action}`, 'POST');
            showToast(action === 'approve' ? 'Фото одобрено' : 'Фото отклонено');
            await load(filter);
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось изменить статус', 'error');
        }
    }

    async function remove(photo: Photo) {
        if (!confirm('Удалить фото безвозвратно?')) return;

        try {
            await api(`/api/photos/${photo.id}`, 'DELETE');
            showToast('Фото удалено', 'info');
            await load(filter);
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось удалить фото', 'error');
        }
    }

    function formatTime(value: string): string {
        return new Date(value).toLocaleString('ru-RU', {
            day: 'numeric',
            month: 'short',
            hour: '2-digit',
            minute: '2-digit'
        });
    }

    function statusLabel(status: string): string {
        switch (status) {
            case 'Pending':
                return 'на модерации';
            case 'Rejected':
                return 'отклонено';
            default:
                return 'одобрено';
        }
    }

    async function loadSettings() {
        try {
            const settings = await api<{ photoApprovedPoints: number }>('/api/photos/settings');
            photoPoints = settings.photoApprovedPoints;
        } catch { /* настройки не критичны */ }
    }

    async function saveSettings() {
        savingSettings = true;

        try {
            const settings = await api<{ photoApprovedPoints: number }>(
                '/api/photos/settings',
                'PUT',
                { photoApprovedPoints: Number(photoPoints) }
            );
            photoPoints = settings.photoApprovedPoints;
            showToast('Настройки сохранены');
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось сохранить', 'error');
        } finally {
            savingSettings = false;
        }
    }
</script>

<h2>📸 Модерация фото</h2>

<div class="settings">
    <label for="photo-points">Баллов за одобренное фото</label>
    <input id="photo-points" type="number" min="0" max="1000" bind:value={photoPoints} />
    <button class="filter" disabled={savingSettings} on:click={saveSettings}>
        {savingSettings ? 'Сохраняем…' : 'Сохранить'}
    </button>
</div>

<div class="filters">
    {#each filters as item}
        <button class="filter" class:active={filter === item.id} on:click={() => (filter = item.id)}>
            {item.label}
        </button>
    {/each}
</div>

{#if error}
    <p class="message error">{error}</p>
{:else if loading && photos.length === 0}
    <p class="muted">Загружаем…</p>
{:else if photos.length === 0}
    <p class="muted">Здесь пусто.</p>
{:else}
    <ul class="list">
        {#each photos as photo (photo.id)}
            <li class="row">
                {#if urls[photo.id]}
                    <img src={urls[photo.id]} alt="Фото от {photo.uploadedByName}" loading="lazy" />
                {:else}
                    <div class="thumb-placeholder">…</div>
                {/if}

                <div class="info">
                    <span class="author">{photo.uploadedByName}</span>
                    <span class="meta">{formatTime(photo.uploadedAt)} · ❤️ {photo.likesCount}</span>
                    <span class="meta">{statusLabel(photo.status)}</span>
                </div>

                <div class="actions">
                    {#if photo.status !== 'Approved'}
                        <button class="approve" on:click={() => moderate(photo, 'approve')}>Одобрить</button>
                    {/if}
                    {#if photo.status !== 'Rejected'}
                        <button class="reject" on:click={() => moderate(photo, 'reject')}>Отклонить</button>
                    {/if}
                    <button class="remove" on:click={() => remove(photo)}>Удалить</button>
                </div>
            </li>
        {/each}
    </ul>
{/if}

<style>
    h2 { margin-bottom: 14px; }

    .filters {
        display: flex;
        gap: 6px;
        margin-bottom: 14px;
        flex-wrap: wrap;
    }

    .settings {
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
        margin-bottom: 16px;
    }

    .settings label {
        font-size: 13px;
        color: var(--muted, #aaa);
    }

    .settings input {
        width: 90px;
        background: var(--bg-soft, #12122e);
        border: 1px solid #2a2a5e;
        border-radius: 8px;
        color: inherit;
        padding: 8px 10px;
        font-size: 14px;
    }

    .filter {
        background: var(--bg-soft, #12122e);
        border: none;
        color: var(--muted, #aaa);
        padding: 6px 12px;
        border-radius: 8px;
        font-size: 13px;
        cursor: pointer;
    }

    .filter.active { background: var(--card-soft, #2a2a5e); color: var(--accent, #f5a623); font-weight: bold; }

    .list {
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: 10px;
    }

    .row {
        display: flex;
        align-items: center;
        gap: 12px;
        background: var(--bg-soft, #12122e);
        border-radius: 10px;
        padding: 10px 12px;
        flex-wrap: wrap;
    }

    .row img,
    .thumb-placeholder {
        width: 64px;
        height: 64px;
        object-fit: cover;
        border-radius: 8px;
        flex-shrink: 0;
    }

    .thumb-placeholder {
        display: flex;
        align-items: center;
        justify-content: center;
        background: #1d1d40;
        color: var(--muted, #aaa);
    }

    .info {
        display: flex;
        flex-direction: column;
        gap: 2px;
        flex: 1;
        min-width: 140px;
    }

    .author { font-weight: bold; font-size: 14px; }
    .meta { font-size: 12px; color: var(--muted, #aaa); }

    .actions {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
    }

    .actions button {
        border: none;
        border-radius: 8px;
        padding: 7px 12px;
        font-size: 13px;
        font-weight: bold;
        cursor: pointer;
    }

    .approve { background: #27ae60; color: #fff; }
    .reject { background: #f5a623; color: #12122e; }
    .remove { background: #e74c3c; color: #fff; }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
