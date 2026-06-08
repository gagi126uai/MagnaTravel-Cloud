# ADR-020 — Rediseño completo del ciclo de vida de la reserva

- **Estado:** Propuesto — **round 2** (fixes del review round 1 aplicados: B1–B6, M1–M7 y 5 menores; pendiente re-review de `software-architect-reviewer` antes de implementar)
- **Fecha:** 2026-06-07 (round 1) / 2026-06-07 (round 2)
- **Decisor de producto:** Gastón (decisiones textuales en `docs/ux/guia-ux-gaston.md`, sección "Ciclo de vida de la reserva (REDISEÑO — decisiones de Gastón, 2026-06-07)", líneas 46-84)
- **Reemplaza:** el rediseño Fase A-D del 2026-05-30 (ciclo dual Budget→Sold→…→ToSettle detrás del flag `EnableSoldToSettleStates`). "Vendida" (Sold) muere.
- **Regla dura del dueño (2026-06-07):** "BASTA DE LLAVES". Este rediseño se construye DIRECTO, sin feature flag nuevo, y ELIMINA el flag `EnableSoldToSettleStates` con todo su andamiaje. Rollback = `git revert` + migraciones `Down`.
- **Regla dura del dueño:** sin estimaciones de tiempo. Este ADR define QUÉ y en qué ORDEN, nunca cuánto tarda.

**Historial de revisiones:**
- Round 1: versión inicial (review: Changes Required, 6 bloqueantes + 7 majors + 5 menores).
- Round 2: `Quotation→Lost` entra a la matriz con revert por historial (B1, decisión del dueño); migración partida en `Adr020_M1` (F1) + `Adr020_M2` (F2) con `Down` de orden explícito que restaura `Balance` (B2+B6); orden de deploy con downtime declarado (B3); resolución vs confirmación del aéreo separadas en dos hechos (`ConfirmedAt` PNR vs `TicketIssuedAt` emisión) + default del aéreo nuevo deja de ser "HK" (B4, decisión del dueño); `Cancelled` manual sin factura viva se INTRODUCE (B5, decisión del dueño); predicados SQL del backfill espejan `MapGenericStatus` por construcción (M1); contrato real del motor: saves separados post-commit + reconciliación nocturna (M2); diff de Balance en F0 + job cierra con `Balance <= 0` (M3); gate sin-pagos para `→Lost` (M4); muere el check "tiene servicios" del revert a Budget y se unifican las dos copias divergentes (M5); inventario frontend completado + criterio de cierre F6 (M6); INV-020-06 acotado + log de estado en los 7 sitios que escriben `Status` por fuera (M7); menores (nombre `ConvertToFileAsync`, cref CS1574, fallback de notificación a admins, tab `"reserved"→"confirmed"`, índice y regla de unicidad de autorizaciones).

---

## 1. Contexto

### 1.1 El problema de negocio

Gastón detectó (2026-06-07, sufriéndolo en vivo) tres problemas estructurales del ciclo actual:

1. **Se puede crear una reserva directamente "Confirmada"** — `ReservaService.CreateReservaAsync` acepta `request.Status` crudo (`src/TravelApi.Infrastructure/Services/ReservaService.cs:1419-1421`) y `QuoteService.ConvertToFileAsync` nace en `Confirmed` con el flag OFF (`src/TravelApi.Infrastructure/Services/QuoteService.cs:335-342`). No existe la etapa comercial real (cotización → presupuesto → gestión).
2. **Un servicio "Solicitado" genera deuda del cliente** — `WorkflowStatusHelper.CountsForReservaBalance` devuelve `true` para `Solicitado` Y `Confirmado` (`src/TravelApi.Domain/Entities/WorkflowStatusHelper.cs:38-41`), así que `ReservaMoneyCalculator` suma al saldo servicios que el operador todavía no confirmó. "En pendiente aparece dinero cuando está solicitado" (textual del dueño).
3. **No se puede borrar un servicio si la reserva salió de Budget** — `DeleteGuards.GetServiceDeleteBlockReasonAsync` bloquea el delete para cualquier estado ≠ Budget (`src/TravelApi.Infrastructure/Services/Reservations/DeleteGuards.cs:88-93`, regla C26). Con el ciclo dual prendido, una reserva en `Sold` traba el borrado de un borrador de servicio que jamás se pidió a ningún operador. Este es el error puntual que disparó el rediseño ("no se puede eliminar el servicio: la reserva esta en estado 'Sold'").

Además, el ciclo dual (flag `EnableSoldToSettleStates`, 4 matrices de transición, dos ramas en cada consumidor) es deuda de complejidad que el dueño ordenó eliminar: el producto está en desarrollo, él es el único usuario, y el dual-cycle solo existía como mecanismo de rollback que ya no quiere pagar.

### 1.2 Estado actual del código (VERIFICADO 2026-06-07)

**Estados persistidos** (`src/TravelApi.Domain/Entities/Reserva.cs:42-73`, clase `EstadoReserva`, `Status` es string):
`Budget`, `Sold` (flag), `Confirmed`, `Traveling`, `ToSettle` (flag), `Closed`, `Cancelled`, `PendingOperatorRefund` + literal legacy `'Archived'` (soft-delete, presente en el CHECK).

**CHECK constraint:** `chk_TravelFiles_status_valid` con 9 valores, tabla `"TravelFiles"` (migración `src/TravelApi.Infrastructure/Persistence/Migrations/App/20260530130000_ReservaSoldToSettleStates.cs:50-66`).

**Máquina de estados:** 4 matrices estáticas en `ReservaService.cs:731-787` (forward/revert × clásico/nuevo), elegidas en runtime por el flag. Gates: `EnsureReadinessForSaleAsync` (≥1 servicio + normalización a Solicitado + pax nominales, `:2130`), `GetUnconfirmedServicesBlockReasonAsync` (gate de servicios sin confirmar), `EnsureCanStartTravelingAsync`, `EnsureCanCloseAndStampClosedAt` (bloquea solo `Balance > 0`, `:2229`). `UpdateStatusAsync` (`:1933`) bifurca en `ApplyClassicTransitionAsync` (`:2017`) / `ApplySoldToSettleTransitionAsync` (`:2066`). `Cancelled` se permite desde cualquier estado (la cancelación real la maneja su flujo propio, ADR-002).

**Job de automatización:** `ReservaLifecycleAutomationService` (Hangfire daily): repara `EndDate` null en Traveling, promueve `Confirmed→Traveling` (StartDate ≤ hoy, con chequeo de capacidad), cierra `Traveling→Closed` (EndDate < hoy AND `Balance == 0`, predicado en `ReservaLifecycleAutomationService.cs:216`). NO toca ToSettle (desvío manual). Defensa de concurrencia: re-lee el estado antes de persistir.

**Estados de servicio (por tipo):**
- `HotelBooking`/`PackageBooking`/`TransferBooking`/`AssistanceBooking`: `Status` string libre, default `"Solicitado"`.
- `FlightSegment.Status`: `MaxLength(2)`, códigos IATA, default `"HK"` (`src/TravelApi.Domain/Entities/FlightSegment.cs:75`). Tiene `TicketNumber` (nullable, `:66`) y `ConfirmationNumber`, pero **NO existe ningún campo de emisión** (ni timestamp ni marca). `WorkflowStatusHelper.MapFlightStatus` (`WorkflowStatusHelper.cs:14-24`): HK/TK/KK/KL → Confirmado; UN/UC/HX/NO → Cancelado; resto (incluido cualquier código desconocido) → Solicitado.
- `ServicioReserva` genérico: `ReservationStatuses` = Borrador/Solicitado/Confirmado/Emitido/Cancelado, default `"Borrador"` (`src/TravelApi.Domain/Entities/ServicioReserva.cs:16-23`).
- **No existe `ConfirmedAt` en ningún servicio** (verificado por grep; el único `ConfirmedAt` del dominio es `BookingCancellation.PenaltyConfirmedAt`).
- `ReservaCapacityRules.ConfirmedServiceStatuses` = `{"Confirmado","Emitido","HK","TK","KK","KL"}` (`src/TravelApi.Infrastructure/Services/ReservaCapacityRules.cs:26-29`); lo consumen `GetUnconfirmedServicesBlockReasonAsync` (gate de Traveling) y `GetStatusDowngradeBlockReasonAsync` (`:87-100`, bloqueo de "des-confirmar" un servicio — lo invoca `BookingService` en 10 sitios); `ReservaStatusesRequiringConfirmedServices` = `{Traveling, Closed}`.

**Plata:** `ReservaMoneyCalculator` (Domain, puro) es la fuente única; suma `SalePrice`/`NetCost` de los 6 tipos de servicio filtrando por `CountsForReservaBalance` (Solicitado + Confirmado cuentan; Cancelado no) y calcula `Balance = TotalSale − TotalPaid`. Lo invocan `ReservaService.UpdateBalanceAsync`, `PaymentService.RecalculateReservaBalanceAsync` (`PaymentService.cs:799-821`) y `AfipService` (`:1702`) — las tres copias YA están unificadas sobre el calculador (P1.5). **Patrón de ejecución real:** el recálculo corre como un `SaveChanges` SEPARADO inmediatamente después del `SaveChanges` de la mutación (ej. `AddPaymentAsync:1863-1865`), NO dentro de la misma transacción.

**Deuda con proveedor:** `WorkflowStatusHelper.CountsForSupplierDebtByType` (`WorkflowStatusHelper.cs:58-64`) — SOLO confirmado genera deuda; lo consume `SupplierService.cs:925`. Esta regla NO cambia con este ADR (§4.3).

**Conjuntos de estados "Fase D"** (barrido semántico 2026-05-30) que incluyen Sold/ToSettle y hay que re-mapear:
- `PaymentService.ActiveCollectionStatuses` = {Sold, Confirmed, Traveling, ToSettle} (`PaymentService.cs:41-46`).
- `InvoiceService`: set facturable = {Confirmed, Traveling, ToSettle} (`InvoiceService.cs:67-69`; **Sold NO factura**) + guard explícito anti-facturar-Sold (`:318`).
- `SupplierService.ValidReservationStatuses` = {Sold, Confirmed, Traveling, ToSettle, …} (`SupplierService.cs:19-24`) + query `:286-291` (incluye Budget).
- `TreasuryService.activeStatuses` = {Sold, Confirmed, Traveling, ToSettle} (`TreasuryService.cs:32-37`).
- `AlertService`: viajes urgentes con deuda {Sold, Confirmed, Traveling} (`AlertService.cs:109-111`) y "Próximos inicios" ADR-019 {Sold, Confirmed, Traveling} (`AlertService.cs:177-179`).
- `ReservaService`: `ActiveCount` (`:1075-1078`), `SoldCount`/`ToSettleCount` (`:1083-1084`), filtros de tab `"sold"`/`"to-settle"` y `"reserved"` (que hoy mapea a `Confirmed`, `:2406`) (`:2404-2420`).

