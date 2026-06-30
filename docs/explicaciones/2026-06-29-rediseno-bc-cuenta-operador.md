# Rediseño técnico de los Pasos B y C de la "cuenta del operador" (post architecture-review)

Fecha: 2026-06-29
Autor: software-architect
Estado: Propuesto — **revisión 2** (resuelve los bloqueantes B1-B4 + mejoras M1-M2 del `software-architect-reviewer`).
Corrige el cuadre y el fix del reconciler del diseño previo
(`2026-06-28-diseno-conciliacion-cuenta-operador.md`). Insumo fiscal duro:
`2026-06-29-resolucion-fiscal-cuenta-operador.md` (NO se reabre).

> **Changelog rev 2 (post-review, todo verificado contra código):**
> - **B2** — la "Multa retenida" NO se deriva de `line.PenaltyStatus==Confirmed`: ese estado **nunca se setea a
>   propósito** (`BookingCancellationService.cs:3776-3779`, comentario explícito: tocarlo activaría el conteo
>   multi-operador de la ND). La confirmación vive en el **padre** `bc.PenaltyStatus==Confirmed` (`:3346`). Se deriva
>   de `line.PenaltyAmount>0` **gateado por** `bc.PenaltyStatus==Confirmed`. Corregido §2, §3.2.
> - **B3** — los 4 términos tienen ciclos de vida distintos. Multa y Reembolso se leen sobre **TODAS las BC
>   no-abortadas** del operador (incluida `Closed`), no solo las abiertas; solo `Y` se scopea a BCs abiertas. Si no,
>   al cerrar la BC el pool re-mintea la fuga. Corregido §3.2, §4.3, + test de regresión.
> - **B1+B4** — retiré el reclamo de una identidad aritmética universal sobre fuentes 100% independientes: los pagos
>   **no están etiquetados por servicio**, así que `Pagado(anulados)` NO es computable independiente, y `RefundCap`
>   está **capeado por costo** (`min(pool_pagado, serviceCost) − penalty`, `:5263-5271`). Reemplazado por (i)
>   invariante estructural del reconciler, (ii) invariante de cierre del circuito, (iii) matriz de escenarios con
>   esperados de dominio, y (iv) **decisión de producto** sobre excedente costo-cap y sobre-reembolso. §2, §2.4.
> - **M1** — atomicidad cross-servicio precisada: un solo escritor de `SupplierBalanceByCurrency`
>   (`SupplierDebtPersister.PersistAsync`, sin SaveChanges ni transacción propia), sin anidar execution strategies.
>   §4.4-4.5.
> - **M2** — **prueba** de net-neutralidad del camino de anular ⇒ el throw `INV-SUPCREDIT-001` es **inalcanzable
>   desde anular** (sí alcanzable desde editar/borrar pago y desde sobre-reembolso). No se diseña UX de error de
>   anulación. §4.6.

> En fácil (para Gastón): cuando anulás un viaje que ya le pagaste al operador, hoy la cuenta del operador queda
> mostrando que te debe toda la plata para siempre. Queremos dos números separados por moneda — **"Le debo $X"** y
> **"Me tiene que devolver $Y"** — que cierran en cero cuando el operador devuelve o se queda una multa. Este
> documento corrige dos cosas que el revisor marcó del diseño anterior: (1) el "cuadre" de antes no probaba nada
> (era una cuenta que daba bien por definición), ahora se prueba con plata real; y (2) hay una cañería vieja
> (el "saldo a favor" del operador) que, si no se toca con cuidado, convierte un "me tiene que devolver" en plata
> gastable — una fuga real. Acá está el arreglo.

---

## 0. Qué de esto ya está construido (verificado en HEAD)

- **Paso A — cierre sin multa: HECHO.** `WaiveOperatorPenaltyAsync` existe y está testeado
  (`BookingCancellationService.cs:3426`; audit `AuditActions.OperatorPenaltyWaived`; suite
  `CancellationWaivePenaltyTests.cs`). Su reversión Admin también (`BookingCancellationService.cs:3543+`). **NO se
  rehace.** Este doc agrega solo el disparo del reconciler en esos caminos (§5).
- **Fase 0 — RefundCap neto de multa: HECHO.** `AllocateConfirmedPenaltyToLinesAsync` setea
  `line.PenaltyAmount = share` y `line.RefundCap -= share` por línea (`BookingCancellationService.cs:3869-3870`),
  **solo para las líneas del operador principal** `l.SupplierId == bc.SupplierId` (línea 3797). Esto es central para
  el cuadre real (§2) y para la limitación multi-operador (§3.4).

---

## 1. Qué cambia respecto del diseño previo, y por qué

