<script lang="ts">
    import { api } from '../api';
    import { showToast } from '../stores';

    const stickers = ['🎉', '❤️', '🔥', '😂', '👏', '🥳', '💃', '🍾', '😱', '🎈', '⭐', '🤯'];
    const maxTextLength = 80;

    let open = false;
    let selected = stickers[0];
    let text = '';
    let busy = false;

    function onBackdrop(event: MouseEvent) {
        if (event.target === event.currentTarget) {
            open = false;
        }
    }

    async function tapSticker(sticker: string) {
        selected = sticker;
        await send({ emoji: sticker });
    }

    async function sendText() {
        const value = text.trim();
        if (!value) return;

        const ok = await send({ emoji: selected, text: value });
        if (ok) text = '';
    }

    async function sendConfetti() {
        busy = true;

        try {
            await api('/api/screen/confetti', 'POST');
            showToast('🎉 Конфетти полетело!');
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не получилось запустить конфетти', 'error');
        } finally {
            busy = false;
        }
    }

    async function send(payload: { emoji?: string; text?: string }): Promise<boolean> {
        busy = true;

        try {
            await api('/api/screen/reactions', 'POST', payload);
            showToast('✨ Улетело на экран!');
            return true;
        } catch (e) {
            showToast(e instanceof Error ? e.message : 'Не получилось отправить', 'error');
            return false;
        } finally {
            busy = false;
        }
    }
</script>

<button class="fab" on:click={() => (open = true)} title="Реакции на экран" aria-label="Реакции на экран">
    ✨
</button>

{#if open}
    <div class="overlay" on:click={onBackdrop} role="presentation">
        <div class="sheet">
            <div class="sheet-header">
                <h3>✨ На большой экран</h3>
                <button class="close" on:click={() => (open = false)} aria-label="Закрыть">✕</button>
            </div>

            <p class="hint">Стикеры и подписи всплывают на экране у всех — жми!</p>

            <div class="stickers">
                {#each stickers as sticker}
                    <button
                        class="sticker"
                        class:selected={selected === sticker}
                        disabled={busy}
                        on:click={() => tapSticker(sticker)}
                    >{sticker}</button>
                {/each}
            </div>

            <div class="composer">
                <span class="selected-sticker" title="Стикер, который полетит с подписью">{selected}</span>
                <input
                    type="text"
                    bind:value={text}
                    maxlength={maxTextLength}
                    placeholder="Подпись к стикеру…"
                    on:keydown={(e) => e.key === 'Enter' && sendText()}
                />
                <button class="send" on:click={sendText} disabled={busy || text.trim().length === 0}>
                    Отправить
                </button>
            </div>

            <div class="row">
                <button class="confetti-btn" on:click={sendConfetti} disabled={busy}>🎉 Запустить конфетти</button>
            </div>
        </div>
    </div>
{/if}

<style>
    .fab {
        position: fixed;
        right: 16px;
        bottom: calc(16px + env(safe-area-inset-bottom, 0px));
        width: 56px;
        height: 56px;
        border-radius: 50%;
        border: none;
        background: linear-gradient(135deg, #f5a623, #e74c3c);
        color: #fff;
        font-size: 24px;
        cursor: pointer;
        box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
        z-index: 90;
    }

    .fab:active { transform: scale(0.95); }

    .overlay {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.7);
        display: flex;
        align-items: flex-end;
        justify-content: center;
        z-index: 100;
        padding: 20px;
        padding-bottom: calc(20px + env(safe-area-inset-bottom, 0px));
    }

    .sheet {
        background: var(--card, #1a1a3e);
        border-radius: 16px;
        padding: 18px 20px;
        width: 100%;
        max-width: 460px;
        max-height: 85vh;
        max-height: calc(100dvh - 40px);
        overflow-y: auto;
    }

    .sheet-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        margin-bottom: 8px;
    }

    .sheet-header h3 { color: #f5a623; font-size: 17px; }

    .close {
        background: none;
        border: none;
        color: #aaa;
        font-size: 18px;
        cursor: pointer;
        padding: 4px 8px;
    }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        margin-bottom: 12px;
    }

    .stickers {
        display: grid;
        grid-template-columns: repeat(6, 1fr);
        gap: 8px;
        margin-bottom: 14px;
    }

    .sticker {
        aspect-ratio: 1;
        border: 2px solid transparent;
        border-radius: 12px;
        background: var(--bg-soft, #12122e);
        font-size: 26px;
        cursor: pointer;
        transition: transform 0.15s, border-color 0.15s;
    }

    .sticker:active { transform: scale(0.92); }
    .sticker.selected { border-color: #f5a623; }
    .sticker:disabled { opacity: 0.5; }

    .composer {
        display: flex;
        align-items: center;
        gap: 8px;
        margin-bottom: 12px;
    }

    .selected-sticker {
        width: 46px;
        height: 46px;
        flex-shrink: 0;
        border: 2px solid #f5a623;
        border-radius: 12px;
        background: var(--bg-soft, #12122e);
        font-size: 24px;
        cursor: default;
    }

    .composer input {
        flex: 1;
        min-width: 0;
        background: var(--bg-soft, #12122e);
        border: 1px solid #2a2a5e;
        border-radius: 10px;
        color: inherit;
        padding: 11px 12px;
        font-size: 14px;
    }

    .send {
        background: #27ae60;
        color: #fff;
        border: none;
        border-radius: 10px;
        padding: 11px 14px;
        font-size: 14px;
        font-weight: bold;
        cursor: pointer;
        white-space: nowrap;
    }

    .send:disabled { opacity: 0.5; cursor: default; }

    .row { display: flex; }

    .confetti-btn {
        flex: 1;
        background: #f5a623;
        color: #12122e;
        border: none;
        border-radius: 10px;
        padding: 12px;
        font-size: 15px;
        font-weight: bold;
        cursor: pointer;
    }

    .confetti-btn:disabled { opacity: 0.5; cursor: default; }

    @media (max-width: 380px) {
        .stickers { grid-template-columns: repeat(4, 1fr); }
    }
</style>
