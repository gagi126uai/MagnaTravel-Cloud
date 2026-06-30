# Cuenta del operador: los dos números ("Le debo / Me tiene que devolver") + cierre de fugas de plata

Fecha: 2026-06-30
Estado: Backend HECHO y revisado en verde (3 reviews). COMMITEADO local, SIN pushear (deploy lo hace Gaston). Falta: pantalla (Fase D, gate UX) + 2 follow-ups de plata declarados.

## En fácil (para Gaston)

La cuenta del operador ahora lleva bien **dos números separados por moneda**: *"Le debo"* (lo que vos todavía le tenés que pagar) y *"Me tiene que devolver"* (la plata que el operador te debe por viajes anulados que ya le habías pagado). Antes, al anular un viaje ya pagado, la cuenta quedaba con un saldo negativo eterno y —peor— el sistema estaba por convertir esa plata que el operador te debe en **"saldo a favor" tuyo gastable**. Eso era una fuga de plata: te dejaba gastar plata que en realidad no tenías.

Se cerró la fuga, que aparecía por **varias puertas**, y se cerró **de raíz**: el "te debe" ahora se calcula con la misma regla exacta que mueve la plata, así no se pueden separar nunca más.

## Lo fiscal (resuelto por investigación, sin contador)

Decisión del dueño: no se consulta contador; se investiga y se informa. Informe: `docs/explicaciones/2026-06-29-resolucion-fiscal-cuenta-operador.md` (fuentes ARCA/AFIP reales, supuestos y riesgos marcados). Conclusiones clave:
- La cuenta con el operador es **contabilidad interna**: por ella **no se emite nada al ARCA**. Lo único fiscal es la NC/ND al cliente, que ya estaba decidida y NO se tocó.
- El operador **reembolsa neto** de su multa; la multa retenida **reduce "me tiene que devolver"**, no infla "le debo".
- La perilla reseller/intermediario debe variar **por operador** (follow-up, NO entró en este build; default = reseller = comportamiento actual).
- Multimoneda: el receivable nace en la moneda del pago; diferencia al cobrar en otra moneda = diferencia de cambio a resultados (sin papel fiscal). Multa en moneda ≠ pago = follow-up.

## Diseño (rev 2, tras 2 rondas de architecture-review)

`docs/explicaciones/2026-06-29-rediseno-bc-cuenta-operador.md`. Correcciones incorporadas:
- Se sacó la "garantía de cuadre" tautológica; se reemplazó por invariantes testeables reales + matriz de escenarios.
- Se arregló el reconciler de saldo a favor (ADR-041) que convertía un "me tiene que devolver" en saldo a favor gastable.
- Extracto de caja **intacto** (invariante respetada); el circuito de cancelación va en bloque separado, derivado en lectura.

## Qué se construyó (backend)

- **Calculador puro** `SupplierAccountReconciliation` (Domain): X = "Le debo", Y = "Me tiene que devolver", Prepago, por moneda.
- **Lector** `SupplierCancellationCircuitReader` (Infra): única fuente de Multa retenida / Reembolso recibido / Y.
- **Reconciler** (`SupplierCreditReconciler`): fórmula económica `overpayment = max(0, −(Balance + Multa + Reembolso + Y))`; disparado además al anular, confirmar multa, waive/revert, recibir/anular reembolso (misma transacción).
- **DTO** del extracto: campos aditivos `TheyOweMe`(Y), `ITheyOwe`(X), `Prepayment`, `CircuitLines`, con etiquetas en español y enmascarado por permiso de costo.
- **C2**: la multa que es **fee propio de la agencia** (agency-owned) ya no reduce mal lo que el operador debe devolver.

## El fix de RAÍZ (lo importante)

El bug se manifestaba por **3 puertas** y cada review encontró una más profunda:
1. Reembolso parcial al cerrar la BC (cliente consume su crédito) → minteaba el residuo.
2. Estado pre-CAE (`AwaitingFiscalConfirmation`) en el "Anular" formal normal → minteaba el pagado completo en la ventana hasta que AFIP responde.
3. Cancelación parcial de un servicio (`CancelServiceAsync`) que deja la BC en `Drafted` con caja negativa → minteaba en el próximo movimiento.

