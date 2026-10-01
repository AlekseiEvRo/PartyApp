using System.Reflection;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

using PartyApp.Api.Common.Middleware;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Admin;
using PartyApp.Api.Modules.Auth;
using PartyApp.Api.Modules.Auth.Services;
using PartyApp.Api.Modules.Events;
using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Moderation;
using PartyApp.Api.Modules.Notifications;
using PartyApp.Api.Modules.Photos;
using PartyApp.Api.Modules.Push;
using PartyApp.Api.Modules.Qr;
using PartyApp.Api.Modules.Screen;
using PartyApp.Api.Modules.SpyGame;
using PartyApp.Api.Modules.Submissions;
using PartyApp.Api.Modules.Toast;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Files;
using PartyApp.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, loggerConfig) =>
{
    loggerConfig.ReadFrom.Configuration(context.Configuration);
    loggerConfig.WriteTo.Console();
});

// EF Core + SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseSqlite(connectionString, sqlite =>
    {
        sqlite.MigrationsAssembly("PartyApp.Infrastructure");
    });
});


// JWT Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "Bearer";
    options.DefaultChallengeScheme = "Bearer";
})
.AddJwtBearer("Bearer", options =>
{
    // Не мапим стандартные JWT-claims в ClaimTypes, используем короткие имена
    options.MapInboundClaims = false;

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,

        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "party-app",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "party-app-clients",
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(
                builder.Configuration["Jwt:SigningKey"] ?? "SuperSecretKeyForDevelopmentOnly12345!"
            )
        ),

        NameClaimType = "name",
        RoleClaimType = "role"
    };
    
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
});

// Authorization + политика для админов
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// === Rate limiting ===
// Защищает /api/auth от перебора пароля, а отправку ответов в ивентах — от случайных
// дублей и скриптов. Настройки читаются на каждый запрос, чтобы их можно было
// переопределять в конфиге (в том числе в интеграционных тестах).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            """{"error":"Слишком много запросов. Попробуй чуть позже"}""", ct);
    };

    // Логин/регистрация — окно на IP-адрес
    options.AddPolicy("auth", httpContext =>
    {
        IConfiguration config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        string key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!config.GetValue("RateLimiting:Enabled", true))
            return RateLimitPartition.GetNoLimiter(key);

        int permitLimit = config.GetValue("RateLimiting:AuthPermitLimit", 10);
        TimeSpan window = TimeSpan.FromSeconds(config.GetValue("RateLimiting:AuthWindowSeconds", 60));

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0
        });
    });

    // Ответы в ивентах — лимит на игрока, без авторизации — на IP
    options.AddPolicy("submit", httpContext =>
    {
        IConfiguration config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        string key = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        if (!config.GetValue("RateLimiting:Enabled", true))
            return RateLimitPartition.GetNoLimiter(key);

        int tokenLimit = config.GetValue("RateLimiting:SubmitTokenLimit", 30);
        TimeSpan refill = TimeSpan.FromSeconds(config.GetValue("RateLimiting:SubmitRefillSeconds", 60));

        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokenLimit,
            ReplenishmentPeriod = refill,
            AutoReplenishment = true,
            QueueLimit = 0
        });
    });
});

// Services
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();
builder.Services.AddSingleton<ToastService>();
builder.Services.AddSingleton<SpyGameService>();
builder.Services.AddSingleton<IPushNotificationService, PushNotificationService>();
builder.Services.AddSingleton<IPointsAwardService, PointsAwardService>();
builder.Services.AddScoped<ModerationNotifier>();
builder.Services.AddSingleton<ScreenService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSignalR();

// === Конструктор ивентов ===

// Автоматическая регистрация всех IEventHandler через reflection
var handlerTypes = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => typeof(IEventHandler).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

foreach (var handlerType in handlerTypes)
{
    builder.Services.AddSingleton(typeof(IEventHandler), handlerType);
}

// Словарь русских слов
builder.Services.AddSingleton<RussianDictionaryService>();

// QR сервис
builder.Services.AddSingleton<IQrTokenService, QrTokenService>();

// Файловое хранилище (фотоальбом)
string uploadRoot = builder.Configuration["Files:UploadRoot"] ?? "App_Data/uploads";
builder.Services.AddSingleton<IFileStorage>(sp =>
    new LocalFileStorage(uploadRoot, sp.GetRequiredService<ILogger<LocalFileStorage>>()));

// Фабрика обработчиков
builder.Services.AddSingleton<IEventHandlerFactory, EventHandlerFactory>();

// Сервис управления ивентами
builder.Services.AddSingleton<IEventService, EventService>();

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "PartyApp API",
        Version = "v1"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Введите JWT токен"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// CORS для будущего фронтенда
