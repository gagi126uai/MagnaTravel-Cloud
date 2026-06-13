using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// Auditoria ERP 2026-06-12 (hallazgo #1): UNICO punto que devenga/actualiza la comision del vendedor de
/// una reserva. Se invoca al final de <see cref="ReservaMoneyPersister"/> (el unico chokepoint de la plata
/// de la reserva), asi que CUALQUIER cambio que mueva el saldo (cobro, mutacion de servicio, anulacion de
/// factura, cancelacion) dispara este recalculo SIN tocar cada call-site.
///
/// <para><b>Idempotencia</b>: el recalculo es deterministico. En cada corrida se vuelve a calcular la
/// comision por moneda y se UPSERTEA la fila (ReservaId + Vendedor + Moneda); nunca se inserta una fila
/// duplicada (lo garantiza el indice unico). Recobrar la misma reserva o recalcular dos veces da el mismo
/// resultado, no acumula.</para>
///
/// <para><b>Tope cero</b>: si la reserva deja de devengar (se canceló, el saldo volvio a positivo, se
/// quito el vendedor, o el toggle se apago), las filas existentes se ponen en <c>Amount = 0</c>. NUNCA se
/// borran (se conservan para auditoria de "esta reserva alguna vez devengo X") y NUNCA quedan negativas.</para>
///
/// <para><b>Toggle (decision del dueño)</b>: si <c>EnableSellerCommissions</c> esta en false, este persister
/// es un no-op TOTAL: no calcula ni escribe nada. Comportamiento byte-identico a antes de esta feature.</para>
///
/// <para><b>Porcentaje (decision del dueño, 2026-06-13)</b>: el % es UNO SOLO y parejo para TODAS las reservas,
/// tomado de <c>OperationalFinanceSettings.SellerCommissionPercent</c>. Ya NO se resuelve por operador/tipo
/// contra <c>CommissionRule</c> (el dueño saco las reglas). Con % global = 0, este persister tampoco escribe
/// nada (la posicion segura: prender el interruptor no devenga hasta elegir un numero).</para>
/// </summary>
internal static class CommissionAccrualPersister
{
    /// <summary>
    /// Recalcula y persiste las comisiones de la reserva. No llama a SaveChanges del lado del escalar:
    /// guarda sus propios cambios en una SaveChangesAsync separada (corre DESPUES de que el persister de
    /// plata ya escribio el saldo, por eso lee un Balance ya actualizado).
    /// </summary>
    public static async Task RecalculateAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        // 1) Toggle: si la feature esta apagada, no hacemos absolutamente nada (no-op).
        //    Leemos la fila singleton de settings sin tracking (solo necesitamos el flag).
        var settings = await db.OperationalFinanceSettings
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(ct);

        bool commissionsEnabled = settings?.EnableSellerCommissions ?? false;
        if (!commissionsEnabled) return;

        // Auditoria ERP 2026-06-13 (decision del dueño): UN SOLO porcentaje parejo para TODAS las reservas.
        // El % ya NO sale de la tabla CommissionRule (por operador/tipo): sale de este campo unico de settings.
        // Con 0 no se devenga nada aunque el interruptor este prendido (el dueño primero prende, despues elige
        // el numero). Cortamos temprano para no recorrer la reserva al pedo.
        decimal globalPercent = settings?.SellerCommissionPercent ?? 0m;
        if (globalPercent <= 0m) return;

        // 2) Cargamos la reserva con los 6 tipos de servicio (mismo grafo economico que el persister de
        //    plata). Necesitamos SupplierId/Currency/Commission/Status de cada servicio y el Balance/Status
        //    ya recalculado de la reserva.
        var reserva = await db.Reservas
            .Include(r => r.FlightSegments)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .Include(r => r.Servicios)
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct);

        if (reserva == null) return;

        // 3) El % es PAREJO para todo (decision del dueño): cualquier (proveedor, tipo) devenga el mismo
        //    porcentaje global. Ya no hay query a CommissionRule ni resolucion por "mejor match" — el dueño
        //    saco las reglas a proposito. La funcion ignora sus argumentos y devuelve siempre el % global.
        decimal ResolvePercent(int? supplierId, string serviceType) => globalPercent;

        // 4) Calculo PURO por moneda. Lista vacia = la reserva no devenga (tope cero / sin vendedor /
        //    estado no devengable / no cobrada).
        var lines = SellerCommissionCalculator.Calculate(reserva, ResolvePercent);

        // 5) Upsert idempotente contra las filas existentes de esta reserva.
        await SyncAccrualRowsAsync(db, reserva, lines, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sincroniza las filas <see cref="CommissionAccrual"/> de la reserva con el detalle por moneda.
    /// Upsertea las monedas que devengan y pone en 0 (tope cero) las que ya no devengan (sin borrarlas).
    /// </summary>
    private static async Task SyncAccrualRowsAsync(
        AppDbContext db,
        Reserva reserva,
        IReadOnlyList<SellerCommissionCalculator.CommissionLine> lines,
        CancellationToken ct)
    {
        var existingRows = await db.CommissionAccruals
            .Where(accrual => accrual.ReservaId == reserva.Id)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        // El vendedor responsable ACTUAL de la reserva. Si la reserva no tiene vendedor, 'lines' viene vacia
        // (la calculadora corta antes), asi que todas las filas existentes caen en el bloque de "tope cero".
        string? sellerUserId = reserva.ResponsibleUserId;
        string? sellerName = reserva.ResponsibleUserName;

        // Indexamos lo existente por moneda PARA EL VENDEDOR ACTUAL (clave funcional: una reserva tiene un
        // solo vendedor responsable a la vez; si cambia, las filas del vendedor anterior se ponen en 0).
        var existingByCurrencyForSeller = existingRows
            .Where(row => row.SellerUserId == sellerUserId)
            .ToDictionary(row => row.Currency, StringComparer.Ordinal);

        var currenciesAccruedNow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            currenciesAccruedNow.Add(line.Currency);

            if (existingByCurrencyForSeller.TryGetValue(line.Currency, out var row))
            {
                // Idempotente: re-escribimos monto/% sobre la misma fila. No duplica.
                row.Amount = line.Amount;
                row.RatePercent = line.EffectiveRatePercent;
                row.SellerName = sellerName;
                row.UpdatedAt = now;
                // No tocamos Status: si ya estaba "Liquidada", recalcular el monto no la "des-liquida".
            }
            else
            {
                db.CommissionAccruals.Add(new CommissionAccrual
                {
                    SellerUserId = sellerUserId!, // si lines no esta vacia, hay vendedor (la calc lo exige)
                    SellerName = sellerName,
                    ReservaId = reserva.Id,
                    Currency = line.Currency,
                    Amount = line.Amount,
                    RatePercent = line.EffectiveRatePercent,
                    Status = CommissionAccrualStatus.Devengada,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        // Tope cero: cualquier fila existente (de CUALQUIER vendedor) que ya no figura en el devengo actual
        // se pone en 0. Cubre cancelacion, saldo que vuelve a positivo, cambio de vendedor y moneda que
        // desaparecio. NO se borra: queda como historico en 0.
        foreach (var row in existingRows)
        {
            bool stillAccruing = row.SellerUserId == sellerUserId && currenciesAccruedNow.Contains(row.Currency);
            if (stillAccruing) continue;

            if (row.Amount != 0m || row.RatePercent != 0m)
            {
                row.Amount = 0m;
                row.RatePercent = 0m;
                row.UpdatedAt = now;
            }
        }
    }
}
