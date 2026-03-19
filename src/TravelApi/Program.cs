using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
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

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog(); // Use Serilog for logging

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

builder.Services.AddExceptionHandler<TravelApi.Middleware.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddHttpContextAccessor();
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
    options.UseNpgsql(connectionString, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
});

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
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
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<IAfipService, AfipService>();
builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IReservaService, ReservaService>();
builder.Services.AddScoped<ISupplierService, SupplierService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IServicioReservaService, ServicioReservaService>();
builder.Services.AddScoped<ICommissionService, CommissionService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IRateService, RateService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

// Pilar 1: Cotizador + CRM + Vouchers
builder.Services.AddScoped<IQuoteService, QuoteService>();
builder.Services.AddScoped<ILeadService, LeadService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IVoucherService, VoucherService>();

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
        policy.WithOrigins(allowedOrigins)
              .SetIsOriginAllowedToAllowWildcardSubdomains()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Hangfire Configuration (PostgreSQL)
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfireServer();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseExceptionHandler(opt => { }); // Use GlobalExceptionHandler

app.Use(async (context, next) =>
{
    try
    {
        await next();
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
    
    // --- MOGOLICA MODE: Direct SQL Hotfix ---
    try 
    {
        app.Logger.LogInformation("Checking/Adding TotalPaid column via Raw SQL...");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"TravelFiles\" ADD COLUMN IF NOT EXISTS \"TotalPaid\" numeric(18,2) NOT NULL DEFAULT 0.0;");
        app.Logger.LogInformation("Raw SQL migration finished.");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Raw SQL migration skipped or failed (might already exist): {Message}", ex.Message);
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
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

app.UseSerilogRequestLogging();

app.UseRouting();

// 2. CORS (Explicitly permissive for known origins + wildcard fallback if needed)
app.UseCors("web");


// Hangfire Dashboard Secure Access
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hangfire"))
    {
        if (context.Request.Cookies.TryGetValue("hangfire_auth", out var token))
        {
            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var jwtOptions = builder.Configuration.GetSection(TravelApi.Domain.Options.JwtOptions.SectionName).Get<TravelApi.Domain.Options.JwtOptions>();
            
            if (jwtOptions != null && handler.CanReadToken(token))
            {
                 try 
                 {
                     var principal = handler.ValidateToken(token, new TokenValidationParameters
                     {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwtOptions.Issuer,
                        ValidAudience = jwtOptions.Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key))
                     }, out var validatedToken);

                     context.User = principal;
                 }
                 catch 
                 { 
                     // Invalid token, ignore
                 }
            }
        }
    }
    await next();
});

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new TravelApi.Filters.HangfireAuthorizationFilter() } 
});

app.MapGet("/api/auth/hangfire-login", (string token, HttpContext context) => 
{
    context.Response.Cookies.Append("hangfire_auth", token, new CookieOptions 
    { 
        HttpOnly = true, 
        Secure = true, 
        SameSite = SameSiteMode.Lax,
        Expires = DateTime.UtcNow.AddHours(1) 
    });
    return Results.Redirect("/hangfire");
});

app.UseAuthentication();
app.UseAuthorization();

// 3. Health Check
app.MapGet("/health", () => Results.Ok("Healthy")).AllowAnonymous();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
    // EF Core Migrations
    await dbContext.Database.MigrateAsync();

    // Hotfixes and schema updates are now managed by EF Core Migrations.


    // Seed roles
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roles = new[] { "Admin", "Colaborador" };
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }

    var admins = await userManager.GetUsersInRoleAsync("Admin");
    if (admins.Count == 0)
    {
        var firstUser = await userManager.Users.OrderBy(user => user.Id).FirstOrDefaultAsync();
        if (firstUser is not null)
        {
            await userManager.AddToRoleAsync(firstUser, "Admin");
        }
    }

    // ONE-TIME PASSWORD RESET for gagi126@gmail.com
    var targetUser = await userManager.FindByEmailAsync("gagi126@gmail.com");
    if (targetUser is not null)
    {
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(targetUser);
        // Password MUST have: uppercase, lowercase, digit, special char, min 8 chars
        var resetResult = await userManager.ResetPasswordAsync(targetUser, resetToken, "Aa1234567890$");
        if (resetResult.Succeeded)
        {
            app.Logger.LogInformation("Password reset successful for gagi126@gmail.com");
        }
        else
        {
            foreach (var error in resetResult.Errors)
            {
                app.Logger.LogError("Password reset failed: {0} - {1}", error.Code, error.Description);
            }
        }
    }
    else
    {
        app.Logger.LogWarning("User gagi126@gmail.com not found for password reset");
    }
}

app.MapControllers();

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
