<script lang="ts">
    import { createEventDispatcher } from 'svelte';
    import { api } from '../api';
    import { balance, showToast } from '../stores';

    export let open = false;

    interface Player {
        id: string;
        displayName: string;
        username: string;
    }

    const dispatch = createEventDispatcher();

    let players: Player[] = [];
    let loadingPlayers = false;
    let recipientId = '';
    let amount = 10;
    let comment = '';
    let sending = false;
    let error = '';

    $: if (open) {
        void loadPlayers();
    }

    async function loadPlayers() {
        loadingPlayers = true;
        error = '';

        try {
            players = await api<Player[]>('/api/wallet/players');
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить игроков';
        } finally {
            loadingPlayers = false;
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

    async function send() {
        if (sending) return;

        const recipient = players.find((p) => p.id === recipientId);
        if (!recipient) {
            showToast('Выбери получателя', 'error');
            return;
        }

        if (!Number.isFinite(amount) || amount <= 0) {
            showToast('Сумма должна быть больше 0', 'error');
            return;
        }

        if (amount > $balance) {
            showToast('Недостаточно баллов', 'error');
            return;
        }

        sending = true;
        error = '';

        try {
            const result = await api<{ newBalance: number }>('/api/wallet/transfer', 'POST', {
                recipientId,
                amount,
                comment: comment.trim() || null
            });

            balance.set(result.newBalance);
            showToast(`✅ Переведено ${amount} баллов: ${recipient.displayName}`, 'success');

            recipientId = '';
            amount = 10;
            comment = '';
            close();
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось перевести баллы';
        } finally {
            sending = false;
        }
    }
</script>

{#if open}
    <div class="overlay" on:click={onBackdropClick} role="presentation">
        <div class="modal">
            <div class="modal-header">
                <h3>💸 Перевод баллов</h3>
                <button class="close" on:click={close} aria-label="Закрыть">✕</button>
            </div>

            <p class="balance-line">Твой баланс: ⭐ {$balance}</p>

            {#if error}
                <div class="message error">{error}</div>
            {/if}

            {#if loadingPlayers}
                <p class="hint">Загружаем игроков…</p>
            {:else if players.length === 0}
                <p class="hint">Нет других игроков для перевода.</p>
            {:else}
                <label class="field">
                    <span>Кому</span>
                    <select bind:value={recipientId}>
                        <option value="">— выбери игрока —</option>
                        {#each players as p (p.id)}
                            <option value={p.id}>{p.displayName} ({p.username})</option>
                        {/each}
                    </select>
                </label>

                <label class="field">
                    <span>Сколько</span>
                    <input type="number" min="1" step="1" bind:value={amount} />
                </label>

                <label class="field">
                    <span>Комментарий (необязательно)</span>
                    <input type="text" maxlength="200" placeholder="Например: за помощь" bind:value={comment} />
                </label>

                <button
                    class="send"
                    disabled={sending || !recipientId || !(amount > 0)}
                    on:click={send}
                >{sending ? 'Переводим…' : '💸 Перевести'}</button>
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

    .balance-line {
        color: var(--muted, #aaa);
        font-size: 14px;
        margin-bottom: 12px;
    }

    .field {
        display: block;
        margin-bottom: 12px;
    }

    .field span {
        display: block;
        margin-bottom: 6px;
        color: var(--muted, #aaa);
        font-size: 13px;
    }

    .field select,
    .field input {
        width: 100%;
        padding: 10px;
        border-radius: 8px;
        border: 1px solid var(--border, #333);
        background: var(--bg-soft, #12122e);
        color: #fff;
        font-size: 14px;
        box-sizing: border-box;
    }

    .send {
        width: 100%;
        margin-top: 4px;
        padding: 12px;
        border: none;
        border-radius: 10px;
        background: var(--green, #27ae60);
        color: #fff;
        font-size: 15px;
        font-weight: bold;
        cursor: pointer;
    }

    .send:disabled {
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
        margin-bottom: 12px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
