namespace TravelApi.Services.Bsp.Models;

public record BspParsedRecord(
    int LineNumber,
    string RawContent,
    string TicketNumber,
    string ReservationReference,
    DateTime IssueDate,
    string Currency,
    decimal BaseAmount,
    decimal TaxAmount,
    decimal TotalAmount);
