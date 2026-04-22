namespace TravelApi.Application.DTOs;

public class LeadActivityDto
{
    public Guid PublicId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LeadSummaryDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? InterestedIn { get; set; }
    public string? TravelDates { get; set; }
    public string? Travelers { get; set; }
    public decimal EstimatedBudget { get; set; }
    public string? Notes { get; set; }
    public string? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime? NextFollowUp { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Guid? ConvertedCustomerPublicId { get; set; }
    public string? ConvertedCustomerName { get; set; }
    public int ActivitiesCount { get; set; }
    public string? LastActivity { get; set; }
}

public class LeadDetailDto : LeadSummaryDto
{
    public List<LeadActivityDto> Activities { get; set; } = new();
}

public class LeadConversionResultDto
{
    public Guid CustomerPublicId { get; set; }
}

public class QuoteDraftResultDto
{
    public Guid QuotePublicId { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public Guid? CustomerPublicId { get; set; }
    public Guid LeadPublicId { get; set; }
}

public class QuoteConversionResultDto
{
    public Guid ReservaPublicId { get; set; }
}

public class CustomerReferenceDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
}

public class LeadReferenceDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
}

public class ReservaReferenceDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public class QuoteItemDto
{
    public Guid PublicId { get; set; }
    public string ServiceType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? SupplierPublicId { get; set; }
    public string? SupplierName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal MarkupPercent { get; set; }
    public decimal TotalCost { get; set; }
    public decimal TotalPrice { get; set; }
    public Guid? RatePublicId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class QuoteSummaryDto
{
    public Guid PublicId { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? CustomerPublicId { get; set; }
    public string? CustomerName { get; set; }
    public Guid? LeadPublicId { get; set; }
    public string? LeadName { get; set; }
    public Guid? ConvertedReservaPublicId { get; set; }
    public string? ConvertedReservaNumeroReserva { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? TravelStartDate { get; set; }
    public DateTime? TravelEndDate { get; set; }
    public string? Destination { get; set; }
    public int Adults { get; set; }
    public int Children { get; set; }
    public decimal TotalCost { get; set; }
    public decimal TotalSale { get; set; }
    public decimal GrossMargin { get; set; }
    public string? Notes { get; set; }
}

public class QuoteDetailDto : QuoteSummaryDto
{
    public CustomerReferenceDto? Customer { get; set; }
    public LeadReferenceDto? Lead { get; set; }
    public ReservaReferenceDto? ConvertedReserva { get; set; }
    public List<QuoteItemDto> Items { get; set; } = new();
}

public class UpsertQuoteRequest
{
    public string? CustomerPublicId { get; set; }
    public string? LeadPublicId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime? TravelStartDate { get; set; }
    public DateTime? TravelEndDate { get; set; }
    public string? Destination { get; set; }
    public int Adults { get; set; } = 2;
    public int Children { get; set; }
    public string? Notes { get; set; }
}

public class UpsertQuoteItemRequest
{
    public string ServiceType { get; set; } = "Hotel";
    public string Description { get; set; } = string.Empty;
    public string? SupplierPublicId { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal UnitCost { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal MarkupPercent { get; set; }
    public string? RateId { get; set; }
    public string? Notes { get; set; }
}
