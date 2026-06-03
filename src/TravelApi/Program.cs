using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using MassTransit;
using Minio.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using System.Threading.RateLimiting;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Domain.Options;
using TravelApi.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Hangfire;
using Hangfire.PostgreSql;
using TravelApi.Filters;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Contracts.Auth;
using TravelApi.Application.Ai;
using TravelApi.Infrastructure.Ai;

using TravelApi.Authorization;
using TravelApi.Infrastructure.Authorization;
using TravelApi.Infrastructure.Logging;
using TravelApi.Hubs;
using TravelApi.Services;
using TravelApi.Errors;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    // C21: rolling diario + retencion de 14 archivos + tope de 50MB por archivo.
    // Ojo: la rotacion solo controla cuanto disco se usa; los logs siguen pudiendo
    // contener datos sensibles, por eso ademas estan ignorados por gitignore.
    .WriteTo.File(
        "logs/log-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50L * 1024L * 1024L,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Sink(new SignalRSink())
    .CreateLogger();




try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog(); // Use Serilog for logging

    // ADR-016 F0a: lee un entero de config probando primero la forma con ':' y luego la
    // forma con '__' (variables de entorno), igual que el patron de secretos del repo.
    // Si no esta o no parsea, devuelve el default. Evita repetir el doble-lookup + TryParse
    // en cada setting numerico del cerebro de IA.
    static int ReadIntConfig(IConfiguration configuration, string colonKey, string envKey, int defaultValue)
    {
        var raw = configuration[colonKey] ?? configuration[envKey];
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    static bool IsPlaceholderSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Contains("CHANGE_THIS_SECRET", StringComparison.OrdinalIgnoreCase)
            || value.Contains("change_this", StringComparison.OrdinalIgnoreCase)
            || value.Contains("travelpass", StringComparison.OrdinalIgnoreCase);
    }

    if (builder.Environment.IsProduction())
    {
        var jwtKey = builder.Configuration["Jwt:Key"] ?? builder.Configuration["Jwt__Key"];
        var webhookSecret = builder.Configuration["WhatsApp:WebhookSecret"] ?? builder.Configuration["WhatsApp__WebhookSecret"];
        var metricsToken = builder.Configuration["Metrics:Token"] ?? builder.Configuration["METRICS_TOKEN"];
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        if (IsPlaceholderSecret(jwtKey) || IsPlaceholderSecret(webhookSecret) || IsPlaceholderSecret(metricsToken) || IsPlaceholderSecret(connectionString))
        {
            throw new InvalidOperationException(
                "Production startup blocked because placeholder secrets or default credentials are still configured.");
        }
    }

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddSignalR();

builder.Services.AddExceptionHandler<TravelApi.Middleware.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("CatalogCache", builder =>
        builder.Expire(TimeSpan.FromHours(24)).Tag("catalog"));
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});
builder.Services.AddAutoMapper(typeof(Program), typeof(TravelApi.Application.Mappings.MappingProfile));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Ingresa el token JWT con el formato: Bearer {token}"
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// FC1 (review BR3, 2026-05-14): interceptor que traduce CHECK violations
// de Postgres (SqlState 23514) a BusinessInvariantViolationException -> HTTP 409
// con mensaje en espanol via GlobalExceptionHandler. Stateless, scoped junto al
// DbContext para asegurar que se enganche en todos los SaveChangesAsync.
builder.Services.AddSingleton<BusinessInvariantInterceptor>();

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(connectionString, o =>
    {
        o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
    });
    options.AddInterceptors(sp.GetRequiredService<BusinessInvariantInterceptor>());
});