| # | Diseño previo (2026-06-28) | Corrección | Sección |
|---|---|---|---|
| 1 | Cuadre = identidad `X − Y − Prepago ≡ EconomicClosingBalance` | **Tautológico** (X y Prepago se *definen* desde Econ e Y). Reemplazado por: invariante estructural del reconciler + invariante de cierre del circuito + matriz de escenarios con esperados de dominio (no hay identidad universal sobre fuentes 100% independientes; los pagos no se etiquetan por servicio). | §2 |
| 2 | Fix del reconciler descripto en prosa | Detallado: fórmula `Prepago = max(0, −(Balance + Multa + Reembolso + Y))`, **alcance temporal de cada término** (B3), disparadores, atomicidad cross-servicio (M1), análisis del modo de falla (M2). | §4 |
| 3 | "Bloque separado vs invariante nueva" dejado a decidir | **Decidido**: bloque "Circuito de cancelación" aparte; el running balance de caja y la invariante `ClosingBalance == SupplierBalanceByCurrency.Balance` (`SupplierAccountStatementBuilder.cs:67-78`) **no se tocan**. | §3 |
| 4 | Multa "infla la cuenta del operador" como cargo +300 que neutraliza el pago | **El receivable Y YA nace neto de multa** (`RefundCap = pagado − multa`). El "+Multa retenida" del extracto es la **contrapartida de visualización** del pago negativo, no un segundo descuento. | §2 |

El insumo fiscal (`2026-06-29`) confirma la dirección: el operador **siempre reembolsa neto de su multa**; la multa
retenida **reduce "me tiene que devolver", no infla "le debo"** (SEAM 1/3). Eso es exactamente lo que hace hoy
`RefundCap = pagado − multa`. El diseño se apoya en ese mecanismo ya existente en vez de inventar un posteo nuevo.

---

## 2. El cuadre (qué se prueba, sin tautología y sin sobre-prometer)

### 2.0 Las fuentes y su gate correcto (corrección B2)

Por **operador + moneda**, los términos del circuito y su fuente **verificada**:

| Término | Fuente | Gate / alcance | Verificado |
|---|---|---|---|
| `Balance` (caja) | `SupplierBalanceByCurrency.Balance` = compras vivas − pagos | permanente; el reembolso recibido **NO** reduce `SupplierPayment` | `SupplierDebtPersister.cs:62-81` |
| `MultaRetenida` | `Σ BookingCancellationLine.PenaltyAmount` | `bc.PenaltyStatus==Confirmed` **(padre)** AND `line.PenaltyAmount>0` AND `ConceptKind=PassThrough` AND `bc.Status != Aborted` | `:3346` (set padre), `:3869` (set línea), **NO** `line.PenaltyStatus** (`:3776-3779`) |
| `ReembolsoRecibido` | `Σ OperatorRefundAllocation.NetAmount` | NOT `IsVoided` AND `bc.Status != Aborted` | `OperatorRefundAllocation.cs:42,50` |
| `Y` (por cobrar) | `Σ max(0, RefundCap − ReceivedRefundAmount)` | **solo BCs abiertas** (`AwaitingOperatorRefund`/`AbandonedByOperator`) | `OperatorRefundReadModelService.cs:96-97,134-136` |

> **B2 (crítico, corregido):** el diseño rev 1 derivaba "Multa retenida" de `line.PenaltyStatus==Confirmed`. Eso
> **nunca dispara**: `AllocateConfirmedPenaltyToLinesAsync` setea `PenaltyAmount` pero **deja `line.PenaltyStatus`
> intacto a propósito** (`BookingCancellationService.cs:3776-3779`: tocarlo activaría
> `CountSuppliersWithConfirmedPenaltyAsync` y desviaría la ND del cliente). La confirmación real es el **padre**
> `bc.PenaltyStatus = Confirmed` (`:3346`). → El gate correcto es `line.PenaltyAmount>0` **AND**
> `bc.PenaltyStatus==Confirmed`. Sin esta corrección, el feature no-opea en silencio (Multa siempre 0).

### 2.1 Qué se prueba realmente (no es una identidad universal)

**No existe** una identidad aritmética universal sobre fuentes 100% independientes, y el diseño rev 1 la
sobre-prometió. Dos razones verificadas:

1. **Los pagos NO están etiquetados por servicio.** `SupplierPayment` imputa por reserva/servicio opcionalmente,
   pero el universo de "lo pagado por los servicios anulados" no es separable de "lo pagado por servicios vivos" sin
   una atribución que el modelo no garantiza. → `Pagado(anulados)` no es un término independiente computable.
2. **`RefundCap` está capeado por COSTO**, no por lo pagado: `capBeforePenalty = min(pool_pagado, serviceCost)` y
   `RefundCap = capBeforePenalty − penalty` (`BookingCancellationService.cs:5263-5271`). Si sobrepagué un servicio
   y lo anulo, el excedente sobre el costo **no entra en `Y`**.

Lo que SÍ se prueba (rev 2):

**(a) Invariante estructural del reconciler** (debe valer para CUALQUIER dato; lo garantiza la fórmula §4.2):
```
   X − Prepago  ==  Balance + MultaRetenida + ReembolsoRecibido + Y
   X ≥ 0 ,  Prepago ≥ 0 ,  X · Prepago == 0        (nunca ambos positivos)
```
Esto NO es el cuadre que detecta bugs (es la definición de X/Prepago); es la garantía de que el calculador nunca
produce un estado imposible (deuda y saldo a favor simultáneos en la misma moneda).

**(b) Invariante de cierre del circuito** (semi-independiente, detecta bugs de sourcing/scope):
```
   Σ capBeforePenalty(líneas anuladas)  ==  MultaRetenida + ReembolsoRecibido + Y
   donde  capBeforePenalty(línea) = RefundCap + PenaltyAmount   (reconstrucción verificada en :5243)
