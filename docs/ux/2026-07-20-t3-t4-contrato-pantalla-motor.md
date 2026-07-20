# Spec UX — Tandas 3 y 4 del plan de remediación del contrato pantalla ↔ motor

**Fecha**: 2026-07-20
**Autor**: ux-ui-disenador (diseña SOLO desde `docs/ux/guia-ux-gaston.md`)
**Insumos**: `docs/architecture/2026-07-18-contrato-pantalla-motor-PLAN-remediacion.md` (Tandas 3 y 4 + Decisión C + §6 firmadas por Gastón el 2026-07-18) · `docs/ux/2026-07-18-t1-t2-contrato-pantalla-motor.md` (formato de referencia) · código real (`CancelarReservaInline.jsx`, `CancelReservaModal.jsx`, `CancelarVariosServiciosInline.jsx`, `ServiceList.jsx`, `BookingCancellationService.cs`, `ReservaCapabilities.cs`, `MutationGuards.cs`).
**Para**: frontend-senior + backend-dotnet-senior (implementan al pie de la letra).

---

## Resumen para el orquestador (leer esto primero)

**Las dos tandas están 100% CUBIERTAS por la guía + el plan ya firmado + la propia regla de vocabulario del dueño. NO hay preguntas nuevas para Gastón.**

- **T3 (anular reserva dice el motivo real):** el swallow de `CancelarReservaInline.jsx` es deliberado (evita mostrar texto crudo) pero se lleva puestos mensajes que el propio backend YA calcula bien. La Decisión C del plan (envelope aditivo `code`/`invariantCode`, firmada como D2 el 2026-07-18: "mejorar solo los rotos") y el precedente del componente MUERTO `CancelReservaModal.jsx` (que sí mapeaba INV-152 a un texto claro) me dan todo lo necesario para armar el mapa código→criollo. El único caso "nuevo" que auditando encontré (plata pagada al operador sin factura, a nivel de TODA la reserva) usa el MISMO botón "Emitir factura" que Gastón ya aprobó como D1 el 2026-07-18 para el caso gemelo de un servicio suelto — no es una decisión nueva, es la misma aplicada al mismo freno.
- **T4 (anular varios ayuda igual que anular uno + vocabulario):** la paridad pedida es copiar el patrón que YA existe en `ServiceList.jsx` (`ModalBloqueoCancelacionServicio`, botón "Ver facturas de la reserva") hacia las filas fallidas de `CancelarVariosServiciosInline.jsx`, reusando el mismo callback `onIrAFacturas` que `ServiceList.jsx` ya recibe y no le pasa. El rename "cancelar" → "anular" es la propia regla del dueño (no UX): audité el backend y encontré 4 mensajes concretos que todavía dicen "cancelar" en el sentido de anular un servicio.

No se inventan componentes nuevos, ni modales nuevos, ni pantallas nuevas en ninguna de las dos tandas.

---

# TANDA 3 — "Anular reserva" dice el motivo real

## Qué cubre la guía + el plan firmado (citado)

1. **Cartel rojo dentro del panel en línea, ficha intacta — mismo patrón que T1.**
   `CancelarReservaInline.jsx` YA tiene ese cartel (`data-testid="cancelar-inline-conflict-msg"`, fondo rosa, `role="alert"`, arriba de los botones). Guía, "Ficha de carga en línea F2 → Ronda 2" (2026-06-05): *"cartel rojo arriba de los botones (...); reintenta en el mismo botón."* No hay pantalla nueva que diseñar: el cartel que existe hoy pasa a mostrar el texto correcto según el código que llegue.

2. **Decisión C del plan (envelope aditivo) + D2 firmada (2026-07-18): "mejorar solo los mensajes rotos, no reescribir todo."**
   El backend YA manda un `message` seguro en cada `BusinessInvariantViolationException` (con `invariantCode`, ver verificación abajo). El front NO reescribe los ~90 casos que ya andan bien: solo intercepta los pocos códigos donde hoy se pierde información al mostrar el genérico de siempre.

