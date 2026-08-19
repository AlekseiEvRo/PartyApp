<script lang="ts">
    import { user, balance, connectionState } from '../stores';
    import { canRequestPermission, requestNotificationPermission } from '../notifications';
    import { onMount } from 'svelte';

    let showNotifButton = false;

    onMount(() => {
        if (canRequestPermission() && Notification.permission === 'default') {
            showNotifButton = true;
        }
    });

    async function enableNotifications() {
        const result = await requestNotificationPermission();
        if (result === 'granted') {
            showNotifButton = false;
            new Notification('🔔 Уведомления включены!', {
                body: 'Теперь ты узнаешь о новых ивентах первым'
            });
        } else if (result === 'denied') {
            showNotifButton = false;
        }
    }
</script>

<header>
    <div class="header-left">
        <span class="logo">🎉</span>
        <span class="name">{$user?.displayName}</span>
    </div>
    <div class="header-right">
        {#if showNotifButton}
            <button class="notif-btn" on:click={enableNotifications} title="Включить уведомления">
                🔔
            </button>
        {/if}
        <span class="balance">⭐ {$balance}</span>
        <span class="conn">{$connectionState === 'connected' ? '🟢' : '🔴'}</span>
    </div>
</header>

<style>
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
    }
</style>