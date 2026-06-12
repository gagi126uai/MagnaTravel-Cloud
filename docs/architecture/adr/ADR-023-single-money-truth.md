# ADR-023 — Fuente única de la plata: saldo de cliente unificado + Cobranza desde el libro + permisos

- **Estado:** **Aceptado** — revisado 2x por `software-architect-reviewer` (2026-06-12, ambos "Changes Required"); TODAS las correcciones (B1 vínculo factura diferido, B1-bis INV-T2-7 reformulado, B2 masking sobre LedgerSourceType crudo, M1-M4, m1-m5) aplicadas en este documento. Listo para `backend-dotnet-senior`.
- **Fecha:** 2026-06-12
- **Autor:** software-architect
- **Depende de:** ADR-020 (ciclo de vida de la reserva), ADR-021 (multimoneda), ADR-022 (libro de caja `CashLedgerEntry`)
- **Reemplaza/limpia:** los ~5 cálculos divergentes de "saldo del cliente" y la unión al vuelo de `GetHistoryAsync`
- **Sin feature flags** (regla del dueño "basta de llaves"): los cambios salen directos.

---

## 1. Contexto

El sistema tiene HOY varias formas distintas de contestar dos preguntas que deberían tener una sola respuesta:

1. **¿Cuánto me debe este cliente?**
2. **¿Qué plata se movió (Cobranza y Facturación)?**

Esto produce números que no cierran entre pantallas. ADR-021 ya estableció `ReservaMoneyByCurrency` como la verdad del saldo por moneda y `FinancePositionService.GetAccountsReceivableByCurrencyAsync` como el cálculo canónico de cuenta por cobrar. ADR-022 ya estableció `CashLedgerEntry` (libro de caja inmutable) como la verdad de la caja, y `TreasuryService` ya lee del libro. Pero la pantalla de **Clientes** y la pantalla de **Cobranza y Facturación** todavía usan cálculos viejos que no leen de esas fuentes.

### 1.1 Estado verificado en el código (evidencia)

**A) Saldos de cliente — cálculos divergentes hoy:**

| # | Lugar | Predicado de estado | Problema |
|---|-------|--------------------|----------|
| A1 | `CustomerService.GetCustomersAsync` (líneas 44-47) y `GetCustomerAsync` (70-73) — proyección `CustomerListItemDto.CurrentBalance` | `!= Cancelled && != Budget && != "Archived"` sobre `Reserva.Balance` (escalar) | Incluye `Quotation`, `Lost`, `PendingOperatorRefund`, `Closed`; suma cross-moneda en un escalar |
| A2 | `GetCustomerAccountOverviewAsync` — `CustomerAccountCustomerDto.CurrentBalance` (284-287) | mismo que A1, sobre `Reservas` del cliente (`Id`) | igual que A1 |
| A3 | `GetCustomerAccountOverviewAsync` — `CustomerAccountSummaryDto.TotalSales/TotalPaid/TotalBalance` (301-303) | **SIN filtro de estado**, todas las reservas del `PayerId` | Incluye canceladas y cotizaciones — es el número grande "Saldo Actual" del front. **El más roto.** |
| A4 | `GetCustomerAccountOverviewAsync` — `ReceivableByCurrency` (344-361, vía `BuildCustomerReceivableByCurrencyAsync`) | `ReceivableStatuses` = `{InManagement, Confirmed, Traveling, ToSettle}` sobre `ReservaMoneyByCurrency` | **CORRECTO** (lista canónica). Se conserva como referencia. |
| A5 | `ReportService.GetDetailedReceivablesAsync` (604-624) | `!= Closed && != Cancelled && != Budget` sobre `ReservaMoneyByCurrency` | Incluye `Quotation`, `Lost`, `PendingOperatorRefund` |
| A6 | `ReportService.BuildDetailedSummaryByCurrencyAsync` — `saldoPendiente` (562-573) | `!= Closed && != Cancelled && != Budget` | igual que A5 (dashboard) |
| A7 | `Customer.CurrentBalance` (entidad, `Customer.cs:26`) | **NUNCA se escribe** (zombie) | Usado en `ApplyCustomerOrdering` (559-561, ordena por 0 constante) y en Excel "Cuentas por Cobrar" (`ReportService.cs:692-695`, `WHERE CurrentBalance > 0` → reporte vacío/incorrecto) |

**B) `PaymentService.GetHistoryAsync` (262-411) vs libro de caja:**
- Une al vuelo `Payments` + `Invoices` + `ManualCashMovements` (`!IsVoided`). Verificado.
- **Sí** excluye el puente de sobrepago en `Payments`... NO. Verificado: la unión de `customerPayments` (281-315) **NO** excluye el puente (`Method == BridgeMethod && !AffectsCash && OriginalPaymentId != null`) ni filtra `Status == "Cancelled"` ni `IsDeleted`. (La exclusión del puente sí está en `GetPaymentsForReservaAsync` y en el `PaymentCount`, pero no en el historial.) → fila fantasma negativa.
- **NO** incluye `SupplierPayments` (egresos a proveedor invisibles en Cobranza).
- Anulación de cobro = soft delete (`payment.IsDeleted=true`) → desaparece del historial; en el libro queda contra-asiento visible (`TreasuryService.GetMovementsAsync` muestra original + reversa). **Dos historias distintas para el mismo hecho.**
- `FinanceHistoryItemDto` (verificado, `FinanceHistoryDto.cs`) **NO tiene `Currency`** → el front formatea todo como ARS.
- **NO** aplica masking `see_cost` (`TreasuryService` sí, vía `CostMasking`). Hoy no expone costo porque no incluye `SupplierPayments`; al incluirlos, hay que enmascarar.
- `RestorePaymentAsync` (820-838): restaura el pago (`IsDeleted=false`) y recalcula el saldo, pero **NO re-crea asiento en el libro** → queda el par original+reversa (neto 0) con un pago vivo. **Bug de integridad libro↔pagos.** Confirmado: a diferencia de `DeletePaymentAsync` (1224-1225) que sí llama `ReverseLivePaymentLedgerEntryAsync`, `RestorePaymentAsync` no tiene la contraparte.
- Pagos con `EntryType == CreditNoteReversal` (`AffectsCash=false`) aparecen como fila "Reversion"; **no** tienen asiento en el libro (correcto, no mueven caja).
- No existe imputación pago↔factura formal. `Payment.RelatedInvoiceId` existe (`Payment.cs:99`); hoy solo lo usa la reversión de NC. El dueño quiere un vínculo básico cobro↔factura (scope mínimo).

**C) Permisos:**
- `TreasuryController` (verificado): clase `[Authorize]` sin `[RequirePermission]`. `GET /summary`, `/cash-summary`, `/movements` → cualquier autenticado lee el libro. `POST/PUT/DELETE manual-movements` ya son `[Authorize(Roles="Admin")]`.
- `CustomersController` (verificado): clase `[Authorize]`. La familia `/account`, `/account/reservas`, `/account/payments`, `/account/invoices` (167-239) **no tiene permisos finos** → cualquier autenticado lee la cuenta completa de cualquier cliente.
- `LeadsController` (verificado): clase `[Authorize]`, ningún método usa `crm.view`/`crm.edit`. `WebhooksController` `POST /api/leads/{id}/whatsapp-message` (148): solo `[Authorize]`.
- Los permisos `crm.view`/`crm.edit` (`Permissions.cs:82-83`) existen y se siembran en roles, pero **no se verifican en backend**.
- `PaymentsController.GetHistory` (48-52) **ya** tiene `[RequirePermission(Permissions.CobranzasView)]`. El historial reescrito hereda ese gate; el masking de costo va dentro del service.

### 1.2 Decisiones del dueño que NO se re-litigan
1. **Saldo del cliente = solo reservas en firme** = `{InManagement, Confirmed, Traveling, ToSettle}` (misma lista que `FinancePositionService.ActiveReceivableStatuses`). Cotizaciones/presupuestos/perdidas/canceladas/cerradas **no** son deuda exigible.
2. **`CreditLimit` se saca de la vista**: fuera de DTOs y requests. La columna en DB **queda** (no borrar datos), pero deja de viajar y de poder setearse por API.
3. La parte de **plata** de "Cobranza y Facturación" (`GET /payments/history`) sale del **libro de caja** (misma fuente que Caja). Las **facturas/NC** siguen saliendo de `Invoices` (no son caja).

---

## 2. Decisión

Unificar la verdad de la plata en tres frentes, sin tablas nuevas de imputación y sin migraciones de esquema obligatorias:

1. **Una sola regla de saldo de cliente**, derivada de `ReservaMoneyByCurrency` filtrado por los estados en firme, expuesta por un único componente (`FinancePositionService` extendido) y consumida por `CustomerService` y `ReportService`. Se deja de leer `Customer.CurrentBalance` y se saca `CreditLimit` de la vista.
2. **`GetHistoryAsync` reescrito**: las filas de **plata** se leen de `CashLedgerEntry` (misma fuente que Caja, incluye pagos a proveedor con masking, reversas visibles, `Currency` en el DTO); las filas de **comprobante** se leen de `Invoices`. Se arregla `RestorePaymentAsync↔libro` y se agrega un vínculo básico cobro↔factura reusando `Payment.RelatedInvoiceId`.
3. **Permisos finos** en `treasury`, `customers/account` y `leads`.

**Principio rector:** el libro de caja (`CashLedgerEntry`) es la única fuente de "qué plata se movió". `ReservaMoneyByCurrency` es la única fuente de "cuánto se debe". Ningún consumidor recalcula esas verdades por su cuenta; las pide al componente que las posee.

---

## 3. Diseño detallado por tanda

> Las tres tandas son **independientes entre sí** y se pueden mergear por separado. Dentro de cada tanda el orden es el listado. T1 y T2 NO requieren migración de esquema (ver §5). T3 tampoco.

### TANDA T1 — Regla única de saldo de cliente

**Objetivo:** que "cuánto me debe el cliente" se calcule en un solo lugar, con un solo predicado de estado, derivado de `ReservaMoneyByCurrency`, y que los escalares de compat que el front todavía consume sean coherentes con eso.

#### T1.1 — Componente canónico (extender `FinancePositionService`)

Archivo: `src/TravelApi.Infrastructure/Services/FinancePositionService.cs` e interfaz `src/TravelApi.Application/Interfaces/IFinancePositionService.cs`.

Hoy `FinancePositionService` ya tiene `ActiveReceivableStatuses` y `GetAccountsReceivableByCurrencyAsync()` (global). Se agregan **dos métodos por cliente** que reusan el MISMO predicado, para que `CustomerService` no vuelva a escribir la lista de estados:

```csharp
// IFinancePositionService.cs — agregar:

/// <summary>
/// ADR-023 T1: saldo a COBRAR del cliente POR MONEDA (deuda exigible), derivado de
/// ReservaMoneyByCurrency de sus reservas en firme. Misma definicion canonica de "en firme"
/// que el AR global. NUNCA mezcla monedas.
/// </summary>
Task<List<FinanceCurrencyAmount>> GetCustomerReceivableByCurrencyAsync(int customerId, CancellationToken cancellationToken);

/// <summary>
/// ADR-023 T1: escalar de compat (suma cross-moneda) del saldo a cobrar de UN cliente. Solo
/// para los campos escalares que el front actual todavia lee (CurrentBalance / TotalBalance).
/// NUNCA se usa para decidir nada por moneda. Con todo en ARS coincide con la unica linea ARS.
/// </summary>
Task<decimal> GetCustomerReceivableScalarAsync(int customerId, CancellationToken cancellationToken);

/// <summary>
/// ADR-023 T1: saldo a cobrar escalar de TODOS los clientes activos de una sola pasada
/// (para el ordenamiento de la lista y el Excel). Devuelve customerId -> escalar.
/// </summary>
Task<Dictionary<int, decimal>> GetReceivableScalarByCustomerAsync(CancellationToken cancellationToken);
```

Implementación (las dos primeras son el patrón ya probado en `BuildCustomerReceivableByCurrencyAsync` de `CustomerService`, que se moverá aquí; la tercera es la versión agrupada por cliente):

```csharp
public async Task<List<FinanceCurrencyAmount>> GetCustomerReceivableByCurrencyAsync(int customerId, CancellationToken cancellationToken)
{
    var query =
        from row in _dbContext.ReservaMoneyByCurrency
        join reserva in _dbContext.Reservas on row.ReservaId equals reserva.Id
        where reserva.PayerId == customerId
            && ActiveReceivableStatuses.Contains(reserva.Status)
            && row.Balance > 0
        select new { row.Currency, row.Balance };

    var grouped = await query
        .GroupBy(x => x.Currency)
        .Select(g => new { Currency = g.Key, Amount = g.Sum(x => x.Balance) })
        .ToListAsync(cancellationToken);

    return Normalize(grouped.Select(x => (x.Currency, x.Amount)));
}

public async Task<decimal> GetCustomerReceivableScalarAsync(int customerId, CancellationToken cancellationToken)
    => (await GetCustomerReceivableByCurrencyAsync(customerId, cancellationToken)).Sum(x => x.Amount);

public async Task<Dictionary<int, decimal>> GetReceivableScalarByCustomerAsync(CancellationToken cancellationToken)
{
    var grouped = await (
        from row in _dbContext.ReservaMoneyByCurrency
        join reserva in _dbContext.Reservas on row.ReservaId equals reserva.Id
        where reserva.PayerId != null
            && ActiveReceivableStatuses.Contains(reserva.Status)
            && row.Balance > 0
        group row.Balance by reserva.PayerId!.Value into g
        select new { CustomerId = g.Key, Amount = g.Sum() })
        .ToListAsync(cancellationToken);

    return grouped.ToDictionary(x => x.CustomerId, x => EconomicRulesHelper.RoundCurrency(x.Amount));
}
```

> **Nota sobre el escalar cross-moneda:** sumar ARS + USD en un escalar es semánticamente impuro, pero es **exactamente lo que el front actual ya espera** en `CurrentBalance`/`TotalBalance`, y el desglose real por moneda viaja aparte (`ReceivableByCurrency`). El escalar es de compatibilidad, NO una fuente de decisión. Esto se documenta en el código (igual criterio que `CashSummaryDto` escalares en `TreasuryService`). El reviewer debe confirmar que es aceptable como puente hasta que el front migre 100% a por-moneda; alternativa más pura en §7 (riesgo R1).

#### T1.2 — `CustomerService` consume el componente

Archivo: `src/TravelApi.Infrastructure/Services/CustomerService.cs`.

`CustomerService` debe recibir `IFinancePositionService` por DI (hoy solo tiene `AppDbContext`). Agregar al constructor:

```csharp
private readonly IFinancePositionService _financePosition;
public CustomerService(AppDbContext dbContext, IFinancePositionService financePosition)
{
    _dbContext = dbContext;
    _financePosition = financePosition;
}
```

Cambios concretos:

- **`GetCustomersAsync` (lista, 31-49):** la proyección LINQ a SQL no puede llamar al service por fila. Estrategia: proyectar primero `CustomerListItemDto` SIN `CurrentBalance` (o con 0), materializar la página, y luego enriquecer `CurrentBalance` con el dict de `GetReceivableScalarByCustomerAsync`. Como el ordenamiento por saldo (T1.4) también necesita el dict, se obtiene una sola vez. Pseudocódigo:

  ```csharp
  var page = await projectedQuery.ToPagedResponseAsync(query, cancellationToken); // sin CurrentBalance
  var scalars = await _financePosition.GetReceivableScalarByCustomerAsync(cancellationToken);
  // mapear PublicId->Id no es trivial en el DTO; incluir Id interno temporal en la proyeccion
  // o devolver el dict por PublicId. Recomendado: la proyeccion incluye un campo interno
  // CustomerId (no en el DTO publico) o se resuelve por un segundo dict PublicId->Id.
  foreach (var item in page.Items) item.CurrentBalance = scalars.GetValueOrDefault(idByPublicId[item.PublicId], 0m);
  ```

  > **Detalle de implementación a resolver por backend:** el DTO expone `PublicId`, no `Id`. Para mapear, la opción más simple es que `GetReceivableScalarByCustomerAsync` devuelva el escalar por `PublicId` del Customer (agregar `join customer` y agrupar por `customer.PublicId`). Backend elige; el invariante es: **el `CurrentBalance` de la lista = escalar derivado de `ReservaMoneyByCurrency` en firme, NO `Customer.CurrentBalance` ni `Reserva.Balance` sin filtrar.**

- **`GetCustomerAsync` (detalle, 52-77):** mismo enriquecimiento para un solo cliente: proyectar sin `CurrentBalance` y setear `customer.CurrentBalance = await _financePosition.GetCustomerReceivableScalarAsync(id, ct)`.

