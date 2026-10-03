# PartyApp — фронтенд (Svelte 5 + Vite)

SPA игрока, админки и большого экрана. Svelte 5, TypeScript, Vite и `vite-plugin-pwa`; realtime — `@microsoft/signalr`.

## Команды

```powershell
npm install       # зависимости
npm run dev       # dev-сервер на http://localhost:5173
npm run check     # svelte-check + tsc — единственная проверка типов
npm run build     # сборка в ../src/PartyApp.Api/wwwroot
```

`npm run dev` проксирует `/api` и `/hubs` (включая WebSocket) на `localhost:5000`, поэтому рядом должен быть запущен backend:

```powershell
dotnet run --project ../src/PartyApp.Api
```

## Важно

- Сборка идёт в `src/PartyApp.Api/wwwroot` (`emptyOutDir: true`) — это сгенерированный каталог, не редактируйте его вручную. Исходники статики — `web/public` (например, `push-sw.js`), код — `web/src`.
- В dev-режиме service worker выключен (`devOptions.enabled: false`): PWA и push-уведомления проверяются только на собранном приложении и только по HTTPS.
- Маршруты: `/` — игрок, `/admin` — админка, `/screen` — TV-экран.

Общее описание проекта, архитектура и команды из корня — в [корневом README](../README.md).
