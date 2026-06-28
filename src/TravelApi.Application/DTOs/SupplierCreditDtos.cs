namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-041 TANDA 3: vista del saldo a favor consumible con un operador, agrupado por moneda. El total por
/// moneda es la suma de <c>RemainingBalance</c> de los entries activos (fuente AUTORITATIVA), NUNCA un
/// <c>max(0,-Balance)</c> calculado al vuelo. Los montos respetan el masking <c>cobranzas.see_cost</c>.
/// </summary>
public class SupplierCreditOverviewDto
{
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>Una linea por moneda con saldo a favor disponible (&gt; 0).</summary>
    public List<SupplierCreditCurrencyLineDto> Currencies { get; set; } = new();

    /// <summary>
    /// Aplicaciones VIVAS de saldo a favor a otras reservas del operador (cada <see cref="SupplierCreditApplication"/>
    /// de Kind=Applied que todavia NO fue revertida). El front las lista para poder revertir cada una por su
    /// <c>ApplicationPublicId</c>. Espejo de <c>ClientCreditOverviewDto.ActiveApplications</c> del lado cliente.
    /// Los montos respetan el masking <c>cobranzas.see_cost</c> igual que el resto del overview (es costo de la agencia).
    /// </summary>
    public List<SupplierCreditApplicationLineDto> ActiveApplications { get; set; } = new();
}

/// <summary>
/// ADR-041 TANDA 3 (lado operador): una aplicacion VIVA de saldo a favor (una <c>SupplierCreditApplication</c>
/// Applied sin reversa). Es la fila revertible que el front muestra para deshacer la imputacion en una reserva
/// destino. Espejo conceptual de <c>ClientCreditApplicationLineDto</c> del lado cliente.
/// </summary>
public class SupplierCreditApplicationLineDto
{
    /// <summary>PublicId de la aplicacion a revertir (lo recibe el endpoint <c>.../credit/applications/{publicId}/reverse</c>).</summary>
    public Guid ApplicationPublicId { get; set; }

    /// <summary>PublicId del bolsillo (entry) del que salio.</summary>
    public Guid EntryPublicId { get; set; }

    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }

    public Guid TargetReservaPublicId { get; set; }
    public string? TargetReservaNumber { get; set; }

    /// <summary>Titular de la reserva destino (puede ser cualquier cliente; a diferencia del lado cliente no aplica INV-093).</summary>
    public string? TargetReservaHolderName { get; set; }

    public DateTime AppliedAt { get; set; }
}

/// <summary>Saldo a favor disponible con el operador en UNA moneda, con el detalle de bolsillos activos.</summary>
public class SupplierCreditCurrencyLineDto
{
    public string Currency { get; set; } = "ARS";

    /// <summary>Suma de <c>RemainingBalance</c> de los bolsillos activos en esta moneda. Disponible para aplicar.</summary>
    public decimal AvailableBalance { get; set; }

    public List<SupplierCreditEntryLineDto> Entries { get; set; } = new();
}

/// <summary>Un bolsillo de saldo a favor (entry) con su saldo disponible.</summary>
public class SupplierCreditEntryLineDto
{
    public Guid PublicId { get; set; }
    public decimal CreditedAmount { get; set; }
    public decimal RemainingBalance { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>ADR-041 TANDA 3: pedido de APLICAR saldo a favor del operador a otra reserva del mismo operador.</summary>
public record ApplySupplierCreditRequest(
    string Currency,
    decimal Amount,
    Guid TargetReservaPublicId);

/// <summary>
/// ADR-041 TANDA 3: pedido de REVERTIR una aplicacion de saldo a favor del operador. El motivo es OPCIONAL
/// (decision del dueño, simetrico con el lado cliente): puede venir null/vacio y la reversa procede igual; si
/// viene, se registra en la auditoria.
/// </summary>
public record ReverseSupplierCreditApplicationRequest(string? Reason);

/// <summary>ADR-041 TANDA 3: resultado de aplicar/revertir saldo a favor del operador (para el front).</summary>
public class SupplierCreditApplicationResultDto
{
    public Guid ApplicationPublicId { get; set; }
    public Guid EntryPublicId { get; set; }
    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
    public Guid TargetReservaPublicId { get; set; }
    public bool IsReversal { get; set; }

    /// <summary>Saldo a favor que queda disponible con el operador en esa moneda DESPUES del movimiento.</summary>
    public decimal AvailableBalanceAfter { get; set; }
}
