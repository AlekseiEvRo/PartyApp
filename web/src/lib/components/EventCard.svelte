<script lang="ts">
    import { api } from '../api';
    import { showToast, balance, user } from '../stores';
    import { onMount } from 'svelte';

    export let event: any;

    let input = '';
    let cooldownUntil: string | null = null;
    let cooldownLeft = 0;
    let questions: any[] = [];
    let answered = new Set<number>();

    onMount(async () => {
        if (event.type === 'quiz') {
            try {
                const data = await api<any>(`/api/events/${event.sessionId}/data`);
                questions = data.config.questions || [];
            } catch {}
        }
    });

    async function submit(payload: any) {
        try {
            const data = await api<any>(`/api/events/${event.sessionId}/submit`, 'POST', {
                payloadJson: JSON.stringify(payload)
            });
            showToast(`${data.message} (+${data.pointsAwarded})`, 'success');
            refreshBalance();
            if (data.data?.busyUntilUtc) startCooldown(data.data.busyUntilUtc);
            return data;
        } catch (e: any) {
            showToast(e.message, 'error');
            return null;
        }
    }

    async function refreshBalance() {
        try {
            const data = await api<any>('/api/wallet/balance');
            balance.set(data.balance);
        } catch {}
    }

    function startCooldown(untilIso: string) {
        cooldownUntil = untilIso;
        const until = new Date(untilIso).getTime();
        const tick = () => {
            const left = Math.ceil((until - Date.now()) / 1000);
            if (left <= 0) {
                cooldownUntil = null;
                cooldownLeft = 0;
            } else {
                cooldownLeft = left;
                setTimeout(tick, 1000);
            }
        };
        tick();
    }

    async function answerQuiz(qIndex: number, aIndex: number) {
        if (answered.has(qIndex)) return;
        const result = await submit({ questionIndex: qIndex, answerIndex: aIndex });
        if (result) answered = new Set([...answered, qIndex]);
    }
</script>

<div class="event-card">
    <h3>{event.displayName}</h3>
    {#if event.description}
        <p class="desc">{event.description}</p>
    {/if}

    {#if event.type === 'quick_checkin'}
        <button class="btn toast-btn" disabled={!!cooldownUntil} on:click={() => submit({ playerName: $user?.displayName })}>
            {cooldownUntil ? `⏳ ${cooldownLeft}с` : '🥂 Сказать тост'}
        </button>

    {:else if event.type === 'promo_code'}
        <div class="row">
            <input type="text" placeholder="Промокод" bind:value={input} />
            <button class="btn" on:click={() => { submit({ code: input.trim() }); input = ''; }}>🎁 Активировать</button>
        </div>

    {:else if event.type === 'word_rush'}
        <div class="row">
            <input type="text" placeholder="Слово с А и Е" bind:value={input} />
            <button class="btn" on:click={() => { submit({ word: input.trim() }); input = ''; }}>📝 Отправить</button>
        </div>

    {:else if event.type === 'qr_scan'}
        <div class="row">
            <input type="text" placeholder="Код с QR" bind:value={input} style="text-transform: uppercase" />
            <button class="btn" on:click={() => { submit({ code: input.trim().toUpperCase() }); input = ''; }}>📷 Активировать</button>
        </div>

    {:else if event.type === 'quiz'}
        {#each questions as q, qi}
            <div class="quiz-q">
                <h4>{qi + 1}. {q.text}</h4>
                {#each q.options as opt, oi}
                    <button
                            class="quiz-opt"
                            disabled={answered.has(qi)}
                            on:click={() => answerQuiz(qi, oi)}
                    >
                        {opt}
                    </button>
                {/each}
            </div>
        {/each}

    {:else}
        <p class="desc">Ивент скоро появится</p>
    {/if}
</div>