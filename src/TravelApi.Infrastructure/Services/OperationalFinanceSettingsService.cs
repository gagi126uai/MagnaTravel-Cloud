using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class OperationalFinanceSettingsService : IOperationalFinanceSettingsService
{
    private readonly AppDbContext _dbContext;

    public OperationalFinanceSettingsService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationalFinanceSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<OperationalFinanceSettingsDto> UpdateAsync(OperationalFinanceSettingsDto request, CancellationToken cancellationToken)
    {
        // B1.15 Fase 2a: el rango 0..100 de MaxDiscountPercentWithoutOverride se valida
        // en el DTO via [Range] (atributo de DataAnnotations). El binding de ASP.NET Core
        // dispara ModelState invalido y devuelve 400 antes de llegar al service.
        var entity = await GetEntityAsync(cancellationToken);
        entity.RequireFullPaymentForOperativeStatus = request.RequireFullPaymentForOperativeStatus;
        entity.RequireFullPaymentForVoucher = request.RequireFullPaymentForVoucher;
        entity.AfipInvoiceControlMode = string.IsNullOrWhiteSpace(request.AfipInvoiceControlMode)
            ? AfipInvoiceControlModes.AllowAgentOverrideWithReason
            : request.AfipInvoiceControlMode;
        entity.EnableUpcomingUnpaidReservationNotifications = request.EnableUpcomingUnpaidReservationNotifications;
        entity.UpcomingUnpaidReservationAlertDays = Math.Clamp(request.UpcomingUnpaidReservationAlertDays, 1, 60);
        entity.MaxDiscountPercentWithoutOverride = request.MaxDiscountPercentWithoutOverride;

        // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.0, 2026-05-22): persistimos
        // los 3 settings configurables. Los 2 flags maestros Fase 2
        // (EnablePartialCreditNoteRealEmission, EnableTotalPlusNewInvoiceAutoProcessing)
        // intencionalmente NO se exponen en el DTO ni se actualizan aca — mismo
        // criterio que con los flags FC1.2/FC1.3, que se manejan via SQL/seed.
        // El [Range] de DataAnnotations del DTO valida tolerancia y umbral antes
        // de llegar a este metodo (HTTP 400 si esta fuera de rango).
        //
        // B-002 fix (2026-05-26): update CONDICIONAL (no full-replace). Los 3
        // campos del DTO son nullable; solo se persiste si vinieron con valor.
        // Si el cliente manda null o omite el campo, dejamos el valor actual
        // de la entidad intacto. Esto evita regresion silenciosa cuando un PUT
        // legacy o un frontend que olvido un campo pisaria la config del admin
        // con el default del DTO. Patron: "patch-like via PUT" (no full PUT).
        if (request.IvaProrrateoMode.HasValue)
        {
            entity.IvaProrrateoMode = request.IvaProrrateoMode.Value;
        }
        if (request.PartialCreditNoteRoundingTolerance.HasValue)
        {
            entity.PartialCreditNoteRoundingTolerance = request.PartialCreditNoteRoundingTolerance.Value;
        }
        if (request.IdempotencyKeyStaleThresholdMinutes.HasValue)
        {
            entity.IdempotencyKeyStaleThresholdMinutes = request.IdempotencyKeyStaleThresholdMinutes.Value;
        }

        // ADR-012 MVP (facturar en USD, 2026-05-29): persistimos el flag maestro de
        // multimoneda. Update CONDICIONAL (patch-like, mismo criterio B-002): solo se aplica
        // si el request trae valor; si viene null u omitido, dejamos el valor actual intacto.
        // Asi un PUT legacy que no conozca este campo no lo apaga sin querer.
        if (request.EnableMultiCurrencyInvoicing.HasValue)
        {
            entity.EnableMultiCurrencyInvoicing = request.EnableMultiCurrencyInvoicing.Value;
        }

        // ADR-013 (Nota de Debito en cancelacion, 2026-06-01): persistimos el flag de emision
        // de ND. Update CONDICIONAL (patch-like, criterio B-002): solo se aplica si el request
        // trae valor. La pre-condicion (requiere EnableNewCancellationFlow) se valida mas abajo
        // junto a las demas cross-field rules, una vez aplicados todos los flags sobre 'entity'.
        if (request.EnableCancellationDebitNote.HasValue)
        {
            entity.EnableCancellationDebitNote = request.EnableCancellationDebitNote.Value;
        }

        // ADR-016 F0a (Base del copiloto de IA, 2026-06-03): persistimos el flag maestro del
        // copiloto. Update CONDICIONAL (patch-like, criterio B-002): solo se aplica si el request
        // trae valor. Es un flag de comportamiento puro y en F0a NO tiene dependencias con otros
        // flags (el flag del piloto que lo consumiria llega en F1), por eso NO hay validacion
        // cruzada para este flag mas abajo.
        if (request.EnableAiCopilot.HasValue)
        {
            entity.EnableAiCopilot = request.EnableAiCopilot.Value;
        }

        // ADR-017 F1.1 (catalogo find-or-create + fechas limite, 2026-06-05): persistimos los 2 flags
        // nuevos + el setting StaleCostReferenceDays. Update CONDICIONAL (patch-like, criterio B-002):
        // solo se aplica si el request trae valor. Son flags/setting de comportamiento puro, sin
        // dependencias con otros flags, por eso NO hay validacion cruzada para ellos mas abajo.
        if (request.EnableCatalogFindOrCreate.HasValue)
        {
            entity.EnableCatalogFindOrCreate = request.EnableCatalogFindOrCreate.Value;
        }
        if (request.EnableServiceDeadlineAlerts.HasValue)
        {
            entity.EnableServiceDeadlineAlerts = request.EnableServiceDeadlineAlerts.Value;
        }
        if (request.StaleCostReferenceDays.HasValue)
        {
            entity.StaleCostReferenceDays = request.StaleCostReferenceDays.Value;
        }
        // ADR-017 F1.4: ventana de las alertas de fechas limite. Clamp 1..60 igual que
        // UpcomingUnpaidReservationAlertDays (defensa adicional al [Range] del DTO).
        if (request.ServiceDeadlineAlertDays.HasValue)
        {
            entity.ServiceDeadlineAlertDays = Math.Clamp(request.ServiceDeadlineAlertDays.Value, 1, 60);
        }

        // Auditoria ERP 2026-06-12 (hallazgo #1): interruptor de comision del vendedor. Update CONDICIONAL
        // (patch-like, criterio B-002): solo se aplica si el request trae valor. Es un ajuste de negocio
        // puro, sin dependencias con otros flags, por eso NO hay validacion cruzada para el mas abajo.
        if (request.EnableSellerCommissions.HasValue)
        {
            entity.EnableSellerCommissions = request.EnableSellerCommissions.Value;
        }

        // Auditoria ERP 2026-06-13 (decision del dueño): porcentaje unico de comision para todas las reservas.
        // Update CONDICIONAL (patch-like, criterio B-002): solo se aplica si el request trae valor. El rango
        // 0..100 lo valida el [Range] del DTO (HTTP 400 antes de llegar aca); el Math.Clamp es defensa adicional
        // por si entra por un camino que no pasa por el binder (seed/test).
        if (request.SellerCommissionPercent.HasValue)
        {
            entity.SellerCommissionPercent = Math.Clamp(request.SellerCommissionPercent.Value, 0m, 100m);
        }

        entity.UpdatedAt = DateTime.UtcNow;

        // FC1.3.2 (ADR-009 §2.10, N-004 round 3, 2026-05-21): pre-condicion GR-002.
        // FC1.3 (EnablePartialCreditNotes=true) DEPENDE de FC1.2 (EnableNewCancellationFlow=true).
        // No tiene sentido tener NC parcial activa si el modulo base de cancelacion esta apagado.
        //
        // Por que validamos contra 'entity' y NO contra 'request': hoy el DTO no expone los flags
        // FC1.2/FC1.3 (no hay endpoint admin que los modifique via UpdateAsync — se cambian via
        // SQL/seed/migration). El check defensivo lee el estado actual de la entidad y rechaza
        // re-guardar si la combinacion es invalida. Cuando una sub-fase futura agregue esos flags
        // al DTO, este mismo check seguira funcionando porque 'entity' ya tiene los valores
        // post-aplicacion del request.
        //
        // Tiramos ValidationException (System.ComponentModel.DataAnnotations) que el
        // GlobalExceptionHandler ya mapea a HTTP 400 — mismo manejo que [Range] sobre el DTO.
        if (entity.EnablePartialCreditNotes && !entity.EnableNewCancellationFlow)
        {
            throw new ValidationException(
                "Combinacion de flags invalida (GR-002): para tener EnablePartialCreditNotes=true se requiere " +
                "EnableNewCancellationFlow=true. Si quiere apagar FC1.2, primero apague FC1.3 " +
                "(EnablePartialCreditNotes=false) en el mismo UPDATE o en uno anterior.");
        }

        // ADR-013 (Nota de Debito en cancelacion, 2026-06-01): pre-condicion GR-013.
        // EnableCancellationDebitNote DEPENDE de EnableNewCancellationFlow=true. La ND se
        // dispara desde el callback de la NC total, que solo existe en el flujo de cancelacion
        // nuevo (FC1.2). Sin ese flujo no hay desde donde engancharse a emitir la ND.
        //
        // Por que validamos contra 'entity' (estado post-aplicacion del request) y NO solo
        // contra 'request': hoy el DTO NO expone EnableNewCancellationFlow, asi que no se puede
        // prender FC1.2 en el mismo PUT. Si el admin manda EnableCancellationDebitNote=true y
        // la entidad tiene EnableNewCancellationFlow=false (porque FC1.2 se prende via SQL/seed),
        // 'entity' ya refleja la combinacion invalida y la rechazamos.
        //
        // CRITICO: este mismo check existe como startup-check en Program.cs (la app NO arranca
        // si la combinacion es invalida). Validar aca tambien evita que un PUT deje guardada una
        // configuracion que despues impida que la app vuelva a arrancar. Mejor un 400 ahora que
        // una app caida en el proximo restart.
        //
        // Tiramos ValidationException (System.ComponentModel.DataAnnotations), que el
        // GlobalExceptionHandler ya mapea a HTTP 400 — mismo manejo que GR-002.
        if (entity.EnableCancellationDebitNote && !entity.EnableNewCancellationFlow)
        {
            throw new ValidationException(
                "Combinacion de flags invalida (GR-013): para tener EnableCancellationDebitNote=true se requiere " +
                "EnableNewCancellationFlow=true. La Nota de Debito se dispara desde el flujo de cancelacion nuevo; " +
                "sin ese flujo no hay donde engancharse. Prenda el flujo de cancelacion (via SQL/seed) antes de " +
                "prender la emision de Nota de Debito.");
        }

        // ============================================================
        // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.0, 2026-05-22):
        // mismas pre-condiciones encadenadas que GR-002 pero para los flags Fase 2.
        // Misma logica defensiva: validamos sobre 'entity' (no sobre 'request')
        // porque hoy el DTO no expone los flags Fase 2; el check actua aunque la
        // combinacion invalida llegue por SQL/seed/migration y el admin trate de
        // reguardar la entidad.
        // ============================================================

        // F2 (real emission) depende de F1 (clasificador). Sin Fase 1 no hay
        // liquidacion clasificada para emitir.
        if (entity.EnablePartialCreditNoteRealEmission && !entity.EnablePartialCreditNotes)
        {
            throw new ValidationException(
                "Combinacion de flags invalida (FC1.3 Fase 2): para tener " +
                "EnablePartialCreditNoteRealEmission=true se requiere " +
                "EnablePartialCreditNotes=true. Sin Fase 1 (clasificador) no hay liquidacion " +
                "para emitir. Si quiere apagar Fase 1, primero apague Fase 2 " +
                "(EnablePartialCreditNoteRealEmission=false) en el mismo UPDATE o en uno anterior.");
        }

        // Flow dual (caso 4 y 7) depende del plumbing de emision real Fase 2.
        // No tiene sentido habilitar el dual sin tener antes la emision real Fase 2.
        if (entity.EnableTotalPlusNewInvoiceAutoProcessing && !entity.EnablePartialCreditNoteRealEmission)
        {
            throw new ValidationException(
                "Combinacion de flags invalida (FC1.3 Fase 2 dual): para tener " +
                "EnableTotalPlusNewInvoiceAutoProcessing=true se requiere " +
                "EnablePartialCreditNoteRealEmission=true. El flow dual NC total + " +
                "factura nueva necesita el plumbing de emision real para correr. Si quiere apagar " +
                "Fase 2, primero apague el dual (EnableTotalPlusNewInvoiceAutoProcessing=false) " +
                "en el mismo UPDATE o en uno anterior.");
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<OperationalFinanceSettings> GetEntityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await LoadOrCreateEntityAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            await OperationalFinanceSchemaBootstrapper.EnsureAsync(_dbContext, cancellationToken);
            return await LoadOrCreateEntityAsync(cancellationToken);
        }
    }

    private async Task<OperationalFinanceSettings> LoadOrCreateEntityAsync(CancellationToken cancellationToken)
    {
        var entity = await _dbContext.OperationalFinanceSettings
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity != null)
            return entity;

        entity = new OperationalFinanceSettings();
        _dbContext.OperationalFinanceSettings.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private static OperationalFinanceSettingsDto Map(OperationalFinanceSettings entity)
    {
        return new OperationalFinanceSettingsDto
        {
            RequireFullPaymentForOperativeStatus = entity.RequireFullPaymentForOperativeStatus,
            RequireFullPaymentForVoucher = entity.RequireFullPaymentForVoucher,
            AfipInvoiceControlMode = entity.AfipInvoiceControlMode,
            EnableUpcomingUnpaidReservationNotifications = entity.EnableUpcomingUnpaidReservationNotifications,
            UpcomingUnpaidReservationAlertDays = entity.UpcomingUnpaidReservationAlertDays,
            MaxDiscountPercentWithoutOverride = entity.MaxDiscountPercentWithoutOverride,
            // FC1.3 Fase 2: tres settings configurables visibles en el panel admin.
            IvaProrrateoMode = entity.IvaProrrateoMode,
            PartialCreditNoteRoundingTolerance = entity.PartialCreditNoteRoundingTolerance,
            IdempotencyKeyStaleThresholdMinutes = entity.IdempotencyKeyStaleThresholdMinutes,
            // ADR-012 MVP: el GET devuelve el estado actual del flag de multimoneda para que la
            // pantalla de Configuracion -> Facturacion lo muestre como un toggle.
            EnableMultiCurrencyInvoicing = entity.EnableMultiCurrencyInvoicing,
            // ADR-013: el GET expone el flag de emision de Nota de Debito en cancelacion.
            EnableCancellationDebitNote = entity.EnableCancellationDebitNote,
            // ADR-016 F0a: el GET expone el flag maestro del copiloto de IA.
            EnableAiCopilot = entity.EnableAiCopilot,
            // ADR-017 F1.1: el GET expone los 2 flags nuevos + el umbral, para que el panel los muestre.
            EnableCatalogFindOrCreate = entity.EnableCatalogFindOrCreate,
            EnableServiceDeadlineAlerts = entity.EnableServiceDeadlineAlerts,
            StaleCostReferenceDays = entity.StaleCostReferenceDays,
            // ADR-017 F1.4: el GET expone la ventana de alertas para que el panel la muestre/edite.
            ServiceDeadlineAlertDays = entity.ServiceDeadlineAlertDays,
            // Auditoria ERP 2026-06-12 (hallazgo #1): el GET expone el interruptor de comision del vendedor.
            EnableSellerCommissions = entity.EnableSellerCommissions,
            // Auditoria ERP 2026-06-13: el GET expone el porcentaje unico de comision para que el panel lo muestre.
            SellerCommissionPercent = entity.SellerCommissionPercent,
        };
    }
}
