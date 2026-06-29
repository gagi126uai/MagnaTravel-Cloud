# 2026-06-28 — Auditoría crítica del CORE de Reserva (para "endurecer el núcleo")

Autor: software-architect. Método: lectura de código real (file:line) sobre HEAD `f9e7466`.
Construye sobre `2026-06-24-auditoria-integral-reservas.md` y `2026-06-28-analisis-desconexion-reservas-operadores.md`; **no los repite**: los re-verifica contra el código de HOY y reporta qué sigue abierto.

> Pedido del dueño: voz crítica e imparcial. Donde algo está sólido, lo digo. Donde es humo, lo digo. Sin inflar ni minimizar.

---

## 0. Meta-conclusión (con criterio, imparcial)

**El núcleo está bastante más sano de lo que sugiere el doc del 2026-06-24.** La mayoría de los BLOQUEANTES de aquel día YA FUERON ARREGLADOS y verificados en el código actual. Esto es importante decirlo para no perseguir fantasmas:

| Bloqueante 2026-06-24 | Estado HOY | Evidencia |
|---|---|---|
| B-FISCAL-1 (NC contada doble en el cuadre) | **ARREGLADO** | Regla única `CountsInNetBilled` = `"A"` (`ReservaInvoicingCuadreCalculator.cs:140`); los 3 call-sites la usan consistente (`ReservaService.cs:221, 315-320, 2525-2529`). Factura anulada suma + su NC resta = sin doble conteo. |
| B-FISCAL-2 (CAE duplicado en factura de venta) | **ARREGLADO** | Idempotencia anti-doble-CAE en el job principal (`AfipService.cs:1105-1187`): snapshot del numerador + idemKey por Invoice + recovery de key huérfana. Cubre factura/NC total/ND. |
| B-PROV-1 (deuda de operador stale al anular) | **ARREGLADO** | `BookingCancellationService.ConfirmAsync` recalcula deuda+comisión en el mismo request (`RecalculateMoneyAfterTotalCancellationAsync`, `:1581`). |
| B-INTEG-1/2/3 (cancelación no atómica) | **ARREGLADO** | Confirmación envuelta en transacción (`:1502-1516`), auditoría stageada en el mismo commit (`:1596-1621`). |
| B-INTEG-4 (job nocturno con saldo rancio) | **ARREGLADO** | El job re-lee Status **y Balance fresco** antes de promover (`ReservaLifecycleAutomationService.cs:881-923`, `MoneyGatePasses :1070`). |
| Hueco legacy `AddPaymentAsync(int)` sin gate | **CERRADO** | `EnsureCollectable()` cableado en ambos accesos (`PaymentService.cs:731`, `ReservaService.cs:3679`). |
| Desconexión operador — FASE 0 (reembolso esperado sobreestimado) | **ARREGLADO** | `AllocateConfirmedPenaltyToLinesAsync` (`BookingCancellationService.cs:3538`) cableado en el camino síncrono (`:1273`) y el diferido (`:3342`). |

**Dónde el núcleo es genuinamente sólido (sin complacencia, es buena ingeniería):**
- La **matemática de la plata** por moneda es pura, con fuente única y sin mezclar ARS/USD (`ReservaMoneyCalculator.cs`).
- La **política de capacidades por estado** es declarativa, pura, documentada como compuerta-de-UI con los guards finos como defensa final, y tiene un **test cruzado** que prohíbe que diga "permitido" donde `EnsureCollectable` rechaza (`ReservaCapabilitiesCrossCheckTests.cs`). No "miente".
- La **idempotencia fiscal** (factura y NC parcial) está bien pensada con recovery contra ARCA.

**Dónde siguen los problemas reales (lo que esta auditoría agrega):** son menos y más localizados de lo que parecía. Tres son estructurales y bloquean uso real; el resto es endurecimiento. Los detallo abajo.

---

## BLOCKS-REAL-USE (impiden que una agencia opere su día a día con confianza)

