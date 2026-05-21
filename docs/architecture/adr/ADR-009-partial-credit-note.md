# ADR-009 — Nota de Credito parcial fiscal para cancelaciones Hotel (FC1.3 Fase 1)

- **Status**: Proposed (Round 2 - corrige Round 1 con 13 hallazgos del reviewer + 6 decisiones Gaston GR-001..GR-006).
- **Date**: 2026-05-21 (round 1) / 2026-05-21 (round 2).
- **Author(s)**: software-architect agent.
- **Supersedes**:
  - Round 1 de este mismo ADR (sintaxis SQL Server por error de brief de la sesion principal; persistencia de `FiscalLiquidation` Fase 1; auto-procesamiento de `TotalPlusNewInvoice`; hipotesis CommissionOnly + penalty no reduce; uso de `MutationContext`).
  - §2.2 punto 1 de [ADR-002](ADR-002-cancellation-refund.md) ("NC siempre por total facturado"). FC1.3 acepta NC parcial cuando el criterio fiscal lo amerita.
- **Related**:
  - [ADR-002 Cancelacion / Refund](ADR-002-cancellation-refund.md).
  - [ADR-001 Domain Invariants](ADR-001-domain-invariants.md) (**status: Changes Required, NO mergeado** — no asumir patrones de ese ADR).
  - Plan funcional FC1.3 ([plan-tactico-fc1-3.md](../plan-tactico-fc1-3.md) §1..§16) — autoria `travel-agency-accountant-argentina` 2026-05-21.
  - Plan tactico FC1.2 v3 ([plan-tactico-fc1-2.md](../plan-tactico-fc1-2.md)).
  - Doc criterio contador [2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md](../../explicaciones/2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md).

---

## 1. Contexto

### 1.1 Que cambia el contador respecto de FC1.2

ADR-002 (vigente, FC1.2 mergeado tecnico al 100%) cerro la decision "NC siempre por total facturado" como verdad fiscal del modulo. El **2026-05-21** el contador de MagnaTravel actualizo el criterio:

> "La NC refleja la parte del comprobante que pierde causa fiscal o comercial, NO el monto devuelto."

Entrego una **matriz de 8 casos** (cubre Factura B/C/A, modo reseller vs intermediario, con/sin penalidad, con/sin items no reintegrables, factura confusa) + **2 escenarios concretos** + **7 excepciones**. El nuevo agente `travel-agency-accountant-argentina` produjo el plan funcional cubriendo el QUE fiscal/contable/negocio. Este ADR resuelve el COMO tecnico.

### 1.2 Ejemplo pelotudo (kiosco)

Imaginate el kiosco. Cliente paga `$1000` por 4 milanesas + `$50` por envoltorio regalo (no devuelve nunca). Se arrepiente y se lleva 3. La caja vieja (FC1.2) anulaba TODA la cuenta y empezaba de nuevo: poco prolijo, pero funcionaba. La caja nueva (FC1.3) **arranca una NC parcial por las dos cosas**: lo que pierde causa fiscal (1 milanesa = `$250`) + el envoltorio regalo es item no reintegrable (no entra en la NC porque la agencia ya lo facturo como ingreso). La factura sigue viva por `$800` (3 milanesas) + el envoltorio `$50` queda como ingreso reconocido de la agencia.

El sistema viejo escribia "ANULADO" en la factura entera y emitia una nueva por `$800` (lo de FC1.2). El sistema nuevo emite **una NC por `$250`** y la factura original queda intacta. Cuaderno limpio.

### 1.3 Hechos verificados en el repo (relevantes para el diseno)

Confirmados leyendo entidades, services y migraciones reales:

- **Stack confirmado: PostgreSQL** (Npgsql 8.x). Evidencia: `Program.cs` con `UseNpgsql`, `AppDbContext.UseXminAsConcurrencyToken()` (linea 1180), `PostgresIntegrationFixture` con TestContainers (`postgres:16`), migracion `FC1_AddCancellationModule.cs` con `using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata` + identificadores con comillas dobles + tipo `xid` (`xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)`). El interceptor `BusinessInvariantInterceptor` mapea `PostgresException.SqlState='23514'` (CHECK violation) a `BusinessInvariantViolationException`.
- `Invoice.OriginalInvoiceId` self-reference + `<CbtesAsoc>` ya emitidos por `AfipService.cs:827-838`. El plumbing fiscal de vinculo NC<->factura ya esta. Lo que falta es persistir el monto fiscal calculado, no la NC en si.
- `InvoiceItem` no tiene `IsRefundable` ni `ItemCategory` ni FK al servicio origen. Agregar Fase 1.
- `BookingCancellationStatus` tiene 8 estados (0..7) hoy. **Insertamos 4 estados nuevos con valores 8..11** para no chocar con valores existentes ni cambiar serializacion previa.
- `ApprovalRequest` tiene `Metadata string?` JSON arbitrario y `ResolverNotes string?` (max 1000 chars). **Pero NO tiene concurrency token hoy** — riesgo de race en edicion admin. Se agrega como **migracion separada pre-requisito** (ver §5.1 M0).
- `ApprovalRequestType` enum llega hasta 10. Agregamos `PartialCreditNoteApproval = 11`. `InvariantOverride = 7` ya existe y es el patron real para overrides (NO `MutationContext` que pertenece a ADR-001 rechazado).
- `ApprovalRequestsController` acepta cualquier `RequestType` + filtros por user/pending. Reutilizable, no se crea bandeja paralela (cierre G2).
- `OperationalFinanceSettings` tiene espacio claro para los nuevos thresholds + template. `EnableNewCancellationFlow` linea 74 (default `false`) es el flag de FC1.2.
- `FiscalSnapshot` es VO owned en `BookingCancellation`, persistido como columnas prefijadas `FiscalSnapshot_*` (verificado en migracion FC1).
- `BookingCancellationService.cs:980-987` tiene helper `EnsureFeatureFlagOnAsync` que tira `InvalidOperationException` cuando `EnableNewCancellationFlow=false` (kill switch real, no comportamiento legacy).
- `BookingCancellationService.cs:261-281` es el patron real de override: busqueda de `ApprovalRequest` con `RequestType=InvariantOverride=7`, `EntityType="BookingCancellation"`, `EntityId=bc.Id`, `Status=Approved`, `RequestedByUserId == userId`, `ExpiresAt > UtcNow`.
- `AuditLog.Changes` es `string?` con shape JSON `{"Field": {"Old": "Val1", "New": "Val2"}}` (verificado linea 28).
- `AssignmentServiceType.Hotel = "Hotel"` (constante string en `PassengerServiceAssignment.cs:11`). `ServicioReserva.ProductType` es `string?` con default `ServiceTypes.Flight = "Aereo"`. Los valores `Hotel`, `Aereo`, etc. estan en `ServiceTypes` (`ServicioReserva.cs:5-14`). Para validacion FC1.3 usamos la **constante `ServiceTypes.Hotel`**, NO string literal.

### 1.4 Que NO entra en Fase 1

- **Emision real de NC parcial al ARCA**. Hoy `AfipService.cs` emite NC asumiendo monto total. Calcular `<ImpTotal>`/`<ImpNeto>`/`<ImpIVA>` con prorrateo proporcional al neto acreditado entra en **Fase 2**. La Fase 1 calcula `FiscalLiquidation` **en memoria solamente** (GR-004), bloquea casos sensibles, y NO persiste la liquidacion entera. Solo se persisten `CreditNoteKind`, `ReviewRequiredReason` y `LiquidationComputedAt`.
- **Persistencia entera de `FiscalLiquidation`** (GR-004). Diferida a Fase 2 cuando AfipService emita NC parcial real y necesite leer los montos calculados.
- **Auto-procesamiento de `TotalPlusNewInvoice`** (GR-001). Si el clasificador devuelve `TotalPlusNewInvoice`, el sistema **rechaza el Confirm con error explicito** y no persiste nada. Casos 4 y 7 quedan para flujo legacy o FC1.3 Fase 2.
- **Calculo automatico en modo `CommissionOnly`** (GR-003). Si el clasificador detecta `Supplier.InvoicingMode = CommissionOnly`, pasa directo a `ManualReviewPending` con flag `InvoicingModeCommissionOnly`. Pendiente respuesta F2 round 3 contador para implementar formula.
- **Auto-procesamiento de modo `TotalToCustomer` con penalty operador > 0** (GR-006). Contradiccion interna en plan funcional (§5.5 vs §12.3): contador debe responder F4 round 3 si penalty resta o no del monto fiscal. Mientras tanto, el caso 3 va a `ManualReviewPending` con flag `PenaltyResetUncertainInResellerMode`.
- **UI modal** del admin para revisar/aprobar liquidaciones (Fase 3).
- **Plumbing AfipService** para NC parcial (Fase 2).
- **Servicios distintos a Hotel**. Vuelo/Paquete/Traslado/Asistencia siguen flujo FC1.2 (NC por total). Si la reserva mezcla, se rechaza FC1.3 con mensaje claro.

### 1.5 Decisiones cerradas que NO se pueden cambiar (cierres 2026-05-19/21 + GR-001..GR-006 round 2)

| ID | Decision | Implicancia tecnica |
|---|---|---|
| G1 | Preseleccion auto `IsRefundable=false` para items `Insurance`/`AdministrativeFee`/`OperatorAdvance`. Vendedor puede destildar con confirmacion. | Default logic en creacion de `InvoiceItem`. UI cubre confirmacion (Fase 3). |
| G2 | Reutilizar `ApprovalRequestsController` con nuevo `PartialCreditNoteApproval=11`. No hay bandeja separada. | Solo sumar enum + handler. |
| G3 | Admin edita liquidacion en `ManualReviewPending` con `editLiquidation()` + audit + comentario obligatorio distinto al de aprobacion. | Self-loop `ManualReviewPending`->`ManualReviewPending` con audit. Metadata del approval refleja el cambio. |
| G4 | NO ND complementaria para cliente RI. Factura A original + NC parcial alcanzan. | Sin cambios en `Invoice` para ND. ADR-002 §2.2 punto 2 vigente. |
| G5 | Sin rol nuevo Fase 1. Admin actual aprueba >$2M con comentario min 100 chars + `AccountingReviewRequired=true` en metadata. | Validacion min chars segun threshold. Sin permiso nuevo. |
| G6 | Comision vendedor sobre `FinalNetInvoiced`. | Service de comision usa este campo. Fuera de scope FC1.3 Fase 1 (solo se documenta). |
| **GR-001** | **Rechazar Confirm con `InvalidOperationException`** si clasificador devuelve `TotalPlusNewInvoice` (casos 4 y 7). | Eliminar path "BC en ManualReviewApproved esperando Fase 2" para `TotalPlusNewInvoice`. Mensaje: "Caso fiscal requiere FC1.3 Fase 2 - use flujo legacy". |
| **GR-002** | **FC1.3 se mergea DESPUES de FC1.2 ON en prod** (post signoff OPS-FISCAL-001). | ADR asume `EnableNewCancellationFlow=ON` antes de prender `EnablePartialCreditNotes`. Validacion startup rechaza FC1.3 ON con FC1.2 OFF. |
| **GR-003** | **Diferir `CommissionOnly` a manual review obligatorio** Fase 1. | Clasificador NO calcula, dispara flag `InvoicingModeCommissionOnly` y va a `ManualReviewPending`. |
| **GR-004** | **NO persistir `FiscalLiquidation` entera Fase 1**. | Migracion FC1.3.0 NO crea columnas `FiscalLiquidation_*`. Solo se persisten `CreditNoteKind`, `ReviewRequiredReason`, `LiquidationComputedAt`, `LiquidationComputedByUserId/Name`. La liquidacion completa se calcula in-memory + se serializa al `ApprovalRequest.Metadata` JSON (para audit + edicion admin G3). |
| **GR-005** | **Setting `Allow4EyesBypassWhenSingleAdmin`** default `false`. | Cuando `true` AND sistema tiene 1 solo admin AND vendedor=admin, permite self-approval con comentario reforzado 100+ chars + flag audit `SelfApprovedDueToSingleAdmin=true`. |
| **GR-006** | **Diferir caso 3 con penalty operador en modo `TotalToCustomer`** hasta respuesta contador F4 round 3. | Flag `PenaltyResetUncertainInResellerMode` activado. No auto-procesa. |
| Mat | Matriz 8 casos + Escenarios A/B del contador. | Clasificador (matriz 8) implementado en service aislado testeable. |
| Cri | Criterio NC parcial = pierde causa fiscal, NO monto devuelto. | INV-FC1.3-005 + dos campos separados (`FiscalAmountToCredit` vs `AmountToRefundCustomer`) en el DTO transitorio (NO persistidos Fase 1). |

---

## 2. Decision

Implementar FC1.3 Fase 1 como **extension aditiva de FC1.2** (no rewrite), con alcance **deliberadamente reducido** por GR-001/GR-003/GR-004/GR-006:

1. **Un service nuevo** `IFiscalLiquidationCalculator` (puro, sin DbContext directo) que implementa la matriz 8 casos como **clasificador aislado y testeable** y devuelve un DTO transitorio (`FiscalLiquidationDto`).
2. **Persistencia minima Fase 1** en `BookingCancellation`: solo `CreditNoteKind`, `ReviewRequiredReason`, `LiquidationComputedAt`, `LiquidationComputedByUserId/Name`, `PartialCreditNoteApprovalRequestId` (FK), `ManualReviewer*` fields. **El detalle de la liquidacion** (montos, items, penalty, regla aplicada) se serializa al `ApprovalRequest.Metadata` JSON.
3. **4 estados nuevos** en `BookingCancellationStatus` con valores 8..11.
4. **Reutilizacion del `ApprovalRequestsController` existente** con tipo nuevo `PartialCreditNoteApproval=11`.
5. **3 modificaciones** a entidades existentes: `Supplier` (InvoicingMode + PenaltyPolicyJson), `InvoiceItem` (IsRefundable + ItemCategory + SourceServicioReservaId), `FiscalSnapshot` (InvoicingModeAtEvent + OriginalInvoiceTypeAtEvent).
6. **Settings nuevos** en `OperationalFinanceSettings` para thresholds, template, GR-005 y heuristicas.
7. **Feature flag nuevo** `EnablePartialCreditNotes` (independiente de `EnableNewCancellationFlow`), default `false` en prod, separado para poder mergear sin romper FC1.2. **Pre-condicion startup (GR-002): si `EnablePartialCreditNotes=true && EnableNewCancellationFlow=false` -> rechazar arranque con error claro.**
8. **5 migraciones agrupadas por aggregate + 1 pre-requisito M0** (RH-006/RH-007).
9. **Mantenimiento de FC1.2** intacto: si flag FC1.3 off, todo el modulo se comporta exactamente como hoy. Si flag on pero la reserva no entra al clasificador (caso 2 trivial sin disparadores), tambien se comporta como FC1.2 (NC total).

