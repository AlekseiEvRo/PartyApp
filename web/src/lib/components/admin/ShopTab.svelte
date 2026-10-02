<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface ShopItem {
        id: string;
        name: string;
        description: string | null;
        price: number;
        stock: number | null;
        isActive: boolean;
    }

    interface Purchase {
        id: string;
        itemName: string;
        price: number;
        isFulfilled: boolean;
        createdAt: string;
        playerName: string;
    }

    interface AdminLot {
        id: string;
        name: string;
        description: string | null;
        minBid: number;
        durationMinutes: number;
        endsAt: string | null;
        status: string;
        winnerName: string | null;
        winningBid: number | null;
        bids: { amount: number; playerName: string; createdAt: string }[];
    }

    let items: ShopItem[] = [];
    let lots: AdminLot[] = [];
    let purchases: Purchase[] = [];
    let busy = false;
    let error = '';

    // Форма товара
    let editingId: string | null = null;
    let itemName = '';
    let itemDescription = '';
    let itemPrice = 30;
    let itemStock = '';
    let itemActive = true;

    // Форма лота: длительность в часах и минутах, старт — отдельной кнопкой
    let lotName = '';
    let lotDescription = '';
    let lotMinBid = 10;
    let lotHours = 0;
    let lotMinutes = 30;

    onMount(load);

    async function load() {
        error = '';

        try {
            [items, lots, purchases] = await Promise.all([
                api<ShopItem[]>('/api/shop/items?includeInactive=true'),
                api<AdminLot[]>('/api/shop/lots/all'),
                api<Purchase[]>('/api/shop/purchases')
            ]);
        } catch (e) {
            error = e instanceof Error ? e.message : 'Не удалось загрузить магазин';
        }
    }

    function resetItemForm() {
        editingId = null;
        itemName = '';
        itemDescription = '';
        itemPrice = 30;
        itemStock = '';
        itemActive = true;
    }

    function editItem(item: ShopItem) {
        editingId = item.id;
        itemName = item.name;
        itemDescription = item.description ?? '';
        itemPrice = item.price;
        itemStock = item.stock === null ? '' : String(item.stock);
        itemActive = item.isActive;
    }

    async function saveItem() {
        busy = true;

        try {
            const payload = {
                name: itemName,
                description: itemDescription.trim() || null,
                price: Number(itemPrice),
                stock: itemStock === '' ? null : Number(itemStock),
                isActive: itemActive
            };

            if (editingId) {
                await api(`/api/shop/items/${editingId}`, 'PUT', payload);
                showToast('Товар обновлён');
            } else {
                await api('/api/shop/items', 'POST', payload);
                showToast('Товар добавлен');
            }

            resetItemForm();
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось сохранить товар', 'error');
        } finally {
            busy = false;
        }
    }

    async function toggleItem(item: ShopItem) {
        busy = true;

        try {
            await api(`/api/shop/items/${item.id}`, 'PUT', {
                name: item.name,
                description: item.description,
                price: item.price,
                stock: item.stock,
                isActive: !item.isActive
            });
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось изменить товар', 'error');
        } finally {
            busy = false;
        }
    }

    async function removeItem(item: ShopItem) {
        if (!confirm(`Удалить «${item.name}»?`)) return;

        busy = true;

        try {
            await api(`/api/shop/items/${item.id}`, 'DELETE');
            showToast('Товар удалён', 'info');
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось удалить товар', 'error');
        } finally {
            busy = false;
        }
    }

    async function createLot() {
        const durationMinutes = Number(lotHours) * 60 + Number(lotMinutes);

        if (!lotName.trim() || durationMinutes <= 0) {
            showToast('Заполни название и длительность приёма ставок', 'error');
            return;
        }

        busy = true;

        try {
            await api('/api/shop/lots', 'POST', {
                name: lotName,
                description: lotDescription.trim() || null,
                minBid: Number(lotMinBid),
                durationMinutes
            });
            showToast('Лот создан. Нажми «Начать», когда будете готовы');
            lotName = '';
            lotDescription = '';
            lotMinBid = 10;
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось создать лот', 'error');
        } finally {
            busy = false;
        }
    }

    async function startLot(lot: AdminLot) {
        busy = true;

        try {
            await api(`/api/shop/lots/${lot.id}/start`, 'POST');
            showToast(`🔨 Лот «${lot.name}» запущен!`);
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось запустить лот', 'error');
        } finally {
            busy = false;
        }
    }

    async function closeLot(lot: AdminLot) {
        if (!confirm(`Закрыть лот «${lot.name}» сейчас? Победит максимальная ставка.`)) return;

        busy = true;

        try {
            await api(`/api/shop/lots/${lot.id}/close`, 'POST');
            showToast('Лот закрыт');
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось закрыть лот', 'error');
        } finally {
            busy = false;
        }
    }

    async function cancelLot(lot: AdminLot) {
        if (!confirm(`Отменить лот «${lot.name}»? Все ставки вернутся игрокам.`)) return;

        busy = true;

        try {
            await api(`/api/shop/lots/${lot.id}/cancel`, 'POST');
            showToast('Лот отменён', 'info');
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось отменить лот', 'error');
        } finally {
            busy = false;
        }
    }

    async function fulfill(purchase: Purchase) {
        busy = true;

        try {
            await api(`/api/shop/purchases/${purchase.id}/fulfill`, 'POST');
            showToast('Отмечено как выданное');
            await load();
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не удалось отметить', 'error');
        } finally {
            busy = false;
        }
    }

    function statusLabel(status: string): string {
        switch (status) {
            case 'Draft':
                return 'черновик';
            case 'Finished':
                return 'завершён';
            case 'Cancelled':
                return 'отменён';
            default:
                return 'идёт';
        }
    }

    function formatTime(value: string): string {
        return new Date(value).toLocaleString('ru-RU', {
            day: 'numeric',
            month: 'short',
            hour: '2-digit',
            minute: '2-digit'
        });
    }