- **`GetCustomerAccountOverviewAsync` (271-327):**
  - `CustomerAccountCustomerDto.CurrentBalance` (284-287): reemplazar el subquery por `await _financePosition.GetCustomerReceivableScalarAsync(id, ct)`.
  - **`CustomerAccountSummaryDto.TotalBalance` (303):** reemplazar `reservasQuery.SumAsync(r => r.Balance)` (sin filtro) por el escalar canónico `GetCustomerReceivableScalarAsync(id, ct)`. **Este es el fix del "Saldo Actual" grande y roto.**
  - **`TotalSales` (301) y `TotalPaid` (302):** hoy suman TODAS las reservas del `PayerId` sin filtro. Decisión: filtrar a los mismos estados en firme para que el trío `TotalSales − TotalPaid = TotalBalance` cierre. Se agrega al `reservasQuery` un `.Where(r => ActiveReceivableStatuses.Contains(r.Status))` ANTES de los tres `SumAsync`. (Exponer `ActiveReceivableStatuses` como `public static` en `FinancePositionService`, o un helper `IFinancePositionService.IsInFirmStatus`.) **Invariante INV-T1-2 (§4).**
  - `ReceivableByCurrency` (318) y `CreditBalanceByCurrency` (319): se conservan. `BuildCustomerReceivableByCurrencyAsync` (344-361) se **borra** de `CustomerService` y se reemplaza su llamada por `_financePosition.GetCustomerReceivableByCurrencyAsync(id, ct)` (mapear `FinanceCurrencyAmount` → `CurrencyAmountDto`). `BuildCustomerCreditByCurrencyAsync` (368-378) queda en `CustomerService` (es de `ClientCreditEntry`, no de AR).
  - `ReceivableStatuses` local de `CustomerService` (331-337) se **borra** (queda solo en `FinancePositionService`).

- **`UpdateCustomerAsync` (194-233) — fix documentType clobber:** verificado que `MapCustomer` (CustomersController:241-258) SÍ mapea `DocumentType`, y `UpdateCustomerAsync:221` hace `existing.DocumentType = customer.DocumentType`. La corrupción aparece cuando el front **omite** `documentType` en el body del PUT → `request.DocumentType == null` → se persiste null. Fix backend (no se confía en el front): **preservar el valor existente cuando el entrante es null/vacío** en `UpdateCustomerAsync`:

  ```csharp
  // ADR-023 T1: no pisar el documento con null cuando el PUT no lo manda (form que omite el campo).
  if (!string.IsNullOrWhiteSpace(customer.DocumentType))
      existing.DocumentType = customer.DocumentType;
  if (!string.IsNullOrWhiteSpace(customer.DocumentNumber))
      existing.DocumentNumber = customer.DocumentNumber;
  ```

  > Riesgo de esta decisión: con este guard ya no se puede **borrar** el documento vía PUT (mandar vacío = "no tocar"). Es aceptable: hoy no hay flujo de "borrar documento", y el daño actual (corrupción silenciosa) es peor. Si en el futuro se necesita borrar, se hace con un endpoint/intención explícita. El reviewer valida el trade-off.

#### T1.3 — `ReportService` consume el componente

Archivo: `src/TravelApi.Infrastructure/Services/ReportService.cs`. `ReportService` recibe `IFinancePositionService` por DI si no lo tiene.

- **`GetDetailedReceivablesAsync` (593-641):** el predicado `!= Closed && != Cancelled && != Budget` (610-612) se reemplaza por la lista en firme `ActiveReceivableStatuses.Contains(reservaPadre.Status)`. El resto (group by cliente+moneda, `Balance > 0`) queda. Esto alinea el dashboard "detallado" con el resto.
- **`BuildDetailedSummaryByCurrencyAsync` — `saldoPendiente` (562-573):** el predicado `!= Closed && != Cancelled && != Budget` (565-567) se reemplaza por `ActiveReceivableStatuses.Contains(reservaPadre.Status)`. (Las otras agregaciones de ese método —`ventas`, `costos`, `cobros`, `pagosProveedores`— son de **período**, no de saldo, y NO cambian.)
- **Excel "Cuentas por Cobrar" (`ExportReportAsync`, 685-708):** hoy lee `Customer.CurrentBalance > 0` (zombie → vacío). Se reescribe desde la fuente canónica:

  ```csharp
  // ADR-023 T1: el Excel de cuentas por cobrar deja de leer el zombie Customer.CurrentBalance.
  // Sale de la MISMA fuente que el dashboard/detalle: ReservaMoneyByCurrency en firme.
  var receivables = await _reportService_or_financePosition.GetDetailedReceivablesAsync(cancellationToken);
  // receivables ya viene por cliente + MONEDA. El Excel agrega una columna "Moneda"
  // (una fila por cliente+moneda) en vez de un escalar mezclado.
  ```

  > Cambio de forma del Excel: pasa de `{Cliente, Documento, Saldo}` a `{Cliente, Documento, Moneda, Saldo}` (una fila por moneda). Es lo correcto (no se puede sumar ARS+USD). El reviewer/contador confirma que la planilla por-moneda es aceptable; es consistente con ADR-021. **No** se inventa una conversión a una sola moneda.

#### T1.4 — Ordenamiento de la lista de clientes (no zombie)

`ApplyCustomerOrdering` (552-569) ordena por `customer.CurrentBalance` (siempre 0). Como el saldo real ya no vive en la entidad, el orden por saldo no se puede hacer en SQL puro sobre `Customers`. Opciones:

- **Recomendada:** cuando `SortBy == "currentbalance"`, ordenar **en memoria** la página ya enriquecida con el dict de `GetReceivableScalarByCustomerAsync`. Es decir: traer la página por el orden secundario estable (FullName), enriquecer `CurrentBalance`, y reordenar la lista materializada por ese valor. Limitación: el orden por saldo se vuelve **por página** si hay paginación server-side. Para el universo de un solo usuario sin clientes reales esto es aceptable; documentarlo.
- **Alternativa (si se quiere orden global correcto):** materializar el dict completo, ordenar los `customerId` por saldo, y paginar sobre esa lista ordenada haciendo el `Where(c => pageIds.Contains(c.Id))`. Más correcto, algo más caro. Backend elige según cuánto importe el orden global; el invariante es **no ordenar por el zombie**.

#### T1.5 — `CreditLimit` fuera de la vista *(ajustado por review M1)*

- Request: **borrar** `CreditLimit` de `CustomerUpsertRequest` (CustomersController:272) y quitar `CreditLimit = request.CreditLimit` de `MapCustomer` (:255) y `existing.CreditLimit = customer.CreditLimit` de `UpdateCustomerAsync` (:227). **Esto va en esta tanda** (ya no se puede setear por API).
- DTOs: el review marcó que borrar `CreditLimit` del DTO **antes** de tocar el front deja en blanco la tarjeta "Limite credito" visible (`CustomerAccountPage.jsx:536`) sin pasar por el UX gate. **Decisión: el campo se borra de los DTOs (`CustomerListItemDto`, `CustomerAccountCustomerDto`) EN LA MISMA tanda de frontend** que quita la tarjeta (con UX gate, ya aprobada en intención por el dueño: "sacarlo de la vista"). Hasta entonces el DTO lo sigue sirviendo en read-only.
- **La columna `Customer.CreditLimit` y `Customer.CurrentBalance` quedan en la entidad y en la DB** (no se borran datos, no hay migración). `Customer.CurrentBalance` deja de leerse por completo (queda como columna histórica); se marca con un comentario `// ADR-023: zombie, no se lee ni se escribe. No borrar (datos historicos).`

#### T1.6 — `Customer.CurrentBalance`: destino

No se sigue leyendo en ningún lado (tras T1.1-T1.4) ni escribiendo (nunca se escribió). Se deja la columna. Se agrega comentario en la entidad. No se intenta poblarla (sería una cuarta fuente de verdad).

---

### TANDA T2 — `GetHistoryAsync` reescrito desde el libro

**Objetivo:** que la pantalla "Cobranza y Facturación" muestre la MISMA plata que Caja (porque sale del mismo libro), más las facturas/NC (que no son caja).

Archivo: `src/TravelApi.Infrastructure/Services/PaymentService.cs` (`GetHistoryAsync`, 262-411) y DTO `src/TravelApi.Application/DTOs/FinanceHistoryDto.cs`.

#### T2.1 — `FinanceHistoryItemDto`: campos aditivos

Agregar SIN romper los existentes (el front se retoca después con UX gate):

```csharp
// ADR-023 T2: aditivos. Mantener todos los campos previos para compat del front actual.
public string? Currency { get; set; }          // moneda REAL del movimiento (cobro/pago/manual). null=ARS legacy
public bool IsReversal { get; set; }           // true = fila de anulacion (contra-asiento del libro)
public bool AmountMasked { get; set; }         // true = monto ocultado por falta de see_cost
public string? LedgerSourceType { get; set; }  // SourceType CRUDO del asiento (espejo de CashMovementDto.LedgerSourceType)
```

> `IsManual` ya existe; `MovementSourceType`/`MovementDirection` ya existen y se reusan para las filas de libro.
>
> **`LedgerSourceType` (crudo) es obligatorio por review B2:** si `MovementSourceType` se proyecta colapsado para compat del front (p.ej. `OperatorRefund → "ManualAdjustment"`, como hace `TreasuryService:319-322`), el masking NO puede decidirse sobre el valor colapsado o se filtra el costo del refund de operador. El masking de T2.4 decide SIEMPRE sobre este campo crudo, igual que `TreasuryService` (que para esto agregó su propio `LedgerSourceType`, 354-356).

