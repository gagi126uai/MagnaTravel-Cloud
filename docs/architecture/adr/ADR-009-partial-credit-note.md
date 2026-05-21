# ADR-009 — Nota de Credito parcial fiscal para cancelaciones Hotel (FC1.3 Fase 1)

- **Status**: Proposed (depende de FC1.2 mergeado, OPS-FISCAL-001 firmado, y signoff Fase 1 de Gaston)
- **Date**: 2026-05-21
- **Author(s)**: software-architect agent (Fase 1, post plan funcional del subagente integrado `travel-agency-accountant-argentina`)
- **Supersedes**: §2.2 punto 1 de [ADR-002](ADR-002-cancellation-refund.md) ("NC siempre por total facturado"). FC1.3 acepta NC parcial cuando el criterio fiscal lo amerita.
- **Related**:
  - [ADR-002 Cancelacion / Refund](ADR-002-cancellation-refund.md)
  - [ADR-001 Domain Invariants](ADR-001-domain-invariants.md)
  - Plan funcional FC1.3 ([plan-tactico-fc1-3.md](../plan-tactico-fc1-3.md) §1..§16) — autoria `travel-agency-accountant-argentina` 2026-05-21
  - Plan tactico FC1.2 v3 ([plan-tactico-fc1-2.md](../plan-tactico-fc1-2.md))
  - Doc criterio contador [2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md](../../explicaciones/2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md)

---

## 1. Contexto

### 1.1 Que cambia el contador respecto de FC1.2

ADR-002 (vigente, FC1.2 mergeado tecnico al 100%) cerro la decision "NC siempre por total facturado" como verdad fiscal del modulo. El **2026-05-21** el contador de MagnaTravel actualizo el criterio:

> "La NC refleja la parte del comprobante que pierde causa fiscal o comercial, NO el monto devuelto."

Entrego una **matriz de 8 casos** (cubre Factura B/C/A, modo reseller vs intermediario, con/sin penalidad, con/sin items no reintegrables, factura confusa) + **2 escenarios concretos** + **7 excepciones**. El nuevo agente `travel-agency-accountant-argentina` produjo el plan funcional cubriendo el QUE fiscal/contable/negocio. Este ADR resuelve el COMO tecnico.

### 1.2 Ejemplo pelotudo (kiosco)

Imaginate el kiosco. Cliente paga `$1000` por 4 milanesas + `$50` por envoltorio regalo (no devuelve nunca). Se arrepiente y se lleva 3. La caja vieja (FC1.2) anulaba TODA la cuenta y empezaba de nuevo: poco prolijo, pero funcionaba. La caja nueva (FC1.3) **arranca una NC parcial por las dos cosas**: lo que pierde causa fiscal (1 milanesa = `$250`) + el envoltorio regalo es item no reintegrable (no entra en la NC porque la agencia ya lo facturo como ingreso). La factura sigue viva por `$800` (3 milanesas) + el envoltorio `$50` queda como `FinalNetInvoiced` de la operacion.

El sistema viejo escribia "ANULADO" en la factura entera y emitia una nueva por `$800` (lo de FC1.2). El sistema nuevo emite **una NC por `$250`** y la factura original queda intacta. Cuaderno limpio.

### 1.3 Hechos verificados en el repo (relevantes para el diseno)

Confirmados leyendo entidades y services:

- `Invoice.OriginalInvoiceId` self-reference + `<CbtesAsoc>` ya emitidos por `AfipService.cs:827-838`. **El plumbing fiscal de vinculo NC<->factura ya esta**. Lo que falta es persistir el monto fiscal calculado, no la NC en si.
- `InvoiceItem` no tiene `IsRefundable` ni `ItemCategory` ni FK al servicio origen. Agregar Fase 1.
- `BookingCancellationStatus` tiene 8 estados (0..7) hoy. **Insertamos 4 estados nuevos antes de `AwaitingFiscalConfirmation` con valores 8..11** para no chocar con valores existentes ni cambiar serializacion previa.
- `ApprovalRequest.Metadata` ya es `string?` JSON arbitrario. **Sirve para persistir liquidacion + edicion admin (G3) sin tabla extra**.
- `ApprovalRequestType` enum llega hasta 10. Agregamos `PartialCreditNoteApproval = 11`.
- `ApprovalRequestsController` acepta cualquier `RequestType` + filtros por user/pending. **Reutilizable, no se crea bandeja paralela** (cierre G2).
- `OperationalFinanceSettings` tiene espacio claro para los nuevos thresholds + template.
- `FiscalSnapshot` es VO owned, persistido como columnas prefijadas. Buen precedente para `FiscalLiquidation`.
- Stack confirmado: **SQL Server** (no Postgres, el plan funcional menciono Postgres por reflejo y se ignora). `UseRowVersionConcurrencyToken` o equivalente, no `xmin`.

### 1.4 Que NO entra en Fase 1

- **Emision real de NC parcial al ARCA**. Hoy `AfipService.cs` emite NC asumiendo monto total. Calcular `<ImpTotal>`/`<ImpNeto>`/`<ImpIVA>` con prorrateo proporcional al neto acreditado entra en **Fase 2**. La Fase 1 calcula `FiscalLiquidation`, persiste el `CreditNoteKind`, abre approval y deja al BC parado en `ManualReviewApproved` cuando hace falta intervencion. La emision real sigue por la ruta FC1.2 vigente (NC por total) mientras el flag `EnablePartialCreditNotes` esta off.
- **UI modal** del admin para revisar/aprobar liquidaciones (Fase 3).
- **Plumbing AfipService** para NC parcial (Fase 2).
- **Servicios distintos a Hotel**. Vuelo/Paquete/Traslado/Asistencia siguen flujo FC1.2 (NC por total). Si la reserva mezcla, se rechaza FC1.3 con mensaje claro.

### 1.5 Decisiones cerradas que NO se pueden cambiar (cierres 2026-05-19/21)

| ID | Decision (contador/Gaston) | Implicancia tecnica |
|---|---|---|
| G1 | Preseleccion auto `IsRefundable=false` para items `Insurance`/`AdministrativeFee`/`OperatorAdvance`. Vendedor puede destildar con confirmacion. | Default logic en creacion de `InvoiceItem`. UI cubre confirmacion (Fase 3). |
| G2 | Reutilizar `ApprovalRequestsController` con nuevo `PartialCreditNoteApproval=11`. No hay bandeja separada. | Solo sumar enum + handler. |
| G3 | Admin edita liquidacion en `ManualReviewPending` con `editLiquidation()` + audit + comentario obligatorio distinto al de aprobacion. | Self-loop `ManualReviewPending`->`ManualReviewPending` con audit. Metadata del approval refleja el cambio. |
| G4 | NO ND complementaria para cliente RI. Factura A original + NC parcial alcanzan. | Sin cambios en `Invoice` para ND. ADR-002 §2.2 punto 2 vigente. |
| G5 | Sin rol nuevo Fase 1. Admin actual aprueba >$2M con comentario min 100 chars + `AccountingReviewRequired=true` en metadata. | Validacion min chars segun threshold. Sin permiso nuevo. |
| G6 | Comision vendedor sobre `FinalNetInvoiced`. | Service de comision usa este campo. Fuera de scope FC1.3 Fase 1 (solo se persiste el valor). |
| Mat | Matriz 8 casos + Escenarios A/B del contador. | Clasificador (matriz 8) implementado en service aislado testeable. |
| Cri | Criterio NC parcial = pierde causa fiscal, NO monto devuelto. | INV-FC1.3-005 + dos campos separados (`FiscalAmountToCredit` vs `AmountToRefundCustomer`). |

---

## 2. Decision

Implementar FC1.3 Fase 1 como **extension aditiva de FC1.2** (no rewrite), con:

1. **Un Owned VO nuevo** `FiscalLiquidation` en `BookingCancellation` (paralelo a `FiscalSnapshot`).
2. **Un service nuevo** `IFiscalLiquidationCalculator` (puro, sin DbContext directo) que implementa la matriz 8 casos como **clasificador aislado y testeable**.
3. **4 estados nuevos** en `BookingCancellationStatus` insertados antes de `AwaitingFiscalConfirmation` (valores 8..11).
4. **Reutilizacion del `ApprovalRequestsController` existente** con tipo nuevo `PartialCreditNoteApproval=11`. `Metadata` JSON captura la liquidacion + edicion admin.
5. **3 modificaciones** a entidades existentes: `Supplier` (InvoicingMode + PenaltyPolicyJson), `InvoiceItem` (IsRefundable + ItemCategory + SourceServicioReservaId), `FiscalSnapshot` (InvoicingModeAtEvent + OriginalInvoiceTypeAtEvent).
6. **Settings nuevos** en `OperationalFinanceSettings` para thresholds y template parametrizable.
7. **Feature flag nuevo** `EnablePartialCreditNotes` (independiente de `EnableNewCancellationFlow`), default `false` en prod, separado para poder mergear sin romper FC1.2.
8. **Mantenimiento de FC1.2** intacto: si flag off, todo el modulo se comporta exactamente como hoy. Si flag on pero la reserva no entra al clasificador (caso 2 trivial sin disparadores), tambien se comporta como FC1.2 (NC total). FC1.3 abre la puerta solo cuando la clasificacion lo amerita.

### 2.1 Glosario adicional (extiende ADR-002 §2.1)

| Termino | Definicion |
|---|---|
| **NC parcial** | NC vinculada a factura original via `OriginalInvoiceId` por un monto **menor** que el total facturado. La factura original sigue viva por el saldo. |
| **NC total** | NC vinculada a factura original por el 100% del total facturado (es lo que hace FC1.2 hoy). |
| **NC total + nueva factura** | Anular factura original (NC por total) + emitir factura nueva por el remanente conceptual. Casos 4 y 7 de la matriz. Fase 2 implementa la nueva factura. |
| **`FiscalLiquidation`** | Owned VO que persiste el calculo fiscal del momento de confirmacion: monto facturado, penalidad, items no reintegrables, monto fiscal acreditado, monto a devolver, neto facturado final, regla aplicada (1..8), `CreditNoteKind`. |
| **`CreditNoteKind`** | Enum: `PartialOnOriginal` (casos 1, 2, 3, 5, 6 — una sola NC). `TotalPlusNewInvoice` (casos 4, 7 — NC total + factura nueva en Fase 2). |
| **Clasificador matriz 8** | Logica que mira `OriginatingInvoice.TipoComprobante`, `Supplier.InvoicingMode`, items no reintegrables, retenciones, modo cambiado, etc., y decide caso 1..8 + `ReviewRequiredReason`. |

