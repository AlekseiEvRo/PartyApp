<script lang="ts">
    import { createEventDispatcher, onMount } from 'svelte';
    import {
        disablePush,
        enablePush,
        getExistingSubscription,
        getPermission,
        getPushSupport,
        sendTestPush
    } from '../push';

    export let open = false;

    const dispatch = createEventDispatcher();
    const support = getPushSupport();

    let permission: NotificationPermission | 'unsupported' = 'default';
    let hasSubscription = false;
    let checking = true;
    let busy = false;
    let message = '';
    let messageType: 'info' | 'success' | 'error' = 'info';

    onMount(() => {
        permission = getPermission();
    });

    $: if (open) {
        void refresh();
    }

    async function refresh() {
        permission = getPermission();
        checking = true;
        hasSubscription = (await getExistingSubscription()) !== null;
        checking = false;
    }

    async function enable() {
        busy = true;
        message = '';

        const result = await enablePush();

        busy = false;

        if (result.ok) {
            messageType = 'success';
            message = 'Уведомления включены!';
        } else {
            messageType = 'error';
            message = result.error ?? 'Не удалось включить уведомления';
        }

        await refresh();
    }

    async function disable() {
        busy = true;
        message = '';

        const result = await disablePush();

        busy = false;

        if (result.ok) {
            messageType = 'info';
            message = 'Уведомления отключены';
        } else {
            messageType = 'error';
            message = result.error ?? 'Не удалось отключить уведомления';
        }

        await refresh();
    }

    async function test() {
        busy = true;
        message = '';

        try {
            const report = await sendTestPush();

            if (report.sent > 0) {
                messageType = 'success';
                message = 'Отправлено! Сверни приложение — уведомление должно появиться.';
            } else if (report.removed > 0) {
                messageType = 'error';
                message = 'Подписка устарела и была удалена. Включи уведомления заново.';
            } else {
                messageType = 'error';
                message = 'Не получилось отправить. Попробуй выключить и включить уведомления.';
            }
        } catch (e) {
            messageType = 'error';
            message = e instanceof Error ? e.message : 'Ошибка отправки';
        }

        busy = false;
    }

    function close() {
        dispatch('close');
    }

    function onBackdropClick(event: MouseEvent) {
        if (event.target === event.currentTarget) {
            close();
        }
    }

    function permissionLabel(): string {
        switch (permission) {
            case 'granted':
                return 'включены';
            case 'denied':
                return 'запрещены';
            case 'unsupported':
                return 'не поддерживаются';
            default:
                return 'не запрашивались';
        }
    }

    function supportDescription(): string {
        if (support.reason === 'ios-not-installed') {
            return 'На iPhone уведомления работают только у приложения, добавленного на экран «Домой».';
        }

        if (support.reason === 'no-push-manager' && support.ios) {
            return 'Этот iPhone не поддерживает web-push. Нужен iOS 16.4 или новее.';
        }

        if (support.reason === 'insecure') {
            return 'Для уведомлений нужен HTTPS.';
        }

        return 'Этот браузер не поддерживает push-уведомления.';
    }
</script>

