namespace TravelApi.Application.DTOs;

/// <summary>
/// FC4 (saldo a favor del cliente aplicado a otra reserva): vista del saldo a favor DISPONIBLE del cliente,
/// agrupado por moneda. Espejo conceptual de <see cref="SupplierCreditOverviewDto"/> del lado operador.
///
/// <para>El total por moneda es la suma de <c>RemainingBalance</c> de los bolsillos activos
/// (<see cref="ClientCreditEntry"/>), que es la fuente AUTORITATIVA: el saldo a favor del cliente es un ledger
/// de PRIMERA CLASE (se decrementa atomicamente en cada retiro), no un numero derivado. A diferencia del lado
/// operador, los montos NO se enmascaran por <c>cobranzas.see_cost</c>: es plata del cliente (venta/cobranza),
/// no un costo de la agencia.</para>
/// </summary>
public class ClientCreditOverviewDto
{
    public Guid CustomerPublicId { get; set; }
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Una linea por moneda con saldo a favor disponible (&gt; 0).</summary>
    public List<ClientCreditCurrencyLineDto> Currencies { get; set; } = new();

    /// <summary>
    /// Aplicaciones VIVAS de saldo a favor a otras reservas del cliente (cada retiro
    /// <see cref="ClientCreditWithdrawal"/> de kind <c>AppliedToNewBooking</c> con su Payment puente todavia
    /// activo). El front las lista en el extracto para revertir cada una por su <c>ApplicationPublicId</c>. Asi
    /// un apply que drenó N bolsillos queda como N filas, cada una revertible de forma independiente.
    /// </summary>
    public List<ClientCreditApplicationLineDto> ActiveApplications { get; set; } = new();
}

/// <summary>
/// FC4: una aplicacion VIVA de saldo a favor (un retiro <c>AppliedToNewBooking</c> con puente activo). Es la
/// fila revertible que el front muestra en el extracto del cliente ("Saldo a favor aplicado a R-XXXX −$monto").
/// </summary>
public class ClientCreditApplicationLineDto
{
    /// <summary>PublicId del retiro a revertir (lo recibe el endpoint <c>.../credit/applications/{publicId}/reverse</c>).</summary>
    public Guid ApplicationPublicId { get; set; }

    /// <summary>PublicId del bolsillo del que salio.</summary>
    public Guid EntryPublicId { get; set; }

    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }

    public Guid TargetReservaPublicId { get; set; }
    public string? TargetReservaNumber { get; set; }

    /// <summary>Titular de la reserva destino. Por INV-093 siempre es el MISMO cliente del saldo a favor.</summary>
    public string? TargetReservaHolderName { get; set; }

    public DateTime AppliedAt { get; set; }
}

/// <summary>Saldo a favor disponible del cliente en UNA moneda, con el detalle de bolsillos activos.</summary>
public class ClientCreditCurrencyLineDto
{
    public string Currency { get; set; } = "ARS";

    /// <summary>Suma de <c>RemainingBalance</c> de los bolsillos activos en esta moneda. Disponible para aplicar.</summary>
    public decimal AvailableBalance { get; set; }

    public List<ClientCreditEntryLineDto> Entries { get; set; } = new();
}

/// <summary>Un bolsillo de saldo a favor del cliente (entry) con su saldo disponible.</summary>
public class ClientCreditEntryLineDto
{
    public Guid PublicId { get; set; }
    public decimal CreditedAmount { get; set; }
    public decimal RemainingBalance { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// FC4: pedido de APLICAR saldo a favor del cliente a otra reserva del mismo cliente (misma moneda). El backend
/// drena los bolsillos por antiguedad (FIFO) hasta cubrir el <see cref="Amount"/>.
/// </summary>
public record ApplyClientCreditRequest(
    string Currency,
    decimal Amount,
    Guid TargetReservaPublicId);

/// <summary>
/// FC4: pedido de REVERTIR una aplicacion de saldo a favor del cliente. El motivo es OPCIONAL (decision del
/// dueño): puede venir null/vacio y la reversa procede igual; si viene, se registra en la auditoria.
/// </summary>
public record ReverseClientCreditApplicationRequest(string? Reason);

/// <summary>FC4: resultado de aplicar/revertir saldo a favor del cliente (para el front).</summary>
public class ClientCreditApplicationResultDto
{
    /// <summary>
    /// PublicId de la "aplicacion" (el <see cref="ClientCreditWithdrawal"/> de kind <c>AppliedToNewBooking</c>).
    /// Si la aplicacion drena VARIOS bolsillos (FIFO), aca viaja el PRIMER retiro creado; cada retiro se revierte
    /// de forma independiente con su propio PublicId (mismo contrato que el lado operador).
    /// </summary>
    public Guid ApplicationPublicId { get; set; }

    /// <summary>PublicId del bolsillo (<see cref="ClientCreditEntry"/>) del primer retiro.</summary>
    public Guid EntryPublicId { get; set; }

    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
    public Guid TargetReservaPublicId { get; set; }
    public bool IsReversal { get; set; }

    /// <summary>Saldo a favor que le queda al cliente en esa moneda DESPUES del movimiento.</summary>
    public decimal AvailableBalanceAfter { get; set; }
}
