<script lang="ts">
    import { onMount } from 'svelte';

    let deferredPrompt: any = null;
    let showInstallButton = false;
    let isIOS = false;
    let isStandalone = false;
    let showIOSInstructions = false;

    onMount(() => {
        // Проверяем, уже ли запущено как PWA
        isStandalone = window.matchMedia('(display-mode: standalone)').matches
            || (window.navigator as any).standalone === true;

        if (isStandalone) return; // Уже установлено

        // Определяем iOS
        isIOS = /iPad|iPhone|iPod/.test(navigator.userAgent)
            || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);

        // Слушаем событие beforeinstallprompt (Android/Chrome)
        window.addEventListener('beforeinstallprompt', (e) => {
            e.preventDefault();
            deferredPrompt = e;
            showInstallButton = true;
        });
    });

    async function install() {
        if (!deferredPrompt) return;
        deferredPrompt.prompt();
        const { outcome } = await deferredPrompt.userChoice;
        if (outcome === 'accepted') {
            showInstallButton = false;
        }
        deferredPrompt = null;
    }

    function showIOSHelp() {
        showIOSInstructions = true;
    }
</script>

{#if !isStandalone}
    {#if showInstallButton}
        <button class="install-btn" on:click={install}>
            📲 Установить приложение
        </button>
    {:else if isIOS}
        <button class="install-btn" on:click={showIOSHelp}>
            📲 Как установить на iPhone
        </button>

        {#if showIOSInstructions}
            <div class="ios-modal">
                <div class="ios-content">
                    <h3>Установка на iPhone</h3>
                    <ol>
                        <li>Нажми кнопку <strong>«Поделиться»</strong> <span class="share-icon">⬆️</span> в Safari</li>
                        <li>Выбери <strong>«На экран "Домой"»</strong></li>
                        <li>Нажми <strong>«Добавить»</strong></li>
                    </ol>
                    <p class="hint">После этого уведомления будут работать как в обычном приложении</p>
                    <button class="close-btn" on:click={() => showIOSInstructions = false}>Понятно</button>
                </div>
            </div>
        {/if}
    {/if}
{/if}

<style>
    .install-btn {
        position: fixed;
        bottom: 80px;
        bottom: calc(80px + env(safe-area-inset-bottom, 0px));
        left: 50%;
        transform: translateX(-50%);
        background: #27ae60;
        color: #fff;
        border: none;
        padding: 12px 24px;
        border-radius: 24px;
        font-size: 15px;
        font-weight: bold;
        cursor: pointer;
        box-shadow: 0 4px 12px rgba(0,0,0,0.3);
        z-index: 50;
    }

    .ios-modal {
        position: fixed;
        inset: 0;
        background: rgba(0,0,0,0.7);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 100;
        padding: 20px;
    }

    .ios-content {
        background: #1a1a3e;
        border-radius: 16px;
        padding: 24px;
        max-width: 340px;
        width: 100%;
    }

    .ios-content h3 {
        color: #f5a623;
        margin-bottom: 16px;
    }

    .ios-content ol {
        padding-left: 20px;
        line-height: 1.8;
    }

    .share-icon {
        font-size: 18px;
    }

    .hint {
        color: #aaa;
        font-size: 13px;
        margin-top: 12px;
    }

    .close-btn {
        width: 100%;
        margin-top: 16px;
        padding: 12px;
        background: #3498db;
        color: #fff;
        border: none;
        border-radius: 8px;
        cursor: pointer;
    }
</style>