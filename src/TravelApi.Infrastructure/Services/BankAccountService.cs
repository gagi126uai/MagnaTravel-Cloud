using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-041 (2026-06-27): servicio de cuentas bancarias polimorficas (Agencia / Cliente / Proveedor).
/// Responsabilidades: VALIDAR los obligatorios server-side, ENMASCARAR el CBU en los listados, PERSISTIR y
/// AUDITAR cada alta/edicion/baja. La AUTORIZACION por dueño la resuelve el controller (depende del rol + tipo
/// de dueño en runtime, ver BankAccountsController).
/// </summary>
public class BankAccountService : IBankAccountService
{
    // CBU argentino: exactamente 22 digitos numericos.
    private static readonly Regex CbuPattern = new(@"^\d{22}$", RegexOptions.Compiled);

    // Alias bancario AR: 6 a 20 caracteres, letras/numeros y puntos. Es una aproximacion pragmatica al formato
    // del BCRA (no empieza ni termina en punto). Suficiente para rechazar basura sin bloquear alias validos.
    private static readonly Regex AliasPattern = new(@"^(?!\.)(?!.*\.$)[A-Za-z0-9.]{6,20}$", RegexOptions.Compiled);

    private readonly AppDbContext _dbContext;

    // Auditoria SENSIBLE (alta/edicion/baja + lectura del dato completo). OBLIGATORIA (sin default null): una
    // cuenta bancaria NUNCA debe escribirse ni leerse en claro sin rastro. Con StageBusinessEvent la fila de
    // audit entra en el MISMO SaveChanges que el cambio (atomico). Los tests inyectan un IAuditService.
    private readonly IAuditService _auditService;

    public BankAccountService(AppDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<BankAccountListItemDto>> ListAsync(
        BankAccountOwnerType ownerType,
        int ownerId,
        CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.BankAccounts
            .AsNoTracking()
            .Where(account => account.OwnerType == ownerType && account.OwnerId == ownerId && account.IsActive)
            // Orden: agrupado por moneda y, DENTRO de cada moneda, la principal primero (IsPrimary=true antes que
            // false). Asi quien mira la lista ve de un vistazo la cuenta por defecto de cada moneda. Desempate
            // estable por fecha de creacion y luego Id.
            .OrderBy(account => account.Currency)
            .ThenByDescending(account => account.IsPrimary)
            .ThenBy(account => account.CreatedAt)
            .ThenBy(account => account.Id)
            .ToListAsync(cancellationToken);

        // El CBU se enmascara ACA, no en el front: la lista NUNCA debe llevar el CBU completo por la red.
        return accounts.Select(ToListItemDto).ToList();
    }

    public async Task<BankAccountDetailDto?> GetByPublicIdAsync(Guid publicId, CancellationToken cancellationToken)
    {
        var account = await _dbContext.BankAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.PublicId == publicId, cancellationToken);

        return account is null ? null : ToDetailDto(account);
    }

