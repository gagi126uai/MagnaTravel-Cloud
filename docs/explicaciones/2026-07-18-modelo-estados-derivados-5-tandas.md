# El día que la ficha dejó de mentir — modelo de estados derivados completo (5 tandas)

**Sesión 2026-07-17/18.** Para cualquiera del equipo, sin jerga.

## De dónde veníamos

El 17/07 Gaston probó la reserva 1046 y la pantalla le dijo tres mentiras juntas:
"CONFIRMADA" (con los 2 servicios anulados), "SIN FACTURAR" (con la nota de
crédito emitida a la vista) y "Operador impago US$385" (sobre un servicio
anulado). Su orden fue: frenar features y construir la capa de coherencia.

## La causa, en criollo

La reserva tenía cuatro relojes separados (estado, cobro, factura, deuda al
operador) y ningún reloj maestro. Cada cartel miraba un reloj distinto y
ninguno preguntaba "¿los servicios de abajo siguen vivos?". Además, los caminos
de la plata (cobrar, anular, emitir notas) recalculaban el saldo pero nunca
avisaban al motor de estados. Los ERP grandes (SAP, Odoo) resuelven esto igual:
la cabecera SE CALCULA desde las líneas y los comprobantes.

## Qué se aprobó (Gaston, regla por regla)

Las 10 reglas del borrador (docs/architecture/2026-07-17-modelo-estados-
derivados-BORRADOR-para-aprobar.md), una por una. Correcciones de Gaston:
- Regla 9: SIN pasada nocturna — el estado se corrige en el momento, siempre.
- Cartel: "Anulada" (el de siempre); transición 100% automática; anulación
  parcial sigue "Confirmada" con el detalle tachado.
- Si el operador debe la devolución: "Esperando reembolso del operador" hasta
  que salde; recién ahí "Anulada".
- UX Tanda 4: chip "✓ Facturada y devuelta" gris; sin chip de Pago en anulada
  sin plata; etiqueta "Con multa" visible en la fila (eligió la opción visible).

## Qué quedó en producción (5 deploys verdes)

1. **Tanda 1** (`557be0e` + fix `7a7854f`): el motor corre en TODOS los caminos
   de la plata, en la misma transacción (plata y estado en un solo golpe). La
   cabecera pasa sola a "Anulada"/"Esperando reembolso" cuando muere el último
   servicio, sin emitir ninguna NC extra, con auditoría. Reparación única:
   2 reservas de producción que mentían (la 1046 incluida) quedaron corregidas
   (conteo validado antes y después: 2 → 0).
2. **Tanda 2** (`08089d3`): "Operador impago" solo sobre servicios vivos o
   multas reales confirmadas (y por el monto del cargo, jamás el costo pleno).
   El contador de turismo validó el criterio. De paso murió una incoherencia
   vieja entre la vista por servicio y la ficha del proveedor.
3. **Tanda 3** (`193f0100`): el eje de facturación distingue "nunca se facturó"
   de "se facturó y se devolvió". 8 reservas de producción ya muestran la verdad.
4. **Tanda 4** (mismo deploy): tachados en importes de anulados (todas las
   variantes), etiqueta "Con multa"/"✓ Multa cobrada" (con correlación exacta:
   jamás "cobrada" en falso), chips coherentes, y UNA sola definición de
   "reserva sin efecto" (par Anulada/Esperando reembolso) para todo el front.
5. **Tanda 5** (`121f0c61`): ejes materializados en la cabecera para que los
   listados filtren/ordenen sin recalcular; backfill validado contra PROD
   (44 filas, 0 sin llenar); el bloqueante del review (facturar no refrescaba
   la columna) cerrado en los 3 únicos caminos donde entra un comprobante.

## Cómo se trabajó (lo que funcionó)

Diseño → review de arquitectura (atrapó 3 bloqueantes EN DISEÑO) → construcción
con modelo económico → 3-4 reviews en paralelo por tanda (backend, seguridad,
fugas técnicas, y contador cuando tocó criterio fiscal) → re-review de cada
bloqueante hasta verde → SQL crudo validado contra PROD antes de pushear →
CI con Postgres real como juez final → verificación post-deploy con conteos.
El circuito pagó: el primer fix del bloqueante B-1 era un no-op y el revisor
lo rechazó; el CI encontró 6 tests rojos que destaparon un bug real de criterio
(la devolución del operador es un TOPE, no una deuda exacta); el revisor de T5
encontró que facturar dejaba la columna vieja. Nada de eso llegó a producción.

## Verificado DE VERDAD vs no verificado

- Verificado: suites completas verdes en CI (unit + integración Postgres +
  front) en los 4 runs; deploy verde; conteos contra PROD (reparación 2→0,
  backfill 0 sin llenar, 0 comprobantes raros, 8 "Facturada y devuelta").
- NO verificado end-to-end a mano: nadie navegó la app real. Checklist para
  Gaston: (1) abrir la 1046 → cabecera ya no dice "Confirmada"; (2) chip
  Factura de una anulada con NC → "✓ Facturada y devuelta"; (3) fila de
  servicio anulado → importes tachados + etiqueta de multa si corresponde;
  (4) anular el último servicio de una reserva de prueba con devolución
  pendiente → "Esperando reembolso" y al saldar → "Anulada"; (5) listado de
  reservas → chips coherentes con las fichas.

## Seguimientos anotados (no urgentes)

- Test flaky Adr042 D_TwoConcurrentRetries (timeout de candado; pasó en re-run).
- Atomicidad del refresco post-CAE (commit separado inmediato, patrón
  consistente con el resto del job; endurecimiento opcional).
- Recovery de NC parcial no re-aplica cobro (preexistente, documentado).
- Cruce de monedas en cargos del operador (display; gate contador ya anotado).
- Concurrencia multi-usuario (reserva colgada con 2 reembolsos solapados;
  hoy mono-usuario).
- Empate de DraftedAt en correlación de multas (ThenBy Id como desempate).
- Reemplazo del último servicio: anular primero deja la reserva anulada y el
  candado no deja cargar el reemplazo (camino seguro: cargar primero). Decidir
  si hace falta un aviso que guíe.
- Etiqueta "Multa por cobrar $0" en contexto de reserva (previo, sigue anotado).
