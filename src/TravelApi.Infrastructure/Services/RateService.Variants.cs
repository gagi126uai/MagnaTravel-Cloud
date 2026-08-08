using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// La memoria por VARIANTE puesta al servicio de la venta (spec firmada 2026-08-07, M-15 / M-18 / M-19):
/// que precio sugerir para la habitación que se está vendiendo, que nombres finos ya se usaron alguna
/// vez, y como corregir la etiqueta de una habitación sin tocar un solo importe.
/// </summary>
public partial class RateService
{
    // ============================================================
    // M-15 — que precio sugerir para ESTA habitacion
    // ============================================================

    /// <summary>
    /// Devuelve el precio que corresponde a la combinacion exacta (producto + operador + variante) y, si
    /// de esa no hay, el de la habitacion MAS PARECIDA, marcado para que la pantalla NO lo precargue
    /// (V9=A: el casillero queda vacio y el precio de la parecida se muestra abajo, en gris, diciendo de
    /// cual es). Devuelve null cuando el producto no tiene ningun precio aprendido.
    /// </summary>
    public async Task<VariantPriceSuggestionDto?> GetVariantPriceSuggestionAsync(
        VariantPriceSuggestionQuery query, CancellationToken ct)
    {
        // Include del operador: si el producto todavia no se vendio, la sugerencia sale de la tarifa
        // cargada a mano y necesita el nombre del operador para armar el renglon gris.
        var rate = await _db.Rates
            .AsNoTracking()
            .Include(item => item.Supplier)
            .FirstOrDefaultAsync(item => item.PublicId == query.RatePublicId, ct);
        if (rate is null) return null;

        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var stalePriceDays = await GetStalePriceDaysAsync(ct);
        var today = DateTime.UtcNow;

        var wanted = CatalogVariant.For(
            rate.ServiceType,
            roomType: query.RoomType, mealPlan: query.MealPlan, fineName: query.RoomCategory,
            cabinClass: query.CabinClass, vehicleType: query.VehicleType);

        var sales = await _db.RateSupplierSales
            .AsNoTracking()
            // Las filas ESCONDIDAS por una union (nada se borra) NO son precios vigentes: sugerir una
            // seria ofrecerle al vendedor un precio que el tarifario ya dejo de mostrar.
            .Where(sale => sale.RateId == rate.Id && sale.AbsorbedByTidyUpActionId == null)
            .Select(sale => new
            {
                sale.SupplierId,
                SupplierPublicId = sale.Supplier != null ? (Guid?)sale.Supplier.PublicId : null,
                SupplierName = sale.Supplier != null ? sale.Supplier.Name : null,
                sale.VariantKey,
                sale.VariantLabel,
                sale.LastSoldAt,
                sale.LastNetCost,
                sale.LastSalePrice,
                sale.LastCurrency,
                sale.LastPriceUnit,
                ReservaPublicId = sale.LastReserva != null ? (Guid?)sale.LastReserva.PublicId : null,
                NumeroReserva = sale.LastReserva != null ? sale.LastReserva.NumeroReserva : null
            })
            .ToListAsync(ct);

        // Sin ninguna venta aprendida todavia, vale el precio que alguien cargo A MANO en el tarifario
        // (V16=A): para el vendedor es el mismo dato — "lo que sé que sale este producto". Si tampoco hay
        // eso, no se sugiere nada.
        if (sales.Count == 0)
        {
            return BuildSuggestionFromManualRate(rate, wanted, canSeeCost, stalePriceDays, today);
        }

        // Si el vendedor ya eligio operador, manda ese; si no, se mira todo el producto.
        var pool = supplierId.HasValue
            ? sales.Where(sale => sale.SupplierId == supplierId.Value).ToList()
            : sales;
        if (pool.Count == 0) pool = sales;

        // 1) La MISMA variante: es la unica que se puede precargar.
        // Si el vendedor TODAVIA no eligio la habitacion (clave vacia) en un tipo que SI tiene variante,
        // ningun precio puede considerarse "el de esta habitacion": precargarlo seria meterle en el
        // casillero el precio de una habitacion que el no eligio (V9=A). Se muestra como referencia.
        var variantApplies = CatalogVariant.AppliesTo(rate.ServiceType);
        var wantedIsEmpty = variantApplies && wanted.Key.Length == 0;

        var exact = wantedIsEmpty
            ? null
            : pool
                .Where(sale => string.Equals(sale.VariantKey, wanted.Key, StringComparison.Ordinal))
                .OrderByDescending(sale => sale.LastSoldAt)
                .FirstOrDefault();

        var isSameVariant = exact is not null;

        // 2) Si no hay, la mas parecida (en hotel se mide por capacidad/regimen/nombre fino; en los demas
        //    tipos alcanza con la mas reciente).
        var chosen = exact;
        if (chosen is null)
        {
            var isHotel = string.Equals(
                TextNormalizer.NormalizeForMatch(rate.ServiceType), "hotel", StringComparison.Ordinal);

            chosen = isHotel
                ? pool.OrderByDescending(sale => CatalogVariant.HotelSimilarity(wanted.Key, sale.VariantKey))
                      .ThenByDescending(sale => sale.LastSoldAt)
                      .First()
                : pool.OrderByDescending(sale => sale.LastSoldAt).First();
        }

        var price = canSeeCost ? chosen.LastNetCost : chosen.LastSalePrice;
        var supplierName = chosen.SupplierName ?? "Sin operador";
        var priceDate = chosen.LastSoldAt;

        return new VariantPriceSuggestionDto
        {
            IsSameVariant = isSameVariant,
            Price = price,
            PriceKind = canSeeCost ? "Costo" : "Venta",
            Currency = chosen.LastCurrency,
            PriceUnit = chosen.LastPriceUnit,
            PriceUnitLabel = CatalogDisplayLabels.PriceUnit(chosen.LastPriceUnit),
            VariantLabel = chosen.VariantLabel,
            SupplierPublicId = chosen.SupplierPublicId,
            SupplierName = supplierName,
            PriceDate = priceDate,
            PriceAgeText = RelativeDateText.Age(today, priceDate),
            IsOldPrice = IsOldPrice(priceDate, today, stalePriceDays),
            ReservaPublicId = chosen.ReservaPublicId,
            NumeroReserva = chosen.NumeroReserva,
            SuggestionText = BuildSuggestionText(
                isSameVariant, supplierName, chosen.VariantLabel, price, chosen.LastCurrency,
                chosen.LastPriceUnit, priceDate)
        };
    }

