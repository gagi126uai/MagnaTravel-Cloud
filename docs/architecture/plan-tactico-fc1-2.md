# Plan tactico FC1.2 — Servicios de dominio del modulo cancelacion/refund (v3)

**Fecha v3**: 2026-05-17
**Autor**: software-architect agent (iteracion 3 post-review v2)
**Base HEAD**: `184134f` (FC1.1 commiteado, 388/388 tests OK)
**Para review**: `software-architect-reviewer`, despues `backend-dotnet-senior`
**Idioma**: rioplatense, nivel trainee/junior.
**Status**: v3 — cierra 4 bloqueantes v2 (BR-V2-01..04) + 6 mejoras (MR-V2-01..06) + agrega seccion §13 fiscal.

> **OPEN QUESTION FISCAL-OPS-001**: la opcion (a) de BR-V2-03 (un solo `InvariantOverride` cubre la NC fiscal) requiere signoff explicito del contador real de MagnaTravel + `arca-tax-expert-argentina` **ANTES** de mergear FC1.2 a produccion. Ver §13.

---

## 0. Cambios v2 -> v3

Esta seccion lista los **cambios concretos** entre v2 y v3 — no narrativa. Si venis del v2, leer esto primero. Los items v1->v2 quedan integrados (se conservan los antiguos §0 v2 puntos BR-01..05 / MR-01..06 en el cuerpo del plan, pero no se re-listan aca).

| ID v3 | Que decia v2 | Que dice v3 | Justificacion / decision Gaston |
|---|---|---|---|
| BR-V2-01 | No habia escape manual: si AFIP devuelve CAE pero el callback del BC service falla repetidas veces, OPS1 quedaba como query SQL sin endpoint para forzar transicion. | Se agrega endpoint admin `POST /api/cancellations/{publicId}/force-arca-confirmation` (§4.4) + metodo en interface `IBookingCancellationService.ForceArcaConfirmationAsync(...)` (§2.1). Requiere `InvariantOverride` aprobado especifico para el BC (entityType=`"BookingCancellation"`, entityId=bc.Id). Idempotente. Solo Admins. Audit nuevo `BookingCancellationArcaConfirmedManually`. | Decision Gaston: si AFIP confirmo (NC existe) y el callback fallo 24h+, queremos un boton humano para empatar el estado del BC con la realidad fiscal — sin requerir intervencion en DB. |
| BR-V2-02 | §10.2 decia generico "backfill NCs legacy" pero no documentaba precondicion operativa para habilitar `EnableNewCancellationFlow=true` en prod. | Se agrega §10.2.1 "Precondicion operativa flag prod" con query SQL bloqueante + 2 opciones de mitigacion antes de prender el flag. | Decision Gaston: si hay Reservas activas sin `ResponsibleUserId`, el ownership del BC (que hereda de Reserva) revienta para esos casos. Hay que decidir antes del flip. |
| BR-V2-03 | §6.1 paso 9 hace `EnqueueAnnulmentAsync(..., requesterIsAdmin: true)`, pero no documentaba como cierra el approval `InvoiceAnnulment` del flujo normal. El reviewer pidio elegir entre (a) un solo override cubre todo, (b) doble approval, (c) refactor cross-reference. | Se acepta opcion (a). Se agrega: (1) `Invoice.AnnulmentReason` se setea con un prefijo `"BC cancellation override: <approvalRequestPublicId>"` para cross-reference (§5 + §6.1 paso 9). (2) Nuevo campo opcional `Invoice.AnnulmentApprovalRequestId` (FK nullable a `ApprovalRequest.Id`) propuesto en migracion §10.1 (se VERIFICO que no existe hoy). (3) Nueva seccion §13 con OPEN QUESTION FISCAL-OPS-001: requiere signoff contador + arca-tax-expert. (4) Fallback documentado: si signoff negativo, ir a opcion (b) o (c). | Decision Gaston: aceptamos el bypass con trazabilidad cruzada pero marcamos como BLOCKER de merge a prod hasta signoff fiscal. |
| BR-V2-04 | §7.3 mostraba el `BuildServiceProvider()` minimalista pero el reviewer detecto que faltan dependencias transitivas (`IRepository<>`, `IUserContext`, varias HttpClients, etc.) — el implementador se pega contra "Unable to resolve service for type X" en los 4 tests paralelos. | Se agrega §7.3 "Smoke test obligatorio" (`BuildServiceProvider_ResolvesAllServices`) que se corre ANTES de los 4 tests funcionales. Si el smoke falla, el implementador sabe que falta registrar X — no debugea via tests funcionales rotos. Tambien se documenta que el implementador tiene que recorrer constructores de los 3 services + `AuditService` + `ApprovalRequestService` antes de codear los 4 tests. Estimacion ajustada (+1.5h). | Decision Gaston: el smoke es barato (5 GetRequiredService) y ahorra horas de diagnostico. |
| MR-V2-01 | `BuildManualCashMovement` quedaba como helper privado duplicado en `OperatorRefundService` y `ClientCreditService` (§6.6). | Se agrega `src/TravelApi.Domain/Helpers/ManualCashMovementBuilder.cs` con 2 metodos estaticos `BuildIncomeForRefund(...)` y `BuildExpenseForWithdrawal(...)`. Sin estado, sin DI, sin SaveChanges. Ambos services lo invocan. Validacion centralizada (Amount > 0, Direction valido, strings no vacios). §6.6 refactor. | Decision Gaston: aprobada en bloque. Es helper sin estado — la duplicacion no aportaba nada. |
| MR-V2-02 | Comentario en §2.1.bis sobre por que la bridge interface estaba bien, pero no quedaba claro en `Program.cs` el motivo del split. | Se agrega bloque de comentario explicativo en el snippet de registro DI dentro del plan (§2.1.bis + §6.1.bis). El implementador copia ese comentario al `Program.cs` real. | Decision Gaston: aprobado. Quien lea `Program.cs` en 6 meses tiene que entender por que hay 2 interfaces apuntando a la misma impl. |
| MR-V2-03 | §2.3 decia "identico v1" — el plan v2 no era autosuficiente para alguien que no haya leido v1. | Se expande §2.3 con la firma completa de `IClientCreditService.CreateEntryAsync` y `WithdrawAsync`. | Decision Gaston: aprobado. El plan tiene que stand-alone. |
| MR-V2-04 | §6.6 ejemplo decia `refund.SupplierName` pero `OperatorRefundReceived` no tiene esa propiedad — tiene `Supplier` (navigation) con `Name`. | Se corrige a `refund.Supplier.Name` + se agrega nota "el caller debe hacer `.Include(r => r.Supplier)` antes de llamar al builder". | Decision Gaston: aprobado. Error de pseudocodigo, no de modelo. |
| MR-V2-05 | El Libro de Caja no tenia guidance sobre como renderizar movimientos N:M (un Income que cubre varias BCs). | Se agrega nota explicita en §6.3 paso 10 + §6.6: `RelatedReservaId = null` cuando es Income de operador (porque el ingreso N:M cubre potencialmente varias reservas); trazabilidad via `OperatorRefundReceivedId`. `TreasuryService.GetCashSummaryAsync` debe renderizar como `"Devolucion operador {SupplierName} ({N} BCs asociados)"` cuando detecta `RelatedReservaId IS NULL AND OperatorRefundReceivedId IS NOT NULL`. | Decision Gaston: aprobado. Si no, el usuario ve un Income sin reserva asociada y no entiende. |
| MR-V2-06 | §6 no tenia paso-a-paso de `VoidAllocationAsync` ni `ReassociateAllocationAsync` — solo aparecian en la tabla de endpoints. | Se agrega §6.3.bis "Void y Reassociate de allocations" con guards, atomicidad, audit, idempotencia y concurrencia. | Decision Gaston: aprobado. Son operaciones criticas de back-office que necesitan documentacion. |

**Items diferidos vs v2**: mantengo igual seccion §10 (era §10 en v2), agregando §10.1 campo `Invoice.AnnulmentApprovalRequestId` + §10.2.1 precondicion ResponsibleUserId.

---

## 1. Orden de implementacion + dependencias

Identico v2. El nuevo endpoint manual de ARCA (BR-V2-01), el helper (MR-V2-01) y los tests adicionales se integran dentro de las sub-fases existentes (anotado a la derecha en cada fila de §9).

