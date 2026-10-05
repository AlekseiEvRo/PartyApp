<script lang="ts">
    import { api } from '../../api';
    import { moderationVersion, showToast } from '../../stores';

    interface Wish {
        id: string;
        playerName: string;
        text: string;
        status: string;
        createdAt: string;
    }

    interface WishPage {
        items: Wish[];
        total: number;
    }

    type Filter = 'Pending' | 'Rejected' | 'all';

    const filters: { id: Filter; label: string }[] = [
        { id: 'Pending', label: 'На модерации' },
        { id: 'Rejected', label: 'Отклонённые' },
        { id: 'all', label: 'Все' }
    ];

    let wishes: Wish[] = [];
    let filter: Filter = 'Pending';
    let loading = true;
    let error = '';
    let lastModerationVersion = -1;

    $: void load(filter);

    // Новый отзыв на модерации — обновляем очередь
    $: if ($moderationVersion !== lastModerationVersion) {
        lastModerationVersion = $moderationVersion;
        void load(filter);
    }

    async function load(current: Filter) {
        loading = true;
        error = '';

        try {
            const page = await api<WishPage>('/api/wishes?limit=100');
            wishes = current === 'all'
                ? page.items
                : page.items.filter((w) => w.status === current);
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить отзывы';
        } finally {
            loading = false;
        }
    }

    async function moderate(wish: Wish, action: 'approve' | 'reject') {
        try {
            await api(`/api/wishes/${wish.id}/${action}`, 'POST');
            showToast(action === 'approve' ? 'Отзыв одобрен' : 'Отзыв отклонён');
            await load(filter);
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось изменить статус', 'error');
        }
    }

    async function remove(wish: Wish) {
        if (!confirm('Удалить отзыв?')) return;

        try {
            await api(`/api/wishes/${wish.id}`, 'DELETE');
            await load(filter);
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось удалить', 'error');
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
</script>

<h2>💌 Модерация отзывов</h2>

<div class="filters">
    {#each filters as item}
        <button class="filter" class:active={filter === item.id} on:click={() => (filter = item.id)}>
            {item.label}
        </button>
    {/each}
</div>

{#if error}
    <p class="message error">{error}</p>
{:else if loading && wishes.length === 0}
    <p class="muted">Загружаем…</p>
{:else if wishes.length === 0}
    <p class="muted">Здесь пусто.</p>
{:else}
    <ul class="list">
        {#each wishes as wish (wish.id)}
            <li class="row">
                <div class="info">
                    <p class="text">{wish.text}</p>
                    <span class="meta">{wish.playerName} · {formatTime(wish.createdAt)} · {statusLabel(wish.status)}</span>
                </div>

                <div class="actions">
                    {#if wish.status !== 'Approved'}
                        <button class="approve" on:click={() => moderate(wish, 'approve')}>Одобрить</button>
                    {/if}
                    {#if wish.status !== 'Rejected'}
                        <button class="reject" on:click={() => moderate(wish, 'reject')}>Отклонить</button>
                    {/if}
                    <button class="remove" on:click={() => remove(wish)}>Удалить</button>
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

    .info {
        flex: 1;
        min-width: 200px;
        display: flex;
        flex-direction: column;
        gap: 4px;
    }

    .text { font-size: 14px; line-height: 1.4; overflow-wrap: anywhere; }
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
