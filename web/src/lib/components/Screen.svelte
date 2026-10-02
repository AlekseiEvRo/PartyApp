<script lang="ts">
    import { onDestroy, onMount } from 'svelte';
    import { crossfade } from 'svelte/transition';
    import { api } from '../api';
    import { connect, getConnection, reconnectIfNeeded } from '../signalr';
    import { loadPhotoUrl, releasePhotoUrls } from '../photos';
    import { RAFFLE_SPIN_MS } from '../raffle';

    interface ScreenState {
        mode: string;
        sessionId: string | null;
        message: string | null;
        version: number;
        serverTimeUtc: string;
    }

    interface AvailableEvent {
        sessionId: string;
        type: string;
        displayName: string;
        description?: string | null;
        startedAt: string;
    }

    interface LeaderboardEntry {
        id: string;
        displayName: string;
        balance: number;
    }

    interface EventData {
        sessionId: string;
        type: string;
        displayName: string;
        config: Record<string, any>;
        live?: Record<string, any> | null;
        player?: Record<string, any> | null;
    }

    interface ShopItem {
        id: string;
        name: string;
        description: string | null;
        price: number;
        stock: number | null;
        isActive: boolean;
    }

    interface ScreenSettings {
        photoSeconds: number;
        leaderboardSeconds: number;
        shopSeconds: number;
    }

    interface ShopLot {
        id: string;
        name: string;
        minBid: number;
        endsAt: string;
        bidsCount: number;
        topBid: number | null;
        leaderName: string | null;
    }

    interface RaffleParticipant {
        id: string;
        name: string;
    }

    interface Photo {
        id: string;
        uploadedByName: string;
        status: string;
    }

    interface Slide {
        id: string;
        url: string;
        author: string;
    }

    interface Reaction {
        id: string;
        emoji: string | null;
        text: string | null;
        authorName: string;
        createdAt: string;
    }

    interface FloatingReaction extends Reaction {
        left: number;
        bottom: number;
        duration: number;
        delay: number;
        rotation: number;
        scale: number;
        drift: number;
    }

    interface ConfettiPiece {
        id: number;
        left: number;
        delay: number;
        duration: number;
        emoji: string;
        size: number;
    }

    const slideIntervalMs = 8000;
    const refreshIntervalMs = 5000;
    const maxReactionsOnScreen = 40;

    const [sendSlide, receiveSlide] = crossfade({ duration: 900 });

    let state: ScreenState = {
        mode: 'idle',
        sessionId: null,
        message: null,
        version: 0,
        serverTimeUtc: new Date().toISOString()
    };

    // Локальный автопереход на свежий ивент. Сбрасывается, когда админ
    // сам переключает режим (приходит ScreenUpdated).
    let forced: { mode: string; sessionId?: string } | null = null;

    // Разница между серверным и клиентским временем: таймеры считаем от сервера
    let clockOffset = 0;
    let now = Date.now();

    let leaderboard: LeaderboardEntry[] = [];
    let eventInfo: AvailableEvent | null = null;
    let eventData: EventData | null = null;
    let slides: Slide[] = [];
    let slideIndex = 0;
    let lastSlideAt = 0;
    let confetti: ConfettiPiece[] = [];
    let reactions: FloatingReaction[] = [];
    let reactionTimers: ReturnType<typeof setTimeout>[] = [];
    let shopItems: ShopItem[] = [];
    let lots: ShopLot[] = [];

    // Лототрон
    const wheelColors = ['#f5a623', '#e74c3c', '#3498db', '#27ae60', '#9b59b6', '#e67e22', '#1abc9c', '#e84393'];
    let raffleParticipants: RaffleParticipant[] = [];
    let raffleWinnerId: string | null = null;
    let wheelRotation = 0;
    let wheelSpunFor: string | null = null;
    let wheelSettled = false;
    let wheelTimer: ReturnType<typeof setTimeout> | undefined;

    // Настройки ротации секций (таймауты задаются в админке)
    let settings: ScreenSettings = { photoSeconds: 8, leaderboardSeconds: 60, shopSeconds: 60 };
    let rotationPhase = 'photos';
    let rotationPhaseStartedAt = 0;
    let rotationActive = false;

    let clockTimer: ReturnType<typeof setInterval>;
    let refreshTimer: ReturnType<typeof setInterval>;
    let confettiTimer: ReturnType<typeof setTimeout> | undefined;
    let balanceHandler: (() => void) | null = null;

    $: displayMode = forced?.mode ?? state.mode;
    $: displaySessionId = forced?.sessionId ?? state.sessionId;
    $: currentView = displayMode === 'rotation' ? rotationPhase : displayMode;
    $: remainingSeconds = computeRemaining(now);
    $: toastRemaining = computeToastRemaining(now);
    $: reactionStartsIn = computeReactionStartsIn(now);
    $: reactionEnded = computeReactionEnded(now);
    $: currentSlide = slides.length > 0 ? slides[slideIndex % slides.length] : null;

    // Не показываем данные предыдущего ивента, пока грузятся данные нового
    $: if (eventData && eventData.sessionId !== displaySessionId) {
        eventData = null;
    }

    // Листаем слайдшоу
    $: if (displayMode === 'photos' && slides.length > 1 && now - lastSlideAt >= slideIntervalMs) {
        lastSlideAt = now;
        slideIndex = (slideIndex + 1) % slides.length;
    }

    // Лототрон: следим за участниками и запускаем колесо один раз на победителя
    $: if (displayMode === 'event' && eventData?.type === 'raffle') {
        syncRaffle(eventData.live);
    }

    function syncRaffle(live: any): void {
        const participants: RaffleParticipant[] = live?.participants ?? [];
        if (participants.length !== raffleParticipants.length) {
            raffleParticipants = participants;
        }

        const winner = live?.winner ?? null;
        if (winner && wheelSpunFor !== winner.id) {
            wheelSpunFor = winner.id;
            raffleWinnerId = winner.id;
            spinWheel(winner.id);
        }
    }

    function spinWheel(winnerId: string): void {
        const index = raffleParticipants.findIndex((p) => p.id === winnerId);
        if (index < 0 || raffleParticipants.length === 0) return;

        const segment = 360 / raffleParticipants.length;
        const center = (index + 0.5) * segment;
        const target = 360 * 4 + ((360 - center) % 360);

        wheelSettled = false;
        wheelRotation += target;

        clearTimeout(wheelTimer);
        wheelTimer = setTimeout(() => (wheelSettled = true), RAFFLE_SPIN_MS + 200);
    }

    function wheelColor(index: number): string {
        return wheelColors[index % wheelColors.length];
    }

    function wheelGradient(count: number): string {
        if (count <= 1) return wheelColors[0];

        const step = 100 / count;
        const parts: string[] = [];

        for (let i = 0; i < count; i++) {
            parts.push(`${wheelColor(i)} ${i * step}% ${(i + 1) * step}%`);
        }

        return `conic-gradient(${parts.join(', ')})`;
    }

    // === Ротация секций: фото → лидерборд → магазин → ... ===
    $: if (displayMode === 'rotation' && !rotationActive) {
        startRotation();
    }

    $: if (displayMode !== 'rotation' && rotationActive) {
        rotationActive = false;
    }

    $: if (rotationActive && now - rotationPhaseStartedAt >= rotationPhaseDurationMs()) {
        advanceRotation();
    }

    // В фазе фото листаем их по таймауту из настроек
    $: if (rotationActive && rotationPhase === 'photos' && slides.length > 0) {
        const secondsIntoPhase = Math.floor((now - rotationPhaseStartedAt) / 1000);
        slideIndex = Math.floor(secondsIntoPhase / Math.max(1, settings.photoSeconds)) % slides.length;
    }

    onMount(async () => {
        try {
            await connect();
        } catch (e) {
            console.error('Screen: SignalR connect failed', e);
        }

        const connection = getConnection();
        if (connection) {
            connection.on('ScreenUpdated', onScreenUpdated);
            connection.on('ScreenConfetti', onConfetti);
            connection.on('ScreenReaction', onReaction);
            connection.on('EventStarted', onEventStarted);
            connection.on('EventFinished', onEventFinished);
            connection.on('EventLiveUpdated', onEventLiveUpdated);
            connection.on('ScreenSettingsUpdated', onScreenSettingsUpdated);
            connection.on('BidPlaced', onBidPlaced);
            connection.on('LotStarted', onLotStarted);
            connection.on('RaffleDrawn', onRaffleDrawn);

            balanceHandler = () => {
                if (displayMode === 'leaderboard') void loadLeaderboard();
            };
            connection.on('BalanceUpdated', balanceHandler);
        }

        clockTimer = setInterval(() => (now = Date.now()), 1000);
        refreshTimer = setInterval(() => void refreshContent(true), refreshIntervalMs);

        try {
            await loadState();
            await loadSettings();
        } catch (e) {
            console.error('Screen: не удалось получить состояние', e);
        }
    });

    onDestroy(() => {
        clearInterval(clockTimer);
        clearInterval(refreshTimer);
        clearTimeout(confettiTimer);
        for (const timer of reactionTimers) clearTimeout(timer);

        const connection = getConnection();
        if (connection) {
            connection.off('ScreenUpdated', onScreenUpdated);
            connection.off('ScreenConfetti', onConfetti);
            connection.off('ScreenReaction', onReaction);
            connection.off('EventStarted', onEventStarted);
            connection.off('EventFinished', onEventFinished);
            connection.off('EventLiveUpdated', onEventLiveUpdated);
            connection.off('ScreenSettingsUpdated', onScreenSettingsUpdated);
            connection.off('BidPlaced', onBidPlaced);
            connection.off('LotStarted', onLotStarted);
            connection.off('RaffleDrawn', onRaffleDrawn);
            clearTimeout(wheelTimer);
            if (balanceHandler) connection.off('BalanceUpdated', balanceHandler);
        }

        releasePhotoUrls();
    });

    async function loadState() {
        const fetched = await api<ScreenState>('/api/screen/state');

        // Пока запрос летел, могло прийти более свежее состояние по SignalR
        if (fetched.version < state.version) return;

        state = fetched;
        clockOffset = new Date(fetched.serverTimeUtc).getTime() - Date.now();
        await refreshContent();
    }

    function onScreenUpdated(updated: ScreenState) {
        // Админ сам выбрал режим — следуем за сервером
        forced = null;
        state = updated;
        clockOffset = new Date(updated.serverTimeUtc).getTime() - Date.now();
        void refreshContent();
    }

    function onEventStarted(event: {
        sessionId: string;
        type: string;
        displayName: string;
        description?: string | null;
        startedAt: string;
    }) {
        // Новый ивент показываем автоматически, даже если админ не переключил экран
        forced = { mode: 'event', sessionId: event.sessionId };
        eventData = null;
        eventInfo = {
            sessionId: event.sessionId,
            type: event.type,
            displayName: event.displayName,
            description: event.description ?? null,
            startedAt: event.startedAt
        };
        void loadEvent();
    }

    function onEventLiveUpdated(data: { sessionId: string; live: EventData['live'] }) {
        if (data.sessionId !== displaySessionId || !eventData) return;

        eventData = { ...eventData, live: data.live };
    }

    function onEventFinished(data: { sessionId: string }) {
        if (displaySessionId !== data.sessionId) return;

        // Ивент закончился — показываем лидерборд с результатами
        forced = { mode: 'leaderboard' };
        void loadLeaderboard();
    }

    async function refreshContent(checkConnection = false) {
        try {
            if (checkConnection) await reconnectIfNeeded();

            if (currentView === 'leaderboard') await loadLeaderboard();
            else if (currentView === 'event') await loadEvent();
            else if (currentView === 'photos') await loadPhotos();
            else if (currentView === 'shop') await loadShopItems();
            else if (currentView === 'lots') await loadLots();
        } catch (e) {
            console.error('Screen: не удалось обновить содержимое', e);
        }
    }

    async function loadLeaderboard() {
        leaderboard = await api<LeaderboardEntry[]>('/api/admin/leaderboard');
    }

    async function loadEvent() {
        const sessionId = displaySessionId;
        if (!sessionId) return;

        const [data, available] = await Promise.all([
            api<EventData>(`/api/events/${sessionId}/data`),
            api<AvailableEvent[]>('/api/events/available')
        ]);

        // Пока грузилось, экран могли переключить на другой ивент
        if (displaySessionId !== sessionId || displayMode !== 'event') return;

        eventData = data;
        eventInfo = available.find((e) => e.sessionId === sessionId)
            ?? (eventInfo?.sessionId === sessionId ? eventInfo : null);
    }

    async function loadPhotos() {
        const page = await api<{ items: Photo[] }>('/api/photos?limit=100');
        const approved = page.items.filter((p) => p.status === 'Approved');

        const next: Slide[] = [];
        for (const photo of approved) {
            try {
                const url = await loadPhotoUrl(photo.id);
                next.push({ id: photo.id, url, author: photo.uploadedByName });
            } catch {
                // Фото могли удалить между запросами — просто пропускаем
            }
        }

        slides = next;
        if (slideIndex >= slides.length) slideIndex = 0;
    }

    async function loadShopItems() {
        shopItems = await api<ShopItem[]>('/api/shop/items');
    }

    async function loadLots() {
        lots = await api<ShopLot[]>('/api/shop/lots');
    }

    function onBidPlaced() {
        if (currentView === 'lots') void loadLots();
    }

    function onLotStarted() {
        if (currentView === 'lots') void loadLots();
    }

    function onRaffleDrawn() {
        if (displayMode === 'event') void loadEvent();
    }

    async function loadSettings() {
        settings = await api<ScreenSettings>('/api/screen/settings');
    }

    function onScreenSettingsUpdated(updated: ScreenSettings) {
        settings = updated;
    }

    function startRotation() {
        rotationActive = true;
        rotationPhase = 'photos';
        rotationPhaseStartedAt = Date.now();
        void refreshContent();
    }

    function advanceRotation() {
        rotationPhase = rotationPhase === 'photos'
            ? 'leaderboard'
            : rotationPhase === 'leaderboard' ? 'shop' : 'photos';
        rotationPhaseStartedAt = Date.now();
        void refreshContent();
    }

    function rotationPhaseDurationMs(): number {
        if (rotationPhase === 'photos') {
            // Минимум один интервал — даём фотографиям время загрузиться
            return Math.max(slides.length, 1) * Math.max(1, settings.photoSeconds) * 1000;
        }

        if (rotationPhase === 'leaderboard') {
            return Math.max(5, settings.leaderboardSeconds) * 1000;
        }

        return Math.max(5, settings.shopSeconds) * 1000;
    }

    function onConfetti() {
        const emojis = ['🎉', '🎊', '✨', '🥳', '🎈', '⭐'];

        confetti = Array.from({ length: 70 }, (_, i) => ({
            id: Date.now() + i,
            left: Math.random() * 100,
            delay: Math.random() * 0.6,
            duration: 2.5 + Math.random() * 2.5,
            emoji: emojis[i % emojis.length],
            size: 22 + Math.random() * 40
        }));

        clearTimeout(confettiTimer);
        confettiTimer = setTimeout(() => (confetti = []), 5200);
    }

    function onReaction(reaction: Reaction) {
        const piece: FloatingReaction = {
            ...reaction,
            left: 6 + Math.random() * 84,
            bottom: 6 + Math.random() * 30,
            duration: 4.5 + Math.random() * 3,
            delay: Math.random() * 0.4,
            rotation: -18 + Math.random() * 36,
            scale: 0.9 + Math.random() * 0.8,
            drift: -90 + Math.random() * 180
        };

        reactions = [...reactions.slice(-(maxReactionsOnScreen - 1)), piece];

        const timer = setTimeout(() => {
            reactions = reactions.filter((r) => r.id !== piece.id);
            reactionTimers = reactionTimers.filter((t) => t !== timer);
        }, (piece.delay + piece.duration + 0.5) * 1000);

        reactionTimers.push(timer);
    }

    function computeRemaining(clientNow: number): number | null {
        if (displayMode !== 'event' || !eventInfo || !eventData) return null;
        if (eventInfo.sessionId !== displaySessionId) return null;

        const timeLimit = Number(eventData.config?.timeLimitSec ?? 0);
        if (!timeLimit) return null;

        const startedAt = new Date(eventInfo.startedAt).getTime();
        const elapsed = (clientNow + clockOffset - startedAt) / 1000;

        return Math.max(0, Math.ceil(timeLimit - elapsed));
    }

    /** Сколько секунд осталось говорить тост (quick_checkin), либо null. */
    function computeToastRemaining(clientNow: number): number | null {
        if (displayMode !== 'event' || eventData?.type !== 'quick_checkin') return null;

        const busyUntil = eventData.live?.busyUntilUtc;
        if (!busyUntil) return null;

        const remaining = (new Date(busyUntil).getTime() - (clientNow + clockOffset)) / 1000;
        return remaining > 0 ? Math.ceil(remaining) : null;
    }

    /** Сколько секунд осталось до сигнала в «Кто быстрее», либо 0. */
    function computeReactionStartsIn(clientNow: number): number {
        if (displayMode !== 'event' || eventData?.type !== 'reaction') return 0;

        const startsAt = eventData.live?.startsAtUtc;
        if (!startsAt) return 0;

        const diff = new Date(startsAt).getTime() - (clientNow + clockOffset);
        return Math.max(0, Math.ceil(diff / 1000));
    }

    /** Закончилось ли окно нажатий в «Кто быстрее». */
    function computeReactionEnded(clientNow: number): boolean {
        if (displayMode !== 'event' || eventData?.type !== 'reaction') return false;

        const endsAt = eventData.live?.endsAtUtc;
        if (!endsAt) return false;

        return clientNow + clockOffset >= new Date(endsAt).getTime();
    }

    function formatClock(timestamp: number): string {
        return new Date(timestamp).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    }

    function formatSeconds(value: number): string {
        const minutes = Math.floor(value / 60);
        const seconds = value % 60;
        return minutes > 0 ? `${minutes}:${seconds.toString().padStart(2, '0')}` : `${seconds}`;
    }

    /** Сколько осталось до конца приёма ставок. */
    function formatLotsRemaining(endsAt: string): string {
        const leftMs = new Date(endsAt).getTime() - (now + clockOffset);
        if (leftMs <= 0) return 'приём закрыт';

        const totalSeconds = Math.ceil(leftMs / 1000);
        const minutes = Math.floor(totalSeconds / 60);
        const seconds = totalSeconds % 60;

        return minutes > 0
            ? `${minutes} мин ${seconds.toString().padStart(2, '0')} с`
            : `${seconds} с`;
    }

    // Обёртка для crossfade: слайд всегда один, но анимации нужен keyed each
    function asList(slide: Slide | null): Slide[] {
        return slide ? [slide] : [];
    }
