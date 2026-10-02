# Хостинг PartyApp на Windows-ПК (белый IP + Let's Encrypt)

Задача: телефон должен открывать сайт по HTTPS. Без HTTPS service worker и push-уведомления
не работают вообще, а iOS не даст добавить сайт как приложение с уведомлениями.

## 1. Что нужно

- Белый IP и домен (например, `party-app.online`).
- Проброс портов **80** и **443** на этот ПК в настройках роутера.
- Firewall Windows: входящие TCP 80 и 443 разрешены; порт 5000 наружу **не** открываем.
- DNS: A-запись `party-app.online` → белый IP. Если домен в Cloudflare — режим **DNS only**
  (оранжевую тучу выключить, иначе Caddy не сможет получить сертификат).

## 2. Сборка фронтенда

Фронтенд отдаёт ASP.NET Core из `wwwroot`, поэтому перед запуском API:

```
cd web
npm run build
```

## 3. Caddy (HTTPS из коробки)

1. Скачай Caddy для Windows: https://caddyserver.com/download (caddy_windows_amd64.exe).
2. Положи в удобную папку, например `C:\caddy\`, и переименуй в `caddy.exe`.
3. Скопируй туда `deploy/Caddyfile` (при другом домене — поправь).
4. Проверь домен в конфиге и запусти:

```
C:\caddy\caddy.exe run --config C:\caddy\Caddyfile
```

При первом запуске Caddy получит сертификат и будет продлевать его сам.
Открой `https://party-app.online` — должен открыться SPA.

## 4. Автозапуск (служба Windows)

Caddy не умеет регистрироваться как служба сам, поэтому используем NSSM или WinSW.

Вариант NSSM:

```
nssm install PartyAppCaddy C:\caddy\caddy.exe
nssm set PartyAppCaddy AppParameters run --config C:\caddy\Caddyfile
nssm set PartyAppCaddy AppDirectory C:\caddy
nssm start PartyAppCaddy
```

Аналогично можно зарегистрировать API:

```
dotnet publish src/PartyApp.Api -c Release -o C:\PartyApp\api
nssm install PartyAppApi C:\Program Files\dotnet\dotnet.exe
nssm set PartyAppApi AppParameters C:\PartyApp\api\PartyApp.Api.dll
nssm set PartyAppApi AppDirectory C:\PartyApp\api
nssm start PartyAppApi
```

Важно: `App_Data` (база, VAPID- и JWT-ключи, логи, бэкапы) будет создана в рабочей
папке приложения (`C:\PartyApp\api\App_Data`). Не удаляй её — вместе с `vapid.json`
пропадут push-подписки, а с `jwt.json` все пользователи будут разлогинены.
Автобэкапы складываются в `App_Data/backups` (хранятся 5 последних); свежую копию
можно скачать в админке: «📊 Дашборд» → «💾 Скачать бэкап».

## 5. Проверка после запуска

1. `https://party-app.online/health` → `{"status":"ok","database":"ok"}`.
2. `https://party-app.online/manifest.webmanifest` открывается.
3. На iPhone: Safari → «Поделиться» → «На экран "Домой"» → запуск с иконки.
4. В приложении нажать 🔔 → «Включить уведомления» → «Отправить тестовое уведомление».

## Почему не Vite dev-сервер

`npm run dev` (порт 5173) отдаёт SPA без service worker (`devOptions.enabled: false`),
поэтому PWA и push там не проверяются. Для проверки — только собранное приложение
через API (порт 5000) и Caddy.
