# ADR-019 — "Próximos inicios": aviso automático por reserva con descarte manual (reemplaza "fechas límite")

**Estado:** PROPUESTO — Round 3 (puntos del re-review resueltos: B1-bis, B2-nuevo, M-A, M-B + menores; las 2 preguntas de producto del round 3 respondidas por el dueño el 2026-06-06). Sin preguntas abiertas. Pendiente: lo visual pasa por el gate UX obligatorio antes de F2.
**Mandato:** guía UX Rondas 8 y 9 (`docs/ux/guia-ux-gaston.md:109-119`), decisiones FIRMES del dueño + respuestas Round 2 (§7).
**Reemplaza:** el bucket `serviceDeadlines` y las fechas límite manuales de ADR-017 F1.4 (commit `be6e699`), nunca prendidos en prod (flag `EnableServiceDeadlineAlerts` OFF). **Corrección Round 2 (B2):** las migraciones Adr017 M1-M5 SÍ están aplicadas en VPS desde el 2026-06-06 — la premisa "sin aplicar" del round 1 era falsa. Las columnas de deadline manual pueden contener datos de prueba; Gastón confirmó explícitamente borrarlos ("Sí, borralas").
**Fecha:** 2026-06-06 (round 1) / 2026-06-06 (round 2) / 2026-06-06 (round 3). HEAD de referencia: `823cd71`.

## 1. Contexto

Hoy (flag ON, nunca prendido) la campanita muestra fechas límite **manuales** por servicio (Hotel/Paquete `OperatorPaymentDeadline`, Aéreo `TicketingDeadline`) cargadas en la ficha, y la columna "Avisos" de la fila muestra la pill por esas mismas fechas (`AlertService.ComputeServiceDeadlinesAsync`, `DeadlinePill.jsx`). Gastón decidió (Ronda 8) que el aviso pasa a ser **automático para todas las reservas vendidas** — X días antes de que empiece el **primer servicio** — y que el campo manual **desaparece** de las fichas. Ronda 9 fija: UN aviso POR RESERVA en la campanita, botón **"Listo"** que lo apaga a mano, texto "⏰ Empieza el {dd/MM} (en {N} días)" ámbar / "Empieza HOY {dd/MM}" rojo, columna "Avisos" de la fila QUEDA pero **por servicio y calculada sola**, nombre "Próximos inicios", y los textos nuevos de Configuración. Cancelado nunca avisa (Ronda 5). Visibilidad: cada vendedor las suyas, admin todas (igual que hoy, fail-closed).

**Hechos verificados en código (no supuestos):**
- `Reserva.StartDate` se recalcula como MIN de fechas de servicios (`ReservaScheduleCalculator.ComputeAsync`) pero **incluye servicios cancelados** (no filtra Status) → NO sirve directo como "primer inicio" del aviso (P1 exige excluir cancelados).
- **(B1, nuevo round 2)** `Reserva.StartDate` además es **editable a mano**: `ReservaService.UpdateDatesAsync` (ReservaService.cs:213-216) permite `ClearStartDate` → null y valores manuales arbitrarios **sin recálculo** desde los servicios. Conclusión: el StartDate persistido NO es confiable ni siquiera como prefiltro excluyente — un prefiltro `StartDate != null && StartDate <= window` puede SILENCIAR avisos de reservas con StartDate borrado o atrasado a mano mientras sus servicios sí caen en ventana. **(B1-bis, round 3):** como es editable en AMBAS direcciones (adelantable y atrasable), tampoco la variante null-tolerante es segura — conclusión final: NO hay prefiltro de fecha posible sobre este campo (ver D2).
- **(B2-nuevo, round 3) Job de lifecycle:** `ReservaLifecycleAutomationService.AutoTransitionConfirmedToTravelingAsync` (ReservaLifecycleAutomationService.cs:138-144) corre vía Hangfire `Cron.Daily(3)` = 03:00 UTC = **00:00 ART** (Program.cs:814-817) y promueve `Confirmed → Traveling` cuando el **StartDate persistido** (el MIN CON cancelados de `ReservaScheduleCalculator`, además editable a mano) es `<= hoy`. Consecuencia verificada: el mismo día del inicio, cuando el vendedor abre el sistema la reserva ya suele estar en `Traveling` — si el conjunto elegible del aviso fuera solo `{Sold, Confirmed}`, el aviso rojo "Empieza HOY" no se vería nunca. Resuelto en D2; riesgo y deuda preexistente del job en R8.
- Fechas de inicio por tipo: Hotel `CheckIn`, Paquete `StartDate`, Aéreo `DepartureTime`, Traslado `PickupDateTime`, Asistencia `ValidFrom`, genérico `DepartureDate`. El front ya las normaliza a un campo único `date` por servicio (`reservationServiceModel.js:118-207`).
- Cancelación por tipo: Hotel/Paquete/Traslado/Asistencia/genérico `Status != "Cancelado"`; Aéreo `Status NOT IN (UN, UC, HX, NO)` — mismos predicados que el bucket actual.
- `ServiceDeadlineAlertDays` (1-60, default 7) y `EnableServiceDeadlineAlerts` ya existen en `OperationalFinanceSettings` (migración Adr017_M3). El front **NO** conoce los días: `OperationalFlagsResponse` tiene regla dura "solo booleanos" con test de shape por reflection.
- `AlertCallerContext` + filtro ownership (`ResponsibleUserId == caller.UserId`, fail-closed con UserId null) ya implementados y revisados en F1.4. `AlertsContext.jsx` poll cada 30 s para todo autenticado; **ante error de fetch NO resetea el estado: conserva el último payload bueno** (AlertsContext.jsx:33-35, el catch solo loguea).
- **(Q3, nuevo round 2) "Titular":** `Reserva` NO tiene campo titular ni `Passenger` tiene flag de titular. La convención YA existente en el código es **`reserva.Payer?.FullName` con label "Titular"** (VoucherService.cs:69 y :1187, fallback "---"); `Reserva.Payer` está comentado como "Payer/Main Client". El aviso usa esa misma convención (ver D1).
- **(M1, nuevo round 2) Ownership de endpoints por reserva:** la convención del repo es `[RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]` (ReservasController.cs:64 y siguientes). Semántica verificada del filtro (RequireOwnershipAttribute.cs): sin identidad o sin UserId → **401** (:45-56); rol Admin → bypass (:59-62); permiso `reservas.view_all` → bypass (:66-77); no-owner → **403** (:104-110). `OwnershipResolver.IsOwnerAsync` devuelve `false` tanto para **reserva inexistente** como para `ResponsibleUserId` null (OwnershipResolver.cs:64-68) → el 403 es **uniforme** entre "existe y no es tuya" e "no existe": no filtra existencia.
- **(M3, nuevo round 2) Timezone — supuesto load-bearing:** las horas de vuelo/traslado se guardan como **hora-de-pared con Kind=Utc** (sin conversión de instante): `BookingService.NormalizeAirportWallClock` (BookingService.cs:383-388) toma los componentes de pared tal cual y solo marca Kind=Utc. Excepciones conocidas: `QuoteService.ConvertQuoteToReserva` usa fallbacks `DateTime.UtcNow` (QuoteService.cs:395/:433/:464) cuando el quote no trae fechas (instantes UTC reales, no pared); `ServicioReservaService.cs:50-51` normaliza segmentos del servicio genérico con `NormalizeUtc`. El front deriva dd/MM con **string-split de la parte fecha** justamente para no correrse de día (DeadlinePill.jsx:25-34). Mismatch preexistente: las celdas de fecha de `ServiceList.jsx` usan `new Date(...).toLocaleDateString('es-AR')` → pueden mostrar día-1 en ART. Inventario verificado round 3 (no solo :249): **:243/:245 (Asistencia `validFrom`/`validTo`)**, :249 (fecha genérica), y el mismo patrón en :237-238 y :364-369 (rangos hotel/paquete y vista compacta). Es un **issue aparte para el gate UX que debe cubrir la columna entera; NO se resuelve en este ADR**.
- La columna "Avisos" hoy está gateada por `isCatalogFindOrCreateEnabled` (ServiceList.jsx:287) — cambia en D6 por decisión del dueño.

