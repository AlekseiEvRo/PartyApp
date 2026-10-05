<script lang="ts">
    import { api } from '../api';
    import { showToast, balance, user, dareConfirmed, bingoCellConfirmed, bingoCellRejected, bingoLocked, bingoLineAwarded, raffleDrawn } from '../stores';
    import { serverNow } from '../time';
    import { RAFFLE_SPIN_MS } from '../raffle';
    import { getPendingQrCode } from '../qr';
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
    let reactionEnded = false;
    let myReactionMs: number | null = null;
    let myReactionPoints = 0;

    // Фанты: один фант на игрока, баллы начисляет админ после проверки
    let dareTask: string | null = null;
    let dareStatus: 'none' | 'pending' | 'confirmed' = 'none';
    let darePoints = 0;
    let confirmedDareApplied = false;

    // Бинго: выбор предсказаний (1 этап), решения админа (2 этап) и линии
    let bingoSize = 5;
    let bingoCells: string[] = [];
    let bingoMarked = new Set<number>();
    let bingoPending = new Set<number>();
    let bingoPredicted = new Set<number>();
    let bingoRejected = new Set<number>();
    let bingoAllConfirmed = new Set<number>();
    let bingoSelectedCount = 0;
    let bingoMaxPredictions = 12;
    let bingoAnswersLocked = false;
    let bingoBusy = false;
    let bingoLines = 0;
    let bingoWonLines = 0;

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
    let raffleMyTickets: number[] = [];
    let raffleMaxTickets = 1;
    let raffleNextPrice = 0;
    let rafflePlayers = 0;
    let raffleWinnerTicket: number | null = null;
    let raffleBusy = false;
    $: raffleCanBuyMore = raffleJoined && raffleMyTickets.length < raffleMaxTickets;

    const dataTypes = ['quiz', 'reaction', 'dare', 'bingo', 'emoji_song', 'predictions', 'raffle'];

    onMount(async () => {
        // Если код из QR-ссылки не активировался автоматически (например,
        // не было сети), подставляем его — игрок отправит вручную
        if (event.type === 'qr_scan') {
            const pending = getPendingQrCode();
            if (pending) input = pending;
        }

        if (!dataTypes.includes(event.type)) return;

        try {
            const data = await api<any>(`/api/events/${event.sessionId}/data`);
            dataConfig = data.config ?? {};
            playerData = data.player ?? null;

            if (event.type === 'quiz') {
                questions = dataConfig.questions || [];
            } else if (event.type === 'reaction') {
                reacted = playerData?.reacted ?? false;
                myReactionMs = playerData?.elapsedMs ?? null;
                myReactionPoints = playerData?.points ?? 0;
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
                applyRafflePlayer(playerData);
                raffleWinnerTicket = data.live?.winnerTicket ?? null;
                rafflePlayers = data.live?.playersCount ?? playerData?.playersCount ?? 0;
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

    // Админ подтвердил клетку бинго — обновляем сетку и линии.
    // Обрабатываем каждое событие ровно один раз по ссылке на объект из стора:
    // сбрасываемый флаг в условии снова запускал бы этот же блок (бесконечный цикл).
    let handledBingoConfirm: any = null;

    $: if (event.type === 'bingo'
        && $bingoCellConfirmed
        && $bingoCellConfirmed.sessionId === event.sessionId
        && handledBingoConfirm !== $bingoCellConfirmed) {
        handledBingoConfirm = $bingoCellConfirmed;
        showToast('✅ Ведущий подтвердил событие в бинго!', 'info');
        void refreshBingoState();
    }

    // Админ отклонил клетку бинго — событие не состоялось
    let handledBingoReject: any = null;

    $: if (event.type === 'bingo'
        && $bingoCellRejected
        && $bingoCellRejected.sessionId === event.sessionId
        && handledBingoReject !== $bingoCellRejected) {
        handledBingoReject = $bingoCellRejected;
        showToast('🙅 Ведущий: этого события не было', 'info');
        void refreshBingoState();
    }

    // Админ закрыл или вернул приём предсказаний (граница 1 и 2 этапов)
    let handledBingoLock: any = null;

    $: if (event.type === 'bingo'
        && $bingoLocked
        && $bingoLocked.sessionId === event.sessionId
        && handledBingoLock !== $bingoLocked) {
        handledBingoLock = $bingoLocked;
        showToast(
            $bingoLocked.locked
                ? '🔒 Приём предсказаний закрыт'
                : '✏️ Приём предсказаний снова открыт',
            'info'
        );
        void refreshBingoState();
    }

    // Разыграна линия бинго — бонус получил самый быстрый
    let handledBingoLine: any = null;

    $: if (event.type === 'bingo'
        && $bingoLineAwarded
        && $bingoLineAwarded.sessionId === event.sessionId
        && handledBingoLine !== $bingoLineAwarded) {
        handledBingoLine = $bingoLineAwarded;
        handleBingoLineAwarded($bingoLineAwarded.lineAwards);
    }

    // Лототрон: победный номер и уведомление — только после анимации барабана
    $: if (event.type === 'raffle'
        && $raffleDrawn
        && $raffleDrawn.sessionId === event.sessionId) {
        handleRaffleDrawn($raffleDrawn);
    }

    let raffleHandledKey: string | null = null;

    function handleRaffleDrawn(data: {
        sessionId: string;
        winnerTicket: number;
        ticketsCount: number;
    }): void {
        const key = `${data.sessionId}:${data.winnerTicket}`;
        if (raffleHandledKey === key) return;

        raffleHandledKey = key;

        // Даём барабану на экране докрутиться, потом объявляем номер
        setTimeout(() => {
            raffleWinnerTicket = data.winnerTicket;

            if (raffleMyTickets.includes(data.winnerTicket)) {
                showToast(`🎉 Твой билет №${data.winnerTicket} выиграл!`, 'success');
            } else {
                showToast(`🏆 Выиграл билет №${data.winnerTicket}`, 'info');
            }
        }, RAFFLE_SPIN_MS);
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
        const delaySec = dataConfig.delaySec ?? 5;
        const timeLimitSec = dataConfig.timeLimitSec ?? 15;
        const startAt = new Date(event.startedAt).getTime() + delaySec * 1000;
        const endAt = startAt + timeLimitSec * 1000;

        const tick = () => {
            const nowMs = serverNow();
            reactionLeft = Math.max(0, Math.ceil((startAt - nowMs) / 1000));
            reactionEnded = nowMs >= endAt;

            if (!reactionEnded && !reacted) setTimeout(tick, 200);
        };

        tick();
    }

    async function react() {
        const result = await submit({});
        if (result) {
            reacted = true;
            reactionLeft = 0;
            myReactionMs = result.data?.elapsedMs ?? null;
            myReactionPoints = result.pointsAwarded ?? 0;
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
        // bingoBusy защищает от повторных тапов, пока летит запрос: на iOS
        // быстрый двойной тап иначе успевал снять и тут же вернуть выбор
        if (bingoAnswersLocked || bingoBusy) return;

        // До блокировки повторное нажатие по своей клетке снимает выбор
        if (bingoMarked.has(index)) {
            await unmarkBingo(index);
            return;
        }

        if (isBingoCellLocked(index)) return;

        bingoBusy = true;

        try {
            const result = await submit({ cellIndex: index });
            if (result?.data) {
                bingoMarked = new Set<number>(result.data.markedCells ?? []);
                bingoPending = new Set<number>([...bingoPending, index]);
                bingoSelectedCount = result.data.selectedCount ?? result.data.pendingCount ?? bingoMarked.size;
                await refreshBingoState();
            }
        } finally {
            bingoBusy = false;
        }
    }

    async function unmarkBingo(index: number) {
        bingoBusy = true;

        try {
            const data = await api<any>(`/api/events/bingo/${event.sessionId}/marks/${index}`, 'DELETE');
            bingoMarked = new Set<number>(data.markedCells ?? []);
            bingoPending = new Set<number>([...bingoPending].filter((cell) => cell !== index));
            bingoSelectedCount = data.selectedCount ?? bingoMarked.size;
            showToast('Выбор снят — можно выбрать другое событие', 'info');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            bingoBusy = false;
        }
    }

    /** Клетку нельзя выбрать: приём закрыт, событие отклонено или исчерпан лимит выбора. */
    function isBingoCellLocked(index: number): boolean {
        if (bingoAnswersLocked || bingoRejected.has(index)) return true;
        if (bingoMarked.has(index)) return false; // свою отметку можно снять

        return bingoSelectedCount >= bingoMaxPredictions;
    }

    /** Состояние бинго: выбор игрока, решения админа, блокировка и линии. */
    function applyBingoState(state: any): void {
        bingoSize = dataConfig.size ?? 5;
        bingoCells = dataConfig.cells ?? [];
        bingoMarked = new Set<number>(state?.markedCells ?? []);
        bingoPending = new Set<number>(state?.pendingCells ?? []);
        bingoPredicted = new Set<number>(state?.predictionCells ?? state?.confirmedCells ?? []);
        bingoRejected = new Set<number>(state?.rejectedCells ?? []);
        bingoAllConfirmed = new Set<number>(state?.allConfirmedCells ?? []);
        bingoSelectedCount = state?.selectedCount ?? bingoMarked.size;
        bingoMaxPredictions = state?.maxPredictions ?? 12;
        bingoAnswersLocked = state?.locked ?? false;
        bingoLines = state?.lines ?? 0;
        bingoWonLines = state?.wonLines ?? 0;
    }

    function handleBingoLineAwarded(awards: { lineIndex: number; lineLabel: string; playerId: string; amount: number }[]): void {
        const mine = awards.find((award) => award.playerId === $user?.userId);
        if (mine) {
            showToast(`🏆 Твоя линия «${mine.lineLabel}»! +${mine.amount}`, 'success');
        }

        void refreshBingoState();
    }

    // Параллельные обновления (подтверждение + линия + отметка) склеиваем в один запрос
    let bingoRefreshInFlight: Promise<void> | null = null;

    function refreshBingoState(): Promise<void> {
        if (bingoRefreshInFlight) return bingoRefreshInFlight;

        bingoRefreshInFlight = (async () => {
            try {
                const data = await api<any>(`/api/events/${event.sessionId}/data`);
                applyBingoState(data.player ?? null);
            } catch { /* обновимся при следующем действии */ } finally {
                bingoRefreshInFlight = null;
            }
        })();

        return bingoRefreshInFlight;
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
        if (raffleBusy) return;
        if (raffleNextPrice > 0 && !confirm(`Купить ещё один билет за ${raffleNextPrice} баллов?`)) {
            return;
        }

        raffleBusy = true;

        try {
            const result = await submit({});
            if (result?.data?.joined) {
                applyRafflePlayer(result.data);
            }
        } finally {
            raffleBusy = false;
        }
    }

    /** Обновляет билеты игрока из ответа submit или данных игрока. */
    function applyRafflePlayer(data: any): void {
        if (!data) return;

        raffleJoined = data.joined ?? raffleJoined;

        if (Array.isArray(data.myTickets)) {
            raffleMyTickets = data.myTickets;
        }

        if (typeof data.maxTickets === 'number') raffleMaxTickets = data.maxTickets;
        if (typeof data.nextTicketPrice === 'number') raffleNextPrice = data.nextTicketPrice;
        if (typeof data.playersCount === 'number') rafflePlayers = data.playersCount;
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
            <p class="desc">
                ⚡ Твой результат:
                {myReactionMs !== null ? `${(myReactionMs / 1000).toFixed(2)} с` : 'засчитано'}
                {myReactionPoints > 0 ? `(+${myReactionPoints})` : ''}
            </p>
        {:else if reactionEnded}
            <p class="desc">⌛ Время вышло — в этот раз не успел</p>
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
                        class:pending={bingoPending.has(i)}
                        class:won={bingoPredicted.has(i)}
                        class:happened={bingoAllConfirmed.has(i) && !bingoPredicted.has(i)}
                        class:rejected={bingoRejected.has(i)}
                        disabled={isBingoCellLocked(i)}
                        on:click={() => markBingo(i)}
                    >
                        <span class="bingo-cell-text">{bingoCells[i]}</span>
                        {#if bingoPredicted.has(i)}
                            <span class="bingo-badge">🏆</span>
                        {:else if bingoAllConfirmed.has(i)}
                            <span class="bingo-badge">✅</span>
                        {:else if bingoPending.has(i)}
                            <span class="bingo-badge">✓</span>
                        {/if}
                    </button>
                {/each}
            </div>
            <p class="event-counter">
                {#if bingoAnswersLocked}
                    🔒 Приём закрыт · угадано: {bingoPredicted.size} из {bingoSelectedCount}
                {:else}
                    Выбрано: {bingoSelectedCount}/{bingoMaxPredictions}
                {/if}
                · линий: {bingoLines}
                {bingoWonLines > 0 ? ` · выиграно: ${bingoWonLines}` : ''}
            </p>
            <p class="event-counter">
                {#if bingoAnswersLocked}
                    ✅ — событие произошло, 🏆 — твоё сбывшееся предсказание. Ждём решения ведущего.
                {:else}
                    Выбери события, которые, по-твоему, произойдут: не больше {bingoMaxPredictions}.
                    Повторное нажатие снимает выбор.
                {/if}
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
        {#if raffleWinnerTicket !== null}
            {#if raffleMyTickets.includes(raffleWinnerTicket)}
                <p class="desc raffle-winner">🎉 Твой билет №{raffleWinnerTicket} выиграл!</p>
            {:else}
                <p class="desc raffle-winner">🏆 Выиграл билет №{raffleWinnerTicket}</p>
            {/if}
        {:else if raffleJoined}
            <p class="desc dare-task">
                🎟 {raffleMyTickets.length > 1
                    ? `Твои билеты: ${raffleMyTickets.map((n) => `№${n}`).join(', ')}`
                    : `Твой билет: №${raffleMyTickets[0]}`}
            </p>
            {#if raffleCanBuyMore}
                <button class="btn" on:click={joinRaffle} disabled={raffleBusy}>
                    {raffleNextPrice > 0 ? `🎟 Ещё билет за ${raffleNextPrice}` : '🎟 Ещё билет'}
                    {#if raffleMaxTickets > 1}
                        · {raffleMyTickets.length}/{raffleMaxTickets}
                    {/if}
                </button>
            {/if}
            <p class="event-counter">Участников: {rafflePlayers} · ждём розыгрыша</p>
        {:else}
            <button class="btn" on:click={joinRaffle} disabled={raffleBusy}>🎟 Участвовать</button>
            {#if rafflePlayers > 0}
                <p class="event-counter">Уже участвуют: {rafflePlayers}</p>
            {/if}
        {/if}

    {:else}
        <p class="desc">Ивент скоро появится</p>
    {/if}
</div>