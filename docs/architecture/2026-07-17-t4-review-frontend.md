# Review frontend — Tanda 4 (modelo de estados derivados) — 2026-07-17

> **RE-REVIEW 2026-07-17 (post-fix B1 + M1): APROBADO.** Ver sección "Re-review" al final.
> El review original quedó en *Changes required*; el bloqueante y la mejora fueron corregidos.

---


Revisor: frontend-reviewer (crítico).
Alcance revisado: `src/TravelWeb` del diff sin commitear + archivos nuevos
`CancellationPenaltyLabel.jsx` y `t4TachadoYMultaPorServicio.test.mjs`. El backend de Tandas 3-4
lo revisan otros; acá solo se cruzó el CONTRATO que el front consume (nombres de campo).

Fuentes de verdad usadas:
1. `docs/ux/2026-07-17-t4-estados-derivados-ficha-reserva.md` (spec FIRMADA por el dueño).
2. `docs/ux/guia-ux-gaston.md` (guía general — vía las citas de regla que la spec referencia).

---

## VEREDICTO: Changes required

Hay **1 desvío de la spec FIRMADA** (tachado de importes incompleto en la rama de costo
confirmable, desktop y mobile). Por la regla del proyecto —"los desvíos de mockup firmado se
realinean, nunca se dejan pasar"— eso bloquea. Todo lo demás cumple la spec con exactitud
(texto/color/posición/estados verificados uno por uno). Es un bloqueante acotado a una
combinación de permiso + flag; el resto del punto 1 está bien.

---

## Hechos verificados

- **Chip "✓ Facturada y devuelta" — presente en las DOS pantallas (foco 1). VERIFICADO.**
  - `ReservaStatusChips.jsx:77-81` → `label: '✓ Facturada y devuelta'`,
    `className: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-700'`,
    tooltip textual = spec. Se renderiza vía `INVOICING_CHIP[reserva.invoicingStatus]`
    (`:141`), con `data-testid` `chip-factura-FullyReturned` (`:184`). Coincide EXACTO con
    spec Punto 2 (bg-slate-100 text-slate-600 border-slate-200 + dark, tilde ✓ adelante).
  - `EstadoCuentaResumen.jsx:483-492` → mismo texto, `bg-slate-100 text-slate-600 dark:...`
    (sin borde, coherente con los otros tres chips de esa pantalla que tampoco lo llevan).
    **ANTES devolvía `null` para este valor → el chip desaparecía; ahora se muestra.** Es el
    hueco que el foco 1 pedía cerrar. VERIFICADO.
  - Contrato backend confirmado: `ReservaInvoicingStatus.cs:50` (`FullyReturned`), calculado
    en `:81`; expuesto en `ReservaDto.cs:464`. El front no inventa el valor.

- **`isVoided` — fuente única en el front. VERIFICADO (con 1 residuo menor, ver Mejoras).**
  - `moneyStatus.js:53-61` `isReservaAnulada(reservaOrStatus)` lee `reserva.isVoided` (boolean)
    y solo cae al `ESTADOS_ANULADOS_FALLBACK` (Cancelled/PendingOperatorRefund por string)
    cuando el DTO no trae el campo. `getMoneyStatus` pasa la reserva COMPLETA (`:95`).
  - Contrato backend confirmado: `ReservaDto.cs:285` y `ReservaListDto.cs:35` (`IsVoided`).
  - `ReservaDetailPage.jsx`: `esEstadoCongelado` (`:105`), `esCongeladoParaRecibos` (`:130`) y
    la rama de render de anulada (`:1368`) pasaron de comparar el par de strings a
    `isReservaAnulada(reserva)`. Import agregado (`:36`).
  - `ReservaSummaryStrip.jsx:29` y `CustomerAccountPage.jsx:836` pasan `reserva` (no `.status`).
  - **Los strings `'Cancelled'`/`'PendingOperatorRefund'` que QUEDAN en `ReservaDetailPage`
    (`:853, :1305, :1346, :1554`) NO son el comparador que la spec mandaba unificar**: cada uno
    distingue *entre* los dos estados (comportamiento distinto por estado: polling de multa solo
    en PendingOperatorRefund, panel de multa solo en Cancelled, textos), no deciden "es anulada
    (el par)". Correcto dejarlos como string. La spec unifica la decisión "anulada = el par",
    no las ramas que separan un estado del otro.