## 2. Decisiones

### D1 — Contrato: bucket nuevo `upcomingStarts`; `serviceDeadlines` se ELIMINA (no se mantiene legacy)

Sin clientes reales y con el flag OFF en todos lados, mantener dos buckets sería deuda gratuita. `AlertsResponse` cambia:

- `ServiceDeadlines` → se borra. Nace `UpcomingStarts` (mismo patrón: nullable + `JsonIgnore WhenWritingNull`, omitido con flag OFF → camino byte-idéntico actual intacto).
- Campo nuevo `UpcomingStartsWindowDays` (`int?`, omitido con flag OFF): los X días que el front necesita para la pill por servicio (ver D6). Viaja acá y NO en `OperationalFlagsResponse` para respetar su regla dura "solo booleanos" (el test de shape rompería). No es dato sensible: es un umbral de UI, visible para cualquier autenticado igual que el bucket mismo.
- Item de `upcomingStarts` (uno por reserva): `{ reservaPublicId, numeroReserva, name, holderName, firstStartDate, daysLeft }`.
  - **`holderName` (titular, Q3):** el server lo deriva con la convención existente — `Payer.FullName`; si la reserva no tiene Payer, **primer `Passenger` por `Id`** (orden de carga); si tampoco hay pasajeros, `null`. El front renderiza la línea 2 como "Reserva {numeroReserva} · {holderName ?? name}" — el fallback final es el nombre de la reserva, nunca una línea rota.
  - `daysLeft` lo computa el server con el "hoy" pared Argentina (`AgencyTimezone.TodayWallClockUtc()`, mismo criterio F1.4); `daysLeft == 0` ⇒ "Empieza HOY" rojo. No hay estado "vencido": pasado el inicio, el aviso desaparece solo (Ronda 9 no define overdue para inicios; para viaje-en-curso ya existe `urgentTrips`).
- `TotalCount` = financieros + upcomingStarts + costsToConfirm (mismo esquema actual; la campanita sigue sumando su propio badge: upcomingStarts + costos + notificaciones sin leer, Ronda 6 sin cambios).
- **`windowDays` ante error de fetch (menor del review):** `AlertsContext` no resetea estado en error — el front conserva el último payload bueno, incluido `upcomingStartsWindowDays`, hasta el próximo poll exitoso (30 s). La pill puede operar hasta 30 s con un umbral stale: aceptado y documentado (umbral de UI no sensible; alternativa de resetear a null haría parpadear la columna a "—" ante cualquier error transitorio).

### D2 — Elegibilidad y cómputo del "primer inicio"

`firstStart(reserva)` = MIN de las fechas de inicio (`.Date`) de los servicios **no cancelados** de la reserva, sobre los 6 tipos. Aviso si y solo si:

- **`reserva.Status ∈ {Sold, Confirmed, Traveling}` (Q2 round 2 + B2-nuevo round 3).** Presupuestos NO avisan (Q2, decisión del dueño) — CAMBIO respecto del mecanismo viejo, que incluía `Budget` (AlertService.cs:166-168): el round 1 proponía mantener Budget y Gastón lo rechazó. Con `EnableSoldToSettleStates` OFF "vendida" = `Confirmed`; con el flag ON, `Sold` y `Confirmed`. **`Traveling` se suma en round 3 (decisión del dueño, Opción 2):** el aviso rojo "Empieza HOY" debe verse durante TODO el día de inicio, y el job de lifecycle (00:00 ART, ver §1 y R8) ya pasó la reserva a `Traveling` para cuando alguien la mira. NO hace falta condición extra sobre `Traveling`: la ventana `today <= firstStart` ya garantiza que solo entra cuando `firstStart >= hoy` — una reserva genuinamente en viaje tiene firstStart en el pasado y queda afuera sola. `Budget/Cancelled/ToSettle/Closed/PendingOperatorRefund` nunca avisan; y
- `today <= firstStart <= today + X` (ventana inclusiva, `today` = pared ART) y
- ownership: admin todas; vendedor solo `ResponsibleUserId == UserId`; no-admin sin UserId ⇒ vacío (fail-closed, idéntico a F1.4) y
- no existe descarte vigente (D3).

