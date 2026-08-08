using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// "Es otro": el par de productos que una persona ya miro y dijo que NO son el mismo
/// (spec firmada 2026-08-07, §6 / M-17).
///
/// <para><b>Por que existe</b>: sin esta memoria, la bandeja de repetidos volveria a proponer el mismo
/// par todas las semanas y el usuario aprenderia a ignorarla — que es la peor forma de matar una
/// bandeja de revision.</para>
///
/// <para><b>Truco del par ordenado</b>: se guarda siempre con el id mas chico primero
/// (<see cref="LowRateId"/>) y el mas grande despues. Asi el par (7, 3) y el par (3, 7) son LA MISMA
/// fila y el indice unico alcanza para que no se duplique, sin tener que consultar dos veces.</para>
/// </summary>
public class CatalogNotDuplicatePair
{
    public int Id { get; set; }

    /// <summary>Siempre el id MENOR de los dos productos.</summary>
    public int LowRateId { get; set; }
    public Rate? LowRate { get; set; }

    /// <summary>Siempre el id MAYOR de los dos productos.</summary>
    public int HighRateId { get; set; }
    public Rate? HighRate { get; set; }

    [MaxLength(450)]
    public string? MarkedByUserId { get; set; }

    public DateTime MarkedAt { get; set; } = DateTime.UtcNow;
}