// C17: MassTransit + EntityFramework Outbox.
// Migrado desde el ex sub-servicio TravelReservations.Api (eliminado en Fase A C17).
// Las tablas Inbox/Outbox ya estan declaradas en AppDbContext.OnModelCreating.
//
// Gating por env var MassTransit__Enabled:
//  - docker-compose `worker` (y opcionalmente `api`) deben setearla en "true".
//  - Tests / desarrollo local sin Rabbit la dejan sin setear -> bus NO se registra
//    y el host levanta normalmente. Esto evita que el smoke test intente abrir
//    una conexion a RabbitMQ.
//
// Hoy no hay consumers ni publishers en el codigo (verificado: cero IConsumer,
// cero Publish/Send). El bus queda registrado para no perder la infra que ya
// existia en el sub-servicio, sin habilitar comportamiento nuevo.
var massTransitEnabled = builder.Configuration.GetValue("MassTransit:Enabled", false);
if (massTransitEnabled)
{
    builder.Services.AddMassTransit(x =>
    {
        x.AddEntityFrameworkOutbox<AppDbContext>(o =>
        {
            o.UsePostgres();
            o.UseBusOutbox();
        });

        x.UsingRabbitMq((context, cfg) =>
        {
            var host = builder.Configuration["RABBITMQ_HOST"] ?? "localhost";
            var user = builder.Configuration["RABBITMQ_USER"] ?? "guest";
            var pass = builder.Configuration["RABBITMQ_PASSWORD"] ?? "guest";

            cfg.Host(host, "/", h =>
            {
                h.Username(user);
                h.Password(pass);
            });

            cfg.ConfigureEndpoints(context);
        });
    });
}

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>();
        if (jwtOptions is null)
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
        {
            throw new InvalidOperationException(
                "JWT key must be at least 32 characters (256 bits). Update Jwt__Key in environment variables.");
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (!string.IsNullOrWhiteSpace(context.Token))
                {
                    return Task.CompletedTask;
                }

                // /hangfire prefiere la cookie efimera "Hangfire", pero cae al "Access"
                // regular si no esta disponible. Esto evita que el dashboard quede en
                // "Preparando acceso seguro..." si la cookie efimera no se creo.
                if (context.Request.Path.StartsWithSegments("/hangfire"))
                {
                    if (context.Request.Cookies.TryGetValue(AuthCookieNames.Hangfire, out var hangfireToken))
                    {
                        context.Token = hangfireToken;
                        return Task.CompletedTask;
                    }
                    if (context.Request.Cookies.TryGetValue(AuthCookieNames.Access, out var fallbackAccess))
                    {
                        context.Token = fallbackAccess;
                    }
                    return Task.CompletedTask;
                }

                if (context.Request.Cookies.TryGetValue(AuthCookieNames.Access, out var accessToken))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// B1.15 Fase 1: infra de autorizacion por permisos. Los attributes existen
// pero NINGUN endpoint los usa todavia (la migracion de controllers es Fase 2).
// FallbackPolicy/DefaultPolicy se mantienen sin cambios.
//
// Scopes:
//  - PolicyProvider: Singleton (no depende de servicios scoped).
//  - Handler: Scoped (consume IUserPermissionResolver que es Scoped por DbContext).
//  - Resolvers: Scoped (consumen AppDbContext y UserManager).
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddScoped<IUserPermissionResolver, UserPermissionResolver>();
builder.Services.AddScoped<IOwnershipResolver, OwnershipResolver>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            message = "Demasiadas solicitudes. Intenta nuevamente en unos minutos."
        }, cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("auth", context =>
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("webhooks", context =>
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 45,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("uploads", context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? context.Connection.RemoteIpAddress?.ToString()
                     ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("public-leads", context =>
    {
        var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("afip", context =>
    {
        var isAuth = context.User.Identity?.IsAuthenticated == true;
        if (isAuth) return RateLimitPartition.GetNoLimiter<string>("no-limit");

        var partitionKey = $"{context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous"}:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("fiscal", context =>
    {
        var isAuth = context.User.Identity?.IsAuthenticated == true;
        if (isAuth) return RateLimitPartition.GetNoLimiter<string>("no-limit");
        
        var partitionKey = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? context.Connection.RemoteIpAddress?.ToString()
                         ?? "unknown";
                         
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 50,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<IAfipService, AfipService>();

// ============================================================
// ADR-016 F0a — Cerebro del copiloto de IA (detras del flag EnableAiCopilot, default OFF).
//
// IMPORTANTE: registrar estos servicios NO los invoca. Con el flag OFF, nadie llama a
// IAiAssistantService, asi que el cerebro queda inerte y el comportamiento es byte-identico.
// El arranque NO hace NINGUNA llamada a la IA (no hay hosted service ni warmup aca).
//
// La config del proveedor (base_url, key, modelo, timeout...) se lee de variables de entorno
// con el patron del repo (["Ai:X"] ?? ["Ai__X"]). La API KEY es un SECRETO: solo por env,
// nunca a la DB, nunca logueada. Cambiar de proveedor = editar .env + restart, cero codigo
// (la unica implementacion del provider es OpenAI-compatible y esta 100% parametrizada).
// ============================================================
var aiConnectionOptions = new AiConnectionOptions
{
    // Default del modelo VOLATIL documentado en .env.example; aca solo damos un fallback inerte.
    BaseUrl = builder.Configuration["Ai:BaseUrl"] ?? builder.Configuration["Ai__BaseUrl"] ?? string.Empty,
    ApiKey = builder.Configuration["Ai:ApiKey"] ?? builder.Configuration["Ai__ApiKey"] ?? string.Empty,
    Model = builder.Configuration["Ai:Model"] ?? builder.Configuration["Ai__Model"] ?? string.Empty,
    TimeoutSeconds = ReadIntConfig(builder.Configuration, "Ai:TimeoutSeconds", "Ai__TimeoutSeconds", defaultValue: 15),
    MaxTokens = ReadIntConfig(builder.Configuration, "Ai:MaxTokens", "Ai__MaxTokens", defaultValue: 512),
    MaxRetries = ReadIntConfig(builder.Configuration, "Ai:MaxRetries", "Ai__MaxRetries", defaultValue: 2),
};
// Singleton: la config no cambia en runtime (cambiar de proveedor implica restart, por diseno).
builder.Services.AddSingleton(aiConnectionOptions);
// Typed HttpClient para el provider (mismo patron que IAfipService). El timeout efectivo lo
// controla el provider por llamada via CancellationToken; el del HttpClient queda holgado.
builder.Services.AddHttpClient<IAiChatProvider, OpenAiCompatibleChatProvider>();
builder.Services.AddScoped<IAiAssistantService, AiAssistantService>();
builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IApprovalRequestService, ApprovalRequestService>();

// FC1.2.1 v3 (MR-V2-02, 2026-05-17) + FC1.3.2 (ADR-009 §2.7, 2026-05-21) —
// BookingCancellationService implementa TRES interfaces:
//   - IBookingCancellationService (API publica que llaman controllers).
//   - IInvoiceAnnulmentBcBridge (interface chica de 2 metodos que InvoiceService
//     inyecta para sincronizar el BC post-CAE de AFIP).
//   - IPartialCreditNoteApprovalBridge (interface chica de 2 metodos que
//     ApprovalRequestService inyecta para sincronizar el BC despues de aprobar
//     o rechazar un PartialCreditNoteApproval). Stubs en FC1.3.2, logica real FC1.3.3.
//
// Sin este split, los services hermanos (InvoiceService, ApprovalRequestService)
// tendrian que inyectar IBookingCancellationService completo y se abriria un
// ciclo DI bidireccional (BC tambien inyecta esos servicios). El resolver
// detecta el ciclo al startup y aborta ("Scoped circular reference"). Con el
// split, el ciclo queda solo logico (uno llama al otro en runtime via los
// callbacks) pero NO en el grafo de tipos del DI container.
//
// Registramos la clase concreta una vez + las tres interfaces como factory que
// resuelve la MISMA instancia dentro del scope. Es critico que sea la misma
// instancia: comparten AppDbContext y ChangeTracker, asi los callbacks ven los
// cambios commiteados por el flujo principal. Si fueran instancias distintas,
// cada una tendria su propio tracker y los reads no verian los writes recientes.
builder.Services.AddScoped<BookingCancellationService>();
builder.Services.AddScoped<IBookingCancellationService>(sp =>
    sp.GetRequiredService<BookingCancellationService>());
builder.Services.AddScoped<IInvoiceAnnulmentBcBridge>(sp =>
    sp.GetRequiredService<BookingCancellationService>());
builder.Services.AddScoped<IPartialCreditNoteApprovalBridge>(sp =>
    sp.GetRequiredService<BookingCancellationService>());

// FC1.2.2 (2026-05-18) — OperatorRefundService gestiona los ingresos del operador
// (T2 del flujo) + la matriz fiscal Mono/RI + las allocations N:M con retry xmin.
// Depende de IBookingCancellationService (callbacks On*Async) y de IClientCreditService
// (crea ClientCreditEntry al imputar el net amount). Sin dependencias circulares
// porque los 3 services hablan en una sola direccion: OperatorRefund -> BC + CC.
builder.Services.AddScoped<IOperatorRefundService, OperatorRefundService>();

// FC1.2.2 (2026-05-18) — ClientCreditService stub minimo en FC1.2.2 (solo
// CreateEntryAsync). La implementacion completa con WithdrawAsync llega en FC1.2.3.
builder.Services.AddScoped<IClientCreditService, ClientCreditService>();

// FC1.3.1 (ADR-009 §2.6, 2026-05-21) — clasificador fiscal puro de NC parcial.
// Service stateless sin dependencias de DbContext: recibe entidades pre-cargadas
// + settings y devuelve el DTO transitorio. El caller (BookingCancellationService
// en sub-fase FC1.3.3) lo invoca para decidir auto-emite vs manual review vs
// rechaza (TotalPlusNewInvoice por GR-001).
builder.Services.AddScoped<IFiscalLiquidationCalculator, FiscalLiquidationCalculator>();

// FC1.3.3 (ADR-009 §2.3.4.bis N-002, 2026-05-21) — service chico que cuenta
// admins activos para la regla GR-005 (bypass 4-ojos en agencias de 1 sola
// persona). Existe como interface dedicada para evitar que el BC tenga que
// inyectar UserManager directamente (mockearlo en tests requiere 8+ deps).
builder.Services.AddScoped<IAdminUserCountService, AdminUserCountService>();

// FC1.3 Fase 3 (ADR-010 R1, 2026-05-29) — evaluador compartido de la regla GR-005
// (bypass de 4-ojos cuando hay un solo admin). Extraido del metodo privado que vivia
// en BookingCancellationService para que el cierre de la bandeja de reconciliacion
// use exactamente la misma evaluacion (DRY). Depende solo de IAdminUserCountService.
builder.Services.AddScoped<IFourEyesBypassEvaluator, FourEyesBypassEvaluator>();

// FC1.3 Fase 3 (ADR-010, 2026-05-29) — bandeja de reconciliacion de NC parciales con
// recibos vivos. Lista + cierra casos. La creacion del caso vive en AfipService
// (transaccional con el Payment reversal), no aca.
builder.Services.AddScoped<IPartialCreditNoteReconciliationService, PartialCreditNoteReconciliationService>();

builder.Services.AddScoped<IApprovalPolicyService, ApprovalPolicyService>();
builder.Services.AddScoped<IMovementsService, MovementsService>();
builder.Services.AddScoped<ISupplierService, SupplierService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IBnaExchangeRateService, BnaExchangeRateService>();
builder.Services.AddScoped<IServicioReservaService, ServicioReservaService>();
builder.Services.AddScoped<ICommissionService, CommissionService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IOperationalFinanceSettingsService, OperationalFinanceSettingsService>();
builder.Services.AddScoped<ITreasuryService, TreasuryService>();
builder.Services.AddScoped<OperationalFinanceMonitorService>();
builder.Services.AddScoped<TravelApi.Infrastructure.Services.ReservaLifecycleAutomationService>();
// FC1.3.6 (ADR-009 §2.10, 2026-05-21): job que alerta a Admins cuando un BC
// queda mucho tiempo en ManualReviewPending (riesgo plazo RG 4540 fiscal).
builder.Services.AddScoped<TravelApi.Infrastructure.Services.PartialCreditNoteReviewAlertJob>();

// FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): job que reconcilia approvals
// resueltos cuyo BC quedo huerfano en ManualReviewPending. Reaplica el callback
// del bridge con anti-spam (max N reintentos, una notificacion al limite).
builder.Services.AddScoped<TravelApi.Infrastructure.Services.PartialCreditNoteBridgeReconciliationJob>();

// FC1.3.F2.6a (plan tactico Fase 2 §FC1.3.F2.6a, 2026-05-28): job que reconcilia NC
// PARCIALES colgadas en Resultado='PENDING' (el POST a ARCA se encolo pero el resultado
// nunca se persistio por crash/timeout). Consulta ARCA y reconcilia o escala a manual.
// No-op si EnablePartialCreditNotes=false.
builder.Services.AddScoped<TravelApi.Infrastructure.Services.PartialCreditNotePostingReconciliationJob>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IRateService, RateService>();
builder.Services.AddScoped<ICountryService, CountryService>();
builder.Services.AddScoped<IDestinationService, DestinationService>();
builder.Services.AddScoped<ICatalogCacheInvalidator, CatalogCacheInvalidator>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<INotificationRealtimeDispatcher, SignalRNotificationDispatcher>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IEntityReferenceResolver, EntityReferenceResolver>();
builder.Services.AddScoped<ISensitiveDataProtector, SensitiveDataProtector>();
builder.Services.AddScoped<ICatalogPackageService, CatalogPackageService>();
builder.Services.AddScoped<IWhatsAppBotConfigService, WhatsAppBotConfigService>();
builder.Services.AddScoped<IWhatsAppConversationService, WhatsAppConversationService>();
builder.Services.AddScoped<IWhatsAppWebhookService, WhatsAppWebhookService>();
builder.Services.AddScoped<IWhatsAppGateway, WhatsAppGateway>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<IFileStoragePort, MinioFileStoragePort>();
builder.Services.AddSingleton<InternalMetricsService>();

var realtimeHostedServicesEnabled = builder.Configuration.GetValue("HostedServices:RealtimeEnabled", true);
if (realtimeHostedServicesEnabled)
{
    builder.Services.AddHostedService<LogStreamingService>();
    builder.Services.AddHostedService<BotLogMonitorService>();
}

// Pilar 1: Cotizador + CRM + Vouchers
builder.Services.AddScoped<IQuoteService, QuoteService>();
builder.Services.AddScoped<ILeadService, LeadService>();
builder.Services.AddScoped<IWhatsAppDeliveryService, WhatsAppDeliveryService>();

// C17 Fase A: el sub-servicio TravelReservations.Api fue eliminado.
// Las dependencias de reservas se resuelven siempre in-process contra AppDbContext.
builder.Services.AddScoped<IReservaService, ReservaService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<ITimelineService, TimelineService>();
builder.Services.AddScoped<IVoucherService, VoucherService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();

builder.Services.AddMinio(options =>
{
    options.Endpoint = builder.Configuration["Minio:Endpoint"] ?? builder.Configuration["MINIO_ENDPOINT"] ?? "localhost:9000";
    options.AccessKey = builder.Configuration["Minio:AccessKey"] ?? builder.Configuration["MINIO_ACCESS_KEY"] ?? "minioadmin";
    options.SecretKey = builder.Configuration["Minio:SecretKey"] ?? builder.Configuration["MINIO_SECRET_KEY"] ?? "minioadmin";
});

// Load allowed origins from configuration (appsettings.json or ENV)
var allowedOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>();

if (allowedOrigins.Length == 0)
{
    Log.Warning("No CORS origins configured. API might be inaccessible from browser clients.");
}
else
{
    Log.Information("Allowed CORS Origins: {Origins}", string.Join(", ", allowedOrigins));
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithExposedHeaders("Content-Disposition");
        }
    });
});

// Hangfire Configuration (PostgreSQL)
var jobStorageConnectionString = builder.Configuration.GetConnectionString("JobStorageConnection")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(jobStorageConnectionString));

var hangfireServerEnabled = builder.Configuration.GetValue("Hangfire:ServerEnabled", true);
if (hangfireServerEnabled)
{
    builder.Services.AddHangfireServer();
}

var app = builder.Build();
GlobalJobFilters.Filters.Add(new HangfireMetricsFilter(app.Services.GetRequiredService<InternalMetricsService>()));

if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler(opt => { }); // Use GlobalExceptionHandler

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException)
    {
        // Don't log as error, just return 499 (Client Closed Request)
        context.Response.StatusCode = 499;
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled exception");
        throw;
    }
});