**Prefiltro (B1-bis, corregido round 3): NO hay prefiltro de fecha. Punto.** El prefiltro sobre `Reservas` es **Status elegible + ownership**, nada más. Razón: no existe prefiltro seguro sobre un `StartDate` editable a mano **en ambas direcciones** y borrable (`UpdateDatesAsync`, ReservaService.cs:213-216) — la variante null-tolerante del round 2 seguía pudiendo excluir candidatos válidos. Y el prefiltro no compraba nada: las 6 consultas de servicios ya están acotadas por la ventana, así que el costo de evaluar todas las reservas con Status elegible es el que esas consultas pagan igual. La verdad sobre la ventana la dan SIEMPRE las fechas de los servicios. **Los dos tests obligatorios del round 2 se CONSERVAN** (reserva con `StartDate` null + servicio en ventana → avisa; reserva con `StartDate` manual tardío desincronizado + servicio en ventana → avisa): ahora su rol es proteger que nadie reintroduzca un prefiltro excluyente sobre `StartDate`.

Implementación: helper único `UpcomingStartCalculator` (Infrastructure, junto a `ReservaScheduleCalculator`) usado por el bucket Y por el endpoint de dismiss — una sola definición de "primer inicio", cero duplicación. Después del prefiltro, 6 consultas `GROUP BY ReservaId, MIN(fecha)` con el predicado de no-cancelado por tipo (joins explícitos, inline, compatibles con InMemory — mismo estilo F1.4); combinación y ventana en memoria. NO se cambia `ReservaScheduleCalculator` (su MIN-con-cancelados alimenta el lifecycle Traveling/Closed; tocarlo es otro alcance y otro riesgo). **Comentario cruzado obligatorio (menor del review, extendido round 3):** tanto `UpcomingStartCalculator` como `ReservaScheduleCalculator` llevan un comentario apuntando al otro que explica por qué existen DOS MIN distintos — el del lifecycle incluye cancelados (histórico, mueve estados) y el del aviso los excluye (P1) — para que nadie los "unifique" por error. El comentario menciona además al **tercer consumidor**: `AutoTransitionConfirmedToTravelingAsync` decide la promoción `Confirmed → Traveling` con el `StartDate` **persistido** (MIN con cancelados), no con ninguno de los dos cálculos en vivo — ver R8 para las tres definiciones coexistentes de "cuándo empieza".

### D3 — Persistencia del "Listo": tabla nueva, descarte GLOBAL anclado a la fecha descartada

**Decisión del dueño (Q1, round 2): "Listo" apaga el aviso PARA TODOS** — es una tarea por reserva, no por persona; queda registrado quién y cuándo lo apagó (supervisión por auditoría, no por re-aviso). La variante por-usuario del round 1 queda descartada.

**Tabla `UpcomingStartAlertDismissals`** (entidad nueva, NO campo en `Reserva` — ver §3-A3):

| Columna | Tipo | Nota |
|---|---|---|
| `Id` | int identity | PK |
| `ReservaId` | int, FK → Reservas, `ON DELETE CASCADE` | |
| `DismissedFirstStartDate` | timestamptz (date-only pared, como los deadlines de F1.4) | **la fecha de primer inicio que se descartó** |
| `DismissedByUserId` | text | auditoría: quién apretó Listo |
| `DismissedAtUtc` | timestamptz | auditoría |

Índice **UNIQUE en `ReservaId`** (descarte global confirmado — cierra B3: la migración queda definida sin contingencias).

**Semántica de re-armado (simple y robusta):** el aviso está oculto **únicamente** si existe un descarte con `DismissedFirstStartDate == firstStart actual` (comparación date-only). Cualquier cambio del primer inicio — se agrega un servicio que empieza antes, se corre la fecha (antes O después), se cancela el servicio que era el primero — hace que la fecha actual ya no coincida → **el aviso reaparece**. Es mejor re-avisar de más que callar de menos: el costo de un falso re-aviso es un click en "Listo"; el costo de un silencio es una reserva sin gestionar. Re-descartar = upsert de la misma fila (se pisa fecha + auditoría; no se acumula historia — trade-off aceptado, el detalle fino queda en logs).

**Dónde vive la garantía anti-doble-fila (M4, explícito round 2):** en el **índice UNIQUE de Postgres**, no en código C#. El provider InMemory de los tests **NO aplica índices únicos**, así que el test unit del upsert NO prueba la carrera de dos POST concurrentes — prueba solo la lógica feliz. El caso de carrera (dos requests insertan a la vez → uno recibe violación de unique → reintenta como update) se valida en los **tests de integración Postgres en VPS** (suite habitual), igual que se hizo con otros invariantes de unicidad del proyecto.

**Limpieza:** con el UNIQUE hay a lo sumo **una fila por reserva** (cota = cantidad de reservas) y borrado en cascada con la reserva → no hay basura que crezca ni hace falta job. Las filas con fecha pasada son inertes (el bucket ya no genera el aviso por ventana). Sin job de background: complejidad operativa cero.

### D4 — Endpoint de descarte

`POST /api/alerts/upcoming-starts/{reservaPublicId}/dismiss` → `204 No Content` (en `AlertsController`, ruta base actual `/api/alerts`).

**Autorización (M1, decisión round 2): se reusa la convención del repo** — `[Authorize]` + `[RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]` con el route param del publicId. Se descarta el 404-custom del round 1. Razones:

- **Consistencia:** es el patrón de TODOS los endpoints por-reserva (ReservasController.cs:64+); inventar un esquema 404 paralelo para un solo endpoint es deuda de revisión permanente.
- **Sin leak de existencia:** verificado en código — `OwnershipResolver` devuelve `false` para reserva **inexistente** igual que para reserva ajena (OwnershipResolver.cs:64-68), así que el no-owner recibe **403 uniforme** sin poder distinguir "existe" de "no existe". El argumento que motivaba el 404 ya lo cubre el filtro.
- **Semántica resultante (explícita, precisada round 3 — M-B):** sin identidad/UserId null → **401** (filtro, :45-56); no-owner sin bypass → **403**; **Admin y portadores de `reservas.view_all` pueden descartar cualquier reserva** — coherente con Q1: el descarte es global, y quien puede VER el aviso (admin/view_all ven todas) puede marcarlo gestionado; queda auditado en `DismissedByUserId`. **Sobre el 404:** un vendedor común NUNCA ve 404 por reserva inexistente — `OwnershipResolver` devuelve `false` para inexistente igual que para ajena, así que el filtro corta con **403 ANTES** de llegar al controller. El **404 del controller lo ven únicamente Admin y `reservas.view_all`** (bypass del filtro) cuando el publicId no existe. Flag `EnableServiceDeadlineAlerts` OFF → **404** del controller para quien pasa el filtro (la feature no existe); nota: el filtro corre ANTES que el check de flag, así que un no-owner recibe 403 aun con flag OFF — revela que el endpoint existe como ruta, no revela datos; trade-off aceptado y documentado.

- **El server recalcula `firstStart` con el MISMO helper de D2** y lo persiste — el cliente NO manda la fecha (no se confía en el cliente; elimina la carrera "vi una fecha, descarto otra": se descarta lo que el server ve AHORA, y si difiere de lo que vio el usuario, el re-armado de D3 lo cubre). `firstStart` null (sin servicios elegibles) → 204 no-op, no escribe.
- **Idempotente:** doble click / repetir POST = mismo estado final (upsert protegido por el índice UNIQUE; ante violación por carrera, reintentar como update una vez — ver M4 en D3 sobre dónde se prueba esto).
- **Observabilidad:** log estructurado (reservaId, userId, fecha descartada). La fila misma es el audit trail mínimo; integrar al AuditLog general queda fuera de alcance (anotado como mejora).

### D5 — Campanita (sección PRÓXIMOS INICIOS)

`SeccionFechasLimite` → `SeccionProximosInicios` (primera sección, mismas reglas de apilado Ronda 6). Línea 1: texto y colores EXACTOS de Ronda 9 (`daysLeft > 0` ámbar "⏰ Empieza el {dd/MM} (en {N} días)"; `daysLeft == 0` rojo "Empieza HOY {dd/MM}"). **Línea 2 (Q3, decisión del dueño): "Reserva {numeroReserva} · {titular}"**, donde titular = `holderName` del item con fallback a `name` (derivación y fallbacks en D1). Copy fino restante (singular "(en 1 día)", ubicación/estilo del botón "Listo") lo fija el gate UX — `ux-ui-disenador` ANTES de implementar F2, regla del dueño. Click en el item navega a la reserva (igual que hoy).

**"Listo" optimista — contrato ante fallo (menor del review):** click en "Listo" hace el POST, saca el item optimistamente y dispara `refreshAlerts()` — el badge baja solo porque el server ya no devuelve el item. **Si el POST falla** (red, 403, 5xx): NO se muestra cartel especial; el item simplemente **reaparece en el próximo refresh** (el `refreshAlerts()` inmediato o el poll de 30 s, porque el server nunca registró el descarte). Es el mismo contrato fail-soft del resto de la campanita; si el gate UX pide feedback explícito de error, se ajusta ahí.

El item del bucket no necesita campo `dismissible`: todo item que el caller ve, lo puede descartar (el server ya filtró por ownership, y admin/view_all descartan global por Q1).

### D6 — Pill por servicio (columna "Avisos"): cómputo 100 % frontend

La pill NO consulta nada nuevo: usa el campo normalizado `service.date` (ya viaja en los DTOs y ya está unificado en `normalizeReservaServices`), `workflowStatus !== "Cancelado"` (criterio existente), y la ventana `upcomingStartsWindowDays` que llega por el payload de `/alerts` ya consumido vía `useAlerts()`. Regla Ronda 9: pill solo si `0 <= (date - hoyLocal) <= X` con el MISMO texto del aviso; resto "—". Sin estado vencido. `DeadlinePill.jsx` se reescribe como `UpcomingStartPill.jsx` (helpers puros exportados para tests `.mjs`, patrón actual) y se borra el viejo.

**La pill es INFORMACIÓN, no aviso (M-A, decisión deliberada del dueño, round 3 — asentada en guia-ux-gaston Ronda 9):** aparece SIEMPRE que el servicio empiece dentro de la ventana, **sin importar el Status de la reserva** — presupuestos y reservas en viaje incluidos; servicios cancelados nunca. La pill NO recibe ni evalúa el Status de la reserva (sus únicos insumos son `service.date`, `workflowStatus` del servicio y `windowDays`). La divergencia con la campanita (elegibilidad `{Sold, Confirmed}` + `Traveling` día-de-inicio, D2) es a propósito: la campanita es ACCIONABLE (tiene botón "Listo", representa una tarea), la pill es contexto pasivo de la fila. Nadie debe "alinear" los dos criterios pensando que es un bug.

**Derivación de fecha en la pill (M3):** SIEMPRE con string-split de la parte fecha del ISO (patrón existente DeadlinePill.jsx:25-34), **NUNCA `new Date(...).toLocaleDateString()`** — eso corre el día en ART para fechas guardadas como pared-UTC. (El mismatch preexistente de las celdas de fecha de `ServiceList.jsx` queda anotado como issue aparte para el gate UX, cubriendo la columna entera: **:243/:245 — Asistencia `validFrom`/`validTo` —**, :249, y el mismo patrón en :237-238 y :364-369; este ADR no lo toca.)

**Gating de la columna (decisión explícita del dueño, round 2):** la columna "Avisos" pasa de `EnableCatalogFindOrCreate` (ServiceList.jsx:287) a **gate ÚNICO por `EnableServiceDeadlineAlerts`**. Matriz de flags confirmada por Gastón:

