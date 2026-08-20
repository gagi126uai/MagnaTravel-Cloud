# Micro-spec: descalce devolución-caja y anulación a medias

Fecha: 2026-08-19. Decisiones firmadas por Gastón en
`docs/ux/guia-ux-gaston.md` → sección **"Descalce devolución-caja y anulación a medias: avisos
que trabajan, no que gritan (2026-08-19)"**. Esta spec convierte esas 3 decisiones en 4 entregas
concretas (una pantalla se partió en dos por tener dos ramas de diseño distintas). Cero preguntas
nuevas: todo lo que faltaba se resolvió leyendo el motor real (marcado como **DEP backend** o
**sin dependencia** en cada bloque) — no son decisiones de UX pendientes, son cableado técnico.

Reglas de la constitución citadas por número: P-3, P-9 (+ enmienda 11/08), P-10, P-11, P-13,
P-16, P-17, P-20, T-1, T-4, T-5, T-6, T-10, B.5 (`docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md`,
molde `StatusChip`).

## Qué NO cambia

- El banner naranja full-width sigue existiendo como componente, pero queda **reservado para
  caídas/eventos de todo el sistema** (decisión 1). Este descalce puntual de UNA reserva nunca
  más lo usa.
- El mecanismo de emisión de notas de crédito (idempotente, firmado 2026-07-01) no se toca: la
  ADR-042 sigue igual puertas adentro. Solo cambian el texto, la estructura visual y el botón.
- `OperatorRefundsPendingSection` (bloque "a cobrar") no se toca — el descalce es un caso de
  reembolso YA REGISTRADO (la caja diverge del extracto DESPUÉS de haberlo anotado), vive en
  `OperatorRefundsRegisteredSection`.
- El permiso de tesorería sigue siendo `tesoreria.supplier_payments` en las tres superficies.

---

## 1. Ítem descalzado en la solapa Reembolsos del operador

**Archivo:** `src/TravelWeb/src/features/suppliers/components/OperatorRefundsRegisteredSection.jsx`
(función `FilaReembolsoRegistrado`). Rige P-3, P-16, T-4, B.5.

### Condición de aparición

Solo en una fila **viva** (`item.isVoided === false`) cuyo `hasCashLedgerDivergence === true`.
Una fila deshecha nunca muestra esto (ya está tachada, sin acciones — regla existente).

### Mockup — desktop (dentro de la fila, debajo del renglón principal, arriba de los botones)

```
Reserva F-2026-1189 · Fam. Rodríguez   USD   US$ 1.200,00   14/08/2026   [Deshacer] [Corregir reserva]

┌─────────────────────────────────────────────────────────────────────┐
│ [No coincide con la caja]                                           │
│                                                                       │
│   Figura recibido        US$ 1.200,00                               │
│   En la caja              US$   900,00                              │
│   Diferencia               US$   300,00                              │
│                                                                       │
│   Ver la anulación ↗          Ver el movimiento de caja ↗            │
└─────────────────────────────────────────────────────────────────────┘
```

### Mockup — mobile (misma tarjeta, apilado)

```
┌ Reserva F-2026-1189 ──────────────┐
│ Fam. Rodríguez · US$ 1.200,00      │
│ 14/08/2026                         │
│                                    │
│ [No coincide con la caja]         │
│  Figura recibido   US$ 1.200,00   │
│  En la caja         US$   900,00  │
│  Diferencia          US$   300,00  │
│                                    │
│  Ver la anulación ↗               │
│  Ver el movimiento de caja ↗      │
│                                    │
│ [Deshacer]     [Corregir reserva] │
└────────────────────────────────────┘
```

### Detalle

- Chip `StatusChip tone="ambar"`: **"No coincide con la caja"** (B.5: ámbar = "te pide algo").
  Sin ícono (no hace falta, ya está el texto).
- Caja contenedora: mismo molde que el aviso `penalty-pending` ya existente en
  `OperatorRefundsPendingSection.jsx` (`rounded-[10px] border border-amber-200 bg-amber-50 px-3
  py-2`, variante dark ya definida ahí) — reusar la misma clase, no inventar una nueva.
- Tres líneas, **una por moneda si hubiera más de una divergencia en la misma reserva** (P-3: cada
  moneda su propio bloque de 3 líneas, nunca sumadas):
  - `Figura recibido` — monto con `formatCurrency` (T-4).
  - `En la caja` — ídem.
  - `Diferencia` — valor absoluto de la resta, mismo formato. Va en negrita (es el número que
    importa resolver).
- Dos accesos con `ExternalLink` (mismo patrón que "Ir a la reserva a confirmar"):
  - **"Ver la anulación"** → `Link to={/reservas/${item.reservaPublicId}}`.
  - **"Ver el movimiento de caja"** → `Link to="/cash"` (la página de Caja no tiene pestañas ni
    resaltado por fila hoy; ir al listado general es lo mejor disponible sin trabajo nuevo. Mejora
    futura opcional, no bloqueante: deep-link + resaltar la fila).
- data-testid sugerido: `descalce-caja-${item.publicId}`.

### DEP backend (necesario, acotado)

`OperatorRefundRegisteredItemDto` (`src/TravelApi.Application/DTOs/OperatorRefundRegisteredDtos.cs`)
necesita 3 campos nuevos, calculados con el mismo cálculo que ya usa
`CashLedgerRefundReconciliationJob` / `CashLedgerRefundReconciliationCalculator` (no hay que
inventar la cuenta, ya existe ahí):

```csharp
public bool HasCashLedgerDivergence { get; set; }
public decimal DerivedAmount { get; set; }   // "Figura recibido"
public decimal LedgerAmount { get; set; }    // "En la caja"
```

Nota de alcance heredada (no es un gap nuevo): el job solo evalúa reembolsos cuyas asignaciones
apuntan a UNA sola cancelación (los repartidos N:M quedan fuera, documentado en el propio job). Por
eso estos 3 campos se pueden calcular 1:1 sobre la fila de la allocation sin ambigüedad — cuando
haya reparto entre varias cancelaciones, `HasCashLedgerDivergence` simplemente no se enciende (mismo
comportamiento que hoy tiene el aviso de la campanita).

---

## 2. Aviso en la campanita

**Archivos:** backend `src/TravelApi.Infrastructure/Services/CashLedgerRefundReconciliationJob.cs`
(método `BuildUserMessage`, línea ~287, y la creación de la `Notification` en
`ConciliarUnaCancelacionAsync`, línea ~269); frontend
`src/TravelWeb/src/components/NotificationBell.jsx`. Rige P-11, P-17, T-1, T-5.

### Texto exacto (reemplaza el actual)

Una moneda divergente:

> **"La devolución del operador de la reserva {numeroReserva} no coincide con la caja: hay una
> diferencia de {moneda} {monto}. Revisala cuando puedas."**

Dos o más monedas divergentes en la misma reserva (P-3: nunca sumar):

> **"La devolución del operador de la reserva {numeroReserva} no coincide con la caja: hay una
> diferencia de {moneda1} {monto1} y {moneda2} {monto2}. Revisala cuando puedas."**

Sin número de reserva (caso defensivo ya contemplado en el código actual):

> **"La devolución del operador de una reserva no coincide con la caja. Revisala cuando puedas."**

Notar: se saca "antes de cerrarla" (sonaba a plazo urgente, lo que contradice la decisión de
bajarle el tono) y se agrega el monto de la diferencia por moneda (pedido explícito de la
decisión 1). Sujeto = "la devolución"/"la reserva", nunca "el sistema"/"el job" (P-17). Sin
términos fiscales (no dice "nota de crédito" ni "ARCA" — esto es un aviso genérico de campanita,
no una pantalla de facturación).

### Prioridad y tono (cambio de dato, no de código en NotificationBell)

- `Notification.Priority` pasa de `"Urgent"` a **`"Normal"`** (decisión 1: sin banner, sin
  etiqueta "⚡ Urgente", sin fila resaltada en rojo — `getRowHighlight`/el bloque `priority ===
  "Urgent"` de `NotificationBell.jsx` ya dejan de aplicar solos con este cambio de dato).
- `Notification.Type` se mantiene `"Warning"` → punto ámbar en la lista (`getDotColor`), coherente
  con el chip ámbar de la solapa Reembolsos (B.5).

### Audiencia