#### T2.2 — Filas de PLATA desde `CashLedgerEntry`

Reemplazar las dos ramas `customerPayments` y `manualMovements` por una sola lectura del libro (`_dbContext.CashLedgerEntries`), proyectando a `FinanceHistoryItemDto`. La proyección es **análoga a `TreasuryService.GetMovementsAsync`** (313-357) — el mapeo de origen→descripción ya está resuelto ahí; reusar el mismo patrón de resolución por FK (`Payment`/`SupplierPayment`/`ManualCashMovement`).

Reglas de la proyección de libro:
- `EntityType = "ledger"` (o conservar `"payment"`/`"movement"` según `SourceType` para minimizar el impacto en el front; backend decide y lo documenta — **recomendado conservar** `"payment"` para `CustomerPayment`, `"movement"` para el resto, así el front actual no se rompe).
- `Amount` = `e.Amount` con signo: ingreso positivo, egreso negativo (igual que la rama `manualMovements` actual, línea 376). Para reversas, el signo invertido ya viene dado por `Direction` del contra-asiento.
- `Currency = e.Currency` (la real de caja). **Fin del bug ARS.**
- `IsReversal = e.IsReversal`. Las reversas se muestran como filas propias (igual que Caja), con `Kind = "Anulacion"` o título que lo indique. **El soft-delete ya no esconde la anulación.**
- `Kind`/`Title`/`Subtitle`: derivar de `SourceType` (Cobranza / Pago a proveedor / Caja / Anulación).
- `OccurredAt = e.OccurredAt`.
- **Incluye `SupplierPayment`** (egresos a proveedor) — visibles en Cobranza para usuarios con `view_all`/back-office (enmascarados sin `see_cost`). **Matiz del review (M4):** para roles con owner-scope, los asientos sin `ReservaId` (la mayoría de los pagos a proveedor y los ajustes manuales puros) quedan excluidos por el filtro de scope — para un Vendedor siguen mayormente invisibles, lo cual es correcto.

**Filtro de owner-scope:** hoy `GetHistoryAsync` filtra por `ownerScope` (reserva a cargo del user, 269-279). El libro tiene `ReservaId` opcional; replicar: `if (ownerScope is not null) ledger = ledger.Where(e => e.Reserva != null && e.Reserva.ResponsibleUserId == ownerScope)`. Los asientos sin reserva (ajustes manuales puros) se excluyen para roles sin `view_all`, igual que hoy con `manualMovements`.

**El puente de sobrepago NO aparece**: el puente (`Method == BridgeMethod && !AffectsCash`) tiene `AffectsCash=false`, por lo que **nunca generó asiento de caja** (`DeletePaymentAsync` solo asienta `if (payment.AffectsCash)`; `CreatePaymentAsync` idem). Al leer del libro, el puente desaparece **por construcción** — se elimina el bug de la fila fantasma sin un `Where` extra. **INV-T2-3.**

**`Status == "Cancelled"` / `IsDeleted`**: un pago soft-deleted tiene su asiento revertido en el libro (par original+reversa). Al leer del libro, ambas filas aparecen (cobro + su anulación), neteando a 0, que es la historia verdadera. No se filtra por `IsDeleted` (el libro es su propia verdad). **INV-T2-2.**

#### T2.3 — Filas de COMPROBANTE desde `Invoices`

La rama `invoices` (317-362) se **conserva** casi igual (las facturas/NC NO son caja). Cambios:
- **`Resultado`**: hoy no filtra y **queda decidido que NO filtra** (OPS-INV-001 — respuesta del dueño 2026-06-12: "todas, marcadas claro"). El historial incluye comprobantes aprobados (`"A"`), en proceso y rechazados (`"R"`), cada uno con su estado **bien visible**: `InvoiceResultado` ya viaja en el DTO; el backend además deriva `Kind`/`Title` que distinga el estado (p.ej. `Title = "Factura rechazada por ARCA"` para `"R"`), para que una factura rechazada nunca pase por aprobada ni desaparezca. No se agrega `Where` por `Resultado`.
- **Dominio real de `Resultado`** (corrección factual m1 del review): el valor de pendiente es **`"PENDING"`** (`AfipService:746,1269,1330`), no `"P"`. No escribir comparaciones contra `"P"`.
- **Comprobantes anulados** (m3 del review): una factura con `AnnulmentStatus == Succeeded` **se muestra** junto con su NC (historia completa), con título/estado que diga "anulada" — coherente con la decisión del dueño "todas, marcadas claro". No se excluye por omisión.

- `CreditNoteReversal`: los `Payment` con `EntryType == CreditNoteReversal` tenían `AffectsCash=false` → **no** están en el libro, así que **no** aparecen en la rama de plata. ¿Se siguen mostrando? Decisión: **no** como fila separada de "Reversion" en el historial nuevo — la NC ya aparece en la rama `Invoices` (es un comprobante), y su efecto en caja (si lo hubo) ya está en el libro. La fila "Reversion" actual (293-296) era una representación del `Payment` técnico, redundante con la NC. **Se elimina esa representación.** Si el reviewer considera que se pierde trazabilidad, alternativa: mantenerla con `Kind="Reversion"` leyendo de `Payments` solo los `EntryType==CreditNoteReversal` (rama chica adicional). **Recomendado eliminar**; documentar.

#### T2.4 — Masking `see_cost`

`PaymentService` debe poder decidir `CanSeeCost`. `TreasuryService` ya usa `CostMasking.CanSeeCostAsync(httpContextAccessor, permissionResolver, ct)`. `PaymentService` ya tiene `_httpContextAccessor` (lo usa en `ResolveLedgerActor`, 885-893). Inyectar `IUserPermissionResolver` (opcional, fail-closed como `TreasuryService`) y aplicar, **después** de materializar la página, el mismo masking que `TreasuryService.GetMovementsAsync` (389-397):

```csharp
// ADR-023 T2: simetria con Caja. Sin cobranzas.see_cost, los egresos de COSTO se enmascaran.
// IMPORTANTE (review B2): decidir sobre LedgerSourceType CRUDO, nunca sobre el colapsado del front.
if (!await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, cancellationToken))
{
    foreach (var item in page.Items.Where(i =>
        i.LedgerSourceType == CashLedgerSourceTypes.SupplierPayment ||
        i.LedgerSourceType == CashLedgerSourceTypes.OperatorRefund))
    {
        item.Amount = 0m;
        item.AmountMasked = true;
    }
}
```

> Corrección factual del review: `PaymentService` **ya tiene** `_permissionResolver` y `_httpContextAccessor` opcionales (líneas 28/29/54/55) — no se inyecta nada nuevo. `CostMasking` es fail-closed (sin contexto, oculta), ver R6 para el impacto en tests.

> Igual que `TreasuryService`: NO se enmascaran cobros de cliente (venta), ajustes manuales genuinos, ni la devolución física al cliente (`ClientCreditWithdrawal`). Se usa el `SourceType` crudo del asiento (proyectar `LedgerSourceType` al DTO o reusar `MovementSourceType` con el valor crudo). **INV-T2-4.**

#### T2.5 — Fix `RestorePaymentAsync` ↔ libro

`RestorePaymentAsync` (820-838) restaura el pago pero no re-asienta. Fix: tras `payment.IsDeleted=false`, re-crear el asiento vivo. La forma **correcta y simétrica** con el resto de ADR-022 es: en vez de un `Add` directo, **revertir la reversa** del delete. Pero ADR-022 prohíbe revertir una reversa (`CashLedgerEntryFactory.Reverse` tira si `original.IsReversal`). Por lo tanto, la operación es **re-asentar un asiento nuevo vivo** equivalente al original:

```csharp
// ADR-023 T2.5: restaurar un cobro re-asienta en el libro. El delete dejo el original
// IsReversed=true + su reversa (neto 0). Restaurar NO des-revierte (el libro no reescribe):
// crea un asiento vivo NUEVO equivalente al cobro, asi el neto vuelve a +Amount.
if (payment.AffectsCash)
{
    // Guard de idempotencia: no duplicar si ya existe un asiento vivo para este pago.
    var hasLive = await _dbContext.CashLedgerEntries
        .AnyAsync(e => e.PaymentId == payment.Id && !e.IsReversal && !e.IsReversed, cancellationToken);
    if (!hasLive)
    {
        var (userId, userName) = ResolveLedgerActor();
        var entry = CashLedgerEntryFactory.ForPayment(payment, userId, userName);
        _dbContext.CashLedgerEntries.Add(entry);
    }
}
await _dbContext.SaveChangesAsync(cancellationToken);
```

