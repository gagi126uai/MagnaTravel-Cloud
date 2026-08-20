# La ficha del operador no borra la historia (20/08/2026) — explicado en fácil

Gastón hizo una prueba completa: reserva con servicios de un operador,
factura al cliente, anulación, y "sin multa del operador". Fue a la ficha
del operador y estaba vacía — "como si nunca hubiese pasado nada". Pidió
investigar cómo está hecho y cómo funciona en los ERPs del sector.
Commit: `415d1e19`.

## Lo que encontramos

1. **La ficha se autolimpiaba a propósito**: las tres solapas de plata
   (Cuenta corriente, Deuda, Servicios) filtran por reservas "vivas" — al
   anular, todo desaparece para que el saldo cierre solo. Cerraba el número
   borrando el rastro.
2. **Los datos estaban todos guardados** (la anulación, quién decidió "sin
   multa" y cuándo, la auditoría completa). Faltaba solo la vista.
3. **Benchmark (SAP, Dynamics, NetSuite, Odoo, Lemax)**: unánime — una
   cancelación nunca borra la línea: queda la compra + una contra-línea de
   anulación, ambas visibles, neteando cero. Y las fichas tienen una capa
   de historial (el "chatter" de Odoo, las "System Notes" de NetSuite)
   donde también viven las decisiones sin plata. Nuestra constitución ya lo
   pedía: F-6, "nada se borra, se tacha".

## Las tres decisiones firmadas (multiple choice)

1. Las compras de una reserva anulada **quedan tachadas** en el extracto
   del operador, con contra-línea "Anulación de compra" (netean cero).
2. **Solapa nueva "Historial"** en la ficha del operador.
3. El **Historial de la reserva** también muestra la anulación y la multa.

## Qué se ve distinto

- **Extracto del operador**: la compra de una reserva anulada aparece
  tachada con chip rojo "Anulada", y abajo la contra-línea "Anulación de
  compra" con chip ámbar. El saldo no cambia (netean).
- **Servicios comprados**: los servicios de reservas anuladas se ven con
  chip "Anulada" (checkbox "Mostrar anuladas" para ocultarlos, prendido por
  defecto).
- **Solapa "Historial" (nueva, 8ª)**: todo lo que pasó con ese operador en
  orden — compra confirmada, reserva anulada, multa confirmada o **"cerrada
  sin multa" con quién y cuándo** (la decisión que antes no se veía en
  ningún lado), reembolsos, pagos, facturas del operador.
- **Historial de la reserva**: ahora muestra "X anuló la reserva", la
  decisión de multa y las notas de crédito de la anulación.
- **En Caja**: los movimientos viejos que mostraban un código técnico entre
  paréntesis (el "ejemplo de santa catalina") quedan limpios — una
  migración reescribe esos textos fosilizados de mayo-julio.

## Qué no se ve pero cambió

- El método de un pago al operador viaja traducido ("Transferencia", nunca
  "Transfer") y solo a quien tiene permiso de tesorería (patrón SEC-1).
- Los montos del historial se enmascaran sin permiso de ver costos (F-14);
  la multa en el historial de la reserva se muestra al vendedor a propósito
  — misma decisión de producto ya vigente en el panel de multa (se traslada
  1:1 al cliente), documentada en el código con test candado.
- La migración es idempotente, quirúrgica (regex + JOIN por claves), con
  tests de integración contra Postgres real que corren en CI.

## Reviews

Backend y seguridad bloquearon (método de pago crudo y sin gate; la multa
en el timeline — resuelta con evidencia del precedente, no con un gate
cosmético); corregidos y re-aprobados. Data-exposure y frontend aprobados.
3837 tests frontend + 452 unit backend del área verdes.

## Deuda anotada (gaps de esquema, documentados en el código)

- El actor de "cerrada sin multa" solo se persiste para el operador
  principal (secundarios salen sin nombre).
- `SupplierInvoice` no guarda quién la anuló.
- No hay historial de reintentos de NC (evento "NC reintentada" no
  construible hoy).