### 2.1 Glosario adicional (extiende ADR-002 §2.1)

| Termino | Definicion |
|---|---|
| **NC parcial** | NC vinculada a factura original via `OriginalInvoiceId` por un monto **menor** que el total facturado. La factura original sigue viva por el saldo. |
| **NC total** | NC vinculada a factura original por el 100% del total facturado (es lo que hace FC1.2 hoy). |
| **NC total + nueva factura** | Anular factura original (NC por total) + emitir factura nueva por el remanente conceptual. Casos 4 y 7 de la matriz. **Fase 1 RECHAZA estos casos en Confirm (GR-001)**. Fase 2 implementa. |
| **`FiscalLiquidationDto`** | DTO transitorio devuelto por el calculator con monto facturado, penalidad, items no reintegrables, monto fiscal acreditado, monto a devolver, neto facturado final, regla aplicada (1..8), `CreditNoteKind`, `ReviewRequiredReason`. **NO se persiste Fase 1** (GR-004). Se serializa al `ApprovalRequest.Metadata` cuando hay review manual. |
| **`CreditNoteKind`** | Enum: `PartialOnOriginal` (casos 1, 2, 3, 5, 6 — una sola NC). `TotalPlusNewInvoice` (casos 4, 7 — Fase 1 rechaza). |
| **Clasificador matriz 8** | Logica que mira `OriginatingInvoice.TipoComprobante`, `Supplier.InvoicingMode`, items no reintegrables, retenciones, modo cambiado, etc., y decide caso 1..8 + `ReviewRequiredReason`. |

### 2.2 Decisiones cerradas no negociables (Fase 1)

1. **Stack es PostgreSQL** (Npgsql 8.x). CHECK constraints en sintaxis Postgres con identificadores entre comillas dobles. Concurrency token via `xmin` shadow column + `UseXminAsConcurrencyToken()`. Interceptor mapea `PostgresException.SqlState='23514'` a `BusinessInvariantViolationException`.
2. **No se persiste `FiscalLiquidation` entera Fase 1** (GR-004). Owned VO eliminado del modelo Fase 1.
3. **`Supplier.PenaltyPolicyJson` es columna `jsonb`** (RH-014), no `text`. Permite validacion sintactica automatica + future `jsonb_typeof` checks.
4. **Clasificador en service separado** `IFiscalLiquidationCalculator` (cierre §17.1 item 3). Argumentado en §2.6.
5. **Approval via `ApprovalRequest.Metadata` JSON** + FK `BC.PartialCreditNoteApprovalRequestId`. Argumentado en §2.7.
6. **Feature flag nuevo `EnablePartialCreditNotes`** + pre-condicion `EnableNewCancellationFlow=true` validada al startup (GR-002).
7. **Auto-emision por umbral $500k**: default editable en `OperationalFinanceSettings`. Si el contador propone otro valor en respuesta a F1 round 3, se actualiza el setting sin tocar codigo.
8. **`CreditNoteKind = TotalPlusNewInvoice` provoca rechazo en Confirm** (GR-001). No queda BC colgado.
9. **Heuristicas factura confusa DESACTIVADAS por default** (RH-008). `GenericDescriptionPatterns = ""` (vacio) + `Fc13DeployDate = null`. Solo se activan si el contador lo pide explicitamente en round 3.
10. **`ApprovalRequest` necesita concurrency token antes de FC1.3** (RH-006): migracion pre-requisito M0 agrega `xmin` a la tabla `ApprovalRequests`.

### 2.3 Modelo de datos (Fase 1 - REDUCIDO por GR-004)

#### 2.3.1 Entidades NUEVAS

```csharp
// src/TravelApi.Application/DTOs/Cancellation/FiscalLiquidationDto.cs
//
// DTO transitorio que el calculator devuelve. NO se persiste Fase 1 (GR-004).
// Se serializa al ApprovalRequest.Metadata JSON cuando hay manual review.
public record FiscalLiquidationDto(
    decimal OriginalInvoiceAmount,
    decimal CancellationAmount,
    decimal OperatorPenaltyAmount,
    decimal NonRefundableItemsAmount,
    decimal FiscalAmountToCredit,
    decimal AmountToRefundCustomer,
    decimal FinalNetInvoiced,
    PartialCreditNoteCase ComputedCase,
    CreditNoteKind CreditNoteKind,
    ReviewRequiredReason ReviewRequiredReason,
    string Currency,
    string ClassificationExplanation);

// src/TravelApi.Domain/Entities/PartialCreditNoteCase.cs
public enum PartialCreditNoteCase
{
    Unset = 0,
    Case1_PartialRefundNoPenalty = 1,
    Case2_FullCancellationNoRetention = 2,
    Case3_FullCancellationWithPenalty = 3,
    Case4_OriginalInvoiceUnclear = 4,         // Fase 1: rechaza Confirm (GR-001)
    Case5_CommissionOnlyPartial = 5,          // Fase 1: manual review (GR-003)
    Case6_CommissionOnlyFull = 6,             // Fase 1: manual review (GR-003)
    Case7_RetentionChangesNature = 7,         // Fase 1: rechaza Confirm (GR-001)
    Case8_FacturaA = 8,
}

// src/TravelApi.Domain/Entities/CreditNoteKind.cs
public enum CreditNoteKind
{
    Unset = 0,
    PartialOnOriginal = 1,         // casos 1, 2, 3, 5, 6 — una sola NC vinculada
    TotalPlusNewInvoice = 2,       // casos 4, 7 — Fase 1 RECHAZA Confirm (GR-001)
}

// src/TravelApi.Domain/Entities/ReviewRequiredReason.cs
// Bitflag: un BC puede activar multiples motivos.
[Flags]
public enum ReviewRequiredReason
{
    None = 0,
    CustomerIsRiOrFacturaA = 1 << 0,                  // caso 8 obligatorio
    HasNonRefundableItems = 1 << 1,
    AmountAboveAdminThreshold = 1 << 2,
    AmountAboveAccountingThreshold = 1 << 3,          // > PartialNcAccountingReviewThreshold (G5)
    RetentionChangesNature = 1 << 4,                  // caso 7 — RECHAZADO Fase 1 (GR-001)
    OriginalInvoiceUnclear = 1 << 5,                  // caso 4 — RECHAZADO Fase 1 (GR-001)
    MultiCurrency = 1 << 6,                           // futuro Fase 2
    LegacyInvoice = 1 << 7,                           // OFF por default (RH-008)
    InvoicingModeCommissionOnly = 1 << 8,             // NUEVO RH-015/GR-003: dispara manual review
    PenaltyResetUncertainInResellerMode = 1 << 9,     // NUEVO GR-006: dispara manual review
    Other = 1 << 10,                                  // catch-all (NC en cadena, etc.)
}

// src/TravelApi.Domain/Entities/SupplierInvoicingMode.cs
public enum SupplierInvoicingMode
{
    TotalToCustomer = 0,  // reseller: factura al cliente el total del servicio. Default conservador.
    CommissionOnly = 1,   // intermediario: factura solo la comision. Fase 1 va a manual review (GR-003).
}

// src/TravelApi.Domain/Entities/InvoiceItemCategory.cs
public enum InvoiceItemCategory
{
    Service = 0,            // default — concepto principal del servicio
    AdministrativeFee = 1,  // cargo gestion agencia. G1: IsRefundable=false por default
    Insurance = 2,          // seguro cancelacion. G1: IsRefundable=false por default
    OperatorAdvance = 3,    // anticipo no reembolsable. G1: IsRefundable=false por default
    Penalty = 4,            // penalidad operador facturada
    Other = 99,
}
```

#### 2.3.2 Modificaciones a entidades existentes

```csharp
// src/TravelApi.Domain/Entities/Supplier.cs — 2 props nuevas
public class Supplier
{
    // ... props existentes ...

    /// <summary>
    /// FC1.3 (ADR-009, 2026-05-21): modelo de facturacion al cliente para este operador.
    /// TotalToCustomer = reseller (factura el total del servicio).
    /// CommissionOnly = intermediario (factura solo la comision). FASE 1: manual review obligatorio (GR-003).
    /// Snapshot al momento de emitir factura queda en FiscalSnapshot.InvoicingModeAtEvent.
    /// Editable por admin. Default TotalToCustomer (conservador, comportamiento legacy).
    /// </summary>
    public SupplierInvoicingMode InvoicingMode { get; set; } = SupplierInvoicingMode.TotalToCustomer;

    /// <summary>
    /// FC1.3 (ADR-009, 2026-05-21): tabla de penalidades por antelacion en JSON.
    /// Tipo: jsonb (RH-014). CHECK Postgres valida que sea objeto top-level.
    /// Schema: { "tiers": [{"minDaysBefore": int, "penaltyPercent": decimal}, ...], "currency": "USD"|"ARS" }
    /// Tiers ordenados DESC por minDaysBefore. Vendedor puede override manual al confirmar (D2 2026-05-21).
    /// Validacion via FluentValidation en SupplierService antes de persistir.
    /// Si null: sin tabla, vendedor ingresa manual cada vez.
    /// Calculator: try/catch al deserializar + fallback "manual input requerido" + log warning grave.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? PenaltyPolicyJson { get; set; }
}

// src/TravelApi.Domain/Entities/InvoiceItem.cs — 3 props nuevas
public class InvoiceItem
{
    // ... props existentes ...

    /// <summary>
    /// FC1.3 (ADR-009, 2026-05-21): si false, este item NO entra en FiscalAmountToCredit
    /// (no se acredita al cliente en NC parcial). Default true. INMUTABLE post-emision de factura
    /// (CHECK indirecto via Invoice MutationGuards). G1: preseleccion false para categories
    /// AdministrativeFee/Insurance/OperatorAdvance.
    /// </summary>
    public bool IsRefundable { get; set; } = true;

    /// <summary>
    /// FC1.3 (ADR-009): clasificacion del item para alertas UI y default de IsRefundable (G1).
    /// </summary>
    public InvoiceItemCategory ItemCategory { get; set; } = InvoiceItemCategory.Service;

    /// <summary>
    /// FC1.3 (ADR-009): trazabilidad linea InvoiceItem <-> ServicioReserva origen.
    /// Habilita el calculo "que linea pertenece a que servicio". Nullable: facturas legacy
    /// o conceptos sueltos no tienen origen.
    /// </summary>
    public int? SourceServicioReservaId { get; set; }
    public ServicioReserva? SourceServicioReserva { get; set; }
}

// src/TravelApi.Domain/Entities/FiscalSnapshot.cs — 2 props nuevas
public class FiscalSnapshot
{
    // ... props existentes ...

    /// <summary>
    /// FC1.3 (ADR-009): snapshot del Supplier.InvoicingMode al momento de emitir la factura.
    /// CRITICO: si el operador cambia de modo despues, el calculo NC parcial debe usar este
    /// valor (no el actual). Mitigacion R3 plan funcional. Nullable porque las facturas legacy
    /// no tienen este snapshot.
    /// </summary>
    public SupplierInvoicingMode? InvoicingModeAtEvent { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): redundante con OriginatingInvoice.TipoComprobante pero permite queries
    /// de auditoria sin join. Nullable por legacy.
    /// </summary>
    public int? OriginalInvoiceTypeAtEvent { get; set; }
}

// src/TravelApi.Domain/Entities/BookingCancellation.cs — 7 props nuevas (REDUCIDO por GR-004)
public class BookingCancellation
{
    // ... props existentes ...

    // GR-004: NO persistimos la liquidacion entera. Solo guardamos el resultado de clasificacion
    // + timestamp + quien lo corrio. El detalle JSON vive en ApprovalRequest.Metadata cuando hay review.

    /// <summary>
    /// FC1.3 (ADR-009, GR-004): tipo de NC clasificado por el calculator. Solo Fase 1 maneja
    /// PartialOnOriginal (los casos TotalPlusNewInvoice se rechazan en Confirm via GR-001).
    /// Null hasta que el calculator corra.
    /// </summary>
    public CreditNoteKind? CreditNoteKind { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009, GR-004): bitflag de motivos que activaron review manual.
    /// Se persiste para queries de auditoria/reporting. None = clasificador permitio auto-emision.
    /// </summary>
    public ReviewRequiredReason ReviewRequiredReason { get; set; } = ReviewRequiredReason.None;

    /// <summary>
    /// FC1.3 (ADR-009): momento en que corrio el calculator. Null si nunca corrio (FC1.2 path).
    /// </summary>
    public DateTime? LiquidationComputedAt { get; set; }

    [MaxLength(450)]
    public string? LiquidationComputedByUserId { get; set; }

    [MaxLength(200)]
    public string? LiquidationComputedByUserName { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): FK al ApprovalRequest tipo PartialCreditNoteApproval que aprueba
    /// la liquidacion. Null hasta que el BC pase por ManualReviewPending. Persistido para
    /// audit cross-reference (mismo patron que Invoice.AnnulmentApprovalRequestId).
    /// OnDelete: Restrict.
    /// </summary>
    public int? PartialCreditNoteApprovalRequestId { get; set; }
    public ApprovalRequest? PartialCreditNoteApprovalRequest { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): trazabilidad de la revision manual. Null si el BC paso por flujo auto.
    /// </summary>
    [MaxLength(450)]
    public string? ManualReviewerUserId { get; set; }

    [MaxLength(200)]
    public string? ManualReviewerUserName { get; set; }

    public DateTime? ManualReviewedAt { get; set; }

    [MaxLength(1000)]
    public string? ManualReviewComment { get; set; }
}

// src/TravelApi.Domain/Entities/HotelBooking.cs — 1 prop nueva
public class HotelBooking
{
    // ... props existentes ...

    /// <summary>
    /// FC1.3 (ADR-009): lista JSON de conceptos no reintegrables que se imputan al cliente
    /// fuera del costo neto/venta (ej: cargo gestion $5.000, seguro cancelacion $20.000).
    /// Cada concepto se traduce a un InvoiceItem con IsRefundable=false al facturar.
    /// Tipo jsonb (consistencia con PenaltyPolicyJson).
    /// Schema: [{"description": string, "amount": decimal, "category": InvoiceItemCategory}, ...]
    /// Null o vacio: sin conceptos adicionales.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? NonRefundableConceptsJson { get; set; }
}

// src/TravelApi.Domain/Entities/ApprovalRequest.cs — SIN cambios de propiedades,
// SOLO se agrega xmin shadow + UseXminAsConcurrencyToken() en migracion M0 pre-requisito (RH-006).
```

#### 2.3.3 Extension de enums

