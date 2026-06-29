# Diseño técnico: cerrar el lado operador de las cancelaciones y dejar la cuenta del operador confiable

Fecha: 2026-06-28
Autor: software-architect
Estado: Propuesto (diseño, sin implementar). Requiere review de `software-architect-reviewer` + firma del contador en los puntos marcados.
Alcance: cómo termina la pata del OPERADOR en una cancelación, y cómo el extracto del operador muestra DOS números que siempre cuadran.

> En fácil (para Gastón): hoy, cuando se anula un viaje ya pagado al operador, la cuenta del operador
> queda "rota": muestra que el operador te debe toda la plata para siempre, aunque ya te haya devuelto o
> se haya quedado con una multa. Este diseño hace que la cuenta del operador muestre dos cosas claras y
> separadas: **"Le debo: $X"** (lo que la agencia todavía le tiene que pagar) y **"Me tiene que devolver:
> $Y"** (lo que el operador todavía te tiene que reembolsar), por moneda. Y que cuando el operador devuelve
> o se queda con una multa, esos números bajen solos hasta cerrar en cero.

---

## 1. Estado actual verificado (con archivo:línea)

### 1.1 Cómo se arma hoy la cuenta del operador (extracto)
- `SupplierService.GetSupplierAccountStatementAsync` (`src/TravelApi.Infrastructure/Services/SupplierService.cs:574`)
  arma el extracto como libro mayor SOLO con dos tipos de movimiento:
  - **Compras confirmadas** que cuentan como deuda (`WorkflowStatusHelper.CountsForSupplierDebtByType`,
    `src/TravelApi.Domain/Entities/WorkflowStatusHelper.cs:85`) → cargo (+).
  - **Pagos vivos al operador** (`SupplierPayments`, filtro `!IsDeleted`) → abono (−).
- El builder puro es `SupplierAccountStatementBuilder` (`src/TravelApi.Domain/Reservations/SupplierAccountStatementBuilder.cs`),
  con solo dos `Kinds`: `Purchase` y `Payment` (líneas 12-19). El saldo de cierre por moneda DEBE coincidir con
  `SupplierBalanceByCurrency.Balance` (invariante declarada, líneas 67-78; hay test).
- La deuda materializada (`SupplierBalanceByCurrency`) la produce `SupplierDebtPersister` = `compras − pagos`,
  SOLO caja.

### 1.2 Qué pasa al cancelar un servicio ya pagado
- Al cancelar, el servicio cambia de estado y **deja de contar como compra** (`CountsForSupplierDebtByType`
  devuelve false para Cancelado). El pago al operador SIGUE contando. Resultado: el saldo del extracto cae a
  **negativo por el total pagado** y nunca vuelve.
- **Verificado**: `BookingCancellationService` NO está entre los callers de `PersistSupplierBalanceAsync`
  (grep). O sea: al cancelar, el saldo materializado del operador **ni siquiera se recalcula** en ese momento;
  queda viejo hasta el próximo movimiento de pago a ese operador. Es un agujero adicional al que describe el pedido.

### 1.3 La multa confirmada hoy NO impacta la cuenta del operador
- `ConfirmPenaltyAsync` (`BookingCancellationService.cs:3156`) asume SIEMPRE que hay multa: exige
  `ConfirmedPenaltyAmount` y dispara una Nota de Débito (precondiciones 5 y 6, líneas 3213-3246). No existe un
  camino "sin multa".
- La capacidad de UI `EvaluateConfirmOperatorPenalty` (`ReservaCapabilities.cs:696-701`) habilita el botón solo si
  `HasPendingOperatorPenalty`. No hay acción alternativa "cerrar sin multa".
- Fase 1 (hoy, 2026-06-28): `AllocateConfirmedPenaltyToLinesAsync` (`BookingCancellationService.cs:3538`) baja el
  `RefundCap` de las líneas del operador por la multa confirmada (`RefundCap = capBeforePenalty − multa`,
  líneas 3589-3622). Eso corrige el READ-MODEL de "Reembolsos a cobrar", pero **no postea nada a la cuenta/extracto
  del operador**.

### 1.4 El reembolso recibido hoy NO impacta la cuenta del operador
- `OperatorRefundService.RecordReceivedAsync` (`OperatorRefundService.cs:80`) crea el `OperatorRefundReceived`,
  un `ManualCashMovement` (Income en caja) y un `CashLedgerEntry`. **Nada al extracto del operador.**
