<script lang="ts">
    import { onMount } from 'svelte';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface EventTypeInfo {
        type: string;
        defaultConfigJson: string;
        requiresCustomStart?: boolean;
    }

    interface EventDefinition {
        id: string;
        type: string;
        displayName: string;
        description?: string | null;
        configJson: string;
        availability: string | number;
        isActive: boolean;
        durationMinutes?: number | null;
        createdAt: string;
        createdById?: string | null;
    }

    interface EventSession {
        id: string;
        definitionId: string;
        definitionName: string;
        type: string;
        state: string;
        startedAt: string;
        endsAt?: string | null;
        endedAt?: string | null;
        startedBy: string;
        submissionCount: number;
    }

    let definitions: EventDefinition[] = [];
    let sessions: EventSession[] = [];
    let types: EventTypeInfo[] = [];

    // Состояние формы создания/редактирования
    let formOpen = false;
    let editingId: string | null = null;
    let saving = false;
    let formType = '';
    let formDisplayName = '';
    let formDescription = '';
    let formConfigJson = '{}';
    let formIsActive = true;
    // Длительность ивента в минутах; пусто — завершение вручную
    let formDuration: number | undefined = undefined;
    let configTouched = false;
    let configError = '';
    // Бинго: удобное поле поверх ConfigJson (сам ключ хранится в конфиге)
    let formMaxPredictions = 13;
    // Лототрон: удобные поля поверх ConfigJson (ключи хранятся в конфиге)
    let formRafflePrice = 0;
    let formRaffleMaxTickets = 1;
    // «Шпионы»: баллы и пары слов поверх ConfigJson
    let formSpyPoints = 50;
    let formSpyCitizenPoints = 25;
    let formSpyPairs: { citizen: string; spy: string }[] = [];

    const typeLabels: Record<string, string> = {
        quick_checkin: 'Тост за именинника',
        promo_code: 'Промокоды',
        quiz: 'Квиз',
        word_rush: 'Слова на буквы',
        qr_scan: 'Охота за QR-кодами',
        spyfall: 'Шпионы'
    };

    function typeLabel(type: string) {
        return typeLabels[type] ?? type;
    }

    function availabilityLabel(value: string | number) {
        return value === 0 || value === 'Manual' ? 'Вручную' : String(value);
    }

    async function loadDefinitions() {
        try {
            definitions = await api<EventDefinition[]>('/api/events/definitions?includeInactive=true');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function loadSessions() {
        try {
            sessions = await api<EventSession[]>('/api/admin/sessions');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function loadTypes() {
        try {
            types = await api<EventTypeInfo[]>('/api/events/types');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function startEvent(definitionId: string) {
        try {
            await api(`/api/events/${definitionId}/start`, 'POST');
            showToast('Ивент запущен!');
            loadSessions();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function finishEvent(sessionId: string) {
        try {
            await api(`/api/events/${sessionId}/finish`, 'POST');
            showToast('Ивент завершён');
            loadSessions();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    function hasActiveSession(definitionId: string) {
        return sessions.some(s => s.definitionId === definitionId && s.state === 'Active');
    }

    function formatJson(raw: string): string {
        try {
            return JSON.stringify(JSON.parse(raw), null, 2);
        } catch {
            return raw;
        }
    }

    function defaultConfigFor(type: string): string {
        const info = types.find(t => t.type === type);
        return info ? formatJson(info.defaultConfigJson) : '{}';
    }

    function readConfigObject(): Record<string, any> | null {
        try {
            const parsed = JSON.parse(formConfigJson || '{}');
            if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
                return parsed;
            }
        } catch { /* невалидный JSON — поля не трогаем */ }
        return null;
    }

    /**
     * Подтягивает maxPredictions из ConfigJson в удобное поле.
     * useDefault=true (открытие формы, смена типа) подставляет половину поля, если ключа нет;
     * при ручном редактировании JSON значение не сбрасывается, если ключ убрали.
     */
    function syncBingoMaxPredictions(useDefault = false) {
        const config = readConfigObject();
        const value = Math.round(Number(config?.maxPredictions));

        if (Number.isFinite(value) && value > 0) {
            formMaxPredictions = value;
        } else if (useDefault) {
            const size = Number(config?.size);
            const totalCells = Number.isFinite(size) && size >= 3 && size <= 7 ? size * size : 25;
            formMaxPredictions = Math.max(1, Math.ceil(totalCells / 2));
        }
    }

    /** Переносит поле maxPredictions в ConfigJson перед сохранением. */
    function applyBingoMaxPredictions(): boolean {
        if (formType !== 'bingo') return true;

        const config = readConfigObject();
        if (!config) {
            configError = 'Невалидный JSON';
            return false;
        }

        const value = Math.round(Number(formMaxPredictions));
        if (!Number.isFinite(value) || value < 1) {
            configError = 'Лимит выбора должен быть больше 0';
            return false;
        }

        const size = Number(config.size);
        const totalCells = Number.isFinite(size) && size >= 3 && size <= 7 ? size * size : 25;
        const maxAllowed = Math.max(1, Math.ceil(totalCells / 2));
        if (value > maxAllowed) {
            configError = `Лимит выбора не может быть больше половины поля (${maxAllowed})`;
            return false;
        }

        config.maxPredictions = value;
        formConfigJson = JSON.stringify(config, null, 2);
        configError = '';
        return true;
    }

    /**
     * Подтягивает ticketPrice и maxTickets лототрона из ConfigJson в удобные поля.
     * useDefault=true (открытие формы, смена типа) подставляет 0 и 1, если ключей нет.
     */
    function syncRaffleSettings(useDefault = false) {
        const config = readConfigObject();

        const price = Math.round(Number(config?.ticketPrice));
        if (Number.isFinite(price) && price >= 0) {
            formRafflePrice = price;
        } else if (useDefault) {
            formRafflePrice = 0;
        }

        const maxTickets = Math.round(Number(config?.maxTickets));
        if (Number.isFinite(maxTickets) && maxTickets > 0) {
            formRaffleMaxTickets = maxTickets;
        } else if (useDefault) {
            formRaffleMaxTickets = 1;
        }
    }

    /** Переносит цену и лимит билетов лототрона в ConfigJson перед сохранением. */
    function applyRaffleSettings(): boolean {
        if (formType !== 'raffle') return true;

        const config = readConfigObject();
        if (!config) {
            configError = 'Невалидный JSON';
            return false;
        }

        const price = Math.round(Number(formRafflePrice));
        if (!Number.isFinite(price) || price < 0) {
            configError = 'Цена билета не может быть отрицательной';
            return false;
        }

        const maxTickets = Math.round(Number(formRaffleMaxTickets));
        if (!Number.isFinite(maxTickets) || maxTickets < 1) {
            configError = 'Максимум билетов должен быть больше 0';
            return false;
        }

        config.ticketPrice = price;
        config.maxTickets = maxTickets;
        formConfigJson = JSON.stringify(config, null, 2);
        configError = '';
        return true;
    }

    /**
     * Подтягивает баллы и пары слов «Шпионов» из ConfigJson в удобные поля.
     * useDefault=true (открытие формы, смена типа) подставляет значения по умолчанию.
     */
    function syncSpyFallSettings(useDefault = false) {
        const config = readConfigObject();

        const pointsSpy = Math.round(Number(config?.pointsSpy));
        if (Number.isFinite(pointsSpy) && pointsSpy >= 0) {
            formSpyPoints = pointsSpy;
        } else if (useDefault) {
            formSpyPoints = 50;
        }

        const pointsCitizen = Math.round(Number(config?.pointsCitizen));
        if (Number.isFinite(pointsCitizen) && pointsCitizen >= 0) {
            formSpyCitizenPoints = pointsCitizen;
        } else if (useDefault) {
            formSpyCitizenPoints = 25;
        }

        if (Array.isArray(config?.pairs)) {
            formSpyPairs = config.pairs.map((p: any) => ({
                citizen: String(p?.citizen ?? ''),
                spy: String(p?.spy ?? '')
            }));
        } else if (useDefault) {
            formSpyPairs = [];
        }
    }

    /** Переносит баллы и пары слов «Шпионов» в ConfigJson перед сохранением. */
    function applySpyFallSettings(): boolean {
        if (formType !== 'spyfall') return true;

        const config = readConfigObject();
        if (!config) {
            configError = 'Невалидный JSON';
            return false;
        }

        const pointsSpy = Math.round(Number(formSpyPoints));
        const pointsCitizen = Math.round(Number(formSpyCitizenPoints));
        if (!Number.isFinite(pointsSpy) || pointsSpy < 0 || !Number.isFinite(pointsCitizen) || pointsCitizen < 0) {
            configError = 'Баллы не могут быть отрицательными';
            return false;
        }

        const pairs = formSpyPairs
            .map((p) => ({ citizen: p.citizen.trim(), spy: p.spy.trim() }))
            .filter((p) => p.citizen || p.spy);

        if (pairs.some((p) => !p.citizen || !p.spy)) {
            configError = 'В каждой паре должны быть заполнены оба слова';
            return false;
        }

        if (pairs.length < 3) {
            configError = 'Нужно минимум 3 пары слов';
            return false;
        }

        config.pointsSpy = pointsSpy;
        config.pointsCitizen = pointsCitizen;
        config.pairs = pairs;
        formConfigJson = JSON.stringify(config, null, 2);
        configError = '';
        return true;
    }

    function addSpyPair() {
        formSpyPairs = [...formSpyPairs, { citizen: '', spy: '' }];
    }

    function removeSpyPair(index: number) {
        formSpyPairs = formSpyPairs.filter((_, i) => i !== index);
    }

    /** Ивенты со своим порядком запуска стартуют из отдельной вкладки админки. */
    function requiresCustomStart(type: string): boolean {
        return types.find((t) => t.type === type)?.requiresCustomStart === true;
    }

    function openCreateForm() {
        editingId = null;
        formType = types[0]?.type ?? '';
        formDisplayName = '';
        formDescription = '';
        formConfigJson = defaultConfigFor(formType);
        formIsActive = true;
        formDuration = undefined;
        configTouched = false;
        configError = '';
        syncBingoMaxPredictions(true);
        syncRaffleSettings(true);
        syncSpyFallSettings(true);
        formOpen = true;
    }

    function openEditForm(definition: EventDefinition) {
        editingId = definition.id;
        formType = definition.type;
        formDisplayName = definition.displayName;
        formDescription = definition.description ?? '';
        formConfigJson = formatJson(definition.configJson);
        formIsActive = definition.isActive;
        formDuration = definition.durationMinutes ?? undefined;
        configTouched = true;
        configError = '';
        syncBingoMaxPredictions(true);
        syncRaffleSettings(true);
        syncSpyFallSettings(true);
        formOpen = true;
    }

    function closeForm() {
        formOpen = false;
        editingId = null;
    }

    function handleTypeChange(event: Event) {
        formType = (event.currentTarget as HTMLSelectElement).value;
        if (!editingId && !configTouched) {
            formConfigJson = defaultConfigFor(formType);
        }
        syncBingoMaxPredictions(true);
        syncRaffleSettings(true);
        syncSpyFallSettings(true);
    }

    function handleConfigInput() {
        configTouched = true;
        configError = '';
        syncBingoMaxPredictions();
        syncRaffleSettings();
        syncSpyFallSettings();
    }

    function formatConfig() {
        try {
            formConfigJson = JSON.stringify(JSON.parse(formConfigJson), null, 2);
            configError = '';
        } catch {
            configError = 'Невалидный JSON';
        }
    }

    function validateConfig(): boolean {
        try {
            const parsed = JSON.parse(formConfigJson || '{}');
            if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
                configError = 'Конфиг должен быть JSON-объектом';
                return false;
            }
            configError = '';
            return true;
        } catch {
            configError = 'Невалидный JSON';
            return false;
        }
    }

    async function saveDefinition() {
        if (!formDisplayName.trim()) {
            showToast('Укажи название ивента', 'error');
            return;
        }

        // Пусто — ивент завершается вручную, иначе автозакрытие через N минут
        const duration = formDuration === undefined || formDuration === null
            ? null
            : Math.round(Number(formDuration));
        if (duration !== null && (!Number.isFinite(duration) || duration < 1 || duration > 1440)) {
            showToast('Длительность — от 1 до 1440 минут (или оставь поле пустым)', 'error');
            return;
        }

        if (!applyBingoMaxPredictions()) {
            showToast('Проверь настройки бинго', 'error');
            return;
        }
        if (!applyRaffleSettings()) {
            showToast('Проверь настройки лототрона', 'error');
            return;
        }
        if (!applySpyFallSettings()) {
            showToast('Проверь настройки «Шпионов»', 'error');
            return;
        }
        if (!validateConfig()) {
            showToast('Проверь ConfigJson', 'error');
            return;
        }

        saving = true;
        try {
            if (editingId) {
                await api(`/api/events/definitions/${editingId}`, 'PUT', {
                    displayName: formDisplayName.trim(),
                    description: formDescription.trim() || null,
                    configJson: formConfigJson,
                    isActive: formIsActive,
                    durationMinutes: duration
                });
                showToast('Ивент обновлён');
            } else {
                await api('/api/events/definitions', 'POST', {
                    type: formType,
                    displayName: formDisplayName.trim(),
                    description: formDescription.trim() || null,
                    configJson: formConfigJson,
                    durationMinutes: duration
                });
                showToast('Ивент создан');
            }

            closeForm();
            loadDefinitions();
        } catch (e: any) {
            showToast(e.message, 'error');
        } finally {
            saving = false;
        }
    }

    async function toggleActive(definition: EventDefinition) {
        try {
            await api(`/api/events/definitions/${definition.id}`, 'PUT', {
                displayName: definition.displayName,
                description: definition.description ?? null,
                configJson: definition.configJson,
                isActive: !definition.isActive,
                durationMinutes: definition.durationMinutes ?? null
            });
            showToast(definition.isActive ? 'Ивент деактивирован' : 'Ивент активирован');
            loadDefinitions();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function deleteDefinition(definition: EventDefinition) {
        if (!confirm(`Удалить ивент «${definition.displayName}»? Действие необратимо.`)) {
            return;
        }

        try {
            await api(`/api/events/definitions/${definition.id}`, 'DELETE');
            showToast('Ивент удалён');
            loadDefinitions();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    function badgeClass(state: string) {
        if (state === 'Active') return 'badge-active';
        if (state === 'Finished') return 'badge-finished';
        return 'badge-waiting';
    }

    onMount(() => {
        loadTypes();
        loadDefinitions();
        loadSessions();
    });
</script>

<h2>Управление ивентами</h2>

{#if formOpen}
    <div class="card form-card">
        <h3>{editingId ? 'Редактирование ивента' : 'Новый ивент'}</h3>

        <div class="form-row">
            <div class="form-group">
                <label for="event-type">Тип</label>
                <select id="event-type" value={formType} on:change={handleTypeChange} disabled={!!editingId}>
                    {#each types as t}
                        <option value={t.type}>{typeLabel(t.type)} ({t.type})</option>
                    {/each}
                </select>
                {#if editingId}
                    <p class="hint">Тип нельзя изменить после создания</p>
                {/if}
            </div>
            <div class="form-group">
                <label for="event-name">Название</label>
                <input id="event-name" type="text" maxlength="100" bind:value={formDisplayName}
                       placeholder="Например: Квиз про именинника" />
            </div>
            <div class="form-group form-group-small">
                <label for="event-duration">Длительность, мин</label>
                <input id="event-duration" type="number" min="1" max="1440" bind:value={formDuration}
                       placeholder="вручную" />
                <p class="hint">Пусто — завершение только кнопкой</p>
            </div>
            {#if editingId}
                <div class="form-group form-group-small">
                    <label for="event-active">Активен</label>
                    <input id="event-active" type="checkbox" bind:checked={formIsActive} />
                </div>
            {/if}
        </div>

        <div class="form-group">
            <label for="event-description">Описание</label>
            <input id="event-description" type="text" bind:value={formDescription}
                   placeholder="Что увидит игрок" />
        </div>

        {#if formType === 'bingo'}
            <div class="bingo-settings">
                <h4>⚙️ Настройки бинго</h4>
                <div class="form-row">
                    <div class="form-group">
                        <label for="bingo-max-predictions">Максимум предсказаний</label>
                        <input id="bingo-max-predictions" type="number" min="1" max="24"
                               bind:value={formMaxPredictions} />
                        <p class="hint">
                            Сколько событий игрок выбирает на 1 этапе. Не больше половины клеток поля
                            с округлением вверх (для 5×5 — 13). После «Завершить приём» выбор фиксируется
                            и не меняется.
                        </p>
                    </div>
                </div>
            </div>
        {/if}

        {#if formType === 'raffle'}
            <div class="event-settings">
                <h4>⚙️ Настройки лототрона</h4>
                <div class="form-row">
                    <div class="form-group">
                        <label for="raffle-price">Цена билета, баллов</label>
                        <input id="raffle-price" type="number" min="0" bind:value={formRafflePrice} />
                        <p class="hint">
                            Первый билет бесплатный, каждый следующий списывает эту сумму.
                            0 — все билеты бесплатные.
                        </p>
                    </div>
                    <div class="form-group">
                        <label for="raffle-max-tickets">Максимум билетов на игрока</label>
                        <input id="raffle-max-tickets" type="number" min="1" max="999"
                               bind:value={formRaffleMaxTickets} />
                        <p class="hint">1 — участие одним билетом, больше — билеты можно докупать.</p>
                    </div>
                </div>
            </div>
        {/if}

        {#if formType === 'spyfall'}
            <div class="event-settings">
                <h4>⚙️ Настройки «Шпионов»</h4>
                <div class="form-row">
                    <div class="form-group">
                        <label for="spyfall-points-spy">Баллы шпиону</label>
                        <input id="spyfall-points-spy" type="number" min="0" bind:value={formSpyPoints} />
                        <p class="hint">
                            За победу: угадал слово горожан или горожане не вычислили шпиона.
                        </p>
                    </div>
                    <div class="form-group">
                        <label for="spyfall-points-citizen">Баллы горожанам</label>
                        <input id="spyfall-points-citizen" type="number" min="0" bind:value={formSpyCitizenPoints} />
                        <p class="hint">
                            Каждому, кто проголосовал против настоящего шпиона, — но только если горожане победили.
                        </p>
                    </div>
                </div>

                <h4>Пары слов</h4>
                <p class="hint">
                    Первое слово видят горожане, второе — шпион. Нужно минимум 3 пары;
                    какая сторона пары кому достанется, сервер решает случайно.
                </p>

                {#each formSpyPairs as pair, i}
                    <div class="pair-row">
                        <input type="text" placeholder="Слово горожан" bind:value={pair.citizen} />
                        <input type="text" placeholder="Слово шпиона" bind:value={pair.spy} />
                        <button class="btn btn-danger" on:click={() => removeSpyPair(i)} title="Удалить пару">✖</button>
                    </div>
                {/each}

                <div class="form-actions">
                    <button class="btn btn-secondary" on:click={addSpyPair}>➕ Добавить пару</button>
                </div>
            </div>
        {/if}

        <div class="form-group">
            <label for="event-config">ConfigJson</label>
            <textarea id="event-config" rows="12" spellcheck="false" bind:value={formConfigJson}
                      on:input={handleConfigInput}></textarea>
            {#if configError}
                <p class="error">{configError}</p>
            {/if}
        </div>

        {#if editingId && hasActiveSession(editingId)}
            <p class="warning">⚠️ Идёт сессия этого ивента — правки конфига и деактивация применятся немедленно.</p>
        {/if}

        <div class="form-actions">
            <button class="btn btn-primary" on:click={formatConfig}>🧹 Форматировать</button>
            <button class="btn btn-success" on:click={saveDefinition} disabled={saving}>
                {saving ? 'Сохранение…' : '💾 Сохранить'}
            </button>
            <button class="btn btn-secondary" on:click={closeForm} disabled={saving}>Нет, отмена</button>
        </div>
    </div>
{/if}

<div class="card">
    <div class="card-head">
        <h3>Определения ивентов</h3>
        <div class="card-actions">
            <button class="btn btn-primary" on:click={openCreateForm} disabled={types.length === 0}>➕ Добавить ивент</button>
            <button class="btn btn-secondary" on:click={loadDefinitions}>🔄 Обновить</button>
        </div>
    </div>
    <div class="table-wrap">
        <table>
            <thead>
            <tr>
                <th>Название</th>
                <th>Тип</th>
                <th>Доступность</th>
                <th>Статус</th>
                <th>Действия</th>
            </tr>
            </thead>
            <tbody>
            {#each definitions as d}
                <tr class:inactive={!d.isActive}>
                    <td>
                        {d.displayName}
                        {#if !d.createdById}
                            <span class="system-mark" title="Создан приложением при первом запуске, удалить нельзя">системный</span>
                        {/if}
                    </td>
                    <td><code>{d.type}</code></td>
                    <td>{availabilityLabel(d.availability)}</td>
                    <td>
                        {#if d.isActive}
                            <span class="badge badge-active">Активен</span>
                        {:else}
                            <span class="badge badge-finished">Неактивен</span>
                        {/if}
                    </td>
                    <td class="actions">
                        {#if d.isActive && !requiresCustomStart(d.type)}
                            <button class="btn btn-success" on:click={() => startEvent(d.id)}>▶️ Запустить</button>
                        {/if}
                        {#if requiresCustomStart(d.type)}
                            <span class="custom-start-hint">запуск из своей вкладки</span>
                        {/if}
                        <button class="btn btn-primary" on:click={() => openEditForm(d)}>✏️ Изменить</button>
                        <button class="btn btn-warning" on:click={() => toggleActive(d)}>
                            {d.isActive ? '⏸ Деактивировать' : '▶️ Активировать'}
                        </button>
                        {#if d.createdById}
                            <button class="btn btn-danger" on:click={() => deleteDefinition(d)}>🗑 Удалить</button>
                        {/if}
                    </td>
                </tr>
            {/each}
            </tbody>
        </table>
    </div>
</div>

<div class="card">
    <div class="card-head">
        <h3>Сессии ивентов</h3>
        <div class="card-actions">
            <button class="btn btn-secondary" on:click={loadSessions}>🔄 Обновить</button>
        </div>
    </div>
    <div class="table-wrap">
        <table>
            <thead>
            <tr>
                <th>Ивент</th>
                <th>Тип</th>
                <th>Состояние</th>
                <th>Запущен</th>
                <th>Окончание</th>
                <th>Завершён</th>
                <th>Сабмитов</th>
                <th>Действия</th>
            </tr>
            </thead>
            <tbody>
            {#each sessions as s}
                <tr>
                    <td>{s.definitionName}</td>
                    <td><code>{s.type}</code></td>
                    <td><span class="badge {badgeClass(s.state)}">{s.state}</span></td>
                    <td>{new Date(s.startedAt).toLocaleTimeString()}</td>
                    <td>{s.endsAt ? new Date(s.endsAt).toLocaleTimeString() : '—'}</td>
                    <td>{s.endedAt ? new Date(s.endedAt).toLocaleTimeString() : '—'}</td>
                    <td>{s.submissionCount}</td>
                    <td>
                        {#if s.state === 'Active'}
                            <button class="btn btn-danger" on:click={() => finishEvent(s.id)}>⏹ Завершить</button>
                        {/if}
                    </td>
                </tr>
            {/each}
            </tbody>
        </table>
    </div>
</div>

<style>
    .card { background: var(--bg, #0f0f23); border-radius: 8px; padding: 16px; margin: 16px 0; }
    .form-card { border: 1px solid var(--border, #333); }
    .card-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; }
    .card-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .form-row { display: flex; gap: 16px; flex-wrap: wrap; }
    .form-group { flex: 1; min-width: 220px; margin-bottom: 12px; }
    .form-group-small { flex: 0 0 auto; min-width: 110px; }
    .form-group label { display: block; margin-bottom: 6px; color: var(--muted, #aaa); font-size: 14px; }
    .form-group input[type="text"], .form-group input[type="number"], .form-group select, .form-group textarea {
        width: 100%; padding: 10px; border-radius: 6px;
        border: 1px solid var(--border, #333); background: var(--bg, #0f0f23); color: #fff;
        font-family: inherit;
    }
    .form-group textarea {
        font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
        font-size: 13px; resize: vertical;
    }
    .form-group input[type="checkbox"] { width: auto; }
    .hint { color: var(--muted, #aaa); font-size: 12px; margin: 6px 0 0; }

    .bingo-settings, .event-settings {
        border: 1px dashed var(--border, #333);
        border-radius: 8px;
        padding: 12px 12px 0;
        margin-bottom: 12px;
    }

    .bingo-settings h4, .event-settings h4 {
        margin: 0 0 10px;
        font-size: 14px;
        color: var(--accent, #f5a623);
    }

    .pair-row {
        display: flex;
        gap: 8px;
        margin-bottom: 8px;
        flex-wrap: wrap;
    }

    .pair-row input {
        flex: 1 1 140px;
        min-width: 0;
        padding: 10px;
        border-radius: 6px;
        border: 1px solid var(--border, #333);
        background: var(--bg, #0f0f23);
        color: #fff;
    }

    .pair-row .btn { flex: 0 0 auto; }

    .custom-start-hint {
        color: var(--muted, #aaa);
        font-size: 12px;
        align-self: center;
    }
    .error { color: var(--red, #e74c3c); font-size: 13px; margin: 6px 0 0; }
    .warning { color: var(--orange, #f39c12); font-size: 14px; margin: 0 0 12px; }
    .form-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .table-wrap { overflow-x: auto; -webkit-overflow-scrolling: touch; margin-top: 12px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border, #333); white-space: nowrap; }
    th { color: var(--accent, #f5a623); }
    tr.inactive { opacity: 0.55; }
    .system-mark {
        color: var(--muted, #aaa); font-size: 11px; margin-left: 6px;
        border: 1px solid var(--border, #333); border-radius: 4px; padding: 1px 5px;
    }
    .actions { display: flex; gap: 6px; flex-wrap: wrap; }
    .btn { padding: 8px 14px; border: none; border-radius: 6px; cursor: pointer; font-size: 13px; }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .btn-primary { background: var(--blue, #3498db); color: #fff; }
    .btn-success { background: var(--green, #27ae60); color: #fff; }
    .btn-warning { background: var(--orange, #f39c12); color: #fff; }
    .btn-danger { background: var(--red, #e74c3c); color: #fff; }
    .btn-secondary { background: #555; color: #fff; }
    .badge { padding: 4px 10px; border-radius: 12px; font-size: 12px; font-weight: bold; }
    .badge-active { background: var(--green, #27ae60); color: #fff; }
    .badge-finished { background: #7f8c8d; color: #fff; }
    .badge-waiting { background: var(--orange, #f39c12); color: #fff; }
</style>
