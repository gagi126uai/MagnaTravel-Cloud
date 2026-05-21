using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MassTransit;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;

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
    public DbSet<PassengerServiceAssignment> PassengerServiceAssignments => Set<PassengerServiceAssignment>();
    public DbSet<ReservaStatusChangeLog> ReservaStatusChangeLogs => Set<ReservaStatusChangeLog>();

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
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ApprovalPolicy> ApprovalPolicies => Set<ApprovalPolicy>();

    // FC1 (ADR-002, 2026-05-13): modulo cancelacion/refund — 3 aggregate roots
    // + 3 children. Configuracion fluida en OnModelCreating (CHECK constraints,
    // unique partial index y xmin concurrency token en la migracion EF).
    public DbSet<BookingCancellation> BookingCancellations => Set<BookingCancellation>();
    public DbSet<OperatorRefundReceived> OperatorRefundReceived => Set<OperatorRefundReceived>();
    public DbSet<OperatorRefundAllocation> OperatorRefundAllocations => Set<OperatorRefundAllocation>();
    public DbSet<DeductionLine> DeductionLines => Set<DeductionLine>();
    public DbSet<ClientCreditEntry> ClientCreditEntries => Set<ClientCreditEntry>();
    public DbSet<ClientCreditWithdrawal> ClientCreditWithdrawals => Set<ClientCreditWithdrawal>();

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
        ConfigurePublicEntity<PassengerServiceAssignment>(modelBuilder);
        ConfigurePublicEntity<ReservaStatusChangeLog>(modelBuilder);

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
            entity.Property(c => c.DocumentType).HasMaxLength(20);
            entity.Property(c => c.DocumentNumber).HasMaxLength(50);
            entity.Property(c => c.Address).HasMaxLength(300);
            entity.HasIndex(c => new { c.IsActive, c.FullName });

            // Unicidad parcial: un mismo (TipoDoc, NumDoc) no puede repetirse cuando ambos estan presentes.
            entity.HasIndex(c => new { c.DocumentType, c.DocumentNumber })
                  .IsUnique()
                  .HasFilter("\"DocumentNumber\" IS NOT NULL AND \"DocumentType\" IS NOT NULL");
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

            // FK formal a AspNetUsers se declara desde Infrastructure (sin nav prop en Domain)
            // para mantener Reserva libre de dependencias de ASP.NET Identity.
            entity.HasOne<ApplicationUser>()
                  .WithMany()
                  .HasForeignKey(f => f.ResponsibleUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.Property(f => f.ResponsibleUserName).HasMaxLength(200);

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

            // B1.15 Fase 1: trazabilidad de quien creo el pago.
            entity.Property(p => p.CreatedByUserId).HasMaxLength(450);
            entity.Property(p => p.CreatedByUserName).HasMaxLength(200);

            entity.HasOne<ApplicationUser>()
                  .WithMany()
                  .HasForeignKey(p => p.CreatedByUserId)
                  .OnDelete(DeleteBehavior.SetNull);
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

            // C24: bloquear el delete fisico del Supplier mientras existan segmentos
            // de vuelo asociados. La validacion de negocio vive en SupplierService;
            // este Restrict es la red de seguridad a nivel BD.
            entity.HasOne(s => s.Supplier)
                  .WithMany()
                  .HasForeignKey(s => s.SupplierId)
                  .IsRequired()
                  .OnDelete(DeleteBehavior.Restrict);
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

            // B1.15 Fase 1: trazabilidad de quien emitio la factura.
            entity.Property(i => i.IssuedByUserId).HasMaxLength(450);
            entity.Property(i => i.IssuedByUserName).HasMaxLength(200);

            entity.HasOne<ApplicationUser>()
                  .WithMany()
                  .HasForeignKey(i => i.IssuedByUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            // B1.15 Fase 2a (FIX 6): trazabilidad y estado de la anulacion.
            entity.Property(i => i.AnnulledByUserId).HasMaxLength(450);
            entity.Property(i => i.AnnulledByUserName).HasMaxLength(200);
            entity.Property(i => i.AnnulmentReason).HasMaxLength(500);
            entity.Property(i => i.AnnulmentStatus)
                  .HasConversion<int>()
                  .HasDefaultValue(AnnulmentStatus.None);

            entity.HasOne<ApplicationUser>()
                  .WithMany()
                  .HasForeignKey(i => i.AnnulledByUserId)
                  .OnDelete(DeleteBehavior.SetNull);

            // FC1.2.0 v3 §10.1 (BR-V2-03, 2026-05-17): cross-reference fiscal.
            // Restrict para preservar trazabilidad: no permitir borrar el
            // ApprovalRequest si hay Invoices anuladas vinculadas. Si fuera
            // SetNull, perderiamos el rastro del approval que valido la NC.
            entity.HasOne(i => i.AnnulmentApprovalRequest)
                  .WithMany()
                  .HasForeignKey(i => i.AnnulmentApprovalRequestId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Indice nullable para queries de auditoria fiscal:
            //   "todas las NCs aprobadas por approval X" / contador.
            entity.HasIndex(i => i.AnnulmentApprovalRequestId)
                  .HasDatabaseName("IX_Invoices_AnnulmentApprovalRequestId");
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

            // C24: ver nota en FlightSegment.
            entity.HasOne(h => h.Supplier)
                  .WithMany()
                  .HasForeignKey(h => h.SupplierId)
                  .IsRequired()
                  .OnDelete(DeleteBehavior.Restrict);
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

            // C24: ver nota en FlightSegment.
            entity.HasOne(t => t.Supplier)
                  .WithMany()
                  .HasForeignKey(t => t.SupplierId)
                  .IsRequired()
                  .OnDelete(DeleteBehavior.Restrict);
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

            // C24: ver nota en FlightSegment.
            entity.HasOne(p => p.Supplier)
                  .WithMany()
                  .HasForeignKey(p => p.SupplierId)
                  .IsRequired()
                  .OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(v => v.RevokedByUserId).HasMaxLength(200);
            entity.Property(v => v.RevokedByUserName).HasMaxLength(200);
            entity.Property(v => v.RevocationReason).HasMaxLength(1000);
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

        modelBuilder.Entity<PassengerServiceAssignment>(entity =>
        {
            entity.ToTable("PassengerServiceAssignments");
            entity.Property(a => a.ServiceType).HasMaxLength(20).IsRequired();
            entity.Property(a => a.SeatNumber).HasMaxLength(20);
            entity.Property(a => a.Notes).HasMaxLength(500);
            // Un pasajero no puede estar asignado dos veces al mismo servicio.
            entity.HasIndex(a => new { a.PassengerId, a.ServiceType, a.ServiceId }).IsUnique();
            // Indice por servicio para listar pasajeros de un booking eficientemente.
            entity.HasIndex(a => new { a.ServiceType, a.ServiceId });

            entity.HasOne(a => a.Passenger)
                  .WithMany()
                  .HasForeignKey(a => a.PassengerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReservaStatusChangeLog>(entity =>
        {
            entity.ToTable("ReservaStatusChangeLogs");
            entity.HasIndex(l => new { l.ReservaId, l.OccurredAt });
            entity.HasOne(l => l.Reserva)
                  .WithMany()
                  .HasForeignKey(l => l.ReservaId)
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

            // FK formal a AspNetUsers se declara desde Infrastructure. La coleccion inversa
            // (ApplicationUser.RefreshTokens) tambien vive en Infrastructure, por lo que
            // RefreshToken (Domain) no tiene nav prop al usuario.
            entity.HasOne<ApplicationUser>()
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

            // B1.15 Fase 0' (CODE-10 / INV-2): query filter para excluir pagos
            // proveedor soft-deleted. Indices auxiliares para cuentas corrientes
            // que ahora deben filtrar `IsDeleted = false` antes de sumar.
            entity.HasQueryFilter(p => !p.IsDeleted);
            entity.HasIndex(p => p.IsDeleted);
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

        // B1.15 Fase B' (2026-05-11): solicitudes de aprobacion polimorficas.
        ConfigurePublicEntity<ApprovalRequest>(modelBuilder);
        modelBuilder.Entity<ApprovalRequest>(entity =>
        {
            entity.ToTable("ApprovalRequests");
            entity.Property(a => a.RequestType).HasConversion<int>();
            entity.Property(a => a.Status).HasConversion<int>();
            // Indice principal: bandeja por estado (lista pending para reviewer).
            entity.HasIndex(a => a.Status);
            // "Mis solicitudes": filtrar por usuario + estado.
            entity.HasIndex(a => new { a.RequestedByUserId, a.Status });
            // Idempotencia + cooldown: matchea por entidad objetivo.
            entity.HasIndex(a => new { a.EntityType, a.EntityId, a.Status });
            // Job nightly de expiracion: filter por ExpiresAt + Status pending/approved.
            entity.HasIndex(a => a.ExpiresAt);

            // FC1.3.0a (ADR-009 §2.2 punto 10 / RH-006, 2026-05-21): concurrency
            // token via xmin para proteger la edicion concurrente del Metadata
            // por parte de dos admins en paralelo (ej. ambos editan la
            // liquidacion partial NC al mismo tiempo desde la bandeja).
            //
            // Por que aca y no en FC1.3.0: la edicion admin del Metadata es la
            // unica via mediante la cual un approval pending puede mutar; sin
            // xmin, el "last write wins" pisa silenciosamente cambios fiscales.
            // Migracion M0 separada para permitir hotfix antes del resto FC1.3.
            //
            // Nota tecnica: UseXminAsConcurrencyToken NO agrega columna fisica
            // (xmin es pseudo-columna nativa de Postgres en TODAS las tablas) —
            // solo registra una shadow property uint en el modelo EF para que
            // los UPDATE/DELETE comparen contra ella y tiren
            // DbUpdateConcurrencyException si la fila cambio.
            entity.UseXminAsConcurrencyToken();
        });

        // B1.15 Fase B'' (2026-05-11): policies por RequestType.
        modelBuilder.Entity<ApprovalPolicy>(entity =>
        {
            entity.ToTable("ApprovalPolicies");
            entity.HasIndex(p => p.RequestType).IsUnique();
        });

        ConfigureCancellationModule(modelBuilder);
    }

    /// <summary>
    /// FC1 (ADR-002, 2026-05-13): configuracion EF del modulo de cancelacion/refund.
    /// 6 entidades nuevas + relaciones explicitas + concurrency tokens (xmin) +
    /// owned entity FiscalSnapshot. Los CHECK constraints SQL y el unique partial
    /// index se aplican via <c>migrationBuilder.Sql(...)</c> en la migracion porque
    /// EF Core no tiene API fluida para CHECK ni para indices WHERE.
    ///
    /// IMPORTANTE — UseXminAsConcurrencyToken (Npgsql 8.x):
    ///  - Crea una shadow property <c>uint</c> mapeada a la columna pseudo-de-sistema
    ///    <c>xmin</c> de Postgres (id de transaccion que modifico la fila).
    ///  - EF la usa como concurrency token: si dos sesiones concurrentes leen el mismo
    ///    BC y ambas intentan SaveChanges, la segunda lanza
    ///    <c>DbUpdateConcurrencyException</c>. El caller decide reintentar o reportar 409.
    ///  - No hay que agregar columna en la entidad ni en la migracion — xmin existe
    ///    en TODAS las tablas Postgres automaticamente.
    /// </summary>
    private static void ConfigureCancellationModule(ModelBuilder modelBuilder)
    {
        // Public IDs (Guid uuid en BD + unique index, patron del repo).
        ConfigurePublicEntity<BookingCancellation>(modelBuilder);
        ConfigurePublicEntity<OperatorRefundReceived>(modelBuilder);
        ConfigurePublicEntity<OperatorRefundAllocation>(modelBuilder);
        ConfigurePublicEntity<DeductionLine>(modelBuilder);
        ConfigurePublicEntity<ClientCreditEntry>(modelBuilder);
        ConfigurePublicEntity<ClientCreditWithdrawal>(modelBuilder);

        // ===== BookingCancellation (aggregate root) =====
        modelBuilder.Entity<BookingCancellation>(entity =>
        {
            entity.ToTable("BookingCancellations");
            entity.Property(b => b.Status).HasConversion<int>();
            entity.Property(b => b.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(b => b.DraftedByUserId).HasMaxLength(450).IsRequired();
            entity.Property(b => b.DraftedByUserName).HasMaxLength(200);
            entity.Property(b => b.ConfirmedByUserId).HasMaxLength(450);
            entity.Property(b => b.ConfirmedByUserName).HasMaxLength(200);

            // FC1.2.1 (BR-V2-01): campos del escape hatch manual ARCA.
            entity.Property(b => b.ArcaConfirmedManuallyByUserId).HasMaxLength(450);
            entity.Property(b => b.ArcaErrorMessage).HasMaxLength(1000);

            entity.Property(b => b.AmountPaidAtCancellation).HasPrecision(18, 2);
            entity.Property(b => b.EstimatedRefundAmount).HasPrecision(18, 2);
            entity.Property(b => b.ReceivedRefundAmount).HasPrecision(18, 2);

            // INV-081: una sola cancelacion por reserva. Adicionalmente el CHECK
            // de Reservas.Status garantiza que el valor PendingOperatorRefund
            // sea valido (ver migracion SQL).
            entity.HasIndex(b => b.ReservaId).IsUnique();

            // INV-100 (review BR4, 2026-05-14): la factura original que se anula no
            // puede pertenecer a dos cancelaciones distintas. Sin este UNIQUE seria
            // posible reabrir una cancelacion sobre la misma factura A y generar
            // dos NCs huerfanas — incidente fiscal grave.
            entity.HasIndex(b => b.OriginatingInvoiceId)
                  .IsUnique()
                  .HasDatabaseName("IX_BookingCancellations_OriginatingInvoiceId");

            entity.HasOne(b => b.Reserva)
                  .WithMany()
                  .HasForeignKey(b => b.ReservaId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(b => b.Customer)
                  .WithMany()
                  .HasForeignKey(b => b.CustomerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(b => b.Supplier)
                  .WithMany()
                  .HasForeignKey(b => b.SupplierId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(b => b.OriginatingInvoice)
                  .WithMany()
                  .HasForeignKey(b => b.OriginatingInvoiceId)
                  .OnDelete(DeleteBehavior.Restrict);

            // NC opcional: existe solo despues de T0. SetNull para que el rollback
            // de una NC (caso raro) no rompa el aggregate.
            entity.HasOne(b => b.CreditNoteInvoice)
                  .WithMany()
                  .HasForeignKey(b => b.CreditNoteInvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            // FiscalSnapshot owned: columnas con prefijo "FiscalSnapshot_" en la
            // misma tabla. ExtrasJson queda como text — no se mapea como jsonb
            // por ahora (revisitar cuando haya casos de filtrado por contenido).
            entity.OwnsOne(b => b.FiscalSnapshot, snap =>
            {
                snap.Property(s => s.ExchangeRateAtOriginalInvoice).HasPrecision(18, 6);
                snap.Property(s => s.ExchangeRateAtOperatorRefundReceipt).HasPrecision(18, 6);
                snap.Property(s => s.ExchangeRateAtClientWithdrawal).HasPrecision(18, 6);
                snap.Property(s => s.Source).HasConversion<int>();
            });

            // Concurrency lock-free (ADR-002 §2.5 / B11). Pre-requisito FC1.1
            // verificado: Npgsql 8.x soporta xmin nativamente.
            entity.UseXminAsConcurrencyToken();
        });

        // ===== OperatorRefundReceived (aggregate root) =====
        modelBuilder.Entity<OperatorRefundReceived>(entity =>
        {
            entity.ToTable("OperatorRefundsReceived");
            entity.Property(r => r.ReceivedAmount).HasPrecision(18, 2);
            entity.Property(r => r.AllocatedAmount).HasPrecision(18, 2);
            entity.Property(r => r.ExchangeRateAtReceipt).HasPrecision(18, 6);
            entity.Property(r => r.Method).HasMaxLength(50).IsRequired();
            entity.Property(r => r.Reference).HasMaxLength(100);
            entity.Property(r => r.Currency).HasMaxLength(3).IsRequired();
            entity.Property(r => r.ReceivedByUserId).HasMaxLength(450).IsRequired();
            entity.Property(r => r.ReceivedByUserName).HasMaxLength(200).IsRequired();

            entity.HasOne(r => r.Supplier)
                  .WithMany()
                  .HasForeignKey(r => r.SupplierId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(r => r.Allocations)
                  .WithOne(a => a.Refund)
                  .HasForeignKey(a => a.OperatorRefundReceivedId)
                  // Restrict: si hay allocations historicas no se borra el ingreso fisico.
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(r => r.ReceivedAt);
            entity.HasIndex(r => r.SupplierId);

            entity.UseXminAsConcurrencyToken();
        });

        // ===== OperatorRefundAllocation (relacion N:M con BC) =====
        modelBuilder.Entity<OperatorRefundAllocation>(entity =>
        {
            entity.ToTable("OperatorRefundAllocations");
            entity.Property(a => a.GrossAmount).HasPrecision(18, 2);
            entity.Property(a => a.NetAmount).HasPrecision(18, 2);
            entity.Property(a => a.CreatedByUserId).HasMaxLength(450).IsRequired();
            // FC1.2.2: metadata del void (anulacion / reassociate).
            entity.Property(a => a.VoidedByUserId).HasMaxLength(450);
            entity.Property(a => a.VoidedReason).HasMaxLength(500);

            entity.HasOne(a => a.BookingCancellation)
                  .WithMany()
                  .HasForeignKey(a => a.BookingCancellationId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Self-reference para la cadena "voids/reemplaza a" — SetNull para que
            // un rollback de la nueva no destruya el rastro de la original voided.
            entity.HasOne(a => a.VoidsAllocation)
                  .WithMany()
                  .HasForeignKey(a => a.VoidsAllocationId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(a => a.Deductions)
                  .WithOne(d => d.Allocation)
                  .HasForeignKey(d => d.OperatorRefundAllocationId)
                  // Cascade porque DeductionLine es child sin sentido sin la allocation.
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(a => a.BookingCancellationId);
            // El unique partial index "WHERE NOT IsVoided" se aplica con SQL
            // crudo en la migracion (EF no tiene API fluida para indices filtrados).
        });

        // ===== DeductionLine (child 1:N) =====
        modelBuilder.Entity<DeductionLine>(entity =>
        {
            entity.ToTable("DeductionLines");
            entity.Property(d => d.Kind).HasConversion<int>();
            entity.Property(d => d.Amount).HasPrecision(18, 2);
            entity.Property(d => d.CertificateNumber).HasMaxLength(50);
            entity.Property(d => d.CertificatePdfUrl).HasMaxLength(500);
            entity.Property(d => d.Jurisdiction).HasMaxLength(50);
            entity.Property(d => d.ForeignCountryCode).HasMaxLength(2);
            entity.Property(d => d.Description).HasMaxLength(500);
            entity.Property(d => d.SupportingDocumentRef).HasMaxLength(200);
            entity.Property(d => d.JustificationComment).HasMaxLength(1000);
            entity.Property(d => d.Comment).HasMaxLength(1000);

            entity.HasIndex(d => d.OperatorRefundAllocationId);
            entity.HasIndex(d => d.Kind);
        });

        // ===== ClientCreditEntry (aggregate root, vive en Customer) =====
        modelBuilder.Entity<ClientCreditEntry>(entity =>
        {
            entity.ToTable("ClientCreditEntries");
            entity.Property(c => c.CreditedAmount).HasPrecision(18, 2);
            entity.Property(c => c.RemainingBalance).HasPrecision(18, 2);

            entity.HasOne(c => c.Customer)
                  .WithMany()
                  .HasForeignKey(c => c.CustomerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(c => c.Allocation)
                  .WithMany()
                  .HasForeignKey(c => c.OperatorRefundAllocationId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(c => c.BookingCancellation)
                  .WithMany()
                  .HasForeignKey(c => c.BookingCancellationId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(c => c.Withdrawals)
                  .WithOne(w => w.Entry)
                  .HasForeignKey(w => w.ClientCreditEntryId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(c => c.CustomerId);
            entity.HasIndex(c => new { c.CustomerId, c.IsFullyConsumed });

            entity.UseXminAsConcurrencyToken();
        });

        // ===== ClientCreditWithdrawal (child 1:N) =====
        modelBuilder.Entity<ClientCreditWithdrawal>(entity =>
        {
            entity.ToTable("ClientCreditWithdrawals");
            entity.Property(w => w.Amount).HasPrecision(18, 2);
            entity.Property(w => w.Kind).HasConversion<int>();
            entity.Property(w => w.ExecutedByUserId).HasMaxLength(450).IsRequired();
            entity.Property(w => w.ExecutedByUserName).HasMaxLength(200).IsRequired();
            entity.Property(w => w.ApprovalRequestId).HasMaxLength(64);

            // SetNull: si el ManualCashMovement se anula, conservamos la fila de
            // retiro pero perdemos el link al egreso fisico (auditable).
            entity.HasOne(w => w.ManualCashMovement)
                  .WithMany()
                  .HasForeignKey(w => w.ManualCashMovementId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(w => w.ExecutedAt);
        });

        // ===== ManualCashMovement: 2 FKs nuevas hacia el modulo de cancelacion =====
        // Estos linkean los egresos/ingresos fisicos generados por T2/T3 de modo
        // que aparezcan en TreasuryService.GetCashSummaryAsync (bug INV-CONT-09).
        modelBuilder.Entity<ManualCashMovement>(entity =>
        {
            entity.HasOne(m => m.ClientCreditWithdrawal)
                  .WithMany()
                  .HasForeignKey(m => m.ClientCreditWithdrawalId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(m => m.OperatorRefundReceived)
                  .WithMany()
                  .HasForeignKey(m => m.OperatorRefundReceivedId)
                  .OnDelete(DeleteBehavior.SetNull);
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
