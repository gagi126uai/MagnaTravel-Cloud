# T4 — Estados derivados de la reserva: ficha + listados (spec UX, 2026-07-17)

> **Qué es esto:** la especificación de pantalla de la **Tanda 4** del modelo de estados
> derivados. La escribe `ux-ui-disenador` a partir de `docs/ux/guia-ux-gaston.md` (única
> fuente de verdad de UX) y del diseño de arquitectura
> `docs/architecture/2026-07-17-modelo-estados-derivados-DISENO-implementacion.md` (§T4, B3)
> + el borrador aprobado por Gastón regla por regla.
>
> **Depende de T1** (M4): T4 lee del backend el booleano `isVoided`, que cubre el PAR
> `{Cancelled, PendingOperatorRefund}` (B3). No se implementa T4 sin T1.
>
> **Alcance:** solo lectura/presentación. No toca reglas de negocio ni fiscales ni el cálculo
> de plata. No renombra estados ni carteles de cabecera (esos ya están decididos: la cabecera
> con todos los servicios anulados muestra el cartel **"Anulada"** de hoy, y **"Esperando el
> reembolso del operador"** mientras el operador deba — decisión de Gastón, no se re-pregunta).
>
> Al final hay un bloque **PREGUNTAS PARA GASTON** con los 3 huecos que la guía NO cubre.
> Todo lo demás está cubierto y se cita la regla que lo respalda.

---

## Punto 1 — Importes tachados del servicio anulado en la lista de servicios

### Qué cubre la guía (no se pregunta)
- **Regla 6 del modelo de estados (aprobada por Gastón, 2026-07-17):** "Un servicio anulado no
  genera avisos de cobro al cliente ni de pago al operador, salvo multa/ND propia; **sus importes
  se muestran tachados/históricos**." → La decisión de tachar los importes ya está tomada.
- **(2026-06-13)** "Servicio cancelado **TACHADO** en la lista, con motivo + quién + cuándo."
- **(2026-06-11 / 2026-06-13)** "El **total al pie NO cuenta** el servicio cancelado."
- **Patrón visual ya existente y aprobado (auditado en `ServiceList.jsx:1261-1266`, 1683-1684):**
  el **nombre** del servicio anulado ya se muestra `line-through` + gris atenuado (`text-slate-400`),
  con la línea de auditoría "Anulado por {usuario} el {fecha}" debajo.

Hoy el nombre se tacha pero las **dos columnas de importe se muestran plenas** (Costo neto en
`ServiceList.jsx:1409`, Precio venta en `:1420`). Eso es exactamente lo que la regla 6 viene a
corregir.

### Spec
Aplicar a las dos celdas de importe del servicio anulado **el mismo tratamiento visual que ya
tiene el nombre** (no se inventa estilo nuevo: se reusa el patrón aprobado):

- **Costo neto** (solo visible para `cobranzas.see_cost`) → `line-through text-slate-400`.
- **Precio venta** → `line-through text-slate-400` (pierde el `font-bold text-slate-900` del vivo).
- Los importes **NO se ocultan**: quedan visibles pero tachados (son historia de la reserva).
- El **total al pie** sigue **sin** contar el servicio anulado (ya es así; no cambia).

Esto vale en escritorio y en la versión mobile de la fila (mismo criterio que el nombre).

```
LISTA DE SERVICIOS (reserva Confirmada con una anulación parcial)
┌───────────────────────────────────────────────────────────────────────────┐
│ Servicio                         Fechas      Costo neto   Precio venta      │
├───────────────────────────────────────────────────────────────────────────┤
│ Hotel Maitei Posadas   [Confirmado]                                         │
│   Operador: Ola                  12/08–15/08   US$ 300      US$ 450         │
├───────────────────────────────────────────────────────────────────────────┤
│ A̶é̶r̶e̶o̶ ̶A̶E̶P̶ ̶→̶ ̶I̶G̶R̶   [Anulado]                                          │
│   Operador: Aerolíneas           10/08         U̶S̶$̶ ̶1̶8̶0̶     U̶S̶$̶ ̶2̶4̶0̶       │
│   Anulado por Gastón el 17/07                                               │
├───────────────────────────────────────────────────────────────────────────┤
│                                          TOTAL: US$ 300 · US$ 450  (excluye │
│                                          el anulado)                        │
└───────────────────────────────────────────────────────────────────────────┘
```

### Convivencia con la multa / cargo propio (lo que pide el punto 1)
La **multa NO es un importe de la fila del servicio**: no se muestra ni en Costo neto ni en Precio
venta. La multa vive en su lugar único ya decidido (paso de multa en la ficha + chip Pago "Multa
por anulación pendiente de cobro" + renglón del extracto — 2026-07-08 y 2026-07-16). Por eso en la
fila del servicio anulado **no hay conflicto**: los importes propios del servicio quedan tachados
(historia), y la multa se resuelve/mira aparte. La fila del servicio anulado muestra:
**badge "Anulado"** + **nombre e importes tachados** + **línea "Anulado por … el …"**, y nada más
de plata.

### Qué NO hacer
- No ocultar los importes del anulado (quedan tachados, no desaparecidos).
- No sumar el anulado al total al pie.
- No inventar un estilo de tachado distinto del que ya usa el nombre.
- No poner el monto de la multa en las columnas del servicio.

---

## Punto 2 — Chip Factura: nueva lectura "Facturada y devuelta"

### Qué cubre la guía (no se pregunta)
- **Regla 5 del modelo (aprobada):** "El chip de Factura distingue 'nunca se facturó' de 'se
  facturó y se devolvió': con factura + NC dice **'Facturada y devuelta'**, jamás 'Sin facturar'."
  → El **texto** ya está decidido por Gastón.
- **(2026-06-21)** El chip Factura es siempre visible, tratamiento secundario (chico, prefijo
  "Factura:" en gris), y hoy tiene 3 valores: `Sin facturar` / `Facturada en parte` /
  `Facturada total` (`ReservaStatusChips.jsx:35-51`).
- **Sin variante parcial-devuelta:** por el alcance del diseño (T3), el nuevo valor
  "Facturada y devuelta" aparece **solo** cuando hubo factura (bruto con CAE > 0) y el neto quedó
  ~0 por las notas de crédito. Si todavía queda algo facturado en pie (neto > 0), el chip sigue
  siendo "Facturada en parte" o "Facturada total". **No hay un cuarto texto "facturada en parte y
  devuelta en parte"** — no hace falta preguntarlo.

### Spec (P1 = B, FIRMADA por Gastón 2026-07-17)
Se agrega un cuarto valor al chip Factura:

- **Label:** `✓ Facturada y devuelta` (con una **tilde ✓ adelante** del texto).
- **Color/tono:** **gris pizarra**, la misma familia neutra que "Sin facturar"
  (`bg-slate-100 text-slate-600 border-slate-200` + su variante dark). La tilde ✓ + el texto lo
  separan claramente de "Sin facturar": la tilde comunica "ciclo cerrado" (se facturó y se dio
  marcha atrás), no "todavía nada". No se inventa un color nuevo (coherente con la paleta de chips
  existente).
- **Tooltip:** "Se facturó y después se devolvió con una nota de crédito. No queda saldo facturado."

```
CABECERA — chips de plata (reserva anulada que tuvo factura + NC)
   Pago:  [ Saldo a favor ]      Factura:  [ ✓ Facturada y devuelta ]
                                            └── gris pizarra + tilde

(Los cuatro estados posibles del eje Factura, de referencia)
   Factura: [ Sin facturar ]            gris
   Factura: [ Facturada en parte ]      ámbar
   Factura: [ Facturada total ]         verde
   Factura: [ ✓ Facturada y devuelta ]  gris + tilde ✓   ← FIRMADA (P1=B)
```

### Qué NO hacer
- Nunca mostrar "Sin facturar" en una reserva que tuvo factura y NC (esa es justo la mentira que
  se corrige).
- No agregar variantes de texto que el diseño no contempla.

---

## Punto 3 — Chip Pago en la reserva sin efecto (circuito de anulación)

### Qué cubre la guía (no se pregunta)
- **Regla 4 del modelo (aprobada):** "El chip de Pago **nunca dice 'Sin movimientos'** si la
  reserva quedó sin efecto: muestra el **circuito de anulación** (saldo a favor / multa)."
- Ese circuito **ya existe** en `moneyStatus.js` (rama `getMoneyStatusAnulada`) y sus textos ya
  están aprobados (Tanda 6, 2026-07-05 + multa fantasma, 2026-07-06). Se pintan en
  `ReservaStatusChips.jsx:99-113`:
  - **Saldo a favor** → chip verde, texto **"Saldo a favor"**.
  - **Multa por anulación** → chip ámbar, texto **"Multa por anulación pendiente de cobro"**.
- **El único cambio de T4 es mecánico** (INV-048-08): `moneyStatus.js` deja de decidir "anulada"
  con el string de estado hardcodeado (`ESTADOS_ANULADOS = {Cancelled, PendingOperatorRefund}`,
  `:32`) y pasa a leer el booleano **`isVoided`** que expone el backend, que cubre el mismo par
  (B3). Así, cuando T1 lleva la cabecera al terminal, el chip entra solo a la rama de anulación.
  Sin este cambio, una reserva `PendingOperatorRefund` perdería el circuito.

### Spec
- **Saldo a favor pendiente** → `Pago: [ Saldo a favor ]` (verde).
- **Multa por anulación pendiente de cobro** → `Pago: [ Multa por anulación pendiente de cobro ]`
  (ámbar).
- **Multa en revisión / dato inconsistente** → **no se muestra chip de Pago** (regla ya vigente:
  no se le promete al vendedor un cobro sin comprobante; lo atiende el back-office). Sin cambios.
- **Sin plata (anulada limpia: ni saldo a favor ni multa)** → **no se muestra chip de Pago**
  (P2 = A, FIRMADA por Gastón 2026-07-17). El badge "Anulada" ya es la foto del estado; no hay plata
  que reportar, así que el eje Pago no dice nada. (Compatible con la regla 4: no hay "Sin
  movimientos"; simplemente no hay chip.)

```
CABECERA — reserva Anulada, según el circuito
  a) quedó plata del cliente:   Pago: [ Saldo a favor ]                        (verde)
  b) hay multa por cobrar:      Pago: [ Multa por anulación pendiente de cobro ](ámbar)
  c) anulada sin plata:         (sin chip de Pago)                             ← FIRMADA (P2=A)
```

### Qué NO hacer
- Nunca mostrar "Sin movimientos" ni "Debe" en una reserva sin efecto.
- No volver a decidir "anulada" leyendo el string de estado en el front: se lee `isVoided`.

---

## Punto 4 — Avisos por servicio que desaparecen en el anulado

### Qué cubre la guía (no se pregunta)
- **Reglas 6 y 7 del modelo (aprobadas):** un servicio anulado **no** genera avisos de cobro al
  cliente ni de pago al operador (salvo multa/ND propia); "Operador impago" solo existe sobre
  servicios vivos o multas reales.
- **Lo que QUEDA en la fila anulada ya está decidido** (no es un vacío):
  - **Badge "Anulado"** en la columna Estado (2026-06-21 P6=A / A-cuater: "todos sus servicios
    muestran 'Anulado'").
  - **Nombre e importes tachados** (Punto 1) + **línea "Anulado por … el …"** (2026-06-13).

### Spec
- El **badge de pago al operador** (`OperadorPagoStatusBadge`, "⚠️ Operador impago" / "✔ Operador
  pagado") **NO se muestra** en un servicio anulado. (Tras T2, el backend deja de reportar estado
  de pago al operador para servicios anulados, así que el badge no tiene datos que pintar.)
- Los **avisos de cobro/próximo inicio por fila** (etiqueta de "Próximos inicios") tampoco
  aparecen sobre un anulado — ya es así hoy (2026-06-06 Ronda 5: "En servicios CANCELADOS no
  aparece").
- **Lo que se conserva** en la fila: badge "Anulado", nombre + importes tachados, línea de
  auditoría. Nada de "Operador impago"/"pagado" ni aviso de inicio.

```
Fila de servicio anulado SIN multa — así queda (sin avisos de plata viva)
┌───────────────────────────────────────────────────────────────────┐
│ A̶é̶r̶e̶o̶ ̶A̶E̶P̶ ̶→̶ ̶I̶G̶R̶   [Anulado]                                    │
│   Operador: Aerolíneas         U̶S̶$̶ ̶1̶8̶0̶      U̶S̶$̶ ̶2̶4̶0̶               │
│   Anulado por Gastón el 17/07                                       │
│   (sin "Operador impago", sin aviso de inicio)                     │
└───────────────────────────────────────────────────────────────────┘
```

### Sub-caso: servicio cancelado que dejó multa del operador — etiqueta "Con multa" (P3, FIRMADA por Gastón 2026-07-17)
Gastón eligió que la fila del servicio cancelado **con multa** muestre una **etiqueta chica propia**,
para verla de un vistazo sin abrir el detalle. (Sigue valiendo que la multa se resuelve/mira en su
lugar único: paso de multa + chip Pago + extracto; la etiqueta es solo un **cartelito de aviso a la
vista**, no una acción ni un monto.)

**Texto:** `Con multa` (sin monto — la etiqueta nunca muestra cifras, para que la vea también quien
no tiene permiso de costos; misma regla que el badge de operador, 2026-06-21 P4=B).

**Formato visual** (reusa el patrón del badge hermano `OperadorPagoStatusBadge.jsx`, que es el
cartelito de plata por servicio: puntito de color + texto chico, sin recuadro):
- Estilo idéntico: `inline-flex items-center gap-1 text-[10px] font-semibold`.
- **Multa pendiente de cobro / firme sin cobrar del todo →** ámbar suave:
  puntito `bg-amber-400` + texto `text-amber-700` (dark `text-amber-400`).
  Ámbar = "ojo, hay plata", el mismo lenguaje del chip Pago "Multa por anulación pendiente de
  cobro". **No** se usa rosa/rojo: rosa es el color de error/"Anulado", y esto no es un error.

**Posición:** en la **columna Estado**, en la misma celda/renglón donde vivía el badge de operador
(`ServiceList.jsx:~1369`), debajo del badge "Anulado". Así queda como el único dato "vivo" de una
fila por lo demás histórica y tachada — que es justo lo que Gastón quiere ver de un vistazo.

**Qué pasa cuando la multa YA se cobró por completo** (decisión de spec, tomada con el criterio del
cartel "Multa cobrada" ya aprobado el 2026-07-16 — cobrada = gris + tilde + cerrado; **no** se
re-pregunta):
- **Multa cobrada por completo →** la etiqueta **cambia a estado cerrado**: gris + tilde ✓,
  texto **`✓ Multa cobrada`** (puntito/gris `text-slate-500`, con ✓). **No desaparece**: queda a la
  vista en gris, coherente con que el cartel de multa cobrada tampoco desaparece (se cierra en gris).
  Así el vendedor ve de un vistazo que ese servicio cancelado tuvo multa y que ya está saldada.
- **Multa en revisión / en trámite (nota de débito todavía sin emitir):** la etiqueta se muestra
  igual como **`Con multa`** ámbar (es un hecho: hay multa). El chip Pago de la cabecera mantiene su
  regla más estricta de no prometer cobro sin comprobante; la etiqueta de la fila solo informa que
  el servicio dejó multa, no promete cobro. (Data dependency abajo.)

```
Fila de servicio cancelado CON multa pendiente
┌───────────────────────────────────────────────────────────────────┐
│ A̶é̶r̶e̶o̶ ̶A̶E̶P̶ ̶→̶ ̶I̶G̶R̶   [Anulado]  • Con multa                     │
│   Operador: Aerolíneas         U̶S̶$̶ ̶1̶8̶0̶      U̶S̶$̶ ̶2̶4̶0̶               │
│   Anulado por Gastón el 17/07                                       │
└───────────────────────────────────────────────────────────────────┘
        └── puntito ámbar + "Con multa" (text-[10px], sin monto)

Misma fila, una vez COBRADA la multa por completo
┌───────────────────────────────────────────────────────────────────┐
│ A̶é̶r̶e̶o̶ ̶A̶E̶P̶ ̶→̶ ̶I̶G̶R̶   [Anulado]  ✓ Multa cobrada                 │
│   Operador: Aerolíneas         U̶S̶$̶ ̶1̶8̶0̶      U̶S̶$̶ ̶2̶4̶0̶               │
│   Anulado por Gastón el 17/07                                       │
└───────────────────────────────────────────────────────────────────┘
        └── gris + tilde ✓ (cerrado; no desaparece)
```

**Data dependency (backend, para el implementador):** por servicio, un flag de "tiene multa del
operador" + el estado de esa multa (pendiente/firme · cobrada por completo · en trámite/revisión).
Es la misma información que ya alimenta el paso de multa y el chip Pago; la etiqueta solo la pinta,
no la recalcula. Sin ese dato, la etiqueta no se muestra (degradación silenciosa, igual que el
badge de operador cuando el endpoint falla).

### Qué NO hacer
- No mostrar "Operador impago" ni "✔ Operador pagado" sobre un servicio anulado (esos badges se
  van; en su lugar, si hay multa, aparece "Con multa").
- No mostrar montos en la etiqueta "Con multa".
- No usar rosa/rojo para "Con multa" (rosa = error/Anulado; la multa es un aviso, no un error).
- No hacer desaparecer la etiqueta cuando la multa se cobra: pasa a "✓ Multa cobrada" gris.
- No mostrar la etiqueta de próximo inicio sobre un anulado.

---

## Punto 5 — Coherencia con los listados

### Qué cubre la guía (no se pregunta)
- **(2026-07-05 P1=A)** Las anuladas viven en su **propia pestaña "Anuladas"** (con las
  "Esperando el reembolso del operador" adentro, P2=A). Ya construido.
- **(2026-07-05 P3=A)** En el listado **NO se atenúa ni se tacha la fila** de la anulada: el
  cartelito/badge de estado alcanza. (El tachado es cosa de la ficha, no del listado.)
- **Fuente única:** el listado (`ReservaTable`) y la cuenta del cliente usan **la misma**
  `getMoneyStatus`. Al leer `isVoided` del backend (Punto 3), la fila anulada enruta sola a la
  rama de anulación: nunca dirá "Debe"/"Sin movimientos" sobre una sin efecto (INV-048-08).
- **Extracto del cliente:** ya muestra las anuladas **dentro** del extracto (factura como cargo +
  NC como abono + multa como renglón) desde 2026-07-16 (P1=A). No cambia con T4.

### Spec
- El listado de reservas **no cambia visualmente**: sigue sin tachar filas (P3=A), y el badge de
  estado ya distingue Anulada / Esperando reembolso (2026-07-05). El único efecto de T4 es que,
  al derivarse el estado (T1), esas filas caen en la pestaña correcta y su lectura de plata
  (vía `getMoneyStatus` + `isVoided`) queda coherente con la ficha.
- Los chips Pago/Factura **en el listado** quedan **fuera de alcance de T4**: hoy el listado no
  pinta esos chips de plata (es un follow-up ya anotado el 2026-06-21). No se agregan acá.

### Qué NO hacer
- No tachar ni atenuar la fila de la anulada en el listado.
- No abrir el follow-up de chips de plata del listado dentro de T4.

---

## Resumen de estados de la ficha (para el implementador)

| Superficie | Reserva viva | Reserva sin efecto (isVoided) |
|---|---|---|
| Badge de cabecera | estado real | "Anulada" / "Esperando el reembolso del operador" (ya existe) |
| Chip Pago | Pagada / Sin movimientos / Debe — no viaja | Saldo a favor / Multa por anulación pendiente de cobro / (sin chip si no hay plata) |
| Chip Factura | Sin facturar / Facturada en parte / Facturada total | + **✓ Facturada y devuelta** (gris + tilde) |
| Fila de servicio anulado | — | nombre + importes tachados, badge "Anulado", línea de auditoría, sin avisos de plata; si dejó multa → etiqueta **"Con multa"** ámbar (→ **"✓ Multa cobrada"** gris al saldarse) |
| Listado | badge de estado, sin tachar | pestaña "Anuladas", sin tachar la fila |

Estados por cubrir en la caminata E2E de T4: cargando, vacío (sin servicios), reserva
`PendingOperatorRefund`, reserva `Cancelled` con saldo a favor, con multa, y sin plata; anulación
parcial (cabecera sigue Confirmada, una fila tachada). Sin exponer IDs/enums/strings internos en
ningún texto (gate data-exposure).

---

# DECISIONES DE GASTON — FIRMADAS 2026-07-17

Las tres preguntas abiertas quedaron respondidas por Gastón. **La spec está LISTA PARA IMPLEMENTAR.**

- **P1 = B (FIRMADA).** Chip Factura "Facturada y devuelta" = **gris pizarra + tilde ✓**
  (`✓ Facturada y devuelta`), misma familia neutra que "Sin facturar", desambiguado por la tilde y
  el texto. Ver Punto 2.
- **P2 = A (FIRMADA).** En una reserva anulada **sin plata** (ni saldo a favor ni multa) **no se
  muestra chip de Pago**. El badge "Anulada" es la foto; no hay "Sin movimientos". Ver Punto 3.
- **P3 = etiqueta "Con multa" (FIRMADA — Gastón eligió la opción NO recomendada).** La fila del
  servicio cancelado **con multa** muestra una **etiqueta chica "Con multa"** (ámbar suave, sin
  monto), para verla de un vistazo sin abrir el detalle. Al saldarse la multa por completo, la
  etiqueta pasa a **"✓ Multa cobrada"** (gris, cerrado, no desaparece — criterio del cartel de multa
  cobrada 2026-07-16). Spec completa en el Punto 4, sub-caso "Con multa".
