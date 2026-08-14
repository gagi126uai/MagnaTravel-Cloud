# ADR-053 — Fechas del viaje: calculadas y de solo lectura (Salida/Regreso) + campo nuevo "fecha prometida"

- **Status**: PROPUESTO — Round 3 (cierra B5-B7, M3-M4 del round 2 review). Negocio: decisiones (1)-(5) YA
  FIRMADAS por el dueño el 2026-08-11 (NO reabrir). Técnica: lista para veredicto final del reviewer.
- **Date**: 2026-08-12 (round 1) / 2026-08-12 (round 2) / 2026-08-13 (round 3)
- **Autor**: software-architect, sobre código real inspeccionado (§1). Cada afirmación de este documento
  está atada a un archivo y una línea, o marcada explícitamente como propuesta/no verificada.
- **Related**: **ADR-019** (este ADR REEMPLAZA su regla R8 — "el MIN/MAX de la reserva incluye servicios
  cancelados a propósito" — con la firma del dueño del 2026-08-11); ADR-020 (candado de autorización F4);
  ADR-035 (primera compuerta por estado); ADR-036 ("Sacar de viaje" / `CorrectTravelingEntryAsync`);
  ADR-048/050 (estados derivados, "un solo escritor"); ADR-052 (precedente del patrón de backfill
  embebido en la migración, que este ADR reutiliza).
- **Reglas de la constitución citadas**: F-2, P-20, P-21, PR-12, T-3, T-7, T-8, T-13, T-14.

## 0. Qué decidió el dueño (2026-08-11, NO reabrir)

1. La ventana del viaje (Salida/Regreso de la reserva) pasa a ser **CALCULADA Y DE SOLO LECTURA** desde
   los servicios **VIGENTES** — los servicios **ANULADOS dejan de contar**. Esto **reemplaza ADR-019 R8**.
2. Cuando un servicio nuevo/editado **mueve la ventana**, **AVISO SUAVE en el momento, sin frenar**
   (P-20/P-21), con rastro de quién/cuándo (PR-12). Ejemplo del dueño: *"Con este hotel, el viaje pasa a
   terminar el 12/04 — ¿la fecha del hotel está bien?"*
3. Campo **NUEVO, aparte, opcional y visible**: **"fecha prometida"** (patrón Odoo calculada+prometida) —
   editable, **jamás pisado por el cálculo**.
4. El candado invisible `DatesManuallySet` **se elimina** a favor de un **estado VISIBLE** con botón
   **"volver a calcular"**.
5. Migración: al deployar se **RECALCULAN TODAS las reservas existentes** — decisión explícita del dueño
   **contra la recomendación del arquitecto** de dejar las viejas como están; el riesgo fue avisado y
   aceptado.

Este documento diseña CÓMO se construyen esas cinco decisiones sobre el código real, y nombra tres
problemas que la investigación encontró y que la decisión (1) obliga a resolver (no eran parte del pedido
original, pero quedarían rotos si no se tocan — ver §1.4).

## 1. Contexto (hechos verificados en el repo)

### 1.1 Cálculo de hoy (lo que cambia)

`ReservaScheduleCalculator.ComputeAsync` (`src/TravelApi.Infrastructure/Services/ReservaScheduleCalculator.cs:34-111`)
calcula `(Start, End)` = MIN/MAX de fechas de los 6 tipos de servicio (Vuelo, Hotel, Traslado, Paquete,
Asistencia, genérico), **incluyendo servicios cancelados a propósito** — comentario explícito en
`:17-24` que cita ADR-019 R8. Ese MIN/MAX alimenta `Reserva.StartDate`/`EndDate` persistidos
(`src/TravelApi.Domain/Entities/Reserva.cs:279-280`, columnas `timestamp with time zone` nullable en la
tabla real **`TravelFiles`**, confirmado en `AppDbContext.cs:503`).

Ya existe en el repo un cálculo GEMELO que SÍ excluye cancelados: `UpcomingStartCalculator`
(`src/TravelApi.Infrastructure/Services/UpcomingStartCalculator.cs:54-134`, construido para ADR-019 "Próximos
inicios"). Usa el predicado `Status != "Cancelado"` para Hotel/Paquete/Traslado/Asistencia/genérico y
`Status NOT IN (UN, UC, HX, NO)` para Vuelo — **este es el predicado que la decisión (1) pide aplicar
también al cálculo de cabecera**, con la salvedad de que `UpcomingStartCalculator` solo calcula el
**inicio** (para el aviso de la campanita) en **lote** (muchas reservas a la vez, con una optimización de
tope superior); el de cabecera necesita **inicio Y fin**, **una reserva a la vez**, en el camino de
escritura. Por eso no se pueden fusionar en una sola función sin más — pero **si comparten predicado**
(D1), el riesgo "tres definiciones de cuándo empieza la reserva" que ADR-019 R8 dejó nombrado como deuda
**se reduce** (§4, Consecuencias).

### 1.2 El escape manual que la decisión (1) elimina

`ReservaService.UpdateDatesAsync` (`src/TravelApi.Infrastructure/Services/ReservaService.cs:719-787`)
permite editar `StartDate`/`EndDate` a mano (incluido borrarlos) y prende `reserva.DatesManuallySet = true`
(`:773`). Desde ahí, `BookingService.RecalculateReservationScheduleAsync`
(`src/TravelApi.Infrastructure/Services/BookingService.cs:749-771`) **respeta la marca y deja de recalcular**
(`:759`, `if (reserva.DatesManuallySet) return;`). El endpoint es `PATCH /{id}/dates`
(`src/TravelApi/Controllers/ReservasController.cs:817-826`, DTO `UpdateReservaDatesRequest`). Este endpoint,
el DTO y la marca **desaparecen** con la decisión (1): no tiene sentido "corregir a mano" un valor que pasa
a ser 100% calculado.

### 1.3 Quién dispara el recálculo hoy — y el hallazgo: NO es un solo escritor (viola T-7 ya HOY)

`RecalculateReservationScheduleAsync` (BookingService.cs) se llama desde **16 sitios**: 5 en
`BookingService.CatalogCreates.cs` (líneas 173, 290, 383, 469, 557), 10 en `BookingService.cs` (líneas
1085, 1153, 1386, 1439, 1617, 1669, 1857, 1909, 2128, 2180 — cubren create/update/delete/cambio de estado
de Hotel/Vuelo/Traslado/Paquete/Asistencia) y 1 en `BookingService.Reschedule.cs:93` ("Reprogramar viaje").

**Pero existen DOS implementaciones más, independientes, del mismo cálculo "recompute y persistí":**

- `ReservaLifecycleAutomationService.AutoRepairTravelingDatesAsync`
  (`src/TravelApi.Infrastructure/Services/ReservaLifecycleAutomationService.cs:226-254`) — job de reparación
  que también llama `ReservaScheduleCalculator.ComputeAsync` y escribe `reserva.StartDate`/`EndDate`
  directamente (`:240-241`), con su **propio** filtro `!r.DatesManuallySet` (`:231`).
- **Tres sitios que NUNCA llaman a ningún recálculo**, todos en `ReservaService.cs` (servicio genérico
  "Otro" + el endpoint de borrado unificado):
  - `AddServiceAsync` (alta del servicio genérico, `ReservaService.cs:4114-4241`): guarda el servicio
    (`:4211-4212`) y solo actualiza saldo/deuda de proveedor — **cero recálculo de fecha**.
  - `UpdateServiceAsync` (edición del servicio genérico, `ReservaService.cs:4243-4410`): **escribe
    `service.DepartureDate`/`ReturnDate` directamente** (`:4308-4309`) y tampoco recalcula la cabecera.
  - `RemoveServiceAsync` (`ReservaService.cs:4412-4525`) — el borrado **unificado** de CUALQUIERA de los 6
    tipos, expuesto en `DELETE /api/reservas/services/{servicePublicIdOrLegacyId}`
    (`src/TravelApi/Controllers/ReservasController.cs:322-330`, Admin-only). Prueba: para Vuelo, Hotel,
    Traslado, Paquete y Asistencia (`:4438-4522`) hace `Remove` + `UpdateBalanceAsync`, **nunca**
    `RecalculateReservationScheduleAsync`. Este es un camino **live** y **distinto** del
    `DeleteHotelAsync`/`DeleteFlightAsync`/etc. de `BookingService.cs` (que SÍ recalculan) — los
    controllers por-tipo (`HotelBookingsController.cs:207`, `FlightSegmentsController.cs:198`, etc.) usan
    `BookingService`; el controller unificado de `ReservasController.cs:325` usa `ReservaService`. **Hoy
    conviven dos caminos de borrado para los mismos 6 tipos, y solo uno de los dos recalcula.**

**Cuarto agujero (B1 del review, verificado)** — `QuoteService.ConvertToFileCoreAsync`
(`src/TravelApi.Infrastructure/Services/QuoteService.cs:329-373`, privado, invocado por
`ConvertToFileAsync:323-327` dentro de una transacción `IsolationLevel.Serializable` abierta en `:331`):
crea la `Reserva` copiando `StartDate`/`EndDate` DIRECTO de la cabecera del presupuesto
(`quote.TravelStartDate`/`TravelEndDate`, `:363-364`), y arma los servicios más abajo (`:397-568`) con
fallbacks **propios e independientes** cuando el presupuesto no trae fecha — `CheckIn = quote.TravelStartDate
?? DateTime.UtcNow` (`:404`), lo mismo para vuelo (`:451-452`), traslado (`:487`) y paquete (`:519-520`).
Nunca llama a ningún recálculo; el método hace `SaveChangesAsync` + `transaction.CommitAsync`
en `:586-587` con la cabecera ya persistida y potencialmente desalineada de los servicios reales que se
acaban de crear. Es un camino de ALTA de reserva (no de edición) — hoy pasa desapercibido porque los
fallbacks casi siempre coinciden con lo que se copió a la cabecera, pero no hay garantía de eso (un item
sin `RateId` con fechas propias, o una fecha de item que el mapeo no usa, deja la cabecera mintiendo desde
el primer commit).

**Conclusión verificada**: el sistema de HOY ya tiene, de hecho, **cuatro implementaciones divergentes** de
"recalculá la ventana de la reserva" (BookingService, el job de reparación, la ausencia total en 3 puntos
de ReservaService, y la asignación directa sin recálculo de `QuoteService`) — T-7 ("un solo escritor por
estado derivado") **ya está roto hoy**, con bajo impacto porque la cabecera era "solo una sugerencia". La
decisión (1) la vuelve **autoritativa y de solo lectura**: estos cuatro agujeros dejan de ser una
curiosidad de bajo impacto y pasan a ser **datos derivados incorrectos en producción** (violación directa
de F-2) si no se cierran como parte de esta obra.

### 1.4 Consumidores de la ventana (por qué el orden de deploy importa)

- **Deudores** — `PaymentService.GetDebtorsByDepartureAsync`
  (`src/TravelApi.Infrastructure/Services/PaymentService.Debtors.cs:30-88`): `dueDate =
  ResolvePaymentDueDate(reserva.StartDate, dueDays)` (`:47`), y el listado se ordena por `DepartureDate`
  (`:76-79`). El **vencimiento del saldo** (fecha límite de pago) depende directamente de `StartDate`.
- **Aviso de reserva impaga próxima a salir** — `ReservaUnpaidAlertWindow.IsWithin(...)` llamado desde
  `ReservaService.cs:3371-3376`, con `startDate: file.StartDate` — otro consumidor de `StartDate` no citado
  en el pedido original, encontrado en la investigación.
- **Automático de estados** — `ReservaLifecycleAutomationService`:
  - `AutoTransitionConfirmedToTravelingAsync` (`:268-274`) promueve `Confirmed → Traveling` cuando
    `r.StartDate.Value.Date <= today` (`:272-273`), corre por Hangfire `Cron.Daily(3)` = 00:00 ART
    (`Program.cs:814-817`, confirmado en ADR-019 §1).
  - `AutoTransitionTravelingToClosedAsync` (`:430-441`) cierra `Traveling → Closed` cuando
    `r.EndDate.Value.Date < today` (`:440-441`).
- **Comentario DESACTUALIZADO, confirmado por esta investigación** —
  `ReservaService.cs:2389-2390` dice *"Verificado (grep 2026-06-22): nada calcula plata/comision/vencimiento
  sobre Reserva.StartDate"*. Eso era cierto el 2026-06-22. `PaymentService.Debtors.cs` (pantalla de
  Cobranzas, spec firmada **2026-08-06**, posterior al comentario) **sí calcula un vencimiento** —
  `dueDate` — a partir de `StartDate` (arriba). El comentario queda **falso hoy** y se corrige como parte
  de esta obra (§5, tanda de limpieza).

### 1.5 "Sacar de viaje" también usa `StartDate = null` como señal — y deja de poder hacerlo

`CorrectTravelingEntryAsync` ("Sacar de viaje", `ReservaService.cs:2330-2409`) hoy borra `StartDate` a
propósito (`:2391`) como señal de "esta reserva necesita que alguien revise la fecha del servicio", y usa
esa MISMA señal (`Status == Confirmed && StartDate == null`) para armar
`ReservaDto.IsUnderCorrection` (`ReservaService.cs:3704-3706`), que pinta el chip "En corrección" en el
front (`ReservaDto.cs:594-602`). Bajo la decisión (1), `StartDate` es de solo lectura y la recalcula el
único escritor — **poner `null` a mano deja de ser válido** (T-7) y, además, si la reserva todavía tiene
servicios vigentes, el próximo recálculo lo volvería a llenar igual, sin que nadie haya corregido nada.
**Esto no lo pidió el dueño explícitamente, pero es una consecuencia directa y obligatoria de la decisión
(1)** — se resuelve en D4 con el mismo mecanismo del botón "volver a calcular" (decisión 4), sin tocar el
contrato que ya consume el front (`IsUnderCorrection` sigue significando lo mismo).

### 1.6 "Reprogramar viaje" (contexto, no se rediseña)

`BookingService.RescheduleAsync` (`BookingService.Reschedule.cs:24-107`) mueve TODAS las fechas de TODOS
los servicios (incluidos los cancelados, a propósito — comentario `:169-176`, justificado hoy por la
simetría del MIN/MAX-con-cancelados de ADR-019 R8) y usa el mismo guard fiscal CODE-03
(`MutationGuards.GetReservaDatesMutationBlockReasonAsync`, `:59-60`). Esta obra **no cambia el
comportamiento** de "reprogramar" (sigue moviendo también los cancelados, por fidelidad histórica del
itinerario), pero su comentario que justifica esa decisión citando el viejo MIN-con-cancelados queda
desactualizado y se corrige (§5).

## 2. Decisiones de diseño

### D1 — Modelo: columnas PERSISTIDAS, un solo escritor compartido (no "calculadas al vuelo")

**Recomendación única: `Reserva.StartDate`/`EndDate` siguen siendo columnas persistidas**, no una vista
calculada en cada lectura. Motivos:

- Ya se **indexan y ordenan por ellas** hoy (`AppDbContext.cs:523`, índice `(Status, StartDate,
  CreatedAt)`; el listado de Deudores ordena por `DepartureDate`, `PaymentService.Debtors.cs:76-79`).
  Calcularlas al vuelo obligaría a un JOIN/agregación sobre 6 tablas en cada consulta que hoy filtra u
  ordena por fecha — costo real, sin beneficio, dado que el problema de hoy NO es "persistir está mal",
  es "no hay un solo escritor confiable" (§1.3).
- Es el mismo patrón que la constitución ya exige para otros derivados de esta misma tabla
  (`DerivedCollectionStatus`/`DerivedInvoicingStatus`, ADR-048 T5): **un solo escritor, en la misma
  transacción que la mutación que dispara el cambio** (T-7, F-2) — nunca una pasada nocturna aparte.

**El cambio real es CUÁL función es el escritor, y que sea UNA sola.** `ReservaScheduleCalculator.ComputeAsync`
cambia su predicado para excluir servicios cancelados/anulados — esto ES el reemplazo de ADR-019 R8. Se
extrae una función compartida — firma **corregida en round 3 (B5)**:

```
ReservaScheduleCalculator.RecalculateAndPersistAsync(
    db, reservaId, string? actorUserId, string? actorUserName, ct)
```

(mismo archivo), con `actorUserId`/`actorUserName` **nullable**. Que:

1. Lee `Reserva.StartDate`/`EndDate` actuales.
2. Llama a `ComputeAsync` (ya con el predicado nuevo — D1.1).
3. Si cambió, persiste (`SaveChangesAsync`) y limpia `NeedsDateRecalculation` si estaba prendida (D4). El
   aviso (`PendingScheduleWarning`, D2.1) se escribe DENTRO de esta misma función, **solo cuando
   `actorUserId != null`** (D2.1, B7) — el escritor único sigue siendo uno solo; lo que cambia es que ya no
   depende de un `GetActor()` global fijo, sino de lo que cada llamador le pase.
4. Devuelve `(DateTime? Start, DateTime? End, bool Changed)` para que el llamador arme el aviso suave (D2).

**Por qué la firma cambia (B5 del review, round 2)**: la v2 de este documento daba por sentado un
`GetActor()` compartido, pero `ReservaScheduleCalculator` es un servicio de infraestructura sin
`IHttpContextAccessor` propio, y los tres servicios que lo van a llamar resuelven el actor cada uno a su
manera, verificado contra el código real:

- `BookingService`: helper privado `GetActor()` (`BookingService.cs:2561-2567`), ya usado por
  `StageRescheduleAudit` y otros audit trails del mismo archivo.
- `ReservaService`: helper privado `ResolveAuditActor()` (`ReservaService.cs:101-108`) — mismo patrón,
  nombre distinto; ya usado en `AddServiceAsync`/otros audit trails del archivo.
- `QuoteService`: **verificado que NO existe ningún helper de actor hoy** (`grep` de `userId|actor|
  NameIdentifier` sobre `QuoteService.cs` no devuelve resultados) — el servicio tiene
  `_httpContextAccessor` inyectado (`:24`) pero nunca lo usa para resolver identidad. Para
  `ConvertToFileCoreAsync` (D1, D7) hay que resolver el actor inline con el mismo patrón de claims que
  `ReservaService.ResolveAuditActor()` (2-3 líneas, sin agregar un helper nuevo formal — a criterio de
  quien implemente si vale la pena extraerlo).

Cada call-site pasa **su propio** `(actorUserId, actorUserName)` a `RecalculateAndPersistAsync` — el
escritor único conserva la cohesión (una sola función que decide y persiste), pero deja de asumir de dónde
sale el actor.

**Los 16 sitios existentes** (§1.3) pasan a llamar a esta función en vez de al método privado de
`BookingService`, pasando el actor que resuelven con `GetActor()`. **Los 4 agujeros encontrados**
(`AddServiceAsync`/`UpdateServiceAsync` del genérico, `RemoveServiceAsync` unificado,
`QuoteService.ConvertToFileCoreAsync`) también pasan a llamarla con su actor resuelto (`ResolveAuditActor()`
para los tres de `ReservaService`; resolución inline para `QuoteService`) — esto cierra la violación de T-7
que ya existe hoy (§1.3), no solo la que introduce esta obra. **El job de reparación**
(`AutoRepairTravelingDatesAsync`) también pasa a llamarla, pero con `actorUserId: null, actorUserName: null`
a propósito — no corre en un `HttpContext`, no hay actor humano que avise (B7, D2.1). `BookingService.
RecalculateReservationScheduleAsync` (el método privado actual) se retira; su único rol pasa a la función
compartida. Para `QuoteService.ConvertToFileCoreAsync` puntualmente: se saca la asignación directa de
`:363-364` y se llama a `RecalculateAndPersistAsync(_db, file.Id, actorUserId, actorUserName, ct)` (actor
resuelto inline, arriba) después del `foreach` que crea los servicios (`:569`) y ANTES del
`SaveChangesAsync`/`CommitAsync` finales (`:586-587`) — misma transacción `Serializable`, ningún commit
parcial con la cabecera desalineada.

#### D1.1 — Predicado "vigente" (B2 del review): el canónico de `WorkflowStatusHelper`, no el literal de `UpcomingStartCalculator`

**Corrección respecto de la primera versión de este documento**: NO se reutiliza el predicado tal cual
está escrito en `UpcomingStartCalculator` (`Status != "Cancelado"` literal, comparación case-sensitive).
Ese predicado es una comparación ingenua que **el propio repo ya documentó como fuente de bugs**:
`WorkflowStatusHelper.MapGenericStatus` (`src/TravelApi.Domain/Entities/WorkflowStatusHelper.cs:36-55`) es
la definición CANÓNICA de "vigente vs cancelado" que usa la plata (`CountsForSupplierDebtByType`,
`CountsForQuotedTotal`) y el listado, y su comentario (`:41-45`) explica por qué: *"Anclado al INICIO
(StartsWith), NO Contains: textos como 'A confirmar', 'sin emitir', 'desconfirmado' o 'no confirmado'
CONTIENEN la palabra pero significan lo CONTRARIO"* — y, para lo que toca acá, `Status != "Cancelado"`
literal tampoco matchea `"Cancelada"` (femenino, dato real que puede estar cargado), `"cancelado"` en
minúscula, ni `"Cancelado "` con espacio. Un servicio con cualquiera de esas variantes seguiría contando
como vigente bajo el predicado viejo y correría la ventana del viaje mal.

**Predicado que rige D1/D7.1 y el backfill de D5**, dispatcheado por tipo (mismo criterio de dispatch que
`CountsForSupplierDebtByType:87-89`):

- **Vuelo**: `MapFlightStatus` — cancelado si el código IATA normalizado (`Trim().ToUpperInvariant()`) es
  `UN`/`UC`/`HX`/`NO` (`WorkflowStatusHelper.cs:20-34`).
- **Hotel/Traslado/Paquete/Asistencia/genérico**: `MapGenericStatus` — cancelado si
  `Trim().ToLowerInvariant()` **empieza con** `"cancel"` (`:36-46`).

**Nota técnica (EF Core no traduce métodos C# propios dentro de `Where()`)** — ya documentada en el propio
repo (`UpcomingStartCalculator.cs:66-70`: *"el predicado de no-cancelado INLINE... EF Core no traduce
helpers propios dentro de la query"*). Por eso `RecalculateAndPersistAsync`/`ComputeAsync` NO llaman
directo a `WorkflowStatusHelper.MapGenericStatus`/`MapFlightStatus` dentro del `Where`: usan el
EQUIVALENTE inline, traducible a SQL (EF Core sí traduce `.Trim()`, `.ToLower()`/`.ToUpper()` y
`.StartsWith()` a funciones SQL/`LIKE`):

```csharp
// 5 tipos no-vuelo:
!(status ?? "").Trim().ToLower().StartsWith("cancel")
// Vuelo:
!new[] { "UN", "UC", "HX", "NO" }.Contains((status ?? "").Trim().ToUpper())
```

Se agrega un test unitario que compara, para un muestreo de strings de estado reales (incluido `"Cancelada"`,
`"CANCELADO"`, `" Cancelado"`), que el resultado del inline de arriba coincide EXACTO con el de
`WorkflowStatusHelper.MapGenericStatus`/`MapFlightStatus` — así, si alguien las hace divergir en un refactor
futuro, el test lo agarra (ver también M1, D7). **Corrección round 3 (M3)**: la v2 de este documento usaba
`"Cancelacion"` como ejemplo de "falso-positivo a NO excluir" — pero `"cancelacion".StartsWith("cancel")` da
`true`, así que el predicado SÍ la excluiría; no era un contraejemplo válido y se saca sin reemplazo. Hoy NO
existe ningún estado real que empiece con `"cancel"` y NO signifique cancelado — verificado contra
`WorkflowStatusHelperTests` (ninguno de sus casos lo es; los contraejemplos que sí prueba ese archivo, `"A
confirmar"`/`"sin emitir"`/etc., son para la rama `confirm`/`emit` de `MapGenericStatus`, no para `cancel`,
`WorkflowStatusHelper.cs:41-47`). Si el negocio alguna vez crea un estado así (ej. "Cancelación en trámite"
que NO deba tratarse como ya-cancelado), este predicado se revisa entonces — no es un caso a simular hoy con
un dato inventado.

**Alternativa descartada — "calculadas al vuelo" (sin persistir)**: obligaría a reescribir la consulta de
Deudores, el índice de listado y las dos queries de lifecycle como agregaciones en caliente sobre 6 tablas;
no resuelve el problema real (los 4 agujeros de escritura) y agrega costo de lectura permanente a cambio de
nada. Rechazada.

### D2 — Aviso suave (P-20, P-21, PR-12, T-13)

El aviso vive en la RESPUESTA de la mutación que movió la ventana — no en un campo separado que haya que
ir a buscar. Diseño:

- La función compartida de D1 compara `(oldStart, oldEnd)` vs `(newStart, newEnd)` **antes** de persistir.
  Si cambió, arma un texto en criollo, ya listo para pintar (T-13: el front no reconstruye nada, solo
  pinta el string que llega). Borrador de copy — **el texto final lo fija el gate UX** antes de F2:
  *"Con este {tipo de servicio}, el viaje pasa a {empezar/terminar} el {dd/MM} — ¿la fecha de {tipo} está
  bien?"*
- **Campo nuevo y separado**, NO se reutiliza el `Warning` que ya existe hoy en algunos DTOs de servicio
  (ej. `AddServiceAsync` del genérico ya devuelve un `Warning` propio para "el costo supera el precio de
  venta", `ReservaService.cs:4141-4144` — es una alerta DISTINTA; si se comparte el mismo campo, una de las
  dos se pisa en silencio).
- **DECIDIDO (B3 del review, verificado): vive en `ReservaDto`, con persistencia efímera — NO en los 6
  DTOs de servicio.** Se verificó el patrón real del front (`ReservaDetailPage.jsx`, decenas de sitios
  `onSuccess={(options) => fetchReserva(options)}`): el guardado de un servicio **descarta** el DTO que
  devuelve la mutación y dispara un `GET` aparte de `ReservaDto` para refrescar toda la ficha. Poner
  `ScheduleWarning` en los 6 DTOs de servicio sería código muerto — nadie lo lee. Ver D2.1 (a continuación)
  para la columna nueva y la semántica de limpieza (consumo-al-leer, con el caso de dos ediciones seguidas
  documentado ahí).
- **Solo en altas/ediciones/cambios de estado** (caminos que devuelven un DTO). Los **borrados duros**
  (`Delete*Async`, `RemoveServiceAsync`, todos devuelven `204 No Content`) **NO llevan aviso** — decisión
  propia, no pedida por el dueño: no hay "¿esta fecha está bien?" que confirmar cuando lo que se hizo fue
  borrar, y agregarle cuerpo a un 204 para esto solo sería ruido. El recálculo SÍ corre igual (arregla la
  cabecera), simplemente no hay mensaje que mostrar.
- **PR-12 (rastro)**: la función compartida deja un log estructurado (reservaId, ventana vieja, ventana
  nueva, tipo/id del servicio que disparó el cambio) — no un evento de `AuditLog` nuevo por cada recálculo.
  Motivo: el `AuditLog` YA registra la mutación real (se editó tal hotel, tal vuelo) que es el "por qué"
  real; agregar un evento de auditoría por cada recálculo derivado sería ruido desproporcionado, y es el
  mismo criterio que ya usa `BookingService.Reschedule.cs` (`StageRescheduleAudit`, audita el
  desplazamiento, no cada fecha recalculada). Si el reviewer prefiere un evento de auditoría dedicado, es
  un cambio acotado (una llamada más) — se deja anotado como punto a confirmar en la revisión técnica.

### D2.1 — `PendingScheduleWarning` (B3): persistencia efímera en `ReservaDto`

Columna nueva en `"TravelFiles"`: `"PendingScheduleWarning"` (`text`, nullable) +
`"PendingScheduleWarningByUserId"` (`text`, nullable — quién generó el aviso, ver abajo). Semántica:

- **Se escribe** por la función compartida de D1 cuando el recálculo cambia la ventana, en la MISMA
  transacción que la mutación (mismo trigger que ya diseñaba D2) — pisa cualquier valor anterior
  (last-write-wins, ver caso de dos ediciones abajo). Guarda también el `UserId` del actor que disparó el
  cambio — **corrección round 3 (B5)**: ya no viene de un `GetActor()` fijo dentro de la función compartida,
  sino del parámetro `actorUserId` que cada call-site le pasa (D1). **Corrección round 3 (B7)**: **si
  `actorUserId` es `null` — hoy, únicamente `AutoRepairTravelingDatesAsync` (el job de reparación, sin
  `HttpContext`) — la función NO escribe ningún `PendingScheduleWarning`.** El recálculo y la persistencia de
  `StartDate`/`EndDate` corren igual (D1); lo único que se suprime es el aviso, porque no hay a quién
  avisarle. Regla explícita: **actor `null` = sin aviso.**
- **Se lee y se limpia en el mismo `GetReservaByIdAsync`** que arma `ReservaDto` (`ReservaService.cs`
  alrededor de `:799`, donde hoy vive `BuildDatesCoherenceWarningAsync`): si hay un valor pendiente Y el
  `UserId` del caller coincide con `PendingScheduleWarningByUserId`, se copia a `dto.ScheduleWarning` y se
  limpia la columna. **Corrección round 3 (B6): se elimina la cláusula "o el caller es Admin/`view_all`"**
  que tenía la v2 — el reviewer marcó esa excepción como error de categoría: visibilidad de datos (quién
  puede VER la ficha) no es lo mismo que ownership de una notificación efímera (quién generó el aviso y a
  quién le sirve). Consumo estrictamente `PendingScheduleWarningByUserId == callerUserId`. La comparación es
  **null-safe** (B7): `callerUserId != null && callerUserId == pendingUserId` — un pendiente con
  `PendingScheduleWarningByUserId` en `null` (por diseño, nunca lo escribe el job de reparación; solo podría
  aparecer por datos viejos de una corrida previa a este fix) **NUNCA matchea a nadie**, ni siquiera a un
  caller cuyo propio `UserId` viniera `null`. Si el `UserId` NO coincide (o el pendiente no tiene dueño), el
  valor queda intacto para que lo consuma su dueño real en su próximo `GET` (no se le muestra a un tercero
  que está mirando la misma reserva, incluido un Admin distinto del autor, ni se le pisa el aviso al que lo
  generó). **Mecanismo de limpieza (M4)**: `GetReservaByIdAsync` es 100% `AsNoTracking()`
  (`ReservaService.cs:3336-3351`) — la limpieza NO convierte esa lectura en tracked; es un `ExecuteUpdateAsync`
  puntual (EF Core 8) sobre la fila (`SET "PendingScheduleWarning" = NULL, "PendingScheduleWarningByUserId" =
  NULL WHERE "Id" = @id`), separado de la query de lectura. Nota menor aceptada: dos `GET` concurrentes del
  mismo actor pueden ambos leer el pendiente antes de que el primero lo limpie y duplicar el toast en el
  front (double-toast) — sin corrupción de datos, solo un mensaje repetido. Si eso molestara en la práctica,
  el remedio es un `UPDATE ... RETURNING` atómico (lee y limpia en un solo statement) en vez del
  `ExecuteUpdateAsync` + lectura separados de hoy; no se construye ahora, se deja anotado.
- **Vencimiento silencioso**: si el pendiente tiene más de un día de antigüedad cuando se lee, se descarta
  SIN mostrarlo (se limpia igual, pero `dto.ScheduleWarning` queda `null`). Motivo: la decisión (2) pide un
  aviso "EN EL MOMENTO" — mostrarlo días después, fuera de contexto (el usuario ya se fue de la pantalla y
  volvió por otra razón), sería confuso, no útil. No hace falta columna de timestamp nueva: se reutiliza el
  criterio de que la función compartida solo escribe el pendiente cuando cambia algo, y un `GET` disparado
  por CUALQUIER otra acción del mismo día lo va a consumir de todos modos en la práctica (la ventana de
  "más de un día sin que el actor vuelva a mirar su propia reserva recién editada" es un caso borde, no el
  camino feliz).
- **Dos ediciones seguidas antes de que el actor vea la primera (caso pedido explícitamente por el
  review)**: como es last-write-wins, si el actor edita el hotel (aviso A: "el viaje pasa a terminar el
  12/04") y ANTES de que su próximo `GET` lo consuma edita también el vuelo (aviso B: "el viaje pasa a
  empezar el 10/04"), el pendiente que sobrevive es SOLO el B — A se pierde en silencio. **Aceptado a
  propósito**: es un aviso informativo (P-20), no una cola de notificaciones; la ventana YA calculada que
  el usuario ve en la cabecera después del segundo guardado es la correcta en cualquier caso (el dato
  autoritativo nunca se pierde, solo el mensaje de "por qué cambió" del primer paso intermedio). Si el
  reviewer prefiere no perder ninguno, la alternativa es una tabla de cola en vez de una columna — se
  declara como sobre-ingeniería para un aviso no bloqueante y se deja anotada, no construida.

### D3 — "Fecha prometida" (patrón Odoo calculada + prometida)

- Columnas nuevas, nullables, `timestamp with time zone`: `Reserva.PromisedStartDate`,
  `Reserva.PromisedEndDate`. **Nunca las toca el escritor único de D1** — son 100% manuales.
- Se ofrecen las DOS (salida y regreso prometidas), no una sola: la decisión del dueño dice "fecha
  prometida" en singular citando el patrón Odoo (que usa una sola fecha de compromiso), pero acá la
  reserva ya tiene un PAR calculado (Salida/Regreso) — ofrecer un par prometido simétrico es más simple de
  entender que explicar por qué una sola fecha prometida cubre dos calculadas, y no cierra la puerta a que
  el vendedor solo cargue una de las dos (ambas son opcionales e independientes). Es una decisión de
  diseño propia, con default, no una pregunta nueva al dueño (PR-11).
- **Endpoint nuevo, reemplaza al de hoy**: `PATCH /{id}/promised-dates` (retira `PATCH /{id}/dates` y
  `UpdateReservaDatesRequest`). Mismo shape de request (`PromisedStartDate`, `ClearPromisedStartDate`,
  `PromisedEndDate`, `ClearPromisedEndDate`) y **misma cadena de compuertas que usa hoy `UpdateDatesAsync`**:
  primero `ReservaCapacityRules.EnsureReservaDataEditableByStateAsync` (candado por estado, ADR-035,
  reservas cerradas quedan de solo lectura dura) y después `EnsureReservaEditableAsync(...,
  ReservaEditAuthorizationOperations.ReservaDataEdited, ...)` (candado de autorización ADR-020 F4 en
  Confirmada — esto da el rastro de quién/cuándo del PR-12 GRATIS, es el mismo mecanismo que ya deja huella
  hoy para la edición de cabecera).
- **NO reutiliza el guard fiscal CODE-03** (`MutationGuards.GetReservaDatesMutationBlockReasonAsync`): ese
  guard protege el período FISCAL declarado en una factura/voucher ya emitido (§1 de `MutationGuards.cs:188-192`
  — el período aparece en el comprobante). La fecha prometida es una nota interna de planificación, sin rol
  fiscal — bloquearla contra un CAE vivo sería una restricción nueva sin justificación. El guard SIGUE
  vivo para `RescheduleAsync` (`BookingService.Reschedule.cs:59-60`), que sí mueve las fechas reales de los
  servicios.
- **DTO**: `ReservaDto` gana `PromisedStartDate`/`PromisedEndDate` (nullables), junto a `StartDate`/`EndDate`
  (que pasan a ser siempre el valor calculado). Se **retiran** `SuggestedStartDate`/`SuggestedEndDate`
  (`ReservaDto.cs:443-454`, escritos en `ReservaService.cs:3381-3383`): existían solo para "sugerir un valor
  cuando `StartDate` está vacío para precargar el input" — con `StartDate` de solo lectura ya no hay ningún
  input que precargar con una sugerencia; el campo queda muerto y se saca (limpieza, no una funcionalidad
  que se pierde).
- **Dónde se muestra**: la recomendación de arquitectura es junto a Salida/Regreso (de solo lectura) en la
  cabecera de la ficha, editable inline. El layout, la etiqueta exacta y la interacción **pasan
  obligatoriamente por el gate UX** (`ux-ui-disenador` + `docs/ux/guia-ux-gaston.md`, regla del CLAUDE.md
  del repo) antes de tocar el front — esta spec no decide píxeles.

### D4 — Reemplazo del candado invisible + botón "volver a calcular" + arregla "Sacar de viaje"

- Se **elimina** la columna `Reserva.DatesManuallySet`.
- Se agrega `Reserva.NeedsDateRecalculation` (`bool`, default `false`) — el estado VISIBLE que pide la
  decisión (4). Reemplaza DOS cosas a la vez:
  1. El candado invisible de hoy (ya no hay "modo manual" que proteger — el escritor único siempre
     recalcula).
  2. La señal rota de "Sacar de viaje" (§1.5): `CorrectTravelingEntryAsync` deja de poner
     `reserva.StartDate = null` (`ReservaService.cs:2391`) y en su lugar pone
     `reserva.NeedsDateRecalculation = true`.
- **Se apaga sola** como efecto de cualquier corrida exitosa del escritor único de D1 (mismo comportamiento
  que hoy: "corregís el servicio y se arregla sola", `ReservaDto.cs:592`) — no hace falta lógica nueva para
  esto, es gratis por venir del mismo lugar.
- **Endpoint nuevo**: `POST /{id}/recalculate-dates` — mismas dos compuertas que D3 (estado + autorización).
  Llama al escritor único de forma incondicional (aunque la ventana no cambie) y apaga
  `NeedsDateRecalculation`. Es el botón "volver a calcular" de la decisión (4): sirve para el caso de
  "Sacar de viaje" (no hace falta tocar ningún servicio, solo confirmar que la ventana actual ya es
  confiable) y además queda como red de seguridad operativa general — dado que esta misma investigación
  encontró 4 agujeros de escritor único vigentes hoy (§1.3), tener un botón manual de "forzame el
  recálculo" es barato y cubre cualquier caso no previsto, presente o futuro.
- `ReservaDto.IsUnderCorrection` **mantiene su nombre y su contrato de cara al front** (no rompe nada
  existente, T-8) pero cambia su fórmula interna: de `Status == Confirmed && StartDate == null`
  (`ReservaService.cs:3601-3603`) a `Status == Confirmed && NeedsDateRecalculation`. El chip "En
  corrección" que ya pinta el front sigue funcionando igual, ahora respaldado por una bandera honesta en
  vez de una columna de fecha usada como semáforo.

### D5 — Migración (T-8)

Nombres reales verificados contra el código: tabla **`"TravelFiles"`**, columnas **`"StartDate"`**,
**`"EndDate"`**, **`"DatesManuallySet"`** (`Reserva.cs:279-295`, `AppDbContext.cs:503`). **No verificado
contra PROD directamente** (no se corrió una consulta contra la base real) — antes de aplicar, correr
`SELECT column_name FROM information_schema.columns WHERE table_name = 'TravelFiles'` en PROD para
confirmar que no hay drift, mismo hábito que la lección del proyecto sobre SQL crudo.

**Se divide en DOS migraciones, no una** (corrección respecto de la primera versión de este documento —
ver M2, D6.2, para el motivo: el `DROP COLUMN` tiene una ventana real de rotura durante el deploy que el
split elimina). `Adr053_M1` es 100% aditiva; `Adr053_M2` es el único paso destructivo y se deploya aparte.

**`Adr053_M1_TripWindowRecalculatedAndPromisedDates`** (aditiva, sin riesgo de ventana — columnas nuevas
que el código VIEJO simplemente ignora):

1. `AddColumn "PromisedStartDate"` / `"PromisedEndDate"` (nullable, `timestamp with time zone`).
2. `AddColumn "NeedsDateRecalculation"` (`boolean`, `NOT NULL DEFAULT false`).
3. `AddColumn "PendingScheduleWarning"` (`text`, nullable) / `"PendingScheduleWarningByUserId"` (`text`,
   nullable) — D2.1.
4. **Tabla nueva `"Adr053TripWindowBackfillLog"`** (B4 del review — rastro DURABLE, no un `SELECT`
   informativo que nadie guarda): `"Id"` (identity, PK), `"ReservaId"` (int, FK a `"TravelFiles"`,
   `ON DELETE CASCADE`), `"OldStartDate"`/`"OldEndDate"`/`"NewStartDate"`/`"NewEndDate"` (`timestamp with
   time zone`, nullable), `"MigratedAtUtc"` (`timestamp with time zone`, `NOT NULL`). **Se conserva
   permanentemente** (no se exporta-y-dropea): es un log de una sola escritura, una fila por reserva CUYO
   valor cambió (no una fila por cada una de las reservas del sistema), volumen chico y acotado — borrarlo
   después contradice el propósito de PR-12 ("cada cambio... queda en el rastro"). Si en la revisión
   técnica se prefiere exportarlo a un archivo y dropear la tabla, es un cambio menor de este punto, no del
   diseño.
5. **Backfill**, con `migrationBuilder.Sql(...)` — mismo patrón ya usado en este repo para
   `Adr048_M2_AddDerivedStatusColumnsToReserva.cs:81-92` (el SQL vive en una clase estática aparte,
   `Adr053BackfillSql`, para que un test de integración corra el MISMO texto contra Postgres real — ver D7
   más abajo, incluida la advertencia M1 sobre qué prueba y qué NO prueba ese test). El SQL:
   1. Calcula, por reserva, `(NewStart, NewEnd)` = MIN/MAX de fechas de los 6 tipos, con el predicado
      CANÓNICO de D1.1 (`NOT (lower(trim("Status")) LIKE 'cancel%')` para los 5 tipos no-vuelo;
      `upper(trim("Status")) NOT IN ('UN','UC','HX','NO')` para Vuelo — la traducción SQL directa del mismo
      predicado que rige `ComputeAsync` en C#, no el literal case-sensitive de `UpcomingStartCalculator`) y
      las mismas reglas de coalesce que `ReservaScheduleCalculator` (`ArrivalTime ?? DepartureTime`,
      `ReturnDateTime ?? PickupDateTime`, `EndDate ?? StartDate` del paquete) — una reserva sin ningún
      servicio vigente queda con `NULL`/`NULL`.
   2. `INSERT INTO "Adr053TripWindowBackfillLog"` una fila por reserva cuyo `(NewStart, NewEnd)` calculado
      difiere del `"StartDate"`/`"EndDate"` actual (comparación `IS DISTINCT FROM`, para no perderse los
      casos con `NULL`), con `"MigratedAtUtc" = now()`.
   3. `UPDATE "TravelFiles"` con los valores nuevos, **solo** para esas mismas filas (las que no cambiaron
      no se tocan — ni falta hacerlo, ni ensucia el log).
   `"NeedsDateRecalculation"` backfillea en `false` para todas las filas (el chip "En corrección" solo
   tiene sentido hacia adelante, desde una acción NUEVA de "Sacar de viaje").
6. **Down**: dropea las 4 columnas nuevas y la tabla de log. No recrea `DatesManuallySet` (esa columna la
   sigue teniendo la base hasta `Adr053_M2` — ver abajo).

**`Adr053_M2_DropDatesManuallySet`** (destructiva, se deploya en un release APARTE, solo después de
confirmar que TODOS los contenedores `api`/`worker` ya corren el código que dejó de referenciar
`DatesManuallySet`):

1. `DropColumn "DatesManuallySet"`.
2. **Down**: recrea la columna vacía (forward-only, igual criterio que ADR-019 D8 — la política de PROD es
   roll-forward, el `Down` es solo para la cadena de migraciones en desarrollo local).

**Decisión (5) documentada explícitamente**: el dueño eligió recalcular TODAS las reservas existentes
**contra la recomendación del arquitecto** de dejar las filas viejas como están y dejar que se
auto-corrijan solas la próxima vez que alguien las toque (que es como se comportan HOY los otros backfills
de este mismo patrón — aditivos, sin tocar lo que ya había). Acá el backfill SÍ puede **cambiar** un valor
que ya existía (una reserva vieja con un servicio cancelado que hoy cuenta en el MIN/MAX puede terminar con
otra `StartDate`/`EndDate` tras el recálculo) — el riesgo real es el de D6 (impacto en deudores/lifecycle),
no un riesgo de la migración en sí (`Adr053_M1` es determinística, idempotente y aditiva por diseño; el
único paso con riesgo de ventana es `Adr053_M2`, ver D6).

### D6 — Orden de deploy: impacto en deudores/lifecycle, y ventana real del `DROP COLUMN` (M2 del review)

#### D6.1 — Impacto del recálculo masivo en deudores y lifecycle (decisión 5)

El recálculo masivo de la decisión (5) puede mover, de un día para el otro, la `StartDate`/`EndDate`
persistida de cualquier reserva vieja que tuviera un servicio cancelado contando en el viejo MIN/MAX (R8).
Consecuencias reales, no solo teóricas:

- **Vencimientos de Cobranzas** (`PaymentService.Debtors.cs:47`): una reserva puede pasar de "vence en 3
  días" a "vence en 10 días" (o al revés) de un momento a otro, sin que nadie haya tocado nada.
- **Candidatura a los dos jobs automáticos** (`ReservaLifecycleAutomationService.cs:268-274` y `:430-441`):
  una reserva que hoy a la noche iba a promover sola a `Traveling` puede dejar de calificar, o una que no
  iba a calificar puede empezar a hacerlo.

**Ninguno de los dos es un motivo para bloquear el deploy** (es el riesgo que el dueño ya aceptó en la
decisión 5). El log durable de `Adr053TripWindowBackfillLog` (D5) es, de yapa, la herramienta para
explicar cualquier vencimiento o promoción "rara" que aparezca los días siguientes al deploy — antes de
esta corrección, esa pregunta no tenía respuesta con datos.

#### D6.2 — Ventana real de rotura del `DROP COLUMN` (M2 del review, verificado)

**Hallazgo verificado, no hipotético**: `scripts/ops/deploy.sh` corre el contenedor `migrate`
**`--no-deps`** (línea 70: `docker compose up -d --force-recreate --no-deps migrate`) y espera a que
termine (`docker wait travel_migrate`, línea 72) ANTES de tocar `api`/`worker` — esos recién se recrean en
la línea 83 (`docker compose up -d --remove-orphans api worker web whatsapp-bot postgres-backup`). Entre
esas dos líneas, **los contenedores `api`/`worker` VIEJOS siguen corriendo y sirviendo tráfico** contra un
schema que el `migrate` ya cambió. Si `Adr053_M1+M2` fueran una sola migración con `DROP COLUMN
"DatesManuallySet"` adentro, cualquier request que ese código viejo le haga a `Reservas`/`TravelFiles`
durante esa ventana (el modelo compilado de EF todavía espera esa columna) rompe — no es un `500`
aislado, es la ficha de reserva entera (lectura y escritura) caída para todos los usuarios activos durante
la ventana.

**Cuantificación del riesgo**: la ventana dura desde que `migrate` termina (línea 78) hasta que
`api`/`worker` terminan de recrearse y pasan su healthcheck (línea 83 + lo que tarde `check-prod.sh`,
línea 86) — en la práctica, decenas de segundos a un par de minutos, según lo que tarde el pull/build de
las imágenes nuevas y el arranque de los contenedores. No es instantáneo ni previsible de antemano.

**Por qué un guard de horario SOLO no alcanza (verificado)**: `.github/workflows/ci-cd.yml:18-22` —
`concurrency: group: ci-cd-main, cancel-in-progress: false`, con el comentario propio del repo *"Un solo
deploy a la vez... si ya hay un run corriendo, el nuevo QUEDA EN COLA"*. Un push a `main` NO deploya en el
instante del push: si hay un deploy previo corriendo (o la cola tiene más de uno acumulado), el deploy de
esta migración puede terminar ejecutándose bastante después de lo que alguien planeó al mergear — incluido,
por mala suerte, cruzando la medianoche. "Mergear temprano" no es una garantía por sí sola.

**Mitigación primaria (por qué se dividió en D5): separar el `DROP COLUMN` en `Adr053_M2`, un release
APARTE.** Con `Adr053_M1` sola (100% aditiva), la ventana de `migrate --no-deps` deja de ser peligrosa: el
código viejo simplemente ignora las columnas nuevas que no conoce — **no hay rotura posible**, sin importar
la hora ni la cola de CI. `Adr053_M2` (el único `DROP`) recién se sube en un commit/release posterior,
cuando ya está confirmado que TODOS los `api`/`worker` en producción corren el código que dejó de leer
`DatesManuallySet` (es decir, después de que `Adr053_M1` + el código de F1 ya estén deployados y estables
un tiempo). Esto lleva la ventana de riesgo real de "cualquier deploy de esta obra" a "solo el deploy de
`Adr053_M2`", que puede planearse aparte con más margen.

**Mitigación secundaria (defensa en profundidad para EL deploy de `Adr053_M2` puntualmente), runbook**:

1. NO confiar en "pushear antes de las 00:00 ART" a secas (la cola de CI lo invalida, arriba). En su lugar,
   **mergear a `main` y quedarse mirando el run de Actions en vivo** hasta ver el log `"Starting
   application services..."` (deploy.sh línea 80) completo, con margen — no mergear y desentenderse.
2. Si al mergear ya hay un deploy corriendo o encolado (visible en la pestaña Actions), **esperar a que
   termine** antes de mergear el `Adr053_M2`, para no heredar una cola impredecible.
3. Elegir un horario con tráfico bajo Y lejos de las 00:00 ART (evita además la superposición con el job de
   lifecycle, D6.1) — horario sugerido, a confirmar con Gastón: primera hora de la mañana, antes de que
   abra la agencia.
4. Verificar `docker logs --tail=80 travel_migrate` (ya lo hace `deploy.sh:75` en caso de fallo) y
   `check-prod.sh` en verde antes de dar la migración por terminada.
5. Si algo se traba a mitad de camino, el rollback es el de siempre del proyecto (restaurar desde el
   resguardo pre-deploy, ADR-052) — no hay un mecanismo nuevo que aprender para este caso puntual.

### D7 — Plan de tandas

**F1 — Backend** (`backend-dotnet-senior` + `backend-dotnet-reviewer`; toca vencimientos/lifecycle de
reservas y dinero-adyacente ⇒ `security-data-risk-reviewer` por la lista obligatoria del CLAUDE.md del
repo):

1. `ReservaScheduleCalculator.ComputeAsync`: excluir cancelados con el predicado CANÓNICO de D1.1 (NO el
   literal de `UpcomingStartCalculator`) — reemplazo formal de ADR-019 R8. Actualizar el comentario propio
   (`:17-24`) y el de `UpcomingStartCalculator` que lo cita (`:12-23`) y el de
   `AutoTransitionConfirmedToTravelingAsync` (§1 de ADR-019, "tres definiciones de cuándo empieza" pasa a
   ser un caso menos divergente — ver §4).
2. Extraer `RecalculateAndPersistAsync` compartida (D1) y migrar:
   - Los 16 sitios existentes de `BookingService` + `Reschedule.cs`.
   - Los 3 agujeros de `ReservaService` (`AddServiceAsync`, `UpdateServiceAsync`, `RemoveServiceAsync` del
     genérico/unificado).
   - `AutoRepairTravelingDatesAsync` (job de reparación) — llama con `actorUserId: null, actorUserName: null`
     (B5/B7, D1, D2.1): recalcula y persiste igual, pero nunca escribe `PendingScheduleWarning`.
   - **`QuoteService.ConvertToFileCoreAsync`** (B1 del review, §1.3): sacar la asignación directa de
     `StartDate`/`EndDate` desde `quote.TravelStartDate`/`TravelEndDate` (`:363-364`), resolver el actor
     inline desde `_httpContextAccessor` (D1 — no existe helper propio en `QuoteService` hoy) y llamar a
     `RecalculateAndPersistAsync(_db, file.Id, actorUserId, actorUserName, ct)` después del `foreach` de
     creación de servicios (`:569`) y antes del `SaveChangesAsync`/`CommitAsync` finales (`:586-587`) —
     dentro de la MISMA transacción `Serializable` que ya abre el método (`:331`).

   Retirar `BookingService.RecalculateReservationScheduleAsync` (privado, se reemplaza por la función
   compartida).
3. Entidad `Reserva`: agregar `PromisedStartDate`/`PromisedEndDate`/`NeedsDateRecalculation`/
   `PendingScheduleWarning`/`PendingScheduleWarningByUserId` (D2.1); sacar `DatesManuallySet` (recién en
   `Adr053_M2`, ver punto 4).
4. **Dos migraciones** (D5): `Adr053_M1` (columnas nuevas + tabla `Adr053TripWindowBackfillLog` +
   `Adr053BackfillSql` con su test de integración dedicado que compara SQL vs C# — ver también el caveat de
   M1 en la lista de tests abajo) en esta misma tanda; `Adr053_M2` (el `DROP COLUMN "DatesManuallySet"`)
   queda preparada en el mismo PR pero **su deploy se agenda aparte** (D6.2) — no se sube al mismo release
   que el resto de F1.
5. `CorrectTravelingEntryAsync`: cambiar `StartDate = null` por `NeedsDateRecalculation = true` (D4).
6. `ReservaDto.IsUnderCorrection`: nueva fórmula (D4).
7. Endpoints: `PATCH /{id}/promised-dates` (reemplaza `PATCH /{id}/dates`), `POST /{id}/recalculate-dates`
   (nuevo). Retirar `UpdateDatesAsync`/`UpdateReservaDatesRequest`.
8. `ScheduleWarning` en `ReservaDto`, leído/limpiado desde `PendingScheduleWarning` en `GetReservaByIdAsync`
   (D2.1 — decidido, ya no toca los 6 DTOs de servicio).
9. Limpieza: retirar `SuggestedStartDate`/`SuggestedEndDate` de `ReservaDto` y su único escritor
   (`ReservaService.cs:3381-3383`); sacar la llamada a `GetReservaDatesMutationBlockReasonAsync` de
   `UpdateDatesAsync` (que desaparece) manteniendo el guard vivo para `RescheduleAsync`; corregir el
   comentario desactualizado de `ReservaService.cs:2389-2390` (§1.4); corregir el comentario de
   `BookingService.Reschedule.cs:169-176` (ya no cita el viejo MIN-con-cancelados como motivo).

Tests clave de F1 (backend, unit InMemory salvo el de integración del backfill):

- **Predicado canónico (D1.1, B2)**: para cada uno de los 6 tipos, un servicio en estado `"Cancelado"`
  queda excluido — Y TAMBIÉN `"Cancelada"` (femenino, caso explícito pedido por el review), `"CANCELADO"`
  (mayúsculas), `" Cancelado"` (espacio). **Corrección round 3 (M3)**: se saca el caso negativo
  `"Cancelacion solicitada"` que traía la v2 — `"cancelacion".StartsWith("cancel")` es `true`, así que ese
  ejemplo se auto-contradecía (el propio predicado lo excluiría, no era un caso negativo válido). Hoy no
  hay ningún estado real que empiece con "cancel" y no sea cancelado (D1.1); si el negocio crea uno en el
  futuro, se agrega el caso negativo entonces. Test de igualdad: el predicado inline usado en LINQ produce
  el MISMO resultado que `WorkflowStatusHelper.MapGenericStatus`/`MapFlightStatus` para un muestreo de
  strings reales.
- `ReservaScheduleCalculator`: un servicio cancelado de cada uno de los 6 tipos queda EXCLUIDO del MIN/MAX
  (caso borde: el servicio cancelado era el único, la reserva queda con `Start`/`End` en `null`).
- Escritor único: los 16+4+1 sitios recalculan en la MISMA transacción que su mutación (ya sea con un test
  de humo por sitio, ya sea con un test de contrato que verifique que ningún sitio nuevo persiste
  `Servicios`/`HotelBookings`/etc. sin pasar por la función compartida — a definir con el reviewer).
  Regresión explícita de los 4 agujeros: alta/edición/borrado de un servicio genérico, borrado por
  `DELETE /api/reservas/services/{id}`, y **conversión de cotización histórica
  (`ConvertToFileAsync`) con items sin `RateId`/con fallback propio** actualizan `StartDate`/`EndDate` de
  forma consistente con los servicios reales creados (no con la cabecera del presupuesto).
- Aviso suave: agregar un servicio que corre la ventana ⇒ `ReservaDto.ScheduleWarning` no vacío con el
  texto esperado en el PRÓXIMO `GET` del mismo actor; agregar un servicio que NO corre la ventana ⇒
  `null`; borrar un servicio ⇒ sin aviso (pero `StartDate`/`EndDate` sí se actualizan).
- **`PendingScheduleWarning` (D2.1, B3)**: se limpia en el primer `GET` que lo entrega (segundo `GET`
  inmediato del mismo actor ⇒ `null`); un `GET` de OTRO usuario (o de un caller sin `UserId` que no
  matchea) NO lo consume ni lo limpia; **caso nuevo round 3 (B6)**: un `GET` de un **Admin distinto del
  autor** tampoco lo consume ni lo limpia — ya no hay excepción de alcance por rol, solo
  `PendingScheduleWarningByUserId == callerUserId` (null-safe, B7); dos ediciones seguidas del MISMO actor
  antes de su próximo `GET` ⇒ solo sobrevive el aviso de la SEGUNDA edición (regresión explícita del caso
  "se pisa", documentado en D2.1); un pendiente con más de un día de antigüedad se limpia pero NO se
  muestra; **caso nuevo round 3 (B7)**: el job de reparación (`AutoRepairTravelingDatesAsync`, actor `null`)
  recalcula y persiste `StartDate`/`EndDate` pero NUNCA escribe `PendingScheduleWarning` — el siguiente
  `GET` de cualquier actor no encuentra ningún pendiente que atribuir a ese recálculo.
- `PATCH /{id}/promised-dates`: setea/borra cada campo independientemente; NUNCA toca `StartDate`/`EndDate`;
  respeta el candado de estado (ADR-035) y el de autorización (ADR-020 F4); rechazado si la reserva está
  Cerrada/Perdida/Anulada/PendingOperatorRefund.
- `POST /{id}/recalculate-dates`: fuerza el recálculo incluso sin cambios; apaga `NeedsDateRecalculation`.
- `CorrectTravelingEntryAsync`: deja `NeedsDateRecalculation = true`, NO toca `StartDate`; el siguiente
  guardado de un servicio (o el botón "volver a calcular") la apaga sola.
- `IsUnderCorrection`: `Confirmed + NeedsDateRecalculation=true` ⇒ `true`; cualquier otro estado ⇒ `false`
  aunque la marca esté prendida (regresión: antes dependía de `StartDate == null`, ahora no).
- **Backfill (`Adr053BackfillSqlIntegrationTests`, Postgres real)**: el SQL de la migración produce
  exactamente el mismo `(NewStart, NewEnd)` por reserva que `ReservaScheduleCalculator.ComputeAsync` en C#
  (YA CORREGIDO con el predicado canónico de D1.1), para un set de reservas armado a mano que cubra los 6
  tipos, con y sin cancelados — incluido un caso `"Cancelada"` femenino — y una reserva sin ningún servicio
  vigente (⇒ `NULL`/`NULL`). **Además**: se inserta exactamente una fila en
  `Adr053TripWindowBackfillLog` por cada reserva cuyo valor cambió, con `Old*`/`New*` correctos, y NINGUNA
  fila para las que no cambiaron.
  **(M1 del review — advertencia explícita para quien lea este test más adelante)**: este test es un
  oráculo SQL↔C#, NO un test de que el predicado sea correcto para el negocio — si alguien apunta el lado
  C# de la comparación a una implementación ingenua (case-sensitive, `Status != "Cancelado"` literal), el
  test daría VERDE comparando dos versiones igualmente equivocadas entre sí. La cobertura real del negocio
  (que "Cancelada" cuente como cancelado, que "A confirmar" NO cuente como cancelado) la dan los tests del
  punto "Predicado canónico" de arriba, contra `WorkflowStatusHelper` — no este test de integración. **Un
  test verde acá NUNCA debe leerse como "el predicado es correcto"**, solo como "el SQL no divergió del C#
  que ya se probó por separado".
- Deudores/lifecycle (regresión, no nueva lógica): `PaymentService.Debtors` y los dos jobs de
  `ReservaLifecycleAutomationService` siguen leyendo `StartDate`/`EndDate` igual que hoy — ningún cambio de
  contrato ahí, solo el VALOR que reciben cambia (cubierto por los tests de arriba).

**F2 — Frontend** (**gate UX obligatorio primero** — `ux-ui-disenador` con `docs/ux/guia-ux-gaston.md`,
regla del repo; después `frontend-senior` + `frontend-reviewer`, verificando cumplimiento de esa guía):
Salida/Regreso pasan a mostrarse de solo lectura; UI de "fecha prometida" (par de campos, editables,
opcionales); chip "En corrección" (ya existe, solo cambia lo que lo prende, sin cambio de UI necesario);
botón "volver a calcular"; toast/banner no bloqueante para `ReservaDto.ScheduleWarning` (P-20: informa, no
frena; llega ya calculado en el `GET` normal de la ficha, sin fetch nuevo); retirar cualquier UI que hoy
edite Salida/Regreso directamente.

**F3 — Verificación**: `data-exposure-reviewer` (obligatorio por el CLAUDE.md del repo — esta obra agrega
campos a respuestas de API y un mensaje nuevo de cara al usuario); grep final de
`DatesManuallySet\|SuggestedStartDate\|SuggestedEndDate\|UpdateReservaDatesRequest` = solo historia en
ADRs/migraciones (una vez que `Adr053_M2` ya se deployó); build + suites verdes; smoke en el entorno de
Gastón con el conteo pre/post del backfill y una lectura de `Adr053TripWindowBackfillLog` (D6.1); **repetir
el recordatorio de M1**: el test de oráculo del backfill (arriba) no reemplaza los tests del predicado
canónico contra `WorkflowStatusHelper` — el reviewer de F1 debe confirmar que AMBOS corrieron, no solo el
del backfill en verde.

## 3. Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| `StartDate`/`EndDate` calculadas al vuelo (vista/subquery), sin persistir | Rompe índices y ordenamientos existentes (Deudores, listado); el problema real es el escritor múltiple (§1.3), no la persistencia (D1). |
| Fusionar `ReservaScheduleCalculator` y `UpcomingStartCalculator` en una sola función | Formas distintas (una-reserva-con-fin vs lote-solo-inicio-con-optimización de tope); fusionarlas complica ambas por una duplicación que, tras esta obra, queda reducida a "mismo predicado, forma distinta" — no a código idéntico. Se documenta el predicado compartido en vez de forzar una abstracción. |
| Reusar el campo `Warning` que ya existe en algunos DTOs de servicio para el aviso de ventana | Pisaría en silencio otros avisos ya existentes (ej. costo > precio de venta en el genérico, `ReservaService.cs:4141-4144`) — dos avisos, un solo slot, uno se pierde. |
| "Fecha prometida" como una sola columna (fiel al singular de Odoo) | La reserva ya tiene un PAR calculado (Salida/Regreso); una sola fecha prometida obliga a decidir arbitrariamente si prometé la salida o el regreso. Par simétrico, cada una opcional e independiente, es más simple de explicar y de construir. |
| Dejar el aviso suave solo para servicios que YA hoy llaman a `RecalculateReservationScheduleAsync`, sin cerrar los agujeros de `ReservaService`/`QuoteService` | Bajo la decisión (1) la cabecera es AUTORITATIVA — dejar 4 caminos de escritura sin el escritor único deja datos derivados incorrectos en producción (viola F-2 directamente, no es un detalle cosmético). |
| Predicado "no cancelado" literal (`Status != "Cancelado"`, el de `UpcomingStartCalculator` tal cual) | Comparación case-sensitive; no matchea `"Cancelada"` (femenino), minúsculas ni espacios — el propio repo ya documentó ese tipo de bug en `WorkflowStatusHelper.cs:41-45` (B2 del review). |
| `DROP COLUMN "DatesManuallySet"` en la misma migración que las columnas nuevas | Ventana real de rotura entre el commit de `migrate` y el recreate de `api`/`worker` (`deploy.sh:70-83`, verificado) — un guard de horario solo no alcanza porque el CI encola deploys (`ci-cd.yml:18-22`). Separado en `Adr053_M2`, deployada aparte (M2 del review, D6.2). |
| Backfill (D5) fuera de la migración, como servicio C# separado registrado en `DatabaseSchemaUpdater` (como ADR-021/022/025) | Ese patrón es para backfills que deben re-chequearse en CADA arranque (`NeedsBackfillAsync` barato). Acá es una corrección de una sola vez, explícitamente pedida para HOY (decisión 5) — el patrón de migración con SQL embebido (`Adr048_M2`) es más simple, y el marcador de "ya corrió" lo da gratis `__EFMigrationsHistory`. |
| Dejar `Reserva.DatesManuallySet` pero resignificarlo | La decisión (4) del dueño es explícita: reemplazar el candado invisible por un estado VISIBLE con botón — resignificar la columna vieja sin cambiar su naturaleza (invisible, sin botón) no cumple la decisión. |

## 4. Consecuencias

**Positivas**

- Cierra una violación de T-7 que **ya existe hoy** (4 caminos de escritura sin escritor único, más un job
  de reparación con su propia copia de la lógica) — no es deuda nueva de esta obra, es deuda vieja que la
  obra obliga a pagar porque la cabecera pasa a ser autoritativa.
- El comentario de ADR-019 que nombraba "tres definiciones de cuándo empieza la reserva" como deuda (R8,
  §6 de ese ADR) **se reduce**: el cálculo de cabecera y el de "Próximos inicios" (`UpcomingStartCalculator`)
  pasan a compartir predicado (ambos excluyen cancelados) — la única definición que sigue siendo distinta
  es el job de lifecycle, que ahora lee el MISMO valor que el cálculo en vivo excluye-cancelados (antes
  leía el valor CON cancelados). Esto es un efecto colateral positivo, no el objetivo de esta obra — se
  deja anotado para que el reviewer lo confirme.
- "Sacar de viaje" deja de depender de un valor de fecha usado como semáforo (`StartDate == null`) y pasa a
  tener una bandera con nombre propio — más fácil de entender y de testear.

**Negativas**

- Superficie de cambio más ancha de lo que el pedido original sugería: 4 agujeros de escritor único
  (`ReservaService` ×3 + `QuoteService` ×1) + 1 job de reparación duplicado, encontrados en la
  investigación, que hay que cerrar para que la decisión (1) sea cierta (no cosmética). Están
  explícitamente separados en §1.3/§1.5 para que se puedan revisar como hallazgos, no como alcance oculto.
- Dos migraciones y dos ventanas de deploy en vez de una (D5/D6.2) — más coordinación operativa que un
  único `Adr053_M1` con todo adentro, a cambio de eliminar una rotura real de disponibilidad.
- Una tabla nueva permanente (`Adr053TripWindowBackfillLog`, D5) y dos columnas efímeras
  (`PendingScheduleWarning`/`PendingScheduleWarningByUserId`, D2.1) — costo de esquema real, ambos exigidos
  por PR-12/B3/B4 del review, no gratuitos.
- El recálculo masivo (decisión 5) puede mover vencimientos de Cobranzas y candidaturas de los dos jobs
  automáticos de un día para el otro — riesgo aceptado por el dueño, mitigado con orden de deploy (D6.1) y
  ahora explicable con datos (`Adr053TripWindowBackfillLog`), no eliminado.

**Riesgos declarados**

- **No verificado**: el conteo real de filas de `TravelFiles` en PROD cuya `StartDate`/`EndDate` va a
  cambiar tras el backfill (D6.1 recomienda medirlo antes de aplicar, sin bloquear; el log de D5 lo deja
  además registrado después).
- Last-write-wins de `PendingScheduleWarning` (D2.1): dos ediciones seguidas del mismo actor antes de su
  próximo `GET` pierden el primer mensaje. Aceptado a propósito para no construir una cola de
  notificaciones por un aviso no bloqueante — documentado, no oculto.
- Si el reviewer prefiere un evento de `AuditLog` dedicado por cada recálculo (en vez del log estructurado
  propuesto en D2) o prefiere exportar-y-dropear `Adr053TripWindowBackfillLog` en vez de conservarla
  (D5), son cambios acotados, sin impacto en el resto del diseño.

## 5. Rollback

- **De la obra completa**: revertir el commit de `Adr053_M1` + su código deja el comportamiento de hoy
  (cabecera editable a mano, incluye cancelados) — `Adr053_M1` es aditiva, así que un rollback de código
  contra ella es seguro (T-8). Si `Adr053_M2` (el `DROP COLUMN`) ya se deployó, el rollback de código debe
  ir junto con su `Down` (recrea `DatesManuallySet` vacía) — el split de D5/D6.2 hace que ESTE sea el único
  paso que necesita coordinación fina entre código y migración; `Adr053_M1` no.
- **Del backfill** (D5): no hay forma de "deshacer" el recálculo sin perder la corrección (sería volver a
  incluir cancelados a propósito). Si algo sale mal, el camino es restaurar desde el resguardo
  pre-deploy (mecanismo ya existente del proyecto, ADR-052), no un `Down` de datos —
  `Adr053TripWindowBackfillLog` queda como evidencia de qué cambió, incluso si se decide restaurar.

---

## Resumen (10 líneas, decisiones de diseño + qué le pediría confirmar al dueño)

1. Un solo escritor persistido (`RecalculateAndPersistAsync`), no columnas calculadas al vuelo — reusa
   índices/queries existentes.
2. `ReservaScheduleCalculator` pasa a excluir cancelados con el predicado CANÓNICO de
   `WorkflowStatusHelper` (`StartsWith("cancel")` normalizado, no el literal case-sensitive de
   `UpcomingStartCalculator`, corregido en round 2 — B2) — reemplazo formal de ADR-019 R8.
3. Hallazgo: HOY ya hay **4** caminos de escritura (alta/edición/borrado del servicio genérico + borrado
   unificado de los 6 tipos + `QuoteService.ConvertToFileCoreAsync`, este último sumado en round 2 — B1)
   que NUNCA recalculan la cabecera, más un job de reparación con su propia copia del cálculo — se cierran
   como parte obligatoria de esta obra, no son alcance nuevo inventado.
4. Aviso suave (P-20) vive en `ReservaDto.ScheduleWarning` con persistencia efímera
   (`PendingScheduleWarning`, consumo-al-leer, decidido en round 2 — B3, verificado que el front descarta
   la respuesta de guardado y refetchea) — NO en los 6 DTOs de servicio como proponía la v1.
5. Aviso NO aplica a borrados (204 sin cuerpo, sin ambigüedad que confirmar) — decisión propia, no pedida.
6. "Fecha prometida" = PAR de columnas (`PromisedStartDate`/`PromisedEndDate`), no una sola — más simple
   que forzar el singular de Odoo sobre una reserva que ya tiene un par calculado.
7. Candado invisible → `NeedsDateRecalculation` visible + botón "volver a calcular"; ese mismo mecanismo
   arregla, de yapa, la señal rota de "Sacar de viaje" (que hoy usa `StartDate = null`, inválido bajo
   solo-lectura).
8. Backfill (decisión 5 del dueño, contra mi recomendación) va como SQL embebido en `Adr053_M1`, con el
   predicado canónico (B2) y con rastro DURABLE en `Adr053TripWindowBackfillLog` (round 2 — B4, reemplaza
   el `SELECT` informativo de la v1); el test de integración que lo compara contra el C# es un oráculo de
   consistencia, no de corrección del predicado (round 2 — M1, anotado explícitamente en F3).
9. **`DROP COLUMN "DatesManuallySet"` separado en `Adr053_M2`, deployada aparte** (round 2 — M2): la v1
   proponía una sola migración; se verificó que `deploy.sh` corre `migrate --no-deps` antes de recrear
   `api`/`worker` (ventana real de rotura) y que el CI encola deploys (`cancel-in-progress: false`), así
   que un guard de horario solo no alcanza — el split a una migración 100% aditiva elimina el riesgo en vez
   de solo mitigarlo con horario.
10. **Nada de esto requiere una decisión NUEVA del dueño.** Los hallazgos (4 agujeros de escritor único,
    comentario desactualizado, señal rota de "Sacar de viaje", y ahora los 4 puntos del review) se
    resuelven con defaults propios, documentados con su razón. Único trade-off explícito, no una pregunta
    pendiente: `PendingScheduleWarning` es last-write-wins (dos ediciones seguidas antes de verla pierden
    la primera) — aceptado para no construir una cola de notificaciones por un aviso no bloqueante (D2.1).

## Changelog

**Round 3 (2026-08-13)** — cierra B5-B7 y M3-M4 del `software-architect-reviewer` (round 2), verificados
contra código real antes de aplicar:

- **B5**: la firma de la función compartida cambia a `RecalculateAndPersistAsync(db, reservaId,
  actorUserId, actorUserName, ct)`, con actor **nullable**. Cada call-site resuelve su propio actor:
  `BookingService.GetActor()` (`:2561-2567`), `ReservaService.ResolveAuditActor()` (`:101-108`), y para
  `QuoteService` — verificado que hoy NO existe ningún helper de actor — resolución inline. El aviso
  (`PendingScheduleWarning`) se sigue escribiendo DENTRO de la función compartida, ahora condicionado a
  `actorUserId != null` (D1, D2.1).
- **B6**: se elimina la cláusula "o el caller es Admin/`view_all`" del consumo de
  `PendingScheduleWarning` — el reviewer la marcó como error de categoría (visibilidad de datos ≠ ownership
  de una notificación efímera). Consumo estrictamente `PendingScheduleWarningByUserId == callerUserId`
  (D2.1, D7).
- **B7**: el job de reparación (`AutoRepairTravelingDatesAsync`) pasa actor `null` — recalcula y persiste
  igual, pero JAMÁS escribe `PendingScheduleWarning` ("actor null = sin aviso", regla explícita en D2.1). La
  comparación de consumo es null-safe: `callerUserId != null && callerUserId == pendingUserId` (D2.1, D7).
- **M3**: se saca el caso de test `"Cancelacion solicitada"` de D1.1 y de la lista de tests de F1 —
  `"cancelacion".StartsWith("cancel")` es `true`, el ejemplo se auto-contradecía. Nota agregada: hoy no
  existe ningún estado real que empiece con "cancel" y no signifique cancelado (verificado contra
  `WorkflowStatusHelperTests`); si el negocio crea uno, se revisa entonces (D1.1, D7).
- **M4**: agregada en D2.1 la mecánica de limpieza — `GetReservaByIdAsync` es 100% `AsNoTracking()`
  (`ReservaService.cs:3336-3351`), así que la limpieza de `PendingScheduleWarning` usa un `ExecuteUpdateAsync`
  puntual (EF Core 8) sobre la fila, sin convertir la lectura en tracked. Anotada la nota menor del reviewer:
  dos `GET` concurrentes del mismo actor pueden duplicar el toast (sin corrupción); el remedio, si hiciera
  falta, es un `UPDATE ... RETURNING` atómico — no construido ahora.
- **Citas corregidas** (drift por la tanda de PDF del mismo día): `ReservaService.cs:3601-3603` →
  `:3704-3706` (asignación de `IsUnderCorrection`); `ReservaDto.cs:587-595` → `:594-602` (su XML-doc,
  `src/TravelApi.Application/DTOs/ReservaDto.cs`).

**Round 2 (2026-08-12)** — cierra los 4 bloqueantes y las 2 mejoras del `software-architect-reviewer`
(round 1), todos verificados contra código real antes de aplicar:

- **B1**: sumado un 4to agujero de escritor único — `QuoteService.ConvertToFileCoreAsync`
  (`QuoteService.cs:329-373`, transacción `Serializable`) asigna `StartDate`/`EndDate` directo de la
  cabecera del presupuesto y nunca recalcula desde los servicios que arma con fallbacks propios. Entra al
  plan F1 (punto 2) llamando a `RecalculateAndPersistAsync` dentro de la misma transacción, antes del
  commit (§1.3, D1).
- **B2**: el predicado "vigente" pasa del literal case-sensitive de `UpcomingStartCalculator` al canónico
  de `WorkflowStatusHelper.MapGenericStatus`/`MapFlightStatus` (`StartsWith` normalizado), con la nota
  técnica de por qué el C# del helper no se puede invocar directo dentro de un `Where()` de EF Core y cuál
  es el inline traducible que sí se usa. Afecta D1 (nueva sub-sección D1.1), el SQL del backfill (D5) y se
  agregó el caso `"Cancelada"` femenino a los tests de F1 (§2, D5, D7).
- **B3**: decidido en el documento (no dejado "a criterio de F2"): `ScheduleWarning` vive en `ReservaDto`
  con persistencia efímera (`PendingScheduleWarning`/`PendingScheduleWarningByUserId`, D2.1 nueva),
  verificado contra el patrón real del front (`fetchReserva` descarta la respuesta de guardado). Semántica
  de limpieza al leer, alcance por actor, vencimiento silencioso a las 24 h, y el caso de dos ediciones
  seguidas antes de verla documentado como last-write-wins aceptado (D2, D2.1, D7).
- **B4**: el backfill deja rastro DURABLE — tabla nueva `Adr053TripWindowBackfillLog` (una fila por reserva
  que cambió, conservada permanentemente), reemplaza el `SELECT` informativo de la v1 (D5).
- **M1**: anotado explícitamente en F1/F3 que el test de oráculo SQL↔C# del backfill es tautológico
  respecto del predicado — solo prueba consistencia interna; la cobertura real del negocio depende de los
  tests contra `WorkflowStatusHelper` de B2 (D7).
- **M2**: la migración se divide en `Adr053_M1` (100% aditiva) y `Adr053_M2` (el único `DROP COLUMN`,
  deployada en un release aparte). Se cuantificó la ventana real de rotura con `deploy.sh:60-83`
  (`migrate --no-deps` antes de recrear `api`/`worker`) y se verificó que el guard de horario solo no
  alcanza por la cola de CI (`ci-cd.yml:18-22`, `cancel-in-progress: false`) — se agregó el runbook de D6.2
  (D5, D6).

**Round 1 (2026-08-12)** — versión inicial: modelo de escritor único persistido, aviso suave P-20/P-21,
"fecha prometida" como par de columnas, reemplazo del candado invisible por `NeedsDateRecalculation` +
botón, backfill de la decisión (5) como SQL embebido en una sola migración, orden de deploy antes de las
00:00 ART.