</script>

<div class="screen">
    <header class="topbar">
        <span class="brand">🎉 PartyApp</span>
        <span class="clock">{formatClock(now)}</span>
    </header>

    {#if currentView === 'idle'}
        <main class="center">
            <div class="idle-emoji">🎉</div>
            <h1>Скоро начнём!</h1>
            <p class="muted">Следи за приложением — ивенты появятся здесь</p>
        </main>
    {:else if currentView === 'leaderboard'}
        <main class="leaderboard">
            <h1>🏆 Лидерборд</h1>

            {#if leaderboard.length === 0}
                <p class="muted">Пока нет баллов</p>
            {:else}
                <ol>
                    {#each leaderboard.slice(0, 10) as entry, i (entry.id)}
                        <li class:top={i < 3}>
                            <span class="place">{i === 0 ? '🥇' : i === 1 ? '🥈' : i === 2 ? '🥉' : i + 1}</span>
                            <span class="name">{entry.displayName}</span>
                            <span class="points">{entry.balance}</span>
                        </li>
                    {/each}
                </ol>
            {/if}
        </main>
    {:else if currentView === 'event'}
        <main class="event">
            <h1>{eventData?.displayName ?? eventInfo?.displayName ?? 'Ивент'}</h1>

            {#if eventInfo?.description}
                <p class="description">{eventInfo.description}</p>
            {/if}

            {#if remainingSeconds !== null && eventData?.type !== 'reaction'}
                <div class="timer" class:over={remainingSeconds === 0}>
                    {remainingSeconds > 0 ? formatSeconds(remainingSeconds) : 'Время вышло'}
                </div>
            {/if}

            {#if eventData?.type === 'quiz' && eventData.config?.questions?.length}
                <div class="questions">
                    {#each eventData.config.questions as question}
                        <article>
                            <h2>{question.text}</h2>
                            <div class="options">
                                {#each question.options as option}
                                    <span>{option}</span>
                                {/each}
                            </div>
                        </article>
                    {/each}
                </div>
            {:else if eventData?.type === 'word_rush'}
                <div class="letters">
                    {#each eventData.config?.requiredLetters ?? [] as letter}
                        <span>{letter}</span>
                    {/each}
                </div>
                <p class="muted">Слова от {eventData.config?.minWordLength ?? 3} букв</p>
            {:else if eventData?.type === 'quick_checkin'}
                {#if toastRemaining !== null && eventData.live?.speakerName}
                    <div class="speaker">
                        <span class="speaker-name">🎤 {eventData.live.speakerName} говорит тост</span>
                        <span class="speaker-timer">{formatSeconds(toastRemaining)}</span>
                    </div>
                {:else}
                    <p class="muted">Нажми кнопку в приложении — расскажи тост!</p>
                {/if}
            {:else if eventData?.type === 'reaction'}
                {#if reactionEnded}
                    <h2>⚡ Результаты реакции</h2>
                    {#if eventData.live?.results?.length}
                        <ul class="reaction-results">
                            {#each eventData.live.results as row, i}
                                <li>
                                    <span class="place">
                                        {i === 0 ? '🥇' : i === 1 ? '🥈' : i === 2 ? '🥉' : i + 1}
                                    </span>
                                    <span class="item-name">{row.playerName}</span>
                                    <span class="reaction-time">{(row.elapsedMs / 1000).toFixed(2)} с</span>
                                    <span class="points">+{row.points}</span>
                                </li>
                            {/each}
                        </ul>
                    {:else}
                        <p class="muted">Никто не успел нажать</p>
                    {/if}
                {:else if reactionStartsIn > 0}
                    <p class="muted">Приготовься…</p>
                    <div class="timer">{reactionStartsIn}</div>
                {:else}
                    <div class="reaction-live">⚡ ЖМИ!</div>
                    <p class="muted">Уже нажали: {eventData.live?.reactedCount ?? 0}</p>
                {/if}
            {:else if eventData?.type === 'dare'}
                <p class="muted">
                    🎲 Ждут проверки: {eventData.live?.pending ?? 0} · выполнено: {eventData.live?.confirmed ?? 0}
                </p>
            {:else if eventData?.type === 'bingo'}
                <p class="muted">
                    Отмечено клеток: {eventData.live?.markedCount ?? 0}
                    · подтверждено: {eventData.live?.confirmedCount ?? 0}
                    · играют: {eventData.live?.playersCount ?? 0}
                </p>
            {:else if eventData?.type === 'emoji_song'}
                <div class="song-grid">
                    {#each eventData.live?.songs ?? [] as song}
                        <div class="song-card">
                            <span class="song-emoji">{song.emoji}</span>
                            <span class="song-hint">{song.hint}</span>
                        </div>
                    {/each}
                </div>
                <p class="muted">
                    Угадано: {eventData.live?.guessedCount ?? 0} · играют: {eventData.live?.playersCount ?? 0}
                </p>
            {:else if eventData?.type === 'predictions'}
                {#if eventData.live?.revealed?.length}
                    <h2>🔮 Предсказания</h2>
                    <ul class="predictions">
                        {#each eventData.live.revealed.slice(0, 10) as prediction}
                            <li><b>{prediction.playerName}</b>: {prediction.text}</li>
                        {/each}
                    </ul>
                {:else}
                    <p class="muted">🔮 Собрано предсказаний: {eventData.live?.count ?? 0}</p>
                {/if}
            {:else if eventData?.type === 'raffle'}
                {#if raffleParticipants.length > 0}
                    <div class="wheel-wrap">
                        <div class="wheel-pointer">▼</div>
                        <div
                            class="wheel"
                            style="background: {wheelGradient(raffleParticipants.length)}; transform: rotate({wheelRotation}deg)"
                        ></div>
                    </div>

                    <div class="wheel-names">
                        {#each raffleParticipants.slice(0, 24) as participant, i}
                            <span class="wheel-name" class:winner={wheelSettled && raffleWinnerId === participant.id}>
                                <i style="background: {wheelColor(i)}"></i>{participant.name}
                            </span>
                        {/each}
                    </div>

                    {#if wheelSettled && raffleWinnerId}
                        <h2 class="wheel-result">
                            🏆 {raffleParticipants.find((p) => p.id === raffleWinnerId)?.name}
                        </h2>
                    {:else if raffleWinnerId}
                        <p class="muted">🎡 Крутим…</p>
                    {:else}
                        <p class="muted">🎟 Участников: {raffleParticipants.length}</p>
                    {/if}
                {:else}
                    <p class="muted">🎟 Пока никто не участвует</p>
                {/if}
            {/if}
        </main>
    {:else if currentView === 'photos'}
        <main class="photos">
            {#if currentSlide}
                {#each asList(currentSlide) as slide (slide.id)}
                    <img
                        class="slide"
                        src={slide.url}
                        alt="Фото от {slide.author}"
                        in:receiveSlide={{ key: slide.id }}
                        out:sendSlide={{ key: slide.id }}
                    />
                {/each}
                <span class="author">📸 {currentSlide.author}</span>
            {:else}
                <p class="muted">Пока нет одобренных фото</p>
            {/if}
        </main>
    {:else if currentView === 'shop'}
        <main class="shop">
            <h1>🛍 Призы</h1>

            {#if shopItems.length === 0}
                <p class="muted">Пока нет призов</p>
            {:else}
                <ul>
                    {#each shopItems as item (item.id)}
                        <li class:sold-out={item.stock === 0}>
                            <span class="item-name">{item.name}</span>
                            <span class="item-price">⭐ {item.price}</span>
                            <span class="item-stock">
                                {item.stock === null ? '∞' : item.stock === 0 ? 'нет в наличии' : `осталось ${item.stock}`}
                            </span>
                        </li>
                    {/each}
                </ul>
            {/if}
        </main>
    {:else if currentView === 'lots'}
        <main class="lots">
            <h1>🔨 Ставки</h1>

            {#if lots.length === 0}
                <p class="muted">Активных лотов нет</p>
            {:else}
                <ul>
                    {#each lots as lot (lot.id)}
                        <li>
                            <span class="item-name">{lot.name}</span>
                            {#if lot.topBid !== null}
                                <span class="lot-leader">{lot.leaderName}: {lot.topBid}</span>
                            {:else}
                                <span class="lot-leader empty">от {lot.minBid}</span>
                            {/if}
                            <span class="lot-time">{formatLotsRemaining(lot.endsAt)}</span>
                        </li>
                    {/each}
                </ul>
            {/if}
        </main>
    {:else if currentView === 'message'}
        <main class="center">
            <p class="big-message">{state.message}</p>
        </main>
    {/if}

    {#if reactions.length > 0}
        <div class="reactions" aria-hidden="true">
            {#each reactions as reaction (reaction.id)}
                <div
                    class="reaction"
                    style="--left: {reaction.left}%; --bottom: {reaction.bottom}vh; --duration: {reaction.duration}s; --delay: {reaction.delay}s; --rotation: {reaction.rotation}deg; --scale: {reaction.scale}; --drift: {reaction.drift}px;"
                    title={reaction.authorName}
                >
                    {#if reaction.text}
                        <span class="bubble">
                            {#if reaction.emoji}<b>{reaction.emoji}</b>{/if}{reaction.text}
                        </span>
                    {:else}
                        <span class="sticker">{reaction.emoji}</span>
                    {/if}
                </div>
            {/each}
        </div>
    {/if}

    {#if confetti.length > 0}
        <div class="confetti" aria-hidden="true">
            {#each confetti as piece (piece.id)}
                <span
                    style="left: {piece.left}%; animation-delay: {piece.delay}s; animation-duration: {piece.duration}s; font-size: {piece.size}px;"
                >{piece.emoji}</span>
            {/each}
        </div>
    {/if}
</div>

<style>
    .screen {
        position: fixed;
        inset: 0;
        overflow: hidden;
        background: radial-gradient(circle at 50% 0%, #23235c 0%, #0f0f23 60%, #0a0a18 100%);
        color: #fff;
        font-family: inherit;
    }

    .topbar {
        position: absolute;
        top: 0;
        left: 0;
        right: 0;
        display: flex;
        justify-content: space-between;
        align-items: center;
        padding: 2vh 3vw;
        z-index: 10;
        font-size: clamp(14px, 1.4vw, 22px);
        color: var(--muted, #aaa);
    }

    .brand { font-weight: bold; letter-spacing: 0.5px; }

    main {
        position: absolute;
        inset: 0;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        padding: 8vh 5vw 4vh;
        text-align: center;
    }

    h1 {
        font-size: clamp(32px, 5vw, 84px);
        line-height: 1.1;
        margin-bottom: 2vh;
    }

    h2 { font-size: clamp(18px, 1.8vw, 30px); margin-bottom: 1vh; }

    .muted { color: var(--muted, #aaa); font-size: clamp(16px, 1.6vw, 26px); }

    .center { gap: 2vh; }

    .idle-emoji {
        font-size: clamp(80px, 14vw, 220px);
        animation: pulse 3s ease-in-out infinite;
    }

    @keyframes pulse {
        0%, 100% { transform: scale(1); }
        50% { transform: scale(1.08); }
    }

    .big-message {
        font-size: clamp(36px, 6vw, 110px);
        font-weight: bold;
        line-height: 1.15;
        overflow-wrap: anywhere;
    }

    /* Лидерборд */
    .leaderboard ol {
        list-style: none;
        width: min(900px, 90vw);
        display: flex;
        flex-direction: column;
        gap: 1.2vh;
    }

    .leaderboard li {
        display: flex;
        align-items: center;
        gap: 2vw;
        background: rgba(255, 255, 255, 0.06);
        border-radius: 14px;
        padding: 1.4vh 2vw;
        font-size: clamp(18px, 2vw, 34px);
    }

    .leaderboard li.top {
        background: rgba(245, 166, 35, 0.16);
        border: 1px solid rgba(245, 166, 35, 0.45);
    }

    .place { width: 2.2em; text-align: center; }

    .name {
        flex: 1;
        text-align: left;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .points { color: #27ae60; font-weight: bold; }

    /* Ивент */
    .description { color: var(--muted, #aaa); font-size: clamp(16px, 1.8vw, 30px); margin-bottom: 2vh; }

    .timer {
        font-size: clamp(48px, 8vw, 160px);
        font-weight: bold;
        color: #f5a623;
        font-variant-numeric: tabular-nums;
        margin: 1vh 0 3vh;
    }

    .timer.over { color: #e74c3c; font-size: clamp(28px, 4vw, 72px); }

    .questions {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(420px, 90vw), 1fr));
        gap: 2vh 3vw;
        width: min(1500px, 92vw);
        max-height: 60vh;
        overflow: hidden;
    }

    .questions article {
        background: rgba(255, 255, 255, 0.06);
        border-radius: 16px;
        padding: 2vh 2vw;
    }

    .options {
        display: flex;
        flex-wrap: wrap;
        justify-content: center;
        gap: 0.8vw;
    }

    .options span {
        background: rgba(245, 166, 35, 0.15);
        border: 1px solid rgba(245, 166, 35, 0.4);
        border-radius: 999px;
        padding: 0.6vh 1.2vw;
        font-size: clamp(14px, 1.4vw, 22px);
    }

    .letters {
        display: flex;
        gap: 3vw;
        font-size: clamp(80px, 14vw, 220px);
        font-weight: bold;
        color: #f5a623;
        line-height: 1;
    }

    /* Тост за именинника */
    .speaker {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 2vh;
    }

    .speaker-name {
        font-size: clamp(32px, 5vw, 88px);
        font-weight: bold;
        line-height: 1.15;
    }

    .speaker-timer {
        font-size: clamp(60px, 10vw, 200px);
        font-weight: bold;
        color: #27ae60;
        font-variant-numeric: tabular-nums;
    }

    /* Новые ивенты */
    .reaction-live {
        font-size: clamp(48px, 9vw, 180px);
        font-weight: bold;
        color: #f5a623;
        animation: pulse 0.8s ease-in-out infinite;
    }

    .song-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(220px, 40vw), 1fr));
        gap: 2vh 2vw;
        width: min(1300px, 92vw);
        max-height: 55vh;
        overflow: hidden;
    }

    .song-card {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 0.8vh;
        background: rgba(255, 255, 255, 0.06);
        border-radius: 16px;
        padding: 2vh 1vw;
    }

    .song-card .song-emoji { font-size: clamp(34px, 4vw, 64px); }

    .song-card .song-hint {
        color: var(--muted, #aaa);
        font-size: clamp(13px, 1.2vw, 20px);
        text-align: center;
    }

    /* Ставки */
    .lots ul {
        list-style: none;
        width: min(1100px, 92vw);
        display: flex;
        flex-direction: column;
        gap: 1.2vh;
    }

    .lots li {
        display: flex;
        align-items: center;
        gap: 2vw;
        background: rgba(255, 255, 255, 0.06);
        border-radius: 14px;
        padding: 1.4vh 2vw;
        font-size: clamp(18px, 2vw, 34px);
    }

    .lot-leader {
        color: #f5a623;
        font-weight: bold;
        white-space: nowrap;
    }

    .lot-leader.empty { color: var(--muted, #aaa); font-weight: normal; }

    .lot-time {
        color: var(--green, #27ae60);
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
        font-size: clamp(15px, 1.6vw, 28px);
    }

    .predictions {
        list-style: none;
        width: min(1100px, 92vw);
        display: flex;
        flex-direction: column;
        gap: 1.2vh;
        max-height: 62vh;
        overflow: hidden;
    }

    .predictions li {
        background: rgba(255, 255, 255, 0.06);
        border-radius: 14px;
        padding: 1.2vh 2vw;
        font-size: clamp(16px, 1.8vw, 30px);
        text-align: left;
        overflow-wrap: anywhere;
    }

    .predictions b { color: #f5a623; }

    /* Лототрон */
    .wheel-wrap {
        position: relative;
        width: min(52vh, 70vw);
        aspect-ratio: 1;
        display: flex;
        align-items: center;
        justify-content: center;
        margin-bottom: 1vh;
    }

    .wheel {
        width: 100%;
        height: 100%;
        border-radius: 50%;
        box-shadow: 0 0 60px rgba(0, 0, 0, 0.55), inset 0 0 0 8px rgba(15, 15, 35, 0.9);
        transition: transform 5s cubic-bezier(0.15, 0.9, 0.15, 1);
    }

    .wheel-pointer {
        position: absolute;
        top: -3vh;
        z-index: 2;
        font-size: clamp(24px, 4vw, 56px);
        color: #f5a623;
        filter: drop-shadow(0 4px 8px rgba(0, 0, 0, 0.6));
    }

    .wheel-names {
        display: flex;
        flex-wrap: wrap;
        gap: 0.6vh 1.4vw;
        justify-content: center;
        max-width: 92vw;
    }

    .wheel-name {
        display: inline-flex;
        align-items: center;
        gap: 0.4em;
        font-size: clamp(12px, 1.2vw, 20px);
        color: #ddd;
        white-space: nowrap;
    }

    .wheel-name i {
        width: 0.9em;
        height: 0.9em;
        border-radius: 3px;
        display: inline-block;
    }

    .wheel-name.winner { color: #f5a623; font-weight: bold; }

    .wheel-result { color: #f5a623; margin-top: 1.5vh; }

    /* Результаты реакции */
    .reaction-results {
        list-style: none;
        width: min(1100px, 92vw);
        display: flex;
        flex-direction: column;
        gap: 1.1vh;
        max-height: 58vh;
        overflow: hidden;
    }

    .reaction-results li {
        display: flex;
        align-items: center;
        gap: 2vw;
        background: rgba(255, 255, 255, 0.06);
        border-radius: 14px;
        padding: 1.2vh 2vw;
        font-size: clamp(17px, 1.9vw, 32px);
    }

    .reaction-results .place { width: 2em; text-align: center; }

    .reaction-time {
        color: var(--green, #27ae60);
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
    }

    .reaction-results .points { color: #f5a623; font-weight: bold; white-space: nowrap; }

    /* Призы */
    .shop ul {        list-style: none;
        width: min(1000px, 92vw);
        display: flex;
        flex-direction: column;
        gap: 1.2vh;
    }

    .shop li {
        display: flex;
        align-items: center;
        gap: 2vw;
        background: rgba(255, 255, 255, 0.06);
        border-radius: 14px;
        padding: 1.4vh 2vw;
        font-size: clamp(18px, 2vw, 34px);
    }

    .shop li.sold-out { opacity: 0.45; }

    .item-name { flex: 1; text-align: left; }

    .item-price { color: #f5a623; font-weight: bold; white-space: nowrap; }

    .item-stock {
        color: var(--muted, #aaa);
        font-size: clamp(13px, 1.3vw, 22px);
        white-space: nowrap;
    }

    /* Фото */
    .photos { padding: 0; }

    .slide {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: contain;
        background: #000;
    }

    .author {
        position: absolute;
        bottom: 3vh;
        left: 3vw;
        z-index: 2;
        background: rgba(0, 0, 0, 0.6);
        border-radius: 999px;
        padding: 0.8vh 1.4vw;
        font-size: clamp(14px, 1.4vw, 22px);
    }

    /* Реакции игроков */
    .reactions {
        position: fixed;
        inset: 0;
        overflow: hidden;
        pointer-events: none;
        z-index: 40;
    }

    .reaction {
        position: absolute;
        left: var(--left);
        bottom: var(--bottom);
        transform: rotate(var(--rotation)) scale(var(--scale));
        animation: float-up var(--duration) ease-out var(--delay) both;
        will-change: transform, opacity;
    }

    .sticker {
        font-size: clamp(48px, 7vw, 110px);
        line-height: 1;
        filter: drop-shadow(0 6px 16px rgba(0, 0, 0, 0.45));
    }

    .bubble {
        display: inline-flex;
        align-items: center;
        gap: 0.5em;
        max-width: 42vw;
        background: rgba(255, 255, 255, 0.14);
        border: 1px solid rgba(255, 255, 255, 0.28);
        border-radius: 999px;
        padding: 0.6em 1.1em;
        font-size: clamp(20px, 2.2vw, 36px);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        box-shadow: 0 10px 30px rgba(0, 0, 0, 0.35);
    }

    .bubble b { font-size: 1.2em; }

    @keyframes float-up {
        0% {
            transform: translate(0, 40px) rotate(var(--rotation)) scale(var(--scale));
            opacity: 0;
        }
        12% { opacity: 1; }
        80% { opacity: 1; }
        100% {
            transform: translate(var(--drift), -75vh) rotate(calc(var(--rotation) + 24deg)) scale(var(--scale));
            opacity: 0;
        }
    }

    /* Конфетти */
    .confetti {
        position: fixed;
        inset: 0;
        overflow: hidden;
        pointer-events: none;
        z-index: 50;
    }

    .confetti span {
        position: absolute;
        top: -80px;
        animation-name: fall;
        animation-timing-function: linear;
        animation-fill-mode: forwards;
    }

    @keyframes fall {
        to {
            transform: translateY(115vh) rotate(720deg);
        }
    }
</style>
