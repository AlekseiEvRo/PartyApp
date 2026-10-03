<script lang="ts">
    import { onMount } from 'svelte';
    import { api, getToken } from '../../api';
    import { showToast, user } from '../../stores';
    import BalanceHistory from '../BalanceHistory.svelte';

    interface Player {
        id: string;
        username: string;
        displayName: string;
        role: string;
        isActive: boolean;
        balance: number;
        createdAt: string;
    }

    interface BackupFile {
        fileName: string;
        sizeBytes: number;
        createdAtUtc: string;
    }

    let players: Player[] = [];
    let backups: BackupFile[] = [];
    let loading = true;
    let historyPlayer: Player | null = null;
    let busyId: string | null = null;
    let downloading = false;
    let resetPassword: { player: Player; password: string } | null = null;

    async function loadPlayers() {
        loading = true;
        try {
            players = await api<Player[]>('/api/admin/players');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            loading = false;
        }
    }

    async function loadBackups() {
        try {
            backups = await api<BackupFile[]>('/api/admin/backups');
        } catch {
            backups = [];
        }
    }

    onMount(() => {
        void loadPlayers();
        void loadBackups();
    });

    async function updatePlayer(player: Player, changes: Record<string, unknown>) {
        busyId = player.id;
        try {
            await api(`/api/admin/players/${player.id}`, 'PATCH', changes);
            await loadPlayers();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busyId = null;
        }
    }

    function toggleRole(player: Player) {
        if (player.role === 'SuperAdmin') return;

        const next = player.role === 'Admin' ? 'Player' : 'Admin';
        const label = next === 'Admin' ? 'админа' : 'игрока';
        if (!confirm(`Сделать ${player.displayName} ${label}? Его текущие сессии завершатся.`)) return;
        void updatePlayer(player, { role: next });
    }

    /** Роль супер-админа может выдать только супер-админ; роль после этого не меняется. */
    $: isSuperAdmin = $user?.role === 'SuperAdmin';

    function promoteToSuperAdmin(player: Player) {
        if (!isSuperAdmin || player.role === 'SuperAdmin') return;
        if (!confirm(
            `Сделать ${player.displayName} супер-админом? Роль супер-админа нельзя будет изменить, ` +
            'а бан/кик/сброс пароля будут доступны только другому супер-админу.'
        )) return;
        void updatePlayer(player, { role: 'SuperAdmin' });
    }

    /** Супер-админа трогает только другой супер-админ. */
    function canManage(player: Player): boolean {
        return player.role !== 'SuperAdmin' || isSuperAdmin;
    }

    function roleLabel(role: string): string {
        if (role === 'SuperAdmin') return '⭐ Супер-админ';
        return role === 'Admin' ? '👑 Админ' : '🎮 Игрок';
    }

    function toggleActive(player: Player) {
        const message = player.isActive
            ? `Заблокировать ${player.displayName}? Он не сможет войти и участвовать.`
            : `Разблокировать ${player.displayName}?`;
        if (!confirm(message)) return;
        void updatePlayer(player, { isActive: !player.isActive });
    }

    async function kick(player: Player) {
        if (!confirm(`Кикнуть ${player.displayName}? Ему придётся войти заново.`)) return;
        busyId = player.id;
        try {
            await api(`/api/admin/players/${player.id}/kick`, 'POST');
            showToast(`👢 ${player.displayName} разлогинен`, 'info');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busyId = null;
        }
    }

    async function resetPlayerPassword(player: Player) {
        if (!confirm(`Сбросить пароль ${player.displayName}? Старый перестанет работать.`)) return;
        busyId = player.id;
        try {
            const response = await api<{ password: string }>(
                `/api/admin/players/${player.id}/reset-password`,
                'POST'
            );
            resetPassword = { player, password: response.password };
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busyId = null;
        }
    }

    async function downloadBackup() {
        downloading = true;
        try {
            const response = await fetch('/api/admin/backup', {
                headers: { Authorization: `Bearer ${getToken()}` }
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);

            const blob = await response.blob();
            const disposition = response.headers.get('Content-Disposition') ?? '';
            const match = disposition.match(/filename="?([^";]+)"?/i);
            const fileName = match?.[1] ?? 'party-backup.zip';

            const url = URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.download = fileName;
            link.click();
            URL.revokeObjectURL(url);

            await loadBackups();
            showToast('💾 Бэкап скачан', 'info');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            downloading = false;
        }
    }

    function formatSize(bytes: number): string {
        if (bytes < 1024) return `${bytes} Б`;
        if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} КБ`;
        return `${(bytes / 1024 / 1024).toFixed(1)} МБ`;
    }
</script>

<h2>Участники</h2>
<button class="btn btn-primary" on:click={loadPlayers}>🔄 Обновить</button>

{#if loading}
    <p style="color:#666;">Загрузка...</p>
{:else}
    <div class="table-wrap">
        <table>
            <thead>
            <tr>
                <th>Имя</th>
                <th>Логин</th>
                <th>Роль</th>
                <th>Статус</th>
                <th>Баланс</th>
                <th>Зарегистрирован</th>
                <th>Действия</th>
            </tr>
            </thead>
            <tbody>
            {#each players as p}
                <tr class:blocked={!p.isActive}>
                    <td>{p.displayName}</td>
                    <td>{p.username}</td>
                    <td>{roleLabel(p.role)}</td>
                    <td>{p.isActive ? '✅ Активен' : '🚫 Заблокирован'}</td>
                    <td><strong>{p.balance}</strong></td>
                    <td>{new Date(p.createdAt).toLocaleDateString()}</td>
                    <td>
                        <div class="actions">
                            {#if p.role !== 'SuperAdmin'}
                                <button class="btn-small" disabled={busyId === p.id}
                                        title="Сменить роль" on:click={() => toggleRole(p)}>
                                    {p.role === 'Admin' ? '🎮' : '👑'}
                                </button>
                            {/if}
                            {#if isSuperAdmin && p.role !== 'SuperAdmin'}
                                <button class="btn-small" disabled={busyId === p.id}
                                        title="Сделать супер-админом" on:click={() => promoteToSuperAdmin(p)}>⭐</button>
                            {/if}
                            {#if canManage(p)}
                                <button class="btn-small" disabled={busyId === p.id}
                                        title="Кикнуть" on:click={() => kick(p)}>👢</button>
                                <button class="btn-small" disabled={busyId === p.id}
                                        title="Сбросить пароль" on:click={() => resetPlayerPassword(p)}>🔑</button>
                                <button class="btn-small" disabled={busyId === p.id}
                                        title={p.isActive ? 'Заблокировать' : 'Разблокировать'}
                                        on:click={() => toggleActive(p)}>
                                    {p.isActive ? '🚫' : '✅'}
                                </button>
                            {/if}
                            <button class="btn-history" on:click={() => (historyPlayer = p)}>📜</button>
                        </div>
                    </td>
                </tr>
            {/each}
            </tbody>
        </table>
    </div>
{/if}

{#if resetPassword}
    <div class="password-box">
        <p>
            Новый пароль для <strong>{resetPassword.player.displayName}</strong>:
            <code>{resetPassword.password}</code>
        </p>
        <p class="hint">Передай его игроку — больше он не покажется. Старые сессии уже завершены.</p>
        <button class="btn btn-primary" on:click={() => (resetPassword = null)}>Понятно</button>
    </div>
{/if}

<h2 class="backup-title">Резервные копии</h2>
<div class="backup-actions">
    <button class="btn btn-primary" disabled={downloading} on:click={downloadBackup}>
        {downloading ? '⏳ Готовим...' : '💾 Скачать бэкап'}
    </button>
    <span class="hint">База, фото и ключи в одном zip. Автобэкап — раз в сутки, хранится 5 последних.</span>
</div>

{#if backups.length > 0}
    <ul class="backup-list">
        {#each backups as backup}
            <li>
                <span>{backup.fileName}</span>
                <span class="hint">
                    {formatSize(backup.sizeBytes)} · {new Date(backup.createdAtUtc).toLocaleString()}
                </span>
            </li>
        {/each}
    </ul>
{/if}

{#if historyPlayer}
    <BalanceHistory
        open={true}
        url={`/api/admin/players/${historyPlayer.id}/transactions`}
        title={`⭐ ${historyPlayer.displayName}: баллы`}
        emptyText="У игрока пока нет транзакций."
        on:close={() => (historyPlayer = null)}
    />
{/if}

<style>
    .table-wrap { overflow-x: auto; -webkit-overflow-scrolling: touch; margin-top: 16px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border, #333); white-space: nowrap; }
    th { color: var(--accent, #f5a623); font-weight: 600; }
    tr:hover { background: var(--card-soft, #2a2a5e); }
    tr.blocked { opacity: 0.55; }
    .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; margin-top: 12px; }
    .btn-primary { background: var(--blue, #3498db); color: #fff; }
    .btn-primary:disabled { opacity: 0.6; cursor: default; }
    .btn-history {
        padding: 6px 10px; border: 1px solid var(--border, #333); border-radius: 6px;
        background: none; color: var(--accent, #f5a623); font-size: 13px; cursor: pointer;
    }
    .btn-history:hover { background: var(--card-soft, #2a2a5e); }
    .actions { display: flex; gap: 6px; flex-wrap: wrap; justify-content: flex-end; }
    .btn-small {
        padding: 6px 8px; border: 1px solid var(--border, #333); border-radius: 6px;
        background: none; color: var(--text, #eee); font-size: 13px; cursor: pointer;
    }
    .btn-small:hover:not(:disabled) { background: var(--card-soft, #2a2a5e); }
    .btn-small:disabled { opacity: 0.4; cursor: default; }
    .password-box {
        margin-top: 16px; padding: 16px; border-radius: 8px;
        background: var(--card-soft, #2a2a5e); border: 1px solid var(--accent, #f5a623);
    }
    .password-box code {
        display: inline-block; margin-left: 6px; padding: 4px 10px; border-radius: 6px;
        background: var(--bg, #0f0f23); color: var(--accent, #f5a623);
        font-size: 18px; letter-spacing: 1px;
    }
    .hint { color: var(--muted, #aaa); font-size: 13px; }
    .backup-title { margin-top: 32px; }
    .backup-actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .backup-list { list-style: none; padding: 0; margin: 12px 0 0; }
    .backup-list li {
        display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap;
        padding: 8px 12px; border-bottom: 1px solid var(--border, #333); font-size: 14px;
    }
</style>
