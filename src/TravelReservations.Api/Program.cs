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

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddAutoMapper(typeof(Program), typeof(TravelApi.Application.Mappings.MappingProfile));
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddDbContext<ReservationsDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseNpgsql(connectionString, o =>
    {
        o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
    });
});

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
    x.AddEntityFrameworkOutbox<ReservationsDbContext>(o =>
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
    var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
    // Do NOT run MigrateAsync() — the schema is managed by the monolith's AppDbContext migrations.
    // Only verify connectivity. MassTransit outbox tables are created by AppDbContext migrations.
    if (!await db.Database.CanConnectAsync())
    {
        app.Logger.LogCritical("ReservationsDbContext cannot connect to the database.");
    }
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "reservations" })).AllowAnonymous();
app.MapGet("/health/ready", async (ReservationsDbContext dbContext, CancellationToken cancellationToken) =>
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

