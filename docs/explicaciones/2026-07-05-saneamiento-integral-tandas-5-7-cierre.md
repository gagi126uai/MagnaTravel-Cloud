# 2026-07-05 — Saneamiento integral COMPLETO: tandas 5 a 7 y cierre

Continuación de `2026-07-05-saneamiento-integral-tandas-1-4.md`. Con esta
sesión, el plan de 7 tandas quedó completo y deployado.

## Tanda 5 (`ab2f1ec`) — Avisos que se apagan solos

Los avisos de la campanita ahora conocen su causa. Cuando la causa se
resuelve (pagaste, la anulación salió al reintentar, el vigía reparó algo),
el aviso muere solo. Además:
- "Visto" es una sola cosa: cerrar el banner rojo también lo saca de la
  campanita y viceversa (antes eran dos marcas separadas).
- El aviso de "error al anular" desaparece cuando el reintento triunfa
  (antes convivían "error" y "éxito" para siempre). Los viejos que habían
  quedado en ese estado se limpiaron con la migración.
- Los avisos de AFIP llegan al momento, sin recargar la página.
- Los avisos diarios ya no se acumulan uno por noche: mientras el aviso
  siga vivo no se re-crea; solo vuelve si lo atendiste y la causa persiste.
- El vigía nocturno ganó su chequeo W4: avisos zombies (causa muerta) se
  auto-resuelven cada noche como red de seguridad.
- Bonus del gate de exposición: la API mandaba al navegador la ficha
  interna completa de cada notificación; ahora viaja solo lo que se ve
  (id, mensaje, tipo, prioridad, fecha).

## Tanda 6 (`578b346`) — Un solo cálculo de plata en las pantallas

- **Se encontró y arregló la causa raíz de tu factura en $0**: al editar
  un servicio confirmado (hotel/aéreo/traslado/paquete/asistencia), si el
  formulario no mandaba el estado, el sistema lo devolvía a "Solicitado"
  en silencio → el servicio se des-confirmaba → la factura no lo
  precargaba. Ahora editar sin tocar el estado conserva el estado.
- El formulario de factura además explica: si un servicio queda afuera te
  dice cuál y por qué ("todavía no está confirmado" / "está cancelado" /
  "tiene precio $0"), y si por eso el monto propuesto queda en $0, lo dice.
- La regla "¿debe plata?" ahora vive en UN solo lugar del frontend
  (moneyStatus.js) y la usan las 4 pantallas que antes la calculaban cada
  una a su manera. Una anulada nunca muestra "Debe": muestra "Saldo a
  favor", "Multa por anulación pendiente de cobro", o nada.
- "Recaudado" (franja) y "Cobrado" (solapa Cuenta) ahora son el mismo
  número (antes la franja sumaba pagos puente y podía diferir).
- Se eliminó un parche que DOBLABA los números por un instante después de
  guardar un servicio.
- La cuenta del cliente muestra cada reserva en su moneda real (las de
  dólares ya no aparecen en pesos) y el saldo a favor en verde.
- "Cerrar reserva" ahora exige saldo en cero EN CADA moneda (antes una
  deuda en dólares se podía tapar con saldo a favor en pesos).

## Tanda 7 (`540cb9b`) — Fin de la mamushka de carteles

Según el diseño aprobado (1C/2B/3A/4B/5A): lo que pide acción queda
siempre a la vista (el "Dar OK" grande; el candado en una línea con su
botón); lo informativo se pliega en "N avisos más [Ver]"; y nada se dice
dos veces ("Debe — no viaja" y "En corrección" quedan solo como etiquetas
del encabezado). La guía UX quedó actualizada con la regla global.

## Números del cierre

Backend: 3141/3141 tests unitarios. Frontend: 1765/1765 + build limpio.
Cada tanda pasó sus gates (backend/frontend + seguridad + exposición de
internos). Los gates encontraron y corrigieron 3 bloqueantes reales antes
de deploy (un texto técnico que iba a verse en pantalla, la entidad cruda
de notificaciones viajando al navegador, y un aviso fantasma post-anulación).

## Qué mirar en el dogfood (checklist para Gaston)

1. Abrir una reserva confirmada con cambios pendientes → el orden nuevo de
   carteles; el "Dar OK" a la vista; los informativos plegados.
2. Abrir una anulada VIEJA (pre 24/06) → sin deuda, servicios dados de baja
   por "Ajuste del sistema", plata con contexto (después de que el vigía
   haya corrido su primera noche).
3. Editar un servicio confirmado (sin tocar el estado) → sigue confirmado;
   emitir factura → precarga el precio (adiós $0 mudo).
4. Cerrar el banner rojo de un aviso urgente → también desaparece de la
   campanita.
5. La campanita: sin avisos duplicados acumulados; el aviso del vigía (si
   encontró algo) es UNO con resumen en criollo.

## Pendientes menores anotados (backlog, no urgentes)

- Job de NC parcial (corre cada 30 min): si leés el aviso y la causa
  persiste, puede re-crearlo a la media hora (antes 1/día). Se puede atar
  a resolución real si molesta.
- Contexto de plata de anuladas con pesos Y dólares mezclados usa el neto
  (caso raro; el vigía marca los sospechosos).
- Ver si la cuenta del cliente debería exigir "dueño del cliente" además
  del permiso general de clientes (pregunta de política, pre-existente).
- Anulada con multa apenas "estimada" y saldo positivo puede caer como
  "para revisar" en el vigía (ruido esperable, raro).
