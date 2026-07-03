# ADR-042 — Anular una reserva con múltiples facturas (multimoneda): una NC por factura, todo-o-nada a nivel reserva

## 1. Estado

**APROBADO CON CONDICIONES — condiciones cerradas (revisión 3, pasada final de diseño).** `software-architect-reviewer` aprobó rev.2 (B1/B2/B3-regla/B4/M1/M2/M3 verificados como resueltos) y pidió cerrar C1 (plata) + C2 (operativa) + 3 menores. Todos cerrados en rev.3. Listo para arrancar implementación. Quedan solo las **decisiones de contador** de §9.2 (no bloquean el diseño; se ajustan al construir).

### Registro de cambios rev.3 (cierre de condiciones del re-review)

- **C1 (plata, sitio de minteo):** §3.3.2 corregido con evidencia — el `ClientCreditEntry` en el camino de ADR-042 (con NC) lo crea `OperatorRefundService.CreateEntryAsync:538` con `currency = refund.Currency`, NO el converter (`ApplyAnnulWithPaymentsToCreditAsync` es el camino SIN CAE) ni `OnAllocationRecordedAsync` (solo transiciona). Enchufe de `ResolveCreditAllocationByCurrency` = ese sitio. Reconciliación C1(c): reembolso vs obligación **no** son consistentes por construcción (el código valida refund contra la línea del operador, no contra la obligación) → regla concreta: minteo 1:1 con lo devuelto; divergencia → revisión manual (nunca FX inventado).
- **C2 (operativa, lock):** §3.5.1.b documenta el patrón completo del `FOR UPDATE` (nuevo en el repo): un lock por `Id`, orden único, sin I/O dentro del lock, `lock_timeout` acotado (~5s), job falla limpio ante timeout → Hangfire reintenta re-leyendo fresco, liberado al commit.
- **Menores:** (1) §3.5.1.b — la ND (`TryEmit…`) queda post-commit, nunca sosteniendo el lock a través de un enqueue. (2) §3.3.2 — discriminador de moneda = `Payment.ImputedCurrency` (ADR-021), `LinkedInvoiceId` es informativo. (3) §9.2.4 marcado RESUELTO (el flujo no auto-imputa; a-cuenta = moneda de pago, ya es lo que hace el código).

### Registro de cambios rev.2 (respuesta al review)

- **B1 (carrera en callbacks):** §3.5 adopta lock pesimista del padre (`SELECT … FOR UPDATE`) en la transacción del callback; la completitud se cuenta con lectura fresca de BD, no del ChangeTracker. La garantía NO depende de la idempotencia por-hija ni del `WorkerCount`.
- **B2 (`ForceArcaConfirmationAsync`):** §3.5.3 lo reescribe para operar **por-hija** + re-evaluar completitud con el mismo lock (opción b del reviewer).
- **B3 (atribución del crédito por moneda):** §9 reclasifica la moneda como **decidida** (2026-07-01); §3.3.2 especifica la regla de reparto y el punto único enchufable.
- **B4 (guard INV-081 con cero hijas):** §3.4 pliega el fallback legacy dentro del guard.
- **M1/M2/M3 y menores:** §3.6, §3.3, §3.7, §7, §8 (ver cada sección).

## 2. Contexto

### 2.1 Lo que ya está decidido (no se reabre)

- Una reserva puede tener 2+ facturas legítimas con CAE (ej. una USD + una ARS).
- Al anular la reserva se emite **una NC por cada factura activa con CAE**, cada NC en la moneda de su factura, asociada a su comprobante (`CbtesAsoc`), heredando el TC congelado de la factura original.
- **Todo-o-nada a nivel estado de reserva**: la reserva no queda "anulada" hasta que TODAS las NC tengan CAE. Si una sale y otra falla, la que salió no se revierte (no existe rollback de CAE); la reserva queda en un estado de revisión con aviso y debe poder reintentarse (solo las NC faltantes, idempotente).
- La ND por multa del operador es **una por cancelación** (no se multiplica por factura).
- Sin feature flags nuevos.

### 2.2 Estado real del código (verificado, no asumido)

Evidencia inspeccionada:

- `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs`
- `src/TravelApi.Infrastructure/Services/InvoiceService.cs`
- `src/TravelApi.Infrastructure/Services/AfipService.cs`
- `src/TravelApi.Domain/Entities/BookingCancellation.cs`

Hechos:

1. **`BookingCancellation` es single-factura por diseño.** Tiene `OriginatingInvoiceId` (uno), `CreditNoteInvoiceId` (uno, nullable) y `SupplierId` denormalizado. El aggregate es 1:1 con `Reserva` (INV-081). Ver `BookingCancellation.cs:46-51`.

2. **INV-100 bloquea el caso multi-factura A PROPÓSITO hoy.** En `DraftAsync` (`BookingCancellationService.cs:183-204`) se listan las facturas de venta vivas (excluyendo NC/ND y filas fantasma sin CAE) y si hay más de una con `OnePerReservaInvoicePolicy` on → rechaza INV-100. El comentario de las líneas 178-181 dice explícitamente que el caso USD+ARS legítimo "se hará aparte" — esta ADR es ese "aparte".

3. **La NC se emite por-factura y ya hereda moneda+TC del origen.** `ConfirmAsync` (`BookingCancellationService.cs:1749-1756`), tras commitear el core, llama **una** vez a `InvoiceService.EnqueueAnnulmentAsync(bc.OriginatingInvoiceId, …)`. Esa cola dispara `ProcessAnnulmentJob` (`InvoiceService.cs:1194+`), que arma la NC heredando `MonId`/`MonCotiz` de la factura origen (`InvoiceService.cs:1373-1379`), asocia `CbtesAsoc` y llama a `ProcessInvoiceJob` para POSTear a ARCA (`InvoiceService.cs:1501-1504`).

4. **Los callbacks de ARCA están acoplados a UNA factura originante.** `OnArcaSucceededAsync(originatingInvoiceId, creditNoteInvoiceId)` (`BookingCancellationService.cs:3191`) y `OnArcaFailedAsync(originatingInvoiceId, …)` (`:5014`) buscan el BC con `WHERE OriginatingInvoiceId == X AND Status == AwaitingFiscalConfirmation`. **En el éxito, transicionan el BC a `AwaitingOperatorRefund` en el PRIMER callback** y setean `CreditNoteInvoiceId`. Este es el acople central que rompe el caso multi-NC: con N facturas llegan N callbacks y hoy el primero cerraría la anulación aunque falten NCs.

5. **Anti-doble-CAE por comprobante ya existe.** `ProcessInvoiceJob` usa `ArcaIdempotencyKeys` + stale-key recovery (`AfipService.cs:1103-1187`). Como cada NC pasa por `CreatePendingInvoice` + `ProcessInvoiceJob`, **cada NC hereda idempotencia a nivel ARCA gratis**. Además `EnqueueAnnulmentAsync` rechaza re-anular una factura ya `Pending`/`Succeeded` y **permite reintento desde `Failed`** (`InvoiceService.cs:1038-1044`).

