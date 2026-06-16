# ADR-031 — Pasajeros nominales exigidos por servicio (al emitir/confirmar), sobre EL SET DEL SERVICIO

## 1. Status

Propuesto — **revisión 4 (v2.1 del modelo)**. Corrige el `software-architect-reviewer` (Changes Required sobre v2): simplifica la regla del subconjunto a **una sola regla determinística**, cierra el cambio semántico del helper (B2), vuelve **requisito de implementación** la limpieza transaccional de asignaciones al borrar un servicio (M1), agrega **auditoría** del alta/baja de asignaciones, y elimina la ambigüedad de v2 (ya no hay opción "primeros N por Id" en ninguna parte). Listo para re-review.

Diseño solamente. No incluye código de implementación. Nada está desplegado, así que se puede cambiar el diseño sin migración de datos.

### 1.1 Por qué hay una v2.1 (correcciones del reviewer sobre v2)

La v2 introdujo el "set del servicio" pero dejó **dos caminos posibles** para resolver el set cuando un servicio declaraba una cantidad propia `n` menor que el total (la "Q-V2-SUBSET-DEFAULT": exigir a todos, o exigir solo `n`). El reviewer marcó eso como ambigüedad peligrosa (M2/M3) y señaló además dos problemas de integridad y semántica (M1, B2). v2.1 los cierra:

- **Regla unificada del subconjunto (M2 + M3):** el **único determinante** del set es la **asignación explícita** (`PassengerServiceAssignment`). La cantidad propia del servicio (`HotelBooking.Adults`, etc.) **NUNCA** achica el set del gate. Desaparece la opción "primeros N por Id". Ver §3.2 y §4.
- **B2 (cambio semántico explícito):** el helper deja de **recortar a los primeros `declaredPax` por Id** (lo que hace hoy `PassengerNominalRules.CheckAllDeclared`, líneas 176-183 — verificado). El gate v2.1 valida **el set completo del servicio**. Es un cambio de **semántica**, no solo de firma. Ver §5.
- **M1 (integridad, bloqueante):** al borrar un servicio hay que borrar **en la misma transacción** sus `PassengerServiceAssignments`. Hoy **no** se limpian (verificado en `DeleteFlightAsync` y los demás `Delete*Async` de `BookingService`) → asignaciones huérfanas → si el Id se reusa, un servicio nuevo heredaría el set de uno muerto (set incorrecto silencioso). Requisito de implementación, no "verificar después". Ver §4.3 y §7bis.
- **Auditoría:** alta/baja de asignaciones se audita (quién/cuándo), porque ahora **determinan quién aparece en un ticket/voucher**. Ver §6.5.

### 1.2 Qué se mantiene de la v1/v2 (sigue válido)

- Mover la exigencia de nombres desde "el cliente aceptó el presupuesto" (`Budget → InManagement`, ahora solo cantidad) hacia el punto en que **cada servicio se resuelve/emite** con el operador.
- El **cierre del bypass B1**: el gate corre en TODOS los call-sites que pueden dejar un servicio resuelto (los `Create*Async`/`Update*Async`, no solo los `Update*StatusAsync`). El motor automático no se toca. **Esto NO cambia** (cambian los chokepoints de REGLA — resolver el set —, no los de DÓNDE corre).
- El gate corre **antes** de persistir el status resuelto y antes de `UpdateBalanceAsync`.
- Helper de dominio puro `PassengerNominalRules` como fuente única compartida front/back.
- Titular determinístico (sin campo nuevo), ahora relativo al set.
- Mensajes de error sin número de documento.

## 2. Contexto y estado actual del código (verificado en esta revisión)

### 2.1 Ciclo de vida y transiciones

Sin cambios. `Quotation → Budget → InManagement → Confirmed → Traveling → ToSettle → Closed` (+ `Lost`, `Cancelled`, `PendingOperatorRefund`). `Budget → Confirmed` **NO** es manual; lo alcanza/abandona solo el motor (`ReservaAutoStateService.EvaluateAndApplyAsync`).

### 2.2 Resolución de cada servicio (verificado)

`ServiceResolutionRules.IsResolved` (clase pura). Aéreo resuelve por `TicketIssuedAt != null && !cancelado`; Hotel/Paquete/Asistencia/Genérico por status confirmado/emitido; Traslado por confirmado o `NoConfirmationRequired`. Toda ruta de resolución llama `UpdateBalanceAsync` → `EvaluateAndApplyAsync`; el motor pasa a `Confirmed` cuando `AllLiveServicesResolved`.

### 2.3 Bypass B1 (verificado, sigue vigente)

Un servicio puede nacer/quedar resuelto vía `Create*Async`/`Update*Async` estando la reserva en `InManagement`. El motor entonces auto-confirma sin pasar por ningún `Update*StatusAsync`. Por eso el gate vive en TODOS esos call-sites. **El cierre de B1 no cambia.**

### 2.4 Gate v1 ya implementado (verificado) — y QUÉ debe cambiar

- `PassengerNominalRules` (`src/TravelApi.Domain/Reservations/PassengerNominalRules.cs`, verificado): pura, recibe **la `Reserva`** y cuenta por **cantidad declarada** (`AdultCount + ChildCount + InfantCount`, línea 154). `GetLeadPassenger(reserva)` = `Passengers.OrderBy(p => p.Id).FirstOrDefault()` (líneas 55-61). `EnsureCovered(reserva, kind)` / `GetMissing(reserva, kind)`.
  - **B2 — recorte a eliminar:** `CheckAllDeclared` (líneas 148-191) toma `declaredPax`, ordena por Id y valida **solo los primeros `declaredPax`** (bucle líneas 176-183). v2.1 elimina ese recorte: el universo pasa a ser el **set completo del servicio**.
