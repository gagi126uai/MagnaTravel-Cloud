using System;

namespace TravelApi.Domain.Entities;

public static class WorkflowStatuses
{
    public const string Solicitado = "Solicitado";
    public const string Confirmado = "Confirmado";
    public const string Cancelado = "Cancelado";
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
            _ => WorkflowStatuses.Solicitado
        };
    }

    public static string MapGenericStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return WorkflowStatuses.Solicitado;
        var lower = status.Trim().ToLowerInvariant();

        if (lower.Contains("cancel")) return WorkflowStatuses.Cancelado;
        if (lower.Contains("confirm") || lower.Contains("emit")) return WorkflowStatuses.Confirmado;
        
        return WorkflowStatuses.Solicitado;
    }

    // Indicates if this status counts for the Reserva Balance (Cost/Sale)
    public static bool CountsForReservaBalance(string workflowStatus)
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
