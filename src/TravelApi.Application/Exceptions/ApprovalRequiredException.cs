using TravelApi.Domain.Entities;

namespace TravelApi.Application.Exceptions;

/// <summary>
/// B1.15 Fase D (2026-05-11): el caller intentó una acción que requiere un
/// <see cref="ApprovalRequest"/> aprobado previamente y no existe ninguno
/// válido. El controller traduce esta excepción a HTTP 409 con un body que
/// le indica al frontend qué tipo de solicitud crear y para qué entidad.
/// </summary>
public class ApprovalRequiredException : Exception
{
    public ApprovalRequestType RequestType { get; }
    public string EntityType { get; }
    public int EntityId { get; }

    public ApprovalRequiredException(
        ApprovalRequestType requestType,
        string entityType,
        int entityId)
        : base($"Requiere aprobacion: {requestType} sobre {entityType}#{entityId}")
    {
        RequestType = requestType;
        EntityType = entityType;
        EntityId = entityId;
    }
}