```
El único término plenamente independiente es `ReembolsoRecibido` (allocations). La igualdad **falla** si:
el scope de Multa/Reembolso es incorrecto (B3), si el gate de Multa es incorrecto (B2), o si hay sobre-reembolso
(`received > cap`, ver §2.4-b). Es un detector real, no álgebra.

**(c) Matriz de escenarios con esperados de dominio** (el núcleo no-tautológico): cada escenario alimenta hechos de
ledger independientes (pagos, compras, penalidades, reembolsos) y asierta `X/Y/Prepago` calculados **a mano por
razonamiento de negocio**, no por la fórmula. Un bug de scope/gate/doble-conteo desvía el resultado del esperado.
Casos obligatorios: M-A..M-F (§2.3) + **cierre de BC** (B3, §4.3) + **sobrepago-cap** y **sobre-reembolso** (§2.4).

### 2.2 Los dos números del header (derivados de un calculador puro)

Por operador + moneda:

```
   CashClosingBalance      = saldo del extracto de CAJA (intacto; == SupplierBalanceByCurrency.Balance)
   CircuitTotal            = MultaRetenida(+) + ReembolsoRecibido(+)        ← bloque separado
   EconomicClosingBalance  = CashClosingBalance + CircuitTotal              ← derivado, solo header
   Y  = ReembolsoPorCobrar = Σ max(0, RefundCap − ReceivedRefundAmount)     ← read-model autoritativo
   X  = LeDebo             = max(0,  EconomicClosingBalance + Y)
   Prepago (consumible)    = max(0, −(EconomicClosingBalance + Y))
