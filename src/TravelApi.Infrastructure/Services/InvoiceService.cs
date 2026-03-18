using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
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
    private readonly IAfipService _afipService;
    private readonly IInvoicePdfService _pdfService;
    private readonly IMapper _mapper;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(
        AppDbContext context, 
        IAfipService afipService, 
        IInvoicePdfService pdfService,
        IMapper mapper,
        IBackgroundJobClient backgroundJobClient,
        ILogger<InvoiceService> logger)
    {
        _context = context;
        _afipService = afipService;
        _pdfService = pdfService;
        _mapper = mapper;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public async Task<IEnumerable<InvoiceDto>> GetAllAsync(CancellationToken ct)
    {
        return await _context.Invoices
            .OrderByDescending(i => i.CreatedAt)
            .ProjectTo<InvoiceDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<InvoiceDto> CreateAsync(CreateInvoiceRequest request, CancellationToken ct)
    {
        // 1. Create Pending in DB
        var invoice = await _afipService.CreatePendingInvoice(request.ReservaId, request);
        
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

    public async Task<IEnumerable<InvoiceDto>> GetByReservaIdAsync(int reservaId, CancellationToken ct)
    {
        return await _context.Invoices
            .Where(i => i.ReservaId == reservaId)
            .OrderByDescending(i => i.CreatedAt)
            .ProjectTo<InvoiceDto>(_mapper.ConfigurationProvider)
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
                ReservaId = original.ReservaId ?? 0,
                CbteTipo = cbteTipo,
                Concepto = 3, // Productos y Servicios (default)
                DocTipo = 99, // Sin info
                DocNro = 0,
                OriginalInvoiceId = invoiceId,
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
            var newInvoice = await _afipService.CreatePendingInvoice(request.ReservaId, request);
            
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
}
