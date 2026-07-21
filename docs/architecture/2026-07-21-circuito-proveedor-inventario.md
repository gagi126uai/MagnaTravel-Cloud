> **DECISIONES FIRMADAS POR GASTON (2026-07-21, en sesión):** D1 regla única
> des-confirmar=anular (recomendada) · D2 costo<pagado: aviso+confirmación
> (recomendada) · D3 "Nueva factura" solo confirmados (recomendada) · D4 orden
> P1→P2(reembolsos)→P3(costo)→P4(resto) (recomendado). El aviso fijo con link
> en la cuenta del operador reusa el patrón D1 ya aprobado el 2026-07-18.

# Inventario de coherencia del circuito PROVEEDOR

**Fecha**: 2026-07-21
**Tipo de trabajo**: SOLO investigación (no se modificó código de producto). Es la obra hermana de
`docs/architecture/2026-07-18-contrato-pantalla-motor-inventario.md`, pero del lado del operador
(proveedor), su plata, sus facturas y sus reembolsos, y el cruce con la cuenta del cliente.

**Motivación**: Gaston (dueño, no programador) reportó que "la parte de proveedores está muy pero
muy incompleta" y dio evidencia concreta: dos guards del MISMO circuito, ante la MISMA intención
("sacarme de encima un servicio que ya le pagué al operador"), dan instrucciones contradictorias —
uno dice "anulá los pagos al proveedor antes de des-confirmar", el otro "emití la factura de venta
o gestioná el reembolso". Además: editar el costo de un servicio por debajo de lo ya pagado al
operador pasa sin aviso, y anular un comprobante de plata del operador puede dejarlo en un estado
sin salida.

**Cómo se hizo**: lectura directa de archivo:línea en los 4 backends del circuito
(`BookingService.cs`, `ReservaCapacityRules.cs`, `BookingCancellationService.cs`,
`DeleteGuards.cs`, `SupplierService.cs`, `SupplierCreditReconciler.cs`, `SupplierDebtPersister.cs`,
`OperatorRefundService.cs`) y sus 2 controllers (`SuppliersController.cs`,
`OperatorRefundsController.cs`), cruzado con los componentes de frontend que los consumen
(`ServiceList.jsx`, `ServiceInlineCard.jsx`, `PagarProveedorInline.jsx`,
`SupplierInvoicesSection.jsx`, `UsarSaldoOperadorInline.jsx`, `SupplierAccountPage.jsx`,
`operatorRefundsApi.js`). Todo verificado contra el código REAL del working tree al 2026-07-21,
incluyendo cambios de la Tanda 1 del plan "contrato pantalla-motor" que están construidos pero
**sin commitear todavía** (confirmado por `git status`; se los trata como código real porque están
en el checkout, con una nota explícita en cada fila donde aplica).

**Hallazgo de contexto importante, antes de la tabla**: el plan "contrato pantalla-motor" YA
resolvió, en Tanda 7 (`src/TravelApi.Domain/Reservations/ServiceCancellationPreflight.cs`, ya
commiteado), el candado R1 del lado "Anular servicio" (papelera): hoy tiene pre-chequeo, mensaje
específico y un botón "Emitir factura" (`ServiceList.jsx:648-662`). Es un trabajo bien hecho y
reciente. El problema que reporta Gaston sigue vivo porque existe un **guard gemelo, en la
pantalla de EDITAR el servicio**, que cubre el MISMO riesgo de plata (pagos al operador sin
factura que los ancle) y que esa tanda nunca tocó — sigue sin pre-chequeo, con jerga, y con una
regla más ancha e imprecisa que la que ya se arregló del otro lado. Ese es el hallazgo #1 de este
documento.

---

## A. Servicio ↔ operador: confirmar / editar / des-confirmar / anular

