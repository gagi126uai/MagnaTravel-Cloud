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
}
