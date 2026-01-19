using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TravelApi.Models;

namespace TravelApi.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Tariff> Tariffs => Set<Tariff>();
    public DbSet<TariffValidity> TariffValidities => Set<TariffValidity>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteVersion> QuoteVersions => Set<QuoteVersion>();
    public DbSet<TreasuryReceipt> TreasuryReceipts => Set<TreasuryReceipt>();
    public DbSet<TreasuryApplication> TreasuryApplications => Set<TreasuryApplication>();
    public DbSet<Cupo> Cupos => Set<Cupo>();
    public DbSet<CupoAssignment> CupoAssignments => Set<CupoAssignment>();
    public DbSet<BspImportBatch> BspImportBatches => Set<BspImportBatch>();
    public DbSet<BspImportRawRecord> BspImportRawRecords => Set<BspImportRawRecord>();
    public DbSet<BspNormalizedRecord> BspNormalizedRecords => Set<BspNormalizedRecord>();
    public DbSet<BspReconciliationEntry> BspReconciliationEntries => Set<BspReconciliationEntry>();
    public DbSet<AccountingEntry> AccountingEntries => Set<AccountingEntry>();
    public DbSet<AccountingLine> AccountingLines => Set<AccountingLine>();
    public DbSet<Agency> Agencies => Set<Agency>();
    public DbSet<TravelFile> TravelFiles => Set<TravelFile>();
    public DbSet<FlightSegment> FlightSegments => Set<FlightSegment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // ... (existing code) ...

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(s => s.Name).IsRequired().HasMaxLength(100);
            entity.Property(s => s.ContactName).HasMaxLength(100);
            entity.Property(s => s.Email).HasMaxLength(100);
            entity.Property(s => s.Phone).HasMaxLength(50);
        });

        modelBuilder.Entity<TravelFile>(entity =>
        {
            entity.HasOne(f => f.Payer)
                  .WithMany()
                  .HasForeignKey(f => f.PayerId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
        
        // Previous configs...

        modelBuilder.Entity<FlightSegment>(entity =>
        {
            entity.Property(s => s.AirlineCode).HasMaxLength(3).IsRequired();
            entity.Property(s => s.FlightNumber).HasMaxLength(10).IsRequired();
            entity.Property(s => s.Origin).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Destination).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Status).HasMaxLength(2);

            entity.HasOne(s => s.Reservation)
                .WithMany(r => r.Segments)
                .HasForeignKey(s => s.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(customer => customer.FullName)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(customer => customer.Email)
                .HasMaxLength(200);

            entity.Property(customer => customer.DocumentNumber)
                .HasMaxLength(50);

            entity.Property(customer => customer.Address)
                .HasMaxLength(300);
        });

        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.Property(reservation => reservation.ReferenceCode)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(reservation => reservation.Status)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(reservation => reservation.ProductType)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(reservation => reservation.TotalAmount)
                .HasPrecision(12, 2);

            entity.Property(reservation => reservation.BasePrice)
                .HasPrecision(12, 2);

            entity.Property(reservation => reservation.Commission)
                .HasPrecision(12, 2);

            entity.Property(reservation => reservation.SupplierName)
                .HasMaxLength(200);

            entity.HasOne(reservation => reservation.Customer)
                .WithMany(customer => customer.Reservations)
                .HasForeignKey(reservation => reservation.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(payment => payment.Amount)
                .HasPrecision(12, 2);

            entity.Property(payment => payment.Method)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(payment => payment.Status)
                .HasMaxLength(50)
                .IsRequired();

            entity.HasOne(payment => payment.Reservation)
                .WithMany(reservation => reservation.Payments)
                .HasForeignKey(payment => payment.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(supplier => supplier.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(supplier => supplier.Email)
                .HasMaxLength(200);

            entity.Property(supplier => supplier.Phone)
                .HasMaxLength(50);
        });

        modelBuilder.Entity<Tariff>(entity =>
        {
            entity.Property(tariff => tariff.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(tariff => tariff.Description)
                .HasMaxLength(500);

            entity.Property(tariff => tariff.ProductType)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(tariff => tariff.Currency)
                .HasConversion<string>()
                .HasMaxLength(10);

            entity.Property(tariff => tariff.DefaultPrice)
                .HasPrecision(12, 2);

            entity.HasMany(tariff => tariff.Validities)
                .WithOne(validity => validity.Tariff)
                .HasForeignKey(validity => validity.TariffId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Quote>(entity =>
        {
            entity.Property(quote => quote.ReferenceCode)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(quote => quote.Status)
                .HasMaxLength(30)
                .IsRequired();

            entity.HasOne(quote => quote.Customer)
                .WithMany()
                .HasForeignKey(quote => quote.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(quote => quote.Versions)
                .WithOne(version => version.Quote)
                .HasForeignKey(version => version.QuoteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuoteVersion>(entity =>
        {
            entity.Property(version => version.ProductType)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(version => version.Currency)
                .HasConversion<string>()
                .HasMaxLength(10);

            entity.Property(version => version.TotalAmount)
                .HasPrecision(12, 2);

            entity.Property(version => version.Notes)
                .HasMaxLength(500);
        });

        modelBuilder.Entity<TreasuryReceipt>(entity =>
        {
            entity.Property(receipt => receipt.Reference)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(receipt => receipt.Method)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(receipt => receipt.Currency)
                .HasConversion<string>()
                .HasMaxLength(10);

            entity.Property(receipt => receipt.Amount)
                .HasPrecision(12, 2);

            entity.Property(receipt => receipt.Notes)
                .HasMaxLength(500);

            entity.HasMany(receipt => receipt.Applications)
                .WithOne(application => application.TreasuryReceipt)
                .HasForeignKey(application => application.TreasuryReceiptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TreasuryApplication>(entity =>
        {
            entity.Property(application => application.AmountApplied)
                .HasPrecision(12, 2);

            entity.HasOne(application => application.Reservation)
                .WithMany()
                .HasForeignKey(application => application.ReservationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TariffValidity>(entity =>
        {
            entity.Property(validity => validity.Price)
                .HasPrecision(12, 2);

            entity.Property(validity => validity.Notes)
                .HasMaxLength(500);
        });

        modelBuilder.Entity<Cupo>(entity =>
        {
            entity.Property(cupo => cupo.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(cupo => cupo.ProductType)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(cupo => cupo.RowVersion)
                .IsConcurrencyToken();

            entity.HasMany(cupo => cupo.Assignments)
                .WithOne(assignment => assignment.Cupo)
                .HasForeignKey(assignment => assignment.CupoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CupoAssignment>(entity =>
        {
            entity.HasOne(assignment => assignment.Reservation)
                .WithMany()
                .HasForeignKey(assignment => assignment.ReservationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BspImportBatch>(entity =>
        {
            entity.Property(batch => batch.FileName)
                .HasMaxLength(200);

            entity.Property(batch => batch.Format)
                .HasMaxLength(20);

            entity.Property(batch => batch.Status)
                .HasMaxLength(20);

            entity.HasMany(batch => batch.RawRecords)
                .WithOne(record => record.BspImportBatch)
                .HasForeignKey(record => record.BspImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(batch => batch.NormalizedRecords)
                .WithOne(record => record.BspImportBatch)
                .HasForeignKey(record => record.BspImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(batch => batch.Reconciliations)
                .WithOne(entry => entry.BspImportBatch)
                .HasForeignKey(entry => entry.BspImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BspImportRawRecord>(entity =>
        {
            entity.Property(record => record.RawContent)
                .HasColumnType("text");
        });

        modelBuilder.Entity<BspNormalizedRecord>(entity =>
        {
            entity.Property(record => record.TicketNumber)
                .HasMaxLength(50);

            entity.Property(record => record.ReservationReference)
                .HasMaxLength(50);

            entity.Property(record => record.Currency)
                .HasMaxLength(10);

            entity.Property(record => record.BaseAmount)
                .HasPrecision(12, 2);

            entity.Property(record => record.TaxAmount)
                .HasPrecision(12, 2);

            entity.Property(record => record.TotalAmount)
                .HasPrecision(12, 2);

            entity.HasOne(record => record.ReconciliationEntry)
                .WithOne(entry => entry.BspNormalizedRecord)
                .HasForeignKey<BspReconciliationEntry>(entry => entry.BspNormalizedRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BspReconciliationEntry>(entity =>
        {
            entity.Property(entry => entry.Status)
                .HasMaxLength(30);

            entity.Property(entry => entry.DifferenceAmount)
                .HasPrecision(12, 2);

            entity.HasOne(entry => entry.Reservation)
                .WithMany()
                .HasForeignKey(entry => entry.ReservationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AccountingEntry>(entity =>
        {
            entity.Property(entry => entry.Description)
                .HasMaxLength(300);

            entity.Property(entry => entry.Source)
                .HasMaxLength(50);

            entity.Property(entry => entry.SourceReference)
                .HasMaxLength(100);

            entity.HasMany(entry => entry.Lines)
                .WithOne(line => line.AccountingEntry)
                .HasForeignKey(line => line.AccountingEntryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccountingLine>(entity =>
        {
            entity.Property(line => line.AccountCode)
                .HasMaxLength(20);

            entity.Property(line => line.Debit)
                .HasPrecision(12, 2);

            entity.Property(line => line.Credit)
                .HasPrecision(12, 2);

            entity.Property(line => line.Currency)
                .HasMaxLength(10);
        });

        modelBuilder.Entity<Agency>(entity =>
        {
            entity.Property(agency => agency.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(agency => agency.TaxId)
                .HasMaxLength(50);

            entity.Property(agency => agency.Email)
                .HasMaxLength(200);

            entity.Property(agency => agency.Phone)
                .HasMaxLength(50);

            entity.Property(agency => agency.Address)
                .HasMaxLength(300);

            entity.Property(agency => agency.CreditLimit)
                .HasPrecision(12, 2);

            entity.Property(agency => agency.CurrentBalance)
                .HasPrecision(12, 2);

            entity.HasMany(agency => agency.Users)
                .WithOne(user => user.Agency)
                .HasForeignKey(user => user.AgencyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TravelFile>(entity =>
        {
            entity.Property(file => file.FileNumber)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(file => file.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(file => file.Status)
                .HasMaxLength(50)
                .IsRequired();

            entity.HasMany(file => file.Reservations)
                .WithOne(reservation => reservation.TravelFile)
                .HasForeignKey(reservation => reservation.TravelFileId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
