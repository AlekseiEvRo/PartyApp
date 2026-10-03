<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface PartyDto {
        id: string;
        name: string;
        status: string;
        startedAt: string;
        endedAt: string | null;
        createdAt: string;
    }

    interface PartyListItem extends PartyDto {
        sessionsCount: number;
        photosCount: number;
    }

    interface TopPlayer {
        playerId: string;
        displayName: string;
        earned: number;
    }

    interface Summary {
        party: PartyDto;
        durationMinutes: number;
        playersCount: number;
        topPlayers: TopPlayer[];
        eventsCount: number;
        eventsByType: Record<string, number>;
        submissionsCount: number;
        photosCount: number;
        topPhoto: { photoId: string; caption: string | null; uploadedByName: string; likesCount: number } | null;
        wishesCount: number;
    }

    interface ScheduleItem {
        id: string;
        definitionId: string;
        displayName: string;
        type: string;
        description: string | null;
        order: number;
        startedAt: string | null;
        sessionId: string | null;
    }

    interface Definition {
        id: string;
        displayName: string;
        type: string;
        isActive: boolean;
    }

    let parties: PartyListItem[] = [];
    let current: Summary | null = null;
    let schedule: ScheduleItem[] = [];
    let definitions: Definition[] = [];
    let selectedDefinitionId = '';
    let newName = '';
    let resetBalances = true;
    let creating = false;
    let busy = false;
    let loading = true;
    let openedSummary: Summary | null = null;

    onMount(load);

    async function load() {
        loading = true;
        try {
            parties = await api<PartyListItem[]>('/api/parties');
            const currentResponse = await api<{ summary: Summary | null }>('/api/parties/current');
            current = currentResponse.summary;

            if (current && current.party.status === 'Active') {
                schedule = await api<ScheduleItem[]>(`/api/parties/${current.party.id}/schedule`);
                definitions = await api<Definition[]>('/api/events/definitions');
                if (!selectedDefinitionId && definitions.length > 0) {
                    selectedDefinitionId = definitions[0].id;
                }
            } else {
                schedule = [];
            }

            if (!newName) {
                newName = `Вечеринка ${new Date().toLocaleDateString('ru-RU')}`;
            }
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            loading = false;
        }
    }

    async function startParty() {
        const name = newName.trim();
        if (!name) return;

        const message = current && current.party.status === 'Active'
            ? `Начать новую вечеринку «${name}»? Текущая завершится, активные ивенты закроются` +
              (resetBalances ? ', а баллы всех игроков обнулятся.' : '.')
            : `Начать вечеринку «${name}»?` + (resetBalances ? ' Баллы игроков обнулятся.' : '');

        if (!confirm(message)) return;

        creating = true;
        try {
            await api('/api/parties', 'POST', { name, resetBalances });
            showToast('🎉 Вечеринка началась!', 'info');
            await load();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            creating = false;
        }
    }

    async function finishParty() {
        if (!current || current.party.status !== 'Active') return;
        if (!confirm(`Завершить вечеринку «${current.party.name}»? Активные ивенты закроются, экран покажет итоги.`)) return;

        busy = true;
        try {
            const summary = await api<Summary>(`/api/parties/${current.party.id}/finish`, 'POST');
            showToast('🏁 Вечеринка завершена', 'info');
            openedSummary = summary;
            await load();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function showSummaryOnScreen() {
        busy = true;
        try {
            await api('/api/screen/state', 'POST', { mode: 'summary' });
            showToast('🎊 Итоги на большом экране');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function addScheduleItem() {
        if (!current || !selectedDefinitionId) return;

        busy = true;
        try {
            await api(`/api/parties/${current.party.id}/schedule`, 'POST', { definitionId: selectedDefinitionId });
            schedule = await api<ScheduleItem[]>(`/api/parties/${current.party.id}/schedule`);
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function moveItem(item: ScheduleItem, up: boolean) {
        if (!current) return;

        busy = true;
        try {
            await api(`/api/parties/${current.party.id}/schedule/${item.id}/move`, 'POST', { up });
            schedule = await api<ScheduleItem[]>(`/api/parties/${current.party.id}/schedule`);
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function removeItem(item: ScheduleItem) {
        if (!current) return;
        if (!confirm(`Убрать «${item.displayName}» из сценария?`)) return;

        busy = true;
        try {
            await api(`/api/parties/${current.party.id}/schedule/${item.id}`, 'DELETE');
            schedule = await api<ScheduleItem[]>(`/api/parties/${current.party.id}/schedule`);
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function startItem(item: ScheduleItem) {
        if (!current) return;
        if (!confirm(`Запустить «${item.displayName}»?`)) return;

        busy = true;
        try {
            await api(`/api/parties/${current.party.id}/schedule/${item.id}/start`, 'POST');
            showToast(`▶ ${item.displayName} запущен`, 'info');
            await load();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function openSummary(party: PartyListItem) {
        try {
            openedSummary = await api<Summary>(`/api/parties/${party.id}/summary`);
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    function formatDate(value: string | null): string {
        return value ? new Date(value).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—';
    }
</script>

<h2>🎉 Вечеринки</h2>

{#if loading}
    <p style="color:#666;">Загрузка...</p>
{:else}
    <div class="current">
        {#if current}
            <div>
                <strong>{current.party.name}</strong>
                <span class="status" class:active={current.party.status === 'Active'}>
                    {current.party.status === 'Active' ? 'идёт' : 'завершена'}
                </span>
                <span class="hint">
                    с {formatDate(current.party.startedAt)}
                    {#if current.party.endedAt} до {formatDate(current.party.endedAt)}{/if}
                </span>
            </div>
            <div class="actions">
                <button class="btn" on:click={showSummaryOnScreen} disabled={busy}>🎊 Итоги на экран</button>
                {#if current.party.status === 'Active'}
                    <button class="btn danger" on:click={finishParty} disabled={busy}>🏁 Завершить</button>
                {/if}
            </div>
        {:else}
            <p class="hint">
                Вечеринка ещё не начата: баллы и лидерборд общие, как раньше. Начни смену,
                чтобы обнулить баллы и вести статистику.
            </p>
        {/if}
    </div>

    <div class="block">
        <p class="group-label">Начать новую вечеринку</p>
        <div class="inline">
            <input type="text" bind:value={newName} maxlength="100" placeholder="Название вечеринки" />
            <label class="checkbox">
                <input type="checkbox" bind:checked={resetBalances} />
                Обнулить баллы
            </label>
            <button class="btn accent" on:click={startParty} disabled={creating || newName.trim().length === 0}>
                {creating ? 'Начинаем…' : '🚀 Начать'}
            </button>
        </div>
    </div>

    {#if current && current.party.status === 'Active'}
        <div class="block">
            <p class="group-label">Сценарий вечеринки: {current.party.name}</p>
            <div class="inline">
                <select bind:value={selectedDefinitionId}>
                    {#if definitions.length === 0}
                        <option value="">Нет доступных ивентов</option>
                    {/if}
                    {#each definitions as definition (definition.id)}
                        <option value={definition.id}>{definition.displayName}</option>
                    {/each}
                </select>
                <button class="btn" on:click={addScheduleItem} disabled={busy || !selectedDefinitionId}>
                    ➕ Добавить в очередь
                </button>
            </div>

            {#if schedule.length === 0}
                <p class="hint">Очередь пуста. Добавь ивенты — экран в режиме ожидания покажет «Далее».</p>
            {:else}
                <ol class="schedule">
                    {#each schedule as item (item.id)}
                        <li class:started={item.startedAt}>
                            <span class="order">{item.order}</span>
                            <span class="item-name">{item.displayName}</span>
                            <span class="hint">{item.startedAt ? `запущен ${formatDate(item.startedAt)}` : 'в очереди'}</span>
                            <span class="actions">
                                <button class="btn-small" disabled={busy || !!item.startedAt}
                                        title="Выше" on:click={() => moveItem(item, true)}>↑</button>
                                <button class="btn-small" disabled={busy || !!item.startedAt}
                                        title="Ниже" on:click={() => moveItem(item, false)}>↓</button>
                                <button class="btn-small" disabled={busy || !!item.startedAt}
                                        title="Запустить" on:click={() => startItem(item)}>▶</button>
                                <button class="btn-small" disabled={busy}
                                        title="Убрать" on:click={() => removeItem(item)}>🗑</button>
                            </span>
                        </li>
                    {/each}
                </ol>
            {/if}
        </div>
    {/if}

    <h2 class="history-title">История вечеринок</h2>
    {#if parties.length === 0}
        <p class="hint">Вечеринок пока не было.</p>
    {:else}
        <div class="table-wrap">
            <table>
                <thead>
                <tr>
                    <th>Название</th>
                    <th>Статус</th>
                    <th>Начало</th>
                    <th>Конец</th>
                    <th>Ивентов</th>
                    <th>Фото</th>
                    <th></th>
                </tr>
                </thead>
                <tbody>
                {#each parties as party (party.id)}
                    <tr>
                        <td>{party.name}</td>
                        <td>{party.status === 'Active' ? '🔥 идёт' : '✅ завершена'}</td>
                        <td>{formatDate(party.startedAt)}</td>
                        <td>{formatDate(party.endedAt)}</td>
                        <td>{party.sessionsCount}</td>
                        <td>{party.photosCount}</td>
                        <td>
                            <button class="btn-history" on:click={() => openSummary(party)}>📊 Итоги</button>
                        </td>
                    </tr>
                {/each}
                </tbody>
            </table>
        </div>
    {/if}
{/if}

{#if openedSummary}
    <div class="summary-box">
        <div class="summary-head">
            <h3>🎊 {openedSummary.party.name}</h3>
            <button class="btn-small" on:click={() => (openedSummary = null)}>✖</button>
        </div>
        <p class="hint">
            {openedSummary.party.status === 'Active' ? 'Идёт' : 'Завершена'} ·
            {openedSummary.durationMinutes} мин · {openedSummary.playersCount} участников
        </p>
        <p>
            🎮 {openedSummary.eventsCount} ивентов · ✍ {openedSummary.submissionsCount} ответов ·
            📸 {openedSummary.photosCount} фото · 💌 {openedSummary.wishesCount} пожеланий
        </p>
        {#if openedSummary.topPlayers.length > 0}
            <ol class="summary-players">
                {#each openedSummary.topPlayers.slice(0, 5) as player, i (player.playerId)}
                    <li>
                        <span class="order">{i + 1}</span>
                        <span class="item-name">{player.displayName}</span>
                        <strong>+{player.earned}</strong>
                    </li>
                {/each}
            </ol>
        {/if}
        {#if openedSummary.topPhoto}
            <p class="hint">
                📸 Лучшее фото: {openedSummary.topPhoto.caption ?? openedSummary.topPhoto.uploadedByName}
                (♥ {openedSummary.topPhoto.likesCount})
            </p>
        {/if}
    </div>
{/if}

<style>
    .current {
        display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap;
        padding: 14px 16px; border-radius: 10px; background: var(--bg-soft, #12122e);
        border: 1px solid var(--border, #333);
    }
    .status { margin-left: 8px; color: var(--muted, #aaa); }
    .status.active { color: var(--green, #27ae60); font-weight: bold; }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .block { margin-top: 20px; }
    .group-label { color: var(--muted, #aaa); font-size: 13px; margin-bottom: 6px; }
    .inline { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .inline input[type='text'] { flex: 1; min-width: 220px; }
    .inline select, .inline input[type='text'] {
        background: var(--bg-soft, #12122e); border: 1px solid #2a2a5e; border-radius: 8px;
        color: inherit; padding: 9px 12px; font-size: 14px;
    }
    .checkbox { display: flex; align-items: center; gap: 6px; font-size: 14px; color: var(--muted, #aaa); }
    .btn {
        background: var(--blue, #3498db); color: #fff; border: none; border-radius: 8px;
        padding: 9px 16px; font-size: 14px; font-weight: bold; cursor: pointer;
    }
    .btn.accent { background: var(--accent, #f5a623); color: #12122e; }
    .btn.danger { background: var(--red, #e74c3c); }
    .btn:disabled { opacity: 0.5; cursor: default; }
    .btn-small {
        padding: 6px 9px; border: 1px solid var(--border, #333); border-radius: 6px;
        background: none; color: var(--text, #eee); font-size: 13px; cursor: pointer;
    }
    .btn-small:hover:not(:disabled) { background: var(--card-soft, #2a2a5e); }
    .btn-small:disabled { opacity: 0.4; cursor: default; }
    .schedule { list-style: none; padding: 0; margin: 12px 0 0; }
    .schedule li {
        display: flex; align-items: center; gap: 10px; padding: 8px 12px;
        border-bottom: 1px solid var(--border, #333); font-size: 14px;
    }
    .schedule li.started { opacity: 0.55; }
    .order { width: 22px; text-align: center; color: var(--muted, #aaa); }
    .item-name { flex: 1; }
    .hint { color: var(--muted, #aaa); font-size: 13px; }
    .history-title { margin-top: 32px; }
    .table-wrap { overflow-x: auto; margin-top: 12px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border, #333); white-space: nowrap; }
    th { color: var(--accent, #f5a623); font-weight: 600; }
    tr:hover { background: var(--card-soft, #2a2a5e); }
    .btn-history {
        padding: 6px 10px; border: 1px solid var(--border, #333); border-radius: 6px;
        background: none; color: var(--accent, #f5a623); font-size: 13px; cursor: pointer;
    }
    .summary-box {
        margin-top: 20px; padding: 16px; border-radius: 10px;
        background: var(--bg-soft, #12122e); border: 1px solid var(--accent, #f5a623);
    }
    .summary-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
    .summary-head h3 { margin: 0; }
    .summary-players { list-style: none; padding: 0; margin: 12px 0 0; }
    .summary-players li {
        display: flex; align-items: center; gap: 10px; padding: 6px 0;
        border-bottom: 1px solid var(--border, #333); font-size: 14px;
    }
</style>
