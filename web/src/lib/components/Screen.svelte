<script lang="ts">
    import { onDestroy, onMount } from 'svelte';
    import { fade } from 'svelte/transition';
    import { api } from '../api';
    import { connect, getConnection, reconnectIfNeeded } from '../signalr';
    import { loadPhotoUrl, releasePhotoUrls } from '../photos';

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
        type: string;
        displayName: string;
        config: Record<string, any>;
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

    interface ConfettiPiece {
        id: number;
        left: number;
        delay: number;
        duration: number;
        emoji: string;
        size: number;
    }

    const slideIntervalMs = 8000;
    const refreshIntervalMs = 15000;

    let state: ScreenState = {
        mode: 'idle',
        sessionId: null,
        message: null,
        version: 0,
        serverTimeUtc: new Date().toISOString()
    };

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

    let clockTimer: ReturnType<typeof setInterval>;
    let refreshTimer: ReturnType<typeof setInterval>;
    let confettiTimer: ReturnType<typeof setTimeout> | undefined;
    let balanceHandler: (() => void) | null = null;

    $: remainingSeconds = computeRemaining(now);
    $: currentSlide = slides.length > 0 ? slides[slideIndex % slides.length] : null;

    // Листаем слайдшоу
    $: if (state.mode === 'photos' && slides.length > 1 && now - lastSlideAt >= slideIntervalMs) {
        lastSlideAt = now;
        slideIndex = (slideIndex + 1) % slides.length;
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
            balanceHandler = () => {
                if (state.mode === 'leaderboard') void loadLeaderboard();
            };
            connection.on('BalanceUpdated', balanceHandler);
        }

        clockTimer = setInterval(() => (now = Date.now()), 1000);
        refreshTimer = setInterval(() => void refreshContent(true), refreshIntervalMs);

        try {
            await loadState();
        } catch (e) {
            console.error('Screen: не удалось получить состояние', e);
        }
    });

    onDestroy(() => {
        clearInterval(clockTimer);
        clearInterval(refreshTimer);
        clearTimeout(confettiTimer);

        const connection = getConnection();
        if (connection) {
            connection.off('ScreenUpdated', onScreenUpdated);
            connection.off('ScreenConfetti', onConfetti);
            if (balanceHandler) connection.off('BalanceUpdated', balanceHandler);
        }

        releasePhotoUrls();
    });

    async function loadState() {
        state = await api<ScreenState>('/api/screen/state');
        clockOffset = new Date(state.serverTimeUtc).getTime() - Date.now();
        await refreshContent();
    }

    function onScreenUpdated(updated: ScreenState) {
        state = updated;
        clockOffset = new Date(updated.serverTimeUtc).getTime() - Date.now();
        void refreshContent();
    }

    async function refreshContent(checkConnection = false) {
        try {
            if (checkConnection) await reconnectIfNeeded();

            if (state.mode === 'leaderboard') await loadLeaderboard();
            else if (state.mode === 'event') await loadEvent();
            else if (state.mode === 'photos') await loadPhotos();
        } catch (e) {
            console.error('Screen: не удалось обновить содержимое', e);
        }
    }

    async function loadLeaderboard() {
        leaderboard = await api<LeaderboardEntry[]>('/api/admin/leaderboard');
    }

    async function loadEvent() {
        if (!state.sessionId) return;

        const [data, available] = await Promise.all([
            api<EventData>(`/api/events/${state.sessionId}/data`),
            api<AvailableEvent[]>('/api/events/available')
        ]);

        eventData = data;
        eventInfo = available.find((e) => e.sessionId === state.sessionId) ?? null;
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

    function computeRemaining(clientNow: number): number | null {
        if (state.mode !== 'event' || !eventInfo || !eventData) return null;

        const timeLimit = Number(eventData.config?.timeLimitSec ?? 0);
        if (!timeLimit) return null;

        const startedAt = new Date(eventInfo.startedAt).getTime();
        const elapsed = (clientNow + clockOffset - startedAt) / 1000;

        return Math.max(0, Math.ceil(timeLimit - elapsed));
    }

    function formatClock(timestamp: number): string {
        return new Date(timestamp).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    }

    function formatSeconds(value: number): string {
        const minutes = Math.floor(value / 60);
        const seconds = value % 60;
        return minutes > 0 ? `${minutes}:${seconds.toString().padStart(2, '0')}` : `${seconds}`;
    }
</script>

<div class="screen">
    <header class="topbar">
        <span class="brand">🎉 PartyApp</span>
        <span class="clock">{formatClock(now)}</span>
    </header>

    {#if state.mode === 'idle'}
        <main class="center">
            <div class="idle-emoji">🎉</div>
            <h1>Скоро начнём!</h1>
            <p class="muted">Следи за приложением — ивенты появятся здесь</p>
        </main>
    {:else if state.mode === 'leaderboard'}
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
    {:else if state.mode === 'event'}
        <main class="event">
            <h1>{eventData?.displayName ?? 'Ивент'}</h1>

            {#if eventInfo?.description}
                <p class="description">{eventInfo.description}</p>
            {/if}

            {#if remainingSeconds !== null}
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
            {/if}
        </main>
    {:else if state.mode === 'photos'}
        <main class="photos">
            {#if currentSlide}
                {#key currentSlide.id}
                    <img
                        class="slide"
                        src={currentSlide.url}
                        alt="Фото от {currentSlide.author}"
                        transition:fade={{ duration: 700 }}
                    />
                {/key}
                <span class="author">📸 {currentSlide.author}</span>
            {:else}
                <p class="muted">Пока нет одобренных фото</p>
            {/if}
        </main>
    {:else if state.mode === 'message'}
        <main class="center">
            <p class="big-message">{state.message}</p>
        </main>
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

    /* Фото */
    .photos { padding: 0; }

    .slide {
        width: 100vw;
        height: 100vh;
        object-fit: contain;
        background: #000;
    }

    .author {
        position: absolute;
        bottom: 3vh;
        left: 3vw;
        background: rgba(0, 0, 0, 0.6);
        border-radius: 999px;
        padding: 0.8vh 1.4vw;
        font-size: clamp(14px, 1.4vw, 22px);
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
