using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>Que le paso a una fila de memoria de precio durante una union. Va por codigo, no por texto.</summary>
public static class CatalogTidyUpSaleChangeKinds
{
    /// <summary>La fila cambio de producto (del absorbido al que quedo). Deshacer la devuelve.</summary>
    public const string Moved = "Movida";

    /// <summary>
    /// La fila del que quedo fue PISADA por una mas nueva del absorbido (mismo operador y misma
    /// habitacion). Deshacer le devuelve sus valores viejos, guardados aca.
    /// </summary>
    public const string Overwritten = "Pisada";

    /// <summary>
    /// La fila perdio contra otra igual y quedo ESCONDIDA (no se borro: sigue en la base, apagada).
    /// Deshacer la vuelve a mostrar.
    /// </summary>
    public const string Hidden = "Escondida";

    /// <summary>
    /// Fila que el sistema CREO durante la union para mudar el precio que estaba cargado a mano en el
    /// producto absorbido. Deshacer la esconde (el precio original nunca se toco: sigue en su producto).
    /// </summary>
    public const string CreatedFromManualPrice = "CreadaDelPrecioAMano";
}

/// <summary>
/// La FOTO de una fila de memoria de precio ANTES de que una union la tocara (spec 2026-08-07, §6).
///
/// <para><b>Por que existe</b>: el dueño firmo que el sistema puede unir productos por su cuenta, y eso
/// solo es aceptable si el Deshacer devuelve las cosas EXACTAMENTE como estaban. Guardar "que filas se
/// movieron" no alcanza: cuando dos filas chocan (mismo operador, misma habitacion) una le pisa los
/// importes a la otra, y sin esta foto esos importes viejos no vuelven nunca mas.</para>
///
/// <para><b>Nada se borra</b> (orden del dueño, 2026-08-03): ninguna union elimina filas de precio. La
/// que pierde queda ESCONDIDA (<c>RateSupplierSale.AbsorbedByTidyUpActionId</c>) y el Deshacer la vuelve
/// a mostrar.</para>
/// </summary>
public class CatalogTidyUpSaleChange
{
    public int Id { get; set; }

    public int TidyUpActionId { get; set; }
    public CatalogTidyUpAction? TidyUpAction { get; set; }

    /// <summary>La fila de precio afectada.</summary>
    public int RateSupplierSaleId { get; set; }
    public RateSupplierSale? RateSupplierSale { get; set; }

    /// <summary>Que le paso. Valores de <see cref="CatalogTidyUpSaleChangeKinds"/>.</summary>
    [Required]
    [MaxLength(40)]
    public string Kind { get; set; } = string.Empty;

    // ===== La foto de ANTES. Con esto se reconstruye la fila tal cual estaba. =====

    public int PreviousRateId { get; set; }

    /// <summary>Operador de la fila. Sirve ademas para verificar, al deshacer, que la fila sigue siendo LA MISMA.</summary>
    public int PreviousSupplierId { get; set; }

    [MaxLength(120)]
    public string PreviousVariantKey { get; set; } = string.Empty;

    [MaxLength(200)]
    public string PreviousVariantLabel { get; set; } = string.Empty;

    public DateTime PreviousSoldAt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PreviousNetCost { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PreviousTax { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PreviousSalePrice { get; set; }

    [MaxLength(3)]
    public string? PreviousCurrency { get; set; }

    [MaxLength(30)]
    public string PreviousPriceUnit { get; set; } = string.Empty;

    public int? PreviousReservaId { get; set; }

    public int PreviousSalesCount { get; set; }
}