- Envoltorio `EnsureNominalCoverageBeforeResolvingAsync(reservaId, serviceWasResolved, serviceIsResolved, serviceKind, ct)` (`BookingService.cs:135-156`, verificado): gatea SOLO la transición no-resuelto → resuelto; carga `Reservas.Include(r => r.Passengers)` AsNoTracking y llama `EnsureCovered(reserva, kind)`.
- Gate espejo del genérico: `EnsureGenericNominalCoverageBeforeResolvingAsync` (`ReservaService.cs:2802-2818`, verificado) — mismo patrón, también carga la reserva con `Passengers` y llama `EnsureCovered(reserva, Generic)`. **Debe realinearse al set** igual que el envoltorio principal.
- `EnsureReadinessForSaleAsync` (`ReservaService.cs` ~2752): `Budget → InManagement` ya quedó solo en cantidad. **No cambia.**

### 2.5 Modelo de pasajero (verificado)

`Passenger`: `FullName` (required), `DocumentType?`, `DocumentNumber?`, `BirthDate?`, `Gender?`, `PassportExpiry?`. **No hay campo de titular.** `DocumentNumber` nunca se valida hoy fuera de este gate.

### 2.6 Mecanismo de subconjunto — `PassengerServiceAssignment` (verificado, ya existe)

`PassengerServiceAssignment` (`src/TravelApi.Domain/Entities/PassengerServiceAssignment.cs`, verificado), N:M pasajero↔servicio, **persistido** (migración `Phase2_1_PassengerServiceAssignment`, `20260504045136`), `DbSet` en `AppDbContext`, tabla `PassengerServiceAssignments`:

- `PassengerId` (FK a `Passenger`, cascade), `ServiceType` (string discriminator: `Hotel/Transfer/Package/Flight/Assistance/Generic`, ver `AssignmentServiceType`), `ServiceId` (**soft-FK** al Id del booking/segment — EF no cascadea porque la tabla destino varía; lo dice el propio comentario de la entidad), metadata opcional `RoomNumber`/`SeatNumber`/`Notes`, `CreatedAt`, `PublicId`.
- **Índice único** `(PassengerId, ServiceType, ServiceId)`.
- **Índice** `(ServiceType, ServiceId)` → listar pasajeros de un servicio es eficiente.
- La asignación **NO es obligatoria** (un pasajero puede existir sin asignaciones).
- **Alta/baja hoy:** `ReservaService.CreateAssignmentAsync` (línea 404) y `RemoveAssignmentAsync` (línea 531). **Ninguna audita** (verificado). v2.1 lo corrige (§6.5).

**Conclusión: este modelo soporta el subconjunto sin migración.** Regla adoptada (§4): un servicio **sin** asignaciones = para TODOS; **con** asignaciones = solo esos.

### 2.7 Cantidad propia de cada servicio (verificado) — y su rol en v2.1

| Servicio | Campos de cantidad propia |
|----------|---------------------------|
| `HotelBooking` | `Adults` (default 2), `Children` (default 0) |
| `PackageBooking` | `Adults` (default 2), `Children` (default 0) |
| `AssistanceBooking` | `Adults` (default 1), `Children` (default 0) |
| `TransferBooking` | `Passengers` (default 1) |
| `FlightSegment` | `PassengerCount` (nullable int) — existe (verificado, migración `AddFlightSegmentConfirmationAndPaxCount`) |

**En v2.1 la cantidad propia NO determina el set del gate.** Tiene dos usos, y solo dos:

1. **Sugerir el total de la reserva** (autocompletado, §6.4) vía `ComputePaxCompositionFromServices`.
2. **Dato operativo del servicio** (rooming, asientos, cupo del operador).

> **Por qué la cantidad propia NO debe achicar el set (cierra M3):** los campos de cantidad tienen **defaults** (`HotelBooking.Adults = 2`, etc.). Un hotel recién creado en una reserva de 3 personas tendría `Adults = 2` **por accidente del default**, no porque el agente dijera "este hotel es para 2". Si el set se derivara de la cantidad propia, ese hotel parecería "para algunos" y el gate pediría menos nombres de los que corresponde — pedir **de menos** es el error inseguro. Por eso el set se decide **solo por asignaciones explícitas**, y el default es "todos".

### 2.8 Autocompletado del total ya existe (verificado) — `ComputePaxCompositionFromServices`

`ReservaService.ComputePaxCompositionFromServices(reserva)` (~735-771) deriva una composición sugerida: anchor = candidato Hotel/Package/Assistance con mayor `Adults + Children`; fallback `TransferBookings.Max(Passengers)`; `ambiguous = true` si dos candidatos tienen mismo total y distinta composición (warning, no bloqueo); `FlightSegment` no participa hoy. Se usa SOLO como **sugerencia** para pre-rellenar el modal (`GetTransitionReadinessAsync` ~668). v2.1 lo eleva a sugerencia del total declarado (que el agente confirma/ajusta); el total persistido sigue siendo la **fuente única**.

## 3. Decisión (v2.1)

### 3.1 Resumen