{#if open}
    <div class="overlay" on:click={onBackdropClick} role="presentation">
        <div class="modal">
            <div class="modal-header">
                <h3>🔔 Настройки уведомлений</h3>
                <button class="close" on:click={close} aria-label="Закрыть">✕</button>
            </div>

            {#if !support.supported}
                <div class="status">
                    <p class="unavailable">{supportDescription()}</p>

                    {#if support.reason === 'ios-not-installed'}
                        <ol class="steps">
                            <li>Нажми кнопку <strong>«Поделиться»</strong> ⬆️ в Safari</li>
                            <li>Выбери <strong>«На экран "Домой"»</strong></li>
                            <li>Запусти приложение с иконки и вернись сюда</li>
                        </ol>
                    {/if}
                </div>
            {:else}
                <div class="status">
                    <div class="row">
                        <span>Уведомления в системе</span>
                        <span class="value" class:ok={permission === 'granted'} class:bad={permission === 'denied'}>
                            {permissionLabel()}
                        </span>
                    </div>
                    <div class="row">
                        <span>Подписка на этом устройстве</span>
                        <span class="value" class:ok={hasSubscription}>
                            {checking ? 'проверяем…' : (hasSubscription ? 'активна' : 'нет')}
                        </span>
                    </div>
                </div>

                {#if permission === 'denied'}
                    <p class="hint">
                        Разрешение запрещено. На iPhone: <strong>Настройки → Уведомления → PartyApp → Разрешить уведомления</strong>.
                        На Android: настройки браузера → уведомления для этого сайта.
                    </p>
                {/if}

                <div class="actions">
                    {#if permission !== 'granted' || !hasSubscription}
                        <button class="primary" on:click={enable} disabled={busy || checking}>
                            {busy ? 'Включаем…' : 'Включить уведомления'}
                        </button>
                    {:else}
                        <button class="primary" on:click={test} disabled={busy}>
                            {busy ? 'Отправляем…' : 'Отправить тестовое уведомление'}
                        </button>
                        <button class="secondary" on:click={disable} disabled={busy}>
                            Отключить уведомления
                        </button>
                    {/if}
                </div>

                <p class="hint">
                    Уведомления приходят о новых ивентах, тостах, сообщениях ведущего и начислении баллов —
                    даже когда приложение закрыто.
                </p>
            {/if}

            {#if message}
                <div class="message {messageType}">{message}</div>
            {/if}
        </div>
    </div>
{/if}

<style>
    .overlay {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.7);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 100;
        padding: 20px;
        padding-bottom: calc(20px + env(safe-area-inset-bottom, 0px));
    }

    .modal {
        background: var(--card, #1a1a3e);
        border-radius: 16px;
        padding: 20px 24px;
        width: 100%;
        max-width: 380px;
        max-height: 85vh;
        max-height: calc(100dvh - 40px);
        overflow-y: auto;
        overscroll-behavior: contain;
    }

    .modal-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        margin-bottom: 16px;
    }

    .modal-header h3 {
        color: #f5a623;
        font-size: 17px;
    }

    .close {
        background: none;
        border: none;
        color: #aaa;
        font-size: 18px;
        cursor: pointer;
        padding: 4px 8px;
    }

    .status {
        display: flex;
        flex-direction: column;
        gap: 10px;
        background: var(--bg-soft, #12122e);
        border-radius: 10px;
        padding: 12px 14px;
    }

    .row {
        display: flex;
        justify-content: space-between;
        gap: 10px;
        font-size: 14px;
        color: #ccc;
    }

    .value {
        font-weight: bold;
        color: #aaa;
        white-space: nowrap;
    }

    .value.ok { color: #27ae60; }
    .value.bad { color: #e74c3c; }

    .actions {
        display: flex;
        flex-direction: column;
        gap: 10px;
        margin-top: 16px;
    }

    button.primary,
    button.secondary {
        width: 100%;
        padding: 12px;
        border: none;
        border-radius: 10px;
        font-size: 15px;
        font-weight: bold;
        cursor: pointer;
    }

    button.primary {
        background: #27ae60;
        color: #fff;
    }

    button.primary:disabled,
    button.secondary:disabled {
        opacity: 0.6;
        cursor: default;
    }

    button.secondary {
        background: #2a2a5a;
        color: #ddd;
    }

    .steps {
        padding-left: 20px;
        line-height: 1.8;
        font-size: 14px;
    }

    .unavailable {
        font-size: 14px;
        line-height: 1.5;
    }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        line-height: 1.5;
        margin-top: 12px;
        overflow-wrap: anywhere;
    }

    .message {
        margin-top: 14px;
        padding: 10px 12px;
        border-radius: 8px;
        font-size: 14px;
        line-height: 1.4;
        overflow-wrap: anywhere;
    }

    .message.info { background: #23324d; color: #cfe1ff; }
    .message.success { background: #1e3d2a; color: #b7efc5; }
    .message.error { background: #3d1e22; color: #ffc9c9; }
</style>
