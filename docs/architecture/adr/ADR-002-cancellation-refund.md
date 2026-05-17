# ADR-002 — Módulo de cancelación de reservas y gestión de refund operador-cliente

- **Status**: Proposed (depends on [ADR-001](ADR-001-domain-invariants.md) acceptance — actualmente `Changes Required`)
- **Date**: 2026-05-13
- **Author(s)**: software-architect agent (rounds 1+2), validado con Gaston, accounting-expert-argentina (rounds 1+2), arca-tax-expert-argentina (rounds 1+2), software-architect-reviewer (rounds 1+2)
- **Supersedes**: R6 de ADR-001 (refund parcial/cuotas modelado acá).
- **Related**:
  - [ADR-001 Domain Invariants](ADR-001-domain-invariants.md)
  - [ADR-001 Review](ADR-001-review-2026-05-12.md)
  - Policy `.claude/agents/travel-agency-domain-expert.md` §"Project-specific policies (MagnaTravel)" (17 reglas)
  - Memoria transversal `memory/project_sistema_impositivo_por_condicion_fiscal.md`
  - Plan táctico de invariantes `.claude/agent-memory/software-architect/project_invariantes_phase3_plan.md`

## 1. Contexto

MagnaTravel-Cloud (ERP de agencia minorista de viajes argentina) hoy tiene tres problemas operativos críticos en el manejo de cancelaciones:

### 1.1 Bug fiscal del Libro de Caja

`AfipService.cs:1050` crea `Payment` con `CreditNoteReversal = true` y `AffectsCash = false` siempre. `TreasuryService.GetCashSummaryAsync` excluye `CreditNoteReversal` del `CashOut`. Cuando una NC se aprueba y la agencia devuelve dinero físico al cliente, el egreso no aparece en el Libro de Caja → INV-CONT-09 violado. Rompe contabilidad.

### 1.2 Modelo operativo de cancelación inexistente

Las reglas de negocio reales (regla 1 a regla 17 del policy MagnaTravel) involucran un flujo T0→T1→T2→T3 (confirmar cliente → CAE NC → recibir refund operador → cliente decide). Hoy el sistema modela solo "anular factura" como evento puntual, sin máquina de estados ni trazabilidad del flujo completo.

### 1.3 Decisiones fiscales no contempladas

- Refund operador siempre parcial (regla 3).
- Tipificación obligatoria deducciones operador (regla 5).
- Retenciones NO se trasladan al cliente (regla 6).
- Saldo cliente sin caducidad (regla 5).
- NC siempre por total facturado (decisión Gaston 2026-05-13 con contador real, sobreescribe regla 8 original).
- Devolución parcial / N retiros (regla 12).
- Reversal post-decisión cliente (no modelado, riesgo operativo).

### 1.4 Sistema impositivo configurable según condición fiscal

HOY MagnaTravel = **Monotributista**. En el futuro puede crecer a **Responsable Inscripto** sin redeploy. Todo módulo nuevo debe contemplar la matriz cruzada `Agency.TaxCondition × Operator.TaxCondition`. Este módulo es el primero "consciente" del régimen impositivo.

## 2. Decisión

Implementar un módulo dedicado de cancelación/refund con **3 aggregate roots**, **máquina de estados tipada**, **multimoneda con TC inmutable**, **idempotencia ARCA via Hangfire**, **concurrencia lock-free**, **llamada directa entre services** (sin MediatR ni outbox de dominio), y **sistema impositivo configurable por `AgencySettings.TaxCondition`**.

### 2.1 Glosario

| Término | Definición |
|---|---|
| **T0** | Confirmación de cancelación con el cliente. La NC se emite a AFIP. |
| **T1** | AFIP responde con CAE de la NC. `Invoice.AnnulmentStatus = Succeeded`. |
| **T2** | Operador devuelve dinero parcial (con deducciones tipificadas). |
| **T3** | Cliente decide qué hacer con el crédito (saldo, retiro físico, transferencia, aplicar a nueva reserva). |
| **BC** | `BookingCancellation` (aggregate root del flujo). |
| **NC** | Nota de Crédito fiscal (Invoice de tipo 3/8/13 según letra original). |
| **ND** | Nota de Débito fiscal. **No se usa en flow normal** (consecuencia de NC siempre por total). |

### 2.2 Decisiones cerradas no negociables