- **Tachado de importes (foco 3). VERIFICADO con una GAP (ver Bloqueante).**
  - Estilo reutilizado = `line-through text-slate-400 dark:text-slate-500`, el mismo del nombre
    (spec Punto 1). Aplicado en: costo neto rama simple (`ServiceList.jsx:1160` aprox / td
    `mostrarCosto`), precio venta desktop (td con className condicional), venta mobile (span),
    costo mobile rama simple (`:1810`). El precio de venta pierde el `font-bold text-slate-900`
    en anulado, como pide la spec.

- **Etiqueta "Con multa"/"✓ Multa cobrada" (foco 4). VERIFICADO.**
  - `CancellationPenaltyLabel.jsx`: formato `inline-flex items-center gap-1 text-[10px]
    font-semibold`; Pending → puntito `bg-amber-400` + texto `text-amber-700 dark:text-amber-400`
    "Con multa"; Collected → puntito `bg-slate-400` + `text-slate-500 dark:text-slate-400`
    "✓ Multa cobrada"; null/otro → `null`. Coincide EXACTO con el badge hermano
    `OperadorPagoStatusBadge.jsx` (mismo patrón puntito+texto, sin recuadro) y con spec Punto 4.
  - **Posición**: se renderiza en la misma celda de la columna Estado (`ServiceList.jsx:1345`,
    `flex flex-col items-start gap-1.5`), DEBAJO del badge "Anulado" (`:1348`), gateada por
    `esServicioAnulado(svc)` (`:1389`). El badge de operador se apaga en anulado
    (`!esServicioAnulado(svc)`, `:1373` desktop / `:1780` mobile). Coincide con spec.
  - **Estados**: contrato backend confirmado — el proyector `StampCancellationPenaltyPerService`
    (`ReservaService.cs:5308-5311`) emite SOLO `"Collected"`/`"Pending"`; el front maneja
    exactamente esos dos + null. Sin valor huérfano.

- **P2 — anulada sin plata → sin chip de Pago (foco 5). VERIFICADO.**
  - `getMoneyStatus` devuelve `kind:"none"` para anulada limpia; `ReservaStatusChips.jsx:83-122`
    solo asigna `chipPago` para kinds conocidos, y `:147` lo renderiza solo `if (chipPago)`.
    Nunca "Sin movimientos" en anulada. Tests lo fijan (`moneyStatus.test.mjs:171-193`).

- **Null-tolerance de los datos nuevos (foco 6). VERIFICADO.**
  - `cancellationPenaltyState` null/ausente → etiqueta no se muestra (degradación silenciosa).
  - `invoicingStatus` desconocido → chip cae a "Sin facturar" (fallback), pero `FullyReturned`
    está contemplado antes del fallback en ambas pantallas.
  - `isReservaAnulada(null/undefined)` → `false` sin reventar (test `:158`).

---

## Blocking issues

**B1 — Tachado de importes NO se aplica en la rama de costo confirmable (desvío de spec Punto 1).**
`ServiceList.jsx:1401` (desktop, `CostConfirmCell`) y `:1801` (mobile, `CostConfirmCellMobile`)
renderizan el costo cuando `mostrarCosto && puedeConfirmarCosto && !isGeneric`.
`puedeConfirmarCosto = isCatalogFindOrCreateEnabled && mostrarCosto` (`:1036`) — **no está
gateado por `esServicioAnulado`**. Resultado: un usuario con permiso de costos, con el flag de
catálogo ON, mirando un servicio anulado de tipo específico (hotel/aéreo/traslado/paquete/
asistencia), ve el **costo neto SIN tachar**. La spec Punto 1 exige la celda Costo neto tachada
para el anulado, "en escritorio y en la versión mobile", sin excepción. El tachado solo se
aplicó a la rama simple (flag OFF / genérico / sin permiso).
- Por qué importa: regla del proyecto — desvío de mockup firmado se realinea, no se deja pasar.
  Además el importe sin tachar contradice el mensaje "esto es historia, no un dato vivo".
- Fix sugerido: llevar el mismo `esServicioAnulado(svc) ? 'line-through text-slate-400
  dark:text-slate-500' : ...` a la celda que envuelve `CostConfirmCell`/`CostConfirmCellMobile`
  (o tachar dentro de esos componentes cuando el servicio está anulado). Ver también D2.

---

## Non-blocking improvements