Ya está bien encaminada en el job (`GetUsersInRoleAsync("Admin")`), pero la decisión 1 dice
explícitamente "el aviso va SOLO a quien maneja tesorería (mismo permiso que la solapa
Reembolsos)". **DEP backend:** cambiar el filtro de audiencia de "rol Admin" a "usuarios con el
permiso `tesoreria.supplier_payments`" (mismo criterio que ya usa el front en
`OperatorRefundsRegisteredSection` vía `hasPermission`).

### Navegación al tocar (DEP backend + DEP frontend, funcionalidad nueva)

Hoy los avisos genéricos de la campanita (sección "NOTIFICACIONES") **no navegan a ningún lado**
— son un `<div>` sin `Link`, solo tienen el botón de marcar como leída. El propio `NotificationDto`
tiene un comentario explícito (T-5) prohibiendo exponer `RelatedEntityType`/`RelatedEntityId`
crudos. Hace falta un campo NUEVO y seguro, no reusar esos.

- **DEP backend:** agregar a `NotificationDto` (`src/TravelApi/Contracts/NotificationDto.cs`) un
  campo `string? TargetUrl` — una ruta relativa YA ARMADA del lado del servidor, con `PublicId`
  (nunca el id interno). Para este aviso puntual:
  `TargetUrl = $"/suppliers/{supplierPublicId}/account?tab=reembolsos"` (el job ya puede resolver
  `SupplierPublicId` desde el `BookingCancellation`, agregar esa columna a la query existente).
- **DEP frontend:** en `NotificationBell.jsx`, cuando `notification.targetUrl` existe, envolver la
  fila en un `<Link to={notification.targetUrl} onClick={cerrarPanel}>` (mismo patrón que
  `SeccionProximosInicios`); el botón de "marcar leída" sigue con `stopPropagation()` para no
  disparar la navegación (ya lo hace hoy con `handleMarkAsRead`). Si `targetUrl` es null (avisos
  viejos, otros tipos de notificación), la fila se comporta EXACTAMENTE igual que hoy (sin
  romper nada existente).
- **DEP frontend (SupplierAccountPage):** hoy `activeTab` es puro `useState` local, sin leer la
  URL. Agregar lectura de `?tab=` al montar (si viene `tab=reembolsos`, arrancar en esa solapa en
  vez de `"cuenta-corriente"`). Cambio chico, mismo id de solapa que ya existe (`"reembolsos"`).

---

## 3. Freno en Caja/Tesorería

**Archivos:** `src/TravelWeb/src/features/payments/components/MovementsTab.jsx` (página `/cash`,
única pantalla de movimientos manuales — no tiene pestañas internas) y
`src/TravelWeb/src/features/payments/lib/cashMovementBadgeLogic.js`. Rige P-9 (+ enmienda 11/08),
P-10, P-17.

**Sin dependencia de backend.** El dato ya existe: todo movimiento de caja generado por un
reembolso de operador trae `movement.category === "OperatorRefund"` (constante ya mapeada en
`cashMovementLabels.js` → `"Devolución recibida del operador"`) y `movement.numeroReserva` ya
viaja en la fila. Es 100% cableado frontend.

### Regla

Un movimiento con `category === "OperatorRefund"` apaga Editar y Anular, además de las causas que
ya lo apagan hoy (`isReplaced`/`isAnnulled`). No se agrega ningún chip nuevo — el ícono ya se ve
gris cuando el botón está apagado, eso alcanza para distinguirlo a simple vista (P-16: no repetir
el dato con un badge de más).

### Motivo exacto (idéntico en escritorio y táctil)

> **"Atado a la devolución recibida del operador de la reserva {numeroReserva}. Se corrige desde
> el circuito de la devolución (solapa Reembolsos), no acá."**

### Dónde va el motivo (P-9 + enmienda 11/08 — mismo patrón que "Archivar" del listado de reservas)

- **Escritorio:** globito nativo. Envolver el par de botones (Pencil + Trash2) en un
  `<span title={motivo}>` — el `title` NO vive en el `<button disabled>` (un botón deshabilitado
  no dispara el hover, mismo bug ya documentado en `ReservaTable.jsx`). Sin texto fijo debajo en
  este caso puntual (el motivo solo aparece al pasar el mouse).
