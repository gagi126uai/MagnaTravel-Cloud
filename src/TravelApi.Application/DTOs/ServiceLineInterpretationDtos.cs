using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs;

/// <summary>
/// Lo que manda la ficha de carga de servicio cuando el vendedor escribe la frase entera
/// ("sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9") y aprieta buscar.
/// La reserva NO viaja aca: va en la direccion del pedido, porque ademas define quien puede llamarlo.
/// </summary>
public class InterpretServiceLineRequest
{
    /// <summary>
    /// La frase tal cual la escribio el vendedor. El tope es generoso pero existe: es texto que se le
    /// manda a un modelo por internet, y sin tope alguien podria pegar un libro entero.
    ///
    /// <para><b>No lleva "obligatorio" a proposito</b>: si llega vacio, la respuesta es "no entendi"
    /// (200, todo vacio) y no un rechazo. Esta caja no puede tirarle un error al vendedor por escribir
    /// poco: es un buscador, no un formulario.</para>
    /// </summary>
    [MaxLength(500)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// El tipo de servicio que el vendedor ya eligio en la solapa ("Hotel", "Aereo", "Traslado",
    /// "Paquete", "Asistencia"). No se adivina: la solapa siempre esta elegida antes de escribir.
    /// Vacio se trata igual que la frase vacia: "no entendi", sin error.
    /// </summary>
    [MaxLength(50)]
    public string ServiceType { get; set; } = string.Empty;
}

/// <summary>
/// Que tan seguro esta el sistema de cada dato que precargo. Viaja por CODIGO (T-13): la pantalla
/// decide con esto, no interpretando textos.
///
/// <para><b>Ojo</b>: un dato del que el sistema no esta razonablemente seguro NO se manda con
/// confianza baja — directamente no se manda (queda vacio en la ficha, §3.3/§3.5). Por eso en la
/// practica solo se ven <see cref="High"/> y <see cref="Medium"/>.</para>
/// </summary>
public static class InterpretationConfidence
{
    public const string High = "alta";
    public const string Medium = "media";
}

/// <summary>
/// Los codigos de DUDA GRANDE (§4 / M-22, + duda de producto 2026-08-10). Los decide el MOTOR, nunca
/// el modelo: son las situaciones donde el sistema no puede resolver solo algo que cambia la plata o
/// la identidad del producto.
/// </summary>
public static class ServiceLineDoubtCodes
{
    /// <summary>
    /// El buscador encontro DOS productos con el mismo nombre (o casi) pero en lugares distintos
    /// ("Panamericano" en Buenos Aires Y en Bariloche). Es la duda ESTRELLA (aprobada por el dueño
    /// 2026-08-10): gana sobre las otras tres porque, sin saber cual de los dos es, cualquier otro dato
    /// (precio, operador, fechas) podria estar respondiendo sobre el producto equivocado.
    /// </summary>
    public const string AmbiguousProduct = "productoAmbiguo";

    /// <summary>El texto trae un numero pero no dice si es por noche o por toda la estadia.</summary>
    public const string PricePerNight = "precioPorNoche";

    /// <summary>El operador escrito se parece a uno de la agencia, pero no es igual.</summary>
    public const string AmbiguousSupplier = "operadorAmbiguo";

    /// <summary>Las fechas no traian el año escrito: el sistema eligio uno.</summary>
    public const string DatesYear = "anioDeFechas";
}

/// <summary>Que campo de la ficha toca la duda. La pantalla lo usa para saber cual vaciar si el vendedor dice "No".</summary>
public static class ServiceLineDoubtFields
{
    public const string Product = "producto";
    public const string Price = "precio";
    public const string Supplier = "operador";
    public const string Dates = "fechas";
}

/// <summary>
/// La UNICA pregunta de la respuesta (§4): una linea, si o no. El texto ya viene armado en criollo
/// por el motor; la pantalla lo muestra tal cual, no lo compone.
/// </summary>
public class ServiceLineDoubtDto
{
    /// <summary>Codigo de la duda (<see cref="ServiceLineDoubtCodes"/>). Interno: NO se muestra.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>La pregunta lista para mostrar: <c>¿US$ 48 es el precio por noche?</c></summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Campo que se vacia si el vendedor contesta "No" (<see cref="ServiceLineDoubtFields"/>).</summary>
    public string Field { get; set; } = string.Empty;
}

/// <summary>El producto del tarifario que el sistema eligio precargar.</summary>
public class InterpretedProductDto
{
    public Guid RatePublicId { get; set; }