6. **El patrón de reintento todo-o-nada del commit `46d2b65` está en `ConfirmPenaltyAsync`/`RetryDebitNoteEmissionAsync`** (`BookingCancellationService.cs:3428`, `:3693`): auditoría *staged* (no commitea) + reconciler + **una** `SaveChangesAsync` + `TryEmit…` envuelto en catch que ante fallo deja el comprobante en revisión manual (nunca 500) + endpoint de retry que **re-vincula una ND huérfana** (anti-doble-emisión, `:3746-3768`) o re-emite. Este es el molde a copiar para el retry de NCs.

7. **CORRECCIÓN A LA PREMISA #5 del pedido — `CanMisMonExt` YA SE EMITE.** El pedido dice "AfipService.cs HOY NO emite el nodo `CanMisMonExt`". Eso es **incorrecto según el código**: existe `BuildCanMisMonExtFragment(monId)` (`AfipService.cs:1057-1068`) y se emite en el envelope `FECAESolicitar` en `AfipService.cs:1392`, justo después de `MonId`/`MonCotiz`. Hoy devuelve string vacío para pesos y **`"N"` fijo para moneda extranjera**. Los gaps **reales** no son "falta el nodo", sino:
   - (a) está **hardcodeado `"N"`**, nunca deriva `"S"` de la moneda de cobro real;
   - (b) el **orden del nodo** en el envelope contra el XSD del WSFEv1 v4 **no está verificado** (el propio comentario `:1345-1348` lo marca como pendiente de homologación);
   - (c) **no se homologó** un comprobante USD (ni NC/ND USD) contra el ARCA de prueba.
   Esta ADR trata esos tres gaps, no "agregar el nodo".

### 2.3 Problema

El modelo single-factura del `BookingCancellation` no representa N NCs, y los callbacks cierran la anulación en el primer éxito. Hay que:

1. Modelar N (factura → NC) por cancelación.
2. Mover la decisión de "reserva anulada" de "primer callback exitoso" a "todos los callbacks resueltos y todos exitosos".
3. Manejar el estado intermedio "parcialmente emitida" (algunas con CAE vivo, otras pendientes/falladas) sin liberar jamás algo con NC viva, y permitir reintento solo de las faltantes.
4. Frenar TODO a revisión si alguna factura extranjera tiene TC sospechoso (TC=1) **antes** de emitir nada.
5. No romper la ND-única-por-cancelación.
6. Cerrar los tres gaps reales de `CanMisMonExt`.

## 3. Decisión

### 3.1 Modelo de datos — tabla hija `BookingCancellationCreditNote`

Se introduce una tabla hija del aggregate `BookingCancellation`, **una fila por (factura de venta a anular → su NC)**:

```
BookingCancellationCreditNote
  Id                       int  PK
  PublicId                 Guid
  BookingCancellationId    int  FK -> BookingCancellations (cascade con el padre)
  OriginatingInvoiceId     int  FK -> Invoices  (la factura de venta que se anula)
  CreditNoteInvoiceId      int? FK -> Invoices  (la NC; null hasta que se crea/CAE)
  ArcaCurrency             string(3)   -- MonId de la factura origen (denormalizado, observabilidad)
  Status                   int  -- enum: Pending / Succeeded / Failed
  ArcaErrorMessage         string(1000)?
  CreatedAt                datetime
  UNIQUE (BookingCancellationId, OriginatingInvoiceId)
```

**Se conservan** `BookingCancellation.OriginatingInvoiceId` y `BookingCancellation.CreditNoteInvoiceId` como puntero a la factura/NC **PRINCIPAL** (la más reciente por `CreatedAt`, criterio ya usado en `DraftAsync:189`). Motivos de compatibilidad hacia atrás (todos verificados como dependientes del puntero singular):

