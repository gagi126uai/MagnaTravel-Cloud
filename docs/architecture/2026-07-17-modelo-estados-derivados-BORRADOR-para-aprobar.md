# BORRADOR — Modelo de estados derivados (para que Gaston apruebe las reglas UNA POR UNA)

> Estado: **PENDIENTE DE APROBACIÓN DEL DUEÑO**. Nada de esto se programa hasta
> que Gaston apruebe o corrija cada regla. Preparado el 2026-07-17 tras la
> captura de F-2026-1046 (tres afirmaciones falsas en una pantalla). Es LO
> PRIMERO de la próxima sesión.

## El problema (diagnóstico verificado en código, no supuesto)

La reserva F-2026-1046 mostraba a la vez: chip "CONFIRMADA" (con 2 de 2
servicios anulados), "PAGO: SIN MOVIMIENTOS" y "FACTURA: SIN FACTURAR" (con la
NC C 0001-00000037 emitida a la vista) y "Operador impago US$385" sobre un
servicio anulado. Las cuatro mentiras tienen la MISMA causa: el sistema tiene
CUATRO máquinas de estado paralelas (operativa, cobro, facturación, deuda al
operador) y ninguna función que las una en "qué ES esta reserva".

### Causa exacta de cada mentira (archivo:línea)

1. **"CONFIRMADA" con todo anulado**: el chip lee SOLO `reserva.status`
   (ReservaHeader.jsx:257 → ReservaStatusBadge.jsx:131). El motor de estados
   (ReservaAutoStateService.cs:105-124) SABE que la reserva quedó sin servicios
   vivos (BuildNeedsReviewReason :258-261 lo dice textual) pero POR DISEÑO
   (2026-06-24, "una confirmada no vuelve sola") solo marca
   `HasUnacknowledgedChanges` — nunca deriva un estado terminal. Existe el
   patrón simétrico a imitar: InManagement→Confirmed automática cuando todos
   los servicios se resuelven (:93-104, ADR-020 §4.4). Falta el espejo.
2. **"SIN FACTURAR" con NC emitida**: `invoicingStatus` deriva del NETO
   (facturas − NC); si la NC compensa la factura, neto≈0 → "Sin facturar"
   (ReservaInvoicingStatus.cs:51-69, el comentario :57-60 lo admite). Colapsa
   "virgen" con "facturada-y-devuelta". La historia real ya existe en el
   extracto (AddInvoiceLines, ReservaService.cs:213-286) — el chip no la lee.
3. **"SIN MOVIMIENTOS" con NC emitida**: `collectionStatus` mira balance +
   ConfirmedSale + TotalPaid (ReservaCollectionStatus.cs:104-130) — ignora
   comprobantes por completo. Y la rama "plata de anulada" de moneyStatus.js
   (:32, :70-72) está candadeada al string Status ∈
   {Cancelled, PendingOperatorRefund} → como el header quedó Confirmed, cae a
   la rama de reserva viva.
4. **"Operador impago" sobre anulado**: GetReservaSupplierPaymentStatusAsync
   (SupplierService.cs:3045) gatea por estado de RESERVA (:3070-3072, Confirmed
   pasa) y arma filas SIN filtrar servicios anulados (:3274-3358, ningún Where
   por status; único continue :3125 es CommissionOnly). DeriveOperatorPaymentStatus
   (:3250-3255): netCost>0 y covered=0 → unpaid. Además las columnas Costo neto
   (ServiceList.jsx:1409) y Precio venta (:1420) se muestran plenas, sin tachar.
   **Los tres síntomas se retroalimentan**: si el estado se derivara, el gate
   de deuda al operador ya lo filtraría.

### Mapa completo de indicadores de la ficha (quién lee qué)

Ver informe del diagnóstico (tabla completa): el badge de estado, el candado,
los chips Pago/Viaje/Factura/Corrección, las 3 tarjetas de plata, el bloque de
anulada, y los avisos por servicio — cada uno con su fuente y si considera
anulaciones. Conclusión transversal: (a) ningún eje del header cruza la
historia de comprobantes (la única fuente veraz es el extracto); (b) el estado
operativo es independiente de la vitalidad de los servicios; (c) los gates de
deuda al operador son a nivel reserva, no a nivel servicio.

