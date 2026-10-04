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

    interface ScreenSession {
        id: string;
        definitionName: string;
        state: string;
    }

    interface ScreenSettings {
        photoSeconds: number;
        leaderboardSeconds: number;
        shopSeconds: number;
    }

    let state: ScreenState | null = null;
    let sessions: ScreenSession[] = [];
    let settings: ScreenSettings = { photoSeconds: 8, leaderboardSeconds: 60, shopSeconds: 60 };
    let selectedSessionId = '';
    let message = '';
    let busy = false;
    let savingSettings = false;
    let error = '';

    onMount(load);

    async function load() {
        error = '';

        try {
            state = await api<ScreenState>('/api/screen/state');
            settings = await api<ScreenSettings>('/api/screen/settings');

            // Берём и активные, и недавно завершённые сессии: последние нужны,
            // например, чтобы показать раскрытые предсказания
            sessions = (await api<ScreenSession[]>('/api/admin/sessions')).slice(0, 15);

            if (!selectedSessionId && sessions.length > 0) {
                selectedSessionId = sessions[0].id;
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

    async function saveSettings() {
        savingSettings = true;

        try {
            settings = await api<ScreenSettings>('/api/screen/settings', 'PUT', {
                photoSeconds: Number(settings.photoSeconds),
                leaderboardSeconds: Number(settings.leaderboardSeconds),
                shopSeconds: Number(settings.shopSeconds)
            });
            showToast('Настройки ротации сохранены');
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось сохранить настройки', 'error');
        } finally {
            savingSettings = false;
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
            case 'shop':
                return 'призы';
            case 'lots':
                return 'ставки';
            case 'rotation':
                return 'ротация';
            case 'message':
                return 'сообщение';
            case 'qr':
                return 'QR-статистика';
            case 'spy':
                return 'шпионаж';
            default:
                return 'ожидание';
        }
    }
</script>

<h2>🖥 Большой экран</h2>

<p class="hint">
    Открой <a href="/screen" target="_blank" rel="noopener">/screen</a> на устройстве, подключённом к проектору
    или ТВ, и переключай режимы отсюда. Игроки сами могут запускать конфетти и отправлять
    стикеры с подписями — они всплывают поверх любого режима.
</p>

{#if error}
    <p class="message error">{error}</p>
{:else}
    <div class="status">
        Сейчас: <strong>{state ? modeLabel(state.mode) : '…'}</strong>
        {#if state?.mode === 'event' && state.sessionId}
            <span class="muted">({sessions.find((s) => s.id === state?.sessionId)?.definitionName ?? 'ивент'})</span>
        {/if}
    </div>

    <div class="row">
        <button class="btn" on:click={() => setMode('idle')} disabled={busy}>Ожидание</button>
        <button class="btn" on:click={() => setMode('leaderboard')} disabled={busy}>🏆 Лидерборд</button>
        <button class="btn" on:click={() => setMode('photos')} disabled={busy}>📸 Слайдшоу</button>
        <button class="btn" on:click={() => setMode('shop')} disabled={busy}>🛍 Призы</button>
        <button class="btn" on:click={() => setMode('lots')} disabled={busy}>🔨 Ставки</button>
        <button class="btn" on:click={() => setMode('qr')} disabled={busy}>📷 QR-статистика</button>
        <button class="btn" on:click={() => setMode('spy')} disabled={busy}>🕵 Шпионаж</button>
        <button class="btn accent" on:click={() => setMode('rotation')} disabled={busy}>🔁 Ротация</button>
        <button class="btn accent" on:click={fireConfetti} disabled={busy}>🎉 Конфетти</button>
    </div>

    <div class="block settings">
        <p class="group-label">Ротация: фото → лидерборд → призы</p>
        <div class="inline">
            <label class="field">
                Секунд на фото
                <input type="number" min="3" max="600" bind:value={settings.photoSeconds} />
            </label>
            <label class="field">
                Лидерборд, сек
                <input type="number" min="5" max="3600" bind:value={settings.leaderboardSeconds} />
            </label>
            <label class="field">
                Призы, сек
                <input type="number" min="5" max="3600" bind:value={settings.shopSeconds} />
            </label>
            <button class="btn accent" on:click={saveSettings} disabled={savingSettings}>
                {savingSettings ? 'Сохраняем…' : 'Сохранить'}
            </button>
        </div>
    </div>

    <div class="block">
        <label for="screen-event">Показать ивент:</label>
        <div class="inline">
            <select id="screen-event" bind:value={selectedSessionId}>
                {#if sessions.length === 0}
                    <option value="">Нет ивентов</option>
                {/if}
                {#each sessions as session (session.id)}
                    <option value={session.id}>
                        {session.definitionName}{session.state === 'Finished' ? ' (завершён)' : ''}
                    </option>
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

    .block label,
    .group-label {
        display: block;
        font-size: 13px;
        color: var(--muted, #aaa);
        margin-bottom: 6px;
    }

    .settings .field {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-bottom: 0;
    }

    .settings .field input {
        width: 120px;
        background: var(--bg-soft, #12122e);
        border: 1px solid #2a2a5e;
        border-radius: 8px;
        color: inherit;
        padding: 9px 12px;
        font-size: 14px;
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