3. **Cada rechazo dice QUÉ HACER AHORA, sin jerga, sin IDs, sin nombres internos.**
   Regla transversal del plan (heredada de la guía). El fallback para códigos no mapeados sigue siendo el texto neutro actual — nunca el texto crudo del backend, salvo que esté explícitamente en el mapa.

4. **El botón "Emitir factura" en el cartel del freno de plata (candado R1): ya es D1 firmada (2026-07-18).**
   Texto de Gastón, §6.B del plan: *"Recomiendo: sí, botón 'Emitir factura' en el cartel."* Auditando el código encontré que este mismo freno (plata pagada al operador sin factura que ancle el reembolso) existe TAMBIÉN a nivel de la reserva entera (no solo por servicio suelto, que es Tanda 7): `EnsureReservaAnnulHasReceivableAnchorAsync` en `BookingCancellationService.cs:931-949`, con un mensaje que YA dice "Emití la factura de venta...". Es el mismo freno, la misma decisión D1, aplicada al mismo texto.

**Conclusión T3:** no hay decisión de diseño nueva. Lo que falta es trabajo de mapeo, no de invención.

## Auditoría del patrón vigente (lo que ya está construido, y lo que ya se perdió)

`CancelarReservaInline.jsx` tiene **tres puntos de swallow**, todos con la misma forma: atrapan cualquier 409 y muestran un texto genérico fijo, salvo un único caso ya resuelto (`CONCURRENT_EDIT`, que si funciona bien y no se toca).

| Punto de swallow | Endpoint | Qué pierde hoy |
|---|---|---|
| `handleCancelar` → caso `DirectCancel`/`PaymentsToCredit` | `POST /annul-with-credit` | Cualquier 409 → *"No se pudo anular la reserva. Probá de nuevo; si el problema sigue, contactá a administración."* — sin importar si la causa real es "no está firme", "ya tiene factura" o "sin pagador". |
| `handleCancelar` → caso `CreditNote`, paso `draft()` | `POST /cancellations/draft` | Cualquier 409 → *"No se pudo iniciar la anulación. Probá de nuevo; si el problema sigue, contactá a administración."* — pierde INV-152, INV-081, INV-100. |
| `handleCancelar` → caso `CreditNote`, paso `confirm()` | `PATCH /cancellations/{publicId}/confirm` | Ya distingue `CONCURRENT_EDIT` (texto correcto, NO tocar). Cualquier OTRO 409 → mismo genérico de arriba — pierde INV-093 e INV-100. |

El componente **`CancelReservaModal.jsx` está muerto** (no lo importa nadie — verificado, cero referencias fuera del propio archivo). Ahí vivía el único mapeo bueno que existió (INV-152 con texto claro). Se **retira** del código; su texto se rescata en el mapa nuevo de abajo.

## El mapa código → criollo (lo que hay que construir)

**Regla del mapa (aplica a los tres puntos de swallow por igual — es UNA sola función que se reusa en los tres `catch`):**

1. Si el 409 trae un código que está en la tabla de abajo → se muestra el texto de la tabla (y el botón, si la fila lo tiene).
2. Si no está en la tabla (código desconocido o ausente) → se muestra el **mismo texto neutro que hoy** ("No se pudo [iniciar/confirmar/anular] la [anulación/reserva]. Probá de nuevo; si el problema sigue, contactá a administración."). **Nunca** el texto crudo del backend fuera de esta tabla — sigue siendo la política de seguridad original del componente.
3. `CONCURRENT_EDIT` sigue exactamente como está (no se toca: ya funciona bien).

