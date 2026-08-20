# La ficha del operador no borra la historia — spec ejecutable

**Fecha:** 2026-08-20 · **Origen:** decisiones firmadas al final de `docs/ux/guia-ux-gaston.md`,
sección **"La ficha del operador no borra la historia (2026-08-19/20)"** (líneas 2749-2773). Esta
spec traduce esas 3 decisiones a layout, textos exactos y componentes concretos. **CERO preguntas
a Gastón**: todo sale de esas 3 decisiones + moldes que YA existen y están en producción (citados
con ruta de archivo en cada bloque). No implementa código — es la spec que sigue `frontend-senior`.

**Vocabulario obligatorio (P-1):** en todo texto nuevo de esta spec, "anular"/"Anulada" — nunca
"cancelar"/"Cancelada". El campo interno puede seguir llamándose `Cancelled`/`workflowStatus`
puertas adentro; lo que lee el usuario dice siempre "Anulada".

---

## 1. Extracto del operador con rastro

**Componente:** `src/TravelWeb/src/features/suppliers/components/SupplierExtractoSection.jsx`
(función `FilaExtractoProveedor`, línea ~558; tabla dentro de `BloqueExtractoProveedor`, línea
~377).

**Decisión que ejecuta:** guía 2026-08-19 #1 — "las compras de una reserva anulada QUEDAN en la
plata del operador [...] la línea de compra original + una contra-línea 'Anulación' con la fecha
de la anulación, ambas visibles, neteando cero; la compra va tachada con chip 'Anulada' (mismo
molde que la cuenta del cliente con facturas anuladas)." (F-2, F-6, P-3, F-14.)

### 1.1 El molde a reusar EXACTO (cliente → operador)

El molde de "factura anulada" del lado del cliente vive en
`src/TravelWeb/src/features/customers/components/FacturacionClienteTab.jsx` (`ChipEstadoFiscal`,
línea 62) + `src/TravelWeb/src/features/customers/lib/facturacionFilters.js`
(`resolverChipEstadoComprobante`, línea 155):

```js
// resolverChipEstadoComprobante — estado "Succeeded" (anulada de verdad):
{ tone: "rojo", etiqueta: "Anulada", tachado: true }
```

Pintado con `<StatusChip tone="rojo" className="line-through">Anulada</StatusChip>`
(`src/TravelWeb/src/components/ui/badge.jsx`, tone `rojo` = "freno / sin efecto", regla P-20 de
la constitución "un color, un significado").

**Se reusa TAL CUAL para la compra anulada del operador**: mismo `tone="rojo"`, mismo
`line-through` en el chip, misma palabra "Anulada" (nunca "Cancelada", P-1).

Además, la descripción de la compra en sí (columna Concepto) se tacha completa — ese es el OTRO
molde ya en producción, el de `ServiceList.jsx` (línea ~1523: `line-through text-slate-400
dark:text-slate-500` sobre el nombre del servicio cancelado). Se combinan los dos: **texto
tachado gris + chip rojo tachado al lado**, igual que ya conviven en la fila de un servicio
anulado dentro de la reserva.

### 1.2 La fila de compra (SIN cambios de columna, solo estado visual)

Columnas de la tabla: **Fecha · Concepto · Comprobante · Cargo · Abono** (sin tocar). Cuando la
compra pertenece a una reserva anulada:

```
Fecha       Concepto                                          Comprobante   Cargo       Abono
──────────────────────────────────────────────────────────────────────────────────────────────
10/06/2026  Compra: Hotel Bariloche 3 noches [ANULADA]        HTL-4471      $ 90.000      —
            (tachado gris, chip rojo tachado)
```

- El texto de la descripción (`linea.description`, tal cual lo manda hoy el backend) se envuelve
  en `line-through text-slate-400 dark:text-slate-500` cuando `linea.reservaIsVoided === true`.
- El chip `<StatusChip tone="rojo" className="ml-2 line-through">Anulada</StatusChip>` se agrega
  al lado, mismo lugar donde hoy va el chip ámbar "Anulación" (línea ~611-620 del componente) —
  son dos chips **distintos con distinto significado**, ver §1.4.