    /// <summary>
    /// La sugerencia cuando el producto todavía NO se vendió nunca: sale del precio que alguien cargó a
    /// mano en el tarifario, con la habitación que le pusieron en esa carga (V16=A). Devuelve null si esa
    /// tarifa no tiene precio cargado — no se inventa un cero.
    /// </summary>
    private static VariantPriceSuggestionDto? BuildSuggestionFromManualRate(
        Rate rate, (string Key, string Label) wanted, bool canSeeCost, int stalePriceDays, DateTime today)
    {
        // El alta a mano carga UN precio y lo guarda como precio de VENTA (decisión firmada 2026-08-06):
        // el costo real recién se conoce cuando se vende. Así que acá, aunque el caller pueda ver costos,
        // si no hay costo cargado se sugiere la venta y se dice que es venta — nunca un cero disfrazado
        // de costo. Sin permiso de costos, siempre la venta (F-14).
        var showCost = canSeeCost && rate.NetCost > 0m;
        var price = showCost ? rate.NetCost : rate.SalePrice;
        if (price <= 0m) return null;

        var manual = CatalogVariant.For(
            rate.ServiceType,
            roomType: rate.RoomType, mealPlan: rate.MealPlan, fineName: rate.RoomCategory,
            cabinClass: rate.CabinClass, vehicleType: rate.VehicleType);

        // Mismo criterio que arriba: sin habitacion elegida no se precarga nada.
        var wantedIsEmpty = CatalogVariant.AppliesTo(rate.ServiceType) && wanted.Key.Length == 0;
        var isSameVariant = !wantedIsEmpty
            && string.Equals(manual.Key, wanted.Key, StringComparison.Ordinal);
        var priceDate = rate.UpdatedAt ?? rate.CreatedAt;
        var supplierName = rate.Supplier?.Name ?? "Sin operador";

        return new VariantPriceSuggestionDto
        {
            IsSameVariant = isSameVariant,
            Price = price,
            PriceKind = showCost ? "Costo" : "Venta",
            Currency = rate.Currency,
            PriceUnit = rate.PriceUnit,
            PriceUnitLabel = CatalogDisplayLabels.PriceUnit(rate.PriceUnit),
            VariantLabel = manual.Label,
            SupplierPublicId = rate.Supplier?.PublicId,
            SupplierName = supplierName,
            PriceDate = priceDate,
            PriceAgeText = RelativeDateText.Age(today, priceDate),
            IsOldPrice = IsOldPrice(priceDate, today, stalePriceDays),
            ReservaPublicId = null,
            NumeroReserva = null,
            SuggestionText = BuildSuggestionText(
                isSameVariant, supplierName, manual.Label, price, rate.Currency, rate.PriceUnit, priceDate)
        };
    }