```
FC1.2.0  Settings + migracion (OperatorRefundTimeoutDays, OnePerReservaInvoicePolicy,
         Ley25345ThresholdAmount, PhysicalRefundAlertThreshold, EnableNewCancellationFlow)
         + TaxConditionCanonical + TaxConditionNormalizer
         + Ownership enum extension
         + Permission CancellationsForceArcaConfirmation
         + Invoice.AnnulmentApprovalRequestId column                  ─┐
                                                                       │
FC1.2.1  IInvoiceAnnulmentBcBridge interface (chica)                  │
         BookingCancellationService (impl ambos: IBookingCancellation- │
         Service + IInvoiceAnnulmentBcBridge)                          │ depende settings
         T0, T1 transitions, draft/confirm/abort                       │ + normalizer
         ForceArcaConfirmationAsync (BR-V2-01)                  ──────┤
                                                                       │
FC1.2.2  OperatorRefundService (T2)                            ──────┤ depende BC
         allocacion N:M, retry xmin                                    │
         + ManualCashMovementBuilder (helper estatico, MR-V2-01)       │
         + VoidAllocationAsync / ReassociateAllocationAsync (MR-V2-06) │
                                                                ──────┤
FC1.2.3  ClientCreditService (T3)                               ──────┤ depende OperatorRefund
         withdraw, retry xmin                                          │
         + ManualCashMovementBuilder (helper estatico, MR-V2-01)       │
                                                                ──────┤
FC1.2.4  InvoiceService hook (inyectar IInvoiceAnnulmentBcBridge,     │
         llamar OnArcaSucceededAsync post-CAE)                          │
         ApprovalRequest defaults para los 3 ApprovalRequestType        │
         nuevos                                                ──────┤
                                                                       │
FC1.2.5  Fixture extension: PostgresIntegrationFixture +              │
         IServiceProvider + smoke test (BR-V2-04)              ──────┤
                                                                       │
FC1.2.6  3 Controllers + endpoint force-arca-confirmation              │
         permisos + ownership decorators + DI wiring en Program.cs     │
                                                                ──────┤
                                                                       │
FC1.2.7  E2E happy + 5 variantes + concurrencia paralela 4 tests       │
         + 3 tests ForceArca (BR-V2-01)                                │
         + audit constants + observability counters             ──────┘
```

**Justificacion del orden** (sin cambios vs v2 — agregados):

- `Invoice.AnnulmentApprovalRequestId` debe agregarse en FC1.2.0 (misma migracion que los 4 settings) para que la coordinacion BC <-> Invoice del paso 9 (§6.1) pueda setearlo.
- `CancellationsForceArcaConfirmation` permission tambien en FC1.2.0 para que el endpoint del FC1.2.6 quede limpio (sin DB seed posterior).
- `ManualCashMovementBuilder` (helper estatico) se agrega en FC1.2.2 (primera vez que se usa). FC1.2.3 lo reusa.

---

## 2. Contratos publicos

### 2.1 `IBookingCancellationService`

Ubicacion: `src/TravelApi.Application/Interfaces/IBookingCancellationService.cs`

```csharp
public interface IBookingCancellationService
{
    // ===== Comandos (UI) =====
    Task<BookingCancellationDto> DraftAsync(
        DraftCancellationRequest request,
        string userId, string? userName,
        CancellationToken ct);

    Task<BookingCancellationDto> ConfirmAsync(
        Guid publicId,
        ConfirmCancellationRequest request,
        string userId, string? userName,
        bool requesterIsAdmin,
        CancellationToken ct);

    Task<BookingCancellationDto> AbortAsync(
        Guid publicId, string reason, string userId, CancellationToken ct);

    // ===== Endpoint manual admin (BR-V2-01, FC1.2 v3) =====
    /// <summary>
    /// Escape hatch: si AFIP devuelve CAE pero el callback automatico
    /// (OnArcaSucceededAsync) fallo o quedo desincronizado (ej. job zombie,
    /// excepcion no recuperable), un Admin puede empatar manualmente el BC
    /// con la NC ya emitida.
    /// Requiere ApprovalRequest aprobado: InvariantOverride scoped al BC.
    /// Idempotente: si el BC ya esta en AwaitingOperatorRefund o adelante,
    /// retorna no-op + log warning + 200 OK.
    /// </summary>
    Task<BookingCancellationDto> ForceArcaConfirmationAsync(
        Guid publicId,
        ForceArcaConfirmationRequest request,
        string userId, string? userName,
        CancellationToken ct);

    // ===== Reacciones internas (NO via IInvoiceAnnulmentBcBridge — esas
    //       las llama el job de Hangfire) =====
    Task OnAllocationRecordedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct);
    Task OnAllocationVoidedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct);
    Task OnAllCreditConsumedAsync(int bookingCancellationId, CancellationToken ct);

    // ===== Queries =====
    Task<BookingCancellationDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct);
    Task<BookingCancellationDto?> GetByReservaIdAsync(int reservaId, CancellationToken ct);
    Task<PagedResponse<BookingCancellationListItemDto>> GetAllAsync(
        BookingCancellationsListQuery query, CancellationToken ct);
}
```

### 2.1.bis `IInvoiceAnnulmentBcBridge` (sin cambios vs v2, cierra BR-04)

Ubicacion: `src/TravelApi.Application/Interfaces/IInvoiceAnnulmentBcBridge.cs`

```csharp
public interface IInvoiceAnnulmentBcBridge
{
    Task OnArcaSucceededAsync(int originatingInvoiceId, int creditNoteInvoiceId, CancellationToken ct);
    Task OnArcaFailedAsync(int originatingInvoiceId, string? afipError, CancellationToken ct);
}
```

**Comentario del registro DI (MR-V2-02)** — el implementador copia este bloque al `Program.cs` real:

```csharp
// BookingCancellationService implementa AMBAS interfaces:
//   - IBookingCancellationService (API publica que llaman controllers)
//   - IInvoiceAnnulmentBcBridge (interface chica, 2 metodos, la inyecta InvoiceService)
// Sin este split, IInvoiceService <-> IBookingCancellationService genera ciclo DI
// rechazado por el resolver al startup (Scoped circular reference).
// Registramos la clase concreta una sola vez + ambas interfaces apuntan a la
// misma instancia dentro del scope (para que compartan AppDbContext y EF ChangeTracker).
services.AddScoped<BookingCancellationService>();
services.AddScoped<IBookingCancellationService>(sp => sp.GetRequiredService<BookingCancellationService>());
services.AddScoped<IInvoiceAnnulmentBcBridge>(sp => sp.GetRequiredService<BookingCancellationService>());
```

**Dependencias inyectadas en `BookingCancellationService`** (extiende v2):
- `AppDbContext _db`
- `IInvoiceService _invoiceService`
- `IApprovalRequestService _approvalService`
- `IAuditService _auditService`
- `IOperationalFinanceSettingsService _settings`
- `ILogger<BookingCancellationService> _logger`

### 2.2 `IOperatorRefundService`

Identico v2, ahora con metodos void/reassociate explicitos (MR-V2-06):

```csharp
public interface IOperatorRefundService
{
    Task<OperatorRefundReceivedDto> RecordReceivedAsync(
        RecordOperatorRefundReceivedRequest request,
        string userId, string? userName, CancellationToken ct);

    Task<OperatorRefundAllocationDto> AllocateAsync(
        Guid refundPublicId,
        AllocateRefundRequest request,
        string userId, string? userName, CancellationToken ct);

    /// <summary>
    /// MR-V2-06 — anula una allocation existente (deja la inversa de
    /// AllocatedAmount y libera el cap del refund). Requiere
    /// approval CobranzasAnnul + reason >= 20 chars.
    /// </summary>
    Task<OperatorRefundAllocationDto> VoidAllocationAsync(
        Guid allocationPublicId,
        VoidAllocationRequest request,
        string userId, string? userName, CancellationToken ct);

    /// <summary>
    /// MR-V2-06 — reasocia una allocation a otra BC (caso: contador detecto
    /// imputacion incorrecta). Atomic: void de la vieja + create de la nueva
    /// dentro de la misma tx. Requiere approval + reason.
    /// </summary>
    Task<OperatorRefundAllocationDto> ReassociateAllocationAsync(
        Guid allocationPublicId,
        ReassociateAllocationRequest request,
        string userId, string? userName, CancellationToken ct);

    Task<OperatorRefundReceivedDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct);
    Task<decimal> GetAvailableCapAsync(Guid refundPublicId, CancellationToken ct);
    Task<PagedResponse<OperatorRefundReceivedListItemDto>> GetAllAsync(
        OperatorRefundsListQuery query, CancellationToken ct);
}
```

**Dependencias**: identicas v2 + nada nuevo (el helper `ManualCashMovementBuilder` es estatico, no se inyecta).

### 2.3 `IClientCreditService` (MR-V2-03 — expande explicito)

```csharp
public interface IClientCreditService
{
    /// <summary>
    /// Crea una entry de credito a favor del cliente. Llamado SOLO desde
    /// OperatorRefundService.AllocateAsync (no expuesto via API externa).
    /// El entry hereda CurrencyAtEvent del BC.FiscalSnapshot.
    /// RemainingBalance arranca = NetAmount.
    /// </summary>
    Task<ClientCreditEntry> CreateEntryAsync(
        int bookingCancellationId,
        int operatorRefundAllocationId,
        decimal netAmount,
        string currency,
        string userId, string? userName,
        CancellationToken ct);

    /// <summary>
    /// Retira (consume) saldo de un ClientCreditEntry. Soporta 4 kinds:
    /// - PhysicalCash / Transfer: dispara ManualCashMovement Expense.
    /// - ReversedToOperator: requiere approval ClientRefundReversal. Dispara
    ///   ManualCashMovement Income (vuelve a caja antes de re-pago al operador).
    /// - AppliedToNewBooking: throw NotImplementedException (FC4).
    /// Si tras el retiro TODOS los entries del BC tienen RemainingBalance == 0
    /// (reverificado en tx con xmin), invoca BookingCancellationService.OnAllCreditConsumedAsync.
    /// </summary>
    Task<ClientCreditWithdrawalDto> WithdrawAsync(
        Guid entryPublicId,
        WithdrawClientCreditRequest request,
        string userId, string? userName,
        CancellationToken ct);

    Task<ClientCreditEntryDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct);
    Task<PagedResponse<ClientCreditEntryDto>> GetByCustomerAsync(
        Guid customerPublicId, CancellationToken ct);
}
```

