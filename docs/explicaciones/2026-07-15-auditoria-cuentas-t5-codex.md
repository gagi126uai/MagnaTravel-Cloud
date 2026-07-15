# Auditoría Codex — T5 y cuentas corrientes cliente/proveedor

> Auditoría histórica previa a la implementación. El cierre y estado vigente están en
> `docs/explicaciones/2026-07-15-cierre-t5-cuentas-codex.md`.

Fecha: 2026-07-15
Estado: diagnóstico confirmado; sin cambios funcionales todavía.
Objetivo: dejar una base única para continuar con Codex o Claude sin volver a investigar desde cero.

## Veredicto ejecutivo

La percepción de Gastón es correcta. No es solamente una UX desordenada: hoy conviven varios read-models
financieros que representan conceptos distintos y el frontend intenta coserlos. Eso produce importes que no
reconcilian entre sí, acciones backend sin pantalla y pantallas que prometen acciones que el dominio rechaza.

T5 está **parcialmente terminado**: backend implementado y probado; frontend de confirmación/emisión ausente.

## T5 — estado real

### Construido

- Captura de cancelación parcial conservando viva la reserva por los servicios restantes.
- Endpoint `POST /api/cancellations/{id}/emit-partial-credit-note`.
- Tope por remanente, moneda/TC heredados de la factura y escritura atómica.
- Reconciliador dedicado para no confundir la NC parcial con una anulación total.
- Rechazo ARCA reintentable e idempotencia.
- 22 tests unitarios y suite PostgreSQL verde en CI.

### Falta

- Cliente API React para `emit-partial-credit-note`.
- Panel aprobado en la ficha de reserva, doble confirmación y actualización automática.
- Estados visibles: listo, emitiendo, emitido, rechazado, reintento y bloqueos.
- Resolver desde UI la factura destino cuando hay más de una.
- Alinear permisos: hoy emitir y listar pendientes exigen permisos distintos.
- Tests HTTP de autorización/errores y tests frontend del flujo.

Conclusión: el usuario todavía no puede completar T5 desde la aplicación.

## Cuenta corriente del cliente — bloqueantes

1. **La multa se ve, pero no se puede cobrar.** El selector excluye reservas anuladas y el backend rechaza
   pagos sobre reservas `Cancelled`/`PendingOperatorRefund`.
2. **“Debe” excluye multas.** La deuda de reservas firmes y `PendingPenalties` se calculan por separado.
3. **La misma multa tiene dos números:** bruto congelado en el bloque superior y neto pendiente en la fila de
   reserva.
4. **“Cobrado” no significa caja real:** suma abonos del extracto que incluyen puentes internos
   `AffectsCash=false`.
5. **“Facturas” cuenta todos los comprobantes**, incluyendo NC y ND; la etiqueta es incorrecta.
6. El modal solo carga 100 reservas y puede ofrecer estados que el backend luego rechaza.
7. El cobro cruzado soportado por backend no está ofrecido de forma completa en el modal.
8. La venta se fecha en el extracto con `Reserva.CreatedAt`, no con el hecho de confirmación.
9. Los permisos de overview/statement y los de la solapa Reservas no son equivalentes aunque muestran plata.
10. `Nueva cotización` está discontinuada en su página, pero dashboards, Clientes y Cuenta Cliente siguen
    abriendo el modal mediante `?create=1`; la creación legacy continúa activa.

## Cuenta del proveedor/operador — bloqueantes

1. Listado, alertas y Excel todavía usan `Supplier.CurrentBalance`, escalar que mezcla ARS+USD y se muestra
   como pesos.
2. Backend calcula reembolsos atascados/abandonados, pero el frontend no consume esos buckets: son alertas
   invisibles.
3. El flujo avanzado de reembolsos (deducciones, asignación y corrección) existe solo en API; la UI usa el
   atajo simple.
4. `InvoicingMode` reseller/intermediario afecta la deuda, pero no se configura desde UI/API pública.
5. El vencimiento sugerido existe, pero no se opera ni se muestra como aging.
6. No existe una factura del operador como open item: faltan documento, vencimiento real, aprobación,
   impuestos/percepciones, adjunto, saldo pendiente e imputaciones.
7. CxP, reembolso por cobrar y saldo a favor se muestran separados arriba, pero neteados en el extracto.
   Hay tres lecturas válidas pero diferentes.
8. `SupplierAccountPage.jsx` coordina varios endpoints y refresh keys manualmente; hay cargas duplicadas y
   endpoints de pagos solapados.
9. Parte de los editores se muestra sin la capacidad real y algunos tipos de servicio no tienen la misma UI.

## Patrón objetivo de un ERP vendible

Benchmark contrastado:

- Odoo: facturas/bills y pagos son open items reconciliables; pagos no imputados quedan como crédito/débito
  pendiente; AR/AP tienen partner ledger y aging.