    /// <summary>
    /// El renglón gris, armado por el motor (T-13). Cuando el precio es de OTRA habitación, la frase lo
    /// dice: es la única forma de que el vendedor entienda por qué el casillero quedó vacío (V9=A).
    /// </summary>
    private static string BuildSuggestionText(
        bool isSameVariant, string supplierName, string? variantLabel, decimal price,
        string? currency, string? priceUnit, DateTime priceDate)
    {
        // El simbolo lo antepone el llamador (el helper de la casa solo formatea el numero): "US$ 48,00".
        var symbol = string.Equals(currency, Monedas.USD, StringComparison.OrdinalIgnoreCase) ? "US$" : "$";
        var amount = $"{symbol} {CurrencyDisplayFormat.Amount(price)}";
        var unit = CatalogDisplayLabels.PriceUnit(priceUnit);
        var withUnit = unit.Length > 0 ? $"{amount} {unit}" : amount;
        var when = priceDate.ToString("dd/MM/yyyy");
        var room = string.IsNullOrWhiteSpace(variantLabel) ? null : variantLabel;

        if (isSameVariant)
        {
            var pieces = new[] { supplierName, room, withUnit, when }.Where(piece => !string.IsNullOrEmpty(piece));
            return "Último precio: " + string.Join(" · ", pieces!);
        }

        // Precio de otra habitación: se dice de cuál es, y NO se precarga.
        return room is null
            ? $"Último precio de este producto: {supplierName} · {withUnit} · {when}"
            : $"No hay precio de esa habitación. El de \"{room}\" es {withUnit} ({supplierName} · {when}).";
    }

    // ============================================================
    // M-19 — el texto libre con memoria
    // ============================================================

    /// <summary>
    /// Los nombres finos de habitación (o los vehículos) que ya se escribieron alguna vez, para
    /// ofrecerlos antes de que alguien invente una variación nueva. Delega en la memoria compartida, que
    /// es la misma que usa la venta (M-19): si cada pantalla tuviera su propia lista, unificarían distinto.
    /// </summary>
    public Task<IReadOnlyList<string>> GetVariantNameSuggestionsAsync(
        string? serviceType, string? search, CancellationToken ct)
        => CatalogVariantNameMemory.SuggestAsync(_db, serviceType, search, ct);

    /// <summary>
    /// Unifica una variación de tipeo con el nombre que ya existe ("SUPERIOR" → "Superior"). Si no se
    /// parece a nada conocido, devuelve lo escrito tal cual (M-19).
    /// </summary>
    public Task<string?> ResolveVariantNameAsync(
        string? serviceType, string? writtenName, CancellationToken ct)
        => CatalogVariantNameMemory.ResolveAsync(_db, serviceType, writtenName, ct);

    // ============================================================
    // M-18 — corregir la etiqueta de una habitacion (NUNCA los importes)
    // ============================================================