| Código | Dónde salta | Texto criollo (reemplaza al genérico) | Botón / camino |
|---|---|---|---|
| `INV-152` | `draft()` — reserva con servicios de varios operadores | "Esta reserva tiene servicios de varios operadores. Por ahora la anulación de reservas con varios operadores no está disponible desde acá." | "Gestionala manualmente o pedile ayuda a un administrador." (texto, sin botón — rescatado de `CancelReservaModal.jsx`, único ajuste: "cancelación" → "anulación") |
| `INV-081` | `draft()` — ya existe una anulación activa sobre esta reserva | "Esta reserva ya tiene una anulación en curso." | Sin botón. "Actualizá la página para ver en qué quedó." |
| `INV-100` | `draft()` o `confirm()` — la factura original ya fue anulada con una nota de crédito | "La factura de esta reserva ya fue anulada con una nota de crédito. No queda nada más para anular." | Sin botón (informativo: no hay acción posible). |
| `INV-093` | `confirm()` — la anulación cambió de estado mientras el panel estaba abierto | "Esta anulación cambió de estado mientras la tenías abierta." | Sin botón. "Actualizá la página para ver cómo sigue." |
| *(nuevo, backend agrega el code)* — reserva no está En gestión ni Confirmada | `annul-with-credit` | "Esta reserva todavía no está En gestión ni Confirmada. Para anularla así, primero tiene que estar en una de esas dos etapas." | Sin botón. |
| *(nuevo)* — la reserva ya tiene factura emitida (camino formal) | `annul-with-credit` | "Se emitió una factura en esta reserva mientras la tenías abierta. Para anularla ahora hay que hacerlo por el camino con nota de crédito." | "Cerrá este panel y volvé a abrir 'Anular reserva' — el sistema ya te va a ofrecer el camino correcto." (sin botón nuevo: al reabrir, el panel relee `cancellationCase` de la reserva y solo ya cae en el flujo con factura) |
| *(nuevo)* — sin cliente pagador asignado | `annul-with-credit` | "Esta reserva no tiene un cliente asignado, así que no hay a quién devolverle el saldo a favor." | "Asigná un cliente pagador a la reserva y volvé a intentar." (sin botón: se resuelve en la ficha de datos de la reserva, fuera de este panel) |
| *(nuevo)* — plata pagada al operador y la reserva todavía no tiene factura para anclar el reembolso (freno de plata, D1 firmada) | `annul-with-credit` | "Ya le pagaste al operador por uno o más servicios y esta reserva todavía no tiene factura emitida para registrar ese reembolso a tu favor." | **Botón "Emitir factura"** (D1, 2026-07-18) |

Los 4 códigos marcados **"(nuevo)"** hoy no tienen ningún identificador en el body 409 (`annul-with-credit` lanza `InvalidOperationException` sin código, a diferencia de `draft`/`confirm` que ya usan `BusinessInvariantViolationException` con `invariantCode`). El backend agrega un discriminador para esos 4 casos (mismo patrón aditivo que la Decisión C: se suma un campo, nunca se saca el `message`). El nombre técnico del código lo define el backend; el texto de la tabla es lo que el usuario tiene que ver pase lo que pase.

## Spec de pantalla — panel "Anular reserva"

### Estado HOY (el callejón — peor caso #3 + #8 del inventario)

```
┌─ Anular reserva ──────────────────────────────────────── [x] ┐
│ #R-1042 — Fam. García                                        │
│                                                                │
│  ⚠ Con factura: al anular se emite una Nota de Crédito.      │
│                                                                │
│  Motivo de la anulación *                                     │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ El cliente tuvo que reprogramar por un problema médico  │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                                │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ ⚠ No se pudo iniciar la anulación. Probá de nuevo; si    │  │  ← genérico
│  │   el problema sigue, contactá a administración.          │  │     SIEMPRE,
│  └────────────────────────────────────────────────────────┘  │     sin importar
│                                    [ Volver ] [ Anular reserva ]│    la causa real
└────────────────────────────────────────────────────────────────┘
```

El backend sabía exacto que la reserva tiene servicios de dos operadores distintos (INV-152). El vendedor lee "contactá a administración" y no tiene ni idea de qué pasó ni qué hacer distinto la próxima vez.

### Estado NUEVO (mismo panel, mensaje real según el código)

