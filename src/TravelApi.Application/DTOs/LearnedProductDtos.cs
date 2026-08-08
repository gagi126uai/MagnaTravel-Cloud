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

    /// <summary>
    /// Los precios AGRUPADOS POR VARIANTE (spec 2026-08-07, V5=A): primero la habitación (o la cabina, o
    /// el vehículo) y adentro los operadores. Un hotel con doble y triple trae dos variantes.
    ///
    /// <para><b>Cambio de contrato respecto del 2026-08-06</b>: antes esto era una lista plana
    /// <c>suppliers</c>. Cambió porque el precio sin la habitación no dice nada: US$ 48 de una doble y
    /// US$ 70 de una triple no son el mismo precio del mismo producto.</para>
    /// </summary>
    public IReadOnlyList<LearnedProductVariantDto> Variants { get; set; }
        = Array.Empty<LearnedProductVariantDto>();

    /// <summary>Cuántos renglones de precio tiene el producto en total (contando todas las variantes).</summary>
    public int TotalPriceRows { get; set; }

    /// <summary>Cuántos NO se mandaron por el tope de la lista. 0 cuando vienen todos.</summary>
    public int HiddenPriceRows { get; set; }

    /// <summary>
    /// Frase ya armada del renglón gris: "+ 3 precios más — tocá el hotel para verlos". Vacía cuando no
    /// hay escondidos. La escribe el motor (T-13): la pantalla no arma plurales ni cuenta nada.
    /// </summary>
    public string MorePricesText { get; set; } = string.Empty;
}

/// <summary>
/// Una VARIANTE del producto (la habitación del hotel, la cabina del aéreo, el vehículo del traslado) con
/// todos los operadores que la vendieron.
/// </summary>
public class LearnedProductVariantDto
{
    /// <summary>Clave interna para comparar. La pantalla la usa como identificador, nunca la muestra.</summary>
    public string VariantKey { get; set; } = string.Empty;

    /// <summary>
    /// La variante escrita para una persona: "Doble Superior con desayuno". <b>Vacía</b> cuando el dato no
    /// está cargado: en ese caso la celda va vacía, NO se escribe "Sin especificar" (V3=A).
    /// </summary>
    public string VariantLabel { get; set; } = string.Empty;

    /// <summary>Un renglón por operador, del precio más nuevo al más viejo.</summary>
    public IReadOnlyList<LearnedProductPriceDto> Suppliers { get; set; }
        = Array.Empty<LearnedProductPriceDto>();

    // ============================================================
    // Las PIEZAS de la variante, para que "Corregir" arranque con lo que hay cargado (§7 / M-18).
    //
    // Por qué viajan: sin ellas, el formulario de corrección arrancaba siempre en "Doble / Desayuno",
    // así que corregir el NOMBRE FINO de una triple la convertía en doble sin que nadie lo pidiera.
    // Son palabras del negocio ("Doble", "Media Pension", "Superior"), nunca la clave interna.
    // Null en la pieza que ese tipo de producto no tiene.
    // ============================================================

    /// <summary>Hotel: capacidad de la habitación ("Doble").</summary>
    public string? RoomType { get; set; }

    /// <summary>Hotel: régimen ("Desayuno").</summary>
    public string? MealPlan { get; set; }

    /// <summary>Hotel: nombre fino de la habitación ("Superior").</summary>
    public string? RoomCategory { get; set; }

    /// <summary>Aéreo: cabina ("Business").</summary>
    public string? CabinClass { get; set; }

    /// <summary>Traslado: vehículo ("Van").</summary>
    public string? VehicleType { get; set; }
}

/// <summary>Una solapa de la barra de arriba del Tarifario (V8=A), con su conteo de productos.</summary>
public class LearnedProductTypeTabDto
{
    /// <summary>Tipo tal como lo guarda el sistema ("Hotel"). Es lo que se manda de vuelta como filtro.</summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>Como se lee la solapa: "Hoteles", "Aéreos", "Asistencias".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Cuántos productos hay de ese tipo. 0 = la solapa se ve apagada.</summary>
    public int Count { get; set; }
}

/// <summary>
/// Respuesta del listado del Tarifario: la página de productos + las solapas por tipo. Tiene la misma
/// forma que el paginado de siempre, con las solapas al lado.
/// </summary>
public class LearnedProductsResponse
{
    public IReadOnlyList<LearnedProductDto> Items { get; set; } = Array.Empty<LearnedProductDto>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasPreviousPage { get; set; }
    public bool HasNextPage { get; set; }

    /// <summary>Las cinco solapas, SIEMPRE las cinco (la que está en cero se ve apagada).</summary>
    public IReadOnlyList<LearnedProductTypeTabDto> Tabs { get; set; }
        = Array.Empty<LearnedProductTypeTabDto>();

    public static LearnedProductsResponse Create(
        IReadOnlyList<LearnedProductDto> items, int page, int pageSize, int totalCount,
        IReadOnlyList<LearnedProductTypeTabDto> tabs)
    {
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        return new LearnedProductsResponse
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            HasPreviousPage = page > 1,
            HasNextPage = page < totalPages,
            Tabs = tabs
        };
    }

    public static LearnedProductsResponse Empty(
        int page, int pageSize, IReadOnlyList<LearnedProductTypeTabDto> tabs)
        => Create(Array.Empty<LearnedProductDto>(), page, pageSize, 0, tabs);
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

    /// <summary>Clave interna de la variante de este precio (para comparar; nunca se muestra).</summary>
    public string VariantKey { get; set; } = string.Empty;

    /// <summary>La variante escrita ("Doble Superior con desayuno"). Vacía si no hay dato (V3=A).</summary>
    public string VariantLabel { get; set; } = string.Empty;
}