1. **Presupuesto (`Budget → InManagement`):** sin cambios — solo cantidad (`declaredPax > 0`) + ≥1 servicio. **No** nominales aquí.
2. **El total de la reserva sigue siendo la única fuente de verdad de quiénes viajan y el dato persistido**, pero se **autocompleta/sugiere** desde los servicios. El agente confirma/ajusta. La sugerencia **nunca pisa en silencio** lo que el agente ya cargó (§6.4).
3. **Por defecto cada servicio es para TODOS los pasajeros de la reserva** (default seguro, cero clics).
4. **El SET del servicio se define por UNA sola regla determinística (§3.2):** asignaciones explícitas si existen; si no, todos los pasajeros de la reserva. **La cantidad propia del servicio nunca entra en esta resolución.**
5. **El gate de nombres por servicio opera sobre el SET del servicio.** La matriz por tipo (§5) opera sobre ese set, completo (sin recorte por `declaredPax` — B2).
6. **Chokepoints/call-sites del gate (DÓNDE corre): IDÉNTICOS** a v1 (§7). El cierre de B1 sigue vigente. Cambia la **REGLA** (resolver el set), no el cableado.
7. **Limpieza transaccional de asignaciones al borrar un servicio (M1):** requisito de implementación (§4.3, §7bis).
8. **Auditoría de alta/baja de asignaciones (§6.5).**
9. **Voucher:** refuerzo por tipo + titular validando el **set del servicio** (§6.1). **Factura:** sin cambios (§6.2). **Motor:** no se toca (§6.3).

### 3.2 Regla unificada del SET del servicio (reemplaza la ambigüedad de v2)

> **El ÚNICO determinante del subconjunto de un servicio son las ASIGNACIONES EXPLÍCITAS (`PassengerServiceAssignment`).**
>
> 1. **Servicio CON asignaciones** (`PassengerServiceAssignment` con ese `ServiceType` + `ServiceId`) → SET = exactamente esos pasajeros.
> 2. **Servicio SIN asignaciones** → SET = **todos los pasajeros de la reserva** (default seguro).
>
> La **cantidad propia del servicio** (`HotelBooking.Adults/Children`, etc.) **NUNCA achica el set**. Se usa solo para sugerir el total (autocompletado) y como dato operativo. **No existe "primeros N por Id".**

**Consecuencia (cierra M2 sin regla extra):** si el agente quería "solo algunos" pero **NO asignó a nadie**, el set = todos y el gate pide **TODOS** los nombres. Pide **de más, nunca de menos** = seguro, sin adivinar. Para lograr "solo los adultos", el agente **DEBE asignar explícitamente** a los adultos (acción deliberada y auditada, §6.5). No se infiere identidad de ninguna cantidad.

Esta regla es determinística: dado el estado persistido (asignaciones + pasajeros de la reserva), el set queda definido sin ambigüedad y es testeable sin DB pasando las dos colecciones.

**Caso del dueño que motiva el modelo:** reserva `2A + 1C`. Una excursión solo para los 2 adultos se logra **asignando los 2 adultos** a esa excursión → su set son los 2 adultos → el gate pide 2 nombres, **no** el del menor. El aéreo de la misma reserva, **sin asignaciones**, tiene set = los 3 → pide los 3. Sin asignar, la excursión también pediría los 3 (de más, seguro).

### 3.3 Definición del TITULAR (lead) sobre el set — determinística

> **Titular del set = primer `Passenger` del SET por `Id` ascendente.**

Para Hotel/Traslado (que solo exigen titular), el titular es el primer pasajero **del set del servicio**. Si el servicio no tiene asignaciones, el set = toda la reserva y el titular coincide con el de v1. Estable (Id inmutable), sin campo nuevo, sin migración, testeable.

## 4. Mecanismo del subconjunto (`PassengerServiceAssignment`)

### 4.1 Por qué reusar `PassengerServiceAssignment` y no inventar nada

Ya existe, ya está migrado, ya tiene índices (incluido `(ServiceType, ServiceId)`) y constraint único. Es exactamente el vínculo pasajero↔servicio que el modelo necesita. Su metadata (`RoomNumber`, `SeatNumber`) es ortogonal. Alternativas (columna "alcance" + tabla aparte, o lista de Ids en JSON) son redundantes y exigirían migración. Rechazadas (§12).

### 4.2 Resolución del SET en el gate (forma del contrato)

El gate necesita, por servicio: el `ServiceType` (lo sabe el call-site), el `ServiceId` (el Id del booking/segment que se está resolviendo) y los pasajeros de la reserva. La capa de infraestructura resuelve:

```
assignedIds = PassengerServiceAssignments
                .Where(a => a.ServiceType == t && a.ServiceId == serviceId)
                .Select(a => a.PassengerId)
set = assignedIds.Any()
        ? reserva.Passengers.Where(p => assignedIds.Contains(p.Id))
        : reserva.Passengers   // sin asignaciones -> todos (default seguro)
```

`PassengerNominalRules` recibe el **set ya resuelto** (lista de `Passenger`), **no** la reserva, **no** las asignaciones, **no** la cantidad propia. Sigue siendo **clase pura** (no toca EF ni DB). La resolución del set vive en `BookingService` (infraestructura), igual que hoy vive ahí la carga de `Passengers`. El espejo del genérico hace lo mismo en `ReservaService`.

### 4.3 Limpieza transaccional de asignaciones al borrar un servicio (M1 — requisito de implementación)

