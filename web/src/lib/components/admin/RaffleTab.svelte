<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface AdminSession {
        id: string;
        definitionName: string;
        type: string;
        state: string;
    }

    let sessions: AdminSession[] = [];
    let selectedSessionId = '';
    let busy = false;
    let error = '';
    let winnerName: string | null = null;

    onMount(load);

    async function load() {
        error = '';

        try {
            const all = await api<AdminSession[]>('/api/admin/sessions');
            sessions = all.filter((s) => s.type === 'raffle').slice(0, 10);

            if (!selectedSessionId && sessions.length > 0) {
                selectedSessionId = sessions[0].id;
            }
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить сессии';
        }
    }

    async function draw() {
        if (!selectedSessionId) return;

        busy = true;

        try {
            const data = await api<{ winner: { name: string } }>(
                `/api/events/raffle/${selectedSessionId}/draw`,
                'POST'
            );
            winnerName = data.winner.name;
            showToast(`🏆 Победитель: ${data.winner.name}`);
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось разыграть', 'error');
        } finally {
            busy = false;
        }
    }
</script>

<h2>🎡 Лототрон</h2>

<p class="hint">
    Игроки жмут «Участвовать» в приложении. Нажми «Разыграть» — сервер выберет победителя,
    а на большом экране прокрутится колесо. Повторный розыгрыш в одной сессии невозможен.
</p>

{#if error}
    <p class="message error">{error}</p>
{:else}
    <div class="toolbar">
        <select bind:value={selectedSessionId} on:change={() => (winnerName = null)}>
            {#if sessions.length === 0}
                <option value="">Нет сессий лототрона</option>
            {/if}
            {#each sessions as session (session.id)}
                <option value={session.id}>
                    {session.definitionName}{session.state === 'Finished' ? ' (завершён)' : ''}
                </option>
            {/each}
        </select>
        <button class="btn accent" on:click={draw} disabled={busy || !selectedSessionId}>
            {busy ? 'Крутим…' : '🎲 Разыграть'}
        </button>
    </div>

    {#if winnerName}
        <p class="result">🏆 Победитель: {winnerName}</p>
    {/if}
{/if}

<style>
    h2 { margin-bottom: 10px; }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        line-height: 1.5;
        margin-bottom: 16px;
    }

    .toolbar {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-bottom: 14px;
    }

    .toolbar select {
        flex: 1;
        min-width: 180px;
        background: var(--bg-soft, #12122e);
        border: 1px solid #2a2a5e;
        border-radius: 8px;
        color: inherit;
        padding: 9px 12px;
        font-size: 14px;
    }

    .btn {
        background: #2a2a5a;
        color: #ddd;
        border: none;
        border-radius: 8px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
    }

    .btn.accent { background: #f5a623; color: #12122e; }
    .btn:disabled { opacity: 0.5; cursor: default; }

    .result {
        font-size: 18px;
        font-weight: bold;
        color: var(--accent, #f5a623);
    }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