| # | Acción y dónde vive | Guard(s) | Mensaje EXACTO | ¿Pre-chequeo? | Veredicto |
|---|---|---|---|---|---|
| A1 | **Anular servicio** (papelera) — `ServiceList.jsx` → `BookingCancellationService.CancelServiceAsync` → R1 `EnsurePaidServiceCancellationHasReceivableAnchorAsync` (`BookingCancellationService.cs:899-919`) | Bloquea SOLO si: el servicio puntual tiene plata pagada al operador (`RefundCap` reconstruido > 0, por SERVICIO) **Y** la reserva no tiene factura de venta viva. Con factura viva, no bloquea (el ancla ya existe). | `"No se puede anular este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el reembolso con el operador antes de anular el servicio."` (`ServiceCancellationPreflightPolicy.cs:48-51`) | **SÍ** — `ServiceCancellationPreflight.cs` (Tanda 7, ya commiteada) calcula el mismo hecho en el GET de la ficha; la papelera se apaga con el motivo antes del clic, y si igual explota por carrera, el modal trae botón **"Emitir factura"** (`ServiceList.jsx:648-662`). | **OK — ya remediado.** Bien resuelto, preciso (por servicio), con escape válido (factura viva) y camino servido. |
| A2 | **Editar servicio → bajar el estado de "Confirmado" a "Solicitado"/"Cancelado"** (dropdown de estado dentro de la ficha de edición) — `BookingService.UpdateHotelAsync` (y gemelos de Vuelo/Traslado/Paquete/Asistencia) → `ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync` (`ReservaCapacityRules.cs:312-336`) | Bloquea si: el estado pasaba de confirmado a no-confirmado **Y** existe CUALQUIER `SupplierPayment` sumando > 0 para **toda la reserva** (no por servicio puntual — el propio comentario del código, línea 306-308, admite la granularidad: *"si hay 2 hoteles y solo se pagó uno, igual no podrás des-confirmar el otro"*). **No mira si hay factura de venta viva** — a diferencia de R1, no tiene escape. | `"No se puede degradar el estado del servicio '{serviceLabel}' (de '{oldServiceStatus}' a '{newServiceStatus}'): esta reserva tiene pagos al proveedor registrados por ${totalPaid:N2}. Anula los pagos al proveedor antes de des-confirmar."` (`ReservaCapacityRules.cs:333-335`) | **NO** — nada en la ficha de edición anticipa este bloqueo; explota recién al guardar. No existe una versión de `ServiceCancellationPreflight` para este guard. | **CONTRADICTORIO + JERGA + FORMATO.** Ver hallazgo #1 abajo. |
| A3 | **Borrar servicio confirmado** (no anularlo, eliminarlo físico) — `BookingService.DeleteHotelAsync` (y gemelos) → `DeleteGuards.GetServiceDeleteBlockReasonAsync` (`DeleteGuards.cs:132-183`) | Bloquea si el servicio ya está confirmado con el operador (`ConfirmedAt != null`). | `"No se puede borrar un servicio ya confirmado con el operador. Cancelalo (queda tachado, con quien y cuando, y su monto se resta del saldo del cliente)."` (`DeleteGuards.cs:155-156`) | Parcial — el 409 llega al guardar, pero el mensaje mismo YA funciona como camino: te manda al botón correcto (Anular = A1, que ya está bien resuelto). | **OK.** Redacción criolla, camino correcto, sin contradicción (a diferencia de A2, funnela a la MISMA solución que A1). |
| A4 | **Reasignar operador o moneda de un servicio confirmado y pagado** — mismo `UpdateHotelAsync` → `GuardOperatorOrCurrencyReassignmentAsync` (`BookingService.cs:103-130`) → `BookingCancellationService.EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync` | Mismo criterio R1 (RefundCap del servicio + factura viva como ancla), pero SOLO se dispara si cambia el proveedor o la moneda. **No se dispara si el costo simplemente baja, con el mismo proveedor y moneda** — ver A5. | (mismo texto/patrón de R1, no re-verificado carácter por carácter en esta pasada — mismo mecanismo) | Parcial (mismo mecanismo que R1, no tiene su propio `Preflight` de UI) | **OK en la regla, gap en UI** — no forma parte de los peores casos porque cubre un escenario menos frecuente (cambiar de operador) y sí tiene ancla. |
| A5 | **Editar el costo (`NetCost`) de un servicio confirmado, MISMO proveedor y MISMA moneda, ya con pagos registrados** — mismo `UpdateHotelAsync`, vía `ResolveUpdateCostFieldsAsync` (`BookingService.cs:332-...`, invocado en `:1196`) | **Ninguno.** `ResolveUpdateCostFieldsAsync` solo decide si el caller ve/puede tocar el costo (permiso `cobranzas.see_cost`); no compara el `NetCost` nuevo contra `SupplierPayments` ya registrados. `GuardOperatorOrCurrencyReassignmentAsync` (A4) NO se dispara acá porque ni el proveedor ni la moneda cambiaron. | (ninguno — la edición se guarda en silencio) | No aplica (no hay guard) | **AGUJERO-PLATA.** Ver hallazgo #3 abajo — el peor de los dos que reportó Gaston. |

