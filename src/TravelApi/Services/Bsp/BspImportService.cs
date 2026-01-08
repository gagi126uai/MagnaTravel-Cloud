using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Services.Bsp.Models;

namespace TravelApi.Services.Bsp;

public class BspImportService
{
    private const decimal AmountTolerance = 0.01m;

    private readonly AppDbContext _dbContext;
    private readonly IReadOnlyDictionary<string, IBspImportParser> _parsers;

    public BspImportService(AppDbContext dbContext, IEnumerable<IBspImportParser> parsers)
    {
        _dbContext = dbContext;
        _parsers = parsers.ToDictionary(parser => parser.Format, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<BspImportBatch> ImportAsync(
        string fileName,
        string format,
        string content,
        CancellationToken cancellationToken)
    {
        if (!_parsers.TryGetValue(format, out var parser))
        {
            throw new InvalidOperationException($"Formato BSP no soportado: {format}.");
        }

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var parsedResult = await parser.ParseAsync(stream, cancellationToken);
        ValidateAmounts(parsedResult.Records);

        var batch = new BspImportBatch
        {
            FileName = fileName,
            Format = format.ToLowerInvariant(),
            ImportedAt = DateTime.UtcNow,
            Status = "Open"
        };

        var references = parsedResult.Records
            .Select(record => record.ReservationReference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reservations = await _dbContext.Reservations
            .AsNoTracking()
            .Where(reservation => references.Contains(reservation.ReferenceCode))
            .ToListAsync(cancellationToken);

        var reservationLookup = reservations
            .GroupBy(reservation => reservation.ReferenceCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var record in parsedResult.Records)
        {
            batch.RawRecords.Add(new BspImportRawRecord
            {
                LineNumber = record.LineNumber,
                RawContent = record.RawContent
            });

            var normalized = new BspNormalizedRecord
            {
                TicketNumber = record.TicketNumber,
                ReservationReference = record.ReservationReference,
                IssueDate = record.IssueDate,
                Currency = record.Currency,
                BaseAmount = record.BaseAmount,
                TaxAmount = record.TaxAmount,
                TotalAmount = record.TotalAmount
            };

            var reconciliation = BuildReconciliation(normalized, reservationLookup);
            normalized.ReconciliationEntry = reconciliation;
            reconciliation.BspNormalizedRecord = normalized;

            batch.NormalizedRecords.Add(normalized);
            batch.Reconciliations.Add(reconciliation);
        }

        _dbContext.BspImportBatches.Add(batch);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return batch;
    }

    public async Task<BspImportBatch> CloseBatchAsync(int batchId, CancellationToken cancellationToken)
    {
        var batch = await _dbContext.BspImportBatches
            .Include(b => b.RawRecords)
            .Include(b => b.Reconciliations)
            .Include(b => b.NormalizedRecords)
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

        if (batch is null)
        {
            throw new InvalidOperationException("No se encontró el lote BSP.");
        }

        if (batch.Status.Equals("Closed", StringComparison.OrdinalIgnoreCase))
        {
            return batch;
        }

        if (batch.Reconciliations.Any(entry => !entry.Status.Equals("Matched", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("No se puede cerrar el lote hasta conciliar todos los registros.");
        }

        var accountingEntries = batch.NormalizedRecords.Select(record => new AccountingEntry
        {
            EntryDate = DateTime.UtcNow,
            Description = $"BSP ticket {record.TicketNumber}",
            Source = "BSP",
            SourceReference = batch.FileName,
            Lines = new List<AccountingLine>
            {
                new()
                {
                    AccountCode = "1101",
                    Debit = record.TotalAmount,
                    Credit = 0m,
                    Currency = record.Currency
                },
                new()
                {
                    AccountCode = "4101",
                    Debit = 0m,
                    Credit = record.TotalAmount,
                    Currency = record.Currency
                }
            }
        }).ToList();

        _dbContext.AccountingEntries.AddRange(accountingEntries);
        batch.Status = "Closed";
        batch.ClosedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return batch;
    }

    private static void ValidateAmounts(IEnumerable<BspParsedRecord> records)
    {
        foreach (var record in records)
        {
            var expectedTotal = record.BaseAmount + record.TaxAmount;
            if (Math.Abs(expectedTotal - record.TotalAmount) > AmountTolerance)
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Importe inválido en línea {0}: base {1} + impuestos {2} != total {3}.",
                        record.LineNumber,
                        record.BaseAmount,
                        record.TaxAmount,
                        record.TotalAmount));
            }
        }
    }

    private static BspReconciliationEntry BuildReconciliation(
        BspNormalizedRecord record,
        IReadOnlyDictionary<string, Reservation> reservationLookup)
    {
        if (!reservationLookup.TryGetValue(record.ReservationReference, out var reservation))
        {
            return new BspReconciliationEntry
            {
                Status = "MissingReservation",
                DifferenceAmount = record.TotalAmount,
                ReservationId = null,
                BspNormalizedRecord = record
            };
        }

        var difference = record.TotalAmount - reservation.TotalAmount;
        if (Math.Abs(difference) <= AmountTolerance)
        {
            return new BspReconciliationEntry
            {
                Status = "Matched",
                DifferenceAmount = 0m,
                ReservationId = reservation.Id,
                BspNormalizedRecord = record
            };
        }

        return new BspReconciliationEntry
        {
            Status = "AmountMismatch",
            DifferenceAmount = difference,
            ReservationId = reservation.Id,
            BspNormalizedRecord = record
        };
    }
}