> Esto debe ir DENTRO de la misma `SaveChanges` que limpia `IsDeleted` (mover el `SaveChanges` después del `Add`). Resultado: libro = original(revertido) + reversa + nuevo-vivo → neto +Amount, historia completa (se ve el cobro, la anulación, y el re-cobro). **INV-T2-5.** El guard `hasLive` hace la operación idempotente (re-restaurar no duplica).

#### T2.6 — Vínculo básico cobro↔factura — **DIFERIDO (sacado de ADR-023 por review B1)**

El review verificó que reusar `Payment.RelatedInvoiceId` **congela el cobro**: `DeleteGuards.cs:285-289` bloquea borrar cualquier pago con `RelatedInvoiceId != null`, y `MutationGuards.cs:145-157` bloquea editarlo si apunta a factura viva con CAE. Esos guards asumen "`RelatedInvoiceId != null` ⇒ pago técnico de reversión de NC, intocable" (hoy solo lo setea `AfipService:1471/1618`). Un cobro normal con esa FK quedaría no-editable/no-borrable, y además lo captaría la query de reconciliación de NC parcial (`AfipService:1488`).

**Decisión:** el vínculo cobro↔factura sale de esta ADR y se hace en la tanda ARCA con **columna propia `Payment.LinkedInvoiceId`** (migración aditiva, NO consultada por los guards ni por la reconciliación). T2.1–T2.5 se implementan sin él. El campo `RelatedInvoicePublicId` NO se agrega al DTO en esta tanda.

---

### TANDA T3 — Permisos

**Objetivo:** cerrar lecturas sensibles sin gate. Patrón a seguir: `[RequirePermission(Permissions.X)]` (+ `[RequireOwnership(...)]` donde el dato sea por-cliente y exista bypass por `view_all`), como en `PaymentsController`.

#### T3.1 — `TreasuryController` (el libro)

Archivo: `src/TravelApi/Controllers/TreasuryController.cs`. Hoy clase `[Authorize]` sin permiso fino en los GET.

- `GET /summary`, `GET /cash-summary`, `GET /movements`: agregar `[RequirePermission(Permissions.CajaView)]`. (El módulo Caja ya tiene `caja.view`/`caja.edit` sembrados; el libro/tesorería es "Caja" desde la vista del dueño. Si el reviewer prefiere `cobranzas.view`, es defendible — pero `caja.view` describe mejor el recurso "arqueo/libro".)
- El masking de costo dentro de `TreasuryService` (ya existente) sigue tapando montos a quien tiene `caja.view` pero no `cobranzas.see_cost`. **No** se cambia esa lógica.
- `POST/PUT/DELETE manual-movements`: ya `[Authorize(Roles="Admin")]`. Se deja.

> Verificar que `CajaView` esté en los defaults de los roles que deben ver Caja (`Permissions.cs` `DefaultColaborador` lo tiene en línea 193; `DefaultVendedor` **no** lo tiene). Decisión del dueño implícita en ADR-022 (tesorería = back-office): Vendedor no ve el libro completo. Si el dueño quiere que el Vendedor vea Caja, se agrega `CajaView` a `DefaultVendedor` — **pregunta abierta OPS-PERM-001 (§8)**, no se asume.

#### T3.2 — `CustomersController` familia `/account`

Archivo: `src/TravelApi/Controllers/CustomersController.cs`. Endpoints 167-239.

- `GET /{id}/account`, `/account/reservas`, `/account/payments`, `/account/invoices`: agregar `[RequirePermission(Permissions.ClientesView)]` + `[RequirePermission(Permissions.CobranzasView)]` en `/account` y `/account/payments` (los que muestran montos). **Resuelto por review (m5): `RequirePermission` compone AND apilando atributos (`AllowMultiple=true`) y OR pasando varios permisos en un atributo** — no hay impedimento técnico.
- **Ampliación por review (M3):** TODO `CustomersController` está sin permiso fino, no solo `/account/*`. `GET /customers` (lista, con documento/TaxId/saldo) y `GET /customers/{id}` se gatean también con `[RequirePermission(Permissions.ClientesView)]`, y las escrituras (`POST`/`PUT`/`DELETE`) con `ClientesEdit` (verificar nombre exacto del permiso de edición de clientes en `Permissions.cs`; si no existe permiso de edición, usar `ClientesView` en lecturas y dejar escrituras como están, documentándolo). Verificar antes de mergear que los roles que operan clientes (Admin/Vendedor/Colaborador) tengan `ClientesView` en sus defaults para no romper pantallas que hoy usan.
- Ownership: la cuenta es de UN cliente, no de una reserva, y no hay `OwnedEntity.Customer` necesariamente. **No** se agrega ownership por cliente en esta tanda (no hay regla de "vendedor solo ve sus clientes" establecida); el gate por permiso `ClientesView` es suficiente para cerrar el hallazgo "cualquier autenticado lee cualquier cuenta". Si el dueño quiere scoping por vendedor, es otra tanda. **No se inventa.**

#### T3.3 — `LeadsController` + WhatsApp

Archivos: `src/TravelApi/Controllers/LeadsController.cs`, `src/TravelApi/Controllers/WebhooksController.cs` (método 148).

- Lecturas (`GET` /, /pipeline, /{id}, /journey): `[RequirePermission(Permissions.CrmView)]`.
- Escrituras (`POST`, `PUT`, `DELETE`, `PATCH /status`, `POST /activities`, `POST /convert`, **`POST /quote-draft`** — aunque devuelve 410, se gatea igual, m4 del review): `[RequirePermission(Permissions.CrmEdit)]`.
- `POST /api/leads/{id}/whatsapp-message` (WebhooksController:148): `[RequirePermission(Permissions.CrmEdit)]` (o `Permissions.MessagesSend` si se considera "mensajería"; recomendado `CrmEdit` porque opera sobre un lead). El webhook entrante público (134, sin auth) **no** se toca (es el canal de WhatsApp del bot).

> Como `crm.view`/`crm.edit` ya están sembrados en los roles (Vendedor tiene ambos, Permissions.cs:220), aplicar el gate **no rompe** a los roles que ya operan CRM. **OPS-PERM-002 RESUELTA (dueño, 2026-06-12): Leads = Vendedor y Admin.** El Colaborador NO recibe `CrmView`/`CrmEdit` (su `DefaultColaborador` no los tiene y así queda); al gatear, el Colaborador queda sin Leads **a propósito**. Si algún día hace falta, se le otorga por permiso individual. No se modifica `DefaultColaborador`.

---

## 4. Invariantes (deben quedar cubiertos por tests)

- **INV-T1-1** — El saldo a cobrar de un cliente (lista, detalle, overview, dashboard, detailed receivables, Excel) sale SIEMPRE de `ReservaMoneyByCurrency` filtrado por `{InManagement, Confirmed, Traveling, ToSettle}`. Una reserva en `Quotation`/`Budget`/`Lost`/`Cancelled`/`Closed`/`PendingOperatorRefund` NO suma a la deuda en ninguna pantalla.
- **INV-T1-2** — En `CustomerAccountSummaryDto`: `TotalSales`, `TotalPaid`, `TotalBalance` se calculan sobre el MISMO conjunto de reservas en firme, de modo que `TotalSales − TotalPaid` es coherente con `TotalBalance` (salvo redondeo cross-moneda del escalar de compat).
- **INV-T1-3** — `Customer.CurrentBalance` no se lee en ningún code-path productivo. `CreditLimit` no se acepta en ningún request (su retiro del DTO va junto con la tanda de front, ver T1.5).
- **INV-T1-4** — Un PUT de cliente que omite `documentType`/`documentNumber` NO borra el valor almacenado.
- **INV-T2-1** — Toda fila de PLATA de `GetHistoryAsync` proviene de `CashLedgerEntry`; toda fila de COMPROBANTE proviene de `Invoices`. No hay otra fuente.
- **INV-T2-2** — Un cobro anulado aparece en el historial como dos filas (cobro + anulación) que netean a 0, con la anulación visible (`IsReversal=true`). NUNCA desaparece.
- **INV-T2-3** — El puente de sobrepago (`AffectsCash=false`) NO aparece en el historial (por construcción: no tiene asiento).
- **INV-T2-4** — Sin `cobranzas.see_cost`, los montos de `SupplierPayment`/`OperatorRefund` se enmascaran (`Amount=0`, `AmountMasked=true`) en el historial, igual que en Caja. La decisión de enmascarar se toma sobre el campo **crudo `FinanceHistoryItemDto.LedgerSourceType`** (nunca sobre el `MovementSourceType` colapsado — review B2). Cobros de cliente, ajustes manuales y devolución física al cliente NO se enmascaran.
- **INV-T2-5** — Tras `RestorePaymentAsync`, existe exactamente UN asiento vivo (`!IsReversal && !IsReversed`) para ese `PaymentId`, y el neto del libro para ese pago es `+Amount`. Re-restaurar es idempotente (no duplica).
- **INV-T2-6** — `FinanceHistoryItemDto.Currency` refleja la moneda real del movimiento; un cobro en USD nunca se reporta como ARS.
- **INV-T2-7** *(reformulado por review B1)* — Para un usuario con `cobranzas.view_all` (o Admin), el universo de filas de PLATA de Cobranza == universo de movimientos de Caja del libro, mismo período/filtro (misma fuente `CashLedgerEntry`), módulo la presentación (Cobranza añade comprobantes). **Para roles con owner-scope NO se exige igualdad**: Cobranza scopea por reserva propia (Caja ni se le muestra — no tiene `caja.view`). Ambas pantallas comparten FUENTE, no scoping.
- **INV-T3-1** — Ningún endpoint de `treasury` (GET), `customers/{id}/account/*` ni `leads` responde 200 a un usuario sin el permiso correspondiente; responde 403.

