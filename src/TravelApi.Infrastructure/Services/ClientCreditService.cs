using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.2.2 v3 §2.3 (2026-05-18, <b>implementacion parcial</b>): gestiona el
/// saldo a favor del cliente cuando el operador devuelve fondos.
///
/// <para>
/// <b>Alcance FC1.2.2</b>: SOLO <see cref="CreateEntryAsync"/>. Es lo unico que
/// invoca <c>OperatorRefundService.AllocateAsync</c> en FC1.2.2. El resto del
/// flujo (retiros del cliente, ManualCashMovement Expense, reversal al operador)
/// llega en FC1.2.3 como parte de esa fase del plan tactico.
/// </para>
///
/// <para>
/// <b>Patron de "no commit aca"</b>: este service NO hace <c>SaveChangesAsync</c>
/// porque es llamado en cadena DESDE <c>OperatorRefundService</c> que ya tiene
/// una transaccion envolvente. Hacer commit aca rompe la atomicidad — si un
/// paso posterior falla, la allocation queda creada pero sin el entry o
/// viceversa. Reglas HC1/HC2 del plan v3.
/// </para>
///
/// <para>
/// <b>Por que no es un stub <c>NotImplementedException</c></b>: aunque el service
/// es "stub" desde el punto de vista de la API publica (no hay metodos de retiro),
/// la logica de creacion del entry SI es definitiva — no la vamos a reescribir
/// en FC1.2.3. La unica logica que crece es alrededor de los retiros.
/// </para>
/// </summary>
public class ClientCreditService : IClientCreditService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ClientCreditService> _logger;

    public ClientCreditService(
        AppDbContext db,
        ILogger<ClientCreditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Crea un <see cref="ClientCreditEntry"/> con saldo inicial igual al
    /// <paramref name="netAmount"/> del allocation que lo origino. NO hace
    /// <c>SaveChangesAsync</c> — el caller (OperatorRefundService) commitea
    /// la transaccion envolvente.
    ///
    /// <para>
    /// <b>RemainingBalance arranca = CreditedAmount</b>: el cliente todavia no
    /// retiro nada. El primer Withdraw (FC1.2.3) decrementa este valor.
    /// </para>
    ///
    /// <para>
    /// <b>Currency</b>: lo recibimos del caller (no se usa todavia en la entidad
    /// porque el saldo se interpreta en la moneda del FiscalSnapshot del BC,
    /// que es la misma del refund). Lo dejamos en el parametro porque FC1.2.3
    /// va a guardar las monedas explicitamente para multi-moneda.
    /// </para>
    /// </summary>
    public Task<ClientCreditEntry> CreateEntryAsync(
        int bookingCancellationId,
        int operatorRefundAllocationId,
        int customerId,
        decimal netAmount,
        string currency,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (netAmount <= 0m)
        {
            // Defensivo: si por algun motivo el OperatorRefundService llamara aca
            // con netAmount=0 (porque todas las deducciones igualaron al gross),
            // no tendria sentido crear un entry con saldo cero. Loggeamos +
            // retornamos null seria peor (rompe contrato). Mejor fallar fuerte:
            // el service caller deberia haber rechazado antes.
            throw new ArgumentException(
                "ClientCreditEntry no se puede crear con netAmount <= 0.",
                nameof(netAmount));
        }

        var entry = new ClientCreditEntry
        {
            BookingCancellationId = bookingCancellationId,
            OperatorRefundAllocationId = operatorRefundAllocationId,
            CustomerId = customerId,
            CreditedAmount = netAmount,
            RemainingBalance = netAmount,
            CreatedAt = DateTime.UtcNow,
            IsFullyConsumed = false,
        };
        _db.ClientCreditEntries.Add(entry);

        _logger.LogDebug(
            "ClientCreditEntry pendiente Add para BcId={BcId} AllocationId={AllocationId} Customer={CustomerId} NetAmount={NetAmount} {Currency}.",
            bookingCancellationId, operatorRefundAllocationId, customerId, netAmount, currency);

        // No SaveChanges: el OperatorRefundService.AllocateAsync hara el commit.
        return Task.FromResult(entry);
    }
}