- **Táctil/mobile:** el motivo queda **escrito**, visible siempre, en el mismo lugar donde hoy se
  muestra `estadoBadge.motivoBotonesApagados` para Reemplazado/Anulado (span chico gris a la
  derecha de los botones, `MobileRecordCard`).

### Mockup — fila de Caja, escritorio

```
14/08/2026   [↙] Cobranza          Reserva F-2026-1189      Transferencia   US$   [Editar-gris] [Anular-gris]
                 Devolución recibida del operador                                  ⤷ hover: "Atado a la devolución
                                                                                       recibida del operador de la
                                                                                       reserva F-2026-1189. Se corrige
                                                                                       desde el circuito de la
                                                                                       devolución (solapa Reembolsos),
                                                                                       no acá."
```

### Mockup — tarjeta de Caja, mobile

```
┌ Cobranza ──────────────────────────┐
│ Devolución recibida del operador    │
│ Reserva F-2026-1189                 │
│                                      │
│           US$ 1.200,00  [✎gris][🗑gris]│
│           Atado a la devolución de  │
│           la reserva F-2026-1189.   │
│           Se corrige desde el       │
│           circuito de la devolución │
│           (solapa Reembolsos), no   │
│           acá.                      │
└──────────────────────────────────────┘
```

### Nota de alcance

Esto NO toca el freno ya existente de `isReplaced`/`isAnnulled` (esos siguen mostrando su badge y
su motivo visible como hoy, en ambas vistas — no está en el pedido de esta obra tocarlos).

---

## 4. Cartel de anulación a medias en la ficha de la reserva

**Archivos:** `src/TravelWeb/src/features/reservas/pages/ReservaDetailPage.jsx` (líneas ~1592-1643,
rama `mostrarFranjaEnRevision`); `src/TravelWeb/src/features/cancellations/lib/multiCreditNoteFlow.js`;
`src/TravelWeb/src/features/cancellations/components/cancelarReservaCopy.js`;
`src/TravelWeb/src/features/cancellations/components/NotasCreditoProgressList.jsx`. Rige P-11,
P-13, P-17 (acá SÍ se permite "nota de crédito"/"ARCA": es la pantalla de anulación con factura,
mismo precedente ya firmado en `cancelarReservaCopy.js`, no un aviso genérico), T-1, T-6.

Hoy `stuckCancellation` (y por lo tanto la franja) se enciende con UNA sola condición del backend
(`canRetryCreditNotes`), que mezcla dos causas distintas: una nota **rechazada por ARCA** (hay
motivo textual) y una nota **atascada sin trabajo en curso** (sin motivo, el job de anulación de
esa factura murió). Las dos entran a la MISMA rama hoy. La decisión 3 solo pide dividir la
alarma real del caso "se resuelve sola" — mantenemos ambas causas actuales dentro de la rama de
alarma (las dos necesitan el botón, ninguna se resuelve sin acción), y agregamos una rama nueva,
tranquila, para el caso que HOY no muestra nada: una nota todavía Pendiente y con el job
trabajándola en este mismo instante (`canRetryCreditNotes = false` pero la cancelación sigue sin
cerrar del todo). *(Este mapeo de condiciones se dedujo leyendo `EvaluateCanRetryCreditNotes` en
`BookingCancellationService.cs`; frontend-senior lo verifica contra el motor real antes de
implementar — no es una decisión de UX, es lectura de código.)*

### Rama A — alarma (reemplaza la franja actual, mismo trigger `stuckCancellation`)

**Encabezado:** se mantiene `construirTextoEncabezadoRevision` tal cual (ya aprobado, sigue
sirviendo de resumen arriba de la lista).

**Lista por factura (NUEVA, reemplaza a `NotasCreditoProgressList` en este contexto):**

```
🟠 La reserva quedó EN REVISIÓN: una nota de crédito salió bien y la otra no.
   La que salió no se deshace.

   ✓  Factura B 0001-00012345 — nota de crédito emitida
   ✗  Factura B 0001-00012346 — la nota no salió. ARCA respondió:
      «CUIT del emisor sin habilitación para operar»

              [ Emitir la nota que faltó ]
```

