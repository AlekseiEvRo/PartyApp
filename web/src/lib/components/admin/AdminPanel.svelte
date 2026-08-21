<script lang="ts">
    import DashboardTab from './DashboardTab.svelte';
    import EventsTab from './EventsTab.svelte';
    import QrTab from './QrTab.svelte';
    import GrantTab from './GrantTab.svelte';
    import LeaderboardTab from './LeaderboardTab.svelte';
    import SpyGameTab from './SpyGameTab.svelte';
    import { user } from '../../stores';

    let activeTab = 'dashboard';

    const tabs = [
        { id: 'dashboard', label: '📊 Дашборд' },
        { id: 'events', label: '🎮 Ивенты' },
        { id: 'qr', label: '📷 QR-коды' },
        { id: 'grant', label: '💰 Начисление' },
        { id: 'leaderboard', label: '🏆 Лидерборд' },
        { id: 'spy', label: '🕵 Шпионаж' }
    ];

    function logout() {
        localStorage.removeItem('party_token');
        location.href = '/admin';
    }
</script>

<div class="admin">
    <header class="admin-header">
        <h1>🎉 PartyApp Админка</h1>
        <div class="header-actions">
            <a href="/" class="back-link">← Режим игрока</a>
            <span class="admin-name">{$user?.displayName}</span>
            <button class="logout-btn" on:click={logout}>Выйти</button>
        </div>
    </header>

    <div class="tabs">
        {#each tabs as tab}
            <button
                    class="tab"
                    class:active={activeTab === tab.id}
                    on:click={() => activeTab = tab.id}
            >
                {tab.label}
            </button>
        {/each}
    </div>

    <div class="tab-content">
        {#if activeTab === 'dashboard'}
            <DashboardTab />
        {:else if activeTab === 'events'}
            <EventsTab />
        {:else if activeTab === 'qr'}
            <QrTab />
        {:else if activeTab === 'grant'}
            <GrantTab />
        {:else if activeTab === 'leaderboard'}
            <LeaderboardTab />
        {:else if activeTab === 'spy'}
            <SpyGameTab />
        {/if}
    </div>
</div>

<style>
    .admin { min-height: 100vh; background: #0f0f23; }
    .admin-header {
        background: #1a1a3e;
        padding: 16px 24px;
        display: flex;
        justify-content: space-between;
        align-items: center;
        border-bottom: 2px solid #f5a623;
        flex-wrap: wrap;
        gap: 12px;
    }
    .admin-header h1 { color: #f5a623; font-size: 22px; }
    .header-actions { display: flex; align-items: center; gap: 16px; }
    .back-link { color: #3498db; text-decoration: none; font-size: 14px; }
    .admin-name { color: #aaa; font-size: 14px; }
    .logout-btn {
        background: #e74c3c;
        color: #fff;
        border: none;
        padding: 6px 14px;
        border-radius: 6px;
        cursor: pointer;
    }
    .tabs {
        display: flex;
        gap: 4px;
        padding: 16px 24px 0;
        flex-wrap: wrap;
    }
    .tab {
        padding: 10px 18px;
        background: #1a1a3e;
        border: none;
        color: #aaa;
        cursor: pointer;
        border-radius: 8px 8px 0 0;
        font-size: 14px;
    }
    .tab.active { background: #2a2a5e; color: #f5a623; font-weight: bold; }
    .tab-content {
        background: #1a1a3e;
        margin: 0 24px 24px;
        padding: 24px;
        border-radius: 0 8px 8px 8px;
        min-height: 400px;
    }
</style>