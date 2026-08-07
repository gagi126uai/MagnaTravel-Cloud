using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs;

/// <summary>
/// Filtros de la pantalla "Tarifario" nueva (spec firmada 2026-08-06, M-1): buscador por texto,
/// filtro por tipo de servicio y por operador, mas el paginado de siempre.
/// </summary>
public class LearnedProductsQuery : PagedQuery
{
    /// <summary>Tipo de servicio ("Hotel", "Aereo", "Paquete"...). Vacio = todos.</summary>
    public string? ServiceType { get; set; }

    /// <summary>Operador por el que filtrar (PublicId o id legacy). Vacio = todos.</summary>
    public string? SupplierId { get; set; }
}

/// <summary>
/// Un PRODUCTO del tarifario (un hotel, un tramo aereo, un paquete...) con todos los precios que el
/// sistema le aprendio, un renglon por operador.
///
/// <para>Un mismo producto puede estar cargado varias veces en el tarifario viejo (una tarifa por
/// habitacion, por operador, etc.); esas filas se colapsan en UN solo producto (P2=A: una sola lista,
/// sin decir de donde salio cada cosa).</para>
/// </summary>
public class LearnedProductDto
{
    /// <summary>
    /// Identificador del producto de cara al front: el PublicId de la tarifa REPRESENTANTE del grupo
    /// (la del precio mas nuevo). Es con el que se abre la ficha y la "Carga completa".
    /// </summary>
    public Guid ProductPublicId { get; set; }

    /// <summary>Nombre lindo: el del hotel si es hotel, el del producto en el resto de los tipos.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Renglon gris debajo del nombre: ciudad (hotel), ruta (aereo/traslado) o destino.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Tipo de servicio tal como lo guarda el sistema ("Hotel", "Aereo"...).</summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>El mismo tipo, ya escrito como lo lee una persona ("Hotel", "Aéreo", "Asistencia").</summary>
    public string ServiceTypeLabel { get; set; } = string.Empty;

    /// <summary>Un renglon por operador, del precio mas nuevo al mas viejo.</summary>
    public IReadOnlyList<LearnedProductPriceDto> Suppliers { get; set; } = Array.Empty<LearnedProductPriceDto>();
}

/// <summary>
/// El ultimo precio conocido de un producto CON UN OPERADOR. Todo lo derivado (si el precio esta
/// viejo, hace cuanto fue) lo calcula el motor: la pantalla no resta fechas (T-13).
/// </summary>
public class LearnedProductPriceDto
{
    public Guid? SupplierPublicId { get; set; }

    /// <summary>Nombre del operador. "Sin operador" cuando la tarifa vieja no tenia ninguno cargado.</summary>
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>
    /// El precio que se muestra. Es el COSTO (lo que cobra el operador) para quien tiene permiso de ver
    /// costos; para el resto es el precio de VENTA (F-14: sin permiso no viajan costos, pero tampoco se
    /// muestra un guion: se muestra la venta).
    /// </summary>
    public decimal Price { get; set; }

    /// <summary>Que es ese numero, en criollo: "Costo" o "Venta". Lo decide el motor, no la pantalla.</summary>
    public string PriceKind { get; set; } = string.Empty;

    public string? Currency { get; set; }

    /// <summary>Unidad tal como la guarda el sistema ("noche_habitacion", "pasajero"...).</summary>
    public string? PriceUnit { get; set; }

    /// <summary>La misma unidad escrita para una persona: "por noche", "por pasajero", "" si no aplica.</summary>
    public string PriceUnitLabel { get; set; } = string.Empty;

    /// <summary>Cuando quedo ese precio (fecha de la venta, o de la ultima edicion si se cargo a mano).</summary>
    public DateTime? PriceDate { get; set; }

    /// <summary>Antigüedad ya escrita por el motor: "hace 5 meses", "hoy", "" si no hay fecha.</summary>
    public string PriceAgeText { get; set; } = string.Empty;

    /// <summary>
    /// True cuando el precio quedo viejo (mas dias que el umbral de "costo a confirmar", 60 por default).
    /// La pantalla lo usa para pintar la fecha en ámbar (P10=A).
    /// </summary>
    public bool IsOldPrice { get; set; }

    /// <summary>Reserva que dejo ese precio (link a la ficha). Null si el precio no vino de una venta.</summary>
    public Guid? ReservaPublicId { get; set; }

    /// <summary>Numero de esa reserva ("F-2026-1042"). Null si el precio no vino de una venta.</summary>
    public string? NumeroReserva { get; set; }
}