```
┌─ Anular reserva ──────────────────────────────────────── [x] ┐
│ #R-1042 — Fam. García                                        │
│                                                                │
│  ⚠ Con factura: al anular se emite una Nota de Crédito.      │
│                                                                │
│  Motivo de la anulación *                                     │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ El cliente tuvo que reprogramar por un problema médico  │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                                │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ ⚠ Esta reserva tiene servicios de varios operadores.     │  │  ← mensaje
│  │   Por ahora la anulación de reservas con varios          │  │     REAL, según
│  │   operadores no está disponible desde acá.               │  │     el código
│  │   Gestionala manualmente o pedile ayuda a un              │  │     que llegó
│  │   administrador.                                          │  │
│  └────────────────────────────────────────────────────────┘  │
│                                    [ Volver ] [ Anular reserva ]│
└────────────────────────────────────────────────────────────────┘
```

Caso con botón (freno de plata, D1 firmada):

```
│  ┌────────────────────────────────────────────────────────┐  │
│  │ ⚠ Ya le pagaste al operador por uno o más servicios y    │  │
│  │   esta reserva todavía no tiene factura emitida para     │  │
│  │   registrar ese reembolso a tu favor.                     │  │
│  │                                    [ Emitir factura ]     │  │  ← botón,
│  └────────────────────────────────────────────────────────┘  │     mismo cartel
│                                    [ Volver ] [ Anular reserva ]│
```

- **Todo lo cargado queda intacto** (motivo escrito, caso de anulación) — el panel no se cierra ni se limpia.
- **El cartel es el mismo de siempre**: rosa, `role="alert"`, arriba de los botones. Solo cambia el texto (y a veces aparece un botón dentro del mismo cartel, mismo patrón que usa T1 para "Nueva factura").
- El botón **"Emitir factura"**, si aparece, abre el flujo de emisión de factura que ya existe en la ficha (`EmitirFacturaInline`) — no se construye nada nuevo.

## Retiro de código muerto

`src/TravelWeb/src/features/cancellations/components/CancelReservaModal.jsx` se **borra**. Cero referencias activas (verificado). Su único aporte de valor (el texto de INV-152) queda rescatado en la tabla de arriba.

## Estados del panel (checklist frontend)

| Estado | Qué se ve |
|---|---|
| Escribiendo el motivo | Lo de hoy (contador de caracteres, mínimo 10). |
| Enviando | Botón → "Anulando...", deshabilitado (anti doble envío). Lo de hoy. |
| Error mapeado (tabla de arriba) | Cartel rosa con el texto de la tabla (+ botón si corresponde) + panel intacto + reintento en el mismo botón. |
| Error NO mapeado | Cartel rosa con el texto neutro de siempre (fallback, política sin cambios). |
| `CONCURRENT_EDIT` | Sigue igual: "Otro usuario modificó esta cancelación al mismo tiempo. Recargá la página y volvé a intentar." |
| Éxito | Lo de hoy (mensaje de éxito según el caso + cierre del panel). |

**Nota sobre el sub-flujo multi-factura (ADR-042):** `handleConfirmarMulti` y `handleReintentarDesdeRevision` llaman al MISMO endpoint `confirm()`. El mismo mapa código→texto aplica ahí también (hoy ya intercepta `CONCURRENT_EDIT` con el mismo criterio; se le suma INV-093 e INV-100 con la misma función compartida, sin duplicar lógica).

## Qué NO hay que hacer (T3)

- NO crear un modal nuevo ni una pantalla nueva: el cartel que ya existe en el panel muestra el texto nuevo.
- NO reescribir los mensajes que ya andan bien (Decisión C / D2 firmada): solo se interviene la tabla de códigos de arriba.
- NO mostrar texto crudo del backend para códigos fuera de la tabla: sigue el fallback neutro de siempre.
- NO inventar un botón nuevo para el caso "factura viva → camino NC": la solución es cerrar y reabrir (el panel ya recalcula el caso solo).
- NO dejar `CancelReservaModal.jsx` en el árbol de archivos: se borra.

---

# TANDA 4 — "Anular varios servicios" ayuda igual que anular uno + vocabulario

## Qué cubre la guía + el plan firmado (citado)

