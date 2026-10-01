<script lang="ts">
    import { user, toast } from './lib/stores';
    import Login from './lib/components/Login.svelte';
    import Party from './lib/components/Party.svelte';
    import AdminPanel from './lib/components/admin/AdminPanel.svelte';
    import Screen from './lib/components/Screen.svelte';
    import UpdateBanner from './lib/components/UpdateBanner.svelte';
    import { setToken } from './lib/api';
    import { onMount } from 'svelte';

    let loading = true;
    let isAdminRoute = window.location.pathname.startsWith('/admin');
    let isScreenRoute = window.location.pathname.startsWith('/screen');

    function isTokenExpired(token: string): boolean {
        try {
            const payload = JSON.parse(atob(token.split('.')[1]));
            const exp = payload.exp * 1000;
            return Date.now() > exp;
        } catch {
            return true;
        }
    }

    async function tryRestoreSession(): Promise<boolean> {
        const token = localStorage.getItem('party_token');
        if (!token) return false;

        if (isTokenExpired(token)) {
            setToken(null);
            return false;
        }

        const maxRetries = 3;
        for (let attempt = 0; attempt < maxRetries; attempt++) {
            try {
                const res = await fetch('/api/auth/me', {
                    headers: { Authorization: `Bearer ${token}` }
                });

                if (res.ok) {
                    const data = await res.json();
                    user.set({
                        userId: data.userId,
                        username: data.username,
                        displayName: data.displayName,
                        role: data.role
                    });
                    return true;
                }

                if (res.status === 401) {
                    setToken(null);
                    return false;
                }
            } catch (e) {
                // Сетевая ошибка — пробуем снова
            }

            if (attempt < maxRetries - 1) {
                await new Promise((r) => setTimeout(r, 1000 * (attempt + 1)));
            }
        }

        return false;
    }

    onMount(async () => {
        await tryRestoreSession();
        loading = false;
    });
</script>

{#if loading}
    <div class="loading">Загрузка...</div>
{:else if isAdminRoute}
    {#if $user && $user.role === 'Admin'}
        <AdminPanel />
    {:else}
        <div class="admin-login">
            <div class="admin-login-card">
                <h2>🔐 Вход в админку</h2>
                <p>Требуется роль администратора</p>
                <Login embedded />
            </div>
        </div>
    {/if}
{:else if isScreenRoute}
    {#if $user && $user.role === 'Admin'}
        <Screen />
    {:else}
        <div class="admin-login">
            <div class="admin-login-card">
                <h2>🖥 Вход для экрана</h2>
                <p>Экран доступен администратору</p>
                <Login embedded />
            </div>
        </div>
    {/if}
{:else if $user}
    <Party />
{:else}
    <Login />
{/if}

{#if $toast}
    <div class="toast toast-{$toast.type}">{$toast.message}</div>
{/if}

<UpdateBanner />

<style>
    .loading {
        display: flex;
        align-items: center;
        justify-content: center;
        min-height: 100vh;
        min-height: 100dvh;
        color: #aaa;
        font-size: 18px;
    }
    .admin-login {
        display: flex;
        min-height: 100vh;
        min-height: 100dvh;
        padding: 20px;
        padding-top: calc(20px + env(safe-area-inset-top, 0px));
        padding-bottom: calc(20px + env(safe-area-inset-bottom, 0px));
        padding-left: calc(20px + env(safe-area-inset-left, 0px));
        padding-right: calc(20px + env(safe-area-inset-right, 0px));
    }
    .admin-login-card {
        background: var(--card, #1a1a3e);
        padding: 32px;
        border-radius: 16px;
        width: 100%;
        max-width: 420px;
        text-align: center;
        /* margin:auto центрирует и не обрезает верх, если карточка выше экрана */
        margin: auto;
    }
    .admin-login-card h2 { color: var(--accent, #f5a623); margin-bottom: 8px; }
    .admin-login-card p { color: var(--muted, #aaa); margin-bottom: 20px; }

    @media (max-width: 480px) {
        .admin-login-card { padding: 24px 18px; }
    }
</style>