| `EnableCatalogFindOrCreate` | `EnableServiceDeadlineAlerts` | Columna "Avisos" |
|---|---|---|
| ON | ON | aparece |
| **OFF** | **ON** | **aparece (decisión del dueño)** |
| ON | OFF | no aparece |
| OFF | OFF | no aparece |

Cada combinación lleva test (frontend `.mjs` del criterio de render + el shape test backend ya cubre la omisión del bucket con flag OFF).

Divergencia conocida y aceptada: la pill usa reloj del cliente y la campanita el "hoy" ART del server — mismo trade-off que la pill actual (`estaVencida` client-side); para una agencia en ART es indistinguible.

Alternativa descartada: computar la pill server-side en los 6 DTOs de servicio — toca 6 DTOs + mappings para un dato que el front ya tiene, y duplica la fecha en el contrato.

### D7 — Qué muere (inventario explícito, completado round 2 — M2)

**Backend:** `AlertService.ComputeServiceDeadlinesAsync` + `BuildDeadlineAlert`; `AlertsResponse.ServiceDeadlines`; campos `OperatorPaymentDeadline`/`TicketingDeadline`/`DeadlinesSpecified` de los requests (`Requests.cs:67-281`) y de los DTOs (`HotelBookingDto`, `PackageBookingDto`, `FlightSegmentDto`); las escrituras en `BookingService.cs` (:457/:543-549/:701/:788/:938/:1012) y `BookingService.CatalogCreates.cs` (:116/:216/:368) + helper `NormalizeDeadlineDate` + el anti-clobber R12; mapeos en `MappingProfile`.

**Tests backend:** `CatalogDeadlineMappingTests`, `BookingServiceDeadlinePersistenceTests` (se borran); `AlertServiceDeadlineBucketsTests`, `ServiceCatalogPillsDtoTests` (se reescriben para el modelo nuevo); `AlertServiceCallerGatingTests.cs` (tests de shape de buckets por caller — se actualizan al shape nuevo); **`Adr018ProductFirstReconciliationTests.cs:511`** (`Bucket(payload, "ServiceDeadlines")` — consumidor encontrado en verificación round 2, no estaba en el inventario del review; se actualiza al bucket nuevo o se elimina la aserción según lo que pruebe).

**Frontend — consumidores de `serviceDeadlines` (inventario completo M2):**
- `AlertsContext.jsx:14,29` (estado default + reset del fetch);
- `useFinanceHome.js:6,10` (shape del payload);
- `alertsContract.test.mjs:19,34,53,66` (contrato camelCase del payload);
- `notificationBell.test.mjs:55-56` (`calcularBadge`/orden de deadlines);
- `NotificationBell.jsx:197-338` (consumo del contexto :201, `hayAvisosNuevos` :205, `totalBadge` :209, `SeccionFechasLimite` :338);
- `DeadlinePill.jsx` y sus tests;
- `ServiceList.jsx:287` (gating + render de la columna).

**Frontend — campos manuales que se borran:** "Fecha límite de seña/pago" en `HotelInlineForm` (:554-563), "Fecha límite de emisión" en `FlightInlineForm` (:290-362), "Fecha límite de seña" en `PackageInlineForm` (:396-409); su estado/payload en `ServiceInlineCard.jsx` (:98/:122/:136/:157/:229-254/:460-461/:486-488/:563-564); textos "Señar antes del/Venció señar/Emitir antes del/Venció emitir" en `NotificationBell.jsx` y tests `.mjs`; en `OperationalFinanceSettingsTab.jsx` los textos viejos → "Avisos de próximos inicios" + descripción nueva textual de Ronda 9 ("Días de anticipación del aviso" queda igual).

**Columnas DB — se DROPEAN (B2, justificación corregida round 2):** `HotelBookings.OperatorPaymentDeadline`, `PackageBookings.OperatorPaymentDeadline`, `FlightSegments.TicketingDeadline`. Justificación honesta: **las migraciones Adr017 M1-M5 SÍ están aplicadas en VPS (2026-06-06)**, por lo que las columnas existen allá y **pueden contener datos de prueba**; el dueño confirmó explícitamente borrarlos ("Sí, borralas" — eran datos de prueba). El flag nunca estuvo ON con clientes reales. **Paso previo opcional en VPS (informativo, NO bloqueante):** antes de aplicar la migración, `SELECT "MigrationId" FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%Adr017%'` + `SELECT COUNT(*)` de no-null en las 3 columnas — solo para dejar constancia de cuántas filas de prueba se pierden.

**NO se renombra** lo interno: `EnableServiceDeadlineAlerts` y `ServiceDeadlineAlertDays` conservan su nombre en C#/DB (renombrar = migración + churn en panel/flags/tests por cero valor de usuario; el nombre de cara a Gastón es solo el texto de la UI). Anotado en código con comentario "UI: Próximos inicios".

### D8 — Migración

`Adr019_M1_UpcomingStartsAndDropManualDeadlines` (una sola, encolada detrás de Adr017_M6): (1) `CREATE TABLE UpcomingStartAlertDismissals` + UNIQUE(ReservaId) + FK cascade; (2) `DROP COLUMN` de las 3 columnas de D7.

**`Down` y forward-only (menor del review, explícito):** el `Down` recrea las 3 columnas **nullable y vacías** y dropea la tabla. Esto NO contradice el criterio forward-only de Adr017 M5/M6: "forward-only" en este repo significa que **el `Down` no restaura datos** y que en VPS la política operativa es roll-forward (nunca se corre `Down` en prod) — el `Down` existe solo para mantener la cadena de migraciones válida y reversible en el loop de desarrollo local. La pérdida de los valores dropeados es irreversible por diseño y está aceptada por el dueño (eran datos de prueba). El rollback REAL en prod es el flag, no el `Down` (ver §4).

## 3. Alternativas consideradas

