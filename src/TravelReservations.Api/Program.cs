using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;

using System.Threading.RateLimiting;

using MassTransit;
using Minio.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

static bool IsPlaceholderSecret(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    return value.Contains("CHANGE_THIS_SECRET", StringComparison.OrdinalIgnoreCase)
        || value.Contains("travelpass", StringComparison.OrdinalIgnoreCase);
}

var internalReservationsToken =
    builder.Configuration["Services:Reservations:InternalToken"] ??
    builder.Configuration["InternalServiceAuth:Token"];

if (IsPlaceholderSecret(internalReservationsToken))
{
    throw new InvalidOperationException(
        "Reservations service startup blocked because the internal service token is missing or still uses a placeholder value.");
}

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddAutoMapper(typeof(Program), typeof(TravelApi.Application.Mappings.MappingProfile));
builder.Services.AddEndpointsApiExplorer();

// All DB access goes through AppDbContext — it has all correct table/column mappings.

// Los servicios de negocio (ReservaService, etc.) dependen de AppDbContext.
// Registramos AppDbContext apuntando a la misma base de datos.
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(connectionString, o =>
    {
        o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
    });
});

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

        cfg.Host(host, "/", h => {
            h.Username(user);
            h.Password(pass);
        });

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddAuthentication(InternalServiceAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, InternalServiceAuthenticationHandler>(
        InternalServiceAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(InternalServiceAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("uploads", _ => RateLimitPartition.GetNoLimiter("reservations-service-uploads"));
});

builder.Services.AddScoped(typeof(IRepository<>), typeof(ReservationsRepository<>));
builder.Services.AddHttpClient();
builder.Services.AddScoped<IEntityReferenceResolver, EntityReferenceResolver>();
builder.Services.AddScoped<IOperationalFinanceSettingsService, OperationalFinanceSettingsService>();
builder.Services.AddScoped<IReservaService, ReservaService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<ITimelineService, TimelineService>();
builder.Services.AddScoped<IVoucherService, VoucherService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddScoped<ISupplierService, SupplierService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IWhatsAppDeliveryService, WhatsAppDeliveryService>();

builder.Services.AddMinio(options =>
{
    options.Endpoint = builder.Configuration["Minio:Endpoint"] ?? builder.Configuration["MINIO_ENDPOINT"] ?? "minio:9000";
    options.AccessKey = builder.Configuration["Minio:AccessKey"] ?? builder.Configuration["MINIO_ACCESS_KEY"] ?? "minioadmin";
    options.SecretKey = builder.Configuration["Minio:SecretKey"] ?? builder.Configuration["MINIO_SECRET_KEY"] ?? "minioadmin";
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var minio = scope.ServiceProvider.GetRequiredService<Minio.IMinioClient>();
    var bucketName = builder.Configuration["Minio:BucketName"] ?? "reservations";
    try
    {
        bool found = await minio.BucketExistsAsync(new Minio.DataModel.Args.BucketExistsArgs().WithBucket(bucketName));
        if (!found)
        {
            await minio.MakeBucketAsync(new Minio.DataModel.Args.MakeBucketArgs().WithBucket(bucketName));
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to initialize MinIO bucket on startup. It might be unavailable.");
    }
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Do NOT run MigrateAsync() — the schema is managed by the monolith's AppDbContext migrations.
    if (!await db.Database.CanConnectAsync())
    {
        app.Logger.LogCritical("Cannot connect to the database.");
    }
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "reservations" })).AllowAnonymous();
app.MapGet("/health/ready", async (AppDbContext dbContext, CancellationToken cancellationToken) =>
{
    if (!await dbContext.Database.CanConnectAsync(cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new { status = "ready", service = "reservations" });
}).AllowAnonymous();

app.MapControllers();
app.Run();

public partial class Program;

internal sealed class InternalServiceAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "InternalService";
    private readonly IConfiguration _configuration;

    public InternalServiceAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expectedToken = _configuration["Services:Reservations:InternalToken"] ?? _configuration["InternalServiceAuth:Token"];
        var providedToken = Request.Headers["X-Internal-Service-Token"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(expectedToken) || !string.Equals(expectedToken, providedToken, StringComparison.Ordinal))
        {
            var correlationId = Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Request.HttpContext.TraceIdentifier;
            Logger.LogWarning(
                "Rejected internal service request {Method} {Path}. CorrelationId: {CorrelationId}. Reason: {Reason}",
                Request.Method,
                Request.Path,
                correlationId,
                string.IsNullOrWhiteSpace(expectedToken)
                    ? "expected token missing"
                    : string.IsNullOrWhiteSpace(providedToken)
                        ? "provided token missing"
                        : "token mismatch");
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal service token."));
        }

        var claims = new List<Claim>();
        var userId = Request.Headers["X-User-Id"].FirstOrDefault();
        var userName = Request.Headers["X-User-Name"].FirstOrDefault();
        var roles = Request.Headers["X-User-Roles"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(userId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            claims.Add(new Claim(ClaimTypes.Name, userName));
        }

        if (!string.IsNullOrWhiteSpace(roles))
        {
            foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

