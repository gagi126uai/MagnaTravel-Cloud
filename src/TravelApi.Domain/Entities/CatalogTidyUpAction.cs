using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>Que tipo de orden hizo el sistema (o una persona) sobre el tarifario. Van por codigo, no por texto.</summary>
public static class CatalogTidyUpKinds
{
    /// <summary>Dos productos que eran el mismo quedaron en uno solo.</summary>
    public const string ProductsMerged = "ProductosUnidos";

    /// <summary>
    /// Un producto que tenia la habitacion metida DENTRO del nombre ("Sheraton - Doble Superior") se unio
    /// al producto limpio y ese pedazo del nombre paso a ser la habitacion.
    /// </summary>
    public const string SuffixConvertedToVariant = "HabitacionRescatadaDelNombre";

    /// <summary>
    /// Alguien CORRIGIO como se llama una habitacion de un producto (M-18). Deja rastro y Deshacer igual
    /// que una union, porque si la correccion deja dos habitaciones iguales una queda escondida.
    ///
    /// <para><b>Ojo al deshacer</b>: en este tipo de accion NO hay producto absorbido — el producto nunca
    /// se apago. Deshacer solo devuelve las filas de precio, no toca el nombre ni el estado del producto.
    /// Por eso <c>SurvivingRateId</c> y <c>AbsorbedRateId</c> apuntan al MISMO producto.</para>
    /// </summary>
    public const string VariantRenamed = "HabitacionCorregida";
}

/// <summary>
/// RASTRO de todo lo que el sistema ordeno solo en el tarifario, con lo necesario para DESHACERLO
/// (spec firmada 2026-08-07, §6 + Q3=B).
///
/// <para><b>Por que existe</b>: el dueño firmo que el sistema puede unir por su cuenta los "casi
/// seguros" — pero solo se puede permitir que decida solo si TODO lo que hizo se puede ver y volver
/// atras. Sin esta tabla, "que decida solo" seria "que rompa solo y no te enteres".</para>
///
/// <para><b>Nada se borra</b> (regla del 2026-08-03): unir NO borra el producto absorbido. Lo desactiva
/// (<c>IsActive=false</c>) y le deja escrito en quien quedo (<c>Rate.MergedIntoRateId</c>). Deshacer es
/// volver a prenderlo y devolverle sus precios; por eso esta fila guarda EXACTAMENTE que filas de
/// precio se movieron.</para>
/// </summary>
public class CatalogTidyUpAction
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Que se hizo. Valores de <see cref="CatalogTidyUpKinds"/>.</summary>
    [Required]
    [MaxLength(60)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Producto que QUEDO (el sobreviviente).</summary>
    public int SurvivingRateId { get; set; }
    public Rate? SurvivingRate { get; set; }

    /// <summary>Producto que fue ABSORBIDO (sigue existiendo, apagado).</summary>
    public int AbsorbedRateId { get; set; }
    public Rate? AbsorbedRate { get; set; }

    /// <summary>Nombre que tenia el absorbido antes de la union (para poder contarlo y para revertir).</summary>
    [MaxLength(200)]
    public string AbsorbedName { get; set; } = string.Empty;

    /// <summary>Nombre del sobreviviente al momento de unir (para la linea del rastro).</summary>
    [MaxLength(200)]
    public string SurvivingName { get; set; } = string.Empty;

    /// <summary>
    /// Si la union rescato una habitacion que estaba escondida en el nombre, aca queda cual
    /// ("Doble Superior"). Vacio cuando no aplica.
    /// </summary>
    [MaxLength(200)]
    public string VariantLabelRescued { get; set; } = string.Empty;

    /// <summary>Clave normalizada de esa habitacion rescatada (para poder deshacer con exactitud).</summary>
    [MaxLength(120)]
    public string VariantKeyRescued { get; set; } = string.Empty;

    /// <summary>Nombre del PRODUCTO del absorbido antes de la union (para devolverselo tal cual al deshacer).</summary>
    [MaxLength(200)]
    public string AbsorbedProductName { get; set; } = string.Empty;

    /// <summary>
    /// La FOTO de cada fila de precio que esta union toco. Sin esto el Deshacer seria una promesa vacia:
    /// habria que adivinar que valores tenia antes cada fila. Ver <see cref="CatalogTidyUpSaleChange"/>.
    /// </summary>
    public ICollection<CatalogTidyUpSaleChange> SaleChanges { get; set; } = new List<CatalogTidyUpSaleChange>();

    /// <summary>
    /// True cuando la union la decidio el sistema por su cuenta (criterio automatico); false cuando la
    /// confirmo una persona en la bandeja. <b>No dice quien la ejecuto</b>: eso siempre queda en
    /// <see cref="PerformedByUserId"/>, tambien en las automaticas (alguien apreto el boton que las dispara).
    /// </summary>
    public bool DecidedByTheSystem { get; set; }

    /// <summary>Quien la ejecuto. SIEMPRE se guarda si hay usuario resoluble, aunque el criterio fuera automatico.</summary>
    [MaxLength(450)]
    public string? PerformedByUserId { get; set; }

    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cuando alguien lo deshizo. Null = sigue vigente. La fila NO se borra al deshacer.</summary>
    public DateTime? UndoneAt { get; set; }

    [MaxLength(450)]
    public string? UndoneByUserId { get; set; }

    /// <summary>
    /// Por que esta union YA NO se puede deshacer sola, escrito para una persona ("Después de esto hubo
    /// ventas nuevas..."). Null = se puede deshacer.
    ///
    /// <para>Lo escribe el motor cuando pasa algo que hace imposible una vuelta atras fiel: una venta
    /// nueva piso una de las filas movidas, o un "Empezar de cero" se llevo la memoria de precios. Es
    /// preferible decir "esto ya no se puede deshacer" que deshacer mal y romper la plata de otro.</para>
    /// </summary>
    [MaxLength(300)]
    public string? UndoBlockedReason { get; set; }
}
