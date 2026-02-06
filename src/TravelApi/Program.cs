using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Options;

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key))
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy =>
    {
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
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """);

    // Mark legacy migration as applied if tables exist
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        DO $$
        BEGIN
            IF EXISTS (SELECT FROM pg_tables WHERE schemaname = 'public' AND tablename = 'Customers') THEN
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260119202735_InitialRetailPivot', '8.0.0')
                ON CONFLICT DO NOTHING;
            END IF;
        END
        $$;
        """);

    await dbContext.Database.MigrateAsync();

    // Hotfixes for existing databases
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"TaxId\" character varying(20);");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"ContactName\" character varying(100) DEFAULT '';");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"IsActive\" boolean DEFAULT TRUE;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"CurrentBalance\" numeric DEFAULT 0;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"Email\" character varying(100);"); 
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"Phone\" character varying(50);");

    // TravelFiles
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"TravelFiles\" ADD COLUMN IF NOT EXISTS \"Description\" text;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"TravelFiles\" ADD COLUMN IF NOT EXISTS \"StartDate\" timestamp with time zone;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"TravelFiles\" ADD COLUMN IF NOT EXISTS \"EndDate\" timestamp with time zone;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"TravelFiles\" ADD COLUMN IF NOT EXISTS \"TotalCost\" numeric DEFAULT 0;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"TravelFiles\" ADD COLUMN IF NOT EXISTS \"TotalSale\" numeric DEFAULT 0;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"TravelFiles\" ADD COLUMN IF NOT EXISTS \"Balance\" numeric DEFAULT 0;");

    // Reservations
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"SupplierId\" integer;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"ConfirmationNumber\" text;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"ServiceType\" text;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"Description\" text;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"NetCost\" numeric DEFAULT 0;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"SalePrice\" numeric DEFAULT 0;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"Tax\" numeric DEFAULT 0;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ADD COLUMN IF NOT EXISTS \"ServiceDetailsJson\" text;");

    // Relax constraints
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ALTER COLUMN \"CustomerId\" DROP NOT NULL;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ALTER COLUMN \"ServiceType\" DROP NOT NULL;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Reservations\" ALTER COLUMN \"ConfirmationNumber\" DROP NOT NULL;");

    // Customers
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Customers\" ADD COLUMN IF NOT EXISTS \"TaxId\" character varying(20);");

    // Passengers Table
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS "Passengers" (
            "Id" SERIAL PRIMARY KEY,
            "TravelFileId" integer NOT NULL REFERENCES "TravelFiles"("Id") ON DELETE CASCADE,
            "FullName" character varying(200) NOT NULL,
            "DocumentType" character varying(20),
            "DocumentNumber" character varying(50),
            "BirthDate" timestamp with time zone,
            "Nationality" character varying(50),
            "Phone" character varying(50),
            "Email" character varying(200),
            "Gender" character varying(10),
            "Notes" text,
            "CreatedAt" timestamp with time zone DEFAULT NOW()
        );
        """);

    // Payments - Add TravelFileId and Notes
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Payments\" ADD COLUMN IF NOT EXISTS \"TravelFileId\" integer REFERENCES \"TravelFiles\"(\"Id\");");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Payments\" ADD COLUMN IF NOT EXISTS \"Notes\" text;");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Payments\" ALTER COLUMN \"ReservationId\" DROP NOT NULL;");

    // Sprint 4: SupplierPayments (Egresos)
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS "SupplierPayments" (
            "Id" SERIAL PRIMARY KEY,
            "SupplierId" integer NOT NULL REFERENCES "Suppliers"("Id") ON DELETE RESTRICT,
            "TravelFileId" integer REFERENCES "TravelFiles"("Id"),
            "ReservationId" integer REFERENCES "Reservations"("Id"),
            "Amount" numeric(18,2) NOT NULL DEFAULT 0,
            "PaidAt" timestamp with time zone NOT NULL DEFAULT NOW(),
            "Method" character varying(50) NOT NULL DEFAULT 'Transfer',
            "Reference" text,
            "Notes" text,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW()
        );
        """);

    // Sprint 4: AgencySettings
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS "AgencySettings" (
            "Id" SERIAL PRIMARY KEY,
            "AgencyName" character varying(200) NOT NULL DEFAULT 'Mi Agencia de Viajes',
            "TaxId" character varying(20),
            "Address" character varying(500),
            "Phone" character varying(100),
            "Email" character varying(200),
            "DefaultCommissionPercent" numeric(5,2) NOT NULL DEFAULT 10,
            "Currency" character varying(3) NOT NULL DEFAULT 'ARS',
            "UpdatedAt" timestamp with time zone NOT NULL DEFAULT NOW()
        );
        """);

    // Seed default agency settings if not exists
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        INSERT INTO "AgencySettings" ("AgencyName", "DefaultCommissionPercent")
        SELECT 'Mi Agencia de Viajes', 10
        WHERE NOT EXISTS (SELECT 1 FROM "AgencySettings");
        """);

    // Supplier new columns + fix existing data
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"TaxCondition\" character varying(50);");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"Address\" character varying(200);");
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ADD COLUMN IF NOT EXISTS \"CreatedAt\" timestamp with time zone DEFAULT NOW();");
    // Fix existing suppliers with NULL CreatedAt
    await dbContext.Database.ExecuteSqlRawAsync(
        "UPDATE \"Suppliers\" SET \"CreatedAt\" = NOW() WHERE \"CreatedAt\" IS NULL;");
    // Make TaxId nullable if it was NOT NULL
    await dbContext.Database.ExecuteSqlRawAsync(
        "ALTER TABLE \"Suppliers\" ALTER COLUMN \"TaxId\" DROP NOT NULL;");


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

public partial class Program { }
