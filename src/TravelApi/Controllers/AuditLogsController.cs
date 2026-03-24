using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AuditLogsController : ControllerBase
{
    private readonly IAuditService _auditService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public AuditLogsController(IAuditService auditService, EntityReferenceResolver entityReferenceResolver)
    {
        _auditService = auditService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditLog>>> GetAuditLogs(
        [FromQuery] string? entityName,
        [FromQuery] string? entityId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? userId,
        CancellationToken ct)
    {
        var alternateEntityId = await ResolveAlternateEntityIdAsync(entityName, entityId, ct);
        var logs = await _auditService.GetAuditLogsAsync(entityName, entityId, alternateEntityId, dateFrom, dateTo, userId, ct);
        return Ok(logs);
    }

    private async Task<string?> ResolveAlternateEntityIdAsync(string? entityName, string? entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(entityId))
            return null;

        return entityName switch
        {
            nameof(Reserva) => await ResolveInternalIdAsync<Reserva>(entityId, ct),
            nameof(Customer) => await ResolveInternalIdAsync<Customer>(entityId, ct),
            nameof(Supplier) => await ResolveInternalIdAsync<Supplier>(entityId, ct),
            nameof(Lead) => await ResolveInternalIdAsync<Lead>(entityId, ct),
            nameof(Quote) => await ResolveInternalIdAsync<Quote>(entityId, ct),
            nameof(Payment) => await ResolveInternalIdAsync<Payment>(entityId, ct),
            nameof(Invoice) => await ResolveInternalIdAsync<Invoice>(entityId, ct),
            nameof(Passenger) => await ResolveInternalIdAsync<Passenger>(entityId, ct),
            nameof(ServicioReserva) => await ResolveInternalIdAsync<ServicioReserva>(entityId, ct),
            nameof(FlightSegment) => await ResolveInternalIdAsync<FlightSegment>(entityId, ct),
            nameof(HotelBooking) => await ResolveInternalIdAsync<HotelBooking>(entityId, ct),
            nameof(PackageBooking) => await ResolveInternalIdAsync<PackageBooking>(entityId, ct),
            nameof(TransferBooking) => await ResolveInternalIdAsync<TransferBooking>(entityId, ct),
            nameof(ReservaAttachment) => await ResolveInternalIdAsync<ReservaAttachment>(entityId, ct),
            _ => null
        };
    }

    private async Task<string?> ResolveInternalIdAsync<TEntity>(string entityId, CancellationToken ct)
        where TEntity : class, IHasPublicId
    {
        var entity = await _entityReferenceResolver.FindAsync<TEntity>(entityId, ct);
        return entity switch
        {
            Customer customer => customer.Id.ToString(),
            Supplier supplier => supplier.Id.ToString(),
            Reserva reserva => reserva.Id.ToString(),
            Lead lead => lead.Id.ToString(),
            Quote quote => quote.Id.ToString(),
            Payment payment => payment.Id.ToString(),
            Invoice invoice => invoice.Id.ToString(),
            Passenger passenger => passenger.Id.ToString(),
            ServicioReserva servicio => servicio.Id.ToString(),
            FlightSegment flight => flight.Id.ToString(),
            HotelBooking hotel => hotel.Id.ToString(),
            PackageBooking packageBooking => packageBooking.Id.ToString(),
            TransferBooking transfer => transfer.Id.ToString(),
            ReservaAttachment attachment => attachment.Id.ToString(),
            _ => null
        };
    }
}
