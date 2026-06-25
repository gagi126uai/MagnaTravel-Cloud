# 2026-06-24 — Bloque "arregla todo de una": H1-H4 + G6 + emisión de factura clara

Gastón pidió disparar de una todo el pendiente del día. Se hizo de corrido, con cadena completa de
agentes (negocio/back/front/seguridad/revisores) y arreglo de los bloqueantes que aparecieron.

**Estado:** 51 archivos en working tree, **sin commitear / sin pushear / sin desplegar**. Migración
nueva `Adr039_M1`. Backend 2441/2441 unit; front 1079/1079 + build verde. Todo revisado en verde.

## Qué se hizo

### H1 — "Pago: pagada" sin cobros
Una reserva nueva (sin cargos ni cobros) mostraba "Pagada". Causa: el estado de cobro derivado metía
"sin movimientos" en la misma bolsa que "saldado". Se agregó el estado `NoCharges` ("SinMovimientos");
el chip ahora dice **"Sin movimientos"** (decisión de Gastón). `Saldado`/"Pagada" queda solo cuando
hubo plata y se saldó.

### H3 — "Confirmar multa del operador" donde no corresponde
Se mostraba por estado (PendingOperatorRefund). Ahora hay una capacidad `CanConfirmOperatorPenalty`
que es verdadera **solo si existe una multa de operador pendiente de confirmar**; el botón se pinta por
esa capacidad. Bloqueante arreglado: la consulta proyectaba a una entidad mapeada (rompía en Postgres);
se pasó a proyección anónima + tests de integración.

### H4 — Auditoría de avisos / "Operador impago"
El endpoint de estado de pago al operador reportaba "impago" en cualquier estado. Se acotó a estados
donde realmente hay deuda con proveedor (`{InManagement, Confirmed, Traveling, Closed}`, misma fuente
que el cálculo de cuenta corriente del proveedor). En Cotización/Presupuesto/Perdida/Anulada ya no
aparece. Se barrieron todos los avisos backend (tabla en el retomo); los demás ya estaban bien.

### G6 — Caducidad de presupuesto/cotización
Settings configurables por separado (`BudgetExpirationDays`/`QuotationExpirationDays`, 0 = no caduca,
default 0). Un job pasa a "Perdido" lo que vence sin avanzar, con guard de plata viva (no caduca algo
con cobro vivo o factura con CAE; lo saltea y lo loguea). Pantalla de config con dos casilleros (sin
interruptor, "0 = no caduca nunca"). Aviso "por caducar" en la campanita (Q9), anticipación 3 días.

### H2 — Emisión de factura clara (lo más pedido)
La emisión es **asíncrona** (se encola en AFIP, el CAE llega después). Flujo nuevo:
1. Cartel de confirmación antes de emitir, con texto fiscal correcto (verificado contra ARCA): *"Una
   vez emitida no se puede eliminar; solo se corrige o anula con una Nota de Crédito."*
2. Estado **procesando** ("Estamos emitiendo la factura en AFIP. En unos instantes vas a ver el número.")
   con spinner; se quitó la jerga "Comprobante AFIP encolado".
3. Auto-actualiza (sin refrescar) a **éxito** (número + CAE) o **rechazo** (motivo de AFIP + "Corregir y
   reintentar" sin perder lo cargado). Endpoint poll `GET /api/invoices/reserva/{id}/fiscal-status`.
4. La factura emitida muestra número + CAE + vto en el Estado de Cuenta, con "Ver PDF" y **"Enviar al
   cliente"** (endpoint nuevo `POST /api/messages/invoice`, blindado: solo factura de venta emitida, no
   NC/ND, no anulada, ownership validado). Bloqueante arreglado: antes podía mandar una NC rotulada
   "Factura".

## Por qué importa (ARCA)
Una factura electrónica con CAE **no se puede borrar**; solo se neutraliza con una Nota de Crédito. Por
eso el cartel confirma antes de un acto irreversible, y por eso el envío al cliente bloquea NC/ND.
Fuentes: ARCA/AFIP guía de anulación de comprobantes; guías 2026 de anulación de factura electrónica.

## Pendiente
- Commit + push + deploy (lo hace Gastón). Validar `Adr039_M1` en Postgres.
- Confirmar con Gastón: anticipación del aviso (3 días), criterio de antigüedad de caducidad, texto del
  mensaje de envío de factura.