- Al imputar (`AllocateAsync` → región `OperatorRefundService.cs:486-540`): incrementa
  `line.ReceivedRefundAmount`, mueve el estado del BC y crea un `ClientCreditEntry` (acredita al CLIENTE). De nuevo
  **nada al lado proveedor**. Por eso el saldo del extracto del operador queda negativo por el total pagado y nunca cierra.

### 1.5 Las DOS representaciones que pueden discrepar (núcleo del problema)
1. **Saldo del extracto** (`SupplierBalanceByCurrency.Balance` = compras − pagos): tras la cancelación se va a
   `−(total pagado)`. Mezcla en un solo número con signo dos cosas distintas: "le debo" y "me tiene que devolver".
2. **Read-model "Reembolsos a cobrar"** (`OperatorRefundReadModelService.cs`, `BuildItem` líneas 113-155):
   `Σ max(0, RefundCap − ReceivedRefundAmount)` por moneda, sobre BCs `AwaitingOperatorRefund` /
   `AbandonedByOperator`.

Divergencias reales hoy: la multa baja (1) nada y (2) el `RefundCap`; el reembolso recibido baja (2) a cero pero
(1) sigue negativo para siempre. Un número con signo significando dos cosas = lo que el dueño NO quiere.

### 1.6 Pieza ya existente que vamos a reutilizar
- `SupplierCreditEntry` (saldo a favor consumible con el operador, ADR-041 TANDA 3,
  `src/TravelApi.Domain/Entities/SupplierCreditEntry.cs`) ya tiene un campo **`SourceOperatorRefundReceivedId`
  reservado "para reembolso tardío" y HOY sin usar** (líneas 57-62).
- `SupplierCreditReconciler` (`src/TravelApi.Infrastructure/Reservations/SupplierCreditReconciler.cs`) mantiene el
  pool de saldo a favor = `max(0, −Balance)` por moneda. **Solo se dispara desde alta/edición/baja de un pago**
  (`SupplierService` líneas 867, 977). NO se dispara por cancelación. (Esto es clave en §3 y §4.)

---

## 2. Decisión de arquitectura: una sola verdad, no dos ledgers

### 2.1 El problema económico, en limpio
Pagar al operador $1000 por un servicio y después anularlo crea un saldo negativo de −1000 en el extracto. Ese
negativo **es** un "me tiene que devolver", no un saldo a favor genérico (prepago). El modelo actual (un único
número `compras − pagos`) no puede distinguir:
- **deuda comercial** (le debo por servicios vivos),
- **reembolso por cobrar** (me tiene que devolver por servicios anulados ya pagados),
- **prepago genuino** (le pagué de más a un servicio vivo, saldo a favor consumible).

### 2.2 La identidad de caja que hace cuadrar todo
Para un servicio anulado-y-pagado, la plata cierra en cero cuando se postean los DOS hechos que faltan:

```
Pago al operador        −1000   (abono, ya existe)
Multa que retuvo        + 300   (cargo, FALTA — punto b)
Reembolso recibido      + 700   (cargo, FALTA — punto c)
                        ------
Saldo                       0
```

O sea: **la multa retenida por el operador y el reembolso recibido son movimientos legítimos del lado del
operador** (consumieron / devolvieron lo que se le había pagado). Postearlos en el MISMO extracto es lo que lo
hace cerrar a cero. No hace falta un segundo ledger.

### 2.3 Opciones consideradas

**Opción A — Ledger paralelo de "refund receivable" (entidad nueva tipo `SupplierRefundReceivable`).**
El extracto suma una línea de reembolso por cobrar además del pago negativo. Problema: duplica la plata (el pago
−1000 Y un receivable +1000 = −2000), salvo que además reviertas el efecto del pago. Reintroduce exactamente la
doble representación que queremos eliminar. **Rechazada.**

**Opción B — El extracto (proyección de caja) es la única verdad y se EXTIENDE con dos tipos de movimiento
derivados-en-lectura: "Multa retenida" (+) y "Reembolso recibido" (+).** El read-model "Reembolsos a cobrar" se
mantiene como verdad AUTORITATIVA del receivable. Los dos números del header se DERIVAN y cuadran por construcción.
**Recomendada.**

