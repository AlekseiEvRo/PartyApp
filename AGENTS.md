# AGENTS.md

Party/event app: ASP.NET Core 9 minimal API (`src/`) + Svelte 5 SPA (`web/`). SQLite via EF Core, SignalR for realtime, JWT auth. User-facing strings and most code comments are Russian — keep new ones Russian.

## Layout

- `src/PartyApp.Domain` — entities, enums, abstractions; no infrastructure dependencies.
- `src/PartyApp.Infrastructure` — `AppDbContext`, EF configurations, migrations under `Persistence/Migrations`, wallet/file/QR services.
- `src/PartyApp.Api` — minimal API endpoints grouped by feature in `Modules/<Feature>/`, SignalR in `Hubs/`. `Program.cs` wires everything (DI, JWT, CORS, seeding).
- `web/` — Svelte 5 + Vite + TS PWA; Vite build output goes to `src/PartyApp.Api/wwwroot`.
- `tests/` — два xUnit проекта: `PartyApp.UnitTests` (реальная SQLite in-memory, NSubstitute, FluentAssertions) и `PartyApp.IntegrationTests` (`WebApplicationFactory<Program>`, реальные миграции в Temp-SQLite, SignalR). Детали и известные ограничения — `tests/README.md`. CI нет.

## Commands

Backend (from repo root):

- `dotnet build PartyApp.sln`
- `dotnet test PartyApp.sln` (407 тестов: unit + integration; `Program.cs` обязан оставаться доступным тестам через `public partial class Program`)
- `dotnet run --project src/PartyApp.Api` — http://localhost:5000, Swagger at `/swagger`; applies migrations and seeds event definitions on startup.
- New migration: `dotnet ef migrations add <Name> --project src/PartyApp.Infrastructure --startup-project src/PartyApp.Api -o Persistence/Migrations`. No design-time factory, so both projects are required; the `-o` is required to match the existing migrations and their namespaces. Never run `database update` — startup migrates.

Frontend (in `web/`):

- `npm run dev` — Vite on 5173, proxies `/api` and `/hubs` (incl. WebSocket) to `localhost:5000`.
- `npm run check` — svelte-check + tsc; the only frontend verification.
- `npm run build` — writes to `../src/PartyApp.Api/wwwroot` with `emptyOutDir: true`.

## Gotchas

