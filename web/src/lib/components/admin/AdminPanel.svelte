<script lang="ts">
    import DashboardTab from './DashboardTab.svelte';
    import EventsTab from './EventsTab.svelte';
    import QrTab from './QrTab.svelte';
    import GrantTab from './GrantTab.svelte';
    import LeaderboardTab from './LeaderboardTab.svelte';
    import SpyGameTab from './SpyGameTab.svelte';
    import PhotosTab from './PhotosTab.svelte';
    import WishesTab from './WishesTab.svelte';
    import ScreenTab from './ScreenTab.svelte';
    import { user } from '../../stores';

    let activeTab = 'dashboard';

    const tabs = [
        { id: 'dashboard', label: '📊 Дашборд' },
        { id: 'events', label: '🎮 Ивенты' },
        { id: 'qr', label: '📷 QR-коды' },
        { id: 'grant', label: '💰 Начисление' },
        { id: 'leaderboard', label: '🏆 Лидерборд' },
        { id: 'spy', label: '🕵 Шпионаж' },
        { id: 'photos', label: '📸 Фото' },
        { id: 'wishes', label: '💌 Пожелания' },
        { id: 'screen', label: '🖥 Экран' }
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
            <a href="/" class="back-link">🎮 Режим игрока</a>
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
        {:else if activeTab === 'photos'}
            <PhotosTab />
        {:else if activeTab === 'wishes'}
            <WishesTab />
        {:else if activeTab === 'screen'}
            <ScreenTab />
        {/if}
    </div>
</div>

<style>
    .admin {
        min-height: 100vh;
        min-height: 100dvh;
        padding-bottom: env(safe-area-inset-bottom, 0px);
        background: var(--bg, #0f0f23);
    }
    .admin-header {
        background: var(--card, #1a1a3e);
        padding: 16px 24px;
        padding-top: calc(16px + env(safe-area-inset-top, 0px));
        padding-left: calc(24px + env(safe-area-inset-left, 0px));
        padding-right: calc(24px + env(safe-area-inset-right, 0px));
        display: flex;
        justify-content: space-between;
        align-items: center;
        border-bottom: 2px solid var(--accent, #f5a623);
        flex-wrap: wrap;
        gap: 12px;
    }
    .admin-header h1 { color: var(--accent, #f5a623); font-size: 22px; }
    .header-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 16px; }
    .back-link {
        background: var(--green, #27ae60);
        color: #fff;
        text-decoration: none;
        padding: 6px 12px;
        border-radius: 6px;
        font-size: 13px;
        font-weight: 500;
        white-space: nowrap;
        transition: background 0.2s;
    }
    .back-link:hover { background: #2ecc71; }
    .admin-name { color: var(--muted, #aaa); font-size: 14px; overflow-wrap: anywhere; }
    .logout-btn {
        background: var(--red, #e74c3c);
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
        padding-left: calc(24px + env(safe-area-inset-left, 0px));
        padding-right: calc(24px + env(safe-area-inset-right, 0px));
        flex-wrap: wrap;
    }
    .tab {
        padding: 10px 18px;
        background: var(--card, #1a1a3e);
        border: none;
        color: var(--muted, #aaa);
        cursor: pointer;
        border-radius: 8px 8px 0 0;
        font-size: 14px;
    }
    .tab.active { background: var(--card-soft, #2a2a5e); color: var(--accent, #f5a623); font-weight: bold; }
    .tab-content {
        background: var(--card, #1a1a3e);
        margin: 0 24px 24px;
        margin-left: calc(24px + env(safe-area-inset-left, 0px));
        margin-right: calc(24px + env(safe-area-inset-right, 0px));
        padding: 24px;
        border-radius: 0 8px 8px 8px;
        min-height: 400px;
        overflow-wrap: anywhere;
    }

    @media (max-width: 600px) {
        .admin-header {
            padding: 12px 14px;
            padding-top: calc(12px + env(safe-area-inset-top, 0px));
            padding-left: calc(14px + env(safe-area-inset-left, 0px));
            padding-right: calc(14px + env(safe-area-inset-right, 0px));
            gap: 8px;
        }
        .admin-header h1 { font-size: 18px; }
        .header-actions { gap: 10px; }
        .tabs {
            padding: 12px 14px 0;
            padding-left: calc(14px + env(safe-area-inset-left, 0px));
            padding-right: calc(14px + env(safe-area-inset-right, 0px));
        }
        .tab { padding: 8px 12px; font-size: 13px; }
        .tab-content {
            margin: 0 14px 14px;
            margin-left: calc(14px + env(safe-area-inset-left, 0px));
            margin-right: calc(14px + env(safe-area-inset-right, 0px));
            padding: 14px;
            min-height: 300px;
        }
    }
</style>