- El monto de Cargo (`$ 90.000`) sigue viéndose (tachado también, mismo tratamiento que
  `ServiceList.jsx` tacha el importe de un servicio anulado) — **no se esconde el número**, F-6
  dice "se tacha", no "se borra".
- La fecha de la fila NO cambia: sigue siendo la fecha ORIGINAL de la compra (cuándo se compró),
  no la de la anulación.

### 1.3 La contra-línea nueva: "Anulación de compra"

```
Fecha       Concepto                                          Comprobante   Cargo       Abono
──────────────────────────────────────────────────────────────────────────────────────────────
19/08/2026  Anulación de compra · Reserva F-2026-1050          —              —       $ 90.000
            (Hotel Bariloche) [ANULACIÓN]
```

- **Texto exacto del Concepto:** `Anulación de compra` — sufijo con reserva y servicio, MISMO
  patrón que ya arma `construirSufijoDestinoPago()` (línea 550 del propio componente) para los
  pagos: ` · Reserva {numero} ({servicio})`. Ejemplo completo: `Anulación de compra · Reserva
  F-2026-1050 (Hotel Bariloche)`.
- **Fecha de la fila:** la fecha en que se confirmó la anulación de la reserva (no la fecha de la
  compra original) — texto de la decisión: "una contra-línea 'Anulación' con la fecha de la
  anulación".
- **Columna Abono:** el mismo importe que tenía la compra en Cargo (`$ 90.000`), para que ambas
  líneas neteen exactamente cero — ninguna resta manual, es un contra-asiento (F-6: "una reversa
  es un contra-asiento, no un borrado").
- **Chip:** lleva el chip ámbar **"Anulación"** que YA existe (`esLineaDeCircuitoCancelacion`,
  línea 525-532 del componente) — es la MISMA familia visual que ya usan `PenaltyRetained` /
  `RefundReceived` / `OperatorChargeInvoiced` / `TreasuryFxAdjustment`: "esto es un movimiento del
  circuito de anulación, no una compra nueva". Se agrega un kind nuevo a esa función (ver
  backend, §1.5) para que la contra-línea entre en el mismo `if`.
- Esta fila **nunca lleva** el chip rojo "Anulada" (ese chip es SOLO de la compra original: marca
  QUÉ compra quedó sin efecto, no el movimiento contable que la revierte).

### 1.4 Los DOS chips no compiten — significan cosas distintas

| Chip | Tone | Dónde aparece | Qué dice |
|---|---|---|---|
| **Anulada** (rojo, tachado) | rojo | En la línea de COMPRA original | "esta compra puntual ya no tiene efecto" |
| **Anulación** (ámbar) | ambar | En cualquier línea del circuito de cancelación (contra-línea, multa retenida, reembolso, ajuste de dólar) | "este movimiento es parte de un circuito de anulación, no una compra/pago normal" |

Nunca se muestran juntos en la misma fila (P-16: un dato no se dice dos veces con dos chips
iguales) — la compra lleva "Anulada", el contra-asiento lleva "Anulación".

### 1.5 Backend (no es decisión de UX, habilita la pantalla)

- **Hoy la compra de una reserva anulada directamente NO aparece** en `GET
  /suppliers/{id}/account/statement` — el origen documenta que "las solapas se autolimpian
  (filtro que excluye Cancelled para que el saldo cierre solo)". Hay que sacar ese filtro de
  exclusión y, en su lugar, mandar la línea de compra + una línea nueva de reverso.
- Nuevo campo en cada línea del extracto: `reservaIsVoided: boolean` (mismo criterio que
  `Reserva.IsVoided` que ya expone el dominio en otras pantallas — ver `isReservaAnulada()` en
  `src/TravelWeb/src/features/reservas/moneyStatus.js`). El front NO decide si algo está anulado:
  lo lee (F-1, T-3).
