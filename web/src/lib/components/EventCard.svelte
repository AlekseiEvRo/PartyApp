<script lang="ts">
    import { api } from '../api';
    import { showToast, balance, user, dareConfirmed, bingoCellConfirmed, raffleDrawn } from '../stores';
    import { serverNow } from '../time';
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

    // Фанты: один фант на игрока, баллы начисляет админ после проверки
    let dareTask: string | null = null;
    let dareStatus: 'none' | 'pending' | 'confirmed' = 'none';
    let darePoints = 0;
    let confirmedDareApplied = false;

    // Бинго
    let bingoSize = 5;
    let bingoCells: string[] = [];
    let bingoMarked = new Set<number>();
    let bingoConfirmed = new Set<number>();
    let bingoLines = 0;
    let bingoRefreshPending = false;

    // Песни по эмодзи
    let songs: any[] = [];
    let songInputs: Record<number, string> = {};
    let songAnswered = new Set<number>();
    let songResults: Record<number, { correct: boolean; answer: string }> = {};

    // Предсказания
    let predictionText = '';
    let predictionSent: string | null = null;
    let predictionPrompt = 'Что случится на вечеринке?';
    let revealedPredictions: any[] | null = null;

    // Лототрон
    let raffleJoined = false;
    let raffleParticipants = 0;
    let raffleWinner: { id: string; name: string } | null = null;

    const dataTypes = ['quiz', 'reaction', 'dare', 'bingo', 'emoji_song', 'predictions', 'raffle'];

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
                applyDare(playerData?.dare ?? null);
            } else if (event.type === 'bingo') {
                applyBingoState(playerData);
            } else if (event.type === 'emoji_song') {
                songs = dataConfig.songs ?? [];
                songAnswered = new Set<number>(playerData?.answered ?? []);
                songInputs = Object.fromEntries(songs.map((_, i) => [i, '']));
            } else if (event.type === 'predictions') {
                predictionPrompt = dataConfig.prompt ?? predictionPrompt;
                predictionSent = playerData?.text ?? null;
                revealedPredictions = data.live?.revealed ?? null;
            } else if (event.type === 'raffle') {
                raffleJoined = playerData?.joined ?? false;
                raffleParticipants = data.live?.participants?.length ?? playerData?.participants ?? 0;
                raffleWinner = data.live?.winner ?? null;
            }
        } catch { /* карточка просто останется без данных */ }
    });

    // Админ подтвердил фант — сразу показываем начисленные баллы
    $: if (!confirmedDareApplied
        && $dareConfirmed
        && $dareConfirmed.sessionId === event.sessionId
        && $dareConfirmed.playerId === $user?.userId) {
        confirmedDareApplied = true;
        applyConfirmedDare($dareConfirmed.points);
    }

    // Админ подтвердил клетку бинго — обновляем сетку и линии
    $: if (event.type === 'bingo'
        && $bingoCellConfirmed
        && $bingoCellConfirmed.sessionId === event.sessionId
        && !bingoRefreshPending) {
        bingoRefreshPending = true;
        showToast('✅ Ведущий подтвердил событие в бинго!', 'info');
        void refreshBingoState().finally(() => (bingoRefreshPending = false));
    }

    // Лототрон: победитель выбран — показываем результат всем
    $: if (event.type === 'raffle'
        && $raffleDrawn
        && $raffleDrawn.sessionId === event.sessionId) {
        raffleWinner = $raffleDrawn.winner;
        raffleParticipants = $raffleDrawn.participants.length;
        showToast(`🏆 Лототрон: победил ${$raffleDrawn.winner.name}!`, 'info');
    }

    async function submit(payload: any) {
        try {
            const data = await api<any>(`/api/events/${event.sessionId}/submit`, 'POST', {
                payloadJson: JSON.stringify(payload)
            });
            const suffix = data.pointsAwarded > 0 ? ` (+${data.pointsAwarded})` : '';
            showToast(`${data.message}${suffix}`, 'success');
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
            const left = Math.ceil((until - serverNow()) / 1000);
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
            reactionLeft = Math.max(0, Math.ceil((startAt - serverNow()) / 1000));
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
            applyDare(result.data);
        }
    }

    /** Применяет состояние фанта из ответа сервера или данных игрока. */
    function applyDare(dare: any): void {
        if (!dare) return;

        dareTask = dare.task;
        dareStatus = dare.status === 'confirmed' ? 'confirmed' : 'pending';
        darePoints = dare.points ?? 0;
    }

    function applyConfirmedDare(points: number): void {
        dareStatus = 'confirmed';
        darePoints = points;
        showToast(`✅ Фант засчитан! +${points}`, 'success');
        refreshBalance();
    }

    async function markBingo(index: number) {
        if (bingoMarked.has(index)) return;

        const result = await submit({ cellIndex: index });
        if (result?.data) {
            bingoMarked = new Set<number>(result.data.markedCells ?? []);
        }
    }

    /** Состояние бинго: отметки, подтверждённые клетки и линии. */
    function applyBingoState(state: any): void {
        bingoSize = dataConfig.size ?? 5;
        bingoCells = dataConfig.cells ?? [];
        bingoMarked = new Set<number>(state?.markedCells ?? []);
        bingoConfirmed = new Set<number>(state?.confirmedCells ?? []);
        bingoLines = state?.lines ?? 0;
    }

    async function refreshBingoState(): Promise<void> {
        try {
            const data = await api<any>(`/api/events/${event.sessionId}/data`);
            applyBingoState(data.player ?? null);
        } catch { /* обновимся при следующем действии */ }
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

    async function submitPrediction() {
        const text = predictionText.trim();
        if (!text) return;

        const result = await submit({ text });
        if (result?.data?.text) {
            predictionSent = result.data.text;
            predictionText = '';
        }
    }

    async function joinRaffle() {
        const result = await submit({});
        if (result?.data?.joined) {
            raffleJoined = true;
            raffleParticipants = result.data.participants ?? raffleParticipants;
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

        {#if dareStatus === 'confirmed'}
            <button class="btn dare-done" disabled>✅ Баллы начислены (+{darePoints})</button>
        {:else if dareStatus === 'pending'}
            <button class="btn" disabled>⏳ Жди подтверждения</button>
        {:else}
            <button class="btn" on:click={drawDare}>🎲 Вытянуть фант</button>
        {/if}

    {:else if event.type === 'bingo'}
        {#if bingoCells.length >= bingoSize * bingoSize}
            <div class="bingo" style="grid-template-columns: repeat({bingoSize}, 1fr)">
                {#each range(bingoSize * bingoSize) as i}
                    <button
                        class="bingo-cell"
                        class:marked={bingoMarked.has(i) && !bingoConfirmed.has(i)}
                        class:confirmed={bingoConfirmed.has(i)}
                        on:click={() => markBingo(i)}
                    >{bingoCells[i]}</button>
                {/each}
            </div>
            <p class="event-counter">
                Подтверждено клеток: {bingoConfirmed.size} · линий: {bingoLines}
            </p>
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

    {:else if event.type === 'predictions'}
        {#if revealedPredictions}
            <p class="desc">🔮 Предсказания раскрыты!</p>
            <ul class="predictions">
                {#each revealedPredictions as prediction}
                    <li><b>{prediction.playerName}</b>: {prediction.text}</li>
                {/each}
            </ul>
        {:else if predictionSent}
            <p class="desc dare-task">🔮 {predictionSent}</p>
            <p class="event-counter">Ждём раскрытия в конце вечеринки</p>
        {:else}
            <p class="desc">{predictionPrompt}</p>
            <div class="row">
                <input type="text" placeholder="Твоё предсказание" maxlength="200" bind:value={predictionText} />
                <button class="btn" on:click={submitPrediction}>🔮 Отправить</button>
            </div>
        {/if}

    {:else if event.type === 'raffle'}
        {#if raffleWinner}
            <p class="desc raffle-winner">🏆 Победитель: {raffleWinner.name}</p>
        {:else if raffleJoined}
            <p class="desc dare-task">🎟 Ты участвуешь!</p>
            <p class="event-counter">Участников: {raffleParticipants} · ждём розыгрыша</p>
        {:else}
            <button class="btn" on:click={joinRaffle}>🎟 Участвовать</button>
            {#if raffleParticipants > 0}
                <p class="event-counter">Уже участвуют: {raffleParticipants}</p>
            {/if}
        {/if}

    {:else}
        <p class="desc">Ивент скоро появится</p>
    {/if}
</div>