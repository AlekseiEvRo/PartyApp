<script lang="ts">
    import { onMount, onDestroy } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    let players: any[] = [];
    let selectedPlayerIds: Set<string> = new Set();
    let state: any = null;
    let stateInterval: any = null;

    async function loadPlayers() {
        try {
            players = await api<any[]>('/api/admin/players');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    function togglePlayer(id: string) {
        if (selectedPlayerIds.has(id)) selectedPlayerIds.delete(id);
        else selectedPlayerIds.add(id);
        selectedPlayerIds = selectedPlayerIds;
    }

    async function startGame() {
        const ids = [...selectedPlayerIds];
        if (ids.length < 4) {
            showToast('Выбери минимум 4 игроков', 'error');
            return;
        }
        try {
            const result = await api<any>('/api/spygame/start', 'POST', { playerIds: ids });
            showToast(`Игра запущена! Слово: ${result.secretWord}`);
            loadState();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function finishGame() {
        try {
            await api('/api/spygame/finish', 'POST');
            showToast('Игра завершена');
            loadState();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function loadState() {
        try {
            state = await api<any>('/api/spygame/state');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    onMount(() => {
        loadPlayers();
        loadState();
        stateInterval = setInterval(loadState, 3000);
    });

    onDestroy(() => {
        if (stateInterval) clearInterval(stateInterval);
    });
</script>

<h2>🕵 Шпионаж</h2>

<div class="card">
    <h3>Выбор участников</h3>
    <p class="hint">Выбери минимум 4 игроков. 2 из них станут шпионами (пара).</p>
    <button class="btn btn-primary" on:click={loadPlayers}>🔄 Обновить список</button>

    <div class="players-list">
        {#each players.filter(p => p.role === 'Player') as p}
            <label class="player-item">
                <input
                        type="checkbox"
                        checked={selectedPlayerIds.has(p.id)}
                        on:change={() => togglePlayer(p.id)}
                />
                {p.displayName} (⭐ {p.balance})
            </label>
        {/each}
    </div>

    <div class="actions">
        <button class="btn btn-success" on:click={startGame}>🎮 Запустить игру</button>
        <button class="btn btn-danger" on:click={finishGame}>⏹ Завершить</button>
    </div>
</div>

<div class="card">
    <h3>Состояние игры</h3>
    {#if !state || state.phase === 'Idle'}
        <p style="color:#666;">Игра не запущена</p>
    {:else}
        <p><b>Фаза:</b> {state.phase}</p>
        <p><b>Секретное слово:</b> <code class="word">{state.secretWord}</code></p>

        {#if state.phase === 'Finished'}
            {#if state.winner === 'spies'}
                <p class="winner spies">🕵 ПОБЕДА ШПИОНОВ!</p>
            {:else if state.winner === 'town'}
                <p class="winner town">👤 ПОБЕДА ГОРОЖАНИНА: {state.townWinnerName}</p>
            {:else if state.winner === 'draw'}
                <p class="winner draw">🤝 НИЧЬЯ</p>
            {/if}
        {/if}

        <p><b>Игроки:</b></p>
        <ul>
            {#each state.players as p}
                <li>
                    {p.displayName} —
                    {p.role === 'Spy' ? '🕵 Шпион' : '👤 Горожанин'}
                    {#if p.hasAccused}<span class="accused">(обвинил)</span>{/if}
                </li>
            {/each}
        </ul>
    {/if}
</div>

<style>
    .card { background: #0f0f23; border-radius: 8px; padding: 16px; margin: 16px 0; }
    .hint { color: #aaa; font-size: 13px; margin: 8px 0; }
    .players-list { margin: 12px 0; }
    .player-item { display: block; margin: 6px 0; cursor: pointer; }
    .actions { display: flex; gap: 8px; margin-top: 12px; }
    .btn { padding: 10px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; }
    .btn-primary { background: #3498db; color: #fff; }
    .btn-success { background: #27ae60; color: #fff; }
    .btn-danger { background: #e74c3c; color: #fff; }
    .word { background: #0f0f23; padding: 4px 8px; border-radius: 4px; }
    .winner { font-size: 18px; font-weight: bold; margin: 12px 0; }
    .winner.spies { color: #e74c3c; }
    .winner.town { color: #27ae60; }
    .winner.draw { color: #f39c12; }
    .accused { color: #f39c12; }
    ul { line-height: 1.8; padding-left: 20px; }
</style>