### C1 — Cancelar/anular una reserva con DOS o más operadores está prohibido de raíz
- **Área:** Cancelaciones / estado.
- **Problema:** `InferSingleSupplierIdAsync` lanza `INV-152` si la reserva tiene servicios de más de un operador: *"La cancelación de reservas con varios operadores todavía no está disponible. Gestionala manualmente por ahora."* (`BookingCancellationService.cs:4766-4782`). Una reserva real de turismo combina rutinariamente aéreo de un operador + hotel de otro + asistencia de un tercero. Hoy esa reserva **no se puede anular por el sistema** — ni NC, ni saldo a favor, ni estado: queda colgada y se "gestiona a mano" (fuera del sistema, sin rastro fiscal).
- **Tipo:** [GAP]
- **Severidad:** BLOCKS-REAL-USE.
- **Dirección de fix:** habilitar la cancelación por LÍNEA de operador (la infraestructura de `BookingCancellationLine` por operador/moneda ya existe; el bloqueo es el inferidor de operador único). Es un rediseño acotado, no un rewrite. Requiere decidir con contador la emisión de NC/ND por operador.

### C2 — La cuenta del OPERADOR no concilia con el circuito de cancelación/reembolso
- **Área:** Reserva ↔ Operador (plata).
- **Problema:** confirmada en el código de HOY (la FASE 0 se arregló, pero las patas 1/2 siguen abiertas). `OperatorRefundService` registra el reembolso recibido SOLO como `ClientCreditEntry` (`OperatorRefundService.cs:486-628`); **no escribe nada en la cuenta del operador** (grep de `SupplierPayment`/`SupplierCreditService` en ese servicio → sin resultados). Combinado con que un servicio cancelado deja de contar como deuda (`WorkflowStatusHelper.CountsForSupplierDebt`), si ya se le pagó al operador el saldo de su cuenta se vuelve **negativo** ("nos debe") y **nunca se cancela** aunque devuelva la plata. Quedan **dos representaciones del mismo hecho** (saldo negativo del operador vs. read-model "Reembolsos a cobrar") que no coinciden.
- **Tipo:** [FISCAL→accountant] + [WRONG-MODEL]
- **Severidad:** BLOCKS-REAL-USE (la cuenta del operador miente tras cualquier cancelación con pago previo).
- **Dirección de fix:** postear el reembolso recibido y la multa retenida en la cuenta del operador (reusar `SupplierCreditEntry` si el contador lo valida) para que el saldo negativo se cierre. **NO avanzar el posteo sin firma del contador** (naturaleza de la multa retenida; evitar doble conteo cliente↔operador). Ver Fase 1/2 del doc 2026-06-28.

### C3 — El modo "intermediario" (factura solo comisión) está modelado pero es inalcanzable
- **Área:** Fiscal / configuración de operador.
- **Problema:** `Supplier.InvoicingMode` existe (`Supplier.cs:85`, default `TotalToCustomer`) y maneja la diferencia fiscal crítica reseller vs. intermediario; el calculador fiscal deriva a revisión manual cuando es `CommissionOnly` (`IFiscalLiquidationCalculator.cs:23-24`). **Pero no hay forma de setearlo:** cero referencias en el frontend (grep `.jsx` → ninguna) y ningún DTO/controller que lo escriba (grep en `Application`/`Controllers` → solo lo LEE el calculador). En la práctica **todo operador es para siempre `TotalToCustomer`**. Una agencia que opera como intermediaria (que es un modelo fiscal común en turismo) está mal modelada y no tiene cómo configurarlo.
- **Tipo:** [GAP] (rama muerta y no configurable) — relevante para "vender el software a todas las agencias", no solo a una reseller.
- **Severidad:** BLOCKS-REAL-USE para agencias intermediarias (IMPORTANT si el objetivo inmediato fuera solo reseller).
- **Dirección de fix:** exponer `InvoicingMode` por operador en la ficha de operador (pantalla + DTO + endpoint), con gate UX y firma del contador sobre la fórmula `CommissionOnly`. Hasta entonces, ser explícito de que el producto solo soporta el modelo reseller.

---

## IMPORTANT (erosionan confianza o dejan datos inconsistentes, pero no frenan la operación básica)

### I1 — Nota de Débito multi-operador: la multa solo se imputa al operador principal
- **Área:** Fiscal / cancelación.
- **Problema:** `AllocateConfirmedPenaltyToLinesAsync` imputa la multa SOLO a las líneas de `bc.SupplierId` (operador principal) (`BookingCancellationService.cs:3548-3549`); el monto de la ND se lee a nivel BC-padre. Con dos operadores con multa en monedas distintas, la ND saldría por uno solo. **Mitigante fuerte:** hoy C1 bloquea la cancelación multi-operador de raíz, así que esto es **latente** hasta que se levante C1 — pero hay que resolverlo JUNTO con C1, no después.
- **Tipo:** [FISCAL→accountant]
- **Severidad:** IMPORTANT (latente mientras C1 bloquee).
- **Dirección de fix:** emisión/confirmación de ND por línea de operador (mismo rediseño que C1).

