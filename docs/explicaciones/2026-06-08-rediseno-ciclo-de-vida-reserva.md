# Rediseño completo del ciclo de vida de la reserva (2026-06-08)

## Por qué lo hicimos
Gastón venía sufriendo los estados de la reserva ("siento que está todo mal"). El disparador
concreto: al querer borrar un servicio de una reserva *Vendida* el sistema tiraba
*"No se puede eliminar el servicio: la reserva está en estado 'Sold'... cancelá ese servicio
con el proveedor primero..."*. Era el síntoma de un modelo de estados a medio camino (el viejo
ciclo con la llave `EnableSoldToSettleStates` conviviendo con parches). Decisión: rehacerlo
completo y matar el viejo, **sin llave** (regla nueva de Gastón: "basta de llaves, esto es un
producto").

## El ciclo nuevo (lo que ve y usa Gastón)
Cotización → [Pasar a presupuesto] → Presupuesto → [El cliente aceptó] → En gestión →
**Confirmada (automática)** → En viaje → Finalizada. Más: **Perdido** (cotiz/presup que no
compró), **Cancelada** (cualquier etapa), **A liquidar** (desvío manual desde En viaje).

- Toda reserva **nace como Cotización**. Ya no se puede crear Confirmada directo.
- **Confirmada es automática**: el sistema la confirma sola cuando *todos* los servicios están
  resueltos (aéreo = ticket emitido; hotel/paquete = confirmado por el operador; asistencia =
  voucher; traslado = confirmado o marcado "no requiere confirmación"). Si el operador cancela/
  reprograma o se agrega un servicio nuevo, **vuelve sola a En gestión** + aviso.
- **La deuda del cliente nace por servicio confirmado.** Un servicio solo solicitado ya no
  genera deuda (era un bug). Se ven dos números: *Saldo a cobrar* (lo confirmado) y, chiquito,
  lo *presupuestado*.
- **Candado**: desde Confirmada en adelante la reserva queda trabada; solo un admin la destraba
  por 30 minutos con un motivo, y cada cambio queda registrado. Cobrar y facturar nunca se traban.
- **Borrar vs cancelar un servicio**: si el operador no lo confirmó, se borra; si lo confirmó,
  se cancela (queda tachado). Adiós a la pared del error.
- "Vendida" desapareció.

## Las 10 decisiones de pantalla
Gastón aprobó (2026-06-08) las 10 decisiones de UI propuestas (candado visual, flujo de
autorización, botones "Marcar emitido"/"No requiere confirmación", vista "N de M resueltos",
dos números de plata, franja de regresión, botón Perdido, papelera que decide borrar/cancelar,
colores nuevos). Quedaron escritas en `docs/ux/guia-ux-gaston.md`.

## Cómo se construyó
software-architect (ADR-020) → architect-reviewer (2 rounds, Ready) → backend (fases F1-F6) →
frontend → backend-reviewer + frontend-reviewer + security-data-risk-reviewer → ronda de fixes
→ re-review de seguridad del delta (Approved) → commit `4ef7332` a main.

Fixes destacados de la revisión: un aéreo emitido y luego cancelado seguía contando como deuda
(corregido en vivo y en la migración); la papelera no ofrecía "Cancelar" (callejón sin salida,
resuelto); la franja naranja de regresión no se mostraba (cableada); permisos de propietario
faltantes en endpoints de estado; auditoría de la cancelación del operador.

Tests: backend 1230 verde, frontend 372 verde.

## Documentos
- ADR técnico: `docs/architecture/adr/ADR-020-ciclo-de-vida-reserva.md`
- Decisiones de producto/UI: `docs/ux/guia-ux-gaston.md` (secciones del ciclo de vida).

## Pendiente
- **Deploy (manual de Gastón en el VPS):** backup + ver el diff de saldos (incluyendo reservas
  Finalizadas) + orden: parar la app → migrar (M1 → M2 → M3) → levantar la versión nueva.
  La migración borra la llave vieja, así que la app vieja no puede seguir corriendo mientras se
  migra.
- **Menores (a decidir con Gastón):** (1) ¿el motivo de cancelación de un servicio debe quedar
  guardado en el historial? (hoy es opcional y no se persiste — faltaría un campo en el backend);
  (2) ¿un servicio "genérico" puede estar confirmado por el operador? (si sí, falta su acción de
  cancelar en la UI); (3) ¿los botones de editar en una reserva trabada se ven grisados o se
  permiten y el sistema abre el cartel de autorización al intentar?
