# Modelo de estados derivados de la reserva — DISEÑO de implementación (2026-07-17)

> Documento de arquitectura. **No es código**: es el plano para implementarlo.
> Parte del BORRADOR aprobado por Gaston regla-por-regla
> (`2026-07-17-modelo-estados-derivados-BORRADOR-para-aprobar.md`) y del REVIEW de
> arquitectura (`2026-07-17-modelo-estados-derivados-REVIEW.md`, veredicto
> **APROBADO CON CAMBIOS**). El borrador tiene el diagnóstico verificado de las 4
> mentiras de F-2026-1046, cómo lo hacen SAP/Odoo, las 10 reglas y las 3
> preguntas respondidas. **Acá no se re-diagnostica**: se diseña.
>
> Cada afirmación sobre el código actual está anclada a `archivo:línea` que
> verifiqué (Grep/Read). Lo que no pude verificar lo digo con esas palabras.

---

## Cambios tras review (qué cambió respecto de la v1 del diseño)

Esta versión incorpora los 3 bloqueantes y las mejoras del REVIEW, más las
respuestas de Gaston a las preguntas abiertas:

1. **OQ-1 resuelta por Gaston (impacta B1 / §1.2 / T1).** Mientras el operador
   deba la devolución → cabecera **"Esperando reembolso del operador"**
   (`PendingOperatorRefund`); cuando se salda → **"Anulada"** (`Cancelled`). Esto
   NO es un toggle: obliga a rediseñar la derivación del terminal a **nivel
   reserva con N BC** y a corregir los callbacks de cierre que hoy razonan
   **por-BC** (`BookingCancellationService.cs:3779-3833` y `:4656-4672`). **T1 se
   redimensiona con esto adentro.**
2. **B2 (atomicidad, regla 9).** Se elimina el fallback "la próxima mutación
   corrige" (era corrección diferida encubierta, justo lo que la regla 9 mata).
   Se adopta la **vía atómica**: la derivación del terminal es un **método puro
   estático** (estilo `ReservaStatusTransitioner`, sin `SaveChanges`) invocado
   **dentro de `ReservaMoneyPersister.PersistAsync`, ANTES de su `SaveChanges`**
   (`ReservaMoneyPersister.cs:70`) → plata y estado comparten UNA `SaveChanges`.
3. **B3 (semántica de `IsVoided`).** `IsVoided`/`SinEfecto` incluye **ambos**
   estados terminales `{Cancelled, PendingOperatorRefund}` (más el derivado por
   servicios). El terminal materializado **no es un único `Status`**: es el par.
   T4 y T5 deben respetarlo.
4. **M1.** Bajo la vía atómica, `ReservaService.cs:5005` deja **solo la campana**
   (aviso "confirmada con cambios"); el motor/derivación corre en el persister →
   no se corre dos veces.
5. **M2+M3.** La reparación única pasa a ser **migración EF** (usa
   `__EFMigrationsHistory` como marcador robusto de "corre una sola vez", seteado
   al final por EF). El deploy del VPS es **stop-then-start con migrador dedicado
   que corre antes de levantar la app** (confirmado por el coordinador) → no hay
   rolling, no hay solape de escritura.