### I2 — Una reserva Confirmada puede quedarse con 0 pasajeros
- **Área:** Integridad de datos / ciclo.
- **Problema:** `RemovePassengerAsync` (`ReservaService.cs:3612-3639`) bloquea solo: estados terminales (gate de estado) y guard fiscal/voucher (`DeleteGuards`). No hay guard de "último pasajero". En una Confirmada sin voucher ni CAE todavía, se puede borrar hasta dejar 0 pasajeros; el motor de estados no mira pasajeros. El voucher sí exige titular, pero el **estado queda inconsistente** (una venta firme sin nadie que viaje).
- **Tipo:** [BUG]
- **Severidad:** IMPORTANT.
- **Dirección de fix:** rechazar borrar el último pasajero de una reserva en venta firme (Confirmed+), con mensaje claro.

### I3 — Validación de fechas imposibles incompleta (fin antes que inicio)
- **Área:** Integridad de datos.
- **Problema:** reportado por el doc 2026-06-24 (Aéreo/Traslado/Paquete/Genérico sin validar fin<inicio; solo Hotel/Asistencia validan). **No re-verifiqué exhaustivamente** en esta pasada (no encontré una validación de fin<inicio en el árbol de servicios), así que lo dejo como **probable-abierto pendiente de confirmación**. Si se confirma, una fecha imposible se cuela a cabecera y voucher.
- **Tipo:** [BUG] (a confirmar)
- **Severidad:** IMPORTANT.
- **Dirección de fix:** validación uniforme fin≥inicio en los 6 tipos de servicio, en el boundary de creación/edición.

### I4 — KPIs agregados que pueden mezclar o inflar (reportado 2026-06-24, no re-verificado a fondo)
- **Área:** Reportes / cobranza.
- **Problema:** "cobrado este mes" podría inflarse con saldo a favor aplicado si no filtra `AffectsCash`; "pendiente de facturar"/"pendiente de cobrar" agregados que suman monedas distintas en un escalar. El detalle por moneda de la reserva SÍ está bien separado (`ReservaMoneyCalculator`); el riesgo vive en los **agregados de listado/dashboard**, no en la reserva individual.
- **Tipo:** [BUG] (a confirmar)
- **Severidad:** IMPORTANT (es display/decisión, no corrompe la plata de la reserva).
- **Dirección de fix:** auditar los SUM de KPI: excluir puentes `AffectsCash=false` y no sumar cross-moneda en un escalar.

### I5 — Factura A sin chequeo de coherencia letra ↔ condición IVA del receptor
- **Área:** Fiscal.
- **Problema:** reportado 2026-06-24; emitir Factura A a un receptor cuya condición IVA no la admite es rechazo seguro de ARCA. Marcado para el contador.
- **Tipo:** [FISCAL→accountant]
- **Severidad:** IMPORTANT.
- **Dirección de fix:** pre-chequeo letra↔condición antes de encolar el CAE (aviso o bloqueo blando), con criterio del contador.

---

## MINOR (pulido; no bloquean ni corrompen)

### M1 — `esEstadoCongelado` / `FullyInvoiced` en el front: ya no es "regla inventada"
- **Área:** Frontend detalle.
- **Problema/observación:** el doc 2026-06-24 lo marcó como regla inventada contra ADR-037. En el código actual, ocultar "Emitir factura" y congelar vouchers usa el **carril derivado del backend** `reserva.invoicingStatus === 'FullyInvoiced'` (`ReservaDetailPage.jsx:1760, :66-73`), y el propio comentario aclara que NO gobierna cobro (`:61-64`). Es decir: el front lee el rail del backend, no inventa. Es **defendible**. Lo dejo como MINOR porque "FullyInvoiced congela vouchers" es una decisión de negocio (¿querés reimprimir/emitir voucher de una totalmente facturada? hoy: no), que conviene confirmar con el dueño, no un bug.
- **Tipo:** [DECISION-needed]
- **Severidad:** MINOR.