**Opción C — Materializar el receivable como `SupplierCreditEntry` negativo / reusar ese pool.** El pool
`SupplierCreditEntry` es para saldo a favor CONSUMIBLE (aplicable a otra reserva). Un reembolso por cobrar NO es
consumible (es plata que el operador debe devolver en efectivo, no un crédito para descontar de otra compra).
Mezclar ambos en el mismo pool corrompe el significado del saldo a favor. **Rechazada** (pero ver §3.3: el pool de
prepago debe quedar bien separado del receivable).

### 2.4 Por qué derivar-en-lectura y no persistir filas de ledger
El extracto actual NO persiste filas: deriva compras (de `Servicio`) y pagos (de `SupplierPayment`) en lectura.
Para ser coherentes, la "Multa retenida" y el "Reembolso recibido" también se derivan en lectura desde su fuente de
verdad ya existente:
- Multa: `BookingCancellationLine.PenaltyAmount` con `PenaltyStatus=Confirmed`, en `PenaltyCurrency`, por `SupplierId`.
- Reembolso: `OperatorRefundAllocation.NetAmount` (no anuladas) vía `OperatorRefundReceived.Currency`, por operador.

Ventaja: **cero entidad nueva, cero migración para el posteo, idempotencia automática** (deriva del estado, no de
un evento que se puede repetir). Es lo menos invasivo y elimina la doble representación de raíz.

---

## 3. Diseño de cada capacidad

### 3.1 (a) Camino de cierre SIN multa

**Dónde**: nuevo método `WaiveOperatorPenaltyAsync` (o `ConfirmNoPenaltyAsync`) en `BookingCancellationService`,
hermano de `ConfirmPenaltyAsync`. Endpoint nuevo en `CancellationsController`. Capacidad de UI: REUSA el gate
`HasPendingOperatorPenalty` (`ReservaCapabilities.cs:696`); cuando ese flag está en true, la UI ofrece DOS acciones
mutuamente excluyentes: "Confirmar multa" y "Cerrar sin multa / Reembolsa todo".

**Qué hace**:
- Para cada línea del operador pendiente: setear `PenaltyStatus = Confirmed` y `PenaltyAmount = 0`. El `RefundCap`
  queda en el total pagado (no se reduce nada). `OperatorPenaltyConfirmedDate = OperatorConfirmationDate`.
- Setear los campos de auditoría de clasificación/confirmación (quién/cuándo/motivo).

**Qué NO emite**:
- NO crea Nota de Débito. NO toca `DebitNoteInvoiceId` / `DebitNoteStatus` (quedan `NotApplicable`).
- NO toca `PenaltyAmountAtEvent` (la cara fiscal hacia el cliente).
- NO cambia el estado de la reserva por sí mismo: la reserva sigue esperando el reembolso completo; cierra cuando
  llega el reembolso total (`CloseReservaIfOperatorRefundComplete`, `BookingCancellationService.cs:1991`).

**Idempotencia**: mismo guard que confirmar multa — si `PenaltyStatus == Confirmed` (cualquier vía) → 409/no-op.
Confirmar-multa y cerrar-sin-multa comparten el candado: el primero gana, el segundo rebota. Audit dedicado nuevo
(p.ej. `OperatorPenaltyWaived`) para que el contador distinga "no hubo multa" de "multa = 0 por error".

**Preconditions**: recomiendo replicar las de `ConfirmPenaltyAsync` salvo las fiscales de ND (no aplican). **Seam
contador (§4)**: ¿un cierre sin multa requiere ALGÚN registro fiscal, o basta el audit interno? Asumo que no emite
nada fiscal; confirmar.

### 3.2 (b) Postear la multa confirmada a la cuenta del operador

**Modelo elegido**: línea derivada-en-lectura en el extracto, nuevo `Kind = PenaltyRetained` (cargo +).
**Fuente**: `BookingCancellationLine` donde `PenaltyStatus=Confirmed` AND `PenaltyAmount > 0` AND
`ConceptKind = OperatorPenaltyPassThrough` (la multa que el operador RETUVO). Moneda = `PenaltyCurrency`. Operador =
`SupplierId` de la línea.

**Importante — solo pass-through postea al operador**: una multa de "cargo propio de la agencia"
(`ConceptKind` agency-owned) NO es plata que retuvo el operador; el operador igual debería devolver todo. Postearla
al extracto del operador sería incorrecto. → Solo pasa al extracto la porción pass-through. **Seam contador (§4)**:
hoy `AllocateConfirmedPenaltyToLinesAsync` reduce el `RefundCap` por la multa SIN mirar el `ConceptKind`. Para
agency-owned eso puede estar mal (el operador refunda completo y la agencia cobra al cliente aparte). Hay que
decidir si la baja de `RefundCap` es solo para pass-through.

