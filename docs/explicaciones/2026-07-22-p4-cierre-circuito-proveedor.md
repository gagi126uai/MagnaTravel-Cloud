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

---

## AGREGADO (madrugada): puntos de cierre y dos cazas más de los gates

Después de P4 se cerraron los puntos que habían pedido las reviews:

- **Barrido completo del formato gringo**: Reportes, el buscador, la papelera
  de pagos, los números del listado de reservas y el modal del operador (que
  además ahora muestra el saldo POR MONEDA y distingue "sin permiso" de "sin
  saldo"). Toda la plata visible quedó en formato argentino.
- **El buscador mostraba la forma de pago en inglés crudo** ("Transfer",
  "Paid") — cazado por el gate de exposición. Ahora: Transferencia/Pagado, con
  guion seguro si aparece un valor desconocido.
- **Aviso claro cuando "El cliente aceptó" rebota por el candado de plata**:
  cartel fijo con el motivo real del motor. La primera versión ofrecía un
  botón "Emitir factura" que el paseo E2E demostró ROTO (la reserva sigue en
  Presupuesto, donde no se puede facturar) — se sacó el botón: el cartel
  explica y no promete lo imposible. Verificado en vivo.
- **Limpieza de la API**: se dejó de mandar un campo que la pantalla nunca
  usó, y se agregaron los tests que pidieron las reviews.

Commit `a28fdccc`, CI verde, deploy OK.

**Anotado para después** (no urgente): los totales sumados de Reportes y el
listado pueden mezclar pesos y dólares en una sola cifra (deuda vieja de
diseño, el arreglo real es backend y conecta con el norte multimoneda); y la
pregunta de negocio de si puede existir plata pagada al operador con la
reserva todavía en Presupuesto (el candado defensivo ya lo cubre).

**Con esto, el circuito proveedor queda cerrado del todo del lado del
sistema. Falta únicamente la prueba a mano de Gaston en producción.**