**M1 — Comparador suelto que saltea `isVoided` (mobile de la cuenta del cliente).**
`CustomerAccountPage.jsx:888` sigue llamando `isReservaAnulada(reserva.status)` mientras la fila
desktop equivalente (`:836`) ya pasa `reserva`. Hoy NO produce bug visible porque el DTO de la
cuenta corriente (`CustomerAccountReservaListItemDto`) todavía no manda `isVoided` y ambos caen
al fallback por string. Pero es la unificación a medias: si ese DTO suma `isVoided`, el mobile lo
ignoraría y el desktop no. Alinear a `isReservaAnulada(reserva)`.

**M2 — Chip "Facturada y devuelta" del Estado de Cuenta sin `data-testid`.**
`EstadoCuentaResumen.jsx:488` (y el resto de `ChipInvoicingStatus`) no exponen selector estable;
la automatización de esa pantalla depende del texto. `ReservaStatusChips` sí lo tiene. No es
regresión (ya era así), pero conviene sumarlo cuando se toque.

**M3 — Réplica de test da falsa confianza sobre el tachado del costo.**
`t4TachadoYMultaPorServicio.test.mjs:34` `claseCostoNeto` modela SOLO la rama simple
(`esServicioAnulado ? TACHADO : 'text-slate-500'`) y afirma "servicio anulado queda tachado".
No modela la rama `CostConfirmCell` (que es justo donde NO se tacha, B1). El test pasa en verde
mientras el componente real deja un caso sin tachar. Al arreglar B1, agregar la cobertura de esa
rama.

---

## UX / accessibility risks