**Dependencias** (sin cambios v2): `AppDbContext`, `IBookingCancellationService`, `IApprovalRequestService`, `IAuditService`, `IOperationalFinanceSettingsService`, `ILogger<ClientCreditService>`.

### 2.4 `ManualCashMovementBuilder` (NUEVO en v3, cierra MR-V2-01)

Ubicacion: `src/TravelApi.Domain/Helpers/ManualCashMovementBuilder.cs`

```csharp
public static class ManualCashMovementBuilder
{
    /// <summary>
    /// Construye un ManualCashMovement de tipo Income para un ingreso fisico
    /// de devolucion del operador. NO hace Add() al ChangeTracker ni SaveChanges
    /// — el caller hace _db.ManualCashMovements.Add(...) y commitea la tx envolvente.
    /// Caller debe haber hecho .Include(r => r.Supplier) antes (MR-V2-04).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Si refund.ReceivedAmount <= 0, refund.Supplier == null, refund.Method/Reference vacios.
    /// </exception>
    public static ManualCashMovement BuildIncomeForRefund(
        OperatorRefundReceived refund,
        string createdByUserId);

    /// <summary>
    /// Construye un ManualCashMovement de tipo Expense para un retiro de
    /// ClientCreditEntry. Para Kind == ReversedToOperator construye Income
    /// (caso especial: vuelve a caja). Caller hace Add + SaveChanges envolvente.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Si withdrawal.Amount <= 0, kind == AppliedToNewBooking, kind == KeptAsCredit
    /// (no genera movimiento de caja), method/category vacios.
    /// </exception>
    public static ManualCashMovement BuildExpenseForWithdrawal(
        ClientCreditWithdrawal withdrawal,
        ClientCreditEntry entry,
        string createdByUserId);
}
```

**Por que estatico y NO un servicio**:
- Sin estado, sin DI, sin SaveChanges.
- Reutilizable desde tests unit (no requiere fixture).
- No agrega al ChangeTracker — el caller decide cuando hacerlo.
- Cuando FC2 / FC3 necesiten construir movimientos similares, lo importan directo.

**Tests unit del builder** (nuevos en FC1.2.0b o 2a):
- `BuildIncomeForRefund_ConRefundValido_ReturnsMovimientoCorrecto`.
- `BuildIncomeForRefund_RefundSupplierNull_LanzaInvalidOperationException`.
- `BuildIncomeForRefund_AmountCero_LanzaInvalidOperationException`.
- `BuildExpenseForWithdrawal_ConKindPhysicalCash_DirectionExpense`.
- `BuildExpenseForWithdrawal_ConKindReversedToOperator_DirectionIncome`.
- `BuildExpenseForWithdrawal_ConKindAppliedToNewBooking_LanzaNotImplementedException`.
- `BuildExpenseForWithdrawal_ConKindKeptAsCredit_LanzaInvalidOperationException` (no se debe llamar al builder en ese kind).

### 2.5 `TaxConditionCanonical` enum + helper (sin cambios v2)

(Mismo contenido v2: enum `Unknown/RI/Mono/Exento/CF/Extranjero` + helper `Normalize/ToCanonicalString` con tabla de mappings.)

---

## 3. DTOs

Identico v2 §3 + agregados v3:

### 3.1 `DraftCancellationRequest`

(Sin cambios.)

### 3.2 `ConfirmCancellationRequest`

(Sin cambios.)

### 3.2.bis `ForceArcaConfirmationRequest` (NUEVO en v3, cierra BR-V2-01)

```csharp
public record ForceArcaConfirmationRequest(
    [Required] Guid CreditNoteInvoicePublicId,
    [Required] Guid ApprovalRequestPublicId,
    [Required, MinLength(20), MaxLength(500)] string Reason
);
```

- `CreditNoteInvoicePublicId`: el admin lo busca manualmente en el listado de Invoices (`SELECT * FROM Invoices WHERE OriginalInvoiceId = X AND TipoComprobante IN (3, 8, 13)` — NC tipo A/B/C).
- `ApprovalRequestPublicId`: debe ser un `InvariantOverride` aprobado con `entityType="BookingCancellation"`, `entityId=bc.Id`. **NO** se acepta un approval de tipo `InvoiceAnnulment` (entityId mismatch).
- `Reason`: motivo escrito por el admin (min 20 chars, para auditoria).

### 3.3 Matriz fiscal — corrected (sin cambios v2)

(Sin cambios.)

### 3.4 `VoidAllocationRequest` (NUEVO en v3, cierra MR-V2-06)

```csharp
public record VoidAllocationRequest(
    [Required, MinLength(20), MaxLength(500)] string Reason,
    [Required] Guid ApprovalRequestPublicId
);
```

### 3.5 `ReassociateAllocationRequest` (NUEVO en v3, cierra MR-V2-06)

```csharp
public record ReassociateAllocationRequest(
    [Required] Guid TargetBookingCancellationPublicId,
    [Required, MinLength(20), MaxLength(500)] string Reason,
    [Required] Guid ApprovalRequestPublicId
);
```

---

## 4. Endpoints REST + ownership

### 4.0 Tabla ownership (sin cambios v2)

(Sin cambios — `BookingCancellation` + `ClientCreditEntry` extension.)

### 4.1 `CancellationsController` — `api/cancellations`

| Verb + path | Body | Permiso | Ownership | HTTP |
|---|---|---|---|---|
| `POST /api/cancellations` | `DraftCancellationRequest` | `ReservasCancel` | `Reserva` (via body) | 201, 409, 403 |
| `POST /api/cancellations/{publicId}/confirm` | `ConfirmCancellationRequest` | `ReservasCancelWithPayment` | `BookingCancellation` (via path) | 200, 409, 422, 403 |
| `POST /api/cancellations/{publicId}/abort` | `{ reason }` | `ReservasCancel` | `BookingCancellation` | 200, 409, 403 |
| `POST /api/cancellations/{publicId}/force-arca-confirmation` | `ForceArcaConfirmationRequest` | **`CancellationsForceArcaConfirmation`** (NUEVO permiso) | NO (Admin-only via permission) | 200, 409, 422, 403, 404 |
| `GET /api/cancellations/{publicId}` | — | `ReservasView` | `BookingCancellation` | 200, 404, 403 |
| `GET /api/cancellations/reserva/{reservaPublicIdOrLegacyId}` | — | `ReservasView` | `Reserva` | 200, 404, 403 |
| `GET /api/cancellations` | query | `ReservasView` (+ filtro mine si no `ReservasViewAll`) | — | 200, 403 |

### 4.2 `OperatorRefundsController` — `api/operator-refunds`

(Sin cambios vs v2 + 2 endpoints void/reassociate ya listados v2.)

### 4.3 `ClientCreditsController` — `api/client-credits`

(Sin cambios v2.)

### 4.4 Nuevo permission `CancellationsForceArcaConfirmation` (BR-V2-01)

**Estado verificado**: el permiso NO existe hoy (`grep -r "CancellationsForceArcaConfirmation"` -> 0 hits). Hay un `ConfiguracionAfip` (`configuracion.afip`) pero esta scoped a "configurar credenciales AFIP", semantica distinta.

**Decision**: crear nuevo permiso en `Permissions.cs`:

```csharp
// FC1.2 v3 (BR-V2-01): forzar transicion fiscal del BC cuando el callback automatico fallo.
// Solo Admin (alto riesgo: salta el flujo normal de approval InvoiceAnnulment).
public const string CancellationsForceArcaConfirmation = "cancellations.force_arca_confirmation";
```

Agregar al `AllByModule["Reservas"]` y a `DefaultAdmin`. **NO** lo recibe ni Colaborador ni Vendedor (decision tomada por Gaston).

---

## 5. Validators (sin FluentValidation)

Mantengo v2 + agregados v3:

**`BookingCancellationService.ConfirmAsync` guards completos** — sin cambios respecto a v2, pero el paso 9 ahora documenta el cross-reference fiscal (BR-V2-03):

