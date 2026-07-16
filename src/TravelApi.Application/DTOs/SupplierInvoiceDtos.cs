using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs;

// OJO: en records los atributos de validacion van en el PARAMETRO del constructor primario
// (sin "property:"). Con [property:] ASP.NET tira InvalidOperationException (500) al validar
// el request ("validation metadata must be associated with the constructor parameter").
// Bug real reportado por Gaston el 2026-06-06 (catalogo) y OTRA VEZ el 2026-07-16 (esta
// pantalla: registrar factura del operador reventaba con el error generico en cada intento).
// El guardian que impide que vuelva a entrar: RecordValidationAttributePlacementTests.
public sealed record SupplierInvoiceCreateRequest(
    [Required, MaxLength(80)] string Number,
    [Required, MaxLength(3)] string Currency,
    DateTime IssuedAt,
    DateTime DueDate,
    [Required, MinLength(1)] IReadOnlyList<SupplierInvoiceLineRequest> Lines);
public sealed record SupplierInvoiceLineRequest(string ServiceRecordKind, Guid ServicePublicId, decimal Amount);
public sealed record SupplierInvoicePaymentApplicationRequest(Guid SupplierPaymentPublicId, decimal Amount);
public sealed record SupplierInvoicePaymentApplicationReversalRequest(
    [Required, MinLength(10), MaxLength(500)] string Reason);
public sealed record SupplierInvoiceVoidRequest(
    [Required, MinLength(10), MaxLength(500)] string Reason);

public sealed class SupplierInvoiceDto
{
    public Guid PublicId { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime DueDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public decimal Applied { get; set; }
    public decimal Pending { get; set; }
    public bool IsDocumentaryOnly { get; set; } = true;
    public bool AmountsVisible { get; set; }
    public IReadOnlyList<SupplierInvoiceLineDto> Lines { get; set; } = Array.Empty<SupplierInvoiceLineDto>();
    public IReadOnlyList<SupplierInvoicePaymentApplicationDto> Applications { get; set; } = Array.Empty<SupplierInvoicePaymentApplicationDto>();
}
public sealed record SupplierInvoiceLineDto(Guid PublicId, Guid ReservaPublicId, string? ReservaNumber, string ServiceType, Guid ServicePublicId, string Description, decimal Amount);
public sealed record SupplierInvoicePaymentApplicationDto(
    Guid PublicId, Guid SupplierPaymentPublicId, decimal Amount, DateTime CreatedAt, string? CreatedByUserName,
    bool IsReversed, DateTime? ReversedAt, string? ReversedByUserName, string? ReversalReason);