---

## 5. Migraciones

- **T1: ninguna.** Se reusan `ReservaMoneyByCurrency`, `Reservas`, `ClientCreditEntry` existentes. `Customer.CurrentBalance`/`CreditLimit` quedan como columnas (no se tocan). Quitar campos de DTOs/requests no es migración (no es esquema de DB).
- **T2: ninguna.** El vínculo cobro↔factura se difirió a la tanda ARCA (T2.6, review B1) — irá con columna propia `Payment.LinkedInvoiceId` allá. Los índices del libro **ya existen** (verificado por review: `IX_CashLedgerEntries_ReservaId` y `IX_CashLedgerEntries_Currency_OccurredAt` en Adr022_M1; AppDbContext:2039/2041).
- **T3: ninguna.** Solo atributos de autorización. Por OPS-PERM-001/002 **no se toca ningún seed de rol** (Vendedor sin Caja, Colaborador sin CRM).

**CERO migraciones de esquema en las tres tandas.** (Confirmado por review.)

---

## 6. Plan de tests

> InMemory para unit; los joins usan join explícito (ya es el patrón de `FinancePositionService`) para correr igual en Postgres e InMemory. Los aspectos de índice parcial / CHECK del libro se validan en integración Postgres (ya cubiertos por ADR-022; T2 no cambia el esquema del libro).

**T1:**
- Unit `FinancePositionService`: cliente con reservas en varios estados → solo las en firme suman; multi-moneda → dos líneas, escalar = suma; cliente sin reservas en firme → 0.
- Unit `CustomerService`: `GetCustomerAccountOverviewAsync` con una reserva Cancelada + una InManagement → `TotalBalance` solo cuenta la InManagement (regresión del bug A3). `GetCustomersAsync` lista → `CurrentBalance` por la fuente canónica.
- Unit `UpdateCustomerAsync`: PUT sin `documentType` no borra el documento (INV-T1-4).
- Unit `ReportService.GetDetailedReceivablesAsync`: reserva `Lost`/`PendingOperatorRefund` NO aparece (regresión A5).
- **Tests existentes a revisar — ENUMERADOS por el review (M1/M2), no es "algunos asserts":**
  - **Rompen COMPILACIÓN** (~15 call-sites por el cambio de constructor `CustomerService(AppDbContext)` → `(AppDbContext, IFinancePositionService)` y `ReportService` ídem): `CustomerServiceTests.cs:33,55,76,136`; `Adr022Tanda3Tests.cs:426,449,466`; `ReportServiceDashboardScopingTests.cs`; `Adr022ReportBridgeFilterTests.cs:40`; `Adr021Capa7ContractTests.cs:52`; `Adr022DashboardBnaDegradationTests.cs`; `Adr018ProductFirstReconciliationTests.cs:515`. Todos se actualizan pasando `new FinancePositionService(context)` (construible solo con `AppDbContext`). **PROHIBIDO el atajo de constructor opcional/sobrecarga que deje dos caminos de saldo.**
  - **Cambian de VALOR esperado**: `Adr022Tanda3Tests.cs:470` (asserta `overview.Customer.CurrentBalance == 300m` con el subquery viejo); `Adr021Capa7ContractTests.cs:216-243` (filas/montos de `GetDetailedReceivablesAsync` con el predicado viejo). **Recomputar el número esperado A MANO sobre la regla canónica (reservas en firme), no copiar el output nuevo a ciegas.**

**T2:**
- Unit `GetHistoryAsync`: cobro normal aparece con `Currency`; cobro USD aparece como USD (INV-T2-6); cobro anulado → dos filas neteando 0 con `IsReversal` (INV-T2-2); puente de sobrepago no aparece (INV-T2-3); `SupplierPayment` aparece; sin `see_cost` → monto de proveedor enmascarado (INV-T2-4); factura `Resultado="R"` aparece con título/estado de rechazada (nunca como aprobada); NC aparece desde `Invoices`.
- Unit `RestorePaymentAsync`: tras restaurar, un solo asiento vivo, neto `+Amount` (INV-T2-5); re-restaurar idempotente.
- **Nuevos por review:** (a) INV-T2-7 corregido: mismo libro, usuario `view_all` vs usuario owner-scope → Cobranza scopeada es subconjunto; para `view_all`, universo de plata == Caja. (b) B2: `OperatorRefund` en el historial con usuario sin `see_cost` → monto enmascarado (no solo `SupplierPayment`), decidido sobre `LedgerSourceType` crudo aunque `MovementSourceType` viaje colapsado.
- **Tests existentes a revisar:** los que afirman la forma vieja del historial (fila "Reversion", ausencia de `SupplierPayments`, todo en ARS) deben actualizarse. Los tests de `GetHistoryAsync` sin HttpContext deben inyectar `IHttpContextAccessor`/`IUserPermissionResolver` o esperarán montos enmascarados por fail-closed (R6).

**T3:**
- Integración (`WebApplicationFactory`): usuario sin `caja.view` → 403 en `/treasury/movements`; sin `clientes.view` → 403 en `/customers/{id}/account`; sin `crm.view` → 403 en `/leads`; con el permiso → 200. (Patrón: ya hay tests de autorización por controller, p.ej. `InvoicesControllerAnnulAuthorizationTests`.)

---

## 7. Riesgos y trade-offs

- **R1 (escalar cross-moneda):** los escalares de compat (`CurrentBalance`, `TotalBalance`) suman ARS+USD. Es impuro pero es lo que el front actual espera; el desglose real va por moneda. **Mitigación:** documentar en código; el front migra a por-moneda en una iteración futura (UX gate). **Alternativa más pura** (rechazada por ahora): borrar los escalares del DTO y forzar al front a usar solo `ReceivableByCurrency` — rompe el front actual sin UX gate, contra la regla del dueño de no tocar UI sin consultarlo.
- **R2 (orden de lista por saldo):** con saldo fuera de la entidad, el orden global por saldo requiere materializar. Para un usuario sin clientes reales el orden por página alcanza; si molesta, §3 T1.4 alternativa. **No bloqueante.**
- **R3 (cambio de números visibles):** al corregir A3, el "Saldo Actual" grande que el dueño ve HOY va a **cambiar** (bajar, porque deja de incluir canceladas/cotizaciones). Es el comportamiento correcto, pero hay que **avisarle al dueño** que el número se mueve a propósito (no es un bug nuevo). Documento de explicación al cerrar.
- **R4 (seed de roles ya existentes):** RESUELTO por OPS-PERM-001/002 — no se modifica ningún default de rol (Vendedor sin Caja, Colaborador sin CRM), así que no hay problema de re-seed. Queda solo como nota: si en el futuro se agregan permisos a defaults, verificar que el seeding re-aplique a roles ya existentes en DB.
- **R5 (`Resultado` de Invoice):** RESUELTO por OPS-INV-001 — se muestran todos los estados con marcación clara; el riesgo de ocultar comprobantes forzados desaparece. Nuevo cuidado: el front debe distinguir visualmente rechazadas (ya recibe `InvoiceResultado`).
- **R6 (masking en historial):** `PaymentService` pasa a depender de `IUserPermissionResolver`/`IHttpContextAccessor`. En tests unitarios sin HttpContext, `CostMasking` es fail-closed (oculta). Verificar que los tests existentes de `GetHistoryAsync` sigan pasando o se ajusten para inyectar el contexto.

---

## 8. Preguntas abiertas — TODAS RESUELTAS por el dueño (2026-06-12)

- **OPS-PERM-001 — RESUELTA: el Vendedor NO ve Caja.** Tesorería/libro es back-office: solo Admin y quien tenga `caja.view`. `DefaultVendedor` queda sin `CajaView` (sin cambios de seed).
- **OPS-PERM-002 — RESUELTA: Leads = Vendedor y Admin.** El Colaborador NO recibe `CrmView`/`CrmEdit`; queda fuera de Leads a propósito al aplicar el gate. No se toca `DefaultColaborador` (el riesgo R4 de re-seed deja de aplicar).
- **OPS-INV-001 — RESUELTA: se muestran TODAS las facturas (aprobadas, en proceso y rechazadas), cada una con su estado bien visible.** No se filtra por `Resultado`; el DTO ya expone `InvoiceResultado` y el título debe distinguir el estado.
- **OPS-MONEY-001 — RESUELTA: confirmado.** El "Saldo Actual" cambia de número a propósito (deja de contar canceladas/cotizaciones); el dueño ya eligió la regla "solo reservas en firme" y está avisado del cambio visible.

---

## 9. Fuera de alcance

- **Frontend.** Todo cambio visible (nuevas columnas por moneda, ocultar `CreditLimit`, anulaciones visibles) pasa por UX gate con el dueño y `frontend-senior` **después**, en otra tanda. Esta ADR solo agrega campos al DTO sin romper los existentes. **Nota (M3 review):** tras T3, un deep-link de un Vendedor a `/cash` o de un rol sin `clientes.view` a `/customers/:id/account` devuelve 403 (el router del front no gatea inline, solo el sidebar esconde) — aceptable hoy (un solo usuario Admin), se pule en la tanda de front.
- **Vínculo cobro↔factura** (ex T2.6): diferido a la tanda ARCA con columna propia `Payment.LinkedInvoiceId` (review B1: reusar `RelatedInvoiceId` congelaba el cobro por los guards de borrado/edición).
- **Imputación parcial/compleja cobro↔factura** (un cobro repartido en varias facturas, validación de que cobros = total facturado).
- **ARCA / fiscal.** No se emite ni reclasifica ningún comprobante; T2 solo lee `Invoices`.
- **Leads funcional** (lógica de pipeline, conversión, bot). T3 solo agrega gates de permiso a los endpoints existentes.
- **Scoping de clientes por vendedor** (ownership por `Customer`). No hay regla establecida; no se inventa.
- **Backfill de `Customer.CurrentBalance`.** Queda zombie; no se puebla (sería una cuarta fuente).
- **Conversión a moneda única** en reportes/Excel. Se reporta por moneda (consistente con ADR-021).

---

## 10. Resumen para implementación (orden sugerido)

1. **T1** (saldo único) — primero, es la base conceptual y no toca el libro. Reviewer + tests. Avisar a dueño por R3/OPS-MONEY-001.
2. **T2** (historial desde libro) — depende de que el libro (ADR-022) esté desplegado (ya lo está). Reviewer + `security-data-risk-reviewer` (toca pagos/facturas/masking). Tests.
3. **T3** (permisos) — independiente; `security-data-risk-reviewer` obligatorio. Resolver OPS-PERM-001/002 y R4 antes de mergear.

Cada tanda compila y pasa tests por sí sola. Ninguna requiere migración de esquema (salvo, opcional, índices aditivos en T2 si ADR-022 no los creó).

---

## Review (software-architect-reviewer)

**Fecha:** 2026-06-12 · **Veredicto: CHANGES REQUIRED.**

El diseño es mayormente sólido y bien anclado en ADR-021/022. Las verdades de saldo (`ReservaMoneyByCurrency` + `ActiveReceivableStatuses`) y de caja (`CashLedgerEntry`) son las correctas, los bugs A1–A7 están bien diagnosticados, y T1/T3 son casi enteramente correctos. Hay **un bloqueante real** (B1) que invalida la afirmación central de T2.6, más correcciones factuales y de proceso. Detalle abajo.

### Hechos verificados contra código

- **A1/A2/A3** confirmados: `CustomerService.GetCustomersAsync:44-46` y `GetCustomerAsync:70-72` filtran solo `!= Cancelled && != Budget && != "Archived"` (incluyen Quotation/Lost/Closed/PendingOperatorRefund). `GetCustomerAccountOverviewAsync:301-303` (`TotalSales/TotalPaid/TotalBalance`) **sin filtro de estado** — el bug "Saldo Actual" grande es real.
- **A4** confirmado correcto: `BuildCustomerReceivableByCurrencyAsync:344+` usa la lista en firme sobre `ReservaMoneyByCurrency`.
- **`FinancePositionService.ActiveReceivableStatuses`** es `private static readonly` (líneas 20-26) — exponerlo `public` o vía helper es necesario y sano. (Nota: el ADR lo nombra a veces `ReceivableStatuses`; el nombre real es `ActiveReceivableStatuses`.)
- **`GetHistoryAsync`** (262-411) confirmado: une `Payments`+`Invoices`+`ManualCashMovements(!IsVoided)` vía `Concat` de IQueryables → `ToPagedResponseAsync` (UNION ALL + Skip/Take en SQL). El `Concat`+paginación seguirá traduciéndose con `CashLedgerEntries`+`Invoices` (ambas entidades EF del mismo contexto). **Pagina bien.**
- **`RestorePaymentAsync`** (820-838) confirmado: NO re-asienta en el libro; `DeletePaymentAsync` sí llama `ReverseLivePaymentLedgerEntryAsync` (1225). El fix de T2.5 (re-asentar `ForPayment` nuevo + guard `hasLive`) es correcto: `Reverse` rechaza re-revertir (factory:167-168), y el índice único parcial filtra `IsReversal=false AND IsReversed=false` (migración Adr022_M1:235) → un asiento vivo nuevo no colisiona con el original revertido. OK.
- **Índices del libro (§5/punto 7):** `IX_CashLedgerEntries_ReservaId` (migración:244) y `IX_CashLedgerEntries_Currency_OccurredAt` (:207) YA existen. NO hay índice standalone por `OccurredAt` solo; el orden del historial por `OccurredAt` no lo aprovecha óptimamente, pero **no es correctitud**. **T2 no necesita migración.** Confirmar el §5: "verificar si ADR-022 los creó" → sí, no hace falta migración de índices.
- **Masking:** `PaymentService` YA inyecta `IUserPermissionResolver? _permissionResolver` (línea 28) y `IHttpContextAccessor? _httpContextAccessor` (29), ambos **opcionales con default** (54-55). **Corrección factual: T2.4 NO necesita "inyectar" nada nuevo** — usa los campos existentes. `CostMasking.CanSeeCostAsync` es fail-closed (CostMasking.cs:20-21). El DTO no tiene campo `LedgerSourceType`; backend debe proyectar el `SourceType` crudo en `MovementSourceType` para que el masking funcione (T2.2/T2.4 ya lo insinúan; dejarlo explícito).
- **Permisos:** todas las constantes existen (`Permissions.cs`: `CajaView:65`, `ClientesView:40`, `CrmView/CrmEdit:82-83`, `CobranzasView:50`, `CobranzasSeeCost:57`). `DefaultColaborador` TIENE `CajaView` (:193) y NO tiene CRM; `DefaultVendedor` TIENE `CrmView/CrmEdit` (:220) y NO tiene `CajaView`. → OPS-PERM-001/002 son consistentes **sin tocar seeds**. **`RequirePermission` compone AND apilando atributos** (`RequirePermissionAttribute.cs:7-8`, `AllowMultiple=true`) → la duda de implementación de T3.2 está resuelta: se pueden stackear `[RequirePermission(ClientesView)]` + `[RequirePermission(CobranzasView)]`.
- **Front gating (§concern 6):** el sidebar YA esconde `/crm`(crm.view), `/cash`(caja.view), `/customers`(clientes.view) (`Sidebar.jsx:29/32/35/72`). PERO el router solo gatea `/crm` inline (`App.jsx:245`); **`/cash` (:230) y `/customers/:id/account` (:214) NO están gateados inline** — un Vendedor con deep-link a `/cash`, o cualquier rol sin `clientes.view` a `/customers/:id/account`, renderiza la página y dispara los GET que tras T3 dan 403. Pantalla rota por deep-link. Aceptable para el dueño hoy, pero **el ADR no lo documenta** (ver M3).

### Bloqueantes