var migrateOnly = args.Any(arg => string.Equals(arg, "--migrate-only", StringComparison.OrdinalIgnoreCase));
var applyMigrationsOnStartup = builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", !app.Environment.IsProduction());

// In production migrations run from the dedicated migrate command/service before the API starts serving traffic.
if (migrateOnly || applyMigrationsOnStartup)
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            app.Logger.LogInformation("Bootstrapping operational finance schema via raw SQL...");
            await OperationalFinanceSchemaBootstrapper.EnsureAsync(db);
            await OperationalFinanceSchemaBootstrapper.MarkOperationalFinanceMigrationAsAppliedAsync(db);
            app.Logger.LogInformation("Operational finance bootstrap finished.");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning("Operational finance bootstrap skipped or failed: {Message}", ex.Message);
        }

        try
        {
            app.Logger.LogInformation("Bootstrapping refresh token schema via raw SQL...");
            await RefreshTokenSchemaBootstrapper.EnsureAsync(db);
            await RefreshTokenSchemaBootstrapper.MarkRefreshTokenMigrationAsAppliedAsync(db);
            app.Logger.LogInformation("Refresh token bootstrap finished.");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning("Refresh token bootstrap skipped or failed: {Message}", ex.Message);
        }

        try
        {
            app.Logger.LogInformation("Bootstrapping BNA exchange rate snapshot schema via raw SQL...");
            await BnaExchangeRateSchemaBootstrapper.EnsureAsync(db);
            app.Logger.LogInformation("BNA exchange rate snapshot bootstrap finished.");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning("BNA exchange rate snapshot bootstrap skipped or failed: {Message}", ex.Message);
        }

        int retries = 5;
        while (retries > 0)
        {
            try
            {
                app.Logger.LogInformation("Applying EF Core Migrations (Attempts remaining: {Retries})...", retries);
                await db.Database.MigrateAsync();
                app.Logger.LogInformation("EF Core Migrations applied successfully.");
                break;
            }
            catch (Exception ex)
            {
                retries--;
                if (retries == 0)
                {
                    app.Logger.LogError(ex, "CRITICAL: FAILED TO APPLY EF CORE MIGRATIONS AFTER MULTIPLE ATTEMPTS");
                    throw;
                }
                else
                {
                    app.Logger.LogWarning("Migration failed, retrying in 5 seconds... Error: {Message}", ex.Message);
                    await Task.Delay(5000);
                }
            }
        }
    }

    if (migrateOnly)
    {
        app.Logger.LogInformation("Migration-only command completed. Exiting without starting HTTP server.");
        return;
    }
}
else
{
    app.Logger.LogInformation("Database migrations skipped on startup. Run `dotnet TravelApi.dll --migrate-only` before deploy.");
}

