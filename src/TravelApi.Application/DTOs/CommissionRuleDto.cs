namespace TravelApi.Application.DTOs;

/// <summary>
/// Regla de comision tal como la ve la pantalla de configuracion (Administracion → Comisiones).
///
/// <para><b>Por que existe</b> (deuda cerrada el 2026-07-31, gate de exposicion de datos): el alta y la
/// edicion de reglas devolvian la ENTIDAD de base tal cual, asi que la respuesta incluia campos internos
/// que la pantalla no usa (el numero interno del proveedor, el objeto proveedor vacio). Este DTO expone
/// exactamente los mismos campos que ya devuelve el listado de reglas — ni uno mas — y el proveedor se
/// identifica por su identificador publico, nunca por el numero interno de la tabla.</para>
/// </summary>
public class CommissionRuleDto
{
    /// <summary>Identificador de la regla, el mismo que usa el listado para editarla o borrarla.</summary>
    public int Id { get; set; }

    /// <summary>Identificador publico del proveedor al que aplica. Null = la regla aplica a todos.</summary>
    public Guid? SupplierPublicId { get; set; }

    /// <summary>Nombre del proveedor, para mostrarlo sin tener que resolverlo aparte. Null = todos.</summary>
    public string? SupplierName { get; set; }

    /// <summary>Tipo de servicio al que aplica (Aereo, Hotel, ...). Null = todos.</summary>
    public string? ServiceType { get; set; }

    /// <summary>Porcentaje de comision de la regla (0 a 100).</summary>
    public decimal CommissionPercent { get; set; }

    /// <summary>Prioridad: gana la regla mas especifica cuando varias aplican.</summary>
    public int Priority { get; set; }

    public bool IsActive { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
}