- `src/PartyApp.Api/wwwroot` is generated output. Never edit it; edit `web/public/` (static files such as `admin.html`, `signalr-test.html`, `push-sw.js`, icons) or `web/src`. `wwwroot/index.html` exists only after `npm run build`, so a fresh clone's API serves the SPA only post-build; during development use the Vite dev server. `npm run build` also rewrites/deletes some stale tracked assets under `wwwroot` and dirties git — don't commit those changes.
- `src/PartyApp.Api/App_Data/` (SQLite `party.db`, uploads, dictionary) is gitignored and absent from a fresh clone. `word_rush` requires `App_Data/russian.txt` (one word per line); without it every submitted word is rejected (`RussianDictionaryService`).
- No admin bootstrap anywhere: registration always creates `UserRole.Player` (`Admin = 10`), so to use admin endpoints set the role directly in SQLite, e.g. `UPDATE Users SET Role = 10 WHERE Username = '...';`, then log in again (role is in the JWT).
- New event type requires: an `IEventHandler` in `src/PartyApp.Api/Modules/Events/Handlers/` (auto-registered via reflection; `EventType` must equal `EventDefinition.Type`), a seeded `EventDefinition` in `Program.cs`, and a UI branch in `web/src/lib/components/EventCard.svelte`. Note: `PartyApp.Domain.Abstractions.IEventHandler` is an unused empty stub — implement the `PartyApp.Api.Modules.Events.Handlers` one.
- Фотоальбом и стенка пожеланий живут в `Modules/Photos/` и `Modules/Submissions/`; файлы фото лежат под `Files:UploadRoot` (`App_Data/uploads`), в БД только метаданные. Модерация управляется конфигом `Photos:RequireModeration` и `Wishes:RequireModeration` (по умолчанию оба true); админские вкладки «Фото»/«Пожелания» в SPA-админке. О новых материалах на модерации админам сообщает `Modules/Moderation/ModerationNotifier` — SignalR-событие `ModerationPending` в группу `admins` (хаб добавляет туда по роли при подключении) и push с тегом `moderation` (троттлинг 30 секунд на админа).
- Host-экран: `Modules/Screen/` (`GET/POST /api/screen/state`, `POST /api/screen/confetti`, `POST /api/screen/reactions`) + singleton `ScreenService` в памяти; SPA-маршрут `/screen` (только админ) и вкладка «🖥 Экран» в админке. Состояние рассылается событием `ScreenUpdated`, конфетти — `ScreenConfetti`, стикеры и подписи от игроков — `ScreenReaction` (любой авторизованный, лимит `submit`). Экран сам переключается на свежий ивент по `EventStarted` и на лидерборд по `EventFinished`; ручное переключение админом (`ScreenUpdated`) сбрасывает автопереход. Таймер ивента считается от `StartedAt` + смещение серверного времени. «Живые» данные ивента отдаются в `GET /data` полем `live` (для `quick_checkin` — кто говорит тост) и обновляются событием `EventLiveUpdated`; хендлеры могут переопределить `IEventHandler.GetLiveDataAsync`.
- `ToastService` and `SpyGameService` are singletons and keep game state in memory (lost on restart); event sessions/submissions are persisted in SQLite.
- Push-уведомления: `Modules/Push/` (`/api/push/subscribe`, `/unsubscribe`, `/test`, `/vapid-public-key`), отправка через NuGet `WebPush`. VAPID-ключи автогенерируются в `App_Data/vapid.json` (не удалять — иначе подписки станут невалидными), переопределяются конфигом `Push:*`. Обработчик `push`/`notificationclick` живёт в `web/public/push-sw.js` и подключается в сгенерированный SW через `workbox.importScripts`. В Vite dev service worker выключен, поэтому PWA и push проверяются только на собранном приложении (`npm run build` + API). На iOS push работает лишь в приложении, добавленном на экран «Домой» (iOS 16.4+, Safari), и только по HTTPS. Баллы начисляются через `Modules/Wallet/PointsAwardService` — там же рассылка `BalanceUpdated` и push.
- Хостинг: `deploy/Caddyfile` + `deploy/README.md` — Caddy терминирует HTTPS и проксирует на `localhost:5000`. Корень `/` отдаёт SPA (`MapFallbackToFile`); API-документация осталась на `/swagger`.
- Claims are not remapped (`MapInboundClaims = false`): `sub`, `name`, `role`. `Clients.User(playerId)` works because `SubClaimUserIdProvider` uses `sub`.
- Two separate admin UIs share the API but not code: SPA route `/admin` (`web/src/lib/components/admin/`) and standalone `web/public/admin.html`. Check which one a task refers to.
- CORS allows only `http://localhost:5173`; SignalR takes the JWT from the `access_token` query string for `/hubs`.
- Rate limiting включается конфигом `RateLimiting:*` (`Enabled`, `AuthPermitLimit`, `SubmitTokenLimit` и т.д.) и читается на каждый запрос. В интеграционных тестах выключен настройкой в `PartyAppFactory`; тесты самих лимитов поднимают отдельную фабрику через `ConfigureOverrides`.

## Conventions

- `.editorconfig` is authoritative: 4 spaces, file-scoped namespaces, braces on new lines, `var` avoided; private fields `_camelCase`, private static fields `s_camelCase`, constants PascalCase; `.cs` files get no final newline.
- Keep feature code under `Modules/<Feature>/` as static `Map*Endpoints` extension classes; register new maps in `Program.cs`.
- Singleton handlers/services must resolve `AppDbContext` via `IServiceScopeFactory` (as the existing handlers do), never inject the scoped context.