// 1. Forwarded Headers (CRITICAL for Nginx Reverse Proxy) - MUST BE FIRST
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// 2. CORS (MUST be before any other middleware that responds or sets headers)
app.UseCors("web");

if (app.Environment.IsProduction())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'self'; frame-ancestors 'self'; object-src 'none'; " +
        "img-src 'self' data: blob: https:; style-src 'self' 'unsafe-inline' https:; font-src 'self' data: https:; " +
        "script-src 'self' https:; connect-src 'self' https: ws: wss:;";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
    await next();
});

app.UseSerilogRequestLogging();

app.UseResponseCompression();
app.UseOutputCache();

app.UseRouting();
app.UseMiddleware<InternalMetricsMiddleware>();
app.UseCors("web");
app.UseAuthentication();
app.UseMiddleware<TravelApi.Middleware.CookieCsrfMiddleware>();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new TravelApi.Filters.HangfireAuthorizationFilter() } 
});

var hangfireSchedulerEnabled = app.Configuration.GetValue("Hangfire:SchedulerEnabled", hangfireServerEnabled);
if (hangfireSchedulerEnabled)
{
    RecurringJob.AddOrUpdate<OperationalFinanceMonitorService>(
        "upcoming-unpaid-reservas",
        service => service.GenerateUpcomingUnpaidReservationNotificationsAsync(),
        Cron.Daily());

    // Lifecycle automation: promueve Reserved->Operational cuando arranca el viaje (o esta cobrado)
    // y cierra Operational->Closed al dia siguiente del EndDate.
    // Corre temprano (3am UTC) para que cuando el agente abre el sistema en la manana ya este aplicado.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.ReservaLifecycleAutomationService>(
        "reserva-lifecycle-automation",
        service => service.RunDailyAsync(CancellationToken.None),
        Cron.Daily(3));

    // FC1.3.6 (ADR-009 §2.10, 2026-05-21): chequeo diario de BCs trabados en
    // ManualReviewPending. Corre 8am UTC para que la alerta caiga apenas el
    // admin entra al sistema (no a las 3am cuando nadie esta mirando).
    // El job es no-op si EnablePartialCreditNotes=false.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.PartialCreditNoteReviewAlertJob>(
        "partial-credit-note-review-alert",
        job => job.RunAsync(CancellationToken.None),
        "0 8 * * *");

    // FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): reconciliacion bridge cada
    // 30 min. La "ventana de gracia" para considerar un approval staleness se
    // controla por setting BridgeReconciliationStalenessMinutes (default 30
    // tambien) — la cron y el setting estan desacoplados a proposito: la cron
    // dispara el job, el filtro de antiguedad evita re-disparar callbacks frescos.
    // Es no-op si EnablePartialCreditNotes=false.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.PartialCreditNoteBridgeReconciliationJob>(
        "partial-credit-note-bridge-reconciliation",
        job => job.RunAsync(CancellationToken.None),
        "*/30 * * * *");

    // FC1.3.F2.6a (plan tactico Fase 2 §FC1.3.F2.6a, 2026-05-28): reconciliacion del
    // POSTING de NC parciales colgadas en PENDING. Misma cron que el job bridge (cada 30
    // min); la ventana de "staleness" para considerar una NC colgada corre EN LA QUERY del
    // job (setting IdempotencyKeyStaleThresholdMinutes), no en la cron. No-op si
    // EnablePartialCreditNotes=false.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.PartialCreditNotePostingReconciliationJob>(
        "partial-credit-note-posting-reconciliation",
        job => job.RunAsync(CancellationToken.None),
        "*/30 * * * *");
}

