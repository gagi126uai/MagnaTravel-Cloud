using System.Data;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-017 F1.3 (catalogo find-or-create desde la venta, 2026-06-05): "corazon" del find-or-create.
///
/// <para>TODO lo de este archivo vive detras del flag <c>EnableCatalogFindOrCreate</c>. Con el flag
/// APAGADO nada de aca se ejecuta y los <c>Create*Async</c> de <c>BookingService.cs</c> corren su codigo
/// historico (byte-identico). Con el flag PRENDIDO, los <c>Create*</c> derivan a los <c>Create*WithCatalogAsync</c>
/// de este archivo, que envuelven el alta en UNA transaccion atomica e incorporan:</para>
/// <list type="number">
///   <item>Creacion inline del producto (<c>NewCatalogProduct</c>) o reuso de un producto existente
///   (find-or-create defensivo, §2.4).</item>
///   <item>Regla "request manda" (§2.3.b.3): el Rate aporta identidad + relleno de huecos, pero los
///   precios/supplier/moneda/atributos los manda el request (NO el snapshot que pisa del path OFF).</item>
///   <item>Cadena de costo D7 (§2.8) para callers SIN <c>cobranzas.see_cost</c>: el costo se resuelve
///   server-side (RateSupplierSale -> campos del Rate -> 0) y se marca "costo a confirmar" en los casos
///   dudosos. Un servicio marcado NO upsertea RateSupplierSale hasta que alguien confirme.</item>
///   <item>Upsert atomico de <c>RateSupplierSale</c> (§2.1) con precios UNITARIOS.</item>
/// </list>
/// </summary>
public partial class BookingService
{
    // ============================================================
    // Lectura de flag + settings del catalogo
    // ============================================================

    /// <summary>
    /// Lee el flag <c>EnableCatalogFindOrCreate</c>. Fail-closed: sin service de settings inyectado
    /// (ctores de tests legacy) se considera APAGADO -> el create corre el path historico (byte-identico).
    /// </summary>
    private async Task<bool> IsCatalogFindOrCreateEnabledAsync(CancellationToken ct)
    {
        if (_settingsService is null) return false;
        var settings = await _settingsService.GetEntityAsync(ct);
        return settings.EnableCatalogFindOrCreate;
    }

    /// <summary>Dias a partir de los cuales una referencia de costo se considera "vieja" (D7). Default 60.</summary>
    private async Task<int> GetStaleCostReferenceDaysAsync(CancellationToken ct)
    {
        if (_settingsService is null) return 60;
        var settings = await _settingsService.GetEntityAsync(ct);
        return settings.StaleCostReferenceDays;
    }

    // ============================================================
    // Transaccion atomica (patron de la casa: ExecutionStrategy + Serializable)
    // ============================================================

    /// <summary>
    /// Corre <paramref name="body"/> dentro de UNA transaccion Serializable, usando la estrategia de
    /// reintentos ya configurada en Program.cs (<c>EnableRetryOnFailure</c>). Patron identico a
    /// <c>ReservaService.CreateReservaAsync</c>.
    ///
    /// <para><b>Por que Serializable + reintento</b>: dos vendedores creando "Hotel Maitei" a la vez
    /// producen un serialization failure (40001) en uno; Npgsql lo marca transient y la estrategia
    /// reintenta; el reintento ENCUENTRA el Rate del ganador y lo reusa (find-or-create idempotente).
    /// Asi ninguna venta se duplica ni se aborta por la carrera (R10).</para>
    ///
    /// <para><b>Delegate RE-EJECUTABLE (leccion commit 723a905)</b>: en un reintento, el
    /// <c>ChangeTracker</c> puede arrastrar entidades del intento fallido. Por eso lo limpiamos al
    /// entrar: todo lo que el body necesita lo (re)carga/instancia DENTRO del delegate.</para>
    ///
    /// <para><b>INVARIANTE DE ATOMICIDAD (no cambiar lifetimes)</b>: que el upsert de RateSupplierSale y los
    /// refrescos de saldo (SupplierService/ReservaService) entren en ESTA misma transaccion depende de que
    /// <c>AppDbContext</c>, <c>BookingService</c>, <c>SupplierService</c> y <c>ReservaService</c> compartan el
    /// MISMO scope (todos registrados Scoped en Program.cs, verificado). Si alguno pasara a Singleton/Transient
    /// usaria otro DbContext y escribiria FUERA de esta transaccion -> se rompe el "todo o nada".</para>
    /// </summary>
    private async Task<T> RunCatalogTransactionAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // Re-ejecutable: arrancamos siempre desde cero (sin estado residual de un intento previo).
            _db.ChangeTracker.Clear();

            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
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

    // ============================================================
    // Validacion de entrada comun a los 5 creates con flag ON
    // ============================================================