**Flag y andamiaje a eliminar:** `OperationalFinanceSettings.EnableSoldToSettleStates` (entidad + columna), `OperationalFinanceSettingsService` (PATCH `:81-84` — verificado: **sin validación cruzada con otros flags**, la GR-013 no lo involucra), `AfipSettingsResponse`, `OperationalFlagsResponse.EnableSoldToSettleStates` + `OperationalFlagsController`, panel admin (`src/TravelWeb/src/components/OperationalFinanceSettingsTab.jsx`), frontend (`OperationalFlagsContext.jsx`, `ReservasPage.jsx`, `ReservaDetailPage.jsx`, `useReservas.js`, `ReservaStatusBadge.jsx`, `ReservaHeader.jsx`, `ReservaStatusChips.jsx`, `ConfirmReservaModal.jsx` — inventario completo verificado por grep, §6 F3).

**Auditoría existente reutilizable:** `ReservaStatusChangeLog` (FromStatus, ToStatus, Direction Forward/Revert, ByUserId/Name, supervisor autorizante, Reason, OccurredAt — `src/TravelApi.Domain/Entities/ReservaStatusChangeLog.cs`). `Notification` (UserId, Message, Type, Priority Normal/Urgent, RelatedEntityId/Type — `src/TravelApi.Domain/Entities/Notification.cs`). `UpcomingStartAlertDismissal` (ADR-019).

**Tests que mueren o cambian:** `ReservaSoldToSettleStatesTests`, `FaseDStateSetTests`, `EstadoReservaCoverageTests` (CHECK 9 valores), `OperationalFlagsControllerTests`, `OperationalFinanceSettingsFlagsTests`, parte de `AlertServiceUpcomingStartsTests`, `ReservaMoneyCalculatorTests`.

### 1.3 Contradicciones encontradas entre supuestos y código

1. El brief asumía que el cálculo de totales "suma todos los servicios sin importar status". **Falso a medias:** ya filtra por `CountsForReservaBalance`, pero ese filtro incluye `Solicitado` — el bug es la definición del filtro, no la ausencia de filtro. Las tres copias del recálculo ya están unificadas sobre `ReservaMoneyCalculator` (los comentarios "suma plana" en `AfipService:1683` y `PaymentService:796` describen el estado ANTERIOR a P1.5).
2. La memoria del proyecto decía que el flag tenía "validación cruzada GR-013". **Falso para este flag:** el código dice explícitamente "sin dependencias con otros flags, por eso NO tiene validación cruzada" (`OperationalFinanceSettingsService.cs:77-84`). La GR-013 involucra a `EnableCancellationDebitNote`/`EnableNewCancellationFlow`; eliminar `EnableSoldToSettleStates` no la toca.
3. `FlightSegment.Status` es `MaxLength(2)` (IATA): NO puede almacenar "Emitido". La resolución del aéreo necesita un campo nuevo (§4.3), no un valor nuevo de status.

### 1.4 Verificaciones adicionales (round 2)

Evidencia levantada para los fixes del review:

1. **`BookingCancellationService` NO lee `Reserva.Balance`** (pregunta 4 del reviewer). Grep exhaustivo de `Balance` en `BookingCancellationService.cs`: las únicas coincidencias son `RemainingBalance` del ledger de crédito de cliente (`:1202,:1245,:1249,:1262`) — entidad propia del BC, sin relación con `Reserva.Balance`. Los cálculos de refund/crédito del flujo de cancelación son independientes de la fórmula de saldo de la reserva. **La fórmula nueva `Balance = ConfirmedSale − TotalPaid` NO impacta refunds ni créditos.**
2. **`AddPaymentAsync` legacy NO tiene gate de estado** (`ReservaService.cs:1849-1868`): acepta pagos sobre una reserva en cualquier estado, incluido Budget. De ahí el gate M4 para `→ Lost` (§4.1).
3. **El gate "volver a Presupuesto" tiene DOS copias divergentes:** `EnsureCanRevertToBudgetAsync` (`:2171-2181`: pagos + facturas + **servicios cargados**) y la copia inline de `RevertStatusAsync` (`:896-903`: pagos + facturas, SIN check de servicios). El propio doc-comment (`:2167-2169`) admite la duplicación. Se unifican en §4.1 (M5).
4. **Sitios que escriben `Reserva.Status` por fuera de `UpdateStatusAsync`/`RevertStatusAsync`:** `BookingCancellationService.cs:821,:1048,:1298,:1842,:2921,:3768` (todas `bc.Reserva.Status = …`, flujo ADR-002) y `ArchiveReservaAsync` (`ReservaService.cs:2246`, `file.Status = "Archived"`). Ninguno escribe `ReservaStatusChangeLog` hoy. Se cubre en §6 F6 (M7).
5. **Patrón de transacción envolvente disponible en el repo:** `DeleteReservaAsync` usa `_context.Database.CreateExecutionStrategy()` + transacción explícita (`ReservaService.cs:2266`). Evaluado y descartado para el motor (§4.4, M2).
6. **`OperationalFinanceSettings.cs:375`** tiene `<see cref="EstadoReserva.Sold"/>` en un doc-comment: al morir la constante, ese cref produce warning CS1574. Se corrige en F1.
7. **`Reserva.ResponsibleUserId` es nullable** (`Reserva.cs:111`) — la notificación de regresión necesita fallback (§4.4).
8. **Frontend:** el grep `Sold|ToSettle|soldToSettle` sobre `src/TravelWeb/src` da 57 ocurrencias en 10 archivos, incluidos `ReservaHeader.jsx` (16), `ReservaStatusChips.jsx` (3) y `ConfirmReservaModal.jsx` (5) que el round 1 no inventariaba. `ProductSearchField.jsx` tiene 2 falsas coincidencias (`formatSoldDate`/`soldAt` del historial de ventas de catálogo, sin relación con el estado Sold).

---

## 2. Decisión de producto (fuente de verdad, NO negociable)

Textual de `docs/ux/guia-ux-gaston.md:52-84`:

```
COTIZACIÓN (borrador interno) ── [Pasar a presupuesto] ──> PRESUPUESTO ──> PERDIDO (no compró)
PRESUPUESTO ── [El cliente aceptó] ──> EN GESTIÓN
EN GESTIÓN ── (AUTOMÁTICA: todos los servicios resueltos) ──> CONFIRMADA 🔒
CONFIRMADA ── (automática día del viaje) ──> EN VIAJE ──> FINALIZADA
EN VIAJE ── [Apartar para liquidar] (manual, opcional) ──> A LIQUIDAR ──> FINALIZADA
CANCELADA: desde cualquier etapa (cotiz/presup → Perdido sin proceso de plata;
           en gestión/confirmada → proceso completo de penalidades y reembolsos).
```

- Nace SIEMPRE como cotización; jamás Confirmada directa.
- Confirmada = todos los servicios RESUELTOS (aéreo = ticket EMITIDO; hotel/paquete = confirmado por operador; asistencia = voucher emitido; traslado = confirmado O marca manual "no requiere confirmación" con registro de quién/cuándo, cualquier vendedor).
- Paso a Confirmada AUTOMÁTICO; la vuelta TAMBIÉN (servicio nuevo solicitado, u operador cancela/reprograma → vuelve sola a En gestión + aviso fuerte al vendedor).
- Confirmación CON CAMBIOS del operador → el servicio sigue solicitado con la propuesta; pasa a confirmado cuando el cliente acepta.
- El saldo del cliente nace POR SERVICIO CONFIRMADO; solicitado NO genera deuda. La fecha de confirmación de CADA servicio se guarda siempre.
- Edición libre hasta Confirmada; Confirmada = candado salvo autorización explícita registrada (vale para admin también).
- "En gestión" muestra detalle por servicio; no existe "confirmada parcial".
- Borrar vs cancelar servicio: manda EL SERVICIO. No confirmado por operador → se BORRA; confirmado → solo se CANCELA (tachado, quién/cuándo, su monto se resta del saldo). Con candado, ambas piden autorización primero.
- "Vendida" muere. Sin flag nuevo.

**Decisiones adicionales del dueño (round 2, vía orquestador, FINALES):**
- **B1:** `Quotation → Lost` SÍ existe — "cotiz/presup → Perdido sin proceso de plata"; el Perdido queda en historial. El "volvió a interesarse" re-entra por revert al estado desde el que se perdió (§4.1).
- **B4:** en el aéreo se separan DOS hechos: la **confirmación del operador** (PNR HK/TK/KK/KL — desde ahí el servicio no se borra, solo se cancela, y corren penalidades) y la **emisión del ticket** (la RESOLUCIÓN, lo que cuenta para que el file pase a Confirmada). La regla borrar-vs-cancelar usa la confirmación, NO la resolución.
- **B5:** `Cancelled` manual (sin flujo ADR-002) permitido desde {InManagement, Confirmed, Traveling, ToSettle} cuando NO hay factura viva, siempre con `ReservaStatusChangeLog`. Bloqueado desde Quotation/Budget (ahí la salida es Lost o borrado). Con factura viva → flujo ADR-002 como hoy.

---

## 3. Decisión técnica — resumen