```csharp
// src/TravelApi.Domain/Entities/BookingCancellationStatus.cs — 4 valores nuevos
public enum BookingCancellationStatus
{
    Drafted = 0,
    AwaitingFiscalConfirmation = 1,
    AwaitingOperatorRefund = 2,
    ClientCreditApplied = 3,
    Closed = 4,
    AbandonedByOperator = 5,
    Aborted = 6,
    ArcaRejected = 7,
    // ===== FC1.3 nuevos =====
    RequiresManualReview = 8,    // clasificador identifico caso review pero approval no abierto aun (transitorio dentro de misma tx)
    ManualReviewPending = 9,     // ApprovalRequest tipo PartialCreditNoteApproval abierto
    ManualReviewApproved = 10,   // admin aprobo, siguiente paso emitCreditNote() (Fase 1: avanza a AwaitingFiscalConfirmation)
    ManualReviewRejected = 11,   // admin rechazo, BC vuelve a Drafted o se aborta
}

// src/TravelApi.Domain/Entities/ApprovalRequestType.cs — 1 valor nuevo
public enum ApprovalRequestType
{
    // ... existentes 0..10 ...
    PartialCreditNoteApproval = 11,  // FC1.3 (ADR-009): admin aprueba liquidacion FC1.3
}
```

#### 2.3.4 Extension de `OperationalFinanceSettings`

```csharp
public class OperationalFinanceSettings
{
    // ... props existentes ...

    /// <summary>
    /// FC1.3 (ADR-009): feature flag maestro del modulo FC1.3. Si false, BC service ignora
    /// el clasificador y se comporta exactamente como FC1.2 (NC por total).
    /// Default false en prod. Independiente de EnableNewCancellationFlow.
    /// PRE-CONDICION (GR-002): si este flag es true, EnableNewCancellationFlow tambien debe
    /// ser true. Validacion en startup rechaza arranque con combinacion invalida.
    /// </summary>
    public bool EnablePartialCreditNotes { get; set; } = false;

    /// <summary>
    /// FC1.3 (ADR-009): por debajo de este monto en ARS, NC parcial se auto-emite si no
    /// hay otros disparadores manuales. Default 500.000. Sujeto a confirmacion contador (F1).
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal PartialNcAutoApprovalThreshold { get; set; } = 500_000m;

    /// <summary>
    /// FC1.3 (ADR-009): por encima de PartialNcAutoApprovalThreshold y hasta este monto,
    /// admin reforzada (comentario min 20 chars + 4-eyes). Default 2.000.000 ARS.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal PartialNcAdminReviewThreshold { get; set; } = 2_000_000m;

    /// <summary>
    /// FC1.3 (ADR-009 + G5): por encima de PartialNcAdminReviewThreshold, admin reforzada
    /// con comentario min 100 chars + flag AccountingReviewRequired=true en metadata.
    /// Si null, no hay tope superior y todo lo > Admin Review entra al flujo G5.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal? PartialNcAccountingReviewThreshold { get; set; } = null;

    /// <summary>
    /// FC1.3 (ADR-009): template de descripcion de la NC parcial. Variables soportadas:
    /// {invoiceType}, {invoiceNumber}, {pointOfSale}, {fiscalAmount}, {currency},
    /// {cancellationReason}, {nonRefundableAmount}, {operatorPenaltyAmount}, {customerName},
    /// {customerTaxId}. Validacion al guardar.
    /// </summary>
    [MaxLength(500)]
    public string PartialNcDescriptionTemplate { get; set; } =
        "NC parcial s/Fc {invoiceType} {invoiceNumber} (PV {pointOfSale}). " +
        "Monto fiscal acreditado: {fiscalAmount} {currency}. " +
        "Concepto: {cancellationReason}. " +
        "Items no reintegrables retenidos: {nonRefundableAmount} {currency}.";

    /// <summary>
    /// FC1.3 (ADR-009): dias desde T2 despues de los cuales se alerta al admin que el plazo
    /// RG 4540 (15 dias) esta a punto de vencer en un BC en ManualReviewPending. Default 10.
    /// </summary>
    public int ManualReviewMaxDaysBeforeRg4540Alert { get; set; } = 10;

    /// <summary>
    /// FC1.3 (ADR-009 + RH-013): timestamp del deploy de FC1.3 a prod. Heuristica caso 4
    /// (factura confusa): facturas emitidas antes de esta fecha se flagean como
    /// "legacy invoice" para revision manual.
    /// **Default null** (RH-008): la heuristica legacy esta DESACTIVADA por default.
    /// La migracion M3 setea automaticamente UtcNow() solo si el contador lo pide explicitamente
    /// en round 3. Si null + EnablePartialCreditNotes=true, startup setea UtcNow + log warning
    /// (RH-013). Activar solo si contador lo pide.
    /// </summary>
    public DateTime? Fc13DeployDate { get; set; } = null;

    /// <summary>
    /// FC1.3 (ADR-009 + RH-008): unica expresion regex con alternativas separadas por '|' que
    /// el clasificador caso 4 usa para flagear "factura con descripcion generica unica".
    /// **Default vacio (string.Empty) — heuristica DESACTIVADA** (RH-008/RH-021).
    /// Configurable por agencia. Si no esta vacio, se evalua case-insensitive sobre Description
    /// del unico InvoiceItem. Activar solo si contador lo pide explicitamente en round 3 + test
    /// previo contra dataset legacy (< 5% falsos positivos).
    /// Ejemplo si activo: "^(servicio|concepto|importe|operacion|reserva)".
    /// </summary>
    [MaxLength(1000)]
    public string GenericDescriptionPatterns { get; set; } = string.Empty;

    /// <summary>
    /// FC1.3 (ADR-009 + GR-005): si true, permite self-approval cuando el sistema tiene 1 solo
    /// admin y vendedor = admin. Requiere comentario reforzado 100+ chars + flag audit
    /// SelfApprovedDueToSingleAdmin=true en Metadata. Default false (4-eyes estricto).
    /// Pensado para agencias chicas (1 sola persona admin). NO afecta cuando hay 2+ admins.
    /// </summary>
    public bool Allow4EyesBypassWhenSingleAdmin { get; set; } = false;
}
```

#### 2.3.5 CHECK constraints SQL (sintaxis PostgreSQL)

**Convencion**: `chk_<tabla>_<concepto>` + `migrationBuilder.Sql(@"ALTER TABLE ...")` + interceptor `PostgresException.SqlState='23514'` -> `BusinessInvariantViolationException` -> HTTP 409.

```sql
-- RH-014: PenaltyPolicyJson debe ser objeto top-level si no es null.
ALTER TABLE "Suppliers"
  ADD CONSTRAINT chk_Suppliers_penaltypolicy_object
  CHECK (
    "PenaltyPolicyJson" IS NULL
    OR jsonb_typeof("PenaltyPolicyJson") = 'object'
  );

-- ManualReviewPending/Approved/Rejected requiere ApprovalRequest FK (INV-FC1.3-002).
ALTER TABLE "BookingCancellations"
  ADD CONSTRAINT chk_BookingCancellations_manualreview_approvalref
  CHECK (
    "Status" NOT IN (9, 10, 11)
    OR "PartialCreditNoteApprovalRequestId" IS NOT NULL
  );

-- Coherencia CreditNoteKind: si BC paso por clasificador (LiquidationComputedAt != null),
-- CreditNoteKind no puede ser null.
ALTER TABLE "BookingCancellations"
  ADD CONSTRAINT chk_BookingCancellations_creditnotekind_consistent
  CHECK (
    "LiquidationComputedAt" IS NULL
    OR "CreditNoteKind" IS NOT NULL
  );

-- Status enum acepta valores 0..11 (extiende CHECK FC1 si existe).
-- Si el CHECK actual no enumera valores explicitos, esta linea no aplica.
-- Verificar en migracion FC1 antes de M4.

-- NOTA: INV-FC1.3-005 (suma de componentes = OriginalInvoiceAmount) NO existe como CHECK
-- en BD Fase 1 porque NO persistimos los componentes (GR-004). Se valida en el calculator
-- in-memory. Tests unit cubren.
```

### 2.4 Por que NO persistimos `FiscalLiquidation` Fase 1 (GR-004)

**Decision**: la liquidacion calculada se mantiene **in-memory** durante el flujo de Confirm. Solo se persisten 5 campos summary (`CreditNoteKind`, `ReviewRequiredReason`, `LiquidationComputedAt`, `LiquidationComputedByUserId/Name`). El detalle completo (montos, items, penalty, regla) se serializa al `ApprovalRequest.Metadata` JSON **solamente cuando hay manual review**.

**Argumentos a favor de NO persistir**:

- **Blast radius reducido**: Fase 1 NO toca AfipService (no emite NC parcial real al ARCA). Persistir montos que nadie lee es introducir columnas que luego habria que migrar/limpiar.
- **Cero riesgo de divergencia**: si persistieramos `FiscalAmountToCredit` pero Fase 1 sigue emitiendo NC total via FC1.2, los datos en BD contradirian la NC emitida. **Era el bloqueante RH-004 del reviewer.**
- **Auditabilidad preservada**: el snapshot completo se guarda en `ApprovalRequest.Metadata` para los casos que necesitan review humano (los unicos donde importa documentar la decision). Para auto-approval, el calculator corre, decide "OK pase a FC1.2 flow", y no genera approval — el log de la operacion queda en el audit log generico del BC.
- **Fase 2 introduce persistencia completa** cuando AfipService emita NC parcial real. La migracion Fase 2 podra agregar columnas leyendo de los Metadata JSON ya guardados como backfill.

**Argumentos en contra evaluados**:

- *"Si Fase 2 demora, perdemos data historica de calculos auto-aprobados"*. Respuesta: el clasificador es deterministico. Re-correrlo Fase 2 sobre los inputs reproduce el mismo resultado. No hay perdida real.
- *"Reporting necesita los montos"*. Respuesta: Fase 1 no genera reporting de NC parcial — eso es Fase 3 UI. Para los casos manual review (~10-30% del total), el JSON del approval tiene todo.

**Costo de cambiar despues**: bajo. Fase 2 agrega 10 columnas `FiscalLiquidation_*` con backfill desde `ApprovalRequest.Metadata`. Aditivo, sin perdida.

### 2.5 Por que `Supplier.PenaltyPolicyJson` es `jsonb` y no tabla normalizada

**Decision**: columna `jsonb` (RH-014 elevo de `text` a `jsonb`).

**Argumentos a favor**:

- **Acceso patron**: la tabla se lee al momento de calcular liquidacion. No es hot path. Una sola lectura por flujo.
- **Sin necesidad de queries cross-supplier**: no hay reporte "todos los suppliers con penalidad > 30% para 15 dias antes". Si aparece, `jsonb` permite `JSON_VALUE`-like queries (`->`, `->>`, `@>`).
- **Schema evoluciona**: hoy 4 tiers, manana podria ser 7 tiers, o estructura curve-based. JSON acomoda sin migrar.
- **Override manual del vendedor (D2)**: vendedor puede ignorar la tabla y poner monto manual. La tabla es sugerencia, no restriccion dura.
- **Convencion existente**: `BookingCancellation.FiscalSnapshot.ExtrasJson`, `HotelBooking.RoomingAssignmentsJson` ya usan JSON columns. Consistencia con repo.
- **`jsonb` vs `text`**: `jsonb` valida sintaxis SQL automaticamente al insert/update y soporta CHECK `jsonb_typeof = 'object'` (ver §2.3.5). `text` plano no.

**Argumentos en contra evaluados**:

- *"No queryable nativamente para reportes complejos"*. Respuesta: `jsonb` soporta queries y indexing GIN.
- *"Validacion de schema interno (tiers ordenados, percentages 0..100)"*. Respuesta: validacion en `SupplierService` con FluentValidation antes de persistir. Mas robusto que CHECK constraints aislados.

**Manejo de errores en calculator** (RH-014):

```csharp
// FiscalLiquidationCalculator
try {
    var policy = JsonSerializer.Deserialize<PenaltyPolicy>(supplier.PenaltyPolicyJson);
    // ... usar policy.Tiers ...
} catch (JsonException ex) {
    _logger.LogError(ex, "Supplier {SupplierId} PenaltyPolicyJson malformed - falling back to manual input", supplier.Id);
    // fallback: tratar como si fuera null (vendedor ingreso manual)
}
```

**Costo de cambiar despues**: medio. Migrar `jsonb` -> tabla normalizada requiere script de extraccion + crear tabla `SupplierPenaltyTier`. Blast radius acotado al calculator.

### 2.6 `IFiscalLiquidationCalculator` como servicio aparte

**Decision**: extraer service nuevo, **no** meter la logica dentro de `BookingCancellationService.ConfirmAsync`.

**Ubicacion**: `src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs` (interface) + `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` (impl).

```csharp
// src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs
public interface IFiscalLiquidationCalculator
{
    /// <summary>
    /// Calcula la liquidacion fiscal y clasifica el caso (matriz 8 contador 2026-05-21).
    /// Puro: sin DbContext, sin async, sin IO. Input: snapshot de datos ya cargados.
    /// Output: DTO transitorio + narrativa. El caller decide si persiste el summary
    /// + serializa al ApprovalRequest.Metadata.
    /// </summary>
    FiscalLiquidationDto Calculate(FiscalLiquidationInput input, OperationalFinanceSettings settings);
}

public record FiscalLiquidationInput(
    Invoice OriginatingInvoice,
    IReadOnlyList<InvoiceItem> Items,            // items de la factura origen, con IsRefundable
    Supplier Supplier,                            // con InvoicingMode y PenaltyPolicyJson
    SupplierInvoicingMode? InvoicingModeAtEvent, // si null se usa Supplier.InvoicingMode actual
    decimal CancellationAmount,                   // monto cancelado (general = OriginalInvoiceAmount)
    decimal OperatorPenaltyAmount,                // ingresado por vendedor (tabla o manual)
    bool RetentionNatureChangedByUser,            // checkbox manual vendedor (caso 7 manual)
    bool OriginalInvoiceUnclearByUser);           // checkbox manual vendedor (caso 4 manual)
```

**Argumentos a favor**: ver round 1 ADR §2.6 (sin cambios). Testeable aislado, sin coupling al estado de transaccion, reuse futuro UI preview, inyectable/mockeable.

### 2.7 Mapping al `ApprovalRequest` existente

**Decision**: usar `ApprovalRequest` con `RequestType = PartialCreditNoteApproval`, `EntityType = "BookingCancellation"`, `EntityId = bc.Id`, `Metadata` JSON con la liquidacion + edicion admin. FK `BC.PartialCreditNoteApprovalRequestId` para join inverso.

**Schema del `Metadata` JSON** (estable, versionado):

