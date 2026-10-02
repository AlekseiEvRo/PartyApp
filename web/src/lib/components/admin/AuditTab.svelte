<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface AuditEntry {
        id: string;
        adminName: string;
        targetUserId: string | null;
        targetName: string | null;
        action: string;
        details: string | null;
        createdAt: string;
    }

    const actionLabels: Record<string, string> = {
        grant_points: '⭐ Начисление',
        deduct_points: '➖ Списание',
        rename: '✏️ Переименование',
        role_change: '👑 Смена роли',
        ban: '🚫 Блокировка',
        unban: '✅ Разблокировка',
        kick: '👢 Кик',
        password_reset: '🔑 Сброс пароля'
    };

    let items: AuditEntry[] = [];
    let total = 0;
    let loading = true;

    async function load() {
        loading = true;
        try {
            const response = await api<{ items: AuditEntry[]; total: number }>('/api/admin/audit?limit=100');
            items = response.items;
            total = response.total;
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            loading = false;
        }
    }

    onMount(load);
</script>

<h2>Журнал действий</h2>
<button class="btn" on:click={load}>🔄 Обновить</button>

{#if loading}
    <p style="color:#666;">Загрузка...</p>
{:else if items.length === 0}
    <p style="color:#666;">Пока никаких действий не было.</p>
{:else}
    <p class="hint">Последние {items.length} из {total} записей</p>
    <div class="table-wrap">
        <table>
            <thead>
            <tr>
                <th>Время</th>
                <th>Админ</th>
                <th>Действие</th>
                <th>Игрок</th>
                <th>Детали</th>
            </tr>
            </thead>
            <tbody>
            {#each items as entry (entry.id)}
                <tr>
                    <td>{new Date(entry.createdAt).toLocaleString()}</td>
                    <td>{entry.adminName}</td>
                    <td>{actionLabels[entry.action] ?? entry.action}</td>
                    <td>{entry.targetName ?? '—'}</td>
                    <td>{entry.details ?? '—'}</td>
                </tr>
            {/each}
            </tbody>
        </table>
    </div>
{/if}

<style>
    .table-wrap { overflow-x: auto; -webkit-overflow-scrolling: touch; margin-top: 16px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border, #333); white-space: nowrap; }
    th { color: var(--accent, #f5a623); font-weight: 600; }
    tr:hover { background: var(--card-soft, #2a2a5e); }
    .btn {
        padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px;
        margin-top: 12px; background: var(--blue, #3498db); color: #fff;
    }
    .hint { color: var(--muted, #aaa); font-size: 13px; margin-top: 12px; }
</style>
