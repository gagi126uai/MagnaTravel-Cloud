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
using TravelApi.Infrastructure.Reservations;
using Microsoft.Extensions.Logging;

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

// SEGURIDAD (anti fuga de informacion): el 400 automatico de [ApiController] ante un error de
// model binding/conversion filtra el nombre del tipo .NET interno y el path del campo. Reemplazamos
// la fabrica de esa respuesta por una que devuelve SOLO mensajes amables en espanol, conservando la
// forma (errors{}+title) que el front ya parsea. Ver ApiValidationErrorResponseFactory.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = TravelApi.Errors.ApiValidationErrorResponseFactory.Create;
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
builder.Services.AddAutoMapper(
    _ => { },
    typeof(Program).Assembly,
    typeof(TravelApi.Application.Mappings.MappingProfile).Assembly);
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

// Rate limiting: helper compartido para particionar por la IP real del cliente.
//
// Hallazgo 2026-08-06 (no bloqueante, mismo review de seguridad que el fix de ForwardedHeaders
// de mas arriba): si por algun motivo el middleware no logra determinar una IP (un pedido sin
// conexion TCP real, o una topologia de proxy que cambio), TODOS esos pedidos comparten un
// unico balde "unknown". Es un fallback INTENCIONAL (mejor compartir un balde que no limitar
// nada), pero si pasa seguido en produccion es señal de que algo esta mal (por ejemplo, la
// misma familia de bug que causo el hallazgo B1). Un solo LogWarning (no uno por pedido, para
// no inundar los logs) avisa la primera vez que pasa.
var unknownClientIpWarningLogged = 0;
string ResolveClientIpPartitionKey(HttpContext context)
{
    var remoteIp = context.Connection.RemoteIpAddress?.ToString();
    if (remoteIp is not null)
    {
        return remoteIp;
    }

    if (Interlocked.Exchange(ref unknownClientIpWarningLogged, 1) == 0)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RateLimiter")
            .LogWarning(
                "No se pudo determinar la IP del cliente para el limitador de pedidos; los " +
                "pedidos sin IP comparten un unico balde ('unknown'). Si esto se repite seguido " +
                "en produccion, revisar la configuracion de ForwardedHeaders.");
    }

    return "unknown";
}

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
        var partitionKey = ResolveClientIpPartitionKey(context);
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
        var partitionKey = ResolveClientIpPartitionKey(context);
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    // "auth-refresh" separada de "auth" (2026-08-06, mismo hallazgo que el fix de
    // ForwardedHeaders de arriba): /auth/login es un objetivo de fuerza bruta (alguien
    // probando contraseñas), asi que tiene sentido que sea estricta. /auth/refresh es
    // TODO LO CONTRARIO: no hay nada que adivinar (exige la cookie de refresh, un token
    // aleatorio de 64 bytes ya emitido), y el frontend la llama SOLA, en automatico, cada
    // vez que el token de acceso vence (cada 15 min) o que una pestaña reconecta despues de
    // un corte de red (por ejemplo, el reinicio del contenedor en cada deploy). Compartir el
    // balde de 10 pedidos/5min entre ambas hacia que una sola persona con varias pestañas
    // abiertas (SignalR reconectando + polling de avisos + navegacion) pudiera agotar el
    // balde con trafico 100% legitimo y quedar deslogueada por un 429, no por session invalida.
    options.AddPolicy("auth-refresh", context =>
    {
        var partitionKey = ResolveClientIpPartitionKey(context);
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("webhooks", context =>
    {
        var partitionKey = ResolveClientIpPartitionKey(context);
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
                     ?? ResolveClientIpPartitionKey(context);
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
        var partitionKey = ResolveClientIpPartitionKey(context);
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    // "ai-test" (spec firmada 2026-08-07 §15, M-31): tope de intentos del boton "Probar conexion".
    // Ese boton hace que el SERVIDOR le pegue a una direccion escrita por el usuario, asi que aunque
    // solo entre un Admin, se acota cuantas veces por rato se puede disparar. 12 en 5 minutos alcanza
    // de sobra para configurar (probar, corregir, volver a probar) y no sirve para barrer nada.
    options.AddPolicy("ai-test", context =>
    {
        var partitionKey = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                           ?? ResolveClientIpPartitionKey(context);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    // "ai-line" (spec firmada 2026-08-07 §3, M-20): tope de la caja que entiende lo que escribe el
    // vendedor. Cada llamada gasta cuota del proveedor de inteligencia artificial, asi que se acota
    // POR USUARIO: 40 por minuto es mucho mas de lo que se puede cargar a mano en un minuto y evita
    // que un bucle del navegador (o una pestaña colgada reintentando) se coma la cuota de la agencia.
    options.AddPolicy("ai-line", context =>
    {
        var partitionKey = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                           ?? ResolveClientIpPartitionKey(context);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 40,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("afip", context =>
    {
        var isAuth = context.User.Identity?.IsAuthenticated == true;
        if (isAuth) return RateLimitPartition.GetNoLimiter<string>("no-limit");

        var partitionKey = $"{context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous"}:{ResolveClientIpPartitionKey(context)}";
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
                         ?? ResolveClientIpPartitionKey(context);

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
// Cerebro de la inteligencia artificial (ADR-016 F0a + adenda firmada del 2026-08-07 §15).
//
// IMPORTANTE: registrar estos servicios NO los invoca. El arranque NO hace NINGUNA llamada a la
// IA (no hay hosted service ni warmup aca).
//
// DE DONDE SALE LA CONFIGURACION (cambio del 2026-08-07): la pantalla "Configuracion →
// Inteligencia artificial" MANDA; estas variables de entorno quedan como RESPALDO para las
// instalaciones donde la clave la dejo el tecnico. Quien decide cual gana en cada llamada es
// IAiConnectionResolver, y lo resuelve LEYENDO la base cada vez (nada de config congelada al
// arrancar, nada de cache que quede viejo). Ver la adenda a ADR-016 §11.
//
// El viejo interruptor EnableAiCopilot NO gobierna mas nada (M-33): la unica pregunta es
// "¿hay configuracion de IA utilizable?".
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
// Singleton: es el RESPALDO leido del servidor, y eso si se congela al arrancar (cambiar una
// variable de entorno siempre implico reiniciar). Lo que NO se congela es la eleccion del dueño.
builder.Services.AddSingleton(aiConnectionOptions);
// Revisor de direcciones (anti-SSRF): sin estado, se comparte. Lo usan el probador y el guardado.
builder.Services.AddSingleton<AiEndpointGuard>();
// Scoped porque lee la base (mismo patron que el resto de los servicios que tocan AppDbContext).
builder.Services.AddScoped<IAiConnectionResolver, AiConnectionResolver>();
builder.Services.AddScoped<IAiSettingsService, AiSettingsService>();
// Typed HttpClient para el provider y para el probador (mismo patron que IAfipService). El timeout
// efectivo lo controla cada uno por llamada via CancellationToken; el del HttpClient queda holgado.
//
// DOS CANDADOS que valen para los dos clientes (hallazgo de seguridad de la review, 2026-08-09):
//
//  1. NO SEGUIR REDIRECCIONES. Por default, HttpClient sigue solo un "302 mudate para alla". Eso
//     esquivaria por completo la revision de direccion (AiEndpointGuard): alcanzaba con que un
//     servidor de afuera contestara "seguime a https://169.254.169.254/" para que el servidor
//     terminara pegandole igual a la red interna. Con esto apagado, un 302 es simplemente una
//     respuesta que no es exitosa y ahi termina.
//  2. TACHAR la cabecera Authorization en los logs de HttpClient. Es la cabecera donde viaja la
//     clave del proveedor; sin esto, subir el nivel de log del cliente HTTP la escupiria al archivo.
builder.Services.AddHttpClient<IAiChatProvider, OpenAiCompatibleChatProvider>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .RedactLoggedHeaders(new[] { "Authorization" });
builder.Services.AddHttpClient<IAiConnectionTester, AiConnectionTester>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .RedactLoggedHeaders(new[] { "Authorization" });
builder.Services.AddScoped<IAiAssistantService, AiAssistantService>();

// "La linea inteligente" (spec firmada 2026-08-07 §3, M-20 a M-23 + M-27): el primer consumidor real
// del cerebro. El tiempo de espera es CORTO y propio (el vendedor esta parado frente a la ficha), por
// eso no reusa el timeout general de las llamadas a la IA.
builder.Services.AddSingleton(new ServiceLineInterpretationOptions
{
    TimeoutSeconds = ReadIntConfig(
        builder.Configuration, "Ai:ServiceLineTimeoutSeconds", "Ai__ServiceLineTimeoutSeconds", defaultValue: 8),
});
// Cache de respuestas de la linea inteligente (obra "prompt mas barato", 2026-08-10): Singleton
// porque es una cache en memoria de todo el proceso, no algo por-pedido (ver ServiceLineInterpretationCache).
builder.Services.AddSingleton<ServiceLineInterpretationCache>();
builder.Services.AddScoped<IServiceLineInterpreter, ServiceLineInterpreter>();
builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
// Obra "PDF de presupuesto" (2026-08-11/12), TANDA 3: renderer del PDF que ve el cliente. Espejo del
// registro de IInvoicePdfService de arriba.
builder.Services.AddScoped<IQuotePdfService, QuotePdfService>();
// Maqueta "PDF minimalista elegante" (2026-08-14 §5): paleta de acento por destino, segundo consumidor
// del cerebro IA. Scoped porque, como ServiceLineInterpreter, cuelga de IAiConnectionResolver (que lee
// la base) — el cacheo por 30 dias vive en IMemoryCache (compartido, ya registrado con AddMemoryCache
// mas arriba), no en el servicio en si.
builder.Services.AddScoped<IDestinationPaletteService, DestinationPaletteService>();
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

// ADR-041 TANDA 4 (2026-06-28): read-model SOLO LECTURA de "reembolsos a cobrar del operador" (ficha del
// proveedor + bandeja global). Enmascara montos de costo via CostMasking (IHttpContextAccessor + permisos).
builder.Services.AddScoped<IOperatorRefundReadModelService, OperatorRefundReadModelService>();

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
// ADR-041 TANDA 3 (lado proveedor): saldo a favor CONSUMIBLE con un operador (aplicar/revertir).
builder.Services.AddScoped<ISupplierCreditService, SupplierCreditService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
// ADR-041 (2026-06-27): cuentas bancarias polimorficas (Agencia / Cliente / Proveedor).
builder.Services.AddScoped<IBankAccountService, BankAccountService>();
builder.Services.AddScoped<IPassengerSearchService, PassengerSearchService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IBnaExchangeRateService, BnaExchangeRateService>();
// ADR-011 (enmienda 2026-08-05, "tipo de cambio real" + "el dolar nunca falta"): resolver de
// sugerencia de TC (solo LEE la libreta ExchangeRateQuotes, nunca le pega a ARCA EN VIVO — la unica
// excepcion es encolar ExchangeRateSyncJob on-demand cuando falta la fila de hoy, fire-and-forget)
// + el job por hora que la llena (unico que le pega a ARCA/APIs publicas). Sin flag (T-11): sale directo.
builder.Services.AddScoped<IExchangeRateResolver, ExchangeRateResolver>();
// ADR-011 (enmienda 2026-08-05, "hallazgo del dueño en vivo" + "el dolar nunca falta"): respaldo
// REAL via CINCO APIs publicas (dolarapi.com / monedapi.ar / criptoya.com / argentinadatos.com /
// bluelytics.com.ar) para cuando ARCA no sirve un numero util (ej. homologacion, que devuelve
// cotizaciones de juguete). Solo lo consume el job, nunca el camino interactivo.
builder.Services.AddScoped<IOfficialDollarPublicApiService, OfficialDollarPublicApiService>();
builder.Services.AddScoped<TravelApi.Infrastructure.Services.ExchangeRateSyncJob>();
builder.Services.AddScoped<IServicioReservaService, ServicioReservaService>();
builder.Services.AddScoped<ICommissionService, CommissionService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IOperationalFinanceSettingsService, OperationalFinanceSettingsService>();
builder.Services.AddScoped<ITreasuryService, TreasuryService>();
// ADR-022 §4.7 (T4): fuente unica AR/AP por moneda, consumida por dashboard y tesoreria.
builder.Services.AddScoped<IFinancePositionService, FinancePositionService>();
builder.Services.AddScoped<OperationalFinanceMonitorService>();
// ADR-020 F3: motor de estados automatico (confirmacion/regresion automatica + estampado ConfirmedAt).
builder.Services.AddScoped<TravelApi.Infrastructure.Services.Reservations.ReservaAutoStateService>();
builder.Services.AddScoped<TravelApi.Infrastructure.Services.ReservaLifecycleAutomationService>();
// (2026-07-04, hallazgo A1) Recalculador de coherencia de plata de reservas anuladas. Servicio inyectable
// para que lo pueda llamar tanto el endpoint admin de mantenimiento como el vigía nocturno (pieza futura).
builder.Services.AddScoped<TravelApi.Infrastructure.Services.CoherenceMoneyRecalculator>();
// (Tanda 4, 2026-07-04) Vigía de coherencia: job nocturno que detecta datos incoherentes, auto-repara lo seguro
// (marcas colgadas, plata desactualizada) y reporta el resto (anuladas con servicios vivos / deuda sin comprobante)
// con UNA notificación urgente. Reusa el CoherenceMoneyRecalculator de arriba. Ver CoherenceWatchdogJob.
builder.Services.AddScoped<TravelApi.Infrastructure.Services.CoherenceWatchdogJob>();
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

// (2026-06-26): job nocturno que cierra el ciclo del reembolso del operador. Las cancelaciones trabadas en
// AwaitingOperatorRefund con OperatorRefundDueBy vencido pasan a AbandonedByOperator (reserva -> Cancelled).
builder.Services.AddScoped<TravelApi.Infrastructure.Services.OperatorRefundTimeoutJob>();
// (2026-07-04): barrido propio de cierre de anulaciones sin reembolso pendiente del operador (receivable $0).
// Desacoplado del job de timeouts para que corra aunque aquel falle (ver ZeroReceivableCancellationCloseJob).
builder.Services.AddScoped<TravelApi.Infrastructure.Services.ZeroReceivableCancellationCloseJob>();
// FIX B (2026-07-04): red de seguridad para el aviso de AFIP perdido en la NC TOTAL (analogo total del job bridge
// parcial). Re-aplica el callback del bridge cuando la NC ya tiene resultado final pero el aviso se perdio.
builder.Services.AddScoped<TravelApi.Infrastructure.Services.TotalCreditNoteBridgeReconciliationJob>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IRateService, RateService>();
// El bibliotecario del tarifario (2026-08-07): ordena repetidos y rescata habitaciones escondidas en el
// nombre. Version determinística; cuando llegue la de IA implementa la MISMA interfaz y no se toca nada mas.
builder.Services.AddScoped<ICatalogLibrarianService, CatalogLibrarianService>();
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

// Obra "Empezar de cero" (2026-07-27): borrado masivo de datos con backup previo obligatorio.
// El puerto de backup depende de IMinioClient (registrado mas abajo via AddMinio) - el orden de
// registro no importa para DI (resuelve lazy), pero se agrega aca al lado de sus pares.
builder.Services.AddScoped<IWipeBackupPort, PgDumpAndMinioWipeBackupPort>();
builder.Services.AddScoped<ISystemDataWipeService, SystemDataWipeService>();

// Obra "Restaurar desde la app" (2026-07-27, Parte B): reusa el mismo directorio de backups (Wipe:BackupDirectory)
// y los mismos binarios de postgresql-client-16 que el backup del wipe.
builder.Services.AddScoped<IDatabaseRestorePort, PgDatabaseRestorePort>();
builder.Services.AddScoped<ISystemDataRestoreService, SystemDataRestoreService>();

// B7 (plan 2026-07-31 tarde, deuda ADR-052) + retomo 2026-08-03: tras un restore, purga los jobs de
// Hangfire que quedaron encolados/programados DENTRO de la foto restaurada (ver el XML doc de la interfaz).
//
// Truco de framework: se pasa "JobStorage.Current" explicito en vez de dejar que el puerto lo resuelva solo
// adentro del metodo. Es el MISMO storage que "AddHangfire(...)" configura mas abajo (esa llamada deja
// seteado JobStorage.Current apenas corre, y este factory recien LEE esa variable cuando alguien pide el
// puerto por primera vez — no al registrar el servicio, sino ya con la app corriendo). La ventaja: un test
// puede pasarle su PROPIO storage de Postgres efimero al constructor sin tocar esta variable global.
builder.Services.AddScoped<IHangfireJobQueuePurgePort>(sp => new HangfireJobQueuePurgePort(
    JobStorage.Current,
    sp.GetRequiredService<IBackgroundJobClient>(),
    sp.GetRequiredService<ILogger<HangfireJobQueuePurgePort>>()));

// ADR-052 (2026-07-29, firmada): "poner el esquema al dia" pasa a ser un puerto compartido por los DOS caminos
// que lo necesitan - el arranque de la app (mas abajo, donde antes vivia esta secuencia escrita a mano) y la
// restauracion de un resguardo de una version anterior. Ver DatabaseSchemaUpdater para las dos politicas.
builder.Services.AddScoped<ISchemaUpdatePort, DatabaseSchemaUpdater>();

// Obra "Restaurar TOTAL" (2026-07-28, firmada): singleton a proposito - el flag de mantenimiento tiene que ser
// UNA sola instancia compartida por TODO el proceso (el middleware de todos los pedidos y el servicio de
// restauracion tienen que ver el MISMO estado), no una instancia nueva por request como los servicios Scoped.
builder.Services.AddSingleton<IMaintenanceModeService, FileMaintenanceModeService>();

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

// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B-10 de la revision funcional): frena CUALQUIER job
// de Hangfire mientras el sistema esta en modo mantenimiento. Sin esto, el proceso worker (que corre TODOS
// los jobs en background) seguiria escribiendo en la base mientras pg_restore la reemplaza entera. Se
// registra en AMBOS procesos (api/worker comparten este mismo Program.cs) - inocuo en la API, que no corre
// Hangfire server salvo que Hangfire:ServerEnabled este prendido.
GlobalJobFilters.Filters.Add(new TravelApi.Filters.MaintenanceModeHangfireFilter(app.Services.GetRequiredService<IMaintenanceModeService>()));

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
        // ADR-052 (D3): esta secuencia (3 bootstrappers de SQL crudo -> MigrateAsync -> 3 backfills idempotentes)
        // vivia escrita a mano ACA. Se movio ENTERA a DatabaseSchemaUpdater para que la restauracion de un
        // resguardo de una version anterior corra EXACTAMENTE lo mismo que un deploy limpio, sin una segunda copia
        // que se pueda desincronizar. La politica "Startup" conserva el comportamiento historico: 5 intentos con
        // espera, y los backfills que fallan se loguean y no abortan el arranque.
        var schemaUpdater = scope.ServiceProvider.GetRequiredService<ISchemaUpdatePort>();
        var schemaUpdate = await schemaUpdater.UpdateAsync(SchemaUpdatePolicy.Startup, CancellationToken.None);

        if (!schemaUpdate.Success)
        {
            app.Logger.LogError(
                "CRITICAL: FAILED TO APPLY EF CORE MIGRATIONS AFTER MULTIPLE ATTEMPTS. Motivo interno: {Message}",
                schemaUpdate.ErrorMessage);
            throw new InvalidOperationException(
                $"No se pudieron aplicar las migraciones de base de datos al arrancar: {schemaUpdate.ErrorMessage}");
        }

        app.Logger.LogInformation(
            "Esquema de base de datos al dia. Migraciones aplicadas en este arranque: {Migraciones}.",
            schemaUpdate.MigrationsApplied);
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
//
// BUG REAL encontrado el 2026-08-06 (deploy invalidaba TODAS las sesiones): por defecto,
// ForwardedHeadersOptions solo confia en el header X-Forwarded-For si el que se conecto
// DIRECTO al contenedor "api" es localhost. Pero ese contenedor nunca recibe conexiones de
// localhost: en docker-compose.yml "api" NO publica el puerto 8080 al host (solo "expose",
// no "ports"), asi que la UNICA forma de llegar a el es a traves del contenedor "web"
// (nginx que sirve el SPA). Como la IP de "web" no es localhost, el middleware descartaba
// el header por "no confiable" y TODOS los usuarios externos aparecian con la MISMA IP
// interna (la del contenedor "web") — eso rompia el rate limiter de mas abajo (ver policy
// "auth"): al particionar por IP, TODOS los usuarios compartian un unico balde de pedidos
// para login+refresh, y una ráfaga de reconexion tras un deploy lo agotaba en segundos,
// deslogueando gente que no hizo nada mal.
//
// La config completa (con el detalle de POR QUE se eligen redes privadas en vez de confiar
// en todo el mundo — hallazgo B1 de la revision de seguridad del 2026-08-06) vive en
// ForwardedHeadersConfiguration.Build(), compartida con los tests que prueban la semantica
// exacta de reenvio (TravelApi.Tests/Http/ForwardedHeadersConfigurationTests.cs).
app.UseForwardedHeaders(TravelApi.Middleware.ForwardedHeadersConfiguration.Build());

// 2. CORS (MUST be before any other middleware that responds or sets headers)
app.UseCors("web");

// Obra "Restaurar TOTAL" (2026-07-28, firmada): lo mas arriba posible del pipeline, a proposito. Mientras el
// sistema esta en mantenimiento, corta CASI todos los pedidos a /api/** con un 503 ANTES de que corran
// routing/autenticacion/autorizacion/compresion/cache - no hace falta nada de eso para decidir "estamos en
// mantenimiento", y la decision entera sale de un flag en memoria (nunca toca la base). Va DESPUES de CORS
// para que la respuesta 503 tambien lleve los headers de CORS (si no, el navegador la trataria como un error
// de CORS en vez de mostrar el mensaje real al usuario).
app.UseMiddleware<TravelApi.Middleware.MaintenanceModeMiddleware>();

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
app.UseRateLimiter();
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

    // (2026-06-26): cierre del ciclo del reembolso del operador. Corre 4am UTC (junto al housekeeping nocturno,
    // despues del lifecycle de las 3am) para que las cancelaciones cuyo operador no reembolso dentro del plazo
    // pasen a AbandonedByOperator y la reserva se cierre, antes de que el usuario abra el sistema a la manana.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.OperatorRefundTimeoutJob>(
        "operator-refund-timeout",
        job => job.RunAsync(CancellationToken.None),
        Cron.Daily(4));

    // (2026-07-04): barrido PROPIO de cierre de anulaciones sin reembolso pendiente del operador (receivable $0).
    // Antes corria SOLO como cola del job de timeouts de arriba; si la query de vencidas de aquel explotaba, esa
    // noche no se barria. Ahora es un job independiente (red de seguridad): corre 5am UTC, una hora DESPUES del de
    // timeouts, para no solaparse. El barrido sigue ademas invocandose al final de ProcessExpiredOperatorRefunds
    // (4am) para cerrar en la misma corrida lo recien abandonado; ambas pasadas son idempotentes (ver el job).
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.ZeroReceivableCancellationCloseJob>(
        "cancellation-zero-receivable-close",
        job => job.RunAsync(CancellationToken.None),
        Cron.Daily(5));

    // FIX B (2026-07-04): red de seguridad para el aviso de AFIP perdido en la NC TOTAL. Cada 30 min (misma cron
    // que el job bridge PARCIAL, del que es el analogo total): la "ventana de gracia" para considerar trabada una
    // BC corre EN LA QUERY (setting BridgeReconciliationStalenessMinutes, compartido), no en la cron. Es no-op si
    // EnableNewCancellationFlow=false. Re-aplica el callback del bridge (idempotente) cuando la NC ya tiene
    // resultado final de AFIP pero el aviso se perdio; al destrabar, la BC puede auto-cerrarse si no hubo plata al
    // operador (correcto). El BC sale de AwaitingFiscalConfirmation y deja de ser candidato -> self-healing.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.TotalCreditNoteBridgeReconciliationJob>(
        "total-credit-note-bridge-reconciliation",
        job => job.RunAsync(CancellationToken.None),
        "*/30 * * * *");

    // (Tanda 4, 2026-07-04): vigía de coherencia. Corre 6am UTC, DESPUÉS de todo el housekeeping nocturno (lifecycle
    // 3am, timeouts de reembolso 4am, cierre de anulaciones sin receivable 5am), para que su recálculo de plata vea
    // el estado ya asentado por esos jobs y no reporte falsos positivos que aquellos estaban por arreglar. Repara lo
    // seguro y, si queda algo para revisar, deja una única notificación urgente lista para cuando el dueño abre el
    // sistema a la mañana. Ver CoherenceWatchdogJob.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.CoherenceWatchdogJob>(
        "coherence-watchdog",
        job => job.RunScheduledAsync(CancellationToken.None),
        Cron.Daily(6));

    // ADR-011 (enmienda 2026-08-05, "el dolar nunca falta"): CADA HORA (antes era 1 vez/dia a las
    // 15:00 UTC ≈ 12:00 ART). El job tiene su propio guard barato al inicio
    // (ver ExchangeRateSyncJob.IsTodayAlreadyFullyCoveredAsync) que corta sin llamar a nadie cuando
    // el dia ya esta resuelto, asi que correr cada hora no significa 24 rondas de llamadas reales por
    // dia — la mayoria de las corridas se encuentran el trabajo ya hecho. El beneficio real: si las
    // fuentes fallan a la mañana, el sistema se auto-sana en la hora siguiente en vez de esperar hasta
    // el dia siguiente. Backfill de 7 dias + reconciliacion siguen viviendo DENTRO del job, no en la
    // cron.
    RecurringJob.AddOrUpdate<TravelApi.Infrastructure.Services.ExchangeRateSyncJob>(
        "exchange-rate-sync",
        job => job.RunAsync(CancellationToken.None),
        "0 * * * *");
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
                pendingMigrations = pendingMigrations.Count()
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
            app.Logger.LogError(ex, "MinIO connectivity check failed");
            return Results.Json(new
            {
                status = "unready",
                storage = "unavailable"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        metrics.SetDatabaseReady(true);
        return Results.Ok(new { status = "ready", storage = "connected" });
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
    // Incidente 2026-07-09 (migracion moneda fantasma): sin esta linea, un arranque fallido
    // termina el proceso con exit code 0. En el contenedor one-shot de migraciones eso es
    // veneno: la migracion revienta, el proceso "sale bien", deploy.sh imprime "Migrations
    // applied successfully" y recien nos enteramos cuando la API queda en /health/ready 503
    // (migracion pendiente) y el deploy aborta 3 minutos despues sin decir por que. Con el
    // exit code en 1, docker wait lo ve, deploy.sh corta ahi mismo y muestra los logs del
    // migrador (el error real), en vez de disfrazar la falla de exito.
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }

