<script lang="ts">
    import { api } from '../api';
    import { balance, showToast, shopVersion } from '../stores';

    interface ShopItem {
        id: string;
        name: string;
        description: string | null;
        price: number;
        stock: number | null;
        isActive: boolean;
    }

    interface Lot {
        id: string;
        name: string;
        description: string | null;
        minBid: number;
        endsAt: string;
        bidsCount: number;
        topBid: number | null;
        leaderName: string | null;
        myBid: number | null;
    }

    interface Purchase {
        id: string;
        itemName: string;
        price: number;
        isFulfilled: boolean;
        createdAt: string;
    }

    let items: ShopItem[] = [];
    let lots: Lot[] = [];
    let purchases: Purchase[] = [];
    let loading = true;
    let error = '';
    let busyId: string | null = null;
    let bidInputs: Record<string, string> = {};
    let lastShopVersion = -1;

    // Загружаем при старте и обновляемся по SignalR: новые товары, ставки,
    // закрытие лота и статус «выдан» приходят без перезагрузки
    $: if ($shopVersion !== lastShopVersion) {
        lastShopVersion = $shopVersion;
        void load();
    }

    async function load() {
        loading = true;
        error = '';

        try {
            const [shopItems, auctionLots, myPurchases] = await Promise.all([
                api<ShopItem[]>('/api/shop/items'),
                api<Lot[]>('/api/shop/lots'),
                api<Purchase[]>('/api/shop/purchases/my')
            ]);

            items = shopItems;
            lots = auctionLots;
            purchases = myPurchases;

            for (const lot of lots) {
                bidInputs[lot.id] ??= '';
            }
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить магазин';
        } finally {
            loading = false;
        }
    }

    async function buy(item: ShopItem) {
        if (!confirm(`Купить «${item.name}» за ${item.price} баллов?`)) return;

        busyId = item.id;

        try {
            const result = await api<{ newBalance: number }>(`/api/shop/items/${item.id}/buy`, 'POST');
            balance.set(result.newBalance);
            showToast(`🎁 «${item.name}» куплен! Забери у ведущего`);
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось купить', 'error');
        } finally {
            busyId = null;
        }
    }

    async function placeBid(lot: Lot) {
        const amount = Number(bidInputs[lot.id] ?? '');

        if (!Number.isFinite(amount) || amount <= 0) {
            showToast('Введи ставку', 'error');
            return;
        }

        busyId = lot.id;

        try {
            const result = await api<{ newBalance: number }>(`/api/shop/lots/${lot.id}/bids`, 'POST', { amount });
            balance.set(result.newBalance);
            bidInputs = { ...bidInputs, [lot.id]: '' };
            showToast('✅ Ставка принята!');
            lots = await api<Lot[]>('/api/shop/lots');
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось сделать ставку', 'error');
        } finally {
            busyId = null;
        }
    }

    function formatEndsAt(value: string): string {
        return new Date(value).toLocaleString('ru-RU', {
            day: 'numeric',
            month: 'short',
            hour: '2-digit',
            minute: '2-digit'
        });
    }
</script>