```

`X` e `Y` **nunca se netean entre sí**: pueden convivir "Le debo 500" y "Me tiene que devolver 700" en la misma
cuenta. El cuadre que se TESTEA no es el cálculo de `X`/`Prepago` (eso es presentación), sino §2.1: que la caja, la
multa, el reembolso y `Y` —cuatro fuentes— conserven la plata.

### 2.3 Ejemplos numéricos verificados a mano

**M-A — anulado pagado 1000, multa 300 confirmada, sin reembolso (1 operador, ARS):**
- `RefundCap = 1000−300 = 700`; recibido 0 → `Y = 700`.
- Caja: la compra cancelada deja de contar; queda pago −1000 → `CashClosing = −1000`.
- Circuito: Multa +300, Reembolso 0 → `CircuitTotal = 300`. `Econ = −700`.
- `X = max(0, −700+700) = 0`; `Prepago = max(0, −(−700+700)) = 0`.
- Invariante de cierre (b): `capBeforePenalty = 700+300 = 1000 == Multa 300 + Reembolso 0 + Y 700` ✓.
  → "Me tiene que devolver 700 / Le debo 0". ✓

**M-B — lo anterior + reembolso 700 recibido:**
- recibido 700 → `Y = max(0, 700−700) = 0`. Caja −1000. Circuito 300+700=1000. `Econ = 0`.
- `X = 0`, `Prepago = 0`. Cuadre cierra en CERO con fuentes independientes: `−1000(caja)+300(multa)+700(reemb)=0` ✓.
  Cuenta limpia. ✓

**M-C — coexisten deuda viva 500 (sin pagar) y anulado pagado 1000 con multa 300 sin reembolsar:**
- Caja: compra viva +500, pago −1000 → `CashClosing = −500`. `RefundCap=700 → Y=700`. Circuito 300. `Econ = −200`.
- `X = max(0, −200+700) = 500`; `Prepago = max(0, −(−200+700)) = max(0,−500) = 0`.
- → "Le debo 500" Y "Me tiene que devolver 700" a la vez, sin netearse. ✓
- **Acá se ve la fuga:** hoy el reconciler haría `max(0,−Balance)=max(0,500)=500` de **saldo a favor gastable**. Pero
  ese −500 de caja NO es prepago: es "pagué 1000 por algo anulado y debo 500 por algo vivo". El fix lleva Prepago a 0. ✓

**M-D — sobrepago genuino, sin anulación: pagué 1500 a un servicio vivo de 1000:**
- Caja: compra +1000, pago −1500 → `CashClosing = −500`. Sin anulación → `Y=0`, circuito 0. `Econ=−500`.
- `X = 0`; `Prepago = max(0, 500) = 500`. ✓ Saldo a favor consumible legítimo, idéntico a hoy.

**M-E — multi-operador (P principal + Q):** son **cuentas separadas**.
- P: pagado 1000, multa 300 confirmada (solo al principal), reembolso 0 → cuenta P: `Econ=−700, X=0, Y=700`.
- Q: pagado 500, **sin multa aplicada** (limitación §3.4), reembolso 0 → cuenta Q: `Econ=−500, X=0, Y=500`.
- Correcto por operador. Limitación conocida: si Q *también* retuvo multa, hoy no se puede registrar (§3.4).

**M-F — multimoneda (pago USD, reembolso en ARS):**
- Pagado USD 1000, multa USD 300 → `RefundCap` USD 700, `Y_USD = 700`. El operador manda ARS.
- Los ledgers ARS y USD **no se netean** (decisión fiscal SEAM 4 + `Suposición` del insumo). El reembolso ARS
  entra a su propio bloque ARS; **no reduce** el receivable USD en el cuadre por moneda. Queda residuo por moneda.
- Regla (SEAM 4, sin inventar TC): el receivable vive en la **moneda del pago** (USD); un reembolso en otra moneda
  se aplica al **TC snapshot del día del cobro** (`OperatorRefundReceived.ExchangeRateAtReceipt`,
  `OperatorRefundReceived.cs:64`); la diferencia es **diferencia de cambio a resultados** (la absorbe la agencia, sin
  comprobante). **El circuito ancla en la moneda del pago.** Si falta snapshot o la moneda de la multa difiere del
  pago, el circuito de ese operador se marca **"no concilia automáticamente — revisar"** en vez de inventar TC.

### 2.4 Los dos casos borde que rompían la identidad ingenua (B1+B4) — y su tratamiento

Ninguno es un bug: la fórmula §4.2 los resuelve sola. Se documentan para que los **tests los esperen como correctos**.

**(a) Sobrepagué-y-anulé (excedente costo-cap):** pagué 1500 a un servicio de costo 1000, lo anulo, sin multa, sin
reembolso.
- `capBeforePenalty = min(1500, 1000) = 1000` (`:5263`) → `RefundCap = 1000` → `Y = 1000`. Caja `Balance = −1500`.
- Multa 0, Reembolso 0. `Econ = −1500`. `X = max(0, −1500+1000) = 0`;
  `Prepago = max(0, −(−1500+1000)) = max(0, 500) = 500`.
- → La fórmula parte los 1500 en **Y=1000 (por cobrar)** + **Prepago=500 (consumible)**. Los 500 sobre el costo NO son
  parte del ciclo de reembolso (el cap se topea en costo): son sobrepago puro, idéntico a M-D.
- **DECISIÓN DE PRODUCTO (recomendación):** el excedente costo-cap de un servicio anulado = **saldo a favor
  CONSUMIBLE** (aplicable a otra reserva del mismo operador/moneda), NO "me tiene que devolver" en efectivo.
  *Fundamento:* ese excedente nunca estuvo atado a la penalidad/refund de la cancelación; estructuralmente es igual a
  pagar de más un servicio vivo (M-D); el operador retiene plata sin servicio detrás = saldo a favor. Tratarlo como
  consumible reusa el pool existente y evita inventar un segundo concepto de "receivable por sobrepago". La fórmula
  ya hace exactamente esto.
- ⚠️ **CAMBIA LO QUE EL DUEÑO VE — consultar antes (lo marco aparte):** en este caso el dueño verá *"Saldo a favor
  500"* (gastable en otra reserva) en vez de *"Me tiene que devolver 500"* (efectivo). La alternativa (tratar el
  excedente como efectivo por cobrar) es defendible si el dueño prefiere **siempre** recuperar plata antes que
  arrastrar crédito con ese operador. **Recomiendo consumible; necesita confirmación del dueño.**

**(b) Sobre-reembolso (`received > RefundCap`):** pagué 1000, multa 300 → `RefundCap=700`, pero el operador devuelve
800.
- `Y = max(0, 700−800) = 0` (clampeado). Caja `Balance = −1000`. Multa 300, Reembolso 800. `Econ = −1000+300+800 = +100`.
- `X = max(0, 100+0) = 100`; `Prepago = 0`. → "Le debo 100": el operador devolvió 100 de más, la agencia los tiene =
  se los debe. **Económicamente correcto**, no es bug. La invariante de cierre (b) **rompe a propósito** acá
  (`Σcap 1000 ≠ Multa 300 + Reembolso 800 + Y 0 = 1100`): el test debe **tolerar** este caso aceptando que el sobrante
  `received − cap` aparece como `X`.
- **Recomendación menor (no bloqueante):** al imputar una allocation cuyo `NetAmount` supera el `Y` disponible del
  operador, mostrar un **aviso** ("el operador devolvió más de lo esperado; quedará como saldo a tu favor del
  operador"). Es guard de captura, follow-up; la fórmula no se rompe sin él.

---

## 3. Cómo se ve en el extracto: caja intacta + bloque "Circuito de cancelación" aparte

### 3.1 La invariante existente NO se toca

`SupplierAccountStatementBuilder` arma el extracto de caja con `Purchase(+)` / `Payment(−)`
(`SupplierAccountStatementBuilder.cs:14-18`) y su `ClosingBalance` **debe** coincidir con
`SupplierBalanceByCurrency.Balance` (invariante con test, `:67-78`). **Las líneas nuevas NO entran a ese running
balance.** El builder de caja queda como está.

### 3.2 Dónde viven las líneas nuevas

Un **bloque separado** por moneda, derivado-en-lectura, con dos `Kinds` nuevos (constantes nuevas en
`SupplierAccountStatementLineKinds`, sin tocar las viejas):
- `PenaltyRetained` (+) — fuente: `BookingCancellationLine.PenaltyAmount`, **gate corregido (B2):**
  `bc.PenaltyStatus==Confirmed` (padre, `:3346`) AND `line.PenaltyAmount>0` AND
  `ConceptKind=OperatorPenaltyPassThrough`. Moneda = `PenaltyCurrency` (ISO, `BookingCancellationLine.cs:85-100`),
  operador = `SupplierId` de la línea. NO se usa `line.PenaltyStatus` (nunca se setea, `:3776-3779`). Solo
  pass-through: un cargo propio de la agencia (agency-owned) NO es plata que retuvo el operador (SEAM 1/3).
- `RefundReceived` (+) — fuente: `OperatorRefundAllocation.NetAmount` (NOT `IsVoided`), moneda =
  `OperatorRefundReceived.Currency`, operador del BC/línea.

**Alcance temporal (corrección B3):** ambas líneas se derivan sobre **TODAS las BC del operador con
`Status != Aborted`**, incluida `Closed`. Si se scopearan solo a las BC abiertas, al cerrar la cancelación el pago
−1000 seguiría en el extracto pero el +300/+700 que lo explican desaparecerían → el extracto volvería a verse "roto"
y (peor) el reconciler re-mintearía la fuga (§4.3). Solo `Y` se scopea a BCs abiertas (es "lo que falta cobrar"; una
BC cerrada ya no tiene nada por cobrar).

Estas líneas son **informativas/contrapartida**: muestran qué pasó con el pago negativo. No alteran la caja ni la
deuda materializada. Idempotentes por construcción (derivan de estado, no de eventos repetibles).

### 3.3 Calculador puro nuevo

`SupplierAccountReconciliation` (Domain, espejo de `SupplierAccountStatementBuilder`, testeable sin DB). Firma:

```
   Input por moneda:  cashLines (las del extracto de caja ya armadas),
                      circuitLines (PenaltyRetained + RefundReceived),
                      receivableY (del read-model, por moneda)
   Output por moneda: { CashClosingBalance, CircuitTotal, EconomicClosingBalance, Y, X, Prepago, Lines }
