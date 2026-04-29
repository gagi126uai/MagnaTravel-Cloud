using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class GenerateVoucherRequest
{
    public string Scope { get; set; } = VoucherScopes.Reservation;
    public List<string> PassengerIds { get; set; } = new();
}

public class IssueVoucherRequest
{
    public string? Reason { get; set; }
    public string? ExceptionalReason { get; set; }
    public string? AuthorizedBySuperiorUserId { get; set; }
}

public class UploadExternalVoucherRequest
{
    public string Scope { get; set; } = VoucherScopes.Reservation;
    public List<string> PassengerIds { get; set; } = new();
    public string ExternalOrigin { get; set; } = "Operador externo";
}

public class RejectVoucherRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class RevokeVoucherRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class VoucherExceptionRequest
{
    public string? ExceptionalReason { get; set; }
    public string? AuthorizedBySuperiorUserId { get; set; }
}

public class EnsureVoucherSendRequest
{
    public string ReservaId { get; set; } = string.Empty;
    public string? PassengerId { get; set; }
    public VoucherExceptionRequest? Exception { get; set; }
}

public class VoucherDto
{
    public Guid PublicId { get; set; }
    public Guid ReservaPublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? ExternalOrigin { get; set; }
    public bool IsEnabledForSending { get; set; }
    public bool CanSend { get; set; }
    public bool ReservationHasOutstandingBalance { get; set; }
    public decimal OutstandingBalance { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? IssuedByUserName { get; set; }
    public DateTime? IssuedAt { get; set; }
    public bool WasExceptionalIssue { get; set; }
    public string? ExceptionalReason { get; set; }
    public string? AuthorizedBySuperiorUserId { get; set; }
    public string? AuthorizedBySuperiorUserName { get; set; }
    public string AuthorizationStatus { get; set; } = string.Empty;
    public string? RejectReason { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedByUserId { get; set; }
    public string? RevokedByUserName { get; set; }
    public string? RevocationReason { get; set; }
    public List<Guid> PassengerPublicIds { get; set; } = new();
    public List<string> PassengerNames { get; set; } = new();
}

public class VoucherAuditEntryDto
{
    public string Action { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public string? Reason { get; set; }
    public bool ReservationHadOutstandingBalance { get; set; }
    public decimal OutstandingBalance { get; set; }
    public string? AuthorizedBySuperiorUserName { get; set; }
}
