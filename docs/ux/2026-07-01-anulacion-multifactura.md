# Anular una reserva con VARIAS facturas en distintas monedas — SPEC FINAL

> **Estado: FINAL, APROBADA por Gastón (2026-07-01).** Las 7 preguntas se respondieron con la opción
> recomendada (P1-A … P7-A), más una precisión de negocio sobre la moneda del saldo a favor.
> frontend-senior implementa esto al pie de la letra; cualquier desvío por costo técnico o regla de
> negocio se le repregunta a Gastón ANTES, nunca se decide solo.
>
> **Fuente única:** `docs/ux/guia-ux-gaston.md` (sección "Anular una reserva con VARIAS facturas en
> distintas monedas, 2026-07-01"). Componente real: `src/TravelWeb/src/features/cancellations/components/CancelarReservaInline.jsx` + su copy en `cancelarReservaCopy.js`.

---

## 1. Qué cambia (contexto funcional, ya decidido)

- Se **elimina** el bloqueo actual: hoy, con más de una factura emitida, el panel muestra un cartel rojo que frena y manda a una "**solapa Facturas**" **que NO existe**. Eso muere.
- Al confirmar, el backend emite **una nota de crédito por cada factura, cada una en su moneda** (ej. una NC en $ y otra en US$). Es asincrónico contra AFIP/ARCA; tarda unos segundos por cada una.
- **Todo o nada a nivel ESTADO:** la reserva queda **Anulada** solo cuando **todas** las notas salieron bien. Si una sale y otra falla, la reserva queda **"En revisión"** — la nota que salió **NO se revierte** — y hay que **reintentar** las que faltan. El reintento es **idempotente**: no re-emite la que ya salió ni duplica.

Este flujo aplica **solo cuando hay 2+ facturas**. Con una sola factura sigue vigente el Caso 4 de la sección 2026-06-25 (cartel ámbar simple, sin progreso por nota, sin "en revisión").

---

## 2. Reglas de la guía que se aplican tal cual (no reinventar)

1. **Panel EN LÍNEA**, nunca ventana flotante (ADR-035 C). Única excepción: el cartel de confirmación "¿Seguro?" de P2 (patrón "¿seguro?" antes de algo irreversible, igual que H2).
2. **Motivo obligatorio, mínimo 10 caracteres** (ya existe en el componente).
3. **Multimoneda dura** (2026-06-09): monedas SIEMPRE separadas, **NUNCA sumadas ni convertidas**; formato `$ 150.000 · US$ 200`; nunca la palabra "diferencia de cambio"; si la reserva es de una sola moneda, se ve como una reserva normal.
4. **Nada de internos ni jerga** (gate data-exposure): prohibido mostrar códigos internos (INV-100), IDs/GUID, nombres de campo, ni el texto crudo del error del backend. Los montos, por moneda. **El motivo que devuelve AFIP SÍ se muestra tal cual** (es info útil para el vendedor, ya aprobado en H2, NO es un interno técnico del sistema).
5. **Molde asíncrono de H2** (2026-06-24): PROCESANDO (spinner + criollo, en el mismo lugar) → ÉXITO (verde, se transforma solo sin refrescar consultando el estado del backend) → si falla, cartel con el motivo de AFIP + reintentar; **nunca se pierde lo cargado** (Ronda 2, 2026-06-06).

---

## 3. Flujo y los 6 estados (con copy EXACTO)

### Estado 0 — Aviso previo (P1=A) · cartel ÁMBAR
El panel de anular, cuando la reserva tiene 2+ facturas. Reemplaza el cartel de freno.

**Copy del cartel** (N = cantidad de facturas):
> Esta reserva tiene **N facturas emitidas** (una en $ y una en US$). Al anular se emite **una nota de crédito por cada factura, cada una en su moneda**.

Debajo, la **lista de facturas** (una fila por factura, monto en su moneda, nunca sumados):
- `Factura B 0001-00012345 — $ 150.000`
- `Factura B 0001-00012346 — US$ 200`

> Nota: el texto "(una en $ y una en US$)" se arma dinámicamente con las monedas reales. Si las N facturas son todas de la misma moneda, decir "(N facturas en US$)" o el equivalente, sin listar monedas que no hay.

```
┌─ Anular reserva ─────────────────── #R-1042 — Fam. García ── [x] ─┐
│                                                                   │
│  ⚠  Esta reserva tiene 2 facturas emitidas (una en $ y una en    │
│     US$). Al anular se emite una nota de crédito por cada         │
│     factura, cada una en su moneda.                              │
│                                                                   │
│        · Factura B 0001-00012345 ....... $ 150.000               │
│        · Factura B 0001-00012346 ....... US$ 200                 │
│                                                                   │
│  Motivo de la anulación *                                        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                                                           │    │
│  └─────────────────────────────────────────────────────────┘    │
│                          [ Volver ]   [ Anular reserva ]         │
└───────────────────────────────────────────────────────────────────┘
```
- Botón "Anular reserva" apagado hasta que el motivo tenga ≥ 10 caracteres (mensaje "Mínimo 10 caracteres", ya existe).

---

### Estado 1 — Confirmación "¿Seguro?" (P2=A) · cartel de confirmación
Al apretar "Anular reserva" con motivo válido, ANTES de mandar nada a AFIP.

**Copy** (N = cantidad de notas = cantidad de facturas):
> **¿Seguro?** Se van a emitir **N notas de crédito en AFIP** (una en $ y una en US$). Una vez emitidas **no se pueden deshacer**.

```
┌─ ¿Seguro? ─────────────────────────────────────────────────┐
│  Se van a emitir 2 notas de crédito en AFIP (una en $ y     │
│  una en US$). Una vez emitidas no se pueden deshacer.       │
│                                                             │
│                             [ Volver ]   [ Sí, anular ]     │
└─────────────────────────────────────────────────────────────┘
```
- "Volver" → cierra el "¿Seguro?" y vuelve al panel con todo cargado intacto.
- "Sí, anular" → dispara la anulación y pasa al Estado 2.

---

### Estado 2 — Procesando, avance por nota (P3=A) · PROCESANDO
Apenas se confirma. En el mismo lugar (no cierra el panel de golpe, no deja un toast suelto).

**Copy del encabezado:**
> Estamos emitiendo las notas de crédito en AFIP. En unos instantes vas a ver el resultado.

**Lista de avance** (una fila por nota; se auto-actualiza sin refrescar, consultando el estado del backend):
- `✔  Nota de crédito en $ — emitida`
- `⏳  Nota de crédito en US$ — emitiendo…`

**Contador:** `1 de 2`

```
┌─ Anulando la reserva… ─────────────────────────────────────┐
│  Estamos emitiendo las notas de crédito en AFIP.           │
│  En unos instantes vas a ver el resultado.                 │
│                                                            │
│     ✔  Nota de crédito en $ ............ emitida           │
│     ⏳  Nota de crédito en US$ .......... emitiendo…        │
│                                                            │
│                                    (1 de 2)                │
└────────────────────────────────────────────────────────────┘
```
- Durante este estado, los botones de acción quedan bloqueados (no se puede cerrar con otra acción encima). El front consulta el estado del backend (mismo patrón que `GET /invoices/reserva/{id}/fiscal-status` de H2).

---

### Estado 3 — Éxito total (P4=A) · cartel VERDE
Cuando TODAS las notas salieron bien. La reserva pasa a **Anulada** (solo lectura, servicios "Anulado" — ADR-036 puntos 3 y 8).

**Copy** (N = cantidad de notas):
> ✔ **Reserva anulada.** Se emitieron **N notas de crédito** (una en $ y una en US$).
> *(si el cliente había pagado)* **Lo cobrado quedó como saldo a favor del cliente: US$ 200.**

**REGLA CLAVE del saldo a favor (decisión de negocio 2026-07-01):** el saldo a favor queda en la **moneda de la FACTURA anulada**, NO en la moneda de lo que el cliente pagó. Ejemplo real: factura en USD que el cliente pagó en pesos → el saldo a favor queda en **US$**. El front muestra el saldo a favor **por moneda, tal como lo devuelve el backend** (separado, nunca sumado). Si hay saldo en dos monedas: `$ 150.000 · US$ 200`. **No se explica ninguna ley en pantalla**; solo se muestra el saldo a favor por moneda.

```
┌────────────────────────────────────────────────────────────┐
│  ✔  Reserva anulada.                                        │
│     Se emitieron 2 notas de crédito (una en $ y una en US$).│
│     Lo cobrado quedó como saldo a favor del cliente: US$ 200│
└────────────────────────────────────────────────────────────┘
```
- La línea de saldo a favor **solo aparece si hubo cobros** que se vuelven saldo a favor. Si no hubo, no se muestra esa línea.

---

### Estado 4 — Falla parcial (P5=A) · cartel NARANJA "En revisión"
Una nota salió y otra no. La reserva NO queda anulada del todo: queda **"En revisión"**. La nota que ya salió **NO se deshace**.

**Copy del encabezado** (adaptar singular/plural según cuántas salieron/fallaron):
> ⚠ La reserva quedó **EN REVISIÓN**: una nota de crédito salió bien y la otra no. **La que salió no se deshace.**

**Lista con el detalle** (cuál salió, cuál no; el motivo de AFIP tal cual debajo de la que falló):
- `✔  Nota de crédito en $ — emitida`
- `✗  Nota de crédito en US$ — no salió`
  - `Motivo de AFIP: «CUIT del emisor sin habilitación»`

**Botón:** `[ Reintentar la que falta ]` (idempotente: reintenta SOLO la que faltó, no re-emite la que ya salió).

```
┌─ Anulación en revisión ────────────────────────────────────┐
│  ⚠ La reserva quedó EN REVISIÓN: una nota de crédito salió  │
│    bien y la otra no. La que salió no se deshace.          │
│                                                            │
│     ✔  Nota de crédito en $ ............ emitida           │
│     ✗  Nota de crédito en US$ ........... no salió         │
│           Motivo de AFIP: «CUIT del emisor sin habilitación»│
│                                                            │
│                              [ Reintentar la que falta ]   │
└────────────────────────────────────────────────────────────┘
```
- Al tocar "Reintentar la que falta", vuelve al Estado 2 (procesando) pero solo con las notas pendientes. Si esta vez salen todas → Estado 3 (éxito total). Si vuelve a fallar → se queda en Estado 4.

---

### Estado 5 — Al volver a entrar en "En revisión" (P6=A) · franja NARANJA arriba de la reserva
Si el vendedor cierra y vuelve a abrir la reserva mientras la anulación quedó a medias, tiene que cantar apenas se abre.

**Copy de la franja** (N = notas que faltan):
> 🟠 **En revisión — anulación a medias, falta emitir N nota(s) de crédito.**

**Botón:** `[ Reintentar anulación ]`

```
┌─ Reserva #R-1042 ──────────────────────────────────────────┐
│  🟠 EN REVISIÓN · Anulación a medias — falta emitir 1 nota │
│     de crédito.                     [ Reintentar anulación ] │
│  ──────────────────────────────────────────────────────── │
│  (la reserva queda de SOLO LECTURA hasta completar la      │
│   anulación; el único botón activo es "Reintentar anulación")│
└────────────────────────────────────────────────────────────┘
```
- La reserva queda de **solo lectura** salvo ese botón (coherente con la franja del candado 2026-06-08 y el chip "En corrección" de "Sacar de viaje" 2026-06-22).
- "Reintentar anulación" abre el flujo de reintento (Estado 2 con las pendientes).

---

## 4. Permiso (P7=A)
- **Reintentar la anulación a medias lo puede hacer cualquier vendedor que ya podía anular esa reserva.** No se restringe a admin: reintentar no es deshacer nada, es completar lo que ya empezó.

---

## 5. Checklist de estados para frontend-senior (frontend-standars)

- **Vacío / normal:** panel de anular con el aviso del Estado 0 según cantidad de facturas y monedas (dato del backend).
- **Cargando (previo):** al abrir, se consulta la lista de facturas (moneda + monto) del backend.
- **Validación:** motivo < 10 caracteres → botón apagado + "Mínimo 10 caracteres" (ya existe).
- **Confirmación:** "¿Seguro?" (Estado 1) antes de mandar nada.
- **Procesando:** Estado 2, avance por nota, auto-actualiza sin refrescar consultando el backend.
- **Éxito total:** Estado 3, reserva → Anulada (solo lectura, servicios "Anulado").
- **Falla parcial → En revisión:** Estados 4 y 5, botón reintentar idempotente.
- **Error de red al confirmar (antes de emitir nada):** cartel rojo "No se pudo iniciar la anulación. Probá de nuevo…", panel intacto (Ronda 2). NO deja la reserva "en revisión" si no llegó a emitir nada.
- **Permiso denegado:** mensaje claro, sin exponer internos.

## 6. Qué NO hacer

- NO nombrar la "solapa Facturas" (no existe) en ningún mensaje.
- NO mostrar códigos internos (INV-100), IDs, GUID, nombres de campo, ni el texto crudo del error del backend. (El motivo de AFIP SÍ se muestra.)
- NO sumar $ + US$ en un solo número, NUNCA.
- NO poner la moneda del saldo a favor según lo que el cliente pagó: va en la **moneda de la factura anulada**.
- NO revertir en silencio la nota que ya salió si la otra falla (es todo-o-nada a nivel ESTADO, pero la NC emitida es un hecho fiscal que queda).
- NO re-emitir en el reintento la nota que ya salió (idempotencia la garantiza el backend; el front solo pide reintentar).

## 7. Dependencia técnica (NO es UX — para backend/frontend)

- El DTO debe exponer la **lista de facturas con su moneda y monto** (para el aviso de P1).
- El backend debe exponer el **estado de progreso/resultado de cada nota de crédito** (procesando / emitida / rechazada + motivo de AFIP) que el front consulta para pintar PROCESANDO / ÉXITO / FALLA PARCIAL (mismo patrón que `GET /invoices/reserva/{id}/fiscal-status` de H2).
- El backend debe exponer el **saldo a favor resultante por moneda** (moneda de la factura anulada).
- El **reintento debe ser idempotente** en el backend.

## 8. Addendum 2026-07-02 — desvío aprobado por Gastón (orden del aviso)

El aviso con la lista de facturas (Estado 0) NO puede mostrarse antes de escribir el motivo: la lista la devuelve el backend al crear el borrador, y el borrador exige motivo válido. Gastón aprobó (2026-07-02) que el aviso aparezca DESPUÉS del click en "Anular reserva", fusionado en un solo cartel con la confirmación "¿Seguro?" (Estado 1): lista de facturas + texto de confirmación + [Volver] [Sí, anular]. Mismos textos exactos, misma cantidad de clicks. Si toca [Volver], el motivo escrito no se pierde. Con UNA sola factura no cambia nada respecto de hoy.
