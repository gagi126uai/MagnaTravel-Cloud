using System.Text.Json.Serialization;

namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-017 F1.4: contrato de <c>GET /alerts</c> cuando al menos un bucket nuevo
/// (<c>ServiceDeadlines</c> / <c>CostsToConfirm</c>) esta activo.
///
/// <para><b>Por que un DTO TIPADO y NO un <c>Dictionary&lt;string,object&gt;</c></b>: System.Text.Json aplica el
/// <c>PropertyNamingPolicy</c> (camelCase, configurado por <c>JsonSerializerDefaults.Web</c> que usa la API) a las
/// PROPIEDADES de un tipo, pero NO a las CLAVES de un diccionario. Con el diccionario, prender un flag cambiaba en
/// SILENCIO el casing de las claves historicas (de <c>urgentTrips</c> a <c>UrgentTrips</c>) y rompia a cualquier
/// consumidor que sirviera para el path con flag OFF. Con este DTO el casing del contrato es el MISMO con flag OFF
/// y ON: siempre camelCase.</para>
///
/// <para><b>Presencia condicional</b>: los dos buckets nuevos son nullable + <see cref="JsonIgnoreAttribute"/> con
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/>. Cuando su gate esta apagado se pasan en null y se OMITEN del
/// JSON — exactamente la misma presencia condicional que tenia el path viejo con diccionario (lo unico que cambia
/// es que ahora el casing es consistente). Las listas viajan como <c>object</c>: sus elementos son objetos
/// anonimos que STJ serializa por su tipo runtime.</para>
/// </summary>
public sealed class AlertsResponse
{
    public AlertsResponse(
        object urgentTrips,
        object supplierDebts,
        object? serviceDeadlines,
        object? costsToConfirm,
        int totalCount)
    {
        UrgentTrips = urgentTrips;
        SupplierDebts = supplierDebts;
        ServiceDeadlines = serviceDeadlines;
        CostsToConfirm = costsToConfirm;
        TotalCount = totalCount;
    }

    public object UrgentTrips { get; }

    public object SupplierDebts { get; }

    /// <summary>Fechas limite de seña/pago/emision. Se OMITE del JSON cuando el bucket esta apagado (null).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ServiceDeadlines { get; }

    /// <summary>Servicios "costo a confirmar". Se OMITE del JSON cuando el bucket esta apagado (null).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? CostsToConfirm { get; }

    public int TotalCount { get; }
}