### 2.2 Decisiones cerradas no negociables (Fase 1)

1. **Stack es SQL Server**. Las CHECK constraints se escriben en sintaxis T-SQL. El plan funcional referencio Postgres por reflejo del lenguaje ADR-002 — se ignora. El interceptor de `AppDbContext` ya transforma `SqlException` (severity / number) en `BusinessInvariantViolationException`.
2. **`FiscalLiquidation` es Owned VO**, no entidad propia (cierre §17.1 plan funcional, item 1). Argumentado en §2.4.
3. **`Supplier.PenaltyPolicyJson` es columna `nvarchar(max)`**, no tabla normalizada (cierre §17.1 item 2). Argumentado en §2.5.
4. **Clasificador en service separado** `IFiscalLiquidationCalculator` (cierre §17.1 item 3). Argumentado en §2.6.
5. **Approval via `ApprovalRequest.Metadata` JSON** + FK `BC.PartialCreditNoteApprovalRequestId` (cierre §17.1 item 4). Argumentado en §2.7.
6. **Feature flag nuevo `EnablePartialCreditNotes`** (cierre §17.1 item 7). Argumentado en §2.10.
7. **Auto-emision por umbral $500k**: el plan funcional propuso este threshold como sugerencia del agente integrado. Lo aceptamos como **default editable en `OperationalFinanceSettings`**. Si el contador propone otro valor en la respuesta a F1, se actualiza el setting sin tocar codigo.
8. **`CreditNoteKind = TotalPlusNewInvoice` queda persistido pero la nueva factura NO se emite en Fase 1**. Solo se marca el BC para que Fase 2 lo procese cuando emita real al ARCA. Hoy queda como "decision documentada, ejecucion diferida". Test cubre que el BC quede en estado terminal Fase 1 (`ManualReviewApproved`) sin avanzar.

### 2.3 Modelo de datos

#### 2.3.1 Entidades nuevas

```csharp
// src/TravelApi.Domain/Entities/FiscalLiquidation.cs
//
// Owned VO de BookingCancellation. Se persiste como columnas con prefijo
// FiscalLiquidation_ en la tabla BookingCancellations (igual que FiscalSnapshot).
// Inmutable post-T0 salvo edicion explicita del admin en ManualReviewPending (G3).
public class FiscalLiquidation
{
    // ===== Origen =====
    [Column(TypeName = "decimal(18,2)")]
    public decimal OriginalInvoiceAmount { get; set; }       // = OriginatingInvoice.ImporteTotal

    [Column(TypeName = "decimal(18,2)")]
    public decimal CancellationAmount { get; set; }          // monto cancelado (en general = OriginalInvoiceAmount, distinto si parcial)

    // ===== Componentes que NO van a la NC =====
    [Column(TypeName = "decimal(18,2)")]
    public decimal OperatorPenaltyAmount { get; set; }       // penalidad operador (modo reseller resta NC, modo intermediario depende)

    [Column(TypeName = "decimal(18,2)")]
    public decimal NonRefundableItemsAmount { get; set; }    // suma de items con IsRefundable=false en la factura origen

    // ===== Resultados calculados =====
    [Column(TypeName = "decimal(18,2)")]
    public decimal FiscalAmountToCredit { get; set; }        // = OriginalInvoiceAmount - NonRefundable - (Penalty si aplica modo). Va a la NC.

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountToRefundCustomer { get; set; }      // dinero que se devuelve. Puede coincidir con FiscalAmountToCredit (modo reseller) o no.

    [Column(TypeName = "decimal(18,2)")]
    public decimal FinalNetInvoiced { get; set; }            // = OriginalInvoiceAmount - FiscalAmountToCredit. Lo que queda como ingreso reconocido agencia.

    // ===== Decision de clasificacion =====
    public PartialCreditNoteCase ComputedCase { get; set; }  // enum 1..8 (matriz contador)

    public CreditNoteKind CreditNoteKind { get; set; }       // PartialOnOriginal | TotalPlusNewInvoice

    public ReviewRequiredReason ReviewRequiredReason { get; set; }  // bitflag, default None

    // ===== Trazabilidad del calculo =====
    public DateTime? ComputedAt { get; set; }

    [MaxLength(450)]
    public string? ComputedByUserId { get; set; }

    [MaxLength(200)]
    public string? ComputedByUserName { get; set; }
}

// src/TravelApi.Domain/Entities/PartialCreditNoteCase.cs
public enum PartialCreditNoteCase
{
    Unset = 0,
    Case1_PartialRefundNoPenalty = 1,
    Case2_FullCancellationNoRetention = 2,
    Case3_FullCancellationWithPenalty = 3,
    Case4_OriginalInvoiceUnclear = 4,
    Case5_CommissionOnlyPartial = 5,
    Case6_CommissionOnlyFull = 6,
    Case7_RetentionChangesNature = 7,
    Case8_FacturaA = 8,
}

// src/TravelApi.Domain/Entities/CreditNoteKind.cs
public enum CreditNoteKind
{
    Unset = 0,
    PartialOnOriginal = 1,         // casos 1, 2, 3, 5, 6 — una sola NC vinculada
    TotalPlusNewInvoice = 2,       // casos 4, 7 — NC total + factura nueva en Fase 2
}

// src/TravelApi.Domain/Entities/ReviewRequiredReason.cs
// Bitflag: un BC puede activar multiples motivos (ej. Factura A + items no reintegrables).
[Flags]
public enum ReviewRequiredReason
{
    None = 0,
    CustomerIsRiOrFacturaA = 1 << 0,         // caso 8 obligatorio
    HasNonRefundableItems = 1 << 1,
    AmountAboveAdminThreshold = 1 << 2,      // > PartialNcAutoApprovalThreshold
    AmountAboveAccountingThreshold = 1 << 3, // > PartialNcAdminReviewThreshold (G5: comentario 100+ chars)
    RetentionChangesNature = 1 << 4,         // caso 7 — mix DeductionKind heterogeneo
    OriginalInvoiceUnclear = 1 << 5,         // caso 4 — heuristicas factura confusa
    MultiCurrency = 1 << 6,                  // futuro Fase 2
    LegacyInvoice = 1 << 7,                  // factura emitida antes de Fc13DeployDate
    Other = 1 << 8,                          // catch-all, ej. NC en cadena
}

// src/TravelApi.Domain/Entities/SupplierInvoicingMode.cs
public enum SupplierInvoicingMode
{
    TotalToCustomer = 0,  // reseller: factura al cliente el total del servicio. Default conservador.
    CommissionOnly = 1,   // intermediario: factura solo la comision. Resto en cuenta corriente con operador.
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
    /// CommissionOnly = intermediario (factura solo la comision).
    /// Snapshot al momento de emitir factura queda en FiscalSnapshot.InvoicingModeAtEvent.
    /// Editable por admin. Default TotalToCustomer (conservador, comportamiento legacy).
    /// </summary>
    public SupplierInvoicingMode InvoicingMode { get; set; } = SupplierInvoicingMode.TotalToCustomer;

    /// <summary>
    /// FC1.3 (ADR-009, 2026-05-21): tabla de penalidades por antelacion en JSON.
    /// Schema: { "tiers": [{"minDaysBefore": int, "penaltyPercent": decimal}, ...], "currency": "USD"|"ARS" }
    /// Tiers ordenados DESC por minDaysBefore. Vendedor puede override manual al confirmar (D2 2026-05-21).
    /// Validacion via FluentValidation en el Service que actualiza Supplier (no en el entity).
    /// Si null o vacio: sin tabla, vendedor ingresa manual cada vez.
    /// </summary>
    public string? PenaltyPolicyJson { get; set; }
}

// src/TravelApi.Domain/Entities/InvoiceItem.cs — 3 props nuevas
public class InvoiceItem
{
    // ... props existentes ...

    /// <summary>
    /// FC1.3 (ADR-009, 2026-05-21): si false, este item NO entra en FiscalLiquidation.FiscalAmountToCredit
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
    /// Habilita el calculo "que linea pertenece a que servicio" para parsing en clasificador
    /// caso 4 (factura confusa). Nullable: facturas legacy o conceptos sueltos no tienen origen.
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
    /// de auditoria sin join. Pone el "es Factura A?" en la tabla del BC para alertas.
    /// Nullable por legacy.
    /// </summary>
    public int? OriginalInvoiceTypeAtEvent { get; set; }
}

// src/TravelApi.Domain/Entities/BookingCancellation.cs — 4 props nuevas + Owned VO
public class BookingCancellation
{
    // ... props existentes ...

    /// <summary>
    /// FC1.3 (ADR-009): liquidacion fiscal calculada al momento de Confirm.
    /// Owned VO (igual patron que FiscalSnapshot). Nullable hasta que el calculator corra.
    /// Inmutable salvo edicion admin en ManualReviewPending (G3) — siempre con audit + nuevo
    /// comentario en metadata del approval.
    /// </summary>
    public FiscalLiquidation? FiscalLiquidation { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): FK al ApprovalRequest tipo PartialCreditNoteApproval que aprueba
    /// la liquidacion. Null hasta que el BC pase por ManualReviewPending. Persistido para
    /// audit cross-reference (igual patron que Invoice.AnnulmentApprovalRequestId).
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
    /// Schema: [{"description": string, "amount": decimal, "category": InvoiceItemCategory}, ...]
    /// Null o vacio: sin conceptos adicionales.
    /// </summary>
    public string? NonRefundableConceptsJson { get; set; }
}
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
    RequiresManualReview = 8,    // clasificador identifico caso review pero approval no abierto aun
    ManualReviewPending = 9,     // ApprovalRequest tipo PartialCreditNoteApproval abierto
    ManualReviewApproved = 10,   // admin aprobo, siguiente paso emitCreditNote()
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
    /// el clasificador y se comporta exactamente como FC1.2 (NC por total). Si true, el
    /// clasificador corre, los nuevos estados ManualReviewPending pueden activarse y se
    /// emite NC parcial cuando aplica (Fase 2 hace la emision real al ARCA).
    /// Default false en prod. Independiente de EnableNewCancellationFlow porque FC1.2
    /// puede estar prendido sin FC1.3.
    /// </summary>
    public bool EnablePartialCreditNotes { get; set; } = false;

    /// <summary>
    /// FC1.3 (ADR-009): por debajo de este monto en ARS, NC parcial se auto-emite si no
    /// hay otros disparadores manuales (Factura A, items no reintegrables, retencion mix,
    /// factura confusa). Default 500.000. Threshold del subagente integrado, sujeto a
    /// confirmacion contador (F1). Editable en panel admin sin redeploy.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal PartialNcAutoApprovalThreshold { get; set; } = 500_000m;

    /// <summary>
    /// FC1.3 (ADR-009): por encima de PartialNcAutoApprovalThreshold y hasta este monto,
    /// admin reforzada (comentario min 20 chars + 4-eyes). Default 2.000.000 ARS.
    /// Confirmar contador.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal PartialNcAdminReviewThreshold { get; set; } = 2_000_000m;

    /// <summary>
    /// FC1.3 (ADR-009 + G5): por encima de PartialNcAdminReviewThreshold, admin reforzada
    /// con comentario min 100 chars + flag AccountingReviewRequired=true en metadata. Sin
    /// rol nuevo Fase 1. Si null, no hay tope superior y todo lo > Admin Review entra al
    /// flujo G5. Default null. Confirmar contador.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PartialNcAccountingReviewThreshold { get; set; } = null;

    /// <summary>
    /// FC1.3 (ADR-009): template de descripcion de la NC parcial. Variables soportadas:
    /// {invoiceType}, {invoiceNumber}, {pointOfSale}, {fiscalAmount}, {currency},
    /// {cancellationReason}, {nonRefundableAmount}, {operatorPenaltyAmount}, {customerName},
    /// {customerTaxId}. Validacion al guardar (rechazar variables no soportadas).
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
    /// FC1.3 (ADR-009): timestamp del deploy de FC1.3 a prod. Heuristica caso 4
    /// (factura confusa): facturas emitidas antes de esta fecha se flagean como
    /// "legacy invoice" para revision manual. Null = sin cutoff (legacy detection off).
    /// </summary>
    public DateTime? Fc13DeployDate { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): patrones regex (uno por linea) que el clasificador caso 4 usa para
    /// flagear "factura con descripcion generica unica". Default: "^(servicio|concepto|importe|operacion|reserva)".
    /// Configurable por agencia. Cada patron se evalua case-insensitive sobre Description del unico InvoiceItem.
    /// </summary>
    [MaxLength(1000)]
    public string GenericDescriptionPatterns { get; set; } =
        "^(servicio|concepto|importe|operacion|reserva)";
}
```