**Por moneda**: la multa puede ser USD aunque el servicio sea ARS (`PenaltyCurrency` es ISO puro,
`BookingCancellationLine.cs:85-100`). La línea cae en el bucket de `PenaltyCurrency`. Nunca cruza monedas.

### 3.3 (c) Conciliar el reembolso recibido en la cuenta del operador

**Modelo elegido**: línea derivada-en-lectura, nuevo `Kind = RefundReceived` (cargo +).
**Fuente**: `OperatorRefundAllocation.NetAmount` (no `IsVoided`) → moneda de `OperatorRefundReceived.Currency`,
operador del BC/línea.

Con (b) y (c) posteados, para el servicio del ejemplo: `−1000 (pago) + 300 (multa) + 700 (reembolso) = 0`. El saldo
del operador vuelve a cero cuando el circuito de cancelación cierra. Esto es lo que hoy nunca pasa.

**Cómo settlea a cero**: el reembolso baja el receivable (`ReceivedRefundAmount` sube → read-model baja) Y postea el
cargo +700 que neutraliza el pago negativo. La multa retenida +300 neutraliza la parte que el operador se quedó. No
queda residuo.

### 3.4 Reconciliación: cómo se calculan "Le debo" y "Me tiene que devolver" y por qué cuadran

Por proveedor + moneda, con el extracto económico ya incluyendo los 4 tipos
(`Purchase +`, `Payment −`, `PenaltyRetained +`, `RefundReceived +`):

- **Y = "Me tiene que devolver"** = read-model = `Σ max(0, RefundCap − ReceivedRefundAmount)` sobre líneas de
  cancelación abiertas. **Fuente autoritativa única del receivable.**
- **EconomicClosingBalance** = saldo de cierre del extracto con los 4 tipos.
- **X = "Le debo"** = `max(0, EconomicClosingBalance + Y)`.
- **Prepago (saldo a favor consumible)** = `max(0, −(EconomicClosingBalance + Y))`.

**Garantía de cuadre (identidad por construcción)**:
```
X − Y − Prepago  ≡  EconomicClosingBalance
```
X y Prepago se DEFINEN a partir de `EconomicClosingBalance` e `Y`, así que nunca pueden contradecir el extracto.
El dueño ve dos números (X, Y) que jamás se netean entre sí, y un tercero opcional (prepago) cuando el operador
quedó pagado de más en un servicio vivo.

**Ejemplos verificados a mano**:
- Anulado pagado 1000, multa 300, sin reembolso: Econ = −1000+300 = −700; Y = 700; X = max(0,0) = 0;
  Prepago = 0. → "Me tiene que devolver 700", "Le debo 0". ✔
- Lo anterior + reembolso 700: Econ = 0; Y = 0; X = 0; Prepago = 0. → cuenta limpia. ✔
- Coexisten deuda viva 500 (compra +500 sin pagar) y anulado pagado 1000 con multa 300 sin reembolsar:
  Econ = 500−1000+300 = −200; Y = 700; X = max(0,−200+700) = 500; Prepago = 0. → "Le debo 500" y
  "Me tiene que devolver 700" a la vez, sin netearse. ✔
- Prepago genuino: pagué 1500 a un servicio vivo de 1000: Econ = 1000−1500 = −500; Y = 0; X = 0;
  Prepago = 500. ✔

**Dónde vive el cálculo**: un calculador PURO nuevo `SupplierAccountReconciliation` (espejo de
`SupplierAccountStatementBuilder`) que recibe `(economicLines, receivableY)` por moneda y devuelve
`{ X, Y, Prepago, ClosingBalance, Lines }`. Testeable sin DB. `SupplierService` lo orquesta (lee caja + multas +
reembolsos + invoca el receivable del `OperatorRefundReadModelService`).

---

## 4. Cambios de modelo de datos, migración y seams del contador

### 4.1 Modelo de datos
- (a) Cierre sin multa: **sin entidad nueva**. Reusa `PenaltyStatus=Confirmed` + `PenaltyAmount=0`. Opcional
  (no esencial): un `AuditAction` nuevo. Aditivo.