- Nuevo `kind` de línea (ej. `PurchaseReversal`) para la contra-línea, sumado a la lista de
  `esLineaDeCircuitoCancelacion()`.
- El saldo por moneda del encabezado (`iTheyOwe`/`theyOweMe`/`prepayment`) ya se calcula sobre
  deuda VIVA — al agregar el par compra+reverso neteando cero, esos tres números **no cambian**
  (F-2: la cabecera sale de las líneas, siempre recalculada). Si la reserva anulada además generó
  una multa retenida o un reembolso, esas líneas siguen existiendo aparte, sin tocar (Fase D ya
  construida, 2026-07-01).

---

## 2. Servicios comprados con anuladas

**Componentes:** `src/TravelWeb/src/features/suppliers/pages/SupplierAccountPage.jsx` (panel
`servicios-comprados`, línea ~1835) + `src/TravelWeb/src/features/suppliers/components/
PurchasedServiceRow.jsx` (fila desktop) + `MobileRecordCard` inline (fila mobile, línea ~1932).

**Decisión que ejecuta:** guía 2026-08-19 #1 — "En Servicios comprados: los servicios de reservas
anuladas se ven con chip 'Anulada', con filtro para ocultarlos." Default: **VISIBLES** (así lo
pide el enunciado del brief).

### 2.1 El filtro: mismo molde que YA existe en Operadores, checkbox de toolbar

El patrón exacto ya está en producción, en la pantalla HERMANA de este mismo módulo —
`src/TravelWeb/src/features/suppliers/pages/SuppliersPage.jsx` línea 110-120, el checkbox
"Mostrar inactivos" adentro del `filterSlot` de `ListToolbar`:

```jsx
<label className="flex cursor-pointer select-none items-center gap-2 rounded-[10px] px-3 py-2 text-sm text-slate-600 transition-colors hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800">
  <input type="checkbox" checked={showInactive} onChange={...} className="rounded border-slate-300 text-primary focus:ring-primary" />
  Mostrar inactivos
</label>
```

Se agrega el mismo control, mismo `ListToolbar` que ya usa "Servicios comprados" (línea 1849-1880
del propio `SupplierAccountPage.jsx`), al lado del `<select>` de Tipo — es el control MÁS CHICO
posible (P-16 del brief: no hace falta cartelito explicativo, P-15) y no inventa un componente
nuevo:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [🔍 Buscar descripción, reserva o archivo...]  [Filtro: Todos los tipos ▾]  ☑ Mostrar anuladas │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Texto exacto del label:** `Mostrar anuladas`.
- **Default:** tildado (`checked=true`) — las anuladas se ven de entrada.
- Al destildarlo, se ocultan (mismo criterio que "Mostrar inactivos": filtro server-side, ya que
  la grilla pagina en el backend — línea 1289-1292, `params.set("type", ...)`, mismo lugar donde
  se agrega `params.set("includeVoided", ...)`).
- **Sin contador "(N)"**: a diferencia del molde de servicios cancelados dentro de una reserva
  (`ServiceList.jsx`, guía 2026-07-05), acá la lista pagina en el servidor — no hay forma barata
  de mostrar cuántas anuladas hay sin una query aparte. Se sigue el molde de "Mostrar inactivos"
  (que tampoco lleva número) en vez de inventar una consulta extra.

### 2.2 La fila con chip "Anulada"

Desktop (`PurchasedServiceRow.jsx`, columna Descripción, línea ~78-83):

```
Tipo    Descripción                          Reserva        Fecha    Vto.   Estado         Código   Costo      Venta
Hotel   Hotel Bariloche 3 noches [ANULADA]    F-2026-1050    10/06    ...    Sin acción      —      $ 78.000  $ 90.000
        (tachado gris)
```

- Mismo chip que en el extracto: `<StatusChip tone="rojo" className="line-through">Anulada</StatusChip>`
  al lado de la descripción, cuando `service.reservaIsVoided === true`.
- Nombre del servicio (línea 79) tachado (`line-through text-slate-400 dark:text-slate-500`) —
  mismo molde que `ServiceList.jsx`.
