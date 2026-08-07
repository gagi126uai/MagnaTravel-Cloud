using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// "El tarifario se arma solo" (spec firmada 2026-08-06): la lista de productos APRENDIDOS y el alta
/// simple con freno de repetidos.
///
/// <para><b>Que es un "producto aprendido"</b>: un producto del tarifario (una fila de <c>Rates</c>) con
/// los precios que el sistema le fue guardando cada vez que se vendio, uno por operador
/// (<c>RateSupplierSale</c>). Las tarifas viejas cargadas a mano entran a la misma lista como un producto
/// mas (P2=A): si nunca se vendieron, su "ultimo precio" sale de la propia tarifa.</para>
///
/// <para><b>Por que agrupa en memoria y no en SQL</b>: el mismo hotel puede estar cargado N veces en el
/// tarifario viejo (una tarifa por habitacion, por operador, por vigencia) y la unica forma de saber que
/// son EL MISMO producto es comparar el nombre ya normalizado (sin tildes, sin mayusculas, sin
/// puntuacion repetida), cosa que Postgres no hace igual que <c>TextNormalizer</c>. Con un tarifario de
/// una agencia (miles de filas, no millones) traer el set filtrado y agrupar en memoria es barato y, sobre
/// todo, da el MISMO resultado que el buscador de la venta, que agrupa con esa misma funcion.</para>
/// </summary>
public partial class RateService
{
    /// <summary>Umbral por defecto de "precio viejo" si no hay settings a mano (mismo default que la cadena D7).</summary>
    private const int DefaultStalePriceDays = 60;

    /// <summary>Cuantos parecidos como maximo se le muestran al usuario cuando el sistema frena un alta.</summary>
    private const int SimilarProductsLimit = 5;

