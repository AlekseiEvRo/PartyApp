<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { pollUpdated, showToast } from '../../stores';
    import { pollPercent, pollWinnerName, type Poll } from '../../polls';

    interface Definition {
        id: string;
        displayName: string;
        type: string;
        isActive: boolean;
    }

    let poll: Poll | null = null;
    let definitions: Definition[] = [];
    let question = 'Что играем дальше?';
    let selected: string[] = [];
    let creating = false;
    let busy = false;
    let loading = true;

    onMount(load);

    // Живые счётчики голосов
    $: if ($pollUpdated) poll = $pollUpdated;

    async function load() {
        loading = true;
        try {
            const [current, defs] = await Promise.all([
                api<{ poll: Poll | null }>('/api/polls/current'),
                api<Definition[]>('/api/events/definitions')
            ]);
            poll = current.poll;
            definitions = defs;
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            loading = false;
        }
    }

    function toggle(definitionId: string) {
        selected = selected.includes(definitionId)
            ? selected.filter((id) => id !== definitionId)
            : [...selected, definitionId];
    }

    async function createPoll() {
        const trimmed = question.trim();
        if (!trimmed || selected.length < 2) return;

        creating = true;
        try {
            poll = await api<Poll>('/api/polls', 'POST', {
                question: trimmed,
                definitionIds: selected
            });
            selected = [];
            showToast('🗳 Голосование создано', 'info');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            creating = false;
        }
    }

    async function closePoll() {
        if (!poll) return;
        if (!confirm('Закрыть голосование?')) return;

        busy = true;
        try {
            poll = await api<Poll>(`/api/polls/${poll.id}/close`, 'POST');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function startWinner() {
        if (!poll) return;
        if (!confirm(`Запустить «${pollWinnerName(poll) ?? 'победителя'}»?`)) return;

        busy = true;
        try {
            await api(`/api/polls/${poll.id}/start-winner`, 'POST');
            showToast('▶ Ивент запущен', 'info');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function showOnScreen() {
        busy = true;
        try {
            await api('/api/screen/state', 'POST', { mode: 'poll' });
            showToast('🗳 Голосование на экране');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }
</script>

<h2>🗳 Голосование за следующий ивент</h2>

{#if loading}
    <p class="hint">Загрузка…</p>
{:else}
    {#if poll}
        <div class="current">
            <div class="head">
                <strong>{poll.question}</strong>
                <span class="status" class:open={poll.status === 'Open'}>
                    {poll.status === 'Open' ? 'идёт' : 'закрыто'}
                </span>
            </div>

            <ul class="options">
                {#each poll.options as option (option.id)}
                    <li class:winner={poll.status === 'Closed' && poll.winnerOptionId === option.id}>
                        <span class="name">{option.displayName}</span>
                        <span class="bar"><i style={`width:${pollPercent(option.votes, poll.totalVotes)}%`}></i></span>
                        <span class="votes">{option.votes} · {pollPercent(option.votes, poll.totalVotes)}%</span>
                    </li>
                {/each}
            </ul>

            <p class="hint">
                Голосов: {poll.totalVotes}
                {#if pollWinnerName(poll)} · победитель: «{pollWinnerName(poll)}»{/if}
            </p>

            <div class="actions">
                <button class="btn" on:click={showOnScreen} disabled={busy}>🖥 На экран</button>
                {#if poll.status === 'Open'}
                    <button class="btn danger" on:click={closePoll} disabled={busy}>🏁 Закрыть</button>
                {:else if poll.winnerOptionId}
                    <button class="btn accent" on:click={startWinner} disabled={busy}>▶ Запустить победителя</button>
                {/if}
                <button class="btn" on:click={load} disabled={busy}>🔄 Обновить</button>
            </div>
        </div>
    {:else}
        <p class="hint">Голосований пока не было. Создай первое — игроки увидят его на телефонах.</p>
    {/if}

    <div class="block">
        <p class="group-label">Новое голосование</p>
        <input class="question" type="text" maxlength="200" bind:value={question}
               placeholder="Например: Что играем дальше?" />

        <div class="definitions">
            {#each definitions as definition (definition.id)}
                <label class="definition">
                    <input type="checkbox" checked={selected.includes(definition.id)}
                           on:change={() => toggle(definition.id)} />
                    <span>{definition.displayName}</span>
                </label>
            {/each}
        </div>

        <div class="actions">
            <button class="btn accent" on:click={createPoll}
                    disabled={creating || question.trim().length === 0 || selected.length < 2 || selected.length > 8}>
                {creating ? 'Создаём…' : `➕ Создать (выбрано ${selected.length})`}
            </button>
            <span class="hint">От 2 до 8 вариантов. Новое голосование закрывает предыдущее.</span>
        </div>
    </div>
{/if}

<style>
    .current {
        padding: 16px;
        border-radius: 12px;
        background: var(--bg-soft, #12122e);
        border: 1px solid var(--accent, #f5a623);
    }

    .head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 12px; }
    .status { color: var(--muted, #aaa); font-size: 13px; }
    .status.open { color: var(--green, #27ae60); font-weight: bold; }

    .options { list-style: none; padding: 0; margin: 0 0 10px; display: flex; flex-direction: column; gap: 8px; }

    .options li {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 8px 12px;
        border-radius: 8px;
        background: var(--card-soft, #2a2a5e);
    }

    .options li.winner { border: 1px solid var(--green, #27ae60); }
    .name { flex: 1; min-width: 0; overflow-wrap: anywhere; }

    .bar {
        width: 140px;
        height: 10px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.12);
        overflow: hidden;
        flex-shrink: 0;
    }

    .bar i { display: block; height: 100%; background: var(--accent, #f5a623); transition: width 0.3s ease; }
    .votes { color: var(--muted, #aaa); font-size: 13px; white-space: nowrap; }

    .actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 10px; }

    .btn {
        padding: 8px 14px;
        border: none;
        border-radius: 8px;
        background: var(--blue, #3498db);
        color: #fff;
        font-size: 13px;
        font-weight: bold;
        cursor: pointer;
    }

    .btn.accent { background: var(--accent, #f5a623); color: #12122e; }
    .btn.danger { background: var(--red, #e74c3c); }
    .btn:disabled { opacity: 0.5; cursor: default; }

    .block { margin-top: 24px; }
    .group-label { color: var(--muted, #aaa); font-size: 13px; margin-bottom: 8px; }

    .question {
        width: 100%;
        box-sizing: border-box;
        background: var(--bg-soft, #12122e);
        border: 1px solid #2a2a5e;
        border-radius: 8px;
        color: inherit;
        padding: 10px 12px;
        font-size: 14px;
    }

    .definitions {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
        gap: 6px;
        margin: 10px 0;
    }

    .definition {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 8px 10px;
        border-radius: 8px;
        background: var(--bg-soft, #12122e);
        font-size: 14px;
        cursor: pointer;
    }

    .hint { color: var(--muted, #aaa); font-size: 13px; }
</style>