> Paso 9 (en pseudocodigo §6.1):
> ```csharp
> // BR-V2-03 cross-reference: el InvariantOverride aprobado para el BC opera
> // como el approval implicito de la NC fiscal. Trazabilidad cruzada:
> //   - Invoice.AnnulmentReason recibe prefijo "BC override <approvalRequestPublicId>: <reason original>"
> //   - Invoice.AnnulmentApprovalRequestId (FK nullable nueva, ver §10.1) recibe el ApprovalRequest.Id
> await _invoiceService.EnqueueAnnulmentAsync(
>     originatingInvoiceId, userId, userName,
>     $"BC override {approvalRequest.PublicId}: {request.OverrideReason ?? request.Reason}",
>     requesterIsAdmin: true,
>     approvalRequestId: approvalRequest.Id, // <-- nuevo: pasa el ID al InvoiceService
>     ct);
> ```
>
> El InvoiceService al persistir la annulacion setea ambos campos del Invoice.

**`BookingCancellationService.ForceArcaConfirmationAsync` guards (NUEVO en v3, BR-V2-01)**:

1. BC existe (busca por `publicId`). Si no: 404.
2. BC esta en `Status == AwaitingFiscalConfirmation` (la unica transicion valida hacia `AwaitingOperatorRefund` manual). **Idempotencia**: si BC esta en `AwaitingOperatorRefund` o adelante, retornar el DTO actual + log warning + audit `BookingCancellationArcaConfirmedManually_NoOp` (200 OK no-op, **no es error**).
3. `request.CreditNoteInvoicePublicId` apunta a una Invoice existente con:
   - `OriginalInvoiceId == bc.OriginatingInvoiceId`.
   - `TipoComprobante in {3, 8, 13}` (NC).
   - `Resultado == "A"` y `CAE != null` (AFIP aprobo).
   - `AnnulmentStatus == None` o `Pending` o `Succeeded` (no validamos esto en el original — la NC es un Invoice aparte).
   - Si no matchea: `InvalidOperationException("La Invoice referenciada no es una NC valida de la factura original del BC.")`.
4. `request.ApprovalRequestPublicId` apunta a un `ApprovalRequest` con:
   - `Type == InvariantOverride`.
   - `EntityType == "BookingCancellation"`, `EntityId == bc.Id`.
   - `Status == Approved`.
   - `RequestedByUserId == userId` (el admin que ejecuta).
   - Si no matchea: `ApprovalRequiredException`.
5. Setear:
   ```csharp
   bc.Status = AwaitingOperatorRefund;
   bc.CreditNoteInvoiceId = creditNoteInvoiceId;
   bc.ArcaConfirmedManuallyAt = DateTime.UtcNow;     // campo nuevo, opcional, ver §10.1
   bc.ArcaConfirmedManuallyByUserId = userId;        // campo nuevo, opcional
   ```
6. `_approvalService.MarkConsumedAsync(approval.Id, ct)`.
7. Audit `BookingCancellationArcaConfirmedManually` con metadata:
   ```json
   {
     "creditNoteInvoiceId": <int>,
     "creditNoteInvoicePublicId": "<guid>",
     "approvalRequestId": <int>,
     "approvalRequestPublicId": "<guid>",
     "reason": "<min 20 chars>",
     "manuallyConfirmedByUserId": "<userId>"
   }
   ```
8. `SaveChangesAsync`.
9. Retornar DTO actualizado.

**Decision**: este endpoint **NO** ejecuta `OnArcaSucceededAsync` literalmente (que dispara su propio audit `BookingCancellationArcaConfirmed`). Hace el trabajo equivalente con audit distinto (`...Manually`) para distinguir manual vs automatico en queries de auditoria. **Justificacion**: si en el futuro alguien analiza "cuantos BCs entraron a `AwaitingOperatorRefund` por callback automatico vs manual", esto es trivialmente discriminable. Si compartiamos el audit string, no.

**`OperatorRefundService.AllocateAsync` guards**: sin cambios v2.

**`ClientCreditService.WithdrawAsync` guards**: sin cambios v2.

---

## 6. Coordinacion inter-service

### 6.1 T0 — `BookingCancellationService.ConfirmAsync` (BR-V2-03 cross-reference)

```
Begin Tx (1 retry xmin envolvente)
  1. Cargar BC + Reserva + OriginatingInvoice + Customer + Supplier (Include).
  2. Guards (estado Drafted, snapshot completo, approval si aplica,
     OriginatingInvoice.AnnulmentStatus != Succeeded, feature flag).
  3. Snapshot fiscal (Normalize + ToCanonicalString de Agency/Supplier/Customer).
  4. bc.Status = AwaitingFiscalConfirmation.
  5. bc.Reserva.Status = "PendingOperatorRefund".
  6. bc.ConfirmedWithClientAt = DateTime.UtcNow. ConfirmedByUserId/Name.
  7. bc.OperatorRefundDueBy = UtcNow + _settings.OperatorRefundTimeoutDays.
  8. SaveChangesAsync.
  9. Llamada directa con cross-reference (BR-V2-03):
        var crossRefReason = approvalRequest != null
            ? $"BC override {approvalRequest.PublicId}: {request.OverrideReason ?? request.Reason}"
            : $"BC cancellation: {request.Reason}";
        await _invoiceService.EnqueueAnnulmentAsync(
            originatingInvoiceId, userId, userName, crossRefReason,
            requesterIsAdmin: true,
            approvalRequestId: approvalRequest?.Id,  // pasa el InvariantOverride si existe
            ct);
     // El InvoiceService setea Invoice.AnnulmentReason = crossRefReason
     // + Invoice.AnnulmentApprovalRequestId = approvalRequest?.Id (campo nuevo §10.1).
 10. Audit "BookingCancellationConfirmed" con metadata incluyendo
     approvalRequestPublicId si hubo override.
 11. Si paso 5 tenia approval: _approvalService.MarkConsumedAsync(approval.Id, ct).
Commit Tx
```

**Cambio en firma `IInvoiceService.EnqueueAnnulmentAsync`** (BR-V2-03 sub-cambio):

```csharp
Task EnqueueAnnulmentAsync(
    int invoiceId, string userId, string? userName, string reason,
    bool requesterIsAdmin = false,
    int? approvalRequestId = null,  // <-- nuevo parametro opcional v3
    CancellationToken ct = default);
```

Backward-compat: el parametro es opcional default `null`. Callers viejos no rompen. El nuevo parametro se persiste en `Invoice.AnnulmentApprovalRequestId` (campo nuevo §10.1) si se pasa.

### 6.1.bis Force ARCA confirmation (BR-V2-01)

```
Begin Tx (sin retry xmin — operacion humana, no concurrente)
  1. Cargar BC por publicId con Include(Reserva).
  2. Idempotencia: si BC.Status >= AwaitingOperatorRefund → audit no-op + return DTO actual.
  3. Validar BC.Status == AwaitingFiscalConfirmation.
  4. Cargar Invoice de la NC por CreditNoteInvoicePublicId. Validar:
     - OriginalInvoiceId == BC.OriginatingInvoiceId.
     - TipoComprobante in {3,8,13}.
     - Resultado == "A" + CAE != null.
  5. Validar ApprovalRequest (InvariantOverride, entityType=BookingCancellation, entityId=bc.Id, Approved, requestedBy=userId).
  6. bc.Status = AwaitingOperatorRefund.
  7. bc.CreditNoteInvoiceId = creditNoteInvoice.Id.
  8. bc.ArcaConfirmedManuallyAt = UtcNow. ArcaConfirmedManuallyByUserId = userId.
  9. _approvalService.MarkConsumedAsync(approval.Id, ct).
 10. Audit "BookingCancellationArcaConfirmedManually" con metadata completa.
 11. SaveChangesAsync.
Commit Tx
```

**Race conditions**: si el callback automatico llega entre paso 3 y paso 6, una de las dos transiciones gana (la primera). La segunda detecta BC ya transicionado y hace no-op. No corrupcion: ambos llegan al mismo estado final (`AwaitingOperatorRefund`).

### 6.2 T1 — Callback ARCA (sin cambios v2)

(Sin cambios.)

### 6.3 T2 — `OperatorRefundService.AllocateAsync` (MR-V2-04 + MR-V2-05)

Paso 10 corregido:

```
 10. Si T2 fue exitoso: crear ManualCashMovement Income via
     ManualCashMovementBuilder.BuildIncomeForRefund(refund, userId).
     IMPORTANTE (MR-V2-04): refund debe venir con .Include(r => r.Supplier).
     IMPORTANTE (MR-V2-05): el movimiento queda con
       - RelatedReservaId = null  (el ingreso N:M cubre potencialmente varias BCs)
       - OperatorRefundReceivedId = refund.Id  (trazabilidad)
     El Libro de Caja (TreasuryService.GetCashSummaryAsync) debe detectar
     "RelatedReservaId IS NULL AND OperatorRefundReceivedId IS NOT NULL" y
     renderizar:
       "Devolucion operador {Supplier.Name} ({COUNT(allocations) BCs asociados})"
     en vez del default "Reserva sin ID" o similar.
     SaveChanges del paso 10 envuelve la creacion.
```

**Pseudocodigo del builder usage**:
```csharp
var movement = ManualCashMovementBuilder.BuildIncomeForRefund(refund, userId);
_db.ManualCashMovements.Add(movement);
// SaveChanges se ejecuta al final del paso 10 dentro de la tx envolvente.
```

