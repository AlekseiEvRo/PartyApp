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

    let dataConfig: any = {};
    let playerData: any = null;

    // «Кто быстрее»
    let reactionLeft = 0;
    let reacted = false;

    // Фанты
    let dareTask: string | null = null;
    let dareCompleted = 0;
    let dareTotal = 0;

    // Бинго
    let bingoSize = 5;
    let bingoCells: string[] = [];
    let bingoMarked = new Set<number>();
    let bingoLines = 0;

    // Песни по эмодзи
    let songs: any[] = [];
    let songInputs: Record<number, string> = {};
    let songAnswered = new Set<number>();
    let songResults: Record<number, { correct: boolean; answer: string }> = {};

    const dataTypes = ['quiz', 'reaction', 'dare', 'bingo', 'emoji_song'];

    onMount(async () => {
        if (!dataTypes.includes(event.type)) return;

        try {
            const data = await api<any>(`/api/events/${event.sessionId}/data`);
            dataConfig = data.config ?? {};
            playerData = data.player ?? null;

            if (event.type === 'quiz') {
                questions = dataConfig.questions || [];
            } else if (event.type === 'reaction') {
                reacted = playerData?.reacted ?? false;
                startReactionCountdown();
            } else if (event.type === 'dare') {
                dareCompleted = playerData?.completed ?? 0;
                dareTotal = playerData?.total ?? (dataConfig.tasks?.length ?? 0);
            } else if (event.type === 'bingo') {
                bingoSize = dataConfig.size ?? 5;
                bingoCells = dataConfig.cells ?? [];
                bingoMarked = new Set<number>(playerData?.markedCells ?? []);
                bingoLines = playerData?.lines ?? 0;
            } else if (event.type === 'emoji_song') {
                songs = dataConfig.songs ?? [];
                songAnswered = new Set<number>(playerData?.answered ?? []);
                songInputs = Object.fromEntries(songs.map((_, i) => [i, '']));
            }
        } catch { /* карточка просто останется без данных */ }
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

    function startReactionCountdown() {
        const startAt = new Date(event.startedAt).getTime() + (dataConfig.delaySec ?? 5) * 1000;

        const tick = () => {
            reactionLeft = Math.max(0, Math.ceil((startAt - Date.now()) / 1000));
            if (reactionLeft > 0) setTimeout(tick, 200);
        };

        tick();
    }

    async function react() {
        const result = await submit({});
        if (result) {
            reacted = true;
            reactionLeft = 0;
        }
    }

    async function drawDare() {
        const result = await submit({});
        if (result?.data) {
            dareTask = result.data.task;
            dareCompleted = result.data.completed;
            dareTotal = result.data.total;
        }
    }

    async function markBingo(index: number) {
        if (bingoMarked.has(index)) return;

        const result = await submit({ cellIndex: index });
        if (result?.data) {
            bingoMarked = new Set<number>(result.data.markedCells ?? []);
            bingoLines = result.data.lines ?? bingoLines;
        }
    }

    async function answerSong(index: number) {
        if (songAnswered.has(index)) return;

        const answer = (songInputs[index] ?? '').trim();
        if (!answer) return;

        const result = await submit({ songIndex: index, answer });

        songAnswered = new Set([...songAnswered, index]);
        songInputs = { ...songInputs, [index]: '' };

        if (result?.data) {
            songResults = {
                ...songResults,
                [index]: { correct: !!result.data.correct, answer: result.data.answer }
            };
        }
    }

    function range(count: number): number[] {
        return Array.from({ length: count }, (_, i) => i);
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

    {:else if event.type === 'reaction'}
        {#if reacted}
            <p class="desc">⚡ Ты уже нажал! Ждём остальных.</p>
        {:else}
            <button class="btn reaction-btn" disabled={reactionLeft > 0} on:click={react}>
                {reactionLeft > 0 ? `⏳ ${reactionLeft}с` : '⚡ ЖМИ!'}
            </button>
        {/if}

    {:else if event.type === 'dare'}
        {#if dareTask}
            <p class="desc dare-task">🎭 {dareTask}</p>
        {/if}
        <div class="row">
            <button class="btn" on:click={drawDare}>🎲 Вытянуть фант</button>
            {#if dareTotal > 0}
                <span class="event-counter">Выполнено: {dareCompleted}/{dareTotal}</span>
            {/if}
        </div>

    {:else if event.type === 'bingo'}
        {#if bingoCells.length >= bingoSize * bingoSize}
            <div class="bingo" style="grid-template-columns: repeat({bingoSize}, 1fr)">
                {#each range(bingoSize * bingoSize) as i}
                    <button
                        class="bingo-cell"
                        class:marked={bingoMarked.has(i)}
                        on:click={() => markBingo(i)}
                    >{bingoCells[i]}</button>
                {/each}
            </div>
            <p class="event-counter">Собрано линий: {bingoLines}</p>
        {/if}

    {:else if event.type === 'emoji_song'}
        {#each songs as song, i}
            <div class="song">
                <div class="song-emoji">{song.emoji}</div>

                {#if songAnswered.has(i)}
                    <div class="song-result" class:wrong={songResults[i] && !songResults[i].correct}>
                        {#if songResults[i]}
                            {songResults[i].correct ? '✅' : '❌'} {songResults[i].answer}
                        {:else}
                            Попытка использована
                        {/if}
                    </div>
                {:else}
                    <div class="row">
                        <input type="text" placeholder="Название песни" bind:value={songInputs[i]} />
                        <button class="btn" on:click={() => answerSong(i)}>🎵</button>
                    </div>
                    {#if song.hint}
                        <span class="event-counter">Подсказка: {song.hint}</span>
                    {/if}
                {/if}
            </div>
        {/each}

    {:else}
        <p class="desc">Ивент скоро появится</p>
    {/if}
</div>