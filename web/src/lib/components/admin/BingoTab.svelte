<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast, bingoCellConfirmed, bingoCellRejected, bingoLineAwarded } from '../../stores';

    interface AdminSession {
        id: string;
        definitionName: string;
        type: string;
        state: string;
    }

    interface LineAward {
        lineIndex: number;
        lineLabel: string;
        playerId: string;
        playerName: string;
        amount: number;
        awardedAt: string;
    }

    interface BingoState {
        sessionId: string;
        displayName: string;
        size: number;
        cells: string[];
        pointsPerCell: number;
        lineBonus: number;
        maxPredictions: number;
        confirmedCells: number[];
        rejectedCells: number[];
        markCounts: Record<string, number>;
        playersCount: number;
        lineAwards: LineAward[];
    }

    let sessions: AdminSession[] = [];
    let selectedSessionId = '';
    let state: BingoState | null = null;
    let loading = false;
    let busyCell: number | null = null;
    let error = '';

    $: confirmedSet = new Set(state?.confirmedCells ?? []);
    $: rejectedSet = new Set(state?.rejectedCells ?? []);

    onMount(load);

    // Клетку могли подтвердить или отклонить с другого экрана — подтянем состояние
    $: if (selectedSessionId
        && ($bingoCellConfirmed?.sessionId === selectedSessionId
            || $bingoCellRejected?.sessionId === selectedSessionId
            || $bingoLineAwarded?.sessionId === selectedSessionId)) {
        void loadState();
    }

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
        if (!state || confirmedSet.has(index) || rejectedSet.has(index)) return;

        busyCell = index;

        try {
            const result = await api<{ awardedPlayers: number }>(
                `/api/events/bingo/${state.sessionId}/cells/${index}/confirm`,
                'POST'
            );
            showToast(`Подтверждено! Баллы за предсказание получили: ${result.awardedPlayers}`);
            await loadState();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось подтвердить клетку', 'error');
        } finally {
            busyCell = null;
        }
    }

    async function rejectCell(index: number) {
        if (!state || confirmedSet.has(index) || rejectedSet.has(index)) return;

        busyCell = index;

        try {
            const result = await api<{ freedPredictions: number }>(
                `/api/events/bingo/${state.sessionId}/cells/${index}/reject`,
                'POST'
            );
            showToast(`События не было. Слоты предсказаний освобождены: ${result.freedPredictions}`);
            await loadState();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось отклонить клетку', 'error');
        } finally {
            busyCell = null;
        }
    }
</script>

<h2>🎯 Бинго: решения по событиям</h2>

<p class="hint">
    Игроки заранее отмечают клетки-предсказания (одновременно не больше {state?.maxPredictions ?? 3}).
    Подтверждай реально случившиеся события — тогда все, кто предсказал их заранее, получат баллы.
    Если события не было, нажми «не было»: предсказания не сбудутся, но игроки снова смогут отмечать
    клетки. Бонус за линию получает тот, кто собрал её быстрее всех.
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
        Игроков: {state.playersCount} · за предсказание: {state.pointsPerCell}
        · за линию: {state.lineBonus} · лимит предсказаний: {state.maxPredictions}
    </p>

    <div class="grid" style="grid-template-columns: repeat({state.size}, 1fr)">
        {#each state.cells.slice(0, state.size * state.size) as cell, i}
            <div class="cell" class:confirmed={confirmedSet.has(i)} class:rejected={rejectedSet.has(i)}>
                <span class="cell-text">{cell}</span>
                <span class="marks">{state.markCounts[i] ?? 0} 👤</span>

                {#if confirmedSet.has(i)}
                    <span class="cell-status confirmed-status">✅ подтверждено</span>
                {:else if rejectedSet.has(i)}
                    <span class="cell-status rejected-status">🙅 не было</span>
                {:else}
                    <div class="cell-actions">
                        <button class="mini confirm" disabled={busyCell === i} on:click={() => confirmCell(i)}>
                            ✅ было
                        </button>
                        <button class="mini reject" disabled={busyCell === i} on:click={() => rejectCell(i)}>
                            🙅 не было
                        </button>
                    </div>
                {/if}
            </div>
        {/each}
    </div>

    {#if state.lineAwards.length > 0}
        <h3 class="awards-title">🏆 Разыгранные линии</h3>
        <ul class="awards">
            {#each state.lineAwards as award (award.lineIndex)}
                <li>{award.lineLabel}: <b>{award.playerName}</b> (+{award.amount})</li>
            {/each}
        </ul>
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
        min-height: 100px;
        background: var(--bg-soft, #12122e);
        color: #ddd;
        border: 1px solid #2a2a5e;
        border-radius: 10px;
        padding: 8px;
        font-size: 11px;
        line-height: 1.25;
        text-align: left;
        transition: border-color 0.15s, background 0.15s;
    }

    .cell.confirmed {
        background: #1e3d2a;
        border-color: #27ae60;
    }

    .cell.rejected {
        background: #2a1a1a;
        border-color: #7a4a4a;
        color: #a07b7b;
    }

    .cell-text { overflow-wrap: anywhere; }

    .cell.rejected .cell-text { text-decoration: line-through; }

    .marks {
        align-self: flex-end;
        font-size: 11px;
        color: var(--muted, #aaa);
    }

    .cell-actions {
        display: flex;
        gap: 4px;
    }

    .mini {
        flex: 1;
        border: none;
        border-radius: 6px;
        padding: 5px 4px;
        font-size: 10px;
        font-weight: bold;
        cursor: pointer;
        white-space: nowrap;
    }

    .mini:disabled { opacity: 0.5; cursor: default; }

    .mini.confirm { background: #1f5f3a; color: #d9ffe9; }
    .mini.reject { background: #5f2a2a; color: #ffd9d9; }

    .cell-status {
        font-size: 10px;
        font-weight: bold;
    }

    .confirmed-status { color: #7fe0a5; }
    .rejected-status { color: #e08f8f; }

    .awards-title { margin: 18px 0 8px; font-size: 15px; }

    .awards {
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 13px;
    }

    .awards li {
        background: var(--bg-soft, #12122e);
        border-radius: 8px;
        padding: 8px 10px;
    }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
