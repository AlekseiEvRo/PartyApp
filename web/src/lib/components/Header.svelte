<script lang="ts">
    import { user, balance, connectionState } from '../stores';
    import { onMount } from 'svelte';
    import { getPermission, getPushSupport } from '../push';
    import NotificationSettings from './NotificationSettings.svelte';

    let showNotifButton = false;
    let notificationsEnabled = false;
    let showSettings = false;

    onMount(() => {
        const support = getPushSupport();

        // На iOS в Safari-вкладке push не работает, но кнопка полезна:
        // она открывает экран настроек с инструкцией по установке
        showNotifButton = support.supported || support.reason === 'ios-not-installed';
        notificationsEnabled = getPermission() === 'granted';
    });

    function openSettings() {
        showSettings = true;
    }

    function closeSettings() {
        showSettings = false;
        notificationsEnabled = getPermission() === 'granted';
    }
</script>

<header>
    <div class="header-left">
        <span class="logo">🎉</span>
        <span class="name">{$user?.displayName}</span>
        {#if $user?.role === 'Admin'}
            <a href="/admin" class="admin-link">⚙️ Админка</a>
        {/if}
    </div>
    <div class="header-right">
        {#if showNotifButton}
            <button
                class="notif-btn"
                class:enabled={notificationsEnabled}
                on:click={openSettings}
                title="Настройки уведомлений"
            >
                🔔
            </button>
        {/if}
        <span class="balance">⭐ {$balance}</span>
        <span class="conn">{$connectionState === 'connected' ? '🟢' : '🔴'}</span>
    </div>
</header>

<NotificationSettings open={showSettings} on:close={closeSettings} />

<style>
    header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        background: #1a1a3e;
        border-bottom: 2px solid #f5a623;
        position: sticky;
        top: 0;
        z-index: 10;

        /* Safe area для iPhone с монобровью */
        padding: 14px 20px;
        padding-top: calc(14px + env(safe-area-inset-top, 0px));
    }

    .header-left {
        display: flex;
        align-items: center;
        gap: 10px;
    }

    .header-right {
        display: flex;
        align-items: center;
        gap: 12px;
    }

    .logo { font-size: 22px; }
    .name { font-weight: bold; font-size: 15px; }
    .balance { font-size: 17px; font-weight: bold; color: #27ae60; }

    .admin-link {
        background: #8e44ad;
        color: #fff;
        text-decoration: none;
        padding: 6px 12px;
        border-radius: 6px;
        font-size: 13px;
        font-weight: 500;
        transition: background 0.2s;
    }

    .admin-link:hover { background: #9b59b6; }

    .notif-btn {
        background: #f5a623;
        border: none;
        border-radius: 50%;
        width: 32px;
        height: 32px;
        font-size: 16px;
        cursor: pointer;
        display: flex;
        align-items: center;
        justify-content: center;
        position: relative;
    }

    .notif-btn.enabled {
        background: #27ae60;
        box-shadow: 0 0 0 2px rgba(39, 174, 96, 0.35);
    }
</style>