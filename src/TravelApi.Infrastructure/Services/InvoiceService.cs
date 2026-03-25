using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Hangfire;

namespace TravelApi.Infrastructure.Services;

public class InvoiceService : IInvoiceService
{
    private readonly AppDbContext _context;
    private readonly EntityReferenceResolver _entityReferenceResolver;
    private readonly IAfipService _afipService;
    private readonly IInvoicePdfService _pdfService;
    private readonly IMapper _mapper;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<InvoiceService> _logger;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly UserManager<ApplicationUser> _userManager;
    private static readonly string[] ActiveInvoicingStatuses =
    {
        EstadoReserva.Reserved,
        EstadoReserva.Operational
    };

    public InvoiceService(
        AppDbContext context, 
        EntityReferenceResolver entityReferenceResolver,
        IAfipService afipService, 
        IInvoicePdfService pdfService,
        IMapper mapper,
        IBackgroundJobClient backgroundJobClient,
        ILogger<InvoiceService> logger,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _entityReferenceResolver = entityReferenceResolver;
        _afipService = afipService;
        _pdfService = pdfService;
        _mapper = mapper;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _userManager = userManager;
    }

    public async Task<PagedResponse<InvoiceListDto>> GetAllAsync(InvoicesListQuery query, CancellationToken ct)
    {
        var invoicesQuery = ApplyInvoiceSearch(_context.Invoices.AsNoTracking(), query.Search);
        invoicesQuery = ApplyInvoiceKind(invoicesQuery, query.Kind);
        invoicesQuery = ApplyInvoiceOrdering(invoicesQuery, query);

        return await invoicesQuery
            .Select(invoice => new InvoiceListDto
            {
                PublicId = invoice.PublicId,
                ReservaPublicId = invoice.Reserva != null ? (Guid?)invoice.Reserva.PublicId : null,
                NumeroReserva = invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                CustomerName = invoice.Reserva != null && invoice.Reserva.Payer != null ? invoice.Reserva.Payer.FullName : null,
                TipoComprobante = invoice.TipoComprobante,
                PuntoDeVenta = invoice.PuntoDeVenta,
                NumeroComprobante = invoice.NumeroComprobante,
                ImporteTotal = invoice.ImporteTotal,
                CreatedAt = invoice.CreatedAt,
                CAE = invoice.CAE,
                Resultado = invoice.Resultado,
                Observaciones = invoice.Observaciones,
                WasForced = invoice.WasForced,
                ForceReason = invoice.ForceReason,
                ForcedByUserId = invoice.ForcedByUserId,
                ForcedByUserName = invoice.ForcedByUserName,
                ForcedAt = invoice.ForcedAt,
                OutstandingBalanceAtIssuance = invoice.OutstandingBalanceAtIssuance,
                InvoiceType =
                    invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3 ? "A" :
                    invoice.TipoComprobante == 6 || invoice.TipoComprobante == 7 || invoice.TipoComprobante == 8 ? "B" :
                    invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13 ? "C" :
                    invoice.TipoComprobante == 51 ? "M" :
                    "UNK"
            })
            .ToPagedResponseAsync(query, ct);
    }

    public async Task<InvoicingSummaryDto> GetInvoicingSummaryAsync(CancellationToken ct)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
        var workItemsQuery = BuildInvoicingWorkItemsQuery(settings);
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var approvedInvoices = _context.Invoices
            .AsNoTracking()
            .Where(i => i.Resultado == "A");