#### 2.3.5 CHECK constraints SQL (T-SQL, SQL Server)

```sql
-- INV-FC1.3-005: suma de componentes = OriginalInvoiceAmount con tolerancia 0.01 por redondeo.
-- Solo aplica cuando FiscalLiquidation_OriginalInvoiceAmount > 0 (BC en Drafted puede tener
-- liquidacion vacia con todo en 0).
ALTER TABLE [BookingCancellations]
  ADD CONSTRAINT chk_BookingCancellations_fiscalliq_sum
  CHECK (
    [FiscalLiquidation_OriginalInvoiceAmount] IS NULL
    OR [FiscalLiquidation_OriginalInvoiceAmount] = 0
    OR ABS(
        ([FiscalLiquidation_FiscalAmountToCredit]
         + [FiscalLiquidation_NonRefundableItemsAmount]
         + [FiscalLiquidation_OperatorPenaltyAmount])
        - [FiscalLiquidation_OriginalInvoiceAmount]
      ) <= 0.01
  );

-- INV-FC1.3-006: items no reintegrables del FiscalLiquidation deben sumar exactamente lo que
-- la factura origen tiene marcado como IsRefundable=false. NO se puede expresar en CHECK
-- inline (requiere join). Se valida en service (en CalculateAsync) + test de integracion.

-- Coherencia FiscalAmountToCredit >= 0 (no se acredita negativo).
ALTER TABLE [BookingCancellations]
  ADD CONSTRAINT chk_BookingCancellations_fiscalliq_nonneg
  CHECK (
    [FiscalLiquidation_FiscalAmountToCredit] IS NULL
    OR ([FiscalLiquidation_FiscalAmountToCredit] >= 0
        AND [FiscalLiquidation_NonRefundableItemsAmount] >= 0
        AND [FiscalLiquidation_OperatorPenaltyAmount] >= 0
        AND [FiscalLiquidation_AmountToRefundCustomer] >= 0
        AND [FiscalLiquidation_FinalNetInvoiced] >= 0)
  );

-- ManualReviewPending requiere ApprovalRequest FK (INV-FC1.3-002).
ALTER TABLE [BookingCancellations]
  ADD CONSTRAINT chk_BookingCancellations_manualreview_approvalref
  CHECK (
    [Status] NOT IN (9, 10, 11)
    OR [PartialCreditNoteApprovalRequestId] IS NOT NULL
  );
```

Convencion EF Core (heredada FC1): `chk_<tabla>_<concepto>` + `migrationBuilder.Sql(@"ALTER TABLE ...")` + interceptor `SqlException` (T-SQL error number 547 para CHECK violations) -> `BusinessInvariantViolationException` -> HTTP 409.

### 2.4 Por que `FiscalLiquidation` es Owned VO y no entidad propia

**Decision**: Owned VO siguiendo el patron de `FiscalSnapshot`.

**Argumentos a favor**:

- **Cohesion**: la liquidacion no tiene vida fuera del BC. Si el BC se borra (no pasa por el flujo normal pero teoricamente), la liquidacion deberia desaparecer. Cascade implicito con owned.
- **Ciclo de vida**: se calcula en el momento de Confirm, se modifica solo en revision manual, y se freeza tras `ManualReviewApproved`. Nunca se consulta independiente del BC.
- **Sin necesidad de Id propio**: no hay queries de tipo "dame todas las liquidaciones con ComputedCase=8". Las queries siempre arrancan por el BC.
- **Precedente repo**: `FiscalSnapshot` ya es Owned VO en `BookingCancellation` desde FC1.1 (commit `184134f`). Mantener simetria reduce curva de aprendizaje.
- **Persistencia simple**: EF Core con `OwnsOne` mete las columnas con prefijo en la tabla padre. Menos joins, menos N+1 risk.

**Argumentos en contra evaluados**:

- *"Si necesitamos historial de versiones de la liquidacion (admin edito 3 veces antes de aprobar), un Owned no sirve"*. Respuesta: el historial vive en el `ApprovalRequest.Metadata` (cada edicion suma una entrada). El estado **actual** vive en el Owned. Si en futuro hace falta tabla de historial, se agrega `FiscalLiquidationHistory` como entidad propia sin tocar el Owned.
- *"Otros aggregates podrian necesitar acceder a la liquidacion"*. Respuesta: no hay caso de uso. Si aparece, se promueve. YAGNI hoy.

**Costo de cambiar despues**: bajo. Migrar Owned -> Entity es script SQL (mover columnas de tabla padre a tabla nueva con FK) + cambio EF mapping. Sin riesgo de datos.

### 2.5 Por que `Supplier.PenaltyPolicyJson` es `nvarchar(max)` y no tabla normalizada

**Decision**: columna JSON.

**Argumentos a favor**:

- **Acceso patron**: la tabla se lee al momento de calcular liquidacion (durante Draft o Confirm). No es hot path. Una sola lectura por flujo.
- **Sin necesidad de queries cross-supplier**: no hay reporte "todos los suppliers con penalidad > 30% para 15 dias antes". Si aparece, se proyecta vista o se hace JOIN sobre el JSON.
- **Schema evoluciona**: hoy 4 tiers, manana podria ser 7 tiers, o estructura curve-based en lugar de discrete. JSON acomoda sin migrar.
- **Override manual del vendedor (D2)**: el vendedor puede ignorar la tabla y poner monto manual. Eso ya implica que la tabla es una sugerencia, no una restriccion dura — no necesita normalizar.
- **Convencion existente**: `BookingCancellation.FiscalSnapshot.ExtrasJson`, `HotelBooking.RoomingAssignmentsJson`, `Invoice.AgencySnapshot`/`CustomerSnapshot` ya usan JSON columns para datos semi-estructurados de baja frecuencia. Consistencia con repo.

**Argumentos en contra evaluados**:

- *"No queryable si manana hace falta reportar"*. Respuesta: SQL Server soporta `JSON_VALUE` y `OPENJSON` para queries ad-hoc. Performance suficiente para reportes de baja frecuencia. Si hace falta, se materializa vista o se normaliza despues.
- *"Validacion mas dificil que en tabla con constraints"*. Respuesta: validacion en `SupplierService` con FluentValidation antes de persistir (verificar schema, tiers ordenados DESC, percentages 0..100). Mas robusto que CHECK constraints aislados.

**Costo de cambiar despues**: medio. Migrar JSON -> tabla normalizada requiere script de extraccion + crear tabla `SupplierPenaltyTier` + reescribir reads. Pero como el codigo cliente es local (clasificador llama a un service), el blast radius esta acotado.

### 2.6 `IFiscalLiquidationCalculator` como servicio aparte

**Decision**: extraer service nuevo, **no** meter la logica dentro de `BookingCancellationService.ConfirmAsync`.

**Ubicacion**: `src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs` (interface) + `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` (impl).

```csharp
// src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs
public interface IFiscalLiquidationCalculator
{
    /// <summary>
    /// Calcula la liquidacion fiscal y clasifica el caso (matriz 8 contador 2026-05-21).
    /// Input: snapshot de datos ya cargados por el BC service (no toca DbContext).
    /// Output: FiscalLiquidation + ReviewRequiredReason. Puro (sin side effects, sin IO).
    /// </summary>
    FiscalLiquidationResult Calculate(FiscalLiquidationInput input, OperationalFinanceSettings settings);
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

public record FiscalLiquidationResult(
    FiscalLiquidation Liquidation,
    string ClassificationExplanation);  // texto narrativo de por que cayo en X caso (audit + UI)
```

**Argumentos a favor**:

- **Testeable aislado**: la matriz 8 casos son **unit tests rapidos** sin DbContext, sin TestContainers, sin Postgres/SqlServer. Se ejecuta toda la matriz en milisegundos. Critico porque el contador puede cambiar reglas y queremos cobertura.
- **Sin acoplamiento al estado de transaccion**: `ConfirmAsync` ya hace 10+ pasos. Sumar 60 lineas de clasificador volveria al metodo ilegible.
- **Reuse futuro**: pantalla UI de "previsualizacion de liquidacion" (cuando se implemente Fase 3) llama al mismo calculator sin Confirm. El service expone solo el calculo.
- **Inyectable y mockeable**: el `BookingCancellationService` recibe `IFiscalLiquidationCalculator` en el constructor. Tests del BC service usan fake calculator que devuelve resultados predefinidos. Decouple del clasificador.

