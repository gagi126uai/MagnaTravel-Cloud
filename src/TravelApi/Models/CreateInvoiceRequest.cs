using TravelApi.DTOs;

namespace TravelApi.Models;

public class CreateInvoiceRequest
{
    public int TravelFileId { get; set; }
    public List<InvoiceItemDto> Items { get; set; } = new();
    public List<InvoiceTributeDto> Tributes { get; set; } = new();
    public int? OriginalInvoiceId { get; set; } // If present, this is a Credit/Debit Note
    public bool IsCreditNote { get; set; } // Explicit flag for NC
}