```json
{
  "schemaVersion": 1,
  "computedAt": "2026-05-21T14:30:00Z",
  "computedByUserId": "user-vendedor-1",
  "computedByUserName": "Juan Vendedor",
  "computedCase": "Case8_FacturaA",
  "originalInvoiceAmount": 1000000.00,
  "cancellationAmount": 1000000.00,
  "operatorPenaltyAmount": 200000.00,
  "nonRefundableItemsAmount": 50000.00,
  "fiscalAmountToCredit": 750000.00,
  "amountToRefundCustomer": 750000.00,
  "finalNetInvoiced": 250000.00,
  "creditNoteKind": "PartialOnOriginal",
  "reviewRequiredReason": ["CustomerIsRiOrFacturaA", "HasNonRefundableItems"],
  "currency": "ARS",
  "classificationExplanation": "Factura A obligatoria a revision por criterio contador 2026-05-21.",
  "accountingReviewRequired": false,
  "selfApprovedDueToSingleAdmin": false,
  "edits": [
    {
      "at": "2026-05-21T15:10:00Z",
      "by": "user-admin-1",
      "byName": "Maria Admin",
      "fields": {
        "operatorPenaltyAmount": { "from": 200000.00, "to": 250000.00 },
        "fiscalAmountToCredit":  { "from": 750000.00, "to": 700000.00 }
      },
      "comment": "Operador mando email con penalidad actualizada por antelacion 18 dias en lugar de 20"
    }
  ]
}
```

**Flujo de edicion admin (G3)**: ver round 1 §2.7 (sin cambios funcionales). RH-006 cubierto: `ApprovalRequest` tendra concurrency token via migracion M0 — admin que edita con `xmin` viejo recibe `DbUpdateConcurrencyException` y reintenta.

**Auditoria extra (RH-012)**: cuando admin edita, el `AuditLog.Changes` registra el diff completo de campos modificados con shape:

```json
{
  "FiscalAmountToCredit": { "Old": "750000.00", "New": "700000.00" },
  "OperatorPenaltyAmount": { "Old": "200000.00", "New": "250000.00" },
  "ReviewRequiredReason": { "Old": "CustomerIsRiOrFacturaA, HasNonRefundableItems", "New": "CustomerIsRiOrFacturaA, HasNonRefundableItems" }
}
```

Test que valida que post-edicion `AuditLog.Changes` deserializado tiene todos los campos modificados con `Old`/`New`.

### 2.8 Maquina de estados (FC1.3 Fase 1)

Extiende ADR-002 §2.4 con los 4 estados nuevos.

#### 2.8.1 Diagrama (revisado por GR-001/GR-003/GR-006)

```
                  ┌──────────────────────────────────────────────────────────────────────┐
                  │                                                                       │
                  v                                                                       │
        ┌──────────────┐                                                                  │
        │   Drafted (0)│                                                                  │
        └──────┬───────┘                                                                  │
               │ confirmCancellation()                                                    │
               │ + EnsureFeatureFlagOn (FC1.2 kill switch — rechaza si FC1.2 OFF)         │
               │ + Calculator.Calculate() corre solo si EnablePartialCreditNotes=true     │
               │                                                                          │
               ├─── FC1.3 flag OFF                                                        │
               │    -> AwaitingFiscalConfirmation (1) -> ... flujo FC1.2 vigente          │
               │                                                                          │
               ├─── FC1.3 flag ON + CreditNoteKind = TotalPlusNewInvoice                  │
               │    -> THROW InvalidOperationException (GR-001)                           │
               │       "Caso fiscal requiere FC1.3 Fase 2 - use flujo legacy"             │
               │       BC vuelve a Drafted, sin persistir nada FC1.3.                     │
               │                                                                          │
               ├─── FC1.3 flag ON + CreditNoteKind = PartialOnOriginal + ReasonNone       │
               │    + caso 2 trivial sin disparadores                                     │
               │    -> AwaitingFiscalConfirmation (1) -> ... flujo FC1.2 vigente          │
               │                                                                          │
               └─── FC1.3 flag ON + CreditNoteKind = PartialOnOriginal + Reason != None   │
                    (incluye: Factura A, items no reintegrables, monto > threshold,       │
                     CommissionOnly mode [GR-003], Penalty + TotalToCustomer [GR-006],    │
                     LegacyInvoice si setting activo, MultiCurrency)                      │
                    -> RequiresManualReview (8) [transitorio dentro misma tx]              │
                          │                                                               │
                          │ submitForReview() (atomico, mismo Confirm)                    │
                          │ abre ApprovalRequest PartialCreditNoteApproval                │
                          │ + persiste Metadata JSON con detalle completo                 │
                          │                                                               │
                          v                                                               │
                    ManualReviewPending (9) <----+ self-loop editLiquidation()            │
                          │                      │ (admin edita penalidad o items)        │
                          │                      │ + audit BC_LiquidationEdited           │
                          │                      │ + apend a Metadata.edits[]             │
                          │                      └─────────────────                       │
                          ├─── approveLiquidation(comment) (4-eyes o GR-005 bypass)       │
                          │    -> ManualReviewApproved (10)                                │
                          │            │                                                  │
                          │            │ emitCreditNote() invocado automaticamente       │
                          │            │ (Fase 1: avanza a FC1.2 path con NC total real, │
                          │            │  log warning "Fase 1: NC parcial calculada pero │
                          │            │  AfipService emite total. Fase 2 emite parcial.")│
                          │            v                                                  │
                          │     AwaitingFiscalConfirmation (1) -> ... flujo FC1.2         │
                          │                                                               │
                          └─── rejectLiquidation(comment) (comment >= 20 chars)           │
                                -> ManualReviewRejected (11)                              │
                                       │                                                  │
                                       ├─── resetToDraft() automatico                     │
                                       │     -> Drafted (0)  ─────────────────────────────┘
                                       │
                                       └─── abort()
                                             -> Aborted (6)
```

**Nota importante**: el path `TotalPlusNewInvoice -> ManualReviewApproved -> queda colgado` del round 1 **fue eliminado**. Hoy esos casos son rechazo explicito en Confirm (GR-001).

#### 2.8.2 Interaccion con `ApprovalRequest`

```
                   BookingCancellation                  ApprovalRequest
                   (BC.Id, BC.Status)                   (AR.Id, AR.Status)
                          │                                   │
   ConfirmAsync ────────► RequiresManualReview (8)             │
   (ReviewRequiredReason != None)                              │
                          │                                   │
                          │ submitForReview() ──────► CREATE   │
                          │  (atomico misma tx)              ──► AR Pending
                          │                                     EntityType="BookingCancellation"
                          │                                     EntityId=BC.Id
                          │                                     RequestType=PartialCreditNoteApproval=11
                          │                                     Metadata={...liquidacion completa...}
                          v                                   │
                   ManualReviewPending (9)                    │
                   PartialCreditNoteApprovalRequestId=AR.Id   │
                          │                                   │
   EditLiquidationAsync ─►│ self-loop, audit               ──► AR.Metadata.edits[] apend
                          │ (no cambia status, no transiciona) │ AR.xmin (RH-006) protege race
                          │                                   │
   POST /approvals/.../approve ──────────────────────────►   AR.Status = Approved
                          │  (callback bridge notifica BC)   │
                          v                                   │
                   ManualReviewApproved (10)                  AR.Status = Consumed
                          │                                  (cuando emitCreditNote corre)
                          │ emitCreditNote() (auto)           │
                          v                                   │
                   AwaitingFiscalConfirmation (1)              │
                   ... flujo FC1.2 sigue
```

**Job de reconciliacion bridge (RH-011)**: detalle en §2.12.

#### 2.8.3 Tabla de transiciones

| Estado origen | Trigger | Estado destino | Condiciones | Quien dispara | Override |
|---|---|---|---|---|---|
| `Drafted` | `confirmCancellation()` (flag off o sin disparadores) | `AwaitingFiscalConfirmation` | (FC1.2 flag on + FC1.3 flag off) OR (ambos on + `ReviewRequiredReason=None`) | Vendedor con permiso `cobranzas.invoice_annul` | NO |
| `Drafted` | `confirmCancellation()` (FC1.2 off) | THROW InvalidOperationException | `EnableNewCancellationFlow=false` (kill switch FC1.2) | — | NO |
| `Drafted` | `confirmCancellation()` (CreditNoteKind=TotalPlusNewInvoice) | THROW InvalidOperationException | FC1.3 flag on + calculator devuelve TotalPlusNewInvoice (GR-001) | — | NO |
| `Drafted` | `confirmCancellation()` (con disparadores) | `RequiresManualReview` -> `ManualReviewPending` (atomico) | FC1.3 flag on + `ReviewRequiredReason != None` + CreditNoteKind=PartialOnOriginal | Vendedor (sistema fuerza) | NO |
| `Drafted` | `abort()` | `Aborted` | — | Vendedor o admin | NO |
| `ManualReviewPending` | `editLiquidation(...)` | `ManualReviewPending` (self-loop) | Admin reforzada: modifica penalty/non-refundable/kind + comentario >= 20 chars + (admin != vendedor OR GR-005 bypass aplica) | Admin | NO |
| `ManualReviewPending` | `POST /approvals/{id}/approve` (callback) | `ManualReviewApproved` | `ApprovalRequest.Status = Approved` + (admin != vendedor OR GR-005 bypass) + comment >= 20 chars (o 100 si AccountingReview) | Admin via callback bridge | NO |
| `ManualReviewPending` | `POST /approvals/{id}/reject` (callback) | `ManualReviewRejected` | Comentario reject >= 20 chars | Admin via callback bridge | NO |
| `ManualReviewApproved` | `emitCreditNote()` (auto inmediato post-approval) | `AwaitingFiscalConfirmation` | Solo CreditNoteKind=PartialOnOriginal (los TotalPlusNewInvoice ni siquiera llegan, GR-001) Fase 1: avanza a FC1.2 path con NC total real, log warning explicito | Sistema | NO |
| `ManualReviewRejected` | `resetToDraft()` (auto inmediato post-reject) | `Drafted` | Inmediato. BC limpia `CreditNoteKind`, `ReviewRequiredReason`, `LiquidationComputed*`, nulea `PartialCreditNoteApprovalRequestId`. Approval queda en Rejected (histórico). | Sistema | NO |
| `ManualReviewRejected` | `abort()` | `Aborted` | — | Vendedor o admin | NO |
| `AwaitingFiscalConfirmation` y posteriores | — | Igual que FC1.2 vigente | — | — | — |

#### 2.8.4 Invariantes Fase 1 (extiende Bucket G)

| ID | Regla | `AdmitsOverride` |
|---|---|---|
| **INV-FC1.3-001** | BC no transiciona a `AwaitingFiscalConfirmation` directamente si `ReviewRequiredReason != None`. Debe pasar por `ManualReviewPending` + `ManualReviewApproved`. | `false` |
| **INV-FC1.3-002** | `Status IN (ManualReviewPending, ManualReviewApproved, ManualReviewRejected)` requiere `PartialCreditNoteApprovalRequestId != NULL`. CHECK SQL. | `false` |
| **INV-FC1.3-003** | `ManualReviewApproved` requiere `ApprovalRequest.Status = Approved`. Verificado en service callback. | `false` |
| **INV-FC1.3-004** | `approveLiquidation()` requiere admin != vendedor (`DraftedByUserId != ResolvedByUserId`). **Excepcion (GR-005)**: si `Allow4EyesBypassWhenSingleAdmin=true` AND solo 1 usuario admin existe AND vendedor=admin, admite self-approval con comentario 100+ chars + flag `SelfApprovedDueToSingleAdmin=true` en Metadata. | `false` (la excepcion GR-005 NO es override, es regla distinta) |
| **INV-FC1.3-005** | Calculator garantiza `FiscalAmountToCredit + NonRefundableItemsAmount + OperatorPenaltyAmount = OriginalInvoiceAmount` (tolerancia 0.01). Validado en calculator. **No es CHECK SQL Fase 1 porque no persistimos los componentes** (GR-004). Sera CHECK SQL en Fase 2 cuando se persistan. | `false` |
| **INV-FC1.3-006** | Items con `IsRefundable=false` en factura origen suman exactamente `NonRefundableItemsAmount` calculado. Validacion en calculator. | `false` |
| **INV-FC1.3-007** | BC FC1.3 solo acepta reservas 100% Hotel (`Reserva.Servicios` todos con `ProductType = ServiceTypes.Hotel`). Validacion en service. | `true` con justificacion admin (`ApprovalRequest` tipo `InvariantOverride=7`, justificacion >= 50 chars **distinta del comentario de aprobacion del BC**, RH-016). |
| **INV-FC1.3-008** | `CreditNoteKind` no cambia post-`ManualReviewApproved`. | `false` |
| **INV-FC1.3-009** | Edicion admin (G3) requiere comentario distinto al comentario de aprobacion. Validacion en service. | `false` |
| **INV-FC1.3-010** | `CreditNoteKind = TotalPlusNewInvoice` NUNCA se persiste en `BookingCancellation` Fase 1 (GR-001). Si calculator lo devuelve, Confirm tira `InvalidOperationException` ANTES de persistir. Test cubre. | `false` |

### 2.9 Reglas del clasificador (matriz 8 + disparadores) — revisado por GR-003/GR-006

El calculator aplica estas reglas en orden. La primera que matchea gana el `ComputedCase`. Los `ReviewRequiredReason` se acumulan en bitflag.

