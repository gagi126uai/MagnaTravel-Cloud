using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Ai;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// "La linea inteligente" (spec firmada 2026-08-07 §3 / M-20 a M-23 y M-27): el vendedor escribe
/// como habla y el sistema devuelve el servicio armado para precargar la ficha en amarillo.
///
/// <para><b>El recorrido, en cinco pasos</b>:</para>
/// <list type="number">
///   <item>¿Hay inteligencia artificial configurada? Si no, se contesta "no entendi" y listo (M-23).</item>
///   <item>Se arma el contexto ACOTADO de la agencia: nombres del tarifario y de los operadores. Nada mas (M-21).</item>
///   <item>Se le pide al modelo que extraiga los datos, con un reloj corto encima.</item>
///   <item>El MOTOR revisa dato por dato: lo que no cierra, se descarta (queda vacio en la ficha).</item>
///   <item>Si algo grande quedo sin resolver, el motor arma UNA pregunta de si o no (M-22).</item>
/// </list>
///
/// <para><b>La regla que gobierna todo</b>: la inteligencia artificial SUGIERE, el motor DECIDE.
/// Ningun dato del modelo llega a la ficha sin pasar por una verificacion contra el tarifario, contra
/// el texto original o contra el calendario. Un modelo que alucine no puede meter en la ficha un
/// producto que no existe ni un precio que nadie escribio.</para>
///
/// <para><b>Nunca lanza</b>: cualquier problema (sin clave, proveedor caido, respuesta ilegible,
/// demora) termina en "no interpretado", que para la pantalla es el buscador de siempre (§3.5).</para>
/// </summary>
public sealed class ServiceLineInterpreter : IServiceLineInterpreter
{
    private readonly AppDbContext _db;
    private readonly IRateService _rateService;
    private readonly IAiAssistantService _assistant;
    private readonly IAiConnectionResolver _connectionResolver;
    private readonly ServiceLineInterpretationOptions _options;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly ILogger<ServiceLineInterpreter> _logger;

    /// <summary>
    /// Cuantas filas del tarifario se miran para elegir los nombres que van al prompt. Un tarifario de
    /// agencia tiene miles de filas, no millones, y de cada una se leen cuatro textos cortos; el tope
    /// esta para que una base rara no convierta esto en una consulta pesada.
    /// </summary>
    private const int CatalogScanLimit = 2000;

    /// <summary>Largo minimo de una palabra para que cuente al comparar la frase con un nombre del tarifario.</summary>
    private const int MeaningfulWordLength = 3;

    /// <summary>Topes del nombre de producto que se le muestra al vendedor ("crear ..."), en palabras y en caracteres.</summary>
    private const int MaxProductSearchWords = 6;
    private const int MaxProductSearchLength = 80;

    /// <summary>Tope del texto libre de la variante (nombre fino de habitacion / vehiculo).</summary>
    private const int MaxFreeVariantTextLength = 40;

    /// <summary>Confianza declarada por el modelo que hace que el motor DESCARTE el dato.</summary>
    private const string LowConfidence = "baja";

    public ServiceLineInterpreter(
        AppDbContext db,
        IRateService rateService,
        IAiAssistantService assistant,
        IAiConnectionResolver connectionResolver,
        ServiceLineInterpretationOptions options,
        ILogger<ServiceLineInterpreter> logger,
        IHttpContextAccessor? httpContextAccessor = null,
        IUserPermissionResolver? permissionResolver = null)
    {
        _db = db;
        _rateService = rateService;
        _assistant = assistant;
        _connectionResolver = connectionResolver;
        _options = options;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _permissionResolver = permissionResolver;
    }

    public async Task<ServiceLineInterpretationDto> InterpretAsync(
        string? freeText,
        string? serviceType,
        CancellationToken cancellationToken)
    {
        var text = (freeText ?? string.Empty).Trim();
        var type = (serviceType ?? string.Empty).Trim();

        if (text.Length == 0 || type.Length == 0)
        {
            return ServiceLineInterpretationDto.NotInterpreted();
        }

        // Sin configuracion utilizable ni se arma el prompt: nos ahorramos las consultas a la base y,
        // sobre todo, esta instalacion simplemente trabaja sin las ayudas (no es un error).
        if (!await _connectionResolver.IsUsableAsync(cancellationToken))
        {
            return ServiceLineInterpretationDto.NotInterpreted();
        }

        try
        {
            var context = await BuildCatalogContextAsync(type, text, cancellationToken);
            var request = ServiceLinePromptBuilder.Build(text, type, context, DateTime.UtcNow.Date);

            var payload = await AskModelAsync(request, cancellationToken);
            if (payload is null)
            {
                return ServiceLineInterpretationDto.NotInterpreted();
            }

            return await BuildInterpretationAsync(payload, text, type, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // El vendedor cerro la ficha. No es una falla nuestra: se propaga.
            throw;
        }
        catch (Exception ex)
        {
            // Red de contencion FINAL: que "esta caja nunca rompe la carga de un servicio" sea verdad
            // POR CONSTRUCCION y no por haber previsto cada falla. Si se cae la base, si el tarifario
            // devuelve algo raro o si aparece un bug nuestro, el vendedor ve el buscador de siempre.
            // El detalle tecnico queda SOLO en el log del servidor (P-17).
            _logger.LogError(ex, "Linea inteligente: falla inesperada interpretando la frase. Se responde sin interpretar.");
            return ServiceLineInterpretationDto.NotInterpreted();
        }
    }

    // ============================================================
    // Paso 2 — el contexto acotado que se le presta al modelo (M-21)
    // ============================================================

    /// <summary>
    /// Junta los nombres que le sirven al modelo para reconocer lo escrito: productos parecidos del
    /// tarifario, operadores de la agencia y nombres de habitacion/vehiculo del TARIFARIO.
    ///
    /// <para><b>Lo que NO entra, y es lo importante</b>: pasajeros, clientes, documentos, telefonos,
    /// mails, numeros de reserva e importes. Nada de eso ayuda a entender "sheraton iguazu doble" y
    /// todo eso viajaria a un proveedor de afuera.</para>
    /// </summary>
    private async Task<ServiceLineCatalogContext> BuildCatalogContextAsync(
        string serviceType, string freeText, CancellationToken ct)
    {
        var productNames = await LoadCandidateProductNamesAsync(serviceType, freeText, ct);

        var supplierNames = await _db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.IsActive)
            .OrderBy(supplier => supplier.Name)
            .Select(supplier => supplier.Name)
            .Take(ServiceLinePromptBuilder.MaxSupplierNames)
            .ToListAsync(ct);

        var variantNames = await LoadTarifarioVariantNamesAsync(serviceType, ct);

        return new ServiceLineCatalogContext(productNames, supplierNames, variantNames);
    }