1. **Auditar patrones existentes antes de crear UI nueva (regla operativa del proyecto).**
   El patrón "causa real + botón 'Ver facturas de la reserva'" YA existe, completo y probado, en `ServiceList.jsx` (`ModalBloqueoCancelacionServicio`). T4 no inventa nada: copia ese patrón a las filas fallidas del lote.

2. **Vocabulario "cancelar" → "anular" (regla del dueño, no UX — reafirmada 2026-07-16 y en las reglas transversales del plan).**
   *"Cancelar" = el cliente ABONA EL TOTAL. "Anular" = dejar sin efecto.* Sacar un servicio de una reserva es **anular**, nunca "cancelar". El front de `ServiceList.jsx` y `CancelarVariosServiciosInline.jsx` YA dicen "Anular" en todo lo que ve el usuario (renombrado 2026-07-16). Lo que falta son **4 mensajes del backend** que todavía se cuelan con "cancelar" en ese sentido — se listan abajo, verificados línea por línea.

3. **El swap del pre-bloqueo — sin decisión de diseño, es un cambio mecánico ya aprobado (M2/M4 del plan).**
   El plan ya verificó que es un SWAP de método (`GetReservaCancellationBlockReasonAsync` → `GetReservaVoucherOnlyBlockReasonAsync`), no lógica nueva. El heads-up ("más servicios facturados van a aparecer como anulables") ya fue comunicado a Gastón como informativa I3 del plan (§6.A) y no requiere cambio visual: la reserva simplemente deja de mostrar el bloqueo cuando no corresponde.

**Conclusión T4:** no hay decisión de diseño nueva. Es copiar un patrón + corregir 4 textos por la propia regla del dueño.

## Parte 1 — Paridad: causa real + "Ver facturas de la reserva" en el lote

### Auditoría del patrón que se copia (`ServiceList.jsx`, flujo individual)

Cuando se anula UN servicio y el backend rechaza con 409 (factura CAE viva o voucher emitido), `ServiceList.jsx` abre `ModalBloqueoCancelacionServicio`:

```
┌─ No se puede anular el servicio ─────────────────── [x] ┐
│                                                            │
│  ┌──────────────────────────────────────────────────┐   │
│  │ [mensaje real del backend con el detalle fiscal]  │   │
│  └──────────────────────────────────────────────────┘   │
│  Para poder anular este servicio primero hay que          │
│  gestionar la nota de crédito correspondiente en la       │
│  sección de facturación de la reserva.                    │
│                                                             │
│                    [ Entendido ] [ 📄 Ver facturas de     │
│                                     la reserva ]           │
└─────────────────────────────────────────────────────────┘
```

`onIrAFacturas` es un callback que **ya existe** como prop de `ServiceList.jsx` (línea 985) y **ya viaja** hasta este modal (línea 1147). Navega a la solapa "Estado de Cuenta" de la reserva, donde están los documentos fiscales.

### Estado HOY del lote (el callejón — peor caso #4 del inventario)

```
┌─ Anular varios servicios ─────────────────────────────── [x] ┐
│  ...                                                          │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ 2 de 3 servicios anulados.                               │  │
│  │ ⚠ Hotel Maitei: Bloqueo fiscal — [mensaje del backend]   │  │  ← se lee la
│  └────────────────────────────────────────────────────────┘  │     causa, pero
│                                                    [ Cerrar ]  │     NO HAY forma
└──────────────────────────────────────────────────────────────┘     de resolverlo
                                                                       desde acá
```

Justo el flujo que más se natural elegir cuando hay varios servicios (tildar y anular en lote) es el que MENOS ayuda: dice la causa pero no ofrece nada para resolverla, mientras que anular uno por uno sí.

### Estado NUEVO del lote (misma sección, ahora con el camino)