| # | Decisión |
|---|----------|
| D1 | Ciclo ÚNICO de 11 estados persistidos (strings EN, labels ES); una sola matriz forward + una de revert; el flag y las 4 matrices mueren. Incluye `Quotation→Lost` (B1) y `Cancelled` manual sin factura viva (B5). |
| D2 | DOS migraciones en el tren F1-F3: `Adr020_M1` (F1: columnas de servicio + remapeo Sold→InManagement + CHECK 11 valores + drop del flag + backfills de confirmación/emisión + tablas de candado + permiso) y `Adr020_M2` (F2: `ConfirmedSale` + backfill + recompute de `Balance`). `Down` de cada una con orden explícito; el `Down` de M2 restaura `Balance = TotalSale − TotalPaid` (B2/B6). |
| D3 | "Resuelto" por servicio definido en una clase de dominio nueva `ServiceResolutionRules` con DOS predicados: `IsOperatorConfirmed` (confirmación del operador) e `IsResolved` (resolución). Aéreo gana `TicketIssuedAt` (resolución) y `ConfirmedAt` se estampa con el PNR confirmado (B4); traslado gana marca "no requiere confirmación" auditada; todos ganan `ConfirmedAt` + auditoría de cancelación. La deuda al proveedor sigue basada en CONFIRMACIÓN, no en resolución. |
| D4 | Motor de confirmación automática + regresión: `ReservaAutoStateService`, invocado como save separado inmediatamente después del commit de cada mutación de servicio (patrón actual de `UpdateBalanceAsync`); reconciliación nocturna como cura; last-write-wins aceptado (M2). Regresión notifica al vendedor (entidad `Notification`, Priority=Urgent; fallback a admins si `ResponsibleUserId` es null; sin duplicados el mismo día). |
| D5 | La plata se parte en dos números: `TotalSale` conserva su semántica (valor comercial del presupuesto: todos los servicios no cancelados) y nace `ConfirmedSale` (solo resueltos/confirmados). `Balance = ConfirmedSale − TotalPaid`. Verificado: el flujo de cancelación NO lee `Reserva.Balance` (§1.4.1). |
| D6 | Candado desde Confirmada: entidad nueva `ReservaEditAuthorization` (+ filas hijas de cambios), índice `(ReservaId, ExpiresAt)`, UNA autorización viva por reserva a la vez (la nueva reemplaza), y permiso nuevo `reservas.authorize_locked_edit`; aplica a TODOS los roles incluido Admin. |
| D7 | Borrar vs cancelar servicio por estado DEL SERVICIO usando la CONFIRMACIÓN del operador (no la resolución): `DeleteGuards` se reescribe; el guard reserva-level C26 muere. Un aéreo HK sin ticket NO se borra. |
| D8 | Eliminación total de `EnableSoldToSettleStates` (entidad, settings, DTOs, endpoints, panel, context frontend) y remapeo de todos los conjuntos Fase D + ADR-019 **en F1** (matar la constante `Sold` lo obliga: el compilador rompe esos sitios). |
| D9 | Toda reserva nace `Quotation`; `CreateReservaAsync` ignora `request.Status`; `QuoteService.ConvertToFileAsync` nace `Quotation` con `Balance = 0`. |

---

## 4. Decisión técnica — detalle

### 4.1 Estados nuevos y matriz única (D1)

**Constantes en `EstadoReserva` (Reserva.cs):**

| String persistido | Label ES (UI) | Origen |
|---|---|---|
| `Quotation` | Cotización | NUEVO — estado inicial único |
| `Budget` | Presupuesto | existe (cambia su significado: ahora es el doc que recibe el cliente, no el inicial) |
| `InManagement` | En gestión | NUEVO — reemplaza a `Sold` |
| `Confirmed` | Confirmada 🔒 | existe (ahora SOLO alcanzable por el motor automático) |
| `Traveling` | En viaje | existe |
| `ToSettle` | A liquidar | existe (desvío manual opcional, se mantiene) |
| `Closed` | Finalizada | existe |
| `Lost` | Perdido | NUEVO — cotización/presupuesto que el cliente no compró; queda en historial |
| `Cancelled` | Cancelada | existe (flujo ADR-002 + transición manual nueva, B5) |
| `PendingOperatorRefund` | Esperando reembolso operador | existe (ADR-002 FC1, intacto) |
| `Archived` | Archivada | literal legacy de soft-delete, intacto |

`Sold` se elimina de `EstadoReserva` (la constante muere; el compilador encuentra todos los usos — por eso el remapeo §4.8 va en F1).

**Nombre elegido:** `InManagement` (sobre alternativas `Managing`, `InProgress`, `Processing`). Razón: traduce literal "En gestión", no colisiona con `IsInProgress` (campo computado existente en `ReservaService.cs:2345` que significa "viajando hoy") y es inequívoco en logs.

**Matriz forward ÚNICA** (reemplaza las 4 de `ReservaService.cs:731-787`):

| Desde | Hacia | Quién dispara | Gate |
|---|---|---|---|
| Quotation | Budget | Botón "Pasar a presupuesto" (vendedor) | ≥1 servicio no cancelado |
| Quotation | Lost | Botón "No compró" (vendedor) — B1 | **sin pagos vivos** (`Payments.Any(!IsDeleted)` debe ser false — M4: el path legacy `AddPaymentAsync:1849` no tiene gate de estado y permite pagos en cualquier etapa) |
| Budget | InManagement | Botón "El cliente aceptó" (vendedor) | `EnsureReadinessForSaleAsync` relocalizado: ≥1 servicio + normalizar borradores a Solicitado + pax nominales (gates existentes, mismo código, nuevo punto) |
| Budget | Lost | Botón "No compró" (vendedor) | **sin pagos vivos** (M4, mismo predicado que Quotation→Lost) |
| InManagement | Confirmed | **AUTOMÁTICA** (motor §4.4) — PROHIBIDA manual | todos los servicios no cancelados resueltos + ≥1 servicio no cancelado |
| Confirmed | Traveling | Job (`ReservaLifecycleAutomationService`, StartDate ≤ hoy) o botón manual (existente) | capacidad (`ReservaCapacityRules.GetBlockReasonAsync`); el chequeo de servicios sin RESOLVER desaparece de este gate (lo garantiza el motor) |
| Confirmed | InManagement | **AUTOMÁTICA** (regresión, motor §4.4) — PROHIBIDA manual | un servicio dejó de estar resuelto |
| Traveling | Closed | Job (EndDate < hoy AND `Balance <= 0`, §4.5/M3) o botón "Cerrar reserva" | manual: bloquea solo `Balance > 0` + estampa `ClosedAt` (gate existente `:2227-2232`) |
| Traveling | ToSettle | Botón "Apartar para liquidar" (manual, opcional) | ninguno (existente) |
| ToSettle | Closed | Botón manual | bloquea `Balance > 0` + `ClosedAt` (existente) |
| {InManagement, Confirmed, Traveling, ToSettle} | Cancelled | **NUEVO (B5): cancelación manual** via `UpdateStatusAsync` (botón con motivo, mismo criterio min 10 chars del revert) | **sin factura viva** = ninguna factura con CAE no vacío cuya anulación no esté `AnnulmentStatus = Succeeded` (convención existente: CAE en `DeleteGuards.cs:55`; anulación exitosa en `BookingCancellationService.cs:183`). Con factura viva → flujo ADR-002 obligatorio, como hoy. Escribe `ReservaStatusChangeLog` (quién/cuándo/motivo). |
| (cualquiera operativo) | Cancelled / PendingOperatorRefund | Flujo de cancelación ADR-002 (intacto) | su flujo propio. Desde Quotation/Budget NO hay cancelación (ni manual ni ADR-002): la salida es Lost o borrado físico (§4.7) |

**Matriz revert ÚNICA** (manual, con la autorización/supervisor existente de `RevertStatusAsync`, `ReservaService.cs:866+`):

| Desde | Hacia | Nota |
|---|---|---|
| Budget | Quotation | nuevo; sin gate extra |
| InManagement | Budget | gate unificado nuevo (M5, ver abajo): sin pagos + sin facturas + **sin servicios resueltos** |
| Lost | Quotation **o** Budget | nuevo ("el cliente volvió") — B1. El target legal es ÚNICO y determinístico: el `FromStatus` de la última transición hacia `Lost` registrada en `ReservaStatusChangeLog`; fallback defensivo `Budget` si no hay fila (no debería ocurrir: toda transición loguea). Elegido por ser lo más simple: no agrega columnas y usa el rastro que ya existe. |
| Traveling | Confirmed | existente |
| ToSettle | Traveling | existente |
| Closed | Traveling | existente (limpia `ClosedAt`, `ReservaService.cs:947`) |

**Gate del revert a Budget — unificación (M5, explícito):**
- El check "tiene servicios cargados" de `EnsureCanRevertToBudgetAsync` (`:2179-2180`) **MUERE**. En el ciclo nuevo es contradictorio: `Budget → InManagement` exige ≥1 servicio, así que con ese check ninguna reserva podría volver jamás a Budget.
- Las DOS copias divergentes del gate (`EnsureCanRevertToBudgetAsync:2171-2181` con check de servicios vs. la copia inline de `RevertStatusAsync:896-903` sin él — divergencia verificada, §1.4.3) se **unifican en UNA** función llamada desde ambos paths.
- Gate nuevo único para `InManagement → Budget`: **sin pagos vivos + sin facturas + sin servicios resueltos** (si algo ya se confirmó con un operador, el camino es cancelar servicios, no retroceder el file).

`Confirmed` NO tiene revert manual: la regresión es automática (agregar un servicio nuevo ya la dispara). Hard blocker existente intacto: reserva con factura CAE no se revierte (`ReservaService.cs:824-830, 891-893`).

`UpdateStatusAsync` pierde la bifurcación por flag: whitelist única, `ApplyClassicTransitionAsync` y `ApplySoldToSettleTransitionAsync` se funden en un solo `ApplyTransitionAsync`. **Todas** las transiciones via `UpdateStatusAsync`/`RevertStatusAsync`/motor/job escriben `ReservaStatusChangeLog` (alcance del invariante INV-020-06, §8). Los 7 sitios que hoy escriben `Status` por FUERA de esos paths (6 de `BookingCancellationService` + `ArchiveReservaAsync`, §1.4.4) ganan la escritura del log de forma ADITIVA en F6 (M7) — no se reestructuran sus flujos.

### 4.2 Migración de datos (D2) — partida en dos (B6)

La migración se parte en DOS para que cada fase compile y deje el sistema coherente: `Adr020_M1` acompaña al binario de F1 (que todavía calcula `Balance = TotalSale − TotalPaid`) y `Adr020_M2` acompaña al binario de F2 (que introduce `ConfirmedSale` y la fórmula nueva). F1–F3 son un único tren de deploy (§6).

**Orden de deploy obligatorio del tren F1-F3 (B3):**
1. **Parar la app** (API + frontend).
2. **Backup de la base** (F0).
3. **Correr las migraciones** (`Adr020_M1` → `Adr020_M2`).
4. **Levantar el binario nuevo.**

Razón: `Adr020_M1` dropea la columna `EnableSoldToSettleStates` que el binario viejo lee al construir `OperationalFinanceSettings` — el binario viejo corriendo contra la base migrada revienta. El downtime es aceptado explícitamente (producto sin clientes, único usuario = el dueño).

#### Adr020_M1_ReservaLifecycleRedesign (F1), orden dentro del `Up`:

1. **Columnas nuevas** (todas aditivas, nullable o con default):
   - `ConfirmedAt timestamptz NULL` en `HotelBookings`, `FlightSegments`, `TransferBookings`, `PackageBookings`, `AssistanceBookings`, `Servicios`.
   - `CancelledAt timestamptz NULL`, `CancelledByUserId text NULL`, `CancelledByUserName text NULL` en las mismas 6 tablas.
   - `TicketIssuedAt timestamptz NULL`, `TicketIssuedByUserId text NULL`, `TicketIssuedByUserName text NULL` en `FlightSegments`.
   - `NoConfirmationRequired boolean NOT NULL DEFAULT false`, `NoConfirmationMarkedAt timestamptz NULL`, `NoConfirmationMarkedByUserId text NULL`, `NoConfirmationMarkedByUserName text NULL` en `TransferBookings`.
   - Tablas nuevas `ReservaEditAuthorizations` (con índice `(ReservaId, ExpiresAt)`) + `ReservaEditAuthorizationChanges` (§4.6).