6. **M5.** El motor en vivo NO cubre `Traveling` (respeta ADR-036 "En viaje
   inmutable"); la **reparación única sí** barre `Traveling` para el terminal
   derivado. Justificación abajo (§6).
7. **Testing.** Se agrega al plan la lista de suites que el review marcó como
   candidatas a romper (se barren cargando datos reales, jamás relajando asserts)
   y las 2 caminatas E2E nuevas (N BC + refund parcial; vía atómica).

---

## 0. Resumen ejecutivo (1 minuto)

Cuatro máquinas de estado paralelas y ninguna las une. La causa raíz NO es plata
(esa aguanta): es la **capa de coherencia de estados/lectura**. El arreglo:

1. **El motor existente (`ReservaAutoStateService`) aprende a derivar el terminal
   de anulación** cuando la reserva se quedó sin servicios vivos (hoy solo la deja
   "Confirmada con cambios / revisar"). El terminal es el **par**
   `PendingOperatorRefund` (mientras el operador deba) → `Cancelled` (cuando
   saldó), decidido a **nivel reserva con N BC**. Reusa el punto único de
   transición → rastro automático, **sin emitir otra NC**.
2. **El motor pasa a correr en TODOS los puntos de mutación de plata** (cobro,
   cancelación por servicio, NC/ND), plegado **dentro del chokepoint de plata**
   en la **misma `SaveChanges`** → atómico (regla 9, sin corrección diferida).
3. **Todas las superficies leen la MISMA fuente derivada** (`IsVoided` cubre el
   par terminal); el gate de deuda al operador filtra por servicio vivo.

Más una **reparación única por migración EF** de las reservas que hoy mienten
(1046 y hermanas), corrida una sola vez en el migrador del deploy — **sin pasada
nocturna** (regla 9).

---

## 1. El modelo

### 1.1 La función de dominio única

Hoy la pregunta "¿qué ES esta reserva?" está respondida cuatro veces:

- Estado operativo: `Reserva.Status` (badge lo lee en `ReservaHeader.jsx:258`,
  vía `statusConfig` en `ReservaStatusBadge.jsx:21-91`).
- Cobro: `ReservaCollectionStatus.Derive(...)` (`ReservaCollectionStatus.cs:104-130`;
  se llama en `ReservaService.cs:2519` detalle y `:2306/:2336` listado). **Ignora
  comprobantes.**
- Facturación: `ReservaInvoicingStatus.Derive(...)` (`ReservaInvoicingStatus.cs:51-69`;
  `ReservaService.cs:2542` detalle y `:2394` listado). **Colapsa "virgen" con
  "facturada y devuelta"** — el propio comentario `:57-60` lo admite.
- Deuda al operador: `SupplierService.GetReservaSupplierPaymentStatusAsync`
  (`SupplierService.cs:3045`). Gatea por estado de RESERVA (`:3070-3072`) y arma
  filas **sin filtrar servicios anulados** (único `continue` es `CommissionOnly`,
  `:3125`).

**Diseño:** una función de dominio pura, sin infraestructura, que recibe las
partes y devuelve los ejes derivados. Vive en `TravelApi.Domain` (junto a
`ServiceResolutionRules`, `EstadoReserva`), es la ÚNICA que decide.

```
ReservaDerivedState.Compute(
    servicios:      las 6 colecciones (IsCancelled / IsResolved de ServiceResolutionRules)
    comprobantes:   facturas + NC + ND (bruto emitido con CAE, y neto = facturas+ND−NC)
    plata:          balances por moneda + señales de actividad (cargos / cobros)
    bookingCancellations: TODAS las BC de la reserva y sus líneas (RefundCap, RefundStatus)  ← NUEVO (B1)
    estadoActual:   Reserva.Status
) -> ReservaDerivedState {
    OperationalAxis  // Vivo | SinEfecto            ← NUEVO
    OperatorRefundPending // bool: ¿el operador todavía debe devolución? (nivel reserva, N BC)  ← NUEVO (B1)
    CollectionAxis   // SinMovimientos | Saldado | ConDeuda | SaldoAFavor | CircuitoAnulacion
    InvoicingAxis    // SinFacturar | FacturadaEnParte | FacturadaTotal | FacturadaYDevuelta  ← NUEVO valor
    IsVoided         // proyección booleana (ver B3 abajo)  ← la lee el front
}
```

**Reglas puras que encapsula** (todas aprobadas):

- **OperationalAxis = SinEfecto** cuando la reserva **tuvo** servicios y **todos
  están anulados** (`IsCancelled` en las 6 colecciones). Distinción crítica:
  "tuvo servicios y todos anulados" ≠ "nunca tuvo servicios" (una reserva nueva
  vacía NO es Anulada). El motor ya distingue esto: `HasAnyLiveService`
  (`ReservaAutoStateService.cs:272-283`) y la rama "sin servicios activos"
  (`BuildNeedsReviewReason:258-261`). Falta llevarlo a la cabecera.
- **OperatorRefundPending (B1, nivel reserva)** = existe **al menos una**
  `BookingCancellationLine` de **cualquiera** de las BC de la reserva con
  `RefundCap > 0` y `RefundStatus != Settled`. Verificado que estos son los campos
  reales y el criterio que ya usa el cierre por-BC
  (`BookingCancellationService.cs:3796-3811`: cuenta `RefundCap > 0`, "totalmente
  reembolsada" = todas `RefundStatus == Settled`). La diferencia del diseño: se
  agrega a **nivel RESERVA (todas las BC)**, no por-BC.
- **InvoicingAxis = FacturadaYDevuelta** cuando **hubo** comprobante con CAE
  (bruto emitido > 0) pero el **neto** quedó ~0 por las NC. Hoy
  `ReservaInvoicingStatus.Derive` solo recibe `(vendido, facturadoNeto)` (`:51`):
  falta pasarle `brutoEmitido` (o booleano `huboComprobanteAlgunaVez`).
- **CollectionAxis = CircuitoAnulacion** cuando `IsVoided`: la plata se lee del
  circuito de anulación (saldo a favor / multa), no del cobro normal. Reemplaza el
  candado hardcodeado de `moneyStatus.js:32`.

**Definición precisa de `IsVoided` / `SinEfecto` (B3):**

`IsVoided = Status ∈ {Cancelled, PendingOperatorRefund}`.

- Ambos son "sin efecto": `PendingOperatorRefund` es el estado intermedio normal
  de toda anulación con reembolso de operador pendiente
  (`BookingCancellationService.cs:2558, 3644, 5980, 6108, 11825, 12945`), y hoy el
  front (`moneyStatus.js:32, :70-72`) y el backend (`IsCancelledLikeStatus` usado
  por `DeriveCancelledMoneyContextAsync`, `ReservaService.cs:5254`) ya tratan a
  **ambos** como anulada.
- **Por qué basta con proyectar de `Status`:** con el rediseño, el motor lleva la
  cabecera a **exactamente** uno de esos dos estados cuando la reserva quedó sin
  servicios vivos (§1.2). Entonces `Status ∈ {Cancelled, PendingOperatorRefund}`
  es equivalente a `OperationalAxis == SinEfecto`. El conjunto de esos dos estados
  vive en **UN solo lugar del dominio** (p. ej. `EstadoReserva.VoidedStatuses`),
  y tanto backend como front lo consultan; el front deja de hardcodearlo (T4).

### 1.2 El estado terminal (par) y su derivación con N BC (B1 + OQ-1)

**Decisión (Gaston, OQ-1): el terminal de anulación es un PAR de estados de
`Reserva.Status`, no uno solo:**

- **`PendingOperatorRefund`** ("Esperando reembolso del operador",
  `ReservaStatusBadge.jsx:80-84`) mientras `OperatorRefundPending == true`.
- **`Cancelled`** ("Anulada", `ReservaStatusBadge.jsx:72-76`) cuando el operador
  ya saldó todo (`OperatorRefundPending == false`).

Ambos se materializan en `Reserva.Status` existente (reusa el badge, el candado
`LOCKED_STATUSES` en `ReservaStatusBadge.jsx:100`, y el circuito de plata de
anulación). **Se reconcilia la v1: el terminal materializado NO es un único
`Status`; es el par** — y `IsVoided` cubre a los dos (§1.1).

Por qué reusar `Status` y no una columna operativa nueva: el badge, el candado,
los gates y el circuito de plata **ya reaccionan a `Status`**
(`moneyStatus.js`, `DeriveCancelledMoneyContext` en `ReservaService.cs:2482`).
Materializar el eje operativo en columna paralela duplicaría todos esos
consumidores y crearía dos verdades (regla 8). Verificado por el review:
`Status=Cancelled` ya se alcanza desde `{InManagement, Confirmed}` por la matriz
manual (`ReservaStatusTransitions.cs:53-54`) — no se inventa acople.

**La transición NO emite NC** (regla 3, INV-048-02): `ReservaStatusTransitioner.ApplyAsync`
(`ReservaStatusTransitioner.cs:52-93`) solo escribe log (`:75-87`), setea `Status`
(`:90`) y limpia marcas (`:92`). No toca comprobantes ni plata.

#### B1 — El problema de las N BC y su remediación

Hoy la cabecera entra a `PendingOperatorRefund` **solo en el flujo de anulación
TOTAL** (`BookingCancellationService.cs:2558`), donde hay **UNA BC total** que
gobierna. El cierre `PendingOperatorRefund → Cancelled` lo disparan callbacks
por-BC, guardados por `Reserva.Status == PendingOperatorRefund`, que miran **las
líneas de ESA BC**:

- `CloseReservaIfOperatorRefundComplete` (`:3779` guard; `:3790-3811` cuenta solo
  `line.BookingCancellationId == bc.Id`; `:3830` cierra a `Cancelled`).
- `OnAllCreditConsumedAsync` (`:4656` guard `ClientCreditApplied`; `:4669` cierra
  a `Cancelled`).

Pero el escenario que el motor viene a cubrir es el **servicio por servicio**:
cada `CancelServiceAsync` crea su **propia BC** (`:482` recalcula). Al caer el
último servicio hay **N BC por servicio, ninguna "total"**. Con las callbacks
actuales, la **primera** BC cuyo refund se salda cerraría la cabecera a
`Cancelled` **aunque otras sigan con refund pendiente** → cierre prematuro,
refunds pendientes invisibles a nivel cabecera.

**Remediación (dentro de T1):**

1. **Helper de dominio puro `ReservaTerminalDerivation`** (testeable con 2+ BC):
   dada la reserva + todas sus BC/líneas, calcula
   `(OperationalAxis, OperatorRefundPending)` a nivel RESERVA. `OperatorRefundPending`
   = `any` línea de `any` BC con `RefundCap > 0 && RefundStatus != Settled`.
2. **El motor** usa el helper para elegir `PendingOperatorRefund` vs `Cancelled`
   al auto-anular.
3. **Los callbacks de cierre `:3779` y `:4656`** cambian su decisión de cierre de
   **por-BC** a **nivel RESERVA**: cierran a `Cancelled` solo cuando **todas** las
   líneas con `RefundCap > 0` de **todas** las BC de la reserva están `Settled`
   (reusan el mismo helper). Así el último refund que salda la reserva entera es
   el que cierra; los anteriores no.
4. Verificado que la **asignación de reembolso NO pasa por el chokepoint de
   plata**: `OperatorRefundService.AllocateAsync` muta `line.RefundStatus` en
   memoria (`:500`, `DistributeReceivedRefundToOperatorLines`) y notifica al BC
   (`:512`), pero **no llama a `ReservaMoneyPersister`** (el saldo del cliente no
   cambia). Por eso el cierre `PendingOperatorRefund → Cancelled` **debe** seguir
   viviendo en los callbacks (ahora a nivel reserva), NO puede depender de que el
   motor corra en el persister. Esta es la razón concreta por la que B1 toca los
   callbacks y no solo el motor.

### 1.3 Materialización de los ejes secundarios (cobro / facturación)

El eje operativo ya queda materializado en `Status` (el par). Los **ejes
secundarios** hoy se calculan en cada lectura, y baratos:

- Cobro en el listado: sale de la tabla hija ya materializada
  `ReservaMoneyByCurrency` (`ReservaService.cs:2311-2340`).
- Facturación en el listado: query agrupada por página, no N+1
  (`FillInvoicingStatusForListAsync`, `ReservaService.cs:2357-2396`).

Por eso **la materialización de estos ejes es HARDENING (Tanda 5), no bloqueante**
de los arreglos de las mentiras. Cuando se materialicen, columnas de proyección en
la cabecera con **único escritor = la derivación pura**, y las lecturas leen la
columna. Habilita filtrar/ordenar listados por "Anulada"/"Facturada y devuelta"
sin recomputar (estilo SAP). Columnas propuestas (en `Reserva`, tras
`LastRegressionAt`, `Reserva.cs:319`):

```
Reserva.DerivedCollectionStatus   string(20)  null  // proyección del CollectionAxis
Reserva.DerivedInvoicingStatus    string(20)  null  // proyección del InvoicingAxis
```

**T5 debe respetar B3:** si estas columnas pasan a ser fuente de lectura, la
proyección de "anulada"/`CircuitoAnulacion` debe cubrir el **par**
`{Cancelled, PendingOperatorRefund}`, o materializaría la mentira de B3 de forma
persistente. (El eje operativo NO agrega columna: es `Status`.)

### 1.4 Migración EF de las columnas nuevas (bug 42701)

- Columnas de 1.3 **solo por migración EF**, en
  `src/TravelApi.Infrastructure/Persistence/Migrations/App/`, naming del repo
  (`Adr048_M1_AddDerivedStatusColumnsToReserva`, patrón `Adr0xx_Mx_...`
  verificado).
- **Regla 42701:** el bootstrapper SQL de arranque NO crea estas columnas. Las
  migraciones corren con `db.Database.MigrateAsync()` (`Program.cs:770`). Un
  `ADD COLUMN` duplicado en SQL de arranque tira `42701 column already exists`.
  Verificado (review) que los bootstraps de `Program.cs:732/744/756` son de datos
  (finance/refresh/BNA), no de schema de `Reserva`: no se tocan.
- Las columnas nacen `null` (compatibles hacia atrás con el binario viejo, que
  recalcula en lectura). La reparación única (§3) las llena.

---

## 2. El único escritor y su enganche atómico

### 2.1 Quién escribe

**El único escritor del estado derivado es la lógica del motor existente**
(`ReservaAutoStateService`), pero partida en dos responsabilidades (Opción C del
review):

1. **Derivación + materialización pura** — un **método estático puro** (estilo
   `ReservaStatusTransitioner`, **sin `SaveChanges`**) que: setea el `Status` al
   terminal del par vía `ReservaStatusTransitioner` (rastro automático, actor
   "sistema", `ReservaAutoStateService.cs:40-41`), y en T5 setea las columnas
   derivadas. Idempotente.
2. **La campana** (`NotifyNeedsReviewAsync`, `ReservaAutoStateService.cs:311-362`)
   — queda como side-effect del path de mutación de servicio, NO se dispara desde
   la plata pura (cobro/NC/ND) para no spamear.

### 2.2 El problema real: el motor hoy no corre en la mayoría de las mutaciones

Verificado: `EvaluateAndApplyAsync` se invoca **solo desde 2 lugares
productivos**: `ReservaService.UpdateBalanceAsync` (`:5005`) y el job nocturno
(`ReservaLifecycleAutomationService.cs:195`). El chokepoint REAL de la plata es
`ReservaMoneyPersister.PersistAsync` (`ReservaMoneyPersister.cs:39-79`,
`SaveChanges` en `:70`, ya dispara `CommissionAccrualPersister` en `:78`), llamado
por **más caminos que los que corren el motor**:

| Camino de mutación | Recálculo de plata | ¿Corre el motor hoy? | Archivo:línea |
|---|---|---|---|
| Alta/baja/edición de servicio | `UpdateBalanceAsync` → persister | **Sí** | `ReservaService.cs:4998,5005` |
| Cobro / edición / anulación de pago | `ReservaMoneyPersister.PersistAsync` directo | **No** | `PaymentService.cs:1394` |
| Cancelación por servicio y total | `ReservaMoneyPersister.PersistAsync` directo | **No** | `BookingCancellationService.cs:482, 822` |
| Emisión / reversa de NC/ND (AFIP) | `ReservaMoneyPersister.PersistAsync` directo | **No** | `AfipService.cs:2268` (desde `:1857`) |

**Ésta es la causa de la mentira #1:** los servicios de la 1046 se anularon por
`BookingCancellationService` (recalcula plata, **no** corre el motor), y ese flujo
solo mueve `Status` a terminal en el path de **reserva completa** (`:2558, 3830`),
no en el de **servicio individual** → header quedó `Confirmed`.

### 2.3 Enganche atómico (B2 — decisión comprometida, no "a confirmar")

**La derivación pura (§2.1 punto 1) se invoca DENTRO de
`ReservaMoneyPersister.PersistAsync`, ANTES de la línea `:70`**, de modo que el
cambio de `Status` (y en T5 las columnas derivadas) se flushee en la **MISMA
`SaveChanges`** que la plata. Esto da atomicidad **incluso sin transacción
explícita del caller**: o se commitea plata+estado juntos, o ninguno.

- Es viable porque: el transicionador NO hace `SaveChanges` y corre en la unidad
  de trabajo del caller (`ReservaStatusTransitioner.cs:21`); el persister y el
  motor comparten el mismo `AppDbContext` scoped; la raíz `Reserva` es la misma
  instancia trackeada.
- **Reentrada:** la derivación vía transicionador solo escribe `Status` + cleanup,
  **no recalcula plata** → no re-llama al persister. Verificado por el review que
  hoy no hay recursión; se mantiene esa invariante.
- **Includes (frontera):** el persister carga la reserva con Includes económicos
  (`ReservaMoneyPersister.cs:44-52`); la derivación necesita las 6 colecciones de
  servicios y las BC/líneas. Al plegarla en el persister hay que **ampliar los
  Includes** del persister (o cargar lo que falte en el método puro) para no
  depender de una colección ausente. **Detalle de implementación a cuidar.**

**Se elimina el fallback "la próxima mutación corrige"**: era corrección diferida,
contraria a la regla 9. La atomicidad es el corazón del ADR, no un "a confirmar".

**M1 — sin doble corrida:** bajo esta vía, `ReservaService.cs:5005` deja **solo la
campana** (aviso "confirmada con cambios"); la derivación ya corrió en el
persister (que `UpdateBalanceAsync` invoca vía `RecalculateMoneyAsync`,
`:4998,5123`). No se deriva dos veces.

### 2.4 Nota de arquitectura (dependencia persistencia → dominio)

`ReservaMoneyPersister` es `internal static` sin DI. La derivación pura es de
**dominio** (no necesita `ILogger` ni notificaciones), así que el persister puede
invocarla sin invertir dependencias hacia un servicio con side-effects. La campana
(que sí necesita `ILogger`/`Notifications`) queda afuera. Como `Domain` ya es
dependencia de `Infrastructure`, el helper estático de dominio se referencia
directo desde el persister; no hace falta DI.

---

## 3. Reparación única de datos existentes (migración EF, sin pasada nocturna)

Las reservas que hoy mienten (1046 y hermanas: `Confirmed` con todos los servicios
anulados, o con NC que dejan neto ~0) deben quedar correctas al deployar. Regla 9
**prohíbe** un reparador batch recurrente.

**Diseño (M2+M3): reparación por MIGRACIÓN EF, una sola vez.**

- **Marcador = `__EFMigrationsHistory`.** EF garantiza que una migración corre
  **exactamente una vez** y registra su ID al final, dentro de su propia
  transacción. Es un marcador más robusto que una `AppSetting` casera (evita el
  bug M2 de "marcador seteado antes de terminar → reparación a medias para
  siempre"). Existe precedente en el repo:
  `20260705015708_RepairLegacyAnnulledReservaServices.cs` (SQL idempotente + backup
  `_repair_*` + `Down` no-op forense).
- **Deploy stop-then-start (confirmado):** el migrador dedicado corre **antes** de
  levantar la app; no hay rolling ni instancia vieja sirviendo → **sin solape de
  escritura, sin choque xmin**. Esto elimina la preocupación de concurrencia de M2.
- **Qué hace:** para cada reserva en `{InManagement, Confirmed, Traveling}` (ver
  §6 M5) que cumpla "tuvo servicios y todos anulados", deriva el terminal con el
  **mismo helper de dominio** que usa el motor en vivo (`ReservaTerminalDerivation`)
  → `PendingOperatorRefund` o `Cancelled` según `OperatorRefundPending` a nivel
  reserva. En T5 además llena `DerivedCollectionStatus`/`DerivedInvoicingStatus`.
- **Cómo se ejecuta la lógica de dominio desde una migración:** hay dos patrones
  válidos y se elige en implementación: (a) la migración invoca, en su `Up`, un
  servicio de reparación de dominio (patrón "migración que llama código"), o
  (b) precomputar en C# el conjunto y emitir el `UPDATE ... SET Status = ...`
  idempotente con backup previo del `Status` en tabla `_repair_*` (patrón del
  precedente). **Recomiendo (a)** para no duplicar la regla del terminal; si el
  entorno lo complica, (b) con el criterio calcado.
- **Rastro:** cada transición queda en `ReservaStatusChangeLog` (verificado que ya
  captura el `Status` previo), actor "sistema". No hace falta backup extra del
  `Status`: el log lo cubre. La reparación usa el motor con el equivalente a
  `suppressNotifications` (no spamear avisos históricos;
  `ReservaAutoStateService.cs:59-63`).
- **Reversibilidad:** solo cambia `Status` de reservas "todo anulado" y setea
  columnas derivadas nuevas (null-tolerantes). El rollback del binario deja las
  columnas (compatibles). Reconstruir un `Status` previo: desde
  `ReservaStatusChangeLog`.
- **Operación:** iterar `{InManagement, Confirmed, Traveling}` suma latencia al
  migrador; **batch + log de progreso + conteo pre/post contra PROD** (lección
  2026-07-09 sobre SQL crudo). No es prod con miles de reservas hoy.

**Nota:** el job nocturno existente (`ReservaLifecycleAutomationService:195`)
incidentalmente aplicaría la derivación una vez deployado, pero **no es el
mecanismo de diseño**. No se extiende ni se elimina (fuera de alcance).
Actualizar el comentario obsoleto del motor `ReservaAutoStateService.cs:35` ("la
reconciliación nocturna cura...") al implementar, para no dejar doc que
contradiga la regla 9.

---

## 4. Auditoría del cambio de estado (regla 10)

**Reusar `ReservaStatusChangeLog`**, que ya escribe `ReservaStatusTransitioner.ApplyAsync`
(`ReservaStatusTransitioner.cs:75-87`): `ReservaId, FromStatus, ToStatus,
Direction, ByUserId, ByUserName, Reason, OccurredAt`. Actor del sistema:
`SystemActorUserId="system:auto-state"` (`ReservaAutoStateService.cs:40-41`), con
`Reason` en criollo (p. ej. "Todos los servicios anulados y el operador ya
reembolsó: reserva anulada (sistema)"). Cubre la regla 10 para el eje operativo
(incluida la transición `PendingOperatorRefund → Cancelled` que ya loguea así,
`:3830, 4669`).

**OQ-2 cerrada (con la recomendación):** auditar **solo la transición operativa**.
Los ejes secundarios (cobro/facturación) ya tienen rastro en sus flujos de plata/
comprobantes; loguear cada recomputo sería ruido.

---

## 5. Plan de TANDAS (deployables e independientes)

> Orden por dependencia. Sin estimaciones de tiempo (regla del dueño).
> **M4 (dependencias):** T1 antes que T4 (dependencia dura: T4 lee `isVoided`/
> `cancelledMoneyContext` con la semántica B3 que T1 fija). T2 y T3 son
> independientes entre sí y de T1. T5 opcional, después de T1-T4.
> Lo que toca pantallas (**T4**) pasa por el **gate UX** ANTES de implementar.

---

### T1 — Motor deriva el terminal (par, N BC) + enganche atómico + fix de callbacks + reparación única

**Alcance:** cerrar la mentira #1 y habilitar #3/#4 (al quedar el `Status` en el
par terminal, el chip Pago entra a la rama de anulación y el gate de operador
filtra por estado). **B1 hace que T1 toque también los callbacks de cierre — es
más código que "el motor deriva Anulada".**

**Archivos a tocar:**
- `TravelApi.Domain/Reservations/ReservaDerivedState.cs` (**nuevo**): función pura
  (§1.1), al menos `OperationalAxis` + `OperatorRefundPending`.
- `TravelApi.Domain/Reservations/ReservaTerminalDerivation.cs` (**nuevo** o parte
  del anterior): helper puro nivel-reserva N-BC (§1.2 B1), testeable con 2+ BC.
- `ReservaAutoStateService.cs`: en `Confirmed && !allResolved` (`:105-124`) e
  `InManagement` sin vivos, distinguir "tuvo servicios y todos anulados"
  (→ transicionar al terminal del par) de "algún vivo sin resolver" (→ marcar
  revisar, comportamiento actual). Reusa `HasAnyLiveService` (`:272-283`).
  Actualizar comentario obsoleto `:35`.
- `ReservaMoneyPersister.cs`: invocar la derivación pura antes de `:70` (§2.3);
  ampliar Includes si falta (servicios + BC/líneas).
- `ReservaService.cs:5005`: dejar solo la campana (M1).
- `BookingCancellationService.cs`: callbacks `:3779-3833` y `:4656-4672` pasan a
  decidir el cierre a **nivel reserva** (todas las líneas `RefundCap>0` de todas
  las BC `Settled`), reusando el helper.
- Migración EF de reparación única (§3).

**Invariantes nuevos:**
- **INV-048-01** — Reserva que **tuvo** servicios y los tiene **todos anulados** y
  está en `{InManagement, Confirmed}` (y `Traveling` solo en la reparación, §6) es
  llevada al terminal del par. Reserva que **nunca tuvo** servicios NO se anula.
- **INV-048-02** — La transición automática al terminal **no** crea comprobantes
  ni mueve plata: solo `Status` + rastro.
- **INV-048-03** — El motor/derivación corre después de **todo** recálculo de
  plata, **en la misma `SaveChanges`** (atómico). No hay camino de mutación de
  plata que deje el estado sin re-derivar ni en commit separado.
- **INV-048-04** — Toda transición automática queda en `ReservaStatusChangeLog`
  con actor "sistema" (regla 10).
- **INV-048-05** — La reparación corre **una sola vez** (marcador
  `__EFMigrationsHistory`); no existe proceso recurrente que derive estado.
- **INV-048-11 (B1)** — El terminal es `PendingOperatorRefund` sii existe alguna
  línea de alguna BC de la reserva con `RefundCap>0 && RefundStatus!=Settled`; si
  no, `Cancelled`. El cierre `PendingOperatorRefund→Cancelled` se decide a nivel
  RESERVA, nunca por-BC.

**Caminatas E2E (patrón del flujo PUT `taxConditionId` ya en CI):**
- **E2E-1 (caso base):** reserva con 2 servicios resueltos → `Confirmed`. Anular
  servicio 1 → sigue `Confirmed`. Anular servicio 2 (sin multa/refund) → header
  pasa solo a `Cancelled` (assert `Status` + fila `ReservaStatusChangeLog` actor
  sistema). **Assert que NO se emitió NC nueva** por la transición.
- **E2E-2 (blinda B1 — N BC + refund parcial):** reserva con 2 servicios de
  operadores que exigen reembolso; anular ambos (N=2 BC, cada una con `RefundCap>0`).
  Al caer el último servicio → header `PendingOperatorRefund` (no `Cancelled`).
  Registrar el refund de **una** BC → sigue `PendingOperatorRefund` (la otra sigue
  pendiente). Registrar el refund de la otra → recién ahí `Cancelled`. Assert que
  el cierre prematuro por-BC NO ocurre.
- **E2E-3 (blinda B2 — atomicidad):** simular fallo entre plata y estado en un
  caller sin transacción → assert de que el estado NO queda stale (o assert de que
  plata y estado comparten `SaveChanges`: un fallo forzado tras setear `Status`
  deja también la plata sin commitear).
- **Negativo:** reserva nueva sin servicios → NO queda anulada.

**Barrido de seeds de integración (CI Postgres) — cargar datos reales, jamás
relajar asserts.** Suites candidatas a romper (verificadas por el review que
ejercen estos flujos):
`Adr027ConfirmedWithChangesTests` (separar el subcaso "todos anulados" que ahora
va a terminal), `ReservaServiceCancellationTests`,
`ReservaServiceCancellationWithPaymentTests`, `LifecycleFixes20260624Tests`,
`Adr020LifecycleTests`, y `Tests/Cancellation/Integration/*`.

**Riesgos:**
- **R-T1-1 (B1):** cierre prematuro de cabecera con refunds de operador pendientes
  → reserva figura cerrada mientras el ERP espera plata del operador (descuadre
  silencioso). Mitiga E2E-2 + el fix de callbacks a nivel reserva.
- **R-T1-2:** efectos colaterales de escribir el par terminal (gates, circuito de
  plata). Es el mismo estado que hoy alcanza la anulación total → comportamiento
  ya probado en ese camino.
- **R-T1-3 (reparación):** toca `Status` productivo. Idempotente + stop-then-start
  (sin solape) + conteo pre/post PROD.

---

### T2 — Deuda al operador solo sobre servicios vivos / multas reales (regla 7)

**Alcance:** cerrar la mentira #4 también en **anulación parcial** (cabecera sigue
`Confirmed`, servicio anulado no reporta "operador impago").

**Archivos:** `SupplierService.cs` — en el loop de
`GetReservaSupplierPaymentStatusAsync` (`:3118-3129`) agregar `continue` para
servicios anulados (`ServiceResolutionRules.IsCancelled`), **salvo** que el
servicio tenga multa/ND real del operador. **Pre-requisito (no verificado, TODO de
implementación):** que `BuildReservaServiceRowsAsync` traiga el flag de
cancelación por servicio; si no, agregarlo.

**Invariantes:** **INV-048-06** — Un servicio anulado no reporta estado de pago al
operador, salvo multa/ND del operador imputada a ese servicio.

**Caminata E2E:** reserva `Confirmed` con servicio A vivo (impago) y B anulado sin
multa → reporta solo A. Variante: B anulado **con** multa → B aparece solo por el
monto de la multa.

**Riesgos:** tipificación de "multa/ND real" — cruzar con
`travel-agency-accountant-argentina`.

---

### T3 — Facturación distingue "Facturada y devuelta" de "Sin facturar" (regla 5)

**Alcance:** cerrar la mentira #2 (NC emitida y el chip dice "Sin facturar").

**Archivos:**
- `ReservaInvoicingStatus.cs`: agregar valor `FullyReturned` ("Facturada y
  devuelta") y tercera entrada al `Derive` (`brutoEmitido` / `huboComprobante`):
  `neto ≈ 0 && bruto > 0` → `FullyReturned`; `neto ≈ 0 && bruto ≈ 0` →
  `NotInvoiced` (hoy `:59-60`).
- `ReservaService.cs`: pasar el bruto emitido en detalle (`:2542`) y listado
  (`FillInvoicingStatusForListAsync`, `:2357-2396`, que ya suma facturas con CAE).
- DTO + label front (chip Factura) — gate UX si el texto es nuevo.

**Invariantes:** **INV-048-07** — Reserva con comprobante emitido (bruto CAE > 0)
y neto ~0 por NC muestra "Facturada y devuelta", nunca "Sin facturar".

**Caminata E2E:** facturar → "Facturada total"; NC total → "Facturada y devuelta"
(assert que NO vuelve a "Sin facturar"). Sin comprobantes → "Sin facturar".

**Riesgos:** mantener la limitación escalar v1 (`ReservaInvoicingStatus.cs:24-28`);
no abrir por-moneda acá.

---

### T4 — Front: superficies leen la misma fuente derivada + importes tachados (reglas 4, 6, 8; respeta B3)

**Alcance:** cerrar la presentación de la mentira #3 e importes históricos por
servicio anulado. **Gate UX primero. Depende de T1 (M4).**

**Archivos:**
- `moneyStatus.js`: eliminar el candado hardcodeado
  `ESTADOS_ANULADOS = {Cancelled, PendingOperatorRefund}` (`:32`) e
  `isReservaAnulada` (`:35-37`); leer el booleano `isVoided` del backend, que
  **cubre el par** `{Cancelled, PendingOperatorRefund}` (B3). **Crítico:** si se
  proyectara solo de `Cancelled`, toda reserva `PendingOperatorRefund` perdería el
  circuito de anulada → nueva mentira. El backend expone `isVoided` con la
  semántica de §1.1.
- `ServiceList.jsx`: tachar/atenuar importes de servicio anulado (`netCost` `:1409`,
  `salePrice` `:1420`) y suprimir sus avisos de cobro/pago (regla 6).
- `ReservaHeader.jsx`/`ReservaStatusBadge.jsx`: el badge ya lee `Status` (`:258`);
  confirmar que ninguna otra superficie lea campos sueltos (regla 8).

**Invariantes:**
- **INV-048-08** — Ninguna superficie del front decide "anulada / con deuda /
  facturada" con un string de `Status` hardcodeado ni recomparando `balance>0`:
  todas pasan por la fuente derivada del backend, y "anulada" cubre el par.
- **INV-048-09** — Importes de servicio anulado tachados/históricos; sin avisos de
  cobro al cliente ni de pago al operador (salvo multa/ND propia).

**Caminata E2E (front/QA):** ficha de reserva `PendingOperatorRefund` → chip Pago
muestra el circuito de anulación (no "Debe"/"Sin movimientos"); ídem `Cancelled`;
servicio anulado con importes tachados y sin avisos. **Blinda B3.**

**Riesgos:** gate data-exposure (no filtrar enums/IDs/strings internos en textos
nuevos). Consistencia con `docs/ux/guia-ux-gaston.md`.

---

### T5 — Materializar ejes secundarios en la cabecera (hardening estilo SAP; respeta B3)

**Alcance:** persistir `DerivedCollectionStatus`/`DerivedInvoicingStatus` escritas
por la derivación pura, para filtrar/ordenar listados sin recomputar. **Opcional:
las mentiras ya están cerradas por T1-T4.** Confirmado por el review que puede
quedar afuera sin dejar mentiras vivas.

**Archivos:** migración EF (columnas §1.3, respetando 42701), derivación pura
(escribe), `ReservaService.cs` (lecturas de listado/detalle leen la columna),
reparación única (llena columnas).

**Invariantes:**
- **INV-048-10** — `DerivedCollectionStatus`/`DerivedInvoicingStatus` solo los
  escribe la derivación pura; ninguna lectura los recalcula por su cuenta. La
  proyección de "anulada"/`CircuitoAnulacion` cubre el par (B3).

**Caminata E2E:** filtrar el listado por "Anulada" y "Facturada y devuelta"
devuelve exactamente las reservas correctas sin recomputar.

**Riesgos:** doble fuente transitoria (columna vs recomputo) — migrar lecturas "de
una". Si materializa la anulada sin cubrir el par → mentira B3 persistente.

---

## 6. Qué NO cambia (anti-sobre-alcance)

- **La mecánica fiscal de NC/ND ya deployada NO se toca.** Emisión, numeración,
  CAE, conversión de multa, circuito ARCA — igual. Esto es **capa de coherencia de
  estados y lectura**, no plata ni fiscal.
- **La transición al terminal NO emite comprobantes** (INV-048-02).
- **El cálculo de plata (`ReservaMoneyCalculator`/`ReservaMoneyPersister`) NO
  cambia su matemática** (`ReservaMoneyPersister.cs:29-31`): sigue siendo la fuente
  única del saldo; se le engancha la derivación en la misma `SaveChanges`.
- **La anulación parcial NO agrega cartel de cabecera intermedio**: sigue
  "Confirmada"; lo anulado tachado por línea (pregunta 3, Gaston).
- **No hay pasada nocturna nueva** (regla 9). El job nocturno existente no se
  extiende ni se elimina.
- **No hay feature flags** (regla del dueño).
- **El vocabulario ya fijado no cambia** ("Anulada"/"Esperando reembolso" en el
  badge, `ReservaStatusBadge.jsx:72-84`).
- **M5 — El motor EN VIVO no cubre `Traveling`.** `isEngineState` es
  `{InManagement, Confirmed}` (`ReservaAutoStateService.cs:85-88`). **Decisión y
  justificación:** ADR-036 hace `Traveling` **inmutable** (solo lectura total): no
  se pueden anular servicios estando en viaje, así que "Traveling con todo
  anulado" **no es alcanzable por el camino en vivo** hacia adelante → no hace
  falta (ni conviene, por respetar la inmutabilidad de edición) que el motor toque
  `Traveling`. **PERO** la mentira sí podría existir en **datos legacy**; por eso
  **la reparación única SÍ barre `Traveling`** (§3) para el terminal derivado —
  corregir una etiqueta mentirosa una sola vez no es "editar un viaje", es sanear
  un dato, y queda auditado. Si al implementar se encuentra que ADR-036 prohíbe
  incluso este saneamiento puntual, se deja `Traveling` afuera también de la
  reparación y se anota; recomendación actual: incluirlo solo en la reparación.

---

## Preguntas abiertas — estado

- **OQ-1 — RESUELTA (Gaston):** terminal = par `PendingOperatorRefund` (operador
  debe) → `Cancelled` (saldó). Incorporado en §1.2 + B1.
- **OQ-2 — CERRADA (con la recomendación):** auditar solo la transición operativa
  (§4).
- **OQ-3 (deploy) — RESUELTA (coordinador):** stop-then-start con migrador
  dedicado antes de la app; sin rolling → sin concurrencia en la reparación (§3).
- **Sin preguntas abiertas bloqueantes restantes.** Quedan TODOs de
  implementación, no decisiones: (a) `BuildReservaServiceRowsAsync` trae el flag de
  cancelación (pre-requisito de T2); (b) ampliar Includes del persister al plegar
  la derivación (§2.3).

---

## Auto-revisión crítica

1. **B1 (N BC + callbacks):** el riesgo más peligroso y menos declarado en la v1.
   Ahora está: helper de dominio a nivel reserva, callbacks `:3779/:4656` a nivel
   reserva, E2E-2 que lo blinda, y la verificación de que el refund NO pasa por el
   persister (por eso los callbacks siguen siendo el mecanismo de cierre).
2. **B2 (atomicidad):** comprometido, no "a confirmar". Derivación pura dentro del
   persister antes de `:70` → una `SaveChanges`. Fallback "próxima mutación
   corrige" eliminado. Riesgo residual: Includes ausentes al plegar (§2.3), es
   detalle de implementación, no de correctitud.
3. **B3 (`IsVoided`):** definido como el par `{Cancelled, PendingOperatorRefund}`,
   con el conjunto en un solo lugar del dominio; T4/T5 obligadas a respetarlo.
4. **Acople persistencia→dominio:** el persister invoca dominio puro (no un
   servicio con side-effects); reentrada verificada como imposible (transicionador
   no recalcula plata).
5. **Sobre/sub-diseño:** eje operativo sin columna nueva (es `Status`);
   materialización de ejes secundarios diferida a T5 (opcional). T1 quedó más
   grande de lo que sugiere su título por B1 — declarado explícitamente.
6. **No verificado / honestidad:** (a) cuántos call-sites del persister abren
   transacción explícita — con la vía atómica **ya no importa** para la
   correctitud (comparten `SaveChanges`); (b) interno de
   `BuildReservaServiceRowsAsync` (TODO de T2); (c) si el vigía de coherencia
   existente ya detecta "todo anulado pero Status no-terminal" como red de
   seguridad — a evaluar, no bloqueante.
7. **No se corrió nada contra la app real ni el CI:** esto es diseño sobre código
   estático. Las caminatas E2E de §5 **hay que crearlas** (imitando la del PUT
   `taxConditionId`). No afirmo que nada "ande": afirmo qué construir y en qué
   orden.