**Causa raíz:** se inferìa "la plata ya salió" desde el **estado de la anulación** (`bc.Status`), que NO es proxy fiel (un `Drafted` puede ser un borrador con servicios vivos o un parcial con un servicio ya cancelado).

**Solución estructural:** se ató el "te debe" (Y) a **la misma señal que produce la caja negativa**: `!WorkflowStatusHelper.CountsForSupplierDebtByType(...)` — el mismísimo predicado que usa `SupplierDebtPersister`. Así `(caja negativa por un servicio) ⟺ (Y cuenta su receivable)` se cumple **por construcción**, en todos los estados presentes y futuros, sin enumerar `bc.Status`. Guarda R2: en el único corner de divergencia futura (servicio que cuenta pero reserva excluida por su status) **sobre-declara Y y loguea**, nunca mintea.

## R1 (cerrado hoy, Camino B)

Fuga de borde: cancelar parcialmente un servicio **pagado** en una reserva **sin factura de venta** → no se podía crear la línea que ancla el receivable (la BC tiene FK NON-NULL a factura + índice único anti-doble-NC) → mint. Gaston eligió taparlo.
- Camino A (representar el receivable sin factura) exige hacer la factura nullable = cambio de núcleo fiscal (ADR-002) + migración + reescritura del índice → inseguro de forzar.
- **Camino B (hecho):** se **bloquea** la cancelación parcial de un servicio pagado-imputado cuando no hay factura, con mensaje claro ("emití la factura o gestioná el reembolso"). Coherente con que la anulación total ya exige factura. Un advance "a cuenta" (no imputado) NO se bloquea (es saldo a favor genuino).

## Tests

Suite no-integración: **2938/2938** verde. Tests nuevos por **path real** (no estados construidos): ConfirmAsync→pool 0, ArcaRejected, Closed sub-reembolsada, parcial Drafted + trigger pago, draft total sin falso receivable, divergencia de filtros (R2), R1 bloqueo. Las 204 fallas de la suite completa son del fixture Postgres/Testcontainers sin Docker (entorno), no del código.

## Pendiente (para mañana / follow-ups)

1. **GEMELO de R1 (Med, pre-existente, verificado):** `ReservaService.ApplyAnnulWithPaymentsToCreditAsync` (el "Anular con saldo a favor") cancela servicios pagados **sin** pasar por la guarda de R1 y sin crear línea → mismo mint por el próximo reconcile. Precondición 4 (`:1242`) confirma que ese flujo es justamente para reservas **sin** factura → alcanzable. **Mismo fix:** extender la guarda (o representar el receivable) a ese método. **PRIMERO de mañana.**
2. **R2** (Low): ya contenido con la guarda sobre-declara-Y; opcional sellar con el filtro `ValidReservationStatuses` espejado (NO espejar ciego: rompería Y en reservas canceladas).
3. **Representar el receivable de verdad (sin bloquear):** requiere el cambio de modelo fiscal (BC sin factura ancla). Pieza separada, alto riesgo, necesita ADR + decisión del dueño.
4. **Fase D — la pantalla** (los dos números + bloque "Circuito de cancelación"): gate UX con Gaston. Verificaciones de exposición: que la celda muestre la etiqueta español, nunca el código `Kind`; que los IDs de cruce no se impriman.
5. Validar en integración Postgres (CHECK SQL/xmin) los paths reales antes de prod.
6. Follow-ups menores: over-refund vía Allocate no atómico (Med); multa multimoneda ≠ pago; destino contable del residuo no reembolsado de una BC abandonada (baja a pérdida vs reclamo) = decisión de negocio.

## Reviews

backend-dotnet-reviewer: Approved. data-exposure-reviewer: Approved. security-data-risk-reviewer: Approved-with-conditions (condiciones = gemelo de R1 + R2, ambos follow-ups, no bloquean este merge).
