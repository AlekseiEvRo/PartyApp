<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';
    import Avatar from '../Avatar.svelte';

    let leaderboard: any[] = [];
    const medals = ['🥇', '🥈', '🥉'];

    async function loadLeaderboard() {
        try {
            leaderboard = await api<any[]>('/api/admin/leaderboard');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    onMount(loadLeaderboard);
</script>

<h2>🏆 Лидерборд</h2>
<button class="btn btn-primary" on:click={loadLeaderboard}>🔄 Обновить</button>

<div class="leaderboard">
    {#each leaderboard as p, i}
        <div class="leaderboard-item">
            <div class="rank">{medals[i] || (i + 1)}</div>
            <Avatar userId={p.id} name={p.displayName} version={p.profileUpdatedAt ?? null} size={36} />
            <div class="name">{p.displayName}</div>
            <div class="score">{p.balance} ⭐</div>
        </div>
    {/each}
</div>

<style>
    .leaderboard { margin-top: 16px; }
    .leaderboard-item {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 14px;
        border-bottom: 1px solid var(--border, #333);
    }
    .rank { width: 44px; flex-shrink: 0; font-size: 22px; font-weight: bold; color: var(--accent, #f5a623); }
    .name { flex: 1; min-width: 0; font-size: 16px; overflow-wrap: anywhere; }
    .score { font-size: 18px; font-weight: bold; color: var(--green, #27ae60); white-space: nowrap; }
    .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; }
    .btn-primary { background: var(--blue, #3498db); color: #fff; margin-top: 12px; }
</style>