# ADR-022 — Circuito ERP de plata conectado: Libro de Caja persistido + imputaciones + fuente única AR/AP

- **Estado:** Accepted — Ready (round 2 + B5 cerrado). Round 1 = *Changes Required* (B1-B4 + factuales + Q1/Q3/Q4 incorporados). Round 2 = *Changes Required (acotado)*: B1-B4 verificados cerrados, único bloqueante nuevo B5 (consumo de un `ClientCreditEntry` de sobrepago chocaba con la lógica de cierre de cancelación). **B5 cerrado** (2026-06-11): regla explícita en §4.9 (el consumo total de un crédito de sobrepago NO invoca `OnAllCreditConsumedAsync`) + separación de los dos caminos de consumo (aplicar a otra reserva = `Payment` con `AffectsCash=false`, no mueve caja; devolución física = `ClientCreditWithdrawal`, sí mueve caja). Listo para construir.
- **Fecha:** 2026-06-11
- **Autor:** software-architect
- **Decisor de producto:** Gastón (único usuario; producto en desarrollo, sin clientes reales). Decisiones de negocio textuales tomadas el 2026-06-11 vía AskUserQuestion (ver §3) y las que cierran Q1/Q3/Q4 (ver §10).
- **Relacionados:**
  - ADR-021 (multimoneda por reserva/proveedor; capas 1-2 ya en `main` commit `8356bd1`, capas 4-7 en `469b507`). Define `Monedas`, `ReservaMoneyByCurrency`, `SupplierBalanceByCurrency`, `Payment.Currency/ImputedCurrency/...`, `SupplierPayment.Currency/...`, `ReservaMoneyPersister`, `SupplierDebtCalculator`/`PersistSupplierBalanceAsync`, el surrogate `Balance`, el límite contable ("nunca diferencia de cambio").
  - ADR-020 (ciclo de vida de la reserva; estados `EstadoReserva`, gates, job de cierre).
  - ADR-002 / FC1 (cancelación: crédito DIFERIDO al cliente, `OperatorRefundReceived`, `ClientCreditEntry`, `ClientCreditWithdrawal`, `ManualCashMovementBuilder`).
- **Reglas duras del dueño aplicadas:**
  - **NO feature flags.** Todo sale directo (regla 2026-06-07). Rollback = `git revert` + migraciones `Down`.
  - **Sin estimaciones de tiempo.** Este ADR define QUÉ y en qué ORDEN, nunca cuánto tarda.

---

## 1. Contexto

### 1.1 El problema de negocio

Gastón pidió "terminar toda la cadena de conexiones entre reservas / proveedores / clientes / caja; que funcione como un ERP de verdad". Hoy el sistema tiene las piezas (cobros, pagos a proveedor, movimientos manuales, deuda por moneda de ADR-021) pero **la caja no es un libro contable**: es una vista que se reconstruye al vuelo en cada consulta, y editar/borrar un pago **cambia la historia hacia atrás**. Eso es exactamente lo que un ERP no puede hacer: la caja tiene que ser un registro inmutable de hechos, y los saldos (reserva / cliente / proveedor) tienen que ser **derivados** de esos hechos, no fuentes paralelas que pueden no cerrar entre sí.

### 1.2 Estado actual del código (VERIFICADO 2026-06-11, con file:line)

**No hay libro de caja persistido (C1).** `TreasuryService.GetMovementsAsync` (`src/TravelApi.Infrastructure/Services/TreasuryService.cs:246-357`) hace tres queries en vivo (`Payments` + `SupplierPayments` + `ManualCashMovements`), las proyecta a `CashMovementDto` y las concatena con `.Concat(...)` en cada request. Los resúmenes (`GetCashSummaryAsync` :176-212, `GetSummaryAsync` :20-107, `GetCashByCurrencyAsync` :115-145) reagregan las mismas tres fuentes. **No existe ninguna entidad asiento-de-caja.** Cada consulta vuelve a unir y sumar las fuentes operativas.

**Editar/borrar un pago mueve la caja hacia atrás (C5).** `PaymentService.DeletePaymentAsync` (`:943+`) hace soft-delete (`IsDeleted=true`); `UpdatePaymentAsync` (`:882-941`) muta el `Amount` en sitio. La caja se reconstruye desde los pagos vivos, así que la "foto" del mes pasado **cambia** cuando hoy borro un cobro de hace dos meses. No hay contra-asiento; la historia vibra.

**P1 — servicio genérico desincroniza la deuda del proveedor (BUG CONFIRMADO).** El servicio genérico SÍ entra en el cálculo de deuda (`SupplierService.BuildSupplierServicesQuery` incluye `Servicios`, `:836-853`), PERO `ReservaService` al crear/editar/borrar un servicio genérico llama solo a `UpdateBalanceAsync(reservaId)` y **nunca** a `_supplierService.UpdateBalanceAsync(supplierId)`:
  - `AddServiceAsync` → `UpdateBalanceAsync(reservaId)` (`ReservaService.cs:1809`).
  - `UpdateServiceAsync` → `UpdateBalanceAsync(service.ReservaId.Value)` (`:1892`).
  - `RemoveServiceAsync` → `UpdateBalanceAsync(resId.Value)` (`:1909`).
  - **Grep confirmado:** `ReservaService` NO inyecta `ISupplierService` ni `SupplierDebtCalculator` (cero matches). El escalar `Supplier.CurrentBalance` y `SupplierBalanceByCurrency` quedan **stale** hasta que otro evento (un pago, una edición tipada) recalcule. `BookingService` (servicios tipados) sí dispara el recálculo del proveedor.

**P2 — el pago a proveedor no deja asiento de caja persistido.** `SupplierService.AddSupplierPaymentAsync` (`:512-579`) crea el `SupplierPayment`, recalcula la deuda (`PersistSupplierBalanceAsync`) y listo. El egreso solo "existe" en la caja porque `TreasuryService` lo une al vuelo. Sin libro, no hay asiento.

**P3 — supuesto leak de pagos borrados en egresos: requiere matización.** El diagnóstico decía que `TreasuryService` no filtra `IsDeleted` en `SupplierPayments` (`:132, :188, :277-301`). **Verificado:** `SupplierPayment` TIENE un query filter global `HasQueryFilter(p => !p.IsDeleted)` (`AppDbContext.cs:1163`) y `Payment` también (`:550`). Como ningún path de `TreasuryService` usa `IgnoreQueryFilters()`, los pagos soft-deleted **sí** se excluyen automáticamente. Entonces **P3 no es un leak activo por ese mecanismo**; el riesgo real es de *legibilidad/fragilidad*: el filtro es implícito, y al migrar al libro hay que asegurar que el backfill y el libro reproduzcan ese filtro explícitamente (un asiento de un pago borrado NO debe contar como movimiento vivo; debe quedar como asiento + reversa). Se trata en §6 y §9 (paso de auditoría explícita), no como hotfix de un leak inexistente.

**P4 — `SupplierPayment.ReservaId` opcional.** `SupplierPayment.ReservaId` es `int?` (`SupplierPayment.cs:18`). `SupplierPaymentRequest` ya tiene `ReservaId` y `ServicioReservaId` (`ISupplierService.cs:24-31`) pero el alta no obliga ni distingue "a cuenta". Conciliación por expediente no cierra cuando el pago va al global del proveedor.

**T2 — `ManualCashMovement` no tiene moneda.** `ManualCashMovement` (`ManualCashMovement.cs`) no tiene `Currency`; `TreasuryService` hardcodea `Monedas.ARS` (`:320`) y suma el manual a la línea ARS (`MergeIntoCurrencyTotals` :161-164). Deuda anotada en ADR-021 §3.

**T3 — `ManualCashMovement` ligado a reserva no afecta el saldo de la reserva.** `RelatedReservaId` (`ManualCashMovement.cs:43`) es solo trazabilidad; el saldo de la reserva sale de `ReservaMoneyCalculator` (servicios + pagos), no de movimientos manuales. Hoy NADIE impide crear un manual con categoría "cobro" ligado a una reserva → doble puerta para el mismo hecho económico.

**T4 — AR/AP por caminos distintos en dashboard vs tesorería.** El dashboard ya tiene `DashboardByCurrencyDto` con `CuentasPorPagar` derivado de `SupplierBalanceByCurrency` (`ReportService.cs:283-289`, ADR-021). Tesorería calcula AR contra `ReservaMoneyByCurrency` (`TreasuryService.cs:43-58`) **pero no incluye AP** (cuentas por pagar). Dos consumidores, dos formas de armar los mismos números → pueden no cerrar.

**C2 — cuenta corriente del cliente: al vuelo, sin saldo a favor.** `CustomerService.GetCustomerAccountOverviewAsync` (`:283-285, :300-302`) suma `reserva.Balance` (el **surrogate** de ADR-021, que NO es monto en multimoneda) cross-reserva e interpreta el total como deuda del cliente. **Este es el consumidor que ADR-021 §4.3 marcó como ruptura real y que las capas 6-7 todavía NO migraron** (sigue leyendo el surrogate). Además no incluye el saldo a favor (`ClientCreditEntry`).

**Lo que ADR-021 ya dejó listo y este ADR reutiliza (no reconstruye):**
- `Monedas` (catálogo ARS/USD), `ReservaMoneyByCurrency`, `SupplierBalanceByCurrency` (tablas hijas materializadas + índices por `(Currency, Balance)`).
- `Payment` y `SupplierPayment` ya tienen `Currency / ImputedCurrency / ExchangeRate / ExchangeRateSource / ExchangeRateAt / ImputedAmount`.
- `ReservaMoneyPersister.PersistAsync` (único punto de escritura del saldo de la reserva: escalar surrogate + tabla hija en la misma `SaveChangesAsync`).
- `PaymentCurrencyResolver` (resuelve/valida el bloque de moneda server-side en el alta de cobro, `PaymentService.cs:459-467`).
- `SupplierService.PersistSupplierBalanceAsync` + `CalculateSupplierDebt` (deuda por moneda self-healing por recálculo).
- `ManualCashMovementBuilder` (Domain) ya construye el movimiento de caja del refund del operador (T2) y del retiro de crédito del cliente (T3), seteando los FKs `OperatorRefundReceivedId` / `ClientCreditWithdrawalId`. **El flujo de cancelación ya crea el movimiento de caja en la misma transacción** (ADR-002 §2.3.2). Esto NO se rompe.

