using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MassTransit;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Persistence;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    private static readonly HashSet<string> SensitiveAuditFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "PasswordHash",
        "SecurityStamp",
        "ConcurrencyStamp",
        "CertificateData",
        "CertificatePassword",
        "Token",
        "Sign",
        "PadronToken",
        "PadronSign",
        "TokenHash",
        "ReplacedByTokenHash",
        "WebhookSecret",
        "RefreshToken",
        "AccessToken",
        "CsrfToken"
    };

    private readonly IHttpContextAccessor? _httpContextAccessor;

    public DbSet<Notification> Notifications => Set<Notification>();

    public AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override int SaveChanges()
    {
        AssignPublicIds();
        var auditEntries = OnBeforeSaveChanges();
        var result = base.SaveChanges();
        OnAfterSaveChanges(auditEntries);
        return result;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AssignPublicIds();
        var auditEntries = OnBeforeSaveChanges();
        var result = await base.SaveChangesAsync(cancellationToken);
        await OnAfterSaveChangesAsync(auditEntries, cancellationToken);
        return result;
    }

    private void AssignPublicIds()
    {
        foreach (var entry in ChangeTracker.Entries<IHasPublicId>())
        {
            if (entry.State == EntityState.Added && entry.Entity.PublicId == Guid.Empty)
            {
                entry.Entity.PublicId = Guid.NewGuid();
            }
        }
    }

    private class AuditEntry
    {
        public EntityEntry Entry { get; set; } = null!;
        public AuditLog AuditLog { get; set; } = null!;
    }

    private List<AuditEntry> OnBeforeSaveChanges()
    {
        ChangeTracker.DetectChanges();
        var auditEntries = new List<AuditEntry>();
        
        var user = _httpContextAccessor?.HttpContext?.User;
        var userId = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = user?.FindFirst("FullName")?.Value 
                      ?? user?.FindFirst(ClaimTypes.Name)?.Value 
                      ?? user?.Identity?.Name 
                      ?? "Sistema";
        var timestamp = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLog || entry.State == EntityState.Detached || entry.State == EntityState.Unchanged)
                continue;

            var auditLog = new AuditLog
            {
                UserId = userId,
                UserName = userName,
                EntityName = entry.Entity.GetType().Name,
                Timestamp = timestamp,
                Action = entry.State.ToString(),
                Category = "Entity"
            };

            var changes = new Dictionary<string, object>();
            
            auditLog.EntityId = GetAuditEntityId(entry);

            if (entry.State == EntityState.Added)
            {
                auditLog.Action = "Create";
                foreach (var property in entry.Properties)
                {
                    if (property.Metadata.IsPrimaryKey()) continue;
                    if (ShouldSkipAuditProperty(property)) continue;
                    if (property.CurrentValue != null)
                    {
                        changes[property.Metadata.Name] = SanitizeAuditValue(property.CurrentValue);
                    }
                }
            }
            else if (entry.State == EntityState.Deleted)
            {
                auditLog.Action = "Delete";
                foreach (var property in entry.Properties)
                {
                    if (property.Metadata.IsPrimaryKey()) continue;
                    if (ShouldSkipAuditProperty(property)) continue;
                    changes[property.Metadata.Name] = SanitizeAuditValue(property.OriginalValue);
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                 var isDeletedProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "IsDeleted");
                 if (isDeletedProp != null && isDeletedProp.CurrentValue is bool isDeleted && isDeleted)
                 {
                     auditLog.Action = "SoftDelete";
                 }
                 else
                 {
                     auditLog.Action = "Update";
                 }

                foreach (var property in entry.Properties)
                {
                    if (ShouldSkipAuditProperty(property)) continue;
                    if (property.IsModified)
                    {
                        changes[property.Metadata.Name] = new
                        {
                            Old = SanitizeAuditValue(property.OriginalValue),
                            New = SanitizeAuditValue(property.CurrentValue)
                        };
                    }
                }
            }

            if (changes.Count == 0)
            {
                continue;
            }

            auditLog.Changes = JsonSerializer.Serialize(changes);
            auditEntries.Add(new AuditEntry { Entry = entry, AuditLog = auditLog });
        }
        
        return auditEntries;
    }

    private void OnAfterSaveChanges(List<AuditEntry> auditEntries)
    {
        if (auditEntries == null || auditEntries.Count == 0) return;

        foreach (var auditEntry in auditEntries)
        {
            if (auditEntry.AuditLog.EntityId == "0" || auditEntry.AuditLog.EntityId == null)
            {
                auditEntry.AuditLog.EntityId = GetAuditEntityId(auditEntry.Entry);
            }
            AuditLogs.Add(auditEntry.AuditLog);
        }

        if (AuditLogs.Local.Any())
        {
            base.SaveChanges(); // Save the logs
        }
    }

    private async Task OnAfterSaveChangesAsync(List<AuditEntry> auditEntries, CancellationToken cancellationToken)
    {
        if (auditEntries == null || auditEntries.Count == 0) return;

        foreach (var auditEntry in auditEntries)
        {
            if (auditEntry.AuditLog.EntityId == "0" || auditEntry.AuditLog.EntityId == null)
            {
                auditEntry.AuditLog.EntityId = GetAuditEntityId(auditEntry.Entry);
            }
            AuditLogs.Add(auditEntry.AuditLog);
        }

        if (AuditLogs.Local.Any())
        {
             await base.SaveChangesAsync(cancellationToken); // Save the logs
        }
    }

    private static string GetAuditEntityId(EntityEntry entry)
    {
        if (entry.Entity is IHasPublicId hasPublicId && hasPublicId.PublicId != Guid.Empty)
        {
            return hasPublicId.PublicId.ToString();
        }

        var keyName = entry.Metadata.FindPrimaryKey()?.Properties.Select(p => p.Name).FirstOrDefault();
        var primaryKey = keyName != null ? entry.Property(keyName).CurrentValue : null;
        return primaryKey?.ToString() ?? "0";
    }

    private static bool ShouldSkipAuditProperty(PropertyEntry property)
    {
        if (SensitiveAuditFields.Contains(property.Metadata.Name))
        {
            return true;
        }

        if (property.Metadata.ClrType == typeof(byte[]) || property.CurrentValue is byte[] || property.OriginalValue is byte[])
        {
            return true;
        }

        return false;
    }

    private static object? SanitizeAuditValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return text.Length <= 256 ? text : $"{text[..256]}...[TRUNCATED]";
        }

        return value;
    }

    // Core Entities - Retail ERP
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Reserva> Reservas => Set<Reserva>();
    public DbSet<Passenger> Passengers => Set<Passenger>();
    public DbSet<ServicioReserva> Servicios => Set<ServicioReserva>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<FlightSegment> FlightSegments => Set<FlightSegment>();
    
    // Sprint 4: Egresos y Configuración
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<AgencySettings> AgencySettings => Set<AgencySettings>();
    public DbSet<OperationalFinanceSettings> OperationalFinanceSettings => Set<OperationalFinanceSettings>();
    public DbSet<CommissionRule> CommissionRules => Set<CommissionRule>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<DestinationDeparture> DestinationDepartures => Set<DestinationDeparture>();
    
    // Sprint 5: Servicios específicos y Tarifario
    public DbSet<HotelBooking> HotelBookings => Set<HotelBooking>();
    public DbSet<TransferBooking> TransferBookings => Set<TransferBooking>();
    public DbSet<PackageBooking> PackageBookings => Set<PackageBooking>();
    public DbSet<Rate> Rates => Set<Rate>();
    public DbSet<CatalogPackage> CatalogPackages => Set<CatalogPackage>();
    public DbSet<CatalogPackageDeparture> CatalogPackageDepartures => Set<CatalogPackageDeparture>();

    // Sprint 5: AFIP / Facturación
    public DbSet<AfipSettings> AfipSettings => Set<AfipSettings>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PaymentReceipt> PaymentReceipts => Set<PaymentReceipt>();
    public DbSet<ManualCashMovement> ManualCashMovements => Set<ManualCashMovement>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ReservaAttachment> ReservaAttachments => Set<ReservaAttachment>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherPassengerAssignment> VoucherPassengerAssignments => Set<VoucherPassengerAssignment>();
    public DbSet<VoucherAuditEntry> VoucherAuditEntries => Set<VoucherAuditEntry>();

    // Pilar 1: Cotizador + CRM
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteItem> QuoteItems => Set<QuoteItem>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();
    public DbSet<WhatsAppBotConfig> WhatsAppBotConfigs => Set<WhatsAppBotConfig>();
    public DbSet<WhatsAppDelivery> WhatsAppDeliveries => Set<WhatsAppDelivery>();
    public DbSet<MessageDelivery> MessageDeliveries => Set<MessageDelivery>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<BusinessSequence> BusinessSequences => Set<BusinessSequence>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<BnaExchangeRateSnapshot> BnaExchangeRateSnapshots => Set<BnaExchangeRateSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // MassTransit Inbox/Outbox
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.HasPostgresExtension("pgcrypto");

        ConfigurePublicEntity<Customer>(modelBuilder);
        ConfigurePublicEntity<Reserva>(modelBuilder);
        ConfigurePublicEntity<Supplier>(modelBuilder);
        ConfigurePublicEntity<Lead>(modelBuilder);
        ConfigurePublicEntity<LeadActivity>(modelBuilder);
        ConfigurePublicEntity<Quote>(modelBuilder);
        ConfigurePublicEntity<QuoteItem>(modelBuilder);
        ConfigurePublicEntity<Payment>(modelBuilder);
        ConfigurePublicEntity<Invoice>(modelBuilder);
        ConfigurePublicEntity<Passenger>(modelBuilder);
        ConfigurePublicEntity<ServicioReserva>(modelBuilder);
        ConfigurePublicEntity<FlightSegment>(modelBuilder);
        ConfigurePublicEntity<HotelBooking>(modelBuilder);
        ConfigurePublicEntity<PackageBooking>(modelBuilder);
        ConfigurePublicEntity<Rate>(modelBuilder);
        ConfigurePublicEntity<CatalogPackage>(modelBuilder);
        ConfigurePublicEntity<CatalogPackageDeparture>(modelBuilder);
        ConfigurePublicEntity<TransferBooking>(modelBuilder);
        ConfigurePublicEntity<ReservaAttachment>(modelBuilder);
        ConfigurePublicEntity<Voucher>(modelBuilder);
        ConfigurePublicEntity<MessageDelivery>(modelBuilder);
        ConfigurePublicEntity<SupplierPayment>(modelBuilder);
        ConfigurePublicEntity<ManualCashMovement>(modelBuilder);
        ConfigurePublicEntity<PaymentReceipt>(modelBuilder);

        // Supplier
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(s => s.Name).IsRequired().HasMaxLength(100);
            entity.Property(s => s.ContactName).HasMaxLength(100);
            entity.Property(s => s.Email).HasMaxLength(100);
            entity.Property(s => s.Phone).HasMaxLength(50);
        });

        // Customer
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(c => c.FullName).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Email).HasMaxLength(200);
            entity.Property(c => c.DocumentNumber).HasMaxLength(50);
            entity.Property(c => c.Address).HasMaxLength(300);
            entity.HasIndex(c => new { c.IsActive, c.FullName });
        });

        // Reserva (Master) - Mapeado a TravelFiles (Legacy)
        modelBuilder.Entity<Reserva>(entity =>
        {
            entity.ToTable("TravelFiles"); 
            entity.Property(f => f.NumeroReserva).HasColumnName("FileNumber").HasMaxLength(50).IsRequired();
            entity.Property(f => f.Name).HasMaxLength(200).IsRequired();
            entity.Property(f => f.Status).HasMaxLength(50).IsRequired();
            entity.Property(f => f.WhatsAppPhoneOverride).HasMaxLength(50);
            entity.HasIndex(f => f.NumeroReserva).IsUnique();
            entity.HasIndex(f => new { f.Status, f.StartDate, f.CreatedAt });

            entity.HasOne(f => f.Payer)
                  .WithMany(c => c.Reservas)
                  .HasForeignKey(f => f.PayerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(f => f.SourceLead)
                  .WithMany()
                  .HasForeignKey(f => f.SourceLeadId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(f => f.SourceQuote)
                  .WithMany()
                  .HasForeignKey(f => f.SourceQuoteId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(f => f.ResponsibleUser)
                  .WithMany()
                  .HasForeignKey(f => f.ResponsibleUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(f => f.Servicios)
                  .WithOne(r => r.Reserva)
                  .HasForeignKey(r => r.ReservaId)
                  .HasConstraintName("FK_Reservations_TravelFiles_TravelFileId")
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(f => f.Passengers)
                  .WithOne(p => p.Reserva)
                  .HasForeignKey(p => p.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(f => f.Payments)
                  .WithOne(p => p.Reserva)
                  .HasForeignKey(p => p.ReservaId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(f => f.Vouchers)
                  .WithOne(v => v.Reserva)
                  .HasForeignKey(v => v.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(f => f.MessageDeliveries)
                  .WithOne(m => m.Reserva)
                  .HasForeignKey(m => m.ReservaId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Passenger
        modelBuilder.Entity<Passenger>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
            entity.Property(p => p.FullName).HasMaxLength(200).IsRequired();
            entity.Property(p => p.DocumentType).HasMaxLength(20);
            entity.Property(p => p.DocumentNumber).HasMaxLength(50);
            entity.Property(p => p.Nationality).HasMaxLength(50);
            entity.Property(p => p.Phone).HasMaxLength(50);
            entity.Property(p => p.Email).HasMaxLength(200);
            entity.Property(p => p.Gender).HasMaxLength(10);
        });

        // ServicioReserva (Item)
        modelBuilder.Entity<ServicioReserva>(entity =>
        {
            entity.ToTable("Reservations"); 
            entity.Property(r => r.ReservaId).HasColumnName("TravelFileId");
            entity.Property(r => r.Status).HasMaxLength(50).IsRequired();
            entity.Property(r => r.SupplierName).HasMaxLength(200);

            entity.Property(r => r.NetCost).HasPrecision(12, 2);
            entity.Property(r => r.SalePrice).HasPrecision(12, 2);
            entity.Property(r => r.Commission).HasPrecision(12, 2);
            entity.Property(r => r.Tax).HasPrecision(12, 2);

            entity.HasOne(r => r.Customer)
                  .WithMany(c => c.Reservations)
                  .HasForeignKey(r => r.CustomerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.Supplier)
                  .WithMany()
                  .HasForeignKey(r => r.SupplierId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.Rate)
                  .WithMany()
                  .HasForeignKey(r => r.RateId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Payment
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
            entity.Property(p => p.ServicioReservaId).HasColumnName("ReservationId");
            entity.Property(p => p.Amount).HasPrecision(12, 2);
            entity.Property(p => p.Method).HasMaxLength(50).IsRequired();
            entity.Property(p => p.Status).HasMaxLength(50).IsRequired();
            entity.Property(p => p.EntryType).HasMaxLength(50).IsRequired();
            entity.HasIndex(p => p.PaidAt);

            // Filtro global: excluir pagos borrados de todas las consultas
            entity.HasQueryFilter(p => !p.IsDeleted);

            entity.HasOne(p => p.Reserva)
                  .WithMany(r => r.Payments)
                  .HasForeignKey(p => p.ReservaId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(p => p.ServicioReserva)
                  .WithMany(s => s.Payments)
                  .HasForeignKey(p => p.ServicioReservaId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.RelatedInvoice)
                  .WithMany()
                  .HasForeignKey(p => p.RelatedInvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(p => p.OriginalPayment)
                  .WithMany(p => p.Reversals)
                  .HasForeignKey(p => p.OriginalPaymentId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(p => p.Receipt)
                  .WithOne(r => r.Payment)
                  .HasForeignKey<PaymentReceipt>(r => r.PaymentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // FlightSegment
        modelBuilder.Entity<FlightSegment>(entity =>
        {
            entity.Property(s => s.ReservaId).HasColumnName("TravelFileId");
            entity.Property(s => s.ServicioReservaId).HasColumnName("ReservationId");
            entity.Property(s => s.AirlineCode).HasMaxLength(3).IsRequired();
            entity.Property(s => s.FlightNumber).HasMaxLength(10).IsRequired();
            entity.Property(s => s.Origin).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Destination).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Status).HasMaxLength(2);

            entity.HasOne(s => s.Reserva)
                  .WithMany(r => r.FlightSegments)
                  .HasForeignKey(s => s.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // FlightSegment -> Rate (Tarifario)
        modelBuilder.Entity<FlightSegment>()
            .HasOne(f => f.Rate)
            .WithMany()
            .HasForeignKey(f => f.RateId)
            .OnDelete(DeleteBehavior.SetNull);

        // Invoice
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.Property(i => i.ReservaId).HasColumnName("TravelFileId");
            entity.Property(i => i.ForceReason).HasMaxLength(1000);
            entity.Property(i => i.ForcedByUserId).HasMaxLength(200);
            entity.Property(i => i.ForcedByUserName).HasMaxLength(200);
            entity.Property(i => i.OutstandingBalanceAtIssuance).HasPrecision(18, 2);
            entity.HasIndex(i => i.CreatedAt);
        });

        // InvoiceItem (Singular table from Program.cs)
        modelBuilder.Entity<InvoiceItem>(entity =>
        {
            entity.ToTable("InvoiceItem");
        });

        // InvoiceTribute (Singular table from Program.cs)
        modelBuilder.Entity<InvoiceTribute>(entity =>
        {
            entity.ToTable("InvoiceTribute");
        });

        // HotelBooking
        modelBuilder.Entity<HotelBooking>(entity =>
        {
            entity.Property(h => h.ReservaId).HasColumnName("TravelFileId");
        });

        // HotelBooking -> Rate (Tarifario)
        modelBuilder.Entity<HotelBooking>()
            .HasOne(h => h.Rate)
            .WithMany()
            .HasForeignKey(h => h.RateId)
            .OnDelete(DeleteBehavior.SetNull);

        // TransferBooking
        modelBuilder.Entity<TransferBooking>(entity =>
        {
            entity.Property(t => t.ReservaId).HasColumnName("TravelFileId");
        });

        // TransferBooking -> Rate (Tarifario)
        modelBuilder.Entity<TransferBooking>()
            .HasOne(t => t.Rate)
            .WithMany()
            .HasForeignKey(t => t.RateId)
            .OnDelete(DeleteBehavior.SetNull);

        // PackageBooking
        modelBuilder.Entity<PackageBooking>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
        });

        // PackageBooking -> Rate (Tarifario)
        modelBuilder.Entity<PackageBooking>()
            .HasOne(p => p.Rate)
            .WithMany()
            .HasForeignKey(p => p.RateId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CatalogPackage>(entity =>
        {
            entity.ToTable("CatalogPackages");
            entity.Property(package => package.Title).HasMaxLength(200).IsRequired();
            entity.Property(package => package.Slug).HasMaxLength(200).IsRequired();
            entity.Property(package => package.Tagline).HasMaxLength(120);
            entity.Property(package => package.Destination).HasMaxLength(120);
            entity.Property(package => package.CountryName).HasMaxLength(120);
            entity.Property(package => package.CountrySlug).HasMaxLength(120);
            entity.Property(package => package.HeroImageFileName).HasMaxLength(260);
            entity.Property(package => package.HeroImageStoredFileName).HasMaxLength(260);
            entity.Property(package => package.HeroImageContentType).HasMaxLength(120);
            entity.Property(package => package.GeneralInfo).HasMaxLength(8000);
            entity.HasIndex(package => package.Slug).IsUnique();
            entity.HasIndex(package => new { package.IsPublished, package.Slug });
            entity.HasIndex(package => new { package.IsPublished, package.CountrySlug, package.DestinationOrder });
            entity.HasIndex(package => new { package.CountrySlug, package.Destination });

            entity.HasMany(package => package.Departures)
                .WithOne(departure => departure.CatalogPackage)
                .HasForeignKey(departure => departure.CatalogPackageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogPackageDeparture>(entity =>
        {
            entity.ToTable("CatalogPackageDepartures");
            entity.Property(departure => departure.TransportLabel).HasMaxLength(100).IsRequired();
            entity.Property(departure => departure.HotelName).HasMaxLength(200).IsRequired();
            entity.Property(departure => departure.MealPlan).HasMaxLength(100);
            entity.Property(departure => departure.RoomBase).HasMaxLength(50);
            entity.Property(departure => departure.Currency).HasMaxLength(3).IsRequired();
            entity.Property(departure => departure.SalePrice).HasPrecision(18, 2);
            entity.HasIndex(departure => new { departure.CatalogPackageId, departure.StartDate });
        });

        modelBuilder.Entity<Country>(entity =>
        {
            entity.ToTable("Countries");
            entity.Property(country => country.Name).HasMaxLength(120).IsRequired();
            entity.Property(country => country.Slug).HasMaxLength(120).IsRequired();
            entity.Property(country => country.IsPublished).HasDefaultValue(true);
            entity.HasIndex(country => country.PublicId).IsUnique();
            entity.HasIndex(country => country.Slug).IsUnique();
            entity.HasIndex(country => new { country.IsPublished, country.Slug });

            entity.HasMany(country => country.Destinations)
                .WithOne(destination => destination.Country)
                .HasForeignKey(destination => destination.CountryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Destination>(entity =>
        {
            entity.ToTable("Destinations");
            entity.Property(destination => destination.Name).HasMaxLength(120).IsRequired();
            entity.Property(destination => destination.Title).HasMaxLength(200).IsRequired();
            entity.Property(destination => destination.Slug).HasMaxLength(200).IsRequired();
            entity.Property(destination => destination.Tagline).HasMaxLength(120);
            entity.Property(destination => destination.HeroImageFileName).HasMaxLength(260);
            entity.Property(destination => destination.HeroImageStoredFileName).HasMaxLength(260);
            entity.Property(destination => destination.HeroImageContentType).HasMaxLength(120);
            entity.Property(destination => destination.GeneralInfo).HasMaxLength(8000);
            entity.HasIndex(destination => destination.PublicId).IsUnique();
            entity.HasIndex(destination => destination.Slug).IsUnique();
            entity.HasIndex(destination => new { destination.CountryId, destination.DisplayOrder });
            entity.HasIndex(destination => new { destination.CountryId, destination.Name });
            entity.HasIndex(destination => new { destination.IsPublished, destination.CountryId, destination.DisplayOrder });

            entity.HasMany(destination => destination.Departures)
                .WithOne(departure => departure.Destination)
                .HasForeignKey(departure => departure.DestinationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DestinationDeparture>(entity =>
        {
            entity.ToTable("DestinationDepartures");
            entity.Property(departure => departure.TransportLabel).HasMaxLength(100).IsRequired();
            entity.Property(departure => departure.HotelName).HasMaxLength(200).IsRequired();
            entity.Property(departure => departure.MealPlan).HasMaxLength(100);
            entity.Property(departure => departure.RoomBase).HasMaxLength(50);
            entity.Property(departure => departure.Currency).HasMaxLength(3).IsRequired();
            entity.Property(departure => departure.SalePrice).HasPrecision(18, 2);
            entity.HasIndex(departure => departure.PublicId).IsUnique();
            entity.HasIndex(departure => new { departure.DestinationId, departure.StartDate });
        });

        // Quotes
        modelBuilder.Entity<Quote>(entity =>
        {
            entity.Property(q => q.ConvertedReservaId).HasColumnName("ConvertedFileId");
            entity.HasOne(q => q.Lead)
                  .WithMany(l => l.Quotes)
                  .HasForeignKey(q => q.LeadId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<QuoteItem>(entity =>
        {
            entity.HasOne(qi => qi.Rate)
                  .WithMany()
                  .HasForeignKey(qi => qi.RateId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WhatsAppDelivery>(entity =>
        {
            entity.ToTable("WhatsAppDeliveries");
            entity.Property(d => d.Phone).HasMaxLength(50).IsRequired();
            entity.Property(d => d.Kind).HasMaxLength(50).IsRequired();
            entity.Property(d => d.Direction).HasMaxLength(20).IsRequired();
            entity.Property(d => d.Status).HasMaxLength(30).IsRequired();
            entity.Property(d => d.MessageText).HasMaxLength(2000);
            entity.Property(d => d.AttachmentName).HasMaxLength(255);
            entity.Property(d => d.BotMessageId).HasMaxLength(200);
            entity.Property(d => d.CreatedBy).HasMaxLength(200);
            entity.Property(d => d.SentBy).HasMaxLength(200);
            entity.Property(d => d.Error).HasMaxLength(1000);

            entity.HasOne(d => d.Reserva)
                  .WithMany(r => r.WhatsAppDeliveries)
                  .HasForeignKey(d => d.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Customer)
                  .WithMany()
                  .HasForeignKey(d => d.CustomerId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Voucher>(entity =>
        {
            entity.ToTable("Vouchers");
            entity.Property(v => v.Source).HasMaxLength(30).IsRequired();
            entity.Property(v => v.Status).HasMaxLength(30).IsRequired();
            entity.Property(v => v.Scope).HasMaxLength(40).IsRequired();
            entity.Property(v => v.FileName).HasMaxLength(255).IsRequired();
            entity.Property(v => v.StoredFileName).HasMaxLength(500);
            entity.Property(v => v.ContentType).HasMaxLength(120).IsRequired();
            entity.Property(v => v.ExternalOrigin).HasMaxLength(200);
            entity.Property(v => v.CreatedByUserId).HasMaxLength(200);
            entity.Property(v => v.CreatedByUserName).HasMaxLength(200);
            entity.Property(v => v.IssuedByUserId).HasMaxLength(200);
            entity.Property(v => v.IssuedByUserName).HasMaxLength(200);
            entity.Property(v => v.IssueReason).HasMaxLength(1000);
            entity.Property(v => v.ExceptionalReason).HasMaxLength(1000);
            entity.Property(v => v.AuthorizedBySuperiorUserId).HasMaxLength(200);
            entity.Property(v => v.AuthorizedBySuperiorUserName).HasMaxLength(200);
            entity.Property(v => v.OutstandingBalanceAtIssue).HasPrecision(18, 2);
            entity.HasIndex(v => new { v.ReservaId, v.Status });

            entity.HasOne(v => v.Reserva)
                  .WithMany(r => r.Vouchers)
                  .HasForeignKey(v => v.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VoucherPassengerAssignment>(entity =>
        {
            entity.ToTable("VoucherPassengerAssignments");
            entity.HasIndex(a => new { a.VoucherId, a.PassengerId }).IsUnique();

            entity.HasOne(a => a.Voucher)
                  .WithMany(v => v.PassengerAssignments)
                  .HasForeignKey(a => a.VoucherId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Passenger)
                  .WithMany()
                  .HasForeignKey(a => a.PassengerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VoucherAuditEntry>(entity =>
        {
            entity.ToTable("VoucherAuditEntries");
            entity.Property(a => a.Action).HasMaxLength(50).IsRequired();
            entity.Property(a => a.UserId).HasMaxLength(200).IsRequired();
            entity.Property(a => a.UserName).HasMaxLength(200);
            entity.Property(a => a.Reason).HasMaxLength(1000);
            entity.Property(a => a.AuthorizedBySuperiorUserId).HasMaxLength(200);
            entity.Property(a => a.AuthorizedBySuperiorUserName).HasMaxLength(200);
            entity.Property(a => a.Details).HasMaxLength(2000);
            entity.Property(a => a.OutstandingBalance).HasPrecision(18, 2);
            entity.HasIndex(a => new { a.ReservaId, a.OccurredAt });

            entity.HasOne(a => a.Voucher)
                  .WithMany(v => v.AuditEntries)
                  .HasForeignKey(a => a.VoucherId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Reserva)
                  .WithMany()
                  .HasForeignKey(a => a.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageDelivery>(entity =>
        {
            entity.ToTable("MessageDeliveries");
            entity.Property(m => m.Channel).HasMaxLength(30).IsRequired();
            entity.Property(m => m.Kind).HasMaxLength(30).IsRequired();
            entity.Property(m => m.Status).HasMaxLength(30).IsRequired();
            entity.Property(m => m.Phone).HasMaxLength(50).IsRequired();
            entity.Property(m => m.MessageText).HasMaxLength(2000);
            entity.Property(m => m.AttachmentName).HasMaxLength(255);
            entity.Property(m => m.BotMessageId).HasMaxLength(200);
            entity.Property(m => m.SentByUserId).HasMaxLength(200);
            entity.Property(m => m.SentByUserName).HasMaxLength(200);
            entity.Property(m => m.Error).HasMaxLength(1000);
            entity.HasIndex(m => new { m.ReservaId, m.CreatedAt });

            entity.HasOne(m => m.Reserva)
                  .WithMany(r => r.MessageDeliveries)
                  .HasForeignKey(m => m.ReservaId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(m => m.Passenger)
                  .WithMany()
                  .HasForeignKey(m => m.PassengerId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(m => m.Customer)
                  .WithMany()
                  .HasForeignKey(m => m.CustomerId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(m => m.Voucher)
                  .WithMany()
                  .HasForeignKey(m => m.VoucherId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAt });
            entity.Property(token => token.TokenHash).HasMaxLength(256).IsRequired();
            entity.Property(token => token.UserId).HasMaxLength(450).IsRequired();
            entity.Property(token => token.CreatedByIp).HasMaxLength(64);
            entity.Property(token => token.UserAgent).HasMaxLength(512);
            entity.Property(token => token.ReplacedByTokenHash).HasMaxLength(256);

            entity.HasOne(token => token.User)
                  .WithMany(user => user.RefreshTokens)
                  .HasForeignKey(token => token.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ReservaAttachment
        modelBuilder.Entity<ReservaAttachment>(entity =>
        {
            entity.ToTable("ReservaAttachments");
            // Usamos ReservaId directamente (estándar nuevo)
            entity.Property(a => a.ReservaId).IsRequired();
        });

        // SupplierPayment (Egresos)
        modelBuilder.Entity<SupplierPayment>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
            entity.Property(p => p.ServicioReservaId).HasColumnName("ReservationId");
        });

        modelBuilder.Entity<OperationalFinanceSettings>(entity =>
        {
            entity.Property(s => s.AfipInvoiceControlMode).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<PaymentReceipt>(entity =>
        {
            entity.Property(r => r.Amount).HasPrecision(18, 2);
            entity.Property(r => r.ReceiptNumber).HasMaxLength(50).IsRequired();
            entity.Property(r => r.Status).HasMaxLength(30).IsRequired();

            entity.HasOne(r => r.Reserva)
                  .WithMany()
                  .HasForeignKey(r => r.ReservaId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ManualCashMovement>(entity =>
        {
            entity.Property(m => m.Direction).HasMaxLength(20).IsRequired();
            entity.Property(m => m.Amount).HasPrecision(18, 2);
            entity.Property(m => m.Method).HasMaxLength(50).IsRequired();
            entity.Property(m => m.Category).HasMaxLength(100).IsRequired();
            entity.Property(m => m.Description).HasMaxLength(500).IsRequired();
            entity.Property(m => m.Reference).HasMaxLength(100);
            entity.Property(m => m.CreatedBy).HasMaxLength(200).IsRequired();
            entity.HasIndex(m => m.OccurredAt);

            entity.HasOne(m => m.RelatedReserva)
                  .WithMany(r => r.ManualCashMovements)
                  .HasForeignKey(m => m.RelatedReservaId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(m => m.RelatedSupplier)
                  .WithMany()
                  .HasForeignKey(m => m.RelatedSupplierId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BusinessSequence>(entity =>
        {
            entity.ToTable("BusinessSequences");
            entity.Property(sequence => sequence.DocumentType).HasMaxLength(100).IsRequired();
            entity.Property(sequence => sequence.LastValue).IsRequired();
            entity.HasIndex(sequence => new { sequence.DocumentType, sequence.Year }).IsUnique();
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("RolePermissions");
            entity.Property(rp => rp.RoleName).HasMaxLength(100).IsRequired();
            entity.Property(rp => rp.Permission).HasMaxLength(100).IsRequired();
            entity.HasIndex(rp => new { rp.RoleName, rp.Permission }).IsUnique();
        });

        modelBuilder.Entity<BnaExchangeRateSnapshot>(entity =>
        {
            entity.ToTable("BnaExchangeRateSnapshots");
            entity.Property(snapshot => snapshot.UsdSeller).HasPrecision(18, 2);
            entity.Property(snapshot => snapshot.EuroSeller).HasPrecision(18, 2);
            entity.Property(snapshot => snapshot.RealSeller).HasPrecision(18, 2);
            entity.Property(snapshot => snapshot.PublishedDate).HasMaxLength(20).IsRequired();
            entity.Property(snapshot => snapshot.PublishedTime).HasMaxLength(10).IsRequired();
            entity.Property(snapshot => snapshot.Source).HasMaxLength(500).IsRequired();
        });
    }

    private static void ConfigurePublicEntity<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IHasPublicId
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.Property(e => e.PublicId)
                .HasColumnType("uuid");
            entity.HasIndex(e => e.PublicId).IsUnique();
        });
    }
}
