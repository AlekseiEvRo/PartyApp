<script lang="ts">
    import { user, toast } from './lib/stores';
    import Login from './lib/components/Login.svelte';
    import Party from './lib/components/Party.svelte';
    import { setToken } from './lib/api';
    import { onMount } from 'svelte';

    onMount(async () => {
        // Если токен есть, попробуем восстановить сессию
        const token = localStorage.getItem('party_token');
        if (token) {
            try {
                const res = await fetch(`${import.meta.env.VITE_API_URL}/api/auth/me`, {
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
                } else {
                    setToken(null);
                }
            } catch {
                setToken(null);
            }
        }
    });
</script>

{#if $user}
    <Party />
{:else}
    <Login />
{/if}

{#if $toast}
    <div class="toast toast-{$toast.type}">{$toast.message}</div>
{/if}