### 1.3 El problema de fondo, en una línea

El sistema tiene **fuentes paralelas de la misma plata** (caja al vuelo vs saldos vs deuda) y **mutabilidad retroactiva** (editar/borrar mueve la historia). Un ERP necesita lo contrario: **un libro de hechos inmutable** del que todo lo demás se deriva, y **una sola puerta** por tipo de hecho económico.

---

## 2. Modelo objetivo (regla madre, del travel-agency-domain-expert)

Todo hecho económico genera **UN asiento persistido e inmutable** en el Libro de Caja, en su **moneda real**, ligado obligatoriamente a su **origen**. El asiento es la **fuente de verdad de la CAJA**. Los saldos (reserva / cliente / proveedor) son **derivados** y conservan su propia fuente (servicios + pagos vía calculadores). **Nada se borra**: se anula con **contra-asiento (reversa)**. **Una sola puerta por tipo de hecho:**

| Hecho económico | Puerta única (origen del asiento) | Dirección |
|---|---|---|
| Cobro a cliente | `Payment` (`PaymentService`) | Income |
| Pago a proveedor | `SupplierPayment` (`SupplierService`) | Expense |
| Devolución recibida del operador | `OperatorRefundReceived` (flujo cancelación) | Income |
| Devolución física al cliente | `ClientCreditWithdrawal` (flujo cancelación) | Expense |
| Gasto/ajuste que no es ninguno de los anteriores (gastos de oficina, ajuste de caja) | `ManualCashMovement` | Income/Expense |

`ManualCashMovement` queda **solo** para hechos que no son cobro/pago/refund/devolución. Si referencia una reserva, es **informativo** y **jamás** afecta el saldo de la reserva.

---

## 3. Decisiones de negocio del dueño (2026-06-11, vía AskUserQuestion) — fijas en este ADR

