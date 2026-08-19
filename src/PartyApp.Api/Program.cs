using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

using PartyApp.Api.Common.Middleware;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Auth;
using PartyApp.Api.Modules.Auth.Services;
using PartyApp.Api.Modules.Events;
using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Notifications;
using PartyApp.Api.Modules.Toast;
using PartyApp.Domain.Entities;
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

// Services
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();
builder.Services.AddSingleton<ToastService>();
builder.Services.AddSignalR();

// === Конструктор ивентов ===

// Автоматическая регистрация всех IEventHandler через reflection
var handlerTypes = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => typeof(IEventHandler).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

foreach (var handlerType in handlerTypes)
{
    builder.Services.AddSingleton(typeof(IEventHandler), handlerType);
}

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
        db.SaveChanges();
    }
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

// Статические файлы (для тестовой страницы SignalR)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Применяем миграции при старте
using (var scope = app.Services.CreateScope())
{
    Directory.CreateDirectory("App_Data");

    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Редирект с корня на Swagger
app.MapGet("/", () => Results.Redirect("/swagger"));

// Health
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Endpoints
app.MapToastEndpoints();
app.MapAuthEndpoints();
app.MapNotificationsEndpoints();
app.MapEventsEndpoints();

// SignalR
app.MapHub<PartyHub>("/hubs/party");

app.Run();