- (b)(c) Posteos: **sin entidad nueva, sin migración** si se derivan en lectura (recomendado). Solo se agregan
  constantes `PenaltyRetained` / `RefundReceived` a `SupplierAccountStatementLineKinds`.
- DTO de salida: agregar al overview/statement, **por moneda**, `{ ITheyOwe (X), TheyOweMe (Y), Prepayment }`.
  Aditivo, backward-compatible (campos nuevos).

### 4.2 Migración
- Ninguna migración de esquema necesaria para el núcleo. Riesgo de datos = **cero filas nuevas**; todo se deriva.
- Lo único con riesgo NO es el esquema sino la **semántica del reconciler de prepago** (§4.3).

### 4.3 Seam crítico: el reconciler de prepago de ADR-041
Hoy `SupplierCreditReconciler` materializa `max(0, −Balance)` (Balance = caja). Si en el futuro el saldo de caja se
vuelve negativo por una cancelación y el reconciler corre, **convertiría un reembolso por cobrar en saldo a favor
consumible** — incorrecto. El pool de prepago debe pasar a reflejar **`Prepago = max(0, −(EconomicClosingBalance +
Y))`**, NO `max(0, −Balance)`. Es decir, el reconciler tiene que descontar el receivable y los posteos de
multa/reembolso. Esto toca código de plata vivo (ADR-041) y debe hacerse en lockstep con §3. **Riesgo alto.**

### 4.4 Seams que NO puedo finalizar sin el contador (en paralelo lo diseña travel-agency-accountant)
1. **Multa pass-through vs cargo propio de la agencia**: ¿la baja de `RefundCap` y el posteo al extracto del
   operador aplican solo a pass-through? (yo asumo que sí). Define si el operador refunda completo o neto.
2. **Cierre sin multa**: ¿requiere algún documento/registro fiscal o basta el audit interno? (asumo que no emite nada).
3. **Naturaleza contable de "Multa retenida"**: ¿es un costo/gasto de cancelación (cargo de compra) o un ajuste?
   Esto fija si suma a "compras" o es un movimiento aparte en el libro mayor del operador.
4. **Reembolso recibido en moneda distinta a la del pago** (pagué USD, devuelven ARS): el cuadre es por moneda;
   si las monedas difieren queda un residuo por moneda que el contador debe decidir cómo tratar (no inventar TC).
5. **Multi-operador**: `ConfirmPenaltyAsync` lleva UN monto a nivel BC-padre y no desagrega por operador
   (limitación declarada, `BookingCancellationService.cs:3331-3335`). Postear multa POR operador requiere multa por
   línea. Mientras siga siendo monto único, el posteo por operador es aproximado en cancelaciones multi-op.

---

## 5. Boundaries de módulo, atomicidad, auditoría e idempotencia

### 5.1 Boundaries
- `BookingCancellationService`: dueño de confirmar/cerrar multa (escribe `PenaltyStatus`, `PenaltyAmount`).
  NO escribe en el lado proveedor (no hay filas que escribir; el extracto las deriva).
- `OperatorRefundService`: dueño del recibo/imputación del reembolso (escribe allocations,
  `ReceivedRefundAmount`). NO escribe extracto.
- `SupplierService` + `SupplierAccountReconciliation` (nuevo, puro): única lectura que combina caja + multas +
  reembolsos + receivable en los dos números. Depende (read-only) de `OperatorRefundReadModelService` para Y.
- Acoplamiento entre módulos = solo lectura a nivel proyección. Sin dependencias circulares.

### 5.2 Atomicidad
- (a) Cierre sin multa: ya corre en transacción propia (patrón de `ConfirmPenaltyAsync`, `SaveChanges` paso c).
- (b)(c) Posteos derivados: no escriben → nada que atomizar.
- (§4.3) Reconciler de prepago: debe correr en la MISMA transacción que el cambio que mueve el balance económico.
  Por eso hay que **disparar el reconciler también desde confirmar-multa y recibir-reembolso** (hoy solo lo
  disparan los pagos al operador). Riesgo de olvido = pool desincronizado.

### 5.3 Auditoría
- Confirmar multa: ya audita (`BookingCancellationService.cs:3345`). Cerrar sin multa: audit nuevo con
  quién/cuándo/motivo. Recibir reembolso: ya audita. Posteos derivados: no requieren audit propio (sus filas fuente
  ya están auditadas).

