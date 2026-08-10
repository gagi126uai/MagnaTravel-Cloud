namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// EXACTAMENTE lo que se le pide al modelo que devuelva. Ni un campo mas: la deserializacion es
/// estricta (<c>UnmappedMemberHandling.Disallow</c> en <c>AiAssistantService</c>), asi que si el
/// modelo inventa una propiedad extra, el parseo falla y se dispara el reintento / la degradacion.
/// Eso es a proposito: preferimos "no entendi" antes que aceptar cualquier cosa.
///
/// <para><b>Todo es opcional</b>: el modelo tiene que poder decir "esto no estaba en la frase"
/// devolviendo <c>null</c>, en vez de inventar. Lo que llegue en null queda vacio en la ficha.</para>
///
/// <para><b>Lo que el modelo NO decide</b>: la unidad del precio ("por noche" o total), el año de
/// una fecha escrita sin año, y si el operador escrito es tal o cual operador de la agencia. Todo eso
/// lo resuelve el motor mirando el texto original y el tarifario (M-22), porque son las decisiones
/// que mueven la plata y no pueden depender de la creatividad de un modelo.</para>
/// </summary>
public sealed class ServiceLineAiPayload
{
    /// <summary>Nombre del producto, limpio ("Sheraton Iguazú"). Sin la ciudad, sin el operador, sin el precio.</summary>
    public string? Producto { get; set; }

    /// <summary>Operador/mayorista tal como aparece escrito en la frase ("ola").</summary>
    public string? Operador { get; set; }

    /// <summary>Hotel: la habitacion ("doble", "triple").</summary>
    public string? Habitacion { get; set; }

    /// <summary>Hotel: el regimen de comidas ("desayuno", "media pension").</summary>
    public string? Regimen { get; set; }

    /// <summary>Hotel: el nombre fino de la habitacion ("Superior", "Vista al mar").</summary>
    public string? NombreFino { get; set; }

    /// <summary>Aereo: la cabina ("economica", "ejecutiva").</summary>
    public string? Cabina { get; set; }

    /// <summary>Traslado: el vehiculo ("van", "auto").</summary>
    public string? Vehiculo { get; set; }

    /// <summary>
    /// El numero de plata que aparece en la frase, sin puntos ni simbolos. Se acepta tanto numero como
    /// texto entre comillas (<see cref="FlexibleDecimalJsonConverter"/>): los modelos mandan
    /// <c>"48"</c> muy seguido y no vale la pena perder toda la interpretacion por eso.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal? Precio { get; set; }

    /// <summary>La moneda de ese numero, en codigo: "ARS" o "USD".</summary>
    public string? Moneda { get; set; }

    /// <summary>Fecha de entrada / ida, en formato AAAA-MM-DD.</summary>
    public string? FechaDesde { get; set; }

    /// <summary>Fecha de salida / vuelta, en formato AAAA-MM-DD.</summary>
    public string? FechaHasta { get; set; }

    /// <summary>Que tan seguro esta el modelo de cada dato.</summary>
    public ServiceLineAiConfidence? Confianza { get; set; }

    /// <summary>
    /// Copia todos los campos a un objeto NUEVO. Se usa al guardar en
    /// <see cref="ServiceLineInterpretationCache"/> (fix C-1, 2026-08-1x): la cache tiene que quedarse
    /// con SU PROPIA copia, nunca con la referencia que sigue viva del lado de quien pregunto. Hoy nada
    /// en el codigo escribe estos campos despues de deserializar el JSON del modelo, pero clonar cuesta
    /// nada y evita que el dia de mañana un cambio en otra parte termine mutando lo que hay cacheado
    /// (y con eso, la respuesta que reciben TODOS los que pidan la misma frase despues).
    /// </summary>
    public ServiceLineAiPayload Clone() => new()
    {
        Producto = Producto,
        Operador = Operador,
        Habitacion = Habitacion,
        Regimen = Regimen,
        NombreFino = NombreFino,
        Cabina = Cabina,
        Vehiculo = Vehiculo,
        Precio = Precio,
        Moneda = Moneda,
        FechaDesde = FechaDesde,
        FechaHasta = FechaHasta,
        Confianza = Confianza == null ? null : new ServiceLineAiConfidence
        {
            Producto = Confianza.Producto,
            Operador = Confianza.Operador,
            Variante = Confianza.Variante,
            Precio = Confianza.Precio,
            Fechas = Confianza.Fechas,
        },
    };
}

/// <summary>
/// La seguridad del modelo, dato por dato: "alta", "media" o "baja". Lo que venga en "baja" el motor
/// lo descarta (queda vacio en la ficha): mas vale un casillero en blanco que un dato inventado.
/// </summary>
public sealed class ServiceLineAiConfidence
{
    public string? Producto { get; set; }
    public string? Operador { get; set; }
    public string? Variante { get; set; }
    public string? Precio { get; set; }
    public string? Fechas { get; set; }
}
