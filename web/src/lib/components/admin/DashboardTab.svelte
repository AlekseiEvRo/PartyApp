<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    let players: any[] = [];
    let loading = true;

    async function loadPlayers() {
        loading = true;
        try {
            players = await api<any[]>('/api/admin/players');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            loading = false;
        }
    }

    onMount(loadPlayers);
</script>

<h2>Участники</h2>
<button class="btn btn-primary" on:click={loadPlayers}>🔄 Обновить</button>

{#if loading}
    <p style="color:#666;">Загрузка...</p>
{:else}
    <table>
        <thead>
        <tr>
            <th>Имя</th>
            <th>Логин</th>
            <th>Роль</th>
            <th>Баланс</th>
            <th>Зарегистрирован</th>
        </tr>
        </thead>
        <tbody>
        {#each players as p}
            <tr>
                <td>{p.displayName}</td>
                <td>{p.username}</td>
                <td>{p.role === 'Admin' ? '👑 Админ' : '🎮 Игрок'}</td>
                <td><strong>{p.balance}</strong></td>
                <td>{new Date(p.createdAt).toLocaleDateString()}</td>
            </tr>
        {/each}
        </tbody>
    </table>
{/if}

<style>
    table { width: 100%; border-collapse: collapse; margin-top: 16px; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid #333; }
    th { color: #f5a623; font-weight: 600; }
    tr:hover { background: #2a2a5e; }
    .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; margin-top: 12px; }
    .btn-primary { background: #3498db; color: #fff; }
</style>