### 6.3.bis Void y Reassociate de allocations (NUEVO en v3, cierra MR-V2-06)

#### `VoidAllocationAsync(allocationPublicId, request, userId, userName, ct)`

```
Begin Tx (retry xmin envolvente, 3 intentos backoff 100/400/1600 ms)
  1. Cargar OperatorRefundAllocation con .Include(a => a.OperatorRefundReceived)
     .Include(a => a.BookingCancellation).Include(a => a.DeductionLines).Include(a => a.ClientCreditEntry).ThenInclude(c => c.Withdrawals).
  2. Guards:
     - allocation existe.
     - allocation.Status == Active (no double-void; si == Voided → 409 + log warning).
     - bc.Status in {AwaitingOperatorRefund, ClientCreditApplied} (no si esta en Closed: el credito ya se consumio + ese caso requiere reversal explicito de FC4).
     - approval `CobranzasAnnul` validado: `FindActiveApprovedAsync(InvariantOverride, "OperatorRefundAllocation", allocation.Id, userId, ct)`.
     - Validar que el ClientCreditEntry asociado NO tiene Withdrawals consumidos (si los tiene: rechazar con InvalidOperationException("La allocation tiene retiros consumidos por el cliente. Iniciar reversal de cliente primero.")).
  3. allocation.Status = Voided. VoidedAt = UtcNow. VoidedByUserId/Name. VoidedReason.
  4. refund.AllocatedAmount -= allocation.NetAmount.
     (CHECK SQL chk_..._allocated_not_negative aplica.)
  5. ClientCreditEntry asociado: RemainingBalance = 0; entry.Status = Voided.
     (Si no hay withdrawals: safe; si los hay: el guard del paso 2 ya rechazo.)
  6. Si bc no tiene mas allocations activas:
     - bc.Status volver a AwaitingOperatorRefund (revertir si estaba en ClientCreditApplied).
     - bc.Reserva.Status sigue en "PendingOperatorRefund".
  7. ManualCashMovement: NO se anula el movimiento original.
     Razon: el operador DEPOSITO la plata. Lo que cambia es la imputacion, no el cashflow.
     OPS NOTE: el contador puede necesitar un movimiento contra-asiento separado
     si la void corresponde a un reembolso fisico al operador. FC2 / FC4 lo modelara.
  8. _approvalService.MarkConsumedAsync(approval.Id, ct).
  9. Audit "OperatorRefundAllocationVoided" con metadata { allocationId, netAmount, approvalId, reason, previousBcId }.
 10. SaveChangesAsync.
Commit Tx
```

**Idempotencia**: segunda llamada con misma allocation -> guard paso 2 detecta `Status == Voided` -> 409 (NO no-op aca: el caller esta haciendo algo mal, conviene avisar). Diferencia con ForceArca: aca el cliente fallback no es "buscar la realidad fiscal", es "el contador toco doble por error".

#### `ReassociateAllocationAsync(allocationPublicId, request, userId, userName, ct)`

```
Begin Tx (retry xmin envolvente, 3 intentos)
  1. Validar approval CobranzasAnnul para entityType=OperatorRefundAllocation, entityId=allocation.Id.
  2. Hacer void de la allocation actual (steps internos identicos al VoidAllocationAsync pasos 1-7
     pero SIN llamar al audit ni MarkConsumed — esos se hacen al final).
  3. Crear allocation nueva apuntando a request.TargetBookingCancellationPublicId,
     identica en NetAmount + DeductionLines (snapshot fiscal del NUEVO BC, no del viejo).
     (Recomputar matriz fiscal: el nuevo BC puede tener distinta condicion supplier
      → re-validar guards de AllocateAsync.)
  4. Crear ClientCreditEntry nuevo para la nueva BC.
  5. Si la nueva BC pasa de AwaitingOperatorRefund a ClientCreditApplied
     (porque ya tenia allocations previas y la suma cubre el monto del BC),
     llamar _bcService.OnAllocationRecordedAsync(...).
  6. Audit UNICO "OperatorRefundAllocationReassociated" con metadata:
     { oldAllocationId, newAllocationId, oldBcId, newBcId, netAmount, approvalId, reason }.
     (NO doble audit Void + Create — el reviewer del Libro de Auditoria entiende mas claro
      el evento unico.)
  7. _approvalService.MarkConsumedAsync(approval.Id, ct).
  8. SaveChangesAsync.
Commit Tx
```

**ManualCashMovement**: NO se duplica. El ingreso original sigue siendo el mismo. Solo cambia la imputacion contable (que BC se beneficia).

**Idempotencia**: si la reassociate falla en paso 4 o 5 (ej. la nueva BC no esta en estado valido), la tx revierte completamente. La allocation vieja vuelve a estar `Active`.

**Concurrencia**: si dos admins reassociate la misma allocation paralelos, el primer SaveChanges gana via xmin token. Segundo retry detecta `Status == Voided` y rechaza 409.

### 6.4 T3 — `ClientCreditService.WithdrawAsync` (MR-V2-04 reused)

Paso 5/6 (donde se crea ManualCashMovement) corregido para usar el builder estatico:

```
  5. Si Kind in { PhysicalCash, Transfer }:
       var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, userId);
       _db.ManualCashMovements.Add(movement);
       withdrawal.ManualCashMovementId = movement.Id;  // FK set despues del SaveChanges al final del paso 8.
       // NOTA: el caller hace Include(entry.BookingCancellation) si necesita SupplierName en algun audit.
  6. Si Kind == ReversedToOperator:
       var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, userId);
       // BuildExpenseForWithdrawal detecta kind==ReversedToOperator y lo construye como Income (caso especial).
       _db.ManualCashMovements.Add(movement);
       Audit "ClientRefundReversalApproved" con metadata.
```

### 6.5 Idempotencia ARCA (FC2 stub) — sin cambios v2.

### 6.6 `ManualCashMovementBuilder` helper (REEMPLAZA §6.6 v2, cierra MR-V2-01 + MR-V2-04 + MR-V2-05)

**Donde vive**: `src/TravelApi.Domain/Helpers/ManualCashMovementBuilder.cs`. Estatico, sin estado, sin DI.

**Por que estatico (decision v3 reemplaza decision v2)**:
- En v2 deje los builders como privados duplicados en los services. El reviewer pidio (MR-V2-01) extraer a helper compartido. Lo hago en v3 porque:
  - Es codigo sin estado (construye un POCO).
  - La validacion (`Amount > 0`, `Direction valido`, `strings no vacios`) es identica entre Income/Expense.
  - Reusable desde tests unit sin fixture.
- NO es servicio inyectado porque sigue sin tocar `SaveChanges`. El caller del service decide cuando hacer Add + Commit.

**Snippet completo**:

```csharp
public static class ManualCashMovementBuilder
{
    public static ManualCashMovement BuildIncomeForRefund(
        OperatorRefundReceived refund,
        string createdByUserId)
    {
        // GUARDS (MR-V2-04: refund.Supplier debe estar Include-d):
        if (refund.ReceivedAmount <= 0)
            throw new InvalidOperationException("ReceivedAmount debe ser > 0.");
        if (refund.Supplier == null)
            throw new InvalidOperationException("OperatorRefundReceived.Supplier no esta Included (.Include(r => r.Supplier) en el caller).");
        if (string.IsNullOrWhiteSpace(refund.Method))
            throw new InvalidOperationException("Method requerido.");

        return new ManualCashMovement
        {
            Direction = CashMovementDirections.Income,
            Amount = EconomicRulesHelper.RoundCurrency(refund.ReceivedAmount),
            OccurredAt = refund.ReceivedAt,
            Method = refund.Method,
            Category = "OperatorRefund",
            Description = $"Devolucion del operador {refund.Supplier.Name} ({refund.PublicId})",
            Reference = refund.Reference,
            CreatedBy = createdByUserId,
            RelatedSupplierId = refund.SupplierId,
            RelatedReservaId = null,  // MR-V2-05: N:M, trazabilidad via OperatorRefundReceivedId
            OperatorRefundReceivedId = refund.Id,
            ClientCreditWithdrawalId = null,
        };
    }

    public static ManualCashMovement BuildExpenseForWithdrawal(
        ClientCreditWithdrawal withdrawal,
        ClientCreditEntry entry,
        string createdByUserId)
    {
        if (withdrawal.Amount <= 0)
            throw new InvalidOperationException("Amount debe ser > 0.");
        if (withdrawal.Kind == WithdrawalKind.AppliedToNewBooking)
            throw new NotImplementedException("AppliedToNewBooking diferido a FC4.");
        if (withdrawal.Kind == WithdrawalKind.KeptAsCredit)
            throw new InvalidOperationException("KeptAsCredit no genera ManualCashMovement (saldo se queda como credito).");

        // Caso especial: ReversedToOperator vuelve plata a caja (Income).
        var direction = withdrawal.Kind == WithdrawalKind.ReversedToOperator
            ? CashMovementDirections.Income
            : CashMovementDirections.Expense;

        var category = withdrawal.Kind switch
        {
            WithdrawalKind.PhysicalCash => "ClientCreditWithdrawal",
            WithdrawalKind.Transfer => "ClientCreditWithdrawal",
            WithdrawalKind.ReversedToOperator => "ClientCreditReversal",
            _ => throw new InvalidOperationException($"Kind no soportado: {withdrawal.Kind}"),
        };

        return new ManualCashMovement
        {
            Direction = direction,
            Amount = EconomicRulesHelper.RoundCurrency(withdrawal.Amount),
            OccurredAt = withdrawal.OccurredAt,
            Method = withdrawal.Method,
            Category = category,
            Description = $"Retiro credito cliente {entry.PublicId} ({withdrawal.Kind})",
            Reference = withdrawal.Reference,
            CreatedBy = createdByUserId,
            RelatedSupplierId = null,
            RelatedReservaId = entry.BookingCancellation?.ReservaId,  // SI: el retiro es 1:1 con la BC
            OperatorRefundReceivedId = null,
            ClientCreditWithdrawalId = withdrawal.Id,
        };
    }
}
```

