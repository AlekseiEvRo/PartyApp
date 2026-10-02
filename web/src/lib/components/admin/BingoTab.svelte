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

    interface BingoState {
        sessionId: string;
        displayName: string;
        size: number;
        cells: string[];
        pointsPerCell: number;
        lineBonus: number;
        confirmedCells: number[];
        markCounts: Record<string, number>;
        playersCount: number;
    }

    let sessions: AdminSession[] = [];
    let selectedSessionId = '';
    let state: BingoState | null = null;
    let loading = false;
    let busyCell: number | null = null;
    let error = '';

    $: confirmedSet = new Set(state?.confirmedCells ?? []);

    onMount(load);

    async function load() {
        error = '';

        try {
            const all = await api<AdminSession[]>('/api/admin/sessions');
            sessions = all.filter((s) => s.type === 'bingo').slice(0, 10);

            if (!selectedSessionId && sessions.length > 0) {
                selectedSessionId = sessions[0].id;
            }

            await loadState();
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить сессии';
        }
    }

    async function loadState() {
        if (!selectedSessionId) {
            state = null;
            return;
        }

        loading = true;

        try {
            state = await api<BingoState>(`/api/events/bingo/${selectedSessionId}`);
            error = '';
        } catch (e) {
            state = null;
            error = e instanceof Error ? e.message : 'Не удалось загрузить состояние бинго';
        } finally {
            loading = false;
        }
    }

    async function confirmCell(index: number) {
        if (!state || confirmedSet.has(index)) return;

        busyCell = index;

        try {
            const result = await api<{ awardedPlayers: number }>(
                `/api/events/bingo/${state.sessionId}/cells/${index}/confirm`,
                'POST'
            );
            showToast(`Подтверждено! Баллы получили: ${result.awardedPlayers}`);
            await loadState();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось подтвердить клетку', 'error');
        } finally {
            busyCell = null;
        }
    }
</script>

<h2>🎯 Бинго: подтверждение событий</h2>

<p class="hint">
    Игроки отмечают клетки, но баллы начисляются только когда ты подтвердишь, что событие
    реально было. Линии считаются по подтверждённым клеткам.
</p>

<div class="toolbar">
    <select bind:value={selectedSessionId} on:change={loadState}>
        {#if sessions.length === 0}
            <option value="">Нет сессий бинго</option>
        {/if}
        {#each sessions as session (session.id)}
            <option value={session.id}>
                {session.definitionName}{session.state === 'Finished' ? ' (завершён)' : ''}
            </option>
        {/each}
    </select>
    <button class="btn" on:click={loadState} disabled={loading}>Обновить</button>
</div>

{#if error}
    <p class="message error">{error}</p>
{:else if loading && !state}
    <p class="muted">Загружаем…</p>
{:else if !state}
    <p class="muted">Выбери сессию бинго.</p>
{:else}
    <p class="meta">
        Игроков: {state.playersCount} · за клетку: {state.pointsPerCell} · за линию: {state.lineBonus}
    </p>

    <div class="grid" style="grid-template-columns: repeat({state.size}, 1fr)">
        {#each state.cells as cell, i}
            <button
                class="cell"
                class:confirmed={confirmedSet.has(i)}
                disabled={busyCell === i || confirmedSet.has(i)}
                on:click={() => confirmCell(i)}
                title={confirmedSet.has(i) ? 'Событие подтверждено' : 'Нажми, если событие было'}
            >
                <span class="cell-text">{cell}</span>
                <span class="marks">{state.markCounts[i] ?? 0} 👤</span>
            </button>
        {/each}
    </div>
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
        padding: 9px 16px;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
    }

    .btn:disabled { opacity: 0.5; cursor: default; }

    .meta { font-size: 13px; color: var(--muted, #aaa); margin-bottom: 12px; }

    .grid {
        display: grid;
        gap: 6px;
    }

    .cell {
        display: flex;
        flex-direction: column;
        justify-content: space-between;
        gap: 6px;
        min-height: 84px;
        background: var(--bg-soft, #12122e);
        color: #ddd;
        border: 1px solid #2a2a5e;
        border-radius: 10px;
        padding: 8px;
        font-size: 11px;
        line-height: 1.25;
        text-align: left;
        cursor: pointer;
        transition: border-color 0.15s, background 0.15s;
    }

    .cell:hover:not(:disabled) { border-color: var(--accent, #f5a623); }

    .cell.confirmed {
        background: #1e3d2a;
        border-color: #27ae60;
        cursor: default;
    }

    .cell-text { overflow-wrap: anywhere; }

    .marks {
        align-self: flex-end;
        font-size: 11px;
        color: var(--muted, #aaa);
    }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
