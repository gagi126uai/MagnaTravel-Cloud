# Avisos que trabajan, no que gritan (19/08/2026) — explicado en fácil

Gastón vio en PROD un cartel naranja gigante que tapaba toda la pantalla:
"la devolución del operador de la reserva 2026-1045 no coincide con la
caja", con una notificación "URGENTE" al lado. No se entendía qué había que
hacer ni por qué, y el tono era de catástrofe. Lo mismo le hizo ruido el
cartel de la ficha anulada que pedía completar notas de crédito. Pidió
investigar cómo lo hacen los ERPs del rubro y proponer algo nuevo.
Commit: `e92740ad`.

## Lo que encontramos investigando

1. **El control que generaba el cartelón no tenía spec firmada** — nació de
   un gap técnico (16/08) y quedó solo en código.
2. **Los ERPs grandes (SAP, Odoo, NetSuite, Dynamics) nunca usan un cartel
   que tapa la app para esto.** Un descalce de caja es un ítem con estado en
   una pantalla de trabajo, que ve solo el rol que maneja la plata, con los
   dos números lado a lado y acciones para resolver. Y previenen la causa:
   un movimiento atado a otra cosa no se deja editar.
3. **El control además tenía un bug**: por cómo buscaba los movimientos de
   caja, veía siempre $0 y marcaba descalce en TODAS las devoluciones con
   plata. El cartelón que asustó a Gastón era casi seguro una falsa alarma.

## Las tres decisiones firmadas (multiple choice, 19/08)

1. **Campanita + ficha, chau banner**: el aviso pasa a ser normal (no
   urgente), solo para quien maneja tesorería, con el monto de la diferencia
   y navegación directa. El banner full-width queda reservado para caídas
   del sistema.
2. **Bloquear la causa raíz**: un movimiento de caja atado a una devolución
   del operador no se edita ni se borra desde Tesorería — el sistema te
   manda al circuito de la devolución, donde "Deshacer" deja todo coherente.
3. **Cartel que explica**: en la anulación a medias, lista por factura (cuál
   nota ya salió ✓, cuál no y qué respondió ARCA), botón "Emitir la nota que
   faltó" (antes "Reintentar anulación", que sonaba a re-anular todo), y si
   es solo demora de ARCA, un cartel azul tranquilo sin botón.

## Qué se ve distinto

- **Ya no aparece el banner naranja** por descalces de devolución. El aviso
  llega a la campanita, con la diferencia en plata, y al tocarlo te lleva a
  la solapa Reembolsos del operador (las notificaciones ahora pueden navegar
  — también desde "Ver todas").
- **En la solapa Reembolsos**, la devolución descalzada tiene un chip ámbar
  "No coincide con la caja" con "Figura recibido / En la caja / Diferencia" y
  links para revisar.
- **En Caja**, los movimientos que son devoluciones de operador tienen
  Editar y Anular apagados, con el motivo al pasar el mouse (escrito en
  celular).
- **En la ficha de una anulación a medias**, el cartel lista factura por
  factura con el motivo textual de ARCA y el botón nuevo. Si la nota está
  solo demorada, aparece un cartel azul "se está terminando de emitir, no
  hace falta que hagas nada" (antes no aparecía nada).

## Qué no se ve pero cambió

- **El bug de los falsos positivos quedó arreglado** con un test de
  regresión que reproduce la forma real en que producción escribe la caja.
  Ojo: si existía alguna divergencia REAL silenciada por el bug, ahora sí va
  a aparecer (correcto, pero vigilarlo el primer día).
- El aviso lleva **la variante sin montos** a quien no tiene permiso de ver
  costos (F-14), la audiencia se resuelve con el mecanismo real de permisos
  (excluye usuarios desactivados), y un aviso descartado ya no se recrea
  cada día.
- El freno de Tesorería vive en el motor (no solo botones apagados), con
  código estable y mensaje en criollo.

## Reviews

Backend y frontend aprobados; seguridad bloqueó con dos hallazgos reales
(montos de costo en el aviso sin chequear el permiso de ver costos, y
usuarios desactivados en la audiencia) — corregidos y re-aprobados con
tests. Data-exposure aprobado. 3823 tests frontend + unit backend verdes.

## Deuda anotada

- Un rol solo-Tesorería (sin permiso de ver operadores) recibiría un link
  que da 403 al hacer click (falla cerrado, no filtra nada) — anotado en el
  código hasta definir la política de permisos.
- El deep-link "Ver el movimiento de caja" lleva al listado general de Caja
  (sin resaltar la fila) — mejora futura opcional de la spec.