- La ND (`TryEmitCancellationDebitNoteAsync`, gating de `ConfirmPenaltyAsync`) asocia su `CbtesAsoc` a `bc.OriginatingInvoice` y deriva `PenaltyCurrencyAtEvent` del `MonId` de esa factura → **la ND sigue apuntando a la principal, sin cambios** (premisa #6 intacta).
- El gate de la ND exige `CreditNoteInvoiceId != null` (`:3337`, `:3477`) → se cumple seteando el puntero principal cuando la NC principal obtiene CAE.
- El guard de liberación de INV-081 usa `CreditNoteInvoiceId is null` (ver §3.4).

**Regla dura:** para el caso mono-factura (el 99% histórico) el comportamiento es **byte-equivalente**: una sola fila hija que espeja al puntero principal. El backfill (§7) crea esa fila para todo BC existente.

**Alternativa descartada:** múltiples `BookingCancellation` por reserva (uno por factura). Rechazada porque rompe INV-081, el 1:1 Reserva↔BC, la ND-única, el snapshot fiscal y toda la UI/DTO. Ver §6.

**Alternativa descartada:** sin tabla hija, derivar todo de `Invoice.AnnulmentStatus` filtrando por `ReservaId`. Rechazada: no distingue las facturas "de esta cancelación" de anulaciones históricas, no tiene dónde colgar el error/observabilidad por-NC atado al BC, y deja la completitud como una query frágil. Para un flujo fiscal, el aggregate explícito es más seguro y testeable.

**Migración:** `Adr042_M1_AddBookingCancellationCreditNotes` (estilo del proyecto en `src/TravelApi.Infrastructure/Persistence/Migrations/App/`). Crea la tabla + índice único + backfill (§7). Sin `Down` destructivo (solo `DropTable`).

### 3.2 Máquina de estados de la cancelación multi-NC

Se **reusa** `BookingCancellationStatus` sin agregar estados nuevos. La novedad es que la **decisión de transición se corre del "primer callback" al "todos los callbacks resueltos"**, evaluando las filas hijas:

```
Drafted
  -> (Confirm; pre-flight OK; commit core + N filas hijas Pending)
AwaitingFiscalConfirmation            (>=1 hija Pending)
  -- llega callback ARCA por cada NC; cada uno actualiza SU fila hija --
  |
  |  (quedan hijas Pending)     -> permanece en AwaitingFiscalConfirmation
  |  (0 Pending, 0 Failed)      -> AwaitingOperatorRefund     [ANULACIÓN COMPLETA]
  |  (0 Pending, >=1 Failed)    -> ArcaRejected               [REVISIÓN + RETRY]
```

- `AwaitingFiscalConfirmation` = "faltan NCs por confirmar". Mientras haya al menos una hija `Pending`, el BC no se mueve.
- `AwaitingOperatorRefund` (estado de éxito de siempre) = **todas** las hijas `Succeeded`. Recién acá se setea `CreditNoteInvoiceId` = NC de la factura principal, se transiciona la reserva a `PendingOperatorRefund`, y se dispara el gate de la ND (una sola vez, sobre la principal).
- `ArcaRejected` = todas resueltas y **al menos una falló**. Es el "estado de revisión con aviso" que pide la premisa #3. **Reusa** la notificación y el mensaje existentes de `OnArcaFailedAsync` (`:5037-5065`). La reserva queda bloqueada (no vuelve a `PendingOperatorRefund`) y ofrece **retry de NCs faltantes**.

**Punto clave de no-regresión:** en el caso mono-factura hay una sola hija, así que "0 Pending, 0 Failed" tras el único callback = comportamiento idéntico al de hoy (transiciona en ese primer y único callback).

**Ejes independientes (aclaración, menor del review):** las `BookingCancellationLine` (ADR-025, una por servicio/operador) y las filas hijas `BookingCancellationCreditNote` (una por factura) son **dimensiones ortogonales**. Las Lines modelan "a quién le pido el reembolso"; las hijas modelan "qué comprobante fiscal anulo". Un mismo BC puede tener 3 Lines (3 operadores) y 2 hijas (2 facturas USD+ARS) sin correlación 1:1 entre ambas colecciones.

**`CreditNoteInvoiceId` en `ArcaRejected` parcial (verificado):** en el estado parcial, `BC.CreditNoteInvoiceId` (puntero principal) **queda `null`** mientras la NC principal no tenga CAE. Esto es lo que hoy **frena la ND** — su gate exige `CreditNoteInvoiceId != null` (verificado en `:3477` y `:3737`). Es el comportamiento deseado: la ND no se dispara hasta que la anulación esté **completa** (todas las NCs con CAE → §3.5 setea el puntero principal recién en `AwaitingOperatorRefund`).

### 3.3 CanMisMonExt (los tres gaps reales)

#### 3.3.1 Derivación S/N — persistir en la factura, la NC lo espeja (M2.e del review)

**Corrección al diseño rev.1:** rev.1 proponía recomputar `CanMisMonExt` en tiempo de emisión desde `Payment.Currency`. El reviewer lo rechaza con razón: `CanMisMonExt` es un **dato fiscal del comprobante al momento de emisión**, no algo a recalcular después (los pagos pueden cambiar entre la factura y la NC). Diseño corregido:

- Al emitir la **factura**, se **persiste** el valor `CanMisMonExt` decidido en una columna nueva `Invoice.CanMisMonExt` (`char(1)?`: `'S'`/`'N'`/`null` para PES). Se congela con el comprobante.
- La **NC/ND ESPEJA** ese valor del comprobante original (igual que ya hereda `MonId`/`MonCotiz`), no lo recalcula. Así el ajuste cierra con la misma clasificación fiscal que la factura.
- La derivación del valor al emitir la factura queda en una función pura:
  ```
  ResolveCanMisMonExt(string comprobanteMonId)   // el discriminador es lo que la FACTURA DECLARA
    - comprobante en PES        -> null (no se emite el nodo; byte-idéntico a hoy)
    - comprobante en divisa     -> "N"   (criterio firme; ver abajo)
  ```
  Para esta agencia (y hoy TODAS las facturas en divisa) → `"N"`, **exactamente lo que emite hoy**. La NC/ND espeja ese `"N"`.
- **Fundamento fiscal confirmado (RG 5616/2024):** si el comprobante declara `"S"`, ARCA **fuerza** `MonCotiz` = TC BNA vendedor divisa del **día hábil anterior a la emisión de la NC** → **incompatible** con heredar el `MonCotiz` del comprobante original (§2.2.3). Por eso espejar `"N"` es **lo único coherente** con la herencia de TC ya decidida. Que el cliente haya pagado en dólar billete **NO** cambia retroactivamente el `CanMisMonExt`: el discriminador es lo que la **factura declaró** al emitirse, no cómo se cobró.
- **Camino `"S"` = futuro, pendiente de firma matriculada.** Habilitar `"S"` implica recalcular `MonCotiz` por BNA en la NC (no heredar), lo que rompe el modelo de herencia actual. No se construye ahora.
- **RIESGO a documentar (§8):** una NC `"S"` sobre un original `"N"` (o viceversa) **rompe el par en el libro IVA** y puede **rebotar el CAE**. Por eso el espejado es estricto: la NC toma el `CanMisMonExt` congelado del original, nunca lo redecide.
- **Estado:** operativamente firme para construir; formalización pendiente de contador matriculado (no hay norma que legisle la herencia de `CanMisMonExt`; es criterio prudente y defendible). Citas: RG 5616/2024; decisión 2026-07-01.

#### 3.3.2 Regla de reparto del crédito del cliente por moneda (B3 — DECIDIDA)

**Confirmada por el contable (2026-07-01).** El saldo a favor sale en la **moneda de la obligación que el pago canceló**, por el monto **efectivamente cobrado** — **nunca por lo facturado**:

- **Pago imputado a factura USD** → crédito **USD** por los USD que la imputación registrada dice que ese pago canceló (al **TC del día del pago**, valor que la imputación **ya dejó registrado**; **no se recalcula** — la diferencia de cambio de la cobranza ya está realizada e independiente).
- **Pago imputado a factura ARS** → crédito **ARS** nominal.
- **Pagos parciales** → crédito = **solo lo cobrado** de cada factura. **Punto duro contable:** el **tope** del saldo es lo cobrado-imputado, **NO** el monto de la NC. La NC revierte lo **facturado**; el saldo a favor es un **pasivo civil** (arts. 765/766 CCyC post-DNU 70/2023) → **jamás mintear crédito por plata que no entró**.
- **Pagos a cuenta / sin imputar** → crédito en la **moneda en que se pagaron** (no cancelaron obligación alguna → no heredan moneda de factura).
- **Rechazado explícitamente:** prorrateo entre monedas y "moneda de la factura principal" (arbitrarios, sin base legal).

**Variante opcional no bloqueante:** antes de calcular el saldo, auto-imputar los pagos a cuenta a las facturas abiertas de la reserva con las reglas normales de imputación; solo el residuo genuinamente sin imputar cae en "moneda de pago". Si el flujo actual **no** auto-imputa (a verificar en implementación), la regla directa (a cuenta = moneda de pago) es igualmente defendible; se documenta como variante.

**CORRECCIÓN al sitio de minteo (C1 del re-review — verificado en código):** rev.2 apuntaba a `ApplyAnnulWithPaymentsToCreditAsync` + `OnAllocationRecordedAsync`. **Estaba mal.** Evidencia:

- `ApplyAnnulWithPaymentsToCreditAsync` ejecuta `CancellationToClientCreditConverter`, que es el camino de anular **SIN factura con CAE viva** (docstring `src/TravelApi.Infrastructure/Reservations/CancellationToClientCreditConverter.cs:11-19`). **ADR-042 es el caso opuesto** (anular CON facturas con CAE). Ese converter **no corre** en este escenario. Su moneda sale de `Payment.ImputedCurrency` (ADR-021).
- `OnAllocationRecordedAsync` (`BookingCancellationService.cs:2176-2187`) **no crea** el `ClientCreditEntry`; solo transiciona a `ClientCreditApplied`.
- En el camino CON NC (el de ADR-042), el `ClientCreditEntry` se crea en el **flujo de reembolso del operador**: `OperatorRefundService.CreateEntryAsync(..., currency: refund.Currency, …)` (`OperatorRefundService.cs:533-541`). **La moneda del crédito hoy la fija la moneda del REEMBOLSO DEL OPERADOR**, que se valida contra la moneda de la **línea del operador** (`EnsureBookingCancellationCanReceiveOperatorRefund:940-959`), NO contra la moneda de la obligación del cliente.

**Sitios reales de creación de `ClientCreditEntry` y su fuente de moneda (verificado):**

| Camino | Cuándo | Fuente de moneda hoy |
|---|---|---|
| `CancellationToClientCreditConverter` | anular SIN CAE vivo (no ADR-042) | `Payment.ImputedCurrency` (obligación) |
| `OperatorRefundService.CreateEntryAsync` (`:538`) | reembolso del operador tras NC (ADR-042) | `refund.Currency` (= moneda del reembolso, validada contra línea del operador) |

**Dónde se enchufa `ResolveCreditAllocationByCurrency` para ADR-042:** en el **camino del reembolso del operador** (`OperatorRefundService`, parámetro `currency` de `CreateEntryAsync:538`), **NO** en el converter. Es el único sitio que corre cuando hay NC con CAE.

**Reconciliación "moneda de la obligación" (B3) vs "moneda del reembolso" (código) — C1(c):** verifiqué en código que **NO son consistentes por construcción**. El código garantiza `refund.Currency == moneda de la línea del operador` (`:950-959`), que es un **eje distinto** de la moneda de la obligación del cliente (la factura de venta): un servicio puede facturarse al cliente en USD pero tener su circuito de operador en ARS. No puedo afirmar "el reembolso siempre viene en la moneda de la obligación". Por lo tanto **especifico el ajuste concreto**:

- **Regla:** el crédito del cliente se funda 1:1 con lo que **efectivamente devolvió el operador** (no se inventa FX ni se mintea plata que no entró — coherente con el tope de B3). `ResolveCreditAllocationByCurrency` **valida** que `refund.Currency` coincida con la moneda de la obligación del cliente para la(s) factura(s) anulada(s) de ese operador.
- **Caso normal (una factura, servicio en la misma moneda):** `refund.Currency` == obligación → consistente, minteo 1:1. Es el caso mono-factura y byte-equivalente a hoy.
- **Divergencia (operador reembolsa en moneda ≠ obligación del cliente):** **NO** se mintea en la moneda equivocada ni se inventa un TC → va a **revisión manual** (mismo criterio conservador que el pre-flight de TC=1, §3.5). Un operador matriculado resuelve el caso.

**Discriminador de moneda de la obligación (menor 2 del re-review — verificado):** es `Payment.ImputedCurrency` (ADR-021, `Payment.cs:40`), **no** un vínculo pago↔factura: `Payment.LinkedInvoiceId` (`Payment.cs:106-115`) es **informativo** y los guards/reconciliación deliberadamente **no** lo miran.

**Punto único, no se toca el flujo de NCs:** `ResolveCreditAllocationByCurrency(...)` es función pura invocada solo desde `OperatorRefundService.CreateEntryAsync`. La emisión de NCs (§3.5) es independiente.

**Estado:** operativamente firme para construir; formalización pendiente de contador matriculado. Citas: arts. 765/766 CCyC post-DNU 70/2023; decisión 2026-07-01.

#### 3.3.3 Orden del nodo en el envelope — validar YA, estáticamente (M2.f)

**Corrección al diseño rev.1:** rev.1 difería la verificación del orden a la homologación. El reviewer lo rechaza: **si las facturas USD rebotan hoy, el orden del nodo puede ser la causa raíz**, y no hay que esperar a homologar para descartarlo. Acción:

- Bajar el **XSD/WSDL del WSFEv1 v4** y **validar el orden estáticamente YA** (`CanMisMonExt` va después de `MonId`/`MonCotiz` — confirmar posición exacta contra el esquema, no de memoria; el comentario `AfipService.cs:1345-1348` admite que no está verificado).
- Agregar un **test de validación de esquema del envelope** (`FECAESolicitar` contra el XSD) para que un nodo fuera de orden se detecte en CI, no en un rebote de ARCA en prod.

#### 3.3.4 Homologación

Emitir en el ambiente de prueba de ARCA, con CAE aprobado: (a) una Factura USD, (b) su NC USD (`CanMisMonExt`), (c) una ND USD. Recién con los tres CAE de homologación se habilita USD en prod. **Nada de esto va por feature flag; el gate es operativo (no emitir USD en prod hasta tener los CAE de homologación + el chequeo estático de §3.3.3).**

### 3.4 Guard de liberación INV-081 (fiscal, crítico — B4)

Hoy `TryResolveExistingBcAsync` (`:1121+`) libera un BC para re-cancelar solo si `CreditNoteInvoiceId is null` (no dejó NC viva). Para multi-NC eso es insuficiente: el puntero principal podría ser null y aun así existir una NC hija viva.

**Cambio (con fallback plegado adentro, B4):** el guard debe cubrir el caso de **cero filas hijas** (BC legacy, pre-backfill, o post-rollback §6). "Ninguna hija con NC viva" es **vacuamente verdadero** cuando no hay hijas → liberaría un BC que sí tiene NC viva vía el puntero principal = **segunda NC sobre la misma factura** (incidente fiscal). Definición correcta del guard:

```
EsLiberable(bc):
  si bc NO tiene filas hijas   -> legacy: liberable solo si bc.CreditNoteInvoiceId is null   (comportamiento actual)
  si bc tiene filas hijas      -> liberable solo si NINGUNA hija tiene NC viva
                                  (ninguna Succeeded ni Pending con CreditNoteInvoiceId ya creado)
```

Ante la duda, no liberar (misma regla mental fiscal ya documentada en `:219-222`). **Test explícito obligatorio:** BC con **cero hijas** y `CreditNoteInvoiceId != null` → **NO libera** (§7).

### 3.5 Orden de operaciones atómico

Copia del molde del commit `46d2b65` (staged-audit + un solo `SaveChanges` + emisión posterior recuperable):

**`ConfirmAsync` (multi-NC):**

1. **Pre-flight (ANTES de la transacción y de encolar nada), sobre TODAS las facturas activas con CAE:**
   - ninguna ya `Succeeded`;
   - por cada factura extranjera: TC coherente (`MonCotiz > 0` y `!= 1`) y moneda soportada (`ArcaCurrencyMapper`). Si **alguna** falla → `throw` INV-156 (mensaje a gestión manual) → **no se emite nada** (premisa #4, todo-o-nada al frente). Esto generaliza el guard actual `IsForeignCurrencyInvoiceWithoutReliableRate` (`:1677`) de una factura a todas.
2. Dentro de la transacción envolvente (patrón `IExecutionStrategy` + `BeginTransaction` ya usado en `:1731-1745`): persistir estado BC + Reserva + servicios cancelados + recalculos + **las N filas hijas en `Pending`** + auditoría *staged*. Un solo commit.
3. **Después del commit** (fuera de transacción, porque Hangfire no es transaccional): **loop** `EnqueueAnnulmentAsync(originatingInvoiceId)` por cada fila hija. Cada uno encola su propio job; cada job POSTea a ARCA con su idempotency key por-comprobante (§2.2.5). Si el proceso muere entre commit y algún enqueue, el retry (§3.6) re-encola las hijas `Pending` sin NC — es idempotente.

#### 3.5.1 Callbacks (`OnArcaSucceededAsync` / `OnArcaFailedAsync`), reescritos con lock pesimista (B1)

**Problema B1 (verificado):** Hangfire corre **multi-worker** (`Program.cs:668-672`, `AddHangfireServer()` con `WorkerCount` default) y `ProcessAnnulmentJob` **no** tiene `[DisableConcurrentExecution]`. En rev.1, el callback "quedan hijas Pending" solo tocaba la fila hija, que **no muta la fila del padre** → el token de concurrencia optimista `xmin` del `BookingCancellation` (verificado en `BookingCancellation.cs:17-20`) **no se dispara**. Dos callbacks concurrentes pueden leer cada uno "queda 1 Pending", ninguno transiciona, y el BC queda **atascado en `AwaitingFiscalConfirmation` con TODAS las hijas `Succeeded`** (lost update).

**Decisión: lock pesimista del padre.** Cada callback, dentro de su transacción:

1. `SELECT … FROM "BookingCancellations" WHERE "Id" = @id FOR UPDATE` (SQL crudo vía `FromSqlRaw`) **antes** de leer/contar las hijas. **Nota:** no existe precedente de `FOR UPDATE` en el código (verificado: 0 usos) → es un patrón **nuevo** para este repo, hay que introducirlo con cuidado y test dedicado (§7). Alternativa equivalente: `pg_advisory_xact_lock(<bcId>)`.
2. Actualizar la fila hija (`Succeeded`/`Failed` + `CreditNoteInvoiceId`/`ArcaErrorMessage`).
3. **Contar las hijas con lectura fresca de la BD** (no del `ChangeTracker`), ya serializada por el lock.
4. Decidir la transición del padre:
   - 0 `Pending`, 0 `Failed` → `AwaitingOperatorRefund` + `BC.CreditNoteInvoiceId` = NC **principal** + transicionar reserva + disparar la ND (una vez).
   - hay `Pending` → permanecer en `AwaitingFiscalConfirmation`.
   - 0 `Pending`, ≥1 `Failed` → `ArcaRejected` + notificación (reusa la de hoy, `:5037-5065`).
5. Un solo `SaveChanges`; el lock se libera al commitear la transacción.

**Garantía documentada:** la correctitud del cierre **NO depende** de la idempotencia por-hija ni del `WorkerCount` de infra; depende **exclusivamente** del lock pesimista que serializa los callbacks concurrentes del mismo BC. El `FOR UPDATE` es sobre `BookingCancellations` (el padre), aunque el callback "solo" toque una hija, precisamente para serializar la lectura-decisión de completitud.

**Idempotencia de callback:** si la hija ya está en el estado destino (redelivery de Hangfire), log + return dentro del lock (patrón de los bridges FC1.3, `:5079-5081`).

#### 3.5.1.b Patrón completo del lock pesimista (C2 — sin precedente en el repo, se documenta entero)

Como no existe ningún `FOR UPDATE` en el código hoy, el patrón se especifica completo para que la implementación no improvise:

1. **Un solo lock, por `Id` del padre.** `SELECT 1 FROM "BookingCancellations" WHERE "Id" = @id FOR UPDATE` (o `pg_advisory_xact_lock(@id)`). Nunca lockear hijas ni otras tablas → sin riesgo de deadlock por múltiples recursos.
2. **Orden de adquisición único.** Siempre el padre por su `Id`, antes de leer hijas. No hay un segundo lock que ordenar.
3. **`lock_timeout` acotado** al inicio de la transacción del callback/retry: `SET LOCAL lock_timeout = '5s'` (valor a fijar en implementación; acotado, no infinito). Evita que un worker quede colgado indefinidamente si otro retiene el lock.
4. **Comportamiento ante timeout:** el `SET LOCAL` hace que el `FOR UPDATE` tire (Npgsql → excepción). El job **falla limpio** (no captura y sigue): Hangfire lo **reintenta** y en el reintento **re-lee fresco** (vuelve a tomar el snapshot, cuenta hijas de nuevo). Como el flujo es idempotente (idempotencia de callback arriba), el reintento no duplica efectos.
5. **Sin I/O externa dentro del lock.** Nada de llamadas HTTP a ARCA ni enqueues de Hangfire mientras se sostiene el `FOR UPDATE`. El lock cubre solo lectura-decisión-escritura en BD.
6. **Liberación al commit.** El lock se libera cuando la transacción commitea (o rollbackea). No hay `unlock` manual.

**Minor 1 del re-review — la ND queda FUERA del lock.** El disparo de la ND (`TryEmitCancellationDebitNoteAsync`, hoy post-`SaveChanges` en `:3267-3269`) se mantiene **después del commit** de la transacción del lock, **nunca** sosteniendo el `FOR UPDATE` a través de un enqueue de Hangfire (viola el punto 5). La secuencia es: `[FOR UPDATE → decidir → SaveChanges → COMMIT (libera lock)] → luego, ya sin lock, TryEmit ND`.

#### 3.5.2 InMemory (tests unit)

InMemory no soporta `FOR UPDATE` ni transacciones (ya se ramifica por `IsRelational()` en el código, `:1731`). En InMemory se ejecuta el cuerpo sin lock; la garantía de serialización se valida **solo** en integración Postgres (§7). Mismo criterio que el resto del service.

#### 3.5.3 `ForceArcaConfirmationAsync` reescrito por-hija (B2)

**Problema B2 (verificado):** hoy `ForceArcaConfirmationAsync` (`:1996`) valida la NC contra `bc.OriginatingInvoiceId` (`:2056`) y transiciona el padre a `AwaitingOperatorRefund` + setea `CreditNoteInvoiceId` (`:2082-2083`) **sin mirar hijas**. En multi-NC, forzar con UNA sola NC cerraría la anulación completa (viola todo-o-nada) y podría habilitar la ND prematuramente.

**Decisión (opción b del reviewer): Force opera POR-HIJA.**

1. Toma el mismo lock pesimista del padre (§3.5.1) dentro de su transacción.
2. Localiza la **fila hija** cuya factura origen corresponde a la NC confirmada fuera de banda (validando `OriginalInvoiceId`, tipo NC, `Resultado="A"`, CAE presente — misma validación de `:2054-2063` pero contra la hija, no contra el principal). La fuerza a `Succeeded`.
3. **Re-evalúa completitud EXACTAMENTE igual que el callback** (§3.5.1 paso 3-4), guardado sobre `Status == AwaitingFiscalConfirmation` para **no re-transicionar ni re-disparar la ND** si el BC ya avanzó.
4. La idempotencia actual (no-op si ya está `AwaitingOperatorRefund`/adelante, `:2011-2039`) se conserva a nivel padre; la nueva es a nivel hija.

### 3.6 Retry de NCs faltantes

Endpoint nuevo, espejo de `retry-debit-note`:

```
POST /api/cancellations/{publicId}/retry-credit-notes
```

Lógica (molde de `RetryDebitNoteEmissionAsync:3693`):

- Precondiciones: BC en `ArcaRejected` (o `AwaitingFiscalConfirmation` con hijas `Failed`/atascadas); **mismo permiso server-side que anular** (como `RetryDebitNoteEmissionAsync:3714` exige el permiso de la ND); defensa en profundidad (no confiar en el front).
- **Serialización por-BC con el lock de B1 (M1).** El retry toma el **mismo `SELECT … FOR UPDATE` del padre** (§3.5.1) antes de tocar hijas. Dentro del lock, por cada fila hija **no `Succeeded`**:
  - **Anti-doble-emisión:** buscar una NC ya creada para esa `OriginatingInvoiceId` de esta reserva (tipos NC 3/8/13/53). Si existe → **re-vincular** la hija (no re-emitir), derivando su `Status` del estado real del Invoice. (Igual que la re-vinculación de ND huérfana `:3746-3768`.)
  - Si no existe → **persistir la hija a `Pending` BAJO el lock ANTES de encolar**, y luego `EnqueueAnnulmentAsync(originatingInvoiceId)`. Así un segundo retry concurrente, al tomar el lock, ve la hija ya `Pending` → **no-op** (no re-encola).
- **Por qué el lock es necesario acá (M1, verificado):** `EnqueueAnnulmentAsync` (`:1038`) es **read-check-write no atómico** (lee `AnnulmentStatus`, decide, escribe) — dos retries concurrentes pueden pasar ambos el check desde `Failed`. Y `ArcaIdempotencyKeys` se keyea por el **id de la NC nueva** (`BuildInvoiceIdempotencyKey`), que **no cubre dos NC distintas sobre la misma factura original** (cada intento crea un `CreatePendingInvoice` con id nuevo → key distinta). La idempotencia por-comprobante protege el re-despacho del **mismo** job, no dos jobs que crean NCs distintas. Por eso la serialización real la da el lock del padre, no la idempotency key.
- Nunca 500 que trabe la reserva: ante fallo de emisión, la hija queda `Failed`/revisión y se devuelve éxito-con-aviso.
- Idempotente: reintentar cuando ya está todo `Succeeded` → no-op con mensaje claro.

### 3.7 Contrato API

- **`DraftAsync`**: deja de tirar INV-100 para el caso multi-factura legítimo (varias facturas de venta con CAE). Construye una fila hija por cada factura activa con CAE. Se conserva INV-100 solo para el caso verdaderamente ambiguo (ninguna factura, o inconsistencia de datos). El pre-check de "factura ya anulada" (`:250`) se generaliza a "alguna ya anulada".
- **`ConfirmAsync`**: sin cambios de firma. Internamente hace el pre-flight sobre todas + persiste hijas + loop-enqueue. El request no necesita campos nuevos.
- **`BookingCancellationDto`**: agrega una colección liviana `creditNotes[] { arcaCurrency, status, numeroComprobante?, arcaErrorMessage? }` y un read-model `canRetryCreditNotes` (bool) para que la UI muestre "2 de 3 notas emitidas — reintentar". El diseño de pantalla lo hace `ux-ui-disenador` (gate UX); acá solo se define el contrato.
  - **Gate data-exposure (menor del review):** `arcaErrorMessage` **no** se expone crudo al usuario. Se mapea a copy amigable (ARCA a veces devuelve XML/errores técnicos). Mismo criterio que `Security/CancellationErrorMessageLeakUnitTests.cs`. El detalle técnico va al log/auditoría, no al DTO de cara al usuario.
- **Endpoint nuevo** `POST /api/cancellations/{publicId}/retry-credit-notes` (§3.6). **Autorización server-side:** exige el **mismo permiso que anular** (no confiar en que el front oculte el botón), igual que `RetryDebitNoteEmissionAsync` valida su permiso en `:3714`.

## 4. Consecuencias

**Positivas**

- Se habilita el caso real USD+ARS sin comprometer la atomicidad fiscal.
- El aggregate queda explícito y testeable; la completitud es una cuenta sobre hijas.
- Mono-factura queda byte-equivalente (una hija que espeja el puntero principal).
- La ND, la idempotencia anti-doble-CAE y el molde de retry se **reusan**, no se reinventan.

**Costos / riesgos asumidos**

- Toca los callbacks de ARCA (código fiscal caliente). Mitigación: tests unit + integración Postgres que cubran los tres cierres (todas OK, parcial, todas fallan) y el orden de callbacks intercalado.
- Nuevo estado semántico sobre `ArcaRejected` (ahora también significa "parcial con vivos"). Mitigación: el guard de liberación (§3.4) mira hijas, no el puntero singular, así que no se libera nada con NC viva.
- Migración con backfill sobre una tabla fiscal.

**Lo que NO cambia (explícito)**

- La ND del operador: una por cancelación, asociada a la factura principal, misma moneda derivada del `MonId` principal. Sin tocar `ConfirmPenaltyAsync`/`RetryDebitNoteEmissionAsync`.
- La forma de emitir cada NC (hereda `MonId`/`MonCotiz` del origen; `CbtesAsoc`; idempotencia por-comprobante).
- INV-081 (una cancelación activa por reserva) y el 1:1 Reserva↔BC.
- El valor efectivo de `CanMisMonExt` para la agencia (`"N"`).
- Sin feature flags.

## 5. Alternativas consideradas

1. **Múltiples BC por reserva** — descartada (§3.1): rompe INV-081, ND-única, DTO/UI.
2. **Sin tabla hija, derivar de `Invoice.AnnulmentStatus`** — descartada (§3.1): frágil e inobservable para un flujo fiscal.
3. **Estado nuevo `PartiallyAnnulled`** en `BookingCancellationStatus` — descartada por ahora: reusar `ArcaRejected` + hijas evita tocar el CHECK del state machine y las transiciones permitidas. Si la observabilidad lo pide, se puede agregar después sin migración de datos.

## 6. Plan de migración / rollback

**Migración `Adr042_M1_AddBookingCancellationCreditNotesAndCanMisMonExt`:**

1. `CreateTable BookingCancellationCreditNotes` + índice único `(BookingCancellationId, OriginatingInvoiceId)` + FKs (`OnDelete: Cascade` desde el BC padre; `Restrict` hacia `Invoices` para preservar rastro fiscal, mismo patrón que `PartialCreditNoteApprovalRequest`).
2. `AddColumn Invoices.CanMisMonExt` (`char(1)?`, nullable; `null` = pesos/no aplica). Congela el valor declarado al emitir (§3.3.1). Backfill: `null` para todas las facturas PES existentes; para facturas en divisa existentes → `'N'` (es lo que se emitió de hecho).
3. **Backfill hijas:** por cada `BookingCancellation` existente con `OriginatingInvoiceId`, insertar una fila hija con `CreditNoteInvoiceId = BC.CreditNoteInvoiceId`, `ArcaCurrency` = `MonId` de la factura origen, y `Status` derivado (`Succeeded` si hay NC con CAE, `Pending` si está en `AwaitingFiscalConfirmation`, `Failed` si `ArcaRejected`).

**Rollback:** `DropTable` + `DropColumn`. Como los punteros singulares (`OriginatingInvoiceId`/`CreditNoteInvoiceId`) se conservan y siguen siendo la fuente de verdad para mono-factura, revertir deja el sistema funcionando para el caso de una factura (el multi-factura vuelve a bloquear con INV-100). El código nuevo debe tolerar el arranque previo al backfill (si un BC no tiene hijas, tratarlo como mono-factura vía el puntero principal — es exactamente el fallback del guard B4, §3.4).

**Hueco del `Down` (documentado, del review):** `DropTable BookingCancellationCreditNotes` **pierde las filas hijas NO-principales** (las NCs secundarias de un BC multi-factura). El puntero principal sobrevive, pero el rastro de las NCs secundarias en el aggregate se pierde (las Invoices NC siguen existiendo en `Invoices`, no se borran — se pierde solo el vínculo hijo↔BC). Por eso el rollback **solo es seguro si no se emitió ninguna anulación multi-factura** entre el `Up` y el `Down`. Si ya hubo multi-factura, revertir requiere export previo de las hijas. Se documenta como precondición operativa del rollback.

## 7. Estrategia de test

- **Unit (sin Docker):**
  - `ResolveCanMisMonExt` (PES → null; divisa → "N"); espejado del original a la NC.
  - `ResolveCreditAllocationByCurrency` (B3): imputado a USD → crédito USD por lo cobrado; imputado a ARS → ARS nominal; parcial → tope = cobrado (NO monto de NC); a cuenta → moneda de pago; nunca prorrateo.
  - Evaluación de completitud sobre hijas (todas OK / parcial / todas fallan).
  - Guard de liberación INV-081 (B4): **con hija viva → no libera**; **cero hijas + `CreditNoteInvoiceId != null` → NO libera** (caso legacy, test explícito exigido por el review).
- **Integración Postgres:**
  - (a) 2 facturas (USD+ARS), ambas NC OK → reserva `AwaitingOperatorRefund`, ND una sola vez.
  - (b) una NC OK + una falla → BC `ArcaRejected`, la NC OK **no** se revierte, retry re-emite solo la faltante y cierra.
  - (c) **B1 — dos invocaciones del bridge REALMENTE concurrentes contra Postgres real** (molde: `src/TravelApi.Tests/Cancellation/Integration/XminConcurrencyTests.cs`) que pruebe que el `FOR UPDATE` serializa y **no** deja el BC en `AwaitingFiscalConfirmation` con todas las hijas `Succeeded` (el lost-update de B1).
  - (d) **M1 — dos retries concurrentes** de la misma hija `Failed` → una sola re-emisión (la hija pasa a `Pending` bajo lock; el segundo ve `Pending` → no-op).
  - (e) **B2 — `ForceArcaConfirmationAsync` en multi-NC**: forzar una sola NC **no** cierra la anulación completa (quedan hijas `Pending` → BC sigue en `AwaitingFiscalConfirmation`, ND no se dispara).
  - (f) pre-flight con una factura USD TC=1 → **no se emite ninguna NC** (todo-o-nada al frente).
  - (g) mono-factura sin hija (pre-backfill) sigue funcionando.
- **Esquema del envelope (M2.f):** test que valida `FECAESolicitar` contra el XSD del WSFEv1 v4 (detecta `CanMisMonExt` fuera de orden en CI, no en prod).
- **No romper** la suite existente de cancelación (`src/TravelApi.Tests/Cancellation/**`, `src/TravelApi.Tests/Unit/Cancellation*`, `Adr025*`, `InvoicesControllerAnnul*`). En particular `Security/CancellationErrorMessageLeakUnitTests.cs` (no filtrar internos) y `CancellationTotalAnnulmentRecalcTests.cs` (recálculo total).
- **Homologación ARCA (§3.3.4), obligatoria antes de USD en prod:** CAE aprobado de Factura USD + NC USD + ND USD.

## 8. Riesgos operativos y de datos

- **Riesgo fiscal alto:** una NC con CAE nunca se revierte. El diseño lo respeta (parcial → revisión, nunca rollback). El guard de liberación (§3.4) impide re-cancelar sobre NC viva.
- **Concurrencia (B1/M1):** el lost-update de callbacks/retries se mitiga con el lock pesimista del padre (§3.5). Es un **patrón nuevo** en el repo (0 usos de `FOR UPDATE` hoy) → riesgo de introducirlo mal (deadlock, lock no liberado). Mitigación (§3.5.1.b): un solo lock por `Id` del padre, orden único, sin I/O externa dentro del lock, liberado al commit, **`lock_timeout` acotado (~5s)** con el job fallando limpio ante timeout → **Hangfire reintenta re-leyendo fresco** (el flujo es idempotente), test de concurrencia real (§7.c/d).
- **Observabilidad del lost-update:** métrica **`metric:bc_awaiting_with_zero_pending_children`** (BC en `AwaitingFiscalConfirmation` con 0 hijas `Pending`) — síntoma de que el lock falló. Reusa el prefijo `metric:` del logging estructurado existente (ver `InvoiceService.cs:1573`, `metric:bc_bridge_failed`) para alerting.
- **`CanMisMonExt` orden de nodo (M2.f):** rebota el comprobante si está mal. Se valida **estáticamente contra el XSD YA** (§3.3.3), no se difiere a homologación; puede ser la causa raíz de rebotes USD actuales.
- **`CanMisMonExt` par NC↔original (§3.3.1):** una NC con distinto `CanMisMonExt` que su original **rompe el par en el libro IVA** y puede rebotar el CAE. Mitigación: la NC **espeja** estrictamente el valor congelado del original, nunca lo redecide.
- **Backfill sobre datos fiscales:** validar `Up`/`Down` en Postgres real antes de aplicar. Ojo con el hueco del `Down` (§6): pierde hijas no-principales si ya hubo multi-factura.
- **Exposición de internos:** el DTO nuevo y `arcaErrorMessage` deben pasar el gate `data-exposure-reviewer` (nada de GUIDs/IDs/errores técnicos crudos al usuario, §3.7).

## 9. Decisiones (estado)

### 9.1 Cerradas — se construye con esto

- **Moneda + reparto del saldo a favor (B3):** DECIDIDA por el contable (2026-07-01). Ver §3.3.2 (moneda de la obligación cancelada, por lo cobrado; a cuenta = moneda de pago; tope = cobrado, no NC). Citas: arts. 765/766 CCyC post-DNU 70/2023.
- **`CanMisMonExt` (M2.e):** DECIDIDA (2026-07-01). Persistir el valor declarado en `Invoice`, la NC lo espeja; hoy divisa = `"N"`. Ver §3.3.1. Cita: RG 5616/2024.
- **Ambas** quedan "operativamente firmes para construir; formalización pendiente de contador matriculado" (no hay norma que legisle la herencia de `CanMisMonExt` ni "a cuenta = moneda de pago"; son criterios prudentes y defendibles).

### 9.2 Abiertas — NO inventar

1. **(Contador)** ¿La ND por multa del operador se asocia a la factura **principal** o debería asociarse a una factura por moneda? El diseño mantiene "una ND, asociada a la principal" (no regresión, premisa #6). Confirmar que es fiscalmente correcto en multimoneda.
2. **(Dueño)** Criterio de "factura principal" cuando hay varias: se propone "la más reciente por `CreatedAt`" (criterio ya vigente en `DraftAsync:189`). Confirmar.
3. **(Contador, futuro)** Camino `CanMisMonExt="S"` (recalcular `MonCotiz` por BNA en la NC en vez de heredar): pendiente de firma matriculada. No se construye ahora (§3.3.1).
4. **RESUELTO (re-review, verificado).** El flujo **NO** auto-imputa a nivel factura: la imputación es **por moneda al registrar el pago** (`Payment.ImputedCurrency`, ADR-021; `ReservaService.cs:372`, `CancellationToClientCreditConverter.cs:82-95`), y los pagos a cuenta reales (`ReservaId` null) **no entran** al minteo del crédito. Por lo tanto aplica la **regla directa "a cuenta = moneda de pago"**, que es exactamente lo que ya hace el código. No hace falta la variante opcional de §3.3.2.

## 10. Plan de implementación (orden de construcción, para hacer de corrido)

El diseño está cerrado. La construcción se hace en este orden; cada paso es autocontenido y deja la suite verde antes de pasar al siguiente. La UI (pantalla que muestra "2 de 3 notas emitidas — reintentar") pasa por el gate UX con Gastón antes de tocarse; el backend NO la espera.

**F1 — Modelo + migración (base, sin cambiar comportamiento todavía).**
- Entidad `BookingCancellationCreditNote` (`src/TravelApi.Domain/Entities/`) + navegación `Lines`-style en `BookingCancellation` (§3.1).
- Columna `Invoice.CanMisMonExt` (`char(1)?`).
- Migración `Adr042_M1_AddBookingCancellationCreditNotesAndCanMisMonExt` en `Migrations/App/` con backfill (§6): una hija por cada BC existente + `CanMisMonExt` de facturas divisa a `'N'`.
- Config EF (índice único, FKs Cascade/Restrict). Snapshot actualizado.
- Verificación: `Up`/`Down` en Postgres real; suite existente sigue verde (nada usa la tabla aún).

**F2 — CanMisMonExt correcto (independiente del resto, desbloquea USD hoy).**
- Función pura `ResolveCanMisMonExt` + persistir el valor al emitir la factura; la NC/ND lo **espeja** del original (§3.3.1).
- Validar el **orden del nodo contra el XSD del WSFEv1 v4 estáticamente** + test de esquema del envelope (§3.3.3, M2.f). Esto puede ser la causa raíz de rebotes USD actuales.

**F3 — Draft + Confirm multi-factura (emisión N NCs).**
- `DraftAsync`: dejar de tirar INV-100 para el multi-factura legítimo; crear una hija por factura con CAE (§3.7).
- `ConfirmAsync`: pre-flight sobre TODAS las facturas (TC coherente/moneda soportada, todo-o-nada al frente); persistir hijas `Pending` en la transacción; loop `EnqueueAnnulmentAsync` por hija tras el commit (§3.5).

**F4 — Lock pesimista + callbacks por-hija (el corazón fiscal).**
- Introducir el patrón `SELECT … FOR UPDATE` (nuevo en el repo) con `lock_timeout` (§3.5.1.b, C2).
- Reescribir `OnArcaSucceededAsync`/`OnArcaFailedAsync` para actuar por-hija y transicionar el padre solo con 0 hijas `Pending` (§3.5.1).
- Reescribir `ForceArcaConfirmationAsync` por-hija (§3.5.3, B2).
- Extender el guard de liberación INV-081 con el fallback cero-hijas (§3.4, B4).

**F5 — Retry de NCs faltantes.**
- Endpoint `POST /api/cancellations/{publicId}/retry-credit-notes` (thin controller) + método en el service, serializado con el mismo lock, anti-doble-emisión (§3.6, M1). Mismo permiso server-side que anular.

**F6 — DTO + minteo de crédito por moneda.**
- `BookingCancellationDto.creditNotes[]` + `canRetryCreditNotes`; `arcaErrorMessage` mapeado a copy amigable (§3.7).
- `ResolveCreditAllocationByCurrency` enchufado en `OperatorRefundService.CreateEntryAsync:538`; divergencia refund≠obligación → revisión manual (§3.3.2, C1).

**F7 — Tests + métrica + gates.**
- Toda la batería de §7 (unit + integración Postgres con concurrencia real).
- Métrica `bc_awaiting_with_zero_pending_children` (§8).
- Gates obligatorios del proyecto: `backend-dotnet-reviewer`, `security-data-risk-reviewer`, `data-exposure-reviewer`, y `qa-automation-*`. Homologación ARCA (Factura+NC+ND USD) antes de habilitar USD en prod.

**Fuera de banda (no bloquean el backend):** la pantalla pasa por `ux-ui-disenador` con Gastón; las 3 preguntas abiertas de §9.2 al contador se ajustan al construir sin rediseñar.