- **A1 — Descarte por usuario vs global:** RESUELTO round 2 — Gastón eligió **GLOBAL** (Q1): el aviso representa UNA tarea operativa por reserva; la fila guarda QUIÉN descartó (supervisión por auditoría). La variante por-usuario (UNIQUE compuesto `(ReservaId, DismissedByUserId)`) queda documentada como evolución posible si la operación crece, sin cambio de diseño estructural.
- **A2 — Generar notificaciones persistentes con un job** (reusar la entidad Notification + read flag): rechazada — agrega job/scheduler, duplica estado que ya se deriva de los servicios, y se desincroniza ante cambios de fecha; compute-on-read es el patrón vigente de TODOS los buckets.
- **A3 — Campo de descarte en `Reserva`** (p. ej. `DismissedUpcomingStartDate`): funciona para el alcance global elegido, pero mete un concern de UI en el aggregate central (ya enorme), cierra la evolución por-usuario y ensucia la auditoría. Tabla chica dedicada = frontera limpia.
- **A4 — Mantener `serviceDeadlines` junto al bucket nuevo:** rechazada — sin consumidores reales; contrato limpio (mandato del contexto).
- **A5 — Días X vía `OperationalFlagsResponse`:** rechazada — viola su regla dura documentada ("solo booleanos", test de shape). Va en el payload de `/alerts` (D1).
- **A6 — Conservar columnas DB muertas:** rechazada (ver D7; dueño confirmó el drop).
- **A7 — Aviso también para `Traveling`/overdue:** **parcialmente revertida en round 3 (B2-nuevo, decisión del dueño — Opción 2):** `Traveling` SÍ avisa cuando `firstStart >= hoy` (la ventana lo garantiza sola), porque el job de lifecycle promueve a las 00:00 ART y mataba el aviso rojo "Empieza HOY" durante su único día de vida (D2, R8). Lo que SIGUE rechazado es el overdue: `Traveling` con `firstStart` en el pasado (viaje genuinamente iniciado) no avisa — eso lo cubre `urgentTrips`.
- **A8 — 404 custom en el dismiss para no filtrar existencia (round 1):** rechazada round 2 (M1) — `RequireOwnership` ya no filtra existencia (403 uniforme verificado en OwnershipResolver.cs:64-68) y es la convención de todos los endpoints por-reserva; un esquema 404 paralelo era inconsistencia gratuita. Ver D4.
- **A9 — Incluir presupuestos en el aviso (default round 1):** rechazada por el dueño (Q2) — solo vendidas/confirmadas. Ver D2.

## 4. Plan de implementación (fases — sin F0: las preguntas están todas respondidas)

- **F1 — Backend** (`backend-dotnet-senior` + `backend-dotnet-reviewer`; toca reservas/autorización ⇒ `security-data-risk-reviewer`): migración D8 → entidad + DbSet → `UpcomingStartCalculator` (+ comentario cruzado con `ReservaScheduleCalculator`, D2) → bucket `upcomingStarts` + `upcomingStartsWindowDays` + `holderName` en `AlertService`/`AlertsResponse` (borrar bucket viejo) → endpoint dismiss con `RequireOwnership` (D4) → limpiar requests/DTOs/BookingService/MappingProfile → tests (§5). Flag OFF = byte-idéntico (mismo early-return actual).
- **F2 — Frontend** (`ux-ui-disenador` PRIMERO — gate obligatorio para copy fino, botón "Listo", línea 2, pill, textos de Configuración; después `frontend-senior` + `frontend-reviewer`): `NotificationBell` (sección + Listo + badge) → `UpcomingStartPill` (string-split, D6) + gating de columna en `ServiceList` (gate único `EnableServiceDeadlineAlerts`) → borrar campos de las 3 fichas + `ServiceInlineCard` → textos `OperationalFinanceSettingsTab` → tests `.mjs` (incluida la matriz de flags D6).
- **F3 — Verificación:** grep final de "Señar antes\|Venció señar\|Emitir antes\|Venció emitir\|OperatorPaymentDeadline\|TicketingDeadline\|serviceDeadlines" = solo historia en ADRs/migraciones; build + suites verdes; integración Postgres en VPS (incluye carrera del upsert, M4) al aplicar la cola; smoke con flag ON en entorno de Gastón.

**Rollback:** apagar `EnableServiceDeadlineAlerts` (bucket omitido, pill y columna desaparecen, endpoint 404 para owners; el filtro de ownership sigue activo) — sin tocar datos. Rollback de esquema: `Down` de Adr019_M1 (recrea columnas vacías, NO restaura datos — ver D8; en VPS la política es roll-forward, el `Down` es de dev). Nota: con el flag OFF las fichas igual ya no tienen el campo manual — pero ese campo solo era visible con `EnableCatalogFindOrCreate` ON y su eliminación es decisión firme de Ronda 8, no un efecto colateral.

## 5. Estrategia de tests

**Backend (unit, patrón InMemory actual):** `AlertServiceUpcomingStartsTests` —
- MIN entre tipos (el más temprano gana, incluida Asistencia/genérico); servicio cancelado excluido del MIN por cada tipo (incl. estados UN/UC/HX/NO de vuelo);
- **elegibilidad Q2 + B2-nuevo:** reserva `Budget` NO avisa (cambio vs mecanismo viejo); `Sold` y `Confirmed` avisan; `Cancelled/ToSettle/Closed/PendingOperatorRefund` no; **`Traveling` con `firstStart == hoy` → avisa "HOY" (rojo); `Traveling` con `firstStart < hoy` → NO avisa** (tests nuevos round 3);
- **B1-bis (obligatorios — protegen que nadie reintroduzca un prefiltro excluyente sobre `StartDate`):** reserva con `StartDate == null` + servicio en ventana → avisa; reserva con `StartDate` manual tardío (desincronizado, fuera de ventana) + servicio en ventana → avisa;
- **titular (Q3):** `holderName` = Payer.FullName; sin Payer → primer Passenger por Id; sin pasajeros → null (y el contrato del front cae a `name`);
- ventana inclusiva en bordes (hoy y hoy+X); `daysLeft` 0 en el día; **borde timezone (M3):** servicio con hora de pared 22:00 (ART) → el cómputo date-only no lo corre de día;
- descarte oculta; descarte con fecha distinta NO oculta (re-armado al adelantar Y al atrasar); ownership vendedor/admin + fail-closed UserId null; flag OFF ⇒ shape de respuesta idéntico al actual (sin claves nuevas; actualiza `AlertServiceCallerGatingTests`).

