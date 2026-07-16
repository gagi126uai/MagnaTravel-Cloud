# La factura viene pre-elegida al cancelar un servicio (y el desplegable dice los montos)

Fecha: 2026-07-16 · Commit: `5222181` · Deployado por CI (verde)

## El problema que destapó Gaston

Probando la anulación parcial en la reserva 1046, al cancelar un servicio el sistema
pedía elegir "la factura de la devolución" en un desplegable que decía solo
"Factura C 0001-00000051" — sin monto, sin moneda. Con dos facturas, el usuario
tenía que adivinar en cuál estaba el servicio.

## Lo que se descubrió investigando

- Desde FC1.3 (2026-05-21) existía la columna para guardar "este renglón de factura
  salió de tal servicio" (`InvoiceItem.SourceServicioReservaId`)… pero **ningún flujo
  real la grababa**. El cable estaba puesto sin corriente.
- Además esa columna solo servía para UNA de las 6 tablas de servicios (los genéricos);
  vuelos, hoteles, traslados, paquetes y asistencias quedaban afuera por diseño.

## Lo que se construyó

**Backend**
- Dos columnas nuevas en el renglón de factura: `SourceServiceTable` +
  `SourceServicePublicId` (referencia polimórfica sin FK, mismo patrón que
  `SupplierPayment`). Migración aditiva `20260716055807`, sin SQL crudo.
- Los renglones sugeridos (GET suggested-items) ahora viajan con la identidad del
  servicio de origen; el POST /invoices la acepta (opcional) y la persiste.
  Defensivo: si viene a medias o inválida, se guarda null y la factura sale igual —
  la metadata jamás bloquea una emisión.
- `reserva.invoices[]` expone `servicePublicIds` (qué servicios contiene cada
  factura) y `currency` (ISO, derivada de MonId con default ARS legacy).

**Frontend**
- Label del desplegable: número + moneda + monto ("Factura C 0001-00000051 —
  $ 125.000,50" / "US$ 500,00"), mismo `formatCurrency` y formato aprobado 2026-07-01.
  Aplica a: modal Cancelar servicio, Cancelar varios servicios y panel de la
  devolución parcial (T5).
- Al emitir factura, cada renglón precargado conserva (invisible) de qué servicio
  salió y lo reenvía. Renglón agregado a mano va sin origen.
- Al cancelar servicio(s): si TODOS están en UNA única factura activa, esa factura
  viene pre-elegida con el texto "Este servicio está incluido en esta factura."
  (cambiable). Lógica pura en `serviceInvoiceMatch.js` con tests.

## Límite honesto

Las facturas emitidas ANTES de este deploy no tienen la trazabilidad (no hay forma de
reconstruirla con certeza) → en esas no hay pre-elección, solo el monto en el label.
Todas las facturas nuevas la traen.

## Verificación

- Reviews: backend, frontend y gate de exposición de internos — verdes, 0 bloqueantes.
- Unit backend 3644/3644 · frontend 2191/2191 · builds ok · CI (integración Postgres
  + deploy) verde.
- Retoques post-review aplicados: identidad de servicio unificada con
  `getReservationServicePublicId` en ServiceList, y `aria-describedby` del hint.

## Pendientes anotados (no bloqueantes)

- Tests del cableado React de la pre-selección en CancelarVariosServiciosInline
  (la lógica pura sí está cubierta).
- Extraer `construirItemPayload`/`itemBackendALocal` de EmitirFacturaInline para
  testear las funciones reales (hoy el test prueba una copia).
- Si Gaston lo pide: que una elección manual del desplegable nunca sea pisada por
  una re-sugerencia al cambiar los tildes.
