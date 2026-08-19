<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../api';
    import { connect, disconnect } from '../signalr';
    import { activeEvents, balance, user } from '../stores';
    import Header from './Header.svelte';
    import EventCard from './EventCard.svelte';

    onMount(async () => {
        try {
            const events = await api<any[]>('/api/events/available');
            activeEvents.set(events);

            const b = await api<any>('/api/wallet/balance');
            balance.set(b.balance);

            await connect();
        } catch (e) {
            console.error(e);
        }
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