    public async Task AuditDetailViewedAsync(
        BankAccountDetailDto account,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken)
    {
        // Auditamos el ACCESO al dato sensible DESENMASCARADO (CBU/alias completos). Es una lectura, pero el dato
        // es un destino de transferencia: para el producto multi-agencia queremos saber quien lo vio y cuando.
        // El detalle del log lleva el CBU/alias ENMASCARADOS (no duplicamos el dato en claro en la auditoria).
        // El dueño queda identificado por el OwnerType + el PublicId de la cuenta (entityId del log, mas abajo).
        // No incluimos el OwnerId interno: ya no viaja en el DTO (hardening 2026-06-28) y el PublicId alcanza para
        // rastrear la cuenta en la auditoria.
        var details =
            $"Owner: {account.OwnerType}. " +
            $"Currency: {account.Currency}. " +
            $"Holder: {account.HolderName}. " +
            $"CBU: {MaskCbu(account.Cbu) ?? "(sin CBU)"}. " +
            $"Alias: {MaskAlias(account.Alias) ?? "(sin alias)"}.";

        _auditService.StageBusinessEvent(
            action: AuditActions.BankAccountDetailViewed,
            entityName: AuditActions.BankAccountEntityName,
            entityId: account.PublicId.ToString(),
            details: details,
            userId: actorUserId,
            userName: actorUserName);

        // StageBusinessEvent solo deja el AuditLog "para insertar"; en una lectura no hay otra mutacion, asi que
        // hace falta el SaveChanges para persistir el rastro.
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<BankAccountListItemDto> CreateAsync(
        BankAccountUpsertRequest request,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken)
    {
        var (cbu, alias, holderName, currency, accountType, holderTaxId) = ValidateAndNormalize(request);

        // El dueño se valida AL CREAR (no en update, donde no cambia): si es Cliente/Proveedor, su PublicId (que
        // viaja en request.OwnerId) tiene que existir y estar activo (sin esto, podriamos cargar una cuenta colgada
        // de un dueño inexistente). Si es Agencia, el OwnerId interno es 0 fijo (NO confiamos en el body: singleton).
        var ownerId = await ResolveOwnerInternalIdAsync(request.OwnerType, request.OwnerId, cancellationToken);

        // Decidir si la cuenta nueva queda como principal de su moneda:
        //  - Si el front la pidio principal (request.IsPrimary), se respeta.
        //  - Si NO la pidio pero es la PRIMERA cuenta activa de este dueño+moneda, la marcamos principal sola.
        //    Asi siempre hay una principal por moneda sin que el front tenga que pedirlo en el alta inicial.
        var becomesPrimary = request.IsPrimary;
        if (!becomesPrimary)
        {
            var alreadyHasActiveSameCurrency = await _dbContext.BankAccounts.AnyAsync(
                a => a.OwnerType == request.OwnerType
                  && a.OwnerId == ownerId
                  && a.Currency == currency
                  && a.IsActive,
                cancellationToken);
            becomesPrimary = !alreadyHasActiveSameCurrency;
        }

        var account = new BankAccount
        {
            OwnerType = request.OwnerType,
            OwnerId = ownerId,
            Cbu = cbu,
            Alias = alias,
            HolderName = holderName,
            Currency = currency,
            Bank = Trim(request.Bank),
            AccountType = accountType,
            HolderTaxId = holderTaxId,
            Notes = Trim(request.Notes),
            IsActive = true,
            IsPrimary = becomesPrimary,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = actorUserId,
        };

        _dbContext.BankAccounts.Add(account);

        // PublicId se asigna en AppDbContext.AssignPublicIds durante SaveChanges. Para que el audit (que se
        // arma ANTES del save) lleve un EntityId estable, lo fijamos aca explicitamente.
        if (account.PublicId == Guid.Empty)
            account.PublicId = Guid.NewGuid();

        // Si queda principal, desmarcamos cualquier OTRA principal del mismo dueño+moneda en el MISMO SaveChanges
        // (atomico). En el caso "primera cuenta" no hay otras, asi que no toca nada.
        if (becomesPrimary)
            await UnmarkOtherPrimariesAsync(account, cancellationToken);

        StageAudit(AuditActions.BankAccountCreated, account, actorUserId, actorUserName);
        // Registramos aparte el cambio de principal (destino de pago sugerido) cuando la cuenta nace principal.
        if (becomesPrimary)
            StageAudit(AuditActions.BankAccountSetPrimary, account, actorUserId, actorUserName);

        await _dbContext.SaveChangesAsync(cancellationToken);
        // Respuesta ENMASCARADA: la escritura nunca devuelve el CBU/alias en claro (eso solo el GET de detalle,
        // que audita el acceso). El front recibe el estado actualizado (incluido IsPrimary) sin abrir un camino
        // de lectura del dato sensible sin rastro.
        return ToListItemDto(account);
    }

    public async Task<BankAccountListItemDto> UpdateAsync(
        Guid publicId,
        BankAccountUpsertRequest request,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken)
    {
        var account = await _dbContext.BankAccounts
            .FirstOrDefaultAsync(a => a.PublicId == publicId, cancellationToken);
        if (account is null)
            throw new KeyNotFoundException("Cuenta bancaria no encontrada.");

        var (cbu, alias, holderName, currency, accountType, holderTaxId) = ValidateAndNormalize(request);

        // Detectamos si esta edicion convierte la cuenta en principal (o la "re-principaliza" en una moneda
        // distinta). wasPrimary + la moneda vieja sirven para saber si hubo un cambio real que auditar.
        var wasPrimary = account.IsPrimary;
        var previousCurrency = account.Currency;

        // El dueño NO se cambia en una edicion: mover una cuenta de un cliente a otro (o de cliente a proveedor)
        // seria una reasignacion sensible. OwnerType/OwnerId del request se ignoran a proposito.
        account.Cbu = cbu;
        account.Alias = alias;
        account.HolderName = holderName;
        account.Currency = currency;
        account.Bank = Trim(request.Bank);
        account.AccountType = accountType;
        account.HolderTaxId = holderTaxId;
        account.Notes = Trim(request.Notes);
        account.IsPrimary = request.IsPrimary;
        account.UpdatedAt = DateTime.UtcNow;

        // Hubo cambio de principal si la cuenta queda principal y antes NO lo era (o cambio de moneda, lo que la
        // hace principal de OTRA moneda). En ese caso desmarcamos las otras principales del dueño+moneda nueva.
        var primaryChanged = account.IsPrimary && (!wasPrimary || previousCurrency != currency);
        if (account.IsPrimary)
            await UnmarkOtherPrimariesAsync(account, cancellationToken);

        StageAudit(AuditActions.BankAccountUpdated, account, actorUserId, actorUserName);
        if (primaryChanged)
            StageAudit(AuditActions.BankAccountSetPrimary, account, actorUserId, actorUserName);

        await _dbContext.SaveChangesAsync(cancellationToken);
        // Respuesta ENMASCARADA (ver CreateAsync): la edicion no devuelve el CBU/alias en claro.
        return ToListItemDto(account);
    }

    public async Task DeactivateAsync(
        Guid publicId,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken)
    {
        var account = await _dbContext.BankAccounts
            .FirstOrDefaultAsync(a => a.PublicId == publicId, cancellationToken);
        if (account is null)
            throw new KeyNotFoundException("Cuenta bancaria no encontrada.");

        // Idempotente: desactivar una cuenta ya inactiva no es un error (no auditamos un no-op).
        if (!account.IsActive)
            return;

        account.IsActive = false;
        account.UpdatedAt = DateTime.UtcNow;

        // Nota de diseño: al desactivar la cuenta principal NO promovemos otra automaticamente. La regla de
        // negocio dice que tener principal NO es obligatorio (un dueño puede quedar sin principal en una moneda).
        // Si mas adelante se quiere auto-promover, seria aca; por ahora se deja explicito que no se hace.
        StageAudit(AuditActions.BankAccountDeleted, account, actorUserId, actorUserName);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<BankAccountListItemDto> SetPrimaryAsync(
        Guid publicId,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken)
    {
        var account = await _dbContext.BankAccounts
            .FirstOrDefaultAsync(a => a.PublicId == publicId, cancellationToken);
        if (account is null)
            throw new KeyNotFoundException("Cuenta bancaria no encontrada.");

        // Una cuenta inactiva (soft-deleted) no puede ser el destino de pago por defecto.
        if (!account.IsActive)
            throw new ArgumentException("No se puede marcar como principal una cuenta inactiva.");

        // Idempotente: si ya era principal, igual self-healeamos (desmarcamos cualquier otra que por un bug
        // hubiera quedado principal en la misma moneda) y dejamos rastro del acto explicito del usuario.
        account.IsPrimary = true;
        account.UpdatedAt = DateTime.UtcNow;

        await UnmarkOtherPrimariesAsync(account, cancellationToken);

        StageAudit(AuditActions.BankAccountSetPrimary, account, actorUserId, actorUserName);

        await _dbContext.SaveChangesAsync(cancellationToken);
        // Respuesta ENMASCARADA (ver CreateAsync): marcar principal no devuelve el CBU/alias en claro.
        return ToListItemDto(account);
    }

    /// <summary>
    /// Desmarca cualquier OTRA cuenta principal del mismo (OwnerType, OwnerId, Currency) que la cuenta dada, para
    /// garantizar la regla "una sola principal por dueño+moneda". NO llama a SaveChanges: deja las filas tocadas
    /// en el ChangeTracker para que se persistan en el MISMO SaveChanges que el cambio que la origino (atomico).
    /// La cuenta propia se excluye por Id (en un alta su Id es 0 y aun no esta en BD, asi que tampoco se devuelve).
    ///
    /// <para>FOLLOW-UP (no hacer ahora): NO se agrega un indice unico parcial "una principal por dueño+moneda"
    /// (WHERE IsPrimary AND IsActive). Motivo: el unmark+mark ocurre en el MISMO SaveChanges y, durante ese
    /// instante, hay dos filas con IsPrimary=true; EF no garantiza el orden de los UPDATE, asi que un indice con
    /// chequeo INMEDIATO abortaria el camino feliz. Requeriria una constraint DEFERRABLE INITIALLY DEFERRED
    /// (no soportada por defecto via EF, seria SQL crudo). Riesgo bajo: no mueve plata, se auto-cura en cada
    /// set-primary, y el producto es mono-agencia sin concurrencia real de cajeros sobre la misma cuenta.</para>
    /// </summary>
    private async Task UnmarkOtherPrimariesAsync(BankAccount account, CancellationToken cancellationToken)
    {
        var otherPrimaries = await _dbContext.BankAccounts
            .Where(a => a.OwnerType == account.OwnerType
                     && a.OwnerId == account.OwnerId
                     && a.Currency == account.Currency
                     && a.IsActive
                     && a.IsPrimary
                     && a.Id != account.Id)
            .ToListAsync(cancellationToken);

        foreach (var other in otherPrimaries)
        {
            other.IsPrimary = false;
            other.UpdatedAt = DateTime.UtcNow;
        }
    }

    // ============================================================
    // Validacion server-side (no confiar en el front)
    // ============================================================

    /// <summary>
    /// Traduce el TOKEN PUBLICO del dueño (lo que viaja por la API) al Id INTERNO (int) con el que se persiste y
    /// consulta, validando que el dueño exista y este activo. Agencia -> 0 fijo (singleton, no se confia en el
    /// token). Cliente/Proveedor -> el token es su PublicId (GUID); se resuelve al Id interno, exigiendo que exista
    /// y este activo, si no lanza <see cref="ArgumentException"/> (el controller lo mapea a 400). NO hay FK fisica,
    /// asi que esta es la unica red que evita cuentas colgadas de un dueño inexistente.
    ///
    /// <para>Se direcciona por PublicId (no por Id interno) para mantener la coherencia con el resto de la API de
    /// cuentas (detalle/editar/borrar usan PublicId) y para no aceptar/exponer ids secuenciales por la API.</para>
    /// </summary>
    public async Task<int> ResolveOwnerInternalIdAsync(
        BankAccountOwnerType ownerType,
        string? ownerToken,
        CancellationToken cancellationToken)
    {
        switch (ownerType)
        {
            case BankAccountOwnerType.Agency:
                return 0; // La Agencia es un singleton: su OwnerId interno es 0 fijo, no lo que mande el body.

            case BankAccountOwnerType.Customer:
                {
                    var publicId = ParseOwnerPublicId(ownerToken, "cliente");
                    var customerId = await _dbContext.Customers
                        .Where(c => c.PublicId == publicId && c.IsActive)
                        .Select(c => (int?)c.Id)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (customerId is null)
                        throw new ArgumentException("El cliente indicado no existe o está inactivo.");
                    return customerId.Value;
                }

            case BankAccountOwnerType.Supplier:
                {
                    var publicId = ParseOwnerPublicId(ownerToken, "proveedor");
                    var supplierId = await _dbContext.Suppliers
                        .Where(s => s.PublicId == publicId && s.IsActive)
                        .Select(s => (int?)s.Id)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (supplierId is null)
                        throw new ArgumentException("El proveedor indicado no existe o está inactivo.");
                    return supplierId.Value;
                }

            default:
                throw new ArgumentException("Tipo de dueño desconocido.");
        }
    }

    /// <summary>
    /// Parsea el token publico de un dueño Cliente/Proveedor a su PublicId (GUID). Rechaza vacio, no-GUID y el
    /// GUID vacio (Guid.Empty) con un <see cref="ArgumentException"/> claro — el controller lo mapea a 400.
    /// </summary>
    private static Guid ParseOwnerPublicId(string? ownerToken, string ownerLabel)
    {
        if (string.IsNullOrWhiteSpace(ownerToken)
            || !Guid.TryParse(ownerToken, out var publicId)
            || publicId == Guid.Empty)
        {
            throw new ArgumentException($"El identificador del {ownerLabel} es inválido.");
        }

        return publicId;
    }

    /// <summary>
    /// Valida y normaliza el request. Lanza <see cref="ArgumentException"/> si algun obligatorio falta o un
    /// formato es invalido — el controller lo mapea a 400. Devuelve los valores ya recortados/normalizados.
    /// </summary>
    private static (string? Cbu, string? Alias, string HolderName, string Currency, BankAccountType? AccountType, string? HolderTaxId)
        ValidateAndNormalize(BankAccountUpsertRequest request)
    {
        var holderName = Trim(request.HolderName);
        if (string.IsNullOrEmpty(holderName))
            throw new ArgumentException("El titular de la cuenta es obligatorio.");

        // Moneda obligatoria y soportada (ARS/USD). Una cuenta es de UNA moneda (regla de oro).
        if (string.IsNullOrWhiteSpace(request.Currency) || !Monedas.EsSoportada(request.Currency))
            throw new ArgumentException("La moneda es obligatoria y debe ser ARS o USD.");
        var currency = Monedas.Normalizar(request.Currency);

        var cbu = Trim(request.Cbu);
        var alias = Trim(request.Alias);

        // Al menos uno de CBU o alias. Sin ninguno de los dos, la cuenta no identifica un destino de plata.
        if (string.IsNullOrEmpty(cbu) && string.IsNullOrEmpty(alias))
            throw new ArgumentException("Debe ingresar al menos un CBU o un alias.");

        if (!string.IsNullOrEmpty(cbu) && !CbuPattern.IsMatch(cbu))
            throw new ArgumentException("El CBU debe tener exactamente 22 dígitos numéricos.");

        if (!string.IsNullOrEmpty(alias) && !AliasPattern.IsMatch(alias))
            throw new ArgumentException("El alias no tiene un formato válido (6 a 20 caracteres: letras, números y puntos).");

        var holderTaxId = Trim(request.HolderTaxId);

        return (cbu, alias, holderName, currency, request.AccountType, holderTaxId);
    }

    // ============================================================
    // Mapeo + masking
    // ============================================================

    private static BankAccountListItemDto ToListItemDto(BankAccount account) => new(
        PublicId: account.PublicId,
        OwnerType: account.OwnerType,
        // OwnerId interno NO se mapea a la respuesta (hardening 2026-06-28): no se expone a la UI.
        CbuMasked: MaskCbu(account.Cbu),
        AliasMasked: MaskAlias(account.Alias),
        HolderName: account.HolderName,
        Currency: account.Currency,
        Bank: account.Bank,
        AccountType: account.AccountType,
        HolderTaxId: account.HolderTaxId,
        Notes: account.Notes,
        IsActive: account.IsActive,
        CreatedAt: account.CreatedAt,
        IsPrimary: account.IsPrimary);

    private static BankAccountDetailDto ToDetailDto(BankAccount account) => new(
        PublicId: account.PublicId,
        OwnerType: account.OwnerType,
        // OwnerId interno NO se mapea a la respuesta (hardening 2026-06-28): no se expone a la UI.
        Cbu: account.Cbu,
        Alias: account.Alias,
        HolderName: account.HolderName,
        Currency: account.Currency,
        Bank: account.Bank,
        AccountType: account.AccountType,
        HolderTaxId: account.HolderTaxId,
        Notes: account.Notes,
        IsActive: account.IsActive,
        CreatedAt: account.CreatedAt,
        UpdatedAt: account.UpdatedAt,
        IsPrimary: account.IsPrimary);

    /// <summary>
    /// Enmascara el CBU dejando visibles SOLO los ultimos 4 digitos. null/vacio -> null. Un CBU de 22 digitos
    /// se devuelve como 18 viñetas + los ultimos 4 (ej. "••••••••••••••••••1234"). Si por algun motivo el dato
    /// es mas corto que 4, se enmascara entero (no se filtra nada).
    /// </summary>
    public static string? MaskCbu(string? cbu)
    {
        if (string.IsNullOrEmpty(cbu))
            return null;

        if (cbu.Length <= 4)
            return new string('•', cbu.Length);

        var last4 = cbu[^4..];
        return new string('•', cbu.Length - 4) + last4;
    }

    /// <summary>
    /// Enmascara el ALIAS en los listados: el alias completo tambien es un destino de transferencia, asi que
    /// recibe el mismo trato que el CBU (completo solo en el detalle gateado). Muestra los primeros 2 y los
    /// ultimos 2 caracteres, el resto viñetas (ej. "mi••••••••••co"). Si mide 4 o menos, se enmascara entero.
    /// null/vacio -> null.
    /// </summary>
    public static string? MaskAlias(string? alias)
    {
        if (string.IsNullOrEmpty(alias))
            return null;

        if (alias.Length <= 4)
            return new string('•', alias.Length);

        var first2 = alias[..2];
        var last2 = alias[^2..];
        return first2 + new string('•', alias.Length - 4) + last2;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Deja el evento de auditoria "para insertar" en el mismo SaveChanges que la mutacion (atomico). El detalle
    /// lleva el dueño, la moneda, el titular y el CBU ENMASCARADO — NUNCA el CBU completo en el log de sistema.
    /// </summary>
    private void StageAudit(string action, BankAccount account, string actorUserId, string? actorUserName)
    {
        var details =
            $"Owner: {account.OwnerType}#{account.OwnerId}. " +
            $"Currency: {account.Currency}. " +
            $"Holder: {account.HolderName}. " +
            $"Primary: {(account.IsPrimary ? "yes" : "no")}. " +
            $"CBU: {MaskCbu(account.Cbu) ?? "(sin CBU)"}. " +
            $"Alias: {MaskAlias(account.Alias) ?? "(sin alias)"}.";

        _auditService.StageBusinessEvent(
            action: action,
            entityName: AuditActions.BankAccountEntityName,
            entityId: account.PublicId.ToString(),
            details: details,
            userId: actorUserId,
            userName: actorUserName);
    }
}
