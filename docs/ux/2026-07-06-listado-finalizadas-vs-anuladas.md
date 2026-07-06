# Cambio 1 — Listado de reservas: diferenciar FINALIZADA de ANULADA + filtro para anuladas

> Estado: **APROBADO por Gastón (2026-07-05)** — respondió P1-A, P2-A, P3-A (todas la recomendada).
> Sin preguntas abiertas. Listo para que frontend-senior lo implemente tal cual.
> Reglas en la guía: sección "Listado de reservas: pestaña Anuladas aparte + buscador global (2026-07-05)".
> Pedido textual de Gastón (2026-07-05): "hoy la vista las mezcla".

## Hallazgo (verificado en el código, no es opinión)

- La pestaña **"Finalizadas"** del listado, por dentro, trae **dos estados juntos**: las
  Finalizadas (Closed) **y** las Anuladas (Cancelled). Están mezcladas en la misma lista
  (`ReservaService.ApplyReservaView`, rama `"closed"`: `Status == Closed || Status == Cancelled`).
  Eso es exactamente lo que molesta a Gastón.
- Las reservas **"Esperando el reembolso del operador"** (PendingOperatorRefund — una anulada con
  la multa del operador sin decidir) **hoy no caen en NINGUNA pestaña**: quedan invisibles en el
  listado. Es un agujero aparte que conviene tapar en el mismo cambio.

## Qué dice la guía que aplica

- **El badge ya las pinta distinto** (`ReservaStatusBadge`): **Finalizada** = gris con ✅;
  **Anulada** = rojo/rosa con 🚫; **Esperando reembolso** = rosa con ⏳. Osea, dentro de una fila
  ya se distinguen. Estos colores son preexistentes; los colores que Gastón SÍ decidió fueron los
  de las etapas nuevas (Cotización gris claro, En gestión celeste, Perdido gris oscuro/tachado —
  ciclo de vida decisión #10, 2026-06-08) y dejó dicho que "las etapas que ya existían no cambian
  de color". Finalizada/Anulada entran en "las que ya existían".
- **Vocabulario duro (ADR-036 punto 1, 2026-06-21):** al usuario NUNCA se le dice "Cancelada"
  para deshacer el viaje; se dice **"Anulada"**. Cualquier pestaña/rótulo nuevo usa "Anuladas".
- **"A liquidar" no existe** (ADR-036 punto 0): no se agrega ningún estado nuevo, solo se separa
  lo que ya está.
- **Patrón de filtros del listado:** el listado ya trabaja con **pestañas por estado** (Cotizaciones,
  Presupuestos, En gestión, Confirmadas, En viaje, Finalizadas, Perdidas, Archivadas), cada una con
  su contador. Es el patrón canónico de esta pantalla (auditar patrones antes de crear UI nueva).

## Decisiones tomadas (Gastón, 2026-07-05)

- **P1-A:** las Anuladas salen a su **pestaña propia "Anuladas"** (misma mecánica que el resto de las
  pestañas). "Finalizadas" queda limpia (solo Closed).
- **P2-A:** la pestaña "Anuladas" **incluye también las "Esperando el reembolso del operador"**
  (PendingOperatorRefund). Su contador las suma.
- **P3-A:** **con el badge alcanza** para distinguir; NO se atenúa ni se tacha la fila de la anulada.

## Diseño final aprobado

```
ANTES (mezcladas):
┌──────────────────────────────────────────────────────────────────┐
│ Cotiz. │ Presup. │ En gestión │ Confirmadas │ En viaje │ Finalizadas(12) │ Perdidas │ Archiv. │
└──────────────────────────────────────────────────────────────────┘
  Finalizadas(12) = 7 finalizadas de verdad + 5 anuladas ← el problema

DESPUÉS (propuesta P1-A):
┌──────────────────────────────────────────────────────────────────────────────┐
│ … │ En viaje │ Finalizadas(7) │ Anuladas(5) │ Perdidas │ Archivadas │
└──────────────────────────────────────────────────────────────────────────────┘
  Finalizadas(7) = solo Closed
  Anuladas(5)    = Cancelled + Esperando reembolso
```

Fila de una anulada dentro de su pestaña (el badge ya alcanza para distinguir; P3 define si además
se atenúa la fila):

```
┌────────────┬───────────────────┬──────────────┬──────────┬───────────┐
│ #R-1042    │ Fam. García       │  🚫 Anulada  │ 12/06/26 │  A favor  │
│ Cancún     │ 3 pax             │              │          │  $150.000 │
└────────────┴───────────────────┴──────────────┴──────────┴───────────┘
```

## Estados de la pantalla (no cambian respecto de hoy)

- **Cargando:** el skeleton del listado que ya existe.
- **Vacío:** "No se encontraron reservas" (el que ya está), por pestaña.
- **Error / base caída:** el `DatabaseUnavailableState` que ya existe.

## Qué NO hay que hacer

- NO inventar colores nuevos para Finalizada/Anulada (Gastón: las etapas viejas no cambian de color).
- NO usar la palabra "Cancelada" en la pestaña ni en ningún rótulo (va "Anuladas").
- NO construir la separación hasta que Gastón elija P1 (podría querer toggle en vez de pestaña).

## Dependencia técnica (no es decisión de UX)

- Hay que agregar en el backend una rama de vista **`"cancelled"`** (Cancelled + PendingOperatorRefund)
  y sacar Cancelled de la rama `"closed"`, más su contador (`cancelledCount`) en el summary. Hoy no
  existen. Solo habilita la pantalla; no cambia ninguna decisión de UX.
