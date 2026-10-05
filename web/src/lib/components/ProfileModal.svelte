<script lang="ts">
    import { createEventDispatcher } from 'svelte';
    import { api } from '../api';
    import { achievementsVersion, showToast } from '../stores';
    import { deleteAvatar, uploadAvatar } from '../avatar';
    import Avatar from './Avatar.svelte';

    export let open = false;

    interface Profile {
        userId: string;
        username: string;
        displayName: string;
        statusEmoji: string | null;
        hasAvatar: boolean;
        profileUpdatedAt: string | null;
        maxAvatarBytes: number;
    }

    interface Achievement {
        code: string;
        title: string;
        icon: string;
        description: string;
        points: number;
        awardedAt: string | null;
    }

    const dispatch = createEventDispatcher();

    const emojis = [
        '😀', '😎', '🥳', '🤩', '😈', '🤠', '🥰', '🤪',
        '😇', '🤖', '👻', '🎃', '🎂', '🔥', '⭐', '💃',
        '🕺', '🍻', '🎸', '🐱', '🐶', '🦄', '🍕', '❤️'
    ];

    let profile: Profile | null = null;
    let achievements: Achievement[] = [];
    let awardedCount = 0;
    let lastAchievementsVersion = -1;
    let loading = false;
    let busy = false;
    let fileInput: HTMLInputElement;

    $: if (open) void load();

    // Пока модалка открыта, новые достижения подтягиваются сразу
    $: if (open && $achievementsVersion !== lastAchievementsVersion) {
        lastAchievementsVersion = $achievementsVersion;
        void loadAchievements();
    }

    async function load() {
        loading = true;
        try {
            profile = await api<Profile>('/api/profile');
            await loadAchievements();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            loading = false;
        }
    }

    async function loadAchievements() {
        try {
            const response = await api<{ items: Achievement[]; awardedCount: number }>('/api/achievements');
            achievements = response.items;
            awardedCount = response.awardedCount;
        } catch {
            // Достижения — необязательная часть профиля, ошибку не показываем
        }
    }

    async function selectEmoji(emoji: string) {
        if (!profile || busy) return;

        const next = profile.statusEmoji === emoji ? null : emoji;
        busy = true;
        try {
            await api('/api/profile', 'PATCH', { statusEmoji: next });
            profile = { ...profile, statusEmoji: next };
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    async function onFileChange(event: Event) {
        const input = event.target as HTMLInputElement;
        const file = input.files?.[0];
        if (!file || !profile) return;

        busy = true;
        try {
            await uploadAvatar(file);
            await load();
            showToast('📸 Аватар обновлён');
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
            input.value = '';
        }
    }

    async function removeAvatar() {
        if (!profile || busy) return;
        if (!confirm('Удалить аватар?')) return;

        busy = true;
        try {
            await deleteAvatar();
            await load();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            busy = false;
        }
    }

    function close() {
        dispatch('close');
    }

    function onBackdropClick(event: MouseEvent) {
        if (event.target === event.currentTarget) {
            close();
        }
    }
</script>

{#if open}
    <div class="overlay" on:click={onBackdropClick} role="presentation">
        <div class="modal">
            <div class="modal-header">
                <h3>👤 Профиль</h3>
                <button class="close" on:click={close} aria-label="Закрыть">✕</button>
            </div>

            {#if loading || !profile}
                <p class="hint">Загружаем профиль…</p>
            {:else}
                <div class="avatar-row">
                    <Avatar
                        userId={profile.userId}
                        name={profile.displayName}
                        version={profile.profileUpdatedAt}
                        size={84}
                    />
                    <div class="avatar-actions">
                        <button class="btn" disabled={busy} on:click={() => fileInput.click()}>
                            {profile.hasAvatar ? '📸 Заменить фото' : '📸 Загрузить фото'}
                        </button>
                        {#if profile.hasAvatar}
                            <button class="btn danger" disabled={busy} on:click={removeAvatar}>🗑 Удалить</button>
                        {/if}
                        <p class="hint">
                            JPG, PNG или WebP до {Math.max(1, Math.round(profile.maxAvatarBytes / (1024 * 1024)))} МБ.
                            Фото обрежется в квадрат.
                        </p>
                    </div>
                    <input class="file-input" type="file" accept="image/*"
                           bind:this={fileInput} on:change={onFileChange} />
                </div>

                <div class="section">
                    <p class="group-label">Статус рядом с именем</p>
                    <div class="emoji-grid">
                        {#each emojis as emoji}
                            <button
                                class="emoji"
                                class:active={profile.statusEmoji === emoji}
                                disabled={busy}
                                on:click={() => selectEmoji(emoji)}
                            >{emoji}</button>
                        {/each}
                    </div>
                    <p class="hint">
                        {profile.statusEmoji
                            ? `Сейчас: ${profile.statusEmoji} — нажми ещё раз, чтобы убрать`
                            : 'Выбери эмодзи — он появится в лидерборде и на экране'}
                    </p>
                </div>

                <div class="section identity">
                    <strong>{profile.displayName}</strong>
                    <span class="hint">@{profile.username}</span>
                </div>

                <div class="section">
                    <p class="group-label">🏅 Достижения: {awardedCount} из {achievements.length}</p>
                    <ul class="achievements">
                        {#each achievements as achievement (achievement.code)}
                            <li class:earned={achievement.awardedAt}>
                                <span class="ach-icon">{achievement.icon}</span>
                                <span class="ach-body">
                                    <strong>{achievement.title}</strong>
                                    <span class="hint">{achievement.description} · +{achievement.points}</span>
                                </span>
                                {#if achievement.awardedAt}
                                    <span class="ach-check" title="Получено">✓</span>
                                {/if}
                            </li>
                        {/each}
                    </ul>
                </div>
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
        /* Опускаем модалку ниже: не залезаем под «челку»/вырез iPhone */
        padding-top: calc(32px + env(safe-area-inset-top, 0px));
        padding-bottom: calc(20px + env(safe-area-inset-bottom, 0px));
    }

    .modal {
        background: var(--card, #1a1a3e);
        border-radius: 16px;
        padding: 20px 24px;
        width: 100%;
        max-width: 440px;
        max-height: 85vh;
        max-height: calc(100dvh - 52px - env(safe-area-inset-top, 0px) - env(safe-area-inset-bottom, 0px));
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

    .modal-header h3 { color: var(--accent, #f5a623); font-size: 17px; }

    .close {
        background: none;
        border: none;
        color: #aaa;
        font-size: 18px;
        cursor: pointer;
        padding: 4px 8px;
    }

    .avatar-row {
        display: flex;
        gap: 16px;
        align-items: flex-start;
        margin-bottom: 20px;
    }

    .avatar-actions { display: flex; flex-direction: column; gap: 8px; align-items: flex-start; }

    .file-input { display: none; }

    .btn {
        padding: 8px 14px;
        border: none;
        border-radius: 8px;
        background: var(--blue, #3498db);
        color: #fff;
        font-size: 13px;
        font-weight: bold;
        cursor: pointer;
    }

    .btn.danger { background: var(--red, #e74c3c); }
    .btn:disabled { opacity: 0.5; cursor: default; }

    .section { margin-bottom: 18px; }

    .group-label {
        color: var(--muted, #aaa);
        font-size: 13px;
        margin-bottom: 8px;
    }

    .emoji-grid {
        display: grid;
        grid-template-columns: repeat(8, 1fr);
        gap: 6px;
    }

    .emoji {
        font-size: 22px;
        line-height: 1;
        padding: 6px 0;
        border: 1px solid transparent;
        border-radius: 8px;
        background: var(--bg-soft, #12122e);
        cursor: pointer;
    }

    .emoji.active { border-color: var(--accent, #f5a623); background: rgba(245, 166, 35, 0.18); }
    .emoji:disabled { opacity: 0.5; cursor: default; }

    .identity { color: #fff; }

    .achievements {
        list-style: none;
        padding: 0;
        margin: 0;
        display: flex;
        flex-direction: column;
        gap: 6px;
    }

    .achievements li {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 8px 10px;
        border-radius: 10px;
        background: var(--bg-soft, #12122e);
        opacity: 0.55;
    }

    .achievements li.earned {
        opacity: 1;
        border: 1px solid rgba(245, 166, 35, 0.5);
    }

    .ach-icon { font-size: 22px; line-height: 1; }

    .ach-body { display: flex; flex-direction: column; gap: 2px; flex: 1; min-width: 0; }
    .ach-body strong { font-size: 14px; }
    .ach-body .hint { font-size: 12px; }

    .ach-check { color: var(--green, #27ae60); font-weight: bold; }

    .hint {
        color: var(--muted, #aaa);
        font-size: 13px;
        line-height: 1.4;
    }

    @media (max-width: 420px) {
        .emoji-grid { grid-template-columns: repeat(6, 1fr); }
        .avatar-row { flex-direction: column; align-items: center; }
        .avatar-actions { align-items: center; }
    }
</style>
