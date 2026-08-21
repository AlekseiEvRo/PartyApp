<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    let definitions: any[] = [];
    let sessions: any[] = [];

    async function loadDefinitions() {
        try {
            definitions = await api<any[]>('/api/events/definitions');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function loadSessions() {
        try {
            sessions = await api<any[]>('/api/admin/sessions');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function startEvent(definitionId: string) {
        try {
            await api(`/api/events/${definitionId}/start`, 'POST');
            showToast('Ивент запущен!');
            loadSessions();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function finishEvent(sessionId: string) {
        try {
            await api(`/api/events/${sessionId}/finish`, 'POST');
            showToast('Ивент завершён');
            loadSessions();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    function badgeClass(state: string) {
        if (state === 'Active') return 'badge-active';
        if (state === 'Finished') return 'badge-finished';
        return 'badge-waiting';
    }

    onMount(() => {
        loadDefinitions();
        loadSessions();
    });
</script>

<h2>Управление ивентами</h2>

<div class="card">
    <h3>Определения ивентов</h3>
    <button class="btn btn-primary" on:click={loadDefinitions}>🔄 Обновить</button>
    <table>
        <thead>
        <tr>
            <th>Название</th>
            <th>Тип</th>
            <th>Доступность</th>
            <th>Активен</th>
            <th>Действия</th>
        </tr>
        </thead>
        <tbody>
        {#each definitions as d}
            <tr>
                <td>{d.displayName}</td>
                <td><code>{d.type}</code></td>
                <td>{d.availability}</td>
                <td>{d.isActive ? '✅' : '❌'}</td>
                <td>
                    <button class="btn btn-success" on:click={() => startEvent(d.id)}>▶️ Запустить</button>
                </td>
            </tr>
        {/each}
        </tbody>
    </table>
</div>

<div class="card">
    <h3>Сессии ивентов</h3>
    <button class="btn btn-primary" on:click={loadSessions}>🔄 Обновить</button>
    <table>
        <thead>
        <tr>
            <th>Ивент</th>
            <th>Тип</th>
            <th>Состояние</th>
            <th>Запущен</th>
            <th>Завершён</th>
            <th>Сабмитов</th>
            <th>Действия</th>
        </tr>
        </thead>
        <tbody>
        {#each sessions as s}
            <tr>
                <td>{s.definitionName}</td>
                <td><code>{s.type}</code></td>
                <td><span class="badge {badgeClass(s.state)}">{s.state}</span></td>
                <td>{new Date(s.startedAt).toLocaleTimeString()}</td>
                <td>{s.endedAt ? new Date(s.endedAt).toLocaleTimeString() : '—'}</td>
                <td>{s.submissionCount}</td>
                <td>
                    {#if s.state === 'Active'}
                        <button class="btn btn-danger" on:click={() => finishEvent(s.id)}>⏹ Завершить</button>
                    {/if}
                </td>
            </tr>
        {/each}
        </tbody>
    </table>
</div>

<style>
    .card { background: #0f0f23; border-radius: 8px; padding: 16px; margin: 16px 0; }
    table { width: 100%; border-collapse: collapse; margin-top: 12px; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid #333; }
    th { color: #f5a623; }
    .btn { padding: 8px 14px; border: none; border-radius: 6px; cursor: pointer; font-size: 13px; }
    .btn-primary { background: #3498db; color: #fff; }
    .btn-success { background: #27ae60; color: #fff; }
    .btn-danger { background: #e74c3c; color: #fff; }
    .badge { padding: 4px 10px; border-radius: 12px; font-size: 12px; font-weight: bold; }
    .badge-active { background: #27ae60; color: #fff; }
    .badge-finished { background: #7f8c8d; color: #fff; }
    .badge-waiting { background: #f39c12; color: #fff; }
</style>