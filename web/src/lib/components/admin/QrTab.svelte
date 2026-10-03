<script lang="ts">
    import { onMount } from 'svelte';
    import QRCode from 'qrcode';
    import { api } from '../../api';
    import { showToast } from '../../stores';

    interface QrToken {
        id: string;
        code: string;
        points: number;
        isRedeemed: boolean;
        redeemedAt: string | null;
        redeemedByName: string | null;
    }

    let tokens: QrToken[] = [];
    let qrCount = 5;
    let qrPoints = 20;

    // Печать: картинки строятся на клиенте, кучей за один заход
    let printOnlyAvailable = true;
    let printing = false;

    async function loadTokens() {
        try {
            tokens = await api<QrToken[]>('/api/qr/tokens');
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    async function generateTokens() {
        try {
            const result = await api<any>('/api/qr/tokens/generate', 'POST', {
                count: qrCount,
                points: qrPoints
            });
            showToast(`Сгенерировано ${result.generated} кодов`);
            loadTokens();
        } catch (e: any) {
            showToast(e.message, 'error');
        }
    }

    /** В QR зашита ссылка: приложение само активирует код после входа. */
    function qrPayload(code: string): string {
        return `${window.location.origin}/?qr=${encodeURIComponent(code)}`;
    }

    function tokensForPrint(): QrToken[] {
        return printOnlyAvailable ? tokens.filter((t) => !t.isRedeemed) : tokens;
    }

    async function renderQr(code: string): Promise<string> {
        return QRCode.toDataURL(qrPayload(code), {
            width: 360,
            margin: 1,
            errorCorrectionLevel: 'M'
        });
    }

    /** Открывает отдельное окно с сеткой QR-кодов: удобно печатать и резать. */
    async function printAll() {
        const selected = tokensForPrint();
        if (selected.length === 0) {
            showToast('Нет кодов для печати', 'info');
            return;
        }

        printing = true;
        try {
            const images = new Map<string, string>();
            for (const token of selected) {
                images.set(token.id, await renderQr(token.code));
            }

            const cells = selected.map((token) => `
                <div class="cell">
                    <img src="${images.get(token.id)}" alt="${token.code}" />
                    <div class="code">${token.code}</div>
                    <div class="points">⭐ ${token.points}</div>
                </div>`).join('');

            const html = `<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8" />
<title>QR-коды (${selected.length})</title>
<style>
    body { font-family: system-ui, -apple-system, Segoe UI, sans-serif; margin: 10mm; color: #111; }
    h1 { font-size: 18px; margin: 0 0 6mm; }
    .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 5mm; }
    .cell { border: 1px dashed #999; border-radius: 3mm; padding: 4mm; text-align: center; break-inside: avoid; }
    .cell img { width: 45mm; height: 45mm; }
    .code { font-size: 20px; font-weight: bold; letter-spacing: 2px; margin-top: 2mm; }
    .points { color: #666; font-size: 12px; }
    @media print { .grid { grid-template-columns: repeat(3, 1fr); } }
</style>
</head>
<body>
    <h1>🎉 PartyApp — QR-коды (${selected.length})</h1>
    <div class="grid">${cells}</div>
</body>
</html>`;

            const win = window.open('', '_blank');
            if (!win) {
                showToast('Разреши всплывающие окна, чтобы напечатать QR-коды', 'error');
                return;
            }

            win.document.write(html);
            win.document.close();
            win.focus();
            // Даём картинкам отрисоваться, затем открываем диалог печати
            setTimeout(() => win.print(), 300);
        } catch (e: any) {
            showToast(e instanceof Error ? e.message : 'Не удалось построить QR-коды', 'error');
        } finally {
            printing = false;
        }
    }

    /** Скачивает PNG одного кода. */
    async function downloadPng(token: QrToken) {
        try {
            const url = await renderQr(token.code);
            const link = document.createElement('a');
            link.href = url;
            link.download = `qr-${token.code}.png`;
            link.click();
        } catch (e: any) {
            showToast(e instanceof Error ? e.message : 'Не удалось сохранить PNG', 'error');
        }
    }

    onMount(loadTokens);
</script>

<h2>QR-коды</h2>

<div class="card">
    <h3>Генерация новых кодов</h3>
    <div class="form-row">
        <div class="form-group">
            <label>Количество</label>
            <input type="number" bind:value={qrCount} min="1" max="100" />
        </div>
        <div class="form-group">
            <label>Баллов за код</label>
            <input type="number" bind:value={qrPoints} min="1" />
        </div>
        <div class="form-group">
            <label>&nbsp;</label>
            <button class="btn btn-success" on:click={generateTokens}>🎲 Сгенерировать</button>
        </div>
    </div>
    <p class="hint">
        Каждый QR ведёт на сайт с кодом внутри: игрок сканирует камерой телефона,
        открывается приложение — и код активируется сам. Текст кода напечатан под картинкой
        на случай, если сканирование не сработало.
    </p>
</div>

<div class="card">
    <h3>Существующие коды</h3>
    <div class="toolbar">
        <button class="btn btn-primary" on:click={loadTokens}>🔄 Обновить</button>
        <label class="check">
            <input type="checkbox" bind:checked={printOnlyAvailable} />
            только неактивированные
        </label>
        <button class="btn btn-print" on:click={printAll} disabled={printing}>
            {printing ? '⏳ Готовим…' : '🖨 Печать всех'}
        </button>
    </div>
    <div class="table-wrap">
        <table>
            <thead>
            <tr>
                <th>Код</th>
                <th>Баллы</th>
                <th>Статус</th>
                <th>Кто ввёл</th>
                <th>Использован</th>
                <th></th>
            </tr>
            </thead>
            <tbody>
            {#each tokens as t}
                <tr>
                    <td><strong class="code">{t.code}</strong></td>
                    <td>{t.points}</td>
                    <td>
                        {#if t.isRedeemed}
                            <span class="badge badge-finished">Использован</span>
                        {:else}
                            <span class="badge badge-active">Доступен</span>
                        {/if}
                    </td>
                    <td>{t.redeemedByName ?? '—'}</td>
                    <td>{t.redeemedAt ? new Date(t.redeemedAt).toLocaleTimeString() : '—'}</td>
                    <td>
                        <button class="btn btn-ghost" on:click={() => downloadPng(t)} title="Скачать PNG">
                            ⬇ PNG
                        </button>
                    </td>
                </tr>
            {/each}
            </tbody>
        </table>
    </div>
</div>

<style>
    .card { background: var(--bg, #0f0f23); border-radius: 8px; padding: 16px; margin: 16px 0; }
    .form-row { display: flex; gap: 16px; flex-wrap: wrap; }
    .form-group { flex: 1; min-width: 150px; }
    .form-group label { display: block; margin-bottom: 6px; color: var(--muted, #aaa); font-size: 14px; }
    .form-group input {
        width: 100%; padding: 10px; border-radius: 6px;
        border: 1px solid var(--border, #333); background: var(--bg, #0f0f23); color: #fff;
    }
    .hint { color: var(--muted, #aaa); font-size: 12px; line-height: 1.5; margin: 12px 0 0; }
    .toolbar { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; margin-top: 10px; }
    .check { display: flex; align-items: center; gap: 6px; font-size: 13px; color: var(--muted, #aaa); }
    .table-wrap { overflow-x: auto; -webkit-overflow-scrolling: touch; margin-top: 12px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border, #333); white-space: nowrap; }
    th { color: var(--accent, #f5a623); }
    .code { font-size: 18px; letter-spacing: 2px; }
    .btn { padding: 8px 14px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; }
    .btn:disabled { opacity: 0.5; cursor: default; }
    .btn-primary { background: var(--blue, #3498db); color: #fff; }
    .btn-success { background: var(--green, #27ae60); color: #fff; }
    .btn-print { background: var(--purple, #8e44ad); color: #fff; }
    .btn-ghost { background: #2a2a5a; color: #ddd; font-size: 12px; padding: 6px 10px; }
    .badge { padding: 4px 10px; border-radius: 12px; font-size: 12px; font-weight: bold; }
    .badge-active { background: var(--green, #27ae60); color: #fff; }
    .badge-finished { background: #7f8c8d; color: #fff; }

    @media (max-width: 480px) {
        .form-group { min-width: 100%; }
    }
</style>