`UpcomingStartDismissTests` — 204 idempotente; upsert (una fila — **nota M4:** InMemory no aplica el UNIQUE, este test prueba la lógica, NO la carrera); 401 sin identidad; 403 reserva ajena (vía filtro); **(M-B, corregido round 3)** vendedor común con publicId **inexistente** → **403** (el filtro corta antes del controller — NUNCA 404) / admin y `reservas.view_all` con publicId inexistente → **404** del controller; 404 flag OFF; no-op sin servicios; admin y `reservas.view_all` descartan ajena (auditado en `DismissedByUserId`).

**Frontend (`.mjs` puros):** helpers de texto (ámbar/rojo/HOY/dd-MM con string-split); línea 2 con `holderName` y fallback a `name`; criterio de pill (dentro/fuera de ventana, cancelado, sin fecha, sin windowDays ⇒ "—"; **M-A:** los helpers de la pill NO reciben Status de reserva — un servicio en ventana de una reserva presupuesto o en viaje también muestra pill); **matriz de flags D6** (catálogo OFF + avisos ON ⇒ columna aparece); contrato del dismiss optimista (fallo del POST ⇒ el item vuelve con el refresh, sin estado especial).

**Integración VPS (Postgres):** script habitual al aplicar la cola; incluye el caso de carrera del upsert contra el UNIQUE real (M4) y la verificación informativa pre-drop de D7.

## 6. Riesgos

- **R1 (bajo)** reloj cliente vs ART en la pill — aceptado (D6), preexistente.
- **R2 (medio)** doble definición de "primer inicio" si alguien no usa el helper — mitigado: helper único D2 + comentario cruzado + test que cruza bucket vs dismiss.
- **R3 (bajo)** DROP de columnas en VPS con datos de prueba reales — confirmado por el dueño; pre-check informativo opcional (D7); `Down` recrea esquema sin datos.
- **R4 (bajo)** carrera dismiss vs cambio de fecha — cubierta por "server recalcula" + re-armado D3; carrera de doble insert cubierta por UNIQUE Postgres (validada en integración, M4).
- **R5 (medio)** re-avisos por cambios menores de fecha pueden percibirse como ruido — aceptado a propósito (fallar hacia avisar); si molesta, se relaja después solo el caso "fecha se atrasó" (cambio localizado en el predicado de coincidencia).
- **R6 (bajo)** con flag OFF, un no-owner que adivina la ruta del dismiss recibe 403 (el filtro corre antes que el check de flag) → revela que la ruta existe, no datos — aceptado (D4).
- **R7 (bajo)** `windowDays` stale hasta 30 s ante error de fetch — aceptado y documentado (D1).
- **R8 (medio, nuevo round 3)** TRES definiciones de "cuándo empieza" coexisten y pueden divergir: (a) el **job de lifecycle** decide la promoción `Confirmed → Traveling` con el `StartDate` **persistido** — MIN CON cancelados (`ReservaScheduleCalculator`) y además editable a mano; (b) el **aviso** usa el MIN **computado SIN cancelados** (`UpcomingStartCalculator`); (c) la **pill** usa `service.date` client-side. Ejemplo de divergencia: un servicio cancelado más temprano que el resto hace que el job promueva ANTES de que el aviso diga "HOY". Mitigación en este ADR: B2-nuevo hace el aviso robusto a la promoción (una reserva ya `Traveling` sigue avisando mientras `firstStart >= hoy`) + comentario cruzado de D2 extendido al job. **Deuda preexistente del job, NOMBRADA y NO resuelta acá (alcance aparte):** decidir con el StartDate-con-cancelados puede promover una reserva a `Traveling` antes de tiempo; corregirla implica tocar el lifecycle (otro riesgo, otro ADR).

## 7. Respuestas del dueño (Rounds 2 y 3 — TODAS cerradas)

1. **Q1 (descarte):** "Listo" apaga el aviso **PARA TODOS** (global). Tabla con UNIQUE(ReservaId); se registra quién/cuándo. → D3, D4.
2. **Q2 (presupuestos):** los presupuestos **NO avisan** — solo reservas vendidas/confirmadas (`Sold`/`Confirmed`). Contrario al default propuesto en round 1 y al mecanismo viejo (que incluía Budget): documentado como cambio deliberado. → D2.
3. **Q3 (línea 2):** "Reserva {numero} · {titular}". Titular = convención existente `Payer.FullName` (VoucherService); fallback primer pasajero → nombre de reserva. → D1, D5.
4. **DROP de columnas:** confirmado ("Sí, borralas" — datos de prueba). → D7, D8.
5. **Columna Avisos con catálogo OFF + avisos ON:** SÍ aparece — gate único por `EnableServiceDeadlineAlerts`. → D6 (matriz + tests).

**Round 3 (2026-06-06):**

6. **B2-nuevo ("Empieza HOY" vs job de lifecycle):** el dueño eligió la **Opción 2** — el aviso rojo debe verse durante TODO el día de inicio aunque el job (00:00 ART) ya haya pasado la reserva a `Traveling`. Implementación: `Traveling` se suma al conjunto elegible; la ventana sola garantiza que solo entra con `firstStart >= hoy`. → D2, A7, R8.
7. **M-A (pill de la fila):** la pill es **INFORMACIÓN, no aviso** — aparece siempre que el servicio empiece en ventana, sin importar el Status de la reserva (presupuestos y en-viaje incluidos; servicios cancelados nunca). Asentada en guia-ux-gaston Ronda 9. La campanita mantiene su elegibilidad propia (`{Sold, Confirmed}` + `Traveling` día-de-inicio). → D6.