builder.Services.AddCors(options =>
{
    options.AddPolicy("Web", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Применяем миграции при старте
using (var scope = app.Services.CreateScope())
{
    Directory.CreateDirectory("App_Data");

    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Seed: создаём определение тоста, если его нет
    if (!db.EventDefinitions.Any(d => d.Type == "quick_checkin"))
    {
        db.EventDefinitions.Add(new PartyApp.Domain.Entities.EventDefinition
        {
            Type = "quick_checkin",
            DisplayName = "Тост за именинника",
            Description = "Скажи тост и получи балл! Кнопка блокируется, пока кто-то говорит.",
            ConfigJson = "{\"points\":1,\"cooldownSeconds\":60}",
            Availability = PartyApp.Domain.Enums.AvailabilityMode.Manual,
            IsActive = true,
            CreatedById = null
        });
    }
    
    // Seed: создаём определение промокодов, если его нет
    if (!db.EventDefinitions.Any(d => d.Type == "promo_code"))
    {
        db.EventDefinitions.Add(new PartyApp.Domain.Entities.EventDefinition
        {
            Type = "promo_code",
            DisplayName = "Промокоды",
            Description = "Именинник называет код вслух — введи его здесь и получи баллы!",
            ConfigJson = "{\"codes\":[\"СДР2025\",\"ВЕЧЕРИНКА\",\"АЛЕКСЕЙ\"],\"pointsPerCode\":15,\"oneTimePerPlayer\":true}",
            Availability = PartyApp.Domain.Enums.AvailabilityMode.Manual,
            IsActive = true,
            CreatedById = null
        });
    }
    
    // Seed: создаём определение квиза, если его нет
    if (!db.EventDefinitions.Any(d => d.Type == "quiz"))
    {
        db.EventDefinitions.Add(new PartyApp.Domain.Entities.EventDefinition
        {
            Type = "quiz",
            DisplayName = "Квиз про именинника",
            Description = "Ответь на вопросы правильно и получи баллы!",
            ConfigJson = """
                         {
                             "timeLimitSec": 30,
                             "pointsPerCorrect": 10,
                             "questions": [
                                 {
                                     "text": "В каком городе родился именинник?",
                                     "options": ["Москва", "Санкт-Петербург", "Тайшет", "Новосибирск"],
                                     "correctIndex": 2
                                 },
                                 {
                                     "text": "Любимый напиток именинника?",
                                     "options": ["Пиво", "Вино", "Виски", "Коктейль"],
                                     "correctIndex": 0
                                 },
                                 {
                                     "text": "Кем работает именинник?",
                                     "options": ["Программист", "Дизайнер", "Менеджер", "Маркетолог"],
                                     "correctIndex": 0
                                 }
                             ]
                         }
                         """,
            Availability = PartyApp.Domain.Enums.AvailabilityMode.Manual,
            IsActive = true,
            CreatedById = null
        });
    }
    
    if (!db.EventDefinitions.Any(d => d.Type == "word_rush"))
    {
        db.EventDefinitions.Add(new PartyApp.Domain.Entities.EventDefinition
        {
            Type = "word_rush",
            DisplayName = "Слова с буквами А и Е",
            Description = "Вписывай слова, содержащие буквы А и Е. Только настоящие русские слова!",
            ConfigJson = """
                         {
                             "timeLimitSec": 60,
                             "requiredLetters": ["А", "Е"],
                             "minWordLength": 3,
                             "pointsPerWord": 5,
                             "uniqueWordsOnly": true
                         }
                         """,
            Availability = PartyApp.Domain.Enums.AvailabilityMode.Manual,
            IsActive = true,
            CreatedById = null
        });
    }
    
    // Seed: создаём определение QR-сканирования, если его нет
    if (!db.EventDefinitions.Any(d => d.Type == "qr_scan"))
    {
        db.EventDefinitions.Add(new PartyApp.Domain.Entities.EventDefinition
        {
            Type = "qr_scan",
            DisplayName = "Охота за QR-кодами",
            Description = "Найди QR-коды, спрятанные по коттеджу, и получи баллы!",
            ConfigJson = "{}",
            Availability = PartyApp.Domain.Enums.AvailabilityMode.Manual,
            IsActive = true,
            CreatedById = null
        });
    }
    
    db.SaveChanges();
}

// Обработка исключений
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Swagger в development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Web");

// Статические файлы (для тестовой страницы SignalR).
// HTML не кэшируем: после деплоя новая версия SPA должна подхватываться сразу
void DisableHtmlCache(StaticFileResponseContext context)
{
    if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = DisableHtmlCache });
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = DisableHtmlCache });

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Применяем миграции при старте
using (var scope = app.Services.CreateScope())
{
    Directory.CreateDirectory("App_Data");

    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Health
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Endpoints
app.MapToastEndpoints();
app.MapAuthEndpoints();
app.MapNotificationsEndpoints();
app.MapPushEndpoints();
app.MapEventsEndpoints();
app.MapQrEndpoints();
app.MapAdminEndpoints();
app.MapWalletEndpoints();
app.MapSpyGameEndpoints();
app.MapPhotoEndpoints();
app.MapSubmissionEndpoints();
app.MapScreenEndpoints();

// SignalR
app.MapHub<PartyHub>("/hubs/party");

app.Run();

// Нужно для интеграционных тестов (WebApplicationFactory<Program>)
public partial class Program { }