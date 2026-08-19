<script lang="ts">
    import { onMount, onDestroy } from 'svelte';
    import { api } from '../api';
    import { connect, disconnect, reconnectIfNeeded } from '../signalr';
    import { activeEvents, balance, user } from '../stores';
    import Header from './Header.svelte';
    import EventCard from './EventCard.svelte';

    async function loadInitialData() {
        try {
            const events = await api<any[]>('/api/events/available');
            activeEvents.set(events);

            const b = await api<any>('/api/wallet/balance');
            balance.set(b.balance);
        } catch (e) {
            console.error('Failed to load initial data:', e);
        }
    }

    function handleVisibilityChange() {
        if (document.visibilityState === 'visible') {
            // Пользователь вернулся на страницу — переподключаемся и обновляем данные
            reconnectIfNeeded();
            loadInitialData();
        }
    }

    onMount(async () => {
        await loadInitialData();
        try {
            await connect();
        } catch (e) {
            console.error('SignalR connect failed:', e);
        }

        document.addEventListener('visibilitychange', handleVisibilityChange);
    });

    onDestroy(() => {
        document.removeEventListener('visibilitychange', handleVisibilityChange);
    });

    function logout() {
        disconnect();
        user.set(null);
        localStorage.removeItem('party_token');
        location.reload();
    }
</script>

<div class="party">
    <Header />
    <main>
        <h2>🎮 Активные ивенты</h2>

        {#if $activeEvents.length === 0}
            <div class="empty">
                <p>Пока нет активных ивентов.</p>
                <p>Жди, когда админ запустит что-нибудь интересное! 👀</p>
            </div>
        {:else}
            {#each $activeEvents as event (event.sessionId)}
                <EventCard {event} />
            {/each}
        {/if}
    </main>

    <button class="logout" on:click={logout}>Выйти</button>
</div>