---

## B. Pagos al operador — estado ACTUAL (post Tanda 1, sin commitear)

La Tanda 1 del plan "contrato pantalla-motor" (2026-07-18/20) ya está **construida en el
working tree** (confirmado con `git status`: `SuppliersController.cs`, `SupplierService.cs`,
`PagarProveedorInline.jsx`, `SupplierInvoicesSection.jsx`, `UsarSaldoOperadorInline.jsx`
modificados, sin commitear) y resuelve varios de los peores síntomas que el análisis del
2026-07-20 (`docs/architecture/2026-07-20-analisis-cuenta-proveedor-vs-erps.md`) había
diagnosticado:

- `AddSupplierPayment`/`UpdateSupplierPayment`/`DeleteSupplierPayment` ya NO tragan el mensaje
  real: usan una excepción propia (`SupplierPaymentValidationException`) que el controller
  propaga tal cual (`SuppliersController.cs:408-411, 436-439, 470-473`), en vez del genérico
  `"No se pudo registrar el pago al proveedor."` que describía el inventario anterior.
- El alta de pago devuelve `Impact` (a qué reserva bajó la deuda y cuánto queda pendiente),
  leído después del commit (`SuppliersController.cs:375-393`); el front (`PagarProveedorInline.jsx`)
  ya arma el cartel de éxito con ese dato en vez de cerrar en silencio.
- El selector de servicio ya filtra por la moneda del pago (`PagarProveedorInline.jsx:679-684`) y
  "Nueva factura" ya se esconde para proveedores `CommissionOnly`/facturación directa
  (`SupplierInvoicesSection.jsx:17-21`).

**Verificado (lectura de código, no ejecución)**: esto está construido, no solo planeado. **No
verificado**: no corrí la app; no confirmé el comportamiento E2E real de estos cambios.

Lo que **sigue sin resolver** en esta sección, con evidencia:

| # | Acción | Guard | Mensaje | Veredicto |
|---|---|---|---|---|
| B1 | Anular una factura del operador con pagos aplicados — `SupplierService.VoidSupplierInvoiceCoreAsync` (`SupplierService.cs:2013-2014`) | Bloquea si `PaymentApplications.Any(!IsReversed)` | `"No se puede anular una factura con pagos aplicados."` — CRIOLLO-SIN-CAMINO en el texto crudo (no dice "revertí la aplicación primero") | **OK en la práctica**: el botón "Anular" se ESCONDE cuando hay aplicaciones activas y en su lugar se ofrece "Revertir aplicación" por cada una (`SupplierInvoicesSection.jsx:219,233`). El texto crudo del backend nunca llega a mostrarse por este camino — pero si algún día se llama a este endpoint desde otro lugar (API directa, otra pantalla), el mensaje no tiene camino. |
| B2 | Liquidar un cargo del operador ya liquidado / con importe distinto — `SupplierService.cs` (`:1310-1321` aprox., re-citado del inventario 2026-07-18, no re-verificado carácter por carácter esta sesión) | anti doble-pago | "Ese cargo del operador ya se pagó..." / "el pago debe cubrir su monto completo en la misma moneda" | CRIOLLO-CON-CAMINO, ya prechequeado en UI según el inventario previo. Sin cambios detectados. |

---

## C. Reembolsos del operador — el callejón más severo del circuito

