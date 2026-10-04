<script lang="ts">
    import { onMount, onDestroy } from 'svelte';
    import { api } from '../api';
    import { connect, reconnectIfNeeded } from '../signalr';
    import { activeEvents, balance, spyGameRole, shopVersion, showToast } from '../stores';
    import { getPendingQrCode, clearPendingQrCode } from '../qr';
    import Header from './Header.svelte';
    import EventCard from './EventCard.svelte';
    import PollCard from './PollCard.svelte';
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

    // === QR из ссылки ===
    // Код пришёл из QR-ссылки (?qr=…). Как только появляется активный ивент
    // «Охота за QR-кодами», отправляем его автоматически. При ошибке код
    // остаётся: EventCard подставит его в поле, чтобы можно было повторить вручную.
    let qrSubmitting = false;
    let qrAttemptKey: string | null = null;

    async function trySubmitPendingQr(events: any[]): Promise<void> {
        if (qrSubmitting) return;

        const code = getPendingQrCode();
        if (!code) return;

        const qrEvent = events.find((e) => e.type === 'qr_scan');
        if (!qrEvent) return;

        // Одна автоматическая попытка на пару «сессия + код»
        const key = `${qrEvent.sessionId}:${code}`;
        if (qrAttemptKey === key) return;
        qrAttemptKey = key;

        qrSubmitting = true;
        try {
            const result = await api<any>(`/api/events/${qrEvent.sessionId}/submit`, 'POST', {
                payloadJson: JSON.stringify({ code })
            });
            clearPendingQrCode();
            showToast(`📷 ${result.message}`, 'success');
        } catch (e: any) {
            // Сетевую ошибку есть смысл повторить (код останется), а «уже
            // использован»/«не найден» — нет
            const message: string = e?.message ?? '';
            const retryable = /failed to fetch|network|load failed|HTTP 5\d\d|таймаут/i.test(message);
            if (!retryable) clearPendingQrCode();

            showToast(message || 'Не удалось активировать QR-код', 'error');
        } finally {
            qrSubmitting = false;
        }
    }

    // Новый qr_scan-ивент мог стартовать уже после открытия приложения
    $: if ($activeEvents.length > 0) void trySubmitPendingQr($activeEvents);

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
        <PollCard />

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