2. **Remapeo de estados:** `UPDATE "TravelFiles" SET "Status" = 'InManagement' WHERE "Status" = 'Sold';` (en el VPS el flag nunca se prendió según el estado conocido, así que se esperan 0 filas — el UPDATE es defensa). `Budget` y el resto NO se tocan: las reservas históricas Budget siguen siendo Presupuesto; ninguna histórica pasa a `Quotation`.
3. **CHECK nuevo:** drop + add `chk_TravelFiles_status_valid` con 11 valores: `Quotation, Budget, InManagement, Confirmed, Traveling, ToSettle, Closed, Lost, Cancelled, PendingOperatorRefund, Archived` (mismo patrón idempotente de la migración 20260530130000).
4. **Backfill `ConfirmedAt`** — predicados espejando `WorkflowStatusHelper` POR CONSTRUCCIÓN (M1):
   - 5 tablas genéricas (`HotelBookings`, `PackageBookings`, `TransferBookings`, `AssistanceBookings`, `Servicios`): `SET "ConfirmedAt" = "CreatedAt" WHERE lower("Status") NOT LIKE '%cancel%' AND (lower("Status") LIKE '%confirm%' OR lower("Status") LIKE '%emit%')` — espejo exacto de `MapGenericStatus` (`WorkflowStatusHelper.cs:26-35`: primero descarta cancel, después matchea confirm/emit por substring case-insensitive).
   - `FlightSegments`: `SET "ConfirmedAt" = "CreatedAt" WHERE upper(trim("Status")) IN ('HK','TK','KK','KL')` — espejo de `MapFlightStatus` (`:14-24`, que trimea y pasa a mayúsculas).
   - El `SELECT DISTINCT` de F0 queda como **verificación adicional** (detectar variantes de texto inesperadas antes de confiar en los predicados), no como fuente de los predicados.
   - Es un PROXY (`CreatedAt` no es la fecha real de confirmación histórica); aceptable porque las penalidades por servicio solo aplican hacia adelante. Documentado como limitación.
5. **Backfill aéreos emitidos:** `UPDATE "FlightSegments" SET "TicketIssuedAt" = "CreatedAt" WHERE "TicketNumber" IS NOT NULL AND "TicketNumber" <> ''` — un segmento histórico con número de ticket cargado se considera emitido. Los aéreos confirmados (HK) sin ticket quedan confirmados-pero-NO-resueltos: correcto según B4 (PNR no alcanza para resolver, pero SÍ impide borrar), y visible en "En gestión" como pendiente de emitir.
6. **Drop del flag:** `DropColumn EnableSoldToSettleStates` de `OperationalFinanceSettings`.
7. **Seed de permiso:** `reservas.authorize_locked_edit` para el rol Admin (mismo patrón que `Adr013_M2_SeedClassifyAgencyPenaltyPermission`).

**`Down` de Adr020_M1 — orden EXPLÍCITO (B2):**
1. Remapear estados nuevos a viejos **ANTES de restaurar el CHECK** (si no, el CHECK de 9 valores rechaza las filas nuevas): `Quotation→Budget`, `InManagement→Confirmed`, `Lost→Cancelled` (lossy pero coherente con el ciclo viejo flag-OFF).
2. Restaurar el CHECK `chk_TravelFiles_status_valid` de 9 valores.
3. Re-agregar la columna `EnableSoldToSettleStates` (default false).
4. Dropear columnas y tablas nuevas del punto 1 del `Up`.

#### Adr020_M2_ConfirmedSaleAndBalance (F2), orden dentro del `Up`:

1. **Columna:** `ConfirmedSale numeric(18,2) NOT NULL DEFAULT 0` en `TravelFiles`.
2. **Backfill `ConfirmedSale`:** SQL que suma por reserva el `SalePrice` de servicios resueltos — genéricos con el predicado espejo de `MapGenericStatus` (idéntico al de M1 punto 4) y aéreos con `"TicketIssuedAt" IS NOT NULL`; traslados además `OR "NoConfirmationRequired" = true`.
3. **Recompute `Balance`:** `UPDATE "TravelFiles" SET "Balance" = "ConfirmedSale" - "TotalPaid";`
4. Red de seguridad post-deploy: el endpoint admin de mantenimiento existente que corre `RunDailyDetailedAsync` se extiende con un recompute masivo app-level (fuente única `ReservaMoneyCalculator`) para reconciliar cualquier divergencia SQL-vs-dominio.

**`Down` de Adr020_M2 — orden EXPLÍCITO (B2):**
1. **Restaurar la fórmula vieja del saldo:** `UPDATE "TravelFiles" SET "Balance" = "TotalSale" - "TotalPaid";` (esto satisface el requisito B2 — la restauración de `Balance` vive en el `Down` de la migración que lo cambió; con el split B6, esa es M2, no M1).
2. Dropear `ConfirmedSale`.

**Rollback del tren completo:** `git revert` del código + `Adr020_M2.Down` → `Adr020_M1.Down` (orden inverso), con la app parada, ANTES de levantar el binario viejo. Pérdidas explícitas de los `Down`: timestamps de confirmación/cancelación/emisión, marcas de traslado, autorizaciones de candado, y la distinción Perdido/Cancelada. El backup de F0 es la red real.

**Prevalidación obligatoria en el VPS antes de migrar (F0):**
1. `SELECT "Status", count(*) FROM "TravelFiles" GROUP BY 1;` — si aparece cualquier valor fuera de los 9 actuales, parar y resolver a mano.
2. `SELECT DISTINCT "Status"` sobre las 6 tablas de servicios — conocer las variantes de texto reales como verificación adicional de los predicados espejo (M1).
3. **Diff de `Balance` (M3):** SQL que lista las reservas en `Traveling`/`ToSettle` cuyo `Balance` cambiaría con la fórmula nueva (`ConfirmedSale − TotalPaid` calculado con los predicados del backfill vs. el `Balance` actual), con ambos valores lado a lado. El dueño las ve ANTES de migrar — son las que están más cerca del gate de cierre y donde un salto de saldo sorprende más.

### 4.3 Resolución por servicio (D3) — y su separación de la CONFIRMACIÓN (B4)

Clase nueva de dominio `ServiceResolutionRules` (en `src/TravelApi.Domain/Reservations/`, junto a `ReservaMoneyCalculator` — pura, testeable sin DB), con DOS predicados porque son DOS hechos de negocio distintos:

- **`IsOperatorConfirmed`** — el operador se comprometió. Gobierna: borrar-vs-cancelar (§4.7), estampado de `ConfirmedAt`, penalidades, deuda al proveedor.
- **`IsResolved`** — el servicio está asegurado para viajar. Gobierna: paso automático a Confirmada, `ConfirmedSale`/saldo del cliente.

Para todos los tipos salvo el aéreo, ambos predicados COINCIDEN. En el aéreo divergen (decisión B4 del dueño):

| Tipo | Confirmado por operador (`IsOperatorConfirmed`) | Resuelto (`IsResolved`) |
|---|---|---|
| `FlightSegment` | `MapFlightStatus(Status) == Confirmado` (PNR HK/TK/KK/KL). Estampa `ConfirmedAt`; desde ahí NO se borra (solo cancela) y corren penalidades. | `TicketIssuedAt != null`. Acción nueva "Marcar emitido": estampa `TicketIssuedAt` + quién; pide `TicketNumber` (si viene vacío, se permite con advertencia — supuesto A3, §9). El PNR confirmado NO resuelve. |
| `HotelBooking` | `MapGenericStatus(Status) == Confirmado` | ídem (coinciden) |
| `PackageBooking` | ídem hotel | ídem |
| `AssistanceBooking` | `MapGenericStatus(Status) == Confirmado` (el mapeo ya trata "emit*" como confirmado) | ídem; label UI "Voucher emitido" |
| `TransferBooking` | `MapGenericStatus(Status) == Confirmado` | confirmado **OR** `NoConfirmationRequired == true`. La marca la pone cualquier vendedor; estampa quién/cuándo (campos §4.2.1) |
| `ServicioReserva` genérico | `MapGenericStatus(Status) == Confirmado` | ídem (coinciden) |

Servicios con status mapeado `Cancelado` quedan FUERA de ambos conjuntos (no bloquean la confirmación ni suman al saldo).