```

`SupplierService.GetSupplierAccountStatementAsync` (`:574`) lo orquesta: ya arma `cashLines`
(`inputLines`, `:621-652`); agrega los `circuitLines` desde BC lines + allocations; lee `Y` de
`IOperatorRefundReadModelService`. **El calculador toma `CashClosingBalance` de las líneas frescas (compras vivas −
pagos), NO de la proyección materializada** → la Fase B queda correcta aunque `SupplierBalanceByCurrency` esté viejo
(eso solo afecta a C, §4).

### 3.4 Multa por operador / por línea (limitación y migración)

El insumo fiscal SEAM 5 exige multa **por operador/línea**, en su moneda; el total a nivel reserva es solo roll-up.
Estado verificado: `PenaltyAmount` ya es **por línea** (`BookingCancellationLine.cs:83`) y la derivación de
`PenaltyRetained` agrupa por `SupplierId` correctamente. La limitación NO está en el modelo de datos sino en el
**camino de confirmación**: `AllocateConfirmedPenaltyToLinesAsync` recibe **un** `confirmedPenaltyAmount` y lo reparte
**solo entre las líneas del operador principal** `bc.SupplierId` (`:3797`, `:3849-3871`). En una anulación
multi-operador, los operadores no-principales quedan con `PenaltyAmount` nulo → su `Y` = todo lo pagado.

**Necesita mi diseño multa por línea?** Para el cuadre y el extracto, **no** (ya leen `PenaltyAmount` por línea). Lo
que falta es la **captura**: poder confirmar una multa distinta por operador. Migración sin romper mono-operador:
extender `ConfirmPenaltyAsync` para aceptar un **mapa `{SupplierId → monto, moneda}`** (o llamadas por operador),
manteniendo la firma de monto único como atajo que aplica al principal (comportamiento actual = default). Mientras
sea mono-operador, el resultado es idéntico. **Esto es follow-up, fuera del alcance B/C**; lo declaro para que el
extracto multi-operador se lea "aproximado" hasta entonces (Q queda con Y completo).

---

## 4. (C) El fix del reconciler de prepago — código de plata vivo (ADR-041)

### 4.1 El bug, cuantificado

`SupplierCreditReconciler` materializa el pool de saldo a favor consumible como `overpayment = max(0, −Balance)`,
`Balance = caja (compras − pagos)` (`SupplierCreditReconciler.cs:75`). Una anulación hace que la compra **deje de
contar** mientras el pago sigue → `Balance` negativo. Si el reconciler corre (hoy lo dispara alta/edición/baja de un
pago, `SupplierService.cs:867,977,1029`), **convierte ese negativo en saldo a favor GASTABLE en otra reserva**. Pero
ese negativo es un **reembolso por cobrar** (el operador debe devolver efectivo), **no** un prepago consumible.
Ejemplo M-C: el fix evita mintear 500 de crédito gastable que en realidad el operador te debe.

(Precisión rev 2: `BookingCancellationService` **no** llama al método privado `SupplierService.PersistSupplierBalanceAsync`,
pero **sí** recalcula el balance materializado por los caminos de cancelación formal vía
`SupplierDebtPersister.PersistForReservaSuppliersAsync` (`SupplierDebtPersister.cs:96-100,108`). Lo que falta NO es
el recálculo del `Balance` sino encadenar el **reconcile del pool** después (§4.4 C0). Para B esto es indiferente: el
calculador de B arma la caja en vivo, no lee la proyección (§3.3).)

### 4.2 La fórmula nueva

```
   overpayment[ccy] = max(0, −( Balance[ccy] + MultaRetenida[ccy] + ReembolsoRecibido[ccy] + Y[ccy] ))