- `CancellationPenaltyLabel`: `<span>` semántico, puntito decorativo con `aria-hidden`, `title`
  como tooltip, texto legible. Sin elemento interactivo (es informativo, correcto). El `✓` es
  carácter literal dentro del texto (el lector de pantalla lo lee como "marca de verificación
  Multa cobrada") — aceptable y consistente con los chips ✓ ya existentes. OK.
- Contraste `bg-slate-100`/`text-slate-600` del chip nuevo: suficiente. OK.
- No hay estados de foco/teclado afectados (nada interactivo nuevo). OK.

---

## Automation risks

- Bien: `data-testid="label-servicio-con-multa"` y `label-servicio-multa-cobrada` en la etiqueta
  nueva; `chip-factura-FullyReturned` en la ficha. Estados observables sin sleeps.
- Menor (M2): el chip equivalente en el Estado de Cuenta no tiene testid.
- Sin selectores frágiles introducidos; sin dependencias de tiempo.

---

## Missing tests

- Los tests nuevos son réplicas de lógica pura (`.mjs`), no montan el componente — es el patrón
  ya establecido en el repo (`servicioAnuladoGuards.test.mjs`, etc.); aceptable. Fijan
  comportamientos de la spec (FullyReturned label + no-fallback, precedencia de `isVoided`,
  P2 kind "none", clases de tachado, estados de la etiqueta, gating badge↔etiqueta, sin aviso de
  próximo inicio en anulado). NO son triviales.
- Falta (ligado a B1/M3): test que cubra la rama de costo confirmable en anulado.
- Falta menor: aserción de las CLASES de color del chip FullyReturned (solo se testea el label).

---

## Domain concerns

- **DC1 (cross-check backend, no es bug del front): "Multa en revisión" vs lo que el proyector
  emite.** La spec Punto 4 dice que una multa "en revisión / en trámite (ND todavía sin emitir)"
  debe mostrarse igual como **"Con multa" ámbar** ("es un hecho: hay multa"). El proyector
  (`ReservaService.cs:5273`) filtra `PenaltyStatus == Confirmed`: si una multa aún NO está
  confirmada (en decisión), no se estampa ningún estado → la etiqueta no aparece. El caso
  "confirmada pero ND sin emitir / sin cobrar" SÍ queda cubierto (→ "Pending" → "Con multa"). El
  front tolera el null correctamente; la pregunta es de contrato/negocio: ¿"en revisión" =
  no-confirmada debería igual mostrar "Con multa"? Cruzar con el reviewer de backend/dominio.
- **DC2 (documentado en el propio backend, no bloqueante): correlación POR OPERADOR, no por ND.**
  `ReservaService.cs:5242-5249`: dos servicios del MISMO operador con multas en pasos distintos
  heredan el mismo estado. Es el nivel de detalle que ya expone el chip Pago; consistente, pero
  puede pintar "✓ Multa cobrada" en un servicio cuya ND puntual todavía no se cobró si otra del
  mismo operador sí. Conocido y aceptado por la spec ("mismo nivel de detalle").
- **DC3 (latente, fuera de alcance de T4): acción de confirmar costo sobre un anulado.**
  `CostConfirmCell`/`CostConfirmCellMobile` se renderizan sobre servicios anulados (mismo gating
  que B1). Además de no tacharse, podrían ofrecer confirmar/editar el costo de un servicio ya
  anulado. Preexistente, no lo introduce T4; anotar para revisar aparte.

---

## Suggested fixes (concretos)

1. **B1**: en `ServiceList.jsx`, aplicar el condicional de tachado a la celda que envuelve
   `CostConfirmCell` (`~:1403`) y `CostConfirmCellMobile` (`~:1801`), o tachar dentro de esos
   componentes cuando `esServicioAnulado(service)`. Reusar la constante
   `line-through text-slate-400 dark:text-slate-500`.
2. **M1**: `CustomerAccountPage.jsx:888` → `isReservaAnulada(reserva)`.
3. **M3**: extender `t4TachadoYMultaPorServicio.test.mjs` con la rama de costo confirmable.
4. **DC1**: confirmar con backend/dominio si "multa en revisión (no confirmada)" debe mapear a
   un token que el front pinte como "Con multa", o si la spec se satisface con solo Confirmed.

---

## No verificado

- **No se ejecutó la app real ni los tests** (`node --test`, build de Vite). Este review es por
  lectura de código + cruce de contrato; no confirma render end-to-end ni que la suite pase.
- **No se verificó el valor real del flag `isCatalogFindOrCreateEnabled` en prod**: el impacto de
  B1 depende de que ese flag esté ON. Si estuviera OFF en todos lados, B1 no sería visible hoy
  (pero seguiría siendo un desvío de la spec en el código).
- **No se revisó el backend de Tandas 3-4** más allá de los nombres de campo consumidos por el
  front (`IsVoided`, `CancellationPenaltyState`, `FullyReturned`) y la salida del proyector de
  multa por servicio. La corrección fiscal/dominio de esos cálculos queda para sus reviewers.
- **No se auditó `docs/ux/guia-ux-gaston.md` línea por línea**: se confió en las citas de regla
  que la spec T4 referencia (reglas 4/5/6/7 del modelo, patrones 2026-06-13/06-21/07-05/07-16).

---

## Re-review (2026-07-17, post-fix) — VEREDICTO: APROBADO

Se re-verificó por lectura del código actual los tres puntos pedidos.

- **(a) Tachado cubre TODAS las ramas de la celda de costo en anulados, desktop y mobile — SÍ.**
  Desktop: la rama confirmable (`ServiceList.jsx:1410-1412`) ahora envuelve `CostConfirmCell` en
  un `<td>` con `esServicioAnulado(svc) ? 'line-through text-slate-400 dark:text-slate-500' : ''`;
  la rama simple ya lo tenía. Mobile: la rama confirmable (`:1817`) envuelve `CostConfirmCellMobile`
  en un `<span>` con el mismo condicional; la rama simple (`:1824+`) ya lo tenía. Costo neto y
  precio venta quedan tachados en las dos vistas y en todas las ramas. Cerrado B1.

- **(b) Respetar la decisión del dueño sobre confirm-cost NO deja desvío de spec — COINCIDO.**
  La spec Punto 1 pide "importes tachados" (tratamiento visual = historia), no prohíbe confirmar
  costo sobre un anulado. El fix tacha el envoltorio (la línea atraviesa el contenido de
  `CostConfirmCell`) sin tocar `CostConfirmCell.jsx`, que mantiene la decisión documentada del
  dueño ("confirm-cost se permite aunque el servicio esté cancelado"). Cumple la spec visual y
  respeta la regla de negocio: son ejes distintos. Sin conflicto. (DC3 del review original —
  permitir la acción sobre un anulado— sigue siendo decisión explícita del dueño, no un hallazgo.)

- **(c) Fix de M1 — APLICADO.** `CustomerAccountPage.jsx:891` ahora llama
  `isReservaAnulada(reserva)` (mobile), alineado con la fila desktop (`:836`); ya no saltea
  `reserva.isVoided` si el DTO de la cuenta lo suma. Cerrado M1.

- **M3 (test de la rama compleja)**: el coordinador informa que la réplica de test se extendió;
  **no re-verificado por lectura en este pase** (no se pidió y no se corrió la suite). No bloquea.

**Veredicto re-review: APROBADO.** No quedan bloqueantes. Persisten, no bloqueantes: M2 (chip del
Estado de Cuenta sin `data-testid`) y DC1 (cruce con backend sobre "multa en revisión" no
confirmada → la etiqueta no aparece; el front tolera el null correctamente). No se ejecutó la app
ni la suite: verificación por lectura de código.