        return new InvoicingSummaryDto
        {
            ReadyAmount = Math.Round(await workItemsQuery
                .Where(item => item.FiscalStatus == "ready")
                .Select(item => item.PendingFiscalAmount)
                .DefaultIfEmpty(0m)
                .SumAsync(ct), 2),
            ReadyCount = await workItemsQuery.CountAsync(item => item.FiscalStatus == "ready", ct),
            BlockedCount = await workItemsQuery.CountAsync(
                item => item.FiscalStatus == "blocked" || item.FiscalStatus == "override",
                ct),
            InvoicedThisMonth = Math.Round(await approvedInvoices
                .Where(invoice => invoice.CreatedAt >= startOfMonth)
                .Select(invoice =>
                    invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8 || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53
                        ? -invoice.ImporteTotal
                        : invoice.ImporteTotal)
                .DefaultIfEmpty(0m)
                .SumAsync(ct), 2),
            ForcedCount = await approvedInvoices.CountAsync(invoice => invoice.WasForced, ct)
        };
    }

    public async Task<PagedResponse<InvoicingWorkItemDto>> GetInvoicingWorklistAsync(InvoicingWorklistQuery query, CancellationToken ct)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
        var workItemsQuery = BuildInvoicingWorkItemsQuery(settings);
        workItemsQuery = ApplyInvoicingWorkItemSearch(workItemsQuery, query.Search);
        workItemsQuery = ApplyInvoicingWorkItemStatus(workItemsQuery, query.Status);
        workItemsQuery = ApplyInvoicingWorkItemOrdering(workItemsQuery, query);

        return await workItemsQuery.ToPagedResponseAsync(query, ct);
    }

    public async Task<InvoiceDto> CreateAsync(CreateInvoiceRequest request, string? userId, string? userName, CancellationToken ct)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(request.ReservaId, ct);
        var reserva = await _context.Reservas
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct)
            ?? throw new InvalidOperationException("Reserva no encontrada.");

        if (!request.IsCreditNote && !request.IsDebitNote)
        {
            var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
            var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);

            if (!EconomicRulesHelper.IsEconomicallySettled(reserva))
            {
                if (!request.ForceIssue)
                    throw new InvalidOperationException(afip.BlockReason ?? "La reserva tiene deuda y no puede emitirse en AFIP.");

                if (settings.AfipInvoiceControlMode != AfipInvoiceControlModes.AllowAgentOverrideWithReason)
                    throw new InvalidOperationException("La configuracion actual no permite emitir AFIP con deuda.");

                if (string.IsNullOrWhiteSpace(request.ForceReason) || request.ForceReason.Trim().Length < 10)
                    throw new InvalidOperationException("Debe indicar un motivo valido para emitir AFIP con deuda.");

                request.ForceReason = request.ForceReason.Trim();
                request.ForcedByUserId = userId;
                request.ForcedByUserName = userName;
            }
        }

        // 1. Create Pending in DB
        var invoice = await _afipService.CreatePendingInvoice(reservaId, request);

        if (invoice.WasForced)
        {
            await NotifyAdminsOfForcedInvoiceAsync(invoice, request, ct);
        }
        
        // 2. Enqueue Job
        _backgroundJobClient.Enqueue<IAfipService>(s => s.ProcessInvoiceJob(invoice.Id));

        return _mapper.Map<InvoiceDto>(invoice);
    }

    public async Task<bool> RetryAsync(int id, CancellationToken ct)
    {
        var invoice = await _context.Invoices.FindAsync(new object[] { id }, ct);
        if (invoice == null) return false;
        if (invoice.Resultado == "A") throw new InvalidOperationException("La factura ya está aprobada.");

        // Reset to PENDING so UI shows yellow
        invoice.Resultado = "PENDING";
        invoice.Observaciones = null;
        await _context.SaveChangesAsync(ct);

        _backgroundJobClient.Enqueue<IAfipService>(s => s.ProcessInvoiceJob(id));
        return true;
    }

    public async Task<IEnumerable<InvoiceListDto>> GetByReservaIdAsync(int reservaId, CancellationToken ct)
    {
        return await _context.Invoices
            .AsNoTracking()
            .Where(i => i.ReservaId == reservaId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(invoice => new InvoiceListDto
            {
                PublicId = invoice.PublicId,
                ReservaPublicId = invoice.Reserva != null ? (Guid?)invoice.Reserva.PublicId : null,
                NumeroReserva = invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                CustomerName = invoice.Reserva != null && invoice.Reserva.Payer != null ? invoice.Reserva.Payer.FullName : null,
                TipoComprobante = invoice.TipoComprobante,
                PuntoDeVenta = invoice.PuntoDeVenta,
                NumeroComprobante = invoice.NumeroComprobante,
                ImporteTotal = invoice.ImporteTotal,
                CreatedAt = invoice.CreatedAt,
                CAE = invoice.CAE,
                Resultado = invoice.Resultado,
                Observaciones = invoice.Observaciones,
                WasForced = invoice.WasForced,
                ForceReason = invoice.ForceReason,
                ForcedByUserId = invoice.ForcedByUserId,
                ForcedByUserName = invoice.ForcedByUserName,
                ForcedAt = invoice.ForcedAt,
                OutstandingBalanceAtIssuance = invoice.OutstandingBalanceAtIssuance,
                InvoiceType =
                    invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3 ? "A" :
                    invoice.TipoComprobante == 6 || invoice.TipoComprobante == 7 || invoice.TipoComprobante == 8 ? "B" :
                    invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13 ? "C" :
                    invoice.TipoComprobante == 51 ? "M" :
                    "UNK"
            })
            .ToListAsync(ct);
    }

    public async Task<byte[]> GetPdfAsync(int id, CancellationToken ct)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Reserva)
            .ThenInclude(t => t.Payer)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (invoice == null) throw new KeyNotFoundException("Factura no encontrada");

        var settings = await _context.AfipSettings.FirstOrDefaultAsync(ct);
        if (settings == null) throw new InvalidOperationException("Configuración de AFIP no encontrada");

        var agencySettings = await _context.AgencySettings.FirstOrDefaultAsync(ct) ?? new AgencySettings();

        return _pdfService.GenerateInvoicePdf(invoice, invoice.Reserva, settings, agencySettings);
    }

    public async Task EnqueueAnnulmentAsync(int id, string userId, CancellationToken ct)
    {
        _backgroundJobClient.Enqueue<IInvoiceService>(service => service.ProcessAnnulmentJob(id, userId));
    }

    public async Task ProcessAnnulmentJob(int invoiceId, string userId)
    {
        try
        {
            _logger.LogInformation("Iniciando anulación de factura {InvoiceId} para usuario {UserId}", invoiceId, userId);

            var original = await _context.Invoices
                .Include(i => i.Items)
                .Include(i => i.Tributes)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            if (original == null) throw new Exception("Comprobante original no encontrado");

            // Avoid double processing
            if (await _context.Invoices.AnyAsync(i => i.OriginalInvoiceId == invoiceId && i.Resultado == "A"))
            {
                await CreateNotification(userId, $"El comprobante {original.NumeroComprobante} ya fue anulado.", "Warning", invoiceId);
                return;
            }

            // Determine Type (Credit or Debit Note)
            int cbteTipo = 0;
            // Similar logic to Controller but simplified
            switch (original.TipoComprobante)
            {
                case 1: cbteTipo = 3; break; // Fac A -> NC A
                case 6: cbteTipo = 8; break; // Fac B -> NC B
                case 11: cbteTipo = 13; break; // Fac C -> NC C
                case 3: cbteTipo = 2; break; // NC A -> ND A
                case 8: cbteTipo = 7; break; // NC B -> ND B
                case 13: cbteTipo = 12; break; // NC C -> ND C
            }

            var request = new CreateInvoiceRequest
            {
                ReservaId = original.Reserva?.PublicId.ToString() ?? string.Empty,
                CbteTipo = cbteTipo,
                Concepto = 3, // Productos y Servicios (default)
                DocTipo = 99, // Sin info
                DocNro = 0,
                OriginalInvoiceId = original.PublicId.ToString(),
                IsCreditNote = cbteTipo == 3 || cbteTipo == 8 || cbteTipo == 13 || cbteTipo == 53,
                IsDebitNote = cbteTipo == 2 || cbteTipo == 7 || cbteTipo == 12 || cbteTipo == 52
            };

            // Use Snapshots if available
            if (!string.IsNullOrEmpty(original.CustomerSnapshot))
            {
                var customer = System.Text.Json.JsonSerializer.Deserialize<Customer>(original.CustomerSnapshot);
                if (customer != null)
                {
                   if (!string.IsNullOrEmpty(customer.TaxId)) 
                    {
                        request.DocTipo = 80; // CUIT
                        if (long.TryParse(customer.TaxId.Replace("-", ""), out long cuit)) request.DocNro = cuit;
                    }
                    else if (!string.IsNullOrEmpty(customer.DocumentNumber))
                    {
                        request.DocTipo = 96; // DNI
                         if (long.TryParse(customer.DocumentNumber, out long dni)) request.DocNro = dni;
                    }
                    else
                    {
                        request.DocTipo = 99;
                        request.DocNro = 0;
                    }
                }
            }

            // --- RECONSTRUCTION LOGIC (Moved from Controller) ---
            
            // 1. Try Local Items
            if (original.Items.Any())
            {
                request.Items = original.Items.Select(i => new InvoiceItemDto
                {
                    Description = i.Description,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    Total = i.Total,
                    AlicuotaIvaId = i.AlicuotaIvaId
                }).ToList();
                
                request.Tributes = original.Tributes.Select(t => new InvoiceTributeDto
                {
                    TributeId = t.TributeId,
                    Description = t.Description,
                    BaseImponible = t.BaseImponible,
                    Alicuota = t.Alicuota,
                    Importe = t.Importe
                }).ToList();
            }
            else 
            {
                 // 2. Try AFIP (Legacy Fallback)
                 var details = await _afipService.GetVoucherDetails(original.TipoComprobante, original.PuntoDeVenta, original.NumeroComprobante);
                 
                 if (details != null && details.ImporteTotal > 0)
                 {
                     foreach (var vat in details.VatDetails)
                     {
                         request.Items.Add(new InvoiceItemDto
                         {
                             Description = $"Anulación Comp. {original.NumeroComprobante}",
                             Quantity = 1,
                             UnitPrice = vat.BaseImp, 
                             Total = vat.BaseImp,
                             AlicuotaIvaId = vat.Id
                         });
                     }
                     if (!request.Items.Any() && details.ImporteTotal > 0)
                     {
                          request.Items.Add(new InvoiceItemDto
                          {
                              Description = $"Anulación Comp. {original.NumeroComprobante}",
                              Quantity = 1,
                              UnitPrice = details.ImporteNeto > 0 ? details.ImporteNeto : details.ImporteTotal,
                              Total = details.ImporteNeto > 0 ? details.ImporteNeto : details.ImporteTotal,
                              AlicuotaIvaId = 3 
                          });
                     }
                     foreach (var trib in details.TributeDetails)
                     {
                         request.Tributes.Add(new InvoiceTributeDto
                         {
                             TributeId = trib.Id,
                             Description = trib.Desc,
                             BaseImponible = trib.BaseImp,
                             Alicuota = trib.Alic,
                             Importe = trib.Importe
                         });
                     }
                 }
                 else 
                 {
                     // 3. Last Resort (Local Totals)
                     decimal net = original.ImporteNeto > 0 ? original.ImporteNeto : original.ImporteTotal;
                     decimal iva = original.ImporteIva;
                     int ivaId = 3; 

                     if (iva > 0) ivaId = 5; 
                     
                     if (original.ImporteNeto == 0 && iva > 0) net = original.ImporteTotal - iva;

                     request.Items.Add(new InvoiceItemDto
                     {
                         Description = $"Anulación Comp. {original.NumeroComprobante} (Respaldo Local)",
                         Quantity = 1,
                         UnitPrice = net,
                         Total = net,
                         AlicuotaIvaId = ivaId
                     });
                 }
            }

            // Execute AFIP Call (Chain the pending creation and the processing)
            var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(request.ReservaId);
            var newInvoice = await _afipService.CreatePendingInvoice(reservaId, request);
            
            // Since we are already in a background job, we can process it immediately
            await _afipService.ProcessInvoiceJob(newInvoice.Id);
            
            // Reload to get the result updated by ProcessInvoiceJob
            await _context.Entry(newInvoice).ReloadAsync();

            if (newInvoice.Resultado == "A")
            {
                await CreateNotification(userId, $"Anulación exitosa. Se generó el comprobante {newInvoice.NumeroComprobante}.", "Success", newInvoice.Id);
            }
            else
            {
                await CreateNotification(userId, $"La anulación falló en AFIP: {newInvoice.Observaciones}", "Error", invoiceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico en Job de Anulación");
            
            var errorMsg = ex.Message;
            if (errorMsg.Contains("AFIP RECHAZADO"))
            {
                 // Permanent error (Validation), do not retry
                 await CreateNotification(userId, $"La anulación fue rechazada por AFIP: {errorMsg.Replace("AFIP RECHAZADO: ", "")}", "Error", invoiceId);
                 return; // Job finishes effectively "Failed" but successfully handled
            }

            await CreateNotification(userId, $"Error técnico al anular: {errorMsg}. Se reintentará automáticamente.", "Error", invoiceId);
            throw; // Retry job for network/transient errors
        }
    }

    private async Task CreateNotification(string userId, string message, string type, int relatedId)
    {
        _context.Notifications.Add(new Notification
        {
            UserId = userId,
            Message = message,
            Type = type,
            RelatedEntityId = relatedId,
            RelatedEntityType = "Invoice"
        });
        await _context.SaveChangesAsync();
    }

    private async Task NotifyAdminsOfForcedInvoiceAsync(Invoice invoice, CreateInvoiceRequest request, CancellationToken ct)
    {
        var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
        if (adminUsers.Count == 0)
            return;

        var actor = string.IsNullOrWhiteSpace(request.ForcedByUserName) ? "Un agente" : request.ForcedByUserName;
        var message = $"{actor} emitio AFIP por excepcion para la reserva #{invoice.ReservaId} con saldo pendiente de {invoice.OutstandingBalanceAtIssuance:C2}.";

        foreach (var admin in adminUsers)
        {
            _context.Notifications.Add(new Notification
            {
                UserId = admin.Id,
                Message = message,
                Type = "Warning",
                RelatedEntityId = invoice.Id,
                RelatedEntityType = "Invoice"
            });
        }

        await _context.SaveChangesAsync(ct);
    }

    private IQueryable<InvoicingWorkItemDto> BuildInvoicingWorkItemsQuery(OperationalFinanceSettings settings)
    {
        var allowsOverride = settings.AfipInvoiceControlMode == AfipInvoiceControlModes.AllowAgentOverrideWithReason;
        var overrideBlockReason = "La reserva tiene deuda. AFIP queda bloqueado por defecto y requiere override con motivo.";
        var hardBlockReason = "La reserva no esta cancelada economicamente y no puede emitirse en AFIP.";

        return _context.Reservas
            .AsNoTracking()
            .Where(reserva => ActiveInvoicingStatuses.Contains(reserva.Status))
            .Select(reserva => new
            {
                reserva.Id,
                reserva.PublicId,
                reserva.NumeroReserva,
                CustomerName = reserva.Payer != null ? reserva.Payer.FullName : "Consumidor Final",
                reserva.StartDate,
                TotalSale = Math.Round(reserva.TotalSale, 2),
                Balance = Math.Round(reserva.Balance, 2),
                AlreadyInvoiced = Math.Round(_context.Invoices
                    .AsNoTracking()
                    .Where(invoice => invoice.ReservaId == reserva.Id && invoice.Resultado == "A")
                    .Sum(invoice => (decimal?)(
                        invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8 || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53
                            ? -invoice.ImporteTotal
                            : invoice.ImporteTotal)) ?? 0m, 2),
                ForcedByUserName = _context.Invoices
                    .AsNoTracking()
                    .Where(invoice => invoice.ReservaId == reserva.Id && invoice.Resultado == "A" && invoice.WasForced)
                    .OrderByDescending(invoice => invoice.CreatedAt)
                    .Select(invoice => invoice.ForcedByUserName)
                    .FirstOrDefault()
            })
            .Select(item => new
            {
                item.PublicId,
                item.NumeroReserva,
                item.CustomerName,
                item.StartDate,
                item.TotalSale,
                item.Balance,
                item.AlreadyInvoiced,
                PendingFiscalAmount = item.TotalSale > item.AlreadyInvoiced
                    ? Math.Round(item.TotalSale - item.AlreadyInvoiced, 2)
                    : 0m,
                item.ForcedByUserName
            })
            .Where(item => item.PendingFiscalAmount > 0m)
            .Select(item => new InvoicingWorkItemDto
            {
                ReservaPublicId = item.PublicId,
                NumeroReserva = item.NumeroReserva,
                CustomerName = item.CustomerName,
                StartDate = item.StartDate,
                TotalSale = item.TotalSale,
                AlreadyInvoiced = item.AlreadyInvoiced,
                PendingFiscalAmount = item.PendingFiscalAmount,
                FiscalStatus = item.Balance <= 0m
                    ? "ready"
                    : allowsOverride
                        ? "override"
                        : "blocked",
                FiscalStatusLabel = item.Balance <= 0m ? "Lista para facturar" : "Bloqueada por deuda",
                RequiresOverride = item.Balance > 0m && allowsOverride,
                EconomicBlockReason = item.Balance <= 0m
                    ? null
                    : allowsOverride
                        ? overrideBlockReason
                        : hardBlockReason,
                ForcedByUserName = item.ForcedByUserName
            });
    }

    private static decimal GetNetInvoiceAmount(int tipoComprobante, decimal importeTotal)
    {
        return tipoComprobante == 3 || tipoComprobante == 8 || tipoComprobante == 13 || tipoComprobante == 53
            ? -importeTotal
            : importeTotal;
    }

    private static IQueryable<Invoice> ApplyInvoiceKind(IQueryable<Invoice> query, string? kind)
    {
        var normalizedKind = (kind ?? "all").Trim().ToLowerInvariant();

        return normalizedKind switch
        {
            "creditnote" => query.Where(invoice =>
                invoice.TipoComprobante == 3 ||
                invoice.TipoComprobante == 8 ||
                invoice.TipoComprobante == 13 ||
                invoice.TipoComprobante == 53),
            "issued" => query.Where(invoice =>
                invoice.TipoComprobante != 3 &&
                invoice.TipoComprobante != 8 &&
                invoice.TipoComprobante != 13 &&
                invoice.TipoComprobante != 53),
            _ => query
        };
    }

    private static IQueryable<Invoice> ApplyInvoiceSearch(IQueryable<Invoice> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLowerInvariant();
        return query.Where(invoice =>
            invoice.NumeroComprobante.ToString().Contains(normalized) ||
            invoice.ForceReason != null && invoice.ForceReason.ToLower().Contains(normalized) ||
            invoice.Reserva != null && invoice.Reserva.NumeroReserva.ToLower().Contains(normalized) ||
            invoice.Reserva != null && invoice.Reserva.Payer != null && invoice.Reserva.Payer.FullName.ToLower().Contains(normalized));
    }

    private static IQueryable<Invoice> ApplyInvoiceOrdering(IQueryable<Invoice> query, InvoicesListQuery request)
    {
        var sortBy = (request.SortBy ?? "createdAt").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "numerocomprobante" => desc
                ? query.OrderByDescending(invoice => invoice.NumeroComprobante).ThenByDescending(invoice => invoice.CreatedAt)
                : query.OrderBy(invoice => invoice.NumeroComprobante).ThenByDescending(invoice => invoice.CreatedAt),
            "importetotal" => desc
                ? query.OrderByDescending(invoice => invoice.ImporteTotal).ThenByDescending(invoice => invoice.CreatedAt)
                : query.OrderBy(invoice => invoice.ImporteTotal).ThenByDescending(invoice => invoice.CreatedAt),
            _ => desc
                ? query.OrderByDescending(invoice => invoice.CreatedAt).ThenByDescending(invoice => invoice.Id)
                : query.OrderBy(invoice => invoice.CreatedAt).ThenBy(invoice => invoice.Id)
        };
    }

    private static IQueryable<InvoicingWorkItemDto> ApplyInvoicingWorkItemSearch(
        IQueryable<InvoicingWorkItemDto> query,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLowerInvariant();
        return query.Where(item =>
            item.NumeroReserva.ToLower().Contains(normalized) ||
            item.CustomerName.ToLower().Contains(normalized) ||
            (item.EconomicBlockReason != null && item.EconomicBlockReason.ToLower().Contains(normalized)) ||
            item.FiscalStatusLabel.ToLower().Contains(normalized));
    }

    private static IQueryable<InvoicingWorkItemDto> ApplyInvoicingWorkItemStatus(
        IQueryable<InvoicingWorkItemDto> query,
        string? status)
    {
        var normalizedStatus = (status ?? "all").Trim().ToLowerInvariant();

        return normalizedStatus switch
        {
            "ready" => query.Where(item => item.FiscalStatus == "ready"),
            "blocked" => query.Where(item => item.FiscalStatus == "blocked" || item.FiscalStatus == "override"),
            "override" => query.Where(item => item.FiscalStatus == "override"),
            _ => query
        };
    }

    private static IQueryable<InvoicingWorkItemDto> ApplyInvoicingWorkItemOrdering(
        IQueryable<InvoicingWorkItemDto> query,
        InvoicingWorklistQuery request)
    {
        var sortBy = (request.SortBy ?? "startDate").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "pendingfiscalamount" => desc
                ? query.OrderByDescending(item => item.PendingFiscalAmount).ThenBy(item => item.NumeroReserva)
                : query.OrderBy(item => item.PendingFiscalAmount).ThenBy(item => item.NumeroReserva),
            "numeroreserva" => desc
                ? query.OrderByDescending(item => item.NumeroReserva).ThenByDescending(item => item.StartDate)
                : query.OrderBy(item => item.NumeroReserva).ThenBy(item => item.StartDate),
            _ => desc
                ? query.OrderByDescending(item => item.StartDate).ThenBy(item => item.NumeroReserva)
                : query.OrderBy(item => item.StartDate).ThenBy(item => item.NumeroReserva)
        };
    }
}
