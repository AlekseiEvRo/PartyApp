# Тесты PartyApp

Два проекта xUnit:

| Проект | Что проверяет | Тестов |
|---|---|---|
| `PartyApp.UnitTests` | Сервисы, обработчики ивентов, middleware, EF-конфигурации — без HTTP | 245 |
| `PartyApp.IntegrationTests` | API через `WebApplicationFactory`, реальные миграции, SignalR | 160 |

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
- **Файлы**: `LocalFileStorage` — сохранение/чтение/удаление, уникальные пути,
  валидация расширений, защита от path traversal.
- **Инфраструктура**: `ExceptionHandlingMiddleware` (400/500), EF-конфигурации
  (уникальные индексы, каскады, конвертер дат в UTC), `SubClaimUserIdProvider`.

## PartyApp.IntegrationTests

`PartyAppFactory` поднимает настоящее приложение: временная SQLite-БД в Temp,
реальные миграции и сид из `Program.cs`, тестовый JWT, фейковый push.
`PartyAppApi` — обёртка над `HttpClient` (регистрация, логин, админ,
авторизация, баланс). Параллельность тестов выключена (`AssemblyInfo.cs`).

### Что покрыто

- **Auth/Wallet/Admin**: регистрация/логин/`me`, приветственный бонус,
  403 для игроков, лидерборд, ручные начисления и списания, история транзакций
  кошелька с пагинацией.
- **Rate limiting**: при превышении лимита `/api/auth` возвращает **429**
  с JSON-ошибкой; при `RateLimiting:Enabled=false` лимиты не действуют.
- **Events**: полный E2E-сценарий «админ стартует квиз → игрок отвечает →
  баланс растёт → повтор отклоняется → админ завершает», `available`,
  `data`, определения и CRUD (`POST/PUT/DELETE /api/events/definitions`,
  `GET /types`), конфликты старта/финиша.
- **Toast/QR/Push/Notifications**: реальный кулдаун тостов, одноразовые
  QR-коды (некорректные count/points — 400), защита от SSRF в подписке
  на push, рассылка сообщений ведущего.
- **Фото/Пожелания**: загрузка с проверкой сигнатуры и лимита размера, лента,
  лайки-переключатели, модерация (скрыто до одобрения), права на удаление;
  стенка пожеланий с премодерацией и видимостью только одобренного. Удаление
  и отзыв одобрения рассылаются по SignalR (`PhotoRemoved`/`WishRemoved`),
  чтобы элементы исчезали у всех без перезагрузки. О новом контенте на
  модерации админам приходят `ModerationPending` (только в группу `admins`)
  и push с тегом `moderation`.
- **Экран**: `ScreenService` — режимы, версии состояния и конфетти; API
  `/api/screen/*` с валидацией (режим, sessionId, сообщение, реакции) и правами:
  режимы переключает админ, конфетти и стикеры/подписи доступны игрокам.
  Рассылки `ScreenUpdated`, `ScreenConfetti` и `ScreenReaction`.
- **SignalR**: настоящий `HubConnection` через `TestServer` — отказ без токена,
  `BalanceUpdated`, `EventStarted`, `ReceiveBroadcast`, `SpyGameRoleAssigned`,
  `PhotoRemoved`, `WishRemoved`, `ModerationPending` (админам и никому другому),
  `ScreenUpdated` и `ScreenConfetti`.

## Известные ограничения, зафиксированные тестами

Это не баги тестов, а текущее поведение приложения:

- В `PlayerSubmissions` сохраняются и отклонённые попытки (например, повторный
  ответ на вопрос квиза) — со `Score = 0`.
- `TryGetProperty` в обработчиках чувствителен к регистру ключей payload,
  поэтому клиент обязан слать camelCase (`questionIndex`, `answerIndex`, `word`).
