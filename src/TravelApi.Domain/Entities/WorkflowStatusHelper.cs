using System;

namespace TravelApi.Domain.Entities;

public static class WorkflowStatuses
{
    public const string Solicitado = "Solicitado";
    public const string Confirmado = "Confirmado";
    public const string Cancelado = "Cancelado";

    // B2 (2026-06-24): un servicio PRESTADO/cumplido al cerrar la reserva (Finalizada). NO es "Cancelado":
    // sigue siendo parte de la venta, asi que NO sale del saldo ni de la deuda con el operador. Es un
    // sub-estado mas "fuerte" que Confirmado (ya se cumplio), pero a los efectos de plata cuenta IGUAL que
    // un servicio confirmado/activo. El mapeo (MapGenericStatus / MapFlightStatus) lo trata como Confirmado.
    public const string Finalizado = "Finalizado";
}

public static class WorkflowStatusHelper
{
    public static string MapFlightStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return WorkflowStatuses.Solicitado;
        var upper = status.Trim().ToUpperInvariant();
        return upper switch
        {
            "HK" or "TK" or "KK" or "KL" => WorkflowStatuses.Confirmado,
            "UN" or "UC" or "HX" or "NO" => WorkflowStatuses.Cancelado,
            // B2 (2026-06-24): un vuelo Finalizado (viaje cumplido al cerrar la reserva) cuenta como
            // Confirmado para la plata. No es un codigo IATA; lo seteamos nosotros al cerrar la reserva.
            // Se compara en mayusculas ("FINALIZADO") porque MapFlightStatus normaliza a upper.
            _ when upper == WorkflowStatuses.Finalizado.ToUpperInvariant() => WorkflowStatuses.Confirmado,
            _ => WorkflowStatuses.Solicitado
        };
    }

    public static string MapGenericStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return WorkflowStatuses.Solicitado;
        var lower = status.Trim().ToLowerInvariant();

        // Anclado al INICIO (StartsWith), NO Contains: textos como "A confirmar", "sin emitir",
        // "desconfirmado" o "no confirmado" CONTIENEN la palabra pero significan lo CONTRARIO. Con
        // Contains se leian como Confirmado y podian auto-confirmar (o no cancelar) un servicio que en
        // realidad no lo estaba. StartsWith sigue matcheando las familias reales (Cancelado/Cancelada,
        // Confirmado/Confirmada, Emitido/Emitida) sin esos falsos positivos. Cancel primero (gana sobre confirm).
        if (lower.StartsWith("cancel")) return WorkflowStatuses.Cancelado;
        if (lower.StartsWith("confirm") || lower.StartsWith("emit")) return WorkflowStatuses.Confirmado;

        // B2 (2026-06-24): "Finalizado" (servicio prestado al cerrar la reserva) cuenta como Confirmado para
        // la plata — un servicio cumplido NO sale del saldo ni de la deuda. Se ancla con StartsWith("finaliz")
        // para matchear "Finalizado"/"Finalizada" sin falsos positivos (igual criterio que cancel/confirm).
        if (lower.StartsWith("finaliz")) return WorkflowStatuses.Confirmado;

        return WorkflowStatuses.Solicitado;
    }

    // ADR-020 (2026-06-07): indica si un servicio cuenta para el TOTAL COTIZADO de la reserva
    // (TotalSale = valor comercial del presupuesto). Cuentan los NO cancelados (Solicitado +
    // Confirmado); los cancelados quedan afuera. OJO: esto NO es el saldo del cliente — el saldo
    // (Balance) se calcula sobre la VENTA CONFIRMADA (ConfirmedSale), que filtra por resolucion
    // del servicio (ver ServiceResolutionRules), no por "no cancelado".
    //
    // Antes se llamaba CountsForReservaBalance (nombre heredado de cuando Balance = TotalSale -
    // TotalPaid). Se renombro al separar TotalSale de ConfirmedSale.
    public static bool CountsForQuotedTotal(string workflowStatus)
    {
        return workflowStatus == WorkflowStatuses.Confirmado || workflowStatus == WorkflowStatuses.Solicitado;
    }

    // Indicates if this status counts for Supplier Debt
    public static bool CountsForSupplierDebt(string workflowStatus)
    {
        return workflowStatus == WorkflowStatuses.Confirmado;
    }

    // Indica si un servicio genera DEUDA con el proveedor segun su TIPO (label de la lista
    // de servicios del proveedor) y su estado crudo. Regla de negocio (confirmada por el
    // dueño): SOLO genera deuda cuando el operador CONFIRMO el servicio; "Solicitado"
    // todavia NO es deuda. Los vuelos mapean por codigo IATA (HK/TK/KK/KL = confirmado);
    // el resto de los servicios, por el mapeo generico (texto que contiene "confirm"/"emit").
    // Esta es la UNICA definicion de la regla: SupplierService la reusa en vez de una lista
    // de estados escrita a mano (antes: "Confirmado"/"Emitido"/"HK"/"TK"/"KK"/"KL" plano,
    // que no distinguia tipo y se le escapaban variantes de texto como "confirmado" en
    // minusculas).
    public static bool CountsForSupplierDebtByType(string? serviceType, string? status)
    {
        string mapped = string.Equals(serviceType, "Vuelo", StringComparison.OrdinalIgnoreCase)
            ? MapFlightStatus(status ?? string.Empty)
            : MapGenericStatus(status ?? string.Empty);
        return CountsForSupplierDebt(mapped);
    }
}
