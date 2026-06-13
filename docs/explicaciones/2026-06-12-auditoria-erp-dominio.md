# 2026-06-12 — Auditoría de negocio del ERP (travel-agency-domain-expert)

Auditoría con lente "¿esto se comporta como un ERP de agencia de viajes minorista?". Posterior al bloque plata/ARCA/leads (HEAD `f48910a`). Todo verificado contra código; evidencia archivo:línea en cada punto.

## TOP 10 problemas (por impacto operativo)

| # | Problema | Severidad | Evidencia |
|---|----------|-----------|-----------|
| 1 | **Comisión de vendedor NO existe**: CommissionService solo se usa en un endpoint calculadora suelto; nada se devenga al vender/cobrar; la reserva no tiene vendedor comercial (solo ResponsibleUserId). El campo "Commission" de los servicios es la GANANCIA, no comisión. | ALTA | CommissionService.cs:105-129; CommissionsController.cs:90; Reserva.cs |
| 2 | **Voucher se puede emitir sin servicio confirmado por el operador ni pagado**: solo frena por saldo del cliente (y se saltea con autorización). Riesgo: cliente llega y no tiene reserva. | ALTA | Voucher.cs:136-139; VoucherService.cs:402-478, 918 |
| 3 | **No hay cancelación parcial (un servicio) ni de files multi-operador** (bloqueado con INV-152 "gestionala manualmente") → justo los casos comunes quedan fuera del circuito de plata/fiscal. | ALTA | BookingCancellationService.cs:3159-3188 |
| 4 | **Cuenta corriente de proveedor GLOBAL, no por reserva/file**: no se puede conciliar liquidaciones del operador por expediente. SupplierPayment.ReservaId es opcional. | ALTA | SupplierService.cs:993-1037 |
| 5 | **Sin vencimiento de pago al operador ni time-limit aéreo** (los campos se eliminaron en ADR-019). La alerta mira la fecha del VIAJE, no la de pago/emisión. Es el riesgo clásico que más plata cuesta. | ALTA | HotelBooking.cs:93-95; AlertService.cs:106-140 |
| 6 | **"Viajó y debe" invisible**: la alerta de saldo solo mira viajes futuros (StartDate >= hoy); una reserva En viaje con saldo impago desaparece de las alertas y nunca cierra sola. | MEDIA | AlertService.cs:110-112; ReservaLifecycleAutomationService |
| 7 | **Ranking de vendedores atribuye por quien CREÓ el file (AuditLog), no por el responsable**, y mide TotalSale (presupuesto) en vez de venta confirmada; no excluye PendingOperatorRefund. No confiable. | MEDIA | ReportService.cs:829-862 |
| 8 | **Pasajeros sin vencimiento de pasaporte ni documento requerido por producto** (todo opcional). Sin alerta de vigencia (regla típica 6 meses). | MEDIA | Passenger.cs:18-27 |
| 9 | Factura no se arma desde los servicios de la reserva (ya conocido; confirmado como riesgo de facturar de más/menos sin aviso). | MEDIA | (pendiente declarado ADR-024) |
| 10 | **"Confirmado con cambios" del operador no existe**: si el operador confirma con otro precio/habitación, no hay re-aceptación del cliente ni ajuste de saldo. | MEDIA | (sin entidad/flag; análisis estado Confirmada 2026-06-07) |

## Faltantes de ERP (conceptual)
Comisión vendedor (devengo/liquidación/tope cero) — FALTA. Cuenta proveedor por file + conciliación liquidaciones — FALTA. Cancelación parcial/multi-operador — FALTA. Vencimientos operativos (time-limit, pago operador, CAE) — FALTAN. Gestión documental pasajero (pasaporte/visa/vigencia) — FALTA. Rentabilidad por reserva/proveedor/destino — A MEDIAS. Confirmado-con-cambios — FALTA. Cobranza post-viaje — FALTA. Condiciones de cancelación parametrizables por operador — FALTA.

## Lo que está BIEN (no tocar)
Ciclo de vida de reserva (incl. ConfirmedSale vs TotalSale, saldo exigible por servicio confirmado). Circuito cancelación con refund operador (PendingOperatorRefund, tipificación fiscal). Auditoría de vouchers. Libro de caja inmutable + deuda proveedor por moneda. Masking see_cost consistente. Voucher aclara que no es comprobante fiscal.

## Preguntas para Gastón (decisiones de negocio)
1. ¿Comisión de vendedor en el sistema? ¿% sobre venta o sobre ganancia? ¿Se devenga al confirmar o al cobrar?
2. ¿Bloquear voucher de servicio no confirmado por el operador?
3. ¿Frecuencia real de cancelar UN servicio o files con 2 operadores? (define prioridad)
4. De los vencimientos (time-limit aéreo / pago a operador / pasaporte), ¿cuál quema más? (ADR-019 los sacó a propósito; reintroducir requiere su decisión)
5. ¿Concilia con mayoristas por expediente o por total de cuenta? (define si deuda por file es necesaria)