1. **NC siempre por total facturado** (no por neto al cliente). Confirmado con contador real de MagnaTravel.
2. **No hay ND complementaria** en flow normal (consecuencia de #1).
3. **Las deducciones del operador** se contabilizan como **costo aparte de la agencia**, no como menor NC al cliente.
4. **Factura E no se usa** en MagnaTravel (decisión Gaston).
5. **`OnePerReservaInvoicePolicy = true`** como precondición de cancelación. Si la reserva tiene >1 factura activa, cancelación rechaza con mensaje claro.
6. **MagnaTravel HOY = Monotributista**. Sistema debe soportar toggle a RI vía `AgencySettings.TaxCondition` sin redeploy.
7. **`Invoice.AnnulmentStatus` (enum existente) es única SoT fiscal**. `BookingCancellation.Status` la proyecta.
8. **Override `InvoiceAnnulmentSkip` ELIMINADO**. INV-083 pasa a `admitsOverride: false`. NC sin CAE no existe fiscalmente.
9. **Mecanismo de coordinación inter-service**: llamada directa entre services en mismo `DbContext` (no MediatR, no `INotificationDispatcher`, no MassTransit outbox para este caso).
10. **`AccountingEntry` out-of-scope FC1**. Sistema exporta CSV/Excel mensual con columnas sugeridas (debe/haber). Revisitar al migrar a RI.

### 2.3 Modelo de datos

**3 aggregate roots + 1 entidad de relación + 1 child de deducciones + 1 child de retiros = 6 entidades nuevas**, más 3 modificaciones en entidades existentes.

```
BookingCancellation (aggregate root, T0..T3, 1:1 con Reserva)
   └─ FiscalSnapshot (VO inmutable)

OperatorRefundReceived (aggregate root, ingreso físico, cubre N cancelaciones)
   └─ OperatorRefundAllocation[] (children)
        └─ DeductionLine[] (children, 1:N por allocation — BC-1)

ClientCreditEntry (aggregate root, vive en ficha Customer, saldo cliente)
   └─ ClientCreditWithdrawal[] (children, N retiros)
```

#### 2.3.1 Entidades nuevas

```csharp
public class BookingCancellation : IHasPublicId {
    int Id; Guid PublicId;
    int ReservaId; Reserva Reserva;                          // FK NOT NULL, UNIQUE per reserva (INV-081)
    int CustomerId; Customer Customer;
    int SupplierId; Supplier Supplier;
    int OriginatingInvoiceId; Invoice OriginatingInvoice;    // factura A original (única, INV-100)
    int? CreditNoteInvoiceId; Invoice? CreditNoteInvoice;    // NC emitida en T0

    BookingCancellationStatus Status;                        // enum tipado (ver §2.4)
    string Reason;
    DateTime DraftedAt;
    DateTime? ConfirmedWithClientAt;                         // T0
    DateTime? OperatorRequestedAt;
    DateTime? OperatorRefundDueBy;                           // T0 + setting OperatorRefundTimeoutDays
    DateTime? ClosedAt;
    string DraftedByUserId; string? ConfirmedByUserId;

    decimal AmountPaidAtCancellation;                        // snapshot total cobrado en T0
    decimal EstimatedRefundAmount;                           // informativo (pre-deducción)
    decimal ReceivedRefundAmount;                            // denormalizado: SUM(allocations.NetAmount)

    // FiscalSnapshot extendido (R1 del review + items contables BC)
    FiscalSnapshot FiscalSnapshot;                           // value object

    bool? IsLegacyPreCancellationModel;                      // NULL=legacy, false=nuevo modelo

    uint Xmin;                                               // UseXminAsConcurrencyToken (B11)
}

public class FiscalSnapshot {  // value object
    string CustomerTaxIdAtEvent;
    string CustomerTaxConditionAtEvent;
    string SupplierTaxIdAtEvent;
    string SupplierTaxConditionAtEvent;
    string AgencyTaxConditionAtEvent;                        // CRÍTICO para matriz Mono/RI
    string CurrencyAtEvent;
    decimal ExchangeRateAtOriginalInvoice;                   // T0 — congelado al emitir NC (INV-118)
    decimal? ExchangeRateAtOperatorRefundReceipt;            // T2
    decimal? ExchangeRateAtClientWithdrawal;                 // T3
    ExchangeRateSource Source;                               // enum BCRA_A3500/BNA_Mayorista/etc.
    DateTime FetchedAt;
    string? ManualJustification;                             // requerido si Source = Manual (INV-120)
    string ExtrasJson;                                       // metadata adicional
}

public class OperatorRefundReceived : IHasPublicId {
    int Id; Guid PublicId;
    int SupplierId; Supplier Supplier;
    DateTime ReceivedAt;
    decimal ReceivedAmount;                                  // total cheque/transferencia
    decimal AllocatedAmount;                                 // DENORMALIZADO — CHECK <= ReceivedAmount (B1)
    string Method;                                           // Transfer/Cash/Cheque
    string? Reference;
    string Currency;
    decimal ExchangeRateAtReceipt;
    string ReceivedByUserId; string ReceivedByUserName;

    ICollection<OperatorRefundAllocation> Allocations;
    uint Xmin;
}

public class OperatorRefundAllocation : IHasPublicId {   // BC-1 refactor
    int Id; Guid PublicId;
    int OperatorRefundReceivedId; OperatorRefundReceived Refund;
    int BookingCancellationId; BookingCancellation BookingCancellation;
    decimal GrossAmount;                                     // lo que indica el operador
    decimal NetAmount;                                       // = Gross - SUM(Deductions.Amount)
    bool IsVoided;
    int? VoidsAllocationId;                                  // FK retroversa al allocation original
    DateTime CreatedAt;
    string CreatedByUserId;

    int? AccountingEntryRef;                                 // BC-2: NULL en FC1, se llena cuando AccountingEntry exista
    ICollection<DeductionLine> Deductions;                   // 1:N (BC-1)
}

public class DeductionLine : IHasPublicId {   // BC-1 nuevo
    int Id; Guid PublicId;
    int OperatorRefundAllocationId; OperatorRefundAllocation Allocation;
    DeductionKind Kind;
    decimal Amount;                                          // > 0 (INV-112)

    // Solo retenciones/percepciones AR
    string? CertificateNumber;
    string? CertificatePdfUrl;
    DateTime? CertificateDate;
    string? Jurisdiction;                                    // INV-104

    // Solo ForeignTax
    string? ForeignCountryCode;                              // ISO 3166-1
    string? Description;

    // AdministrativeFee/BankingCost/CancellationPenalty
    string? SupportingDocumentRef;
    string? JustificationComment;
    bool MissingFiscalSupport;

    // Other
    string? Comment;
    bool RequiresAccountingReview;
}

public class ClientCreditEntry : IHasPublicId {
    int Id; Guid PublicId;
    int CustomerId; Customer Customer;
    int OperatorRefundAllocationId; OperatorRefundAllocation Allocation;
    int BookingCancellationId; BookingCancellation BookingCancellation;
    decimal CreditedAmount;                                  // = Allocation.NetAmount
    decimal RemainingBalance;                                // DENORMALIZADO — CHECK >= 0 (INV-085)
    DateTime CreatedAt;
    bool IsFullyConsumed;

    ICollection<ClientCreditWithdrawal> Withdrawals;
    uint Xmin;
}

public class ClientCreditWithdrawal : IHasPublicId {
    int Id; Guid PublicId;
    int ClientCreditEntryId; ClientCreditEntry Entry;
    int? ManualCashMovementId; ManualCashMovement? ManualCashMovement;  // null si KeptAsCredit
    decimal Amount;
    WithdrawalKind Kind;
    string ExecutedByUserId; string ExecutedByUserName;
    DateTime ExecutedAt;
    string? ApprovalRequestId;                               // solo para reversal post-T3
}

public enum DeductionKind {
    // Costos operativos (no impuestos)
    AdministrativeFee = 1,
    BankingCost = 2,
    CancellationPenalty = 3,
    // Retenciones impositivas nacionales (crédito fiscal solo RI)
    IvaWithholding = 10,
    IvaPerception = 11,
    IncomeTaxWithholding = 20,
    // Retenciones impositivas provinciales
    IIBBWithholding = 30,
    IIBBPerception = 31,
    // Impuesto extranjero (no crédito fiscal AR)
    ForeignTax = 40,
    // Otros
    Other = 99
}

public enum WithdrawalKind {
    KeptAsCredit = 0,
    PhysicalCash = 1,
    Transfer = 2,
    AppliedToNewBooking = 3,
    ReversedToOperator = 4                                   // B7
}

public enum BookingCancellationStatus {
    Drafted = 0,
    AwaitingFiscalConfirmation = 1,
    AwaitingOperatorRefund = 2,
    ClientCreditApplied = 3,
    Closed = 4,
    AbandonedByOperator = 5,
    Aborted = 6
}

public enum ExchangeRateSource {
    BCRA_A3500 = 1,
    BNA_Mayorista = 2,
    BNA_Minorista = 3,
    AfipOficial = 4,
    Manual = 5
}
```

#### 2.3.2 Modificaciones a entidades existentes

```csharp
// ManualCashMovement.cs — 2 FKs nuevas nullable
class ManualCashMovement {
    int? ClientCreditWithdrawalId;                           // egreso por refund físico
    int? OperatorRefundReceivedId;                           // ingreso por refund operador
}

// Invoice.cs — campo nuevo para reconciliación ARCA
class Invoice {
    DateTime? LastArcaAttemptAt;                             // FC2: timestamp último intento AFIP
}

// EstadoReserva (static class) — agregar const string PendingOperatorRefund
// + CHECK constraint SQL incluyendo "Archived" legacy

// ApprovalRequestType — agregar 4 valores
enum ApprovalRequestType {
    // existentes 0..6
    InvariantOverride = 7,         // ADR-001 review B4
    ProviderRefundRequest = 8,
    ClientRefundReversal = 9,      // B7 — cliente devuelve dinero ya recibido
    MisassociationReversal = 10    // corrección cashier
}
```

#### 2.3.3 CHECK constraints SQL (convención nueva — sin precedente en el proyecto)

```sql
ALTER TABLE "OperatorRefundReceived"
  ADD CONSTRAINT chk_refund_allocated_not_exceeds
  CHECK ("AllocatedAmount" >= 0 AND "AllocatedAmount" <= "ReceivedAmount");

ALTER TABLE "ClientCreditEntry"
  ADD CONSTRAINT chk_credit_remaining_non_negative
  CHECK ("RemainingBalance" >= 0 AND "RemainingBalance" <= "CreditedAmount");

ALTER TABLE "Reservas"
  ADD CONSTRAINT chk_reserva_status_valid
  CHECK ("Status" IN ('Budget','Confirmed','Traveling','Closed','Cancelled','PendingOperatorRefund','Archived'));

ALTER TABLE "BookingCancellations"
  ADD CONSTRAINT uq_cancellation_per_reserva UNIQUE ("ReservaId");

CREATE UNIQUE INDEX uq_alloc_active_per_refund_per_bc
  ON "OperatorRefundAllocations" ("OperatorRefundReceivedId", "BookingCancellationId")
  WHERE "IsVoided" = false;

ALTER TABLE "OperatorRefundAllocations"
  ADD CONSTRAINT chk_alloc_net_positive
  CHECK ("NetAmount" >= 0 AND "GrossAmount" >= "NetAmount");

ALTER TABLE "DeductionLines"
  ADD CONSTRAINT chk_deduction_amount_positive
  CHECK ("Amount" > 0);
```

Convención EF Core:
- CHECK con nombre canonico `chk_<tabla>_<concepto>`.
- Implementación via `migrationBuilder.Sql(@"ALTER TABLE ...")`.
- Interceptor en `AppDbContext` que captura `PostgresException.SqlState = '23514'` y lanza `BusinessInvariantViolationException` (HTTP 409).

#### 2.3.4 Concurrency tokens (B11)

EF Core 8 + Npgsql: `xmin` de Postgres via `UseXminAsConcurrencyToken()`:

```csharp
// AppDbContext.OnModelCreating
modelBuilder.Entity<BookingCancellation>().UseXminAsConcurrencyToken();
modelBuilder.Entity<OperatorRefundReceived>().UseXminAsConcurrencyToken();
modelBuilder.Entity<ClientCreditEntry>().UseXminAsConcurrencyToken();
```

`uint Xmin` es shadow property. **Pre-requisito FC1.1**: `backend-dotnet-senior` verifica funcional en stack actual.

### 2.4 Máquina de estados `BookingCancellation`

```
                  ┌──────────────────────────────────────────────┐
                  │                                              │
                  v                                              │
[Drafted] ──confirmWithClient()──> [AwaitingFiscalConfirmation]──┤
   │                                       │                    │
   │                                       │ AFIP CAE           │ AFIP fail (retry/manual)
   │                                       v                    │
   │                          [AwaitingOperatorRefund] <─────────┘
   │ abort()                               │
   │ (solo Drafted)                        │ recordRefund() (≥1 allocation)
   │                                       │
   v                                       v
[Aborted]                          [ClientCreditApplied] ──────────┐
                                           │                       │
                                           │ allFundsWithdrawn     │ partialRefund
                                           v                       │
                                       [Closed]  <──────────────────┘

Brazos paralelos:
  - operatorTimeout(): AwaitingOperatorRefund -> [AbandonedByOperator]
  - lateRefundReceived(): AbandonedByOperator -> ClientCreditApplied
```

#### Tabla de transiciones

| Estado | Reserva.Status | Invoice.AnnulmentStatus | Trigger principal |
|---|---|---|---|
| `Drafted` | sin cambio | sin cambio | UI vendedor |
| `AwaitingFiscalConfirmation` | `PendingOperatorRefund` | `Pending` | `confirmWithClient` |
| `AwaitingOperatorRefund` | `PendingOperatorRefund` | `Succeeded` | AFIP CAE (via `ProcessAnnulmentJob` o reconciliation job) |
| `ClientCreditApplied` | `PendingOperatorRefund` | `Succeeded` | `recordRefund` (primer allocation) |
| `Closed` | `Cancelled` | `Succeeded` | `allFundsWithdrawn` |
| `AbandonedByOperator` | `Cancelled` | `Succeeded` | timeout job |
| `Aborted` | sin cambio | sin cambio | abort manual desde Drafted |

**Regla dura**: `BookingCancellation` NUNCA transiciona a estados post-`AwaitingFiscalConfirmation` sin `Invoice.AnnulmentStatus = Succeeded`. **No hay override** (INV-083 `admitsOverride: false`).

### 2.5 Concurrencia N:M (resolución de B1 del review)

**Estrategia**: lock-free con UPDATE atómico + CHECK SQL + retry on `DbUpdateConcurrencyException`.

```csharp
public async Task<Result<OperatorRefundAllocation>> AllocateAsync(
    AllocateRefundRequest req, MutationContext ctx, CancellationToken ct)
{
    const int MaxRetries = 3;
    var delays = new[] { 100, 400, 1600 };

    for (var attempt = 1; attempt <= MaxRetries; attempt++) {
        try {
            using var tx = await _db.Database.BeginTransactionAsync(ct);

            var refund = await _db.OperatorRefundReceived
                .FirstOrDefaultAsync(r => r.Id == req.RefundReceivedId, ct);

            if (refund == null) return Result.NotFound();
            if (refund.AllocatedAmount + req.NetAmount > refund.ReceivedAmount) {
                return Result.Conflict(
                    $"Allocation excede cap. Disponible: {refund.ReceivedAmount - refund.AllocatedAmount}");
            }

            refund.AllocatedAmount += req.NetAmount;

            var allocation = new OperatorRefundAllocation { /* ... */ };
            _db.OperatorRefundAllocations.Add(allocation);
            // Insertar DeductionLines (BC-1)
            foreach (var d in req.Deductions)
                _db.DeductionLines.Add(new DeductionLine { /* ... */ });

            await _db.SaveChangesAsync(ct);  // lanza DbUpdateConcurrencyException si xmin cambió
            await tx.CommitAsync(ct);

            return Result.Ok(allocation);
        }
        catch (DbUpdateConcurrencyException) when (attempt < MaxRetries) {
            var jitter = Random.Shared.Next(0, delays[attempt - 1] / 2);
            await Task.Delay(delays[attempt - 1] + jitter, ct);
            _db.ChangeTracker.Clear();
        }
        catch (PostgresException ex) when (ex.SqlState == "23514") {
            return Result.Conflict("CHECK violado — allocation excede cap.");
        }
    }

    return Result.Conflict("Refund con cambios concurrentes. Reabrir y reintentar.");
}
```

### 2.6 Idempotencia ARCA (resolución de B3)

`Invoice.AnnulmentStatus` = **única SoT fiscal**. `BookingCancellation.Status` proyecta vía llamada directa entre services.

**Hangfire recurrent job** `ArcaAnnulmentReconciliationJob` (patrón existente `Program.cs:679`):

```csharp
RecurringJob.AddOrUpdate<ArcaAnnulmentReconciliationJob>(
    "ArcaAnnulmentReconciliation",
    job => job.RunAsync(CancellationToken.None),
    "*/30 * * * *");  // cron configurable

public class ArcaAnnulmentReconciliationJob {
    public async Task RunAsync(CancellationToken ct) {
        var staleMin = _settings.Get("ArcaStaleAnnulmentThresholdMinutes", 15);
        var threshold = DateTime.UtcNow.AddMinutes(-staleMin);

        var staleInvoices = await _db.Invoices
            .Where(i => i.AnnulmentStatus == AnnulmentStatus.Pending
                     && i.LastArcaAttemptAt < threshold)
            .Take(50)  // batch para no saturar AFIP
            .ToListAsync(ct);

        foreach (var inv in staleInvoices) {
            try {
                var afipState = await _afipService.QueryInvoiceStatusAsync(
                    inv.PuntoDeVenta, inv.NumeroComprobante, ct);

                if (afipState.IsAccepted) {
                    inv.AnnulmentStatus = AnnulmentStatus.Succeeded;
                    inv.CAE = afipState.CAE;
                    inv.LastArcaAttemptAt = DateTime.UtcNow;
                    await _bcService.OnArcaSucceededAsync(inv.Id, ct);  // llamada directa
                    await _audit.LogAsync("ArcaReconciliationRecoveredCae", inv.Id);
                }
                else if (afipState.IsRejected) {
                    inv.AnnulmentStatus = AnnulmentStatus.Failed;
                    inv.LastArcaAttemptAt = DateTime.UtcNow;
                    await _audit.LogAsync("ArcaReconciliationDeclinedCae", inv.Id);
                }
                await _db.SaveChangesAsync(ct);
            }
            catch (AfipServiceException ex) {
                _logger.LogWarning(ex, "Reconciliation skip invoice {Id}", inv.Id);
            }
        }
    }
}
```

**NO se usa**:
- Outbox MassTransit (existente en repo pero es para messaging entre services, no para reconciliación de dominio).
- MediatR (no está instalado en la stack — verificado).
- Outbox de dominio propio (overkill — Hangfire ya tiene reliability).

### 2.7 Multimoneda (Tema 2 accounting)

1. **NC se emite en misma moneda y MISMO TC que factura original** (T0). NO se usa TC del día de la NC. Regla AFIP coherencia fiscal (INV-118).
2. **Diferencia AR$ T0 vs AR$ T2 = Diferencia de cambio** (cuenta "Resultados Financieros y por Tenencia"). RT 17/RT 54 FACPCE distingue realizada vs no realizada.
3. **Modo Monotributista**: registra contable, no impacta cómputo régimen sustitutivo.
4. **Modo RI**: impacta Ganancias (Art. 96 LIG) y a veces IIBB.
5. `FiscalSnapshot` captura los **3 momentos** (T0, T2, T3) con `ExchangeRateSource` enum + `FetchedAt` para auditoría.

### 2.8 Sistema impositivo configurable (transversal)

Toda lógica fiscalmente sensible **lee `AgencySettings.TaxCondition`** y bifurca comportamiento. **No hardcodear "Responsable Inscripto"**.

Matriz cruzada (corrección Gaston 2026-05-13):

| Agencia | Operador | Retenciones permitidas |
|---|---|---|
| Monotributista | Cualquiera | **NINGUNA retención AR** (agencia no es sujeto pasivo). Solo `AdministrativeFee`, `BankingCost`, `CancellationPenalty`, `ForeignTax`, `Other` |
| RI | RI designado | IVA, Ganancias, IIBB (con certificado obligatorio) |
| RI | Monotributista | NINGUNA (operador no es agente retención). Re-categorizar a `AdministrativeFee` / `CancellationPenalty` |
| RI | Extranjero | Solo `ForeignTax` |

**Implicancia**: UI filtra opciones `DeductionKind` según matriz. Backend bloquea con `InvariantViolation` (INV-105, INV-106, INV-115). Cambio `AgencySettings.TaxCondition` requiere `ApprovalRequest.InvariantOverride` + audit + revalidación allocations históricas (INV-117).

### 2.9 Tipificación de deducciones (resolución arca-tax round 2)

Enum `DeductionKind` con 10 valores numerados por bloques. Cada deducción se persiste como `DeductionLine` (BC-1). Reglas obligatorias:

- Retenciones/percepciones AR → requieren certificado (INV-103) + jurisdicción si IIBB (INV-104).
- `ForeignTax` → requiere `ForeignCountryCode` + `Description` (INV-107).
- Costos operativos → requieren comprobante o justificación (INV-108).
- `Other` → comentario obligatorio + flag revisión contable (INV-109).
- Monotributista AGENCIA → bloquea retenciones AR (INV-115).
- Monotributista OPERADOR → bloquea retenciones AR (INV-105).
- Extranjero OPERADOR → solo `ForeignTax` + costos operativos (INV-106).

### 2.10 Reversal post-T3 (resolución B7)

Dos operaciones distintas:

| Operación | `ApprovalRequestType` | Trigger | Efecto |
|---|---|---|---|
| `MisassociationReversal` | `MisassociationReversal = 10` | Cashier se equivocó al asociar | Allocation voided + nueva. `ClientCreditEntry` original voided + nuevo. `AllocatedAmount` recalculado. |
| `ClientRefundReversal` | `ClientRefundReversal = 9` | Cliente devuelve dinero recibido | `ClientCreditWithdrawal.Kind = ReversedToOperator` + `ManualCashMovement` Income + audit reforzado |

**N `OperatorRefundReceived` por BC**: si el operador devuelve en cuotas, cada arrival genera un nuevo `OperatorRefundReceived` con sus allocations al mismo BC. Estado del BC sigue siendo `ClientCreditApplied` tras el primer arrival; los siguientes generan nuevos `ClientCreditEntry` (inmutabilidad regla 5, saldo sin caducidad).

### 2.11 `Reserva.Status` (resolución B6)

Mantener string + agregar `PendingOperatorRefund` + CHECK constraint Postgres incluyendo `"Archived"` legacy. Migración a enum diferida (deuda técnica).

**Inventario verificado**: **11 archivos productivos** con comparaciones contra `EstadoReserva.X` + **4 archivos con otros patrones** (15 total). FC1 audita 5 archivos críticos:
- `AlertService.cs`, `TreasuryService.cs`, `SupplierService.cs`: agregar `PendingOperatorRefund` (alertas/deuda siguen activas).
- `ReservaService.cs`: tabla `AllowedTransitions` + filtros UI (23 ocurrencias).
- `CustomerService.cs`: `PendingOperatorRefund` cuenta como reserva activa del cliente.
- **`ReportService.cs` (18 ocurrencias) — CRÍTICO**: `PendingOperatorRefund` **excluido de revenue queries** (evita reportar ingreso sobre reservas en limbo).
- `PaymentService.cs`, `InvoiceService.cs`: **NO agregar** (no se cobran pagos / no se factura post-cancelación).

### 2.12 `AccountingEntry` (BC-2) — out-of-scope FC1

**Decisión**: out-of-scope FC1. Sistema exporta CSV/Excel mensual con columnas sugeridas (fecha, descripción, debe-cuenta-sugerida, haber-cuenta-sugerida, monto, currency). El contador externo asienta manualmente.

**Razones**:
- MagnaTravel HOY = Monotributista, asientos contables son responsabilidad externa.
- Implementar `AccountingEntry` full en FC1 sería overkill sin plan de cuentas validado por contador real.

**Cuando MagnaTravel migre a RI**: revisitar como nuevo ADR (in-scope futuro). FC1 deja `OperatorRefundAllocation.AccountingEntryRef` nullable + INV-110 condicionado a `AccountingEntryRef IS NOT NULL`.

### 2.13 Coordinación inter-service (B9)

**Llamada directa entre services** en mismo `DbContext` para coherencia transaccional. NO MediatR, NO `INotificationDispatcher` propio, NO MassTransit outbox.

| Operación UI | Aggregates tocados | Servicio orquestador |
|---|---|---|
| T0 confirmar cancelación | BC + Reserva + Invoice | `BookingCancellationService.ConfirmAsync` llama `InvoiceService.AnnulAsync` + actualiza `Reserva.Status`. 1 transacción. |
| T1 AFIP CAE | Invoice + BC | `ProcessAnnulmentJob` o `ArcaAnnulmentReconciliationJob` llama `BookingCancellationService.OnArcaSucceededAsync`. 1 transacción. |
| T2 registrar refund | OperatorRefundReceived + N allocation + N DeductionLine + N ClientCreditEntry + N BC | `OperatorRefundService.RecordRefundAsync` llama `ClientCreditService.CreateEntryAsync`. 1 transacción. |
| T3 cliente retira | ClientCreditWithdrawal + ManualCashMovement + ClientCreditEntry | `ClientCreditService.WithdrawAsync` llama `ManualCashService.CreateExpenseAsync`. 1 transacción. |
| Reasociación | 2× allocation + 2× ClientCreditEntry + 2× BC | `OperatorRefundService.ReassociateAsync` con `ApprovalRequest.MisassociationReversal`. 1 transacción. |

## 3. Consecuencias

### 3.1 Positivas

- **Caja completa**: el bug `AffectsCash` queda mitigado. `ManualCashMovement` separado linkeado a `ClientCreditWithdrawal` aparece en `TreasuryService.GetCashSummaryAsync` (ya suma `ManualCashMovement` Income/Expense en líneas 82-92).
- **Audit fiscal completo**: `FiscalSnapshot` inmutable + 3 momentos de TC + `ExchangeRateSource` para inspección.
- **Partial refunds modelados**: regla 12 policy soportada nativamente (N retiros, saldo sin caducidad).
- **Idempotencia ARCA**: reconciliation job recovery CAEs huérfanos automáticamente.
- **Aggregate boundaries claros**: 3 roots cohesionados, sin saga ni overengineering.
- **Sistema impositivo configurable**: agencia Mono o RI sin redeploy. Primer módulo "consciente" del régimen.
- **25 invariantes Bucket G**: trazabilidad completa de reglas inviolables.

### 3.2 Negativas

- **6 entidades nuevas** + 3 modificaciones + 4 nuevos `ApprovalRequestType`: complejidad de modelo.
- **CHECK constraints sin precedente** en el proyecto: FC1 establece convención (riesgo de error de estilo si no se documenta bien).
- **`Reserva.Status` string libre** con CHECK: deuda técnica explícita, migración a enum diferida.
- **`AccountingEntry` out-of-scope FC1**: cuando MagnaTravel crezca a RI requiere nuevo ADR.
- **Effort 24-34h FC1** (más alto que estimación inicial de 12-16h del architect round 1 — recalibrado por reviewer + BC-1).
- **15 archivos productivos** a auditar para `Reserva.Status` (no 5 como pensaba el architect inicialmente).

## 4. Alternativas consideradas

| Alternativa | Por qué NO |
|---|---|
| **1 aggregate root mono-lítico (BC)** | Rechazado por B2 review: `OperatorRefundReceived` cubre N cancelaciones; `ClientCreditEntry` vive en `Customer`. |
| **Saga / Process Manager** | Overengineering local DbContext. La operación es transaccional cuando se toca todo desde una pantalla. |
| **MediatR para eventos in-process** | NO está en la stack (verificado). No aporta sobre llamada directa entre services para este caso. |
| **MassTransit outbox para ARCA reconciliation** | Latencia excesiva, complejidad innecesaria. Hangfire `RecurringJob` ya tiene reliability + precedente en código. |
| **`SELECT FOR UPDATE` para concurrencia** | Bloquea lectores, no escala. UPDATE atómico + CHECK SQL es lock-free. |
| **Enum `Reserva.Status`** | 15 archivos productivos + 38 tests = refactor masivo con alto riesgo de regresión. Diferido como deuda técnica. |
| **`AccountingEntry` in-scope FC1** | Overkill para MagnaTravel Monotributo. Sin plan de cuentas validado. Revisitar al migrar a RI. |
| **NC por monto neto al cliente** (regla 8 original) | Rechazado por arca-tax round 1 + confirmado con contador real: incorrecto fiscalmente, deja IVA débito huérfano. NC por total. |
| **Refund 1:1 con BC** | Rechazado por B7: operador puede devolver en cuotas, cliente recibe N retiros, modelo N:M obligatorio. |
| **Override `InvoiceAnnulmentSkip`** | Rechazado por arca-tax round 1: NC sin CAE no existe fiscalmente. INV-083 sin override. |

## 5. Migration plan / rollback

### 5.1 Sub-fases FC1..FC4 (incrementales)

| Fase | Descripción | Effort |
|---|---|---|
| **FC1** | Aggregates + state machine + concurrencia + DeductionLine (BC-1) + 5 archivos `Reserva.Status` críticos + tests | **24-34h** |
| **FC2** | NC emission T0 + idempotencia ARCA + reconciliation Hangfire + fix `AfipService.cs:1050` | 14-18h |
| **FC3** | Refund operador + allocations + commission cap-at-zero hook (depende `CommissionLedger`) | 10-14h |
| **FC4** | Cliente credit + N retiros + Ley 25.345 + ClientRefundReversal | 14-20h |
| **FC5 (opcional)** | `FiscalSnapshot` particionado anual + observabilidad (R1/R3 review ADR-001) | 8-12h |
| **Total realista** | | **62-86h** sin bloqueantes; ~75-100h con tickets contador |

### 5.2 Backfill

- `BookingCancellation.IsLegacyPreCancellationModel = NULL` para registros previos al deploy. Setting `MigrationCutoffUtc = fecha deploy FC1`.
- NCs históricas: opción híbrida — `IsLegacyPreCancellationModel = NULL` + reporte separado para reconciliación contador (INV-125).
- `OperatorRefundReceived.AllocatedAmount` para refunds históricos: cero (no aplica modelo nuevo).
- `Reserva.Status` legacy `"Archived"` incluido explícito en CHECK.

### 5.3 Rollback

- Feature flag `EnableNewCancellationFlow` (default false hasta validación).
- Migración EF reverse por sub-fase.
- Estado intermedio: si se rollbackea FC1, `BookingCancellation` queda como tabla huérfana sin escritura.

## 6. Testing strategy

### 6.1 Tests obligatorios FC1

**Concurrencia (B1) — 4 tests**:
1. `Test_TwoTasksAllocateWithinCap_BothSucceed`
2. `Test_TwoTasksAllocateExceedingCap_OneWinsOneRejects409`
3. `Test_VoidedAllocationFreesCap_AllowsReallocation`
4. `Test_ConcurrentVoidAndAllocate_RespectsCap`

**State machine — ~12 tests** (transiciones válidas/inválidas, override prohibido, override admitido donde aplica).

**Sistema impositivo — matriz cruzada**:
- Agencia Mono + Operador RI → `DeductionLine` con `IvaWithholding` rechazada (INV-115).
- Agencia RI + Operador Mono → `DeductionLine` con `IvaWithholding` rechazada (INV-105).
- Agencia RI + Operador Extranjero → solo `ForeignTax` permitido (INV-106).
- Agencia RI + Operador RI + `IvaWithholding` sin certificado → rechazada (INV-103).

**Multimoneda**:
- NC en USD con TC de T0 (no del día NC).
- Diferencia de cambio T0→T2 genera asiento sugerido.

**Integration E2E**: T0→T1→T2→T3 happy path + 4 variantes (AFIP rejected, AFIP timeout, MisassociationReversal, AbandonedByOperator).

### 6.2 Tests sub-fases siguientes

- FC2: ARCA reconciliation (happy/rejected/timeout/unknown).
- FC3: allocation parcial / excede / reversal.
- FC4: N retiros + balance no negativo + Ley 25.345 + ClientRefundReversal.

### 6.3 Performance baseline

R1 review ADR-001: medir p95 pre-FC2 para 5 endpoints más calientes, target ≤15% incremento post-FC.

### 6.4 Migration tests

R4 review ADR-001: dump producción anonimizado, validar migration EF + bootstrapper SQL.

## 7. Operational risks

| Riesgo | Mitigación |
|---|---|
| **Deuda contable preexistente (NCs históricas)** | INV-125 reporte separado + reconciliación off-system contador antes de deploy. |
| **Concurrencia N:M over-allocation** | CHECK SQL + UPDATE atómico + retry. 4 tests obligatorios. |
| **Idempotencia ARCA: CAE huérfano** | Reconciliation job nocturno + INV-082 terminalidad + `Invoice.AnnulmentStatus` SoT única. |
| **Config jerárquico** | Pre-requisito: cerrar B4 ADR-001 review (resolver Agency/Supplier/ProductType/Booking sin bugs). |
| **`Reserva.Status` string libre** | CHECK constraint + auditoría 15 archivos productivos en FC1. |
| **`FiscalSnapshot` crecimiento** | Particionado anual diferido a FC5 + R1 review. |
| **Reversal post-decision cliente con efectivo retirado** | `ClientRefundReversal=9` con approval + audit reforzado. Si cliente no devuelve, AR cobrable (gestión comercial manual). |
| **Operador NUNCA responde (timeout)** | Estado `AbandonedByOperator` + setting `OperatorRefundTimeoutDays` (default 60, sujeto a contador). Recovery `lateRefundReceived`. |
| **Provisión `OperatorRefundExpected` al cierre fiscal** (BF4 vivo) | Política a definir con contador. Buckets antigüedad sugeridos: 0-60/61-120/121-180/>180. |
| **Commission cap-at-zero** (BF2 vivo) | Depende `CommissionLedger` Fase D roadmap. Hook opcional FC3. |
| **TreasuryService bug INV-CONT-09 / NCs legacy** (BF3 mitigado) | Backfill explícito declarado + INV-125 reporte. |

## 8. Security / audit risks

- **Threshold alert** (regla 6 policy): Admin recibe notificación si refund físico > setting. INV-095.
- **Daily egress report**: Hangfire job genera reporte diario de `ClientCreditWithdrawal` con metadata para Admin.
- **Ley 25.345** (INV-094): rechazar `PhysicalCash` si monto > umbral, solo `Transfer`.
- **AuditLog reforzado**: nuevos `Action`:
  - `ClientCreditPhysicalRefundExecuted`
  - `OperatorRefundReceivedRegistered`
  - `OperatorRefundReallocated`
  - `BookingCancellationConfirmed`
  - `ClientRefundReversalApproved`
  - `MisassociationReversalApproved`
  - `ArcaReconciliationRecoveredCae`
  - `ArcaReconciliationDeclinedCae`
- **Override `InvariantOverride`** (ADR-001) con razón obligatoria (min 20 chars) + `WasForced=true` + `ApprovalRequest` consumida.
- **`FiscalSnapshot` immutability** (INV-093): cualquier modificación post-T0 requiere override + AuditLog especial.

## 9. Open questions

### 9.1 10 preguntas obligatorias para contador real de MagnaTravel

**No bloquean redactar ADR-002, sí bloquean mergear FC3/FC4 a producción**:

1. **Plan de cuentas oficial** (Tango/Bejerman/Holistor/manual) + tabla mapeo `DeductionKind → código cuenta`.
2. **Política TC** (BCRA A3500 vs BNA Mayorista vs Minorista vs AFIP; día anterior o mismo día).
3. **Diferencias de cambio**: ¿al cierre mensual no realizada o solo al cobro/pago realizada? (RT 17 / RT 54 FACPCE).
4. **% provisión `OperatorRefundExpected`** por bucket antigüedad + criterio baja a incobrable.
5. **Migración futura Mono→RI**: tratamiento puente saldos USD + NCs legacy.
6. **Operadores extranjeros**: convenios doble imposición + países habituales.
7. **`CancellationPenalty`**: ¿factura/recibo formal del operador? Riesgo deducibilidad Ganancias RI futuro.
8. **IIBB Mono**: jurisdicciones donde MagnaTravel opera que incluyen Mono como sujeto retención.
9. **NCs legacy históricas**: reconciliación manual o deuda fiscal conocida.
10. **Aplicabilidad RT 54 FACPCE** (jurisdicción + fecha).

### 9.2 Decisiones diferidas a Gaston

- Daily egress report formato (PDF/Excel/dashboard).
- `OperatorRefundTimeoutDays` default (sugerido 60, contador confirma).
- `IsLegacyPreCancellationModel` criterio: ¿setting fecha deploy o estado fiscal? **Decisión: setting `MigrationCutoffUtc`**.

### 9.3 Pre-requisitos técnicos

- `backend-dotnet-senior` verifica `UseXminAsConcurrencyToken()` en EF Core 8 + Npgsql actuales (FC1.1).
- Convención CHECK constraint naming `chk_<tabla>_<concepto>` + interceptor `PostgresException.SqlState = '23514'` → `BusinessInvariantViolationException` → HTTP 409.
- Verificar que `BusinessInvariantViolationException` existe en el proyecto (depende de ADR-001 acceptance).

## 10. Invariantes Bucket G (INV-081..125)

Catalogadas en `.claude/agent-memory/software-architect/project_cancellation_redesign_2026_05_13.md` §6. Total: **25 invariantes** distribuidas:

- **INV-081..099** (19 invariantes): aggregate roots, máquina de estados, concurrencia, ARCA, modelo de datos, reglas de policy.
- **INV-100**: `OnePerReservaInvoicePolicy=true` precondición (B10).
- **INV-101..114** (14 invariantes): tipificación deducciones por condición fiscal (arca-tax round 2).
- **INV-115..117** (3 invariantes): matriz cruzada `Agency.TaxCondition × Operator.TaxCondition` (corrección Gaston).
- **INV-118..125** (8 invariantes): multimoneda, retenciones tardías, NCs legacy (accounting round 2).

Cada invariante con `Priority`, `AdmitsOverride`, `TargetEntity`, `Source rule policy`. Implementación via patrón `IBusinessInvariant<T>` del ADR-001.

## 11. Plan de fases FC1..FC5 (resumen final)

```
F3.0 (resolver B4 ADR-001) → FC0/F3.1 (infra invariantes) → FC1 (24-34h)
                                                              ↓
                                                            FC2 (14-18h)
                                                              ↓
                                                            FC3 (10-14h)
                                                              ↓
                                                            FC4 (14-20h)
                                                              ↓
                                                            FC5 opcional (8-12h)
                                                              ↓
                                                          Fase IMP transversal (12-20h)
```

**Convención naming** (`memory/reference_naming_phases.md`): F3.x = framework invariantes ADR-001. FC.x = módulo cancelación ADR-002. IMP = transversal Mono/RI.

## 12. Auto-crítica explícita (transparencia)

1. **¿Verifiqué todo en el repo?** Sí — entidades, services, migración outbox MassTransit, `AgencySettings.TaxCondition` existente, `AfipSettings.TaxCondition` existente, `EstadoReserva` static class con `"Archived"` legacy, ausencia de MediatR, precedente `RecurringJob.AddOrUpdate` en `Program.cs:679`.
2. **¿Asumí algo sin decirlo?** `UseXminAsConcurrencyToken()` funcional en EF Core 8 + Npgsql — pre-requisito FC1.1 a verificar con `backend-dotnet-senior`.
3. **¿Coupling oculto?** `ArcaAnnulmentReconciliationJob` proyecta BC via llamada directa al service. Riesgo: si falla, BC desincronizado. Mitigación: idempotencia + reconciliation rebuild state from Invoice.
4. **¿Tests cubiertos?** Concurrencia, ARCA, state machine, retiros, reversal, bancarización, matriz Mono/RI, multimoneda. **Falta**: tests de fallover del reconciliation job bajo respuestas raras AFIP.
5. **¿Edge case que falta?** Múltiples facturas por reserva: resuelto B10 (`OnePerReservaInvoicePolicy=true`). Si se levanta restricción en futuro, ADR-002 debe revisarse.
6. **¿Riesgo de datos/rollback?** `AllocatedAmount` denormalizado puede desincronizarse del `SUM(allocations.NetAmount)` por bug. Mitigación: test invariante periódico en `OperationalFinanceMonitorService`. Rollback: migración EF reverse + feature flag.
7. **¿Overcomplicado?** 3 aggregates es mínimo. No saga, no MediatR, no outbox dominio. Llamada directa preserva simplicidad.
8. **¿Underdesigned?** `AccountingEntry` diferido out-of-scope FC1 (decisión explícita BC-2). `MisassociationReversal` vs `ClientRefundReversal` distinguidos.
9. **¿Mantenible por otros?** Sí — convención repo (services en Infrastructure, entidades en Domain, FluentValidation API). ADR + memoria agent + invariantes catalogadas.
10. **¿Reviewer podría rechazar?** Puntos débiles:
    - `UseXminAsConcurrencyToken()` asumido funcional → verificación FC1.1.
    - 4 nuevos `ApprovalRequestType` (7-10) → coordinar con ADR-001 evolution.
    - Convención CHECK SQL sin precedente → establecer style guide FC1.
    - Open questions contador (10) → no bloquean ADR pero sí FC3/FC4 prod.

## 13. Trazabilidad

- **Rediseño completo del architect**: `.claude/agent-memory/software-architect/project_cancellation_redesign_2026_05_13.md` (~700 líneas)
- **Review architect round 1**: `.claude/plans/curried-soaring-lagoon-agent-a5cda19f0a5ba7bd8.md`
- **Validación accounting round 1**: `.claude/agent-memory/accounting-expert-argentina/project_adr_002_validation_2026_05_13.md`
- **Validación accounting round 2**: `.claude/agent-memory/accounting-expert-argentina/project_adr_002_validation_round2_2026_05_13.md`
- **Validación arca-tax round 1+2**: incluida en chat de sesión 2026-05-13
- **Policy MagnaTravel (17 reglas)**: `.claude/agents/travel-agency-domain-expert.md` §"Project-specific policies (MagnaTravel)"
- **Memoria transversal sistema impositivo**: `memory/project_sistema_impositivo_por_condicion_fiscal.md`
- **Roadmap B1.15**: `memory/project_b115_roadmap.md` (Fase IMP agregada)
