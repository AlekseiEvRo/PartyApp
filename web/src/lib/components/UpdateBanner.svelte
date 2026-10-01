<script lang="ts">
    import { dismissUpdate, needRefresh, updateApp } from '../pwa';
</script>

{#if $needRefresh}
    <div class="update-banner">
        <span class="text">✨ Доступно обновление приложения</span>

        <div class="actions">
            <button class="update" on:click={updateApp}>Обновить</button>
            <button class="later" on:click={dismissUpdate}>Позже</button>
        </div>
    </div>
{/if}

<style>
    .update-banner {
        position: fixed;
        left: 50%;
        transform: translateX(-50%);
        bottom: calc(16px + env(safe-area-inset-bottom, 0px));
        z-index: 300;
        display: flex;
        align-items: center;
        gap: 12px;
        flex-wrap: wrap;
        justify-content: center;
        background: var(--card, #1a1a3e);
        border: 1px solid rgba(245, 166, 35, 0.5);
        border-radius: 14px;
        padding: 12px 16px;
        box-shadow: 0 12px 32px rgba(0, 0, 0, 0.45);
        max-width: calc(100vw - 24px);
    }

    .text {
        font-size: 14px;
        font-weight: bold;
        white-space: nowrap;
    }

    .actions {
        display: flex;
        gap: 8px;
    }

    button {
        border: none;
        border-radius: 8px;
        padding: 8px 14px;
        font-size: 13px;
        font-weight: bold;
        cursor: pointer;
    }

    .update {
        background: var(--accent, #f5a623);
        color: #12122e;
    }

    .later {
        background: #2a2a5a;
        color: #ddd;
    }

    @media (max-width: 420px) {
        .text { white-space: normal; text-align: center; }
    }
</style>