```
INPUT: FiscalLiquidationInput input + OperationalFinanceSettings s

STEP 0 — EARLY EXIT por modo CommissionOnly (GR-003)
   mode = input.InvoicingModeAtEvent ?? input.Supplier.InvoicingMode
   if (mode == CommissionOnly):
       reason = ReviewRequiredReason.InvoicingModeCommissionOnly
       // NO calculamos formula — pendiente respuesta contador F2 round 3
       // Devolvemos DTO con FiscalAmountToCredit=0, narrativa explicativa, kind=PartialOnOriginal
       return DTO(case=Case5/6, reason=InvoicingModeCommissionOnly, kind=PartialOnOriginal, narrative=...)

STEP 1 — disparadores siempre activos (acumulan a ReviewRequiredReason)
   ─ Si OriginalInvoice.TipoComprobante = 1 (Factura A):
        reason |= CustomerIsRiOrFacturaA
   ─ Si suma items con IsRefundable=false > 0:
        reason |= HasNonRefundableItems
   ─ Si OriginatingInvoice tiene OriginalInvoiceId != null (NC en cadena):
        reason |= Other
   ─ Si Currency != "ARS":
        reason |= MultiCurrency
   ─ Si s.GenericDescriptionPatterns NO esta vacio AND OriginatingInvoice.CreatedAt < s.Fc13DeployDate:
        reason |= LegacyInvoice
        // RH-008: por default ambos settings estan vacio/null, no se dispara
   ─ Si input.InvoicingModeAtEvent != null AND input.InvoicingModeAtEvent != current Supplier.InvoicingMode:
        reason |= RetentionChangesNature  // (caso 7 — sera rechazado en STEP 7)
   ─ Si input.RetentionNatureChangedByUser:
        reason |= RetentionChangesNature
   ─ Si input.OriginalInvoiceUnclearByUser:
        reason |= OriginalInvoiceUnclear   // (caso 4 — sera rechazado en STEP 7)

STEP 2 — heuristicas caso 4 (factura confusa) — RH-008 DESACTIVADAS por default
   if (s.GenericDescriptionPatterns NOT empty) {
       ─ Si Items.Count == 1 AND Description match regex s.GenericDescriptionPatterns:
            reason |= OriginalInvoiceUnclear
       ─ Si > 50% del Total tiene items con SourceServicioReservaId=null:
            reason |= OriginalInvoiceUnclear
       ─ Si SUM(InvoiceItem.ImporteIva) != Invoice.ImporteIva con tolerancia 0.50:
            reason |= OriginalInvoiceUnclear
   }

STEP 3 — calcular liquidacion (modo TotalToCustomer)
   nonRefundableTotal = SUM(items where IsRefundable=false → Total)
   penalty = input.OperatorPenaltyAmount  // ya validado >= 0

   // GR-006: caso 3 con penalty>0 en TotalToCustomer requiere clarificacion contador F4
   if (penalty > 0 AND mode == TotalToCustomer):
       reason |= PenaltyResetUncertainInResellerMode
       // Calculamos suponiendo "penalty resta" (hipotesis conservadora) pero marcamos para review.

   fiscalAmountToCredit = OriginalInvoiceAmount - nonRefundableTotal - penalty
   amountToRefundCustomer = fiscalAmountToCredit
   finalNetInvoiced = OriginalInvoiceAmount - fiscalAmountToCredit

   if (fiscalAmountToCredit < 0) throw InvariantViolation INV-FC1.3-005

STEP 4 — disparadores de monto
   if (s.PartialNcAccountingReviewThreshold != null AND fiscalAmountToCredit > s.PartialNcAccountingReviewThreshold):
        reason |= AmountAboveAccountingThreshold
   else if (fiscalAmountToCredit > s.PartialNcAdminReviewThreshold):
        reason |= AmountAboveAdminThreshold
   else if (fiscalAmountToCredit > s.PartialNcAutoApprovalThreshold):
        reason |= AmountAboveAdminThreshold

STEP 5 — clasificar caso 1..8 (informativo)
   if (reason.HasFlag(CustomerIsRiOrFacturaA))           case = Case8_FacturaA;
   else if (reason.HasFlag(OriginalInvoiceUnclear))      case = Case4_OriginalInvoiceUnclear;
   else if (reason.HasFlag(RetentionChangesNature))      case = Case7_RetentionChangesNature;
   else if (penalty > 0)                                 case = Case3_FullCancellationWithPenalty;
   else if (cancellationAmount == OriginalInvoiceAmount) case = Case2_FullCancellationNoRetention;
   else                                                  case = Case1_PartialRefundNoPenalty;

STEP 6 — CreditNoteKind
   if (case == Case4 || case == Case7) creditNoteKind = TotalPlusNewInvoice
   else                                  creditNoteKind = PartialOnOriginal

STEP 7 — Devolver DTO. El SERVICE caller (BookingCancellationService.ConfirmAsync) decide:
   ─ Si creditNoteKind == TotalPlusNewInvoice:
        SERVICE throws InvalidOperationException (GR-001) "Caso fiscal requiere FC1.3 Fase 2"
        BC NO se persiste con datos FC1.3. Queda en Drafted.
   ─ Si creditNoteKind == PartialOnOriginal AND reason == None:
        SERVICE transiciona BC a AwaitingFiscalConfirmation (FC1.2 path) sin abrir approval.
   ─ Si creditNoteKind == PartialOnOriginal AND reason != None:
        SERVICE persiste summary + abre ApprovalRequest + Metadata JSON con liquidation
        completa. BC -> ManualReviewPending.

OUTPUT: FiscalLiquidationDto inmutable.
```

### 2.10 Feature flag separado + pre-condicion GR-002

**Decision**: `OperationalFinanceSettings.EnablePartialCreditNotes` (bool, default false). Independiente de `EnableNewCancellationFlow`. **Pre-condicion startup**: si `EnablePartialCreditNotes=true && EnableNewCancellationFlow=false`, rechazar arranque con error claro.

**Implementacion pre-condicion** (en `Program.cs` post-build app, antes de `app.Run()`):

```csharp
using (var scope = app.Services.CreateScope())
{
    var settings = await scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>().GetEntityAsync();
    if (settings.EnablePartialCreditNotes && !settings.EnableNewCancellationFlow)
    {
        throw new InvalidOperationException(
            "Configuracion invalida: EnablePartialCreditNotes=true requiere EnableNewCancellationFlow=true. " +
            "FC1.3 depende de FC1.2 (decision GR-002). Apague FC1.3 o prenda FC1.2 antes de arrancar.");
    }
}
```

**Tabla de comportamiento por combinacion de flags** (GR-002 explicito):

| FC1.2 | FC1.3 | Resultado |
|---|---|---|
| OFF | OFF | Sistema legacy (pre-FC1.2). FC1.2 kill switch activo, modulo rechaza. |
| ON | OFF | FC1.2 vigente sin NC parcial (estado actual pos-merge FC1.2). Comportamiento por default tras merge OPS-FISCAL-001. |
| ON | ON | FC1.3 activo, NC parcial calculada cuando aplica. **Combinacion target despues de signoff FC1.3.** |
| OFF | ON | **RECHAZO al arranque** (GR-002). Pre-condicion validada en `Program.cs` post-build. |

**Rollback granular**:

- **Rollback de FC1.3 (apagar `EnablePartialCreditNotes`)**: deja FC1.2 funcional. BCs en estados 8..11 quedan en limbo hasta que admin actua manualmente (mover a Drafted o Aborted).
- **Rollback de FC1.2 (apagar `EnableNewCancellationFlow`)**: tambien apaga FC1.3 implicitamente porque el kill switch FC1.2 corre primero en Confirm. **Si FC1.3 estaba ON y FC1.2 se apaga**: en el siguiente startup, la pre-condicion GR-002 detecta combinacion invalida y exige operador apagar tambien FC1.3.

**Argumentos a favor**: composabilidad, rollback granular, QA escalonado, sin interferencia con OPS-FISCAL-001. Round 1 §2.10 valido sin cambios.

### 2.11 Manejo de servicios no-Hotel (rechazo Fase 1) - patron de override real

`BookingCancellationService` ya tiene el patron real de override en lineas 261-281 (usando `ApprovalRequest` tipo `InvariantOverride=7`). FC1.3 reusa ese mismo patron.

```csharp
// Pseudo
if (settings.EnablePartialCreditNotes)
{
    var nonHotelServices = reserva.Servicios
        .Where(s => !string.Equals(s.ProductType, ServiceTypes.Hotel, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (nonHotelServices.Any())
    {
        // INV-FC1.3-007 admite override
        // Patron real (NO MutationContext.HasOverrideForInvariant — ese patron pertenece
        // a ADR-001 que esta rechazado).
        ApprovalRequest? overrideApproval = null;
        if (request.IsAdminOverride && request.ApprovalRequestPublicId is not null)
        {
            overrideApproval = await _db.ApprovalRequests
                .FirstOrDefaultAsync(a =>
                    a.PublicId == request.ApprovalRequestPublicId
                    && a.RequestType == ApprovalRequestType.InvariantOverride
                    && a.EntityType == "BookingCancellation"
                    && a.EntityId == bc.Id
                    && a.Status == ApprovalStatus.Approved
                    && a.RequestedByUserId == userId
                    && a.ExpiresAt > DateTime.UtcNow
                    && (a.Reason ?? string.Empty).Trim().Length >= 50
                    // RH-016: justificacion del override DEBE ser distinta del ManualReviewComment
                    // del BC. Validado en service (no SQL).
                    , ct);
        }
        if (overrideApproval is null)
            throw new BusinessInvariantViolationException(
                "INV-FC1.3-007",
                $"FC1.3 Fase 1 solo soporta reservas 100% Hotel. " +
                $"Servicios no-Hotel detectados: {string.Join(", ", nonHotelServices.Select(s => s.ProductType))}. " +
                $"Use flujo legacy (apagar EnablePartialCreditNotes para esta operacion) " +
                $"o solicitar override via InvariantOverride approval.");
    }
}
```

### 2.12 Job de reconciliacion bridge (RH-011 cerrado)

**Problema**: si `ApprovalRequestService.ApproveAsync` aprueba el approval pero el callback bridge `IPartialCreditNoteApprovalBridge.OnApprovedAsync` falla (excepcion, timeout, deploy mid-flight), el `ApprovalRequest` queda `Approved` pero `BookingCancellation` queda `ManualReviewPending`. Sin reconciliacion, ese BC queda huerfano.

**Mitigacion**: job nocturno + endpoint admin de force-callback.

**Sub-fase FC1.3.6b** (agregada al plan tactico): `PartialCreditNoteBridgeReconciliationJob`.

```csharp
public class PartialCreditNoteBridgeReconciliationJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Detectar: ApprovalRequest.Status=Approved AND ApprovalRequest.RequestType=PartialCreditNoteApproval
        //           AND existe BC con PartialCreditNoteApprovalRequestId=AR.Id
        //           AND BC.Status=ManualReviewPending
        //           AND AR.ResolvedAt < UtcNow - 30 minutes
        var orphans = await _db.ApprovalRequests
            .Where(a => a.RequestType == ApprovalRequestType.PartialCreditNoteApproval
                     && a.Status == ApprovalStatus.Approved
                     && a.ResolvedAt < DateTime.UtcNow.AddMinutes(-30))
            .Join(_db.BookingCancellations,
                  a => a.Id,
                  bc => bc.PartialCreditNoteApprovalRequestId,
                  (a, bc) => new { Approval = a, BC = bc })
            .Where(x => x.BC.Status == BookingCancellationStatus.ManualReviewPending)
            .ToListAsync(ct);

        foreach (var orphan in orphans)
        {
            _logger.LogWarning("FC1.3 bridge huerfano: AR {ARId} Approved pero BC {BCId} en ManualReviewPending. Forzando callback.", orphan.Approval.Id, orphan.BC.Id);
            try {
                await _bridge.OnApprovedAsync(
                    orphan.Approval.Id,
                    orphan.Approval.ResolvedByUserId ?? "system-reconciliation",
                    orphan.Approval.ResolvedByUserName,
                    orphan.Approval.ResolverNotes,
                    ct);
            } catch (Exception ex) {
                _logger.LogError(ex, "FC1.3 reconciliation failed for AR {ARId}", orphan.Approval.Id);
                await _notificationService.NotifyAdminsAsync(
                    $"FC1.3 callback bridge fallo para AR {orphan.Approval.Id} - intervencion manual requerida", ct);
            }
        }
    }
}
```

**Endpoint admin de force-callback** (casos extremos cuando el job tampoco recupera):

```
POST /api/cancellations/{publicId}/force-approval-callback
RequirePermission(Permissions.CobranzasInvoiceAnnul) + admin role
```

Llama `bridge.OnApprovedAsync` o `OnRejectedAsync` segun `ApprovalRequest.Status`. Audit reforzado.

**Idempotencia del bridge**: `OnApprovedAsync` valida `BC.Status == ManualReviewPending`. Si ya esta `ManualReviewApproved`, log warning + return sin cambios. Mismo para `OnRejectedAsync`.

### 2.13 Comportamiento al apagar flag con BCs en estados FC1.3 (RH-009 cerrado)

**Regla**: el flag `EnablePartialCreditNotes` controla **CREACION** de nuevos BCs FC1.3 (transicion `Drafted -> RequiresManualReview/ManualReviewPending`). **NO controla procesamiento de BCs ya en estados 8..11**.

**Comportamientos especificos**:

| Estado BC | Flag se apaga | Resultado |
|---|---|---|
| `RequiresManualReview` (transitorio, dura microsegundos) | — | No se observa en BD (commit atomico) |
| `ManualReviewPending` | Apago FC1.3 | BC sigue ahi. Admin puede approve/reject por el endpoint. El callback bridge corre normal. Avanza a `ManualReviewApproved` y luego a `AwaitingFiscalConfirmation` (FC1.2 path normal). |
| `ManualReviewApproved` | Apago FC1.3 | BC sigue ahi. `emitCreditNote()` corre (sigue FC1.2 path). |
| `ManualReviewRejected` | Apago FC1.3 | Auto-reset a Drafted corre normal. |
| `AwaitingFiscalConfirmation` y posteriores | Apago FC1.3 | Sin impacto (ya esta en FC1.2 path). |

**Test explicito**: simular BC en `ManualReviewPending`, apagar flag, ejecutar `approve` -> verificar que transiciona normal a `ManualReviewApproved`.

**Justificacion**: apagar el flag no debe romper BCs en flight. Apagar es "no aceptes mas casos FC1.3", no "cancela todo lo que esta en proceso".

---

## 3. Consecuencias

### 3.1 Positivas

- **NC parcial fiscalmente correcta**: la NC refleja la parte que pierde causa fiscal (criterio contador 2026-05-21), no el monto devuelto.
- **Persistencia minima Fase 1 (GR-004)**: blast radius reducido. Solo 7 columnas nuevas en `BookingCancellations` (vs 17 del round 1). Liquidacion completa solo persistida via `ApprovalRequest.Metadata` cuando hay review humano.
- **Sin BCs colgados (GR-001)**: `TotalPlusNewInvoice` se rechaza en Confirm con error claro. No queda nada huerfano en `ManualReviewApproved` esperando Fase 2.
- **`CommissionOnly` diferido a manual (GR-003)**: sin hipotesis sin validar. Cuando contador responde F2 round 3, se quita el flag via setting + se implementa formula.
- **Caso 3 + penalty + TotalToCustomer diferido (GR-006)**: contradiccion interna del plan funcional NO se resuelve por adivinacion. Va a manual review hasta respuesta F4.
- **Items no reintegrables modelados explicitamente**: `IsRefundable` por item + `ItemCategory` para defaults.
- **Snapshot fiscal completo**: `InvoicingModeAtEvent` evita que un cambio de modo del operador rompa cancelaciones historicas.
- **4-eyes obligatorio** con **escape valvula para agencias chicas (GR-005)**: `Allow4EyesBypassWhenSingleAdmin` permite operacion 1-persona con safeguards.
- **Bandeja existente reusada**: sin codigo UI nuevo Fase 1.
- **Clasificador testeable aislado**: matriz 8 casos cubierta por unit tests sin DB.
- **Compatibilidad backward**: FC1.2 sigue funcionando si flag FC1.3 esta off. Migracion no destructiva.
- **Settings parametrizados**: thresholds + template en `OperationalFinanceSettings`. Cambios sin redeploy.
- **Concurrency token en `ApprovalRequest`** (RH-006): race en edicion admin protegida.
- **Job de reconciliacion bridge** (RH-011): BCs huerfanos recuperados automaticamente.
- **Heuristicas factura confusa OFF por default** (RH-008): solo se activan si contador lo pide explicitamente.