- **Columna Estado (P-19, "no se ofrece una acción que no existe"):** cuando el servicio es de
  una reserva anulada, no tiene sentido ofrecer "Confirmar"/"Emitir" — se reemplaza por un texto
  fijo de solo lectura: `Reserva anulada` (gris, sin botón, mismo criterio que ya usa esta misma
  columna cuando `!tieneBotonPrimario`, línea 110-111: cae a `ServiceStatusEditor` en modo
  lectura). No es una decisión nueva: es P-19 aplicada a un caso que hoy no está contemplado.
- Costo y Venta: **no se tachan acá** (a diferencia del extracto) — esta grilla ya tacha el
  nombre, que alcanza como señal; los importes de Costo/Venta en esta tabla son informativos de
  lo que costó el servicio en su momento, no una cuenta corriente viva. (Si Gastón mirando la
  pantalla real pide que también se tachen, es un ajuste de una línea, no una decisión de fondo.)

Mobile (`MobileRecordCard`, línea ~1932-1993): se agrega `statusSlot={<StatusChip tone="rojo"
className="line-through">Anulada</StatusChip>}` cuando corresponda — mismo prop que ya usa
`FacturacionClienteTab.jsx` en su tarjeta mobile (línea 286: `statusSlot={<ChipEstadoFiscal
invoice={invoice} />}`). El `title` de la tarjeta (línea 1936) se tacha con el mismo criterio.

### 2.3 Backend (no es decisión de UX, habilita la pantalla)

- `GET /suppliers/{id}/account/services` hoy probablemente excluye servicios de reservas
  anuladas (mismo patrón de filtro que el extracto, §1.5) — hay que sacar la exclusión y sumar
  `reservaIsVoided: boolean` a cada item.
- Nuevo query param `includeVoided` (default `true` del lado del backend también, para que un
  cliente de API viejo sin el param siga viendo todo — coherente con "default VISIBLES").

---

## 3. Solapa nueva "Historial" en la ficha del operador

**Decisión que ejecuta:** guía 2026-08-19 #2 — "Solapa nueva 'Historial' en la ficha del
operador: línea de tiempo de todo lo que pasó con ese operador [...] Es la única superficie donde
las decisiones SIN plata ('cerrada sin multa') quedan visibles en la ficha. Montos enmascarados
sin permiso de ver costos (F-14); molde visual del Historial de la reserva."

### 3.1 Posición en el array de solapas

Array actual, `SupplierAccountPage.jsx` línea 1537-1545:

```js
const solapas = [
    { id: "cuenta-corriente",    label: "Cuenta corriente",    icon: CreditCard  },
    { id: "deuda-por-reserva",   label: "Deuda por reserva",   icon: Layers      },
    { id: "servicios-comprados", label: "Servicios comprados", icon: Building2   },
    ...(puedeVerFacturasProveedor ? [{ id: "facturas-operador", label: "Facturas operador", icon: FileText }] : []),
    ...(puedeVerReembolsos ? [{ id: "reembolsos", label: labelReembolsos, icon: RotateCcw }] : []),
    { id: "datos-bancarios",     label: "Datos bancarios",      icon: Landmark    },
    { id: "datos",               label: "Datos",                icon: Settings    },
];
```

**Se agrega AL FINAL, después de "Datos":**

```js
{ id: "historial", label: "Historial", icon: Clock },
```

**Por qué al final (razonamiento, no pregunta):** las solapas de plata/operación viva van primero
(Cuenta corriente, Deuda, Servicios, Facturas, Reembolsos); las de "ficha/metadata" van al final
(Datos bancarios, Datos). Historial es una solapa de consulta ocasional — un registro de auditoría
— de la misma familia que "Datos", no una que se mira todos los días como "Cuenta corriente". El
ícono `Clock` es el mismo que ya usa `ReservaDetailPage.jsx` (línea 2311) para su propia solapa
"Historial" — mismo ícono, mismo concepto, en toda la app.

### 3.2 Gate de visibilidad