**Hallazgo confirmado con evidencia directa, no inferido**: el backend tiene un mecanismo
COMPLETO para deshacer un reembolso del operador mal registrado —

- `OperatorRefundService.VoidAllocationAsync` / `TryVoidOnceAsync` (`OperatorRefundService.cs:1068-1232`):
  anula una `OperatorRefundAllocation`, libera el cap del refund, ajusta la línea del `BookingCancellation`,
  reconcilia el pool de saldo a favor del operador — todo en una transacción, con auditoría.
- `OperatorRefundService.ReassociateAllocationAsync` (`OperatorRefundService.cs:1238-1482`): mueve
  una allocation mal imputada de un `BookingCancellation` a otro, para el caso "elegí la reserva
  equivocada en el desplegable".
- Ambos están expuestos en el controller: `DELETE /operator-refunds/allocations/{id}`
  (`OperatorRefundsController.cs:278-309`) y `PATCH /operator-refunds/allocations/{id}/reassociate`
  (`OperatorRefundsController.cs:316+`).
- Ambos tienen tests de integración dedicados (`OperatorRefundServiceTests.cs`,
  `OperatorRefundsControllerTests.cs`).

**Pero el frontend nunca los llama.** `operatorRefundsApi.js` — el ÚNICO wrapper de llamadas HTTP
para reembolsos de operador en todo `TravelWeb/src` (confirmado por grep global, cero resultados
para `VoidAllocation`/`allocations/` fuera de ese archivo) — solo expone 3 métodos:
`getPendingBySupplier`, `reopenForLateRefund`, `recordAndAllocate`. **No existe `voidAllocation` ni
`reassociateAllocation`.** La pestaña "Reembolsos" de la ficha del proveedor
(`OperatorRefundsPendingSection.jsx`, montada desde `SupplierAccountPage.jsx:2227-2233`) solo lista
reembolsos PENDIENTES; no hay ninguna vista de "reembolsos ya registrados" con acción de deshacer.

**Consecuencia real, sin vueltas**: si alguien registra "reembolso recibido del operador"
(`RegistrarReembolsoRecibidoInline.jsx` → `POST /operator-refunds/record-and-allocate`,
`operatorRefundsApi.js:52-74`) con el monto equivocado, la moneda equivocada, o imputado a la
reserva equivocada — **no hay ningún botón en ninguna pantalla del sistema para deshacerlo.** El
motor construido para eso existe y funciona; el camino a la pantalla no. Es, con evidencia directa
de código (no de un mensaje mal redactado, sino de la AUSENCIA total de la llamada), el peor
callejón del circuito proveedor — y calza exactamente con la queja de Gaston de "un comprobante de
cobro que queda en un estado del que no se sabe cómo salir": acá ni siquiera hay un botón para
INTENTAR salir.

Agravante: `recordAndAllocate` es una única llamada atómica que registra Y aplica el reembolso en
el mismo paso (sin una revisión previa tipo "confirmá antes de guardar" — `RegistrarReembolsoRecibidoInline.jsx`
solo tiene el form y el botón final). Un error de tipeo queda grabado sin ningún control posterior.

**Jerga latente (hoy inalcanzable, pero lista para filtrarse el día que se conecte el botón que
falta)**: dentro de `TryVoidOnceAsync`, si la allocation ya tiene retiros consumidos por el
cliente, el guard dice:

```
"La allocation tiene retiros consumidos por el cliente. Iniciar un ClientRefundReversal antes de anular la allocation."
(OperatorRefundService.cs:1151-1154)
```

`"allocation"` y `"ClientRefundReversal"` son nombres de clase internos, sin traducir — exactamente
el patrón de jerga que el gate de exposición de datos prohíbe. Hoy no le llega a ningún usuario
porque el botón que dispararía este guard no existe en la UI; pero es deuda técnica lista para
explotar en cuanto alguien conecte el cable que falta.

---

## D. Cruce con la cuenta corriente del CLIENTE