### 3.2 Negativas

- **3 entidades modificadas + 5 enums nuevos + 8 settings nuevos**: complejidad de modelo. Reducida del round 1 (Owned VO `FiscalLiquidation` eliminado).
- **Fase 1 NO emite NC al ARCA real**: si el BC cae en `ManualReviewApproved` con `PartialOnOriginal`, en Fase 1 avanza por FC1.2 path (emite NC total real). La marca de `CreditNoteKind=PartialOnOriginal` queda en BD para que Fase 2 lo levante. Log warning explicito en cada caso.
- **`Supplier.PenaltyPolicyJson` como columna `jsonb`**: queryable nativamente con `JSON_VALUE`-like. Convencion repo.
- **Hipotesis "penalty reduce NC en TotalToCustomer" pendiente confirmacion** (F4): si contador responde lo contrario, formula en STEP 3 cambia. Cambio aislado.
- **CommissionOnly bloquea operativa hasta respuesta F2 round 3**: si la mayoria de operadores son CommissionOnly, FC1.3 efectivamente solo opera para los `TotalToCustomer`. Mitigacion: contador prioriza respuesta F2.
- **Heuristicas factura confusa OFF**: si manana legacy data rompe el clasificador, hay falsos negativos. Mitigacion: checkbox manual del vendedor sigue activo.

### 3.3 Neutras / a futuro

- **Persistencia entera `FiscalLiquidation`**: Fase 2 con backfill desde `ApprovalRequest.Metadata`.
- **Prorrateo IVA proporcional al neto**: Fase 2 con contador.
- **Multimoneda en NC parcial**: flagged como `MultiCurrency` reason que dispara manual review Fase 1. Fase 2 implementa.
- **Rol contador real (G5)**: hoy admin con comentario reforzado. Cuando exista rol nuevo, se agrega permiso especifico.

---

## 4. Alternativas consideradas

| Alternativa | Por que NO |
|---|---|
| **Persistir `FiscalLiquidation` como Owned VO Fase 1** | GR-004: blast radius reducido. Fase 2 agrega cuando AfipService emita real. Round 1 lo proponia, reviewer marco riesgo de divergencia (RH-004). |
| **Auto-procesar `TotalPlusNewInvoice` Fase 1 con BC en `ManualReviewApproved` parado** | GR-001: deja BCs colgados sin avance. Reviewer marco RH-005. Mejor rechazar Confirm. |
| **Hipotesis `CommissionOnly + penalty no reduce`** sin validar | GR-003 + RH-015: sin confirmacion contador. Fase 1 manda a manual. |
| **`Supplier.PenaltyPolicyJson` como `text`** | RH-014: `jsonb` valida sintaxis + soporta CHECK `jsonb_typeof`. |
| **`MutationContext.HasOverrideForInvariant`** (round 1) | RH-003: ADR-001 esta rechazado, ese patron NO existe. Patron real es `ApprovalRequest` tipo `InvariantOverride=7`. |
| **`ApprovalRequest` sin concurrency token** | RH-006: race en edicion admin. Migracion M0 pre-requisito agrega `xmin`. |
| **Una sola migracion FC1.3.0 con todos los cambios** | RH-007: 5 migraciones agrupadas por aggregate (patron FC1.2 v3). El argumento "ruido en `__EFMigrationsHistory`" no era valido. |
| **Heuristicas factura confusa activas por default** | RH-008: alto riesgo falsos positivos. Defaults vacios. Activar solo si contador lo pide + test contra 100 facturas legacy reales (< 5% falsos positivos). |
| **4-eyes estricto sin escape valvula** | RH-010: bloquea agencias 1-persona. GR-005: `Allow4EyesBypassWhenSingleAdmin` setting opt-in. |
| **Sin job de reconciliacion bridge** | RH-011: BCs huerfanos. FC1.3.6b agrega job + endpoint force-callback. |
| **`Fc13DeployDate` set manual** | RH-013: human error. Migracion M3 setea automaticamente. Startup validation log + auto-set si flag on + null. |
| **`FiscalLiquidation` como entidad propia con tabla nueva** | YAGNI Fase 1. Si en Fase 2 hace falta, se agrega entidad propia con backfill desde `ApprovalRequest.Metadata`. |
| **Tabla `SupplierPenaltyTier` normalizada** | Sin caso de uso queries cross-supplier. `jsonb` acomoda evolucion schema. |
| **Logica clasificador dentro de `BookingCancellationService.ConfirmAsync`** | Metodo ya largo + testeo obliga TestContainers Postgres. Service aislado es unit-test puro. |
| **Tabla nueva `PartialCreditNoteApprovalDetails`** | `ApprovalRequest.Metadata` ya tipado JSON. Overkill. |
| **Bandeja UI nueva separada** | G2 cerrada Gaston: reusar bandeja existente. |
| **Reusar `EnableNewCancellationFlow` para FC1.3** | Acopla rollback. Flag separado preserva granularidad. |
| **Sin enum `CreditNoteKind`, inferir** | Casos 4/7 vs 2/6 tienen mismo monto. Enum explicito necesario. |
| **ND complementaria para cliente RI** | G4 cerrada Gaston: NO. |
| **Persistir matriz 8 en BD como tabla `PartialCreditNoteCaseRule`** | YAGNI. Reglas son codigo. |
| **Bloquear cambio de `Supplier.InvoicingMode`** | Operativamente molesto. Snapshot resuelve. |

---

## 5. Migration plan / rollback

### 5.1 Migraciones EF (orden + dependencias)

**5 migraciones agrupadas por aggregate (RH-007)** + **1 pre-requisito M0 (RH-006)**. Cada una aditiva no destructiva (columnas nullable, sin DROP). Orden sequential. Dependencias explicitas.

| # | Nombre | Cambios | Depende de |
|---|---|---|---|
| **M0** (pre-requisito FC1.3) | `FC1_3_PRE_AddApprovalRequestConcurrencyToken` | Agregar `xmin` shadow + `entity.UseXminAsConcurrencyToken()` a `ApprovalRequests`. Resuelve RH-006 race en edicion admin. **Se mergea ANTES de FC1.3 mainline**, idealmente como hotfix post FC1.2. | FC1 (existente) |
| **M1** | `FC1_3_1_AddSupplierInvoicingModeAndPenaltyPolicy` | `Supplier.InvoicingMode` (`integer NOT NULL DEFAULT 0`) + `Supplier.PenaltyPolicyJson` (`jsonb NULL`) + CHECK `chk_Suppliers_penaltypolicy_object`. | M0 |
| **M2** | `FC1_3_2_AddInvoiceItemRefundabilityAndCategory` | `InvoiceItem.IsRefundable` (`boolean NOT NULL DEFAULT true`) + `InvoiceItem.ItemCategory` (`integer NOT NULL DEFAULT 0`) + `InvoiceItem.SourceServicioReservaId` (`integer NULL` FK `ServiciosReserva.Id`, `OnDelete: SetNull`) + index. | M0 |
| **M3** | `FC1_3_3_AddOperationalFinanceSettingsThresholdsAndTemplate` | 8 columnas nuevas en `OperationalFinanceSettings`: `EnablePartialCreditNotes`, `PartialNcAutoApprovalThreshold`, `PartialNcAdminReviewThreshold`, `PartialNcAccountingReviewThreshold`, `PartialNcDescriptionTemplate`, `ManualReviewMaxDaysBeforeRg4540Alert`, `Fc13DeployDate`, `GenericDescriptionPatterns`, `Allow4EyesBypassWhenSingleAdmin`. Defaults RH-008/RH-013: `Fc13DeployDate` se setea via `migrationBuilder.Sql("UPDATE \"OperationalFinanceSettings\" SET \"Fc13DeployDate\" = NOW() WHERE \"Fc13DeployDate\" IS NULL AND \"EnablePartialCreditNotes\" = true")`. Para flag off, queda null. | M0 |
| **M4** | `FC1_3_4_AddBcPartialCreditNoteFieldsAndFiscalSnapshotExtras` | `BookingCancellation`: 7 columnas (`CreditNoteKind int?`, `ReviewRequiredReason int NOT NULL DEFAULT 0`, `LiquidationComputedAt timestamptz?`, `LiquidationComputedByUserId varchar(450)?`, `LiquidationComputedByUserName varchar(200)?`, `PartialCreditNoteApprovalRequestId int? FK ApprovalRequests.Id OnDelete:Restrict`, `ManualReviewerUserId/UserName/At/Comment`). Extension de `FiscalSnapshot_InvoicingModeAtEvent` + `FiscalSnapshot_OriginalInvoiceTypeAtEvent` (parte del mismo owned config). CHECK constraints `chk_BookingCancellations_manualreview_approvalref` + `chk_BookingCancellations_creditnotekind_consistent`. Extension de CHECK `Status` para 0..11. | M3 |
| **M5** | `FC1_3_5_AddHotelBookingNonRefundableConcepts` | `HotelBooking.NonRefundableConceptsJson` (`jsonb NULL`). | M0 |

**Nota sobre `ApprovalRequestType` enum**: agregar `PartialCreditNoteApproval=11` **no requiere migracion** (enums se almacenan como int). El seeding de `ApprovalRequestPolicy` con defaults para este tipo se agrega en M3 o M4.

**Nota sobre `BookingCancellationStatus` enum**: agregar valores 8..11 **no requiere migracion** salvo CHECK constraint que enumere valores validos. Verificar en M4 si el CHECK actual lista valores 0..7 y extender a 0..11.

**Auto-set `Fc13DeployDate` (RH-013)**:
- En migracion M3, si `EnablePartialCreditNotes=true` ya estaba en true (caso raro de re-merge), setea `Fc13DeployDate = NOW()`.
- En startup, si `EnablePartialCreditNotes=true && Fc13DeployDate IS NULL`, log warning + UPDATE automatico a `NOW()`.
- Para activar FC1.3 limpio: admin actualiza `EnablePartialCreditNotes=true` via panel admin -> al guardar settings, service detecta cambio y setea `Fc13DeployDate=NOW()` si null.

### 5.2 Backfill datos legacy

- `Supplier.InvoicingMode = TotalToCustomer` para todos los existentes (default conservador).
- `Supplier.PenaltyPolicyJson = NULL` para todos los existentes.
- `InvoiceItem.IsRefundable = true` para todos los existentes. Confirmar con contador que esto no genera deuda fiscal — si lo hace, se ofrece script de revision manual.
- `InvoiceItem.ItemCategory = Service` para todos los existentes.
- `InvoiceItem.SourceServicioReservaId = NULL` para todos los existentes.
- `BookingCancellation.CreditNoteKind = NULL`, `ReviewRequiredReason = 0`, `LiquidationComputedAt = NULL` para BCs preexistentes (FC1.2).
- `OperationalFinanceSettings.Fc13DeployDate = NULL` por default (heuristica legacy OFF).
- `OperationalFinanceSettings.GenericDescriptionPatterns = ""` por default (heuristica caso 4 OFF).
- `OperationalFinanceSettings.Allow4EyesBypassWhenSingleAdmin = false` por default (4-eyes estricto).
- `ApprovalRequest.xmin` se inicializa automaticamente por Postgres (transaction id de la primera tupla).

### 5.3 Rollback

- **Camino primario (granular)**: apagar `EnablePartialCreditNotes`. Si FC1.3 explota, FC1.2 sigue funcional. BCs en estados 8..11 quedan en limbo hasta intervencion manual.
- **Camino secundario (apagar FC1.2 tambien)**: si OPS-FISCAL-001 se revierte, apagar `EnableNewCancellationFlow` apaga FC1.2. Pre-condicion GR-002 exige apagar FC1.3 antes (en proximo arranque rechaza si FC1.3 quedo on).
- **Camino terciario (reverse migraciones)**: reverse por orden inverso (M5 -> M4 -> M3 -> M2 -> M1 -> M0). Como todas son aditivas con columnas nullable, reverse no pierde datos. Antes de reverse M4: script "BCs en estados 8..11 != 0 -> bloquear reverse + alerta admin para mover a Drafted/Aborted". Tiempo estimado: < 5 min en BD prod.
- **Rollback de M0** (concurrency token `ApprovalRequest`): inocuo. Quitar `UseXminAsConcurrencyToken()` no afecta datos. Pero pierde proteccion race. Si FC1.3 vuelve a prenderse, re-aplicar M0.

---

## 6. Testing strategy

### 6.1 Tests unit (clasificador) — sin DB

`IFiscalLiquidationCalculator` tests cubren la matriz 8 + edge cases. Sin TestContainers, sin DbContext. Ejecutan en milisegundos.

| Test | Caso esperado | Disparadores esperados |
|---|---|---|
| `Calculate_Case1_PartialNoPenalty_BelowThreshold_ReturnsAutoApprovable` | Case1 | None |
| `Calculate_Case2_FullCancellationNoRetention_BelowThreshold_ReturnsAutoApprovable` | Case2 | None |
| `Calculate_Case3_FullWithPenalty_TotalToCustomer_FlagsPenaltyResetUncertain` | Case3 | PenaltyResetUncertainInResellerMode (GR-006) |
| `Calculate_Case3_FullWithPenalty_AboveAdminThreshold_ReturnsRequiresReview` | Case3 | AmountAboveAdminThreshold + PenaltyResetUncertain |
| `Calculate_Case4_GenericDescriptionMatchesPattern_WhenSettingEnabled` | Case4 | OriginalInvoiceUnclear (solo si setting NO vacio) |
| `Calculate_Case4_GenericDescriptionMatchesPattern_WhenSettingDisabled_Default` | Case1/2 | None (RH-008: setting default vacio NO dispara) |
| `Calculate_Case4_ManualCheckboxOriginalInvoiceUnclear_AlwaysFlags` | Case4 | OriginalInvoiceUnclear |
| `Calculate_Case5_CommissionOnly_PartialReturn_FlagsCommissionOnly` | Case5 | InvoicingModeCommissionOnly (GR-003) |
| `Calculate_Case6_CommissionOnly_FullReturn_FlagsCommissionOnly` | Case6 | InvoicingModeCommissionOnly (GR-003) |
| `Calculate_Case7_InvoicingModeChanged_ReturnsRequiresReview` | Case7 | RetentionChangesNature |
| `Calculate_Case8_FacturaA_AnyAmount_ReturnsRequiresReview` | Case8 | CustomerIsRiOrFacturaA |
| `Calculate_FacturaA_PlusNonRefundable_ReturnsTwoFlags` | Case8 | CustomerIsRiOrFacturaA \| HasNonRefundableItems |
| `Calculate_LegacyInvoice_WhenSettingEnabled_Flags` | (algun caso) | LegacyInvoice (solo si Fc13DeployDate NO null) |
| `Calculate_LegacyInvoice_WhenSettingDisabled_Default_DoesNotFlag` | (algun caso) | sin LegacyInvoice (RH-008) |
| `Calculate_MultiCurrency_ReturnsRequiresReview` | (algun caso) | MultiCurrency |
| `Calculate_SumValidation_BreaksTolerance_ThrowsInvariantViolation` | — | INV-FC1.3-005 violado |
| `Calculate_CommissionOnly_EarlyExit_NoComputationOfPenaltyFormula` | Case5/6 | (verifica GR-003 short-circuit) |
| `Calculate_AccountingThresholdNull_DoesNotFlagAccountingReview` | — | sin flag accounting |
| `Calculate_AmountAboveAccountingThreshold_FlagsAccountingReview` | — | AmountAboveAccountingThreshold |
| `Calculate_ExplanationContainsCaseName_AndReasonFlags` | — | (output narrativo) |
| `Calculate_TotalToCustomer_NoPenalty_NoFlagPenaltyResetUncertain` | Case1/2 | sin PenaltyResetUncertain |
| `Calculate_GenericDescriptionPatternsMalformedRegex_FallsBackGracefully` | (algun caso) | sin OriginalInvoiceUnclear + log warning |
| `Calculate_SupplierPenaltyPolicyJsonMalformed_FallsBackToManualInput` | (algun caso) | log warning + usa input.OperatorPenaltyAmount |
| `Calculate_FacturaA_PlusCommissionOnly_BothFlagsSet` | Case8 | CustomerIsRiOrFacturaA \| InvoicingModeCommissionOnly |