/// <summary>
/// Lo que el motor sabe del precio de UNA combinación concreta (producto + operador + variante) al
/// momento de vender (spec 2026-08-07, M-15 / V9=A).
/// </summary>
public class VariantPriceSuggestionDto
{
    /// <summary>
    /// <b>true</b> = el precio es de la MISMA habitación que se está vendiendo: se puede precargar.
    /// <b>false</b> = es de otra habitación parecida: <b>NO se precarga</b>, solo se muestra abajo en gris
    /// diciendo de cuál es (V9=A: nunca se mete en el casillero un precio de otra habitación).
    /// </summary>
    public bool IsSameVariant { get; set; }

    public decimal Price { get; set; }

    /// <summary>"Costo" o "Venta": sin permiso de costos viaja la venta (F-14).</summary>
    public string PriceKind { get; set; } = string.Empty;

    public string? Currency { get; set; }
    public string? PriceUnit { get; set; }
    public string PriceUnitLabel { get; set; } = string.Empty;

    /// <summary>De qué habitación es este precio ("Triple con desayuno"). Vacío si no hay variante.</summary>
    public string VariantLabel { get; set; } = string.Empty;

    public Guid? SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    public DateTime? PriceDate { get; set; }

    /// <summary>"hace 5 meses", ya escrito por el motor.</summary>
    public string PriceAgeText { get; set; } = string.Empty;

    /// <summary>Más de 60 días: la pantalla pinta la fecha en ámbar (P10=A).</summary>
    public bool IsOldPrice { get; set; }

    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }

    /// <summary>
    /// El renglón gris completo, ya armado: "Último precio: Ola Mayorista · Doble con desayuno · US$ 48 ·
    /// 22/05/2026". Si el precio es de otra habitación, la frase lo aclara. El front lo muestra tal cual.
    /// </summary>
    public string SuggestionText { get; set; } = string.Empty;
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

    // ============================================================
    // La VARIANTE del alta a mano (spec 2026-08-07, §8 / V16=A). Todo opcional: si no viene nada, el
    // producto nace sin variante y se comporta igual que antes.
    //
    // Por que importa: sin esto, un precio cargado a mano no se podia comparar con los que el sistema
    // aprende vendiendo — quedaba en una bolsa aparte, sin habitacion, y al vender no se sugeria.
    // ============================================================

    /// <summary>Hotel: capacidad de la habitacion ("Doble"). La pantalla la manda con Doble ya puesto.</summary>
    [MaxLength(50)]
    public string? RoomType { get; set; }

    /// <summary>Hotel: regimen ("Desayuno"). La pantalla lo manda con Desayuno ya puesto.</summary>
    [MaxLength(50)]
    public string? MealPlan { get; set; }

    /// <summary>
    /// Hotel: nombre fino de la habitacion ("Superior", "Vista al mar"). Texto libre CON memoria: si ya
    /// se escribio antes de otra forma ("sup", "SUPERIOR"), el motor lo unifica con el que ya existe.
    /// </summary>
    [MaxLength(100)]
    public string? RoomCategory { get; set; }

    /// <summary>Aereo: cabina ("Economy" / "Ejecutiva").</summary>
    [MaxLength(50)]
    public string? CabinClass { get; set; }

    /// <summary>Traslado: vehiculo ("Van"). Texto libre con memoria, igual que el nombre fino del hotel.</summary>
    [MaxLength(50)]
    public string? VehicleType { get; set; }
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

/// <summary>
/// Pedido de sugerencia de precio al vender (M-15): "¿qué precio tengo de ESTA habitación de este
/// producto con este operador?".
/// </summary>
public class VariantPriceSuggestionQuery
{
    public Guid RatePublicId { get; set; }

    /// <summary>Operador elegido (PublicId o id legacy). Vacío = se mira todo el producto.</summary>
    public string? SupplierId { get; set; }

    // Hotel
    public string? RoomType { get; set; }
    public string? MealPlan { get; set; }

    /// <summary>Nombre fino de la habitación ("Superior"). Opcional.</summary>
    public string? RoomCategory { get; set; }

    // Aéreo / Traslado
    public string? CabinClass { get; set; }
    public string? VehicleType { get; set; }
}

/// <summary>Pedido de corregir cómo se llama una habitación de un producto (M-18). No toca importes.</summary>
public class RenameVariantRequest
{
    public Guid ProductPublicId { get; set; }

    /// <summary>Clave de la habitación que se está corrigiendo (la que devolvió el listado).</summary>
    [MaxLength(120)]
    public string CurrentVariantKey { get; set; } = string.Empty;

    // Cómo queda: hotel. Los topes son los mismos que aguanta la base, así un texto larguísimo se
    // rechaza en la puerta con un 400 claro en vez de reventar al guardar.
    [MaxLength(50)]
    public string? RoomType { get; set; }

    [MaxLength(50)]
    public string? MealPlan { get; set; }

    [MaxLength(100)]
    public string? RoomCategory { get; set; }

    // Cómo queda: aéreo / traslado
    [MaxLength(50)]
    public string? CabinClass { get; set; }

    [MaxLength(50)]
    public string? VehicleType { get; set; }
}

/// <summary>Resultado de corregir una habitación.</summary>
public class RenameVariantResult
{
    public Guid ProductPublicId { get; set; }
    public string VariantKey { get; set; } = string.Empty;
    public string VariantLabel { get; set; } = string.Empty;

    /// <summary>True si al corregirla quedó igual que otra que ya existía y las dos se juntaron.</summary>
    public bool MergedWithExisting { get; set; }
}