```

equivalente a `max(0, −(EconomicClosingBalance + Y))` = el `Prepago` de §2.2. Verificación con los ejemplos:
M-A `−(−1000+300+0+700)=0`→0; M-C `−(−500+300+0+700)=−500`→0; M-B `−(−1000+300+700+0)=0`→0; M-D `−(−500+0+0+0)=500`→500. ✓
Donde no hay anulación (multa=reemb=Y=0) colapsa a `max(0,−Balance)` = comportamiento actual idéntico (M-D).

**Alcance temporal de cada término (corrección B3) — esto es lo que evita re-mintear la fuga al cerrar la BC:**

| Término | Alcance | Por qué |
|---|---|---|
| `Balance` (caja) | permanente; queda en −pagado para siempre | el reembolso recibido **NO** descuenta `SupplierPayment` (crea un cash income que acredita al cliente, doc previo §1.4) |
| `MultaRetenida` | **todas las BC no-abortadas** del operador (incl. `Closed`) | si se fuera con la BC cerrada, su `+` desaparecería y `Balance` seguiría en −pagado |
| `ReembolsoRecibido` | **todas las allocations no anuladas** del operador (incl. BC `Closed`) | ídem: es la contrapartida permanente del pago negativo |
| `Y` | **solo BCs abiertas** | una BC cerrada ya no tiene nada por cobrar; `Y` desaparece **a propósito** (`OperatorRefundReadModelService.cs:96-97`) |

Verificación del cierre (M-B con la BC **Closed**): `Balance=−1000` (permanente), `Multa=300` (no-abortada),
`Reembolso=700` (no anulada), `Y=0` (BC cerrada). Suma = `−1000+300+700+0 = 0` → `overpayment = 0`. ✓ El pool queda en
0 **después** de cerrar. Con el scope ingenuo (solo abiertas): `−1000+0+0+0 = −1000` → mintearía 1000. ✗ Esa es la
fuga que B3 evita.

### 4.3 Disparadores nuevos (en lockstep)

El pool debe reconciliarse en CADA cambio que mueva alguno de los 4 términos, dentro de la **misma transacción** del
cambio que lo provoca:

| Disparo | Por qué | Dónde (verificado) |
|---|---|---|
| Alta/edición/baja de pago | ya existe | `SupplierService.cs:867,977,1029` |
| **Anular un servicio pagado** | `Balance` cae; hoy se recalcula el balance pero **no** se reconcilia el pool | flujo de cancelación (ya llama `SupplierDebtPersister.PersistForReservaSuppliersAsync`, `:108`) |
| **ConfirmPenalty** | cambia Multa y `RefundCap`→`Y` (net-neutral, §4.6) | `BookingCancellationService.ConfirmPenaltyAsync` |
| **WaiveOperatorPenalty / RevertWaive** | refresca el pool (Multa pasa a 0 / vuelve) | `:3426` / `:3543` |
| **RecordReceived / Allocate / VoidAllocation** | cambia Reembolso y `Y` | `OperatorRefundService` |

**Test de regresión obligatorio (B3):** anular servicio pagado → confirmar multa → recibir reembolso total →
**CERRAR la BC** → assert: `overpayment(pool) == 0` (NO `pagado`). Y la variante sin reembolso: anular → cerrar sin
multa (Waive) → assert el pool refleja `Y` completo como por-cobrar, `Prepago == 0`.

### 4.4 Atomicidad cross-servicio (corrección M1)

Verificado para evitar el doble-escritor y el anidamiento de execution strategies:

1. **Un solo escritor de `SupplierBalanceByCurrency`:** `SupplierDebtPersister.PersistAsync` (`:42-88`). NO hace
   `SaveChanges` ni abre transacción — opera sobre el `AppDbContext` del caller (`:38-41`). `SupplierService`
   delega ahí (`SupplierService.cs:2299-2300`); el flujo de cancelación también (vía `PersistForReservaSuppliersAsync`,
   `:108`, que sí hace un `SaveChanges` final). **No se crea un segundo escritor que compita.**
2. **Trigger del reconciler en la interfaz de aplicación:** agregar a `ISupplierCreditService` (`:14-46`, hoy solo
   Get/Apply/Reverse) un método `Task ReconcileSupplierCreditAsync(int supplierId, CancellationToken ct)`. Su
   implementación (en Infrastructure) llama **directo** a `SupplierCreditReconciler.ReconcileAsync` (transaction-
   agnostic: hace su propio `SaveChanges` pero **no** abre transacción, `:185`). Los servicios de cancelación/refund
   dependen de la interfaz (Application), no del tipo `internal` de Infra → sin ciclo de dependencias.
3. **Sin anidar execution strategies:** el riesgo es abrir un `CreateExecutionStrategy`/`BeginTransaction` dentro de
   otro ya abierto. Regla: el **caller de más arriba** (el flujo de anular/confirmar/refund) abre **una** transacción
   (o reusa la suya); los sub-pasos —`PersistAsync`, `ReconcileSupplierCreditAsync`— son transaction-agnostic y NO
   abren la suya. El método nuevo de la interfaz **no** debe rutear por `SupplierService.RunInWriteTransactionAsync`
   (`:892`, ese sí abre execution strategy): llama al reconciler estático directamente.
4. **Orden dentro de la transacción:** persistir el cambio de estado (`SaveChanges`) → `PersistAsync` por operador
   afectado (`SaveChanges`) → `ReconcileSupplierCreditAsync` (lee `Balance` ya committed-en-txn + circuito + Y;
   `SaveChanges`) → commit. Si el reconciler lanza (§4.6), revierte todo.

**C0 (prerrequisito, ubicado en este flujo):** la anulación ya recalcula `SupplierBalanceByCurrency` por los
caminos `RecalculateMoneyAfterTotalCancellationAsync` / `ApplyAnnulWithPaymentsToCreditAsync` (comentario
`SupplierDebtPersister.cs:96-100`). Lo que **falta** es encadenar el `ReconcileSupplierCreditAsync` después de ese
recálculo, dentro de la misma transacción. C0 = ese encadenado (no un recálculo nuevo).

### 4.5 Idempotencia

El reconciler calcula el pool objetivo y converge (diff=0 → no-op, `:87`). Llamarlo de más es seguro. Los disparos
nuevos no necesitan guard adicional.

### 4.6 ¿El throw `INV-SUPCREDIT-001` es alcanzable desde anular? (corrección M2)

El reconciler **solo lanza** cuando `overpayment` BAJA por debajo de lo ya aplicado a otra reserva (`diff<0` y
`toRemove > totalDrainable`, `:133-138`). Es decir, lanza únicamente si el cambio **reduce** `overpayment`.

**Prueba de net-neutralidad del camino de anular.** Anular un servicio pagado:
- compra deja de contar → `ΔBalance = −costo`;
- se arma la línea BC con `RefundCap = min(pagado, costo) − penalidad`, y al anular la penalidad aún es 0 →
  `ΔY = +min(pagado, costo)`;
- `ΔMulta = 0`, `ΔReembolso = 0`.
- `Δ(Balance+Multa+Reembolso+Y) = −costo + min(pagado, costo)`.
  - si `pagado ≥ costo`: `= −costo + costo = 0` (net-neutral);
  - si `pagado < costo`: `= −costo + pagado = −(costo−pagado) < 0` → la suma **baja** → `−suma` sube →
    `overpayment` **sube o queda igual**.

En ambos casos `overpayment` **nunca baja** al anular → el throw es **inalcanzable desde el camino de anular**.
(Idéntico para `ConfirmPenalty`: `line.PenaltyAmount` se cap-ea al `cap` que reduce, `:3849,3866`, así que
`ΔMulta = +p`, `ΔY = −p` exacto → net-neutral. Y para un reembolso normal `received ≤ cap`: `ΔReembolso=+r`,
`ΔY=−r` → net-neutral.)

**Dónde SÍ es alcanzable** (no cambia, ya existía): editar/bajar un pago (reduce `pagado` → reduce `overpayment`,
`SupplierService.cs:977,1029`), y el caso de **sobre-reembolso** `received > cap` (§2.4-b: `ΔReembolso > −ΔY` →
`overpayment` baja).

**Consecuencia de diseño:** NO se diseña UX de "anulación bloqueada por INV-SUPCREDIT-001" — sería código muerto. El
throw se mantiene en su camino existente (editar/borrar pago) con su mensaje actual; si en el futuro se permite
sobre-reembolso, ese camino reusa el mismo throw saneado. (Esto reemplaza el §4.6 de rev 1, que asumía erróneamente
que anular podía disparar el throw.)

---

## 5. Modelo de datos, migración y persistencia

- **(B) Extracto + dos números:** sin entidad nueva, sin migración. Solo constantes `PenaltyRetained`/`RefundReceived`
  y campos **aditivos** en el DTO: en `SupplierAccountStatementCurrencyBlockDto` (`SupplierReadDtos.cs:305`) agregar
  `EconomicClosingBalance`, `TheyOweMe (Y)`, `ITheyOwe (X)`, `Prepayment`; y/o en
  `SupplierAccountBalanceByCurrencyDto` (`:54`) para el overview. Backward-compatible. Respetan el masking see_cost
  existente (`MapSupplierAccountStatement`, `:701-703`).
- **(C) Reconciler:** sin esquema nuevo; cambia la fórmula y los disparos. Escribe `SupplierCreditEntry` como hoy.
- **TC snapshot (SEAM 4):** ya persisten `SupplierPayment.ExchangeRate*` (`SupplierService.cs:831-833`) y
  `OperatorRefundReceived.ExchangeRateAtReceipt` (`:64`). **Gap verificado:** la multa **no** persiste un TC propio
  (la línea guarda `PenaltyCurrency` pero no su cotización). → El cuadre por moneda solo cierra limpio cuando
  `PenaltyCurrency == moneda del pago`. Multa en otra moneda = **follow-up** (campo TC de multa + firma contador,
  coherente con la nota anti-wire ISO→ARCA de `BookingCancellationLine.cs:92-97`). No invento TC.

**¿Por qué no persistir las líneas del circuito?** Porque derivan determinísticamente de fuentes ya auditadas
(penalty en la línea, allocations con su soft-void + actor). Persistir filas duplicaría la verdad y reintroduciría
la doble representación que se quiere eliminar. Derivar-en-lectura da idempotencia gratis.

---

## 6. Auditoría

- Confirmar/Waive/Revert multa y recibir/anular reembolso **ya auditan** (`OperatorPenaltyWaived` `:3509`,
  `OperatorPenaltyWaiveReverted` `:3609`, refund en `OperatorRefundService`). Las líneas del circuito derivan de esas
  filas → no requieren audit propio.
- El reconciler ya audita creación (`SupplierCreditCreated`, `:108`) y drenaje (`SupplierCreditDrained`, `:167`).
  Con los disparos nuevos, esos audits cubren la reclasificación; el `details` lleva moneda y monto.

---

## 7. Orden de construcción por fases

- **Fase B1 — read-only, riesgo nulo de plata.** Calculador puro `SupplierAccountReconciliation`; derivar circuito
  (PenaltyRetained con el gate B2 + RefundReceived, alcance B3) en bloque separado; `Y` del read-model; DTO aditivo
  con X/Y/Prepago/Econ por moneda; wire en `GetSupplierAccountStatementAsync` y overview. **Caja e invariante
  intactas.** Tests: invariante estructural (§2.1-a), de cierre (§2.1-b), y matriz de escenarios M-A..M-F + casos
  borde §2.4 (§2.1-c). Entrega el valor visible (los dos números) **sin tocar el pool de plata**.
- **Fase C0 — encadenar la reconciliación al recálculo de balance que la anulación ya hace** (§4.4 punto C0).
- **Fase C1 — money-write, riesgo alto.** Fórmula nueva del reconciler (§4.2, con alcance temporal B3) + método
  `ReconcileSupplierCreditAsync` en `ISupplierCreditService` + disparos nuevos (§4.3) + atomicidad §4.4. El throw
  (§4.6) NO se toca (no se diseña UX de anular-bloqueado). Va **después de B** y pasa por `backend-dotnet-reviewer` +
  `security-data-risk-reviewer` (toca pool de plata ADR-041). Incluye el test de regresión B3.
- **Fase D — frontend (gate UX con Gastón).** Mostrar "Le debo / Me tiene que devolver" por moneda + el bloque
  circuito en el extracto. No incluida acá (backend-only).
- **Follow-ups declarados, fuera de B/C:** multa por operador en la captura (§3.4); TC de multa multimoneda (§5);
  "NC de compra recibida del operador" para reversa IVA crédito en RI (SEAM 2, inocuo en Monotributo); perilla
  `InvoicingMode` reseller/intermediario por operador y ND al cliente (default reseller = comportamiento actual,
  **NO** entra en este build).

---

## 8. Autocrítica y qué NO verifiqué

1. **`RefundCap` está capeado por costo** (`min(pool_pagado, serviceCost) − penalty`, `:5263-5271`, verificado en
   rev 2). Eso es lo que descarta la identidad universal ingenua (§2.1) y motiva el caso §2.4-a. Verifiqué los dos
   caminos que escriben `RefundCap` (`AllocateConfirmedPenaltyToLinesAsync :3869` y `AssignRefundCapsAsync :5271`);
   ambos preservan `RefundCap + PenaltyAmount == capBeforePenalty`. La invariante de cierre (§2.1-b) lo testea sobre
   datos.
2. **DECISIÓN DE PRODUCTO ABIERTA (§2.4-a):** excedente costo-cap de un servicio anulado = saldo a favor consumible
   (recomendado) vs efectivo por cobrar. **Cambia lo que el dueño VE.** Lo marqué para consultar con el dueño antes
   de implementar el front (Fase D). No bloquea B1 (la fórmula ya lo trata como consumible; cambiarlo sería un ajuste
   acotado del calculador).
3. **Multimoneda multa≠pago:** la multa no persiste TC. El cuadre por moneda no cierra cross-currency; follow-up con
   firma contador. No inventé cotización (SEAM 4).
4. **Multi-operador:** el extracto lee `PenaltyAmount` por línea correctamente, pero la *captura* de multa por
   operador no existe (solo principal, `:3797`). Operadores no-principales muestran `Y` = todo lo pagado hasta el
   follow-up. Declarado, no resuelto.
5. **Throw `INV-SUPCREDIT-001`:** rev 2 probó (§4.6) que es **inalcanzable desde anular**; queda en su camino
   existente (editar/borrar pago). No se diseña UX de anular-bloqueado. Resuelto.
6. **NO verifiqué:** el test exacto de la invariante extracto↔balance (lo cito por el comentario `:67-78`, no leí el
   test); el frontend; `SupplierDebtCalculator.Calculate` en detalle (asumo correcto por moneda, es código vivo
   usado por toda la cuenta); y si otros consumidores de `SupplierBalanceByCurrency.Balance` (AP, topes de pago)
   verían el `Balance` viejo en la ventana entre el recálculo de la anulación y el reconcile — el orden §4.4 lo
   mitiga, pero no audité todos los lectores.
7. **Lo fiscal NO se reabre**: tomo `2026-06-29-resolucion-fiscal` como dado. Si alguna suposición (Monotributo hoy;
   reseller default) cambia, B/C siguen válidos y se activan los follow-ups RI/intermediario.