```
┌─ Anular varios servicios ─────────────────────────────── [x] ┐
│  ...                                                          │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ 2 de 3 servicios anulados.                               │  │
│  │ ⚠ Hotel Maitei: Bloqueo fiscal — [mensaje del backend]   │  │
│  │            [ 📄 Ver facturas de la reserva ]              │  │  ← mismo botón,
│  └────────────────────────────────────────────────────────┘  │     misma acción
│                                                    [ Cerrar ]  │     que el flujo
└──────────────────────────────────────────────────────────────┘     individual
```

**Reglas del botón:**
- Aparece **solo en las filas fallidas con `esBloqueo409 === true`** (el mismo criterio que hoy separa el texto "Bloqueo fiscal —" del resto de los errores). Filas con otro tipo de error (ej. "Tipo de servicio no reconocido") no lo muestran — ahí no hay factura que ir a ver.
- Mismo texto exacto: **"Ver facturas de la reserva"**, mismo ícono (documento), sin ventana nueva: el clic navega igual que en el flujo individual (misma pestaña "Estado de Cuenta").
- Va **dentro de cada fila fallida**, no una sola vez al pie — si dos servicios fallaron por facturas distintas, cada fila lleva su propio botón (misma acción, apunta al mismo lugar: la solapa de facturas de la reserva).
- `CancelarVariosServiciosInline` recibe una prop nueva `onIrAFacturas` (el MISMO callback que `ServiceList.jsx` ya tiene disponible y hoy no le pasa) — no se crea ningún callback nuevo.

## Parte 2 — Vocabulario: los 4 mensajes del backend que dicen "cancelar" en vez de "anular"

Auditados uno por uno contra el código real (no contra el inventario). Los 4 aparecen en los guards de `CancelServiceAsync` y sus dependencias — el mismo método que sirve tanto al flujo individual como al de lote (`CancelarVariosServiciosInline` llama al mismo endpoint una vez por servicio).

| # | Archivo : línea | Texto HOY | Texto NUEVO |
|---|---|---|---|
| 1 | `ReservaCapabilities.cs:161-162` (`ServiceNotCancellableStatusReason`) | "En este estado los servicios no se cancelan. En un presupuesto, para sacar un servicio borralo." | "En este estado los servicios no se anulan. En un presupuesto, para sacar un servicio borralo." |
| 2 | `MutationGuards.cs:428-429` (`GetReservaVoucherOnlyBlockReasonAsync`) | "No se puede cancelar este servicio: la reserva tiene vouchers emitidos. Anulá los vouchers primero si necesitás corregir datos." | "No se puede anular este servicio: la reserva tiene vouchers emitidos. Anulá los vouchers primero si necesitás corregir datos." |
| 3 | `BookingCancellationService.cs:906-909` (candado R1, un servicio — **acople T4→T7, ver plan §2**) | "No se puede cancelar este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el reembolso con el operador antes de cancelar el servicio." | "No se puede anular este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el reembolso con el operador antes de anular el servicio." |
| 4 | `BookingCancellationService.cs:435-438` (sin Payer, con factura viva) | "No se puede cancelar este servicio: la reserva tiene una factura emitida pero no tiene un cliente asignado para facturarle la nota de crédito. Asigná un cliente a la reserva antes de cancelar." | "No se puede anular este servicio: la reserva tiene una factura emitida pero no tiene un cliente asignado para facturarle la nota de crédito. Asigná un cliente a la reserva antes de anular." |

**Nota sobre la fila #3:** es el texto del candado R1 que Tanda 7 le va a agregar un `code` encima (para el pre-chequeo + botón "Emitir factura" del servicio suelto). El plan ya declaró el orden obligatorio: **T4 corrige el vocabulario primero, T7 trabaja sobre el texto ya corregido** — no se invierte.

No se tocan otros mensajes que ya dicen "modificar" en vez de "cancelar" (ej. `GetReservaMutationBlockReasonInternalAsync`, usado para el candado general de edición, no específico de anular un servicio) — esos ya están bien y no son parte del vocabulario roto.

## Parte 3 — El swap del pre-bloqueo (sin cambio visual nuevo)

