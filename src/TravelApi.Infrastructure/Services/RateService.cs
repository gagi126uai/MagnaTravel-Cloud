using System.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
// GetDbTransaction(): extension que saca la transaccion ADO.NET real de la transaccion de EF.
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

public partial class RateService : IRateService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RateService> _logger;
    // Fuga 1 (ADR-017 §2.7, F1b): dependencias para enmascarar costos del tarifario
    // segun el permiso cobranzas.see_cost. Opcionales (default null) para no romper
    // instancias existentes; sin ellas el masking es fail-closed (oculta el costo),
    // igual que CostMasking en BookingService.
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    // ADR-017 F1.2: lo usa SOLO catalog-search para leer el flag EnableCatalogFindOrCreate.
    // Opcional (default null) para no romper el ctor legacy de los tests de masking; si es null,
    // catalog-search se comporta fail-closed (flag OFF -> 404), igual que el resto del helper.
    private readonly IOperationalFinanceSettingsService? _settingsService;
    // Rastro de autor de los cambios a mano del tarifario (alta simple / renombrar), 2026-08-06.
    // Opcional (default null) para no romper los ctores de los tests viejos: sin el, no hay rastro, pero
    // ninguna operacion se cae.
    private readonly IAuditService? _auditService;

    // ===================================================================
    // Pieza C "tarifario que se llena solo": umbral de similitud difusa.
    //
    // pg_trgm.similarity() devuelve un numero entre 0 (nada que ver) y 1
    // (identico). 0.4 es un punto medio razonable: agarra "Sheraton" vs
    // "Sheratton" (typo) sin inundar con coincidencias casuales.
    //
    // TODO: mover a OperationalFinanceSettings (o un settings de tarifario)
    // cuando exista un lugar natural para configurarlo desde la UI de admin.
    // Por ahora es una constante para NO dispersar el numero magico por el
    // codigo: si hay que tocarlo, se toca aca y en un solo lugar.
    // ===================================================================
    private const double FuzzyMatchSimilarityThreshold = 0.4;

    // Cuantos candidatos difusos como maximo devolvemos (los mas parecidos).
    private const int FuzzyMatchLimit = 5;

    public RateService(
        AppDbContext db,
        ILogger<RateService> logger,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IOperationalFinanceSettingsService? settingsService = null,
        IAuditService? auditService = null)
    {
        _db = db;
        _logger = logger;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _settingsService = settingsService;
        _auditService = auditService;
    }

    // ===================================================================
    // Fuga 1 (ADR-017 §2.7, F1b — fix de seguridad SIN flag): el tarifario
    // devolvia NetCost/Tax/Commission a CUALQUIER usuario logueado (el
    // controller solo tiene [Authorize] de clase). El costo del proveedor
    // deja inferir el margen de la agencia, asi que se enmascara a 0m
    // (convencion de la casa, igual que CostMasking en bookings) para
    // callers sin cobranzas.see_cost. SalePrice NUNCA se enmascara (D1:
    // quien no ve costos ve el precio de venta).
    // Lo persistido en DB no se toca: solo se anula en el DTO de salida.
    // ===================================================================

    /// <summary>
    /// Anula los campos de costo de los items del tarifario si el caller no
    /// puede ver costos. Admin siempre ve (bypass dentro de CanSeeCostAsync);
    /// sin HttpContext/resolver (tests) es fail-closed: oculta.
    /// </summary>
    private async Task MaskRateListCostsAsync(IEnumerable<RateListItemDto> items, CancellationToken ct)
    {
        if (await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct)) return;

        foreach (var item in items)
        {
            item.NetCost = 0m;
            item.Tax = 0m;        // el impuesto es componente del costo: revelarlo deja inferir margen
            item.Commission = 0m; // la ganancia revela el margen directamente
        }
    }

    /// <summary>
    /// Igual que <see cref="MaskRateListCostsAsync"/> pero para los resultados de
    /// /api/rates/search (RateSearchItemDto no expone Commission, solo NetCost/Tax).
    /// </summary>
    private async Task MaskSearchCostsAsync(IEnumerable<RateSearchItemDto> items, CancellationToken ct)
    {
        if (await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct)) return;

        foreach (var item in items)
        {
            item.NetCost = 0m;
            item.Tax = 0m;
        }
    }

    /// <summary>
    /// Escribe la variante ya resuelta en cada item del tarifario. Se hace DESPUES de traer los datos
    /// porque la etiqueta se arma en C# (Postgres no sabe escribir "Doble Superior con desayuno") y
    /// porque el mismo helper tiene que dar el mismo resultado que en la venta y en el listado nuevo.
    /// </summary>
    private static void FillVariantLabels(IEnumerable<RateListItemDto> items)
    {
        foreach (var item in items)
        {
            var variant = CatalogVariant.For(
                item.ServiceType,
                roomType: item.RoomType, mealPlan: item.MealPlan, fineName: item.RoomCategory,
                cabinClass: item.CabinClass, vehicleType: item.VehicleType);

            item.VariantKey = variant.Key;
            item.VariantLabel = variant.Label;
        }
    }

    public async Task<PagedResponse<RateListItemDto>> GetAllAsync(RateListQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var ratesQuery = BuildFilteredRatesQuery(supplierId, query.ServiceType, query.ActiveOnly, query.Search);
        ratesQuery = ApplyRateOrdering(ratesQuery, query);

        var page = await ProjectRateListItems(ratesQuery)
            .ToPagedResponseAsync(query, ct);

        await MaskRateListCostsAsync(page.Items, ct);
        FillVariantLabels(page.Items);
        return page;
    }

    public async Task<PagedResponse<RateGroupDto>> GetGroupsAsync(RateGroupsQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var ratesQuery = BuildFilteredRatesQuery(supplierId, query.ServiceType, query.ActiveOnly, query.Search);
        var now = DateTime.UtcNow;

        var groupedQuery = ratesQuery
            .Select(rate => new
            {
                rate.ServiceType,
                GroupName = rate.ServiceType == "Hotel"
                    ? (rate.HotelName ?? "Hotel sin nombre")
                    : rate.ProductName,
                Subtitle = rate.ServiceType == "Hotel"
                    ? rate.City
                    : null,
                StarRating = rate.ServiceType == "Hotel"
                    ? rate.StarRating
                    : null,
                SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                rate.SalePrice,
                IsExpired = rate.ValidTo.HasValue && rate.ValidTo.Value < now
            })
            .GroupBy(rate => new
            {
                rate.ServiceType,
                rate.GroupName,
                rate.Subtitle,
                rate.StarRating,
                rate.SupplierPublicId,
                rate.SupplierName
            })
            .Select(group => new RateGroupSummary
            {
                ServiceType = group.Key.ServiceType,
                GroupName = group.Key.GroupName,
                Subtitle = group.Key.Subtitle,
                StarRating = group.Key.StarRating,
                SupplierPublicId = group.Key.SupplierPublicId,
                SupplierName = group.Key.SupplierName,
                FromPrice = group.Min(item => item.SalePrice),
                HasExpiredRates = group.Any(item => item.IsExpired),
                ItemCount = group.Count()
            });

        groupedQuery = ApplyRateGroupOrdering(groupedQuery, query);

        var totalCount = await groupedQuery.CountAsync(ct);
        var safePage = query.GetNormalizedPage();
        var safePageSize = query.GetNormalizedPageSize();

        var groups = await groupedQuery
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            group.GroupKey = BuildRateGroupKey(
                group.ServiceType,
                group.GroupName,
                group.Subtitle,
                group.SupplierName,
                group.StarRating);
        }

        if (groups.Count == 0)
        {
            return PagedResponse<RateGroupDto>.Create(Array.Empty<RateGroupDto>(), safePage, safePageSize, totalCount);
        }

        var selectedHotelNames = groups
            .Where(group => group.ServiceType == "Hotel")
            .Select(group => group.GroupName)
            .Distinct()
            .ToList();

        var selectedProductNames = groups
            .Where(group => group.ServiceType != "Hotel")
            .Select(group => group.GroupName)
            .Distinct()
            .ToList();

        var selectedGroupKeys = groups
            .Select(group => group.GroupKey)
            .ToHashSet(StringComparer.Ordinal);

        var groupItems = await ProjectRateListItems(ratesQuery)
            .Where(rate =>
                (rate.ServiceType == "Hotel" && selectedHotelNames.Contains(rate.HotelName ?? "Hotel sin nombre")) ||
                (rate.ServiceType != "Hotel" && selectedProductNames.Contains(rate.ProductName)))
            .ToListAsync(ct);

        // Fuga 1 (F1b): los items de cada grupo llevan costos -> mismo masking que GetAll.
        // FromPrice del grupo es un MIN de SalePrice, no se enmascara (D1).
        await MaskRateListCostsAsync(groupItems, ct);

        var itemsByGroup = groupItems
            .GroupBy(BuildRateGroupKey)
            .Where(group => selectedGroupKeys.Contains(group.Key))
            .ToDictionary(
                group => group.Key,
                group => OrderRateGroupItems(group).ToList() as IReadOnlyList<RateListItemDto>,
                StringComparer.Ordinal);

        var pageItems = groups
            .Select(group => new RateGroupDto
            {
                GroupKey = group.GroupKey,
                ServiceType = group.ServiceType,
                GroupName = group.GroupName,
                Subtitle = group.Subtitle,
                StarRating = group.StarRating,
                SupplierPublicId = group.SupplierPublicId,
                SupplierName = group.SupplierName,
                FromPrice = group.FromPrice,
                HasExpiredRates = group.HasExpiredRates,
                ItemCount = group.ItemCount,
                Items = itemsByGroup.TryGetValue(group.GroupKey, out var items)
                    ? items
                    : Array.Empty<RateListItemDto>()
            })
            .ToList();

        return PagedResponse<RateGroupDto>.Create(pageItems, safePage, safePageSize, totalCount);
    }

    public async Task<PagedResponse<HotelRateGroupDto>> GetHotelGroupsAsync(HotelRateGroupsQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var hotelRatesQuery = BuildFilteredRatesQuery(supplierId, "Hotel", query.ActiveOnly, query.Search);
        var now = DateTime.UtcNow;

        var groupedQuery = hotelRatesQuery
            .Select(rate => new
            {
                HotelName = rate.HotelName ?? "Hotel sin nombre",
                rate.City,
                rate.StarRating,
                SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                rate.SalePrice,
                IsExpired = rate.ValidTo.HasValue && rate.ValidTo.Value < now
            })
            .GroupBy(rate => new
            {
                rate.HotelName,
                rate.City,
                rate.StarRating,
                rate.SupplierPublicId,
                rate.SupplierName
            })
            .Select(group => new HotelRateGroupSummary
            {
                HotelName = group.Key.HotelName,
                City = group.Key.City,
                StarRating = group.Key.StarRating,
                SupplierPublicId = group.Key.SupplierPublicId,
                SupplierName = group.Key.SupplierName,
                FromPrice = group.Min(item => item.SalePrice),
                HasExpiredRates = group.Any(item => item.IsExpired),
                RoomCount = group.Count()
            });

        groupedQuery = ApplyHotelGroupOrdering(groupedQuery, query);

        var totalCount = await groupedQuery.CountAsync(ct);
        var safePage = query.GetNormalizedPage();
        var safePageSize = query.GetNormalizedPageSize();

        var groups = await groupedQuery
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            group.GroupKey = BuildHotelGroupKey(group.HotelName, group.City, group.SupplierName, group.StarRating);
        }

        if (!groups.Any())
        {
            return PagedResponse<HotelRateGroupDto>.Create(Array.Empty<HotelRateGroupDto>(), safePage, safePageSize, totalCount);
        }

        var selectedHotelNames = groups.Select(group => group.HotelName).Distinct().ToList();
        var hotelItems = await ProjectRateListItems(hotelRatesQuery)
            .Where(rate => selectedHotelNames.Contains(rate.HotelName ?? "Hotel sin nombre"))
            .OrderBy(rate => rate.HotelName)
            .ThenBy(rate => rate.RoomType)
            .ThenBy(rate => rate.RoomCategory)
            .ThenBy(rate => rate.SalePrice)
            .ToListAsync(ct);

        // Fuga 1 (F1b): mismo masking de costos que GetAll para los items de cada hotel.
        await MaskRateListCostsAsync(hotelItems, ct);

        var itemsByGroup = hotelItems
            .GroupBy(rate => BuildHotelGroupKey(rate.HotelName, rate.City, rate.SupplierName, rate.StarRating))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RateListItemDto>)group.ToList());

        var pageItems = groups
            .Select(group => new HotelRateGroupDto
            {
                GroupKey = group.GroupKey,
                HotelName = group.HotelName,
                City = group.City,
                StarRating = group.StarRating,
                SupplierPublicId = group.SupplierPublicId,
                SupplierName = group.SupplierName,
                FromPrice = group.FromPrice,
                HasExpiredRates = group.HasExpiredRates,
                RoomCount = group.RoomCount,
                Items = itemsByGroup.TryGetValue(group.GroupKey, out var groupItems)
                    ? groupItems
                    : Array.Empty<RateListItemDto>()
            })
            .ToList();

        return PagedResponse<HotelRateGroupDto>.Create(pageItems, safePage, safePageSize, totalCount);
    }

    public async Task<RateSummaryDto> GetSummaryAsync(RateSummaryQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var filteredRates = BuildFilteredRatesQuery(supplierId, query.ServiceType, query.ActiveOnly, query.Search);
        var now = DateTime.UtcNow;

        var rates = await filteredRates
            .Select(rate => new
            {
                rate.ServiceType,
                rate.HotelName,
                rate.City,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                rate.StarRating,
                IsExpired = rate.ValidTo.HasValue && rate.ValidTo.Value < now
            })
            .ToListAsync(ct);

        return new RateSummaryDto
        {
            TotalCount = rates.Count,
            AereoCount = rates.Count(rate => rate.ServiceType == "Aereo"),
            TrasladoCount = rates.Count(rate => rate.ServiceType == "Traslado"),
            PaqueteCount = rates.Count(rate => rate.ServiceType == "Paquete"),
            HotelGroupCount = rates
                .Where(rate => rate.ServiceType == "Hotel")
                .Select(rate => BuildHotelGroupKey(rate.HotelName, rate.City, rate.SupplierName, rate.StarRating))
                .Distinct()
                .Count(),
            HotelRateCount = rates.Count(rate => rate.ServiceType == "Hotel"),
            ExpiredCount = rates.Count(rate => rate.IsExpired)
        };
    }

    public async Task<RateListItemDto?> GetByIdAsync(int id, CancellationToken ct)
    {
        var item = await ProjectRateListItems(_db.Rates.AsNoTracking().Where(rate => rate.Id == id))
            .FirstOrDefaultAsync(ct);

        // Fuga 1 (F1b): tambien lo usan Create/Update/Deactivate/Reactivate para armar
        // el response, pero esos endpoints son Admin-only y Admin tiene bypass -> para
        // ellos el resultado es identico a antes.
        if (item != null)
        {
            await MaskRateListCostsAsync(new[] { item }, ct);
            FillVariantLabels(new[] { item });
        }
        return item;
    }

    public async Task<RateListItemDto?> GetByPublicIdAsync(string publicId, CancellationToken ct)
    {
        if (!Guid.TryParse(publicId, out var parsedPublicId))
        {
            return null;
        }

        var item = await ProjectRateListItems(_db.Rates.AsNoTracking().Where(rate => rate.PublicId == parsedPublicId))
            .FirstOrDefaultAsync(ct);

        // Fuga 1 (F1b): GET /api/rates/{publicId} es de cualquier logueado -> masking.
        // Las llaves NO son decorativas: si el producto no existe, item es null y las dos ayudas de abajo
        // reventarian con un error tecnico en vez de responder "no lo encontramos".
        if (item != null)
        {
            await MaskRateListCostsAsync(new[] { item }, ct);
            FillVariantLabels(new[] { item });
        }
        return item;
    }

    public async Task<IReadOnlyList<RateSearchItemDto>> SearchAsync(int? supplierId, string? serviceType, string? query, CancellationToken ct)
    {
        var ratesQuery = BuildFilteredRatesQuery(supplierId, serviceType, activeOnly: true, query);

        var results = await ratesQuery
            .OrderBy(rate => rate.ProductName)
            .ThenBy(rate => rate.HotelName)
            .Select(rate => new RateSearchItemDto
            {
                PublicId = rate.PublicId,
                ServiceType = rate.ServiceType,
                ProductName = rate.ProductName,
                Description = rate.Description,
                PriceUnit = rate.PriceUnit,
                NetCost = rate.NetCost,
                Tax = rate.Tax,
                SalePrice = rate.SalePrice,
                Currency = rate.Currency,
                SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                ValidTo = rate.ValidTo,
                Airline = rate.Airline,
                Origin = rate.Origin,
                Destination = rate.Destination,
                CabinClass = rate.CabinClass,
                HotelName = rate.HotelName,
                City = rate.City,
                StarRating = rate.StarRating,
                RoomType = rate.RoomType,
                RoomCategory = rate.RoomCategory,
                RoomFeatures = rate.RoomFeatures,
                MealPlan = rate.MealPlan,
                VehicleType = rate.VehicleType,
                IsRoundTrip = rate.IsRoundTrip,
                DurationDays = rate.DurationDays
            })
            .Take(30)
            .ToListAsync(ct);

        // Fuga 1 (F1b): /api/rates/search era la fuga original reportada (D3) — devolvia
        // NetCost/Tax crudos a cualquier logueado. SalePrice queda (D1).
        await MaskSearchCostsAsync(results, ct);
        return results;
    }

    public async Task<RateListItemDto> CreateAsync(RateDto request, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(request.SupplierId, ct);

        var rate = new Rate
        {
            SupplierId = supplierId,
            ServiceType = request.ServiceType,
            ProductName = request.ProductName,
            Description = request.Description,
            PriceUnit = request.PriceUnit ?? "servicio",
            NetCost = request.NetCost,
            Tax = request.Tax,
            SalePrice = request.SalePrice,
            Commission = request.SalePrice - request.NetCost - request.Tax,
            Currency = request.Currency ?? "USD",
            ValidFrom = request.ValidFrom,
            ValidTo = request.ValidTo,
            InternalNotes = request.InternalNotes,
            IsActive = request.IsActive,
            Airline = request.Airline,
            AirlineCode = request.AirlineCode,
            Origin = request.Origin,
            Destination = request.Destination,
            CabinClass = request.CabinClass,
            BaggageIncluded = request.BaggageIncluded,
            HotelName = request.HotelName,
            City = request.City,
            StarRating = request.StarRating,
            RoomType = request.RoomType,
            RoomCategory = request.RoomCategory,
            RoomFeatures = request.RoomFeatures,
            MealPlan = request.MealPlan,
            HotelPriceType = request.HotelPriceType ?? "base_doble",
            ChildrenPayPercent = request.ChildrenPayPercent,
            ChildMaxAge = request.ChildMaxAge,
            PickupLocation = request.PickupLocation,
            DropoffLocation = request.DropoffLocation,
            VehicleType = request.VehicleType,
            MaxPassengers = request.MaxPassengers,
            IsRoundTrip = request.IsRoundTrip,
            IncludesFlight = request.IncludesFlight,
            IncludesHotel = request.IncludesHotel,
            IncludesTransfer = request.IncludesTransfer,
            IncludesExcursions = request.IncludesExcursions,
            IncludesInsurance = request.IncludesInsurance,
            DurationDays = request.DurationDays,
            Itinerary = request.Itinerary
        };

        // P7 "evitar repetidos a toda costa" (2026-08-06): el nombre normalizado se escribe SIEMPRE, en
        // TODOS los caminos de alta. Antes solo lo escribia el alta desde la venta; un producto cargado
        // desde el formulario largo nacia sin SearchName y quedaba INVISIBLE para el buscador que aprende
        // y para el find-or-create -> el mismo hotel terminaba cargado dos veces.
        rate.SearchName = BuildSearchName(rate.ServiceType, rate.ProductName, rate.HotelName);

        _db.Rates.Add(rate);
        await _db.SaveChangesAsync(ct);

        return await GetByIdAsync(rate.Id, ct)
            ?? throw new InvalidOperationException("No se pudo cargar la tarifa creada.");
    }

    public async Task<RateListItemDto?> UpdateAsync(int id, RateDto request, CancellationToken ct)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (rate == null)
        {
            return null;
        }

        var supplierId = await ResolveOptionalSupplierIdAsync(request.SupplierId, ct);

        // ANTI-BORRADO DE COSTOS (mismo criterio que la "Fuga 3" de ADR-017 F1b en los servicios): a quien
        // NO puede ver costos, la pantalla le muestra 0 en costo e impuesto (enmascarado). Si al guardar se
        // escribiera ese 0, editar el NOMBRE de una tarifa borraria el costo real del proveedor sin que
        // nadie se entere. Por eso: sin permiso, el costo persistido se CONSERVA y la ganancia se recalcula
        // con el costo conservado y el precio de venta del request (que ese si lo puede editar).
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var netCost = canSeeCost ? request.NetCost : rate.NetCost;
        var tax = canSeeCost ? request.Tax : rate.Tax;

        rate.SupplierId = supplierId;
        rate.ServiceType = request.ServiceType;
        rate.ProductName = request.ProductName;
        rate.Description = request.Description;
        rate.PriceUnit = request.PriceUnit ?? "servicio";
        rate.NetCost = netCost;
        rate.Tax = tax;
        rate.SalePrice = request.SalePrice;
        rate.Commission = request.SalePrice - netCost - tax;
        rate.Currency = request.Currency ?? "USD";
        rate.ValidFrom = request.ValidFrom;
        rate.ValidTo = request.ValidTo;
        rate.InternalNotes = request.InternalNotes;
        rate.IsActive = request.IsActive;
        rate.UpdatedAt = DateTime.UtcNow;
        rate.Airline = request.Airline;
        rate.AirlineCode = request.AirlineCode;
        rate.Origin = request.Origin;
        rate.Destination = request.Destination;
        rate.CabinClass = request.CabinClass;
        rate.BaggageIncluded = request.BaggageIncluded;
        rate.HotelName = request.HotelName;
        rate.City = request.City;
        rate.StarRating = request.StarRating;
        rate.RoomType = request.RoomType;
        rate.RoomCategory = request.RoomCategory;
        rate.RoomFeatures = request.RoomFeatures;
        rate.MealPlan = request.MealPlan;
        rate.HotelPriceType = request.HotelPriceType ?? "base_doble";
        rate.ChildrenPayPercent = request.ChildrenPayPercent;
        rate.ChildMaxAge = request.ChildMaxAge;
        rate.PickupLocation = request.PickupLocation;
        rate.DropoffLocation = request.DropoffLocation;
        rate.VehicleType = request.VehicleType;
        rate.MaxPassengers = request.MaxPassengers;
        rate.IsRoundTrip = request.IsRoundTrip;
        rate.IncludesFlight = request.IncludesFlight;
        rate.IncludesHotel = request.IncludesHotel;
        rate.IncludesTransfer = request.IncludesTransfer;
        rate.IncludesExcursions = request.IncludesExcursions;
        rate.IncludesInsurance = request.IncludesInsurance;
        rate.DurationDays = request.DurationDays;
        rate.Itinerary = request.Itinerary;
        // Si le corrigen el nombre (o el nombre del hotel), el nombre normalizado tiene que seguirlo:
        // si no, el buscador seguiria encontrando el producto por el nombre viejo. Ver nota en CreateAsync.
        rate.SearchName = BuildSearchName(rate.ServiceType, rate.ProductName, rate.HotelName);

        await _db.SaveChangesAsync(ct);

        return await GetByIdAsync(rate.Id, ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (rate == null)
        {
            return false;
        }

        var inUse =
            await _db.Servicios.AnyAsync(s => s.RateId == id, ct) ||
            await _db.HotelBookings.AnyAsync(b => b.RateId == id, ct) ||
            await _db.TransferBookings.AnyAsync(b => b.RateId == id, ct) ||
            await _db.PackageBookings.AnyAsync(b => b.RateId == id, ct) ||
            await _db.FlightSegments.AnyAsync(f => f.RateId == id, ct) ||
            // Bloque 3: una asistencia tambien puede referenciar la tarifa; sin esto se podria
            // borrar una tarifa en uso por un seguro y dejar el snapshot de precios huerfano.
            await _db.AssistanceBookings.AnyAsync(b => b.RateId == id, ct) ||
            await _db.QuoteItems.AnyAsync(q => q.RateId == id, ct);

        if (inUse)
            throw new InvalidOperationException("No se puede eliminar la tarifa: esta en uso por reservas, bookings o cotizaciones existentes. Desactivala en su lugar.");

        _db.Rates.Remove(rate);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<RateListItemDto?> DeactivateAsync(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (rate == null)
        {
            return null;
        }

        rate.IsActive = false;
        rate.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<RateListItemDto?> ReactivateAsync(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (rate == null)
        {
            return null;
        }

        rate.IsActive = true;
        rate.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    // ===================================================================
    // Pieza C "tarifario que se llena solo": deteccion de duplicados.
    // ===================================================================

    /// <summary>
    /// Detecta tarifas existentes parecidas a la que el usuario esta por crear.
    ///
    /// Devuelve dos cosas:
    ///   - <c>ExactMatch</c>: una tarifa con la MISMA huella (mismo proveedor +
    ///     mismos campos clave segun el tipo). Si existe, casi seguro es un
    ///     duplicado real.
    ///   - <c>FuzzyMatches</c>: hasta 5 tarifas del mismo proveedor y tipo cuyo
    ///     NOMBRE se parece (no es identico). Sirve para detectar typos o
    ///     variantes de escritura ("Sheraton" vs "Sheratton").
    ///
    /// La busqueda difusa usa pg_trgm (Postgres). Si la extension no estuviera
    /// instalada, degradamos a una busqueda por substring (ILIKE) sin score real.
    /// </summary>
    public async Task<RateDuplicateCheckResponse> FindDuplicateCandidatesAsync(
        RateDuplicateCheckRequest request,
        CancellationToken ct)
    {
        // El SupplierId es obligatorio para detectar duplicados: una tarifa
        // "parecida" solo tiene sentido dentro del MISMO proveedor. Dos hoteles
        // homonimos de proveedores distintos NO son duplicados.
        var supplierId = await ResolveOptionalSupplierIdAsync(request.SupplierId, ct);
        if (!supplierId.HasValue)
        {
            // Sin proveedor no comparamos nada: devolvemos vacio en vez de tirar
            // error, asi el frontend simplemente no muestra advertencias.
            return new RateDuplicateCheckResponse();
        }

        var serviceType = request.ServiceType?.Trim() ?? string.Empty;

        // El nombre a comparar depende del tipo: Hotel compara por HotelName,
        // el resto por ProductName. El usuario lo manda ya resuelto en Name.
        var nameToMatch = request.Name ?? string.Empty;

        var exactMatch = await FindExactMatchAsync(supplierId.Value, serviceType, request, ct);
        var fuzzyMatches = await FindFuzzyMatchesAsync(
            supplierId.Value,
            serviceType,
            nameToMatch,
            excludePublicId: exactMatch?.PublicId,
            ct);

        return new RateDuplicateCheckResponse
        {
            ExactMatch = exactMatch,
            FuzzyMatches = fuzzyMatches
        };
    }

    /// <summary>
    /// Busca una tarifa con la huella EXACTA (todos los componentes clave iguales,
    /// comparados ya normalizados). La comparacion de strings normalizados
    /// (sacar tildes, minusculas, espacios) no se puede traducir a SQL, asi que
    /// traemos los candidatos del proveedor+tipo (un set chico) y comparamos en
    /// memoria con <see cref="TextNormalizer"/>.
    /// </summary>
    private async Task<RateDuplicateExactDto?> FindExactMatchAsync(
        int supplierId,
        string serviceType,
        RateDuplicateCheckRequest request,
        CancellationToken ct)
    {
        // Traemos solo las tarifas activas del mismo proveedor y tipo. Este set
        // es chico (un proveedor no tiene miles de tarifas del mismo tipo), por
        // eso es seguro materializarlo y comparar en memoria.
        var candidates = await _db.Rates
            .AsNoTracking()
            .Where(rate => rate.SupplierId == supplierId
                && rate.ServiceType == serviceType
                && rate.IsActive)
            .ToListAsync(ct);

        var fingerprint = request.Fingerprint;

        // Componentes esperados de la huella, ya normalizados, segun el tipo.
        // Comparamos cada candidato contra estos.
        foreach (var candidate in candidates)
        {
            if (IsExactHuellaMatch(candidate, serviceType, request.Name, fingerprint))
            {
                return new RateDuplicateExactDto
                {
                    PublicId = candidate.PublicId,
                    ProductName = candidate.ProductName,
                    HotelName = candidate.HotelName,
                    SalePrice = candidate.SalePrice,
                    NetCost = candidate.NetCost,
                    Currency = candidate.Currency
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Decide si una tarifa candidata tiene la MISMA huella que lo que el usuario
    /// esta por crear. La huella cambia segun el tipo de servicio (un hotel se
    /// identifica por habitacion+pension, un vuelo por origen+destino+aerolinea).
    /// Todos los componentes se comparan ya normalizados.
    /// </summary>
    private static bool IsExactHuellaMatch(
        Rate candidate,
        string serviceType,
        string? name,
        RateDuplicateFingerprint fingerprint)
    {
        // Normalizamos el tipo para que "hotel" y "Hotel" caigan en la misma rama.
        var typeKey = TextNormalizer.NormalizeForMatch(serviceType);

        return typeKey switch
        {
            // Hotel: proveedor (ya filtrado) + nombre del hotel + habitacion +
            // pension + categoria de habitacion. OJO: dos cuartos distintos del
            // mismo hotel (distinto RoomType) NO son duplicado.
            "hotel" =>
                Matches(candidate.HotelName, name)
                && Matches(candidate.RoomType, fingerprint.RoomType)
                && Matches(candidate.MealPlan, fingerprint.MealPlan)
                && Matches(candidate.RoomCategory, fingerprint.RoomCategory),

            // Traslado: origen + destino + tipo de vehiculo + ida-y-vuelta.
            "traslado" =>
                Matches(candidate.PickupLocation, fingerprint.PickupLocation)
                && Matches(candidate.DropoffLocation, fingerprint.DropoffLocation)
                && Matches(candidate.VehicleType, fingerprint.VehicleType)
                && candidate.IsRoundTrip == fingerprint.IsRoundTrip,

            // Vuelo: origen + destino + aerolinea.
            "aereo" =>
                Matches(candidate.Origin, fingerprint.Origin)
                && Matches(candidate.Destination, fingerprint.Destination)
                && Matches(candidate.Airline, fingerprint.Airline),

            // Asistencia, Paquete y cualquier otro tipo: alcanza con el nombre del
            // producto (mismo proveedor ya filtrado). Es el caso mas simple.
            _ => Matches(candidate.ProductName, name)
        };
    }

    /// <summary>Compara dos textos normalizandolos a ambos (regla de oro del matching).</summary>
    private static bool Matches(string? left, string? right)
    {
        return string.Equals(
            TextNormalizer.NormalizeForMatch(left),
            TextNormalizer.NormalizeForMatch(right),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Busca tarifas con NOMBRE parecido (no identico) dentro del mismo proveedor
    /// y tipo. Usa el operador <c>%</c> de pg_trgm (que aprovecha el indice GIN
    /// trigram) y devuelve el score de <c>similarity()</c> ordenado de mayor a
    /// menor. Si pg_trgm no esta instalada, cae al fallback ILIKE.
    /// </summary>
    private async Task<IReadOnlyList<RateDuplicateFuzzyDto>> FindFuzzyMatchesAsync(
        int supplierId,
        string serviceType,
        string nameToMatch,
        Guid? excludePublicId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nameToMatch))
        {
            return Array.Empty<RateDuplicateFuzzyDto>();
        }

        // El campo sobre el que comparamos depende del tipo: Hotel mira HotelName,
        // el resto ProductName. Como el nombre de columna no se puede parametrizar
        // (es identificador, no valor), lo elegimos de una lista BLANCA fija; nunca
        // viene del usuario, asi que no hay riesgo de inyeccion.
        var isHotel = string.Equals(
            TextNormalizer.NormalizeForMatch(serviceType), "hotel", StringComparison.Ordinal);
        var matchColumn = isHotel ? "HotelName" : "ProductName";

        try
        {
            return await RunTrigramFuzzyQueryAsync(
                supplierId, serviceType, nameToMatch, matchColumn, excludePublicId, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedFunction)
        {
            // pg_trgm no esta instalada: el operador % y similarity() no existen,
            // Postgres tira 42883 (undefined_function). Degradamos a una busqueda
            // por substring (ILIKE) para no dejar al usuario sin ninguna ayuda.
            // El score se reporta 0 porque ILIKE no mide cuan parecido es.
            _logger.LogWarning(
                "pg_trgm no disponible al detectar duplicados de tarifas; usando fallback ILIKE. " +
                "Verificar la extension en el servidor (SELECT * FROM pg_available_extensions WHERE name='pg_trgm').");

            return await RunIlikeFallbackQueryAsync(
                supplierId, serviceType, nameToMatch, matchColumn, excludePublicId, ct);
        }
    }

    /// <summary>
    /// Query difusa real con pg_trgm. Usa SQL crudo PARAMETRIZADO (los valores van
    /// como parametros de Npgsql, nunca interpolados en el string) para evitar
    /// inyeccion. El nombre de columna sale de una lista blanca fija, no del usuario.
    /// </summary>
    private async Task<IReadOnlyList<RateDuplicateFuzzyDto>> RunTrigramFuzzyQueryAsync(
        int supplierId,
        string serviceType,
        string nameToMatch,
        string matchColumn,
        Guid? excludePublicId,
        CancellationToken ct)
    {
        // El WHERE usa `lower(col) % lower(@name)` para que pegue contra el indice
        // GIN trigram, y `similarity(...) >= @threshold` para cortar por umbral.
        // Excluimos el match exacto (si lo hubo) para no listarlo dos veces.
        var sql = $@"
            SELECT ""PublicId"", ""ProductName"", ""HotelName"", ""SalePrice"", ""NetCost"", ""Currency"",
                   similarity(lower(""{matchColumn}""), lower(@name)) AS score
            FROM ""Rates""
            WHERE ""SupplierId"" = @supplierId
              AND ""ServiceType"" = @serviceType
              AND ""IsActive"" = TRUE
              AND ""{matchColumn}"" IS NOT NULL
              AND lower(""{matchColumn}"") % lower(@name)
              AND similarity(lower(""{matchColumn}""), lower(@name)) >= @threshold
              AND (@excludePublicId IS NULL OR ""PublicId"" <> @excludePublicId)
            ORDER BY score DESC
            LIMIT @limit;";

        await using var command = CreateRatesCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("name", nameToMatch));
        command.Parameters.Add(new NpgsqlParameter("supplierId", supplierId));
        command.Parameters.Add(new NpgsqlParameter("serviceType", serviceType));
        command.Parameters.Add(new NpgsqlParameter("threshold", FuzzyMatchSimilarityThreshold));
        command.Parameters.Add(new NpgsqlParameter("limit", FuzzyMatchLimit));
        command.Parameters.Add(new NpgsqlParameter("excludePublicId", (object?)excludePublicId ?? DBNull.Value)
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid
        });

        return await ReadFuzzyMatchesAsync(command, hasRealScore: true, ct);
    }

    /// <summary>
    /// Fallback cuando pg_trgm no esta: busca por substring case-insensitive
    /// (ILIKE '%...%'), igual que el patron de busqueda de
    /// <see cref="BuildFilteredRatesQuery"/>. No hay score real, asi que devolvemos 0.
    /// </summary>
    private async Task<IReadOnlyList<RateDuplicateFuzzyDto>> RunIlikeFallbackQueryAsync(
        int supplierId,
        string serviceType,
        string nameToMatch,
        string matchColumn,
        Guid? excludePublicId,
        CancellationToken ct)
    {
        var sql = $@"
            SELECT ""PublicId"", ""ProductName"", ""HotelName"", ""SalePrice"", ""NetCost"", ""Currency"",
                   0.0 AS score
            FROM ""Rates""
            WHERE ""SupplierId"" = @supplierId
              AND ""ServiceType"" = @serviceType
              AND ""IsActive"" = TRUE
              AND ""{matchColumn}"" IS NOT NULL
              AND ""{matchColumn}"" ILIKE @pattern
              AND (@excludePublicId IS NULL OR ""PublicId"" <> @excludePublicId)
            ORDER BY ""{matchColumn}""
            LIMIT @limit;";

        await using var command = CreateRatesCommand(sql);
        // El % del LIKE se arma como VALOR del parametro, no se concatena en el SQL.
        command.Parameters.Add(new NpgsqlParameter("pattern", $"%{nameToMatch}%"));
        command.Parameters.Add(new NpgsqlParameter("supplierId", supplierId));
        command.Parameters.Add(new NpgsqlParameter("serviceType", serviceType));
        command.Parameters.Add(new NpgsqlParameter("limit", FuzzyMatchLimit));
        command.Parameters.Add(new NpgsqlParameter("excludePublicId", (object?)excludePublicId ?? DBNull.Value)
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid
        });

        return await ReadFuzzyMatchesAsync(command, hasRealScore: false, ct);
    }

    /// <summary>
    /// Crea un comando sobre la conexion del DbContext, abriendola si hace falta.
    /// Reutiliza la conexion de EF para respetar la misma transaccion/config.
    ///
    /// <para><b>Por que se engancha la transaccion en curso</b>: si el DbContext esta adentro de una
    /// transaccion (el alta simple corre en una Serializable), un comando crudo SIN
    /// <c>command.Transaction</c> corre FUERA de ella — Npgsql lo rechaza o lo ejecuta en otro contexto, y
    /// en el mejor caso el freno de repetidos leeria un estado distinto del que la transaccion esta por
    /// escribir. Con esta linea, la busqueda difusa ve exactamente lo mismo que el resto de la operacion.
    /// Si no hay transaccion abierta queda null, que es el comportamiento normal de siempre.</para>
    /// </summary>
    private NpgsqlCommand CreateRatesCommand(string sql)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        return command;
    }

    /// <summary>
    /// Ejecuta el comando y mapea cada fila a <see cref="RateDuplicateFuzzyDto"/>.
    /// Abre la conexion si estaba cerrada y la deja como estaba al terminar.
    /// </summary>
    private static async Task<IReadOnlyList<RateDuplicateFuzzyDto>> ReadFuzzyMatchesAsync(
        NpgsqlCommand command,
        bool hasRealScore,
        CancellationToken ct)
    {
        var connection = command.Connection!;
        var connectionWasClosed = connection.State == ConnectionState.Closed;
        if (connectionWasClosed)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            var results = new List<RateDuplicateFuzzyDto>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new RateDuplicateFuzzyDto
                {
                    PublicId = reader.GetGuid(0),
                    ProductName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    HotelName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    SalePrice = reader.GetDecimal(3),
                    NetCost = reader.GetDecimal(4),
                    Currency = reader.IsDBNull(5) ? null : reader.GetString(5),
                    // En el fallback ILIKE el score es 0 (no hay medida real).
                    Score = hasRealScore ? Convert.ToDouble(reader.GetValue(6)) : 0d
                });
            }

            return results;
        }
        finally
        {
            if (connectionWasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    // ===================================================================
    // ADR-017 F1.2 (catalogo find-or-create, buscador): catalog-search.
    //
    // Buscador difuso UNIFICADO y supplier-AGNOSTICO que usa el vendedor al
    // cargar un servicio. A diferencia del duplicate-check (que es por-proveedor
    // y de back-office), aca el PRODUCTO manda: se busca "el hotel", no "la tarifa
    // del proveedor X". Deduplica las N tarifas legacy del mismo producto en un
    // solo resultado y le cuelga el contexto de la "ultima vez" que se vendio.
    //
    // MEJORA 2026-08-10 (spec firmada, regla 3 del preambulo de la Constitucion;
    // T-11: sin llaves; P-21: los resultados son SUGERENCIAS, nunca deciden por el
    // vendedor). Tres cambios de fondo sobre el buscador original:
    //
    //   1. PALABRA POR PALABRA. Antes se medía el parecido del texto ENTERO contra el
    //      nombre entero, con un piso de 0.4. Eso hacía que "sheraton" NO encontrara
    //      "sheraton buenos aires hotel & convention center" (el nombre largo diluye el
    //      parecido a ~0.25). Ahora cada palabra escrita se busca por separado — en el
    //      nombre, en la ciudad, en el nombre del hotel y (en memoria) en los operadores.
    //   2. EL TIPO YA NO ES UN FILTRO, ES UNA PREFERENCIA. Antes, sin tipo no había
    //      resultados y con tipo no se veía nada de los otros. Ahora se busca en los 5
    //      tipos de la ficha y el tipo de la solapa donde está parado el vendedor solo
    //      EMPUJA sus productos arriba en la lista.
    //   3. TODAS LAS PALABRAS MEJOR QUE ALGUNAS. Si algún producto cubre TODO lo que se
    //      escribió (ej. "sheraton ola" = nombre + operador), se muestran solo esos; si
    //      ninguno cubre todo, se muestran los que cubren más primero (degradé), para no
    //      dejar al vendedor con la lista vacía.
    //
    // SOLO LECTURA: no crea ni escribe nada (la creacion inline y el upsert son F1.3).
    // ===================================================================

    // Cuantos resultados finales muestra el dropdown (ADR §2.3.a: hasta 8).
    private const int CatalogSearchResultLimit = 8;

    // Minimo de caracteres utiles de q para que el buscador haga algo (ADR §2.3.a: 2).
    private const int CatalogSearchMinQueryLength = 2;

    // Cuantos candidatos crudos traemos ANTES de deduplicar. Mas grande que el limite final
    // porque un mismo producto puede tener N tarifas legacy (room types / proveedores) que
    // colapsan a un solo resultado; si trajeramos solo 8 crudos, el dedupe podria dejar el
    // dropdown casi vacio. A escala single-tenant (pocos miles de Rates) es barato.
    // 2026-08-10: sube de 50 a 120 porque ahora la busqueda cruza los 5 tipos y recolecta
    // por palabra suelta (trae mas ruido a proposito); el filtro fino se hace en memoria.
    private const int CatalogSearchCandidateFetchLimit = 120;

    // ===================================================================
    // Piso de parecido POR PALABRA (word_similarity de pg_trgm), 2026-08-10.
    //
    // Ojo: NO es el mismo numero que FuzzyMatchSimilarityThreshold (0.4), que compara
    // textos ENTEROS y lo usan el duplicate-check y el alta a mano. Este compara UNA
    // palabra escrita contra el mejor pedazo del nombre del producto, asi que vive en
    // otra escala: 0.45 agarra "sheratom" -> "sheraton buenos aires" sin traer cualquier
    // cosa. Tocar uno NO debe tocar el otro.
    //
    // Importante — correccion del comentario anterior (C-3a, review 2026-08-1x): los brazos de
    // TEXTO ENTERO ("SearchName" % @q y @q <% "SearchName", en RunCatalogTrigramQueryAsync) SI
    // dependen de los GUC del servidor (pg_trgm.similarity_threshold / word_similarity_threshold
    // respectivamente): si alguien los sube, esos dos brazos pueden dejar de traer un candidato.
    // Pero son solo RECALL EXTRA — le suman candidatos aprovechando el indice GIN (mas rapido que
    // recorrer toda la tabla), nada mas. Los brazos AUTORITATIVOS son los POR-PALABRA de mas abajo
    // (uno por token, con la comparacion EXPLICITA >= @wordThreshold): esos NO dependen de ningun
    // GUC y son los que de verdad deciden si un producto entra. Si el servidor tuviera el GUC en un
    // valor raro, el peor caso es una consulta un poco mas lenta (sin el atajo del indice), nunca
    // un resultado que falte.
    // ===================================================================
    private const double CatalogSearchWordSimilarityThreshold = 0.45;

    // Puntaje que se le pone a un producto que cubre TODAS las palabras escritas, tal cual
    // (sin necesidad de perdonar errores de tipeo). 0.7 esta por ENCIMA del 0.65 con el que
    // la ficha resalta "el mas parecido" y con el que decide no molestar al motor de IA:
    // si el vendedor escribio todo bien y hay un producto que lo tiene todo, es EL producto.
    private const double CatalogSearchFullCoverageExactScore = 0.7;

    // Cubre todas las palabras pero perdonandole algun error de tipeo: es un buen candidato,
    // pero NO lo suficientemente seguro como para apagar la ayuda extra (queda bajo 0.65).
    private const double CatalogSearchFullCoverageFuzzyScore = 0.55;

    // Techo del score de Postgres (word_similarity/similarity) cuando el producto NO cubre todas
    // las palabras escritas (fix C-2, review 2026-08-1x). word_similarity() compara la palabra
    // contra el MEJOR PEDAZO del nombre, no contra el nombre entero: con textos cortos eso infla el
    // numero contra un nombre largo que en realidad solo comparte UNA de las palabras escritas
    // ("sheraton eze" da ~0.69 contra "Sheraton Buenos Aires Hotel & Convention Center", que ni
    // menciona "eze"). Sin este techo ese score cruzaba el 0.65 con el que la ficha resalta "el mas
    // parecido" y decide NO ofrecer el matcher anti-duplicados (P7) — quedaba pareciendo un
    // resultado fuerte cuando en realidad falta la mitad de lo escrito. 0.64 esta justo ABAJO del
    // corte de siempre.
    private const double CatalogSearchPartialCoverageScoreCap = 0.64;

    // Cuanto empuja hacia arriba el tipo de la solapa donde esta parado el vendedor. Es un
    // desempate de ORDEN, no un puntaje: el Score que viaja en el DTO queda limpio (si el
    // empujon se sumara al Score, un producto flojo del tipo correcto se resaltaria como
    // "el mas parecido" en la ficha solo por estar en la solapa).
    private const double CatalogSearchPreferredTypeBoost = 0.05;

    // Los 5 tipos que tienen solapa en la ficha de carga. La busqueda cruza estos y solo
    // estos: "Excursion" y "Otro" no se cargan desde este buscador.
    private static readonly string[] CatalogSearchServiceTypes =
    {
        CatalogServiceTypes.Hotel,
        CatalogServiceTypes.Aereo,
        CatalogServiceTypes.Traslado,
        CatalogServiceTypes.Paquete,
        CatalogServiceTypes.Asistencia
    };

    public async Task<IReadOnlyList<CatalogSearchItemDto>> CatalogSearchAsync(
        string? serviceType, string? query, CancellationToken ct)
    {
        // (2026-08-06, spec firmada P8=A) Ya no hay llave: el buscador que aprende de las ventas esta
        // disponible para todos. Antes este metodo devolvia null con la llave apagada y el controller
        // respondia 404 "como si el endpoint no existiera"; eso murio.

        // 1. Validacion de entrada. Normalizamos q con la MISMA funcion que escribe SearchName
        //    (NormalizeForCatalog), para que "Maitei" / "MAITEI " / "maitei--" se comporten igual
        //    (cierra la nota NB-1 de los reviewers de F1.1). q con menos de 2 chars utiles -> lista
        //    vacia (no es un error: todavia no hay nada que buscar).
        //    El tipo de servicio YA NO filtra (2026-08-10): es el tipo PREFERIDO, y puede venir vacio.
        var preferredServiceType = TextNormalizer.NormalizeForCatalog(serviceType);
        var normalizedQuery = TextNormalizer.NormalizeForCatalog(query);
        if (normalizedQuery.Length < CatalogSearchMinQueryLength)
        {
            return Array.Empty<CatalogSearchItemDto>();
        }

        // Las palabras sueltas que escribio el vendedor. Puede quedar vacio (ej. escribio "de la"):
        // en ese caso el buscador se comporta como antes, midiendo el parecido del texto entero.
        var tokens = CatalogSearchTokens.Tokenize(normalizedQuery);

        // 2. Candidatos crudos (RateId + score). En Postgres usa pg_trgm; en motores no
        //    relacionales (tests InMemory) cae a un fallback en memoria por substring.
        var candidates = await FetchCatalogCandidatesAsync(normalizedQuery, tokens, ct);
        if (candidates.Count == 0)
        {
            return Array.Empty<CatalogSearchItemDto>();
        }

        // 3. Detalle de cada Rate candidato + su ultima venta (RateSupplierSale) + sus operadores.
        var scoreByRateId = candidates
            .GroupBy(candidate => candidate.RateId)
            .ToDictionary(group => group.Key, group => group.Max(candidate => candidate.Score));
        var rateIds = scoreByRateId.Keys.ToList();

        var rates = await LoadCandidateRatesAsync(rateIds, ct);
        var latestSaleByRateId = await LoadLatestSalesAsync(rateIds, ct);
        var supplierNamesByRateId = await LoadSupplierNamesAsync(rateIds, ct);

        // 4. Enmascarado de costo: lo resolvemos UNA vez (mismo resolver que F1b / CostMasking).
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);

        // 5. Filtro FINO en memoria: cuantas de las palabras escritas cubre cada producto. Se hace
        //    aca (y no en SQL) porque el nombre del operador no vive en la fila del Rate y porque
        //    sobre 120 candidatos es instantaneo.
        var matches = new List<CatalogMatch>(rates.Count);
        foreach (var rate in rates)
        {
            var item = BuildCatalogSearchItem(
                rate,
                scoreByRateId.TryGetValue(rate.Id, out var score) ? score : null,
                latestSaleByRateId.TryGetValue(rate.Id, out var sale) ? sale : null,
                canSeeCost);

            supplierNamesByRateId.TryGetValue(rate.Id, out var supplierNames);
            var haystacks = BuildCatalogHaystacks(item, supplierNames);

            matches.Add(new CatalogMatch(
                item,
                MatchedTokens: CatalogSearchTokens.MatchedTokenCount(tokens, haystacks),
                ExactMatchedTokens: CatalogSearchTokens.ExactMatchedTokenCount(tokens, haystacks)));
        }

        var refined = KeepBestTokenCoverage(matches, tokens.Count);
        ApplyCoverageScores(refined, tokens.Count);

        // 6. Dedupe (ADR §2.4 / m1): el mismo producto cargado N veces aparece UNA vez. Para Hotel
        //    la clave incluye la City normalizada (homonimos de 2 ciudades = 2 productos distintos).
        var deduped = DedupeCatalogItems(refined);

        // 7. Orden final: primero el que cubre mas palabras; despues el mas parecido (con el empujon
        //    del tipo de la solapa); a igual todo, la venta mas reciente.
        return SortCatalogResults(deduped, preferredServiceType);
    }

    /// <summary>
    /// Trae los candidatos crudos (RateId + score). En Postgres usa pg_trgm; si la extension no
    /// estuviera, cae al fallback ILIKE; en motores no relacionales (tests InMemory) usa un fallback
    /// en memoria por substring. Los TRES caminos devuelven lo mismo (ids + score opcional) y siguen
    /// despues por el MISMO filtro fino, para que lo que prueban los tests sea la logica real.
    /// </summary>
    private async Task<IReadOnlyList<CatalogCandidate>> FetchCatalogCandidatesAsync(
        string normalizedQuery, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            return await FetchCatalogCandidatesInMemoryAsync(normalizedQuery, tokens, ct);
        }

        try
        {
            return await RunCatalogTrigramQueryAsync(normalizedQuery, tokens, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedFunction)
        {
            // pg_trgm no instalada (42883): mismo criterio degradado que el duplicate-check.
            _logger.LogWarning(
                "pg_trgm no disponible en catalog-search; usando fallback ILIKE. " +
                "Verificar la extension en el servidor (pg_trgm).");
            return await RunCatalogIlikeFallbackAsync(normalizedQuery, tokens, ct);
        }
    }

    /// <summary>
    /// La pesca ANCHA contra Postgres: trae todo lo que se parezca, aunque sea de lejos. El recorte
    /// fino lo hace despues el filtro en memoria — es preferible traer 120 y descartar que perderse
    /// el hotel correcto.
    ///
    /// <para><b>Como pesca</b>: por el texto entero (operadores <c>%</c> y <c>&lt;%</c>, que usan el
    /// indice GIN de <c>SearchName</c>) Y ademas por CADA palabra escrita, contra el nombre, el
    /// nombre del hotel y la ciudad. El puntaje es el mejor de cuatro medidas: parecido del texto
    /// entero y parecido del mejor pedazo (<c>word_similarity</c>), contra <c>SearchName</c> y contra
    /// <c>HotelName</c> — asi "sheraton" saca puntaje alto contra "sheraton buenos aires hotel &amp;
    /// convention center", que con la medida vieja daba ~0.25 y quedaba afuera.</para>
    ///
    /// <para><b>Seguridad</b>: SQL crudo pero 100% PARAMETRIZADO. Lo unico que se interpola en el
    /// texto del SQL son los NOMBRES de parametro (<c>@t0</c>, <c>@p0</c>...), armados con un
    /// contador del propio codigo; ningun caracter escrito por el usuario entra al SQL. El tope de 6
    /// palabras (<see cref="CatalogSearchTokens.MaxTokens"/>) acota cuanto puede crecer la consulta.</para>
    ///
    /// <para><b>Costo</b>: los brazos por palabra sobre <c>HotelName</c>/<c>City</c> no tienen indice
    /// trigram, asi que esta consulta puede recorrer la tabla entera de tarifas. A la escala de una
    /// agencia (miles de filas) es barato; si el tarifario creciera un orden de magnitud, el paso
    /// siguiente es un indice GIN sobre esas dos columnas.</para>
    /// </summary>
    private async Task<IReadOnlyList<CatalogCandidate>> RunCatalogTrigramQueryAsync(
        string normalizedQuery, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        // Un brazo por palabra: aparece como pedazo del nombre/hotel/ciudad, o se parece bastante a
        // algun tramo del nombre (word_similarity tolera el error de tipeo).
        var tokenArms = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            tokenArms.Add($@"(
                    ""SearchName"" ILIKE @p{index}
                    OR lower(coalesce(""HotelName"", '')) ILIKE @p{index}
                    OR lower(coalesce(""City"", '')) ILIKE @p{index}
                    OR word_similarity(@t{index}, ""SearchName"") >= @wordThreshold
                )");
        }
        var tokenClause = tokenArms.Count == 0
            ? string.Empty
            : " OR " + string.Join(" OR ", tokenArms);

        // SearchName ya esta normalizado en DB (lower + sin tildes), por eso se compara directo
        // contra @q (que tambien paso por NormalizeForCatalog). HotelName/City son crudos -> lower(...).
        var sql = $@"
            SELECT ""Id"",
                   GREATEST(
                       word_similarity(@q, ""SearchName""),
                       similarity(""SearchName"", @q),
                       word_similarity(@q, lower(coalesce(""HotelName"", ''))),
                       similarity(lower(coalesce(""HotelName"", '')), @q)
                   ) AS score
            FROM ""Rates""
            WHERE ""IsActive"" = TRUE
              AND ""SearchName"" IS NOT NULL
              AND ""ServiceType"" = ANY(@types)
              AND (
                (""SearchName"" % @q)
                OR (@q <% ""SearchName"")
                {tokenClause}
              )
            ORDER BY score DESC
            LIMIT @limit;";

        await using var command = CreateRatesCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("q", normalizedQuery));
        command.Parameters.Add(new NpgsqlParameter("types", CatalogSearchServiceTypes));
        command.Parameters.Add(new NpgsqlParameter("wordThreshold", CatalogSearchWordSimilarityThreshold));
        command.Parameters.Add(new NpgsqlParameter("limit", CatalogSearchCandidateFetchLimit));
        AddCatalogTokenParameters(command, tokens);

        return await ReadCatalogCandidatesAsync(command, hasRealScore: true, ct);
    }

    /// <summary>
    /// Fallback cuando pg_trgm no esta: substring case-insensitive (ILIKE), por el texto entero y por
    /// cada palabra. Sin score real (null): el puntaje sale despues de la cobertura de palabras.
    /// </summary>
    private async Task<IReadOnlyList<CatalogCandidate>> RunCatalogIlikeFallbackAsync(
        string normalizedQuery, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var tokenArms = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            tokenArms.Add($@"(
                    ""SearchName"" ILIKE @p{index}
                    OR lower(coalesce(""HotelName"", '')) ILIKE @p{index}
                    OR lower(coalesce(""City"", '')) ILIKE @p{index}
                )");
        }
        var tokenClause = tokenArms.Count == 0
            ? string.Empty
            : " OR " + string.Join(" OR ", tokenArms);

        var sql = $@"
            SELECT ""Id"", NULL::real AS score
            FROM ""Rates""
            WHERE ""IsActive"" = TRUE
              AND ""SearchName"" IS NOT NULL
              AND ""ServiceType"" = ANY(@types)
              AND (
                ""SearchName"" ILIKE @pattern
                {tokenClause}
              )
            ORDER BY ""SearchName""
            LIMIT @limit;";

        await using var command = CreateRatesCommand(sql);
        // El % del LIKE se arma como VALOR del parametro, no se concatena en el SQL.
        command.Parameters.Add(new NpgsqlParameter("pattern", $"%{normalizedQuery}%"));
        command.Parameters.Add(new NpgsqlParameter("types", CatalogSearchServiceTypes));
        command.Parameters.Add(new NpgsqlParameter("limit", CatalogSearchCandidateFetchLimit));
        AddCatalogTokenParameters(command, tokens);

        return await ReadCatalogCandidatesAsync(command, hasRealScore: false, ct);
    }

    /// <summary>
    /// Agrega los parametros de cada palabra: <c>@t{i}</c> la palabra tal cual (para
    /// <c>word_similarity</c>) y <c>@p{i}</c> el patron <c>%palabra%</c> (para ILIKE). Los dos son
    /// VALORES de parametro: nunca se concatenan al texto del SQL.
    /// </summary>
    private static void AddCatalogTokenParameters(NpgsqlCommand command, IReadOnlyList<string> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            command.Parameters.Add(new NpgsqlParameter($"t{index}", tokens[index]));
            command.Parameters.Add(new NpgsqlParameter($"p{index}", $"%{tokens[index]}%"));
        }
    }

    /// <summary>
    /// Fallback para motores no relacionales (EF Core InMemory en los tests unitarios): no hay SQL
    /// crudo ni pg_trgm. Trae los productos de los 5 tipos y filtra EN MEMORIA por substring del
    /// texto entero o de cualquiera de las palabras. Score null (no es una medida real de similitud).
    ///
    /// <para>Traer y filtrar en memoria seria una mala idea contra Postgres, pero este camino SOLO
    /// corre con el proveedor InMemory de los tests (decenas de filas): es preferible eso a armar un
    /// arbol de expresiones dinamico que nadie pueda leer.</para>
    /// </summary>
    private async Task<IReadOnlyList<CatalogCandidate>> FetchCatalogCandidatesInMemoryAsync(
        string normalizedQuery, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var rows = await _db.Rates
            .AsNoTracking()
            .Where(rate => rate.IsActive
                && rate.SearchName != null
                && CatalogSearchServiceTypes.Contains(rate.ServiceType))
            .Select(rate => new
            {
                rate.Id,
                rate.SearchName,
                rate.HotelName,
                rate.City
            })
            .ToListAsync(ct);

        var candidates = new List<CatalogCandidate>();
        foreach (var row in rows)
        {
            var searchName = row.SearchName ?? string.Empty;
            var hotelName = TextNormalizer.NormalizeForCatalog(row.HotelName);
            var city = TextNormalizer.NormalizeForCatalog(row.City);

            var matchesWholeText = searchName.Contains(normalizedQuery, StringComparison.Ordinal)
                || hotelName.Contains(normalizedQuery, StringComparison.Ordinal);

            var matchesAnyToken = tokens.Any(token =>
                CatalogSearchTokens.TokenMatches(token, searchName)
                || CatalogSearchTokens.TokenMatches(token, hotelName)
                || CatalogSearchTokens.TokenMatches(token, city));

            if (matchesWholeText || matchesAnyToken)
            {
                candidates.Add(new CatalogCandidate(row.Id, null));
            }

            if (candidates.Count == CatalogSearchCandidateFetchLimit) break;
        }

        return candidates;
    }

    /// <summary>Lee el reader de candidatos (Id + score). Abre/cierra la conexion como estaba.</summary>
    private static async Task<IReadOnlyList<CatalogCandidate>> ReadCatalogCandidatesAsync(
        NpgsqlCommand command, bool hasRealScore, CancellationToken ct)
    {
        var connection = command.Connection!;
        var connectionWasClosed = connection.State == ConnectionState.Closed;
        if (connectionWasClosed)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            var results = new List<CatalogCandidate>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                double? score = hasRealScore && !reader.IsDBNull(1)
                    ? Convert.ToDouble(reader.GetValue(1))
                    : null;
                results.Add(new CatalogCandidate(reader.GetInt32(0), score));
            }

            return results;
        }
        finally
        {
            if (connectionWasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<IReadOnlyList<Rate>> LoadCandidateRatesAsync(IReadOnlyList<int> rateIds, CancellationToken ct)
    {
        return await _db.Rates
            .AsNoTracking()
            // Fix H-4 (review 2026-08-11): se trae el operador CARGADO EN LA FICHA (Rate.Supplier) en
            // la MISMA consulta acotada a los rateIds candidatos — sigue siendo UNA sola consulta (un
            // JOIN, no una por fila), nada de N+1. Lo usa BuildCatalogSearchItem para el fallback
            // cuando el producto todavia no tiene ventas (y por eso no hay operador en LastSale).
            .Include(rate => rate.Supplier)
            .Where(rate => rateIds.Contains(rate.Id))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Para cada Rate candidato, la fila MAS reciente de <c>RateSupplierSale</c> (la tabla puede tener
    /// una por operador). En F1.1/F1.2 la tabla nace vacia, asi que normalmente devuelve un dict vacio
    /// y todos los resultados caen al <c>rateFallback</c>; el join se implementa completo igual para
    /// que el dia que F1.3 empiece a escribir ventas, el contexto "ultima vez" aparezca solo.
    /// </summary>
    private async Task<Dictionary<int, CatalogLatestSale>> LoadLatestSalesAsync(
        IReadOnlyList<int> rateIds, CancellationToken ct)
    {
        var sales = await _db.RateSupplierSales
            .AsNoTracking()
            // Una fila ESCONDIDA por una union (2026-08-07) no es un precio vigente: no puede volver como
            // "la ultima vez que lo vendiste". Invariante de RateSupplierSale: toda lectura filtra por null.
            .Where(sale => rateIds.Contains(sale.RateId) && sale.AbsorbedByTidyUpActionId == null)
            .Select(sale => new CatalogLatestSale(
                sale.RateId,
                sale.LastSoldAt,
                sale.LastNetCost,
                sale.LastSalePrice,
                sale.LastCurrency,
                sale.LastPriceUnit,
                sale.Supplier != null ? (Guid?)sale.Supplier.PublicId : null,
                sale.Supplier != null ? sale.Supplier.Name : null))
            .ToListAsync(ct);

        return sales
            .GroupBy(sale => sale.RateId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(sale => sale.SoldAt).First());
    }

    /// <summary>
    /// Los nombres de operador de cada producto candidato (2026-08-10). Sirven SOLO para el filtro
    /// fino en memoria: es lo que hace que "sheraton ola" encuentre el Sheraton que se le compra a
    /// Ola, aunque la palabra "ola" no este en ningun lado del nombre del producto.
    ///
    /// <para>Junta dos fuentes: los operadores que aprendio de las ventas
    /// (<c>RateSupplierSales</c>, salteando las filas que una union escondio — misma invariante que
    /// <see cref="LoadLatestSalesAsync"/>) y el operador cargado a mano en la tarifa vieja
    /// (<c>Rate.Supplier</c>). Son dos consultas fijas, no una por producto (nada de N+1).</para>
    ///
    /// <para>NO se toca <see cref="LoadLatestSalesAsync"/>: esa decide QUE precio se sugiere, y
    /// mezclarle esta lectura cambiaria la sugerencia de plata que ve el vendedor.</para>
    /// </summary>
    private async Task<Dictionary<int, List<string>>> LoadSupplierNamesAsync(
        IReadOnlyList<int> rateIds, CancellationToken ct)
    {
        var namesFromSales = await _db.RateSupplierSales
            .AsNoTracking()
            .Where(sale => rateIds.Contains(sale.RateId)
                && sale.AbsorbedByTidyUpActionId == null
                && sale.Supplier != null)
            .Select(sale => new { sale.RateId, SupplierName = sale.Supplier!.Name })
            .ToListAsync(ct);

        var namesFromRate = await _db.Rates
            .AsNoTracking()
            .Where(rate => rateIds.Contains(rate.Id) && rate.Supplier != null)
            .Select(rate => new { RateId = rate.Id, SupplierName = rate.Supplier!.Name })
            .ToListAsync(ct);

        var namesByRateId = new Dictionary<int, List<string>>();
        foreach (var row in namesFromSales.Concat(namesFromRate))
        {
            var normalizedName = TextNormalizer.NormalizeForCatalog(row.SupplierName);
            if (normalizedName.Length == 0) continue;

            if (!namesByRateId.TryGetValue(row.RateId, out var names))
            {
                names = new List<string>();
                namesByRateId[row.RateId] = names;
            }

            // El mismo operador puede venir por las dos fuentes: guardamos uno solo.
            if (!names.Contains(normalizedName, StringComparer.Ordinal))
            {
                names.Add(normalizedName);
            }
        }

        return namesByRateId;
    }

    /// <summary>Arma el DTO de un resultado: identidad + sugerencia (ultima venta o fallback del Rate).</summary>
    private static CatalogSearchItemDto BuildCatalogSearchItem(
        Rate rate, double? score, CatalogLatestSale? sale, bool canSeeCost)
    {
        var item = new CatalogSearchItemDto
        {
            RatePublicId = rate.PublicId,
            ServiceType = rate.ServiceType,
            Name = BuildCatalogName(rate),
            Subtitle = BuildCatalogSubtitle(rate),
            CreatedInSale = rate.CreatedInSale,
            Score = score
        };

        if (sale != null)
        {
            // Producto ya vendido: la sugerencia sale de la ultima venta.
            item.LastSale = new CatalogSearchLastSaleDto
            {
                SupplierPublicId = sale.SupplierPublicId,
                SupplierName = sale.SupplierName,
                SoldAt = sale.SoldAt,
                NetCost = canSeeCost ? sale.NetCost : null, // R1/D1: costo solo a quien lo puede ver
                SalePrice = sale.SalePrice,                 // la venta viaja SIEMPRE
                Currency = sale.Currency,
                PriceUnit = sale.PriceUnit
            };
        }
        else
        {
            // Producto sin ventas registradas: fallback a los campos curados del Rate. El operador
            // (H-4) sale de la ficha del producto (Rate.Supplier) porque no hay ninguna venta de la
            // que aprenderlo todavia — es el mismo dato que el vendedor ya ve al abrir el producto en
            // el tarifario, no una invencion.
            item.RateFallback = new CatalogSearchRateFallbackDto
            {
                NetCost = canSeeCost ? rate.NetCost : null,
                SalePrice = rate.SalePrice,
                Currency = rate.Currency,
                PriceUnit = rate.PriceUnit,
                HotelPriceType = rate.HotelPriceType,
                SupplierPublicId = rate.Supplier?.PublicId,
                SupplierName = rate.Supplier?.Name
            };
        }

        return item;
    }

    /// <summary>Nombre lindo para mostrar: misma fuente que SearchName (Hotel prioriza HotelName).</summary>
    private static string BuildCatalogName(Rate rate)
    {
        var isHotel = string.Equals(
            TextNormalizer.NormalizeForMatch(rate.ServiceType), "hotel", StringComparison.Ordinal);
        if (isHotel && !string.IsNullOrWhiteSpace(rate.HotelName))
        {
            return rate.HotelName!;
        }

        return rate.ProductName;
    }

    /// <summary>Subtitulo segun tipo: ciudad (hotel), ruta (aereo/traslado), destino (resto).</summary>
    private static string? BuildCatalogSubtitle(Rate rate)
    {
        var typeKey = TextNormalizer.NormalizeForMatch(rate.ServiceType);
        return typeKey switch
        {
            "hotel" => NullIfBlank(rate.City),
            "aereo" => BuildRoute(rate.Origin, rate.Destination),
            "traslado" => BuildRoute(rate.PickupLocation, rate.DropoffLocation),
            _ => NullIfBlank(rate.Destination)
        };
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? BuildRoute(string? origin, string? destination)
    {
        var from = NullIfBlank(origin);
        var to = NullIfBlank(destination);
        if (from is null && to is null) return null;
        if (to is null) return from;
        if (from is null) return to;
        return $"{from} → {to}";
    }

    /// <summary>
    /// Los textos del producto contra los que se busca cada palabra escrita: el nombre, el subtitulo
    /// (ciudad o ruta), los operadores y la palabra del tipo ("hotel", "aereo", ...). Gracias a esta
    /// ultima, escribir "aereo bue" achica la lista a los aereos sin necesidad de ningun filtro.
    /// Todos vienen normalizados (minuscula, sin tildes) porque las palabras buscadas tambien lo estan.
    /// </summary>
    private static IReadOnlyList<string> BuildCatalogHaystacks(
        CatalogSearchItemDto item, List<string>? supplierNames)
    {
        var haystacks = new List<string>
        {
            TextNormalizer.NormalizeForCatalog(item.Name),
            TextNormalizer.NormalizeForCatalog(item.Subtitle),
            TextNormalizer.NormalizeForCatalog(item.ServiceType)
        };

        if (supplierNames != null)
        {
            haystacks.AddRange(supplierNames);
        }

        return haystacks;
    }

    /// <summary>
    /// El "todas las palabras" con degrade: si ALGUN producto cubre todo lo que se escribio, se
    /// muestran solo esos (es la faceta escondida del buscador: "sheraton ola" = nombre + operador).
    /// Si ninguno cubre todo, no se esconde nada — se devuelven todos y el orden final pone adelante
    /// a los que cubren mas. Nunca se deja al vendedor con la lista vacia teniendo candidatos.
    /// </summary>
    private static IReadOnlyList<CatalogMatch> KeepBestTokenCoverage(
        IReadOnlyList<CatalogMatch> matches, int tokenCount)
    {
        if (tokenCount == 0)
        {
            return matches;
        }

        var fullCoverage = matches.Where(match => match.MatchedTokens == tokenCount).ToList();
        return fullCoverage.Count > 0 ? fullCoverage : matches;
    }

    /// <summary>
    /// Pone el puntaje final de cada resultado. El puntaje NO es cosmetico: la ficha resalta como "el
    /// mas parecido" al primero que llega a 0.65, y con ese mismo numero decide no molestar al motor
    /// de IA. Por eso cubrir TODAS las palabras tal cual como se escribieron vale 0.7 (arriba del
    /// corte) y cubrirlas perdonando errores de tipeo vale 0.55 (abajo del corte, sigue siendo una
    /// sugerencia). El parecido que midio Postgres solo puede ganar si el resultado cubre todas las
    /// palabras: con cobertura parcial se lo recorta a 0.64 (abajo del corte), porque word_similarity
    /// infla textos cortos contra nombres largos ("sheraton eze" daria 0.69 cubriendo 1 de 2).
    ///
    /// <para><b>internal (no private)</b>: asi el test unitario del recorte puede llamarlo directo con
    /// un <see cref="CatalogMatch"/> armado a mano, sin tener que levantar Postgres —
    /// <c>InternalsVisibleTo("TravelApi.Tests")</c> ya esta configurado en el csproj.</para>
    /// </summary>
    internal static void ApplyCoverageScores(IReadOnlyList<CatalogMatch> matches, int tokenCount)
    {
        foreach (var match in matches)
        {
            var coversAllTokens = tokenCount > 0 && match.MatchedTokens == tokenCount;

            var coverageScore = 0d;
            if (tokenCount > 0 && match.ExactMatchedTokens == tokenCount)
            {
                coverageScore = CatalogSearchFullCoverageExactScore;
            }
            else if (coversAllTokens)
            {
                coverageScore = CatalogSearchFullCoverageFuzzyScore;
            }

            var sqlScore = match.Item.Score ?? 0d;
            if (tokenCount > 0 && !coversAllTokens)
            {
                // C-2: cobertura PARCIAL nunca puede parecer "un parecido fuerte" (ver el comentario
                // de CatalogSearchPartialCoverageScoreCap, mas arriba). Se recorta el score que vino
                // de SQL ANTES de competir con el coverageScore de abajo.
                sqlScore = Math.Min(sqlScore, CatalogSearchPartialCoverageScoreCap);
            }

            match.Item.Score = Math.Max(sqlScore, coverageScore);
        }
    }

    /// <summary>
    /// Deduplica los resultados (ADR §2.4 / m1): un mismo producto cargado N veces en el tarifario
    /// legacy aparece UNA sola vez. Representante = el de venta mas reciente; el score y la cobertura
    /// del grupo son los mayores (todas las tarifas del producto compiten por el mismo texto escrito).
    /// </summary>
    private static IReadOnlyList<CatalogMatch> DedupeCatalogItems(IReadOnlyList<CatalogMatch> matches)
    {
        var deduped = new List<CatalogMatch>();

        foreach (var group in matches.GroupBy(match => BuildDedupeKey(match.Item)))
        {
            var representative = group
                .OrderByDescending(match => match.Item.LastSale?.SoldAt ?? DateTime.MinValue)
                .ThenByDescending(match => match.Item.Score ?? -1d)
                .First();

            representative.Item.Score = group.Max(match => match.Item.Score);

            deduped.Add(representative with
            {
                MatchedTokens = group.Max(match => match.MatchedTokens),
                ExactMatchedTokens = group.Max(match => match.ExactMatchedTokens)
            });
        }

        return deduped;
    }

    /// <summary>
    /// Clave de dedupe: TIPO + nombre normalizado (con NormalizeForCatalog, autoritativo). Para Hotel
    /// suma la City normalizada — dos hoteles homonimos de ciudades distintas son productos distintos.
    ///
    /// <para><b>Por que el tipo va en la clave</b> (2026-08-10): desde que la busqueda cruza tipos, en
    /// la misma lista pueden convivir un Hotel y un Paquete que se llaman igual ("Bariloche"). Sin el
    /// tipo adelante, el dedupe los tomaria por el mismo producto y haria desaparecer uno de los dos.
    /// El "es hotel" tambien se decide por producto (antes venia del tipo de la busqueda, que ahora ya
    /// no filtra nada).</para>
    /// </summary>
    private static string BuildDedupeKey(CatalogSearchItemDto item)
    {
        var typeKey = TextNormalizer.NormalizeForCatalog(item.ServiceType);
        var nameKey = TextNormalizer.NormalizeForCatalog(item.Name);

        if (!string.Equals(typeKey, "hotel", StringComparison.Ordinal))
        {
            return $"{typeKey}|{nameKey}";
        }

        var cityKey = TextNormalizer.NormalizeForCatalog(item.Subtitle);
        return $"{typeKey}|{nameKey}|{cityKey}";
    }

    /// <summary>
    /// Orden final del dropdown: (1) el que cubre mas palabras de lo escrito; (2) el mas parecido,
    /// dandole un empujon al tipo de la solapa donde esta parado el vendedor; (3) a igual todo, la
    /// venta mas reciente.
    ///
    /// <para>El empujon del tipo se suma SOLO para ordenar: el <c>Score</c> que viaja en el DTO queda
    /// limpio, para que la ficha no resalte como "el mas parecido" a un producto flojo solo por estar
    /// en la solapa correcta.</para>
    /// </summary>
    private static IReadOnlyList<CatalogSearchItemDto> SortCatalogResults(
        IReadOnlyList<CatalogMatch> matches, string preferredServiceType)
    {
        return matches
            .OrderByDescending(match => match.MatchedTokens)
            .ThenByDescending(match => (match.Item.Score ?? 0d) + PreferredTypeBoost(match.Item, preferredServiceType))
            .ThenByDescending(match => match.Item.LastSale?.SoldAt ?? DateTime.MinValue)
            .Take(CatalogSearchResultLimit)
            .Select(match => match.Item)
            .ToList();
    }

    private static double PreferredTypeBoost(CatalogSearchItemDto item, string preferredServiceType)
    {
        if (preferredServiceType.Length == 0)
        {
            return 0d;
        }

        var itemType = TextNormalizer.NormalizeForCatalog(item.ServiceType);
        return string.Equals(itemType, preferredServiceType, StringComparison.Ordinal)
            ? CatalogSearchPreferredTypeBoost
            : 0d;
    }

    /// <summary>Candidato crudo del buscador: id del Rate + score difuso (null si vino de un fallback).</summary>
    private sealed record CatalogCandidate(int RateId, double? Score);

    /// <summary>
    /// Un resultado con su medida de "que tan completo" es: cuantas palabras de lo escrito cubre
    /// (<paramref name="MatchedTokens"/>, perdonando errores de tipeo) y cuantas cubre tal cual
    /// (<paramref name="ExactMatchedTokens"/>). El DTO de adentro es el que termina viajando al front.
    ///
    /// <para>internal (no private, fix C-2): para que el test unitario del clamp de
    /// <see cref="ApplyCoverageScores"/> pueda armar uno a mano sin pasar por Postgres.</para>
    /// </summary>
    internal sealed record CatalogMatch(
        CatalogSearchItemDto Item, int MatchedTokens, int ExactMatchedTokens);

    /// <summary>Snapshot de la ultima venta de un Rate (para el contexto "ultima vez" del dropdown).</summary>
    private sealed record CatalogLatestSale(
        int RateId,
        DateTime SoldAt,
        decimal NetCost,
        decimal SalePrice,
        string? Currency,
        string? PriceUnit,
        Guid? SupplierPublicId,
        string? SupplierName);

    private async Task<int?> ResolveOptionalSupplierIdAsync(string? supplierPublicId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(supplierPublicId))
        {
            return null;
        }

        var supplierId = await _db.Suppliers
            .AsNoTracking()
            .ResolveInternalIdAsync(supplierPublicId, ct);

        if (!supplierId.HasValue)
        {
            throw new ArgumentException("Proveedor no encontrado.");
        }

        return supplierId.Value;
    }

    private IQueryable<Rate> BuildFilteredRatesQuery(
        int? supplierId,
        string? serviceType,
        bool activeOnly,
        string? search)
    {
        var query = _db.Rates
            .AsNoTracking()
            .AsQueryable();

        if (supplierId.HasValue)
        {
            query = query.Where(rate => rate.SupplierId == supplierId.Value);
        }

        if (!string.IsNullOrWhiteSpace(serviceType))
        {
            query = query.Where(rate => rate.ServiceType == serviceType);
        }

        if (activeOnly)
        {
            var now = DateTime.UtcNow;
            query = query.Where(rate => rate.IsActive && (!rate.ValidTo.HasValue || rate.ValidTo.Value >= now));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(rate =>
                rate.ProductName.ToLower().Contains(normalized) ||
                (rate.Description != null && rate.Description.ToLower().Contains(normalized)) ||
                (rate.HotelName != null && rate.HotelName.ToLower().Contains(normalized)) ||
                (rate.City != null && rate.City.ToLower().Contains(normalized)) ||
                (rate.Airline != null && rate.Airline.ToLower().Contains(normalized)) ||
                (rate.Origin != null && rate.Origin.ToLower().Contains(normalized)) ||
                (rate.Destination != null && rate.Destination.ToLower().Contains(normalized)) ||
                (rate.Supplier != null && rate.Supplier.Name.ToLower().Contains(normalized)));
        }

        return query;
    }

    private static IQueryable<Rate> ApplyRateOrdering(IQueryable<Rate> query, RateListQuery request)
    {
        var sortBy = (request.SortBy ?? "productName").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "suppliername" => desc
                ? query.OrderByDescending(rate => rate.Supplier != null ? rate.Supplier.Name : string.Empty)
                    .ThenByDescending(rate => rate.ProductName)
                : query.OrderBy(rate => rate.Supplier != null ? rate.Supplier.Name : string.Empty)
                    .ThenBy(rate => rate.ProductName),
            "validto" => desc
                ? query.OrderByDescending(rate => rate.ValidTo).ThenByDescending(rate => rate.ProductName)
                : query.OrderBy(rate => rate.ValidTo).ThenBy(rate => rate.ProductName),
            "saleprice" => desc
                ? query.OrderByDescending(rate => rate.SalePrice).ThenByDescending(rate => rate.ProductName)
                : query.OrderBy(rate => rate.SalePrice).ThenBy(rate => rate.ProductName),
            _ => desc
                ? query.OrderByDescending(rate => rate.ProductName).ThenByDescending(rate => rate.ValidTo)
                : query.OrderBy(rate => rate.ProductName).ThenBy(rate => rate.ValidTo)
        };
    }

    private static IQueryable<HotelRateGroupSummary> ApplyHotelGroupOrdering(
        IQueryable<HotelRateGroupSummary> query,
        HotelRateGroupsQuery request)
    {
        var sortBy = (request.SortBy ?? "hotelName").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "city" => desc
                ? query.OrderByDescending(group => group.City).ThenByDescending(group => group.HotelName)
                : query.OrderBy(group => group.City).ThenBy(group => group.HotelName),
            "validto" => desc
                ? query.OrderByDescending(group => group.HasExpiredRates).ThenByDescending(group => group.HotelName)
                : query.OrderBy(group => group.HasExpiredRates).ThenBy(group => group.HotelName),
            "fromprice" => desc
                ? query.OrderByDescending(group => group.FromPrice).ThenByDescending(group => group.HotelName)
                : query.OrderBy(group => group.FromPrice).ThenBy(group => group.HotelName),
            _ => desc
                ? query.OrderByDescending(group => group.HotelName).ThenByDescending(group => group.City)
                : query.OrderBy(group => group.HotelName).ThenBy(group => group.City)
        };
    }

    private static IQueryable<RateGroupSummary> ApplyRateGroupOrdering(
        IQueryable<RateGroupSummary> query,
        RateGroupsQuery request)
    {
        var sortBy = (request.SortBy ?? "groupName").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "servicetype" => desc
                ? query.OrderByDescending(group => group.ServiceType).ThenByDescending(group => group.GroupName)
                : query.OrderBy(group => group.ServiceType).ThenBy(group => group.GroupName),
            "suppliername" => desc
                ? query.OrderByDescending(group => group.SupplierName).ThenByDescending(group => group.GroupName)
                : query.OrderBy(group => group.SupplierName).ThenBy(group => group.GroupName),
            "fromprice" => desc
                ? query.OrderByDescending(group => group.FromPrice).ThenByDescending(group => group.GroupName)
                : query.OrderBy(group => group.FromPrice).ThenBy(group => group.GroupName),
            _ => desc
                ? query.OrderByDescending(group => group.GroupName).ThenByDescending(group => group.ServiceType)
                : query.OrderBy(group => group.GroupName).ThenBy(group => group.ServiceType)
        };
    }

    private static IQueryable<RateListItemDto> ProjectRateListItems(IQueryable<Rate> query)
    {
        return query.Select(rate => new RateListItemDto
        {
            PublicId = rate.PublicId,
            ServiceType = rate.ServiceType,
            ProductName = rate.ProductName,
            Description = rate.Description,
            PriceUnit = rate.PriceUnit,
            NetCost = rate.NetCost,
            Tax = rate.Tax,
            SalePrice = rate.SalePrice,
            Commission = rate.Commission,
            Currency = rate.Currency,
            ValidFrom = rate.ValidFrom,
            ValidTo = rate.ValidTo,
            IsActive = rate.IsActive,
            InternalNotes = rate.InternalNotes,
            Airline = rate.Airline,
            AirlineCode = rate.AirlineCode,
            Origin = rate.Origin,
            Destination = rate.Destination,
            CabinClass = rate.CabinClass,
            BaggageIncluded = rate.BaggageIncluded,
            HotelName = rate.HotelName,
            City = rate.City,
            StarRating = rate.StarRating,
            RoomType = rate.RoomType,
            RoomCategory = rate.RoomCategory,
            RoomFeatures = rate.RoomFeatures,
            MealPlan = rate.MealPlan,
            HotelPriceType = rate.HotelPriceType,
            ChildrenPayPercent = rate.ChildrenPayPercent,
            ChildMaxAge = rate.ChildMaxAge,
            PickupLocation = rate.PickupLocation,
            DropoffLocation = rate.DropoffLocation,
            VehicleType = rate.VehicleType,
            MaxPassengers = rate.MaxPassengers,
            IsRoundTrip = rate.IsRoundTrip,
            IncludesFlight = rate.IncludesFlight,
            IncludesHotel = rate.IncludesHotel,
            IncludesTransfer = rate.IncludesTransfer,
            IncludesExcursions = rate.IncludesExcursions,
            IncludesInsurance = rate.IncludesInsurance,
            DurationDays = rate.DurationDays,
            Itinerary = rate.Itinerary,
            SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
            SupplierName = rate.Supplier != null ? rate.Supplier.Name : null
        });
    }

    private static string BuildHotelGroupKey(string? hotelName, string? city, string? supplierName, int? starRating)
    {
        return $"{hotelName ?? string.Empty}|{city ?? string.Empty}|{supplierName ?? string.Empty}|{starRating?.ToString() ?? string.Empty}";
    }

    private static string BuildRateGroupKey(RateListItemDto item)
    {
        var groupName = item.ServiceType == "Hotel"
            ? (item.HotelName ?? "Hotel sin nombre")
            : item.ProductName;
        var subtitle = item.ServiceType == "Hotel" ? item.City : null;
        var starRating = item.ServiceType == "Hotel" ? item.StarRating : null;

        return BuildRateGroupKey(item.ServiceType, groupName, subtitle, item.SupplierName, starRating);
    }

    private static string BuildRateGroupKey(
        string serviceType,
        string? groupName,
        string? subtitle,
        string? supplierName,
        int? starRating)
    {
        return string.Join(
            "|",
            serviceType ?? string.Empty,
            groupName ?? string.Empty,
            subtitle ?? string.Empty,
            supplierName ?? string.Empty,
            starRating?.ToString() ?? string.Empty);
    }

    private static IEnumerable<RateListItemDto> OrderRateGroupItems(IEnumerable<RateListItemDto> items)
    {
        return items
            .OrderBy(item => item.ServiceType == "Hotel" ? item.RoomType ?? string.Empty : item.ProductName)
            .ThenBy(item => item.RoomCategory ?? string.Empty)
            .ThenBy(item => item.Airline ?? string.Empty)
            .ThenBy(item => item.VehicleType ?? string.Empty)
            .ThenBy(item => item.DurationDays ?? 0)
            .ThenBy(item => item.SalePrice)
            .ThenBy(item => item.ValidTo);
    }

    private sealed class HotelRateGroupSummary
    {
        public string GroupKey { get; set; } = string.Empty;
        public string HotelName { get; set; } = string.Empty;
        public string? City { get; set; }
        public int? StarRating { get; set; }
        public Guid? SupplierPublicId { get; set; }
        public string? SupplierName { get; set; }
        public decimal FromPrice { get; set; }
        public bool HasExpiredRates { get; set; }
        public int RoomCount { get; set; }
    }

    private sealed class RateGroupSummary
    {
        public string GroupKey { get; set; } = string.Empty;
        public string ServiceType { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public int? StarRating { get; set; }
        public Guid? SupplierPublicId { get; set; }
        public string? SupplierName { get; set; }
        public decimal FromPrice { get; set; }
        public bool HasExpiredRates { get; set; }
        public int ItemCount { get; set; }
    }
}