// 3. Health Check
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (AppDbContext dbContext, InternalMetricsService metrics, CancellationToken cancellationToken) =>
{
    try
    {
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            metrics.SetDatabaseReady(false);
            return Results.Json(
                DatabaseExceptionClassifier.CreateProblemDetails(),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pendingMigrations.Any())
        {
            metrics.SetDatabaseReady(false);
            return Results.Json(new
            {
                status = "unready",
                code = "database_not_ready",
                pendingMigrations = pendingMigrations.ToArray()
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Verificar MinIO
        var endpoint = builder.Configuration["Minio:Endpoint"] ?? builder.Configuration["MINIO_ENDPOINT"] ?? "localhost:9000";
        try
        {
            var minioClient = app.Services.GetRequiredService<Minio.IMinioClient>();
            var minioBucket = builder.Configuration["Minio:BucketName"] ?? "reservations";
            await minioClient.BucketExistsAsync(new Minio.DataModel.Args.BucketExistsArgs().WithBucket(minioBucket), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL] MinIO connectivity check failed for endpoint '{endpoint}': {ex.Message}");
            return Results.Json(new
            {
                status = "unready",
                storage = "unavailable",
                endpoint = endpoint,
                error = ex.Message
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        metrics.SetDatabaseReady(true);
        return Results.Ok(new { status = "ready", storage = "connected", endpoint = endpoint });
    }
    catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
    {
        metrics.SetDatabaseReady(false);
        return Results.Json(
            DatabaseExceptionClassifier.CreateProblemDetails(app.Environment.IsDevelopment() ? ex.Message : null),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();
app.MapGet("/internal/metrics", (HttpContext context, IConfiguration configuration, IWebHostEnvironment environment, InternalMetricsService metrics) =>
{
    var configuredToken = configuration["Metrics:Token"] ?? configuration["METRICS_TOKEN"];
    var providedToken = context.Request.Headers["X-Metrics-Token"].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(configuredToken) || IsPlaceholderSecret(configuredToken))
    {
        return environment.IsProduction()
            ? Results.NotFound()
            : Results.Text(metrics.RenderPrometheus(), "text/plain; version=0.0.4; charset=utf-8");
    }

    if (!string.Equals(configuredToken, providedToken, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    return Results.Text(metrics.RenderPrometheus(), "text/plain; version=0.0.4; charset=utf-8");
}).AllowAnonymous();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
    // Seed roles
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roles = new[] { "Admin", "Colaborador", "Vendedor" };
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }

    // Ensure at least one Admin exists
    var admins = await userManager.GetUsersInRoleAsync("Admin");
    if (admins.Count == 0)
    {
        var firstUser = await userManager.Users.OrderBy(user => user.Id).FirstOrDefaultAsync();
        if (firstUser is not null)
        {
            await userManager.AddToRoleAsync(firstUser, "Admin");
            app.Logger.LogInformation("Seeded 'Admin' role to user {Email}", firstUser.Email);
        }
    }
}

// =========================================================================
// FC1.3.2 (ADR-009 §2.10, 2026-05-21) — startup defense-in-depth (GR-002)
// =========================================================================
//
// Hay tres lugares donde la combinacion EnablePartialCreditNotes=true +
// EnableNewCancellationFlow=false puede ser invalida:
//   1) Runtime: OperationalFinanceSettingsService.UpdateAsync ya rechaza el
//      guardado con ValidationException (canonico).
//   2) DTO request: estos flags FC1.2/FC1.3 NO se exponen en
//      OperationalFinanceSettingsDto (se manejan via SQL/seed/migration). El
//      proyecto NO usa FluentValidation registrado — para los settings que SI
//      se exponen (rangos numericos, % de descuento, etc.) usamos
//      DataAnnotations [Range] consistente con el resto del DTO. La cross-field
//      rule de los flags vive en el service (canonico).
//   3) Startup (este bloque): ultima red de seguridad. Si la BD llego a la
//      combinacion invalida por restore de backup, UPDATE manual, escritura
//      legacy o cualquier camino que se saltee el service, la app no arranca.
//
// Tambien aprovechamos este scope para RH-013: si FC1.3 esta prendido pero
// nadie seteo Fc13DeployDate, auto-set a UtcNow + warning. La heuristica
// "factura legacy" del clasificador depende de esa fecha.
//
// El scope es independiente del scope de seed de roles para que el read del
// service no se confunda con el ChangeTracker que viene de seed users.
using (var startupValidationScope = app.Services.CreateScope())
{
    var settingsService = startupValidationScope.ServiceProvider
        .GetRequiredService<IOperationalFinanceSettingsService>();
    var settings = await settingsService.GetEntityAsync(CancellationToken.None);

    if (settings.EnablePartialCreditNotes && !settings.EnableNewCancellationFlow)
    {
        // Pre-condicion GR-002 incumplida. Tiramos InvalidOperationException
        // dentro del bloque catch externo -> Log.Fatal -> proceso termina.
        // El operador tiene que decidir: o apaga FC1.3, o prende FC1.2 antes
        // del proximo arranque.
        throw new InvalidOperationException(
            "Configuracion invalida: EnablePartialCreditNotes=true requiere " +
            "EnableNewCancellationFlow=true (GR-002). " +
            "Apague FC1.3 o prenda FC1.2 antes de arrancar. " +
            "El runtime UpdateAsync ya rechaza esta combinacion: si llegaste aca, " +
            "hubo UPDATE manual a BD, restore de backup o escritura por fuera del service. " +
            "Loguea el escenario para revisar como llegaron los settings a este estado.");
    }

    // ============================================================
    // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.0, 2026-05-22): mismo patron
    // GR-002 pero encadenado para los dos flags nuevos de Fase 2. Ultima red de
    // seguridad: si la BD llego a la combinacion invalida por restore de backup,
    // UPDATE manual o cualquier camino que se saltee el service, la app NO arranca.
    // ============================================================

    // Fase 2 (emision real ARCA) depende de Fase 1 (clasificador).
    if (settings.EnablePartialCreditNoteRealEmission && !settings.EnablePartialCreditNotes)
    {
        throw new InvalidOperationException(
            "Configuracion invalida: EnablePartialCreditNoteRealEmission=true requiere " +
            "EnablePartialCreditNotes=true (FC1.3 Fase 2 depende de Fase 1). " +
            "Apague Fase 2 o prenda Fase 1 antes de arrancar. " +
            "El runtime UpdateAsync ya rechaza esta combinacion: si llegaste aca, " +
            "hubo UPDATE manual a BD, restore de backup o escritura por fuera del service.");
    }

    // Flow dual (caso 4 + 7 auto-procesado) depende del plumbing de emision real Fase 2.
    if (settings.EnableTotalPlusNewInvoiceAutoProcessing && !settings.EnablePartialCreditNoteRealEmission)
    {
        throw new InvalidOperationException(
            "Configuracion invalida: EnableTotalPlusNewInvoiceAutoProcessing=true requiere " +
            "EnablePartialCreditNoteRealEmission=true (el flow dual NC total + factura nueva " +
            "necesita el plumbing de emision real). " +
            "Apague el dual o prenda Fase 2 antes de arrancar. " +
            "El runtime UpdateAsync ya rechaza esta combinacion: si llegaste aca, " +
            "hubo UPDATE manual a BD, restore de backup o escritura por fuera del service.");
    }

    // ADR-013 (2026-06-01): la emision de ND en cancelacion depende del flujo de
    // cancelacion nuevo (la ND se dispara desde el callback de la NC total, que solo
    // existe en el flujo FC1.2). Sin EnableNewCancellationFlow, no hay donde engancharse.
    // Misma red de seguridad que GR-002: si la BD llego a la combinacion invalida por
    // fuera del service, la app NO arranca.
    if (settings.EnableCancellationDebitNote && !settings.EnableNewCancellationFlow)
    {
        throw new InvalidOperationException(
            "Configuracion invalida: EnableCancellationDebitNote=true requiere " +
            "EnableNewCancellationFlow=true (la ND se dispara desde el flujo de cancelacion). " +
            "Apague la ND o prenda el flujo de cancelacion antes de arrancar.");
    }

    // RH-013: si FC1.3 esta prendido pero falta Fc13DeployDate, lo seteamos
    // automaticamente a UtcNow y emitimos warning. El clasificador caso 4
    // (factura legacy / confusa) usa esa fecha para flagear facturas viejas
    // como "revision manual". Sin la fecha, no se puede decidir cual es
    // "antes" y la heuristica queda muda.
    if (settings.EnablePartialCreditNotes && settings.Fc13DeployDate is null)
    {
        var now = DateTime.UtcNow;
        app.Logger.LogWarning(
            "FC1.3 (EnablePartialCreditNotes=true) esta prendido pero Fc13DeployDate=null. " +
            "Auto-set a {Now} para que la heuristica de factura legacy funcione. " +
            "Si esto no era lo esperado, ajusta el setting manualmente.",
            now);

        // Update directo via DbContext: el service de settings hoy no expone un
        // setter especifico para Fc13DeployDate, y agregarlo requeriria tocar
        // DTO + controller + tests (fuera de scope FC1.3.2). El update directo
        // es seguro porque corremos en un scope aislado de startup.
        var dbContext = startupValidationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await dbContext.OperationalFinanceSettings
            .OrderBy(x => x.Id)
            .FirstAsync();
        entity.Fc13DeployDate = now;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync();
    }
}

app.MapControllers();
app.MapHub<LogsHub>("/hubs/logs").RequireAuthorization("AdminOnly");
app.MapHub<NotificationHub>("/hubs/notifications").RequireAuthorization();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application start-up failed");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }

