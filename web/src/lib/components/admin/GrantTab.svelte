<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    let players: any[] = [];
    let selectedPlayerId = '';
    let grantAmount = 10;
    let grantReason = '';
    let grantAllAmount = 5;
    let grantAllReason = '';

    async function loadPlayers() {
        try {
            players = await api<any[]>('/api/admin/players');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function grantPoints() {
        if (!selectedPlayerId) {
            showToast('Выбери игрока', 'error');
            return;
        }
        try {
            const result = await api<any>('/api/admin/grant-points', 'POST', {
                playerId: selectedPlayerId,
                amount: grantAmount,
                reason: grantReason || 'Ручное начисление'
            });
            showToast(`${grantAmount > 0 ? '+' : ''}${grantAmount} баллов. Новый баланс: ${result.newBalance}`);
            loadPlayers();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function grantToAll() {
        if (!grantAllAmount || grantAllAmount === 0) {
            showToast('Укажи сумму', 'error');
            return;
        }
        try {
            const allPlayers = await api<any[]>('/api/admin/players');
            const targets = allPlayers.filter(p => p.role === 'Player');
            if (targets.length === 0) {
                showToast('Нет игроков с ролью Player', 'error');
                return;
            }
            let successCount = 0;
            const errors: string[] = [];
            for (const player of targets) {
                try {
                    await api('/api/admin/grant-points', 'POST', {
                        playerId: player.id,
                        amount: grantAllAmount,
                        reason: grantAllReason || 'Бонус за участие'
                    });
                    successCount++;
                } catch (e: any) {
                    errors.push(`${player.displayName}: ${e.message}`);
                }
            }
            if (successCount > 0) showToast(`Начислено ${grantAllAmount} баллов ${successCount} игрокам`);
            if (errors.length > 0) showToast(`Ошибки: ${errors.join('; ')}`, 'error');
            loadPlayers();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    onMount(loadPlayers);
</script>

<h2>Ручное начисление баллов</h2>

<div class="card">
    <h3>Начислить конкретному игроку</h3>
    <div class="form-row">
        <div class="form-group">
            <label>Игрок</label>
            <select bind:value={selectedPlayerId}>
                <option value="">— выбери —</option>
                {#each players as p}
                    <option value={p.id}>{p.displayName} (⭐ {p.balance})</option>
                {/each}
            </select>
        </div>
        <div class="form-group">
            <label>Сумма (+ / −)</label>
            <input type="number" bind:value={grantAmount} />
        </div>
        <div class="form-group">
            <label>Причина</label>
            <input type="text" bind:value={grantReason} placeholder="Победа в Alias" />
        </div>
    </div>
    <button class="btn btn-success" on:click={grantPoints}>💰 Начислить</button>
</div>

<div class="card">
    <h3>Быстрое начисление всем игрокам</h3>
    <div class="form-row">
        <div class="form-group">
            <label>Сумма</label>
            <input type="number" bind:value={grantAllAmount} />
        </div>
        <div class="form-group">
            <label>Причина</label>
            <input type="text" bind:value={grantAllReason} placeholder="Бонус за участие" />
        </div>
    </div>
    <button class="btn btn-warning" on:click={grantToAll}>🎁 Начислить всем</button>
</div>

<style>
    .card { background: var(--bg, #0f0f23); border-radius: 8px; padding: 16px; margin: 16px 0; }
    .form-row { display: flex; gap: 16px; flex-wrap: wrap; margin-bottom: 12px; }
    .form-group { flex: 1; min-width: 200px; }
    .form-group label { display: block; margin-bottom: 6px; color: var(--muted, #aaa); font-size: 14px; }
    .form-group input, .form-group select {
        width: 100%; padding: 10px; border-radius: 6px;
        border: 1px solid var(--border, #333); background: var(--bg, #0f0f23); color: #fff;
    }
    .btn { padding: 10px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; }
    .btn-success { background: var(--green, #27ae60); color: #fff; }
    .btn-warning { background: var(--orange, #f39c12); color: #fff; }

    @media (max-width: 480px) {
        .form-group { min-width: 100%; }
    }
</style>