**Problema verificado:** `DeleteFlightAsync` (`BookingService.cs:742-760`) y los demás `Delete*Async` (`DeleteHotelAsync`, `DeletePackageAsync`, `DeleteTransferAsync`, `DeleteAssistanceAsync` y el borrado del genérico) **borran el servicio pero NO borran sus `PassengerServiceAssignments`**. Como `ServiceId` es soft-FK, EF no cascadea. Resultado: filas huérfanas. Como `ServiceId` es autoincremental por tabla y se puede **reusar** tras un borrado, un servicio nuevo con el mismo `(ServiceType, ServiceId)` **heredaría el set del servicio muerto** → el gate validaría/voucher-earía a los pasajeros equivocados, en silencio.

**Requisito (no opcional):** todo `Delete*Async` debe, **en la misma transacción que borra el servicio**, ejecutar:

```
DELETE FROM PassengerServiceAssignments WHERE ServiceType = <t> AND ServiceId = <id>
```

- **Atomicidad:** si el provider es relacional, envolver borrado del servicio + borrado de asignaciones en una transacción única; si por algún camino se hace en una sola `SaveChangesAsync`, EF ya las agrupa. El criterio: **nunca** debe quedar el servicio borrado con asignaciones vivas, ni al revés.
- **Auditoría:** la baja de asignaciones por cascada de borrado de servicio se audita igual que la baja manual (§6.5), con un motivo que distinga "baja por borrado de servicio".
- **Tests:** ver §13 (orphan assignments + reuso de Id).

Esto es **bloqueante de integridad** y forma parte del alcance de v2.1, no de un follow-up.

## 5. Regla por tipo de servicio (v2.1 — sobre EL SET COMPLETO del servicio)

`PassengerNominalRules` pasa de "primeros `declaredPax` de la reserva" a "**todos los pasajeros del SET**". La matriz de exigencia por tipo **no cambia**; cambia el universo (set completo, sin recorte):

| Tipo | Pasajeros exigidos | Campos obligatorios por pasajero |
|------|--------------------|----------------------------------|
| **Aéreo (Flight)** | TODOS los del SET | `FullName` + `DocumentType` + `DocumentNumber` |
| **Asistencia** | TODOS los del SET | `FullName` + `DocumentNumber` + `BirthDate` |
| **Hotel** | Solo el **titular del SET** (§3.3) | `FullName` |
| **Traslado (Transfer)** | Solo el **titular del SET** | `FullName` |
| **Paquete** | TODOS los del SET | `FullName` |
| **Genérico** | TODOS los del SET | `FullName` |

Justificaciones por tipo (aéreo necesita documento; asistencia necesita fecha de nacimiento para la prima; hotel/traslado alcanza con titular; paquete/genérico nombre de todos sin documento) **idénticas a v1**.

### 5.1 Nuevo contrato del helper (v2.1) — CAMBIO SEMÁNTICO, no solo de firma

```
// v1 (a reemplazar):
//   EnsureCovered(Reserva reserva, ServiceKind kind)
//   GetMissing(Reserva reserva, ServiceKind kind)
//   GetLeadPassenger(Reserva reserva)
// v2.1:
PassengerNominalRules.EnsureCovered(IReadOnlyList<Passenger> serviceSet, ServiceKind kind)   // lanza con mensaje accionable
PassengerNominalRules.GetMissing(IReadOnlyList<Passenger> serviceSet, ServiceKind kind)       // forma pura, SIN número de documento
PassengerNominalRules.GetLeadPassenger(IReadOnlyList<Passenger> serviceSet)                   // titular = primer por Id DEL SET
```

> **B2 — esto es un cambio SEMÁNTICO, no solo de firma.** En v1 el universo era `AdultCount + ChildCount + InfantCount` y el helper validaba **solo los primeros `declaredPax` pasajeros por Id** (`CheckAllDeclared` líneas 176-183). En v2.1 **se elimina el recorte**: el universo es `serviceSet` y se valida **cada fila del set**. Consecuencias a internalizar al implementar:
> - Ya **no existe** `declaredPax` dentro del helper. El "cuántos" es `serviceSet.Count`.
> - El chequeo "faltan filas respecto de la cantidad declarada" (líneas 168-172) desaparece como tal: el set es la lista concreta a validar; un set vacío es error accionable (§5.2). La comparación cantidad-declarada-vs-filas-cargadas, si hace falta como red, vive **fuera** del helper (en el flujo de readiness de la reserva), no en la regla del set.
> - "TODOS los del SET" = iterar **todo** `serviceSet`. Si el set tiene 3 y uno está incompleto, falla por ese uno, aunque `declaredPax` fuera 2.

### 5.2 Set vacío y relación con la cantidad propia

- **Set vacío** = error accionable ("el servicio no tiene pasajeros para validar"). No debería ocurrir (la reserva pasó `Budget → InManagement` con `declaredPax > 0` y el set por defecto es toda la reserva), pero el helper lo rechaza defensivamente. Puede ocurrir si las asignaciones apuntan a pasajeros borrados; entonces el set queda vacío y se rechaza (no se "cae" a todos).
- **La cantidad propia no entra en el helper** (§2.7, §3.2). `PassengerNominalRules` no lee `HotelBooking.Adults` ni `FlightSegment.PassengerCount`. Mantener la regla pura sobre el set evita acoplarla a cinco formas distintas de declarar cantidad.

## 6. Impacto en voucher / factura / motor / autocompletado / auditoría

### 6.1 Voucher (`VoucherService.cs`)