    public async Task<PagedResponse<LearnedProductDto>> GetLearnedProductsAsync(
        LearnedProductsQuery query, CancellationToken ct)
    {
        var supplierFilterId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var serviceTypeFilter = query.ServiceType?.Trim();
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var stalePriceDays = await GetStalePriceDaysAsync(ct);
        var today = DateTime.UtcNow;

        var rates = await LoadTarifarioRowsAsync(serviceTypeFilter, query.Search, ct);
        if (rates.Count == 0)
        {
            return PagedResponse<LearnedProductDto>.Create(
                Array.Empty<LearnedProductDto>(), query.GetNormalizedPage(), query.GetNormalizedPageSize(), 0);
        }

        var salesByRateId = await LoadLearnedSalesAsync(rates.Select(rate => rate.Id).ToList(), ct);

        // Agrupamos las N tarifas del mismo producto en un solo renglon (ver nota de la clase).
        var products = new List<LearnedProductDto>();
        foreach (var group in rates.GroupBy(BuildProductKey))
        {
            var supplierRows = BuildSupplierRows(
                group.ToList(), salesByRateId, supplierFilterId, canSeeCost, stalePriceDays, today);

            // Filtrando por operador, un producto que ese operador nunca toco no tiene nada que mostrar.
            if (supplierRows.Count == 0) continue;

            // Representante del grupo = la tarifa del precio mas nuevo: es la que abre la ficha.
            var representative = group
                .OrderByDescending(rate => LastKnownDateOf(rate, salesByRateId) ?? DateTime.MinValue)
                .First();

            products.Add(new LearnedProductDto
            {
                ProductPublicId = representative.PublicId,
                Name = representative.DisplayName,
                Subtitle = representative.Subtitle,
                ServiceType = representative.ServiceType,
                ServiceTypeLabel = CatalogDisplayLabels.ServiceType(representative.ServiceType),
                Suppliers = supplierRows
            });
        }

        var ordered = products
            .OrderBy(product => product.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(product => product.Subtitle ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var page = query.GetNormalizedPage();
        var pageSize = query.GetNormalizedPageSize();
        var pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return PagedResponse<LearnedProductDto>.Create(pageItems, page, pageSize, ordered.Count);
    }

    /// <summary>
    /// Arma los renglones por operador de UN producto. Cada operador aparece una sola vez: si vendio el
    /// producto, manda la memoria de venta; si nunca lo vendio, se usa el precio cargado en la tarifa.
    /// </summary>
    private static List<LearnedProductPriceDto> BuildSupplierRows(
        List<TarifarioRow> ratesOfProduct,
        IReadOnlyDictionary<int, List<LearnedSaleRow>> salesByRateId,
        int? supplierFilterId,
        bool canSeeCost,
        int stalePriceDays,
        DateTime today)
    {
        // Clave del renglon = el operador. 0 = "sin operador" (tarifa vieja que no tenia ninguno).
        var rowBySupplier = new Dictionary<int, LearnedProductPriceDto>();
        var dateBySupplier = new Dictionary<int, DateTime?>();

        void Put(int supplierId, LearnedProductPriceDto row, DateTime? date, bool comesFromSale)
        {
            if (supplierFilterId.HasValue && supplierId != supplierFilterId.Value) return;

            // Gana el dato mas nuevo; a igualdad de fecha, gana el que viene de una venta real.
            if (rowBySupplier.TryGetValue(supplierId, out var existing))
            {
                var existingDate = dateBySupplier[supplierId] ?? DateTime.MinValue;
                var candidateDate = date ?? DateTime.MinValue;
                var keepExisting = candidateDate < existingDate
                    || (candidateDate == existingDate && !comesFromSale && existing.ReservaPublicId != null);
                if (keepExisting) return;
            }

            rowBySupplier[supplierId] = row;
            dateBySupplier[supplierId] = date;
        }

        foreach (var rate in ratesOfProduct)
        {
            var sales = salesByRateId.TryGetValue(rate.Id, out var found) ? found : new List<LearnedSaleRow>();

            foreach (var sale in sales)
            {
                var price = canSeeCost ? sale.NetCost : sale.SalePrice;
                Put(sale.SupplierId, new LearnedProductPriceDto
                {
                    SupplierPublicId = sale.SupplierPublicId,
                    SupplierName = sale.SupplierName ?? "Sin operador",
                    Price = price,
                    PriceKind = canSeeCost ? "Costo" : "Venta",
                    Currency = sale.Currency ?? rate.Currency,
                    PriceUnit = sale.PriceUnit,
                    PriceUnitLabel = CatalogDisplayLabels.PriceUnit(sale.PriceUnit),
                    PriceDate = sale.SoldAt,
                    PriceAgeText = RelativeDateText.Age(today, sale.SoldAt),
                    IsOldPrice = IsOldPrice(sale.SoldAt, today, stalePriceDays),
                    ReservaPublicId = sale.ReservaPublicId,
                    NumeroReserva = sale.NumeroReserva
                }, sale.SoldAt, comesFromSale: true);
            }

            if (sales.Count > 0) continue;

            // Tarifa que nunca se vendio: el "ultimo precio" es el que alguien cargo a mano.
            var manualDate = rate.UpdatedAt ?? rate.CreatedAt;
            Put(rate.SupplierId ?? 0, new LearnedProductPriceDto
            {
                SupplierPublicId = rate.SupplierPublicId,
                SupplierName = rate.SupplierName ?? "Sin operador",
                Price = canSeeCost ? rate.NetCost : rate.SalePrice,
                PriceKind = canSeeCost ? "Costo" : "Venta",
                Currency = rate.Currency,
                PriceUnit = rate.PriceUnit,
                PriceUnitLabel = CatalogDisplayLabels.PriceUnit(rate.PriceUnit),
                PriceDate = manualDate,
                PriceAgeText = RelativeDateText.Age(today, manualDate),
                IsOldPrice = IsOldPrice(manualDate, today, stalePriceDays),
                ReservaPublicId = null,
                NumeroReserva = null
            }, manualDate, comesFromSale: false);
        }

        return rowBySupplier.Values
            .OrderByDescending(row => row.PriceDate ?? DateTime.MinValue)
            .ThenBy(row => row.SupplierName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsOldPrice(DateTime? date, DateTime today, int stalePriceDays)
        => date.HasValue && (today.Date - date.Value.Date).TotalDays > stalePriceDays;

    /// <summary>Fecha del dato mas nuevo que conoce una tarifa (su ultima venta, o su ultima edicion).</summary>
    private static DateTime? LastKnownDateOf(
        TarifarioRow rate, IReadOnlyDictionary<int, List<LearnedSaleRow>> salesByRateId)
    {
        if (salesByRateId.TryGetValue(rate.Id, out var sales) && sales.Count > 0)
        {
            return sales.Max(sale => sale.SoldAt);
        }

        return rate.UpdatedAt ?? rate.CreatedAt;
    }

    /// <summary>
    /// Clave con la que dos tarifas se consideran EL MISMO producto (delega en la regla UNICA de
    /// <see cref="BuildProductKey(string, string, string?)"/>): tipo + nombre normalizado, y en hotel
    /// tambien la ciudad (dos "Sheraton" de ciudades distintas son productos distintos).
    /// </summary>
    private static string BuildProductKey(TarifarioRow rate)
        => BuildProductKey(rate.ServiceType, rate.DisplayName, rate.City);

    private async Task<int> GetStalePriceDaysAsync(CancellationToken ct)
    {
        if (_settingsService is null) return DefaultStalePriceDays;
        var settings = await _settingsService.GetEntityAsync(ct);
        return settings.StaleCostReferenceDays > 0 ? settings.StaleCostReferenceDays : DefaultStalePriceDays;
    }

    // ============================================================
    // Lectura de las dos fuentes: las tarifas y su memoria de ventas
    // ============================================================

    /// <summary>
    /// Trae las tarifas ACTIVAS que entran en la lista, ya proyectadas a lo poquito que se necesita
    /// (nunca la entidad entera). Con texto de busqueda, suma los parecidos difusos de Postgres para que
    /// un error de tipeo igual encuentre el producto (P7).
    /// </summary>
    private async Task<List<TarifarioRow>> LoadTarifarioRowsAsync(
        string? serviceType, string? search, CancellationToken ct)
    {
        var ratesQuery = _db.Rates.AsNoTracking().Where(rate => rate.IsActive);

        if (!string.IsNullOrWhiteSpace(serviceType))
        {
            ratesQuery = ratesQuery.Where(rate => rate.ServiceType == serviceType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = TextNormalizer.NormalizeForCatalog(search);
            var raw = search.Trim().ToLowerInvariant();
            var fuzzyIds = await FindFuzzyRateIdsAsync(serviceType, normalized, ct);

            ratesQuery = ratesQuery.Where(rate =>
                fuzzyIds.Contains(rate.Id)
                || (rate.SearchName != null && rate.SearchName.Contains(normalized))
                || rate.ProductName.ToLower().Contains(raw)
                || (rate.HotelName != null && rate.HotelName.ToLower().Contains(raw))
                || (rate.City != null && rate.City.ToLower().Contains(raw)));
        }

        return await ratesQuery
            .Select(rate => new TarifarioRow
            {
                Id = rate.Id,
                PublicId = rate.PublicId,
                ServiceType = rate.ServiceType,
                ProductName = rate.ProductName,
                HotelName = rate.HotelName,
                City = rate.City,
                Origin = rate.Origin,
                Destination = rate.Destination,
                PickupLocation = rate.PickupLocation,
                DropoffLocation = rate.DropoffLocation,
                NetCost = rate.NetCost,
                SalePrice = rate.SalePrice,
                Currency = rate.Currency,
                PriceUnit = rate.PriceUnit,
                CreatedAt = rate.CreatedAt,
                UpdatedAt = rate.UpdatedAt,
                SupplierId = rate.SupplierId,
                SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// La memoria de ventas de esas tarifas: un renglon por (producto, operador) con el ultimo precio,
    /// cuando fue y de que reserva salio. Una sola consulta, sin N+1.
    /// </summary>
    private async Task<Dictionary<int, List<LearnedSaleRow>>> LoadLearnedSalesAsync(
        List<int> rateIds, CancellationToken ct)
    {
        var sales = await _db.RateSupplierSales
            .AsNoTracking()
            .Where(sale => rateIds.Contains(sale.RateId))
            .Select(sale => new LearnedSaleRow
            {
                RateId = sale.RateId,
                SupplierId = sale.SupplierId,
                SupplierPublicId = sale.Supplier != null ? (Guid?)sale.Supplier.PublicId : null,
                SupplierName = sale.Supplier != null ? sale.Supplier.Name : null,
                SoldAt = sale.LastSoldAt,
                NetCost = sale.LastNetCost,
                SalePrice = sale.LastSalePrice,
                Currency = sale.LastCurrency,
                PriceUnit = sale.LastPriceUnit,
                ReservaPublicId = sale.LastReserva != null ? (Guid?)sale.LastReserva.PublicId : null,
                NumeroReserva = sale.LastReserva != null ? sale.LastReserva.NumeroReserva : null
            })
            .ToListAsync(ct);

        return sales
            .GroupBy(sale => sale.RateId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    /// <summary>
    /// Ids de tarifas cuyo nombre SE PARECE al texto buscado, usando pg_trgm. Devuelve vacio si la base
    /// no es Postgres (tests InMemory) o si la extension no esta: en esos casos manda la busqueda por
    /// substring, que es peor pero no rompe nada.
    /// </summary>
    private async Task<List<int>> FindFuzzyRateIdsAsync(
        string? serviceType, string normalizedQuery, CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || normalizedQuery.Length < CatalogSearchMinQueryLength)
        {
            return new List<int>();
        }

        // Los valores van SIEMPRE como parametros de Npgsql (nunca concatenados): sin riesgo de inyeccion.
        var sql = @"
            SELECT ""Id""
            FROM ""Rates""
            WHERE ""IsActive"" = TRUE
              AND (@serviceType = '' OR ""ServiceType"" = @serviceType)
              AND (
                (""SearchName"" IS NOT NULL AND ""SearchName"" % @q AND similarity(""SearchName"", @q) >= @threshold)
                OR (""HotelName"" IS NOT NULL AND lower(""HotelName"") % @q AND similarity(lower(""HotelName""), @q) >= @threshold)
              )
            LIMIT @limit;";

        try
        {
            await using var command = CreateRatesCommand(sql);
            command.Parameters.Add(new NpgsqlParameter("q", normalizedQuery));
            command.Parameters.Add(new NpgsqlParameter("serviceType", serviceType?.Trim() ?? string.Empty));
            command.Parameters.Add(new NpgsqlParameter("threshold", FuzzyMatchSimilarityThreshold));
            command.Parameters.Add(new NpgsqlParameter("limit", CatalogSearchCandidateFetchLimit));

            return await ReadRateIdsAsync(command, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedFunction)
        {
            // pg_trgm no instalada (42883): sin parecidos difusos, queda la busqueda por substring.
            _logger.LogWarning("pg_trgm no disponible al buscar en el tarifario; se busca solo por texto contenido.");
            return new List<int>();
        }
    }

    private static async Task<List<int>> ReadRateIdsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var connection = command.Connection!;
        var connectionWasClosed = connection.State == ConnectionState.Closed;
        if (connectionWasClosed) await connection.OpenAsync(ct);

        try
        {
            var ids = new List<int>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetInt32(0));
            }
            return ids;
        }
        finally
        {
            if (connectionWasClosed) await connection.CloseAsync();
        }
    }

    // ============================================================
    // Alta simple + freno de repetidos (M-3 / M-4, P7)
    // ============================================================

    public async Task<SimpleProductCreationResult> CreateSimpleProductAsync(
        CreateSimpleProductRequest request, CancellationToken ct)
    {
        var serviceType = request.ServiceType?.Trim() ?? string.Empty;
        var name = request.Name?.Trim() ?? string.Empty;
        var city = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim();
        var isHotel = string.Equals(
            TextNormalizer.NormalizeForMatch(serviceType), "hotel", StringComparison.Ordinal);

        // Los mensajes de estos errores SI se le muestran al usuario: por eso son una excepcion TIPADA del
        // tarifario y no un ArgumentException cualquiera (el controller no puede saber, mirando el tipo, si
        // el texto de un ArgumentException es apto para una persona o es una fuga tecnica).
        if (serviceType.Length == 0)
            throw new RateValidationException("Elegí qué tipo de producto es.");
        if (name.Length == 0)
            throw new RateValidationException("El nombre del producto es obligatorio.");
        if (isHotel && city is null)
            throw new RateValidationException("La ciudad es obligatoria para crear un hotel.");
        if (request.Price < 0m)
            throw new RateValidationException("El precio no puede ser menor a cero.");

        // ResolveOptionalSupplierIdAsync (compartido con el resto del tarifario) avisa con un
        // ArgumentException generico; aca lo traducimos a la excepcion propia para que el mensaje que ve
        // el usuario sea el nuestro y no un texto tecnico de un helper interno.
        int? supplierId;
        try
        {
            supplierId = await ResolveOptionalSupplierIdAsync(request.SupplierId, ct);
        }
        catch (ArgumentException)
        {
            throw new RateValidationException("No encontramos ese operador.");
        }

        // El alta corre dentro de UNA transaccion Serializable con reintento (mismo patron que el alta de
        // servicio con catalogo). Sin esto, dos clics seguidos —o dos pestañas— podian crear el mismo
        // producto dos veces: el chequeo de repetidos leia ANTES de que la otra transaccion escribiera.
        return await RunSimpleProductTransactionAsync(async () =>
        {
            // FRENO DE REPETIDOS (P7, palabra del dueño: "evitar repetidos A TODA COSTA"). Vive en el
            // SERVIDOR a proposito: la pantalla ya muestra los parecidos, pero si el chequeo viviera solo
            // ahi, cualquier otro cliente podria colar el duplicado igual.
            if (!request.CreateAnyway)
            {
                var similar = await FindSimilarProductsAsync(serviceType, name, city, isHotel, ct);

                // Solo FRENAN los parecidos FUERTES (mismo nombre ya normalizado). Los demas viajan como
                // acompañamiento para que el usuario los vea, pero no le trabamos el alta: en aereos
                // ("Buenos Aires – Miami" vs "Buenos Aires – Madrid") cualquier coincidencia de texto
                // frenaba el alta y era insoportable.
                var frenan = similar.Where(candidate => candidate.IsSameName).ToList();
                if (frenan.Count > 0)
                {
                    return new SimpleProductCreationResult
                    {
                        Created = null,
                        Reason = SimpleProductCreationReasons.SimilarProductFound,
                        Message = BuildSimilarProductsMessage(frenan[0]),
                        SimilarProducts = similar
                    };
                }
            }
            else
            {
                // "Crear igual" confirmado por el usuario: se respeta, PERO igual se frena el doble clic.
                // Un gemelo exacto creado hace segundos no es una decision: es el mismo submit dos veces.
                var gemeloReciente = await FindTwinCreatedMomentsAgoAsync(serviceType, name, city, isHotel, ct);
                if (gemeloReciente is not null)
                {
                    var yaCreado = await GetByIdAsync(gemeloReciente.Id, ct);
                    if (yaCreado is not null)
                    {
                        return new SimpleProductCreationResult { Created = yaCreado };
                    }
                }
            }

            var currency = string.IsNullOrWhiteSpace(request.Currency)
                ? Monedas.ARS
                : request.Currency.Trim().ToUpperInvariant();
            var priceUnit = string.IsNullOrWhiteSpace(request.PriceUnit)
                ? DefaultPriceUnitFor(serviceType)
                : request.PriceUnit.Trim();

            var rate = new Rate
            {
                ServiceType = serviceType,
                ProductName = name,
                SupplierId = supplierId,
                Currency = currency,
                PriceUnit = priceUnit,
                // El alta simple carga UN precio. Lo tomamos como precio de venta de referencia y dejamos el
                // costo en 0: quien no ve costos no puede cargar un costo, y el costo real lo va a aprender
                // el sistema en la primera venta (ADR-017 §2.1).
                NetCost = 0m,
                Tax = 0m,
                SalePrice = request.Price,
                Commission = request.Price,
                IsActive = true
            };

            if (isHotel)
            {
                rate.HotelName = name;
                rate.City = city;
            }
            else if (city is not null)
            {
                // En los demas tipos, lo que el vendedor escribe como "ciudad" es el destino.
                rate.Destination = city;
            }

            rate.SearchName = BuildSearchName(rate.ServiceType, rate.ProductName, rate.HotelName);

            _db.Rates.Add(rate);
            await _db.SaveChangesAsync(ct);

            // Rastro de AUTOR: quien cargo el producto a mano. Se deja en la AUDITORIA (el mecanismo de
            // rastro de la casa) en vez de sumar una columna: no cambia el esquema y queda en la misma
            // pantalla donde se miran el resto de las acciones sensibles. Va STAGED para entrar en ESTA
            // transaccion — si el alta se revierte, el rastro se revierte con ella.
            StageProductAudit(
                AuditActions.RateCreatedManually, rate,
                $"Alta a mano desde el Tarifario. Tipo: {rate.ServiceType}. " +
                $"Nombre: {rate.ProductName}. Ciudad: {city ?? "(sin ciudad)"}.");
            await _db.SaveChangesAsync(ct);

            var created = await GetByIdAsync(rate.Id, ct)
                ?? throw new InvalidOperationException("No se pudo cargar el producto recién creado.");

            return new SimpleProductCreationResult { Created = created };
        }, ct);
    }

    /// <summary>
    /// Ventana de "doble clic": un producto identico creado hace menos de esto NO es una decision del
    /// usuario, es el mismo formulario enviado dos veces. Nadie carga a proposito el mismo producto dos
    /// veces en medio minuto.
    /// </summary>
    private static readonly TimeSpan DoubleSubmitWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Busca un gemelo EXACTO (mismo tipo, mismo nombre normalizado y, en hotel, misma ciudad) creado dentro
    /// de la ventana de doble clic. Devuelve null si no hay ninguno.
    /// </summary>
    private async Task<Rate?> FindTwinCreatedMomentsAgoAsync(
        string serviceType, string name, string? city, bool isHotel, CancellationToken ct)
    {
        var searchName = BuildSearchName(serviceType, name, isHotel ? name : null);
        var since = DateTime.UtcNow - DoubleSubmitWindow;

        var candidates = await _db.Rates
            .AsNoTracking()
            .Where(rate => rate.ServiceType == serviceType
                && rate.IsActive
                && rate.SearchName == searchName
                && rate.CreatedAt >= since)
            .ToListAsync(ct);

        if (!isHotel) return candidates.FirstOrDefault();

        var normalizedCity = TextNormalizer.NormalizeForCatalog(city);
        return candidates.FirstOrDefault(candidate =>
            string.Equals(
                TextNormalizer.NormalizeForCatalog(candidate.City), normalizedCity, StringComparison.Ordinal));
    }

    /// <summary>
    /// Corre el cuerpo dentro de UNA transaccion Serializable, con la estrategia de reintentos ya
    /// configurada (mismo patron y mismos motivos que <c>BookingService.RunCatalogTransactionAsync</c>):
    /// dos altas simultaneas del mismo producto producen un serialization failure en una, que se reintenta,
    /// y en el reintento YA ENCUENTRA el producto del que gano.
    ///
    /// <para>En motores no relacionales (tests InMemory) no hay transacciones reales: se ejecuta el cuerpo
    /// derecho. La carrera de verdad se prueba contra Postgres.</para>
    /// </summary>
    private async Task<T> RunSimpleProductTransactionAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            return await body();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Delegate RE-EJECUTABLE: en un reintento el ChangeTracker puede arrastrar entidades del intento
            // fallido, asi que se arranca siempre desde cero.
            _db.ChangeTracker.Clear();

            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            try
            {
                var result = await body();
                await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    /// <summary>
    /// Deja el rastro de QUIEN toco un producto del tarifario. STAGED: se inserta con el SaveChanges de la
    /// operacion, para que rastro y cambio sean atomicos. Sin usuario resoluble (tareas del sistema, tests)
    /// no se registra nada: un rastro sin autor no sirve para nada.
    /// </summary>
    private void StageProductAudit(string action, Rate rate, string details)
    {
        if (_auditService is null) return;

        var user = _httpContextAccessor?.HttpContext?.User;
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return;

        _auditService.StageBusinessEvent(
            action: action,
            entityName: AuditActions.RateEntityName,
            entityId: rate.PublicId.ToString(),
            details: details,
            userId: userId,
            userName: user?.FindFirstValue(ClaimTypes.Name) ?? user?.Identity?.Name);
    }

    // ============================================================
    // Renombrar el PRODUCTO entero (spec 2026-08-06 §2.2)
    // ============================================================

    public async Task<RenameLearnedProductResult> RenameLearnedProductAsync(
        RenameLearnedProductRequest request, CancellationToken ct)
    {
        var serviceType = request.ServiceType?.Trim() ?? string.Empty;
        var currentName = request.Name?.Trim() ?? string.Empty;
        var currentCity = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim();
        var newName = request.NewName?.Trim() ?? string.Empty;
        var newCity = string.IsNullOrWhiteSpace(request.NewCity) ? null : request.NewCity.Trim();
        var isHotel = string.Equals(
            TextNormalizer.NormalizeForMatch(serviceType), "hotel", StringComparison.Ordinal);

        if (serviceType.Length == 0)
            throw new RateValidationException("Elegí qué tipo de producto es.");
        if (currentName.Length == 0)
            throw new RateValidationException("Falta el nombre actual del producto.");
        if (newName.Length == 0)
            throw new RateValidationException("El nombre del producto es obligatorio.");
        if (isHotel && newCity is null)
            throw new RateValidationException("La ciudad es obligatoria para un hotel.");

        return await RunSimpleProductTransactionAsync(async () =>
        {
            var groupKey = BuildProductKey(serviceType, currentName, currentCity);
            var destinationKey = BuildProductKey(serviceType, newName, isHotel ? newCity : currentCity);

            // Se traen SOLO las tarifas activas del tipo: en un tarifario de agencia son pocas, y la
            // comparacion final se hace con el normalizador de la app (Postgres no normaliza igual).
            var rates = await _db.Rates
                .Where(rate => rate.IsActive && rate.ServiceType == serviceType)
                .ToListAsync(ct);

            var group = rates.Where(rate => KeyOf(rate) == groupKey).ToList();
            if (group.Count == 0)
            {
                throw new KeyNotFoundException("No encontramos ese producto en el tarifario.");
            }

            // Colision: el nombre nuevo ya lo tiene OTRO producto. NO se fusiona nada (unir dos productos
            // es otra obra, con su propia pantalla): se avisa y se deja todo como estaba.
            if (destinationKey != groupKey && rates.Any(rate => KeyOf(rate) == destinationKey))
            {
                var donde = isHotel && newCity is not null ? $" en {newCity}" : string.Empty;
                throw new RateProductNameTakenException(
                    $"Ya tenés un producto que se llama \"{newName}\"{donde}. " +
                    "Poné otro nombre, o usá el que ya existe para no tenerlo dos veces.");
            }

            foreach (var rate in group)
            {
                rate.ProductName = newName;
                if (isHotel)
                {
                    rate.HotelName = newName;
                    rate.City = newCity;
                }
                else if (newCity is not null)
                {
                    // En los demas tipos la "ciudad" es el destino que se muestra debajo del nombre.
                    rate.Destination = newCity;
                }

                rate.SearchName = BuildSearchName(rate.ServiceType, rate.ProductName, rate.HotelName);
                rate.UpdatedAt = DateTime.UtcNow;
            }

            var representative = group[0];
            StageProductAudit(
                AuditActions.RateRenamed, representative,
                $"Renombrado en el Tarifario. Antes: {currentName}" +
                (currentCity is null ? string.Empty : $" ({currentCity})") +
                $". Ahora: {newName}" + (newCity is null ? string.Empty : $" ({newCity})") +
                $". Tarifas afectadas: {group.Count}.");

            // Un solo SaveChanges: o se renombra el producto entero con su rastro, o no se renombra nada.
            await _db.SaveChangesAsync(ct);

            return new RenameLearnedProductResult
            {
                ProductPublicId = representative.PublicId,
                Name = newName,
                Subtitle = isHotel ? newCity : (newCity ?? NullIfBlank(representative.Destination)),
                RenamedRates = group.Count
            };
        }, ct);

        // Clave de identidad de una tarifa YA MATERIALIZADA (misma regla que BuildProductKey de la lista).
        string KeyOf(Rate rate)
        {
            var displayName = isHotel && !string.IsNullOrWhiteSpace(rate.HotelName)
                ? rate.HotelName!
                : rate.ProductName;
            return BuildProductKey(rate.ServiceType, displayName, rate.City);
        }
    }

    /// <summary>
    /// Clave con la que dos tarifas son EL MISMO producto: tipo + nombre normalizado (+ ciudad en hotel).
    /// Es la MISMA regla que usa la lista y el buscador — si se separaran, la pantalla mostraria un grupo y
    /// el renombre tocaria otro.
    /// </summary>
    private static string BuildProductKey(string serviceType, string displayName, string? city)
    {
        var typeKey = TextNormalizer.NormalizeForMatch(serviceType);
        var nameKey = TextNormalizer.NormalizeForCatalog(displayName);
        if (typeKey != "hotel") return $"{typeKey}|{nameKey}";

        return $"{typeKey}|{nameKey}|{TextNormalizer.NormalizeForCatalog(city)}";
    }

    /// <summary>
    /// Busca productos que se parezcan al que se esta por crear: primero los de nombre IGUAL (ya
    /// normalizado) y despues los difusos de pg_trgm. En hotel, un homonimo de OTRA ciudad no cuenta como
    /// parecido (son dos hoteles distintos y frenar ahi seria molestar al vendedor por nada).
    /// </summary>
    private async Task<List<SimilarProductDto>> FindSimilarProductsAsync(
        string serviceType, string name, string? city, bool isHotel, CancellationToken ct)
    {
        var normalizedName = TextNormalizer.NormalizeForCatalog(name);
        var normalizedCity = TextNormalizer.NormalizeForCatalog(city);

        var candidateRows = await LoadTarifarioRowsAsync(serviceType, name, ct);

        var similar = new List<SimilarProductDto>();
        foreach (var candidate in candidateRows)
        {
            var candidateName = TextNormalizer.NormalizeForCatalog(candidate.DisplayName);
            var sameName = string.Equals(candidateName, normalizedName, StringComparison.Ordinal);

            if (isHotel)
            {
                var candidateCity = TextNormalizer.NormalizeForCatalog(candidate.City);
                var sameCity = string.Equals(candidateCity, normalizedCity, StringComparison.Ordinal);
                // Mismo nombre pero otra ciudad = otro hotel. No frena.
                if (!sameCity) continue;
            }

            similar.Add(new SimilarProductDto
            {
                RatePublicId = candidate.PublicId,
                Name = candidate.DisplayName,
                Subtitle = candidate.Subtitle,
                IsSameName = sameName
            });
        }

        return similar
            .OrderByDescending(item => item.IsSameName)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(SimilarProductsLimit)
            .ToList();
    }

    /// <summary>Texto del cartel de confirmacion, armado por el motor (T-13, sin jerga).</summary>
    private static string BuildSimilarProductsMessage(SimilarProductDto firstMatch)
    {
        var where = string.IsNullOrWhiteSpace(firstMatch.Subtitle)
            ? string.Empty
            : $" en {firstMatch.Subtitle}";

        return $"Ya tenés \"{firstMatch.Name}\"{where}. " +
               "Si es el mismo, elegí ese y evitás tenerlo dos veces con precios distintos.";
    }

    /// <summary>
    /// Nombre NORMALIZADO con el que el buscador encuentra el producto. La fuente es la regla UNICA de
    /// ADR-017: en hotel manda el nombre del hotel (si esta cargado); en el resto, el nombre del producto.
    /// </summary>
    private static string BuildSearchName(string serviceType, string productName, string? hotelName)
    {
        var isHotel = string.Equals(
            TextNormalizer.NormalizeForMatch(serviceType), "hotel", StringComparison.Ordinal);
        var source = isHotel && !string.IsNullOrWhiteSpace(hotelName) ? hotelName! : productName;
        return TextNormalizer.NormalizeForCatalog(source);
    }

    /// <summary>Unidad de precio que corresponde al tipo cuando el alta simple no la aclara.</summary>
    private static string DefaultPriceUnitFor(string serviceType)
        => TextNormalizer.NormalizeForMatch(serviceType) switch
        {
            "hotel" => CatalogPriceUnits.NocheHabitacion,
            "aereo" or "paquete" => CatalogPriceUnits.Pasajero,
            "asistencia" => CatalogPriceUnits.PasajeroDia,
            _ => CatalogPriceUnits.Servicio
        };

    // ============================================================
    // Filas internas (proyecciones): no salen nunca al cliente
    // ============================================================

    /// <summary>Lo poquito que necesita la lista de una tarifa del tarifario.</summary>
    private sealed class TarifarioRow
    {
        public int Id { get; set; }
        public Guid PublicId { get; set; }
        public string ServiceType { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? HotelName { get; set; }
        public string? City { get; set; }
        public string? Origin { get; set; }
        public string? Destination { get; set; }
        public string? PickupLocation { get; set; }
        public string? DropoffLocation { get; set; }
        public decimal NetCost { get; set; }
        public decimal SalePrice { get; set; }
        public string? Currency { get; set; }
        public string? PriceUnit { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int? SupplierId { get; set; }
        public Guid? SupplierPublicId { get; set; }
        public string? SupplierName { get; set; }

        /// <summary>Nombre lindo: el del hotel si es hotel y esta cargado, si no el del producto.</summary>
        public string DisplayName =>
            string.Equals(TextNormalizer.NormalizeForMatch(ServiceType), "hotel", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(HotelName)
                ? HotelName!
                : ProductName;

        /// <summary>Renglon gris: ciudad (hotel), ruta (aereo/traslado) o destino (resto).</summary>
        public string? Subtitle => TextNormalizer.NormalizeForMatch(ServiceType) switch
        {
            "hotel" => Blank(City),
            "aereo" => Route(Origin, Destination),
            "traslado" => Route(PickupLocation, DropoffLocation),
            _ => Blank(Destination)
        };

        private static string? Blank(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string? Route(string? from, string? to)
        {
            var origin = Blank(from);
            var destination = Blank(to);
            if (origin is null) return destination;
            if (destination is null) return origin;
            return $"{origin} → {destination}";
        }
    }

    /// <summary>Un renglon de la memoria de ventas, ya con el operador y la reserva resueltos.</summary>
    private sealed class LearnedSaleRow
    {
        public int RateId { get; set; }
        public int SupplierId { get; set; }
        public Guid? SupplierPublicId { get; set; }
        public string? SupplierName { get; set; }
        public DateTime SoldAt { get; set; }
        public decimal NetCost { get; set; }
        public decimal SalePrice { get; set; }
        public string? Currency { get; set; }
        public string? PriceUnit { get; set; }
        public Guid? ReservaPublicId { get; set; }
        public string? NumeroReserva { get; set; }
    }
}
