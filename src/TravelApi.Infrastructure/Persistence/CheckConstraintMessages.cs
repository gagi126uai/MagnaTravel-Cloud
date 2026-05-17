namespace TravelApi.Infrastructure.Persistence;

/// <summary>
/// FC1 (review BR3, 2026-05-14): tabla de mapeo de nombre de CHECK constraint
/// (Postgres) a mensaje user-friendly en espanol + codigo de invariante (INV-XXX).
///
/// Cuando Postgres rechaza una insercion/update por CHECK violation, devuelve
/// <c>SqlState='23514'</c> + <c>ConstraintName</c>. El <c>BusinessInvariantInterceptor</c>
/// llama a este helper para obtener un mensaje legible que el frontend pueda mostrar
/// directamente al usuario, sin filtrar nombres tecnicos de columnas internas.
///
/// IMPORTANTE: si una constraint no esta mapeada, se devuelve un mensaje generico
/// con el nombre del CHECK — es preferible a un 500 Internal Server Error opaco,
/// pero el equipo deberia agregar la entrada apenas se observe un fallback en logs.
/// </summary>
internal static class CheckConstraintMessages
{
    /// <summary>
    /// Devuelve <c>(Message, InvariantCode)</c> para un nombre de CHECK constraint.
    /// El nombre puede venir con o sin comillas, en mixed case o lowercase; Postgres
    /// preserva el case si el constraint fue creado entre comillas, por lo que
    /// usamos comparacion case-insensitive para tolerar ambos.
    /// </summary>
    public static (string Message, string InvariantCode) GetUserMessage(string constraintName)
    {
        // OrdinalIgnoreCase: Postgres puede normalizar a lowercase si el constraint
        // no se creo entre comillas dobles. Toleramos ambos casos.
        return constraintName.ToLowerInvariant() switch
        {
            "chk_operatorrefundsreceived_allocated_not_exceeds" =>
                ("El monto a asignar excede el disponible en el refund del operador.", "INV-084"),

            "chk_clientcreditentries_remaining_non_negative" =>
                ("El saldo del cliente no puede quedar negativo.", "INV-085"),

            "chk_operatorrefundallocations_net_positive" =>
                ("El monto neto de la deduccion no puede ser negativo, y el bruto debe ser mayor o igual al neto.", "INV-112"),

            "chk_deductionlines_amount_positive" =>
                ("El monto de la deduccion debe ser mayor a cero.", "INV-112"),

            "chk_travelfiles_status_valid" =>
                ("El estado de la reserva no es valido. Valores permitidos: Budget, Confirmed, Traveling, Closed, Cancelled, PendingOperatorRefund, Archived.", "INV-100"),

            "chk_bookingcancellations_fiscalsnapshot_consistent" =>
                ("La cancelacion requiere un snapshot fiscal completo (moneda, tipo de cambio y fuente) antes de confirmarla con el cliente.", "INV-118"),

            _ => ($"Operacion rechazada por restriccion de integridad: {constraintName}.", "INV-UNKNOWN"),
        };
    }
}