Único pendiente NO bloqueante de backend: copy fino visual (singular "(en 1 día)", ubicación/estilo del botón "Listo") vía gate UX antes de F2.

---

## Changelog

**Round 3 (2026-06-06)** — cierra el re-review del round 2 (los únicos puntos abiertos) + 2 decisiones de producto del dueño:
- **B1-bis:** el prefiltro de fecha de D2 se ELIMINA por completo — prefiltro = Status elegible + ownership, punto. No existe prefiltro seguro sobre un `StartDate` editable a mano en ambas direcciones, y las 6 consultas de servicios ya están acotadas por la ventana (el prefiltro no aportaba nada). Los dos tests obligatorios del round 2 se conservan con rol nuevo: proteger que nadie reintroduzca un prefiltro excluyente (D2, §5, §1).
- **B2-nuevo (decisión del dueño: Opción 2):** `Traveling` se suma al conjunto elegible para que "Empieza HOY" se vea TODO el día de inicio aunque el job de lifecycle (verificado: `AutoTransitionConfirmedToTravelingAsync`, Hangfire `Cron.Daily(3)` = 00:00 ART, Program.cs:814-817) ya haya promovido la reserva. Sin condición extra: la ventana `today <= firstStart` garantiza que una reserva genuinamente en viaje (firstStart pasado) no entra. Riesgo nuevo R8: tres definiciones de "cuándo empieza" coexisten (job = MIN persistido CON cancelados; aviso = MIN computado SIN cancelados; pill = client-side); comentario cruzado de D2 extendido al job; deuda preexistente del job (promueve con StartDate-con-cancelados → puede promover antes de tiempo) NOMBRADA, no resuelta acá. Tests nuevos: `Traveling` con `firstStart == hoy` → avisa HOY; con `firstStart < hoy` → NO avisa (D2, A7, R8, §1, §5).
- **M-A (decisión del dueño):** la pill de la fila es INFORMACIÓN, no aviso — aparece siempre que el servicio empiece en ventana, sin importar el Status de la reserva (presupuesto/en-viaje incluidos; cancelados nunca). Documentada como decisión deliberada (guia-ux-gaston Ronda 9, recién asentada); divergencia con la campanita es a propósito (D6, §5, §7).
- **M-B:** semántica del 404 del dismiss precisada — un vendedor común NUNCA ve 404 por inexistente (el filtro da 403 antes); el 404 del controller lo ven solo Admin/`reservas.view_all`. Test del §5 corregido en consecuencia (D4, §5).
- **Menores:** el issue aparte del `toLocaleDateString` para el gate UX se amplía a las celdas de Asistencia (ServiceList.jsx:243/:245, `validFrom`/`validTo`) — y, verificado en round 3, al mismo patrón en :237-238 y :364-369: el issue cubre la columna entera, no solo :249 (§1, D6).

**Round 2 (2026-06-06)** — incorpora review de software-architect-reviewer (Changes Required) + respuestas completas del dueño:
- **B1:** prefiltro por `Reserva.StartDate` corregido a null-tolerante e inclusivo (`StartDate == null || StartDate <= window`); causa verificada: `UpdateDatesAsync` permite `ClearStartDate`→null y fechas manuales sin recálculo (ReservaService.cs:213-216). Tests obligatorios agregados (D2, §5).
- **B2:** premisa "migraciones sin aplicar en VPS" era FALSA — M1-M5 aplicadas el 2026-06-06; justificación del DROP reescrita con la confirmación explícita del dueño + pre-check informativo opcional (header, D7, R3).
- **B3:** resuelto por Q1 (descarte global) — UNIQUE(ReservaId) definitivo, migración sin contingencias (D3).
- **M1:** el dismiss usa `[RequireOwnership(..., bypassPermission: ReservasViewAll)]` (convención del repo) en lugar del 404 custom; verificado que el 403 es uniforme para inexistente/ajena (sin leak); semántica 401/403/404 explícita; view_all puede descartar (coherente con Q1) (D4, A8).
- **M2:** inventario de consumidores de `serviceDeadlines` completado (AlertsContext, useFinanceHome, alertsContract.test.mjs, notificationBell.test.mjs, AlertServiceCallerGatingTests, NotificationBell, DeadlinePill, ServiceList) + hallazgo propio no listado en el review: `Adr018ProductFirstReconciliationTests.cs:511` (D7).
- **M3:** supuesto timezone declarado (hora-de-pared Kind=Utc, NormalizeAirportWallClock:383-388; excepciones QuoteService :395/:433/:464 y ServicioReservaService.cs:50); cómputo date-only + test de borde 22:00 ART; pill con string-split obligatorio; mismatch preexistente de ServiceList.jsx:249 anotado como issue aparte para el gate UX (§1, D6, §5).
- **M4:** explícito que la garantía anti-doble-fila vive en el UNIQUE de Postgres, que InMemory no lo aplica y que la carrera se valida en integración VPS (D3, §5).
- **Menores:** contrato de `windowDays` stale ante error de fetch (verificado: AlertsContext conserva el último payload bueno) (D1, R7); explicación de por qué el `Down` no contradice el forward-only (D8); contrato del dismiss optimista ante fallo del POST (D5); comentario cruzado entre los dos MIN (D2).
- **Decisiones del dueño:** Q1 global (D3/D4), Q2 presupuestos NO avisan — cambia el predicado de Status vs round 1 y vs mecanismo viejo (D2, A9), Q3 titular con derivación verificada en código (D1/D5), drop confirmado (D7), gate único de la columna por `EnableServiceDeadlineAlerts` con matriz + tests (D6).
- Plan de fases: se elimina F0 (preguntas respondidas); F1 backend → F2 frontend (gate UX primero) → F3 verificación (§4).

**Round 1 (2026-06-06)** — versión inicial: bucket `upcomingStarts` compute-on-read, tabla `UpcomingStartAlertDismissals` anclada a fecha descartada, drop de deadlines manuales, contrato limpio vía `/alerts`, sin renombres internos.