**Default del aéreo nuevo (B4):** el default actual `"HK"` (`FlightSegment.cs:75`) hace nacer cada vuelo YA confirmado — incompatible con el ciclo nuevo (nacería no-borrable y con `ConfirmedAt`). El default pasa a **`"NN"`** (IATA "need/segment requested": cabe en `MaxLength(2)` y `MapFlightStatus` lo mapea a `Solicitado` por la rama default, verificado en `WorkflowStatusHelper.cs:22`). Impacto:
- Solo afecta entidades NUEVAS (el default de C# no toca filas existentes); las filas históricas conservan su `Status` actual y reciben `ConfirmedAt = CreatedAt` por el backfill §4.2.4 — coherente: si decían HK, se las trata como confirmadas.
- El frontend que hoy preseleccione "HK" al crear un vuelo debe alinearse al default nuevo en F3 (se verifica en el inventario F3).

**`ConfirmedAt` (todos los tipos):** se estampa cuando el servicio pasa a `IsOperatorConfirmed` (detectado por el motor §4.4 comparando estado anterior vs nuevo; en el aéreo, al pasar el `Status` a HK/TK/KK/KL — independiente de la emisión). Si el servicio deja de estar confirmado (operador cancela/reprograma), `ConfirmedAt` NO se borra: se conserva como historia y se vuelve a estampar si se re-confirma (el valor vigente es el del último ciclo; las penalidades corren desde la confirmación vigente).

**Confirmación CON CAMBIOS del operador:** sin schema nuevo. Regla operativa + UI: el vendedor NO marca confirmado; deja el servicio Solicitado y anota la propuesta en `Notes`. Recién al aceptar el cliente, actualiza valores y marca confirmado (ahí nace `ConfirmedAt` y el saldo nuevo). El frontend lo guía (gate UX); el backend no puede distinguir "confirmó igual" de "confirmó con cambios" sin input humano, y agregar un sub-estado para esto sería sobre-diseño hoy.

**Qué se re-basa y qué NO (precisión del acople oculto, fix del review):**
- `GetUnconfirmedServicesBlockReasonAsync` (gate de Traveling) **se re-basa sobre `IsResolved`**: no se viaja con un aéreo sin emitir. (Hoy un HK sin ticket pasaba ese gate vía `ConfirmedServiceStatuses`; con el motor esto es defensa en profundidad, porque a Traveling solo se llega desde Confirmed = todo resuelto.)
- La noción de **DEUDA AL PROVEEDOR sigue basada en CONFIRMACIÓN del operador, NO en resolución**: `CountsForSupplierDebtByType` (`WorkflowStatusHelper.cs:58-64`, consumido por `SupplierService.cs:925`) NO cambia — un aéreo HK sin ticket YA le debe plata al consolidador.
- `GetStatusDowngradeBlockReasonAsync` (`ReservaCapacityRules.cs:87-100`, protege contra "des-confirmar" un servicio que ya generó deuda; 10 call-sites en `BookingService`) **sigue basado en CONFIRMACIÓN** — usa `ConfirmedServiceStatuses` tal cual, que se mantiene como el conjunto de confirmación del operador.
- En síntesis: `ConfirmedServiceStatuses` NO se reemplaza en bloque por resolución; cada consumidor se asigna explícitamente a uno de los dos predicados según qué hecho de negocio protege.

### 4.4 Motor de confirmación automática + regresión (D4) — contrato real (M2)

**Dónde vive:** servicio nuevo `ReservaAutoStateService` (`src/TravelApi.Infrastructure/Services/Reservations/`), invocado después de cada mutación de servicio, en el mismo punto donde hoy se invoca el recálculo de saldo. NO es un job: el dueño quiere que el file pase a Confirmada "al resolverse el último servicio", y un job diario llegaría tarde.

**Contrato de ejecución (DECISIÓN, los tests se escriben contra esto):** se elige el **patrón actual del repo — saves separados, post-commit inmediato**, NO una transacción envolvente.

- La mutación del servicio commitea su propio `SaveChanges` (como hoy); inmediatamente después, en el mismo request, corre el motor (`Evaluate` + su propio `SaveChanges`) — exactamente el patrón existente de `UpdateBalanceAsync`/`RecalculateReservaBalanceAsync` (ej. `AddPaymentAsync:1863-1865`).
- **Ventana aceptada:** entre los dos saves existe un instante en que el servicio ya mutó y el `Status` del file todavía no. Con un único usuario real, la probabilidad de observarla es despreciable y el costo de eliminarla no se justifica.
- **Last-write-wins aceptado:** si dos requests concurrentes evalúan, gana el último (el motor es idempotente por evaluación de estado total, así que el resultado converge; no hay locking optimista nuevo).
- **Cura:** la pasada de **reconciliación nocturna** del job (`ReservaLifecycleAutomationService`) re-evalúa el motor sobre todas las reservas en `InManagement`/`Confirmed` y corrige cualquier mutación que haya esquivado el chokepoint o quedado en la ventana. **La reconciliación loguea cuántas reservas curó** (contador en el log estructurado del job; >0 sostenido = hay un chokepoint sin cubrir, accionable).
- **Alternativa evaluada y descartada:** transacción envolvente con `CreateExecutionStrategy()` + `BeginTransaction` (patrón existente en `DeleteReservaAsync`, `ReservaService.cs:2266`). Daría atomicidad mutación+estado, pero obliga a reestructurar TODOS los chokepoints de `ReservaService`/`BookingService` (hoy escritos como save-then-recalculate) y la retry-strategy de Npgsql exige envolver la unidad completa en el delegate — un cambio invasivo cuyo único beneficio es cerrar una ventana que la reconciliación ya cura. Si el producto suma usuarios concurrentes, se revisa.

**Lógica (idempotente, evaluación de estado total, no de eventos):**

```
Evaluate(reserva):
  serviciosVivos = servicios no cancelados (6 colecciones)
  allResolved = serviciosVivos.Count >= 1 && serviciosVivos.All(ServiceResolutionRules.IsResolved)
  si Status == InManagement && allResolved  -> Confirmed  (log "Forward", actor "system:auto-confirm" o el usuario que disparó)
  si Status == Confirmed && !allResolved    -> InManagement (log "Revert/auto", motivo con el servicio causante)
                                              + Notification (ver regla de destinatario y dedup abajo)
  cualquier otro Status -> no-op
```

- **Idempotencia:** evaluar dos veces seguidas no produce segunda transición (compara estado actual vs deseado). El log solo se escribe cuando hay cambio real.
- **Estampado de `ConfirmedAt` por servicio:** el chokepoint le pasa al motor qué servicio cambió (o el snapshot pre-mutación); el motor estampa `ConfirmedAt` en los servicios que acaban de pasar a confirmados por el operador (§4.3).
- **Notificación de regresión:** entidad `Notification` existente (Priority=Urgent, Type=Warning, RelatedEntityType="Reserva", mensaje con qué servicio rompió la confirmación). **Destinatario:** `ResponsibleUserId`; como es nullable (`Reserva.cs:111`), **fallback: se notifica a todos los usuarios con rol Admin** (mejor un aviso de más que una regresión silenciosa). **Dedup:** antes de insertar, se verifica que no exista ya una `Notification` de regresión para la misma reserva creada HOY (mismo `RelatedEntityId` + tipo, `CreatedAt` del día) — una reserva que entra y sale de Confirmada varias veces el mismo día genera UNA notificación (test en §7). La reconciliación nocturna tampoco re-notifica.
- **Regresión en Traveling/ToSettle/Closed: NO automática.** Ahí siguen rigiendo los guards existentes (`ReservaStatusesRequiringConfirmedServices` bloquea cargar/modificar servicios no confirmados en Traveling/Closed); un problema de operador con el viaje empezado se maneja con cancelación de servicio o revert manual, no con regresión silenciosa.
- **Reserva en `InManagement` el día del viaje:** el job NO la promueve (solo promueve Confirmed). El aviso "Próximos inicios" (ADR-019) ya cubre la advertencia operativa; criterio re-mapeado en §4.8.

### 4.5 Saldo por servicio confirmado (D5)

**Diseño: dos números, no uno.** El error a evitar: si `TotalSale` pasara a sumar solo confirmados, el presupuesto mostraría $0 y todos los consumidores comerciales (cotización al cliente, reportes de venta) se romperían. Entonces:

- `TotalSale` (existente) **conserva** su semántica: valor comercial = suma de servicios NO cancelados (Solicitado + Confirmado). Es lo que el cliente ve en el presupuesto.
- `ConfirmedSale` (columna nueva en `TravelFiles`, migración `Adr020_M2` §4.2): suma de `SalePrice` de servicios **resueltos** (`ServiceResolutionRules.IsResolved`). Es la deuda exigible que nace por servicio.
- **`Balance = ConfirmedSale − TotalPaid`** (antes: `TotalSale − TotalPaid`). Un servicio cancelado sale de `ConfirmedSale` automáticamente → "su monto se resta solo de la deuda del cliente" (regla del dueño).
- `TotalCost` mantiene su cuenta actual (deuda con proveedor ya se gobierna por `CountsForSupplierDebtByType`, que SOLO cuenta confirmados — esa regla ya era correcta y queda intacta, §4.3).

**Implementación:** `ReservaMoneyCalculator.Calculate` devuelve `ReservaMoneySummary` extendido con `ConfirmedSale`; `WorkflowStatusHelper.CountsForReservaBalance` se renombra/parte en `CountsForQuotedTotal` (no cancelado — alimenta TotalSale) y la resolución pasa a `ServiceResolutionRules` (alimenta ConfirmedSale). Los tres call-sites persisten el campo nuevo (`ReservaService.UpdateBalanceAsync` / `PaymentService.RecalculateReservaBalanceAsync:799` / `AfipService:1702`).

**Impactos evaluados:**
- *Cancelaciones/refunds (pregunta 4 del reviewer, VERIFICADO §1.4.1):* `BookingCancellationService` **NO lee `Reserva.Balance`** en ningún cálculo de refund/crédito (grep exhaustivo; solo usa `RemainingBalance` del ledger de crédito propio). La fórmula nueva NO altera ningún monto de cancelación, NC, ND ni crédito de cliente.
- *Pagos ya registrados:* intactos. Si el cliente pagó más de lo confirmado (seña antes de confirmar), `Balance` queda negativo = saldo a favor. Es correcto: la seña existe antes que la deuda. La UI debe mostrar "a favor" (gate UX).
- *Cierre con saldo a favor (M3):* el predicado del job auto-close pasa de `Balance == 0` (`ReservaLifecycleAutomationService.cs:216`) a **`Balance <= 0`** — coherente con el gate manual, que solo bloquea `Balance > 0` (`:2229`). Sin este cambio, una reserva con saldo a favor se podría cerrar a mano pero el job jamás la cerraría (asimetría sin sentido).
- *Facturación:* las facturas son de monto manual y las emitidas con CAE son inmutables — el saldo es interno, la factura NO se toca. Sin cambio en `AfipService`/`InvoiceService` salvo el set de estados (§4.8).
- *Cobranzas/alertas de deuda:* `AlertService` (viajes próximos con `Balance > 0`) automáticamente deja de reclamar deuda por servicios solicitados — exactamente el bug que el dueño señaló.
- *Cierre:* gate `Balance > 0` ahora significa "lo confirmado está pagado". Si quedó un servicio Solicitado colgado al cerrar, no bloquea el cierre por plata pero el file no estaría Confirmada/Traveling — incoherencia imposible por matriz.
- *`QuoteService.ConvertToFileAsync` (`QuoteService.cs:322`, cuerpo `:335-359`)* hoy setea `Balance = quote.TotalSale` al nacer; pasa a `Balance = 0` y `ConfirmedSale = 0` (nace sin servicios confirmados).

### 4.6 Candado + autorización (D6)

**Cuándo aplica:** `Status ∈ {Confirmed, Traveling, ToSettle, Closed}` — el candado nace en Confirmada y NO se levanta al avanzar (sería absurdo que "En viaje" fuera más editable que Confirmada). En `Quotation/Budget/InManagement` la edición es libre (regla del dueño).

**Qué operaciones cubre:** editar servicio (cualquier campo, incl. precios/fechas/status manual), borrar servicio, cancelar servicio, editar datos de la reserva (nombre, fechas, titular/payer, descripción), agregar/editar/eliminar pasajeros, agregar servicio nuevo (que además dispara regresión — la autorización es el paso previo consciente). **NO cubre** (tienen flujo/autorización propios): pagos y recibos, facturas/NC/ND, transiciones de estado (revert ya tiene supervisor; `Cancelled` manual tiene su gate §4.1), cancelación de la reserva completa (ADR-002), vouchers.

**Modelo de datos:**

```
ReservaEditAuthorization
  Id, PublicId, ReservaId (FK)
  RequestedByUserId/Name      -- quién va a editar
  AuthorizedByUserId/Name     -- quién autorizó (puede ser el mismo si tiene el permiso; Admin incluido: TAMBIÉN registra)
  Reason (required, min 10 chars — mismo criterio que RevertStatusAsync)
  CreatedAt, ExpiresAt        -- ventana de 30 minutos (supuesto A5)
  ReservaStatusSnapshot       -- estado del file al autorizar
  -- índice: (ReservaId, ExpiresAt)  — el guard consulta "¿hay autorización viva para esta reserva?"

ReservaEditAuthorizationChange   -- "qué cambió"
  Id, AuthorizationId (FK)
  Operation                   -- ServiceEdited|ServiceDeleted|ServiceCancelled|ServiceAdded|ReservaDataEdited|PassengerAdded|PassengerEdited|PassengerDeleted
  EntityType, EntityId
  Summary (texto: campo viejo→nuevo resumido)
  PerformedByUserId/Name, OccurredAt
```

**Regla de unicidad (menor del review):** **UNA autorización viva por reserva a la vez.** Al crear una nueva con una vigente, la vigente se expira en el acto (`ExpiresAt = now`) y la nueva la reemplaza — sin solapamientos, el guard resuelve con un solo lookup por el índice, y el rastro de la reemplazada queda íntegro.

**Flujo:** (1) usuario intenta operación protegida → 409 con motivo "reserva confirmada, requiere autorización"; (2) `POST /api/reservas/{id}/edit-authorizations` con motivo: si el actor tiene `reservas.authorize_locked_edit` se auto-autoriza (registrado igual — esto cubre "vale para admin también"); si no, selecciona un autorizante con ese permiso (mismo patrón de selección de supervisor que `GetRevertOptionsAsync`, `ReservaService.cs:832-861`); (3) dentro de la ventana, las operaciones protegidas del mismo usuario sobre esa reserva pasan y cada una escribe su fila `Change`. El guard vive en un helper único (`ReservaLockGuard`) consultado por `ReservaService`/`BookingService`/endpoints de pasajeros — mismo patrón de regla-en-un-solo-lugar que `DeleteGuards`.

**Limitación aceptada (igual que el flujo de revert existente):** la "autorización del supervisor" es una selección registrada, no una credencial verificada del supervisor. Con un único usuario real hoy, endurecerlo (re-auth del supervisor) es sobre-diseño; queda anotado como mejora futura.

### 4.7 Borrar vs cancelar servicio (D7) — sobre la CONFIRMACIÓN del operador (B4)

`DeleteGuards.GetServiceDeleteBlockReasonAsync` se reescribe; **el guard C26 reserva-level muere** ("la reserva esta en estado X" desaparece de raíz, como pidió el dueño). Regla nueva, por SERVICIO, sobre el predicado `IsOperatorConfirmed` (NO la resolución — decisión B4):

| Estado del servicio | Acción disponible | Detalle |
|---|---|---|
| Nunca confirmado por operador (`ConfirmedAt == null` y `!IsOperatorConfirmed`) | **BORRAR** (hard delete, libre) | era borrador sin compromiso. Si la reserva está bajo candado (§4.6), requiere autorización primero. |
| Confirmado por operador, actual o histórico (`ConfirmedAt != null` o `IsOperatorConfirmed` o `IsResolved`) | **CANCELAR** (no se borra jamás) | `Status → Cancelado` (genéricos/typed) / código IATA de cancelación en `FlightSegment` (se persiste `HX`); estampa `CancelledAt/By`. Queda tachado en la reserva. Su `SalePrice` sale de `ConfirmedSale` → el saldo baja solo. Bajo candado, requiere autorización. **Caso clave (B4): un aéreo HK SIN ticket emitido NO se borra** — el PNR confirmado ya es compromiso con el consolidador (deuda, penalidades), aunque no resuelva el file. |

Guards que se conservan / ajustan:
- **Se conserva:** bloqueo por pago soft-deleted vinculado vía `Payment.ServicioReservaId` (riesgo de hard-delete en cascada, `DeleteGuards.cs:98-116`).
- **Se conserva:** bloqueo por vouchers emitidos de la reserva (`GetServicePaymentsAndVoucherBlockReasonAsync` — anular voucher primero).
- **Se relaja:** el bloqueo genérico "la reserva tiene pagos" deja de aplicar al BORRADO de un servicio nunca-confirmado (los pagos son de la reserva, no del servicio; el recálculo de saldo absorbe el cambio). Sí se mantiene para el delete de la RESERVA completa.
- `GetReservaDeleteBlockReasonAsync` (C25) se extiende: la reserva es borrable en `Quotation` **o** `Budget` (mismos guards de pagos/vouchers/CAE).
- La cancelación de servicio NO pasa por el flujo ADR-002 (ese es para cancelar la reserva/cliente con NC y reembolsos); es una operación local con auditoría. Si la cancelación del servicio implica plata ya facturada, eso se resuelve por el flujo fiscal existente aparte (NC manual) — fuera de alcance de este ADR.

### 4.8 Muerte del flag y remapeo de conjuntos (D8) — TODO el remapeo backend en F1 (B6)

Matar la constante `EstadoReserva.Sold` rompe la compilación de cada conjunto que la referencia: por construcción, **este remapeo entero es parte de F1** (no puede diferirse — el round 1 lo tenía repartido y F1 no compilaba).

**Backend a eliminar (F1):** propiedad en `OperationalFinanceSettings` + bloque PATCH en `OperationalFinanceSettingsService.cs:77-84` + proyección GET `:262` + `AfipSettingsResponse` + `OperationalFlagsResponse.EnableSoldToSettleStates` (+ su test de shape) + `OperationalFinanceSettingsDto` + bifurcaciones en `ReservaService`, `QuoteService:335-342`, `InvoiceService:316-318`, `ReservaLifecycleAutomationService` (el parámetro `soldToSettleEnabled` desaparece; `WriteForwardLog` pasa a `true` siempre) + comentarios "flag ON/OFF" en `AlertService` + **el doc-comment `<see cref="EstadoReserva.Sold"/>` en `OperationalFinanceSettings.cs:375`** (al morir la constante genera warning CS1574; se reescribe el comentario — menor del review).

**Frontend a eliminar (F3, mismo tren):** campo en `OperationalFlagsContext.jsx`, toggle en `OperationalFinanceSettingsTab.jsx` (panel admin), gating en `ReservasPage.jsx`, `ReservaDetailPage.jsx`, `useReservas.js`, `ReservaStatusBadge.jsx`, **`ReservaHeader.jsx`, `ReservaStatusChips.jsx`, `ConfirmReservaModal.jsx`** (M6 — inventario verificado por grep, §1.4.8). El contexto en sí SE QUEDA (sirve a los otros 4 flags).

**Remapeo de conjuntos Fase D — en F1** (regla general: `Sold → InManagement`, `ToSettle` queda):

| Conjunto | Antes | Después | Justificación |
|---|---|---|---|
| `PaymentService.ActiveCollectionStatuses` | Sold, Confirmed, Traveling, ToSettle | **InManagement**, Confirmed, Traveling, ToSettle | la seña se cobra durante la gestión ("la plata viene después, el sí alcanza") |
| `InvoiceService` facturables (`:67-69`) + guard `:318` | Confirmed, Traveling, ToSettle (Sold bloqueado) | Confirmed, Traveling, ToSettle; guard anti-facturar pasa de `== Sold` a `∈ {Quotation, Budget, InManagement, Lost}` | conservador: mantiene la decisión Fase D "antes de Confirmada no se factura". ⚠️ OPEN QUESTION Q1 (§9): facturar señas en gestión |
| `SupplierService.ValidReservationStatuses` (`:19-24`) | Sold, … | InManagement, … | deuda proveedor ya filtra por servicio confirmado (`CountsForSupplierDebtByType`), el set solo define reservas visibles |
| `SupplierService` query `:286-291` | Budget, Sold, … | Quotation, Budget, InManagement, … | ídem |
| `TreasuryService.activeStatuses` (`:32-37`) | Sold, … | InManagement, … | AR activa |
| `AlertService` viajes urgentes (`:109-111`) | Sold, Confirmed, Traveling | InManagement, Confirmed, Traveling | misma intención |
| `AlertService` ADR-019 "Próximos inicios" (`:177-179`) | Sold, Confirmed, Traveling | **InManagement, Confirmed, Traveling** | mapeo documentado: el criterio de ADR-019 "vendidas/confirmadas/en viaje" se traduce a "en gestión/confirmada/en viaje". Quotation/Budget/Lost NO avisan (no hay compromiso) |
| `ReservaService.ActiveCount` (`:1075`) | Sold+Confirmed+Traveling+ToSettle | InManagement+Confirmed+Traveling+ToSettle | |
| `SoldCount`/`ToSettleCount` (`:1083`) | — | `QuotationCount`, `BudgetCount`, `InManagementCount`, `LostCount`, `ToSettleCount` | counters para tabs |
| Filtros de tab (`:2404-2420`) | "sold"/"to-settle"/"reserved" | "quotation", "budget", "in-management", "confirmed", "traveling", "to-settle", "closed", "lost", "cancelled" — **incluye el cambio de clave `"reserved" → "confirmed"`** (hoy `"reserved"` mapea a `Confirmed` en `:2406`; el frontend que manda `tab=reserved` se actualiza en F3, listado explícito en el inventario) | claves API estables en kebab-case |
| Estados negativos (revenue/AR excluyen Cancelled/PendingOperatorRefund) | sin tocar | sin tocar + excluir `Lost` donde se excluye Cancelled | Lost nunca tuvo plata; revisar consumidor por consumidor en F6 con test |

### 4.9 Creación (D9)

- `CreateReservaAsync` (`ReservaService.cs:1419-1421`): **ignora `request.Status`**; toda reserva nace `Quotation`. El campo `Status` se elimina de `CreateReservaRequest` (breaking change de API interno aceptado: producto sin clientes; el frontend se ajusta en la misma fase).
- `QuoteService.ConvertToFileAsync` (`QuoteService.cs:322`, cuerpo `:335-359`): nace `Quotation`, `Balance = 0`, `ConfirmedSale = 0` (los items de la cotización entran como servicios en borrador/solicitados, igual que hoy — con el default nuevo `"NN"` para aéreos, §4.3; se revisa en F1 que entren como NO resueltos). El módulo Quotes/Leads existente NO cambia su pipeline; solo el estado de nacimiento de la reserva resultante. ⚠️ OPEN QUESTION Q2 (§9): avance rápido post-conversión.
- El gate UX (CLAUDE.md) define cómo se ven los botones "Pasar a presupuesto"/"El cliente aceptó"/"No compró"; el backend solo expone las transiciones legales.

---

## 5. Alternativas consideradas

| Alternativa | Por qué NO |
|---|---|
| **A. Mantener el flag y agregar el ciclo nuevo como tercer modo** | Prohibido por regla explícita del dueño ("basta de llaves"). Además 6 matrices serían inmantenibles. |
| **B. "Cotización" como entidad separada (reusar el módulo Quote) en vez de estado de Reserva** | El dueño definió cotización y presupuesto como ETAPAS de la misma reserva (mismo número, mismos servicios, avance con un botón). Convertir entre entidades duplicaría servicios/pax y rompería la trazabilidad. El módulo Quote sobrevive como pipeline de leads, que es otra cosa. |
| **C. Confirmación automática por job (no sincrónica)** | El dueño pidió transición "al quedar todos los servicios resueltos"; un job introduce latencia y ventanas en que el candado no refleja la realidad. El job queda solo como reconciliador. |
| **D. Evento de dominio / outbox para el motor** | Distribución sin necesidad: monolito, una DB, mutaciones sincrónicas. El chokepoint post-commit es más simple y testeable. |
| **D-bis. Transacción envolvente para mutación+motor (`CreateExecutionStrategy`+`BeginTransaction`, patrón `DeleteReservaAsync:2266`)** | Evaluada en round 2 (M2): atomicidad real, pero obliga a reestructurar todos los chokepoints save-then-recalculate y solo cierra una ventana que la reconciliación nocturna ya cura. Descartada; se revisa si aparecen usuarios concurrentes. Detalle en §4.4. |
| **E. `Balance` sigue siendo `TotalSale − TotalPaid` y solo cambia `TotalSale` a confirmados** | Rompería el valor comercial del presupuesto (mostraría $0 antes de confirmar) y decenas de consumidores de `TotalSale` (reportes, facturas, customer view). Dos números separan venta comercial de deuda exigible. |
| **F. Sub-estado "ConfirmadaParcial"** | El dueño lo descartó explícitamente: "En gestión muestra el detalle por servicio, no hace falta confirmada parcial". |
| **G. Enum/tabla de estados en vez de strings** | Cambiaría el contrato de persistencia de TODO el sistema por un beneficio marginal; el CHECK + constantes + tests de cobertura ya dan la seguridad. Fuera de alcance. |
| **H. Guardar "desde dónde se perdió" en una columna nueva para el revert de `Lost`** | Innecesario: `ReservaStatusChangeLog.FromStatus` de la última transición a Lost ya lo dice (B1). Cero schema nuevo. |

---

## 6. Fases de implementación (orden de merge; cada una con build + tests verdes)

Backend primero, frontend después. **Las fases F1–F3 forman UN ÚNICO TREN DE DEPLOY** (declarado, B3+B6): F1 y F2 son merges separados (cada uno compila y su suite pasa) pero NO se deployan sueltos al VPS — el rename de estados exige el frontend mínimo de F3, y el split de migraciones M1/M2 está diseñado para correr junto en la ventana de deploy.

**Orden de deploy del tren (B3, obligatorio):** parar la app → backup (F0) → migrar (`Adr020_M1` → `Adr020_M2`) → levantar el binario nuevo. Downtime aceptado (no hay clientes; único usuario = el dueño). Razón: M1 dropea la columna del flag y el binario viejo revienta si sigue corriendo contra la base migrada (§4.2).

- **F0 — Prevalidación de datos (VPS).** SQL de distribución de `Status` en `TravelFiles` y en las 6 tablas de servicio + **SQL de diff de `Balance`** para reservas `Traveling`/`ToSettle` (M3 — el dueño ve qué saldos cambian ANTES de migrar, §4.2). Backup de la base. Sin código.
- **F1 — Núcleo de estados + migración M1 + muerte del flag + remapeo de conjuntos (backend).** `EstadoReserva` nuevo (11 valores, muere `Sold`); matriz única forward/revert (incl. `→Lost` con gate sin-pagos, `Cancelled` manual B5, revert de `Lost` por historial); fusión de `ApplyClassic/ApplySoldToSettle`; unificación de las dos copias del gate revert-a-Budget (M5); `CreateReservaAsync` ignora Status; `ConvertToFileAsync` nace Quotation; job sin parámetro de flag; **remapeo completo de conjuntos §4.8** (obligado por compilación: la constante `Sold` muere — B6); fix del cref `OperationalFinanceSettings.cs:375` (CS1574); default del aéreo `"HK"→"NN"` (B4 — entidad + DTOs; el form del frontend se alinea en F3); migración `Adr020_M1` (§4.2); eliminación del flag en settings/DTOs/endpoints. Tests: nueva `ReservaLifecycleTests` (matriz completa, transiciones ilegales, gates relocalizados, `→Lost` con pago vivo rechazado, `Cancelled` manual desde InManagement sin factura OK / bloqueado desde Quotation/Budget / bloqueado con factura viva, revert de Lost al estado de origen), `EstadoReservaCoverageTests` re-escrito (CHECK 11), borrado de `ReservaSoldToSettleStatesTests`/`OperationalFinanceSettingsFlagsTests` (parte del flag), `FaseDStateSetTests` re-escrito (movido desde F3: los sets cambian acá).
- **F2 — Resolución por servicio + plata (backend).** `ServiceResolutionRules` (dos predicados §4.3); estampado `ConfirmedAt` (aéreo: por PNR confirmado); acción "Marcar emitido" (aéreo) y "No requiere confirmación" (traslado) con endpoints; **migración `Adr020_M2`** (`ConfirmedSale` + backfill + recompute `Balance`, §4.2); fórmula nueva en calculator/summary/3 call-sites; asignación explícita de cada consumidor de `ConfirmedServiceStatuses` a confirmación o resolución (§4.3); job auto-close pasa a `Balance <= 0` (M3). Tests: `ServiceResolutionRulesTests` (tabla por tipo, incl. aéreo HK-sin-ticket confirmado-pero-NO-resuelto), `ReservaMoneyCalculatorTests` extendido (solicitado no genera deuda; cancelado resta; saldo a favor), backfill verificado contra calculator, `Adr020_M2.Down` restaura `Balance = TotalSale − TotalPaid`.
- **F3 — Motor automático + regresión + frontend mínimo. ← Cierre del tren deployable.** `ReservaAutoStateService` + invocación post-commit en chokepoints de `ReservaService`/`BookingService` (contrato M2, §4.4) + reconciliación en el job (con contador logueado) + `Notification` de regresión (fallback admins + dedup diario); **frontend mínimo para deploy coherente:** labels/badges/tabs/filtros con los estados nuevos (incl. **cambio de clave de tab `"reserved" → "confirmed"`**), botones "Pasar a presupuesto"/"El cliente aceptó"/"No compró", default de status del form de aéreo alineado a `"NN"`, remoción del flag de `OperationalFlagsContext.jsx`/`OperationalFinanceSettingsTab.jsx`/`ReservasPage.jsx`/`ReservaDetailPage.jsx`/`useReservas.js`/`ReservaStatusBadge.jsx`/**`ReservaHeader.jsx`/`ReservaStatusChips.jsx`/`ConfirmReservaModal.jsx`** (M6) — spec por `ux-ui-disenador` ANTES (gate obligatorio). Tests: motor (confirma al resolver el último, regresa al agregar solicitado, regresa si operador cancela, idempotencia, contrato post-commit M2, notificación + dedup + fallback), reconciliación (cura una reserva desincronizada y loguea el contador), `AlertServiceUpcomingStartsTests` re-mapeado, frontend tests de tabs/badges.
- **F4 — Candado + autorización.** Entidades (ya migradas en M1) + permiso + `ReservaLockGuard` + endpoints + guards en operaciones protegidas + regla una-autorización-viva (§4.6). Frontend: modal de autorización (gate UX). Tests: 409 sin autorización, ventana expirada, admin también registra, filas Change escritas, no-cubiertos (pagos) pasan sin candado, nueva autorización expira la vigente.
- **F5 — Borrar vs cancelar servicio.** Reescritura de `DeleteGuards` (§4.7) + acciones de cancelación de servicio con auditoría + UI tachado (gate UX). Tests: borrar nunca-confirmado en cualquier etapa, **vuelo HK sin ticket NO borrable (solo cancelable — B4)**, cancelar confirmado resta saldo, candado pide autorización, guards conservados (soft-deleted payment, voucher).
- **F6 — Limpieza final + cierre de auditoría.** Barrido de referencias muertas (`Sold` en strings, comentarios, docs); **escritura ADITIVA de `ReservaStatusChangeLog` en los 7 sitios que setean `Status` por fuera de la máquina** (M7): `BookingCancellationService.cs:821,:1048,:1298,:1842,:2921,:3768` y `ArchiveReservaAsync` (`ReservaService.cs:2246`) — solo se agrega el log, no se tocan los flujos; exclusión de `Lost` en estados negativos consumidor por consumidor (§4.8); `docs/explicaciones` para Gastón; actualización de GRAPH_REPORT (`/graphify --update`). **Criterio de cierre (M6):** grep de `Sold|soldToSettle` limpio en `src/TravelWeb` — con DOS excepciones verificadas y documentadas (§1.4.8): `ToSettle`/`to-settle` permanece como estado legítimo (tab "A liquidar"), y `formatSoldDate`/`soldAt` de `ProductSearchField.jsx` es el historial de ventas del catálogo, sin relación con el estado. Backend: grep de `EstadoReserva.Sold`/`EnableSoldToSettleStates` limpio en `src/`.

Cada fase: implementación por `backend-dotnet-senior` → `backend-dotnet-reviewer` → `security-data-risk-reviewer` (toca bookings/payments/migraciones) → frontend con gate UX donde aplique, según CLAUDE.md.

---

## 7. Estrategia de testing

- **Unit (dominio puro):** `ServiceResolutionRules` (tabla exhaustiva por tipo y status para AMBOS predicados — confirmación y resolución —, casos borde: status libre en minúsculas, IATA desconocido, HK sin ticket = confirmado y no resuelto, traslado con marca), `ReservaMoneyCalculator` (ConfirmedSale vs TotalSale, cancelado, saldo a favor), matriz de transiciones (toda celda legal e ilegal, incluido el salto prohibido a Confirmed manual — sucesor de INV-SM-01 — y `Cancelled` manual ilegal desde Quotation/Budget).
- **Unit (servicios, InMemory como el patrón del repo):** motor auto-estado (ambas direcciones + idempotencia + actor en el log + **contrato de ejecución M2: la mutación commitea y el motor corre como save separado inmediato** + **notificación con fallback a admins cuando `ResponsibleUserId` es null** + **regresión re-entrante el mismo día NO duplica la `Notification`**), **reconciliación nocturna: cura una reserva desincronizada artificialmente y loguea cuántas curó**, candado (guard + ventana + registro + una-viva-reemplaza), DeleteGuards nuevo (**incluido: vuelo HK sin ticket NO borrable, solo cancelable**), creación siempre-Quotation, conversión de quote, **gates de Lost: `Budget→Lost` y `Quotation→Lost` con pago vivo rechazados**, **`Cancelled` manual: OK desde InManagement sin factura; rechazado con factura viva; rechazado desde Quotation/Budget**, **revert de `Lost` vuelve al estado de origen según `ReservaStatusChangeLog` (y fallback Budget sin log)**.
- **Integración (Postgres, suite del VPS — el InMemory NO valida CHECK constraints, lección de ADR-013):** migraciones `Adr020_M1` + `Adr020_M2` sobre copia de datos reales (Up + Down de ambas, en orden de tren y de rollback), **`Adr020_M2.Down` restaura `Balance = TotalSale − TotalPaid`** (B2), **`Adr020_M1.Down` remapea estados ANTES de restaurar el CHECK de 9** (B2 — el orden inverso debe fallar o quedar demostrado como inviable), CHECK de 11 valores rechaza `Sold`, backfills de `ConfirmedAt`/`ConfirmedSale` cuadran contra el recompute app-level (predicados espejo M1).
- **Regresión:** suites de cancelación (ADR-002/013/014), facturación y multimoneda deben quedar verdes SIN cambios de comportamiento (solo cambian sets de estados donde se documentó; respaldo: §1.4.1 — el flujo de cancelación no lee `Reserva.Balance`).
- **Frontend:** tabs/badges/botonera por estado (incl. clave `"confirmed"` reemplaza `"reserved"`), flujo de autorización, estados de error 409.
- **Smoke post-deploy (Gastón):** crear reserva → ver Cotización; pasar a presupuesto; aceptar; cargar hotel solicitado (saldo $0); confirmar hotel (saldo nace, `ConfirmedAt` visible); cargar aéreo (nace solicitado, NO "HK"), confirmar PNR (queda no-borrable) y marcar emitido → file pasa solo a Confirmada; agregar servicio nuevo → vuelve a En gestión + campanita (una sola aunque repita); borrar el servicio nuevo (debe dejar, era borrador); intentar borrar el aéreo confirmado (debe ofrecer solo cancelar); intentar editar confirmado → pide autorización; marcar un presupuesto como Perdido y revivirlo.

---

## 8. Riesgos y mitigaciones

| Riesgo | Impacto | Mitigación |
|---|---|---|
| Status libres históricos en tablas de servicio que los predicados del backfill no matchean | `ConfirmedAt`/`ConfirmedSale` mal backfilleados → saldos erróneos | predicados espejo de `MapGenericStatus`/`MapFlightStatus` por construcción (M1); F0 `SELECT DISTINCT` como verificación adicional; recompute app-level post-deploy como reconciliador |
| Cambio de fórmula de `Balance` altera qué reservas aparecen en cobranzas/alertas | El dueño ve "desaparecer deuda" que era del bug | Es el comportamiento pedido; F0 incluye el diff de Balance para `Traveling`/`ToSettle` (M3) y se documenta en `docs/explicaciones` el antes/después con ejemplos |
| Binario viejo corriendo contra base migrada (columna del flag dropeada) | Crash al leer settings | Orden de deploy obligatorio B3: parar app → backup → migrar → binario nuevo; downtime aceptado |
| Ventana entre el save de la mutación y el save del motor (contrato M2) | Estado del file desfasado un instante; en concurrencia, last-write-wins | Aceptado explícitamente (un usuario real); reconciliación nocturna como cura, con contador logueado para detectar chokepoints sin cubrir |
| Regresión automática "flapping" (operador confirma/desconfirma repetidamente) | Ruido de notificaciones | Dedup diario por reserva (§4.4); el motor solo notifica en la transición real Confirmed→InManagement; reconciliación nocturna no re-notifica |
| `Down` de las migraciones es lossy | Rollback pierde timestamps y la distinción Lost/Cancelled | Aceptado y documentado; orden de rollback explícito (M2.Down → M1.Down, app parada); backup previo en F0 es la red real |
| El frontend viejo contra backend nuevo (ventana de deploy) | Tabs/badges rotos | F1–F3 son un solo tren de deploy; el deploy del VPS sube API+frontend juntos (compose) |
| Candado débil (supervisor seleccionado, no autenticado) | Auditoría impugnable | Limitación aceptada, igual al revert existente; mejora futura anotada |
| `ConfirmedAt` proxy = `CreatedAt` en históricos | Penalidades históricas con fecha imprecisa | Sin efecto práctico (penalidades corren hacia adelante); anotado |
| `Cancelled` manual esquiva el flujo ADR-002 por error de uso | Cancelación sin NC/penalidades donde correspondía | El gate "sin factura viva" cierra el caso fiscal; sin factura no hay NC que emitir; `ReservaStatusChangeLog` deja quién/cuándo/motivo; reversible solo por flujo existente |
| Quotation sin servicios borrable + numeración `NumeroReserva` consume números | Huecos de numeración | Ya pasa hoy con Budget; sin cambio de riesgo |

**Invariantes que este ADR declara:**
- INV-020-01: ninguna reserva nace en un estado ≠ `Quotation`.
- INV-020-02: `Confirmed` solo se alcanza, y solo se abandona-hacia-`InManagement`, por el motor automático; jamás por `UpdateStatusAsync` manual.
- INV-020-03: `Balance = ConfirmedSale − TotalPaid`; un servicio no resuelto jamás suma a `ConfirmedSale`.
- INV-020-04: un servicio con `ConfirmedAt != null` nunca se borra físicamente; solo se cancela.
- INV-020-05: toda mutación bajo candado referencia una `ReservaEditAuthorization` viva; sin excepción por rol. A lo sumo UNA autorización viva por reserva.
- INV-020-06 (acotado, M7): toda transición de `Status` **ejecutada via `UpdateStatusAsync`, `RevertStatusAsync`, el motor automático o el job** escribe `ReservaStatusChangeLog`. Los 7 escritores externos conocidos (6 sitios de `BookingCancellationService` + `ArchiveReservaAsync`, §1.4.4) ganan el log de forma aditiva en F6; hasta entonces quedan explícitamente fuera del invariante.
- **Se INTRODUCE (B5):** `Cancelled` es alcanzable manualmente (sin flujo ADR-002) SOLO desde {InManagement, Confirmed, Traveling, ToSettle} y SOLO sin factura viva; siempre con `ReservaStatusChangeLog`. Con factura viva, `Cancelled`/`PendingOperatorRefund` siguen siendo exclusivos del flujo ADR-002. Desde Quotation/Budget no existe cancelación (la salida es `Lost` o el borrado).
- Se conserva: factura con CAE inmutable y bloquea reverts (existente).

**Qué NO cambia:** flujos de cancelación/NC/ND (ADR-002/013/014/015) salvo los sets de estados documentados en §4.8 (y verificado §1.4.1: no leen `Reserva.Balance`); facturación ARCA y multimoneda (ADR-012); vouchers; recibos; catálogo (ADR-017/018); mecánica de la campanita (ADR-019 — solo su set de estados); soft-delete `Archived`; módulo Quotes/Leads (solo el estado de nacimiento de la reserva convertida); deuda al proveedor (`CountsForSupplierDebtByType`, §4.3); los otros 4 feature flags existentes.

---

## 9. Supuestos y preguntas abiertas

**Decisiones tomadas por el arquitecto (supuestos razonables, se construye así salvo objeción):**
- A1: candado se extiende a Traveling/ToSettle/Closed (no solo Confirmed) — coherente con la intención "desde confirmada, intocable".
- A2: revert `InManagement→Budget` exige que ningún servicio esté resuelto (y M5: el viejo check "tiene servicios" muere).
- A3: "Marcar emitido" pide `TicketNumber` pero permite vacío con advertencia (no bloquea la operación real donde el ticket llega después por mail).
- A4: backfill `ConfirmedAt = CreatedAt` para servicios históricos ya confirmados.
- A5: ventana de autorización de candado = 30 minutos; una autorización viva por reserva (la nueva reemplaza).
- A6: `Lost` se excluye de revenue/AR igual que `Cancelled`.
- A7 (round 2): default del aéreo nuevo = `"NN"` (mapea a Solicitado por la rama default de `MapFlightStatus`; cabe en `MaxLength(2)`). Si el dueño prefiere otro código IATA "solicitado", es un cambio de un literal.
- A8 (round 2): "factura viva" para el gate de `Cancelled` manual = CAE no vacío AND anulación no exitosa (`AnnulmentStatus != Succeeded`) — más fino que el hard-blocker de revert existente (que mira solo CAE), porque una reserva cuya factura ya fue anulada por NC debe poder cancelarse a mano.

**Resueltas en round 2 por decisión del dueño:** existencia de `Quotation→Lost` y su re-entrada (B1); separación confirmación/emisión del aéreo y default no-confirmado (B4); `Cancelled` manual sin factura viva (B5).

**Preguntas abiertas para Gastón (vía orquestador, antes de la fase que las toca):**
- **Q1 (bloquea F3, set de facturación):** hoy "Vendida" no podía facturar; con el ciclo nuevo, ¿se puede emitir factura por una seña mientras la reserva está EN GESTIÓN (antes de Confirmada)? Recomendación del ADR: mantener conservador (solo desde Confirmada) y abrirlo después si lo necesita; si dice que sí, validar con el contador.
- **Q2 (F3, UX):** al convertir una cotización ACEPTADA del módulo de cotizaciones, la reserva nace en Cotización y hay que apretar dos botones para llegar a En gestión. ¿Quiere un avance rápido (un botón que recorra las etapas, registrado)? Va por el gate UX.
- **Q3 (F4, UX):** quién puede autorizar ediciones bajo candado: propuesta = rol Admin (y quien reciba el permiso nuevo). ¿Está bien o quiere que cualquier vendedor pueda autorizar a otro?

---

## 10. Consecuencias

**Positivas:** un solo ciclo (muere el dual-cycle y sus 4 matrices), el ciclo refleja el negocio real del dueño, el saldo deja de mentir, el bug de borrado muere de raíz, candado auditable, base limpia para penalidades por servicio (corren desde `ConfirmedAt`, que ahora registra el hecho correcto: el compromiso del operador).

**Negativas / costos:** migración con backfill de plata (el punto más delicado — mitigado por F0 con diff de Balance + reconciliación); ~6 suites de tests reescritas; `Down` lossy; downtime de deploy aceptado; el motor automático agrega un comportamiento "que se mueve solo" que el vendedor debe entender (mitigado por notificaciones con dedup y el detalle por servicio en "En gestión"); dos números de venta (`TotalSale`/`ConfirmedSale`) y dos predicados por servicio (confirmado/resuelto) exigen disciplina en consumidores nuevos — las fuentes únicas `ReservaMoneyCalculator` y `ServiceResolutionRules` son la defensa.
