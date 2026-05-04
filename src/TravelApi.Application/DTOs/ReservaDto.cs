namespace TravelApi.Application.DTOs;

public class ReservaDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Budget";
    public Guid? CustomerPublicId { get; set; }
    public Guid? SourceLeadPublicId { get; set; }
    public Guid? SourceQuotePublicId { get; set; }
    public string? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public string? WhatsAppPhoneOverride { get; set; }
    public bool IsEconomicallySettled { get; set; }
    public bool CanMoveToOperativo { get; set; }
    public bool CanEmitVoucher { get; set; }
    public bool CanEmitAfipInvoice { get; set; }
    public string? EconomicBlockReason { get; set; }
    public bool IsInProgress { get; set; }
    /// <summary>True si el cliente no debe nada (Balance == 0). Chip verde "Pagada".</summary>
    public bool IsFullyPaid { get; set; }
    /// <summary>True si el viaje termino y todavia hay deuda (EndDate &lt; hoy AND Balance &gt; 0). Chip rojo "Vencida con deuda".</summary>
    public bool HasOverdueDebt { get; set; }

    public string? CustomerName { get; set; } // Flattened
    public CustomerDto? Payer { get; set; } // Nested for frontend convenience
    public decimal TotalCost { get; set; }
    public decimal TotalSale { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }
    public int AdultCount { get; set; }
    public int ChildCount { get; set; }
    public int InfantCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    /// <summary>
    /// Fecha de salida computada del primer servicio cargado (min de fechas).
    /// Se expone para que la UI pueda sugerir un valor cuando StartDate este vacio.
    /// </summary>
    public DateTime? SuggestedStartDate { get; set; }
    /// <summary>
    /// Fecha de regreso computada del ultimo servicio cargado (max de fechas).
    /// Se expone para que la UI pueda sugerir un valor cuando EndDate este vacio.
    /// </summary>
    public DateTime? SuggestedEndDate { get; set; }
    public DateTime? ClosedAt { get; set; }
    
    // Collections
    public List<PassengerDto> Passengers { get; set; } = new();
    public List<FlightSegmentDto> FlightSegments { get; set; } = new();
    public List<HotelBookingDto> HotelBookings { get; set; } = new();
    public List<TransferBookingDto> TransferBookings { get; set; } = new();
    public List<PackageBookingDto> PackageBookings { get; set; } = new();
    public List<ServicioReservaDto> Servicios { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();    
}
