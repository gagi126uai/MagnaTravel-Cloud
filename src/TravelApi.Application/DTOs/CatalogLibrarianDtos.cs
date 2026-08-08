namespace TravelApi.Application.DTOs;

/// <summary>
/// Un grupo de la bandeja "Repetidos" (spec firmada 2026-08-07, §6 / V11=B): arriba el producto que se
/// queda, abajo todos los que se le parecen, cada uno con sus dos botones.
/// </summary>
public class DuplicateProductGroupDto
{
    /// <summary>El producto que el sistema propone dejar (el del nombre mas limpio y mas precios).</summary>
    public Guid SurvivorPublicId { get; set; }

    public string SurvivorName { get; set; } = string.Empty;

    /// <summary>Ciudad o ruta, lo que corresponda al tipo. Vacio si no hay.</summary>
    public string? SurvivorSubtitle { get; set; }

    /// <summary>Cuantos precios aprendidos tiene el que se queda ("3 precios").</summary>
    public int SurvivorPriceCount { get; set; }

    public IReadOnlyList<DuplicateProductCandidateDto> Candidates { get; set; }
        = Array.Empty<DuplicateProductCandidateDto>();
}

/// <summary>Uno de los productos que se le parecen al de arriba.</summary>
public class DuplicateProductCandidateDto
{
    public Guid RatePublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public int PriceCount { get; set; }

    /// <summary>
    /// Cuando el candidato viene del nombre viejo con la habitacion adentro
    /// ("Sheraton Iguazú - Doble Superior"), aca viaja la habitacion que se va a rescatar
    /// ("Doble Superior") para poder mostrar la unica aclaracion permitida en la bandeja (V14).
    /// Vacio en los demas casos.
    /// </summary>
    public string? VariantLabelToRescue { get; set; }
}

/// <summary>Respuesta de la bandeja: los grupos + el contador del pie ("ordenados por el sistema").</summary>
public class DuplicateProductsResponse
{
    public IReadOnlyList<DuplicateProductGroupDto> Groups { get; set; }
        = Array.Empty<DuplicateProductGroupDto>();

    /// <summary>Cuantos productos ordeno el sistema SOLO en los ultimos 7 dias (linea del pie).</summary>
    public int TidiedUpThisWeek { get; set; }
}

/// <summary>Pedido de unir dos productos ("Es el mismo").</summary>
public class MergeProductsRequest
{
    /// <summary>El que se queda.</summary>
    public Guid SurvivorPublicId { get; set; }

    /// <summary>El que va a ser absorbido (no se borra: se apaga y queda apuntando al que quedo).</summary>
    public Guid AbsorbedPublicId { get; set; }
}

/// <summary>Pedido de marcar dos productos como distintos ("Es otro"): no se vuelven a proponer.</summary>
public class NotDuplicatesRequest
{
    public Guid FirstPublicId { get; set; }
    public Guid SecondPublicId { get; set; }
}

/// <summary>Resultado de unir: que quedo y cuanta memoria de precios se movio.</summary>
public class MergeProductsResult
{
    public Guid SurvivorPublicId { get; set; }
    public string SurvivorName { get; set; } = string.Empty;

    /// <summary>Cuantas filas de precio se mudaron al que quedo.</summary>
    public int MovedPrices { get; set; }

    /// <summary>Habitacion rescatada del nombre viejo, si la hubo. Vacio si no aplica.</summary>
    public string? VariantLabelRescued { get; set; }

    /// <summary>Identificador de la union, para poder deshacerla.</summary>
    public Guid TidyUpActionPublicId { get; set; }
}

/// <summary>Una linea del rastro "Ver qué ordenó" (§6): que hizo el sistema y cuando, con Deshacer.</summary>
public class TidyUpActionDto
{
    public Guid PublicId { get; set; }

    /// <summary>Frase ya armada por el motor: "Sheraton Iguazú - Doble Superior → Sheraton Iguazú".</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Aclaracion cuando se rescato una habitacion: "la habitación quedó como 'Doble Superior'".</summary>
    public string? Detail { get; set; }

    public DateTime PerformedAt { get; set; }

    /// <summary>True si lo decidio el sistema; false si lo confirmo una persona en la bandeja.</summary>
    public bool DecidedByTheSystem { get; set; }

    /// <summary>False cuando ya fue deshecho (la linea queda, tachada, nunca se borra).</summary>
    public bool CanUndo { get; set; }

    /// <summary>
    /// Por que NO se puede deshacer, en criollo ("Después de esto hubo ventas nuevas; ya no se puede
    /// deshacer solo."). Null cuando si se puede, o cuando ya fue deshecho.
    ///
    /// <para>La pantalla lo muestra en gris al lado del boton apagado: un boton que no hace nada, sin
    /// explicacion, parece un error del sistema.</para>
    /// </summary>
    public string? UndoBlockedReason { get; set; }
}

/// <summary>Lo que ordeno el sistema, para la lista de "Ver qué ordenó".</summary>
public class TidyUpLogResponse
{
    public IReadOnlyList<TidyUpActionDto> Actions { get; set; } = Array.Empty<TidyUpActionDto>();
}

/// <summary>Resultado de una pasada del bibliotecario: cuanto ordeno solo.</summary>
public class TidyUpRunResult
{
    /// <summary>Cuantos productos absorbio (uniones automaticas de "casi seguros", Q3=B).</summary>
    public int MergedProducts { get; set; }

    /// <summary>De esos, cuantos ademas rescataron una habitacion que estaba metida en el nombre.</summary>
    public int VariantsRescued { get; set; }

    /// <summary>Cuantos grupos quedaron para que decida una persona en la bandeja.</summary>
    public int LeftForReview { get; set; }

    /// <summary>
    /// Cuantas uniones NO se pudieron hacer (quedan para revisar a mano). El resumen dice la verdad
    /// aunque algo falle: nunca se reporta como "ordenado" algo que no se ordeno.
    /// </summary>
    public int CouldNotMerge { get; set; }
}