Igual criterio que v1 pero sobre el **set del servicio voucher-eado**: aéreo → nombre+documento de todos los del set; asistencia → +nacimiento; hotel/traslado → titular del set; paquete/genérico → nombre de todos los del set. Voucher de alcance reserva → como el servicio resuelto ya pasó el gate, su set ya está completo; mantener ≥1 pasajero + el guard existente de "servicios sin resolver". **Verificar en implementación** que ese guard corre antes de emitir y que la resolución del set del voucher usa la misma función que el gate (no reimplementar).

### 6.2 Factura (`InvoiceService.cs`) — SIN CAMBIOS

Decisión del dueño (sigue): no se factura antes de confirmar el servicio; la factura se emite al pagador, no a los pasajeros. No se agrega guard de pasajeros. No entran agentes fiscales por este ADR.

### 6.3 Motor automático (`ReservaAutoStateService`) — NO se modifica

La invariante "no se llega a Confirmed sin nombres" se sostiene porque el gate corre en todos los call-sites que resuelven (§7), ahora con la regla del set. El motor nunca observa "todo resuelto" con el set incompleto.

### 6.4 Autocompletado del total (sugerencia confirmable) — NUNCA pisa en silencio

- **Fuente:** `ComputePaxCompositionFromServices` (§2.8).
- **Discrepancia entre servicios:** `ambiguous = true` → warning, **no bloqueo**. El agente decide.
- **El total persistido sigue siendo la fuente única.** La sugerencia **se ofrece y el agente confirma**; **no sobrescribe en silencio** lo que ya cargó. El autocompletado solo evita tipear dos veces.
- **Dónde se expone:** hoy en `GetTransitionReadinessAsync` (~668). v2.1 puede exponerla también en alta/edición de servicios (UX, entra por el gate UX §10.7).

### 6.5 Auditoría del alta/baja de asignaciones (NUEVO en v2.1)

**Por qué:** las asignaciones ahora **determinan quién aparece en un ticket/voucher** y a quién se le exigen nombre/documento. Un cambio de asignación cambia qué se emite. Eso debe ser trazable (quién/cuándo), como cualquier dato que afecta un comprobante.

**Mecanismo:** reusar el existente `IAuditService.LogBusinessEventAsync(action, entityName, entityId, details, userId, userName, ct)` (verificado, `IAuditService.cs:38`) y agregar constantes en `AuditActions` siguiendo la convención del archivo (PascalCase, verbo en pasado):

- `PassengerAssignedToService` — alta de asignación. Se loguea en `CreateAssignmentAsync` tras persistir.
- `PassengerUnassignedFromService` — baja de asignación. Se loguea en `RemoveAssignmentAsync`.
- `PassengerUnassignedFromServiceByDelete` — baja por cascada al borrar el servicio (§4.3). Distingue la baja deliberada de la baja por borrado en las queries de auditoría.
- `entityName` = `"PassengerServiceAssignment"`; `entityId` = `PublicId`/`Id` de la asignación (o, para la baja por borrado, el del servicio borrado).

**Contenido del `details` (JSON) — sin datos sensibles:** `serviceType`, `serviceId` (o servicePublicId), `passengerId` (o passengerPublicId), `reservaId`. **NUNCA** `DocumentNumber` (misma regla que el gate). No es obligatorio loguear el `FullName`; con el id alcanza para la traza.

> **Nota de alcance:** el alta/baja de asignaciones vive hoy en `ReservaService` (líneas 404/531), que **no inyecta `IAuditService`** (verificado). Agregar la dependencia es parte de v2.1. La baja por borrado (§4.3) ocurre en `BookingService`; ese servicio debe auditar la cascada en el mismo punto donde limpia las asignaciones.

## 7. Chokepoints del gate (DÓNDE corre) — IDÉNTICOS A v1

**No cambian.** El envoltorio único `EnsureNominalCoverageBeforeResolvingAsync` se invoca, antes de persistir el status resuelto y antes de `UpdateBalanceAsync`, en (todos en `BookingService.cs`, verificados):

- **Status:** `MarkFlightTicketIssuedAsync`, `MarkTransferNoConfirmationRequiredAsync`, `UpdateHotelStatusAsync`, `UpdateTransferStatusAsync`, `UpdatePackageStatusAsync`, `UpdateFlightStatusAsync`, `UpdateAssistanceStatusAsync` (solo cuando el nuevo status resuelve).
- **B1 creación:** `CreateFlightAsync`, `CreateHotelAsync`, `CreateTransferAsync`, `CreatePackageAsync`, `CreateAssistanceAsync`, `CreateGenericAsync` (solo si el alta deja el servicio resuelto), incluyendo el camino de catálogo (`Create*WithCatalogAsync`).
- **B1 edición:** `UpdateFlightAsync`, `UpdateHotelAsync`, `UpdateTransferAsync`, `UpdatePackageAsync`, `UpdateAssistanceAsync` y edición del genérico (cuando dejan el servicio resuelto).
- **Genérico:** el gate espejo `EnsureGenericNominalCoverageBeforeResolvingAsync` en `ReservaService` (verificado, líneas 2802-2818) se realinea a la regla del set.

**Lo único que cambia en estos call-sites:** el envoltorio ahora resuelve y pasa al helper el **set del servicio** (§4.2, con el `ServiceId`/`ServiceType` del booking/segment que está resolviendo), en lugar de la reserva completa. La firma del envoltorio gana `ServiceId`/`ServiceType` (los tiene a mano: es la entidad que resuelve).

