<script lang="ts">
    import { profileUpdated } from '../stores';
    import { loadAvatarUrl } from '../avatar';

    export let userId = '';
    export let name = '';
    export let size = 32;
    export let version: string | null = null;

    let url: string | null = null;
    let lastKey = '';

    // Реагируем на смену версии и на живое обновление профиля (SignalR)
    $: void reload(userId, version, $profileUpdated);

    async function reload(
        id: string,
        ver: string | null,
        update: { userId: string; profileUpdatedAt: string } | null
    ) {
        if (!id) return;

        const effectiveVersion = update && update.userId === id ? update.profileUpdatedAt : ver;
        const key = `${id}:${effectiveVersion ?? ''}`;
        if (key === lastKey) return;
        lastKey = key;

        const loaded = await loadAvatarUrl(id, effectiveVersion);
        if (lastKey === key) url = loaded;
    }

    function initials(): string {
        const parts = name.trim().split(/\s+/).filter(Boolean);
        if (parts.length === 0) return '🙂';
        if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
        return (parts[0][0] + parts[1][0]).toUpperCase();
    }

    function hue(): number {
        let hash = 0;
        for (let i = 0; i < userId.length; i++) {
            hash = (hash * 31 + userId.charCodeAt(i)) | 0;
        }
        return Math.abs(hash) % 360;
    }
</script>

{#if url}
    <img
        class="avatar"
        src={url}
        alt={name}
        style={`width:${size}px;height:${size}px`}
    />
{:else}
    <span
        class="avatar fallback"
        style={`width:${size}px;height:${size}px;font-size:${Math.max(10, Math.round(size * 0.38))}px;background:hsl(${hue()} 45% 38%)`}
    >{initials()}</span>
{/if}

<style>
    .avatar {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        border-radius: 50%;
        object-fit: cover;
        flex-shrink: 0;
        vertical-align: middle;
        background: var(--bg-soft, #12122e);
    }

    .fallback {
        color: #fff;
        font-weight: bold;
        user-select: none;
    }
</style>
