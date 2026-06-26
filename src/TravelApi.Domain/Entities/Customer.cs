using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public class Customer : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    [MaxLength(20)]
    public string? DocumentType { get; set; } // DNI / Pasaporte / CUIT / CUIL / LE / LC
    public string? DocumentNumber { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Retail Pivot: Financials
    [MaxLength(20)]
    public string? TaxId { get; set; } // CUIT/CUIL
    [MaxLength(50)]
    public string TaxCondition { get; set; } = "Consumidor Final"; // Responsable Inscripto, Monotributo, Exento, Consumidor Final
    public int? TaxConditionId { get; set; } // AFIP Code: 1=RI, 4=Exento, 5=Consumidor Final, 6=Monotributo
    public decimal CreditLimit { get; set; } = 0;
    // ADR-023 T1.6: zombie. NUNCA se escribe (nunca se escribio) y desde ADR-023 NUNCA se lee: el saldo a cobrar
    // del cliente se deriva de ReservaMoneyByCurrency via FinancePositionService (fuente unica). No borrar la
    // columna: son datos historicos y poblarla seria una cuarta fuente de verdad.
    public decimal CurrentBalance { get; set; } = 0; // Positive = they owe us
    public bool IsActive { get; set; } = true;

    // ============================================================
    // ADR-040 (cuenta corriente del cliente, 2026-06-26).
    // ============================================================

    /// <summary>
    /// ADR-040: forma de cobro de ESTE cliente. <b>Nullable a proposito</b>: null = "heredar el default de la
    /// agencia" (<c>OperationalFinanceSettings.DefaultCustomerBillingMode</c>). Es un estado real distinto de
    /// "fijado a mano a Prepaid". Mientras siga null o Prepaid, el cliente se comporta byte-identico al prepago
    /// puro de ADR-036. Un cliente solo entra a cuenta corriente cuando se setea explicitamente a
    /// <see cref="CustomerBillingMode.Account"/> (accion sensible: se audita quien y viejo-&gt;nuevo).
    /// </summary>
    public CustomerBillingMode? BillingMode { get; set; }

    /// <summary>
    /// ADR-040: dias de plazo de la cuenta corriente del cliente (vencimiento). Default 0 = sin plazo definido.
    /// <b>FASE 1: todavia NO se usa para calcular mora</b> — el evaluador de credito recibe <c>enMora=false</c>.
    /// Se agrega ya para que la Fase 2 (vencimientos/aging) lo tenga disponible sin otra migracion. Cambiar este
    /// valor es accion sensible (define cuando una deuda esta vencida): se audita.
    /// </summary>
    public int PaymentTermsDays { get; set; } = 0;

    public ICollection<ServicioReserva> Reservations { get; set; } = new List<ServicioReserva>();
    public ICollection<Reserva> Reservas { get; set; } = new List<Reserva>();

    /// <summary>ADR-040: limites de credito por moneda (cuenta corriente). Ver <see cref="CustomerCreditLimitByCurrency"/>.</summary>
    public ICollection<CustomerCreditLimitByCurrency> CreditLimitsByCurrency { get; set; } = new List<CustomerCreditLimitByCurrency>();
}