## 7bis. Chokepoints de LIMPIEZA (M1) — los Delete*

Distintos de los del gate. Todos en `BookingService.cs` (verificado el primero, el resto por simetría de patrón):

- `DeleteFlightAsync` (742-760), `DeleteHotelAsync`, `DeletePackageAsync`, `DeleteTransferAsync`, `DeleteAssistanceAsync`, y el borrado del **genérico** (`ServicioReserva`).
- Cada uno: borra el servicio **y** sus `PassengerServiceAssignments` (`WHERE ServiceType=t AND ServiceId=id`) en la misma transacción, y audita la cascada (§6.5).

## 8. Migración

**Ninguna migración de esquema.** `PassengerServiceAssignment` (tabla + índices) y los campos de cantidad por servicio y los de `Passenger` ya existen (verificado §2.5, §2.6, §2.7). Sin clientes reales operando; reservas históricas en `Confirmed`/`Traveling` no se re-validan. Servicios sin asignaciones siguen comportándose como "para todos".

## 9. Decisiones del dueño (abiertas en v2.1)

> La ambigüedad **Q-V2-SUBSET-DEFAULT de v2 queda CERRADA** por la regla unificada (§3.2): no hay opción "primeros N", el subconjunto se expresa **asignando**. No se le pregunta más al dueño.

> **Q-V2-FLIGHTPAX (informativa):** `FlightSegment.PassengerCount` existe y es nullable. En v2.1 **no influye en el set** (como ninguna cantidad propia). Sigue como insumo de la sugerencia del total (si se decide en Q-V2-ANCHOR-FLIGHT). No bloquea.

> **Q-V2-ANCHOR-FLIGHT (solo sugerencia):** ¿`FlightSegment.PassengerCount` debería entrar como candidato anchor en `ComputePaxCompositionFromServices` (hoy no participa)? Solo afecta la **sugerencia** del total, nunca el set ni el conteo real. Recomiendo dejarlo fuera por ahora (no desglosa adultos/menores).

> **Q-V2-ASSIGN-UX (UX, gate obligatorio):** ¿cómo quiere el dueño marcar "este servicio es solo para algunos" en pantalla? (checklist de pasajeros por servicio; o "para todos" por defecto con un botón "elegir pasajeros"). Va por `ux-ui-disenador` antes del front. **No bloquea el backend**: si el front aún no setea asignaciones, todos los servicios quedan "para todos" (default correcto).

Las decisiones de exigencia por tipo siguen cerradas.

## 10. Plan de implementación (orden backend; qué exponer al front) — sin código, sin tiempos

1. **Dominio puro — refactor de `PassengerNominalRules` (v1 → v2.1).** Cambiar las tres firmas para recibir `IReadOnlyList<Passenger> serviceSet`. **Eliminar el recorte por `declaredPax`** (líneas 176-183) y todo uso de `AdultCount/ChildCount/InfantCount` dentro del helper (B2). `CheckAllDeclared` → "validar TODO el set"; `CheckLeadHasName`/`GetLeadPassenger` → titular = primer por Id **del set**. Rechazar set vacío con mensaje accionable. Mantener mensajes sin número de documento. Actualizar `PassengerNominalRulesTests` para alimentar **sets explícitos**.

2. **Resolución del SET en `BookingService` (infraestructura).** Cambiar `EnsureNominalCoverageBeforeResolvingAsync` para recibir `ServiceId`/`ServiceType`, resolver el set (§4.2: consultar `PassengerServiceAssignments` por `(ServiceType, ServiceId)`; si vacío → `reserva.Passengers`) y pasar el set a `EnsureCovered`. Mantener la guarda "solo transición no-resuelto → resuelto" idéntica. Realinear el espejo del genérico en `ReservaService` (líneas 2802-2818) a la misma resolución.

3. **Adecuar los call-sites del gate (§7).** Solo pasar `ServiceId`/`ServiceType` al envoltorio. DÓNDE corre y la guarda de B1 no cambian.

4. **Limpieza transaccional de asignaciones en los `Delete*` (M1, §4.3, §7bis).** En cada `Delete*Async` de `BookingService` (incluido el genérico): borrar las `PassengerServiceAssignments` del servicio en la misma transacción que el borrado del servicio. **Bloqueante de integridad.**

5. **Auditoría de asignaciones (§6.5).** Agregar las constantes a `AuditActions`; inyectar/usar `IAuditService` en `CreateAssignmentAsync` y `RemoveAssignmentAsync` (`ReservaService`) y en la cascada de borrado (`BookingService`). `details` sin datos sensibles.

6. **Voucher.** Adecuar `VoucherService` para validar el set del servicio voucher-eado (§6.1), reusando la resolución del set del gate. Verificar el orden con el guard de "servicios sin resolver".

7. **Factura.** Sin cambios. No tocar `InvoiceService`. No invocar agentes fiscales.

8. **Autocompletado del total (exponer al front).** Reusar `ComputePaxCompositionFromServices` como sugerencia confirmable; exponer composición sugerida + flag `ambiguous` en alta/edición de servicios. La sugerencia **no** sobrescribe el total sola.