Total estimado: ~24 unit tests. Pattern xUnit + FluentAssertions.

### 6.2 Tests integration (BC service + ApprovalRequest) — con TestContainers Postgres

Ubicacion: `src/TravelApi.Tests/Cancellation/Integration/`. Usa `PostgresIntegrationFixture` existente (NO renombrar — RH-018).

| Test | Setup | Assert |
|---|---|---|
| `Confirm_CaseAutoApprovable_TransitionsToAwaitingFiscalConfirmation` | Reserva Hotel + factura $300k Tipo C + sin items no refundable + sin penalidad + InvoicingMode=TotalToCustomer | BC.Status = AwaitingFiscalConfirmation, BC.CreditNoteKind = PartialOnOriginal, BC.ReviewRequiredReason = None, ApprovalRequest no creado |
| `Confirm_AboveThreshold_TransitionsToManualReviewPending` | Reserva + factura $600k Tipo C | BC.Status = ManualReviewPending, ApprovalRequest tipo PartialCreditNoteApproval Pending con Metadata JSON |
| `Confirm_FacturaA_AnyAmount_TransitionsToManualReviewPending` | Factura A $200k + cliente RI | BC.Status = ManualReviewPending, ReviewRequiredReason flag CustomerIsRiOrFacturaA |
| `Confirm_TotalPlusNewInvoice_RejectsBeforePersistingFC13Fields` (GR-001) | Reserva Hotel + heuristica caso 4 activa + factura confusa | InvalidOperationException con mensaje "Caso fiscal requiere FC1.3 Fase 2". BC NO se persiste con datos FC1.3. BC queda en Drafted o NO se crea. |
| `Confirm_CommissionOnlySupplier_GoesDirectlyToManualReview` (GR-003) | Supplier.InvoicingMode = CommissionOnly | BC.Status = ManualReviewPending, ReviewRequiredReason flag InvoicingModeCommissionOnly, calculator early-exit (sin computar formula) |
| `Confirm_TotalToCustomerPlusPenalty_GoesDirectlyToManualReview` (GR-006) | TotalToCustomer + OperatorPenaltyAmount > 0 | BC.Status = ManualReviewPending, ReviewRequiredReason flag PenaltyResetUncertainInResellerMode |
| `Confirm_NonRefundableItem_FlagsHasNonRefundableItems` | InvoiceItem IsRefundable=false por $50k | BC.ReviewRequiredReason flag HasNonRefundableItems |
| `Confirm_MixedServices_RejectsByInvariant007` | Reserva con Hotel + Vuelo | BusinessInvariantViolationException codigo INV-FC1.3-007 |
| `Confirm_MixedServices_WithInvariantOverride_Allows` | Reserva mixta + ApprovalRequest tipo InvariantOverride=7 Approved con justificacion 60 chars | BC.Status = ManualReviewPending (continua flujo) |
| `EditLiquidation_ByDifferentAdmin_UpdatesMetadataAndAuditTrail` | BC en ManualReviewPending + admin distinto al vendedor | ApprovalRequest.Metadata.edits[] tiene entrada nueva + AuditLog BookingCancellationLiquidationEdited con Changes JSON con diff |
| `EditLiquidation_BySameUserAsVendor_Rejects4Eyes` | BC en ManualReviewPending + admin = vendedor + Allow4EyesBypass=false | BusinessInvariantViolationException INV-FC1.3-004 |
| `EditLiquidation_BySameUserAsVendor_With4EyesBypassAndSingleAdmin_Allows` (GR-005) | Setting Allow4EyesBypass=true + 1 solo usuario admin + admin=vendedor + comentario 110 chars | Permite edicion + AuditLog tiene `SelfApprovedDueToSingleAdmin=true` en JSON |
| `EditLiquidation_With4EyesBypassButTwoAdmins_StillRequires4Eyes` (GR-005) | Setting Allow4EyesBypass=true + 2 usuarios admin + vendedor=admin1 + intenta self-approve | BusinessInvariantViolationException INV-FC1.3-004 |
| `EditLiquidation_ConcurrentEdit_xminConflict_ThrowsConcurrencyException` (RH-006) | 2 admins editan el mismo approval en paralelo | DbUpdateConcurrencyException en el segundo |
| `ApproveLiquidation_TransitionsToManualReviewApproved` | BC en ManualReviewPending + admin distinto + comment >= 20 chars | BC.Status = ManualReviewApproved, ApprovalRequest.Status = Approved |
| `ApproveLiquidation_AboveAccountingThreshold_RequiresCommentMin100Chars` (G5) | BC con AmountAboveAccountingThreshold | Comment 50 chars rechaza, 100 chars OK |
| `RejectLiquidation_TransitionsToManualReviewRejectedThenDrafted` | BC en ManualReviewPending + reject | BC.Status = Drafted (auto reset), CreditNoteKind=null, ReviewRequiredReason=0, LiquidationComputed*=null, PartialCreditNoteApprovalRequestId=null |
| `EmitCreditNote_PartialOnOriginal_TransitionsToAwaitingFiscalConfirmation_Fase1` | BC en ManualReviewApproved con CreditNoteKind=PartialOnOriginal | BC.Status = AwaitingFiscalConfirmation (Fase 1: FC1.2 path con NC total real). Log warning "Fase 1 emite total — Fase 2 emite parcial" |
| `Confirm_FeatureFlagOff_IgnoresPartialCreditNoteLogic` | EnablePartialCreditNotes=false + reserva que normalmente caeria a manual review | BC.Status = AwaitingFiscalConfirmation (calculator no corre) |
| `Confirm_Fc12FeatureFlagOff_RejectsWithKillSwitch` | EnableNewCancellationFlow=false | InvalidOperationException "modulo de cancelacion/refund no esta habilitado" (kill switch FC1.2) |
| `Startup_Fc13OnWithoutFc12_RejectsWithError` (GR-002) | EnablePartialCreditNotes=true + EnableNewCancellationFlow=false | App falla al arrancar con mensaje claro pre-condicion violada |
| `Confirm_FlagToggledOffMidFlight_BCsInManualReviewStillProcessNormally` (RH-009) | BC en ManualReviewPending + admin apaga flag + admin approve | BC transiciona normal a ManualReviewApproved (no se bloquea) |
| `Abort_FromManualReviewPending_TransitionsToAborted` | BC en ManualReviewPending + abort() | BC.Status = Aborted, ApprovalRequest queda como esta (NO se borra), audit logged |
| `BridgeReconciliationJob_OrphanBCInManualReviewPendingWithApprovedAR_ForcesCallback` (RH-011) | BC stuck en ManualReviewPending + AR Approved hace > 30 min | Job detecta + invoca OnApprovedAsync + BC transiciona a ManualReviewApproved |
| `BridgeReconciliationJob_NotStaleEnough_DoesNotForceCallback` | AR Approved hace 10 min | Job no actua |
| `InvariantOverride_ForInv007_RequiresJustificationDistinctFromBCComment` (RH-016) | ApprovalRequest InvariantOverride con `Reason` igual al `ManualReviewComment` futuro | Service rechaza al confirmar override |
| `Confirm_HeuristicasFacturaConfusaPorDefaultOff_NoFalsosPositivos` (RH-008) | 100 facturas legacy seed (datos reales anonimizados) + setting GenericDescriptionPatterns="" + Fc13DeployDate=null | < 5% se marcan OriginalInvoiceUnclear (idealmente 0%) |

Total estimado: ~27 integration tests.

### 6.3 Tests E2E (happy paths)

| Test | Flujo |
|---|---|
| `E2E_PartialCreditNote_Case8_FullHappyPath` | Reserva Hotel + factura A $1M -> Draft -> Confirm -> ManualReviewPending -> Admin Approve -> ManualReviewApproved -> EmitCreditNote -> AwaitingFiscalConfirmation -> ... resto FC1.2 |
| `E2E_PartialCreditNote_Case5_CommissionOnly_AdminAcceptsAndProcessesManually` (GR-003) | Reserva Hotel + Supplier CommissionOnly -> Draft -> Confirm -> ManualReviewPending -> Admin Edit con monto manual -> Admin Approve -> AwaitingFiscalConfirmation |
| `E2E_PartialCreditNote_Case4_TotalPlusNewInvoice_RejectsWithExplicitError` (GR-001) | Reserva Hotel + factura confusa con heuristica activa + setting activado -> Draft -> Confirm -> InvalidOperationException con mensaje claro. BC queda en Drafted. |

### 6.4 Performance baseline

- Calculator: target < 10ms por invocacion (logica pura, sin IO).
- BC `ConfirmAsync` con clasificador: target < 50ms (incluye load Reserva + Invoice + Items + Supplier). Sin regresion sobre FC1.2.

### 6.5 Migration tests

- Aplicar M0..M5 contra dump anonimizado de staging.
- Verificar que BCs preexistentes (FC1.2) **no cambian estado** post-migracion.
- Verificar `SELECT COUNT(*) FROM "BookingCancellations" WHERE "CreditNoteKind" IS NOT NULL` = 0 post-migracion.
- Rollback M5..M0, verificar que dump original es restituido sin perdida.
- Verificar que `ApprovalRequests` post-M0 tienen `xmin` no-null en todas las filas.

---

## 7. Operational risks

| Riesgo | Mitigacion |
|---|---|
| **Heuristicas caso 4 producen falsos positivos masivos** | Defaults OFF (RH-008). Admin habilita explicitamente solo si contador lo pide. Setting ajustable sin redeploy. Test seed con 100 facturas legacy reales antes de habilitar. |
| **Operador cambia `InvoicingMode` durante cancelacion en vuelo** | Snapshot `InvoicingModeAtEvent` capturado en T0. Calculator usa snapshot. |
| **Plazo RG 4540 (15 dias) se vence con BC en `ManualReviewPending`** | Alerta a partir de `ManualReviewMaxDaysBeforeRg4540Alert` dias (default 10). Job nocturno notifica. **No auto-aprueba**. |
| **Threshold $500k/$2M no son los del contador** | Default editable en `OperationalFinanceSettings` sin redeploy. |
| **Items legacy con `IsRefundable=true` por backfill, y eran no reintegrables fiscalmente** | Reporte separado al contador con `InvoiceItem.ItemCategory = AdministrativeFee/Insurance/OperatorAdvance` que tienen `IsRefundable=true`. Decision contable manual. |
| **Calculator clasifica mal por bug** | Tests unit cubren matriz 8 + edges. Si pasa a prod con bug, flag off -> rollback inmediato. |
| **Admin edita liquidacion + aprueba sin firmar contador** | INV-FC1.3-009 (comment edicion != comment aprobacion) + audit log separado. |
| **Multimoneda cancelacion** | reason flag `MultiCurrency` dispara manual review obligatorio Fase 1. Fase 2 implementa logica completa. |
| **Fase 1 marca `PartialOnOriginal` pero AfipService emite NC total** | Comportamiento esperado Fase 1. Log warning explicito en cada Approve. Fase 2 emite parcial real. |
| **Callback bridge falla (RH-011)** | Job de reconciliacion FC1.3.6b + endpoint admin force-callback. |
| **Race condition en edicion admin (RH-006)** | `ApprovalRequest.xmin` via M0 protege. Segundo admin con xmin viejo recibe DbUpdateConcurrencyException + reintenta. |
| **Combinacion invalida de flags (FC1.3 ON + FC1.2 OFF)** | Pre-condicion startup (GR-002) rechaza arranque con error claro. |
| **CommissionOnly mode bloquea operativa** (GR-003) | Manual review obligatorio. Mitigacion: priorizar respuesta contador F2. Admin puede ingresar liquidacion a mano. |
| **Penalty + TotalToCustomer en caso 3 indeterminado** (GR-006) | Flag `PenaltyResetUncertainInResellerMode` dispara manual review. Hipotesis conservadora "penalty resta" persiste en Metadata para auditoria pero requiere confirmacion humana. |

---

## 8. Security / audit risks

- **4-eyes obligatorio**: INV-FC1.3-004 + escape valvula controlada (GR-005).
- **GR-005 self-approval auditable**: cuando aplica, `Metadata.selfApprovedDueToSingleAdmin=true` + comentario 100+ chars + `AuditLog.Changes` con justificacion. Auditor puede filtrar.
- **Cross-reference auditable**: `BC.PartialCreditNoteApprovalRequestId` + `ApprovalRequest.Metadata.edits[]` + `AuditLog`.
- **Permisos**: reusar `cobranzas.invoice_annul` para Confirm + `approvals.review` para aprobacion. **No se crea permiso nuevo Fase 1.**
- **Audit log con diff completo (RH-012)**: `AuditLog.Changes` JSON con shape `{"Field": {"Old":"...", "New":"..."}}` por cada campo editado.
- **`CreditNoteKind` immutability post-Approved**: INV-FC1.3-008.
- **Datos FC1.3 quedan en BD aunque BC se aborte**: para audit historico. Solo se nulean en `resetToDraft()` (post-Rejection).
- **`InvariantOverride` para INV-FC1.3-007 requiere justificacion distinta** (RH-016): evita reusar el comment del BC como justificacion del override.

