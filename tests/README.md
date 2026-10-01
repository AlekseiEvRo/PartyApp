# Тесты PartyApp

Два проекта xUnit:

| Проект | Что проверяет | Тестов |
|---|---|---|
| `PartyApp.UnitTests` | Сервисы, обработчики ивентов, middleware, EF-конфигурации — без HTTP | 229 |
| `PartyApp.IntegrationTests` | API через `WebApplicationFactory`, реальные миграции, SignalR | 116 |

Общий запуск из корня репозитория:

```powershell
dotnet test PartyApp.sln
```

Запуск только одного проекта / класса:

```powershell
dotnet test tests\PartyApp.UnitTests
dotnet test tests\PartyApp.IntegrationTests --filter "FullyQualifiedName~QuizHandlerTests"
```

Покрытие (coverlet уже подключён; отчёт появится в `TestResults/`):

```powershell
dotnet test PartyApp.sln --collect:"XPlat Code Coverage"
```

## PartyApp.UnitTests

Тестирует боевые классы напрямую: реальный DI-контейнер, реальная SQLite
in-memory и записывающие фейки вместо инфраструктуры.

### Инфраструктура (`Testing/`)

| Класс | Назначение |
|---|---|
| `SqliteTestHost` | DI-контейнер + SQLite `Data Source=:memory:`, `EnsureCreated`, scopes как в проде |
| `FakePushNotificationService` | Записывает push-вызовы (`Calls`) вместо отправки |
| `RecordingHubContext` | Записывает рассылки SignalR: адресат (`user:{id}`, `all`), метод, payload |
| `TestData` | Фабрики сущностей (`User`, `EventDefinition`, `EventSession`, `QrToken`) |
| `TestConfiguration` | In-memory `IConfiguration` |
| `TempDictionaryFile` | Временный словарь для `WordRushHandler` |

### Что покрыто

- **Auth**: `TokenService` (claims, issuer/audience, срок жизни, подпись ключом),
  `AuthService` (регистрация, валидация, дубликаты, приветственный бонус, логин).
- **События**: `EventService` (старт/финиш/сабмит/доступные ивенты/данные),
  `EventHandlerFactory` (регистрация, дубликаты, case-insensitive),
  `QrTokenService` (лимиты, формат кодов, уникальность),
  `RussianDictionaryService` (Ё→Е, регистр, отсутствующий файл).
- **Обработчики ивентов**: `quiz`, `word_rush`, `promo_code`, `qr_scan`,
  `quick_checkin` — все ветки ошибок и успеха, повторные попытки, дефолты
  конфигов и валидность `DefaultConfigJson` (заготовки для админки).
- **Кошелёк**: `PointsAwardService` — создание/обновление кошелька, транзакции,
  `BalanceUpdated`, push с троттлингом.
- **Toast/SpyGame**: кулдауны и тайминги через `FakeTimeProvider`,
  роли рассказчик/угадчик, обвинения, победы, ничья.
- **Push**: VAPID из конфига/файла, автогенерация и переиспользование ключей.
- **Инфраструктура**: `ExceptionHandlingMiddleware` (400/500), EF-конфигурации
  (уникальные индексы, каскады, конвертер дат в UTC), `SubClaimUserIdProvider`.

## PartyApp.IntegrationTests

`PartyAppFactory` поднимает настоящее приложение: временная SQLite-БД в Temp,
реальные миграции и сид из `Program.cs`, тестовый JWT, фейковый push.
`PartyAppApi` — обёртка над `HttpClient` (регистрация, логин, админ,
авторизация, баланс). Параллельность тестов выключена (`AssemblyInfo.cs`).

### Что покрыто

- **Auth/Wallet/Admin**: регистрация/логин/`me`, приветственный бонус,
  403 для игроков, лидерборд, ручные начисления и списания.
- **Events**: полный E2E-сценарий «админ стартует квиз → игрок отвечает →
  баланс растёт → повтор отклоняется → админ завершает», `available`,
  `data`, определения и CRUD (`POST/PUT/DELETE /api/events/definitions`,
  `GET /types`), конфликты старта/финиша.
- **Toast/QR/Push/Notifications**: реальный кулдаун тостов, одноразовые
  QR-коды, защита от SSRF в подписке на push, рассылка сообщений ведущего.
- **SignalR**: настоящий `HubConnection` через `TestServer` — отказ без токена,
  `BalanceUpdated`, `EventStarted`, `ReceiveBroadcast`, `SpyGameRoleAssigned`.

## Известные ограничения, зафиксированные тестами

Это не баги тестов, а текущее поведение приложения:

- `QrTokenService.GenerateTokensAsync` при `count` вне 1..100 бросает
  `InvalidOperationException` → клиент получает **500**, а не 400.
- `SpyGameService.StartGameAsync` при невалидном составе игроков тоже даёт
  **500** (исключение не обрабатывается эндпоинтом).
- В `PlayerSubmissions` сохраняются и отклонённые попытки (например, повторный
  ответ на вопрос квиза) — со `Score = 0`.
- `TryGetProperty` в обработчиках чувствителен к регистру ключей payload,
  поэтому клиент обязан слать camelCase (`questionIndex`, `answerIndex`, `word`).