`ReservaService.cs:2747-2748` deja de llamar a `GetReservaCancellationBlockReasonAsync` (la regla vieja: factura CAE viva **O** voucher emitido) y pasa a llamar a `GetReservaVoucherOnlyBlockReasonAsync` (la regla real que usa el guard: **solo** voucher emitido). Esto alimenta `reserva.serviceCancellationBlockReason`, que es el `blockReason` que recibe `CancelarVariosServiciosInline` y que hoy pre-bloquea TODA la sección con el cartel:

```
┌────────────────────────────────────────────────────────┐
│ ⊘ No se puede anular: la reserva tiene un bloqueo       │
│   fiscal activo                                          │
│   [motivo]                                                │
└────────────────────────────────────────────────────────┘
```

**No hay ningún cambio de diseño acá** — el cartel es el mismo, con el mismo aspecto, para el mismo caso real (voucher emitido). Lo único que cambia es que **deja de aparecer** en reservas que tienen factura pero NO tienen voucher (el motor ya permitía anular esos servicios; la pantalla frenaba de más). Es exactamente el heads-up I3 que Gastón ya recibió y no objetó en el plan (§6.A): *"vas a ver más servicios 'anulables' que antes... no es un permiso nuevo."*

## Estados de la sección de lote (checklist frontend)

| Estado | Qué se ve |
|---|---|
| Sin bloqueo de reserva | Lo de hoy: lista de checkboxes, motivo, total por moneda. |
| Bloqueo de reserva (solo voucher, tras el swap) | Cartel rojo arriba + checkboxes deshabilitados + botón Confirmar apagado. Lo de hoy, mismo diseño, condición corregida. |
| Proceso terminado, con fallas por bloqueo fiscal | Fila con el mensaje real **+ botón "Ver facturas de la reserva"** (nuevo). |
| Proceso terminado, con fallas de otro tipo | Fila con el mensaje real, **sin** botón (no cambia). |
| Proceso terminado, todo OK | Cartel verde de siempre. |

## Qué NO hay que hacer (T4)

- NO abrir un modal nuevo para el resultado del lote: el botón va inline en la fila fallida, dentro de la misma sección (coherente con que toda esta sección ya es "en línea", no modal).
- NO agregar el botón a filas que fallaron por una causa que NO es bloqueo fiscal (`esBloqueo409 === false`).
- NO reescribir mensajes del backend que ya dicen "anular" correctamente — solo los 4 de la tabla de la Parte 2.
- NO tocar `GetReservaMutationBlockReasonInternalAsync` ni sus mensajes de "modificar": no son parte de este vocabulario roto.
- NO agregar ningún aviso o cartel nuevo por el swap del pre-bloqueo: es invisible por diseño (la pantalla deja de frenar de más, nada aparece donde antes no había nada).

---

# PREGUNTAS PARA GASTON

**Ninguna.**

Las dos tandas están completamente cubiertas por el plan que Gastón ya firmó (D1-D4, 2026-07-18), por la Decisión C del propio plan, y por su regla de vocabulario "cancelar ≠ anular" (que no es una decisión de UX, es una regla de negocio que ya rige toda la app):

- **T3** arma el mapa código→criollo con la Decisión C (envelope aditivo) + D2 firmada ("mejorar solo los rotos") + el precedente del componente muerto `CancelReservaModal.jsx`. El único caso donde auditando encontré un freno de plata nuevo (a nivel de toda la reserva) usa el botón **"Emitir factura"** que Gastón ya aprobó como D1 para el caso gemelo de un servicio suelto — mismo botón, mismo freno, misma decisión.
- **T4** copia el patrón "causa real + botón 'Ver facturas de la reserva'" que YA existe y funciona en `ServiceList.jsx`, y corrige 4 mensajes del backend por la propia regla de vocabulario del dueño (no una preferencia de diseño).

Si al implementar aparece algún código adicional del backend que no está en la tabla de T3, o un mensaje con "cancelar" que esta auditoría no detectó, frontend-senior/backend-dotnet-senior lo trae acá antes de decidir solo (no se inventa texto nuevo sin pasar por este proceso).