**Validacion duplicada vs Treasury**: el Treasury hace validacion + SaveChanges. El builder hace SOLO validacion + construccion. Coexisten: el cashier por UI sigue usando `TreasuryService.CreateManualMovementAsync` (con su SaveChanges). El modulo de cancelacion usa el builder + commit envolvente. Si en el futuro Treasury quiere reusar el builder, lo importa.

---

## 7. Concurrencia xmin + retry + fixture (BR-V2-04)

### 7.1 Donde aplicar retry

Sin cambios v2.

### 7.2 Codigo de referencia

Sin cambios v2.

### 7.3 Tests obligatorios xmin paralelos + smoke DI (cierra BR-V2-04)

**Fixture extendido — registro de TODAS las dependencias transitivas**:

```csharp
public IServiceProvider BuildServiceProvider()
{
    var services = new ServiceCollection();

    // EF + interceptor (scoped).
    services.AddDbContext<AppDbContext>(o => o
        .UseNpgsql(ConnectionString)
        .AddInterceptors(new BusinessInvariantInterceptor()), ServiceLifetime.Scoped);

    // Logging.
    services.AddLogging();

    // Repositorios genericos.
    // IMPORTANTE (BR-V2-04): verificar el nombre exacto del wrapper en el proyecto.
    // Buscar antes: grep -r "IRepository<" src/ -> identificar el contrato real
    // (puede ser EfRepository, GenericRepository, etc.). Si NO existe, omitir.
    // services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));

    // User context (BR-V2-04: muchos services lo inyectan).
    services.AddScoped<IUserContext, FakeUserContextForTests>();

    // Services del modulo (orden importa para resolucion).
    services.AddScoped<IApprovalRequestService, ApprovalRequestService>();
    services.AddScoped<IAuditService, AuditService>();
    services.AddScoped<IOperationalFinanceSettingsService, OperationalFinanceSettingsService>();

    // BC implementa AMBAS interfaces (MR-V2-02 + BR-04).
    services.AddScoped<BookingCancellationService>();
    services.AddScoped<IBookingCancellationService>(sp => sp.GetRequiredService<BookingCancellationService>());
    services.AddScoped<IInvoiceAnnulmentBcBridge>(sp => sp.GetRequiredService<BookingCancellationService>());

    services.AddScoped<IOperatorRefundService, OperatorRefundService>();
    services.AddScoped<IClientCreditService, ClientCreditService>();

    // Mock IInvoiceService — AllocateAsync no lo necesita, pero el constructor de BC si.
    services.AddScoped<IInvoiceService, FakeInvoiceServiceForTests>();

    return services.BuildServiceProvider();
}
```

**Smoke test obligatorio (BR-V2-04)** — corre ANTES de los 4 tests funcionales:

```csharp
public class OperatorRefundConcurrencyTests : IClassFixture<PostgresIntegrationFixture>
{
    private readonly PostgresIntegrationFixture _fixture;

    public OperatorRefundConcurrencyTests(PostgresIntegrationFixture fixture)
        => _fixture = fixture;

    [Fact]
    public void BuildServiceProvider_ResolvesAllServices()
    {
        // BR-V2-04: este smoke ahorra horas de debug.
        // Si falla, el implementador sabe inmediatamente que falta registrar X.
        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IBookingCancellationService>().Should().NotBeNull();
        sp.GetRequiredService<IInvoiceAnnulmentBcBridge>().Should().NotBeNull();
        sp.GetRequiredService<IOperatorRefundService>().Should().NotBeNull();
        sp.GetRequiredService<IClientCreditService>().Should().NotBeNull();
        sp.GetRequiredService<IAuditService>().Should().NotBeNull();
        sp.GetRequiredService<IApprovalRequestService>().Should().NotBeNull();
        sp.GetRequiredService<IOperationalFinanceSettingsService>().Should().NotBeNull();

        // Tambien validar que BC y Bridge resuelven a la MISMA instancia (MR-V2-02 patron).
        var bcAsService = sp.GetRequiredService<IBookingCancellationService>();
        var bcAsBridge = sp.GetRequiredService<IInvoiceAnnulmentBcBridge>();
        ReferenceEquals(bcAsService, bcAsBridge).Should().BeTrue(
            "BookingCancellationService debe implementar ambas interfaces y registrarse como singleton-per-scope");
    }

    // 4 tests funcionales paralelos despues...
}
```

**Recomendacion al implementador (BR-V2-04 documentado)**:
> Antes de codear los 4 tests paralelos, recorrer los constructores de `BookingCancellationService`, `OperatorRefundService`, `ClientCreditService`, `AuditService`, `ApprovalRequestService`. Para cada `private readonly IFoo _foo` que aparezca, asegurarse que esta registrado en `BuildServiceProvider()`. Si aparece algo no listado arriba (ej. `IUserContext`, `IClock`, `IDateTimeProvider`, `IRepository<>`), agregar el registro o mockear con `FakeXForTests`. **Costo estimado: 1.5h**.

**Test pattern** (en `OperatorRefundConcurrencyTests.cs`):

Sin cambios respecto a v2 — los 4 tests paralelos siguen igual.

### 7.4 Tests ForceArca (NUEVO en v3, cierra BR-V2-01)

En `BookingCancellationServiceTests.cs` (no en concurrency tests):

1. `ForceArca_ConApprovalAprobado_OK`:
   - Setup: BC en `AwaitingFiscalConfirmation`, NC existe con `Resultado="A"`, ApprovalRequest InvariantOverride aprobado para `entityId=bc.Id`.
   - Act: `ForceArcaConfirmationAsync(publicId, request, ...)`.
   - Assert:
     - BC.Status == AwaitingOperatorRefund.
     - BC.CreditNoteInvoiceId == nc.Id.
     - BC.ArcaConfirmedManuallyAt != null. BC.ArcaConfirmedManuallyByUserId == userId.
     - ApprovalRequest.Status == Consumed.
     - AuditLog con Action == "BookingCancellationArcaConfirmedManually".

2. `ForceArca_SinApproval_RechazaApprovalRequired`:
   - Setup: BC en AwaitingFiscalConfirmation, NC existe, ApprovalRequestPublicId apunta a NADA (Guid.NewGuid).
   - Assert: `ApprovalRequiredException`.

3. `ForceArca_BCYaConfirmado_NoOp`:
   - Setup: BC en `AwaitingOperatorRefund` (ya transiciono via callback automatico).
   - Act: ForceArca con request valida.
   - Assert:
     - No throw.
     - BC sigue en AwaitingOperatorRefund (no toca campos).
     - AuditLog tiene Action == "BookingCancellationArcaConfirmedManually_NoOp".
     - HTTP 200 (no 409 — es idempotencia, no error).

---

## 8. Tests obligatorios FC1.2

Identico v2 §8 + adicionales:

**Tests nuevos v3**:
- `BuildServiceProvider_ResolvesAllServices` (smoke DI, BR-V2-04).
- `ForceArca_ConApprovalAprobado_OK` (BR-V2-01).
- `ForceArca_SinApproval_RechazaApprovalRequired` (BR-V2-01).
- `ForceArca_BCYaConfirmado_NoOp` (BR-V2-01).
- `Void_DeAllocationConCreditoConsumido_Rechaza` (MR-V2-06).
- `Void_DeAllocationActiva_OK_LiberaCap` (MR-V2-06).
- `Reassociate_DeAllocationADistintaBc_OK_RecomputaMatriz` (MR-V2-06).
- 7 tests unit del builder estatico (MR-V2-01, listados §2.4).

**Total v3**: ~60 tests integration nuevos + 7 unit. Sumando los 14 FC1.1 -> ~81 tests del modulo.

**Controller-level tests adicionales (BR-V2-01)**:
- `POST /cancellations/{id}/force-arca-confirmation` sin permiso `CancellationsForceArcaConfirmation` -> 403.
- Mismo endpoint con permiso pero BC en estado invalido -> 422.

---

## 9. Estimacion realista por sub-fase (cierra MR-V2-03 estimacion ajustada)

