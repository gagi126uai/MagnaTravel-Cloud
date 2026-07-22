# 2026-07-22 — Tanda P4: cierre del circuito proveedor (P1..P4 completas)

> Explicado en fácil, para retomar sin haber estado.

## Qué salió en esta tanda (la última del circuito)

Gaston respondió las 3 preguntas de diseño (eligió las 3 recomendadas) y los
retoques finales quedaron en producción:

1. **El botón "Eliminar" de un cobro con comprobante anulado volvió a
   aparecer.** El motor siempre lo permitió; la pantalla lo escondía por una
   regla local vieja. Ahora manda el motor botón por botón: "Eliminar"
   habilitado, "Editar" gris con el motivo real ("...recibo anulado que debe
   preservarse para auditoría.").
2. **En "Anular reserva" ya no se amontonan dos avisos.** Cuando hay un
   error, se ve SOLO el cartel del error (con su botón para resolverlo, por
   ejemplo "Emitir factura"); el aviso del caso vuelve al reintentar.
3. **El cartel del candado le habla distinto al Administrador.** Antes le
   decía "pedí autorización" como si fuera vendedor; ahora dice "Podés
   destrabarla para editar" con botón "Destrabar reserva" (abre el mismo
   paso de siempre). El vendedor ve exactamente lo mismo que antes.
4. **Se cerró el último agujero del candado de plata (backend).** Había un
   camino interno (al pasar un presupuesto a "En gestión") que bajaba el
   estado de TODOS los servicios sin pasar por la regla única R1. Ahora esa
   operación valida servicio por servicio ANTES de tocar nada: si alguno
   tiene plata pagada al operador sin factura, se frena TODO (nada queda a
   medias) con el mismo mensaje y código que el resto del circuito.
   Dato del paseo E2E: el camino normal de la app ni siquiera permite armar
   ese escenario — el guard es defensa contra caminos raros (API directa,
   datos viejos), que es exactamente lo que debía ser.
5. **Tildes corregidas** en los motivos de bloqueo de cobros ("auditoría",
   "está", "nota de crédito"). "(CAE)" se mantuvo porque ese texto está
   firmado así en la spec del contrato pantalla-motor.

## Cómo se verificó

- Reviews backend, frontend y exposición de datos: verdes, sin bloqueantes.
- Tests: 6 nuevos del guard + suite completa 3922/3922 + front 269/269.
- **E2E real 28/28** (tres corridas reproducibles) con capturas en
  `scripts/e2e-local/shots-p4/`: banner de Admin + modal, cartel de error
  único con rechazo real del motor, guard del normalizador (rechazo 400 con
  código + caso "escape" sin pago que pasa), y el cobro con recibo anulado
  con los botones como corresponde.
- Commit `340ae6eb`, CI y deploy detrás del push.

## Estado de la obra

**El circuito proveedor queda COMPLETO: P1, P2, P3 y P4 en producción.**
Le queda a Gaston la prueba a mano de todo el paquete (checklist en la
memoria de retomo). Seguimientos menores anotados para un barrido futuro:
formato con punto ($700.00) en las tarjetas resumen de la ficha, tildes del
modal de autorización, y el botón "Emitir factura" que la transición
Presupuesto→En gestión todavía no ofrece (muestra el mensaje plano, alcanza).
