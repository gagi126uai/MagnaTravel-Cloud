namespace TravelApi.Domain.Entities;

/// <summary>
/// Configuracion de multas de cancelacion (2026-07-14): que tan seguido este operador COBRA multa cuando
/// se anula un servicio suyo. Es una PISTA para el vendedor, jamas una decision: en el paso de la multa el
/// sistema solo SUGIERE un camino ("probablemente no cobre" / "probablemente cobre"), pero el vendedor sigue
/// teniendo que confirmar la multa o cerrar sin multa a mano, con los datos reales de la cancelacion.
///
/// <para>NO reemplaza a <see cref="Supplier.PenaltyPolicyJson"/> (la tabla de penalidades por tramos de
/// antelacion, todavia sin construir): este campo es mucho mas simple, una sola pregunta de "que tan seguido
/// pasa" para dar una pista rapida mientras esa tabla no existe.</para>
/// </summary>
public enum SupplierPenaltyBehavior
{
    /// <summary>
    /// No se sabe / depende de la tarifa de cada reserva. Es el valor por defecto: mientras nadie configure
    /// nada para este operador, el paso de la multa NO sugiere ningun camino (el vendedor decide sin pistas,
    /// igual que hoy).
    /// </summary>
    Unknown = 0,

    /// <summary>Este operador casi nunca cobra multa al cancelar. Sugiere el camino "probablemente no haya multa".</summary>
    RarelyCharges = 1,

    /// <summary>Este operador casi siempre cobra multa al cancelar. Sugiere el camino "probablemente cobre multa".</summary>
    UsuallyCharges = 2,
}