    /// <summary>Nombre lindo para mostrar ("Sheraton Iguazú").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Subtitulo segun el tipo: la ciudad en hotel, la ruta en aereo. Vacio si no aplica.</summary>
    public string? Subtitle { get; set; }

    public string Confidence { get; set; } = InterpretationConfidence.Medium;
}

/// <summary>El operador (mayorista) que el sistema reconocio entre los de la agencia.</summary>
public class InterpretedSupplierDto
{
    public Guid SupplierPublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Confidence { get; set; } = InterpretationConfidence.Medium;
}

/// <summary>
/// La habitacion (hotel), la cabina (aereo) o el vehiculo (traslado) que salieron de la frase.
/// Los valores vienen escritos como los espera cada desplegable de la ficha, no como los escribio
/// el vendedor: eso lo normaliza el motor (T-13).
/// </summary>
public class InterpretedVariantDto
{
    // Hotel
    public string? RoomType { get; set; }
    public string? MealPlan { get; set; }

    /// <summary>Nombre fino de la habitacion ("Superior"), ya unificado con el que la agencia usa (M-19).</summary>
    public string? RoomCategory { get; set; }

    // Aereo / Traslado
    public string? CabinClass { get; set; }
    public string? VehicleType { get; set; }

    /// <summary>La variante en una frase, armada por el motor: "Doble con desayuno".</summary>
    public string Label { get; set; } = string.Empty;

    public string Confidence { get; set; } = InterpretationConfidence.Medium;
}

/// <summary>
/// El precio que salio de la frase. Es COSTO, asi que no viaja para quien no puede ver costos
/// (F-14 / M-27): en ese caso este bloque es <c>null</c> y la ficha queda con el casillero vacio.
/// </summary>
public class InterpretedPriceDto
{
    public decimal Amount { get; set; }

    /// <summary>Moneda ISO ("ARS"/"USD"). Si de la frase no sale una moneda conocida, no hay precio.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Unidad del precio en codigo ("noche_habitacion", "pasajero", ...).</summary>
    public string PriceUnit { get; set; } = string.Empty;

    /// <summary>La unidad en criollo, ya escrita por el motor: "por noche".</summary>
    public string PriceUnitLabel { get; set; } = string.Empty;

    public string Confidence { get; set; } = InterpretationConfidence.Medium;
}

/// <summary>
/// Las fechas del servicio (entrada/salida en hotel, ida/vuelta en el resto). Son fechas "de pared"
/// (medianoche Kind=Utc), el mismo formato con el que se guardan los servicios.
/// </summary>
public class InterpretedDatesDto
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string Confidence { get; set; } = InterpretationConfidence.Medium;
}

/// <summary>
/// Lo que el sistema entendio de la frase (M-20). Todo lo que no entendio viaja en <c>null</c>: la
/// ficha deja ese casillero vacio, sin explicaciones (§3.3).
///
/// <para><b>Nunca es un error</b>: si no hay inteligencia artificial configurada, si el proveedor no
/// contesta, si contesta cualquier cosa o si tarda demasiado, la respuesta es igual de exitosa con
/// <see cref="Interpreted"/> en false y todo vacio (M-23). La pantalla se comporta como el buscador
/// de siempre y no muestra ni un cartel.</para>
/// </summary>
public class ServiceLineInterpretationDto
{
    /// <summary>false = el sistema no pudo entender nada. La pantalla sigue con el buscador de siempre.</summary>
    public bool Interpreted { get; set; }

    /// <summary>
    /// Los productos parecidos del tarifario, con EL MISMO orden y la misma forma que devuelve el
    /// buscador de la ficha. Asi la pantalla los pinta con el componente que ya tiene.
    /// </summary>
    public IReadOnlyList<CatalogSearchItemDto> ProductCandidates { get; set; } = Array.Empty<CatalogSearchItemDto>();

    /// <summary>
    /// El nombre de producto que se leyo en la frase ("Amerian Posadas"), para la ultima opcion de la
    /// lista: "crear ...". Vacio si de la frase no salio ningun producto.
    /// </summary>
    public string? ProductSearchText { get; set; }

    public InterpretedProductDto? Product { get; set; }
    public InterpretedSupplierDto? Supplier { get; set; }
    public InterpretedVariantDto? Variant { get; set; }
    public InterpretedPriceDto? Price { get; set; }
    public InterpretedDatesDto? Dates { get; set; }

    /// <summary>La unica duda grande de esta respuesta, o null si no hay ninguna (§4).</summary>
    public ServiceLineDoubtDto? Doubt { get; set; }

    /// <summary>La respuesta de "no entendi nada", que es una respuesta valida y frecuente.</summary>
    public static ServiceLineInterpretationDto NotInterpreted() => new();
}
