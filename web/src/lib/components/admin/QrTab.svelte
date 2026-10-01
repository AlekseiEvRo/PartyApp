<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    let tokens: any[] = [];
    let qrCount = 5;
    let qrPoints = 20;

    async function loadTokens() {
        try {
            tokens = await api<any[]>('/api/qr/tokens');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function generateTokens() {
        try {
            const result = await api<any>('/api/qr/tokens/generate', 'POST', {
                count: qrCount,
                points: qrPoints
            });
            showToast(`Сгенерировано ${result.generated} кодов`);
            loadTokens();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    onMount(loadTokens);
</script>

<h2>QR-коды</h2>

<div class="card">
    <h3>Генерация новых кодов</h3>
    <div class="form-row">
        <div class="form-group">
            <label>Количество</label>
            <input type="number" bind:value={qrCount} min="1" max="100" />
        </div>
        <div class="form-group">
            <label>Баллов за код</label>
            <input type="number" bind:value={qrPoints} min="1" />
        </div>
        <div class="form-group">
            <label>&nbsp;</label>
            <button class="btn btn-success" on:click={generateTokens}>🎲 Сгенерировать</button>
        </div>
    </div>
</div>

<div class="card">
    <h3>Существующие коды</h3>
    <button class="btn btn-primary" on:click={loadTokens}>🔄 Обновить</button>
    <div class="table-wrap">
        <table>
            <thead>
            <tr>
                <th>Код</th>
                <th>Баллы</th>
                <th>Статус</th>
                <th>Использован</th>
            </tr>
            </thead>
            <tbody>
            {#each tokens as t}
                <tr>
                    <td><strong class="code">{t.code}</strong></td>
                    <td>{t.points}</td>
                    <td>
                        {#if t.isRedeemed}
                            <span class="badge badge-finished">Использован</span>
                        {:else}
                            <span class="badge badge-active">Доступен</span>
                        {/if}
                    </td>
                    <td>{t.redeemedAt ? new Date(t.redeemedAt).toLocaleTimeString() : '—'}</td>
                </tr>
            {/each}
            </tbody>
        </table>
    </div>
</div>

<style>
    .card { background: var(--bg, #0f0f23); border-radius: 8px; padding: 16px; margin: 16px 0; }
    .form-row { display: flex; gap: 16px; flex-wrap: wrap; }
    .form-group { flex: 1; min-width: 150px; }
    .form-group label { display: block; margin-bottom: 6px; color: var(--muted, #aaa); font-size: 14px; }
    .form-group input {
        width: 100%; padding: 10px; border-radius: 6px;
        border: 1px solid var(--border, #333); background: var(--bg, #0f0f23); color: #fff;
    }
    .table-wrap { overflow-x: auto; -webkit-overflow-scrolling: touch; margin-top: 12px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border, #333); white-space: nowrap; }
    th { color: var(--accent, #f5a623); }
    .code { font-size: 18px; letter-spacing: 2px; }
    .btn { padding: 8px 14px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; }
    .btn-primary { background: var(--blue, #3498db); color: #fff; }
    .btn-success { background: var(--green, #27ae60); color: #fff; }
    .badge { padding: 4px 10px; border-radius: 12px; font-size: 12px; font-weight: bold; }
    .badge-active { background: var(--green, #27ae60); color: #fff; }
    .badge-finished { background: #7f8c8d; color: #fff; }

    @media (max-width: 480px) {
        .form-group { min-width: 100%; }
    }
</style>