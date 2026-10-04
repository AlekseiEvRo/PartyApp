<script lang="ts">
    import { createEventDispatcher } from 'svelte';
    import { api } from '../api';

    export let open = false;
    export let url = '/api/wallet/transactions';
    export let title = '⭐ История баллов';
    export let emptyText = 'Пока нет начислений — участвуй в ивентах и получай баллы!';

    interface Transaction {
        id: string;
        amount: number;
        type: string;
        description: string;
        relatedSessionId: string | null;
        createdAt: string;
    }

    interface TransactionsPage {
        items: Transaction[];
        total: number;
    }

    const dispatch = createEventDispatcher();
    const pageSize = 50;

    let items: Transaction[] = [];
    let total = 0;
    let loading = false;
    let error = '';

    $: if (open && url) {
        void load();
    }

    async function load() {
        loading = true;
        error = '';

        try {
            const page = await api<TransactionsPage>(
                `${url}?limit=${pageSize}&offset=0`
            );
            items = page.items;
            total = page.total;
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить историю';
        } finally {
            loading = false;
        }
    }

    async function loadMore() {
        loading = true;
        error = '';

        try {
            const page = await api<TransactionsPage>(
                `${url}?limit=${pageSize}&offset=${items.length}`
            );
            items = [...items, ...page.items];
            total = page.total;
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить историю';
        } finally {
            loading = false;
        }
    }

    function close() {
        dispatch('close');
    }

    function onBackdropClick(event: MouseEvent) {
        if (event.target === event.currentTarget) {
            close();
        }
    }

    function formatAmount(amount: number): string {
        return amount > 0 ? `+${amount}` : `${amount}`;
    }

    function formatDate(value: string): string {
        const date = new Date(value);
        return date.toLocaleString('ru-RU', {
            day: 'numeric',
            month: 'short',
            hour: '2-digit',
            minute: '2-digit'
        });
    }

    function typeLabel(type: string): string {
        switch (type) {
            case 'EventReward':
                return 'Ивент';
            case 'QrBonus':
                return 'QR-код';
            case 'AdminGrant':
                return 'Начисление';
            case 'AdminDeduct':
                return 'Списание';
            case 'TransferIn':
            case 'TransferOut':
                return 'Перевод';
            default:
                return type;
        }
    }
</script>

{#if open}
    <div class="overlay" on:click={onBackdropClick} role="presentation">
        <div class="modal">
            <div class="modal-header">
                <h3>{title}</h3>
                <button class="close" on:click={close} aria-label="Закрыть">✕</button>
            </div>

            {#if error}
                <div class="message error">{error}</div>
            {:else if loading && items.length === 0}
                <p class="hint">Загружаем…</p>
            {:else if items.length === 0}
                <p class="hint">{emptyText}</p>
            {:else}
                <ul class="list">
                    {#each items as item (item.id)}
                        <li>
                            <div class="info">
                                <span class="description">{item.description}</span>
                                <span class="meta">{typeLabel(item.type)} · {formatDate(item.createdAt)}</span>
                            </div>
                            <span class="amount" class:positive={item.amount > 0}>
                                {formatAmount(item.amount)}
                            </span>
                        </li>
                    {/each}
                </ul>

                {#if items.length < total}
                    <button class="more" on:click={loadMore} disabled={loading}>
                        {loading ? 'Загружаем…' : `Показать ещё (${total - items.length})`}
                    </button>
                {/if}
            {/if}
        </div>
    </div>
{/if}

<style>
    .overlay {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.7);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 100;
        padding: 20px;
        padding-bottom: calc(20px + env(safe-area-inset-bottom, 0px));
    }

    .modal {
        background: var(--card, #1a1a3e);
        border-radius: 16px;
        padding: 20px 24px;
        width: 100%;
        max-width: 420px;
        max-height: 85vh;
        max-height: calc(100dvh - 40px);
        overflow-y: auto;
        overscroll-behavior: contain;
    }

    .modal-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        margin-bottom: 16px;
    }

    .modal-header h3 {
        color: #f5a623;
        font-size: 17px;
    }

    .close {
        background: none;
        border: none;
        color: #aaa;
        font-size: 18px;
        cursor: pointer;
        padding: 4px 8px;
    }

    .list {
        display: flex;
        flex-direction: column;
        gap: 8px;
        list-style: none;
    }

    .list li {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        background: var(--bg-soft, #12122e);
        border-radius: 10px;
        padding: 10px 12px;
    }

    .info {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
    }

    .description {
        font-size: 14px;
        color: #ddd;
        overflow-wrap: anywhere;
    }

    .meta {
        font-size: 12px;
        color: var(--muted, #aaa);
    }

    .amount {
        font-size: 15px;
        font-weight: bold;
        color: #e74c3c;
        white-space: nowrap;
    }

    .amount.positive { color: #27ae60; }

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

    .more:disabled {
        opacity: 0.6;
        cursor: default;
    }

    .hint {
        color: var(--muted, #aaa);
        font-size: 14px;
        line-height: 1.5;
    }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
        line-height: 1.4;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