- Cada línea: ícono (✓ / ✗) + `factura.comprobanteLabel` (ej. "Factura B 0001-00012345") + " — " +
  resultado. Formato exacto:
  - Succeeded → `"{comprobanteLabel} — nota de crédito emitida"`.
  - Failed → `"{comprobanteLabel} — la nota no salió. ARCA respondió: «{arcaErrorMessage}»"` (el
    motivo TAL CUAL lo manda el motor, sin parafrasear — P-13, ya se hace así en
    `NotasCreditoProgressList` hoy).
  - Pending-atascada sin motivo (job muerto, no hay `arcaErrorMessage` porque nunca llegó a
    fallar) → `"{comprobanteLabel} — la nota todavía no salió."` (sin inventar un motivo que el
    motor no mandó).
- **Botón único, texto exacto:** **"Emitir la nota que faltó"** — reemplaza tanto
  `TEXTO_BOTON_REINTENTAR_ANULACION` (el de la franja) como, por la misma razón y el mismo motivo
  (misma acción, no puede tener dos nombres distintos), `TEXTO_BOTON_REINTENTAR_FALTANTE` (el de
  adentro del panel de reintento, Estado 4). Mismo mecanismo idempotente, sin cambios de
  comportamiento — solo el texto.
- Sigue sin poder deshacerse la nota que sí salió (texto ya existente, no cambia).

### Rama B — "en curso" (NUEVA, sin cartel de alarma, sin botón)

**Cuándo aparece:** la reserva está en el tramo de anulación (`reserva.status ===
"PendingOperatorRefund"`), la cancelación tiene al menos una nota de crédito todavía `Pending`,
NINGUNA `Failed`, y el backend NO habilita el reintento (`canRetryCreditNotes === false` — es
decir, el job de emisión sigue efectivamente trabajándola). Va en el mismo lugar donde hoy no se
muestra nada.

```
🔵 La nota de crédito de esta anulación se está terminando de emitir en ARCA.
   No hace falta que hagas nada — en un rato la vas a ver reflejada sola.
```

- Contenedor azul/informativo (tono `azul` de B.5: "en curso"), NO ámbar ni rojo:
  `rounded-[10px] border border-blue-200 bg-blue-50 p-4 text-sm text-blue-800 dark:border-blue-900/40
  dark:bg-blue-950/20 dark:text-blue-300`.
- Sin ícono de alerta, sin botón, sin acción posible — es puramente informativo (P-20: aviso
  suave, no frena nada, el resto de la ficha sigue de solo lectura como corresponde a
  "PendingOperatorRefund", pero este cartel en particular no agrega ninguna acción nueva).
- `data-testid="banner-anulacion-en-curso"`.

### Qué NO cambia en esta pantalla

- El caso `yaWaived` (cerrada sin multa del operador) no se toca.
- El caso de éxito total (`todasLasNotasSalieronBien`) no se toca — sigue con su cartel verde
  actual.
- El aviso previo y el "¿Seguro?" antes de confirmar la anulación (Estados 0 y 1 de
  `CancelarReservaInline`) no cambian.

---

## Resumen de DEP backend (para priorizar)

1. `OperatorRefundRegisteredItemDto` + 3 campos (`HasCashLedgerDivergence`, `DerivedAmount`,
   `LedgerAmount`) — bloque 1.
2. `CashLedgerRefundReconciliationJob`: nuevo texto del mensaje (bloque 2), prioridad `"Normal"`,
   audiencia por permiso en vez de rol Admin, agregar `SupplierPublicId` a la query existente.
3. `NotificationDto` + campo `TargetUrl` (string, ruta relativa con PublicId) — solo para este
   aviso por ahora; generalizarlo a otros tipos de notificación es decisión aparte, no de esta obra.
4. `BookingCancellationCreditNoteDto` + campo `OriginatingInvoicePublicId` (bloque 4) — el dominio
   ya tiene `OriginatingInvoiceId` en la entidad, solo falta mapear el `PublicId` del `Invoice` al
   DTO (mismo patrón que ya usa `BuildSaleInvoicesDtoAsync`).

Sin DEP backend: bloque 3 completo (Caja) y la lectura de `?tab=` en `SupplierAccountPage`.