    /// <summary>
    /// Reglas de entrada del path catalogo (§2.3.b): Currency obligatoria; <c>NewCatalogProduct</c> y
    /// <c>RateId</c> mutuamente excluyentes; para Hotel la City del producto nuevo es obligatoria (D6).
    /// Lanza <see cref="ArgumentException"/> (que el controller traduce a 400).
    /// </summary>
    private static void ValidateCatalogCreateInputs(
        string? currency, string? rateId, NewCatalogProductRequest? newProduct, bool isHotel)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("La moneda de la venta es obligatoria.");

        var hasRateId = !string.IsNullOrWhiteSpace(rateId);
        if (hasRateId && newProduct != null)
            throw new ArgumentException("No se puede elegir un producto existente y crear uno nuevo a la vez.");

        if (newProduct != null)
        {
            if (string.IsNullOrWhiteSpace(newProduct.Name))
                throw new ArgumentException("El nombre del producto nuevo es obligatorio.");
            if (string.IsNullOrWhiteSpace(newProduct.SupplierPublicId))
                throw new ArgumentException("El operador del producto nuevo es obligatorio.");
            // D6: ciudad obligatoria al crear un HOTEL desde la venta.
            if (isHotel && string.IsNullOrWhiteSpace(newProduct.City))
                throw new ArgumentException("La ciudad es obligatoria para crear un hotel.");
        }
    }

    /// <summary>Normaliza la moneda de la venta para guardar/comparar (trim + mayuscula): "usd" -> "USD".</summary>
    private static string NormalizeCurrency(string? currency)
        => (currency ?? string.Empty).Trim().ToUpperInvariant();

    // ============================================================
    // Find-or-create defensivo del producto (Rate) — §2.4
    // ============================================================

    /// <summary>
    /// Reusa un Rate activo del mismo tipo cuyo <c>SearchName</c> normalizado coincide EXACTO (para Hotel
    /// ademas misma City normalizada); si no existe, crea uno nuevo marcado "creado en venta". El supplier
    /// NO participa de la identidad (un producto es supplier-agnostico; la combinacion va a RateSupplierSale).
    ///
    /// <para><b>Residuo de normalizacion (NB-1/B3)</b>: el backfill SQL de F1.1 NO colapsa puntuacion
    /// repetida como <c>NormalizeForCatalog</c>. Por eso aca SIEMPRE normalizamos con la funcion de la app
    /// (autoritativa) al escribir Y al comparar: el <c>SearchName</c> del Rate nuevo se escribe normalizado,
    /// y la comparacion de igualdad usa el mismo normalizador sobre los candidatos. La City de Hotel se
    /// compara en memoria (NFD completo, sin depender de <c>unaccent</c>), igual criterio que catalog-search.</para>
    /// </summary>
    private async Task<Rate> FindOrCreateRateAsync(
        string serviceType,
        string productName,
        string? city,
        int supplierId,
        string currency,
        CatalogUnitization.Unitized unit,
        int reservaId,
        bool isHotel,
        CancellationToken ct)
    {
        var searchName = TextNormalizer.NormalizeForCatalog(productName);
        var normalizedCity = TextNormalizer.NormalizeForCatalog(city);

        // 1. Candidatos por igualdad EXACTA de SearchName dentro del tipo (Rates activos). A escala
        //    single-tenant son unidades; la comparacion fina de City se hace en memoria.
        var candidates = await _db.Rates
            .Where(rate => rate.ServiceType == serviceType
                && rate.IsActive
                && rate.SearchName == searchName)
            .ToListAsync(ct);

        Rate? existing = null;
        foreach (var candidate in candidates)
        {
            if (isHotel)
            {
                // Hotel: dos homonimos de ciudades distintas son productos distintos. Si uno tiene City y
                // el otro no, NO matchea (en la duda, crear: mas barato que contaminar un producto ajeno).
                var candidateCity = TextNormalizer.NormalizeForCatalog(candidate.City);
                if (!string.Equals(candidateCity, normalizedCity, StringComparison.Ordinal))
                    continue;
            }

            existing = candidate;
            break;
        }

        if (existing != null)
        {
            // REUSO: no se toca nada del Rate existente (ni precios ni supplier). Solo se devuelve para
            // que el booking lo apunte; la combinacion (Rate, supplier) de ESTA venta va a RateSupplierSale.
            return existing;
        }

        // 2. No existe -> crear. Los precios del Rate son UNITARIOS (§2.1). El default "USD" de Rate.cs
        //    NO decide nunca: la moneda sale del request (D5).
        var newRate = new Rate
        {
            ServiceType = serviceType,
            ProductName = productName,
            SearchName = searchName,
            SupplierId = supplierId,
            Currency = currency,
            PriceUnit = unit.PriceUnit,
            NetCost = unit.UnitNetCost,
            Tax = unit.UnitTax,
            SalePrice = unit.UnitSalePrice,
            Commission = unit.UnitSalePrice - unit.UnitNetCost - unit.UnitTax,
            IsActive = true,
            CreatedInSale = true,
            CreatedFromReservaId = reservaId
        };

        if (isHotel)
        {
            newRate.HotelName = productName;
            newRate.City = city;
        }
        else if (!string.IsNullOrWhiteSpace(city))
        {
            // Para los demas tipos la "ciudad" del producto nuevo es el destino/ruta (opcional).
            newRate.Destination = city;
        }

        await _db.Set<Rate>().AddAsync(newRate, ct);
        await _db.SaveChangesAsync(ct);
        return newRate;
    }

    // ============================================================
    // Cadena de costo D7 (§2.8) — solo callers SIN cobranzas.see_cost
    // ============================================================

    /// <summary>Resultado de la cadena D7: costo TOTAL repuesto + si quedo "a confirmar" y por que.</summary>
    private readonly record struct MaskedCostResolution(decimal Net, decimal Tax, bool ToConfirm, string? Reason);

    /// <summary>
    /// Resuelve el costo de un caller que NO ve costos (su request llega con NetCost/Tax = 0 enmascarado).
    /// Cadena en orden (§2.3.b.3-bis): (1) <c>RateSupplierSale</c> del (Rate, supplier elegido) si su moneda
    /// coincide con la de la venta; (2) campos del Rate si su moneda coincide; (3) 0. Devuelve montos TOTALES
    /// (re-multiplica el unitario por el divisor del tipo). Marca "a confirmar" SOLO los dudosos: sin costo
    /// conocido (quedo 0) o referencia mas vieja que <c>StaleCostReferenceDays</c>.
    ///
    /// <para><b>Regla de moneda conservadora (decision de diseño, no del dueño)</b>: una referencia en otra
    /// moneda NO se usa (no mezclamos monedas en un costo invisible para quien vende). Revisitar tras ADR-011.</para>
    ///
    /// <para><b>Interpretacion de unidades del Rate (paso 2)</b>: el Rate guarda precio unitario; lo
    /// re-multiplicamos por el divisor del tipo, mismo criterio que el helper B1 de hotel ya verificado.
    /// Es una referencia editable, no una tarifa firme.</para>
    /// </summary>
    private async Task<MaskedCostResolution> ResolveMaskedCostChainAsync(
        int rateId, Rate? rate, int supplierId, string currency, int divisor, int staleDays, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // (1) RateSupplierSale del (Rate, supplier elegido).
        var sale = await _db.RateSupplierSales
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.RateId == rateId && s.SupplierId == supplierId, ct);

        if (sale != null && CurrencyMatches(sale.LastCurrency, currency))
        {
            var net = CatalogUnitization.ToTotal(sale.LastNetCost, divisor);
            var tax = CatalogUnitization.ToTotal(sale.LastTax, divisor);
            var stale = (now - sale.LastSoldAt).TotalDays > staleDays;
            return new MaskedCostResolution(net, tax, stale, stale ? "StaleReference" : null);
        }

        // (2) Campos del Rate (precio unitario curado o de nacimiento).
        if (rate != null && CurrencyMatches(rate.Currency, currency) && (rate.NetCost > 0m || rate.Tax > 0m))
        {
            var net = CatalogUnitization.ToTotal(rate.NetCost, divisor);
            var tax = CatalogUnitization.ToTotal(rate.Tax, divisor);
            var referenceDate = rate.UpdatedAt ?? rate.CreatedAt;
            var stale = (now - referenceDate).TotalDays > staleDays;
            return new MaskedCostResolution(net, tax, stale, stale ? "StaleReference" : null);
        }

        // (3) Nada utilizable -> 0 + "sin costo conocido".
        return new MaskedCostResolution(0m, 0m, true, "NoKnownCost");
    }

    private static bool CurrencyMatches(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ============================================================
    // Upsert atomico de RateSupplierSale (§2.1)
    // ============================================================

    /// <summary>
    /// Delega en el HELPER UNICO de upsert (<see cref="CatalogSaleUpsert"/>): centralizar la escritura de
    /// <c>RateSupplierSale</c> en un solo lugar (ON CONFLICT atomico en Postgres) es requisito del ADR (R7)
    /// para que la tabla denormalizada no se desincronice entre escritores.
    /// </summary>
    private Task UpsertRateSupplierSaleAsync(
        int rateId, int supplierId, CatalogUnitization.Unitized unit, string currency, DateTime soldAt,
        CancellationToken ct)
        => CatalogSaleUpsert.UpsertAsync(_db, rateId, supplierId, unit, currency, soldAt, ct);
}
