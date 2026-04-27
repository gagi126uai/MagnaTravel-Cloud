using Microsoft.AspNetCore.Authentication.JwtBearer;
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

using TravelApi.Infrastructure.Logging;
using TravelApi.Hubs;
using TravelApi.Services;
using TravelApi.Errors;
using TravelApi.Infrastructure.Services.ReservationsServiceProxy;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Sink(new SignalRSink())
    .CreateLogger();




try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog(); // Use Serilog for logging

    static string AppendTrailingSlash(string value) => value.EndsWith("/") ? value : $"{value}/";

    static bool IsPlaceholderSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Contains("CHANGE_THIS_SECRET", StringComparison.OrdinalIgnoreCase)
            || value.Contains("travelpass", StringComparison.OrdinalIgnoreCase);
    }

    if (builder.Environment.IsProduction())
    {
        var jwtKey = builder.Configuration["Jwt:Key"] ?? builder.Configuration["Jwt__Key"];
        var webhookSecret = builder.Configuration["WhatsApp:WebhookSecret"] ?? builder.Configuration["WhatsApp__WebhookSecret"];
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        if (IsPlaceholderSecret(jwtKey) || IsPlaceholderSecret(webhookSecret) || IsPlaceholderSecret(connectionString))
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
    options.AddBasePolicy(builder => 
        builder.Expire(TimeSpan.FromMinutes(10)));
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

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(connectionString, o =>
    {
        o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
    });
});

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
builder.Services.Configure<ReservationsServiceOptions>(builder.Configuration.GetSection(ReservationsServiceOptions.SectionName));

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
            NameClaimType = ClaimTypes.NameIdentifier,
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

                if (context.Request.Path.StartsWithSegments("/hangfire") &&
                    context.Request.Cookies.TryGetValue(AuthCookieNames.Hangfire, out var hangfireToken))
                {
                    context.Token = hangfireToken;
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
builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
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
builder.Services.AddHostedService<LogStreamingService>();
builder.Services.AddHostedService<BotLogMonitorService>();

// Pilar 1: Cotizador + CRM + Vouchers
builder.Services.AddScoped<IQuoteService, QuoteService>();
builder.Services.AddScoped<ILeadService, LeadService>();
builder.Services.AddScoped<IWhatsAppDeliveryService, WhatsAppDeliveryService>();

var reservationsServiceBaseUrl = builder.Configuration[$"{ReservationsServiceOptions.SectionName}:BaseUrl"];
var reservationsProxyEnabled = !string.IsNullOrWhiteSpace(reservationsServiceBaseUrl);
var reservationsInternalToken = builder.Configuration[$"{ReservationsServiceOptions.SectionName}:InternalToken"];

if (reservationsProxyEnabled)
{
    if (IsPlaceholderSecret(reservationsInternalToken))
    {
        throw new InvalidOperationException(
            "Reservations proxy startup blocked because Services:Reservations:InternalToken is missing or still uses a placeholder value.");
    }

    builder.Services.AddTransient<ReservationsServiceAuthHandler>();

    builder.Services.AddHttpClient<ReservaServiceHttpProxy>(client =>
    {
        client.BaseAddress = new Uri(AppendTrailingSlash(reservationsServiceBaseUrl!));
    }).AddHttpMessageHandler<ReservationsServiceAuthHandler>();

    builder.Services.AddHttpClient<PaymentServiceHttpProxy>(client =>
    {
        client.BaseAddress = new Uri(AppendTrailingSlash(reservationsServiceBaseUrl!));
    }).AddHttpMessageHandler<ReservationsServiceAuthHandler>();

    builder.Services.AddHttpClient<BookingServiceHttpProxy>(client =>
    {
        client.BaseAddress = new Uri(AppendTrailingSlash(reservationsServiceBaseUrl!));
    }).AddHttpMessageHandler<ReservationsServiceAuthHandler>();

    builder.Services.AddHttpClient<TimelineServiceHttpProxy>(client =>
    {
        client.BaseAddress = new Uri(AppendTrailingSlash(reservationsServiceBaseUrl!));
    }).AddHttpMessageHandler<ReservationsServiceAuthHandler>();

    builder.Services.AddHttpClient<VoucherServiceHttpProxy>(client =>
    {
        client.BaseAddress = new Uri(AppendTrailingSlash(reservationsServiceBaseUrl!));
    }).AddHttpMessageHandler<ReservationsServiceAuthHandler>();

    builder.Services.AddHttpClient<AttachmentServiceHttpProxy>(client =>
    {
        client.BaseAddress = new Uri(AppendTrailingSlash(reservationsServiceBaseUrl!));
    }).AddHttpMessageHandler<ReservationsServiceAuthHandler>();

    builder.Services.AddScoped<IReservaService>(sp => sp.GetRequiredService<ReservaServiceHttpProxy>());
    builder.Services.AddScoped<IPaymentService>(sp => sp.GetRequiredService<PaymentServiceHttpProxy>());
    builder.Services.AddScoped<IBookingService>(sp => sp.GetRequiredService<BookingServiceHttpProxy>());
    builder.Services.AddScoped<ITimelineService>(sp => sp.GetRequiredService<TimelineServiceHttpProxy>());
    builder.Services.AddScoped<IVoucherService>(sp => sp.GetRequiredService<VoucherServiceHttpProxy>());
    builder.Services.AddScoped<IAttachmentService>(sp => sp.GetRequiredService<AttachmentServiceHttpProxy>());
}
else
{
    builder.Services.AddScoped<IReservaService, ReservaService>();
    builder.Services.AddScoped<IPaymentService, PaymentService>();
    builder.Services.AddScoped<IBookingService, BookingService>();
    builder.Services.AddScoped<ITimelineService, TimelineService>();
    builder.Services.AddScoped<IVoucherService, VoucherService>();
    builder.Services.AddScoped<IAttachmentService, AttachmentService>();
}

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

builder.Services.AddHangfireServer();

var app = builder.Build();

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

// Apply migrations on startup
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
app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<TravelApi.Middleware.CookieCsrfMiddleware>();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new TravelApi.Filters.HangfireAuthorizationFilter() } 
});

RecurringJob.AddOrUpdate<OperationalFinanceMonitorService>(
    "upcoming-unpaid-reservas",
    service => service.GenerateUpcomingUnpaidReservationNotificationsAsync(),
    Cron.Daily());

// 3. Health Check
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (AppDbContext dbContext, CancellationToken cancellationToken) =>
{
    try
    {
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            return Results.Json(
                DatabaseExceptionClassifier.CreateProblemDetails(),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pendingMigrations.Any())
        {
            return Results.Json(new
            {
                status = "unready",
                code = "database_not_ready",
                pendingMigrations = pendingMigrations.ToArray()
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
    {
        return Results.Json(
            DatabaseExceptionClassifier.CreateProblemDetails(app.Environment.IsDevelopment() ? ex.Message : null),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
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