### 5.4 Idempotencia por posteo
- Cierre sin multa: candado `PenaltyStatus`.
- Posteo de multa (derivado): idempotente — deriva de `PenaltyStatus=Confirmed` (un solo estado).
- Posteo de reembolso (derivado): idempotente — deriva de allocations no anuladas; anular una allocation quita su
  línea automáticamente.

---

## 6. Riesgos y orden de construcción por fases

### 6.1 Riesgos
1. **Reconciler de prepago (ADR-041) mal-clasifica el receivable como saldo a favor** si sigue usando
   `max(0,−Balance)`. ALTO. Hay que arreglarlo en lockstep (§4.3).
2. **Invariante existente** "ClosingBalance del extracto == `SupplierBalanceByCurrency.Balance`"
   (`SupplierAccountStatementBuilder.cs:67-78`, con test) se ROMPE si las líneas de multa/reembolso entran al
   running balance principal y la deuda materializada sigue siendo solo caja. Decidir: (i) bloque "Circuito de
   cancelación" separado en el extracto y mantener el bloque de caja intacto, o (ii) actualizar la invariante a la
   versión económica. Recomiendo (i) para el line-by-line + los dos números en el header desde el calculador. MEDIO.
3. **Saldo materializado del operador no se recalcula al cancelar** (§1.2). Aunque los dos números se deriven, hay
   que decidir si además refrescamos `SupplierBalanceByCurrency` al cancelar para que AP/topes de pago no usen un
   número viejo. MEDIO.
4. **Multa pass-through vs agency-owned** sin resolver puede reducir `RefundCap` de más (operador refunda menos de
   lo real). Seam contador. MEDIO-ALTO (es plata).
5. **Multi-operador / monto único de multa**: posteo aproximado. BAJO mientras sea mono-operador; documentar.
6. **Multimoneda multa≠pago≠reembolso**: residuos por moneda. Seam contador. MEDIO.

### 6.2 Orden de construcción
- **Fase 0** (hecha): `RefundCap` neto de multa.
- **Fase A — Cierre sin multa** (`WaiveOperatorPenaltyAsync` + endpoint + capacidad UI + audit). Bajo riesgo,
  destraba el dead-end. Backend + gate UX para el botón.
- **Fase B — Extracto económico + reconciliación** (read-only): nuevos `Kinds` derivados, calculador puro
  `SupplierAccountReconciliation`, DTO con los dos números por moneda, tests de cuadre. Sin migración. Resolver el
  riesgo 2 (bloque separado vs invariante nueva).
- **Fase C — Reconciler de prepago económico** (§4.3): el más riesgoso (toca el pool de plata ADR-041); va después
  de B para tener los números visibles y testeables. Disparar el reconciler también en confirmar-multa y
  recibir-reembolso.
- **Fase D — Frontend** (gate UX con Gastón): mostrar los dos números, el botón "cerrar sin multa", y los nuevos
  tipos de movimiento en el extracto.
- **Seams contador (§4.4)**: bloquean el cierre fino de B y C; A puede avanzar sin ellos.

---

## 7. Autocrítica (dónde el código actual lo hace difícil / qué no verifiqué)
- El extracto y la deuda materializada están atados por una invariante con test; meter multa/reembolso al running
  balance la rompe. No es gratis: o se separa el bloque o se reescribe la invariante y su test.
- El reconciler de prepago de ADR-041 está acoplado a `max(0,−Balance)`. Es la pieza más frágil: si no se actualiza,
  el saldo a favor del operador puede inflarse con plata que en realidad es "por cobrar". Lo marco como bloqueante
  de la Fase C.
- `ConfirmPenaltyAsync` carga UN monto a nivel BC-padre; el posteo por operador en cancelaciones multi-operador es
  aproximado hasta que la multa sea por línea. No lo resuelvo acá; lo declaro.
- **No verifiqué** (lectura pendiente, lo digo explícito): `SupplierDebtPersister`/`SupplierDebtCalculator` en
  detalle, el test exacto de la invariante extracto↔balance, ni el frontend. La recomendación de "derivar en
  lectura" asume que la proyección del extracto es el único consumidor sensible; AP/topes de pago consumen el
  `Balance` materializado y NO verían los dos números salvo que se ajuste (riesgo 3).
- Lo fiscal de §4.4 NO está cerrado; sin la firma del contador, las Fases B/C no se finalizan, solo se dejan
  preparadas.