**Argumentos en contra evaluados**:

- *"Otro service mas para mantener"*. Respuesta: el costo de mantener es bajo (logica pura). El costo de tener todo en `ConfirmAsync` es alto (testear obliga a setup de Reserva + Invoice + Supplier + InvoiceItem completos en TestContainers para cada variante de matriz).

**Costo de cambiar despues**: bajo. Si fuera necesario fusionar, se inlinea.

### 2.7 Mapping al `ApprovalRequest` existente (cierre §17.1 item 4)

**Decision**: usar `ApprovalRequest` con `RequestType = PartialCreditNoteApproval`, `EntityType = "BookingCancellation"`, `EntityId = bc.Id`, `Metadata` JSON con la liquidacion + edicion admin. FK `BC.PartialCreditNoteApprovalRequestId` para join inverso.

**Schema del `Metadata` JSON**:

```json
{
  "liquidationVersion": 1,
  "computedAt": "2026-05-21T14:30:00Z",
  "computedCase": "Case8_FacturaA",
  "originalInvoiceAmount": 1000000.00,
  "operatorPenaltyAmount": 200000.00,
  "nonRefundableItemsAmount": 50000.00,
  "fiscalAmountToCredit": 750000.00,
  "amountToRefundCustomer": 750000.00,
  "finalNetInvoiced": 250000.00,
  "creditNoteKind": "PartialOnOriginal",
  "reviewRequiredReason": ["CustomerIsRiOrFacturaA", "HasNonRefundableItems"],
  "currency": "ARS",
  "accountingReviewRequired": false,
  "edits": [
    {
      "at": "2026-05-21T15:10:00Z",
      "by": "user-admin-1",
      "fields": {
        "operatorPenaltyAmount": { "from": 200000.00, "to": 250000.00 },
        "fiscalAmountToCredit":  { "from": 750000.00, "to": 700000.00 }
      },
      "comment": "Operador mando email con penalidad actualizada por antelacion 18 dias en lugar de 20"
    }
  ]
}
```

**Como funciona el flujo de edicion admin (G3)**:

1. Admin abre el approval pending desde `/api/approvals/{publicId}`.
2. Admin invoca `POST /api/cancellations/{publicId}/edit-liquidation` con los campos modificados + comentario.
3. `BookingCancellationService.EditLiquidationAsync`:
   - Lee BC + approval por FK.
   - Valida que BC esta en `ManualReviewPending`.
   - Valida que admin != vendedor (4-eyes).
   - Valida comentario min 20 chars.
   - Llama a `IFiscalLiquidationCalculator.Calculate(...)` con los inputs nuevos (overrides).
   - Verifica INV-FC1.3-005 (suma = original).
   - Actualiza `BC.FiscalLiquidation` con los nuevos valores.
   - Apenda entrada al `edits[]` del `approval.Metadata`.
   - Audit log `BookingCancellationLiquidationEdited`.
   - SaveChanges. BC sigue en `ManualReviewPending` (self-loop).
4. Admin aprueba con `POST /api/approvals/{publicId}/approve` (endpoint existente, no se crea nada). El callback del approval service notifica al BC service via interface chica `IPartialCreditNoteApprovalBridge` (paralelo a `IInvoiceAnnulmentBcBridge`) que transiciona BC a `ManualReviewApproved`.

**Por que `Metadata` JSON y no tabla nueva `PartialCreditNoteApprovalDetails`**:

- `ApprovalRequest.Metadata` ya esta tipado `string?` y documentado como "JSON arbitrario con context del request". Convencion existente.
- Edicion del admin es naturalmente versionada (apend-only en `edits[]`). Tabla relacional para 3 ediciones promedio es overkill.
- La tabla tendria FK 1:1 con `ApprovalRequest`, mismas reglas que Owned VO. Mismo overhead que `Metadata` con menos flexibilidad.

**Por que FK `PartialCreditNoteApprovalRequestId` y no busqueda por `EntityType+EntityId`**:

- Busqueda inversa rapida: dado un BC, en un solo SELECT obtenes el approval activo. Sin FK habria que filtrar por `(EntityType="BookingCancellation", EntityId=bc.Id, RequestType=PartialCreditNoteApproval, Status IN (Pending, Approved))`.
- Audit cross-reference simetrico con `Invoice.AnnulmentApprovalRequestId` (FC1.2 v3).
- `OnDelete: Restrict`: si alguien intenta borrar el approval, la BD rechaza. Preserva trazabilidad fiscal.

### 2.8 Maquina de estados (FC1.3 Fase 1)

Extiende ADR-002 §2.4 con los 4 estados nuevos insertados antes de `AwaitingFiscalConfirmation`. Los estados FC1.2 (`AwaitingFiscalConfirmation`..`ArcaRejected`) **quedan intactos** — FC1.3 inserta un sidetrack opcional, no reemplaza el flujo existente.

#### 2.8.1 Diagrama

```
                  ┌──────────────────────────────────────────────────────────────────────┐
                  │                                                                       │
                  v                                                                       │
        ┌──────────────┐                                                                  │
        │   Drafted (0)│                                                                  │
        └──────┬───────┘                                                                  │
               │ confirmCancellation()                                                    │
               │ + Calculator.Calculate() corre                                            │
               │ + flag EnablePartialCreditNotes evaluado                                  │
               │                                                                          │
               ├─── flag OFF                                                              │
               │    -> AwaitingFiscalConfirmation (1) -> ... flujo FC1.2 vigente          │
               │                                                                          │
               ├─── flag ON + caso 2 (NC total simple) + monto < threshold                │
               │    -> AwaitingFiscalConfirmation (1) -> ... flujo FC1.2 vigente          │
               │                                                                          │
               ├─── flag ON + (caso 1, 3, 5, 6) + monto < threshold + sin disparadores   │
               │    -> AwaitingFiscalConfirmation (1) -> ... flujo FC1.2 vigente          │
               │                                                                          │
               └─── flag ON + cualquier disparador (Factura A, items no reintegrables,    │
                    monto > threshold, retencion mix, factura confusa, etc.)              │
                    -> RequiresManualReview (8)                                            │
                          │                                                               │
                          │ submitForReview()                                             │
                          │ (auto-invocado por Confirm si reason != None,                 │
                          │  abre ApprovalRequest PartialCreditNoteApproval)              │
                          │                                                               │
                          v                                                               │
                    ManualReviewPending (9) <----+ self-loop editLiquidation()            │
                          │                      │ (admin edita penalidad o items)        │
                          │                      │ + audit BC_LiquidationEdited           │
                          │                      │ + apend a Metadata.edits[]             │
                          │                      └─────────────────                       │
                          ├─── approveLiquidation(comment) (4-eyes, comment >= 20 chars) │
                          │    -> ManualReviewApproved (10)                                │
                          │            │                                                  │
                          │            │ emitCreditNote() invocado automaticamente       │
                          │            │ (Fase 1: no emite real, marca y avanza)          │
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

#### 2.8.2 Interaccion con `ApprovalRequest` (visual)

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
                          │                                     Metadata={...liquidacion...}
                          v                                   │
                   ManualReviewPending (9)                    │
                   PartialCreditNoteApprovalRequestId=AR.Id   │
                          │                                   │
   EditLiquidationAsync ─►│ self-loop, audit               ──► AR.Metadata.edits[] apend
                          │ (no cambia status, no transiciona) │
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

#### 2.8.3 Tabla de transiciones

| Estado origen | Trigger | Estado destino | Condiciones | Quien dispara | Override |
|---|---|---|---|---|---|
| `Drafted` | `confirmCancellation()` (flag off o sin disparadores) | `AwaitingFiscalConfirmation` | Flag `EnablePartialCreditNotes`=false OR (flag on + `ReviewRequiredReason=None`) | Vendedor con permiso `cobranzas.invoice_annul` | NO |
| `Drafted` | `confirmCancellation()` (con disparadores) | `RequiresManualReview` | Flag on + `ReviewRequiredReason != None` | Vendedor (sistema fuerza) | NO |
| `Drafted` | `abort()` | `Aborted` | — | Vendedor o admin | NO |
| `RequiresManualReview` | `submitForReview()` (auto en mismo Confirm) | `ManualReviewPending` | `FiscalLiquidation` no nula + crea `ApprovalRequest` tipo `PartialCreditNoteApproval` | Sistema (BC service) | NO |
| `RequiresManualReview` | `abort()` | `Aborted` | — | Vendedor o admin | NO |
| `ManualReviewPending` | `editLiquidation(...)` | `ManualReviewPending` (self-loop) | Admin reforzada: modifica penalty/non-refundable/kind + comentario >= 20 chars + admin != vendedor | Admin | NO |
| `ManualReviewPending` | `POST /approvals/{id}/approve` (callback) | `ManualReviewApproved` | `ApprovalRequest.Status = Approved` + admin != vendedor + comment >= 20 chars (o 100 si > AccountingReviewThreshold por G5) | Admin via callback bridge | NO |
| `ManualReviewPending` | `POST /approvals/{id}/reject` (callback) | `ManualReviewRejected` | Comentario reject >= 20 chars | Admin via callback bridge | NO |
| `ManualReviewApproved` | `emitCreditNote()` (auto inmediato post-approval) | `AwaitingFiscalConfirmation` | `CreditNoteKind=PartialOnOriginal`: Fase 1 marca BC, Fase 2 emite NC al ARCA real con monto parcial. `CreditNoteKind=TotalPlusNewInvoice`: Fase 1 marca BC, Fase 2 emite NC total + factura nueva. **Fase 1 no emite, solo deja BC listo.** | Sistema (service) | NO |
| `ManualReviewRejected` | `resetToDraft()` (auto inmediato post-reject) | `Drafted` | Inmediato. BC limpia `FiscalLiquidation` + nulea `PartialCreditNoteApprovalRequestId`. Approval queda en Rejected (histórico). | Sistema | NO |
| `ManualReviewRejected` | `abort()` | `Aborted` | — | Vendedor o admin | NO |
| `AwaitingFiscalConfirmation` y posteriores | — | Igual que FC1.2 vigente | — | — | — |

#### 2.8.4 Invariantes Fase 1 (extiende Bucket G)

Conforme convencion ADR-001 + ADR-002:

| ID | Regla | `AdmitsOverride` |
|---|---|---|
| **INV-FC1.3-001** | BC no transiciona a `AwaitingFiscalConfirmation` directamente si `FiscalLiquidation.ReviewRequiredReason != None`. Debe pasar por `ManualReviewPending` + `ManualReviewApproved`. | `false` |
| **INV-FC1.3-002** | `Status IN (ManualReviewPending, ManualReviewApproved, ManualReviewRejected)` requiere `PartialCreditNoteApprovalRequestId != NULL`. CHECK SQL. | `false` |
| **INV-FC1.3-003** | `ManualReviewApproved` requiere `ApprovalRequest.Status = Approved`. Verificado en service callback. | `false` |
| **INV-FC1.3-004** | `approveLiquidation()` requiere admin != vendedor (`DraftedByUserId != ResolvedByUserId`). 4-eyes. | `false` |
| **INV-FC1.3-005** | `FiscalAmountToCredit + NonRefundableItemsAmount + OperatorPenaltyAmount = OriginalInvoiceAmount` (tolerancia 0.01). CHECK SQL + validacion en calculator. | `false` |
| **INV-FC1.3-006** | Items con `IsRefundable=false` en la factura origen suman exactamente `FiscalLiquidation.NonRefundableItemsAmount`. Validacion en calculator (no inline en CHECK por requerir join). | `false` |
| **INV-FC1.3-007** | BC FC1.3 solo acepta reservas 100% Hotel (`Reserva.Servicios` todos con `ProductType="Hotel"`). | `true` con justificacion admin >= 50 chars (en caso de mal clasificado) |
| **INV-FC1.3-008** | `FiscalLiquidation.CreditNoteKind` no cambia post-`ManualReviewApproved`. | `false` |
| **INV-FC1.3-009** | Edicion admin (G3) requiere comentario distinto al comentario de aprobacion. Validacion en service (no SQL). | `false` |
| **INV-FC1.3-010** | Si `Status = ManualReviewApproved` y `CreditNoteKind = TotalPlusNewInvoice`, **Fase 1** mantiene BC en `ManualReviewApproved` indefinidamente (no avanza a `AwaitingFiscalConfirmation` hasta Fase 2 plumbing). Fase 1 emite warning visible. | `false` Fase 1 |

### 2.9 Reglas del clasificador (matriz 8 + disparadores)

El calculator aplica estas reglas en orden. **La primera que matchea gana el `ComputedCase`**. Los `ReviewRequiredReason` se acumulan en bitflag (un BC puede tener Factura A + items no reintegrables + monto alto = 3 flags activos simultaneos).

```
INPUT: FiscalLiquidationInput input + OperationalFinanceSettings s

STEP 1 — disparadores siempre activos (acumulan a ReviewRequiredReason)
   ─ Si OriginalInvoice.TipoComprobante = 1 (Factura A):
        reason |= CustomerIsRiOrFacturaA
   ─ Si suma items con IsRefundable=false > 0:
        reason |= HasNonRefundableItems
   ─ Si OriginatingInvoice tiene OriginalInvoiceId != null (NC en cadena):
        reason |= Other
   ─ Si OriginatingInvoice.CreatedAt < s.Fc13DeployDate (legacy):
        reason |= LegacyInvoice
   ─ Si Currency != "ARS":
        reason |= MultiCurrency  (Fase 1 no implementa multicurrency, lo manda a manual)

STEP 2 — heuristicas caso 4 (factura confusa)
   ─ Si Items.Count == 1 AND Description match cualquier pattern de s.GenericDescriptionPatterns:
        reason |= OriginalInvoiceUnclear
   ─ Si > 50% del Total tiene items con SourceServicioReservaId=null:
        reason |= OriginalInvoiceUnclear
   ─ Si SUM(InvoiceItem.ImporteIva) != Invoice.ImporteIva con tolerancia 0.50:
        reason |= OriginalInvoiceUnclear
   ─ Si input.OriginalInvoiceUnclearByUser (checkbox manual):
        reason |= OriginalInvoiceUnclear

STEP 3 — caso 7 (cambia naturaleza fiscal del retenido)
   Nota Fase 1: como no tenemos DeductionLines en este punto (solo el OperatorPenaltyAmount
   ingresado por vendedor), las senales 1 y 2 del plan funcional §11.1 quedan diferidas a
   Fase 2 cuando T2 ya tiene allocations. En Fase 1 solo aplica senal 3 (InvoicingMode mismatch)
   + checkbox manual.
   ─ Si input.InvoicingModeAtEvent != null AND input.InvoicingModeAtEvent != input.Supplier.InvoicingMode:
        reason |= RetentionChangesNature
   ─ Si input.RetentionNatureChangedByUser:
        reason |= RetentionChangesNature

STEP 4 — calcular liquidacion segun InvoicingMode
   mode = input.InvoicingModeAtEvent ?? input.Supplier.InvoicingMode
   nonRefundableTotal = SUM(items where IsRefundable=false → Total)
   penalty = input.OperatorPenaltyAmount  // ya validado >= 0

   if (mode == TotalToCustomer) {
       fiscalAmountToCredit = OriginalInvoiceAmount - nonRefundableTotal - penalty
       amountToRefundCustomer = fiscalAmountToCredit  // modo reseller: lo fiscal = lo devuelto
   }
   else /* CommissionOnly */ {
       // Hipótesis Fase 1: penalidad operador NO reduce NC al cliente (depende cuenta corriente operador).
       // Confirmar contador respuesta F1 pregunta 2.
       fiscalAmountToCredit = OriginalInvoiceAmount - nonRefundableTotal
       amountToRefundCustomer = fiscalAmountToCredit
   }

   finalNetInvoiced = OriginalInvoiceAmount - fiscalAmountToCredit

   if (fiscalAmountToCredit < 0) throw InvariantViolation INV-FC1.3-005  // bug en input

STEP 5 — disparadores de monto
   if (fiscalAmountToCredit > s.PartialNcAccountingReviewThreshold ?? infinity):
        reason |= AmountAboveAccountingThreshold
   else if (fiscalAmountToCredit > s.PartialNcAdminReviewThreshold):
        reason |= AmountAboveAdminThreshold
   else if (fiscalAmountToCredit > s.PartialNcAutoApprovalThreshold):
        reason |= AmountAboveAdminThreshold  // mismo flag, mismo tratamiento (admin reforzada)

STEP 6 — clasificar caso 1..8
   La clasificacion del caso es informativa (audit, UI, narrativa). El comportamiento del flujo
   depende de reason, no del case directamente.

   if (reason.HasFlag(CustomerIsRiOrFacturaA))         case = Case8_FacturaA;
   else if (reason.HasFlag(OriginalInvoiceUnclear))    case = Case4_OriginalInvoiceUnclear;
   else if (reason.HasFlag(RetentionChangesNature))    case = Case7_RetentionChangesNature;
   else if (mode == CommissionOnly && cancelaciónFull) case = Case6_CommissionOnlyFull;
   else if (mode == CommissionOnly)                    case = Case5_CommissionOnlyPartial;
   else if (penalty > 0)                               case = Case3_FullCancellationWithPenalty;
   else if (cancellationAmount == OriginalInvoiceAmount) case = Case2_FullCancellationNoRetention;
   else                                                case = Case1_PartialRefundNoPenalty;

STEP 7 — CreditNoteKind
   if (case == Case4 || case == Case7) creditNoteKind = TotalPlusNewInvoice
   else                                  creditNoteKind = PartialOnOriginal