| Sub-fase | Descripcion | Effort | Depende de |
|---|---|---|---|
| FC1.2.0a | Settings (4 nuevos) + migracion incremental + `Invoice.AnnulmentApprovalRequestId` FK nullable (BR-V2-03) + `BC.ArcaConfirmedManuallyAt/By` columnas (BR-V2-01) | 2h | — |
| FC1.2.0b | `TaxConditionCanonical` enum + `TaxConditionNormalizer` helper + 15 tests unit | 1.5h | — |
| FC1.2.0c | Extension `OwnedEntity` enum + `OwnershipResolver` cases nuevos + tests | 1.5h | — |
| FC1.2.0d | `EnableNewCancellationFlow` feature flag setting + wired en services | 0.5h | FC1.2.0a |
| FC1.2.0e | `Permissions.CancellationsForceArcaConfirmation` agregado + asignar a DefaultAdmin + actualizar AllByModule (BR-V2-01) | 0.5h | — |
| FC1.2.0f | `ManualCashMovementBuilder` estatico + 7 tests unit (MR-V2-01) | 1h | — |
| FC1.2.1a | `IInvoiceAnnulmentBcBridge` interface + DI wiring + comment block en Program.cs (MR-V2-02) | 0.5h | — |
| FC1.2.1b | `BookingCancellationService` impl (Draft/Confirm/Abort/On*Async) + tests integration ~19 tests | 6h | 1.2.0a-f, 1.2.1a |
| FC1.2.1c | `ForceArcaConfirmationAsync` impl + 3 tests integration (BR-V2-01) | 2h | 1.2.1b |
| FC1.2.2a | `OperatorRefundService` impl + matriz fiscal + retry xmin envolvente + uso del `ManualCashMovementBuilder` | 5h | 1.2.1b |
| FC1.2.2b | Tests integration ~14 unit + 4 paralelos concurrencia + 1 smoke | 4h | 1.2.2a |
| FC1.2.2c | `VoidAllocationAsync` + `ReassociateAllocationAsync` + 3 tests integration (MR-V2-06) | 1h | 1.2.2a |
| FC1.2.3a | `ClientCreditService` impl + uso del builder | 3h | 1.2.2 |
| FC1.2.3b | Tests integration ~9 | 2h | 1.2.3a |
| FC1.2.4 | `InvoiceService.ProcessAnnulmentJob` hook (callback al bridge) + try/catch + log + `ApprovalRequest` defaults + persistencia `Invoice.AnnulmentApprovalRequestId` (BR-V2-03) | 3h | 1.2.1 |
| FC1.2.5a | `PostgresIntegrationFixture.BuildServiceProvider` extension + smoke test BuildServiceProvider_ResolvesAllServices (BR-V2-04) | 1.5h | 1.2.1-3 |
| FC1.2.5b | DI registration: recorrer constructores 5 services + agregar dependencias transitivas faltantes (BR-V2-04) | 1.5h | 1.2.5a |
| FC1.2.6 | 3 Controllers + endpoint force-arca-confirmation + permisos + ownership decorators + DI wiring en `Program.cs` + `CancellationsControllerTests` etc. (web factory) | 4.5h | 1.2.1-3 |
| FC1.2.7a | E2E happy + 5 variantes (`CancellationFlowE2ETests.cs`) | 3h | 1.2.6 |
| FC1.2.7b | Audit constants + 10 tests "log contains action" (BookingCancellationArcaConfirmedManually inclusive) + observability counters Serilog | 2h | 1.2.6 |
| FC1.2.7c | Documentacion (`docs/explicaciones/2026-05-XX-fc1-2-implementacion.md`) + ADR locales para decisiones v2/v3 + actualizar precondicion ResponsibleUserId (BR-V2-02) | 2h | 1.2.7a |
| Buffer realista | bugs imprevistos, refactors menores, comments review | 4-6h | — |

**Total v3**: **42-46h** codigo + signoff fiscal/contable pendiente (no se contabiliza en horas, ver §13).

---

## 10. Migracion + feature flag

### 10.1 Migracion incremental FC1.2.0

Una sola migracion EF que agrega:

**OperationalFinanceSettings** (sin cambios v2):
- `OperatorRefundTimeoutDays` (int, default 60).
- `OnePerReservaInvoicePolicy` (bool, default true).
- `Ley25345ThresholdAmount` (decimal, default 1000000 — confirmar con contador).
- `PhysicalRefundAlertThreshold` (decimal, default 100000 — confirmar con contador).
- `EnableNewCancellationFlow` (bool, default `false`).

**Invoice** (NUEVO en v3, cierra BR-V2-03):
- `AnnulmentApprovalRequestId` (int? FK nullable → `ApprovalRequests.Id`, `OnDelete: SetNull`).
- Indice `IX_Invoices_AnnulmentApprovalRequestId` para queries de auditoria.

**BookingCancellation** (NUEVO en v3, cierra BR-V2-01):
- `ArcaConfirmedManuallyAt` (DateTime?).
- `ArcaConfirmedManuallyByUserId` (string?, FK a `AspNetUsers.Id`, `OnDelete: NoAction`).

**Verificacion realizada** (cierra precondicion BR-V2-03):
- `grep -n "AnnulmentApprovalRequestId" src/TravelApi.Domain/Entities/Invoice.cs` → 0 hits. **Campo confirmado inexistente, requiere migracion.**
- `grep -n "AnnulmentReason" src/TravelApi.Domain/Entities/Invoice.cs` → existe (line 56, `string? AnnulmentReason` MaxLength 500). Se reusa para el cross-reference prefix.

**Rollback**: la migracion sigue siendo additiva. Las columnas nuevas son nullable -> rollback `dotnet ef database update <PreviousMigration>` revierte sin perdida de datos.

### 10.2 Backfill datos legacy

(Sin cambios v2.)

### 10.2.1 Precondicion operativa para habilitar `EnableNewCancellationFlow = true` en prod (NUEVO en v3, cierra BR-V2-02)

Antes de pasar el feature flag a `true` en produccion, correr la siguiente query bloqueante:

```sql
SELECT COUNT(*) FROM "TravelFiles"
WHERE "Status" NOT IN ('Closed', 'Cancelled', 'Archived')
  AND "ResponsibleUserId" IS NULL;
```

**Caso A — resultado = 0**: feature flag puede habilitarse sin riesgo. El ownership BC -> Reserva.ResponsibleUserId resuelve correctamente para todas las reservas activas.

**Caso B — resultado > 0**:
- **Opcion B.1**: ejecutar comando administrativo `users.set-responsible` (B1.15) para asignar responsable a esas reservas. Es la opcion recomendada.
- **Opcion B.2**: documentar que esas reservas solo podran cancelarse via usuarios con `ReservasViewAll` (no Vendedor con solo `ReservasView`) hasta el backfill. Esto es soft: el ownership decorator del endpoint Cancel devolveria 403 al vendedor; un Admin/Colaborador con ViewAll pasaria. **Riesgo aceptado: el Vendedor no ve sus reservas viejas en la lista BC.**

**Documentar en `docs/explicaciones/2026-05-XX-fc1-2-implementacion.md`** que esta query es checklist obligatoria pre-flip-flag-prod.

### 10.3 Rollback fiscal de la decision (BR-V2-03)

Si el signoff fiscal (§13 OPS-FISCAL-001) resulta NEGATIVO antes de mergear a prod:

- **Plan B (opcion b del review)**: doble approval. `BookingCancellationService.ConfirmAsync` requiere DOS approvals consumibles:
  - `InvariantOverride` scoped al BC (ya implementado).
  - `InvoiceAnnulment` scoped a la Invoice original (nuevo).
  - Costo estimado: +3h codigo + +2h tests.
- **Plan C (opcion c del review)**: refactor cross-reference completo. `EnqueueAnnulmentAsync` deja de aceptar `requesterIsAdmin: true` y exige siempre approval. El BC service automaticamente promueve el `InvariantOverride` a un `InvoiceAnnulment` clonado.
  - Costo estimado: +6h codigo + +3h tests + cambios en interface publica del InvoiceService.

**Decision arquitectonica**: Plan B es el fallback preferido (menor cambio de superficie). Plan C solo si el contador exige separacion fisica de los approvals.

---

## 11. Operacional

(Sin cambios v2 + agregados):

- **OPS4 (NUEVO en v3)** — monitoreo del endpoint ForceArca. Counter Serilog:
  ```csharp
  _logger.LogInformation("metric:cancellation_force_arca_executed {BcPublicId} {AdminUserId}", bc.PublicId, userId);
  ```
  Si el counter supera N por semana, hay un problema sistematico de callbacks fallidos -> investigar `ProcessAnnulmentJob`.

---

## 12. Auto-critica v3

### 12.1 Lo que sigue sin verificar (heredado v2 + nuevos)

(Items v2 1, 2, 3 quedan vigentes.)

