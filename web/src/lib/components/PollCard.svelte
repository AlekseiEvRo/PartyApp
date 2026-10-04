<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../api';
    import { pollUpdated, showToast } from '../stores';
    import { pollPercent, type Poll } from '../polls';

    let poll: Poll | null = null;
    let voting = false;

    onMount(async () => {
        try {
            const response = await api<{ poll: Poll | null }>('/api/polls/current');
            poll = response.poll;
        } catch {
            poll = null;
        }
    });

    // Живые обновления из SignalR: счётчики общие, свой выбор сохраняем
    $: if ($pollUpdated) {
        const incoming = $pollUpdated;
        poll = poll && poll.id === incoming.id
            ? { ...incoming, myOptionId: poll.myOptionId }
            : incoming;
    }

    async function vote(optionId: string) {
        if (!poll || voting || poll.status !== 'Open') return;

        voting = true;
        try {
            poll = await api<Poll>(`/api/polls/${poll.id}/vote`, 'POST', { optionId });
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            voting = false;
        }
    }
</script>

{#if poll && poll.status === 'Open'}
    <section class="poll">
        <h2>🗳 {poll.question}</h2>

        <div class="options">
            {#each poll.options as option (option.id)}
                <button
                    class="option"
                    class:chosen={poll.myOptionId === option.id}
                    disabled={voting}
                    on:click={() => vote(option.id)}
                >
                    <span class="bar" style={`width:${pollPercent(option.votes, poll.totalVotes)}%`}></span>
                    <span class="label">{option.displayName}</span>
                    <span class="votes">{option.votes} · {pollPercent(option.votes, poll.totalVotes)}%</span>
                </button>
            {/each}
        </div>

        <p class="hint">
            {poll.myOptionId
                ? 'Твой голос учтён — до закрытия можно передумать'
                : 'Выбери, что играем дальше'}
        </p>
    </section>
{/if}

<style>
    .poll {
        margin-bottom: 24px;
        padding: 16px;
        border-radius: 12px;
        background: var(--card, #1a1a3e);
        border: 1px solid var(--accent, #f5a623);
    }

    .poll h2 { margin-bottom: 12px; font-size: 18px; }

    .options { display: flex; flex-direction: column; gap: 8px; }

    .option {
        position: relative;
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 10px;
        padding: 12px 14px;
        border: 1px solid var(--border, #333);
        border-radius: 10px;
        background: var(--bg-soft, #12122e);
        color: inherit;
        font-size: 15px;
        cursor: pointer;
        overflow: hidden;
        text-align: left;
    }

    .option:disabled { cursor: default; opacity: 0.85; }
    .option.chosen { border-color: var(--accent, #f5a623); }

    .bar {
        position: absolute;
        inset: 0 auto 0 0;
        background: rgba(245, 166, 35, 0.18);
        transition: width 0.3s ease;
        pointer-events: none;
    }

    .label { position: relative; z-index: 1; }
    .votes { position: relative; z-index: 1; color: var(--muted, #aaa); font-size: 13px; white-space: nowrap; }
    .hint { margin-top: 10px; color: var(--muted, #aaa); font-size: 13px; }
</style>
