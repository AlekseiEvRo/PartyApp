<script lang="ts">
    import { api } from '../api';
    import { showToast, wishesVersion, wishRemovedId } from '../stores';

    interface Wish {
        id: string;
        playerName: string;
        text: string;
        status: string;
        createdAt: string;
        isMine: boolean;
    }

    interface WishPage {
        items: Wish[];
        total: number;
    }

    const maxLength = 500;
    const pageSize = 50;

    let wishes: Wish[] = [];
    let total = 0;
    let loading = true;
    let sending = false;
    let text = '';
    let error = '';
    let lastVersion = -1;
    let lastRemovedId: string | null = null;

    $: if ($wishesVersion !== lastVersion) {
        lastVersion = $wishesVersion;
        void load();
    }

    // Пожелание удалили (или отозвали одобрение) — убираем со стенки у всех
    $: if ($wishRemovedId && $wishRemovedId !== lastRemovedId) {
        lastRemovedId = $wishRemovedId;
        removeLocally($wishRemovedId);
    }

    async function load() {
        loading = true;
        error = '';

        try {
            const page = await api<WishPage>(`/api/wishes?limit=${pageSize}&offset=0`);
            wishes = page.items;
            total = page.total;
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить пожелания';
        } finally {
            loading = false;
        }
    }

    async function submit() {
        const value = text.trim();
        if (value.length === 0 || sending) return;

        sending = true;

        try {
            const result = await api<{ id: string; status: string }>('/api/wishes', 'POST', {
                text: value
            });
            text = '';
            showToast(
                result.status === 'Pending'
                    ? '💌 Пожелание отправлено на модерацию'
                    : '💌 Пожелание добавлено!'
            );
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось отправить пожелание', 'error');
        } finally {
            sending = false;
        }
    }

    async function remove(wish: Wish) {
        if (!confirm('Удалить пожелание?')) return;

        try {
            await api(`/api/wishes/${wish.id}`, 'DELETE');
            removeLocally(wish.id);
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось удалить пожелание', 'error');
        }
    }

    /** Убирает пожелание из открытого списка; повторный вызов (после SignalR) безопасен. */
    function removeLocally(id: string) {
        const existed = wishes.some((w) => w.id === id);
        wishes = wishes.filter((w) => w.id !== id);

        if (existed) total = Math.max(0, total - 1);
    }

    function onKeydown(event: KeyboardEvent) {
        if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
            void submit();
        }
    }

    function formatTime(value: string): string {
        return new Date(value).toLocaleString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    }
</script>

<section class="wishes">
    <h2>💌 Стенка пожеланий</h2>

    <div class="composer">
        <textarea
            bind:value={text}
            maxlength={maxLength}
            rows="2"
            placeholder="Напиши тост или пожелание имениннику…"
            on:keydown={onKeydown}
        ></textarea>

        <div class="composer-footer">
            <span class="counter" class:overflow={text.length > maxLength}>{text.length}/{maxLength}</span>
            <button class="send" on:click={submit} disabled={sending || text.trim().length === 0}>
                {sending ? 'Отправляем…' : 'Отправить'}
            </button>
        </div>
    </div>

    {#if error}
        <p class="message error">{error}</p>
    {:else if loading && wishes.length === 0}
        <p class="muted">Загружаем пожелания…</p>
    {:else if wishes.length === 0}
        <p class="muted">Пока никто ничего не написал — будь первым!</p>
    {:else}
        <ul class="list">
            {#each wishes as wish (wish.id)}
                <li class="card">
                    <p class="text">{wish.text}</p>
                    <div class="footer">
                        <span class="author">— {wish.playerName} · {formatTime(wish.createdAt)}</span>

                        {#if wish.status === 'Pending'}
                            <span class="badge pending">на модерации</span>
                        {:else if wish.status === 'Rejected'}
                            <span class="badge rejected">отклонено</span>
                        {/if}

                        {#if wish.isMine}
                            <button class="delete" on:click={() => remove(wish)} title="Удалить">🗑</button>
                        {/if}
                    </div>
                </li>
            {/each}
        </ul>
    {/if}
</section>

<style>
    .wishes { margin-bottom: 24px; }

    h2 { margin-bottom: 12px; }

    .composer {
        background: var(--bg-soft, #12122e);
        border-radius: 12px;
        padding: 12px;
        margin-bottom: 14px;
    }

    textarea {
        width: 100%;
        resize: vertical;
        background: transparent;
        border: none;
        color: inherit;
        font: inherit;
        outline: none;
    }

    .composer-footer {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 10px;
        margin-top: 8px;
    }

    .counter {
        font-size: 12px;
        color: var(--muted, #aaa);
    }

    .counter.overflow { color: #e74c3c; }

    .send {
        background: var(--accent, #f5a623);
        color: #12122e;
        border: none;
        border-radius: 8px;
        padding: 8px 16px;
        font-weight: bold;
        font-size: 14px;
        cursor: pointer;
    }

    .send:disabled { opacity: 0.5; cursor: default; }

    .list {
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: 10px;
    }

    .card {
        background: var(--bg-soft, #12122e);
        border-radius: 12px;
        padding: 12px 14px;
    }

    .text {
        font-size: 15px;
        line-height: 1.5;
        margin-bottom: 6px;
        overflow-wrap: anywhere;
    }

    .footer {
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
    }

    .author {
        font-size: 12px;
        color: var(--muted, #aaa);
    }

    .badge {
        font-size: 11px;
        padding: 2px 8px;
        border-radius: 10px;
    }

    .badge.pending { background: rgba(245, 166, 35, 0.2); color: #f5a623; }
    .badge.rejected { background: rgba(231, 76, 60, 0.2); color: #e74c3c; }

    .delete {
        margin-left: auto;
        background: none;
        border: none;
        color: #aaa;
        font-size: 15px;
        cursor: pointer;
    }

    .delete:hover { color: #e74c3c; }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
