<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface ScreenState {
        mode: string;
        sessionId: string | null;
        message: string | null;
        version: number;
    }

    interface AvailableEvent {
        sessionId: string;
        type: string;
        displayName: string;
    }

    let state: ScreenState | null = null;
    let events: AvailableEvent[] = [];
    let selectedSessionId = '';
    let message = '';
    let busy = false;
    let error = '';

    onMount(load);

    async function load() {
        error = '';

        try {
            state = await api<ScreenState>('/api/screen/state');
            events = await api<AvailableEvent[]>('/api/events/available');

            if (!selectedSessionId && events.length > 0) {
                selectedSessionId = events[0].sessionId;
            }
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось получить состояние экрана';
        }
    }

    async function setMode(mode: string, sessionId?: string, text?: string) {
        busy = true;

        try {
            state = await api<ScreenState>('/api/screen/state', 'POST', {
                mode,
                sessionId: sessionId ?? null,
                message: text ?? null
            });
            showToast('Экран переключён');
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось переключить экран', 'error');
        } finally {
            busy = false;
        }
    }

    async function showMessage() {
        const value = message.trim();
        if (!value) return;

        await setMode('message', undefined, value);
        message = '';
    }

    async function fireConfetti() {
        busy = true;

        try {
            await api('/api/screen/confetti', 'POST');
            showToast('🎉 Конфетти!');
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось запустить конфетти', 'error');
        } finally {
            busy = false;
        }
    }

    function modeLabel(mode: string): string {
        switch (mode) {
            case 'leaderboard':
                return 'лидерборд';
            case 'event':
                return 'ивент';
            case 'photos':
                return 'слайдшоу';
            case 'message':
                return 'сообщение';
            default:
                return 'ожидание';
        }
    }
</script>

<h2>🖥 Большой экран</h2>

<p class="hint">
    Открой <a href="/screen" target="_blank" rel="noopener">/screen</a> на устройстве, подключённом к проектору
    или ТВ, и переключай режимы отсюда.
</p>

{#if error}
    <p class="message error">{error}</p>
{:else}
    <div class="status">
        Сейчас: <strong>{state ? modeLabel(state.mode) : '…'}</strong>
        {#if state?.mode === 'event' && state.sessionId}
            <span class="muted">({events.find((e) => e.sessionId === state?.sessionId)?.displayName ?? 'ивент'})</span>
        {/if}
    </div>

    <div class="row">
        <button class="btn" on:click={() => setMode('idle')} disabled={busy}>Ожидание</button>
        <button class="btn" on:click={() => setMode('leaderboard')} disabled={busy}>🏆 Лидерборд</button>
        <button class="btn" on:click={() => setMode('photos')} disabled={busy}>📸 Слайдшоу</button>
        <button class="btn accent" on:click={fireConfetti} disabled={busy}>🎉 Конфетти</button>
    </div>

    <div class="block">
        <label for="screen-event">Показать ивент:</label>
        <div class="inline">
            <select id="screen-event" bind:value={selectedSessionId}>
                {#if events.length === 0}
                    <option value="">Нет активных ивентов</option>
                {/if}
                {#each events as event (event.sessionId)}
                    <option value={event.sessionId}>{event.displayName}</option>
                {/each}
            </select>
            <button
                class="btn"
                on:click={() => setMode('event', selectedSessionId)}
                disabled={busy || !selectedSessionId}
            >
                Показать
            </button>
        </div>
    </div>

    <div class="block">
        <label for="screen-message">Сообщение на экран:</label>
        <div class="inline">
            <input
                id="screen-message"
                type="text"
                bind:value={message}
                maxlength="200"
                placeholder="Например: Все на общее фото!"
                on:keydown={(e) => e.key === 'Enter' && showMessage()}
            />
            <button class="btn" on:click={showMessage} disabled={busy || message.trim().length === 0}>
                Показать
            </button>
        </div>
    </div>
{/if}

<style>
    h2 { margin-bottom: 10px; }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        line-height: 1.5;
        margin-bottom: 16px;
    }

    .hint a { color: #f5a623; }

    .status {
        background: var(--bg-soft, #12122e);
        border-radius: 10px;
        padding: 10px 14px;
        margin-bottom: 16px;
        font-size: 14px;
    }

    .muted { color: var(--muted, #aaa); }

    .row {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-bottom: 16px;
    }

    .block { margin-bottom: 16px; }

    .block label {
        display: block;
        font-size: 13px;
        color: var(--muted, #aaa);
        margin-bottom: 6px;
    }

    .inline {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
    }

    .inline select,
    .inline input {
        flex: 1;
        min-width: 200px;
        background: var(--bg-soft, #12122e);
        border: 1px solid #2a2a5e;
        border-radius: 8px;
        color: inherit;
        padding: 9px 12px;
        font-size: 14px;
    }

    .btn {
        background: #2a2a5a;
        color: #ddd;
        border: none;
        border-radius: 8px;
        padding: 9px 16px;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
    }

    .btn.accent { background: #f5a623; color: #12122e; }
    .btn:disabled { opacity: 0.5; cursor: default; }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
