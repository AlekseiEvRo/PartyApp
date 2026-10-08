<script lang="ts">
    import { api } from '../api';
    import { showToast, balance, spyGameRole } from '../stores';

    let wordInput = '';
    let selectedAccused = '';

    async function submitWord() {
        if (!wordInput.trim()) return;
        try {
            const res = await api<any>('/api/spygame/submit-word', 'POST', { word: wordInput.trim() });
            showToast(res.message || 'Слово отправлено!', 'success');
            wordInput = '';
            refreshBalance();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function accuse() {
        if (!selectedAccused) return;
        try {
            const res = await api<any>('/api/spygame/accuse', 'POST', { accusedPlayerId: selectedAccused });
            showToast(res.message || 'Обвинение отправлено', 'success');
            refreshBalance();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function refreshBalance() {
        try {
            const b = await api<any>('/api/wallet/balance');
            balance.set(b.balance);
        } catch {}
    }
</script>

{#if $spyGameRole}
    <div class="spy-game">
        <h3>🕵 Двойной агент</h3>

        {#if $spyGameRole.role === 'Spy'}
            <!-- ШПИОН -->
            {#if $spyGameRole.isGuesser}
                <!-- УГАДЧИК: видит напарника, вводит слово -->
                <div class="spy-role">
                    <p>Ты — <b>ШПИОН-УГАДЧИК</b> 🕵</p>
                    <p>Твой напарник: <b>{$spyGameRole.partnerName}</b></p>
                    <p class="hint">
                        Напарник будет намекать тебе на секретное слово вживую.
                        Слушай внимательно и введи слово здесь, как только догадаешься!
                    </p>
                    <div class="row">
                        <input type="text" placeholder="Введи секретное слово" bind:value={wordInput} />
                        <button class="btn" on:click={submitWord}>✅ Отправить</button>
                    </div>
                </div>
            {:else}
                <!-- РАССКАЗЧИК: видит слово, намекает -->
                <div class="spy-role">
                    <p>Ты — <b>ШПИОН-РАССКАЗЧИК</b> 🕵</p>
                    <p>Твой напарник: <b>{$spyGameRole.partnerName}</b></p>
                    <p>Ваше секретное слово: <b class="secret">{$spyGameRole.secretWord}</b></p>
                    <p class="hint">
                        Намекни напарнику на это слово вживую.
                        Он должен вписать его в приложение. Горожане подслушивают — не спались!
                    </p>
                    <p class="wait">⏳ Жди, пока напарник угадает слово...</p>
                </div>
            {/if}

        {:else}
            <!-- ГОРОЖАНИН -->
            <div class="town-role">
                <p>Ты — <b>ГОРОЖАНИН</b> 👤</p>
                <p class="hint">
                    Среди игроков есть шпионы. Они пытаются передать друг другу секретное слово.
                    Слушай разговоры и вычисли шпиона. У тебя одна попытка обвинения.
                </p>

                <p>Кого ты подозреваешь?</p>
                <select bind:value={selectedAccused}>
                    <option value="">— выбери игрока —</option>
                    {#each $spyGameRole.allPlayers as p}
                        <option value={p.userId}>{p.displayName}</option>
                    {/each}
                </select>
                <button class="btn btn-danger" on:click={accuse} disabled={!selectedAccused}>
                    👉 Обвинить
                </button>
            </div>
        {/if}
    </div>
{/if}

<style>
    .spy-game { background: var(--card, #1a1a3e); border-radius: 12px; padding: 20px; margin-bottom: 16px; border: 2px solid var(--accent, #f5a623); overflow-wrap: anywhere; }
    .spy-role, .town-role { background: var(--bg, #0f0f23); padding: 16px; border-radius: 8px; }
    .secret { color: var(--accent, #f5a623); font-size: 20px; letter-spacing: 1px; }
    .hint { color: var(--muted, #aaa); font-size: 13px; margin: 10px 0; line-height: 1.5; }
    .wait { color: var(--blue, #3498db); font-style: italic; margin-top: 12px; }
    .row { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
    .row input { flex: 1 1 160px; min-width: 0; padding: 12px; border-radius: 8px; border: 1px solid var(--border, #333); background: var(--card, #1a1a3e); color: #fff; font-size: 16px; }
    .btn { padding: 12px 18px; border: none; border-radius: 8px; background: var(--blue, #3498db); color: #fff; font-size: 15px; cursor: pointer; }
    .btn-danger { background: var(--red, #e74c3c); color: #fff; width: 100%; margin-top: 8px; }
    .btn:disabled { background: #444; cursor: not-allowed; }
    select { width: 100%; padding: 12px; border-radius: 8px; background: var(--card, #1a1a3e); color: #fff; border: 1px solid var(--border, #333); margin: 8px 0; font-size: 15px; }

    @media (max-width: 480px) {
        .spy-game { padding: 16px; }
        .spy-role, .town-role { padding: 12px; }
        .row .btn { flex: 1 1 100%; }
    }
</style>