    /// <summary>
    /// Corrige como se llama una habitación de un producto. Toca SOLO textos: los importes son la memoria
    /// de lo que pasó y no se editan a mano (regla firmada 2026-08-06, ratificada en §7).
    ///
    /// <para>Si al corregirla queda igual que otra habitación que ya existía, las dos se juntan solas y
    /// queda el precio MÁS NUEVO de cada operador (§7: eso no es duda grande).</para>
    /// </summary>
    public async Task<RenameVariantResult> RenameVariantAsync(RenameVariantRequest request, CancellationToken ct)
    {
        // Misma transacción con reintento que el resto de las escrituras del tarifario: dos correcciones a
        // la vez sobre el mismo producto chocan, se reintenta, y la segunda ve el estado ya corregido.
        return await RunSimpleProductTransactionAsync(async () =>
        {
            var rate = await _db.Rates.FirstOrDefaultAsync(item => item.PublicId == request.ProductPublicId, ct)
                ?? throw new KeyNotFoundException("No encontramos ese producto en el tarifario.");

            var isHotel = string.Equals(
                TextNormalizer.NormalizeForMatch(rate.ServiceType), "hotel", StringComparison.Ordinal);

            // El nombre fino (y el vehículo) pasan por la MEMORIA (M-19), igual que en el alta a mano: si
            // en la agencia ya se escribió "Superior", corregir con "sup" no inventa una habitación nueva.
            var fineName = await ResolveVariantNameAsync(rate.ServiceType, request.RoomCategory, ct);
            var vehicleName = await ResolveVariantNameAsync(rate.ServiceType, request.VehicleType, ct);

            var target = CatalogVariant.For(
                rate.ServiceType,
                roomType: request.RoomType, mealPlan: request.MealPlan, fineName: fineName,
                cabinClass: request.CabinClass, vehicleType: vehicleName);

            if (target.Key.Length == 0)
            {
                // El nombre de la variante lo pone el motor según el tipo ("la cabina", "el vehículo"):
                // "variante" es palabra nuestra, no del vendedor.
                var loQueFalta = CatalogDisplayLabels.TheVariant(rate.ServiceType);
                throw new RateValidationException(isHotel
                    ? "Elegí la habitación y el régimen."
                    : $"Elegí {(loQueFalta.Length > 0 ? loQueFalta : "el dato que falta")}.");
            }

            // Solo las VISIBLES: una fila escondida por una unión anterior no se corrige ni cuenta como
            // gemela (si la tomáramos como gemela, esconderíamos la buena contra una que ya no se ve).
            var sales = await _db.RateSupplierSales
                .Where(sale => sale.RateId == rate.Id && sale.AbsorbedByTidyUpActionId == null)
                .ToListAsync(ct);

            var toRename = sales
                .Where(sale => string.Equals(sale.VariantKey, request.CurrentVariantKey, StringComparison.Ordinal))
                .ToList();

            if (toRename.Count == 0)
            {
                throw new KeyNotFoundException("No encontramos esa habitación en el producto.");
            }

            // Nada que corregir: alguien abrió el formulario y guardó sin cambiar nada. Es el caso MÁS
            // común ahora que el formulario arranca con la habitación real ya cargada, así que ni se toca
            // una fila ni se ensucia "Ver qué ordenó" con movimientos que no movieron nada.
            var quedaIgual = string.Equals(target.Key, request.CurrentVariantKey, StringComparison.Ordinal)
                && toRename.All(sale => string.Equals(sale.VariantLabel, target.Label, StringComparison.Ordinal));
            if (quedaIgual)
            {
                return new RenameVariantResult
                {
                    ProductPublicId = rate.PublicId,
                    VariantKey = target.Key,
                    VariantLabel = target.Label,
                    MergedWithExisting = false
                };
            }

            var action = await CreateRenameTrailAsync(rate, toRename[0].VariantLabel, target, ct);
            var merged = ApplyVariantRename(sales, toRename, target, action);

            await _db.SaveChangesAsync(ct);

            return new RenameVariantResult
            {
                ProductPublicId = rate.PublicId,
                VariantKey = target.Key,
                VariantLabel = target.Label,
                MergedWithExisting = merged > 0
            };
        }, ct);
    }