9. **Front (con gate UX obligatorio).** **NO implementar sin `ux-ui-disenador` + respuestas del dueño.** El front necesita:
   - Saber, **por servicio**, qué nombres faltan **del set de ESE servicio**. Exponer `GetMissing` por servicio con el set ya resuelto (mismo contrato que el back).
   - Marcar "este servicio es solo para algunos" y elegir los pasajeros (asignaciones) cuando ya hay nombres (Q-V2-ASSIGN-UX).
   - Mostrar **"X de N"** (tamaño del set vs total de la reserva) por servicio, para que el subconjunto sea visible (parte de la protección B1, §11).
   - Mostrar el autocompletado/sugerencia del total y el warning `ambiguous`, sin pisar lo cargado.
   - Mover la carga de nombres al momento de emitir/confirmar cada servicio.

10. **Actualizar tests existentes que rompan.** Los que resuelven servicios sin pasajeros (`BookingServiceCostMaskingTests` y similares): con la regla de set, un servicio sin asignaciones exige a toda la reserva, así que deben cargar los pasajeros. Tests que prueben el subconjunto deben crear asignaciones.

11. **Regresión del motor / reconciliación.** El job nocturno / `EvaluateAndApplyAsync` NO ejecuta el gate y no regresa reservas históricas. Verificar.

## 11. Riesgos

**Operativo / fiscal:**
- Emitir aéreo sin nombre/documento del set → ticket inservible. Mitigado por la regla del set completo (B2: ya no recorta).
- Asistencia sin fecha de nacimiento del set → prima mal calculada. Mitigado.
- Un servicio "para algunos" con set mal armado → emisión con la persona equivocada. **Mitigación (B1, ver §11.1).**

### 11.1 Por qué la protección de v2.1 es suficiente (cierra B1)

El reviewer preguntó si hace falta un piso "set < cantidad propia ⇒ rechazo". **No hace falta, y sería incorrecto:** chocaría con el caso legítimo "excursión de 2 de 3" (un set deliberadamente menor que el total). La protección de v2.1 es la combinación:

1. **Default seguro = TODOS.** Sin acción del agente, el gate pide a toda la reserva (de más, nunca de menos).
2. **Subconjunto SOLO explícito.** Achicar el set requiere **asignar** — una acción deliberada, no un efecto colateral de un default de cantidad (§2.7).
3. **Auditoría (§6.5).** Cada asignación/desasignación queda registrada (quién/cuándo): si alguien achicó un set, hay traza.
4. **UI que muestra el conteo del set ("X de N", §10.9).** El agente ve cuántos pasajeros cubre cada servicio frente al total; un subconjunto accidental es visible.

El "bypass" se reduce, entonces, a que **el agente asigne explícitamente menos gente** — una decisión deliberada, auditada, con el default en "todos" y el conteo a la vista. No es un bypass silencioso. Un piso automático no agregaría seguridad real (el caso legítimo lo volvería falso positivo) y sí rompería el modelo del dueño.

**Técnicos:**
- **Bypass B1 (call-sites):** sin cambios; cubierto por el envoltorio único en todos los call-sites + test del bypass. Riesgo residual: una ruta futura que resuelva sin pasar por el envoltorio. Mitigación: helper único + comentario + test.
- **Doble fuente front/back:** el front debe pedir los faltantes **del set**. Exponer `GetMissing` por servicio con el set resuelto evita divergencia.
- **Asignaciones huérfanas (M1):** **cerrado** por la limpieza transaccional (§4.3). Test obligatorio de orphan + reuso de Id.
- **Set vacío inesperado:** asignaciones a pasajeros borrados → set vacío → el helper rechaza con mensaje accionable (no se cae a "todos").

**Seguridad / datos:**
- Mensajes y logs (incluido el `details` de auditoría) sin número de documento.
- La consulta del set por servicio no expone datos sensibles adicionales.

## 12. Alternativas consideradas (v2.1)

1. **Gate por total de la reserva (v1).** Rechazada por el dueño: no modela "para algunos" (excursión de adultos pediría el nombre del menor).
2. **Derivar el set de la cantidad propia del servicio** (o "primeros N por Id"). Rechazada (M3): la cantidad propia tiene defaults → un servicio parecería "para algunos" por accidente → se pediría de menos (inseguro). "Primeros por Id" no es semánticamente "los 2 adultos". El "quiénes" se expresa **asignando**.
3. **Piso "set < cantidad propia ⇒ rechazo".** Rechazada (§11.1): rompe el caso legítimo "2 de 3" y no agrega seguridad real frente a la combinación default-seguro + asignación-explícita + auditoría + UI.
4. **Nueva columna "alcance" + tabla de Ids.** Rechazada: redundante con `PassengerServiceAssignment` (ya existe/migrado/indexado) y exigiría migración.
5. **`PassengerNominalRules` lee asignaciones/cantidad propia directamente.** Rechazada: acoplaría la clase pura a EF y a cinco formas de declarar cantidad. Se mantiene pura recibiendo el set ya resuelto.
6. **Cascada de borrado por FK de DB.** Rechazada: `ServiceId` es soft-FK (la tabla destino varía); EF no puede cascadear. La integridad la maneja el backend (§4.3).
7. **Gate en el motor / campo de titular en `Passenger`.** Rechazadas (igual que v1).

## 13. Estrategia de testing (v2.1)

- **Unit (`PassengerNominalRules`, sin DB) — sets explícitos:**
  - Matriz por tipo sobre el set; titular = primer Id **del set**.
  - Aéreo exige documento de **todos** los del set; asistencia exige nacimiento; hotel/traslado solo titular del set; paquete/genérico nombre de todos.
  - **B2 (regresión del cambio semántico):** set de 3 con el 3.º incompleto → **rechazo** (en v1, con `declaredPax = 2`, habría pasado). Caso explícito que demuestra que ya no se recorta.
  - **Set vacío → rechazo** con mensaje accionable.
  - Set = subconjunto (titular del set ≠ titular de la reserva).
