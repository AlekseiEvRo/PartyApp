<script lang="ts">
    import { onMount, onDestroy } from 'svelte';
    import { api } from '../api';
    import { connect, reconnectIfNeeded } from '../signalr';
    import { activeEvents, balance, spyGameRole, shopVersion } from '../stores';
    import Header from './Header.svelte';
    import EventCard from './EventCard.svelte';
    import SpyGame from './SpyGame.svelte';
    import PhotoGallery from './PhotoGallery.svelte';
    import WishesWall from './WishesWall.svelte';
    import ShopPanel from './ShopPanel.svelte';
    import ScreenReactions from './ScreenReactions.svelte';
    import InstallPrompt from './InstallPrompt.svelte';
    import { syncPushSubscription } from '../push';

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

    async function restoreSpyGameRole() {
        try {
            const role = await api<any>('/api/spygame/my-role');
            spyGameRole.set(role);
        } catch {
            spyGameRole.set(null);
        }
    }

    function handleVisibilityChange() {
        if (document.visibilityState === 'visible') {
            reconnectIfNeeded();
            loadInitialData();
            restoreSpyGameRole();
            // Пока приложение было свёрнуто, события могли не дойти (например,
            // старт аукциона) — магазин перечитывает лоты по этому сигналу
            shopVersion.update((v) => v + 1);
        }
    }

    onMount(async () => {
        await loadInitialData();
        await restoreSpyGameRole();

        try {
            await connect();
        } catch (e) {
            console.error('SignalR connect failed:', e);
        }

        document.addEventListener('visibilitychange', handleVisibilityChange);

        // Подписка устройства привязывается к тому, кто сейчас вошёл
        await syncPushSubscription();
    });

    onDestroy(() => {
        document.removeEventListener('visibilitychange', handleVisibilityChange);
    });
</script>

<div class="party">
    <Header />
    <InstallPrompt />
    <main>
        <SpyGame />
        
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

        <ShopPanel />
        <PhotoGallery />
        <WishesWall />
    </main>

    <ScreenReactions />
</div>