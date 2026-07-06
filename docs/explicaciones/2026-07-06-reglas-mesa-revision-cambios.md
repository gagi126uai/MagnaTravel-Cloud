# Reglas de negocio — Mesa de revisión de cambios

**Fecha:** 2026-07-06
**Autor:** travel-agency-domain-expert
**Para:** software-architect (esto alimenta el diseño; NO es diseño técnico todavía)
**Nivel:** trainee (con ejemplos cotidianos)

> Este documento define QUÉ tiene que pasar en el negocio cuando una reserva ya
> confirmada "cambia por abajo" y queda marcada para revisar. No dice CÓMO se
> programa. La matriz de reglas por tipo de cambio está en la sección 6.

---

## 1. Qué es la "mesa de revisión de cambios" (en criollo)

Imaginá que vendés un viaje, el cliente lo aceptó y la reserva quedó
**Confirmada** (todos los servicios resueltos: el hotel confirmado, el aéreo
emitido, etc.). Días después, algo cambia sin que vos lo pidas:

- el operador te cancela el hotel,
- el operador te corre la fecha del vuelo,
- el operador te confirma pero a **otro precio**,
- alguien agrega un servicio nuevo a esa reserva ya cerrada,
- se cancelan todos los servicios y la reserva queda vacía.

Antes, el sistema hacía volver la reserva sola a "En gestión". Eso se **eliminó**
en 2026-06-24 (alineado a Odoo/SAP): una reserva confirmada **nunca vuelve sola
para atrás**. En su lugar, queda Confirmada pero con una **marca "necesita
revisión"** (cartelito + campanita + aviso urgente al vendedor).

La **mesa de revisión** es la pantalla donde esa marca se "trabaja": una lista
con **un renglón por cada cosa que cambió**, cada renglón dice **antes → después**
en castellano de negocio, y ofrece las **acciones para corregir ahí mismo**.
Cuando se resuelve el **último** renglón, la marca, el cartel, la campanita y el
aviso urgente se **apagan solos**. Esto reemplaza al viejo botón "Dar OK" (que
apagaba todo de una sin mirar renglón por renglón).

**Ejemplo cotidiano:** es como cuando pedís un delivery, ya lo confirmaste, y el
local te avisa "no tengo la Coca, ¿te mando Sprite o te devuelvo la plata?".
No cancela tu pedido entero: te deja **decidir ese ítem**. La mesa de revisión es
esa charla, pero ordenada y con rastro de quién decidió qué.

---

## 2. Lo que ya está decidido (NO reabrir)

Del pedido del dueño (2026-06-24 y 2026-07-05):

1. **Confirmada no vuelve sola atrás.** Queda Confirmada + marca "necesita
   revisión".
2. **Un renglón por evento**, antes→después en castellano, acciones correctivas
   en el propio renglón. Resolver el último apaga marca + cartel + campanita +
   aviso.
3. **"Aceptar todo tal cual"** existe **solo si ningún cambio toca plata ni
   fechas**. Si toca precio o deja la reserva sin servicios, se decide **renglón
   por renglón**.
4. **Aceptar un cambio de precio** muestra total viejo→nuevo, recalcula y deja
   traza. **Aceptar ES la autorización** (un solo paso, no un doble OK).
5. **Con revisión abierta:** FACTURAR queda **bloqueado**; cobrar y pasar a "En
   viaje" **avisan fuerte** pero dejan avanzar dejando **constancia de quién
   avanzó igual**.
6. **Vocabulario del dueño:** "cancelar" un servicio ≠ "anular" la reserva.
   Anular = dejar sin efecto el viaje (Nota de Crédito total + Nota de Débito por
   multa si la hay). Candado fiscal: lo facturado **no se borra**, se corrige por
   documento inverso (NC/ND).

---

## 3. Qué encontré en el código HOY (verificado, no asumido)

Esto es importante para el arquitecto porque **el diseño de la mesa arranca de
una base que hoy está a medias**.

### 3.1. Cómo se prende hoy la marca "necesita revisión"

La marca es el flag `Reserva.HasUnacknowledgedChanges` (+ `ChangesPendingSince`).
Se prende por **dos caminos distintos**, y solo uno deja detalle estructurado:

**Camino A — cambio de precio/costo (ADR-027).**
`ReservaService.MarkUnacknowledgedChangesIfLiveAsync` prende el flag **y** graba
una fila de detalle (`ReservaPendingChange`) por cada campo que cambió. Hoy los
campos que trackea son **solo dos**: `SalePrice` (precio de venta al cliente) y
`NetCost` (costo del operador). Cada fila guarda: tipo de servicio, descripción,
antes, después, moneda, quién y cuándo. **Este es el único camino que ya produce
"renglones" reales.**

**Camino B — el operador canceló / reprogramó / se agregó un servicio / quedó
sin servicios (`ReservaAutoStateService`).**
Cuando una reserva Confirmada deja de tener "todos los servicios vivos
resueltos", el motor de estados la deja Confirmada y le prende la marca, pero
**NO graba filas de detalle**: solo escribe un **texto libre** en
`LastRegressionReason` (ej.: *"Hay servicios que dejaron de estar resueltos:
Hotel, Aéreo. Puede ser un servicio nuevo o que el operador canceló/reprogramó
uno confirmado. Revisala."*) y manda un aviso urgente.

**Consecuencia para el diseño (GAP #1):** la mesa "un renglón por evento" **hoy
solo tendría renglones para los cambios de precio/costo**. Para los otros cuatro
eventos (canceló, reprogramó, agregó, quedó vacía) **no existe el renglón
estructurado** — hay que crearlo (nuevos tipos de detalle además de `SalePrice`
y `NetCost`).

### 3.2. Qué hace hoy "Dar OK"

`AcknowledgeChangesAsync` (endpoint `acknowledge-changes`): apaga el flag, limpia
el motivo, guarda quién/cuándo dio el OK, y **borra TODAS las filas de detalle de
la reserva**. Es un **"aceptar todo" ciego**: no hay acción por renglón, no
recalcula nada, no toca la factura, no distingue si el cambio tocaba plata. Esto
es exactamente lo que la mesa viene a reemplazar.

### 3.3. Gates que hoy dependen de la marca (y el que falta)

- **Pase a "En viaje": SÍ está gateado.** Mientras la marca esté puesta, ni el
  job nocturno (`ReservaLifecycleAutomationService`) ni el pase manual promueven
  la reserva a Traveling. (Verificado.)
- **FACTURAR: hoy NO está gateado por la marca.** `ReservaCapabilityPolicy.
  EvaluateInvoiceSale` solo mira el estado (Confirmed/Traveling/Closed); no mira
  `HasUnacknowledgedChanges`. Es más: el contexto `ReservaCapabilityContext` **ni
  siquiera transporta ese flag**. **(GAP #2)** La decisión del dueño "con revisión
  abierta, FACTURAR bloqueado" **todavía no está implementada**.
- **Cobrar:** hoy no está gateado por la marca (la decisión es "avisar fuerte y
  dejar avanzar con constancia", así que el gate correcto acá es un **aviso**, no
  un bloqueo).

### 3.4. Tipos de servicio y campos de plata que existen

Seis colecciones de servicio en la reserva: **Aéreo** (FlightSegments), **Hotel**
(HotelBookings), **Traslado** (TransferBookings), **Paquete** (PackageBookings),
**Asistencia** (AssistanceBookings) y **Otro/genérico** (Servicios). Cada uno
tiene su estado (solicitado / confirmado / cancelado), su `ConfirmedAt`, y sus dos
montos: **precio de venta** y **costo**. "Resuelto" se define por tipo (aéreo =
emitido, hotel/paquete = confirmado por operador, traslado = confirmado o marcado
"no requiere confirmación", asistencia = voucher emitido).

### 3.5. Comisión del vendedor: OJO

Hay configuración de **margen de agencia** (`CommissionRule`,
`SellerCommissionPercent` global). Pero según la auditoría de negocio
(2026-06-12) **no existe todavía una comisión del vendedor devengada por reserva**
que se pueda "descontar" al cancelar. Por eso, en la matriz de abajo, los efectos
sobre "comisión del vendedor" son una **nota de diseño futura**, no un
comportamiento actual. No inventar que hoy se ajusta una comisión que no se
liquida.

---

## 4. Definiciones que usa la matriz

- **Menor** (elegible para "aceptar todo"): el cambio **no toca plata ni fechas**
  y **no deja la reserva sin servicios**. Regla dura del dueño.
- **Mayor** (decisión renglón por renglón): **toca precio o fechas**, o **deja la
  reserva sin servicios activos**. Regla dura: *si toca plata o fechas ⇒ mayor.*
- **Facturada** = tiene una factura con CAE vivo (comprobante fiscal sellado en
  AFIP). Candado: no se borra, se corrige por NC/ND.
- **Cobrada total / parcial** = tiene cobros registrados (reales o puente) que
  cubren todo el saldo, o solo una parte.
- **Renglón resuelto** = se ejecutó una de sus acciones (aceptar, corregir,
  sustituir, cancelar la línea, agregar, anular). Un renglón **solo se cierra con
  una acción explícita**; que el servicio se re-resuelva solo (el operador
  re-confirma) **no** cierra el renglón (alguien tiene que haber mirado).

---

## 5. Acciones posibles en un renglón (catálogo)

| Acción | Qué hace en negocio |
|---|---|
| **Aceptar** | Doy por bueno el cambio tal como vino. Si toca precio, muestra total viejo→nuevo, recalcula el saldo y deja traza. Aceptar ES la autorización. |
| **Corregir precio/datos** | Ajusto el monto o el dato a mano (el operador se equivocó, o negocio otro número) antes de aceptar. |
| **Sustituir servicio** | El servicio cancelado/caído lo reemplazo por otro equivalente (otro hotel, otro vuelo). |
| **Cancelar la línea** | Doy por perdido ese servicio (queda cancelado, la reserva sigue con el resto). Ojo: puede disparar cancelación con el operador (multa). |
| **Agregar servicio** | Sumo el servicio que faltaba (caso "quedó incompleta"). |
| **Anular reserva** | Deshago todo el viaje: NC total + ND por multa si la hay. Camino formal (ADR-002). |

**Qué acciones aplican depende del caso** (ver matriz). Regla transversal:

- **Si la reserva NO está facturada:** aceptar un cambio de precio solo recalcula
  el saldo del cliente. Simple.
- **Si la reserva YA está facturada:** aceptar un cambio que mueve el total deja
  la **factura desactualizada**. El candado fiscal manda: no se edita la factura,
  se **corrige por NC/ND**. Ver la regla fiscal en la sección 6.3 y la **duda
  abierta D1** (¿aceptar encadena la NC/ND en el mismo paso, o bloquea el renglón
  hasta emitirla?).

---

## 6. MATRIZ DE REGLAS POR TIPO DE CAMBIO

Para cada evento: (1) renglón modelo, (2) acciones, (3) efectos, (4) menor/mayor,
(5) bordes.

### 6.1. E1 — El operador CANCELÓ un servicio ya confirmado

**Ejemplo:** tenías el Hotel NH Centro confirmado y el operador lo cancela.

**(1) Renglón modelo:**
> *"El operador canceló un servicio que estaba confirmado: **Hotel NH Centro**
> (3 noches). Antes: confirmado. Ahora: cancelado por el operador."*

**(2) Acciones:** Sustituir servicio · Cancelar la línea (dar por perdido) ·
Anular reserva. *(Aceptar "tal cual" NO aplica: dejar un agujero en el viaje no
es "menor".)*

**(3) Efectos por acción:**

| Acción | Total reserva | Estado servicio | Deuda cliente | Factura existente | Comisión vendedor (futuro) |
|---|---|---|---|---|---|
| Sustituir | Cambia si el nuevo cuesta distinto | nuevo servicio "solicitado"→ a confirmar | recalcula | si facturada y cambia total → **NC/ND** (D1) | recalcula sobre nuevo margen |
| Cancelar la línea | Baja por el servicio perdido | queda **cancelado** | baja lo del servicio | si facturada → **NC** por el servicio perdido | baja la parte de ese servicio |
| Anular reserva | Va a 0 | todos cancelados | se resuelve por flujo de anulación (T2, saldo a favor) | **NC total** + **ND** por multa si la hay | tope cero (nunca negativa) |

**(4) Mayor** (toca plata). Renglón por renglón.

**(5) Bordes:**
- Si al cancelar ese servicio la reserva **queda sin ningún servicio vivo**, el
  renglón se combina con el evento **E5** (ver 6.5): la única salida sensata es
  Anular o Agregar/Sustituir.
- Si el operador **re-confirma** el mismo servicio antes de que resuelvas el
  renglón: el renglón sigue abierto (alguien tiene que confirmarlo), pero su texto
  debe **refrescarse** a "el operador lo volvió a confirmar" para no hacer cancelar
  algo que ya está de vuelta.

---

### 6.2. E2 — El operador REPROGRAMÓ la fecha de un servicio

**Ejemplo:** el vuelo de salida pasa del 10 al 12 de marzo.

**⚠ Hallazgo (GAP #3):** hoy una reprogramación de fecha que **mantiene el
servicio confirmado NO prende la marca** (el motor solo mira "resuelto/no
resuelto", y un vuelo reprogramado sigue emitido = sigue resuelto). Es decir, el
cambio de fecha **se pierde**: nadie lo revisa. El dueño quiere que **fechas ⇒
mayor** y que se revise. Entonces la mesa necesita un **disparador nuevo por
cambio de fecha**, además del que ya existe por "dejó de estar resuelto".

**(1) Renglón modelo:**
> *"El operador cambió la fecha de un servicio: **Vuelo AEP→MDZ**. Antes: sale
> 10/03. Ahora: sale 12/03."*

**(2) Acciones:** Aceptar (la nueva fecha) · Corregir datos (si la fecha vino mal)
· Sustituir servicio · Cancelar la línea · Anular reserva.

**(3) Efectos:**
- **Aceptar:** no mueve plata (salvo que la reprogramación traiga costo/precio
  nuevo → entonces es también E3). Actualiza la fecha del servicio y, si esa fecha
  era la que define el inicio del viaje, **puede correr la fecha de todo el
  itinerario** (esto es "reprogramar" en el sentido de `CanReschedule`). Deja
  traza. **La factura NO se toca por una fecha** (la fecha no cambia el importe;
  ojo si el período fiscal ya cerró → warning de período cerrado).
- **Sustituir / Cancelar / Anular:** igual que E1.

**(4) Mayor** (toca fechas — regla dura del dueño). Aunque no mueva plata.

**(5) Bordes:**
- **Fecha nueva ya pasó o cae fuera de la ventana:** si la nueva fecha es
  inviable (ej. el vuelo lo corren a después de la vuelta), el renglón debe
  **alertar incompatibilidad** y no dejar "aceptar" a ciegas.
- Reprogramación + cambio de precio en el mismo movimiento del operador ⇒ **dos
  renglones** (uno de fecha, uno de precio) sobre el mismo servicio, o **un
  renglón con las dos columnas**. Ver duda **D3**.
- **En viaje:** reprogramar aplica en Traveling también (mover un itinerario en
  curso). Ver 6.6.

---

### 6.3. E3 — CAMBIÓ EL PRECIO de un servicio

**Ejemplo:** el operador confirma el paquete pero a $180.000 en vez de $150.000.

Este es el **único evento que hoy ya genera renglón estructurado** (ADR-027).

**(1) Renglón modelo:**
> *"Cambió el precio de un servicio: **Paquete Bariloche 5 noches**. Antes:
> $150.000. Ahora: $180.000. (+$30.000)"*
>
> *(Si además cambió el costo del operador, ese dato es sensible: se enmascara a
> quien no tiene permiso de ver costos — igual que hoy.)*

**(2) Acciones:** Aceptar (el precio nuevo) · Corregir precio (poner otro número
negociado) · Cancelar la línea · Anular reserva.

**(3) Efectos:**

| Sub-caso | Al Aceptar |
|---|---|
| **No facturada, no cobrada** | Muestra total viejo→nuevo, **recalcula el saldo**, deja traza. Un paso. Fin. |
| **No facturada, cobrada parcial** | Recalcula el saldo. Si el precio **subió**, aumenta lo que falta cobrar (aviso al vendedor). Si **bajó** por debajo de lo cobrado → genera **saldo a favor** del cliente (queda a cuenta). |
| **Ya facturada** | La factura queda **desactualizada**. Candado fiscal: NO se edita. Si el precio **subió** → hay que emitir **Nota de Débito** por la diferencia. Si **bajó** → **Nota de Crédito** por la diferencia. **Duda D1:** ¿el "Aceptar" **encadena** la emisión de la nota en el mismo paso, o **deja el renglón abierto** con la acción "emitir la nota" pendiente? |

Comisión del vendedor (futuro): si el precio de venta cambia, el margen cambia,
así que la comisión devengada tendría que recalcularse (cuando exista).

**(4) Mayor** (toca plata). Renglón por renglón, siempre.

**(5) Bordes:**
- **Dos cambios de precio sobre el mismo servicio antes de resolver:** hoy el
  sistema **acumula una fila por edición** (no pisa la anterior). Para la mesa,
  eso significa **dos renglones** del mismo servicio, o **colapsar a uno** que
  muestre "precio original → precio final" (más limpio para decidir). Ver duda
  **D2**.
- **Aceptar un precio que ya volvió a cambiar:** si mientras el renglón estaba
  abierto el operador re-tocó el precio, "Aceptar" debe aceptar **el último**
  valor, no uno viejo. El renglón tiene que reflejar el valor vigente al momento
  de aceptar (releer, no confiar en lo que se pintó).

---

### 6.4. E4 — Se AGREGÓ UN SERVICIO NUEVO a una reserva ya confirmada

**Ejemplo:** la reserva estaba confirmada y el vendedor suma un traslado.

**(1) Renglón modelo:**
> *"Se agregó un servicio nuevo a una reserva ya confirmada: **Traslado
> aeropuerto–hotel**. Antes: no estaba. Ahora: agregado, falta confirmarlo con el
> operador."*

**(2) Acciones:** Aceptar (dejarlo como parte de la reserva; se confirma por el
flujo normal) · Corregir datos · Cancelar la línea (sacar lo que se agregó por
error).

**(3) Efectos:**
- **Aceptar:** el servicio nuevo entra como "solicitado" y sigue el flujo normal
  (cuando el operador lo confirme, la reserva vuelve a estar "todo resuelto").
  **Sube el total** por el precio del servicio nuevo → **sube la deuda** del
  cliente. Si la reserva **ya estaba facturada**, el servicio nuevo **no está en
  esa factura** → hay que **facturar la diferencia** (nueva factura o ND según
  criterio fiscal — ver D1).
- **Cancelar la línea:** saca el agregado; la reserva vuelve a como estaba.

**(4) Depende:**
- Agregar un servicio **con precio** ⇒ **mayor** (toca plata).
- (Teórico) agregar algo sin importe ⇒ menor. En la práctica casi todo servicio
  tiene precio, así que asumir **mayor** salvo importe 0.

**(5) Bordes:**
- Si el servicio agregado **todavía no está confirmado** por el operador, el
  renglón "necesita revisión" convive con el estado normal "falta confirmar".
  Cuidado de no mostrar dos alarmas por lo mismo.

---

### 6.5. E5 — La reserva quedó SIN SERVICIOS ACTIVOS

**Ejemplo:** se cancelaron/eliminaron todos los servicios; la reserva quedó
vacía pero sigue Confirmada.

**(1) Renglón modelo:**
> *"La reserva quedó sin servicios activos (se cancelaron o eliminaron todos).
> Agregá al menos un servicio o anulá la reserva."*

**(2) Acciones:** Agregar servicio (rearmar el viaje) · Anular reserva.
*(Aceptar NO aplica: una reserva Confirmada vacía no es un estado válido para
"dejar así".)*

**(3) Efectos:**
- **Agregar servicio:** vuelve a haber contenido; cuando se resuelva, la marca se
  apaga por el flujo normal + el cierre del renglón.
- **Anular reserva:** camino formal. Si había factura/cobros → **NC total**
  (+ ND por multa si la hay), saldo a favor / reembolso por el flujo de anulación
  (crédito diferido T2, propagación operador→agencia).

**(4) Mayor** (deja la reserva sin servicios — regla dura del dueño).

**(5) Bordes:**
- **Reserva vacía PERO ya facturada y cobrada:** es el caso más delicado. No se
  puede "dejar así" (facturaste un viaje que ya no existe). La mesa debe **empujar
  a Anular** (para emitir la NC) o a **Agregar/Sustituir** el servicio equivalente.
  No permitir cerrar la revisión dejando la reserva facturada-y-vacía.

---

### 6.6. Cruce transversal: reserva EN VIAJE cuando llega el cambio

**Regla:** una reserva **En viaje** es de solo lectura para servicios/pasajeros y
no se cobra ni se cancela (se corrige por NC/ajuste). Pero un operador **puede**
cambiar algo estando en viaje (te corren el vuelo de vuelta).

- El cambio **igual prende la marca** y abre su renglón.
- Las acciones disponibles se **recortan**: Aceptar (registrar lo que pasó) y, a
  lo sumo, Reprogramar (mover fechas del itinerario en curso, que SÍ aplica en
  Traveling). **No** se ofrece Cancelar servicio ni facturar de nuevo desde acá;
  las correcciones de plata van por **NC/ajuste**.
- **Duda D4:** ¿en viaje el cambio se "acepta como constancia" (solo deja rastro)
  o puede seguir moviendo plata vía NC/ND? Recomendación: solo constancia + NC/ND
  aparte, para no reabrir la edición en viaje.

---

## 7. Reglas de la propia mesa (transversales)

- **R1 — Una sola mesa por reserva.** Si ya hay una revisión abierta y llega otro
  cambio, se **agrega un renglón a la misma mesa** (no se abre una segunda). Hoy
  la fecha "desde cuándo hay algo pendiente" (`ChangesPendingSince`) **no se
  re-pisa** con cada cambio: se mantiene la primera. Bien.
- **R2 — El último renglón apaga todo.** Resolver el último renglón baja
  `HasUnacknowledgedChanges`, limpia el motivo, y por el sistema de avisos con
  `ResolutionKey` se apagan cartel + campanita + aviso urgente **solos**.
- **R3 — "Aceptar todo" solo si TODOS los renglones son menores.** Si hay **al
  menos un** renglón que toca plata/fechas o deja sin servicios, el botón "aceptar
  todo tal cual" **no aparece**: se decide renglón por renglón.
- **R4 — Aceptar = autorizar, un paso.** No pedir un segundo OK sobre lo mismo. La
  aceptación queda registrada como autorización (quién, cuándo, viejo→nuevo).
- **R5 — Gate de facturar.** Con la mesa abierta, **FACTURAR bloqueado** (hay que
  implementarlo — GAP #2). Motivo legible sin jerga, ej.: *"Esta reserva tiene
  cambios sin revisar. Revisalos antes de facturar."*
- **R6 — Gate de cobrar y de pasar a En viaje = AVISO, no bloqueo.** Dejan avanzar
  con **constancia de quién avanzó igual** (auditoría). El pase automático nocturno
  a En viaje SÍ se frena (ya está), pero el **manual** avisa y deja pasar con
  registro.
- **R7 — Todo con rastro.** Cada renglón y cada acción: quién, cuándo, viejo→nuevo,
  contra qué servicio/reserva. (Auditoría — ya es requisito del proyecto.)
- **R8 — Cerrar un renglón exige acción explícita.** Que el operador re-resuelva
  el servicio solo **no** cierra el renglón. Pero el **texto del renglón debe
  refrescarse** para reflejar la realidad vigente (no hacer "cancelar" algo que ya
  volvió).

---

## 8. Resumen de la matriz (una mirada)

| Evento | Renglón (antes→después) | Acciones | Menor/Mayor | Efecto fiscal si YA facturada |
|---|---|---|---|---|
| **E1 Operador canceló servicio** | "confirmado → cancelado por el operador" | Sustituir · Cancelar línea · Anular | **Mayor** | NC por el servicio perdido (o NC total si anula) |
| **E2 Operador reprogramó fecha** | "sale 10/03 → sale 12/03" | Aceptar · Corregir · Sustituir · Cancelar · Anular | **Mayor** | Factura no cambia por fecha (warning si período cerrado) |
| **E3 Cambió precio** | "$150.000 → $180.000" | Aceptar · Corregir precio · Cancelar · Anular | **Mayor** | Sube→ND, baja→NC por la diferencia (D1) |
| **E4 Servicio nuevo agregado** | "no estaba → agregado, falta confirmar" | Aceptar · Corregir · Cancelar línea | **Mayor** (si tiene precio) | Facturar la diferencia / ND (D1) |
| **E5 Reserva sin servicios** | "quedó vacía" | Agregar · Anular | **Mayor** | Empujar a Anular → NC total |

**Regla dura que atraviesa todo:** *toca plata o fechas, o deja sin servicios ⇒
Mayor ⇒ decisión renglón por renglón. Nunca entra en "aceptar todo".*

---

## 9. Gaps que el arquitecto tiene que resolver (no inventé nada; están en el código)

- **GAP #1 — Renglones para los 4 eventos no-precio.** Hoy solo existe detalle
  estructurado (`ReservaPendingChange`) para precio/costo. Para E1/E2/E4/E5 hoy
  solo hay un texto libre. Hay que crear tipos de renglón nuevos (servicio
  cancelado, fecha reprogramada, servicio agregado, reserva vacía) con su
  antes→después.
- **GAP #2 — Gate de facturar.** `EvaluateInvoiceSale` no mira la marca; el
  contexto de capacidades ni transporta el flag. Hay que sumarlo para cumplir R5.
- **GAP #3 — Disparador por cambio de fecha.** Hoy reprogramar manteniendo
  confirmado NO prende la marca. Si "fechas ⇒ mayor", falta el disparador.
- **GAP #4 — "Dar OK" es aceptar-todo-ciego.** Hay que reemplazarlo por
  acción-por-renglón; el aceptar-todo queda solo para el caso all-menor (R3).

---

## 10. Dudas de negocio que SOLO el dueño puede cerrar

Formuladas en criollo, con opciones y mi recomendación.

**D1 — Aceptar un cambio de plata cuando la reserva YA está facturada: ¿un paso o
dos?**
Cuando aceptás un precio nuevo (o agregás un servicio) y ya habías facturado, la
factura queda vieja. El candado fiscal dice que no se edita: se corrige con Nota
de Crédito/Débito.
- **Opción A:** "Aceptar" **encadena** la nota automáticamente en el mismo click
  (aceptás y el sistema emite la NC/ND por la diferencia). Un paso, más cómodo.
- **Opción B:** "Aceptar" recalcula y deja el renglón **abierto** con una acción
  pendiente "Emitir la nota", que hacés a mano. Dos pasos, más control fiscal.
- **Mi recomendación: B.** Emitir un comprobante fiscal contra AFIP no debería
  dispararse "de costado" al aceptar un cambio; que sea un acto consciente. Pero
  choca un poco con tu regla "aceptar es un paso" — por eso te lo pregunto.

**D2 — Dos cambios de precio sobre el mismo servicio antes de resolver: ¿un
renglón o dos?**
- **Opción A:** dos renglones (historial completo de cada toque). Más rastro.
- **Opción B:** un solo renglón que muestre "precio original → precio final" (el
  del medio no importa para decidir). Más limpio.
- **Mi recomendación: B para decidir, A para el rastro** — mostrar un renglón
  colapsado "original→final", pero guardar los dos movimientos en la auditoría.

**D3 — Reprogramación + cambio de precio en el mismo movimiento del operador: ¿un
renglón con dos columnas o dos renglones?**
- **Mi recomendación:** un renglón por el mismo servicio que muestre **las dos
  cosas** ("fecha 10→12 y precio $150.000→$180.000"), porque la decisión es sobre
  ese servicio como un todo. Pero quiero tu OK.

**D4 — Cambio del operador con la reserva EN VIAJE: ¿qué se puede hacer?**
- **Opción A:** solo "aceptar como constancia" (deja rastro, no mueve plata; si
  hay diferencia de plata va por NC/ajuste aparte).
- **Opción B:** permitir mover plata desde el propio renglón en viaje.
- **Mi recomendación: A.** En viaje es solo lectura; no reabrir edición ahí. La
  plata se corrige por NC/ajuste por el camino formal.

**D5 — Reserva facturada-y-cobrada que quedó VACÍA (E5): ¿se puede cerrar la
revisión sin anular?**
- **Mi recomendación: NO.** No dejar cerrar la mesa dejando una reserva facturada
  sin ningún servicio: obligar a Anular (emite NC) o a Agregar/Sustituir. ¿De
  acuerdo?

**D6 — Comisión del vendedor.** Hoy no se liquida una comisión devengada por
reserva (solo hay margen de agencia configurable). ¿Confirmás que los efectos
sobre "comisión del vendedor" quedan como **diseño futuro** y NO se implementan en
esta mesa? (Si más adelante existe, aplica el tope cero: la comisión baja hasta 0
pero nunca queda negativa.)

---

*Fin. Este documento alimenta al software-architect (diseño de la máquina de
estados de la mesa, los tipos de renglón, el gate de facturar y la reversión de
acciones) y debe cruzarse con `travel-agency-accountant-argentina` para D1 (NC/ND)
y con `arca-tax-expert-argentina` para el warning de período cerrado.*