- **B1 — T2.6 es FALSO que "no afecta nada / es informativo": reusar `Payment.RelatedInvoiceId` para el vínculo cobro→factura CONGELA el cobro (lo vuelve no-editable y no-borrable).** Verificado:
  - `DeleteGuards.GetPaymentDeleteBlockReasonAsync` (DeleteGuards.cs:285-289) bloquea el borrado de **cualquier** pago con `RelatedInvoiceId.HasValue`, sin importar tipo ni liveness de la factura.
  - `MutationGuards.GetPaymentMutationBlockReasonAsync` (MutationGuards.cs:145-157) bloquea la **edición** del pago si `RelatedInvoiceId` apunta a una factura viva con CAE (excluyendo NC) — es decir, el caso típico (Factura A/B/C real).

  Hoy esos guards asumen "`RelatedInvoiceId != null` ⇒ el pago está atado a un comprobante fiscal, no se toca" (solo lo setean las reversiones de NC en `AfipService:1471/1618`). Si T2.6 hace que un **cobro normal** setee `RelatedInvoiceId` por mera trazabilidad, ese cobro queda inmutable e imborrable desde la UI ("Generá una nota de crédito si corresponde"). Para un producto de un solo usuario que rutinariamente corrige/borra cobros, es una **regresión funcional**, y contradice directamente la frase del ADR ("solo trazabilidad, no afecta saldos / informativo").

  **Remediación (elegir y dejar escrita en el ADR):**
  1. **Recomendado:** NO reusar `RelatedInvoiceId`. Agregar una columna nueva nullable `Payment.LinkedInvoiceId` (puramente informativa, **no consultada por `DeleteGuards`/`MutationGuards`**). Cuesta una migración aditiva (contradice el "cero migraciones", pero es el corte limpio). Proyectar esa FK al DTO para el link.
  2. O bien **aceptar explícitamente** que el cobro vinculado se congela y **confirmarlo con el dueño** como decisión de producto (probablemente NO aceptable dado que edita/borra cobros seguido).
  3. O bien acotar los guards con un discriminador (más invasivo, toca código fiscal sensible — no recomendado para "scope mínimo").

  Mientras T2.6 siga sobre `RelatedInvoiceId`, **además** hay un efecto colateral no analizado: `AfipService:1488` (`r.Payment!.RelatedInvoiceId == invoice.OriginalInvoiceId`) hoy devuelve vacío porque ningún cobro normal setea la FK; con T2.6 esa query empezaría a captar cobros normales y a meterlos en la bandeja de reconciliación de NC parcial. Puede ser deseable, pero **no fue analizado**: si se mantiene el reuso, hay que estudiar/cubrir con test ese path; si se va por `LinkedInvoiceId` separado, desaparece. La opción 1 cierra B1 y este colateral de un solo golpe.

### Mejoras mayores (no bloqueantes, pero hacerlas)

- **M1 — Quitar `CreditLimit`/`CurrentBalance` de los DTOs (T1.5) ROMPE un campo visible AHORA, antes del UX gate.** El front renderiza `customer?.creditLimit` en una tarjeta "Limite credito" (`CustomerAccountPage.jsx:536`) y `CustomerFormModal.jsx:18` lo lee/envía. Sacar `CreditLimit` del DTO en T1 deja esa tarjeta en blanco/`$0` **sin pasar por el gate UX obligatorio del dueño** (regla dura: nada visible cambia sin consultarlo). Remediación: o (a) **conservar `CreditLimit` en el DTO** hasta que el front se actualice en la misma tanda coordinada por UX gate, o (b) mover el borrado del campo (DTO + front) a una sub-tanda detrás del UX gate. **No** shippear el borrado del DTO suelto. (Quitar `CreditLimit` del **request** sí es seguro: el front lo manda y se ignora, no rompe nada visible.)
- **M2 — Constructores que se rompen (enumerar en el plan).** T1.2 agrega `IFinancePositionService` al ctor de `CustomerService` (hoy solo `AppDbContext`, línea 15) → rompe `CustomerServiceTests` y cualquier `new CustomerService(...)`. (En cambio T2.4 **no** rompe `PaymentService` porque sus deps de masking ya son opcionales — ver hechos.) Listar los call-sites de test a actualizar (`new CustomerService(` y los 4 `new PaymentService(` solo si T2 cambiara la firma, que no lo hace). No es bloqueante pero el plan debe nombrarlo para no sorprender al backend.
- **M3 — Documentar la pantalla rota por deep-link (concern 6).** Tras T3, `/cash` (Vendedor) y `/customers/:id/account` (rol sin `clientes.view`) renderizan y reciben 403 porque el router no los gatea inline (solo el sidebar los esconde). Aceptable para el dueño hoy, pero el ADR debe **decirlo explícitamente** en §7 y dejar como follow-up: o gatear esas rutas inline como `/crm`, o que las páginas manejen 403 con estado vacío. Sin esto, el dueño verá una pantalla en error si navega directo.

### Menores

- **m1 —** `INV-T2-7` ("Caja y Cobranza muestran el MISMO universo de plata") es testeable y vale la pena un test directo que compare el set de `(SourcePublicId, Amount, Currency, IsReversal)` entre `TreasuryService.GetMovementsAsync` y la rama de plata de `GetHistoryAsync` para el mismo filtro. Cuidado con la divergencia residual de T2.3: al **eliminar** la fila "Reversion" de `CreditNoteReversal` del historial, Cobranza pierde esa representación; como la NC ya aparece por la rama `Invoices`, no se pierde trazabilidad fiscal, pero el invariante INV-T2-7 debe redactarse como "mismo universo de **caja**" (el libro), no "mismas filas" (Cobranza suma comprobantes y omite el Payment técnico de NC). Está bien, solo precisar la redacción.
- **m2 —** T1.2: `GetReceivableScalarByCustomerAsync` materializa el dict completo de todos los clientes activos por página. Para un solo usuario es trivial; dejar el comentario de que es O(clientes) por request y que si crece se pagina la fuente. Igual criterio para el orden por saldo (R2, ya documentado).
- **m3 —** El guard documentType/documentNumber (T1.2) es correcto y PATCH-like acotado. Notar que `UpdateCustomerAsync:218-227` también pisa incondicionalmente `FullName/Email/Phone/Address/Notes/CreditLimit` — misma clase de bug latente si el front omite alguno; fuera de alcance de este ADR, pero conviene anotarlo para no repetir el patrón.
- **m4 —** `RestorePaymentAsync` (T2.5): el `ForPayment` re-asienta con `Currency=payment.Currency` y `OccurredAt=payment.PaidAt`. Si el asiento original (p.ej. backfill) tenía otra `OccurredAt`, el re-cobro queda con la fecha del pago, no la del backfill. Es lo correcto (la fecha real del cobro), pero confirmarlo en el test de INV-T2-5.

### Concurrencia / idempotencia

- T2.5 `hasLive` hace el restore idempotente. Bien. Asegurar que el `SaveChanges` que limpia `IsDeleted` y el `Add` del asiento ocurran en la **misma** transacción (el ADR ya lo dice: "mover el SaveChanges después del Add"). Sin eso, una caída entre ambos deja pago vivo sin asiento (el mismo bug que se está arreglando). Cubrir con test el orden, no solo el resultado.
- T2.6 (si sobrevive como `LinkedInvoiceId`): validar que la factura pertenezca a la misma reserva del pago, como el ADR ya propone; si no, 400. OK.

### Conflictos con decisiones previas

- Ninguno con ADR-020/021/022 a nivel de diseño. El único choque es **interno al propio sistema**: B1 (reuso de `RelatedInvoiceId` colisiona con la semántica fiscal que `DeleteGuards`/`MutationGuards` ya le dan a esa columna). Las 4 preguntas del §8 están selladas por el dueño y no se re-litigan.

### Independencia de las tandas (concern 8)

- **T1** y **T3** son independientes y cada una deja el sistema consistente. ✔
- **T2** es independiente de T1/T3 EXCEPTO por B1: como está escrita (sobre `RelatedInvoiceId`) introduce la regresión de congelamiento. Con el corte a `LinkedInvoiceId` (B1 remediación 1) vuelve a ser independiente y consistente. La parte de T2 que lee del libro (T2.1–T2.5) es independiente y correcta por sí sola; **se puede mergear T2 sin T2.6** y dejar el vínculo cobro→factura para después con la columna nueva — recomendado.

### Qué cambiar concretamente en el ADR para llegar a Ready

1. **B1:** reescribir T2.6 para NO reusar `Payment.RelatedInvoiceId`. Usar columna nueva `Payment.LinkedInvoiceId` (migración aditiva) NO consultada por los guards, o separar T2.6 a otra tanda y confirmar con el dueño. Corregir las afirmaciones "informativo / no afecta nada". Analizar/anotar el colateral de `AfipService:1488` si se mantuviera el reuso.
2. **M1:** no quitar `CreditLimit`/`CurrentBalance` del **DTO** en T1 sin coordinar el front por UX gate; quitarlo solo del **request** es seguro. Reformular T1.5.
3. **M3:** documentar en §7 la pantalla rota por deep-link en `/cash` y `/customers/:id/account` (router no gatea inline) y dejar el follow-up.
4. **Correcciones factuales:** T2.4 no "inyecta" nada (ya están los campos); §5 confirmar que los índices del libro ya existen (no migración); T3.2 `RequirePermission` SÍ compone AND apilando atributos; precisar redacción de INV-T2-7.

