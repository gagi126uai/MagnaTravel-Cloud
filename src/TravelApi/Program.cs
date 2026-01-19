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
        var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>();
        var originsCsv = builder.Configuration["Cors:Origins"];
        if (!string.IsNullOrWhiteSpace(originsCsv))
        {
            origins = originsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var webOrigin = builder.Configuration["WEB_ORIGIN"];
        if ((origins is null || origins.Length == 0) && !string.IsNullOrWhiteSpace(webOrigin))
        {
            origins = webOrigin.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        if (origins is null || origins.Length == 0)
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
            return;
        }

        var allowedHosts = origins
            .Select(originValue =>
            {
                if (Uri.TryCreate(originValue, UriKind.Absolute, out var uri))
                {
                    return uri;
                }

                return null;
            })
            .Where(uri => uri is not null)
            .ToList();

        policy.SetIsOriginAllowed(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            {
                return false;
            }

            return allowedHosts.Any(allowed =>
                string.Equals(originUri.Host, allowed!.Host, StringComparison.OrdinalIgnoreCase) &&
                (allowed.Port <= 0 || originUri.Port == allowed.Port));
        })
            .AllowAnyHeader()
            .AllowAnyMethod();
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
    dbContext.Database.EnsureCreated();
    if (dbContext.Database.IsRelational())
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"IsActive\" boolean NOT NULL DEFAULT TRUE;");
            
        await dbContext.Database.ExecuteSqlRawAsync(
             "ALTER TABLE \"Tariffs\" ADD COLUMN IF NOT EXISTS \"ProductType\" character varying(50) NOT NULL DEFAULT 'General';");

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Customers" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "FullName" character varying(200) NOT NULL,
                "Email" character varying(200),
                "Phone" character varying(50),
                "DocumentNumber" character varying(50),
                "Address" character varying(300),
                "Notes" text,
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_Customers" PRIMARY KEY ("Id")
            );
            """);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Reservations" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "ReferenceCode" character varying(50) NOT NULL,
                "Status" character varying(50) NOT NULL,
                "ProductType" character varying(50) NOT NULL,
                "DepartureDate" timestamp with time zone NOT NULL,
                "ReturnDate" timestamp with time zone,
                "BasePrice" numeric(12,2) NOT NULL,
                "Commission" numeric(12,2) NOT NULL,
                "TotalAmount" numeric(12,2) NOT NULL,
                "SupplierName" character varying(200),
                "CreatedAt" timestamp with time zone NOT NULL,
                "CustomerId" integer NOT NULL,
                CONSTRAINT "PK_Reservations" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Reservations_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE RESTRICT
            );
            """);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Payments" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Amount" numeric(12,2) NOT NULL,
                "PaidAt" timestamp with time zone NOT NULL,
                "Method" character varying(50) NOT NULL,
                "Status" character varying(50) NOT NULL,
                "ReservationId" integer NOT NULL,
                CONSTRAINT "PK_Payments" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Payments_Reservations_ReservationId" FOREIGN KEY ("ReservationId") REFERENCES "Reservations" ("Id") ON DELETE CASCADE
            );
            """);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Quotes" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "ReferenceCode" character varying(50) NOT NULL,
                "Status" character varying(30) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "CustomerId" integer NOT NULL,
                CONSTRAINT "PK_Quotes" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Quotes_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE RESTRICT
            );
            """);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "QuoteVersions" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "VersionNumber" integer NOT NULL,
                "ProductType" character varying(50) NOT NULL,
                "Currency" character varying(10),
                "TotalAmount" numeric(12,2) NOT NULL,
                "ValidUntil" timestamp with time zone,
                "Notes" character varying(500),
                "CreatedAt" timestamp with time zone NOT NULL,
                "QuoteId" integer NOT NULL,
                CONSTRAINT "PK_QuoteVersions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_QuoteVersions_Quotes_QuoteId" FOREIGN KEY ("QuoteId") REFERENCES "Quotes" ("Id") ON DELETE CASCADE
            );
            """);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "TreasuryReceipts" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Reference" character varying(100) NOT NULL,
                "Method" character varying(50) NOT NULL,
                "Currency" character varying(10),
                "Amount" numeric(12,2) NOT NULL,
                "ReceivedAt" timestamp with time zone NOT NULL,
                "Notes" character varying(500),
                CONSTRAINT "PK_TreasuryReceipts" PRIMARY KEY ("Id")
            );
            """);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "TreasuryApplications" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "AmountApplied" numeric(12,2) NOT NULL,
                "AppliedAt" timestamp with time zone NOT NULL,
                "TreasuryReceiptId" integer NOT NULL,
                "ReservationId" integer NOT NULL,
                CONSTRAINT "PK_TreasuryApplications" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_TreasuryApplications_TreasuryReceipts_TreasuryReceiptId" FOREIGN KEY ("TreasuryReceiptId") REFERENCES "TreasuryReceipts" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_TreasuryApplications_Reservations_ReservationId" FOREIGN KEY ("ReservationId") REFERENCES "Reservations" ("Id") ON DELETE RESTRICT
            );
            """);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Suppliers" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Name" character varying(200) NOT NULL,
                "Email" character varying(200),
                "Phone" character varying(50),
                "Notes" text,
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_Suppliers" PRIMARY KEY ("Id")
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Tariffs" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Name" character varying(200) NOT NULL,
                "Description" character varying(500),
                "ProductType" character varying(50) NOT NULL,
                "Currency" character varying(10),
                "DefaultPrice" numeric(12,2) NOT NULL,
                "IsActive" boolean NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_Tariffs" PRIMARY KEY ("Id")
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "TariffValidities" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "StartDate" timestamp with time zone NOT NULL,
                "EndDate" timestamp with time zone NOT NULL,
                "Price" numeric(12,2) NOT NULL,
                "IsActive" boolean NOT NULL,
                "Notes" character varying(500),
                "CreatedAt" timestamp with time zone NOT NULL,
                "TariffId" integer NOT NULL,
                CONSTRAINT "PK_TariffValidities" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_TariffValidities_Tariffs_TariffId" FOREIGN KEY ("TariffId") REFERENCES "Tariffs" ("Id") ON DELETE CASCADE
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Cupos" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Name" character varying(200) NOT NULL,
                "ProductType" character varying(50) NOT NULL,
                "TravelDate" timestamp with time zone NOT NULL,
                "Capacity" integer NOT NULL,
                "OverbookingLimit" integer NOT NULL,
                "Reserved" integer NOT NULL,
                "RowVersion" uuid NOT NULL,
                CONSTRAINT "PK_Cupos" PRIMARY KEY ("Id")
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "CupoAssignments" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "CupoId" integer NOT NULL,
                "ReservationId" integer,
                "Quantity" integer NOT NULL,
                "AssignedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_CupoAssignments" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_CupoAssignments_Cupos_CupoId" FOREIGN KEY ("CupoId") REFERENCES "Cupos" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CupoAssignments_Reservations_ReservationId" FOREIGN KEY ("ReservationId") REFERENCES "Reservations" ("Id") ON DELETE SET NULL
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "BspImportBatches" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "FileName" character varying(200) NOT NULL,
                "Format" character varying(20) NOT NULL,
                "ImportedAt" timestamp with time zone NOT NULL,
                "Status" character varying(20) NOT NULL,
                "ClosedAt" timestamp with time zone,
                CONSTRAINT "PK_BspImportBatches" PRIMARY KEY ("Id")
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "BspImportRawRecords" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "LineNumber" integer NOT NULL,
                "RawContent" text NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "BspImportBatchId" integer NOT NULL,
                CONSTRAINT "PK_BspImportRawRecords" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_BspImportRawRecords_BspImportBatches_BspImportBatchId" FOREIGN KEY ("BspImportBatchId") REFERENCES "BspImportBatches" ("Id") ON DELETE CASCADE
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "BspNormalizedRecords" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "TicketNumber" character varying(50) NOT NULL,
                "ReservationReference" character varying(50) NOT NULL,
                "IssueDate" timestamp with time zone NOT NULL,
                "Currency" character varying(10) NOT NULL,
                "BaseAmount" numeric(12,2) NOT NULL,
                "TaxAmount" numeric(12,2) NOT NULL,
                "TotalAmount" numeric(12,2) NOT NULL,
                "BspImportBatchId" integer NOT NULL,
                CONSTRAINT "PK_BspNormalizedRecords" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_BspNormalizedRecords_BspImportBatches_BspImportBatchId" FOREIGN KEY ("BspImportBatchId") REFERENCES "BspImportBatches" ("Id") ON DELETE CASCADE
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "BspReconciliationEntries" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "Status" character varying(30) NOT NULL,
                "DifferenceAmount" numeric(12,2),
                "ReconciledAt" timestamp with time zone NOT NULL,
                "BspImportBatchId" integer NOT NULL,
                "BspNormalizedRecordId" integer NOT NULL,
                "ReservationId" integer,
                CONSTRAINT "PK_BspReconciliationEntries" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_BspReconciliationEntries_BspImportBatches_BspImportBatchId" FOREIGN KEY ("BspImportBatchId") REFERENCES "BspImportBatches" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_BspReconciliationEntries_BspNormalizedRecords_BspNormalizedRecordId" FOREIGN KEY ("BspNormalizedRecordId") REFERENCES "BspNormalizedRecords" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_BspReconciliationEntries_Reservations_ReservationId" FOREIGN KEY ("ReservationId") REFERENCES "Reservations" ("Id") ON DELETE SET NULL
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "AccountingEntries" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "EntryDate" timestamp with time zone NOT NULL,
                "Description" character varying(300) NOT NULL,
                "Source" character varying(50) NOT NULL,
                "SourceReference" character varying(100) NOT NULL,
                CONSTRAINT "PK_AccountingEntries" PRIMARY KEY ("Id")
            );
            """);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "AccountingLines" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "AccountCode" character varying(20) NOT NULL,
                "Debit" numeric(12,2) NOT NULL,
                "Credit" numeric(12,2) NOT NULL,
                "Currency" character varying(10) NOT NULL,
                "AccountingEntryId" integer NOT NULL,
                CONSTRAINT "PK_AccountingLines" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_AccountingLines_AccountingEntries_AccountingEntryId" FOREIGN KEY ("AccountingEntryId") REFERENCES "AccountingEntries" ("Id") ON DELETE CASCADE
            );
            """);
    }

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