- NetSuite: el estado de cuenta lista facturas, cargos, credit memos y pagos, con aging y separación por
  moneda.
- Tourplan/Lemax: AR/AP se vinculan a reservas, documentos de cliente/proveedor, pagos, vencimientos,
  anticipos, conciliación y exposición futura todavía no facturada.

Fuentes:

- https://www.odoo.com/documentation/18.0/applications/finance/accounting.html
- https://www.odoo.com/documentation/17.0/applications/finance/accounting/payments.html
- https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N1300430.html
- https://www.tourplan.com/products/
- https://lemax.net/finances-and-accounting/

### Modelo visual y funcional recomendado

Cliente, por moneda:

- **A cobrar documentado**: facturas/ND abiertas menos NC y aplicaciones.
- **Pendiente de documentar**: venta confirmada o multa todavía sin comprobante.
- **Cobrado en caja/banco**: solo movimientos `AffectsCash=true`.
- **Crédito disponible**: anticipos/saldo a favor sin imputar.
- Open items con saldo, vencimiento, estado y aplicaciones; un cobro puede aplicarse a uno o varios.

Proveedor, por moneda:

- **Comprometido** por servicios confirmados.
- **Facturado por el operador** mediante vendor bills/open items.
- **A pagar** y vencido.
- **Nos debe** por reembolsos, sin neteo automático.
- **Saldo aplicable/anticipo**.
- Pagos aplicables a uno o varios documentos/reservas; compensación solo explícita y auditada.

No construir un ledger físico nuevo de una vez. Primero crear un contrato/read-model canónico de open items
sobre las tablas actuales; después migrar escrituras gradualmente. Así se evita romper producción.

## Orden de ejecución recomendado

1. Cerrar decisiones de producto de la sección siguiente.
2. Completar T5 frontend porque backend ya está listo.
3. Resolver el cobro de multa/ND sobre reserva anulada mediante open item/documento, no habilitando cobros
   genéricos sobre cualquier reserva anulada.
4. Crear read-model canónico de cuenta cliente y hacer que encabezado/extracto/aging usen la misma fuente.
5. Corregir escalares ARS+USD del proveedor y recuperar alertas invisibles.
6. Crear open items de proveedor y luego rediseñar su pantalla.
7. Retirar todos los accesos de alta de Quotes legacy; conservar solo archivo/conversión hasta migrar historia.
8. Eliminar endpoints/DTOs/escalares legacy una vez que no tengan consumidores.

## Decisiones que debe confirmar Gastón

Recomendaciones entre paréntesis:

1. ¿`Debe` incluye solo deuda documentada y muestra lo no documentado aparte? (**Sí**).
2. ¿`Cobrado` significa exclusivamente dinero real en caja/banco? (**Sí**).
3. ¿La multa se cobra imputando el pago a la ND/open item concreto, aunque la reserva esté anulada? (**Sí**).
4. ¿Un cobro/pago puede cancelar varios open items del mismo tercero y moneda? (**Sí**).
5. ¿Cliente debe soportar Prepago y Cuenta corriente con límite, plazo y aging por moneda? (**Sí**).
6. ¿Proveedor distingue comprometido vs factura recibida/aprobada? (**Sí**).
7. ¿Las deudas cruzadas con proveedor se compensan solo mediante acción explícita auditada? (**Sí**).
8. ¿Quotes legacy queda solo como archivo y toda nueva propuesta nace como Reserva-Presupuesto? (**Sí**).
9. ¿La primera entrega prioriza exactitud/reconciliación y después la nueva estética? (**Sí**).

## Qué puede probar Gastón ahora

T5 actual:

1. Crear reserva activa con dos servicios y una factura con CAE, todo en una moneda.
2. Cancelar un solo servicio.
3. Verificar que ese servicio quede cancelado y la reserva siga activa por el otro.
4. Verificar que deuda cliente/proveedor baje solo por ese servicio.
5. Ir a Facturación → Comprobantes por resolver y encontrar “Pendiente de emisión”.
6. Volver a la reserva: **no habrá panel para emitir**; ese es el faltante confirmado.

Cuenta cliente actual:

1. Abrir una reserva anulada con multa emitida.
2. Confirmar que aparece en “Multa pendiente de cobro”.
3. Pulsar “Nuevo cobro”: la reserva anulada no aparece y no se puede imputar. Es un bug confirmado, no un
   error de uso.
4. No tomar `Ventas - Cobrado = Debe` como conciliación completa mientras existan multas/saldos internos.

## Regla para el próximo agente

No agregar otro banner, total o endpoint paralelo. Toda función nueva debe declarar:

1. qué open item crea o modifica;
2. qué movimiento de caja produce;
3. cómo se imputa/revierte;
4. qué saldo canónico cambia;
5. cómo reconcilia por tercero y moneda;
6. qué acción concreta tiene el usuario en frontend.