## Cómo lo hacen los ERP grandes (verificado con fuentes)

- **SAP (SD)**: el estado de cabecera se AGREGA desde los ítems (tabla VBUK:
  ejes separados de entrega/facturación/general + `ABSTK` "rechazo global de
  todos los ítems" — cuando cada ítem se rechaza, el documento se completa por
  rechazo SOLO, sin acción extra ni refacturación).
- **Odoo (sale.order)**: `state` + `invoice_status` + `delivery_status` son
  campos CALCULADOS desde las líneas; las líneas canceladas se excluyen del
  monto; `invoice_status` refleja que la factura EXISTIÓ y no vuelve a "por
  facturar" tras una nota de crédito.
- Lección: la cabecera es un cálculo derivado de líneas + comprobantes, no un
  dato suelto; y "hubo comprobante" no se borra porque después se acreditó.

## LAS REGLAS (para aprobar o corregir una por una)

1. **Una sola función decide "qué es esta reserva"**; la cabecera se calcula
   desde sus partes (servicios + comprobantes + plata); nadie más inventa su
   versión. *Hoy cuatro carteles calculan cuatro realidades.*
2. **Si TODOS los servicios quedaron anulados, la cabecera deja de decir
   "Confirmada"** y pasa sola a un estado que refleje "sin servicios vigentes /
   anulada". *El sistema ya lo sabe y no lo lleva a la cabecera.*
3. **Esa transición automática NO dispara otra NC total** si cada servicio ya
   emitió su propia NC. *La plata ya se acreditó línea por línea; encima sería
   doble crédito.*
4. **El chip de Pago nunca dice "Sin movimientos" si la reserva quedó sin
   efecto**: muestra el circuito de anulación (saldo a favor / multa). *Hoy ese
   circuito está candadeado al string de estado.*
5. **El chip de Factura distingue "nunca se facturó" de "se facturó y se
   devolvió"**: con factura + NC dice "Facturada y devuelta", jamás "Sin
   facturar". *Hoy una reserva con NC parece virgen.*
6. **Un servicio anulado no genera avisos de cobro al cliente ni de pago al
   operador**, salvo multa/ND propia; sus importes se muestran tachados/
   históricos. *Los avisos por línea no preguntan si la línea quedó sin efecto.*
7. **"Operador impago" solo existe sobre servicios vivos o multas reales**,
   nunca sobre un anulado sin cargo. *El aviso fantasma exacto de la captura.*
8. **Cabecera y chips leen la MISMA fuente derivada**; si dos superficies
   muestran cosas distintas, es un bug, no dos verdades.
9. **El estado derivado se recalcula en cada cambio** (mismo punto donde hoy se
   recalcula el saldo) **y la pasada nocturna lo repara**. *Reusa el motor que
   ya funciona.*
10. **Todo cambio de estado derivado queda en el rastro** (quién/cuándo/por
    qué), aunque lo dispare el sistema.

## Las 3 preguntas que deciden las reglas 2-3 (para Gaston)

1. El estado terminal derivado: ¿se muestra como **"Anulada"** (mismo cartel de
   hoy) o como cartel propio **"Sin servicios vigentes"** para no confundir con
   el proceso fiscal de NC total?
2. La transición: ¿**100% automática** (como En gestión→Confirmada) o un botón
   **"Cerrar/Anular reserva"** que aprieta Gaston cuando quedó sin servicios?
3. Anulación PARCIAL (quedan servicios vivos + anulados): ¿la cabecera sigue
   "Confirmada" con el detalle por línea (recomendación, alineado a SAP/Odoo) o
   un cartel intermedio "Anulada en parte"?

## Principio de implementación (cuando se apruebe)

UNA función de dominio por entidad (¿tiene servicios vivos? ¿está sin efecto?
¿qué comprobantes tiene?) → el ÚNICO escritor del estado derivado es el motor
existente (ReservaAutoStateService, post-mutación + vigía nocturno como cura) →
los chips del front dejan de leer campos sueltos y solo pintan. Materializar el
estado en la cabecera (como SAP) para poder filtrar/ordenar listados sin
recalcular 6 colecciones por fila. Cada regla aprobada = una caminata E2E en CI.
