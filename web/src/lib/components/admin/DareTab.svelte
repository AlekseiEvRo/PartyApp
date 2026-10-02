<script lang="ts">
    import { onDestroy, onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface PendingDare {
        id: string;
        sessionName: string;
        playerName: string;
        task: string;
        points: number;
        createdAt: string;
    }

    let dares: PendingDare[] = [];
    let error = '';
    let busyId: string | null = null;
    let timer: ReturnType<typeof setInterval>;

    onMount(() => {
        void load();
        // Список обновляем сам: игроки тянут фанты в любой момент
        timer = setInterval(() => void load(), 10_000);
    });

    onDestroy(() => clearInterval(timer));

    async function load() {
        try {
            dares = await api<PendingDare[]>('/api/events/dare/pending');
            error = '';
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить фанты';
        }
    }

    async function confirm(dare: PendingDare) {
        busyId = dare.id;

        try {
            await api(`/api/events/dare/${dare.id}/confirm`, 'POST');
            showToast(`✅ ${dare.playerName}: +${dare.points}`);
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось подтвердить фант', 'error');
        } finally {
            busyId = null;
        }
    }

    function formatTime(value: string): string {
        return new Date(value).toLocaleString('ru-RU', {
            hour: '2-digit',
            minute: '2-digit'
        });
    }
</script>

<h2>🎲 Фанты</h2>

<p class="hint">
    Игрок тянет фант, выполняет его и ждёт подтверждения. После подтверждения баллы начисляются
    сразу, а у игрока кнопка становится зелёной.
</p>

{#if error}
    <p class="message error">{error}</p>
{:else if dares.length === 0}
    <p class="muted">Пока никто не ждёт подтверждения.</p>
{:else}
    <ul class="list">
        {#each dares as dare (dare.id)}
            <li class="row">
                <div class="info">
                    <span class="name">{dare.task}</span>
                    <span class="meta">
                        {dare.playerName} · {dare.sessionName} · ⭐ {dare.points} · {formatTime(dare.createdAt)}
                    </span>
                </div>
                <button class="btn accent" disabled={busyId === dare.id} on:click={() => confirm(dare)}>
                    Подтвердить
                </button>
            </li>
        {/each}
    </ul>
{/if}

<style>
    h2 { margin-bottom: 10px; }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        line-height: 1.5;
        margin-bottom: 16px;
    }

    .list {
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: 8px;
    }

    .row {
        display: flex;
        align-items: center;
        gap: 12px;
        background: var(--bg-soft, #12122e);
        border-radius: 10px;
        padding: 10px 12px;
        flex-wrap: wrap;
    }

    .info {
        flex: 1;
        min-width: 200px;
        display: flex;
        flex-direction: column;
        gap: 3px;
    }

    .name { font-weight: bold; font-size: 14px; }
    .meta { font-size: 12px; color: var(--muted, #aaa); }

    .btn {
        background: #2a2a5a;
        color: #ddd;
        border: none;
        border-radius: 8px;
        padding: 8px 14px;
        font-size: 13px;
        font-weight: bold;
        cursor: pointer;
    }

    .btn.accent { background: #27ae60; color: #fff; }
    .btn:disabled { opacity: 0.5; cursor: default; }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
