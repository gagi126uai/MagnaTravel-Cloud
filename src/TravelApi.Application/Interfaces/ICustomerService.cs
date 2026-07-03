using TravelApi.Domain.Entities;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ICustomerService
{
    Task<PagedResponse<CustomerListItemDto>> GetCustomersAsync(CustomerListQuery query, CancellationToken cancellationToken);
    Task<CustomerListItemDto> GetCustomerAsync(int id, CancellationToken cancellationToken);
    Task<Customer> CreateCustomerAsync(Customer customer, CancellationToken cancellationToken);
    Task<Customer> UpdateCustomerAsync(int id, Customer customer, CancellationToken cancellationToken);

    /// <summary>
    /// ADR-040 (cuenta corriente del cliente, 2026-06-26): setea la configuracion de cuenta corriente de un
    /// cliente — su modo de cobro (Prepaid/Account/null=hereda el default de agencia), su plazo de pago y sus
    /// limites de credito POR MONEDA. Es la accion que ACTIVA la cuenta corriente para un cliente. Es SENSIBLE
    /// (define cuanta plata se presta): se audita quien y viejo-&gt;nuevo, atomico con el cambio.
    ///
    /// <para><b>Limites por moneda</b>: el diccionario es el ESTADO DESEADO COMPLETO. Las monedas presentes se
    /// upsertean; las monedas que el cliente tenia y ya no estan se BORRAN (ausencia = esa moneda vuelve a ser
    /// prepago). El actor (userId/userName) lo resuelve el caller desde el contexto autenticado; este metodo no
    /// toca HttpContext para seguir siendo testeable.</para>
    /// </summary>
    Task<Customer> UpdateCustomerCreditConfigAsync(
        int id,
        CustomerBillingMode? billingMode,
        int paymentTermsDays,
        IReadOnlyDictionary<string, decimal> creditLimitsByCurrency,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken);

    Task<CustomerDeletionResult> DeleteOrArchiveCustomerAsync(int id, CancellationToken cancellationToken);
    Task<Customer> ReactivateCustomerAsync(int id, CancellationToken cancellationToken);
    Task<CustomerAccountOverviewDto> GetCustomerAccountOverviewAsync(int id, CancellationToken cancellationToken);
    Task<PagedResponse<CustomerAccountReservaListItemDto>> GetCustomerAccountReservasAsync(int id, PagedQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<CustomerAccountPaymentListItemDto>> GetCustomerAccountPaymentsAsync(int id, PagedQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<InvoiceListDto>> GetCustomerAccountInvoicesAsync(int id, PagedQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Deuda del cliente DESGLOSADA POR RESERVA y por moneda (solo reservas con saldo pendiente). Misma
    /// fuente y filtro que el saldo a cobrar por moneda (ReservaMoneyByCurrency en firme), sin agregar a
    /// traves de reservas. El front lo usa para ofrecer destinos de "usar saldo a favor" en la moneda correcta.
    /// </summary>
    Task<CustomerDebtByReservaDto> GetCustomerDebtByReservaAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// EXTRACTO (libro mayor) de la cuenta por cobrar del cliente: una linea por cada venta confirmada (cargo)
    /// y cada cobro (abono), con saldo corriente POR MONEDA, calculado EN EL SERVIDOR. El saldo de cierre de
    /// cada moneda reconcilia por construccion con el "Debe" por moneda del header (ReceivableByCurrency),
    /// porque parte de la MISMA fuente (ConfirmedSale - TotalPaid de ReservaMoneyByCurrency en firme). Reemplaza
    /// el armado en el navegador (que mezclaba pagos+facturas con techo de 500 y no cerraba con el resumen).
    /// </summary>
    Task<CustomerAccountStatementDto> GetCustomerAccountStatementAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Lista los saldos a favor DISPONIBLES (RemainingBalance &gt; 0) del cliente, ordenados del más
    /// viejo al más nuevo (FIFO de consumo). El front lo usa para que el usuario elija de qué entry
    /// retirar/aplicar. El agregado por moneda para el cartel ya viene en GetCustomerAccountOverviewAsync.
    /// </summary>
    Task<IReadOnlyList<CustomerAvailableCreditEntryDto>> GetCustomerAvailableCreditAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerSimilarMatchDto>> SearchSimilarAsync(string? fullName, string? documentType, string? documentNumber, string? phone, int take, CancellationToken cancellationToken);
}

public enum CustomerDeletionOutcome
{
    HardDeleted,
    Archived
}

public record CustomerDeletionResult(CustomerDeletionOutcome Outcome, string Message);

public class CustomerSimilarMatchDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public int Score { get; set; }
}