    /// <summary>
    /// Los nombres finos de habitacion y los vehiculos que estan cargados EN EL TARIFARIO.
    ///
    /// <para><b>Por que NO se usa aca la memoria de nombres del sistema</b> (<c>M-19</c>,
    /// <c>CatalogVariantNameMemory</c>, que es la que alimenta las sugerencias mientras se escribe):
    /// esa memoria incluye, ademas del tarifario, lo tipeado a mano DENTRO de las reservas
    /// (<c>HotelBooking.RoomCategory</c>, <c>TransferBooking.VehicleType</c>). Son casillas de texto
    /// libre donde un vendedor apurado escribe cualquier cosa — "Suite flia Peralta 27345678" es un
    /// ejemplo real de lo que termina ahi. Mandar eso a un proveedor de afuera seria filtrar el
    /// apellido y el documento de un pasajero. El tarifario, en cambio, es catalogo: nombres de
    /// producto, no de personas.</para>
    ///
    /// <para>Se pierde poco y se gana mucho: lo que el modelo necesita es reconocer "sup" como
    /// "Superior", y para eso el tarifario alcanza. La unificacion fina con lo escrito en ventas sigue
    /// pasando DESPUES, del lado nuestro, sin salir del servidor.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadTarifarioVariantNamesAsync(
        string serviceType, CancellationToken ct)
    {
        var typeKey = TextNormalizer.NormalizeForMatch(serviceType);
        List<string?> names;

        if (typeKey == "hotel")
        {
            names = await _db.Rates.AsNoTracking()
                .Where(rate => rate.ServiceType.ToLower() == "hotel"
                    && rate.RoomCategory != null && rate.RoomCategory != "")
                .Select(rate => rate.RoomCategory)
                .ToListAsync(ct);
        }
        else if (typeKey == "traslado")
        {
            names = await _db.Rates.AsNoTracking()
                .Where(rate => rate.ServiceType.ToLower() == "traslado"
                    && rate.VehicleType != null && rate.VehicleType != "")
                .Select(rate => rate.VehicleType)
                .ToListAsync(ct);
        }
        else
        {
            // Aereo, paquete y asistencia no tienen nombre fino que ofrecer.
            return Array.Empty<string>();
        }

        return names
            .Select(name => name!.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ServiceLinePromptBuilder.MaxVariantNames)
            .ToList();
    }

    /// <summary>
    /// Elige los productos del tarifario que se PARECEN a lo escrito, para prestarle al modelo una
    /// lista corta en vez del tarifario entero.
    ///
    /// <para><b>Por que se filtra antes de preguntar</b>: mandar 3000 nombres en cada pedido seria caro,
    /// lento y contraproducente (el modelo se pierde). Con un filtro simple por palabras compartidas
    /// alcanza: si la frase dice "sheraton iguazu", entran los que tengan "sheraton" o "iguazu".</para>
    ///
    /// <para>Si nada se parece, la lista va VACIA a proposito: es mejor que el modelo devuelva el
    /// nombre crudo (y la pantalla ofrezca crearlo) a que elija cualquier cosa de una lista al azar.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadCandidateProductNamesAsync(
        string serviceType, string freeText, CancellationToken ct)
    {
        // Mismo filtro de tipo que usa el listado del tarifario: se compara sin importar mayusculas.
        var typeFilter = serviceType.Trim().ToLowerInvariant();

        var rows = await _db.Rates
            .AsNoTracking()
            .Where(rate => rate.IsActive && rate.ServiceType.ToLower() == typeFilter)
            // Orden explicito: sin el, "los primeros 2000" es lo que la base tenga ganas de devolver y
            // dos llamadas iguales podrian prestarle al modelo listas distintas.
            .OrderBy(rate => rate.Id)
            .Select(rate => new
            {
                rate.ProductName,
                rate.HotelName,
                rate.City,
            })
            .Take(CatalogScanLimit)
            .ToListAsync(ct);

        var textWords = MeaningfulWords(freeText);
        var scored = new List<(string Name, int Score)>();

        foreach (var row in rows)
        {
            var displayName = string.IsNullOrWhiteSpace(row.HotelName) ? row.ProductName : row.HotelName!;
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            var score = CountSharedWords(displayName, textWords);
            if (score == 0) continue;

            // La ciudad viaja pegada al nombre para que el modelo distinga dos hoteles homonimos.
            var label = string.IsNullOrWhiteSpace(row.City) ? displayName : $"{displayName} ({row.City})";
            scored.Add((label, score));
        }

        return scored
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Score: group.Max(item => item.Score)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Name)
            .Take(ServiceLinePromptBuilder.MaxProductNames)
            .ToList();
    }

    // ============================================================
    // Paso 3 — preguntarle al modelo, con reloj
    // ============================================================

    /// <summary>
    /// Le pide al modelo la extraccion y devuelve el resultado, o <c>null</c> si no se pudo.
    ///
    /// <para><b>El reloj es nuestro, no del proveedor</b>: el vendedor esta esperando frente a la
    /// ficha. Si pasa el tiempo, se corta y se contesta "no entendi" (§3.5). La cancelacion del
    /// LLAMADOR (cerro la pantalla) es otra cosa y se propaga tal cual.</para>
    /// </summary>
    private async Task<ServiceLineAiPayload?> AskModelAsync(AiChatRequest request, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            _options.TimeoutSeconds,
            ServiceLineInterpretationOptions.MinimumTimeoutSeconds,
            ServiceLineInterpretationOptions.MaximumTimeoutSeconds));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var (value, result) = await _assistant.CompleteStructuredAsync<ServiceLineAiPayload>(
                request, timeoutSource.Token);

            if (!result.Succeeded || value is null)
            {
                // Ya viene degradado del cerebro (sin config, red, JSON invalido tras el reintento).
                // Se avisa por log con el motivo INTERNO; a la pantalla no le llega nada de esto.
                _logger.LogInformation(
                    "Linea inteligente: no se pudo interpretar la frase. Motivo interno: {Reason}",
                    result.DegradationReason ?? "sin detalle");
                return null;
            }

            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El que pidio se fue (cerro la ficha, se corto la conexion). No es un resultado nuestro.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Salto NUESTRO reloj: el modelo tardo mas de lo que el vendedor puede esperar.
            _logger.LogInformation(
                "Linea inteligente: el modelo no contesto dentro de {Seconds} segundos. Se sigue sin interpretar.",
                timeout.TotalSeconds);
            return null;
        }
    }

    // ============================================================
    // Paso 4 — el motor revisa dato por dato
    // ============================================================

    private async Task<ServiceLineInterpretationDto> BuildInterpretationAsync(
        ServiceLineAiPayload payload,
        string freeText,
        string serviceType,
        CancellationToken ct)
    {
        var response = new ServiceLineInterpretationDto();

        // El nombre con el que se busca NO son los caracteres que devolvio el modelo: son LAS PALABRAS
        // DEL VENDEDOR que el modelo señalo como el producto. Ver BuildProductSearchText.
        var productName = IsLowConfidence(payload.Confianza?.Producto)
            ? null
            : BuildProductSearchText(freeText, payload.Producto);

        if (productName != null)
        {
            response.ProductSearchText = productName;

            // El MISMO buscador de la ficha, con el nombre ya limpio: asi los parecidos salen con el
            // orden de siempre y con el enmascarado de costos que ese buscador ya aplica (F-14).
            var candidates = await _rateService.CatalogSearchAsync(serviceType, productName, ct);
            response.ProductCandidates = candidates;
            response.Product = BuildProductFromCandidates(candidates);
        }

        response.Supplier = await ResolveSupplierAsync(payload, freeText, ct);
        response.Variant = await ResolveVariantAsync(payload, serviceType, freeText, ct);

        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var priceWasExplicitPerUnit = MentionsPriceUnit(freeText);
        response.Price = canSeeCost
            ? ResolvePrice(payload, freeText, serviceType)
            : null;

        var yearWasWritten = MentionsYear(freeText);
        response.Dates = ResolveDates(payload);

        response.Doubt = PickSingleDoubt(response, serviceType, priceWasExplicitPerUnit, yearWasWritten, payload);

        response.Interpreted =
            response.Product != null
            || response.ProductCandidates.Count > 0
            || response.ProductSearchText != null
            || response.Supplier != null
            || response.Variant != null
            || response.Price != null
            || response.Dates != null;

        return response;
    }

    /// <summary>
    /// Arma el nombre con el que se va a buscar el producto, USANDO LAS PALABRAS QUE ESCRIBIO EL
    /// VENDEDOR — no las que devolvio el modelo.
    ///
    /// <para><b>Por que asi</b>: este texto se le muestra a la persona (es lo que dice la ultima
    /// opcion de la lista, "crear ..."). Si se mostraran los caracteres del modelo tal cual, cualquier
    /// cosa que el modelo escupa — una explicacion, un texto larguisimo, jerga en ingles — terminaria
    /// en la pantalla. Tomando las palabras de la frase original, lo que se ve es siempre lo que la
    /// persona escribio; lo unico que aporta el modelo es CUALES de esas palabras son el producto
    /// (descartando "doble", "48", "usd", el operador y las fechas), que es justamente su gracia.</para>
    ///
    /// <para>Devuelve <c>null</c> si ninguna palabra del producto que dijo el modelo aparece en la
    /// frase: eso es invento y no se muestra.</para>
    /// </summary>
    private static string? BuildProductSearchText(string freeText, string? modelProductName)
    {
        var wanted = MeaningfulWords(modelProductName ?? string.Empty);
        if (wanted.Count == 0) return null;

        // Se recorren las palabras ORIGINALES (con sus tildes y mayusculas) en el orden en que las
        // escribio el vendedor, y se conservan las que el modelo marco como parte del producto.
        var originalWords = freeText.Split(
            new[] { ' ', ',', ';', '.', '/', '-', '(', ')', '\t', '\n' },
            StringSplitOptions.RemoveEmptyEntries);

        var chosen = new List<string>();
        foreach (var word in originalWords)
        {
            var key = TextNormalizer.NormalizeForCatalog(word);
            if (key.Length < MeaningfulWordLength || !wanted.Contains(key)) continue;

            // Sin repetidos: si el vendedor escribio "hotel" dos veces, alcanza con una.
            if (chosen.Any(existing => TextNormalizer.NormalizeForCatalog(existing) == key)) continue;

            chosen.Add(word);
            if (chosen.Count >= MaxProductSearchWords) break;
        }

        if (chosen.Count == 0) return null;

        var text = string.Join(' ', chosen);
        return text.Length > MaxProductSearchLength ? text[..MaxProductSearchLength].TrimEnd() : text;
    }

    /// <summary>
    /// Elige el producto a precargar entre los parecidos que devolvio el buscador. El primero es el
    /// mas parecido (ese orden lo pone el buscador, no nosotros).
    ///
    /// <para><b>Confianza</b>: si hay UN solo parecido, es alta — no hay con que confundirlo. Si hay
    /// varios, es media: el sistema precarga el primero pero el vendedor tiene la lista abajo para
    /// cambiarlo de un click (§3.4).</para>
    /// </summary>
    private static InterpretedProductDto? BuildProductFromCandidates(IReadOnlyList<CatalogSearchItemDto> candidates)
    {
        if (candidates.Count == 0)
        {
            // El producto no esta en el tarifario. NO se inventa: la pantalla ofrece crearlo (§3.4).
            return null;
        }

        var best = candidates[0];
        return new InterpretedProductDto
        {
            RatePublicId = best.RatePublicId,
            Name = best.Name,
            Subtitle = best.Subtitle,
            Confidence = candidates.Count == 1
                ? InterpretationConfidence.High
                : InterpretationConfidence.Medium,
        };
    }

    /// <summary>
    /// Reconoce el operador entre los de la agencia. Nunca devuelve uno que no exista: si lo escrito
    /// no se parece a ninguno, no hay operador (el casillero queda vacio).
    ///
    /// <para><b>Exacto vs parecido</b>: si el nombre escrito coincide entero, es alta confianza y no
    /// hay nada que preguntar. Si es un pedazo ("ola" -> "Ola Mayorista"), es media y el motor va a
    /// preguntar <c>¿El operador es Ola Mayorista?</c> — cambiar de operador cambia el precio, asi que
    /// es duda grande (§4).</para>
    /// </summary>
    private async Task<InterpretedSupplierDto?> ResolveSupplierAsync(
        ServiceLineAiPayload payload, string freeText, CancellationToken ct)
    {
        var written = CleanUp(payload.Operador);
        if (written == null || IsLowConfidence(payload.Confianza?.Operador)) return null;

        // Mismo criterio anti-invento que el producto: el operador tiene que estar escrito en la frase.
        if (!MentionedIn(freeText, written)) return null;

        var suppliers = await _db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.IsActive)
            .Select(supplier => new { supplier.PublicId, supplier.Name })
            .ToListAsync(ct);

        var writtenKey = TextNormalizer.NormalizeForMatch(written);
        if (writtenKey.Length == 0) return null;

        var exact = suppliers.FirstOrDefault(supplier =>
            TextNormalizer.NormalizeForMatch(supplier.Name) == writtenKey);
        if (exact != null)
        {
            return new InterpretedSupplierDto
            {
                SupplierPublicId = exact.PublicId,
                Name = exact.Name,
                Confidence = InterpretationConfidence.High,
            };
        }

        var partial = suppliers
            .Where(supplier => TextNormalizer.NormalizeForMatch(supplier.Name).Contains(writtenKey, StringComparison.Ordinal))
            .OrderBy(supplier => supplier.Name.Length)
            .ToList();

        // Si el pedazo escrito le calza a DOS operadores distintos, el sistema no puede elegir por su
        // cuenta cual: precargar uno seria jugarse la plata a cara o cruz. Se deja vacio.
        if (partial.Count != 1) return null;

        return new InterpretedSupplierDto
        {
            SupplierPublicId = partial[0].PublicId,
            Name = partial[0].Name,
            Confidence = InterpretationConfidence.Medium,
        };
    }

    /// <summary>
    /// Arma la habitacion / cabina / vehiculo con el vocabulario de los desplegables de la ficha.
    /// Las palabras del vendedor ("dbl sup", "hb") las traduce <see cref="CatalogVariant"/>, que es la
    /// misma pieza que usa la memoria de precios: asi lo interpretado y lo guardado hablan igual.
    ///
    /// <para><b>Lista blanca, no traduccion libre</b>: habitacion, regimen y cabina se aceptan SOLO si
    /// caen en una de las opciones que ofrece el desplegable. Los normalizadores, cuando no reconocen
    /// algo, devuelven el texto tal cual — sin este filtro, un <c>"habitacion": "unknown"</c> del
    /// modelo terminaria escrito en la ficha como "Doble Unknown con desayuno".</para>
    /// </summary>
    private async Task<InterpretedVariantDto?> ResolveVariantAsync(
        ServiceLineAiPayload payload, string serviceType, string freeText, CancellationToken ct)
    {
        if (!CatalogVariant.AppliesTo(serviceType)) return null;
        if (IsLowConfidence(payload.Confianza?.Variante)) return null;

        // Cada pieza cerrada pasa por su lista blanca; lo que no esta en la lista se descarta (queda
        // vacio en la ficha), NO se muestra como si fuera una opcion valida.
        var roomType = KeepIfKnown(payload.Habitacion, CatalogVariant.IsKnownRoomType);
        var mealPlan = KeepIfKnown(payload.Regimen, CatalogVariant.IsKnownMealPlan);
        var cabinClass = KeepIfKnown(payload.Cabina, CatalogVariant.IsKnownCabin);

        // Los dos campos de TEXTO LIBRE (nombre fino de habitacion y vehiculo) no tienen lista cerrada,
        // asi que se revisan aparte. Ver AcceptFreeVariantTextAsync.
        var tarifarioNameKeys = await LoadTarifarioVariantNameKeysAsync(serviceType, ct);
        var fineName = await AcceptFreeVariantTextAsync(
            payload.NombreFino, serviceType, freeText, tarifarioNameKeys, ct);
        var vehicleType = await AcceptFreeVariantTextAsync(
            payload.Vehiculo, serviceType, freeText, tarifarioNameKeys, ct);

        var variant = CatalogVariant.For(
            serviceType,
            roomType: roomType,
            mealPlan: mealPlan,
            fineName: fineName,
            cabinClass: cabinClass,
            vehicleType: vehicleType);

        if (variant.Key.Length == 0) return null;

        // De la clave normalizada volvemos a las palabras EXACTAS que muestran los desplegables, para
        // que la ficha llegue con "Doble" y "Desayuno" elegidos y no con texto suelto que no matchea.
        var parts = CatalogVariant.PartsOf(serviceType, variant.Key);

        return new InterpretedVariantDto
        {
            RoomType = parts.RoomType,
            MealPlan = parts.MealPlan,
            RoomCategory = fineName ?? parts.FineName,
            CabinClass = parts.CabinClass,
            VehicleType = parts.VehicleType,
            Label = variant.Label,
            Confidence = ConfidenceOf(payload.Confianza?.Variante),
        };
    }

    /// <summary>
    /// Valida el numero de plata. Descarta (deja el casillero vacio) cuando:
    /// no hay numero, es cero o negativo, la moneda no es una de las que opera el sistema, o el
    /// numero no aparece escrito en la frase.
    ///
    /// <para><b>Sin moneda no hay precio</b>: un numero suelto sin saber si son pesos o dolares es una
    /// trampa cara. Antes que adivinar, el casillero queda vacio y el vendedor lo escribe.</para>
    /// </summary>
    private static InterpretedPriceDto? ResolvePrice(
        ServiceLineAiPayload payload, string freeText, string serviceType)
    {
        if (IsLowConfidence(payload.Confianza?.Precio)) return null;

        var amount = payload.Precio;
        if (amount is null || amount <= 0m) return null;

        var currency = payload.Moneda;
        if (!Monedas.EsSoportada(currency)) return null;

        // Anti-invento: ese numero tiene que estar escrito en la frase.
        if (!AmountWasWrittenIn(freeText, amount.Value)) return null;

        var priceUnit = DefaultPriceUnitFor(serviceType);

        return new InterpretedPriceDto
        {
            Amount = amount.Value,
            Currency = Monedas.Normalizar(currency),
            PriceUnit = priceUnit,
            PriceUnitLabel = CatalogDisplayLabels.PriceUnit(priceUnit),
            Confidence = ConfidenceOf(payload.Confianza?.Precio),
        };
    }

    /// <summary>
    /// Deja pasar una pieza CERRADA (habitacion, regimen, cabina) solo si es una de las opciones que
    /// ofrece el desplegable. Lo que no esta en la lista se descarta: el casillero queda vacio, que es
    /// la respuesta correcta cuando el sistema no entendio (§3.3).
    /// </summary>
    private static string? KeepIfKnown(string? written, Func<string?, bool> isKnown)
    {
        var value = CleanUp(written);
        if (value == null) return null;
        return isKnown(value) ? value : null;
    }

    /// <summary>
    /// Deja pasar los dos campos de TEXTO LIBRE de la variante (el nombre fino de la habitacion y el
    /// vehiculo), que por definicion no tienen lista cerrada. Se aceptan por dos caminos:
    ///
    /// <list type="number">
    ///   <item><b>Es un nombre que ya esta EN EL TARIFARIO</b>: la memoria (M-19) lo unifica con la
    ///   escritura que ya existe ("sup" -&gt; "Superior") y se muestra esa, no lo que escribio el
    ///   modelo.</item>
    ///   <item><b>O lo escribio el vendedor en la frase</b>, es corto (hasta 40 caracteres) y son solo
    ///   letras, numeros y espacios. Asi entra una habitacion nueva de verdad ("Vista al mar") sin que
    ///   entre cualquier cosa.</item>
    /// </list>
    ///
    /// <para><b>Por que el camino 1 se limita al TARIFARIO</b>: la memoria de nombres tambien conoce lo
    /// tipeado dentro de las reservas, y ahi aparecen cosas como "Suite flia Peralta 27345678". Sin
    /// este limite, un modelo que dijera "suite" haria que ese texto — con apellido y documento de un
    /// pasajero de OTRA reserva — se precargara en la ficha de esta. El tarifario es catalogo; las
    /// reservas tienen datos de personas.</para>
    ///
    /// <para><b>Por que hace falta el filtro en general</b>: este texto se muestra en la ficha y
    /// despues se guarda como el nombre de la habitacion. Sin filtro, una frase entera del modelo (o
    /// una palabra de relleno como "unknown") se convertiria en una habitacion nueva del tarifario, y
    /// ensuciar el tarifario es exactamente lo que toda esta obra viene a evitar (P7).</para>
    /// </summary>
    private async Task<string?> AcceptFreeVariantTextAsync(
        string? written,
        string serviceType,
        string freeText,
        IReadOnlySet<string> tarifarioNameKeys,
        CancellationToken ct)
    {
        var value = CleanUp(written);
        if (value == null) return null;

        // Camino 1: la memoria lo reconocio Y lo reconocido esta en el tarifario.
        var resolved = await _rateService.ResolveVariantNameAsync(serviceType, value, ct);
        if (resolved != null
            && tarifarioNameKeys.Contains(TextNormalizer.NormalizeForCatalog(resolved))
            && IsPlainShortText(resolved))
        {
            return resolved;
        }

        // Camino 2: no esta en el tarifario, asi que tiene que haberlo escrito el vendedor.
        if (!MentionedIn(freeText, value)) return null;
        if (!IsPlainShortText(value)) return null;

        return value;
    }

    /// <summary>
    /// Las claves normalizadas de los nombres finos / vehiculos que hay EN EL TARIFARIO. Es la lista
    /// blanca del camino 1 de <see cref="AcceptFreeVariantTextAsync"/>. Sin tope: si se recortara,
    /// un nombre valido quedaria afuera solo por el orden.
    /// </summary>
    private async Task<IReadOnlySet<string>> LoadTarifarioVariantNameKeysAsync(
        string serviceType, CancellationToken ct)
    {
        var typeKey = TextNormalizer.NormalizeForMatch(serviceType);
        List<string?> names;

        if (typeKey == "hotel")
        {
            names = await _db.Rates.AsNoTracking()
                .Where(rate => rate.ServiceType.ToLower() == "hotel"
                    && rate.RoomCategory != null && rate.RoomCategory != "")
                .Select(rate => rate.RoomCategory)
                .ToListAsync(ct);
        }
        else if (typeKey == "traslado")
        {
            names = await _db.Rates.AsNoTracking()
                .Where(rate => rate.ServiceType.ToLower() == "traslado"
                    && rate.VehicleType != null && rate.VehicleType != "")
                .Select(rate => rate.VehicleType)
                .ToListAsync(ct);
        }
        else
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return names
            .Select(name => TextNormalizer.NormalizeForCatalog(name))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Texto corto y "de gente": hasta 40 caracteres, solo letras (con tildes), numeros y espacios.
    /// Deja afuera frases largas, saltos de linea, comillas y signos raros.
    /// </summary>
    private static bool IsPlainShortText(string value)
    {
        if (value.Length == 0 || value.Length > MaxFreeVariantTextLength) return false;

        return value.All(character =>
            char.IsLetterOrDigit(character) || character == ' ');
    }

    /// <summary>
    /// La unidad en la que se guarda el precio de cada tipo. Es la MISMA que usa la memoria del
    /// tarifario al aprender de una venta (<see cref="CatalogUnitization"/>), para que lo interpretado
    /// y lo aprendido sean comparables.
    /// </summary>
    private static string DefaultPriceUnitFor(string serviceType) =>
        TextNormalizer.NormalizeForMatch(serviceType) switch
        {
            "hotel" => CatalogPriceUnits.NocheHabitacion,
            "aereo" or "paquete" => CatalogPriceUnits.Pasajero,
            "asistencia" => CatalogPriceUnits.PasajeroDia,
            _ => CatalogPriceUnits.Servicio,
        };

    /// <summary>
    /// Valida las fechas. Descarta las dos juntas si alguna es imposible: no se entiende, la salida
    /// es anterior a la entrada, o el año quedo fuera de un rango razonable (un servicio de 1999 o de
    /// 2040 es un error de lectura, no un viaje).
    /// </summary>
    private static InterpretedDatesDto? ResolveDates(ServiceLineAiPayload payload)
    {
        if (IsLowConfidence(payload.Confianza?.Fechas)) return null;

        var from = ParseCalendarDate(payload.FechaDesde);
        var to = ParseCalendarDate(payload.FechaHasta);

        if (from is null && to is null) return null;
        if (from.HasValue && to.HasValue && to.Value < from.Value) return null;

        return new InterpretedDatesDto
        {
            From = from,
            To = to,
            Confidence = ConfidenceOf(payload.Confianza?.Fechas),
        };
    }

    /// <summary>
    /// Lee una fecha "AAAA-MM-DD" y la deja como fecha de pared (medianoche Kind=Utc), que es el
    /// formato con el que viajan y se guardan todas las fechas de servicios en este sistema.
    /// </summary>
    private static DateTime? ParseCalendarDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!DateTime.TryParseExact(
                raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var currentYear = DateTime.UtcNow.Year;
        if (parsed.Year < currentYear - 2 || parsed.Year > currentYear + 5) return null;

        return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
    }

    // ============================================================
    // Paso 5 — UNA sola duda grande, y primero la de plata (M-22 / §4)
    // ============================================================

    /// <summary>
    /// Elige la unica pregunta que se muestra. El orden NO es casual:
    ///
    /// <list type="number">
    ///   <item><b>El precio</b>: "48 por noche" y "48 por toda la estadia" son plata bien distinta.</item>
    ///   <item><b>El operador</b>: define de quien es ese precio y a quien se le compra.</item>
    ///   <item><b>El año</b>: cambia el calendario, no la plata.</item>
    /// </list>
    ///
    /// <para>Si no hay ninguna, no se pregunta nada — que es lo normal cuando la frase venia completa.</para>
    /// </summary>
    private static ServiceLineDoubtDto? PickSingleDoubt(
        ServiceLineInterpretationDto response,
        string serviceType,
        bool priceUnitWasWritten,
        bool yearWasWritten,
        ServiceLineAiPayload payload)
    {
        // 1) La plata: hay precio de hotel y la frase no aclaro si es por noche.
        var isHotel = TextNormalizer.NormalizeForMatch(serviceType) == "hotel";
        if (response.Price != null && isHotel && !priceUnitWasWritten)
        {
            return new ServiceLineDoubtDto
            {
                Code = ServiceLineDoubtCodes.PricePerNight,
                Question = $"¿{FormatMoney(response.Price.Amount, response.Price.Currency)} es el precio por noche?",
                Field = ServiceLineDoubtFields.Price,
            };
        }

        // 2) El operador: se reconocio por un pedazo del nombre, no entero.
        //    La pregunta NO cita lo que devolvio el modelo: el unico texto variable es el nombre del
        //    operador, que sale de nuestra base. Citar al modelo seria dejarlo escribir en la pantalla.
        if (response.Supplier != null
            && response.Supplier.Confidence == InterpretationConfidence.Medium)
        {
            return new ServiceLineDoubtDto
            {
                Code = ServiceLineDoubtCodes.AmbiguousSupplier,
                Question = $"¿El operador es {response.Supplier.Name}?",
                Field = ServiceLineDoubtFields.Supplier,
            };
        }

        // 3) El año: hay fechas y la frase no lo decia, asi que lo eligio el sistema.
        var firstDate = response.Dates?.From ?? response.Dates?.To;
        if (firstDate.HasValue && !yearWasWritten)
        {
            return new ServiceLineDoubtDto
            {
                Code = ServiceLineDoubtCodes.DatesYear,
                Question = $"¿Las fechas son de {MonthName(firstDate.Value)} de {firstDate.Value.Year}?",
                Field = ServiceLineDoubtFields.Dates,
            };
        }

        return null;
    }

    // ============================================================
    // Ayudas chicas
    // ============================================================

    /// <summary>Texto util o null: lo que viene vacio o en espacios es "no hay dato".</summary>
    private static string? CleanUp(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsLowConfidence(string? declared)
        => string.Equals(TextNormalizer.NormalizeForMatch(declared), LowConfidence, StringComparison.Ordinal);

    /// <summary>
    /// Traduce la confianza declarada por el modelo a la nuestra. Solo existen dos valores hacia
    /// afuera: lo dudoso ya se descarto antes de llegar aca.
    /// </summary>
    private static string ConfidenceOf(string? declared)
        => TextNormalizer.NormalizeForMatch(declared) == "alta"
            ? InterpretationConfidence.High
            : InterpretationConfidence.Medium;

    /// <summary>
    /// ¿Lo que devolvio el modelo esta REALMENTE escrito en la frase? Alcanza con que comparta una
    /// palabra de tres letras o mas. Es el freno mas barato contra un modelo que completa de memoria
    /// ("sheraton" -> "Sheraton Buenos Aires" cuando nadie escribio Buenos Aires).
    /// </summary>
    private static bool MentionedIn(string freeText, string candidate)
    {
        var textWords = MeaningfulWords(freeText);
        if (textWords.Count == 0) return false;

        return CountSharedWords(candidate, textWords) > 0;
    }

    /// <summary>Las palabras de un texto que sirven para comparar (normalizadas, de 3 letras o mas).</summary>
    private static HashSet<string> MeaningfulWords(string text)
    {
        var normalized = TextNormalizer.NormalizeForCatalog(text);
        var words = normalized.Split(
            new[] { ' ', ',', ';', '.', '/', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);

        return words
            .Where(word => word.Length >= MeaningfulWordLength)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Cuantas palabras del nombre aparecen entre las palabras de la frase.</summary>
    private static int CountSharedWords(string name, HashSet<string> textWords)
    {
        var nameWords = MeaningfulWords(name);
        return nameWords.Count(word => textWords.Contains(word));
    }

    /// <summary>
    /// ¿El precio esta REALMENTE escrito en la frase? Se compara contra los numeros que aparecen, uno
    /// por uno, no contra todos los digitos pegoteados.
    ///
    /// <para><b>Por que importa la diferencia</b>: pegotear los digitos de "ola 48 usd del 12 al 15/9"
    /// da "4812159", y ahi "adentro" estan 1215, 812 y 48121. Un modelo que inventara cualquiera de
    /// esos precios pasaba el control. Comparando numero contra numero, solo pasa lo que la persona
    /// escribio.</para>
    /// </summary>
    private static bool AmountWasWrittenIn(string freeText, decimal amount)
    {
        var wanted = decimal.Truncate(amount).ToString(CultureInfo.InvariantCulture);
        return ExtractWrittenNumbers(freeText).Contains(wanted);
    }

    /// <summary>
    /// Los numeros que aparecen escritos en la frase, quedandose con su PARTE ENTERA y sin los puntos
    /// de miles: "1.250" -&gt; "1250", "48,50" -&gt; "48", "15/9" -&gt; "15" y "9".
    ///
    /// <para><b>Como distingue miles de decimales</b>: un separador seguido de EXACTAMENTE tres
    /// digitos (y nada mas pegado atras) es de miles; cualquier otro es el separador decimal y lo que
    /// viene despues se descarta. Es la misma regla que usa cualquier persona al leer un precio.</para>
    /// </summary>
    private static HashSet<string> ExtractWrittenNumbers(string freeText)
    {
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (index < freeText.Length)
        {
            if (!char.IsDigit(freeText[index]))
            {
                index++;
                continue;
            }

            // Se toma la corrida completa de digitos y separadores pegados ("1.250,75").
            var start = index;
            while (index < freeText.Length
                   && (char.IsDigit(freeText[index]) || freeText[index] == '.' || freeText[index] == ','))
            {
                index++;
            }

            var run = freeText[start..index].TrimEnd('.', ',');
            var integerPart = BuildIntegerPart(run);
            if (integerPart.Length > 0) numbers.Add(integerPart);
        }

        return numbers;
    }

    /// <summary>Se queda con la parte entera de un numero escrito, uniendo los grupos de miles.</summary>
    private static string BuildIntegerPart(string run)
    {
        var groups = run.Split(new[] { '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length == 0) return string.Empty;

        var integerPart = groups[0];
        for (var position = 1; position < groups.Length; position++)
        {
            // Grupo de 3 digitos = separador de miles, se pega. Cualquier otra cosa = decimales, se corta.
            if (groups[position].Length != 3) break;
            integerPart += groups[position];
        }

        // "0048" y "48" son el mismo precio escrito: se compara sin los ceros de adelante.
        return integerPart.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";
    }

    /// <summary>
    /// ¿La frase aclara en que unidad esta el precio? ("48 por noche", "la noche", "x noche", "total").
    /// Si lo aclara, no hace falta preguntar nada.
    /// </summary>
    private static bool MentionsPriceUnit(string freeText)
    {
        var normalized = TextNormalizer.NormalizeForCatalog(freeText);
        string[] hints =
        {
            "por noche", "la noche", "x noche", "c/noche", "cada noche", "noche",
            "por persona", "por pasajero", "total", "en total", "todo",
        };

        return hints.Any(hint => normalized.Contains(hint, StringComparison.Ordinal));
    }

    /// <summary>¿La frase trae un año escrito con sus cuatro digitos ("2026")?</summary>
    private static bool MentionsYear(string freeText)
    {
        var digitRuns = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var character in freeText)
        {
            if (char.IsDigit(character))
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0)
            {
                digitRuns.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) digitRuns.Add(current.ToString());

        return digitRuns.Any(run =>
            run.Length == 4
            && int.TryParse(run, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year >= 2000
            && year <= 2100);
    }

    /// <summary>Un monto escrito como se lee en Argentina: "US$ 48" / "$ 91.000".</summary>
    private static string FormatMoney(decimal amount, string currency)
    {
        var symbol = string.Equals(currency, Monedas.USD, StringComparison.Ordinal) ? "US$" : "$";
        var culture = CultureInfo.GetCultureInfo("es-AR");
        var hasCents = amount != Math.Truncate(amount);
        return $"{symbol} {amount.ToString(hasCents ? "N2" : "N0", culture)}";
    }

    /// <summary>El mes en castellano, para la pregunta de las fechas.</summary>
    private static string MonthName(DateTime date)
        => CultureInfo.GetCultureInfo("es-AR").DateTimeFormat.GetMonthName(date.Month);
}
