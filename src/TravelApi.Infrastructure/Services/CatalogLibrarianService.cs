using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// EL BIBLIOTECARIO del tarifario (spec firmada 2026-08-07, §6 / M-16, M-17, M-24, M-26).
/// Version v0: <b>determinística, sin IA</b> (la IA es la fase 2 y entra por el mismo contrato).
///
/// <para><b>Que hace</b>: ordena solo el desorden que dejo el formulario viejo. Junta el mismo hotel que
/// quedo cargado tres veces (una por habitacion) devolviendolo a UN producto, y rescata la habitacion que
/// habia quedado escondida dentro del nombre. Lo que no puede decidir solo, lo deja agrupado en la bandeja
/// "Repetidos" para que una persona diga "es el mismo" o "es otro".</para>
///
/// <para><b>Las cuatro reglas que lo hacen seguro</b> (sin ellas, "que decida solo" seria "que rompa solo"):</para>
/// <list type="number">
///   <item><b>NADA SE BORRA</b> (orden del dueño, 2026-08-03): unir no elimina ni un producto ni una fila
///   de precio. El producto absorbido se apaga y apunta al que quedo; la fila de precio que pierde un
///   choque queda ESCONDIDA, no borrada.</item>
///   <item><b>Todo queda fotografiado</b>: cada fila que la union toca guarda como estaba ANTES
///   (<see cref="CatalogTidyUpSaleChange"/>). Sin la foto, el Deshacer seria una promesa vacia.</item>
///   <item><b>Si no se puede deshacer bien, no se deshace</b>: si despues de la union hubo ventas nuevas
///   sobre las filas movidas, la accion se marca como NO reversible con un motivo en criollo. Es preferible
///   decir "esto ya no se puede deshacer" a deshacer mal y romper la plata de otro.</item>
///   <item><b>Solo une los "casi seguros"</b> (Q3=B firmada): mismo nombre base + misma ciudad, y el
///   sobrante del nombre parece una habitacion de verdad. Cualquier otra cosa va a la bandeja.</item>
/// </list>
/// </summary>
public class CatalogLibrarianService : ICatalogLibrarianService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CatalogLibrarianService> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public CatalogLibrarianService(
        AppDbContext db,
        ILogger<CatalogLibrarianService> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _db = db;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    // ============================================================
    // Pasada automatica: une los "casi seguros" (M-16 + Q3=B)
    // ============================================================

    public async Task<TidyUpRunResult> TidyUpAsync(CancellationToken ct)
    {
        var result = new TidyUpRunResult();

        // UNA sola mirada al tarifario: se arman los grupos, se decide todo lo que hay que unir, y recien
        // ahi se une. Antes se recalculaban los grupos al final (una segunda pasada completa por la base).
        var groups = await BuildGroupsAsync(ct);
        var pending = new List<(Guid Survivor, Guid Absorbed)>();

        foreach (var group in groups)
        {
            foreach (var candidate in group.Candidates)
            {
                // Solo se une solo lo que el sistema mismo desordeno: el producto cuyo nombre trae la
                // habitacion pegada atras. Un "parecido" cualquiera NO se toca sin que alguien lo mire.
                if (!candidate.RescuedFromNameSuffix) continue;
                pending.Add((group.Survivor.PublicId, candidate.Rate.PublicId));
            }
        }

        foreach (var (survivorPublicId, absorbedPublicId) in pending)
        {
            try
            {
                var merge = await MergeAsync(survivorPublicId, absorbedPublicId, decidedByTheSystem: true, ct);
                result.MergedProducts++;
                if (!string.IsNullOrEmpty(merge.VariantLabelRescued)) result.VariantsRescued++;
            }
            catch (Exception ex)
            {
                // Una union que falla NO puede frenar a las demas ni mentir en el resumen: se cuenta como
                // "quedo para revisar" y se deja el motivo en el log tecnico (nunca en pantalla).
                result.CouldNotMerge++;
                _logger.LogWarning(ex,
                    "Bibliotecario: no se pudo unir el producto {Absorbed} con {Survivor}.",
                    absorbedPublicId, survivorPublicId);
            }
        }

        // Lo que queda para revisar = los grupos que sobreviven despues de unir. Se recalcula UNA vez.
        result.LeftForReview = (await BuildGroupsAsync(ct)).Count;

        _logger.LogInformation(
            "Bibliotecario del tarifario: {Merged} productos unidos solos, {Rescued} habitaciones rescatadas, " +
            "{Failed} no se pudieron unir, {Pending} grupos quedan para revisar.",
            result.MergedProducts, result.VariantsRescued, result.CouldNotMerge, result.LeftForReview);

        return result;
    }

    // ============================================================
    // La bandeja "Repetidos" (M-24, V11=B)
    // ============================================================

    public async Task<DuplicateProductsResponse> GetDuplicateGroupsAsync(CancellationToken ct)
    {
        var groups = await BuildGroupsAsync(ct);
        var since = DateTime.UtcNow.AddDays(-7);

        var tidiedUpThisWeek = await _db.CatalogTidyUpActions
            .AsNoTracking()
            .CountAsync(action => action.DecidedByTheSystem
                && action.UndoneAt == null
                && action.PerformedAt >= since, ct);

        return new DuplicateProductsResponse
        {
            Groups = groups.Select(group => new DuplicateProductGroupDto
            {
                SurvivorPublicId = group.Survivor.PublicId,
                SurvivorName = ProductDisplayName(group.Survivor),
                SurvivorSubtitle = ProductSubtitle(group.Survivor),
                SurvivorPriceCount = group.SurvivorPriceCount,
                Candidates = group.Candidates.Select(candidate => new DuplicateProductCandidateDto
                {
                    RatePublicId = candidate.Rate.PublicId,
                    Name = ProductDisplayName(candidate.Rate),
                    Subtitle = ProductSubtitle(candidate.Rate),
                    PriceCount = candidate.PriceCount,
                    VariantLabelToRescue = string.IsNullOrEmpty(candidate.VariantLabelToRescue)
                        ? null
                        : candidate.VariantLabelToRescue
                }).ToList()
            }).ToList(),
            TidiedUpThisWeek = tidiedUpThisWeek
        };
    }

    // ============================================================
    // Unir / "es otro" / deshacer (M-17, M-26)
    // ============================================================

    public async Task<MergeProductsResult> MergeProductsAsync(MergeProductsRequest request, CancellationToken ct)
    {
        if (request.SurvivorPublicId == request.AbsorbedPublicId)
        {
            throw new RateValidationException("Elegí dos productos distintos para unir.");
        }

        return await MergeAsync(request.SurvivorPublicId, request.AbsorbedPublicId, decidedByTheSystem: false, ct);
    }

    public async Task MarkAsNotDuplicatesAsync(NotDuplicatesRequest request, CancellationToken ct)
    {
        var first = await FindRateAsync(request.FirstPublicId, ct);
        var second = await FindRateAsync(request.SecondPublicId, ct);
        if (first.Id == second.Id)
        {
            throw new RateValidationException("Elegí dos productos distintos.");
        }

        // Par ORDENADO (menor primero): asi (7,3) y (3,7) son la misma fila y el indice unico alcanza.
        var low = Math.Min(first.Id, second.Id);
        var high = Math.Max(first.Id, second.Id);

        var alreadyMarked = await _db.CatalogNotDuplicatePairs
            .AnyAsync(pair => pair.LowRateId == low && pair.HighRateId == high, ct);
        if (alreadyMarked) return; // idempotente: tocar dos veces "Es otro" no rompe nada

        _db.CatalogNotDuplicatePairs.Add(new CatalogNotDuplicatePair
        {
            LowRateId = low,
            HighRateId = high,
            MarkedByUserId = CurrentUserId()
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<TidyUpLogResponse> GetTidyUpLogAsync(CancellationToken ct)
    {
        var actions = await _db.CatalogTidyUpActions
            .AsNoTracking()
            .Include(action => action.SaleChanges)
            .OrderByDescending(action => action.PerformedAt)
            .Take(100)
            .ToListAsync(ct);

        var lines = new List<TidyUpActionDto>();
        foreach (var action in actions)
        {
            // El motivo por el que ya no se puede deshacer se calcula al vuelo (puede haber aparecido una
            // venta nueva desde la ultima vez que alguien miro esta lista). Lo ya deshecho no se recalcula:
            // no tiene boton, asi que el motivo no cambiaria nada y son consultas de mas.
            var blocked = action.UndoneAt != null
                ? null
                : action.UndoBlockedReason ?? await ResolveUndoBlockerAsync(action, ct);

            var esCorreccionDeHabitacion = string.Equals(
                action.Kind, CatalogTidyUpKinds.VariantRenamed, StringComparison.Ordinal);

            lines.Add(new TidyUpActionDto
            {
                PublicId = action.PublicId,
                // Corregir una habitacion NO es unir dos productos: la linea lo tiene que decir distinto,
                // si no se leeria "Doble con desayuno → Maitei Posadas", que no significa nada.
                Summary = esCorreccionDeHabitacion
                    ? $"{action.AbsorbedName} → {action.VariantLabelRescued}"
                    : $"{action.AbsorbedName} → {action.SurvivingName}",
                Detail = esCorreccionDeHabitacion
                    ? $"en {action.SurvivingName}"
                    : string.IsNullOrEmpty(action.VariantLabelRescued)
                        ? null
                        : $"la habitación quedó como \"{action.VariantLabelRescued}\"",
                PerformedAt = action.PerformedAt,
                DecidedByTheSystem = action.DecidedByTheSystem,
                CanUndo = action.UndoneAt == null && blocked is null,
                UndoBlockedReason = action.UndoneAt == null ? blocked : null
            });
        }

        return new TidyUpLogResponse { Actions = lines };
    }

    /// <summary>
    /// DESHACER un movimiento del tarifario: cada fila de precio que toco vuelve EXACTAMENTE a como estaba
    /// (a la que se movio se la devuelve, a la que fue pisada se le restauran sus importes, la que quedo
    /// escondida se vuelve a mostrar) y, si fue una UNION, el producto absorbido vuelve a prenderse. La
    /// fila del rastro no se borra: queda marcada como deshecha.
    ///
    /// <para>Tambien deshace una CORRECCION de habitacion (M-18), que usa el mismo rastro. La diferencia es
    /// que ahi no hay producto que volver a prender: solo vuelven los precios.</para>
    /// </summary>
    public async Task UndoTidyUpActionAsync(Guid actionPublicId, CancellationToken ct)
    {
        // Deshacer toca varias filas de plata a la vez: o vuelven TODAS o no vuelve ninguna. Va en la misma
        // transaccion con reintento que usa el unir (sin esto, un choque a mitad de camino dejaba media
        // vuelta atras aplicada).
        try
        {
            await RunInTransactionAsync(async () =>
            {
                await UndoInsideTransactionAsync(actionPublicId, ct);
                return true;
            }, ct);
        }
        catch (CatalogTidyUpNotReversibleException ex)
        {
            // El motivo se escribe FUERA de la transaccion a proposito: la transaccion se deshizo entera al
            // rechazar, asi que escribirlo adentro no dejaria rastro. Asi la bandeja lo muestra sin volver
            // a calcularlo.
            await RememberUndoBlockedReasonAsync(actionPublicId, ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// Deja escrito por que ese movimiento ya no se puede deshacer. Es una ayuda para la bandeja, no una
    /// verdad critica: si falla, se loguea y listo — el motivo se vuelve a calcular en la proxima lectura.
    ///
    /// <para><b>Solo se guardan los motivos DEFINITIVOS.</b> "Deshacé primero el movimiento más nuevo" es
    /// temporal por definicion: en cuanto alguien deshace el de arriba, este vuelve a poder deshacerse.
    /// Guardarlo lo dejaria trabado para siempre, porque el motivo guardado le gana al recalculo.</para>
    /// </summary>
    private async Task RememberUndoBlockedReasonAsync(Guid actionPublicId, string reason, CancellationToken ct)
    {
        if (string.Equals(reason, UndoBlockedByLaterTidyUpMessage, StringComparison.Ordinal)) return;

        try
        {
            // Se arranca con el ChangeTracker LIMPIO: lo unico que tiene que viajar en este guardado es el
            // motivo. Si el intento de deshacer alcanzo a tocar entidades antes de rechazar, la transaccion
            // ya las descarto en la base — pero seguirian marcadas como modificadas en memoria, y este
            // SaveChanges las volveria a escribir. Hoy no pasa; el dia que alguien agregue una escritura
            // antes del rechazo, pasaria en silencio.
            _db.ChangeTracker.Clear();

            var action = await _db.CatalogTidyUpActions
                .FirstOrDefaultAsync(item => item.PublicId == actionPublicId, ct);
            if (action is null || action.UndoBlockedReason is not null) return;

            action.UndoBlockedReason = reason;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Bibliotecario: no se pudo guardar el motivo por el que {Action} ya no se puede deshacer.",
                actionPublicId);
        }
    }

    private async Task UndoInsideTransactionAsync(Guid actionPublicId, CancellationToken ct)
    {
        var action = await _db.CatalogTidyUpActions
            .Include(item => item.SaleChanges)
            .FirstOrDefaultAsync(item => item.PublicId == actionPublicId, ct)
            ?? throw new CatalogTidyUpNotFoundException("No encontramos ese movimiento para deshacer.");

        if (action.UndoneAt != null) return; // ya se deshizo: idempotente

        var blocked = action.UndoBlockedReason ?? await ResolveUndoBlockerAsync(action, ct);
        if (blocked is not null)
        {
            // El motivo lo deja escrito el llamador, fuera de esta transaccion (que se va a deshacer entera).
            throw new CatalogTidyUpNotReversibleException(blocked);
        }

        var saleIds = action.SaleChanges.Select(change => change.RateSupplierSaleId).ToList();
        var sales = await _db.RateSupplierSales
            .Where(sale => saleIds.Contains(sale.Id))
            .ToListAsync(ct);

        foreach (var change in action.SaleChanges)
        {
            var sale = sales.FirstOrDefault(item => item.Id == change.RateSupplierSaleId);
            if (sale is null) continue; // la fila ya no esta: el guard de arriba ya decidio que igual vale

            switch (change.Kind)
            {
                case CatalogTidyUpSaleChangeKinds.Moved:
                    sale.RateId = change.PreviousRateId;
                    sale.VariantKey = change.PreviousVariantKey;
                    sale.VariantLabel = change.PreviousVariantLabel;
                    break;

                case CatalogTidyUpSaleChangeKinds.Overwritten:
                    // Le devolvemos TODOS los valores que tenia antes de que la pisaran.
                    sale.LastSoldAt = change.PreviousSoldAt;
                    sale.LastNetCost = change.PreviousNetCost;
                    sale.LastTax = change.PreviousTax;
                    sale.LastSalePrice = change.PreviousSalePrice;
                    sale.LastCurrency = change.PreviousCurrency;
                    sale.LastPriceUnit = change.PreviousPriceUnit;
                    sale.LastReservaId = change.PreviousReservaId;
                    sale.SalesCount = change.PreviousSalesCount;
                    sale.VariantKey = change.PreviousVariantKey;
                    sale.VariantLabel = change.PreviousVariantLabel;
                    break;

                case CatalogTidyUpSaleChangeKinds.Hidden:
                    // Vuelve a mostrarse, en su producto de siempre.
                    sale.AbsorbedByTidyUpActionId = null;
                    sale.RateId = change.PreviousRateId;
                    sale.VariantKey = change.PreviousVariantKey;
                    sale.VariantLabel = change.PreviousVariantLabel;
                    break;

                case CatalogTidyUpSaleChangeKinds.CreatedFromManualPrice:
                    // Esta fila la creo la union para mudar un precio cargado a mano. Al deshacer se
                    // esconde (no se borra) — el precio original nunca se toco: sigue en su producto.
                    sale.AbsorbedByTidyUpActionId = action.Id;
                    break;
            }
        }

        // Corregir una habitacion NO apago ningun producto: no hay nada que volver a prender. Si acá
        // entrara igual, le pisaria el nombre al producto con la etiqueta vieja de la habitacion.
        if (!string.Equals(action.Kind, CatalogTidyUpKinds.VariantRenamed, StringComparison.Ordinal))
        {
            await RestoreAbsorbedProductAsync(action, ct);
        }

        action.UndoneAt = DateTime.UtcNow;
        action.UndoneByUserId = CurrentUserId();

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Vuelve a prender el producto ABSORBIDO y le devuelve su nombre. Solo corre en las uniones: una
    /// correccion de habitacion no absorbe ningun producto.
    /// </summary>
    private async Task RestoreAbsorbedProductAsync(CatalogTidyUpAction action, CancellationToken ct)
    {
        var absorbed = await _db.Rates.FirstOrDefaultAsync(rate => rate.Id == action.AbsorbedRateId, ct)
            ?? throw new CatalogTidyUpNotFoundException("No encontramos el producto que se había unido.");

        absorbed.IsActive = true;
        absorbed.MergedIntoRateId = null;
        absorbed.MergedAt = null;
        absorbed.ProductName = string.IsNullOrEmpty(action.AbsorbedProductName)
            ? action.AbsorbedName
            : action.AbsorbedProductName;
        if (IsHotel(absorbed)) absorbed.HotelName = action.AbsorbedName;
        absorbed.SearchName = TextNormalizer.NormalizeForCatalog(
            IsHotel(absorbed) ? action.AbsorbedName : absorbed.ProductName);
        absorbed.UpdatedAt = DateTime.UtcNow;
    }

    // Motivos por los que un movimiento deja de poder deshacerse solo. Son el texto EXACTO que ve la
    // persona: dicen que pasa y que hacer, sin una sola palabra tecnica.
    private const string UndoBlockedByNewSalesMessage =
        "Después de esto hubo ventas nuevas; ya no se puede deshacer solo.";
    private const string UndoBlockedByMissingRowMessage =
        "Esa memoria de precios ya no está en el sistema; este movimiento ya no se puede deshacer solo.";
    private const string UndoBlockedByChangedRowMessage =
        "Los precios de ese producto cambiaron desde entonces; este movimiento ya no se puede deshacer solo.";
    private const string UndoBlockedByLaterTidyUpMessage =
        "Después de esto se ordenaron esos mismos precios otra vez. Deshacé primero el movimiento más nuevo.";
    private const string UndoBlockedByOccupiedSlotMessage =
        "Ese producto ya tiene otro precio para esa habitación; este movimiento ya no se puede deshacer solo.";

    /// <summary>
    /// ¿Hay algo que impida deshacer este movimiento con fidelidad? Devuelve el motivo en criollo, o null
    /// si se puede. Cinco cosas lo vuelven irreversible:
    /// <list type="bullet">
    ///   <item><b>DESPUES de este hubo OTRO movimiento sobre alguno de esos mismos precios</b> — la regla
    ///   madre: se deshace en orden inverso, del mas nuevo al mas viejo. Ver
    ///   <see cref="WasTouchedByANewerTidyUpAsync"/>;</item>
    ///   <item>una VENTA NUEVA piso alguna de las filas movidas (deshacer se la llevaria al producto
    ///   equivocado y le borraria la habitacion);</item>
    ///   <item>una fila que el movimiento toco ya no existe (un borrado selectivo);</item>
    ///   <item>la fila que hoy tiene ese id NO es la misma de antes (el "Empezar de cero" reinicia los
    ///   numeros y otro dato puede haber quedado con el mismo id), o ya no esta donde este movimiento la
    ///   dejo (una mano ajena al rastro la movio);</item>
    ///   <item>el casillero al que tiene que volver (producto + operador + habitacion) ya lo ocupa otro
    ///   precio: devolverla ahi chocaria contra la regla de "una sola fila por combinacion".</item>
    /// </list>
    /// </summary>
    private async Task<string?> ResolveUndoBlockerAsync(CatalogTidyUpAction action, CancellationToken ct)
    {
        var changes = action.SaleChanges.Count > 0
            ? action.SaleChanges.ToList()
            : await _db.CatalogTidyUpSaleChanges.AsNoTracking()
                .Where(change => change.TidyUpActionId == action.Id)
                .ToListAsync(ct);

        if (changes.Count == 0) return null;

        var saleIds = changes.Select(change => change.RateSupplierSaleId).ToList();

        // LA REGLA MADRE, antes que cualquier otra: si a alguno de esos precios lo tocó un movimiento MAS
        // NUEVO que sigue vigente, este no se puede deshacer todavia. Reemplaza a andar adivinando "que le
        // hizo cada movimiento a cada eje" (producto, habitacion, importes): el rastro ya sabe QUE FILAS
        // toco cada uno, y con eso alcanza para saber que hay algo mas nuevo encima.
        if (await WasTouchedByANewerTidyUpAsync(action, saleIds, ct)) return UndoBlockedByLaterTidyUpMessage;

        var sales = await _db.RateSupplierSales
            .AsNoTracking()
            .Where(sale => saleIds.Contains(sale.Id))
            .ToListAsync(ct);

        // Los precios que HOY viven en los productos a los que van a volver las filas. Con esto se puede
        // ver, antes de tocar nada, si el casillero de destino quedo ocupado por una venta posterior.
        var destinationRateIds = changes.Select(change => change.PreviousRateId).Distinct().ToList();
        var rowsInDestination = await _db.RateSupplierSales
            .AsNoTracking()
            .Where(sale => destinationRateIds.Contains(sale.RateId) && sale.AbsorbedByTidyUpActionId == null)
            .ToListAsync(ct);

        foreach (var change in changes)
        {
            var sale = sales.FirstOrDefault(item => item.Id == change.RateSupplierSaleId);
            if (sale is null) return UndoBlockedByMissingRowMessage;

            // El operador es la huella que distingue "la misma fila" de "otra fila con el mismo numero".
            if (sale.SupplierId != change.PreviousSupplierId) return UndoBlockedByChangedRowMessage;

            // Venta nueva DESPUES de la union: deshacer se la llevaria al producto equivocado.
            if (sale.LastSoldAt > action.PerformedAt) return UndoBlockedByNewSalesMessage;

            if (!IsStillWhereThisTidyUpLeftIt(action, change, sale)) return UndoBlockedByLaterTidyUpMessage;

            if (IsDestinationSlotTaken(change, sale, rowsInDestination)) return UndoBlockedByOccupiedSlotMessage;
        }

        return null;
    }

    /// <summary>
    /// ¿A alguna de esas filas de precio la tocó un movimiento MAS NUEVO que sigue vigente? Si la respuesta
    /// es si, este movimiento no se puede deshacer todavia: primero hay que deshacer el de arriba.
    ///
    /// <para><b>Por que este control es el importante y los de abajo son la red</b>: un movimiento posterior
    /// puede tocar una fila de muchas formas —mudarla de producto, corregirle la habitacion, o pisarle los
    /// importes SIN moverla ni cambiarle la clave (el caso del "gemelo")—. Controlar eje por eje deja
    /// siempre un caso afuera; el gemelo pisado en el lugar, por ejemplo, se veia intacto y deshacer el
    /// movimiento viejo se llevaba los importes del otro precio y hacia desaparecer una habitacion. El
    /// rastro, en cambio, sabe exactamente QUE FILAS toco cada movimiento: con eso alcanza y sobra.</para>
    ///
    /// <para>Se compara por Id de accion (crece siempre) y solo cuentan las que NO se deshicieron: apenas
    /// alguien deshace el movimiento de arriba, este se vuelve a poder deshacer solo.</para>
    /// </summary>
    private async Task<bool> WasTouchedByANewerTidyUpAsync(
        CatalogTidyUpAction action, List<int> saleIds, CancellationToken ct)
    {
        var query =
            from change in _db.CatalogTidyUpSaleChanges.AsNoTracking()
            join newer in _db.CatalogTidyUpActions.AsNoTracking() on change.TidyUpActionId equals newer.Id
            where saleIds.Contains(change.RateSupplierSaleId)
                  && newer.Id > action.Id
                  && newer.UndoneAt == null
            select change.Id;

        return await query.AnyAsync(ct);
    }

    /// <summary>
    /// RED DE REFUERZO: ¿la fila sigue donde la dejo este movimiento? Lo que pudo haberla movido con rastro
    /// ya lo detecto <see cref="WasTouchedByANewerTidyUpAsync"/>; esto cubre lo que NO deja rastro (una mano
    /// en la base, o un camino de codigo futuro que mueva filas sin registrarlo). Ante la duda, no se
    /// deshace.
    /// </summary>
    private static bool IsStillWhereThisTidyUpLeftIt(
        CatalogTidyUpAction action, CatalogTidyUpSaleChange change, RateSupplierSale sale)
        => change.Kind switch
        {
            // La movida y la pisada quedaron VISIBLES, colgando del producto que sobrevivio.
            CatalogTidyUpSaleChangeKinds.Moved or CatalogTidyUpSaleChangeKinds.Overwritten
                => sale.RateId == action.SurvivingRateId && sale.AbsorbedByTidyUpActionId == null,

            // La escondida tiene que seguir escondida POR ESTE movimiento (si la escondio otro, manda el otro).
            CatalogTidyUpSaleChangeKinds.Hidden
                => sale.AbsorbedByTidyUpActionId == action.Id,

            // La creada para mudar un precio a mano vive en el sobreviviente y todavia no la escondio nadie.
            CatalogTidyUpSaleChangeKinds.CreatedFromManualPrice
                => sale.RateId == action.SurvivingRateId
                   && (sale.AbsorbedByTidyUpActionId == null || sale.AbsorbedByTidyUpActionId == action.Id),

            _ => true
        };

    /// <summary>
    /// ¿El casillero (producto + operador + habitacion) al que vuelve la fila ya lo ocupa OTRO precio? Pasa
    /// cuando, despues de unir, alguien vendio el producto viejo y el sistema aprendio un precio nuevo ahi.
    /// La fila creada para mudar un precio a mano no cuenta: al deshacer se esconde, no vuelve a ningun lado.
    /// </summary>
    private static bool IsDestinationSlotTaken(
        CatalogTidyUpSaleChange change, RateSupplierSale sale, List<RateSupplierSale> visibleRowsInDestination)
    {
        if (string.Equals(
                change.Kind, CatalogTidyUpSaleChangeKinds.CreatedFromManualPrice, StringComparison.Ordinal))
        {
            return false;
        }

        return visibleRowsInDestination.Any(other =>
            other.Id != sale.Id
            && other.RateId == change.PreviousRateId
            && other.SupplierId == change.PreviousSupplierId
            && string.Equals(other.VariantKey, change.PreviousVariantKey, StringComparison.Ordinal));
    }

    // ============================================================
    // El corazon: unir dos productos
    // ============================================================

    private async Task<MergeProductsResult> MergeAsync(
        Guid survivorPublicId, Guid absorbedPublicId, bool decidedByTheSystem, CancellationToken ct)
    {
        return await RunInTransactionAsync(async () =>
        {
            var survivor = await FindRateAsync(survivorPublicId, ct);
            var absorbed = await FindRateAsync(absorbedPublicId, ct);

            // ANTI DOBLE CLIC (va ANTES de validar estados): si este producto YA fue absorbido por ESTE
            // mismo sobreviviente, el pedido esta repetido — el segundo clic, la pestaña duplicada, el
            // reintento de la red. La respuesta correcta es devolver la union que ya existe, no un error:
            // el resultado que el usuario pidio ya esta.
            var recent = absorbed.MergedIntoRateId == survivor.Id
                ? await FindExistingMergeAsync(survivor.Id, absorbed.Id, ct)
                : null;
            if (recent is not null)
            {
                return new MergeProductsResult
                {
                    SurvivorPublicId = survivor.PublicId,
                    SurvivorName = ProductDisplayName(survivor),
                    MovedPrices = recent.SaleChanges.Count(change =>
                        change.Kind == CatalogTidyUpSaleChangeKinds.Moved),
                    VariantLabelRescued = string.IsNullOrEmpty(recent.VariantLabelRescued)
                        ? null
                        : recent.VariantLabelRescued,
                    TidyUpActionPublicId = recent.PublicId
                };
            }

            EnsureCanBeMerged(survivor, absorbed);

            var absorbedName = ProductDisplayName(absorbed);

            // ¿La habitacion venia metida en el nombre del absorbido? Entonces se rescata y pasa a ser la
            // variante de las filas que se mudan (V14: nada se pierde).
            var rescued = IsHotel(absorbed)
                ? CatalogProductNameParser.ParseHotelName(absorbedName, absorbed.MealPlan)
                : default;

            var action = new CatalogTidyUpAction
            {
                Kind = rescued.HadVariantInsideTheName
                    ? CatalogTidyUpKinds.SuffixConvertedToVariant
                    : CatalogTidyUpKinds.ProductsMerged,
                SurvivingRateId = survivor.Id,
                AbsorbedRateId = absorbed.Id,
                SurvivingName = ProductDisplayName(survivor),
                AbsorbedName = absorbedName,
                AbsorbedProductName = absorbed.ProductName,
                VariantLabelRescued = rescued.HadVariantInsideTheName ? rescued.VariantLabel : string.Empty,
                VariantKeyRescued = rescued.HadVariantInsideTheName ? rescued.VariantKey : string.Empty,
                DecidedByTheSystem = decidedByTheSystem,
                // Aunque el criterio sea automatico, SIEMPRE queda quien apreto el boton que lo disparo.
                PerformedByUserId = CurrentUserId()
            };
            _db.CatalogTidyUpActions.Add(action);
            await _db.SaveChangesAsync(ct); // necesitamos su Id para colgarle las fotos

            var movedPrices = await MoveSalesAsync(survivor, absorbed, rescued, action, ct);
            await MoveManualPriceAsync(survivor, absorbed, rescued, action, ct);

            // El absorbido NO se borra: se apaga y queda apuntando al que quedo (2026-08-03).
            absorbed.IsActive = false;
            absorbed.MergedIntoRateId = survivor.Id;
            absorbed.MergedAt = DateTime.UtcNow;
            absorbed.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            return new MergeProductsResult
            {
                SurvivorPublicId = survivor.PublicId,
                SurvivorName = ProductDisplayName(survivor),
                MovedPrices = movedPrices,
                VariantLabelRescued = rescued.HadVariantInsideTheName ? rescued.VariantLabel : null,
                TidyUpActionPublicId = action.PublicId
            };
        }, ct);
    }

    /// <summary>
    /// Estados en los que NO se puede unir. Los mensajes son los que ve la persona: dicen que pasa y que
    /// hacer, sin nombres internos.
    /// </summary>
    private static void EnsureCanBeMerged(Rate survivor, Rate absorbed)
    {
        if (survivor.Id == absorbed.Id)
        {
            throw new RateValidationException("Elegí dos productos distintos para unir.");
        }

        if (!string.Equals(
                TextNormalizer.NormalizeForMatch(survivor.ServiceType),
                TextNormalizer.NormalizeForMatch(absorbed.ServiceType), StringComparison.Ordinal))
        {
            throw new RateValidationException("Esos dos productos no son del mismo tipo, no se pueden unir.");
        }

        if (survivor.MergedIntoRateId != null)
        {
            throw new RateValidationException(
                "Ese producto ya se había unido a otro. Elegí el que quedó y probá de nuevo.");
        }

        if (!survivor.IsActive)
        {
            throw new RateValidationException("Ese producto está desactivado: no puede quedarse con los precios de otro.");
        }

        if (!absorbed.IsActive || absorbed.MergedIntoRateId != null)
        {
            throw new RateValidationException("Ese producto ya no está en la lista: puede que alguien lo haya unido antes.");
        }
    }

    /// <summary>La union VIGENTE de ese par, si ya existe. Es lo que devuelve el pedido repetido.</summary>
    private async Task<CatalogTidyUpAction?> FindExistingMergeAsync(int survivorId, int absorbedId, CancellationToken ct)
        => await _db.CatalogTidyUpActions
            .Include(action => action.SaleChanges)
            .Where(action => action.SurvivingRateId == survivorId
                && action.AbsorbedRateId == absorbedId
                && action.UndoneAt == null)
            .OrderByDescending(action => action.PerformedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Muda la memoria de precios del absorbido al que queda, fotografiando cada fila antes de tocarla.
    /// Cuando dos filas chocan (mismo operador, misma habitacion) gana la MAS NUEVA, y la otra queda
    /// ESCONDIDA — nunca borrada.
    /// </summary>
    private async Task<int> MoveSalesAsync(
        Rate survivor, Rate absorbed, CatalogProductNameParser.ParsedName rescued,
        CatalogTidyUpAction action, CancellationToken ct)
    {
        var absorbedSales = await _db.RateSupplierSales
            .Where(sale => sale.RateId == absorbed.Id && sale.AbsorbedByTidyUpActionId == null)
            .ToListAsync(ct);

        var survivorSales = await _db.RateSupplierSales
            .Where(sale => sale.RateId == survivor.Id && sale.AbsorbedByTidyUpActionId == null)
            .ToListAsync(ct);

        var moved = 0;
        foreach (var sale in absorbedSales)
        {
            var previous = CatalogTidyUpTrail.Snapshot(sale, action.Id);

            if (rescued.HadVariantInsideTheName && sale.VariantKey.Length == 0)
            {
                sale.VariantKey = rescued.VariantKey;
                sale.VariantLabel = rescued.VariantLabel;
            }

            var clash = survivorSales.FirstOrDefault(existing =>
                existing.SupplierId == sale.SupplierId
                && string.Equals(existing.VariantKey, sale.VariantKey, StringComparison.Ordinal));

            if (clash != null)
            {
                if (sale.LastSoldAt > clash.LastSoldAt)
                {
                    // La foto del que va a ser PISADO, para poder devolverle sus importes al deshacer.
                    CatalogTidyUpTrail.RecordOverwrite(_db, CatalogTidyUpTrail.Snapshot(clash, action.Id));

                    clash.LastSoldAt = sale.LastSoldAt;
                    clash.LastNetCost = sale.LastNetCost;
                    clash.LastTax = sale.LastTax;
                    clash.LastSalePrice = sale.LastSalePrice;
                    clash.LastCurrency = sale.LastCurrency;
                    clash.LastPriceUnit = sale.LastPriceUnit;
                    clash.LastReservaId = sale.LastReservaId ?? clash.LastReservaId;
                    clash.VariantLabel = sale.VariantLabel;
                    // El contador de ventas NO se suma: la fila perdedora sigue existiendo (escondida) con
                    // su propio contador. Sumarlos contaria dos veces la misma venta.
                }

                // La que pierde queda ESCONDIDA, jamas borrada.
                CatalogTidyUpTrail.Hide(_db, sale, previous, action.Id);
                continue;
            }

            CatalogTidyUpTrail.RecordMove(_db, previous);

            sale.RateId = survivor.Id;
            survivorSales.Add(sale);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// El precio que alguien cargo A MANO en el producto absorbido tambien se muda (no puede desaparecer
    /// de la vista solo porque el producto se unio). Se crea una fila de memoria marcada como carga a mano
    /// —no como venta— con la fecha en que ese precio quedo cargado.
    /// </summary>
    private async Task MoveManualPriceAsync(
        Rate survivor, Rate absorbed, CatalogProductNameParser.ParsedName rescued,
        CatalogTidyUpAction action, CancellationToken ct)
    {
        // Solo aplica si el absorbido mostraba un precio propio: es decir, si NO tenia ventas aprendidas.
        var hadSales = await _db.RateSupplierSales
            .AnyAsync(sale => sale.RateId == absorbed.Id && sale.AbsorbedByTidyUpActionId == null, ct);
        if (hadSales) return;

        var price = absorbed.SalePrice > 0m ? absorbed.SalePrice : absorbed.NetCost;
        if (price <= 0m) return;
        if (absorbed.SupplierId is not int supplierId || supplierId <= 0) return;

        var variant = rescued.HadVariantInsideTheName
            ? (rescued.VariantKey, rescued.VariantLabel)
            : CatalogVariant.For(
                absorbed.ServiceType,
                roomType: absorbed.RoomType, mealPlan: absorbed.MealPlan, fineName: absorbed.RoomCategory,
                cabinClass: absorbed.CabinClass, vehicleType: absorbed.VehicleType);

        // Si el que queda YA tiene un precio de ese operador para esa habitacion, gana el suyo (aprendido
        // de una venta real). El precio a mano no se pierde: sigue en su producto, que no se borro.
        var alreadyThere = await _db.RateSupplierSales.AnyAsync(
            sale => sale.RateId == survivor.Id
                && sale.SupplierId == supplierId
                && sale.VariantKey == variant.Item1
                && sale.AbsorbedByTidyUpActionId == null, ct);
        if (alreadyThere) return;

        var loadedAt = absorbed.UpdatedAt ?? absorbed.CreatedAt;
        var manualRow = new RateSupplierSale
        {
            RateId = survivor.Id,
            SupplierId = supplierId,
            LastSoldAt = loadedAt,
            LastNetCost = absorbed.NetCost,
            LastTax = absorbed.Tax,
            LastSalePrice = absorbed.SalePrice,
            LastCurrency = absorbed.Currency,
            LastPriceUnit = absorbed.PriceUnit ?? CatalogPriceUnits.Servicio,
            SalesCount = 0,           // no es una venta: es un precio que alguien cargo
            FromManualLoad = true,
            VariantKey = variant.Item1,
            VariantLabel = variant.Item2
        };
        _db.RateSupplierSales.Add(manualRow);
        await _db.SaveChangesAsync(ct); // necesitamos su Id para la foto

        _db.CatalogTidyUpSaleChanges.Add(new CatalogTidyUpSaleChange
        {
            TidyUpActionId = action.Id,
            RateSupplierSaleId = manualRow.Id,
            Kind = CatalogTidyUpSaleChangeKinds.CreatedFromManualPrice,
            PreviousRateId = absorbed.Id,
            PreviousSupplierId = supplierId,
            PreviousVariantKey = variant.Item1,
            PreviousVariantLabel = variant.Item2,
            PreviousSoldAt = loadedAt,
            PreviousNetCost = absorbed.NetCost,
            PreviousTax = absorbed.Tax,
            PreviousSalePrice = absorbed.SalePrice,
            PreviousCurrency = absorbed.Currency,
            PreviousPriceUnit = manualRow.LastPriceUnit,
            PreviousReservaId = null,
            PreviousSalesCount = 0
        });
    }

    /// <summary>
    /// Corre el cuerpo dentro de UNA transaccion Serializable con reintento (mismo patron y mismos motivos
    /// que el alta de servicio con catalogo): dos personas uniendo el mismo producto a la vez chocan, se
    /// reintenta, y en el reintento el segundo ve el estado ya unido y lo rechaza con un mensaje claro.
    /// En motores no relacionales (tests InMemory) se ejecuta derecho.
    /// </summary>
    private async Task<T> RunInTransactionAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return await body();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
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

    // ============================================================
    // Deteccion de grupos (lo mismo alimenta la bandeja y la pasada automatica)
    // ============================================================

    private sealed record CandidateInfo(Rate Rate, int PriceCount, bool RescuedFromNameSuffix, string VariantLabelToRescue);
    private sealed record GroupInfo(Rate Survivor, int SurvivorPriceCount, List<CandidateInfo> Candidates);

    /// <summary>
    /// Arma los grupos "uno arriba, los parecidos abajo". Dos productos entran al mismo grupo cuando,
    /// despues de sacarles la habitacion del nombre y normalizar, quedan con el MISMO nombre y la MISMA
    /// ciudad. Se excluyen los pares que alguien ya marco como "es otro".
    /// </summary>
    private async Task<List<GroupInfo>> BuildGroupsAsync(CancellationToken ct)
    {
        // "Otro" queda AFUERA del tarifario (addendum firmado 2026-08-08, V17=C): si no se lista, tampoco
        // tiene sentido proponer unir dos de esos ni que el bibliotecario los toque solo.
        var cajonDeSastre = CatalogServiceTypes.Otro.ToLowerInvariant();
        var rates = await _db.Rates
            .Where(rate => rate.IsActive && rate.ServiceType.ToLower() != cajonDeSastre)
            .ToListAsync(ct);

        if (rates.Count < 2) return new List<GroupInfo>();

        var priceCounts = await _db.RateSupplierSales
            .AsNoTracking()
            .Where(sale => sale.AbsorbedByTidyUpActionId == null)
            .GroupBy(sale => sale.RateId)
            .Select(group => new { RateId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.RateId, row => row.Count, ct);

        var notDuplicatePairs = await _db.CatalogNotDuplicatePairs
            .AsNoTracking()
            .Select(pair => new { pair.LowRateId, pair.HighRateId })
            .ToListAsync(ct);
        var excluded = notDuplicatePairs
            .Select(pair => (pair.LowRateId, pair.HighRateId))
            .ToHashSet();

        // Clave del grupo: tipo + nombre YA sin la habitacion + ciudad, todo normalizado.
        var buckets = new Dictionary<string, List<(Rate Rate, CatalogProductNameParser.ParsedName Parsed)>>(
            StringComparer.Ordinal);

        foreach (var rate in rates)
        {
            var displayName = ProductDisplayName(rate);
            var parsed = IsHotel(rate)
                ? CatalogProductNameParser.ParseHotelName(displayName, rate.MealPlan)
                : new CatalogProductNameParser.ParsedName(displayName, string.Empty, string.Empty, false);

            var key = string.Join('|',
                TextNormalizer.NormalizeForMatch(rate.ServiceType),
                TextNormalizer.NormalizeForCatalog(parsed.CleanName),
                TextNormalizer.NormalizeForCatalog(rate.City));

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<(Rate, CatalogProductNameParser.ParsedName)>();
                buckets[key] = bucket;
            }
            bucket.Add((rate, parsed));
        }

        var groups = new List<GroupInfo>();
        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;

            // Se queda el que tiene el nombre LIMPIO (sin habitacion adentro); a igualdad, el que mas
            // precios aprendidos tiene; a igualdad, el mas viejo (es el que la gente ya conoce).
            var ordered = bucket
                .OrderBy(item => item.Parsed.HadVariantInsideTheName ? 1 : 0)
                .ThenByDescending(item => priceCounts.TryGetValue(item.Rate.Id, out var count) ? count : 0)
                .ThenBy(item => item.Rate.Id)
                .ToList();

            var survivor = ordered[0];
            var candidates = new List<CandidateInfo>();

            foreach (var (rate, parsed) in ordered.Skip(1))
            {
                var pair = (Math.Min(survivor.Rate.Id, rate.Id), Math.Max(survivor.Rate.Id, rate.Id));
                if (excluded.Contains(pair)) continue;

                candidates.Add(new CandidateInfo(
                    rate,
                    priceCounts.TryGetValue(rate.Id, out var count) ? count : 0,
                    parsed.HadVariantInsideTheName,
                    parsed.VariantLabel));
            }

            if (candidates.Count == 0) continue;

            groups.Add(new GroupInfo(
                survivor.Rate,
                priceCounts.TryGetValue(survivor.Rate.Id, out var survivorCount) ? survivorCount : 0,
                candidates));
        }

        return groups
            .OrderBy(group => ProductDisplayName(group.Survivor), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ============================================================
    // Piezas chicas
    // ============================================================

    private async Task<Rate> FindRateAsync(Guid publicId, CancellationToken ct)
        => await _db.Rates.FirstOrDefaultAsync(rate => rate.PublicId == publicId, ct)
           ?? throw new CatalogProductNotFoundException("No encontramos ese producto en el tarifario.");

    private static bool IsHotel(Rate rate)
        => string.Equals(TextNormalizer.NormalizeForMatch(rate.ServiceType), "hotel", StringComparison.Ordinal);

    private static string ProductDisplayName(Rate rate)
        => IsHotel(rate) && !string.IsNullOrWhiteSpace(rate.HotelName) ? rate.HotelName! : rate.ProductName;

    private static string? ProductSubtitle(Rate rate)
        => string.IsNullOrWhiteSpace(rate.City) ? null : rate.City.Trim();

    private string? CurrentUserId()
        => _httpContextAccessor?.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}
