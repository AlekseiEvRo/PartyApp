<script lang="ts">
    import { onMount, onDestroy } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface Definition {
        id: string;
        type: string;
        displayName: string;
        isActive: boolean;
    }

    interface PlayerInfo {
        id: string;
        displayName: string;
        role: string;
        balance: number;
    }

    let definitions: Definition[] = [];
    let players: PlayerInfo[] = [];
    let selectedPlayerIds: Set<string> = new Set();
    let definitionId = '';
    let state: any = null;
    let busy = false;
    let interval: any = null;

    async function loadDefinitions() {
        try {
            const all = await api<Definition[]>('/api/events/definitions?includeInactive=true');
            definitions = all.filter((d) => d.type === 'spyfall' && d.isActive);

            if (!definitionId && definitions.length > 0) {
                definitionId = definitions[0].id;
            }
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function loadPlayers() {
        try {
            players = await api<PlayerInfo[]>('/api/admin/players');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function loadState() {
        try {
            state = await api<any>('/api/events/spyfall/current');
        } catch {
            // Фоновое обновление не должно сыпать ошибками
        }
    }

    function togglePlayer(id: string) {
        if (selectedPlayerIds.has(id)) selectedPlayerIds.delete(id);
        else selectedPlayerIds.add(id);
        selectedPlayerIds = selectedPlayerIds;
    }

    async function startGame() {
        if (!definitionId) {
            showToast('Нет активного ивента «Шпионы» — создай его во вкладке «Ивенты»', 'error');
            return;
        }

        const ids = [...selectedPlayerIds];
        if (ids.length < 4) {
            showToast('Выбери минимум 4 игроков', 'error');
            return;
        }

        busy = true;

        try {
            await api('/api/events/spyfall/start', 'POST', { definitionId, playerIds: ids });
            showToast('Игра запущена — участники получили слова');
            selectedPlayerIds = new Set();
            await loadState();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function finishGame() {
        if (!state?.sessionId || state.state !== 'Active') return;

        if (!confirm('Завершить игру? Незавершённый раунд раскроется без баллов.')) return;

        busy = true;

        try {
            await api(`/api/events/${state.sessionId}/finish`, 'POST');
            showToast('Игра завершена');
            await loadState();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    function winnerLabel(game: any): string {
        if (game.winner === 'citizens') return '👥 Победа горожан';
        if (game.winner === 'spy') {
            return game.resolvedBy === 'guess' ? '🕵 Победа шпиона — слово угадано' : '🕵 Победа шпиона';
        }
        if (game.resolvedBy === 'closed') return '🤝 Закрыто без результата — баллы не начислялись';

        return '';
    }

    onMount(() => {
        loadDefinitions();
        loadPlayers();
        loadState();
        interval = setInterval(loadState, 3000);
    });

    onDestroy(() => {
        if (interval) clearInterval(interval);
    });

    $: activeGame = state?.state === 'Active';
    $: gamePlayers = players.filter((p) => p.role === 'Player');
</script>

<h2>🕵 Шпионы</h2>

<p class="hint">
    Выбери минимум 4 игроков и запусти игру: сервер раздаст роли и слова (горожанам одно,
    шпиону — похожее). Участники увидят карточку с правилами и словом, остальные игроки — ничего.
    Горожане голосуют, шпион может один раз угадать слово; баллы настраиваются в редакторе ивента.
</p>

{#if !activeGame}
    <div class="card">
        <h3>Новый раунд</h3>

        {#if definitions.length === 0}
            <p class="hint">Нет активного ивента «Шпионы» — создай его во вкладке «Ивенты».</p>
        {:else}
            {#if definitions.length > 1}
                <div class="field">
                    <label for="spyfall-definition">Ивент</label>
                    <select id="spyfall-definition" bind:value={definitionId}>
                        {#each definitions as d (d.id)}
                            <option value={d.id}>{d.displayName}</option>
                        {/each}
                    </select>
                </div>
            {/if}

            <h4>Участники ({selectedPlayerIds.size})</h4>
            <div class="players-list">
                {#each gamePlayers as player (player.id)}
                    <label class="player-item">
                        <input
                                type="checkbox"
                                checked={selectedPlayerIds.has(player.id)}
                                on:change={() => togglePlayer(player.id)}
                        />
                        {player.displayName} (⭐ {player.balance})
                    </label>
                {/each}
            </div>

            <div class="actions">
                <button class="btn success" on:click={startGame} disabled={busy || selectedPlayerIds.size < 4}>
                    🎮 Запустить игру
                </button>
            </div>
        {/if}
    </div>
{/if}

<div class="card">
    <h3>Состояние игры</h3>

    {#if !state}
        <p class="hint">Игра ещё не запускалась.</p>
    {:else}
        <p class="line">
            <b>{state.displayName}</b>
            · {state.state === 'Active' ? 'идёт' : 'завершена'}
            · проголосовало {state.votedCount} из {state.citizensCount}
        </p>

        <p class="line">🕵 Шпион: <b>{state.spyName}</b></p>
        <p class="line">🧑‍🌾 Слово горожан: <code>{state.citizenWord}</code></p>
        <p class="line">🕵 Слово шпиона: <code>{state.spyWord}</code></p>

        {#if state.guessUsed}
            <p class="line">
                Попытка шпиона: «{state.guessWord}»
                {state.guessCorrect ? '✅' : '❌'}
            </p>
        {/if}

        {#if state.phase === 'finished'}
            <p class="winner">{winnerLabel(state)}</p>
        {/if}

        <ul class="participants">
            {#each state.participants as participant (participant.userId)}
                <li>
                    {participant.displayName}
                    {#if participant.isSpy}🕵{:else}🧑‍🌾{/if}
                    {#if participant.hasVoted}
                        <span class="voted">проголосовал за {participant.votedForName}</span>
                    {:else if !participant.isSpy}
                        <span class="waiting">ещё не голосовал</span>
                    {/if}
                </li>
            {/each}
        </ul>

        {#if activeGame}
            <div class="actions">
                <button class="btn danger" on:click={finishGame} disabled={busy}>⏹ Завершить игру</button>
            </div>
        {/if}
    {/if}
</div>

<style>
    h2 { margin-bottom: 10px; }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        line-height: 1.5;
        margin-bottom: 16px;
    }

    .card {
        background: var(--bg, #0f0f23);
        border-radius: 8px;
        padding: 16px;
        margin: 16px 0;
        overflow-wrap: anywhere;
    }

    .card h3 { margin-bottom: 10px; }
    .card h4 { margin: 12px 0 8px; }

    .field { margin-bottom: 8px; }
    .field label { display: block; margin-bottom: 6px; color: var(--muted, #aaa); font-size: 14px; }
    .field select {
        width: 100%;
        padding: 10px;
        border-radius: 6px;
        border: 1px solid var(--border, #333);
        background: var(--bg, #0f0f23);
        color: #fff;
    }

    .players-list { margin: 8px 0; }
    .player-item { display: block; margin: 6px 0; cursor: pointer; }

    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
    .btn { padding: 10px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .btn.success { background: var(--green, #27ae60); color: #fff; }
    .btn.danger { background: var(--red, #e74c3c); color: #fff; }

    .line { margin: 4px 0; font-size: 14px; }
    .line code { background: var(--bg-soft, #12122e); padding: 2px 6px; border-radius: 4px; }

    .winner { font-size: 16px; font-weight: bold; color: var(--accent, #f5a623); margin: 10px 0; }

    .participants { list-style: none; margin-top: 10px; line-height: 1.8; }
    .participants .voted { color: var(--green, #27ae60); font-size: 13px; }
    .participants .waiting { color: var(--muted, #aaa); font-size: 13px; }
</style>