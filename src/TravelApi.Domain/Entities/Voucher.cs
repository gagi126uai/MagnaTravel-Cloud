using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public static class VoucherSources
{
    public const string Generated = "Generated";
    public const string External = "External";
}

public static class VoucherStatuses
{
    public const string Draft = "Draft";
    public const string PendingAuthorization = "PendingAuthorization";
    public const string Issued = "Issued";
    public const string UploadedExternal = "UploadedExternal";
    public const string Revoked = "Revoked";
}

public static class VoucherAuthorizationStatuses
{
    public const string NotRequired = "NotRequired";
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class VoucherScopes
{
    public const string Reservation = "ReservaCompleta";
    public const string AllPassengers = "TodosLosPasajeros";
    public const string SelectedPassengers = "PasajerosSeleccionados";
}

public static class VoucherAuditActions
{
    public const string Generated = "Generated";
    public const string Issued = "Issued";
    public const string UploadedExternal = "UploadedExternal";
    public const string ExceptionalIssue = "ExceptionalIssue";
    public const string Sent = "Sent";
    public const string ExceptionalSend = "ExceptionalSend";
    public const string AuthorizationRequested = "AuthorizationRequested";
    public const string AuthorizationApproved = "AuthorizationApproved";
    public const string AuthorizationRejected = "AuthorizationRejected";
}

public class Voucher : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    [MaxLength(30)]
    public string Source { get; set; } = VoucherSources.Generated;

    [MaxLength(30)]
    public string Status { get; set; } = VoucherStatuses.Draft;

    [MaxLength(40)]
    public string Scope { get; set; } = VoucherScopes.Reservation;

    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? StoredFileName { get; set; }

    [MaxLength(120)]
    public string ContentType { get; set; } = "application/pdf";

    public long FileSize { get; set; }

    [MaxLength(200)]
    public string? ExternalOrigin { get; set; }

    public bool IsEnabledForSending { get; set; }

    [MaxLength(200)]
    public string? CreatedByUserId { get; set; }

    [MaxLength(200)]
    public string? CreatedByUserName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(200)]
    public string? IssuedByUserId { get; set; }

    [MaxLength(200)]
    public string? IssuedByUserName { get; set; }

    public DateTime? IssuedAt { get; set; }

    [MaxLength(1000)]
    public string? IssueReason { get; set; }

    public bool WasExceptionalIssue { get; set; }

    [MaxLength(1000)]
    public string? ExceptionalReason { get; set; }

    [MaxLength(200)]
    public string? AuthorizedBySuperiorUserId { get; set; }

    [MaxLength(200)]
    public string? AuthorizedBySuperiorUserName { get; set; }

    [MaxLength(30)]
    public string AuthorizationStatus { get; set; } = VoucherAuthorizationStatuses.NotRequired;

    [MaxLength(1000)]
    public string? RejectReason { get; set; }

    public decimal OutstandingBalanceAtIssue { get; set; }

    public ICollection<VoucherPassengerAssignment> PassengerAssignments { get; set; } = new List<VoucherPassengerAssignment>();
    public ICollection<VoucherAuditEntry> AuditEntries { get; set; } = new List<VoucherAuditEntry>();

    public bool CanBeSent() =>
        IsEnabledForSending &&
        (Status == VoucherStatuses.Issued || Status == VoucherStatuses.UploadedExternal);
}

public class VoucherPassengerAssignment
{
    public int Id { get; set; }
    public int VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    public int PassengerId { get; set; }
    public Passenger? Passenger { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class VoucherAuditEntry
{
    public int Id { get; set; }
    public int VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(200)]
    public string UserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? UserName { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [MaxLength(1000)]
    public string? Reason { get; set; }

    public bool ReservationHadOutstandingBalance { get; set; }
    public decimal OutstandingBalance { get; set; }

    [MaxLength(200)]
    public string? AuthorizedBySuperiorUserId { get; set; }

    [MaxLength(200)]
    public string? AuthorizedBySuperiorUserName { get; set; }

    [MaxLength(2000)]
    public string? Details { get; set; }
}