No se re-auditó esta sección línea por línea en esta sesión (el foco pedido era el lado
proveedor). Referencia relevante ya documentada en memoria del agente
(`project_ar_ap_multa_visibility_gap_2026-07-14`): a esa fecha, el extracto del cliente excluía
reservas Anuladas por filtro de estado, y la deuda del operador excluía `OperatorChargeInvoiced`
del `CurrentBalance`.

**Corrección importante encontrada en esta sesión**: el segundo punto (exclusión de
`OperatorChargeInvoiced`) **ya NO es así** — `SupplierDebtPersister.cs:29-35` documenta
explícitamente que desde el 2026-07-15 el saldo oficial del proveedor (`CurrentBalance` +
`SupplierBalanceByCurrency`) SÍ suma los cargos facturados aparte: *"gap admitido y cerrado en
esta fecha"*. La memoria previa sobre este punto específico está **desactualizada**; no se debe
seguir citando como problema vigente sin re-verificar.

El primer punto (extracto del cliente excluye Anuladas) no se re-verificó esta sesión — el trabajo
de ADR-048 (estados derivados, cerrado 2026-07-18) puede haberlo tocado indirectamente al
materializar el eje de "Con multa"/"Anulada" en la cabecera, pero eso es una hipótesis, no una
verificación. **Queda como pregunta abierta para quien retome el lado cliente, no como hallazgo
confirmado de esta auditoría.**

---

## Los 8 peores casos (rankeados por dolor del dueño)

1. **La contradicción textual que reportó Gaston, con la causa raíz exacta.** "Editar servicio →
   bajar el estado de Confirmado" (`ReservaCapacityRules.cs:333-335`) dice *"Anula los pagos al
   proveedor antes de des-confirmar"*. "Anular servicio" (`ServiceCancellationPreflightPolicy.cs:48-51`)
   dice *"Emití la factura de venta o gestioná el reembolso con el operador"*. Mismo problema de
   fondo (plata pagada al operador sin ancla), dos botones distintos, dos soluciones que se
   contradicen en la dirección: una pide DESHACER la plata movida, la otra pide FORMALIZARLA. Y lo
   más grave: el equipo YA SABE arreglar esto — lo hizo para el guard de "Anular" (Tanda 7, con
   pre-chequeo y botón "Emitir factura") y nunca tocó el guard gemelo de "Editar".
2. **No hay forma de deshacer un reembolso del operador mal registrado, en ninguna pantalla.** El
   motor completo existe (`VoidAllocationAsync`, `ReassociateAllocationAsync`, con tests), pero
   `operatorRefundsApi.js` nunca lo llama — cero botón, cero camino. Peor: registrar el reembolso
   es una única acción atómica sin paso de revisión previa.
3. **Editar el costo de un servicio confirmado por debajo de lo ya pagado al operador no tiene
   NINGÚN guard**, ni siquiera un aviso — a diferencia de "cambiar de operador/moneda" (A4), que sí
   está protegido. Structuralmente puede generar el mismo negativo de caja sin ancla que el
   candado R1 existe específicamente para impedir, por una puerta (`ResolveUpdateCostFieldsAsync`,
   `BookingService.cs:1196`) que no tiene ningún guard de plata.
4. **El guard de "des-confirmar" (A2) es una regla DIFERENTE y más ancha que R1** para el mismo
   riesgo: mira TODA la reserva en vez de un servicio puntual, y no tiene la salida de "factura
   viva" que R1 sí tiene. No es solo un problema de redacción — es una regla de negocio distinta
   resolviendo el mismo problema de dos formas incompatibles.
5. **Formato de moneda "gringo" en mensajes de guards de plata** (`${totalPaid:N2}` → "1,800.00"
   en vez de "$1.800,00"). Confirmado sistémico: sin configuración de cultura en `Program.cs`, el
   mismo patrón aparece en `ReservaCapacityRules.cs`, `ReservaService.cs`, `ClientCreditService.cs`,
   `CustomerService.cs`, `InvoicePdfService.cs` — no es exclusivo del circuito proveedor, pero es
   donde Gaston lo vio primero.
6. **Jerga "degradar"/"des-confirmar"** (`ReservaCapacityRules.cs:303,333,335`): términos técnicos
   internos que llegan crudos al usuario, únicos en todo el circuito proveedor auditado.
