<script lang="ts">
    import { user, toast } from './lib/stores';
    import Login from './lib/components/Login.svelte';
    import Party from './lib/components/Party.svelte';
    import { setToken } from './lib/api';
    import { onMount } from 'svelte';

    let loading = true;

    function isTokenExpired(token: string): boolean {
        try {
            const payload = JSON.parse(atob(token.split('.')[1]));
            const exp = payload.exp * 1000; // exp в секундах → мс
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
            }

            if (attempt < maxRetries - 1) {
                await new Promise((r) => setTimeout(r, 1000 * (attempt + 1)));
            }
        }
        return false;
    }

    onMount(async () => {
        const restored = await tryRestoreSession();
        loading = false;

        if (!restored && localStorage.getItem('party_token')) {
            toast.set({ message: '⚠️ Проблемы с сетью. Попробуй обновить.', type: 'info' });
            setTimeout(() => toast.set(null), 4000);
        }
    });
</script>

{#if loading}
    <div class="loading">Загрузка...</div>
{:else if $user}
    <Party />
{:else}
    <Login />
{/if}

{#if $toast}
    <div class="toast toast-{$toast.type}">{$toast.message}</div>
{/if}