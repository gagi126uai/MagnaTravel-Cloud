namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-013 §3.7 (2026-06-01): "quien se queda la penalidad" de cancelacion para un
/// operador. Es un eje ORTOGONAL a <see cref="SupplierInvoicingMode"/> (que es
/// reseller-vs-intermediario, otra cosa): un operador puede ser reseller y aun asi
/// quedarse o no con la penalidad.
///
/// <para><b>Por que importa</b>: define si la penalidad es ingreso propio de la
/// agencia (puede emitir ND) o pass-through del operador (NO emite ND, seria
/// declarar ingreso ajeno). Este valor se CONGELA en el snapshot al momento del
/// evento, para que una cancelacion futura use el acuerdo vigente AL MOMENTO.</para>
///
/// <para>Default <see cref="Operator"/> a proposito: es el valor conservador
/// (= pass-through = NO ND = comportamiento de hoy). El operador se configura como
/// <see cref="Agency"/> solo cuando el acuerdo dice que la agencia se queda la multa.</para>
/// </summary>
public enum PenaltyOwnership
{
    /// <summary>La penalidad la retiene el operador (pass-through). Default conservador = NO ND.</summary>
    Operator = 0,

    /// <summary>La penalidad es ingreso propio de la agencia. Habilita emitir ND propia.</summary>
    Agency = 1,
}