/// <summary>
/// Alta simple de producto desde el Tarifario (spec firmada 2026-08-06, §2.3 / M-3): lo minimo
/// indispensable. El formulario largo de siempre sigue vivo detras de "Carga completa".
/// </summary>
public class CreateSimpleProductRequest
{
    /// <summary>"Hotel", "Aereo", "Paquete", "Traslado", "Asistencia"...</summary>
    [Required]
    public string ServiceType { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Obligatoria SOLO para hotel: es el arma principal contra los productos repetidos.</summary>
    [MaxLength(100)]
    public string? City { get; set; }

    /// <summary>Operador (PublicId o id legacy). Opcional: se puede cargar el producto sin operador.</summary>
    public string? SupplierId { get; set; }

    /// <summary>Precio de referencia. 0 vale (todavia no lo se).</summary>
    public decimal Price { get; set; }

    /// <summary>"ARS" o "USD". Si no viene, se asume la moneda por defecto del tarifario.</summary>
    public string? Currency { get; set; }

    /// <summary>Unidad del precio ("noche", "pasajero", "servicio"). Si no viene la resuelve el motor por tipo.</summary>
    public string? PriceUnit { get; set; }

    /// <summary>
    /// Confirmacion explicita del usuario: "ya vi los parecidos, igual quiero crearlo". Sin esto, si el
    /// sistema encuentra un parecido fuerte NO crea nada y devuelve los candidatos (P7).
    /// </summary>
    public bool CreateAnyway { get; set; }
}

/// <summary>
/// Renombrar un PRODUCTO del tarifario (spec firmada 2026-08-06, §2.2).
///
/// <para><b>Por que no alcanza con editar una tarifa</b>: un producto de la lista puede estar formado por
/// VARIAS tarifas (el mismo hotel cargado por habitacion, por operador o por vigencia). Si se renombrara
/// una sola, el grupo se partiria en dos productos con nombres distintos — exactamente el repetido que el
/// dueño quiere evitar. Por eso el renombre viaja a nivel producto y el motor toca todas sus tarifas.</para>
/// </summary>
public class RenameLearnedProductRequest
{
    /// <summary>Tipo del producto que se esta renombrando ("Hotel", "Aereo"...).</summary>
    [Required]
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>Nombre ACTUAL del producto (el que se ve en la lista).</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Ciudad actual. Obligatoria en hotel: es parte de la identidad del producto.</summary>
    public string? City { get; set; }

    [Required]
    [MaxLength(200)]
    public string NewName { get; set; } = string.Empty;

    /// <summary>Ciudad nueva. Obligatoria en hotel.</summary>
    [MaxLength(100)]
    public string? NewCity { get; set; }
}

/// <summary>Resultado del renombre: la identidad nueva del producto y cuantas tarifas se corrigieron.</summary>
public class RenameLearnedProductResult
{
    /// <summary>PublicId de la tarifa representante (con el que la pantalla vuelve a abrir la ficha).</summary>
    public Guid ProductPublicId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Subtitle { get; set; }

    /// <summary>Cuantas tarifas del tarifario quedaron con el nombre nuevo (puede ser mas de una).</summary>
    public int RenamedRates { get; set; }
}

/// <summary>Un producto ya existente que se parece al que se esta por crear (freno de repetidos).</summary>
public class SimilarProductDto
{
    public Guid RatePublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Subtitle { get; set; }

    /// <summary>
    /// True cuando el nombre coincide EXACTO (ya normalizado) y, en hotel, tambien la ciudad. Es el caso
    /// en el que el sistema esta casi seguro de que es el mismo producto.
    /// </summary>
    public bool IsSameName { get; set; }
}

/// <summary>
/// Resultado del alta simple. O trae el producto creado, o trae el freno con los parecidos.
/// Se modela asi (y no con excepciones) porque "hay parecidos" NO es un error: es una pregunta.
/// </summary>
public class SimpleProductCreationResult
{
    /// <summary>El producto creado. Null cuando el sistema freno para preguntar.</summary>
    public RateListItemDto? Created { get; set; }

    /// <summary>
    /// Motivo del freno POR CODIGO (patron 2026-07-22: el motivo viaja por codigo, no por texto libre).
    /// Hoy el unico valor es <c>ProductoParecido</c>.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Texto ya armado por el motor para el cartel de confirmacion.</summary>
    public string? Message { get; set; }

    public IReadOnlyList<SimilarProductDto> SimilarProducts { get; set; } = Array.Empty<SimilarProductDto>();
}

/// <summary>Codigos de motivo del freno de repetidos. Van por codigo para que el front no parsee textos.</summary>
public static class SimpleProductCreationReasons
{
    public const string SimilarProductFound = "ProductoParecido";
}

/// <summary>Codigos de motivo del rechazo al renombrar. Mismo criterio: el motivo viaja por codigo.</summary>
public static class LearnedProductRenameReasons
{
    public const string NameAlreadyTaken = "NombreYaUsado";
}