</script>

<h2>🛍 Магазин призов</h2>

{#if error}
    <p class="message error">{error}</p>
{/if}

<h3>Товары</h3>

<div class="form">
    <input type="text" bind:value={itemName} maxlength="100" placeholder="Название" />
    <input type="text" bind:value={itemDescription} maxlength="500" placeholder="Описание (необязательно)" />
    <input type="number" bind:value={itemPrice} min="1" placeholder="Цена" title="Цена в баллах" />
    <input type="number" bind:value={itemStock} min="0" placeholder="Кол-во (пусто = ∞)" />
    <label class="check"><input type="checkbox" bind:checked={itemActive} /> активен</label>
    <button class="btn accent" on:click={saveItem} disabled={busy}>
        {editingId ? 'Сохранить' : 'Добавить'}
    </button>
    {#if editingId}
        <button class="btn" on:click={resetItemForm} disabled={busy}>Отмена</button>
    {/if}
</div>

<ul class="list">
    {#each items as item (item.id)}
        <li class="row">
            <div class="info">
                <span class="name">{item.name}</span>
                <span class="meta">
                    ⭐ {item.price} · {item.stock === null ? '∞' : `осталось ${item.stock}`}
                    {item.isActive ? '' : ' · выключен'}
                </span>
            </div>
            <div class="actions">
                <button class="btn" on:click={() => editItem(item)} disabled={busy}>Изменить</button>
                <button class="btn" on:click={() => toggleItem(item)} disabled={busy}>
                    {item.isActive ? 'Выключить' : 'Включить'}
                </button>
                <button class="btn danger" on:click={() => removeItem(item)} disabled={busy}>Удалить</button>
            </div>
        </li>
    {/each}
</ul>

<h3>Аукцион</h3>

<div class="form">
    <input type="text" bind:value={lotName} maxlength="120" placeholder="Название лота" />
    <input type="text" bind:value={lotDescription} maxlength="500" placeholder="Описание (необязательно)" />
    <input type="number" bind:value={lotMinBid} min="1" placeholder="Мин. ставка" />
    <input type="number" bind:value={lotHours} min="0" max="24" placeholder="Часы" title="Длительность: часы" />
    <input type="number" bind:value={lotMinutes} min="0" max="59" placeholder="Минуты" title="Длительность: минуты" />
    <button class="btn accent" on:click={createLot} disabled={busy}>Создать лот</button>
</div>

<ul class="list">
    {#each lots as lot (lot.id)}
        <li class="row lot">
            <div class="info">
                <span class="name">{lot.name}</span>
                <span class="meta">
                    {statusLabel(lot.status)}
                    {#if lot.status === 'Draft'}
                        · приём {lot.durationMinutes} мин
                    {:else if lot.endsAt}
                        · до {formatTime(lot.endsAt)}
                    {/if}
                    · от {lot.minBid}
                    {#if lot.winnerName}· победил {lot.winnerName} ({lot.winningBid}){/if}
                </span>
                {#if lot.bids.length > 0}
                    <span class="bids">
                        {#each lot.bids as bid}
                            <span class="bid">{bid.playerName}: {bid.amount}</span>
                        {/each}
                    </span>
                {/if}
            </div>
            <div class="actions">
                {#if lot.status === 'Draft'}
                    <button class="btn accent" on:click={() => startLot(lot)} disabled={busy}>Начать</button>
                    <button class="btn danger" on:click={() => cancelLot(lot)} disabled={busy}>Отменить</button>
                {:else if lot.status === 'Open'}
                    <button class="btn" on:click={() => closeLot(lot)} disabled={busy}>Закрыть</button>
                    <button class="btn danger" on:click={() => cancelLot(lot)} disabled={busy}>Отменить</button>
                {/if}
            </div>
        </li>
    {/each}
</ul>

<h3>Покупки</h3>

{#if purchases.length === 0}
    <p class="muted">Пока никто ничего не купил.</p>
{:else}
    <ul class="list">
        {#each purchases as purchase (purchase.id)}
            <li class="row">
                <div class="info">
                    <span class="name">{purchase.itemName} · ⭐ {purchase.price}</span>
                    <span class="meta">
                        {purchase.playerName} · {formatTime(purchase.createdAt)}
                        {purchase.isFulfilled ? ' · выдан' : ''}
                    </span>
                </div>
                {#if !purchase.isFulfilled}
                    <div class="actions">
                        <button class="btn accent" on:click={() => fulfill(purchase)} disabled={busy}>Выдан</button>
                    </div>
                {/if}
            </li>
        {/each}
    </ul>
{/if}

<style>
    h2 { margin-bottom: 14px; }
    h3 { margin: 20px 0 10px; }

    .form {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-bottom: 14px;
    }

    .form input[type='text'],
    .form input[type='number'] {
        background: var(--bg-soft, #12122e);
        border: 1px solid #2a2a5e;
        border-radius: 8px;
        color: inherit;
        padding: 9px 12px;
        font-size: 14px;
        min-width: 140px;
    }

    .check {
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: 13px;
        color: var(--muted, #aaa);
    }

    .list {
        list-style: none;
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin-bottom: 8px;
    }

    .row {
        display: flex;
        align-items: center;
        gap: 12px;
        background: var(--bg-soft, #12122e);
        border-radius: 10px;
        padding: 10px 12px;
        flex-wrap: wrap;
    }

    .info {
        flex: 1;
        min-width: 180px;
        display: flex;
        flex-direction: column;
        gap: 3px;
    }

    .name { font-weight: bold; font-size: 14px; }
    .meta { font-size: 12px; color: var(--muted, #aaa); }

    .bids {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
        margin-top: 4px;
    }

    .bid {
        font-size: 12px;
        background: rgba(245, 166, 35, 0.15);
        border-radius: 999px;
        padding: 2px 8px;
    }

    .actions { display: flex; gap: 6px; flex-wrap: wrap; }

    .btn {
        background: #2a2a5a;
        color: #ddd;
        border: none;
        border-radius: 8px;
        padding: 7px 12px;
        font-size: 13px;
        font-weight: bold;
        cursor: pointer;
    }

    .btn.accent { background: #27ae60; color: #fff; }
    .btn.danger { background: #e74c3c; color: #fff; }
    .btn:disabled { opacity: 0.5; cursor: default; }

    .muted { color: var(--muted, #aaa); }

    .message {
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
    }

    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
