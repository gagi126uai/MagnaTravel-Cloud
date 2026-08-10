using System;
using Microsoft.Extensions.Caching.Memory;
using TravelApi.Domain.Helpers;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// Cache de la EXTRACCION del modelo para "la linea inteligente" (obra "prompt mas barato",
/// 2026-08-10; achicada en el fix C-1, 2026-08-1x).
///
/// <para><b>Por que existe</b>: dos vendedores de la misma agencia escriben frases MUY parecidas todo
/// el dia ("sheraton iguazu doble ola" hoy, mañana, y la semana que viene). Cada vez que se le pregunta
/// lo mismo al modelo se paga de nuevo el mismo pedido. Guardando lo que el modelo DIJO un rato, la
/// segunda vez que alguien escribe (casi) lo mismo no se llama al proveedor de IA.</para>
///
/// <para><b>C-1 — que se cachea y que NO, y por que es una linea dura</b>: aca SOLO vive
/// <see cref="ServiceLineAiPayload"/>, la extraccion cruda del modelo (producto/operador/fechas tal
/// como los escribio el vendedor, ANTES de tocar la base). Lo que sale de la base — los productos
/// parecidos del tarifario (<c>CatalogSearchAsync</c>), el enmascarado de costos segun el permiso del
/// que pregunta, y la duda — se recalcula EN CADA PEDIDO, nunca se cachea. Antes de este fix, la cache
/// guardaba la respuesta entera ya armada: un vendedor sin <c>cobranzas.see_cost</c> podia heredar el
/// costo real que vio un administrador que pregunto lo mismo minutos antes (fuga F-14), y un producto
/// recien creado no aparecia en los candidatos hasta que la cache venciera (rompiendo el anti-duplicados
/// P7). Separar "lo que dijo el modelo" (fijo, no depende de quien pregunta) de "la respuesta armada"
/// (siempre fresca, depende del permiso y de la base AHORA) cierra las dos fugas.</para>
///
/// <para><b>La clave NO lleva la reserva ni el usuario</b>: el prompt que arma
/// <see cref="ServiceLinePromptBuilder"/> no usa ningun dato de la reserva ni de quien pregunta (ver
/// ese archivo, "regla de privacidad"), asi que la misma frase da SIEMPRE la misma extraccion del
/// modelo sin importar quien la escribio — cachear por reserva/usuario solo tiraria cache hits a la
/// basura sin ganar nada. Lo que SI depende de quien pregunta (costos, candidatos) vive afuera de esta
/// cache, en <c>ServiceLineInterpreter.BuildInterpretationAsync</c>.</para>
///
/// <para><b>Por que una cache PROPIA y no <c>IMemoryCache</c> inyectado</b> (que ya usa
/// <c>UserPermissionResolver</c> en otra parte del sistema): esa cache compartida NO tiene tope de
/// entradas (<c>SizeLimit</c> sin configurar en <c>Program.cs</c>). Si le pusieramos <c>SizeLimit</c> a
/// la cache GLOBAL, TODAS las entradas de TODOS los consumidores tendrian que declarar <c>Size</c> o el
/// sistema tira una excepcion al guardar — un cambio que afectaria codigo de otra obra que no tiene
/// nada que ver con esto. Una instancia chica y propia evita ese efecto colateral.</para>
///
/// <para><b>TTL distinto segun el resultado</b>: una extraccion que SI trajo algo util vive 10 minutos
/// (el vendedor puede escribir la misma frase de nuevo en otra pestaña, o corregir un tipeo y volver a
/// probar); una llamada que NO trajo nada usable (timeout, degradado, JSON invalido) vive solo 2
/// minutos, por si el motivo fue algo pasajero y conviene reintentar pronto.</para>
/// </summary>
public sealed class ServiceLineInterpretationCache
{
    /// <summary>
    /// Tope de frases distintas guardadas a la vez. No hace falta mas: son minutos de uso normal de
    /// una agencia, no un catalogo entero. Sirve de piso de seguridad para que una rafaga rara de frases
    /// todas distintas no haga crecer la memoria sin limite.
    /// </summary>
    public const int MaxEntries = 500;

    private static readonly TimeSpan DefaultInterpretedTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultNotInterpretedTtl = TimeSpan.FromMinutes(2);

    private readonly MemoryCache _cache;
    private readonly TimeSpan _interpretedTtl;
    private readonly TimeSpan _notInterpretedTtl;

    public ServiceLineInterpretationCache()
        : this(DefaultInterpretedTtl, DefaultNotInterpretedTtl)
    {
    }

    /// <summary>
    /// Constructor interno SOLO para los tests: permite usar TTLs de milisegundos para probar el
    /// vencimiento sin esperar 10 minutos de verdad (<c>InternalsVisibleTo("TravelApi.Tests")</c> ya
    /// configurado en el csproj, mismo patron que el resto del proyecto).
    /// </summary>
    internal ServiceLineInterpretationCache(TimeSpan interpretedTtl, TimeSpan notInterpretedTtl)
    {
        _interpretedTtl = interpretedTtl;
        _notInterpretedTtl = notInterpretedTtl;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });
    }

    /// <summary>
    /// Busca una extraccion ya guardada para esta clave.
    ///
    /// <para><c>false</c> = nunca se le pregunto al modelo por esta frase (o vencio): hay que
    /// preguntarle de verdad. <c>true</c> con <paramref name="payload"/> en <c>null</c> = YA se le
    /// pregunto y no trajo nada usable (no hace falta preguntar de nuevo hasta que venza el TTL corto).
    /// <c>true</c> con <paramref name="payload"/> con datos = ahi esta la extraccion.</para>
    /// </summary>
    public bool TryGet(string key, out ServiceLineAiPayload? payload)
        => _cache.TryGetValue(key, out payload);

    /// <summary>
    /// Guarda una COPIA inmutable de la extraccion (nunca la referencia que sigue viva del lado del
    /// llamador) con el TTL que le corresponde segun si trajo algo util o no. <paramref name="payload"/>
    /// en <c>null</c> es un resultado valido: significa "se le pregunto al modelo y no sirvio de nada".
    /// </summary>
    public void Set(string key, ServiceLineAiPayload? payload)
    {
        var ttl = payload != null ? _interpretedTtl : _notInterpretedTtl;
        _cache.Set(key, payload?.Clone(), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            // Cada frase guardada "pesa" 1 para el tope de arriba (MaxEntries). No es el tamaño real en
            // bytes: es solo una forma de contar "cuantas frases distintas hay guardadas".
            Size = 1,
        });
    }

    /// <summary>
    /// Arma la clave de cache: tipo de servicio + frase, ambos "lavados" con el MISMO normalizador que
    /// usa el resto del tarifario (<see cref="TextNormalizer.NormalizeForMatch"/>), para que "Hotel" y
    /// "hotel", o "Sheraton  Iguazu" y "sheraton iguazu", pidan la MISMA entrada de cache.
    /// </summary>
    public static string BuildKey(string serviceType, string freeText)
    {
        var normalizedType = TextNormalizer.NormalizeForMatch(serviceType);
        var normalizedText = TextNormalizer.NormalizeForMatch(freeText);
        return $"linea-intel:{normalizedType}:{normalizedText}";
    }
}