7. **"ClientRefundReversal" (nombre de clase) filtrado en un mensaje de guard** (`OperatorRefundService.cs:1151-1154`).
   Hoy inalcanzable (el botón que lo dispara no existe, ver #2), pero es la prueba de que el día
   que se conecte ese botón, la jerga ya está ahí esperando.
8. **"Registrar reembolso recibido" combina registrar + imputar en un solo paso atómico, sin
   revisión previa**, agravando el #2: un típeo en el monto o elegir la reserva equivocada del
   desplegable queda grabado en firme, sin control posterior ni forma de deshacerlo desde la UI.

---

## Propuesta de tandas (chicas, deployables, mismo estilo que el plan pantalla-motor)

### Tanda P1 — Unificar el guard de "des-confirmar" con R1 (peor #1 + #4 + #6 + parte del #5)
**Alcance:**
- `ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync` deja de sumar `totalPaid` a nivel
  RESERVA y en su lugar reusa el mismo `RefundCap` por servicio que ya calcula R1
  (`ComputeUnanchoredOperatorRefundCapAsync` en `BookingCancellationService.cs`), con la MISMA
  salida de "factura viva" que R1 ya tiene. Si el servicio puntual no tiene plata sin ancla, no
  bloquea — igual que Anular.
- El texto se reemplaza por el mismo `ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReason`
  (o una variante que diga "des-confirmar" en vez de "anular", pero con la MISMA instrucción:
  "Emití la factura de venta..."). Elimina "degradar" y el interpolado `'{oldServiceStatus}'` en
  inglés/interno.
- Formato de moneda: usar `CultureInfo("es-AR")` explícito en el `:N2}` (o mejor, un helper
  `FormatCurrencyEsAr` centralizado) — arreglo de una línea, aplicable acá y en los otros 4 archivos
  del hallazgo #5 en la misma pasada (barrido, no una tanda por archivo).
- Front: el formulario de edición de servicio pre-chequea con el mismo dato que ya expone
  `ServiceCancellationPreflight` (extendido para cubrir también la transición de estado en el
  formulario de edición), apagando el dropdown de estado con tooltip y ofreciendo el mismo botón
  "Emitir factura" que ya existe en `ServiceList.jsx`.
**Gate UX**: SÍ (cambia mensaje y agrega pre-chequeo visible). **Decisión del dueño**: confirma que
"des-confirmar" un servicio debe seguir exactamente la misma regla que "anular" un servicio (ver
pregunta D1 abajo).

### Tanda P2 — Guardar el costo del operador por debajo de lo pagado, con aviso (peor #3)
**Alcance:**
- `ResolveUpdateCostFieldsAsync` (o el call-site en cada `Update*Async`) agrega un chequeo: si el
  `NetCost` nuevo es menor que el total ya pagado al operador para ESE servicio y no cambia
  proveedor/moneda, no bloquea automáticamente (bajar el costo es legítimo — el operador puede
  haber dado un descuento) pero **exige confirmación explícita** con el monto exacto de la
  diferencia ("vas a generar $X de saldo a favor con este operador — ¿confirmás?"), en vez de
  guardar en silencio.
- Frontend: cartel de confirmación en el mismo formulario de edición, antes de guardar.
**Gate UX**: SÍ. **Decisión del dueño**: ver pregunta D2 abajo — ¿bloqueo duro o aviso con
confirmación?

### Tanda P3 — Deshacer un reembolso del operador mal cargado (peor #2 + #7 + #8)
**Alcance:**
- Frontend: agregar a `operatorRefundsApi.js` los dos métodos que faltan (`voidAllocation`,
  `reassociateAllocation`) y una vista de "reembolsos ya registrados" (historial, no solo
  pendientes) en la pestaña Reembolsos de la ficha del proveedor, con botón "Deshacer" por fila
  (reusa `VoidAllocationAsync`) y "Corregir reserva" (reusa `ReassociateAllocationAsync`).
- Backend: el mensaje de `TryVoidOnceAsync` para el caso "retiros consumidos" deja de decir
  "Iniciar un ClientRefundReversal" y en su lugar da un camino en criollo con destino concreto
  (ver pregunta D3 — necesita la misma decisión de negocio que la Tanda 8 pendiente del plan
  pantalla-motor, "a dónde mando estos casos que requieren revisión").
- `RegistrarReembolsoRecibidoInline.jsx`: agregar un paso de confirmación antes de guardar
  (mostrar reserva + monto + moneda elegidos, con botón "Confirmar" separado de "Guardar").
**Gate UX**: SÍ (pantalla nueva + flujo nuevo). **Decisión del dueño**: ver pregunta D3.

### Tanda P4 — Barrido de formato de moneda (parte del peor #5, transversal)
**Alcance**: helper único `FormatCurrencyEsAr(decimal, string ccy = null)` usado en los 5 archivos
identificados (`ReservaCapacityRules.cs`, `ReservaService.cs`, `ClientCreditService.cs`,
`CustomerService.cs`, `InvoicePdfService.cs`) y en cualquier otro `:N2}`/`:C2}` que aparezca en un
mensaje de guard (no en logs internos, donde el formato no importa). **No es exclusiva de
proveedores** — se prioriza acá porque es donde Gaston lo vio, pero conviene hacerla de una sola
vez para todo el backend.
**Gate UX**: NO (arreglo de texto sin cambio de flujo). **Decisión del dueño**: no.

---

## Preguntas para Gaston

**D1. Unificar "des-confirmar" con la regla de "anular" (Tanda P1).**
Hoy, si le pagaste al operador y todavía no facturaste al cliente, "anular" el servicio te avisa
bien y te ofrece el botón "Emitir factura". Pero si en cambio *editás* el servicio y le bajás el
estado (de Confirmado a Solicitado), te encontrás con un mensaje distinto y más duro, que además no
te avisa antes de intentarlo. Propongo que las dos acciones usen exactamente la MISMA regla y el
MISMO camino ("Emitir factura"), porque es el mismo riesgo de plata visto desde dos botones
distintos.
→ **Recomiendo: sí, unificar** — misma regla, mismo mensaje, mismo botón en las dos pantallas.

**D2. Editar el costo del operador por debajo de lo pagado: ¿bloqueo o aviso? (Tanda P2)**
Hoy no pasa nada — se guarda calladito y ese dinero de más queda flotando como saldo a favor con
ese operador sin que nadie lo haya decidido a propósito. Hay dos caminos: (a) bloquear la edición
igual que "anular", exigiendo el mismo camino de factura/reembolso, o (b) dejar editar pero avisar
con el monto exacto antes de guardar ("esto va a generar $X de saldo a favor — ¿confirmás?"),
porque bajar el costo puede ser un descuento real que el operador te dio.
→ **Recomiendo: (b) aviso con confirmación**, no bloqueo — a diferencia de anular un servicio
(que borra plata de la reserva), editar el costo hacia abajo suele ser un ajuste legítimo de
precio; lo que falta no es impedirlo, es que quede claro y confirmado antes de guardar.

**D3. Reembolsos del operador mal cargados: ¿a dónde mando los casos que necesitan revisión manual? (Tanda P3)**
Igual que la pregunta pendiente de la Tanda 8 del plan pantalla-motor (deshacer multa con
impuestos): cuando alguien intenta deshacer un reembolso ya consumido por el cliente, hoy el
sistema no tiene ningún lugar claro para mandar ese caso.
→ **Recomiendo: la misma bandeja de revisión en Cobranzas** que ya recomendé para la Tanda 8 (si
la construimos, sirve para los dos casos a la vez) — pero es tu operación, decidilo vos.

**D4. Orden de arranque.**
→ **Recomiendo: P1 primero** (es la contradicción textual que vos mismo reportaste, y ya sabemos
cómo se arregla porque medio camino ya está hecho), después **P3** (el callejón más severo, plata
sin forma de deshacer), después **P2** (agujero silencioso pero de menor frecuencia), y **P4** al
final (cosmético, transversal, se puede hacer en cualquier momento).