---

## 9. Open questions (no bloquean Fase 1, bloquean Fase 2/3)

### 9.1 Fiscales (round 3 al contador — mensaje en `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md`)

- **F1**: threshold $500k/$2M es estricto menor o menor igual? Es el valor adecuado para MagnaTravel?
- **F2**: en modo `CommissionOnly`, como se calcula `FiscalAmountToCredit`? (GR-003 dispara manual hasta respuesta.)
- **F3**: si NC parcial ya emitida necesita correccion, ND complementaria o anular + nueva NC parcial?
- **F4**: en modo `TotalToCustomer` + penalty operador > 0, la penalty resta del monto fiscal acreditable o no? (GR-006 dispara manual hasta respuesta.)

### 9.2 De negocio / G a Gaston (resueltas 2026-05-21 + GR-001..GR-006)

- G1..G6 + GR-001..GR-006 cerradas.

### 9.3 Plan tactico (al architect-reviewer round 2)

- ¿Deberiamos persistir tambien `OriginalInvoiceAmount` Fase 1 (un solo campo) para queries de reporting basicos sin abrir Metadata JSON?
- ¿El job de reconciliacion bridge deberia tener configurable el threshold de 30 min (actualmente hardcoded)?
- ¿El endpoint admin force-callback necesita auditoria reforzada extra (ej. tipo `ApprovalRequest.InvariantOverride`)?

---

## 10. Auto-critica explicita (transparencia)

### 10.0 Confesion explicita (round 2)

**Round 1 use sintaxis SQL Server por error en el brief de la sesion principal.** La sesion principal me indico "Stack es SQL Server, NO Postgres" — esto era falso. El repo usa PostgreSQL (Npgsql 8.x) con `UseXminAsConcurrencyToken` (verificado en `AppDbContext.cs:1180`), `PostgresIntegrationFixture` (verificado), migracion FC1 con `using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata` + `xmin` shadow + tipos `numeric/jsonb/text/timestamptz` + identificadores con comillas dobles. **Round 2 corregido a Postgres en TODA seccion SQL/EF**.

**Round 1 tambien tuvo 6 decisiones de Gaston nuevas (GR-001..GR-006)** que cambiaron alcance:
- **GR-001**: `TotalPlusNewInvoice` ahora se RECHAZA en Confirm. Round 1 lo dejaba colgado en `ManualReviewApproved`.
- **GR-002**: FC1.3 depende de FC1.2 ON. Pre-condicion startup explicita.
- **GR-003**: `CommissionOnly` ahora va directo a manual review. Round 1 tenia hipotesis sin validar.
- **GR-004**: NO persistir `FiscalLiquidation` entera Fase 1. Eliminado Owned VO completo. Reduce blast radius y elimina riesgo divergencia.
- **GR-005**: `Allow4EyesBypassWhenSingleAdmin` para agencias chicas.
- **GR-006**: caso 3 + penalty + TotalToCustomer diferido a manual hasta respuesta F4 contador.

Y los 13 hallazgos del reviewer (RH-001..RH-022, prefijo RH para distinguir de Reviewer Hallazgo) cerrados en §11 abajo.

### 10.1 Self-review estructurado

1. **Verifique todo en el repo?** Si — verifique stack Postgres (5 fuentes), `ApprovalRequest` actual (no tiene xmin -> M0), `EnsureFeatureFlagOnAsync` (es kill switch real), patron override real (`BookingCancellationService:261-281`), `ServiceTypes.Hotel` constante, `AssignmentServiceType.Hotel`, `AuditLog.Changes` shape, `PostgresErrorCodes.UndefinedTable`/`UniqueViolation` constantes usadas.
2. **Asumi algo sin decirlo?** (a) Que el callback bridge `IInvoiceAnnulmentBcBridge` (FC1.2) es el patron a copiar — verificado. (b) Que el seeding de `ApprovalRequestPolicy` para `PartialCreditNoteApproval=11` se hace en migracion M3/M4 — anotado. (c) Que `migrationBuilder.Sql("UPDATE ... SET Fc13DeployDate = NOW()")` corre dentro de la tx de la migracion — Postgres lo soporta sin issues.
3. **Diseno coupling oculto?** Bridge + job reconciliacion + endpoint force-callback son 3 vias para resolver el mismo problema (callback fallido). Es redundancia INTENCIONAL para alta criticidad fiscal. Documentado en §2.12.
4. **Tests cubiertos?** Unit (24) + Integration (27) + E2E (3). Mejor que round 1 (que tenia 20 + 19 + 2).
5. **Edge case que falta?**
   - NC parcial sobre NC parcial (cadena): flag `Other`, va a manual.
   - Cancelacion con BC ya creado en `ClientCreditApplied` (FC1.2 con NC total) y vendedor quiere "convertir" a NC parcial: Fase 1 NO permite.
   - Operador devuelve menos de lo esperado por la liquidacion: FC1.2 ya tiene `DeductionLine.RequiresAccountingReview`.
   - **GR-005 caveat**: si admin se borra, queda 0 admins -> singleadmin check falla. Mitigacion: settings UI bloquea apagar el ultimo admin.
6. **Riesgo de datos/rollback?** Migraciones aditivas + nullable. M0 pre-requisito separado (no se mezcla con FC1.3). Rollback por inversion. Feature flag default off. Riesgo bajo. Script pre-deploy: BCs en estados 8..11 antes de reverse de M4.
7. **Overcomplicado?** Menos que round 1 (eliminamos Owned VO `FiscalLiquidation`, `TotalPlusNewInvoice` colgado, hipotesis CommissionOnly). Sumamos GR-005 (1 setting + logica condicional) + M0 (pequeno) + job reconciliacion + endpoint force-callback. Net: simplificacion.
8. **Underdesigned?** Bridge + reconciliacion + force-callback OK Fase 1. Falta diseño UI Fase 3 (admin modal, vendedor checkbox `IsRefundable`).
9. **Mantenible por otros?** Si — patron heredado FC1.2. Tests unit del calculator hacen matriz 8 facil de re-leer.
10. **Reviewer podria rechazar?** Puntos discutibles:
    - **No persistir `FiscalLiquidation` entera (GR-004)**: si reviewer prefiere persistir para queries de reporting, agregar `OriginalInvoiceAmount` solo (un campo) es trivial.
    - **Endpoint admin force-callback**: si reviewer prefiere job-only, eliminar endpoint.
    - **GR-005 setting** podria ser per-user en lugar de global, pero global es simpler Fase 1.
    - **Heuristicas caso 4 OFF por default** podria ser mas restrictivo (eliminar heuristicas entero, dejar solo checkbox). Compromiso elegido: mantener codigo + setting OFF + activar si contador pide.

---

## 11. Cierre de hallazgos del reviewer round 1

| Hallazgo | Como se cerro | Seccion del ADR |
|---|---|---|
| **RH-001** Postgres vs SQL Server | Rewrite total con sintaxis Postgres (jsonb, text, timestamptz, comillas dobles, xmin, SqlState=23514). §10.0 confesion explicita. | §1.3, §2.2, §2.3.5, §2.5, §5.1, §10.0 |
| **RH-002** `EnableNewCancellationFlow=OFF` es kill switch | Diagrama §2.8.1 muestra rechazo explicito con `InvalidOperationException` via `EnsureFeatureFlagOnAsync`. Tabla §2.8.3 fila "Drafted FC1.2 off". | §1.3, §2.8.1, §2.8.3 |
| **RH-003** `MutationContext.HasOverrideForInvariant` no existe (ADR-001 rechazado) | §2.11 reescrita con patron real `ApprovalRequest` tipo `InvariantOverride=7`, busqueda por EntityType/EntityId/Status/RequestedByUserId/ExpiresAt, mismo patron que `BookingCancellationService:261-281`. | §2.11 |
| **RH-004** Divergencia `FiscalLiquidation` persistida vs NC emitida | GR-004: NO persistir Fase 1. Solo 5 campos summary. Liquidacion completa solo en `ApprovalRequest.Metadata` cuando hay review. | §2, §2.3.2, §2.4 |
| **RH-005** BCs colgados en `ManualReviewApproved` con `TotalPlusNewInvoice` | GR-001: rechazar Confirm con `InvalidOperationException`. Eliminado path colgado. INV-FC1.3-010 enforza. | §2.8.1, §2.8.3, §2.8.4 (INV-010), §2.9 STEP 7 |
| **RH-006** `ApprovalRequest.Metadata` sin row version | M0 pre-requisito FC1.3 agrega `xmin` shadow + `UseXminAsConcurrencyToken()`. Documentado dependency en §5.1. Test integration cubre. | §1.3, §2.2 punto 10, §5.1 M0, §6.2 test concurrencia |
| **RH-007** Contradiccion 1 migracion vs 5 migraciones | Resuelto: 5 migraciones agrupadas por aggregate + M0 pre-requisito. Patron FC1.2 v3 (4 migraciones). §5.1 actualizado. Plan tactico §T1 fila #5 actualizado. | §5.1, plan tactico T1/T3 |
| **RH-008** Heuristicas factura confusa demasiado agresivas | Defaults: `GenericDescriptionPatterns=""`, `Fc13DeployDate=null`. Heuristica OFF. Test seed con 100 facturas legacy reales antes de habilitar. Activar solo si contador pide. | §2.2 punto 9, §2.3.4 (settings), §2.9 STEP 2, §7 |
| **RH-009** Semantica flag mixto | §2.13 explicita: flag controla CREACION, no procesamiento. Test integration cubre BCs en estados 8..11 cuando flag se apaga. | §2.13, §6.2 test `FlagToggledOffMidFlight` |
| **RH-010** 4-eyes en agencias chicas | GR-005: setting `Allow4EyesBypassWhenSingleAdmin` opt-in con safeguards (comentario 100+ chars + flag audit + check de 1 solo admin). | §2.3.4 setting, §2.8.4 INV-004 excepcion, §6.2 tests GR-005 |
| **RH-011** Bridge sin job reconciliacion | §2.12: job `PartialCreditNoteBridgeReconciliationJob` + endpoint admin force-callback. Sub-fase FC1.3.6b en plan tactico. | §2.12, plan tactico FC1.3.6b |
| **RH-012** Audit edicion admin solo JSON metadata | `AuditLog.Changes` con shape `{"Field": {"Old":"...", "New":"..."}}` por cada campo modificado en edicion. Test valida diff completo. | §2.7, §6.2 test `EditLiquidation` |
| **RH-013** `Fc13DeployDate` set manual = bug | Migracion M3 auto-set si `EnablePartialCreditNotes=true`. Startup validation: si flag on + null, log warning + UPDATE auto. Settings UI al guardar tambien setea. | §2.3.4 setting, §5.1 M3 |
| **RH-014** `Supplier.PenaltyPolicyJson` sin validacion schema | Tipo `jsonb` (no `text`). CHECK Postgres `jsonb_typeof = 'object'`. Try/catch en calculator + fallback + log warning. | §2.3.2 Supplier, §2.3.5 CHECK, §2.5 |
| **RH-015** Hipotesis CommissionOnly sin validar | GR-003: diferir a manual review obligatorio Fase 1. Calculator early-exit. Quitar disparador via setting cuando contador responde F2. | §2.9 STEP 0, §2.2, §6.1 tests, §6.2 tests |
| **RH-016** INV-FC1.3-007 override requiere comentario distinto | §2.11 + §2.8.4 INV-FC1.3-007: justificacion del `InvariantOverride` >= 50 chars + DISTINTA del `ManualReviewComment` del BC. Validacion en service. | §2.8.4 INV-007, §2.11 |
| **RH-017** Naming `Servicios`/`ProductType` literal vs constante | `ServiceTypes.Hotel = "Hotel"` constante (verificado `ServicioReserva.cs:8`). §2.11 usa constante via `string.Equals(..., ServiceTypes.Hotel, StringComparison.OrdinalIgnoreCase)`. | §1.3, §2.11 |
| **RH-018** Mencion al rename `PostgresIntegrationFixture` | Eliminada. El fixture es Npgsql vigente, queda como esta. §6.2 lo aclara. | §6.2 |
| **RH-020** Naming `ResolverNotes` vs "comment >= 20 chars" | Usar `ApprovalRequest.ResolverNotes` (max 1000 chars). FluentValidation min length cuando `RequestType == PartialCreditNoteApproval`. | §2.8.3 tabla transiciones, §2.7 |
| **RH-021** `GenericDescriptionPatterns` typing | Una unica regex con alternativas separadas por `|` (no multilinea). Default vacio `""`. | §2.3.4 setting, §2.9 STEP 2 |
| **RH-022** `FiscalLiquidationDto` ubicacion | GR-004 elimina persistencia. DTO se ubica en `src/TravelApi.Application/DTOs/Cancellation/FiscalLiquidationDto.cs` y se usa en FC1.3.1 (calculator). | §2.3.1 DTO, plan tactico FC1.3.1 |

---

## 12. Trazabilidad

- **Plan funcional FC1.3**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-3.md` §1..§16 (autoria `travel-agency-accountant-argentina` 2026-05-21).
- **Doc criterio contador**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md`.
- **Mensaje round 3 al contador** (con F1..F4): `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\operations\2026-05-21-mensaje-contador-fc1-3-round-3.md`.
- **ADR-002 base**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-002-cancellation-refund.md`.
- **Plan FC1.2 v3**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-2.md`.
- **Entidades inspeccionadas** en `src/TravelApi.Domain/Entities/`: `BookingCancellation.cs`, `BookingCancellationStatus.cs`, `Invoice.cs`, `InvoiceItem.cs`, `Supplier.cs`, `FiscalSnapshot.cs`, `HotelBooking.cs`, `OperationalFinanceSettings.cs`, `ApprovalRequest.cs`, `ApprovalRequestType.cs`, `ServicioReserva.cs`, `PassengerServiceAssignment.cs`, `AuditLog.cs`.
- **Service vigente BC**: `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` (lineas 250-310 + 970-987 verificadas como patron real de override y kill switch).
- **AppDbContext** (xmin + Owned VO + interceptor): `src/TravelApi.Infrastructure/Persistence/AppDbContext.cs` (lineas 1140-1210).
- **Migracion FC1** (patron Postgres): `src/TravelApi.Infrastructure/Persistence/Migrations/App/20260514030142_FC1_AddCancellationModule.cs`.
- **Interceptor**: `src/TravelApi.Infrastructure/Persistence/BusinessInvariantInterceptor.cs` (linea 36: `PgCheckViolationSqlState = "23514"`).
- **Controller approvals**: `src/TravelApi/Controllers/ApprovalRequestsController.cs`.

---

**Fin del ADR-009 round 2.** Cierra 13 hallazgos reviewer + 6 decisiones Gaston + rewrite Postgres total. Listo para `software-architect-reviewer` round 2.
