# AGENTS.md

Party/event app: ASP.NET Core 9 minimal API (`src/`) + Svelte 5 SPA (`web/`). SQLite via EF Core, SignalR for realtime, JWT auth. User-facing strings and most code comments are Russian — keep new ones Russian.

## Layout

- `src/PartyApp.Domain` — entities, enums, abstractions; no infrastructure dependencies.
- `src/PartyApp.Infrastructure` — `AppDbContext`, EF configurations, migrations, wallet/file/QR services.
- `src/PartyApp.Api` — minimal API endpoints grouped by feature in `Modules/<Feature>/`, SignalR in `Hubs/`. `Program.cs` wires everything (DI, JWT, CORS, seeding).
- `web/` — Svelte 5 + Vite + TS PWA; Vite build output goes to `src/PartyApp.Api/wwwroot`.
- `tests/` — two xUnit projects, both still placeholder `UnitTest1` stubs. No CI.

## Commands

Backend (from repo root):

- `dotnet build PartyApp.sln`
- `dotnet test PartyApp.sln` (only passes the two placeholder tests)
- `dotnet run --project src/PartyApp.Api` — http://localhost:5000, Swagger at `/swagger`; applies migrations and seeds event definitions on startup.
- New migration: `dotnet ef migrations add <Name> --project src/PartyApp.Infrastructure --startup-project src/PartyApp.Api`. No design-time factory and migrations live in Infrastructure, so both projects are required. Never run `database update` — startup migrates.

Frontend (in `web/`):

- `npm run dev` — Vite on 5173, proxies `/api` and `/hubs` (incl. WebSocket) to `localhost:5000`.
- `npm run check` — svelte-check + tsc; the only frontend verification.
- `npm run build` — writes to `../src/PartyApp.Api/wwwroot` with `emptyOutDir: true`.

## Gotchas

- `src/PartyApp.Api/wwwroot` is generated output. Never edit it; edit `web/public/` (static files such as `admin.html`, `signalr-test.html`, icons) or `web/src`. `index.html` is not committed, so the API serves the SPA only after `npm run build`; for development use the Vite dev server.
- `src/PartyApp.Api/App_Data/` (SQLite `party.db`, uploads, dictionary) is gitignored and absent from a fresh clone. `word_rush` requires `App_Data/russian.txt` (one word per line); without it every submitted word is rejected (`RussianDictionaryService`).
- No admin bootstrap anywhere: registration always creates `UserRole.Player` (`Admin = 10`), so to use admin endpoints set the role directly in SQLite, e.g. `UPDATE Users SET Role = 10 WHERE Username = '...';`, then log in again (role is in the JWT).
- New event type requires: an `IEventHandler` in `src/PartyApp.Api/Modules/Events/Handlers/` (auto-registered via reflection; `EventType` must equal `EventDefinition.Type`), a seeded `EventDefinition` in `Program.cs`, and a UI branch in `web/src/lib/components/EventCard.svelte`. Note: `PartyApp.Domain.Abstractions.IEventHandler` is an unused empty stub — implement the `PartyApp.Api.Modules.Events.Handlers` one.
- `ToastService` and `SpyGameService` are singletons and keep game state in memory (lost on restart); event sessions/submissions are persisted in SQLite.
- Claims are not remapped (`MapInboundClaims = false`): `sub`, `name`, `role`. `Clients.User(playerId)` works because `SubClaimUserIdProvider` uses `sub`.
- Two separate admin UIs share the API but not code: SPA route `/admin` (`web/src/lib/components/admin/`) and standalone `web/public/admin.html`. Check which one a task refers to.
- CORS allows only `http://localhost:5173`; SignalR takes the JWT from the `access_token` query string for `/hubs`.

## Conventions

- `.editorconfig` is authoritative: 4 spaces, file-scoped namespaces, braces on new lines, `_camelCase` private fields, `var` avoided.
- Keep feature code under `Modules/<Feature>/` as static `Map*Endpoints` extension classes; register new maps in `Program.cs`.
- Singleton handlers/services must resolve `AppDbContext` via `IServiceScopeFactory` (as the existing handlers do), never inject the scoped context.