**Ninguno especial.** La solapa se ve para cualquiera que pueda entrar a la ficha del operador
(no está detrás de `puedeVerFacturasProveedor` ni de ningún permiso nuevo) — es la MISMA regla
que ya usan "Cuenta corriente" o "Servicios comprados", que tampoco tienen gate propio. Lo único
que se enmascara son los MONTOS dentro de cada evento (§3.4), no la solapa entera.

### 3.3 El molde visual: reuso exacto de `ReservaTimeline.jsx`

Componente nuevo: `src/TravelWeb/src/features/suppliers/components/SupplierHistorialSection.jsx`,
mismo esqueleto que `src/TravelWeb/src/components/ReservaTimeline.jsx` (agrupado por día con
`agruparEventosPorDia`, separador `SeparadorDeDia`, renglón `Hito` con hora + punto de color +
línea vertical + frase con actor en negrita), apuntando a un endpoint nuevo `GET
/suppliers/{id}/timeline` en vez de `/reservas/{id}/timeline`. Estados de carga/error/vacío
idénticos ("Cargando el historial…" / `ListLoadErrorState` / "Todavía no pasó nada con este
operador.").

**Orden:** más nuevo arriba — mismo critero que ya documenta `ReservaTimeline.jsx`: "el backend ya
manda los eventos del más nuevo al más viejo, acá solo se agrupan por día".

**Sin ícono adentro del punto de la línea de tiempo** — es una desviación consciente del pedido
de "ícono" del brief: `ReservaTimeline.jsx` (el molde que la decisión pide reusar EXACTO) no
lleva íconos, solo un punto de color de 8px + la frase. Meterle un ícono adentro de un punto tan
chico rompería el molde que se pidió reusar tal cual. La categoría de cada evento se lee por el
color del punto + la frase (igual que hoy en la reserva), no por un dibujito.

```
Historial

Hoy — 19/08/2026
19:40  ●  María anuló la reserva.
       │  Motivo: el pasajero se bajó del viaje
18:02  ●  La multa del operador quedó confirmada: $ 45.000.
       │
17:50  ●  Se registró un reembolso del operador: $ 30.000.

Viernes 07/08/2026
11:15  ●  Se compró Hotel Bariloche 3 noches: $ 90.000.
```

### 3.4 Los 6 tipos de evento — texto exacto, color, dato que muestra

Cada evento sigue el mismo formato de `describirEventoHistorial()`: actor en negrita (si lo hizo
una persona) + frase + detalle chico opcional. Los montos respetan F-14: si el usuario NO tiene
`cobranzas.see_cost`, el monto se reemplaza por el texto genérico sin número (nunca "$0", nunca se
esconde el evento entero — solo el número).

| # | Evento | Color del punto | Texto exacto (con permiso de costo) | Sin permiso de costo | Detalle secundario |
|---|---|---|---|---|---|
| 1 | Compra confirmada | neutro (gris) | `Se compró {descripción del servicio}: {monto}.` | `Se compró {descripción del servicio}.` | `Reserva {numero}` |
| 2 | Reserva anulada | rojo | `{Actor} anuló la reserva.` (o `Se anuló la reserva.` si lo hizo el sistema) | igual, no lleva monto | `Motivo: {motivo}` si vino |
| 3a | Multa del operador confirmada | indigo | `La multa del operador quedó confirmada: {monto}.` | `La multa del operador quedó confirmada.` | `Reserva {numero}` |
| 3b | Multa del operador cerrada sin multa | ámbar | `{Actor} cerró la multa del operador sin cobrar nada.` | igual | reusa texto ya escrito en `operatorPenaltyBanner.js` línea 490: `Cerrada sin multa el {fecha} por {actor}` como detalle si el actor no entra en la frase principal |
| 4a | Reembolso del operador registrado | verde | `Se registró un reembolso del operador: {monto}.` | `Se registró un reembolso del operador.` | `Reserva {numero}` |
| 4b | Reembolso del operador deshecho | rojo | `{Actor} deshizo el reembolso del operador.` | igual | reusa chip ya escrito en `OperatorRefundsRegisteredSection.jsx` línea 76-84: `Deshecho` |
| 5 | Pago al operador registrado | neutro (gris) | `Se registró un pago al operador: {monto}.` | `Se registró un pago al operador.` | método de pago si vino, mismo traductor `traducirMetodoPago` que ya usa `reservaTimelineText.js` |
| 6a | Factura del operador cargada | indigo | `Se cargó la factura {número} del operador: {monto}.` | `Se cargó la factura {número} del operador.` | — |
| 6b | Factura del operador anulada | rojo | `{Actor} anuló la factura {número} del operador.` | igual | `Motivo: {motivo}` — reusa texto ya escrito en `SupplierInvoicesSection.jsx` línea 159 ("Anular factura") |

**Por qué esos colores (criterio, no arbitrario):** rojo = evento sin efecto/reversa (mismo
significado que ya usa `ReservaTimeline.jsx` para `SoftDelete`/`Delete` y que usa `StatusChip
tone="rojo"` en toda la app — P-20, "un color, un significado"). Verde = entra plata a favor de
la agencia (mismo espíritu que "un cobro entra plata" de `FraseCobro`, aplicado acá a un
reembolso recibido). Índigo = documento fiscal/comprobante (mismo tono que `ReservaTimeline.jsx`
usa para `Invoice`). Ámbar = decisión relevante que NO mueve plata (la única familia de eventos
"sin cargo/sin abono" de toda la lista — coherente con `StatusChip tone="ambar"` = "te pide
algo"/una decisión notable, no un freno). Neutro = movimiento de rutina sin nada especial que
resaltar.

### 3.5 Backend (no es decisión de UX, habilita la pantalla)

Nuevo endpoint `GET /suppliers/{id}/timeline`, análogo a `TimelineService.GetTimelineAsync` pero
scopeado a un proveedor en vez de a una reserva — junta eventos de varias fuentes: compras
(ServicioReserva por proveedor), `ReservaStatusChangeLogs` de las reservas de ese operador,
`BookingCancellationLine` (multa confirmada/cerrada sin multa), `SupplierPayment`,
`SupplierInvoice`, reembolsos registrados. **No es un timeline nuevo desde cero**: reusa
`TimelineEventDto` (mismo contrato: `eventType`, `relatedEntityType`, `actor`, `timestamp`,
`amount`, `currency`, `details`) filtrando por `SupplierId` en vez de `ReservaId`, más el
`amountsVisible` a nivel raíz que ya usa `SupplierExtractoSection` para F-14.

---

## 4. Historial de la reserva ampliado — la anulación entra al timeline

**Componente:** `src/TravelWeb/src/components/ReservaTimeline.jsx` +
`src/TravelWeb/src/lib/reservaTimelineText.js` (`describirEventoHistorial`).

**Decisión que ejecuta:** guía 2026-08-19 #3 — "El Historial de la RESERVA también muestra la
anulación: anulación confirmada, decisión de multa (cobrada/perdonada) y sus notas de crédito —
hoy ese timeline no incluye la cancelación, ni siquiera en la propia reserva."

### 4.1 Qué falta hoy (constatado en código, no supuesto)

`describirEventoHistorial()` ya maneja bien el cambio de estado genérico vía el evento
`StatusChange` (`fraseYDetalleCambioDeEstado`, línea 287) — SI ese evento llega. El problema es
que la anulación de una reserva corre por
`src/TravelApi.Infrastructure/Services/BookingCancellationService.cs`, y no está confirmado que
ese flujo escriba en `ReservaStatusChangeLogs` con el mismo detalle que un cambio de estado común
(motivo, autorizante) — es la causa más probable de que "el timeline no incluye la cancelación".
Ese cableado es tarea de `backend-dotnet-senior`, no de esta spec de UX.

### 4.2 Textos exactos — TODOS ya escritos en algún lado del código, se reusan sin inventar

| Evento | Texto exacto | De dónde sale (reuso, no invención) |
|---|---|---|
| Anulación confirmada | `{Actor} anuló la reserva.` (o `Se anuló la reserva.` si fue el sistema) | Mismo patrón `fraseYDetalleCambioDeEstado` ya construye para cualquier cambio de estado (`La reserva pasó de X a Anulada.` es la rama genérica; se prefiere la frase de acción "anuló" porque coincide con el toast ya existente `showSuccess(mensajeExito, "Anulación confirmada")` de `CancelarReservaInline.jsx` línea 405) |
| Multa del operador confirmada | `La multa del operador quedó confirmada: {monto}.` | Mismo texto que §3.4 #3a — coherente entre las dos pantallas |
| Multa del operador cerrada sin multa | `{Actor} cerró la multa del operador sin cobrar nada.` | Mismo texto que §3.4 #3b |
| Nota de crédito emitida | `{Factura} — nota de crédito emitida.` | Texto YA escrito en `multiCreditNoteFlow.js` línea 290 (`describirNotaPorFactura`), literal |
| Nota de crédito rechazada por ARCA | `{Factura} — la nota no salió. ARCA respondió: «{motivo}».` (o sin la cita si no vino motivo) | Texto YA escrito en `multiCreditNoteFlow.js` líneas 295-297, literal (T1: nunca se parafrasea el motivo de ARCA) |
| Nota de crédito reintentada | `{Actor} volvió a intentar la nota de crédito.` | Nuevo, mismo patrón gramatical que el resto de la tabla — se dispara cuando el usuario aprieta "Reintentar" sobre una nota Failed; el resultado (emitida/rechazada otra vez) genera su PROPIO evento posterior con los textos de arriba |

Color del punto: mismo criterio que ya define `COLOR_PUNTO` en `ReservaTimeline.jsx` — anulación
y NC rechazada → rojo; NC emitida → índigo (ya es el color de `Invoice`); multa confirmada →
índigo (documento fiscal); multa cerrada sin multa → ámbar (mismo criterio de §3.4); NC
reintentada → neutro (es un intento, no un resultado).

### 4.3 Backend (no es decisión de UX, habilita la pantalla)

- Confirmar que `BookingCancellationService.cs` escribe en `ReservaStatusChangeLogs` con motivo,
  igual que cualquier otro cambio de estado — si no lo hace, agregarlo (mismo patrón que ya usa
  el resto de las transiciones, T-7 "un solo escritor por estado derivado").
- Sumar al timeline de la reserva los eventos de `BookingCancellationLine` (confirmación/perdón de
  multa) y `BookingCancellationCreditNote` (emisión/rechazo/reintento de NC) — mismas tablas que
  ya alimentan §3.5, ahora también scopeadas a `ReservaId` además de `SupplierId`.

---

## Qué NO cambia

- La solapa **"Deuda por reserva"** del operador sigue mostrando SOLO deuda viva — no se le agrega
  nada de esto (así lo pide explícito la decisión #1).
- Ninguna otra solapa de la ficha del operador cambia (Cuenta corriente conserva su estructura de
  3 recuadros + tabla; Facturas operador y Reembolsos no se tocan).
- El extracto sigue siendo documento de CONSULTA — la fila de compra anulada y su contra-línea no
  llevan botones de acción (mismo criterio que el resto del extracto).
- Nada se borra en ningún punto de esta spec — todo lo que hoy desaparece (compra filtrada,
  servicio filtrado, cancelación ausente del timeline) pasa a **quedar visible, tachado o con su
  contra-asiento** (F-6).

---

## Preguntas

Ninguna. Las 3 decisiones firmadas + los moldes ya en producción (chip de factura anulada del
cliente, checkbox "Mostrar inactivos" de Operadores, `ReservaTimeline.jsx`, textos ya escritos de
multa/reembolso/NC/factura) cubren layout, textos, colores y comportamiento de las 4 superficies
pedidas. Los puntos marcados "Backend" no son preguntas de UX: son trabajo de
`backend-dotnet-senior` para habilitar lo que esta spec ya define.