- **Resolución del set (`BookingService`, integración):**
  - Servicio **sin** asignaciones → set = toda la reserva.
  - Servicio **con** asignaciones → set = solo esos.
  - **Defaults (cierra M3):** reserva de 3 con un Hotel `Adults=2` (default) **sin** asignaciones → el gate exige titular **de la reserva** y NO trata al hotel como "para 2"; un Paquete `Adults=2` sin asignaciones exige los **3** nombres (no 2).
  - **Excursión 2 de 3:** reserva `2A + 1C`, excursión (Paquete) **asignada a los 2 adultos** → emitir exige los 2 adultos, **NO** el menor; el aéreo (sin asignaciones) exige los 3.
- **Bypass B1:** crear un servicio ya resuelto en `InManagement` sin nombres del set → rechazado, la reserva NO pasa a `Confirmed`. Repetir por tipo, con y sin asignaciones.
- **Edición a resuelto:** transición no-resuelto → resuelto sin nombres del set → rechazo.
- **Resolución normal:** con nombres del set completos → OK + auto-confirma; faltando → rechazo + permanece en `InManagement`.
- **Orphan / reuso de Id (M1, obligatorio):**
  - Borrar un servicio borra sus asignaciones (no quedan filas con ese `(ServiceType, ServiceId)`).
  - Tras borrar, crear un servicio nuevo que reuse el `ServiceId` → su set es **el default (todos)**, NO hereda asignaciones del muerto.
  - Asignación a pasajero borrado → set vacío → rechazo (no se cae a "todos").
- **Auditoría (§6.5):** alta de asignación → un evento `PassengerAssignedToService`; baja manual → `PassengerUnassignedFromService`; baja por borrado de servicio → `PassengerUnassignedFromServiceByDelete`; ningún `details` contiene número de documento.
- **Autocompletado/sugerencia:** servicios coincidentes → sugerencia correcta; discrepancia → `ambiguous = true` (warning, no bloqueo); el total persistido **no** cambia solo.
- **Voucher:** rechaza voucher de un vuelo cuyo set tiene un pasajero sin documento; acepta voucher de hotel con titular del set nombrado.
- **Motor / reconciliación:** la cura nocturna no regresa reservas ni ejecuta el gate.
- **Seguridad:** mensajes y logs sin número de documento.
- **Tests existentes a actualizar:** los que resuelven servicios sin pasajeros (cargar el set por defecto = pasajeros de la reserva, o crear asignaciones para el subconjunto).

## 14. Rollback

Cambio de regla + reuso de un modelo existente (sin esquema nuevo): rollback = revertir el commit. Sin feature flag (decisión del dueño). Sin migración → sin rollback de datos. Si la regla del set resulta incorrecta para un tipo, se ajusta `PassengerNominalRules`/la resolución del set en un único lugar y se re-despliega. Como nada está desplegado, no hay datos productivos en riesgo. **Atención:** la limpieza transaccional de asignaciones (M1) y la auditoría son cambios de comportamiento; revertir el commit los revierte juntos (no quedan a medias).

## 15. Resumen de cambios v1 → v2 → v2.1

| Aspecto | v1 (implementada, sin desplegar) | v2 | v2.1 (este ADR) |
|---|---|---|---|
| Universo del gate | Primeros `declaredPax` de la reserva por Id | SET del servicio (ambiguo: asignaciones **o** cantidad) | **SET completo del servicio = asignaciones si existen, si no toda la reserva; la cantidad propia NUNCA achica** |
| Recorte "primeros N por Id" | **Presente** (`CheckAllDeclared` 176-183) | sin resolver | **ELIMINADO (B2, cambio semántico)** |
| Determinante del subconjunto | n/a | asignaciones **o** cantidad propia (ambiguo) | **SOLO asignaciones explícitas** |
| Firma `EnsureCovered/GetMissing/GetLead` | `(Reserva, ...)` | `(IReadOnlyList<Passenger> set, ...)` | `(IReadOnlyList<Passenger> set, ...)` |
| Titular | Primer pasajero de la reserva por Id | Primer del set por Id | Primer del set por Id |
| Limpieza de asignaciones al borrar servicio | **No existe** (huérfanas) | "verificar después" | **Requisito transaccional (M1)** |
| Auditoría alta/baja asignaciones | No | No | **Sí (3 eventos, `details` sin documento)** |
| Total de la reserva | Tipeado a mano, fuente única | Autocompletado, fuente única | Autocompletado que **nunca pisa en silencio**, fuente única |
| Protección B1 del subconjunto | n/a | piso posible (sin cerrar) | **default seguro + asignación explícita + auditoría + UI "X de N"; sin piso automático** |
| Matriz de exigencia por tipo | aéreo=doc, asist=nacimiento, hotel/traslado=titular, paq/gen=nombre | idéntica | **idéntica** (cambia el universo, no qué se pide) |
| Chokepoints del gate / cierre B1 | ~20 call-sites + espejo genérico | idénticos | **idénticos** |
| Migración | Ninguna | Ninguna | **Ninguna** |

**Lo que NO cambia:** el cierre del bypass B1, los call-sites del gate, el motor sin tocar, `Budget → InManagement` solo cantidad, factura sin cambios, mensajes sin documento, titular sin campo nuevo, clase de dominio pura.