    /// <summary>
    /// Abre el RASTRO de la corrección (con su Deshacer) y lo guarda para poder colgarle las fotos de cada
    /// fila que se toque. Es la misma pieza que usa el bibliotecario al unir dos productos: una sola forma
    /// de esconder con rastro en todo el tarifario.
    ///
    /// <para>Acá no hay producto absorbido — el producto nunca se apaga —, así que las dos puntas apuntan
    /// al MISMO producto y el Deshacer sabe (por el tipo de acción) que solo tiene que devolver precios.</para>
    /// </summary>
    private async Task<CatalogTidyUpAction> CreateRenameTrailAsync(
        Rate rate, string previousVariantLabel, (string Key, string Label) target, CancellationToken ct)
    {
        var productName = string.Equals(
                              TextNormalizer.NormalizeForMatch(rate.ServiceType), "hotel", StringComparison.Ordinal)
                          && !string.IsNullOrWhiteSpace(rate.HotelName)
            ? rate.HotelName!
            : rate.ProductName;

        var action = new CatalogTidyUpAction
        {
            Kind = CatalogTidyUpKinds.VariantRenamed,
            SurvivingRateId = rate.Id,
            AbsorbedRateId = rate.Id,
            SurvivingName = productName,
            AbsorbedName = previousVariantLabel,     // cómo se llamaba la habitación ANTES
            AbsorbedProductName = rate.ProductName,
            VariantLabelRescued = target.Label,      // cómo se llama ahora
            VariantKeyRescued = target.Key,
            DecidedByTheSystem = false,              // siempre la pide una persona
            PerformedByUserId = CurrentUserIdOrNull()
        };

        _db.CatalogTidyUpActions.Add(action);
        await _db.SaveChangesAsync(ct); // hace falta su Id para colgarle las fotos
        return action;
    }

    /// <summary>
    /// Aplica la corrección fila por fila. Devuelve cuántas quedaron escondidas por haber caído sobre una
    /// habitación que ya existía. <b>Nada se borra</b> (orden del dueño 2026-08-03): la que pierde queda
    /// escondida con su foto, y el Deshacer la vuelve a mostrar.
    /// </summary>
    private int ApplyVariantRename(
        List<RateSupplierSale> allVisibleSales,
        List<RateSupplierSale> toRename,
        (string Key, string Label) target,
        CatalogTidyUpAction action)
    {
        var merged = 0;

        foreach (var sale in toRename)
        {
            // La foto se saca ANTES de tocar nada: el Deshacer necesita cómo estaba, no cómo quedó.
            var previous = CatalogTidyUpTrail.Snapshot(sale, action.Id);

            // ¿Ya hay una fila VISIBLE de ese operador con la habitación NUEVA? Entonces las dos son la
            // misma habitación: se queda el precio más nuevo y la otra se esconde (§7).
            var twin = allVisibleSales.FirstOrDefault(other =>
                other.Id != sale.Id
                && other.AbsorbedByTidyUpActionId == null
                && other.SupplierId == sale.SupplierId
                && string.Equals(other.VariantKey, target.Key, StringComparison.Ordinal));

            if (twin != null)
            {
                // Foto del que se queda, SIEMPRE: aunque no se le pisen los importes, se le pisa la
                // etiqueta unas lineas mas abajo. Sin esta foto, el Deshacer no le devolvia como se
                // llamaba antes.
                CatalogTidyUpTrail.RecordOverwrite(_db, CatalogTidyUpTrail.Snapshot(twin, action.Id));

                if (sale.LastSoldAt > twin.LastSoldAt)
                {
                    twin.LastSoldAt = sale.LastSoldAt;
                    twin.LastNetCost = sale.LastNetCost;
                    twin.LastTax = sale.LastTax;
                    twin.LastSalePrice = sale.LastSalePrice;
                    twin.LastCurrency = sale.LastCurrency;
                    twin.LastPriceUnit = sale.LastPriceUnit;
                    twin.LastReservaId = sale.LastReservaId ?? twin.LastReservaId;
                }

                // El contador de ventas NO se suma: la fila perdedora sigue existiendo (escondida) con el
                // suyo. Sumarlos contaría dos veces la misma venta (mismo criterio que al unir productos).
                twin.VariantLabel = target.Label;
                CatalogTidyUpTrail.Hide(_db, sale, previous, action.Id);
                merged++;
                continue;
            }

            CatalogTidyUpTrail.RecordMove(_db, previous);
            sale.VariantKey = target.Key;
            sale.VariantLabel = target.Label;
        }

        return merged;
    }

    /// <summary>Quién está pidiendo la corrección. Null en tareas del sistema o en tests sin usuario.</summary>
    private string? CurrentUserIdOrNull()
        => _httpContextAccessor?.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}