<section class="shop">
    <h2>🛍 Магазин призов</h2>

    {#if error}
        <p class="message error">{error}</p>
    {:else if loading && items.length === 0 && lots.length === 0}
        <p class="muted">Загружаем…</p>
    {:else}
        {#if items.length > 0}
            <div class="items">
                {#each items as item (item.id)}
                    <div class="card">
                        <div class="info">
                            <span class="name">{item.name}</span>
                            {#if item.description}<span class="desc">{item.description}</span>{/if}
                            <span class="meta">
                                {item.stock === null ? 'без ограничений' : `осталось: ${item.stock}`}
                            </span>
                        </div>
                        <button
                            class="price-btn"
                            disabled={busyId === item.id || item.stock === 0}
                            on:click={() => buy(item)}
                        >
                            ⭐ {item.price}
                        </button>
                    </div>
                {/each}
            </div>
        {/if}

        {#if lots.length > 0}
            <h3>🔨 Аукцион</h3>
            <p class="hint">Открытый аукцион: лидер и сумма видны всем. Баллы замораживаются и вернутся, если не победишь.</p>

            <div class="items">
                {#each lots as lot (lot.id)}
                    <div class="card">
                        <div class="info">
                            <span class="name">{lot.name}</span>
                            {#if lot.description}<span class="desc">{lot.description}</span>{/if}
                            <span class="meta">
                                до {formatEndsAt(lot.endsAt)} · ставок: {lot.bidsCount}
                                {#if lot.topBid !== null}· лидер: {lot.leaderName} ({lot.topBid}){/if}
                                {#if lot.myBid !== null}· твоя: {lot.myBid}{/if}
                            </span>
                        </div>
                        <div class="bid-row">
                            <input
                                type="number"
                                min={lot.minBid}
                                placeholder="от {lot.minBid}"
                                bind:value={bidInputs[lot.id]}
                            />
                            <button
                                class="bid-btn"
                                disabled={busyId === lot.id}
                                on:click={() => placeBid(lot)}
                            >Ставка</button>
                        </div>
                    </div>
                {/each}
            </div>
        {/if}

        {#if items.length === 0 && lots.length === 0}
            <p class="muted">Пока нет призов — ждём, когда ведущий что-нибудь выставит!</p>
        {/if}

        {#if purchases.length > 0}
            <details class="purchases">
                <summary>Мои покупки ({purchases.length})</summary>
                <ul>
                    {#each purchases as purchase (purchase.id)}
                        <li>
                            <span>{purchase.itemName} · ⭐ {purchase.price}</span>
                            <span class:done={purchase.isFulfilled}>
                                {purchase.isFulfilled ? '✅ выдан' : 'ждёт выдачи'}
                            </span>
                        </li>
                    {/each}
                </ul>
            </details>
        {/if}
    {/if}
</section>

<style>
    .shop { margin-bottom: 24px; }

    h2 { margin-bottom: 12px; }
    h3 { margin: 18px 0 8px; }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        line-height: 1.5;
        margin-bottom: 10px;
    }

    .items {
        display: flex;
        flex-direction: column;
        gap: 8px;
    }

    .card {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        background: var(--bg-soft, #12122e);
        border-radius: 12px;
        padding: 12px 14px;
        flex-wrap: wrap;
    }

    .info {
        display: flex;
        flex-direction: column;
        gap: 2px;
        min-width: 0;
        flex: 1;
    }

    .name { font-weight: bold; font-size: 15px; }
    .desc { font-size: 13px; color: #ccc; overflow-wrap: anywhere; }
    .meta { font-size: 12px; color: var(--muted, #aaa); }

    .price-btn {
        background: var(--accent, #f5a623);
        color: #12122e;
        border: none;
        border-radius: 10px;
        padding: 10px 16px;
        font-size: 15px;
        font-weight: bold;
        cursor: pointer;
        white-space: nowrap;
    }

    .price-btn:disabled { opacity: 0.5; cursor: default; }

    .bid-row {
        display: flex;
        gap: 8px;
        align-items: center;
    }

    .bid-row input {
        width: 110px;
        background: #0f0f23;
        border: 1px solid #2a2a5e;
        border-radius: 10px;
        color: inherit;
        padding: 10px 12px;
        font-size: 14px;
    }

    .bid-btn {
        background: #27ae60;
        color: #fff;
        border: none;
        border-radius: 10px;
        padding: 10px 16px;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
    }

    .bid-btn:disabled { opacity: 0.5; cursor: default; }

    .purchases {
        margin-top: 16px;
        background: var(--bg-soft, #12122e);
        border-radius: 12px;
        padding: 12px 14px;
    }

    .purchases summary {
        cursor: pointer;
        font-size: 14px;
        color: var(--muted, #aaa);
    }

    .purchases ul {
        list-style: none;
        margin-top: 10px;
        display: flex;
        flex-direction: column;
        gap: 6px;
    }

    .purchases li {
        display: flex;
        justify-content: space-between;
        gap: 10px;
        font-size: 13px;
        color: #ccc;
    }

    .done { color: #27ae60; }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
