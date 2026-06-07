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
    // Bloque 3: Asistencia al viajero (seguro). Tipo de servicio propio, espejo de HotelBooking.
    public DbSet<AssistanceBooking> AssistanceBookings => Set<AssistanceBooking>();
    public DbSet<Rate> Rates => Set<Rate>();
    // ADR-017 F1.1 (catalogo find-or-create, 2026-06-05): memoria "ultima venta por producto y
    // operador". En F1.1 nace vacia (el upsert que la llena es F1.3). Config en OnModelCreating.
    public DbSet<RateSupplierSale> RateSupplierSales => Set<RateSupplierSale>();
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

    // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.2, 2026-05-27): tabla operacional
    // (no fiscal) para idempotencia de emision de NC parcial al ARCA. Evita doble-POST
    // si Hangfire reintenta el job. Configuracion (indice UNIQUE) en OnModelCreating.
    public DbSet<ArcaIdempotencyKey> ArcaIdempotencyKeys => Set<ArcaIdempotencyKey>();

    // FC1.3 Fase 3 (ADR-010, 2026-05-29): bandeja de reconciliacion de NC parciales
    // con recibos vivos. El caso (padre) nace junto al Payment reversal en
    // AfipService.ApplyPartialCreditNoteReversalAsync; las hijas son el snapshot de
    // recibos vivos. Configuracion (FKs, indice unico, CHECK, xmin) en OnModelCreating.
    public DbSet<PartialCreditNoteReconciliation> PartialCreditNoteReconciliations => Set<PartialCreditNoteReconciliation>();
    public DbSet<PartialCreditNoteReconciliationReceipt> PartialCreditNoteReconciliationReceipts => Set<PartialCreditNoteReconciliationReceipt>();

    // ADR-019 (2026-06-06): descartes manuales ("Listo") del aviso "Proximos inicios" de la
    // campanita. Una fila por reserva (UNIQUE), borrado en cascada con la reserva. Configuracion
    // (FK cascade + indice UNIQUE) en OnModelCreating.
    public DbSet<UpcomingStartAlertDismissal> UpcomingStartAlertDismissals => Set<UpcomingStartAlertDismissal>();

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
        ConfigurePublicEntity<AssistanceBooking>(modelBuilder);
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

            // ADR-013 (2026-06-01): "quien se queda la penalidad". Enum como int,
            // consistente con el resto del modulo. Default Operator (pass-through) lo
            // pone la migracion a nivel BD para que las filas existentes queden en el
            // valor conservador (= NO ND).
            entity.Property(s => s.PenaltyOwnership).HasConversion<int>();
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

            // Rediseño maquina de estados (Fase A+B, 2026-05-30): NO usamos concurrency token (xmin)
            // en Reserva. Se evaluo en el review y se descarto: el token se activa SIEMPRE (no depende
            // del flag EnableSoldToSettleStates), asi que expondria caminos viejos read-modify-write
            // (cancelacion con llamadas largas a ARCA, recalculo de balance, etc.) a
            // DbUpdateConcurrencyException que hoy NO ocurre. Eso seria una regresion que viaja con el
            // flag apagado. La concurrencia job-vs-cajero es un problema preexistente de baja frecuencia
            // y se maneja en el job con un re-chequeo defensivo del estado origen (ver
            // ReservaLifecycleAutomationService). Mejora futura: locking optimista mas fino, fuera de scope.
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
            // ADR-018 (2026-06-06): los 4 campos estructurados dejan de ser NOT NULL. La ficha
            // "producto-primero" identifica el vuelo con ProductName (un solo texto) y no carga
            // aerolinea/nro/origen/destino por separado. Se mantiene el HasMaxLength.
            entity.Property(s => s.AirlineCode).HasMaxLength(3).IsRequired(false);
            entity.Property(s => s.FlightNumber).HasMaxLength(10).IsRequired(false);
            entity.Property(s => s.Origin).HasMaxLength(3).IsRequired(false);
            entity.Property(s => s.Destination).HasMaxLength(3).IsRequired(false);
            entity.Property(s => s.ProductName).HasMaxLength(200);
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

            // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.5, 2026-05-27): moneda del
            // comprobante. NOT NULL con default a nivel BD ('PES' / 1) para que las
            // filas existentes (FC1.2) y los callers que no setean estos campos queden
            // en pesos. El HasDefaultValue ademas mantiene migracion <-> snapshot
            // coherentes: sin el, EF detecta drift entre el defaultValue de la
            // migracion y el modelo (mismo patron que FiscalLiquidation_Currency).
            //
            // Estas columnas son INERTES en esta etapa: el SOAP de AfipService sigue
            // mandando 'PES'/1 hardcoded. El uso real es F2.5.
            entity.Property(i => i.MonId)
                  .HasMaxLength(3)
                  .HasDefaultValue("PES");
            entity.Property(i => i.MonCotiz)
                  .HasPrecision(18, 6)
                  .HasDefaultValue(1m);

            // FC1.3 Fase 2 (Fase2_M2, 2026-05-28): huella real de idempotencia de la NC
            // parcial. NULLABLE y SIN default: las NC viejas no la tienen (caen al
            // fallback de re-derivacion del job de reconciliacion). MaxLength 64 = mismo
            // ancho que ArcaIdempotencyKey.Key (un SHA256 en hex son 64 chars exactos).
            // NO indexamos: el lookup contra ArcaIdempotencyKeys se hace por su columna
            // Key (que YA tiene su UNIQUE); aca solo guardamos el valor para leerlo.
            entity.Property(i => i.IdempotencyKey)
                  .HasMaxLength(64);

            // ADR-012 MVP (facturar en dolares, 2026-05-29): trazabilidad del TC para
            // facturas en moneda extranjera. Las tres columnas son NULLABLE (las facturas
            // en pesos las dejan en NULL). El enum Source se persiste como int igual que
            // FiscalSnapshot.Source, para que la auditoria fiscal lea ambos del mismo modo.
            entity.Property(i => i.ExchangeRateSource)
                  .HasConversion<int>();
            entity.Property(i => i.ExchangeRateJustification)
                  .HasMaxLength(500);

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

            // FC1.3 (ADR-009 §2.3.2, 2026-05-21): persistir el enum como int en
            // BD (consistencia con el resto de enums del modulo: BookingCancellationStatus,
            // ApprovalRequestType, etc.).
            entity.Property(i => i.ItemCategory).HasConversion<int>();

            // FC1.3: FK opcional a ServicioReserva.
            //
            // OnDelete: Restrict — preservar la trazabilidad linea-de-factura -> servicio
            // origen incluso si alguien intenta borrar el servicio. Si la cancelacion FC1.3
            // necesita re-leer de donde viene la linea, no nos podemos quedar sin la FK.
            // Si la trazabilidad estorba para borrar, la operacion legitima la rompe
            // explicitamente (UPDATE InvoiceItem SET SourceServicioReservaId = NULL).
            entity.HasOne(i => i.SourceServicioReserva)
                  .WithMany()
                  .HasForeignKey(i => i.SourceServicioReservaId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Indice para queries inversas tipo "que items facture para este servicio".
            entity.HasIndex(i => i.SourceServicioReservaId)
                  .HasDatabaseName("IX_InvoiceItem_SourceServicioReservaId");
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

        // AssistanceBooking (Bloque 3). Espejo de HotelBooking: ReservaId mapea a la columna
        // legacy TravelFileId, FK al Supplier con Restrict (red de seguridad C24) y FK al Rate
        // con SetNull. El cascade Reserva -> Asistencia se declara via la relacion (abajo).
        modelBuilder.Entity<AssistanceBooking>(entity =>
        {
            entity.Property(a => a.ReservaId).HasColumnName("TravelFileId");

            // C24: ver nota en FlightSegment. Bloquear borrado fisico de la aseguradora
            // (Supplier) mientras tenga asistencias asociadas.
            entity.HasOne(a => a.Supplier)
                  .WithMany()
                  .HasForeignKey(a => a.SupplierId)
                  .IsRequired()
                  .OnDelete(DeleteBehavior.Restrict);

            // La relacion con Reserva se declara explicitamente para fijar el cascade igual
            // que los otros bookings tipados (HotelBooking etc.): al borrar la Reserva se
            // borran sus asistencias. ReservaService igual hace RemoveRange explicito.
            entity.HasOne(a => a.Reserva)
                  .WithMany(r => r.AssistanceBookings)
                  .HasForeignKey(a => a.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // AssistanceBooking -> Rate (Tarifario)
        modelBuilder.Entity<AssistanceBooking>()
            .HasOne(a => a.Rate)
            .WithMany()
            .HasForeignKey(a => a.RateId)
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

            // FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): mappings de las 3
            // columnas que el job de reconciliacion bridge usa para tracking de
            // reintentos. BridgeRetryCount tiene default explicito 0 para que
            // las filas legacy backfilleadas por la migracion arranquen "sin
            // intentos previos". BridgeLastError limitado a 2000 chars para no
            // explotar el log con stack traces enormes. BridgeLastAttemptAt es
            // timestamptz para alinearse con el resto de timestamps de la tabla.
            entity.Property(a => a.BridgeRetryCount).HasDefaultValue(0);
            entity.Property(a => a.BridgeLastError).HasMaxLength(2000);
            entity.Property(a => a.BridgeLastAttemptAt).HasColumnType("timestamp with time zone");
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

        // FC1.3 Fase 3 (ADR-010): el caso de reconciliacion tiene PublicId (la hija no).
        ConfigurePublicEntity<PartialCreditNoteReconciliation>(modelBuilder);

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

            // INV-081: una sola cancelacion ACTIVA por reserva.
            //
            // PARCIAL (B1 fix, 2026-06-03): el filtro EXCLUYE los BC Aborted
            // (Status=6). Un draft abortado es una fila muerta de auditoria, NO
            // una cancelacion activa; no debe trabar la reserva para siempre. Sin
            // el filtro, un confirm fallido (red/AFIP) dejaba un BC huerfano y la
            // reserva quedaba IMPOSIBLE de re-cancelar (el INSERT del retry chocaba
            // con este UNIQUE). El guard aplicativo en DraftAsync (caso a: reusa el
            // draft puro; caso b: permite uno nuevo si el previo esta Aborted) es la
            // primera linea; este indice es el backstop a nivel BD bajo concurrencia.
            //
            // Por que NO excluimos tambien los Drafted: DraftAsync REUSA el draft
            // puro existente (no inserta otra fila), entonces nunca hay dos Drafted
            // activos compitiendo por el mismo ReservaId. Mantener Drafted DENTRO
            // del unique es un backstop extra contra doble-INSERT por doble-click.
            //
            // EF Core traduce HasFilter a "CREATE UNIQUE INDEX ... WHERE <sql>".
            // El literal usa la columna fisica "Status" (int) y el valor 6 = Aborted.
            entity.HasIndex(b => b.ReservaId)
                  .IsUnique()
                  .HasFilter("\"Status\" <> 6");

            // INV-100 (review BR4, 2026-05-14): la factura original que se anula no
            // puede pertenecer a dos cancelaciones distintas. Sin este UNIQUE seria
            // posible reabrir una cancelacion sobre la misma factura A y generar
            // dos NCs huerfanas — incidente fiscal grave.
            //
            // PARCIAL (B1 fix, 2026-06-03): mismo motivo y mismo filtro que el indice
            // de ReservaId. Cuando DraftAsync permite recrear sobre una reserva con un
            // BC Aborted (caso b), el BC nuevo apunta a la MISMA factura activa, asi
            // que este indice tambien debe ignorar los Aborted o el INSERT del retry
            // chocaria aca. Sigue garantizando: como maximo una cancelacion NO-abortada
            // por factura (la proteccion anti-doble-NC se mantiene intacta).
            entity.HasIndex(b => b.OriginatingInvoiceId)
                  .IsUnique()
                  .HasFilter("\"Status\" <> 6")
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

                // FC1.3 (ADR-009 §2.3.2, 2026-05-21): snapshot del modo de
                // facturacion del operador AL MOMENTO DE EMITIR la factura.
                // Persistir como int por consistencia con el resto de enums
                // de la owned entity.
                snap.Property(s => s.InvoicingModeAtEvent).HasConversion<int?>();
            });

            // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.1, 2026-05-26, RH-002):
            // FiscalLiquidation owned VO — columnas con prefijo "FiscalLiquidation_"
            // en la misma tabla "BookingCancellations" (mismo patron que
            // FiscalSnapshot arriba). Persiste el detalle completo de la liquidacion
            // (montos) que antes vivia solo en ApprovalRequest.Metadata JSON.
            //
            // Precision (18, 2) para todos los montos: dos decimales = centavos, que
            // es lo que ARCA y la contabilidad esperan para importes. Currency con
            // largo 3 (codigo ISO 4217: ARS/USD/EUR).
            entity.OwnsOne(b => b.FiscalLiquidation, liquidation =>
            {
                liquidation.Property(l => l.OriginalInvoiceAmount).HasPrecision(18, 2);
                liquidation.Property(l => l.CancellationAmount).HasPrecision(18, 2);
                liquidation.Property(l => l.OperatorPenaltyAmount).HasPrecision(18, 2);
                liquidation.Property(l => l.NonRefundableItemsAmount).HasPrecision(18, 2);
                liquidation.Property(l => l.FiscalAmountToCredit).HasPrecision(18, 2);
                liquidation.Property(l => l.AmountToRefundCustomer).HasPrecision(18, 2);
                liquidation.Property(l => l.FinalNetInvoiced).HasPrecision(18, 2);

                // Currency con default 'ARS' a nivel BD. Asi la migracion M1 puede
                // crear la columna con default y el backfill nunca falla por currency
                // faltante. HasDefaultValue mantiene snapshot y migracion consistentes
                // (sin esto EF detecta drift entre el defaultValue de la migracion y
                // el modelo).
                liquidation.Property(l => l.Currency).HasMaxLength(3).HasDefaultValue("ARS");
                liquidation.Property(l => l.ComputedByUserId).HasMaxLength(450);
                liquidation.Property(l => l.ComputedByUserName).HasMaxLength(200);
            });

            // ============================================================
            // FC1.3 (ADR-009 §2.3.2, 2026-05-21): persistencia summary del
            // resultado del clasificador NC parcial + FK al ApprovalRequest
            // que lo aprueba/rechaza.
            // ============================================================

            entity.Property(b => b.CreditNoteKind).HasConversion<int?>();
            entity.Property(b => b.ReviewRequiredReason).HasConversion<int>();
            entity.Property(b => b.LiquidationComputedByUserId).HasMaxLength(450);
            entity.Property(b => b.LiquidationComputedByUserName).HasMaxLength(200);
            entity.Property(b => b.ManualReviewerUserId).HasMaxLength(450);
            entity.Property(b => b.ManualReviewerUserName).HasMaxLength(200);
            entity.Property(b => b.ManualReviewComment).HasMaxLength(1000);

            // FK al ApprovalRequest que aprueba la liquidacion FC1.3.
            //
            // OnDelete: Restrict — el approval es evidencia fiscal del consentimiento
            // admin para la NC parcial. Si alguien quiere borrar el approval, la BD
            // debe rechazar porque romperia la auditoria del BC. Mismo patron que
            // <c>Invoice.AnnulmentApprovalRequestId</c>.
            entity.HasOne(b => b.PartialCreditNoteApprovalRequest)
                  .WithMany()
                  .HasForeignKey(b => b.PartialCreditNoteApprovalRequestId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Indice nullable para queries de auditoria: "todos los BCs aprobados
            // por este approval X".
            entity.HasIndex(b => b.PartialCreditNoteApprovalRequestId)
                  .HasDatabaseName("IX_BookingCancellations_PartialCreditNoteApprovalRequestId");

            // ============================================================
            // ADR-013 (2026-06-01): Nota de Debito por penalidad propia.
            // Enums persistidos como int (consistencia con el resto del modulo).
            // FK a la Invoice ND con SetNull (mismo patron que CreditNoteInvoice).
            // ============================================================
            entity.Property(b => b.PenaltyStatus).HasConversion<int>();
            entity.Property(b => b.ConceptKind).HasConversion<int>();
            entity.Property(b => b.DebitNotePurpose).HasConversion<int?>();
            entity.Property(b => b.DebitNoteStatus).HasConversion<int>();
            entity.Property(b => b.PenaltyOwnershipAtEvent).HasConversion<int?>();

            entity.Property(b => b.PenaltyAmountAtEvent).HasPrecision(18, 2);
            entity.Property(b => b.PenaltyCurrencyAtEvent).HasMaxLength(3);
            entity.Property(b => b.EmitterTaxConditionAtEvent).HasMaxLength(50);
            entity.Property(b => b.PenaltyConfirmedByUserId).HasMaxLength(450);
            entity.Property(b => b.PenaltyConfirmedByUserName).HasMaxLength(200);
            entity.Property(b => b.ConceptClassifiedByUserId).HasMaxLength(450);
            entity.Property(b => b.ConceptClassifiedByUserName).HasMaxLength(200);
            entity.Property(b => b.DebitNoteArcaErrorMessage).HasMaxLength(1000);

            // ADR-014 (2026-06-02): confirmacion diferida. SupportingDocumentReference
            // con largo 500 (es una referencia/URL, no el archivo). OperatorPenaltyConfirmedDate
            // no necesita config explicita (DateTime? mapea a timestamp nullable por default).
            entity.Property(b => b.SupportingDocumentReference).HasMaxLength(500);

            // ND opcional: existe solo despues de que la NC total salio con CAE y el
            // gating habilito la emision. SetNull para que el rollback de una ND (caso
            // raro) no rompa el aggregate (mismo patron que CreditNoteInvoice).
            entity.HasOne(b => b.DebitNoteInvoice)
                  .WithMany()
                  .HasForeignKey(b => b.DebitNoteInvoiceId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Indice para la bandeja "cancelaciones con NC pero sin su ND": filtra por
            // DebitNoteStatus in (Pending, Failed). Un indice sobre la columna basta.
            entity.HasIndex(b => b.DebitNoteStatus)
                  .HasDatabaseName("IX_BookingCancellations_DebitNoteStatus");

            // Concurrency lock-free (ADR-002 §2.5 / B11). Pre-requisito FC1.1
            // verificado: Npgsql 8.x soporta xmin nativamente.
            entity.UseXminAsConcurrencyToken();
        });

        // ============================================================
        // FC1.3 Fase 3 (ADR-010, 2026-05-29): bandeja de reconciliacion de
        // NC parciales con recibos vivos.
        // ============================================================

        // ===== PartialCreditNoteReconciliation (el caso) =====
        modelBuilder.Entity<PartialCreditNoteReconciliation>(entity =>
        {
            entity.ToTable("PartialCreditNoteReconciliations");

            // Status como STRING (no int) para que el CHECK constraint de la migracion
            // sea legible ('Pending'/'Resolved') y la columna se entienda leyendo la BD
            // directo. Mismo criterio que el ADR-010 §3.2. EF traduce el enum <-> string.
            entity.Property(r => r.Status)
                  .HasConversion<string>()
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(r => r.FiscalAmountCredited).HasPrecision(18, 2);
            entity.Property(r => r.Currency).HasMaxLength(3).IsRequired();
            entity.Property(r => r.OpenedByUserId).HasMaxLength(450).IsRequired();
            entity.Property(r => r.OpenedByUserName).HasMaxLength(200);
            entity.Property(r => r.ResolvedByUserId).HasMaxLength(450);
            entity.Property(r => r.ResolvedByUserName).HasMaxLength(200);
            entity.Property(r => r.ResolutionNotes).HasMaxLength(1000);

            // Indice UNICO sobre CreditNoteInvoiceId (B2): un caso por NC parcial. Es la
            // red de defensa de idempotencia — si el job de reversal reintenta, el segundo
            // intento choca aca. (La idempotencia primaria la da el guard del wrapper
            // ApplyCreditNoteEconomicReversalAsync, que no re-aplica el reversal.)
            entity.HasIndex(r => r.CreditNoteInvoiceId)
                  .IsUnique()
                  .HasDatabaseName("IX_PartialCreditNoteReconciliations_CreditNoteInvoiceId");

            // FK a la NC parcial. Restrict: la NC es evidencia fiscal, no se borra en cascada.
            entity.HasOne(r => r.CreditNoteInvoice)
                  .WithMany()
                  .HasForeignKey(r => r.CreditNoteInvoiceId)
                  .OnDelete(DeleteBehavior.Restrict);

            // FK a la factura original. Restrict por la misma razon.
            entity.HasOne(r => r.OriginalInvoice)
                  .WithMany()
                  .HasForeignKey(r => r.OriginalInvoiceId)
                  .OnDelete(DeleteBehavior.Restrict);

            // FK opcional a la reserva (contexto para el encargado). SetNull si la reserva
            // desaparece (no deberia, pero no rompemos el caso por eso).
            entity.HasOne(r => r.Reserva)
                  .WithMany()
                  .HasForeignKey(r => r.ReservaId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Hijas: snapshot de recibos vivos. CASCADE — si se borra el caso (solo en
            // tests, nunca en prod) se borran sus snapshots. No arrastra los PaymentReceipt
            // reales (esos cuelgan de otra FK, ver config de la hija).
            entity.HasMany(r => r.Receipts)
                  .WithOne(c => c.Reconciliation)
                  .HasForeignKey(c => c.ReconciliationId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Concurrency: dos encargados cerrando el mismo caso -> uno recibe
            // DbUpdateConcurrencyException -> el endpoint resolve devuelve 409.
            entity.UseXminAsConcurrencyToken();
        });

        // ===== PartialCreditNoteReconciliationReceipt (snapshot de un recibo) =====
        modelBuilder.Entity<PartialCreditNoteReconciliationReceipt>(entity =>
        {
            entity.ToTable("PartialCreditNoteReconciliationReceipts");
            entity.Property(c => c.Amount).HasPrecision(18, 2);
            entity.Property(c => c.StatusAtOpen).HasMaxLength(30).IsRequired();

            // FK al PaymentReceipt real. Restrict: el recibo es comprobante con numeracion
            // correlativa, jamas se borra; este snapshot solo apunta a el.
            entity.HasOne(c => c.PaymentReceipt)
                  .WithMany()
                  .HasForeignKey(c => c.PaymentReceiptId)
                  .OnDelete(DeleteBehavior.Restrict);

            // PaymentId NO es FK declarada (es un denormalizado para que el DTO exponga el
            // PublicId del Payment sin join extra). El PaymentReceipt ya garantiza la
            // integridad con el Payment a traves de PaymentReceipt.PaymentId.
            entity.HasIndex(c => c.ReconciliationId);
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

        // ===== ArcaIdempotencyKey (FC1.3 Fase 2 §FC1.3.F2.2, 2026-05-27) =====
        // Tabla operacional anti-doble-POST de NC parcial al ARCA. Ver comentario
        // de la entidad ArcaIdempotencyKey para el por que completo.
        modelBuilder.Entity<ArcaIdempotencyKey>(entity =>
        {
            // Key = hash SHA256 en hex (64 chars fijos). Acotamos a varchar(64) en vez
            // de text libre: autodocumenta el contrato del hash y es mas predecible para
            // el indice UNIQUE. Si F2.2 cambiara el algoritmo, ampliar el varchar es una
            // migracion aditiva trivial.
            entity.Property(k => k.Key).IsRequired().HasMaxLength(64);

            // JobId de Hangfire = id numerico corto como string. varchar(50) holgado.
            entity.Property(k => k.JobId).HasMaxLength(50);

            // Indice UNIQUE sobre Key = corazon del mecanismo anti-duplicados.
            // Cuando un reintento de Hangfire intenta insertar la misma key, Postgres
            // rechaza el INSERT (violacion de unique) y el job sabe que ya hubo un
            // intento previo -> consulta ARCA en vez de re-emitir.
            entity.HasIndex(k => k.Key)
                  .IsUnique()
                  .HasDatabaseName("IX_ArcaIdempotencyKeys_Key");
        });

        // ===== ADR-019 (avisos "Proximos inicios", 2026-06-06) =====
        // Descartes manuales del aviso por reserva (boton "Listo" de la campanita).
        modelBuilder.Entity<UpcomingStartAlertDismissal>(entity =>
        {
            // Mismo ancho que el resto de las columnas de userId de auditoria (ej. Invoice.IssuedByUserId).
            entity.Property(d => d.DismissedByUserId).IsRequired().HasMaxLength(450);

            // Indice UNIQUE = la garantia "una fila por reserva" (descarte GLOBAL, Q1 del ADR).
            // OJO (M4): esta garantia vive en POSTGRES; el provider InMemory de los tests NO aplica
            // indices unicos, asi que la carrera de dos POST concurrentes se valida en los tests de
            // integracion Postgres del VPS, no en la suite unit.
            entity.HasIndex(d => d.ReservaId)
                  .IsUnique()
                  .HasDatabaseName("IX_UpcomingStartAlertDismissals_ReservaId");

            // FK a Reservas (tabla legacy TravelFiles) SIN nav prop: borrar la reserva se lleva el
            // descarte (CASCADE) — el descarte no tiene sentido sin su reserva.
            entity.HasOne<Reserva>()
                  .WithMany()
                  .HasForeignKey(d => d.ReservaId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ===== ADR-017 F1.1 (catalogo find-or-create, 2026-06-05) =====
        // Rate.CreatedFromReservaId: FK opcional a la Reserva donde nacio el producto en venta.
        // ON DELETE SET NULL para que borrar una reserva NO se bloquee por la trazabilidad (la
        // marca es informativa). Reserva mapea a la tabla legacy "TravelFiles"; declaramos la FK
        // sin nav prop (mismo patron que Reserva.ResponsibleUserId -> ApplicationUser).
        modelBuilder.Entity<Rate>()
            .HasOne<Reserva>()
            .WithMany()
            .HasForeignKey(r => r.CreatedFromReservaId)
            .OnDelete(DeleteBehavior.SetNull);

        // RateSupplierSale: memoria "ultima venta por producto y operador".
        modelBuilder.Entity<RateSupplierSale>(entity =>
        {
            entity.Property(s => s.LastPriceUnit).IsRequired().HasMaxLength(30);
            entity.Property(s => s.LastCurrency).HasMaxLength(3);

            // FK al producto (Rate): CASCADE. Si se borra el Rate, su historial de ventas se va con el.
            entity.HasOne(s => s.Rate)
                  .WithMany()
                  .HasForeignKey(s => s.RateId)
                  .OnDelete(DeleteBehavior.Cascade);

            // FK al operador (Supplier): RESTRICT (red de seguridad C24, igual que los bookings
            // tipados): no permitir borrar un Supplier con historial de ventas asociado.
            entity.HasOne(s => s.Supplier)
                  .WithMany()
                  .HasForeignKey(s => s.SupplierId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Una sola fila por combinacion (producto, operador): el corazon del upsert ON CONFLICT
            // que la llena en F1.3. Sin este UNIQUE el upsert no tendria contra que hacer conflicto.
            entity.HasIndex(s => new { s.RateId, s.SupplierId })
                  .IsUnique()
                  .HasDatabaseName("IX_RateSupplierSales_RateId_SupplierId");

            // Indice para sacar rapido el "ultimo precio" de un producto: por RateId y LastSoldAt DESC.
            entity.HasIndex(s => new { s.RateId, s.LastSoldAt })
                  .IsDescending(false, true)
                  .HasDatabaseName("IX_RateSupplierSales_RateId_LastSoldAt");
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