**Nuevos en v3**:
4. **No verifique que `ApprovalRequest.EntityType` acepta `"OperatorRefundAllocation"`** — si no, el implementador tiene que agregar el nuevo entityType al enum/dictionary correspondiente. Posible buffer +0.5h.
5. **No verifique el dispatcher actual de Hangfire** — `JobExpirationTimeout` mencionado en OPS3 puede no existir como opcion configurable directa en la version actual de Hangfire instalada. Si no, ajustar la guidance.
6. **El permiso `CancellationsForceArcaConfirmation` no esta probado en frontend** — el menu de Admin tiene que mostrar el boton condicionalmente. Si el frontend no consume `AllByModule` dinamicamente, hay que tocar UI. FC1.2 no toca frontend. **Tarea diferida a FC1.3 o ticket separado.**
7. **No corri la query de §10.2.1 contra la DB real de Gaston** — el resultado afecta directamente si la opcion B.1 o B.2 es necesaria. Recomendacion: correrla en staging antes del primer deploy.

### 12.2 Blind spots remanentes (asumiendo cierre v3 limpio)

(Items v2 quedan vigentes.)

**Nuevos en v3**:
- **`ForceArcaConfirmationAsync` puede ser usado MAL por Admin**: si el admin elige una NC equivocada (no apunta al `OriginatingInvoiceId` correcto), el guard paso 4 lo rechaza. Pero si elige correctamente PERO la NC tiene un monto distinto al esperado, el BC queda confirmado contra una NC con saldo discrepante. **Mitigacion**: el audit log captura el `creditNoteInvoiceId`, el contador puede auditar. **Tarea diferida**: alerta automatica si `nc.ImporteTotal != originalInvoice.OutstandingBalanceAtIssuance` en el momento del Force.
- **Reassociate concurrente con OnAllocationRecordedAsync**: si la BC vieja perdio su allocation durante un Reassociate mientras `OnAllCreditConsumedAsync` paralelo evaluaba balance, posible inconsistencia. El retry xmin lo cubre pero conviene un test que lo valide. Agregado a §7.4 como tarea "stretch": `Test_ReassociateRace_ConOnAllCreditConsumed` (no obligatorio FC1.2).

### 12.3 Decisiones tomadas en v3 que pueden necesitar revision

(Items v2 quedan vigentes.)

**Nuevos en v3**:
- **Opcion (a) BR-V2-03**: aceptamos que el `InvariantOverride` cubre la NC fiscal con cross-reference. **Si el contador rechaza, fallback Plan B (doble approval).**
- **`ForceArcaConfirmationAsync` solo Admin**: si Operations / Cobranzas senior pide tambien acceder al boton, agregar al permission default Colaborador o crear un nuevo rol "Senior Cobranzas". YAGNI hoy.
- **`Invoice.AnnulmentApprovalRequestId` como FK opcional**: si el contador exige NOT NULL (i.e., toda Annulment debe tener approval explicito), modificar la migracion en FC1.3 + backfill historico. YAGNI hoy.

### 12.4 OPS / observability (sin cambios v2 + OPS4 §11)

---

## 13. Decisiones de seguridad fiscal (NUEVO en v3)

### 13.1 OPS-FISCAL-001: opcion (a) BR-V2-03 — un solo approval cubre la NC fiscal

**Decision tomada (provisional, sujeta a signoff)**:

Cuando `BookingCancellationService.ConfirmAsync` ejecuta el paso 9 (llamada a `InvoiceService.EnqueueAnnulmentAsync`) con `requesterIsAdmin: true`, el approval normal de tipo `InvoiceAnnulment` que aplicaria si el usuario tocara la NC desde el flujo de facturacion **se omite**. La justificacion operacional/legal es:

1. **Equivalencia operacional**: el `InvariantOverride` aprobado por un Admin/Colaborador para el BC (paso 5 del ConfirmAsync) ya cubre el caso de uso "se cancela una reserva con factura emitida". El approval `InvoiceAnnulment` standalone era un workflow paralelo para back-office sin contexto de BC.
2. **Trazabilidad cruzada**:
   - `Invoice.AnnulmentReason` queda con prefijo `"BC override <approvalRequestPublicId>: <reason>"`.
   - `Invoice.AnnulmentApprovalRequestId` (campo nuevo §10.1) referencia el `ApprovalRequest.Id` del `InvariantOverride`.
   - Audit log `BookingCancellationConfirmed` tiene metadata `{ approvalRequestPublicId, ... }`.
   - Audit log `InvoiceAnnulmentRequested` (existente del InvoiceService) puede inspeccionarse cruzando con `Invoice.AnnulmentApprovalRequestId`.
3. **Mitigacion del riesgo fiscal**:
   - La NC fiscal solo se emite tras CAE aprobado por AFIP (validacion externa).
   - El audit logging es completo.
   - El `InvariantOverride` requiere reason >= 20 chars + admin aprobacion.

**OPEN QUESTION OPS-FISCAL-001 — REQUIERE SIGNOFF EXPLICITO ANTES DE MERGEAR A PROD**:

> **Solicitar a Gaston**: programar reunion con (1) contador real de MagnaTravel + (2) `arca-tax-expert-argentina`. Items a confirmar por escrito:
>
> 1. Es valido fiscalmente que la NC se emita tras un `InvariantOverride` BC sin requerir el approval `InvoiceAnnulment` adicional? (Si/No)
> 2. La cross-reference via `Invoice.AnnulmentReason` prefix + `Invoice.AnnulmentApprovalRequestId` FK satisface el requisito de "trazabilidad del que aprobo la annulacion fiscal"? (Si/No)
> 3. Hay obligacion legal/ARCA de un workflow de approval separado para la NC, independiente del workflow de la cancelacion de reserva? (Si/No con cita al art./normativa)
> 4. En caso de respuesta negativa a 1, 2 o 3, cual es la alternativa preferida: Plan B (doble approval) o Plan C (refactor cross-reference)?
>
> **Firmas requeridas**: nombre + matricula contador + fecha. Documento subido a `docs/legal/signoffs/2026-05-XX-fc1-2-fiscal-approval.md`.

**Estado actual**: PENDIENTE. **Blocker para merge a prod**. FC1.2 puede mergearse a `dev` y `staging` con `EnableNewCancellationFlow = true` para QA, **pero no a prod hasta tener el signoff**.

### 13.2 Fallback si signoff es negativo

(Ver §10.3 Plan B / Plan C — descripto.)

### 13.3 Otros items fiscales pendientes (no bloqueantes)

(Heredado de v2 — preguntas contador 1-10 listadas en `docs/preguntas-contador-cancelacion-refund.md`.)

---

## 14. Archivos relevantes inspeccionados (verificacion v3)

Adiciones a la lista v2:
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\Invoice.cs` (verificado: `AnnulmentReason` existe line 56; `AnnulmentApprovalRequestId` NO existe → migracion necesaria).
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\Permissions.cs` (verificado: `CancellationsForceArcaConfirmation` NO existe; `ConfiguracionAfip` existe pero semantica distinta → nuevo permiso necesario).

---

## 15. Resumen ejecutivo v3 (200-300 palabras)

**v3 cierra los 4 bloqueantes v2 + las 6 mejoras aprobadas en bloque por Gaston, y agrega una nueva seccion §13 marcando un OPEN QUESTION fiscal bloqueante para merge a prod**.

- **BR-V2-01 (boton manual ARCA)**: nuevo endpoint admin `POST /api/cancellations/{publicId}/force-arca-confirmation` + metodo `ForceArcaConfirmationAsync` + permiso nuevo `CancellationsForceArcaConfirmation` + columnas `BC.ArcaConfirmedManuallyAt/By` + audit dedicado `BookingCancellationArcaConfirmedManually` (distinto del automatico para discriminar en queries de auditoria) + idempotencia explicita (200 OK no-op si BC ya transiciono).
- **BR-V2-02 (precondicion backfill)**: §10.2.1 con query SQL bloqueante + 2 opciones de mitigacion antes de flip flag prod.
- **BR-V2-03 (decision fiscal)**: opcion (a) aceptada con cross-reference: `Invoice.AnnulmentReason` con prefijo + `Invoice.AnnulmentApprovalRequestId` FK nullable nuevo + nueva seccion §13 con OPEN QUESTION OPS-FISCAL-001 que **bloquea merge a prod hasta signoff contador + arca-tax-expert**. Fallback Plan B / Plan C documentado en §10.3.
- **BR-V2-04 (DI fixture)**: smoke test obligatorio `BuildServiceProvider_ResolvesAllServices` + recomendacion explicita al implementador de recorrer constructores y registrar dependencias transitivas antes de codear los 4 tests funcionales.
- **MR-V2-01..06 aprobados en bloque**: `ManualCashMovementBuilder` estatico en `src/TravelApi.Domain/Helpers/` (MR-V2-01), comentario explicativo bridge DI (MR-V2-02), `IClientCreditService` expandido (MR-V2-03), `refund.Supplier.Name` corregido (MR-V2-04), guidance del Libro de Caja N:M (MR-V2-05), §6.3.bis Void/Reassociate documentado (MR-V2-06).

**Estimacion v3**: 42-46h codigo + signoff fiscal/contable pendiente. Plan listo para `software-architect-reviewer` iteracion 3 + `arca-tax-expert-argentina` consult para §13.
