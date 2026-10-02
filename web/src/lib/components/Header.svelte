<script lang="ts">
    import { user, balance, connectionState } from '../stores';
    import { onMount } from 'svelte';
    import { detachPushSubscription, getPermission, getPushSupport } from '../push';
    import { disconnect } from '../signalr';
    import NotificationSettings from './NotificationSettings.svelte';
    import BalanceHistory from './BalanceHistory.svelte';
    import TransferModal from './TransferModal.svelte';

    let showNotifButton = false;
    let notificationsEnabled = false;
    let showSettings = false;
    let showBalanceHistory = false;
    let showTransfer = false;

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

    function logout() {
        // Отвязываем push на сервере, чтобы уведомления не уходили на телефон
        // следующего пользователя. Браузерную подписку при этом не удаляем.
        void Promise.race([
            detachPushSubscription(),
            new Promise<void>((resolve) => setTimeout(resolve, 1500))
        ]).then(async () => {
            await disconnect();
            user.set(null);
            localStorage.removeItem('party_token');
            location.reload();
        });
    }
</script>

<header>
    <div class="header-left">
        <span class="logo">🎉</span>
        <span class="name" title={$user?.displayName}>{$user?.displayName}</span>
        {#if $user?.role === 'Admin'}
            <a href="/admin" class="admin-link" title="Админка" aria-label="Админка">
                ⚙️<span class="admin-link-text"> Админка</span>
            </a>
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
        <button
            class="transfer-btn"
            on:click={() => (showTransfer = true)}
            title="Перевести баллы"
            aria-label="Перевести баллы"
        >💸</button>
        <button
            class="balance"
            on:click={() => (showBalanceHistory = true)}
            title="История баллов"
            aria-label="История баллов"
        >⭐ {$balance}</button>
        <span class="conn" title={$connectionState === 'connected' ? 'Подключено' : 'Нет соединения'}>
            {$connectionState === 'connected' ? '🟢' : '🔴'}
        </span>
        <button class="logout-btn" on:click={logout} title="Выйти" aria-label="Выйти">🚪</button>
    </div>
</header>

<NotificationSettings open={showSettings} on:close={closeSettings} />
<BalanceHistory open={showBalanceHistory} on:close={() => (showBalanceHistory = false)} />
<TransferModal open={showTransfer} on:close={() => (showTransfer = false)} />

<style>
    header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 10px;
        background: var(--card, #1a1a3e);
        border-bottom: 2px solid var(--accent, #f5a623);
        position: sticky;
        top: 0;
        z-index: 10;

        /* Safe area для iPhone с монобровью и вырезом сбоку */
        padding: 14px 20px;
        padding-top: calc(14px + env(safe-area-inset-top, 0px));
        padding-left: calc(20px + env(safe-area-inset-left, 0px));
        padding-right: calc(20px + env(safe-area-inset-right, 0px));
    }

    .header-left {
        display: flex;
        align-items: center;
        gap: 10px;
        min-width: 0;
        flex: 1;
    }

    .header-right {
        display: flex;
        align-items: center;
        gap: 10px;
        flex-shrink: 0;
    }

    .logo { font-size: 22px; }

    .name {
        font-weight: bold;
        font-size: 15px;
        min-width: 0;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .balance {
        background: none;
        border: none;
        padding: 0;
        font-family: inherit;
        font-size: 17px;
        font-weight: bold;
        color: var(--green, #27ae60);
        white-space: nowrap;
        cursor: pointer;
        transition: opacity 0.2s;
    }

    .balance:hover { opacity: 0.75; }

    .transfer-btn {
        background: none;
        border: none;
        padding: 0;
        font-size: 17px;
        line-height: 1;
        cursor: pointer;
        transition: opacity 0.2s;
    }

    .transfer-btn:hover { opacity: 0.75; }

    .conn { font-size: 12px; }

    .admin-link {
        background: var(--purple, #8e44ad);
        color: #fff;
        text-decoration: none;
        padding: 6px 12px;
        border-radius: 6px;
        font-size: 13px;
        font-weight: 500;
        white-space: nowrap;
        transition: background 0.2s;
    }

    .admin-link:hover { background: #9b59b6; }

    .notif-btn {
        background: var(--accent, #f5a623);
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
        flex-shrink: 0;
    }

    .notif-btn.enabled {
        background: var(--green, #27ae60);
        box-shadow: 0 0 0 2px rgba(39, 174, 96, 0.35);
    }

    .logout-btn {
        background: none;
        border: none;
        color: var(--red, #e74c3c);
        width: 34px;
        height: 34px;
        border-radius: 50%;
        font-size: 18px;
        cursor: pointer;
        display: flex;
        align-items: center;
        justify-content: center;
        flex-shrink: 0;
        transition: background 0.2s;
    }

    .logout-btn:hover { background: rgba(231, 76, 60, 0.15); }

    @media (max-width: 480px) {
        header {
            gap: 8px;
            padding-left: calc(12px + env(safe-area-inset-left, 0px));
            padding-right: calc(12px + env(safe-area-inset-right, 0px));
        }

        .header-left { gap: 8px; }
        .header-right { gap: 8px; }

        .admin-link { padding: 6px 8px; }
        .admin-link-text { display: none; }

        .balance { font-size: 15px; }
        .logout-btn { width: 30px; height: 30px; font-size: 16px; }
    }
</style>