### M2 — Reprogramar viaje permite mover el itinerario al pasado sin aviso
- **Área:** Datos / UX (reportado 2026-06-24).
- **Tipo:** [BUG] menor. **Severidad:** MINOR.
- **Dirección de fix:** aviso (no bloqueo) al reprogramar a una fecha pasada.

### M3 — `PendingOperatorRefund` es terminal de hecho pero por fuera de la matriz de transiciones
- **Área:** State machine.
- **Observación:** `PendingOperatorRefund` se alcanza por el flujo de cancelación (bypass de `UpdateStatusAsync`, `BookingCancellationService.cs:1561`) y no figura en `ReservaStatusTransitions.Forward/Revert`. No es un bug (es intencional: flujo dedicado), pero es deuda de claridad: el "mapa de estados" canónico no incluye dos de sus estados (PendingOperatorRefund, Archived). Documentarlo evita que un futuro cambio asuma que la matriz es completa.
- **Tipo:** [DECISION-needed] (documentación). **Severidad:** MINOR.

---

## Lo que verifiqué que NO es problema (para no inflar)

- **Doble conteo de NC en el cuadre/extracto:** no existe hoy (C2 del 2026-06-24 está cerrado). La regla `CountsInNetBilled` es correcta y única.
- **CAE duplicado:** cubierto por idempotencia (factura, NC total, ND).
- **Cancelación a medias por crash:** cubierto por la transacción envolvente.
- **Capacidades que "mienten":** no encontré una capacidad que diga `Allowed=true` donde el guard fino rechace; el test cruzado lo enforza.
- **La columna vertebral reserva→operador (costo→deuda, pago→saldo, por moneda):** bien conectada y con fuente única (`SupplierService` + `WorkflowStatusHelper.CountsForSupplierDebtByType`). El problema del operador es SOLO el circuito de cancelación/reembolso (C2), no el flujo normal.
- **La moneda de la ND NO está hard-coded a ARS:** sale de la factura original (`MonId`), con ARS solo como fallback legacy.

---

## TOP 8 — "must-fix para ser un núcleo confiable que una agencia pueda usar y podamos vender"

Orden sugerido (primero lo que desbloquea operación real y no necesita contador; intercalando lo fiscal que sí lo necesita para arrancar su gestión en paralelo):

1. **C1 — Habilitar cancelación/anulación multi-operador** (por línea de operador). Sin esto, las reservas reales (multi-proveedor) no se pueden deshacer dentro del sistema. Es el mayor agujero de uso real. [GAP]
2. **C2 — Conciliar la cuenta del operador con el reembolso/multa** (postear el reembolso recibido y la multa retenida en la cuenta del operador, cerrar el saldo negativo). Arranca con el contador en paralelo a C1. [FISCAL]
3. **C3 — Hacer configurable `InvoicingMode` por operador** (reseller vs intermediario) con pantalla + endpoint + firma del contador sobre la fórmula. Es requisito para "venderlo a todas las agencias". [GAP/FISCAL]
4. **I1 — ND multi-operador por línea** (resolver junto con C1, no después). [FISCAL]
5. **I2 — Guard de "último pasajero"** en venta firme. Barato, cierra una inconsistencia de datos visible. [BUG]
6. **I3 — Validación uniforme fin≥inicio** en los 6 tipos de servicio (confirmar primero el alcance real). [BUG]
7. **I5 — Pre-chequeo letra↔condición IVA antes del CAE** (aviso/bloqueo blando). Evita rechazos de ARCA en el día a día. [FISCAL]
8. **I4 — Auditar los KPIs agregados** (cash filter + no sumar cross-moneda). Restaura confianza en los números de tablero. [BUG]

**Fuera del TOP 8, pero antes de cerrar el "harden":** M3 (documentar el mapa de estados completo) y M1 (confirmar con el dueño la regla "FullyInvoiced congela vouchers"). Son baratos y reducen ambigüedad.

---

## Notas de método (anti-alucinación)

- Todo lo marcado ARREGLADO/CERRADO lo leí en el código de HEAD; cito file:line.
- I3 e I4 los marqué explícitamente como **a confirmar**: vienen del doc 2026-06-24 y no los re-verifiqué exhaustivamente en esta pasada.
- Todo lo fiscal (C2, C3, I1, I5) va marcado para el contador: el diseño puede avanzar, pero el comportamiento de plata/comprobantes no se toca sin su firma.
- No inventé estados, reglas de negocio ni números. Donde el código y los docs previos discrepaban, mandó el código.
