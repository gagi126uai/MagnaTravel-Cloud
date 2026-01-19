using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Options;
using TravelApi.Services;
using TravelApi.Services.Bsp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
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
    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<CupoAllocationService>();
builder.Services.AddScoped<BspImportService>();
builder.Services.AddScoped<IBspImportParser, CsvBspImportParser>();
builder.Services.AddScoped<IBspImportParser, JsonBspImportParser>();

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
    .AddEntityFrameworkStores<AppDbContext>();

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key))
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy =>
    {
        // PERMISSIVE CORS POLICY TO FIX VPS/BROWSER ISSUES
        // This allows any origin to connect, which is necessary if the origin headers 
        // are varying or if env vars are missing.
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

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

app.UseRouting();
app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
    // HYBRID MIGRATION STRATEGY (Legacy -> EF Core)
    // 1. Ensure Migration History Table exists
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """);

    // 2. Check if legacy tables exist (e.g. AccountingEntries)
    // If they exist, we mark 'InitialRetailPivot' as ALREADY APPLIED to prevent "Table already exists" error.
    var result = await dbContext.Database.ExecuteSqlRawAsync(
        """
        DO $$
        BEGIN
            IF EXISTS (SELECT FROM pg_tables WHERE schemaname = 'public' AND tablename = 'AccountingEntries') THEN
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260119202735_InitialRetailPivot', '8.0.0')
                ON CONFLICT DO NOTHING;
            END IF;
        END
        $$;
        """);

    // 3. Now run MigrateAsync (Safely skips InitialRetailPivot if we just inserted it)
    await dbContext.Database.MigrateAsync();

    // 4. HOTFIX: Ensure TaxId, ContactName, IsActive exists on Suppliers/Customers
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"TaxId\" character varying(20);");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"ContactName\" character varying(100) DEFAULT '';"); // Missing column fix
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"IsActive\" boolean DEFAULT TRUE;"); // Missing column fix

    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Customers\" ADD COLUMN IF NOT EXISTS \"TaxId\" character varying(20);");

    // Seed initial data if needed (e.g. Roles, Admin User)
    // [Seeding logic remains if present, otherwise empty]
    // [Seeding logic remains if present, otherwise empty]

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
}

app.MapControllers();

app.Run();

public partial class Program { }