1. **NO habrá "pago a cuenta" del cliente.** Todo cobro sigue imputado a UNA reserva (como hoy). No se construye esa feature.
2. **Pago a proveedor:** imputado a reserva concreta como caso normal **+ opción de anticipo "a cuenta"** sin reserva. Legacy con `ReservaId` null tolerado; el alta pasa a pedir el dato con elección explícita "a cuenta".
3. **Saldo a favor del cliente = UN solo bolsillo por moneda.** Se unifica sobre `ClientCreditEntry` (existente). Fuentes: cancelación y **sobrepago** (el sobrepago de una reserva se convierte en crédito del cliente y la reserva queda en 0 — ver §4.9 y Q1 resuelta en §10; no contradice la decisión #1, porque el cobro sigue imputándose a UNA reserva y solo el excedente se convierte). Visible en la ficha del cliente; aplicable a reservas (aplicación = **imputación**, no mueve caja) o devolución física (esa SÍ mueve caja, vía `ClientCreditWithdrawal` que ya existe).
4. **Arqueo / apertura / cierre de caja: FASE POSTERIOR.** El libro debe quedar **preparado** (saldo acumulado por moneda derivable) pero no se construyen pantallas ni flujo de arqueo ahora.

---

## 4. Decisión

### 4.1 Nueva entidad: `CashLedgerEntry` (Libro de Caja persistido)

Un asiento por cada hecho económico que mueve caja, **inmutable**. Vive en `src/TravelApi.Domain/Entities/CashLedgerEntry.cs`.

```csharp
public static class CashLedgerSourceTypes
{
    public const string CustomerPayment   = "CustomerPayment";    // origen: Payment
    public const string SupplierPayment    = "SupplierPayment";    // origen: SupplierPayment
    public const string OperatorRefund      = "OperatorRefund";     // origen: OperatorRefundReceived
    public const string ClientCreditWithdrawal = "ClientCreditWithdrawal"; // origen: ClientCreditWithdrawal
    public const string ManualAdjustment    = "ManualAdjustment";   // origen: ManualCashMovement
}

public class CashLedgerEntry : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // --- Hecho económico (inmutable una vez escrito) ---
    [MaxLength(20)] public string Direction { get; set; } = CashMovementDirections.Income; // Income | Expense (reusa CashMovementDirections)
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }               // SIEMPRE positivo; el signo lo da Direction
    [MaxLength(3)]  public string Currency { get; set; } = Monedas.ARS;                     // moneda REAL del movimiento (la que entró/salió de caja)
    [MaxLength(50)] public string Method { get; set; } = "Transfer";                        // Cash, Transfer, Card, Check...
    public DateTime OccurredAt { get; set; }                                                // cuándo ocurrió el hecho (PaidAt / ReceivedAt / OccurredAt del origen)

    // --- Trazabilidad / auditoría ---
    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;                              // cuándo se escribió el asiento (≠ OccurredAt)
    [MaxLength(20)] public string SourceType { get; set; } = CashLedgerSourceTypes.ManualAdjustment;

    // --- Origen (exactamente UNO no-null según SourceType; CHECK SQL lo garantiza) ---
    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public int? SupplierPaymentId { get; set; }
    public SupplierPayment? SupplierPayment { get; set; }
    public int? OperatorRefundReceivedId { get; set; }
    public OperatorRefundReceived? OperatorRefundReceived { get; set; }
    public int? ClientCreditWithdrawalId { get; set; }
    public ClientCreditWithdrawal? ClientCreditWithdrawal { get; set; }
    public int? ManualCashMovementId { get; set; }
    public ManualCashMovement? ManualCashMovement { get; set; }

    // --- Trazabilidad de negocio (opcional, para filtros/reportes; NO afecta saldos) ---
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // --- Reversa (contra-asiento; anular ≠ borrar) ---
    public bool IsReversal { get; set; }                 // true = este asiento revierte a otro
    public int? ReversedEntryId { get; set; }            // FK al asiento original que revierte
    public CashLedgerEntry? ReversedEntry { get; set; }
    public bool IsReversed { get; set; }                 // true = este asiento YA fue revertido (no contar dos veces)
}
```

**Por qué este shape (decisiones, no opciones):**
- **FKs opcionales tipadas en vez de FK polimórfica `(SourceType, SourceId)`.** EF Core no modela bien polimorfismo por par `(tipo, id)` y rompe la integridad referencial (no hay FK real → no hay cascada ni `JOIN` confiable). Cinco FKs nullable con un CHECK "exactamente una no-null según `SourceType`" da integridad referencial real, índices usables y queries por origen sin `switch` sobre strings. Es el mismo patrón que `ManualCashMovement` ya usa con `OperatorRefundReceivedId` / `ClientCreditWithdrawalId`.
- **`Amount` siempre positivo + `Direction`.** Igual que `ManualCashMovement` y los DTOs de tesorería actuales; evita signos ambiguos y mantiene el shape del `CashMovementDto` existente.
- **Reversa explícita (`IsReversal` + `ReversedEntryId` + `IsReversed`)** en vez de borrar/anular el asiento. El asiento original queda `IsReversed=true` y se crea un asiento espejo `Direction` invertida, `IsReversal=true`, `ReversedEntryId` apuntando al original. Los resúmenes vivos cuentan TODO lo no revertido + las reversas (que se netean): el original (+100) + su reversa (−100) = 0, y la historia queda intacta. **Decisión clave:** NO se usa `IsDeleted` en el libro. El libro nunca borra.
- **`CustomerId` denormalizado opcional** para que la cuenta corriente del cliente pueda filtrar asientos por cliente sin `JOIN` a través de la reserva (decisión de §4.7; se llena desde `Reserva.PayerId` o el origen).

**Relación con `ManualCashMovement`:** `ManualCashMovement` **NO desaparece**. Sigue siendo la entidad de los gastos/ajustes y de los movimientos de cancelación (refund/withdrawal, que YA existen y NO se tocan). Lo que cambia es que ahora un `ManualCashMovement` que se crea (vía `CreateManualMovementAsync` o el builder de cancelación) **también** genera su `CashLedgerEntry`. El asiento es la proyección persistida; el `ManualCashMovement` sigue siendo el origen del gasto/ajuste/refund.

> **Decisión firme (ex-R-1, B2):** el libro tiene **UN asiento por `ManualCashMovement`** (incluidos los de cancelación), con `SourceType` = `OperatorRefund` / `ClientCreditWithdrawal` derivado de los FKs del manual, y `SourceType` = `ManualAdjustment` si es un gasto puro. NO se asienta además el `OperatorRefundReceived`/`ClientCreditWithdrawal` por separado (sería doble conteo) — ni en runtime ni en backfill (§5.2). Una sola fila de caja por hecho; los `SourceType` específicos se derivan de los FKs ya existentes del `ManualCashMovement`. Cobro de cliente (`Payment`) y pago a proveedor (`SupplierPayment`) NO pasan por `ManualCashMovement` → esos generan su `CashLedgerEntry` directo desde su service. (Ver §4.4 transaccionalidad, §5.2 universo del backfill, §6 mapa por servicio.)

### 4.2 Índices

- `CashLedgerEntry`: índice por `(Currency, OccurredAt)` (resúmenes por mes/moneda), `(SourceType)`, y los FKs (`PaymentId`, `SupplierPaymentId`, `OperatorRefundReceivedId`, `ClientCreditWithdrawalId`, `ManualCashMovementId`, `ReservaId`, `SupplierId`, `CustomerId`) para `JOIN`/filtros y para la idempotencia del backfill.
- **Índice único parcial por origen — predicado definitivo (B4):** sobre cada FK de origen, `CREATE UNIQUE INDEX ... ON "CashLedgerEntries" (<fk>) WHERE <fk> IS NOT NULL AND "IsReversal" = false AND "IsReversed" = false`. Es decir: **a lo sumo UN asiento VIGENTE por origen**. Un asiento vigente es el que NO es reversa (`IsReversal=false`) y NO fue revertido todavía (`IsReversed=false`). Esto convive con el ciclo de edición de §4.5: cuando se edita el monto, el asiento viejo pasa a `IsReversed=true` (sale del índice) ANTES de insertar el nuevo, así nunca hay dos filas que compitan por el índice. La reversa (`IsReversal=true`) y el asiento revertido (`IsReversed=true`) quedan fuera del predicado y conviven para siempre como historia.

> **Resuelto (ex-R-2 / B4):** el predicado anterior (`WHERE IsReversal = false`) era insuficiente: al editar monto, el asiento viejo sigue con `IsReversal=false` aunque ya esté revertido, y chocaría con el asiento nuevo (dos filas no-reversa por el mismo `PaymentId`). El predicado correcto es `WHERE <fk> IS NOT NULL AND IsReversal=false AND IsReversed=false`. Postgres soporta índice único parcial (`CREATE UNIQUE INDEX ... WHERE`); EF lo emite vía `HasIndex(...).IsUnique().HasFilter("...")`. La transacción de edición (§4.5) garantiza el orden marcar-viejo-antes-de-insertar-nuevo.

### 4.3 CHECK constraints (vía migración aditiva, EF `HasCheckConstraint` — sin SQL crudo de ALTER de columnas existentes)

- `chk_cashledger_amount_positive`: `Amount > 0`.
- `chk_cashledger_direction`: `Direction IN ('Income','Expense')`.
- `chk_cashledger_exactly_one_source`: exactamente uno de los 5 FKs de origen es no-null (expresión booleana sobre `(... IS NOT NULL)::int` sumando = 1). **Nota de la lección M2:** esto es un CHECK declarado vía `HasCheckConstraint` en la configuración EF, que EF emite como parte del `CREATE TABLE`/`AddCheckConstraint` de una **tabla nueva** — no es un `ALTER` con SQL crudo sobre columnas preexistentes con `HasColumnName` histórico, así que no reproduce el incidente `TravelFileId` vs `ReservaId`. Aun así, se prueba contra Postgres real antes de mergear (§8).

### 4.4 Transaccionalidad — el asiento se escribe con el hecho

El asiento se escribe en la **misma `SaveChangesAsync`** que el hecho que lo origina, no en un paso separado. Esto evita el patrón actual de "muta y después recalcula en otro `SaveChanges`" que ADR-020 documentó como riesgo (saves separados post-commit).

| Servicio | Hecho | Qué se agrega |
|---|---|---|
| `PaymentService.RegisterPayment` (`:469-489`) | crea `Payment` | en la misma `SaveChangesAsync` se agrega un `CashLedgerEntry` (`SourceType=CustomerPayment`, `Direction=Income`, `Amount=Payment.Amount`, `Currency=Payment.Currency` — la moneda REAL de caja, NO la imputada). Setear `Payment` navigation (FK se resuelve en orden topológico, igual que el builder de refund). |
| `PaymentService.DeletePaymentAsync` / `UpdatePaymentAsync` | anula/edita `Payment` | **reversa** (ver §4.5). |
| `SupplierService.AddSupplierPaymentAsync` (`:567-576`) | crea `SupplierPayment` | en la misma transacción, `CashLedgerEntry` (`SourceType=SupplierPayment`, `Direction=Expense`, `Currency=SupplierPayment.Currency`). |
| `SupplierService.DeleteSupplierPaymentAsync` / `UpdateSupplierPaymentAsync` | anula/edita | reversa. |
| Flujo cancelación (`ManualCashMovementBuilder` + `OperatorRefundService`) | crea `ManualCashMovement` para refund (T2) / withdrawal (T3) | en la misma transacción que YA crea el `ManualCashMovement`, se agrega el `CashLedgerEntry` con `SourceType` derivado de los FKs del manual. **No se duplica:** un solo asiento por `ManualCashMovement` (ver §5.2). **Moneda del asiento de cancelación — REGLA EXPLÍCITA (B1):** el `ManualCashMovement` de cancelación NO tiene la moneda real del hecho (su columna `Currency` nace con default `'ARS'`, §4.12), así que la factory **NO** puede tomar la moneda de ahí. La toma del **origen real** vía los FKs del manual: si `OperatorRefundReceivedId` no es null → `Currency`/`ExchangeRate` salen de `OperatorRefundReceived.Currency` + `OperatorRefundReceived.ExchangeRateAtReceipt` (**campos que YA existen hoy**, `OperatorRefundReceived.cs:58-64` — verificado); si `ClientCreditWithdrawalId` no es null → salen de la moneda del `ClientCreditEntry` padre del retiro (`ClientCreditWithdrawal.Entry.Currency`, el campo que este ADR agrega en §4.9). Así un refund en USD asienta en USD aunque su `ManualCashMovement` tenga `Currency='ARS'`. |
| `TreasuryService.CreateManualMovementAsync` (`:359-383`) | crea `ManualCashMovement` (gasto/ajuste) | en la misma transacción, `CashLedgerEntry` (`SourceType=ManualAdjustment`). `UpdateManualMovementAsync` / `DeleteManualMovementAsync` → reversa. |

**Punto único de escritura del asiento:** se extrae un helper de Domain `CashLedgerEntryFactory` (funciones puras que construyen el `CashLedgerEntry` a partir de cada origen, espejo de `ManualCashMovementBuilder`), para que la lógica de mapeo origen→asiento viva en un solo lugar testeable y no se duplique en 4 services. Los services solo hacen `db.CashLedgerEntries.Add(CashLedgerEntryFactory.ForPayment(payment, actor));`.

> **Punto que el reviewer debe desafiar (R-3):** ¿hace falta envolver explícitamente en `IDbContextTransaction`? Hoy varios services hacen `Add` + `SaveChangesAsync` y un `SaveChanges` separado para el recálculo. Si el asiento va en el **mismo** `Add` antes del primer `SaveChangesAsync`, es atómico sin transacción explícita. PERO el recálculo de saldo (`ReservaMoneyPersister` / `PersistSupplierBalanceAsync`) corre en un `SaveChanges` posterior. ¿Asiento + recálculo deben ser una sola transacción? Propuesta: el asiento NO depende del recálculo (el asiento es el hecho de caja; el saldo es derivado y self-healing). Si el recálculo falla después de asentar, el saldo se reconcilia en el próximo recálculo, pero el asiento de caja ya quedó (correcto: la plata entró). Aun así, recomiendo envolver `crear hecho + asiento + recálculo` en una transacción explícita para no dejar el saldo derivado temporalmente desfasado del libro. **Reviewer: validar el trade-off atomicidad vs complejidad.**

### 4.5 Política anular ≠ borrar (reversa)

- **Borrar un cobro/pago confirmado:** el `Payment`/`SupplierPayment` puede seguir con su soft-delete actual (no se cambia ese mecanismo), PERO el libro **NO** borra ni oculta el asiento original. En una sola transacción: (1) se marca el asiento original `IsReversed=true`, y (2) se crea un asiento reversa (`IsReversal=true`, `Direction` invertida, mismo `Amount`/`Currency`, `ReversedEntryId` al original, `OccurredAt` = ahora/fecha de anulación). El libro conserva los dos asientos para siempre. Tras la baja no queda ningún asiento vigente para ese origen (el original salió del índice por `IsReversed=true`; la reversa nunca entró por `IsReversal=true`).
- **Editar el monto de un cobro/pago:** **reversa + asiento nuevo, en una sola transacción y en este orden estricto (B4):** (1) marcar el asiento viejo `IsReversed=true`; (2) insertar la reversa (`IsReversal=true`, `Direction` invertida, mismo `Amount`/`Currency` viejos, `ReversedEntryId` al viejo); (3) insertar el asiento nuevo (`IsReversal=false`, `IsReversed=false`, monto editado). El orden importa: el paso (1) saca al viejo del índice único parcial ANTES de que el paso (3) inserte el nuevo, de modo que en ningún momento hay dos asientos vigentes por el mismo `PaymentId`/`SupplierPaymentId`. Como el `Payment`/`SupplierPayment` se muta in-situ (no se borra), el saldo derivado se recalcula sobre el monto nuevo; el libro, en cambio, conserva el rastro viejo (+) → reversa (−) → nuevo, neto correcto. Esto preserva la historia (la edición no reescribe el pasado). Nota: para pagos **cruzados** (multimoneda), ADR-021 §2.8 ya prohíbe editar el monto (hay que anular+recrear); para esos, "editar monto" no existe y solo aplica anular (1 reversa) + alta nueva (1 asiento nuevo).
- **Editar datos no económicos** (Method/Reference/Notes): NO genera reversa; el asiento es inmutable en su parte económica, pero estos campos pueden actualizarse en el asiento (o, más simple y consistente con "inmutable", NO se reflejan en el asiento histórico — el asiento guarda el `Method` del momento). **Decisión propuesta:** el asiento es 100% inmutable una vez escrito; editar Method/Reference de un pago NO toca el asiento (el asiento es la foto del hecho al ocurrir). El reviewer debe confirmar que esto no confunde al usuario que ve el método viejo en el libro.

> **Punto que el reviewer debe desafiar (R-4):** los resúmenes vivos deben contar `asientos NO reversados + reversas`, o equivalentemente `todos los asientos donde se netean original+reversa`. Hay dos formas: (a) sumar `Direction*Amount` de TODAS las filas (original +, reversa −, neto 0) — simple, robusto; (b) excluir `IsReversed=true` Y `IsReversal=true` — más rápido pero frágil si una fila queda mal marcada. **Propuesta: (a)** — sumar todo con signo por `Direction`, sin excluir nada. Es la definición de un libro mayor: el saldo es la suma de todos los asientos. El reviewer debe validar performance (índice por `(Currency, OccurredAt)` + filtro de mes).

### 4.6 TreasuryService lee del libro (adiós unión al vuelo)

`GetMovementsAsync`, `GetCashSummaryAsync`, `GetSummaryAsync` (parte de caja), `GetCashByCurrencyAsync` pasan a consultar `CashLedgerEntry` en vez de unir `Payments`+`SupplierPayments`+`ManualCashMovements`. El `CashMovementDto` se mapea desde el asiento (mantiene el mismo shape: `SourceType`, `Direction`, `Amount`, `Currency`, `OccurredAt`, `Method`, `Category`, `Description`, `Reference`, `ReservaPublicId`, `SupplierPublicId`, `IsManual`). El `Description`/`Category`/`NumeroReserva`/`SupplierName` se resuelven por `JOIN` a través de los FKs del asiento (o se denormalizan en el asiento si el `JOIN` pesa — decisión de implementación; preferir `JOIN` para no duplicar texto).

**API impact:** los endpoints de tesorería **mantienen su contrato** (`CashMovementDto`, `CashSummaryDto`, `TreasurySummaryDto`, `CashByCurrencyDto`). El frontend no cambia su shape.

**Mostrar reversas — default de API (Q4 resuelta):** el libro **expone las anulaciones como filas propias** con signo invertido (transparencia estilo libro mayor): un asiento de anulación aparece como un movimiento `Direction` invertida que netea al original. Este es el comportamiento por defecto de `GetMovementsAsync`. La **presentación final en pantalla** (si la UI las muestra todas, las agrupa, o las netea visualmente) pasa por un **UX gate aparte** con Gastón; el backend siempre las devuelve para no perder trazabilidad.

**Enmascarado de costo — TRABAJO NUEVO (B3 / verificado):** hoy `TreasuryService`/`TreasuryController` **NO** tienen ningún enmascarado por `cobranzas.see_cost` (verificado: 0 matches; el único masking del sistema vive en `SupplierService` DTOs, `MaskSupplierPaymentAmountsAsync`). Como el libro **expone egresos** (pagos a proveedor = costo) en `GetMovementsAsync`/summary, hay que **AGREGAR** el enmascarado como trabajo nuevo de la capa de tesorería: un usuario sin `cobranzas.see_cost` no debe ver los montos de los movimientos `Expense` que representan costo a proveedor (`SourceType=SupplierPayment` y los egresos de cancelación), igual que hoy se enmascaran en `SupplierService`. Esto NO es reutilización de algo existente: es una pieza nueva que esta capa introduce y que el reviewer de seguridad debe validar. Ver RK-9 y §9 capa 4.

### 4.7 Fuente única AR/AP por moneda (T4)

Se extrae un servicio/query compartido `IFinancePositionService` (o métodos en un helper de lectura) que produce:
- **AR (cuentas por cobrar) por moneda:** suma de `ReservaMoneyByCurrency.Balance > 0` filtrando estados activos (la query que hoy vive en `TreasuryService.cs:43-58`).
- **AP (cuentas por pagar) por moneda:** suma de `SupplierBalanceByCurrency.Balance > 0` (la query que hoy vive en `ReportService.cs:283-289`).

Ambos consumidores (`TreasuryService.GetSummaryAsync` y `ReportService.GetDashboardAsync`) leen de este servicio único. **Tesorería pasa a incluir AP** (`AccountsPayableByCurrency`) en `TreasurySummaryDto`. Así dashboard y tesorería muestran exactamente los mismos números AR/AP porque salen de la misma query.

> **Punto que el reviewer debe desafiar (R-5):** ¿el servicio compartido vive en Application (interfaz + impl en Infrastructure) o es un helper estático de Infrastructure que recibe el `AppDbContext`? Dado que `ReservaMoneyByCurrency`/`SupplierBalanceByCurrency` son tablas EF y la query corre en SQL, propongo interfaz en Application + impl en Infrastructure (inyectable, testeable, sin ciclo: ambos services ya dependen de `AppDbContext`). Verificar que no introduce ciclo con `ReportService`/`TreasuryService`.

### 4.8 Cuenta corriente del cliente derivada de fuentes consistentes (C2)

`CustomerService.GetCustomerAccountOverviewAsync` deja de sumar el surrogate `reserva.Balance` y pasa a:
- **Saldo a cobrar por moneda:** agregar `ReservaMoneyByCurrency.Balance` (filtrando `Reserva.PayerId == id` y estados vivos) agrupado por `Currency` — la misma fuente que AR de tesorería (alineado con ADR-021 capa 6 y con §4.7). El DTO expone `List<{Currency, Balance}>` en vez de un escalar mezclado.
- **Saldo a favor por moneda:** suma de `ClientCreditEntry.RemainingBalance` del cliente, agrupado por moneda (ver §4.9).

`CurrentBalance` escalar del DTO se conserva como **semáforo** (≠0 = tiene movimiento pendiente) para no romper la lista, igual que el surrogate de la reserva; el detalle real va por moneda.

> **Punto que el reviewer debe desafiar (R-6):** el saldo a favor (crédito) y el saldo a cobrar (deuda) son ejes opuestos. ¿Se muestran como dos bloques separados por moneda, o se netean? Decisión de negocio + UX (fuera de alcance backend; el backend expone ambos por separado por moneda y la UI decide). Backend NO netea deuda de una moneda contra crédito de otra (mismo principio que el surrogate: USD no compensa ARS).

### 4.9 Saldo a favor del cliente: un bolsillo por moneda (decisión #3) — RESTRICCIÓN DE MODELO

**Hallazgo verificado (constraint duro):** `ClientCreditEntry` (`ClientCreditEntry.cs`) **NO tiene campo `Currency`**. Es un aggregate denormalizado mono-moneda (`CreditedAmount`, `RemainingBalance`, CHECK `chk_credit_remaining_non_negative`). Hoy el crédito asume implícitamente ARS.

Para "un solo bolsillo por moneda" hay que **agregar `Currency` a `ClientCreditEntry`** (migración aditiva, default ARS para histórico, espejo de lo que ADR-021 hizo con `Payment.Currency`). El origen del crédito ya conoce su moneda:
- **Cancelación (Q2 RESUELTA — verificado):** el crédito sale de `OperatorRefundAllocation.NetAmount`, y la moneda real **ya está disponible hoy**: `OperatorRefundReceived` tiene `Currency` + `ExchangeRateAtReceipt` (`OperatorRefundReceived.cs:58-64`, verificado 2026-06-11; la `OperatorRefundAllocation` hereda la moneda de su refund). Por lo tanto, cuando este ADR crea el `ClientCreditEntry` del crédito de cancelación, puede setear `ClientCreditEntry.Currency = OperatorRefundReceived.Currency` **sin tocar el módulo de cancelación** (la moneda ya viaja en el aggregate del refund). NO queda en ARS forzado: el crédito de cancelación lleva la moneda real desde el día uno. Lo único que falta es agregar la columna `Currency` a `ClientCreditEntry` (aditivo) y poblarla desde el refund en el punto donde se crea el entry.
- **Sobrepago (Q1 RESUELTA por el dueño 2026-06-11):** ver el mecanismo definido abajo. El excedente del cliente se convierte en `ClientCreditEntry` (mismo bolsillo por moneda que el crédito de cancelación), la reserva queda saldada en 0.

**Sobrepago → saldo a favor del cliente (decisión del dueño 2026-06-11, cierra Q1):**

Cuando un cobro deja la reserva con saldo a favor (el cliente pagó **más** de lo que debía en una reserva), el excedente **no** queda como `Balance` negativo de la reserva: se **convierte en saldo a favor del cliente** (`ClientCreditEntry`, el mismo bolsillo por moneda que el crédito por cancelación). La reserva queda **saldada en 0** y el excedente pasa al bolsillo del cliente. Mecanismo (manteniéndolo simple):

- **Cuándo se detecta:** al registrar/editar un cobro en `PaymentService`, después de imputar el pago a la reserva y recalcular su saldo, si el saldo de la reserva en la moneda del pago queda **a favor del cliente** (la reserva pasó a estar "sobre-pagada" en esa moneda), el excedente es el monto sobrepagado.
- **Quién crea el `ClientCreditEntry`:** `PaymentService`, en la misma transacción del cobro (no un job aparte), crea un `ClientCreditEntry` por el excedente. `CreditedAmount = RemainingBalance =` excedente; `IsFullyConsumed=false`.
- **En qué moneda:** la moneda del **saldo que se estaba pagando** (la moneda del cobro / del saldo de esa reserva), que es la misma `Currency` del `ClientCreditEntry`. El bolsillo del cliente es por moneda (mismo principio que cancelación: USD no compensa ARS).
- **Origen / trazabilidad:** el sobrepago no nace de una cancelación, así que `OperatorRefundAllocationId` y `BookingCancellationId` (hoy NOT NULL en `ClientCreditEntry`, verificado `ClientCreditEntry.cs:34,38`) **no aplican**. Esto requiere relajar esos FKs a nullable (migración aditiva) y agregar un origen de tipo "sobrepago" que apunte al `Payment`/`Reserva` que lo originó (auditoría: qué reserva sobre-pagada y qué cobro lo generó, `CreatedByUserId`/`CreatedByUserName`). **Sin esto el sobrepago no se puede modelar como `ClientCreditEntry`** — es un cambio de modelo a confirmar en implementación (ver §10 nota de implementación).
- **Caja:** el sobrepago NO mueve caja de más: el cobro entró completo (su `CashLedgerEntry` Income por el monto real cobrado). Convertir el excedente en crédito del cliente es una **imputación** (mueve de "saldo de reserva" a "bolsillo del cliente"), no un movimiento de caja nuevo. El asiento del cobro ya refleja la plata que entró; el crédito es una posición del cliente, no un egreso.

**REGLA EXPLÍCITA DEL CONSUMO DE UN CRÉDITO DE SOBREPAGO (cierra B5 — verificado en código 2026-06-11):**

El crédito de sobrepago reusa el aggregate `ClientCreditEntry` y, por lo tanto, su retiro reusa el path `ClientCreditService.WithdrawAsync`. Ese path tiene hoy, al consumir totalmente un entry, el siguiente disparo (verificado `ClientCreditService.cs:303-305`):

```csharp
if (request.Kind != WithdrawalKind.KeptAsCredit && entry.IsFullyConsumed)
    await _bcService.OnAllCreditConsumedAsync(entry.BookingCancellationId, ct);
```

`OnAllCreditConsumedAsync(int bookingCancellationId, ...)` (verificado `BookingCancellationService.cs:1232`) cierra la `BookingCancellation` indicada y cancela su `Reserva`. Ese callback **solo tiene sentido cuando el crédito nació de una cancelación**. Para un crédito de **sobrepago** no hay cancelación detrás (`BookingCancellationId` = null tras la relajación del FK), así que:

> **Regla B5:** cuando un `ClientCreditEntry` de origen "sobrepago" (`BookingCancellationId == null`) se consume totalmente, **NO se invoca `OnAllCreditConsumedAsync`** (no hay BC que cerrar). El path de consumo debe ramificar por el origen — concretamente por `entry.BookingCancellationId == null` (o, equivalente, por el discriminador de origen "sobrepago") — y **saltear** el cierre de cancelación. Solo los créditos de origen cancelación (`BookingCancellationId != null`) disparan ese callback. La condición de §303-305 pasa a ser `request.Kind != WithdrawalKind.KeptAsCredit && entry.IsFullyConsumed && entry.BookingCancellationId != null`. Sin esta guarda, el primer retiro total de un sobrepago dispararía lógica de cierre de cancelación sobre una BC inexistente (corrupción de estado o, con el FK ya nullable, una llamada con id null inválida).

**LOS DOS CAMINOS DE CONSUMO DEL CRÉDITO DE SOBREPAGO (NO mezclar; tienen efecto distinto en caja):**

El saldo a favor de un sobrepago puede consumirse por dos caminos completamente separados. Cada uno tiene su propio efecto en el libro de caja:

- **(a) APLICAR el crédito a otra reserva = imputación pura. NO mueve caja.** La plata ya entró a caja cuando se cobró el pago original (su `CashLedgerEntry` Income ya existe). Aplicar el crédito a otra reserva solo **traslada** una posición del cliente (de "bolsillo del cliente" a "saldo de la reserva destino"); no es un hecho de caja nuevo. **Mecanismo concreto (decidido):** la aplicación se materializa como un `Payment` de la reserva destino con `Method = "Crédito"` (o el método interno equivalente "saldo a favor") y **`AffectsCash = false`**. Como el libro **solo** asienta `Payment` con `AffectsCash == true` (ver universo del backfill §5.2: "cada `Payment` vivo … `AffectsCash` …"), ese `Payment` de aplicación **NO genera ningún `CashLedgerEntry`** — el libro no gana un asiento por la aplicación, que es exactamente lo correcto (la caja ya registró la entrada de plata una sola vez). El `Payment` con `AffectsCash=false` imputa el monto al saldo de la reserva destino (vía `ReservaMoneyPersister`, como cualquier cobro) y, en la misma transacción, descuenta el `RemainingBalance` del `ClientCreditEntry` de sobrepago. **Por qué `Payment(AffectsCash=false)` y no `ClientCreditWithdrawal`:** `ClientCreditWithdrawal` es la puerta de la DEVOLUCIÓN FÍSICA (mueve caja, §2 tabla "Devolución física al cliente"); usarlo para una imputación que no mueve caja confundiría las dos cosas y arriesgaría asentar un egreso inexistente. La aplicación reusa la maquinaria de imputación de saldo que `Payment` ya tiene, sin tocar el libro.
- **(b) DEVOLUCIÓN FÍSICA al cliente = `ClientCreditWithdrawal`, como hoy. SÍ mueve caja.** El cliente pide la plata de vuelta: es un egreso real. Se ejecuta por el path existente `ClientCreditService.WithdrawAsync` con `Kind = PhysicalCash`/`Transfer`, que crea el `ClientCreditWithdrawal` + su `ManualCashMovement` (T3). Ese `ManualCashMovement` genera **un** `CashLedgerEntry` Expense (regla §4.4 / §5.2), en la **moneda del `ClientCreditEntry`** (la del bolsillo de sobrepago). Este es el único camino del crédito de sobrepago que toca el libro, y se rige por la guarda B5 de arriba: como el entry no tiene cancelación detrás, el consumo total NO dispara `OnAllCreditConsumedAsync`.

> **Resumen del efecto en caja, para no volver a mezclarlo:** el cobro original asienta la plata UNA vez (Income). Aplicar el excedente a otra reserva = imputación, **cero asientos nuevos** (`Payment` con `AffectsCash=false`). Devolver el excedente en efectivo = **un** asiento Expense (vía `ClientCreditWithdrawal`/`ManualCashMovement`). Nunca hay doble conteo ni asiento fantasma.

> **Alcance del crédito por moneda en este ADR:** agregar `Currency` a `ClientCreditEntry` (aditivo, default ARS), poblarlo desde el refund en cancelación (Q2 resuelta, no toca cancelación), crear el `ClientCreditEntry` de sobrepago desde `PaymentService` (Q1 resuelta), y **exponer** el saldo a favor por moneda en la ficha del cliente. La relajación de los FKs `OperatorRefundAllocationId`/`BookingCancellationId` a nullable + el campo de origen "sobrepago" es el único cambio de modelo nuevo que el sobrepago introduce; el resto reusa el aggregate existente.

### 4.10 Fix P1: ReservaService dispara el recálculo de deuda del proveedor

`ReservaService` necesita recalcular `Supplier.CurrentBalance`/`SupplierBalanceByCurrency` cuando un servicio **genérico** con `SupplierId` se crea/edita/borra. Opciones:
- **(A) Inyectar `ISupplierService` en `ReservaService`** y llamar `_supplierService.UpdateBalanceAsync(supplierId)` (o el método que dispara `PersistSupplierBalanceAsync`). **Riesgo:** posible ciclo de dependencias (¿`SupplierService` depende de `ReservaService`?). **Verificar** antes de elegir.
- **(B) Extraer el recálculo de deuda a un componente sin estado** (`SupplierDebtRecalculator`, ya existe `SupplierDebtCalculator`/`PersistSupplierBalanceAsync` como lógica) que ambos services invocan sobre el `AppDbContext` compartido — espejo de `ReservaMoneyPersister`. Sin ciclo: ambos dependen del recalculador, no entre sí.

**Decisión propuesta: (B)**, por consistencia con el patrón `ReservaMoneyPersister` que ADR-021 ya estableció (helper de Infraestructura sin estado que opera sobre el `AppDbContext` del caller). `ReservaService.AddServiceAsync`/`UpdateServiceAsync`/`RemoveServiceAsync`, **cuando el servicio genérico tiene `SupplierId`** (o lo tenía antes de editarlo/borrarlo — hay que recalcular el proveedor **anterior** y el **nuevo** en un cambio de proveedor), llaman al recalculador del proveedor además de `UpdateBalanceAsync(reservaId)`.

> **Punto que el reviewer debe desafiar (R-7):** el cambio de proveedor en `UpdateServiceAsync` debe recalcular DOS proveedores (el viejo y el nuevo). El borrado debe recalcular el proveedor que tenía. Verificar que el recalculador se llame con el `supplierId` correcto en cada caso y que no falle si el servicio no tenía proveedor (genérico sin `SupplierId` = no toca ningún proveedor).

### 4.11 P3: auditoría explícita del filtro IsDeleted

Como el libro reemplaza la unión al vuelo, se audita que: (a) el backfill NO cree asientos vivos para pagos soft-deleted (crea original + reversa, o no crea nada con criterio explícito, ver §5); (b) los queries del libro no necesiten el query filter implícito de `Payment`/`SupplierPayment` (el libro es su propia fuente). El "leak" original de P3 no era activo (el query filter global cubre), pero el libro lo vuelve **explícito y robusto**: la verdad de caja está en el libro, no en sumar entidades con filtros implícitos.

### 4.12 T2: Currency en ManualCashMovement

Migración aditiva: `ManualCashMovement.Currency` `string(3)` NOT NULL default `'ARS'` a nivel BD (espejo de `Payment.Currency`). `CreateManualMovementAsync`/`UpdateManualMovementAsync` aceptan `Currency` del request (validado contra `Monedas`). El `CashLedgerEntry` de un **gasto/ajuste manual puro** (`SourceType=ManualAdjustment`, sin FK de cancelación) toma `Currency` del propio `ManualCashMovement` (ya no hardcodea ARS en `TreasuryService.cs:320`). El histórico queda en ARS.

> **Aclaración B1 (límite de esta columna):** el `ManualCashMovement.Currency` que se agrega acá llena la moneda **solo de los gastos/ajustes que se cargan a mano**. NO llena la moneda de los `ManualCashMovement` de cancelación (refund/withdrawal): esos nacen del flujo de cancelación con `Currency='ARS'` por default, pero su asiento toma la moneda del origen real (`OperatorRefundReceived.Currency` / `ClientCreditEntry.Currency`), según la regla explícita de §4.4. Por eso esta columna y la moneda del asiento de cancelación son cosas distintas: no derivar la segunda de la primera.

### 4.13 T3: ManualCashMovement con reserva = solo informativo + bloqueo de categorías que dupliquen

`ManualCashMovement` con `RelatedReservaId` NO afecta el saldo de la reserva (ya es así hoy; se documenta el invariante).

**Validación de categorías — DECISIÓN (Q3 resuelta, 2026-06-11): bloqueo duro de las categorías que duplican una puerta propia; el resto libre.** Se agrega validación en `CreateManualMovementAsync`/`UpdateManualMovementAsync`: las categorías que representan **cobro de cliente** o **pago a proveedor** se **rechazan** (error de validación, no warning) desde el alta manual — esos hechos tienen su puerta única (`Payment` / `SupplierPayment`) y un manual no puede impersonarlos. Cualquier otra categoría (gastos de oficina, ajustes de caja, etc.) es **libre**. La lista concreta de etiquetas bloqueadas (ej. `Cobranza`/`CobroCliente`, `PagoProveedor`) se define como constante en el código junto con la validación; el criterio fijo es "si la categoría nombra un cobro de cliente o un pago a proveedor, se rechaza". Esto cierra el riesgo T3 de doble puerta para el mismo hecho económico, no por confianza en el único usuario sino por barrera dura. (Ex-R-8 resuelto: no es validación blanda; es bloqueo.)

---

## 5. Backfill y estrategia de migración

### 5.1 Migraciones (aditivas, sin SQL crudo de ALTER sobre columnas con `HasColumnName` histórico)

`Adr022_M1` (una sola migración EF, aditiva):
- `CREATE TABLE CashLedgerEntries` (+ índices, incluido el único parcial `WHERE <fk> IS NOT NULL AND IsReversal=false AND IsReversed=false` por origen, §4.2; + CHECK constraints vía `HasCheckConstraint`).
- `ALTER TABLE "ManualCashMovements" ADD COLUMN Currency` (NOT NULL default `'ARS'`).
- `ALTER TABLE "ClientCreditEntries" ADD COLUMN Currency` (NOT NULL default `'ARS'`).
- **Sobrepago (Q1):** hacer nullable `ClientCreditEntries.OperatorRefundAllocationId` y `ClientCreditEntries.BookingCancellationId` (hoy NOT NULL) + agregar origen "sobrepago" (FK al `Payment`/`Reserva` que lo originó + auditoría). Relajar un NOT NULL a NULL es aditivo-seguro (no rompe filas existentes, que siguen teniendo valor). El histórico de créditos de cancelación conserva sus FKs; los nuevos de sobrepago los dejan en null y usan el origen nuevo.

Todas las columnas nuevas son aditivas con default a nivel BD; relajar NOT NULL→NULL no toca datos existentes → el histórico queda consistente sin migrar datos. **No hay SQL crudo que referencie nombres de columna** (la lección M2: el incidente fue por SQL crudo usando `ReservaId` cuando la columna real era `TravelFileId` por `HasColumnName`). Aquí EF genera el DDL desde el modelo, usando los nombres reales mapeados.

### 5.2 Backfill del libro (idempotente, en el contenedor `migrate`, vigilado como M4)

**Estrategia de idempotencia — UNA sola (B2).** El backfill NO usa un índice único parcial como mecanismo de idempotencia (el índice único parcial de §4.2 es una **invariante de integridad** del libro, no la herramienta del backfill). Usa el **mismo patrón que ADR-021** (`MultiCurrencyBackfillService`): un job C# con (a) un chequeo barato `NeedsBackfillAsync()` propio del libro y (b) inserción guiada por **clave natural explícita por origen**, no por upsert ciego ni por "atrapar la violación del índice".

- **Clave natural por origen:** para cada hecho histórico, el backfill chequea "¿ya existe un asiento VIGENTE para este origen?" con un `WHERE` explícito sobre el FK correspondiente:
  - `Payment` → `CashLedgerEntries` con `PaymentId == p.Id AND IsReversal=false AND IsReversed=false`.
  - `SupplierPayment` → `SupplierPaymentId == sp.Id AND IsReversal=false AND IsReversed=false`.
  - `ManualCashMovement` → `ManualCashMovementId == m.Id AND IsReversal=false AND IsReversed=false`.
  Si ya existe, lo saltea (no inserta). Si no existe, inserta el asiento vía `CashLedgerEntryFactory`. Correr el job dos veces no duplica (segunda corrida encuentra el asiento y saltea). El índice único parcial queda como red de seguridad de la BD, no como el mecanismo de control de flujo.
- **`NeedsBackfillAsync()` propio del libro:** chequeo barato "¿hay algún hecho económico vivo SIN asiento vigente?" (un `EXISTS` por cada origen). Si todo está cubierto, el job no hace nada. Espejo de `MultiCurrencyBackfillService.NeedsBackfillAsync()`.

**Universo de hechos a asentar (un asiento por hecho, sin doble conteo — B2):**
- Cada `Payment` vivo (`!IsDeleted`, `AffectsCash`, `Status != Cancelled`) → 1 asiento `CustomerPayment` (`Currency = Payment.Currency`).
- Cada `SupplierPayment` vivo (`!IsDeleted`) → 1 asiento `SupplierPayment` (`Currency = SupplierPayment.Currency`).
- Cada `ManualCashMovement` no-voided → **1 solo asiento**, con `SourceType` derivado de sus FKs:
  - si tiene `OperatorRefundReceivedId` → `SourceType=OperatorRefund`, `Currency` del `OperatorRefundReceived` (regla §4.4);
  - si tiene `ClientCreditWithdrawalId` → `SourceType=ClientCreditWithdrawal`, `Currency` del `ClientCreditEntry` padre (regla §4.4);
  - si no tiene ninguno → `SourceType=ManualAdjustment`, `Currency` del propio `ManualCashMovement`.
- **Declaración explícita (B2 / RK-1):** los `OperatorRefundReceived` y `ClientCreditWithdrawal` **NO se asientan por separado** — ni en runtime ni en backfill. Su asiento ES el del `ManualCashMovement` de cancelación que el flujo YA crea (uno por refund, uno por withdrawal — confirmado en `OperatorRefundService.RecordReceivedAsync`, que crea refund + 1 `ManualCashMovement` Income en el mismo `SaveChanges`). Recorrer `ManualCashMovement` cubre la cancelación completa; recorrer además `OperatorRefundReceived`/`ClientCreditWithdrawal` duplicaría. El backfill recorre `Payment`, `SupplierPayment` y `ManualCashMovement`, y NADA MÁS.

- **Decisión sobre borrados:** backfill SOLO de hechos vivos (no recrear historia de borrados, que no tenemos con fecha de anulación confiable). El libro arranca como "saldo de apertura implícito = estado vivo actual". Consistente con la decisión #4 (arqueo es fase posterior; el saldo acumulado debe ser derivable, y desde el estado vivo lo es).

**Criterio de éxito:** para cada moneda, `SUM(Direction*Amount)` del libro == `CashInThisMonth - CashOutThisMonth` calculado por la lógica vieja sobre el mismo universo y período. Se verifica con una query de reconciliación antes de cortar `TreasuryService` al libro.

> **Resuelto (ex-R-9):** el backfill es un **job C# idempotente** (no `migrationBuilder.Sql` con INSERT…SELECT, que sería SQL crudo y riesgo M2). Reusa `CashLedgerEntryFactory` (el mapeo del modelo), evita SQL crudo, y es el mismo patrón que `MultiCurrencyBackfillService` de ADR-021. Ver §5.3 para dónde se engancha.

### 5.3 Orden de deploy

1. Aplicar `Adr022_M1` (tablas/columnas nuevas, vacías). El sistema sigue funcionando: nadie lee el libro todavía.
2. Correr el backfill (job idempotente). El libro queda poblado con el estado vivo.
3. Reconciliar (query de comparación libro vs lógica vieja por moneda/mes).
4. Cortar `TreasuryService` a leer del libro (deploy del código que ya escribe asientos en cada hecho y lee del libro).

Entre el paso 1 y el paso 4, los services ya escriben asientos. Es idempotente con el backfill: la clave natural por origen (§5.2) evita duplicar — si el backfill corre después de que ya entraron asientos vivos nuevos, encuentra el asiento existente y lo saltea.

**Independencia del backfill multimoneda (B2 / verificado):** el backfill del libro **NO depende** del backfill multimoneda de ADR-021. Lee las columnas `Currency` de `Payment` y `SupplierPayment`, que **existen desde `Adr021_M1`** (no desde el backfill; la columna está poblada en cada fila por su default y por el alta de pagos posterior). En el próximo deploy del VPS, `MigrateAsync` (`Program.cs:729`) aplica **todas** las migraciones pendientes en orden — `Adr021_M1` y `Adr022_M1` corren juntas — ANTES de cualquier backfill. Cuando el backfill del libro lee `Payment.Currency`, la columna ya existe y tiene valor. El único insumo que el backfill del libro NECESITA de ADR-021 es que esas columnas existan (lo garantiza la migración), no que el backfill de las tablas hijas por moneda haya corrido.

**Recomendación operativa (dónde engancharlo):** invocar el backfill del libro en `Program.cs`, en el mismo bloque de migración (líneas ~738-763), **DESPUÉS** del backfill multimoneda de ADR-021, con su propio `NeedsBackfillAsync()` y su propio `try/catch` no-abortante (espejo exacto del patrón de ADR-021: si falla, loguea y el arranque continúa; el libro se completa en el próximo arranque). El orden "multimoneda primero, libro después" es por prolijidad (las columnas de moneda quedan garantizadas y los saldos derivados ya recalculados), no por dependencia de datos.

### 5.4 Rollback

`git revert` del código + migración `Down` de `Adr022_M1` (drop de `CashLedgerEntries`, drop de las 2 columnas nuevas). Como todo es aditivo, el `Down` no pierde datos de negocio (el libro es derivado; los `Payment`/`SupplierPayment`/`ManualCashMovement` originales quedan intactos). `TreasuryService` vuelve a la unión al vuelo. **El `Down` debe probarse en Postgres real** (lección M2).

> **Caveat de rollback con créditos de sobrepago escritos (S3, review de seguridad 2026-06-11):** el `Down` de `Adr022_M1` **re-aprieta a `NOT NULL` (con `defaultValue: 0`) las FKs `OperatorRefundAllocationId` y `BookingCancellationId` de `ClientCreditEntry`**, que la migración había relajado a NULLABLE para soportar el crédito de SOBREPAGO (cuyas dos FKs de cancelación son `null` por diseño — §4.9 Q1). Por lo tanto el `Down` **solo es seguro si NO se escribió todavía ningún crédito de sobrepago**: si ya existen filas de sobrepago (FK de cancelación `null`), el `Down` las pondría en `0`, que es un FK inexistente/inválido (corrupción silenciosa del bolsillo del cliente). Antes de ejecutar el `Down` en un entorno donde ya pudo nacer un sobrepago, hay que verificar `SELECT count(*) FROM "ClientCreditEntries" WHERE "OperatorRefundAllocationId" IS NULL AND "BookingCancellationId" IS NULL` = 0; si hay filas, el rollback NO es lossless y debe resolverse a mano (anular/migrar esos créditos) antes de bajar la migración.

---

## 6. Impacto por servicio (resumen)

| Servicio | Cambio |
|---|---|
| `PaymentService` | `RegisterPayment`: + asiento en misma tx. `Delete`/`Update`: + reversa (orden marcar-viejo-IsReversed antes de insertar nuevo, §4.5). + crear `ClientCreditEntry` de **sobrepago** cuando el cobro deja la reserva con saldo a favor (Q1, §4.9). **Aplicar saldo a favor a otra reserva** = `Payment` con `Method="Crédito"` + `AffectsCash=false` (imputación; NO genera asiento de caja, §4.9 camino (a)) + descuenta `RemainingBalance` del entry. |
| `ClientCreditService` | Guarda B5 (§4.9): el consumo total de un entry de **sobrepago** (`BookingCancellationId == null`) **NO** invoca `OnAllCreditConsumedAsync` (no hay cancelación que cerrar). La devolución física del sobrepago sigue por `WithdrawAsync` (camino (b), sí mueve caja). |
| `SupplierService` | `AddSupplierPaymentAsync`: + asiento + setear `Currency`/`ImputedCurrency`/TC del request (hoy NO los setea); imputación a reserva (P4). `Delete`/`Update`: + reversa. |
| `TreasuryService` | Lee del libro (movimientos + resúmenes). **+ AGREGA enmascarado `cobranzas.see_cost`** sobre egresos de costo (no existe hoy, B3). `CreateManualMovementAsync`: + asiento + `Currency` (T2). T3: bloqueo duro de categorías cobro/pago (Q3). Incluye AP en summary (T4). |
| `ReservaService` | P1: dispara recalculador de deuda del proveedor en add/update/remove de servicio genérico con `SupplierId` (proveedor viejo + nuevo en edición). |
| `BookingCancellationService` / `OperatorRefundService` / `ManualCashMovementBuilder` | + asiento por cada `ManualCashMovement` de refund (T2) / withdrawal (T3), en la misma tx que ya los crea. NO duplicar. NO cambiar el flujo diferido. |
| `ReportService` | `GetDashboardAsync`: AR/AP desde la fuente única compartida (T4). |
| `CustomerService` | `GetCustomerAccountOverviewAsync`: deja de sumar surrogate; saldo a cobrar + saldo a favor por moneda (C2 + decisión #3). |

**API impact:** contratos de tesorería se mantienen (`CashMovementDto` etc.). `TreasurySummaryDto` gana `AccountsPayableByCurrency` (aditivo). `CustomerAccountOverviewDto` gana saldo por moneda + saldo a favor (aditivo). `SupplierPaymentRequest` gana bloque de moneda/TC + flag "a cuenta" (aditivo). Sin breaking changes de shape existente.

---

## 7. Riesgos y mitigaciones

| # | Riesgo | Mitigación |
|---|---|---|
| RK-1 | Doble conteo: asentar `ManualCashMovement` de cancelación Y el `OperatorRefundReceived`/`ClientCreditWithdrawal` por separado. | UN asiento por `ManualCashMovement`; `SourceType` derivado de FKs. Cobro/pago directo (`Payment`/`SupplierPayment`) no pasan por manual → asiento directo. Test de "el refund genera exactamente 1 asiento". |
| RK-2 | El libro y los saldos derivados divergen. | El libro es fuente de CAJA; los saldos siguen su propia fuente (calculadores, self-healing). Reconciliación de backfill verifica que cuadran al cortar. No se "sincronizan" entre sí: son ejes distintos (caja real vs deuda imputada). |
| RK-3 | El asiento debe llevar la moneda REAL del hecho, no una moneda derivada de la fila equivocada. | **Cobro/pago:** `CashLedgerEntryFactory.ForPayment` usa `Payment.Currency`/`Amount` (caja real), NUNCA `ImputedAmount`/`ImputedCurrency`. **Cancelación:** la factory toma la moneda del **origen real** (`OperatorRefundReceived.Currency` para refund; `ClientCreditEntry.Currency` del entry padre para withdrawal), NUNCA del `ManualCashMovement.Currency` (que es default ARS y no refleja el hecho) — regla explícita §4.4. Test explícito por cada origen (espejo del test de ADR-021 §2.8): refund USD → asiento USD aunque el `ManualCashMovement` esté en ARS. |
| RK-4 | `ClientCreditEntry` sin moneda → "bolsillo por moneda" mal modelado. | `Currency` aditivo default ARS. **El llenado real con USD NO depende de cancelación multimoneda** (corregido): `OperatorRefundReceived` ya tiene `Currency` (verificado, `.cs:58-64`), así que el crédito de cancelación se puebla con la moneda real sin tocar el módulo de cancelación (§4.9, Q2 resuelta). El sobrepago se puebla con la moneda del saldo de la reserva (§4.9, Q1 resuelta). Este ADR no reescribe el flujo de cancelación; solo lee su moneda y agrega la creación del crédito de sobrepago en `PaymentService`. |
| RK-5 | P1 fix introduce ciclo de dependencias. | Patrón `ReservaMoneyPersister` (recalculador sin estado sobre `AppDbContext`), no inyección de service. Verificar grep de ciclos. |
| RK-6 | Backfill no idempotente → asientos duplicados. | UNA estrategia (§5.2): job C# con `NeedsBackfillAsync()` propio + chequeo de clave natural por origen (`WHERE <fk>=id AND IsReversal=false AND IsReversed=false`) antes de insertar. El índice único parcial (predicado `<fk> IS NOT NULL AND IsReversal=false AND IsReversed=false`, §4.2) es la red de seguridad de la BD, no el mecanismo de control de flujo. |
| RK-7 | Migración revienta en Postgres real (lección M2). | Migración 100% EF aditiva (sin SQL crudo de ALTER por nombre); backfill = job C# (no INSERT…SELECT crudo); probar `Up` y `Down` contra Postgres real antes de mergear. |
| RK-8 | Romper el flujo diferido de cancelación (crédito al refund, no al cancelar). | NO se toca el flujo de cancelación; solo se agrega el asiento en la misma tx donde YA se crea el `ManualCashMovement`. Tests de cancelación existentes deben seguir verdes. |
| RK-9 | Datos sensibles de costo en el libro sin enmascarar. | El libro contiene plata de costo (egresos a proveedor). **El enmascarado `cobranzas.see_cost` NO existe hoy en `TreasuryService`/`TreasuryController` (verificado: 0 matches).** Por eso se **AGREGA** como pieza nueva en la capa de tesorería (§4.6, §9 capa 4): las lecturas que exponen costo (`CashMovementDto` con egresos `SupplierPayment`/cancelación, AP en summary) enmascaran el monto para usuarios sin `cobranzas.see_cost`, espejo del masking que `SupplierService` ya hace (`MaskSupplierPaymentAmountsAsync`). NO es reutilización del masking existente — es trabajo nuevo. Reviewer de seguridad debe validar. |

---

## 8. Estrategia de testing

- **Unit (Domain):** `CashLedgerEntryFactory` — un test por origen (Payment/SupplierPayment/refund/withdrawal/manual) verificando `Direction`, `Amount` positivo, `Currency` = moneda real, `SourceType`, FK seteado. Reversa: `Direction` invertida, `IsReversal`, `ReversedEntryId`.
- **Unit (cross-currency):** asiento de pago cruzado lleva `Currency`/`Amount` reales, NO el imputado. **Cancelación (B1):** refund USD → asiento USD aunque su `ManualCashMovement` tenga `Currency='ARS'`; withdrawal toma la moneda del `ClientCreditEntry` padre.
- **Integración (Postgres real):** CHECK `exactly_one_source`; índice único parcial con predicado `IsReversal=false AND IsReversed=false` (no permite 2 asientos VIGENTES por mismo origen; sí permite original-revertido + reversa + nuevo coexistiendo tras una edición); migración `Up`/`Down`; backfill idempotente (correr 2 veces no duplica — vía clave natural por origen, no por atrapar la violación del índice); reconciliación libro vs lógica vieja por moneda.
- **Integración (servicios):** crear cobro → 1 asiento Income; anular → marcar viejo `IsReversed` + reversa, libro neto 0; editar monto → marcar viejo `IsReversed` + reversa + nuevo (orden estricto §4.5, sin violar el único parcial); pago a proveedor → 1 asiento Expense; manual → 1 asiento; refund de cancelación → exactamente 1 asiento (no doble; NO se asienta el `OperatorRefundReceived`/`ClientCreditWithdrawal` por separado).
- **Sobrepago (Q1):** cobro que excede el saldo de la reserva → reserva queda en 0 + `ClientCreditEntry` de sobrepago por el excedente, en la moneda del saldo pagado; NO genera asiento de caja extra (el cobro ya asentó la plata real); origen "sobrepago" trazable al `Payment`/`Reserva`.
- **Sobrepago — consumo, los dos caminos (B5):** (a) **aplicar** crédito de sobrepago a otra reserva → se crea un `Payment(AffectsCash=false, Method="Crédito")` en la reserva destino que imputa el saldo y descuenta `RemainingBalance`, **sin** generar ningún `CashLedgerEntry` (verificar que el libro NO gana asiento por la aplicación); (b) **devolución física** de un crédito de sobrepago → `WithdrawAsync` con `PhysicalCash`/`Transfer` genera exactamente 1 asiento Expense en la moneda del entry. (c) **Guarda B5:** consumir totalmente un entry de sobrepago (`BookingCancellationId == null`) **NO** invoca `OnAllCreditConsumedAsync` (no se cierra ninguna `BookingCancellation`; ninguna reserva pasa a `Cancelled` por este consumo); consumir totalmente un crédito de **cancelación** (`BookingCancellationId != null`) SÍ lo invoca (regresión del comportamiento existente).
- **Seguridad (B3):** usuario sin `cobranzas.see_cost` → los egresos de costo (`SupplierPayment`, cancelación) salen enmascarados en `GetMovementsAsync`/summary; usuario con el permiso los ve. (Test del masking nuevo, no existía en tesorería.)
- **P1:** crear/editar/borrar servicio genérico con proveedor → `SupplierBalanceByCurrency` queda sincronizado (no stale); cambio de proveedor recalcula ambos.
- **T4:** AR/AP de dashboard == AR/AP de tesorería (misma fuente).
- **C2:** cuenta corriente del cliente por moneda == suma de `ReservaMoneyByCurrency` + crédito por moneda; no usa surrogate.
- **Regresión:** suite de cancelación (ADR-002) verde sin cambios de comportamiento; tesorería devuelve los mismos totales tras cortar al libro (sobre datos de backfill).

---

## 9. Plan de implementación POR CAPAS (sin estimaciones de tiempo)

**Capa 1 — Modelo + migración (invisible, no rompe nada):**
- Entidad `CashLedgerEntry` + config EF (FKs, índice único parcial con predicado `IsReversal=false AND IsReversed=false`, CHECK) + `CashLedgerSourceTypes`.
- `Currency` en `ManualCashMovement` y `ClientCreditEntry` (aditivo default ARS).
- `ClientCreditEntry`: FKs `OperatorRefundAllocationId`/`BookingCancellationId` a nullable + origen "sobrepago" (Q1, §4.9).
- Migración `Adr022_M1`. Probar `Up`/`Down` en Postgres real.

**Capa 2 — Factory + escritura del asiento en cada puerta (el libro empieza a poblarse en vivo):**
- `CashLedgerEntryFactory` (Domain, puro). Moneda de cancelación desde el origen real (`OperatorRefundReceived.Currency` / `ClientCreditEntry.Currency`), NUNCA `ManualCashMovement.Currency` (B1).
- `PaymentService`, `SupplierService`, `TreasuryService.CreateManualMovementAsync`, `ManualCashMovementBuilder`/`OperatorRefundService`: escribir asiento en la misma tx que el hecho. Reversas en delete/update con el orden estricto de §4.5 (marcar viejo `IsReversed` antes de insertar nuevo).
- `PaymentService`: crear `ClientCreditEntry` de **sobrepago** en la misma tx cuando el cobro deja la reserva con saldo a favor (Q1, §4.9).
- **Sobrepago — consumo (B5, §4.9):** (i) `ClientCreditService.WithdrawAsync` ramifica el cierre de cancelación por origen (`BookingCancellationId != null`) — un crédito de sobrepago consumido totalmente NO dispara `OnAllCreditConsumedAsync`; (ii) `PaymentService` materializa la **aplicación a otra reserva** como `Payment(AffectsCash=false, Method="Crédito")` (imputación, sin asiento de caja) que descuenta `RemainingBalance` del entry de sobrepago. La devolución física reusa el `WithdrawAsync` existente (sí genera asiento Expense).
- Tests unit + integración de escritura. (El libro aún no se LEE; tesorería sigue al vuelo. Sin riesgo.)

**Capa 3 — Backfill + reconciliación:**
- Job idempotente que pobla el libro desde el estado vivo. Reconciliación por moneda. Correr en `migrate`.

**Capa 4 — TreasuryService lee del libro:**
- `GetMovementsAsync`/`GetCashSummaryAsync`/`GetSummaryAsync`/`GetCashByCurrencyAsync` consultan `CashLedgerEntry`. Mantener contrato de DTOs.
- **AGREGAR enmascarado `cobranzas.see_cost`** (no existe hoy en tesorería, B3): los montos de egresos que representan costo a proveedor se enmascaran para usuarios sin el permiso, espejo de `SupplierService.MaskSupplierPaymentAmountsAsync`. Reviewer de seguridad valida.

**Capa 5 — Fix P1 (proveedor desde servicio genérico):**
- Recalculador de deuda del proveedor invocado desde `ReservaService` (add/update/remove genérico con `SupplierId`, viejo+nuevo en edición).

**Capa 6 — P4 + T2 + T3 (pago a proveedor imputado/a cuenta + moneda manual + validación manual):**
- `SupplierPaymentRequest` gana bloque moneda/TC + flag "a cuenta"; `AddSupplierPaymentAsync` setea moneda y resuelve imputación a reserva.
- `CreateManualMovementAsync` acepta `Currency`; **bloqueo duro** de categorías cobro-cliente/pago-proveedor (Q3, §4.13).

**Capa 7 — Fuente única AR/AP (T4):**
- `IFinancePositionService`; dashboard y tesorería consumen; tesorería incluye AP.

**Capa 8 — Cuenta corriente del cliente (C2 + saldo a favor por moneda):**
- `CustomerService` por moneda; saldo a favor agregado desde `ClientCreditEntry` por moneda.

**Gate UX (antes de cualquier cambio visible):** las capas 4, 7, 8 cambian lo que el usuario VE en tesorería y ficha del cliente → **UX gate obligatorio con Gastón** (`ux-ui-disenador` contra `docs/ux/guia-ux-gaston.md`) ANTES de tocar frontend. El backend puede construirse y testearse sin frontend; el frontend espera el gate.

---

## 10. Preguntas abiertas — TODAS RESUELTAS (2026-06-11)

Las cuatro preguntas del Round 1 quedaron cerradas por decisión del dueño (Q1, Q3, Q4) o por verificación en código (Q2). No quedan abiertas que bloqueen el alcance.

- **Q1 (saldo a favor / sobrepago) — RESUELTA por el dueño.** El sobrepago del cliente en una reserva se **convierte en saldo a favor del cliente** (`ClientCreditEntry`, mismo bolsillo por moneda que el crédito de cancelación); la reserva queda saldada en 0 y el excedente pasa al bolsillo. Mecanismo en §4.9 (`PaymentService` crea el `ClientCreditEntry` en la misma transacción del cobro; moneda = la del saldo que se estaba pagando; imputación, no movimiento de caja). Desaparece la "contradicción #1 vs #3": el cobro sigue yendo a UNA reserva (decisión #1 intacta), y el excedente —no la imputación normal— es lo que se convierte en crédito.
- **Q2 (moneda del crédito de cancelación) — RESUELTA por verificación.** `OperatorRefundReceived` **SÍ tiene** `Currency` + `ExchangeRateAtReceipt` hoy (`OperatorRefundReceived.cs:58-64`, verificado 2026-06-11). El crédito de cancelación lleva la moneda real desde el primer día, sin tocar el módulo de cancelación. (El texto previo que decía "queda en ARS hasta que cancelación sea multimoneda" era incorrecto y se corrigió en §4.9 y RK-4.)
- **Q3 (categorías de movimiento manual bloqueadas) — RESUELTA: bloqueo duro.** Un manual NO puede ser "cobro de cliente" ni "pago a proveedor" (rechazo de validación, no warning); esas categorías tienen su puerta propia. El resto de categorías queda libre. Detalle en §4.13.
- **Q4 (mostrar reversas en el libro) — RESUELTA: default de API.** El libro **muestra las anulaciones como filas con signo invertido** (transparencia estilo libro mayor); es el comportamiento por defecto de la API. La presentación final en pantalla pasa por **UX gate aparte** (no se decide acá). Detalle en §4.6.

**Nota de implementación (no bloquea el diseño, se cierra al construir):** el sobrepago como `ClientCreditEntry` (Q1) requiere relajar a nullable los FKs `OperatorRefundAllocationId` y `BookingCancellationId` de `ClientCreditEntry` (hoy NOT NULL, verificado `ClientCreditEntry.cs:34,38`) y agregar un origen "sobrepago" que apunte al `Payment`/`Reserva` que lo generó. Es migración aditiva (columnas a nullable + FK de origen nuevo); se valida en la capa de implementación. Mantener simple: un solo origen alternativo, sin reescribir el aggregate.

**Guarda de consumo (B5, regla cerrada en §4.9):** al relajar `BookingCancellationId` a nullable, el path `ClientCreditService.WithdrawAsync` debe ramificar el cierre de cancelación por origen: la condición de `ClientCreditService.cs:303-305` pasa a `request.Kind != WithdrawalKind.KeptAsCredit && entry.IsFullyConsumed && entry.BookingCancellationId != null`, de modo que un crédito de sobrepago consumido totalmente **NO** invoque `OnAllCreditConsumedAsync` (no hay BC que cerrar). Además, los dos caminos de consumo del crédito de sobrepago están separados en §4.9: **(a) aplicar a otra reserva** = `Payment` con `AffectsCash=false` (imputación, no genera asiento de caja), **(b) devolución física** = `ClientCreditWithdrawal` (sí genera asiento Expense). No mezclarlos al construir.

---

## 11. Fuera de alcance (explícito)

- **Arqueo / apertura / cierre de caja** (decisión #4: fase posterior; el libro queda preparado con saldo acumulado por moneda derivable).
- **Pago a cuenta del cliente** (decisión #1: no se construye).
- **Conciliación facturado-vs-vendido como pantalla.**
- **Pantallas nuevas de frontend** (habrá UX gate aparte).
- **Diferencia de cambio** (límite contable de ADR-021: el sistema nunca la calcula ni muestra).
- **Reescritura del flujo de cancelación / módulo multimoneda de cancelación** (ADR-002 / eje cancelación de ADR-021).

---

## 12. Consecuencias

**Positivas:** caja inmutable y auditable (libro mayor real); historia no vibra al editar; una sola puerta por hecho; AR/AP que cuadran entre dashboard y tesorería; deuda del proveedor sincronizada también desde el servicio genérico; base lista para arqueo/cierre futuros sin rediseño.

**Negativas / costos:** una entidad y una proyección más que mantener (el asiento se escribe en 5 puertas — mitigado por la factory única); el backfill es una operación de datos vigilada; el libro contiene costo (egresos) y necesita enmascarado `cobranzas.see_cost` **nuevo** (no existe hoy en tesorería); el sobrepago como crédito (Q1) obliga a relajar dos FKs de `ClientCreditEntry` a nullable y agregar un origen "sobrepago" (cambio de modelo acotado y aditivo). El crédito por moneda queda **completo** en este ADR (cancelación lee la moneda real del refund —ya existe—, y el sobrepago la toma del saldo de la reserva); no queda a medias.

**Neutras:** `ManualCashMovement`, `Payment`, `SupplierPayment` siguen existiendo como orígenes; el libro es su proyección, no los reemplaza.