OUTPUT: FiscalLiquidationResult con liquidation + explicacion narrativa.
```

### 2.10 Feature flag separado

**Decision**: nuevo `OperationalFinanceSettings.EnablePartialCreditNotes` (bool, default false). Independiente de `EnableNewCancellationFlow`.

**Argumentos a favor**:

- **Composabilidad**: FC1.2 puede estar prendido (necesario porque ya esta mergeado) sin que FC1.3 abra NC parciales en prod. Default `false` mantiene comportamiento legacy FC1.2.
- **Rollback granular**: si FC1.3 explota en prod, apagar este flag deja FC1.2 funcionando. Si fuera el mismo flag, apagar tirarrar abajo TODO el modulo.
- **QA escalonado**: staging puede tener ambos prendidos, prod arranca con FC1.3 off, se prende cuando contador firma.
- **Sin interferencia con OPS-FISCAL-001**: ese signoff cubre FC1.2 (override de annulment). FC1.3 abre debate fiscal nuevo (prorrateo IVA, criterio matriz 8). Mantenerlos separados evita re-litigar.

**Argumentos en contra evaluados**:

- *"Dos flags es mas complejidad"*. Respuesta: la tabla de verdad de ambos es chica (4 combos, solo 3 validos: off/off legacy, on/off FC1.2 vigente, on/on FC1.3 activo. on/off no tiene sentido y se rechaza al startup).

### 2.11 Manejo de servicios no-Hotel (rechazo Fase 1)

`BookingCancellationService.DraftAsync` ya carga `Reserva.Servicios`. En FC1.3, antes de calcular liquidacion, validar:

```csharp
// Pseudo
if (settings.EnablePartialCreditNotes)
{
    var nonHotelServices = reserva.Servicios.Where(s => s.ProductType != "Hotel").ToList();
    if (nonHotelServices.Any())
    {
        // INV-FC1.3-007 admite override
        if (!ctx.HasOverrideForInvariant("INV-FC1.3-007"))
            throw new BusinessInvariantViolationException(
                "INV-FC1.3-007",
                $"FC1.3 Fase 1 solo soporta reservas 100% Hotel. " +
                $"Servicios no-Hotel detectados: {string.Join(", ", nonHotelServices.Select(s => s.ProductType))}. " +
                $"Use flujo legacy (apagar EnablePartialCreditNotes para esta operacion) " +
                $"o esperar fases siguientes.");
    }
}
```

Override admite via `InvariantOverride` aprobado tipo 7 con justificacion >= 50 chars.

---

## 3. Consecuencias

### 3.1 Positivas

- **NC parcial fiscalmente correcta**: la NC refleja la parte que pierde causa fiscal (criterio contador 2026-05-21), no el monto devuelto. Cierra el riesgo R1 del plan funcional.
- **Separacion de conceptos**: `FiscalAmountToCredit` vs `AmountToRefundCustomer` quedan separados en BD. Ningun bug futuro puede confundirlos.
- **Items no reintegrables modelados explicitamente**: `IsRefundable` por item + `ItemCategory` para defaults + `NonRefundableConceptsJson` para conceptos adicionales. Cierra R2 + R9.
- **Snapshot fiscal completo**: `InvoicingModeAtEvent` evita que un cambio de modo del operador rompa cancelaciones historicas. Cierra R3.
- **Auto-Factura-A-a-revision**: cualquier Factura A va a revision manual sin importar monto. Cierra R4.
- **4-eyes obligatorio**: admin que aprueba != vendedor que cargo. INV-FC1.3-004.
- **Bandeja existente reusada**: `ApprovalRequestsController` con filtro por tipo. Sin codigo UI nuevo Fase 1.
- **Clasificador testeable aislado**: matriz 8 casos cubierta por unit tests sin DB.
- **Compatibilidad backward**: FC1.2 sigue funcionando si flag FC1.3 esta off. Migracion no destructiva (todas columnas nullable).
- **Settings parametrizados**: thresholds + template + reglas de heuristicas en `OperationalFinanceSettings`. Cambios sin redeploy.

### 3.2 Negativas

- **6 entidades modificadas + 1 Owned VO nuevo + 5 enums nuevos + 7 settings nuevos**: complejidad de modelo. Justificada por la matriz fiscal.
- **Fase 1 NO emite NC al ARCA real**: si el BC cae en `ManualReviewApproved` con `PartialOnOriginal` y flag FC1.3 esta on, en Fase 1 el BC queda parado y avanza por flujo FC1.2 (NC total). Esto es **intencional** para no romper plumbing fiscal hasta Fase 2 — la marca queda en `BC.FiscalLiquidation.CreditNoteKind` para que Fase 2 lo levante. Riesgo: si Fase 2 demora, hay BCs con liquidacion calculada que no usan esa info. Mitigacion: si flag FC1.3 esta off (default prod), el calculator no corre y nada cambia.
- **`TotalPlusNewInvoice` queda como decision documentada pero sin ejecucion Fase 1**: el BC en `ManualReviewApproved` con kind `TotalPlusNewInvoice` queda parado. Fase 2 implementa nueva factura. Tests cubren que Fase 1 no avanza ese caso (no emite NC, no factura).
- **Heuristicas caso 4 (factura confusa) son arbitrarias**: 3 reglas + override manual + setting de regex configurable. Probable falsos positivos con facturas viejas bien hechas. Mitigacion: admin aprueba igual con justificacion.
- **`Supplier.PenaltyPolicyJson` como columna JSON**: no queryable nativamente. Si reporting requiere consulta cross-supplier, tradeoff con `JSON_VALUE`. Aceptado por baja frecuencia.
- **Plan funcional menciono Postgres**: ajustado a SQL Server en este ADR. El subagente integrado heredo el lenguaje de ADR-002 que originalmente fue Postgres (cambio de stack post-FC1.1). Cualquier referencia futura debe usar T-SQL.
- **Penalidad operador en `CommissionOnly`**: hipotesis Fase 1 es "no reduce NC al cliente". Si contador responde lo contrario (F1), cambia formula en STEP 4 calculator. **Cambio aislado** (un metodo del service).

### 3.3 Neutras / a futuro

- **Prorrateo IVA proporcional al neto**: asumido Fase 1, confirmado en Fase 2 con contador. Si cambia, afecta solo `AfipService.EmitirNotaCreditoAsync` (Fase 2).
- **Multimoneda en NC parcial**: flagged como `MultiCurrency` reason que dispara revision manual. Fase 1 no implementa, Fase 2 si.
- **Rol contador real (G5)**: hoy admin con comentario reforzado. Cuando exista rol nuevo, se agrega permiso `cancellations.partial_nc_accounting_review` + filtro en bandeja.

---

## 4. Alternativas consideradas

| Alternativa | Por que NO |
|---|---|
| **`FiscalLiquidation` como entidad propia con su FK** | Sin caso de uso de consulta independiente del BC. Owned VO mantiene cohesion + simetria con `FiscalSnapshot` (precedente FC1.1). Si en futuro necesitamos historial de liquidaciones, se promueve a entidad sin perdida de datos. |
| **`Supplier.PenaltyPolicyJson` como tabla `SupplierPenaltyTier` normalizada** | Sin caso de uso de queries cross-supplier (no hay reporte). Acceso patron es leer una tabla por flujo Draft/Confirm. JSON acomoda evolucion de schema (4 tiers hoy, curve-based manana). Validacion en service con FluentValidation. Convencion existente en repo (`FiscalSnapshot.ExtrasJson`, `HotelBooking.RoomingAssignmentsJson`). |
| **Logica clasificador dentro de `BookingCancellationService.ConfirmAsync`** | El metodo ya tiene 10+ pasos. Sumar 60 lineas de clasificador lo hace ilegible. Testear obliga a setup TestContainers completo para cada variante de matriz. Service aparte es unit-test puro. |
| **Tabla nueva `PartialCreditNoteApprovalDetails` con FK 1:1 a `ApprovalRequest`** | `ApprovalRequest.Metadata` ya tipado JSON arbitrario por convencion B1.15. Tabla relacional para data semi-estructurada con apend-only de ediciones es overkill. Mismo riesgo, mas mantenimiento. |
| **Bandeja UI nueva separada del `ApprovalRequestsController`** | Decision G2 cerrada por Gaston: reusar bandeja existente. Soporta filtro por tipo + ownership en su diseno. |
| **Reusar `EnableNewCancellationFlow` para FC1.3** | Acopla rollback de FC1.2 con FC1.3. Si FC1.3 explota, apagar este flag mata tambien FC1.2 funcional. Composabilidad rota. Dos flags es minima complejidad para maxima granularidad. |
| **Auto-emision sin disparadores hasta cualquier monto** | El contador dijo textual "casos sensibles requieren revision manual". Auto-emision para todo viola criterio. Thresholds + Factura A + items no reintegrables son barreras minimas. |
| **Sin enum `CreditNoteKind`, inferir del valor `FiscalAmountToCredit`** | Casos 4 y 7 (NC total + nueva factura) tienen mismo `FiscalAmountToCredit` que casos 2 y 6 (NC total simple). Sin enum explicito, Fase 2 no sabria que hacer. |
| **ND complementaria para cliente RI por `FinalNetInvoiced`** | G4 cerrada: NO. Factura A original + NC parcial alcanzan. ADR-002 §2.2 punto 2 vigente. |
| **Persistir TODA la matriz 8 en BD como tabla `PartialCreditNoteCaseRule` para queryability** | YAGNI. La matriz son 8 reglas que ya son codigo. Si manana cambia, edit codigo + redeploy (15 min). Persistir reglas en BD agrega complejidad sin ventaja. |
| **Bloquear cambio de `Supplier.InvoicingMode` post-emisiones de factura para evitar mismatch** | Operativamente molesto (el operador puede cambiar contractualmente). Solucion correcta es snapshot (`InvoicingModeAtEvent`) que permite reconstruir el contexto historico. |

---

## 5. Migration plan / rollback

### 5.1 Migraciones EF (orden + dependencias)

5 migraciones agrupadas por aggregate afectado. Cada una es **aditiva no destructiva** (columnas nullable, sin DROP). Orden de aplicacion sequential. Dependencias entre filas explicitas.

| # | Nombre | Cambios | Depende de |
|---|---|---|---|
| **M1** | `FC1_3_0_AddSupplierInvoicingModeAndPenaltyPolicy` | `Supplier.InvoicingMode` (int default 0) + `Supplier.PenaltyPolicyJson` (nvarchar(max) null). | — |
| **M2** | `FC1_3_1_AddInvoiceItemRefundabilityAndCategory` | `InvoiceItem.IsRefundable` (bit default 1) + `InvoiceItem.ItemCategory` (int default 0) + `InvoiceItem.SourceServicioReservaId` (int? FK `ServiciosReserva.Id`, `OnDelete: SetNull`) + index. | — |
| **M3** | `FC1_3_2_AddOperationalFinanceSettingsThresholdsAndTemplate` | 7 columnas nuevas en `OperationalFinanceSettings`: `EnablePartialCreditNotes`, `PartialNcAutoApprovalThreshold`, `PartialNcAdminReviewThreshold`, `PartialNcAccountingReviewThreshold`, `PartialNcDescriptionTemplate`, `ManualReviewMaxDaysBeforeRg4540Alert`, `Fc13DeployDate`, `GenericDescriptionPatterns`. Defaults: ver §2.3.4. | — |
| **M4** | `FC1_3_3_AddBcFiscalLiquidationAndManualReviewFields` | `BookingCancellation.FiscalLiquidation_*` (10 columnas owned VO prefijadas) + `PartialCreditNoteApprovalRequestId` (int? FK `ApprovalRequests.Id`, `OnDelete: Restrict`) + `ManualReviewerUserId/UserName/At/Comment` + extension de check de `Status` para incluir valores 8..11. Extension de `FiscalSnapshot_InvoicingModeAtEvent` + `FiscalSnapshot_OriginalInvoiceTypeAtEvent` (parte del mismo owned config). CHECK constraints `chk_BookingCancellations_fiscalliq_sum`, `chk_BookingCancellations_fiscalliq_nonneg`, `chk_BookingCancellations_manualreview_approvalref`. | M3 (settings deben existir para defaults referenciables — aunque no es estricta FK). |
| **M5** | `FC1_3_4_AddHotelBookingNonRefundableConcepts` | `HotelBooking.NonRefundableConceptsJson` (nvarchar(max) null). | — |

**Nota sobre `ApprovalRequestType` enum**: como es enum en codigo, agregar `PartialCreditNoteApproval=11` **no requiere migracion** (los valores enum se almacenan como int). El seeding de `ApprovalRequestPolicy` con defaults para este tipo se agrega en M3 (`OperationalFinanceSettings` migration) o en un seed extra dentro de M4.

**Nota sobre `BookingCancellationStatus` enum**: agregar valores 8..11 **no requiere migracion** salvo que haya un CHECK constraint que enumere valores validos (heredado de ADR-002 §2.3.3 — verificar en M4 si el CHECK actual lista valores 0..7 y extender a 0..11).

### 5.2 Backfill datos legacy

- `Supplier.InvoicingMode = TotalToCustomer` para todos los existentes (default conservador, asume reseller — comportamiento legacy).
- `Supplier.PenaltyPolicyJson = NULL` para todos los existentes (sin tabla = vendedor ingresa manual).
- `InvoiceItem.IsRefundable = true` para todos los existentes (legacy: nada se trato como no reintegrable). Confirmar con contador que esto no genera deuda fiscal — si lo hace, se ofrece script de revision manual.
- `InvoiceItem.ItemCategory = Service` para todos los existentes.
- `InvoiceItem.SourceServicioReservaId = NULL` para todos los existentes.
- `BookingCancellation.FiscalLiquidation = NULL` para BCs preexistentes (FC1.2). El service ignora BCs sin liquidacion (flag off path).
- `OperationalFinanceSettings.Fc13DeployDate = fecha de deploy FC1.3 a prod` (set manual al merge).

### 5.3 Rollback

- **Feature flag `EnablePartialCreditNotes`**: default false. Si FC1.3 explota en prod, apagar deja FC1.2 funcional. **Camino primario de rollback**.
- **Migraciones EF**: reverse por orden inverso (M5 -> M4 -> M3 -> M2 -> M1). Como todas son aditivas con columnas nullable, reverse no pierde datos. Tiempo estimado: < 5 min en BD prod.
- **Estado intermedio**: si se rollbackea M4 con BCs ya migrados a `RequiresManualReview`/`ManualReviewPending`/`ManualReviewApproved`/`ManualReviewRejected`, esos BCs quedan con Status invalido para el enum FC1.2 (8..11). **Mitigacion**: antes de rollback M4, script "BCs en estados FC1.3 -> mover a Drafted o Aborted segun corresponda" + audit log de rollback.

---

## 6. Testing strategy

### 6.1 Tests unit (clasificador) — sin DB

`IFiscalLiquidationCalculator` tests cubren la matriz 8 casos completa + edge cases. Sin TestContainers, sin DbContext. Ejecutan en milisegundos.

| Test | Caso esperado | Disparadores esperados |
|---|---|---|
| `Calculate_Case1_PartialNoPenalty_BelowThreshold_ReturnsAutoApprovable` | Case1 | None |
| `Calculate_Case2_FullCancellationNoRetention_BelowThreshold_ReturnsAutoApprovable` | Case2 | None |
| `Calculate_Case3_FullWithPenalty_BelowThreshold_ReturnsAutoApprovable` | Case3 | None |
| `Calculate_Case3_FullWithPenalty_AboveAdminThreshold_ReturnsRequiresReview` | Case3 | AmountAboveAdminThreshold |
| `Calculate_Case4_GenericDescriptionMatchesPattern_ReturnsRequiresReview` | Case4 | OriginalInvoiceUnclear |
| `Calculate_Case5_CommissionOnly_PartialReturn_ReturnsAutoApprovable` | Case5 | None |
| `Calculate_Case6_CommissionOnly_FullReturn_ReturnsAutoApprovable` | Case6 | None |
| `Calculate_Case7_InvoicingModeChanged_ReturnsRequiresReview` | Case7 | RetentionChangesNature |
| `Calculate_Case8_FacturaA_AnyAmount_ReturnsRequiresReview` | Case8 | CustomerIsRiOrFacturaA |
| `Calculate_FacturaA_PlusNonRefundable_ReturnsTwoFlags` | Case8 | CustomerIsRiOrFacturaA \| HasNonRefundableItems |
| `Calculate_LegacyInvoice_ReturnsRequiresReview` | (algun caso) | LegacyInvoice |
| `Calculate_MultiCurrency_ReturnsRequiresReview` | (algun caso) | MultiCurrency |
| `Calculate_SumValidation_NonRefundablePlusPenaltyPlusFiscal_EqualsOriginal_WithinTolerance` | — | INV-FC1.3-005 valido |
| `Calculate_SumValidation_BreaksTolerance_ThrowsInvariantViolation` | — | INV-FC1.3-005 violado |
| `Calculate_CommissionOnly_PenaltyDoesNotReduceFiscalAmount` | Case3/5 | (verifica hipótesis Fase 1) |
| `Calculate_TotalToCustomer_PenaltyReducesFiscalAmount` | Case3 | (verifica modo reseller) |
| `Calculate_AccountingThresholdNull_DoesNotFlagAccountingReview` | — | sin flag accounting |
| `Calculate_AmountAboveAccountingThreshold_FlagsAccountingReview` | — | AmountAboveAccountingThreshold |
| `Calculate_ExplanationContainsCaseName_AndReasonFlags` | — | (output narrativo) |

Total estimado: ~20 unit tests. Pattern xUnit + FluentAssertions (convencion repo).

### 6.2 Tests integration (BC service + ApprovalRequest) — con TestContainers SQL Server

Ubicacion: `src/TravelApi.Tests/Cancellation/Integration/`. Reusan `PostgresIntegrationFixture` (rename pendiente a `SqlServerIntegrationFixture` o mantener nombre por deuda tecnica heredada).

| Test | Setup | Assert |
|---|---|---|
| `Confirm_CaseAutoApprovable_TransitionsToAwaitingFiscalConfirmation` | Reserva Hotel + factura $300k Tipo C + sin items no refundable + sin penalidad | BC.Status = AwaitingFiscalConfirmation, FiscalLiquidation.ComputedCase = Case2, ApprovalRequest no creado |
| `Confirm_AboveThreshold_TransitionsToManualReviewPending` | Reserva + factura $600k Tipo C | BC.Status = ManualReviewPending, ApprovalRequest tipo PartialCreditNoteApproval Pending |
| `Confirm_FacturaA_AnyAmount_TransitionsToManualReviewPending` | Factura A $200k + cliente RI | BC.Status = ManualReviewPending, ReviewRequiredReason flag CustomerIsRiOrFacturaA |
| `Confirm_NonRefundableItem_ExcludedFromFiscalAmountToCredit` | InvoiceItem IsRefundable=false por $50k de un total $500k | FiscalLiquidation.NonRefundableItemsAmount = 50k, FiscalAmountToCredit = 450k |
| `Confirm_MixedServices_RejectsByInvariant007` | Reserva con Hotel + Vuelo | BusinessInvariantViolationException codigo INV-FC1.3-007 |
| `EditLiquidation_ByDifferentAdmin_UpdatesValuesAndAppendsAuditTrail` | BC en ManualReviewPending + admin distinto al vendedor | FiscalLiquidation actualizado + approval.Metadata.edits[] tiene entrada nueva + AuditLog BookingCancellationLiquidationEdited |
| `EditLiquidation_BySameUserAsVendor_Rejects4Eyes` | BC en ManualReviewPending + admin = vendedor | BusinessInvariantViolationException INV-FC1.3-004 |
| `ApproveLiquidation_TransitionsToManualReviewApproved` | BC en ManualReviewPending + admin distinto + comment >= 20 chars | BC.Status = ManualReviewApproved, ApprovalRequest.Status = Approved |
| `ApproveLiquidation_BelowAccountingThreshold_DoesNotRequireExtraComment` | BC con FiscalAmountToCredit < AccountingThreshold | Comment 20 chars OK |
| `ApproveLiquidation_AboveAccountingThreshold_RequiresCommentMin100Chars` | BC con FiscalAmountToCredit > AccountingThreshold (G5) | Comment 50 chars rechaza, 100 chars OK |
| `RejectLiquidation_TransitionsToManualReviewRejectedThenDrafted` | BC en ManualReviewPending + reject | BC.Status = Drafted (auto reset), FiscalLiquidation null, PartialCreditNoteApprovalRequestId null, ApprovalRequest Status = Rejected |
| `EmitCreditNote_PartialOnOriginal_TransitionsToAwaitingFiscalConfirmation_Fase1` | BC en ManualReviewApproved con CreditNoteKind=PartialOnOriginal | BC.Status = AwaitingFiscalConfirmation (Fase 1: comportamiento FC1.2 vigente con NC total — Fase 2 emite parcial real) |
| `EmitCreditNote_TotalPlusNewInvoice_StaysInManualReviewApproved_Fase1` | BC en ManualReviewApproved con CreditNoteKind=TotalPlusNewInvoice | BC.Status = ManualReviewApproved (Fase 1 no emite nueva factura), log warning visible |
| `Confirm_FeatureFlagOff_IgnoresPartialCreditNoteLogic` | Flag EnablePartialCreditNotes=false + reserva que normalmente caeria a manual review | BC.Status = AwaitingFiscalConfirmation (comportamiento FC1.2 vigente, calculator no corre) |
| `Confirm_InvoicingModeChangedSinceFactura_FlagsRetentionNature` | Supplier.InvoicingMode = CommissionOnly + FiscalSnapshot.InvoicingModeAtEvent = TotalToCustomer | BC.Status = ManualReviewPending, ReviewRequiredReason flag RetentionChangesNature |
| `Abort_FromManualReviewPending_TransitionsToAborted` | BC en ManualReviewPending + abort() | BC.Status = Aborted, ApprovalRequest.Status (queda como esta, NO se borra), audit logged |
| `Sum_FiscalAmount_NonRefundable_Penalty_NotEqualOriginal_RejectsCheckConstraint` | Bug fabricado: alterar FiscalLiquidation con valores inconsistentes | DbUpdateException por CHECK violado, mapped a HTTP 409 INV-FC1.3-005 |
| `Confirm_LegacyInvoice_FlagsLegacyAndRequiresReview` | OriginalInvoice.CreatedAt < Fc13DeployDate | reason flag LegacyInvoice |
| `Confirm_NcInChain_FlagsOtherAndRequiresReview` | OriginatingInvoice.OriginalInvoiceId != null | reason flag Other |

Total estimado: ~20 integration tests.

### 6.3 Tests E2E (happy path completo)

| Test | Flujo |
|---|---|
| `E2E_PartialCreditNote_Case8_FullHappyPath` | Reserva Hotel + factura A $1M -> Draft -> Confirm -> ManualReviewPending -> Admin Approve -> ManualReviewApproved -> EmitCreditNote -> AwaitingFiscalConfirmation -> ... resto FC1.2 |
| `E2E_PartialCreditNote_Case4_FactureUnclear_AdminRejectsAndReDrafts` | Reserva Hotel + factura confusa -> Draft -> Confirm -> ManualReviewPending -> Admin Reject -> Drafted -> Vendedor corrige -> Confirm de nuevo -> auto-approve |

### 6.4 Performance baseline

- Calculator: target < 10ms por invocacion (logica pura, sin IO).
- BC `ConfirmAsync` con clasificador: target < 50ms (incluye load Reserva + Invoice + Items + Supplier). Sin regresion sobre FC1.2.

### 6.5 Migration tests

- Aplicar M1..M5 contra dump anonimizado de staging.
- Verificar que BCs preexistentes (FC1.2) **no cambian estado** post-migracion.
- Verificar `SELECT COUNT(*) FROM BookingCancellations WHERE FiscalLiquidation_OriginalInvoiceAmount IS NOT NULL` = 0 post-migracion.
- Rollback M5..M1, verificar que dump original es restituido sin perdida.

---

## 7. Operational risks

| Riesgo | Mitigacion |
|---|---|
| **Heuristicas caso 4 producen falsos positivos masivos** (facturas viejas bien hechas flageadas como "legacy" o "confusa") | Admin puede aprobar igual con justificacion. Ademas `Fc13DeployDate` configurable: si genera mucho ruido, mover a fecha anterior o null. Settings `GenericDescriptionPatterns` ajustable. |
| **Operador cambia `InvoicingMode` durante cancelacion en vuelo** | Snapshot `InvoicingModeAtEvent` capturado en T0. Calculator usa snapshot, no valor actual. |
| **Plazo RG 4540 (15 dias) se vence con BC en `ManualReviewPending`** | Alerta a partir de `ManualReviewMaxDaysBeforeRg4540Alert` dias (default 10). Job nocturno consulta y notifica via mismo mecanismo de notification de `ApprovalRequestService`. **No auto-aprueba** — eso seria peligroso. Si admin no actua, queda mora fiscal documentada. |
| **Threshold $500k/$2M no son los del contador** | Default editable en `OperationalFinanceSettings` sin redeploy. Cuando contador responde F1, admin actualiza via panel. |
| **Items legacy con `IsRefundable=true` por backfill, y eran no reintegrables fiscalmente** | Riesgo de deuda fiscal preexistente. Mitigacion: reporte separado al contador con `InvoiceItem.ItemCategory = AdministrativeFee/Insurance/OperatorAdvance` que tienen `IsRefundable=true`. Decision contable manual. |
| **Calculator clasifica mal por bug** | Tests unit cubren matriz 8 + edges. Si pasa a prod con bug, flag off -> rollback inmediato. |
| **Admin edita liquidacion + aprueba sin descansar la firma del contador** | INV-FC1.3-009 (comment edicion != comment aprobacion) + audit log `BookingCancellationLiquidationEdited` separado de `BookingCancellationManualReviewApproved`. Trazable. |
| **Multimoneda cancelacion** | reason flag `MultiCurrency` dispara manual review obligatorio Fase 1. Fase 2 implementa logica completa. |
| **Fase 1 marca `TotalPlusNewInvoice` pero no factura** | BC queda en `ManualReviewApproved` con warning visible. Admin sabe que tiene que esperar Fase 2 o emitir manualmente. Test cubre que no se emite. |

---

## 8. Security / audit risks

- **4-eyes obligatorio en aprobacion**: INV-FC1.3-004 (admin != vendedor) + INV-FC1.3-009 (comment edicion != comment aprobacion).
- **Cross-reference auditable**: `BC.PartialCreditNoteApprovalRequestId` + `ApprovalRequest.Metadata.edits[]` + `AuditLog`. Cualquier auditor reconstruye el flujo.
- **Permisos**: reusar `cobranzas.invoice_annul` para Confirm (existente). Reusar `approvals.review` para aprobacion (existente). **No se crea permiso nuevo Fase 1.**
- **`BookingCancellationLiquidationEdited` audit log**: separado de `BookingCancellationConfirmed` para que queries de auditoria los distingan facilmente.
- **`FiscalLiquidation` immutability post-Approved**: INV-FC1.3-008. Cualquier modificacion requiere admin con override + nuevo `ApprovalRequest` (no cubierto Fase 1 — operacion no expuesta).
- **`BC.FiscalLiquidation` queda en BD aunque BC se aborte**: para audit historico. Solo se nulea en `resetToDraft()` (post-Rejection).

---

## 9. Open questions (no bloquean Fase 1, bloquean Fase 2/3)

### 9.1 Fiscales (round 3 al contador — mensaje ya redactado en `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md`)

- **F1**: threshold $500k/$2M es estricto menor o menor igual? Es el valor adecuado para MagnaTravel? Y los thresholds intermedios?
- **F2**: NC parcial cuando operador devuelve en cuotas (regla 12 ADR-002): una sola NC en T0 por total fiscal, o NCs sucesivas a medida que llega plata? **Hipótesis Fase 1**: una sola NC en T0.
- **F3**: si NC parcial ya emitida necesita correccion (admin se equivoco post-aprobacion), ND complementaria o anular + nueva NC parcial? ADR-002 §2.2 punto 2 dice "no ND" — FC1.3 podria cambiar eso.

### 9.2 De negocio / G a Gaston (resueltas 2026-05-21 pero confirmacion final)

- G1..G6 cerradas en plan funcional. Confirmacion via firma post-merge Fase 1.

### 9.3 Plan tactico (al architect-reviewer)

- ¿`FiscalLiquidation` como Owned VO es el patron correcto a largo plazo, o conviene entidad propia desde el dia 1?
- ¿`PenaltyPolicyJson` JSON es aceptable o reviewer prefiere tabla normalizada upfront?
- ¿Calculator como service separado vs metodo del BC service?
- ¿Feature flag separado es necesario?

---

## 10. Auto-critica explicita (transparencia)

1. **Verifique todo en el repo?** Si — lei plan funcional FC1.3 entero (847 lineas), ADR-002 entero (779 lineas), plan FC1.2 v3 (1106 lineas, partes relevantes), entidades clave (`BookingCancellation`, `BookingCancellationStatus`, `Invoice`, `InvoiceItem`, `Supplier`, `FiscalSnapshot`, `HotelBooking`, `OperationalFinanceSettings`, `ApprovalRequest`, `ApprovalRequestType`), `ApprovalRequestsController`, fragmento de `BookingCancellationService`. Verifique que el stack es SQL Server (override del plan funcional que decia Postgres).
2. **Asumi algo sin decirlo?** Si: (a) que `Invoice.AnnulmentApprovalRequestId` (FC1.2 v3) se puede reutilizar como patron para `BC.PartialCreditNoteApprovalRequestId`. (b) Que el callback bridge `IInvoiceAnnulmentBcBridge` (FC1.2) puede tener un primo `IPartialCreditNoteApprovalBridge` analogamente. (c) Que SQL Server 547 es el error number para CHECK violation (correcto — `error_number()=547`). Estos estan marcados en el ADR donde aparecen.
3. **Disenio coupling oculto?** El BC service recibe `IFiscalLiquidationCalculator` por DI + el callback bridge `IPartialCreditNoteApprovalBridge`. Si el bridge se rompe (ApprovalRequestService olvida llamar al callback), BC queda en `ManualReviewPending` huerfano. Mitigacion: job nocturno que reconcilia (analogamente a `ArcaAnnulmentReconciliationJob` de FC1.2). Tarea para sub-fase FC1.3.5 (no detallada Fase 1, pero anotada como deuda).
4. **Tests cubiertos?** Unit (clasificador, ~20) + Integration (BC service, ~20) + E2E (~2). Falta: tests de carga (calculator < 10ms) + tests de fallover del callback bridge.
5. **Edge case que falta?**
   - NC parcial sobre NC parcial (cadena): flag `Other`, va a manual.
   - Cancelacion con BC ya creado en estado `ClientCreditApplied` (FC1.2 vigente con NC total) y vendedor quiere "convertir" a NC parcial despues. Fase 1 NO permite: el BC ya esta avanzado, no se puede retro-clasificar. Fuera de scope.
   - Operador devuelve menos de lo esperado por la liquidacion (T2 detecta mismatch): FC1.2 ya tiene `DeductionLine.RequiresAccountingReview`. Fase 1 no agrega nada nuevo.
6. **Riesgo de datos/rollback?** Migraciones todas aditivas + nullable. Rollback por inversion. Feature flag default off. Riesgo bajo. Tarea pre-deploy: script de "BCs en estados 8..11 antes de reverse de M4".
7. **Overcomplicado?** Owned VO + 1 calculator + 1 bridge nuevo (analogo al existente) + 4 estados nuevos + 1 flag nuevo + 5 enums nuevos. **Es el minimo para cubrir la matriz 8 casos sin perder cohesion**. No introduzco MediatR, no introduzco outbox, no introduzco saga.
8. **Underdesigned?** El callback bridge `IPartialCreditNoteApprovalBridge` no esta desarrollado en detalle aca (solo mencionado). Sub-fase tactico FC1.3.4 lo cubre. Job de reconciliacion para BCs huerfanos en `ManualReviewPending` queda mencionado pero no implementado Fase 1.
9. **Mantenible por otros?** Si — patron heredado (Owned VO + bridge + service aislado). Mismo lenguaje que ADR-002 (con override de stack). Tests unit del calculator hacen la matriz 8 muy facil de re-leer y mantener.
10. **Reviewer podria rechazar?** Puntos debiles:
    - **Hipotesis "penalidad operador en `CommissionOnly` no reduce NC al cliente"** sin confirmacion contador. Cambio aislado si se equivoca.
    - **Heuristicas caso 4 son arbitrarias**. Reviewer podria pedir suprimirlas y dejar solo el checkbox manual del vendedor.
    - **Fase 1 no emite NC al ARCA real**: si el reviewer prefiere alcance bigger, Fase 1 podria incluir el plumbing de AfipService. Mi posicion: separar para reducir blast radius y para no entrar a litigar prorrateo IVA antes de tener respuesta a F1 del contador.
    - **No defini explicitamente el job de reconciliacion** para BCs huerfanos en `ManualReviewPending`. Anotado como deuda Fase 1 -> ticket FC1.3.5.
    - **Override `INV-FC1.3-007`** (servicios mixtos) admite override admin: reviewer podria rechazar porque mezcla peligrosa. Mi argumento: el over necesita justificacion 50+ chars + audit, y resuelve casos legitimos de servicios mal clasificados en repo.

---

## 11. Trazabilidad

- **Plan funcional FC1.3**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-3.md` §1..§20 (autoria `travel-agency-accountant-argentina` 2026-05-21).
- **Doc criterio contador**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md`.
- **Mensaje round 3 al contador**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\operations\2026-05-21-mensaje-contador-fc1-3-round-3.md`.
- **ADR-002 base (cancelacion/refund)**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-002-cancellation-refund.md`.
- **Plan FC1.2 v3 (referencia estilo + patrones bridge/feature-flag)**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-2.md`.
- **Entidades inspeccionadas** en `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\`: `BookingCancellation.cs`, `BookingCancellationStatus.cs`, `Invoice.cs`, `InvoiceItem.cs`, `Supplier.cs`, `FiscalSnapshot.cs`, `HotelBooking.cs`, `OperationalFinanceSettings.cs`, `ApprovalRequest.cs`, `ApprovalRequestType.cs`.
- **Service vigente BC**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Infrastructure\Services\BookingCancellationService.cs` (lineas 1-100 leidas).
- **Controller approvals**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi\Controllers\ApprovalRequestsController.cs` (verificado: acepta cualquier RequestType).

---

**Fin del ADR-009.** Listo para `software-architect-reviewer` round 1.
