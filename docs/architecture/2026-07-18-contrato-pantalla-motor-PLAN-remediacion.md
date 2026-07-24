# Plan de remediación del contrato pantalla ↔ motor

**Fecha**: 2026-07-18
**Tipo**: diseño de arquitectura (SOLO plan; no se tocó código de producto).
**Insumo base**: `docs/architecture/2026-07-18-contrato-pantalla-motor-inventario.md` (el mapa verificado de 28 acciones, ~96 guards, 24 sin pre-chequeo, 7 callejones + 9 parciales, 4 JERGA, 3 VOCABULARIO-ROTO, "10 peores casos").
**Norte**: endurecer el core (fase 1). El dueño no programa; toca botones que el motor rechaza con carteles que no dicen qué hacer. Cada tanda de este plan elimina callejones ENTEROS (aviso previo + mensaje con camino + vocabulario), no mejoras a medias.

**Estado**: **APROBADO CON CAMBIOS** (review en `docs/architecture/2026-07-18-contrato-pantalla-motor-REVIEW-plan.md`). Los cambios están incorporados abajo (§Cambios tras review) y propagados a las tandas/decisiones afectadas.

---

## Cambios tras review (2026-07-18)

Seis puntos del reviewer, incorporados. Los dos primeros eran bloqueantes.

- **B1 (bloqueante) — Cómo se computa el pre-chequeo R1 en el GET de la ficha (Tanda 7).** Sin declararlo, "aditivo y barato" era falso: el guard real reconstruye el RefundCap por servicio (`ComputeUnanchoredOperatorRefundCapAsync`, caro). **Estrategia declarada:** short-circuit a nivel reserva — calcular `hasLiveSaleInvoice` UNA vez (`ReservaHasLiveSaleInvoiceAsync`, el mismo predicado que ancla R1); si hay factura viva → TODOS los servicios quedan "no bloqueados por R1" sin reconstruir nada. La reconstrucción per-servicio corre **sólo** en el caso sin-factura, y sólo para servicios con plata pagada al operador. Impacto en el GET: el GET de la ficha ya paga `GetOperatorPenaltySituationsAsync` (`ReservaService.cs:2822`) y `MutationGuards` (`:2748`); el costo nuevo (reconstrucción per-servicio) cae únicamente en la minoría (reserva sin factura viva + servicios pagados al operador). Detalle en Tanda 7.
- **B2 (bloqueante) — El cross-check de los flags per-pago (T6) y de R1 (T7) va como test de INTEGRACIÓN Postgres.** Esos flags dependen de estado en la DB (recibo Issued, factura CAE viva, pagos al operador), que el harness PURO del gate C2 no puede cruzar. **Regla dura de merge, por tanda:** cada flag/capacidad nueva de T6 y T7 lleva su test de integración Postgres que lo cruza contra el guard real; sin ese test, no mergea. (El gate C2 puro sigue cubriendo las capacidades que dependen sólo de estado/flags en memoria.)
- **M1 — Cubo explícito "aceptados como están / backlog".** Los ~24 guards que explotan-al-clic pero con mensaje entendible y que NO entran en las 9 tandas quedan listados en §4 (C1 aplicar-saldo, C4 factura duplicada, A6 negativos, D1 archive, etc.). Nada en limbo.
- **M2/M4 — Realineo de `serviceCancellationBlockReason` (Tanda 4) es un SWAP, no lógica nueva.** Verificado: `dto.ServiceCancellationBlockReason` se setea en `ReservaService.cs:2747-2748` desde `GetReservaCancellationBlockReasonAsync` (CAE **o** voucher). El swap es al método voucher-only YA existente `GetReservaVoucherOnlyBlockReasonAsync` (`MutationGuards.cs:419`), que el guard real ya usa. **Heads-up al dueño (en §6):** tras el swap, más servicios de reservas facturadas-sin-voucher van a ofrecerse como anulables — el motor ya lo permitía (ADR-044 T5), la pantalla dejará de frenar de más.
- **M3 — Decisión 1 de §6 reformulada como INFORMATIVA.** El freno de plata (candado R1) se mantiene y NO es elegible sacarlo; lo único que el dueño decide es el camino servido. Las 5 decisiones finales quedan separadas: informativas las no-elegibles, forks reales sólo las de producto.
- **M6 (menores)** — (a) acople de orden **T4→T7** declarado (comparten el texto del candado R1; T4 fija el vocabulario "anular" y T7 le agrega el `code`, no es invertible). (b) Los peores **#5 (T8) y #7 (T9) se despriorizaron a propósito**: T8 porque depende de una decisión de negocio pendiente del dueño; T9 por baja frecuencia relativa frente a los #1–#4. (c) Rutas corregidas: `ReservaCapabilities` en `src/TravelApi.Domain/Reservations/`, `MutationGuards` en `src/TravelApi.Infrastructure/Services/Reservations/`, `useFinanceActions` en `src/TravelWeb/src/features/payments/hooks/`. (d) **Cada tanda cierra con verificación end-to-end real (correr la app) antes de deploy, no sólo suite verde** — regla global, repetida en cada tanda.

---

## 0. Qué verifiqué contra el código real (no contra el inventario)

Antes de decidir, releí el código de las tres zonas que este plan juzga (no me apoyé sólo en el inventario):

| Afirmación a validar | Resultado | Evidencia |
|---|---|---|
| El ancla del candado R1 es estructural | **Confirmado**: `BookingCancellation.OriginatingInvoiceId` es FK no-null + índice único; el receivable Y del operador se deriva EXCLUSIVAMENTE de las `BookingCancellationLine` que cuelgan de ese `BookingCancellation`. | `BookingCancellationService.cs:892-910`, comentarios `:417-427` y `:912-929` |
| "Las capabilities sólo miran Status" | **FALSO** (premisa del pedido corregida). `ReservaCapabilityContext` ya carga 9 datos: Status, Balance, HasLiveCae, HasLiveVoucher, HasLiveEditAuth, HasAnyPayment, HasPendingOperatorPenalty, HasOperatorConfirmedService, OperatorPenaltyOutcome. Es una política PURA, server-side, testeada con gate cruzado C2. | `ReservaCapabilities.cs:60-69, 393-418`; contexto armado en `ReservaService.cs:2859-2869` |
| Existe un patrón de 409 estructurado en el repo | **Confirmado**. El workflow de aprobación ya devuelve `{ requiresApproval, requestType, entityType, entityId, message }` y el front lo mapea. | `CancellationsController.cs:225-227, 301-303, 1048`; `ClientCreditsController.cs:126-128`; front `useFinanceActions.js:95-129` |
| El front ya tiene acceso al body estructurado | **Confirmado**. `getApiErrorMessage` lee `error.payload` (JSON completo) antes que `error.message`. Un campo `code` estaría disponible sin cambiar el cliente HTTP. | `src/TravelWeb/src/lib/errors.js:134-147` |
| El swallow de "anular reserva" es deliberado | **Confirmado** y documentado en el propio archivo. Descarta TODO 409 por seguridad, incluidos los mensajes buenos. | `CancelarReservaInline.jsx:378-422` |
| El swallow de "pagar al proveedor" NO es deliberado | **Confirmado**: 4 `catch` tiran `ex.Message` y devuelven genérico; `DeleteSupplierPayment` (mismo controller) sí propaga. Sin comentario que lo justifique. | `SuppliersController.cs:377-383, 406-412` |

---

## 1. Decisiones de arquitectura (las 3 que el plan debía juzgar)

Formato: contexto → opciones → recomendación única → fundamento → consecuencias.

### Decisión A — El candado "anular servicio pagado al operador exige factura viva"

**Qué es hoy.** `EnsurePaidServiceCancellationHasReceivableAnchorAsync` (`BookingCancellationService.cs:892-910`, regla R1 del 2026-06-30) bloquea anular un servicio que ya tiene plata pagada al operador si la reserva todavía no tiene factura de venta viva. Sin esa factura no hay `BookingCancellation` (su FK `OriginatingInvoiceId` es no-null) → no hay línea que represente el "me lo tiene que devolver" → el reconciler (`SupplierCreditReconciler`) mintearía el negativo de caja del operador como **saldo a favor gastable** (plata que en realidad el operador debe devolver). Premisa que habilita el escenario: ADR-037 desacopló facturar de cobrar (se le puede pagar al operador antes de facturar al cliente).

Se eligió Camino B ("bloquear") sobre Camino A ("hacer nullable el ancla de factura en `BookingCancellation`") por riesgo del núcleo fiscal ADR-002.

**Opciones para HOY:**
- **A.1 — Mantener el bloqueo, pero con pre-chequeo en el front + mensaje con camino servido (botón "Emitir factura").** El botón de anular queda apagado con tooltip cuando hay plata-al-operador-sin-factura; si igual explota por carrera, el cartel trae el botón que lleva a emitir la factura.
- **A.2 — Reabrir Camino A**: hacer nullable `OriginatingInvoiceId`, permitir `BookingCancellation` sin factura y que la línea Y ancle el receivable de otra forma.

**Recomendación: A.1. No reabrir Camino A.**

**Fundamento (evidencia, no opinión):**
1. El ancla es **estructural**: FK no-null + índice único, y TODA la cadena receivable (línea Y → RefundCap → reconciler → saldo a favor del operador) se deriva de ahí. Hacerla nullable reabre el núcleo fiscal ADR-002 y reintroduce exactamente la fuga que R1 vino a cerrar (mintear negativo como saldo gastable). Es cambio de invariante de plata para un caso que tiene salida funcional limpia.
2. **ADR-037 es la premisa, no el problema**: si se le pagó al operador antes de facturar, el camino correcto es **completar la factura que falta**, no debilitar el ancla. El propio mensaje del candado ya dice el camino ("Emití la factura de venta").
3. **Que ADR-048 (modelo de estados) esté deployado NO baja el riesgo de A**: el riesgo de A vive en la derivación receivable/credit, no en la máquina de estados. Reevaluar A "porque ahora ADR-048 está entero" no cambia el veredicto.
4. **El defecto real no es el candado**: es que la pantalla ofrece la papelera sin pre-chequeo, y el cartel —aunque es criollo-con-camino— dice "cancelar este servicio" (vocabulario roto) y no trae botón. Eso es lo que A.1 arregla de raíz.

**Consecuencias.** Se mantiene la seguridad de plata intacta (cero cambio de esquema, cero migración). El costo se paga en UX (pre-chequeo + camino servido), que es justo lo que el dueño pide. Riesgo residual: la carrera (otra pestaña facturó/despagó en el medio) — cubierta por el mensaje-con-botón, no por el pre-chequeo.

**Rollback.** A.1 es aditivo (pre-chequeo + texto). Si algo falla, se revierte el front sin tocar el motor; el candado sigue protegiendo igual.

---

### Decisión B — Patrón canónico de PRE-CHEQUEO para los 24 casos sin aviso previo

**Corrección de premisa.** El pedido dice "hoy las capabilities sólo miran Status". Es **falso** (ver §0): `ReservaCapabilityContext` ya carga 9 datos y es la fuente única que el front consume para pintar botones. "Extender capabilities" NO es arquitectura nueva: es continuar la que ya existe y está testeada.

**Opciones:**
- **B.1 — Extender el patrón de capabilities** con los datos que ya se calculan para el DTO (o capabilities por sub-entidad cuando el guard es a nivel pago, no a nivel reserva).
- **B.2 — Endpoint de pre-flight por acción** (un round-trip extra que devuelve "podés/no podés + motivo").
- **B.3 — Respuesta 409 estructurada** (código + params) que el front mapea a mensaje propio.

**Recomendación: patrón EN CAPAS con B.1 como default, no una sola bala.** Los 24 casos no son homogéneos; forzar uno solo genera o duplicación o round-trips inútiles.

1. **Default (mayoría): B.1 — extender capabilities.** Para todo guard cuyos insumos sean hechos a nivel reserva que el DTO ya calcula (o puede calcular barato), se agrega el flag al contexto puro y una `Cap` nueva. Es lo más consistente, ya tiene gate cruzado (test C2 que impide que la capacidad mienta respecto del guard real) y el front ya sabe leerlo. Mata de raíz el "el botón se ofrece y después explota".
   - **Sub-caso: capabilities por PAGO puntual.** El bug #9 (editar/eliminar cobro) es a nivel del pago individual, no de la reserva. Se resuelve agregando flags de capacidad **por fila de pago** en el DTO del pago (recibo Issued / factura CAE viva), reusando los guards `MutationGuards.cs:133-159` y `DeleteGuards.cs:343-353` como fuente. Mismo patrón, distinta granularidad.
2. **Cuando el insumo NO existe hasta que el usuario elige un destino** (elegir reserva destino, elegir servicio con su moneda): **pre-chequeo client-side contra los datos que el picker ya tiene** (filtrar/deshabilitar opciones). Es lo que ya hacen los flujos bien resueltos (C7 reembolso recibido, D1 archivar). No inventa endpoint.
3. **Residual que igual se escapa por carrera / dato obsoleto**: **B.3 — 409 estructurado (código + params)** para que el front muestre mensaje específico y, cuando aplique, botón de siguiente paso. Reusa el precedente `requiresApproval` que YA existe.

**Se DESCARTA B.2 como patrón canónico.** Un pre-flight genérico por acción agrega un segundo round-trip y una **segunda fuente de verdad** que puede divergir de las capabilities — exactamente el bug que estamos arreglando (el hallazgo transversal de la sección A: `serviceCancellationBlockReason` diverge del guard real). El pre-flight sólo se justifica caso a caso cuando requiere un cálculo que el DTO no tiene (ej. `emission-preflight` de Factura A vs condición fiscal, B6c, que ya existe y está bien). Excepción, no regla.

**Consecuencias.** Una sola fuente de verdad (capabilities) para el 80%; el front deja de "adivinar por estado". Costo: cada guard nuevo pre-chequeado necesita su flag + su test cruzado (barato, ya hay andamiaje). Riesgo: si alguien agrega un flag pero olvida el test cruzado, la capacidad puede mentir → **regla dura: toda `Cap` nueva lleva su test cruzado contra el guard real** (gate de merge, ya existe para las actuales).

---

### Decisión C — Patrón canónico de MENSAJES

**Hoy conviven tres.** (i) backend manda texto crudo que el front muestra (mayoría, y en general ya es criollo-con-camino); (ii) el front descarta el texto bueno del backend (`CancelarReservaInline` tira 5 mensajes limpios; `SuppliersController` descarta 7); (iii) el front hardcodea genéricos (T5 "Ver PDF").

**Recomendación (con matiz, y con push-back al planteo original).** El planteo sugería "catálogo de códigos → el front dueño de TODOS los textos". **No lo recomiendo tal cual**, y el fundamento es evidencia del inventario: la GRAN mayoría de los ~96 mensajes del backend YA son criollo-con-camino y correctos. Reescribir los 96 hacia un catálogo front sería sobre-ingeniería (superficie enorme, riesgo alto) para un 90% que ya funciona, y rompería el `message` como fallback seguro y como salida para otros consumidores (no todo consumidor es la ficha).

**Patrón único = envelope de código de negocio ADITIVO:**
- El backend, en un rechazo de negocio, sigue mandando un `message` **seguro y en criollo** (el que ya tiene) **Y** agrega `code` (+ `params` opcionales). El `message` es el default y el fallback.
- El front es dueño del texto **sólo donde suma valor**: (a) para ofrecer un **botón de siguiente paso** concreto; (b) para **pisar** un mensaje que hoy es jerga/enum crudo; (c) para **des-swallowear** (mapear por `code` en vez de tirar un genérico ciego).
- Códigos aún no migrados → cae al `message` del backend (que ya es seguro). Cero big-bang.
- El swallow de `CancelarReservaInline` se reemplaza por **mapeo por código** (seguro: el front controla el texto vía `code`, no expone texto crudo, y recupera el mensaje real de INV-152, INV-100, etc.).

**Por qué este y no el catálogo total:** reusa el precedente `requiresApproval` (409 estructurado ya vivo), es incremental, backward-compatible, no reescribe 96 guards, y concentra el trabajo del front en los ~15 casos donde de verdad hace falta (botón de camino, jerga, swallow).

**Migración incremental:** cada tanda que toca un flujo agrega el `code` a ese endpoint y el mapeo en ese componente. No hay tanda de "migrar todos los mensajes".

**Consecuencias.** Backend y front dejan de pelear por el texto: el backend garantiza un piso seguro, el front enriquece donde importa. Riesgo: dos textos para el mismo caso (el `message` seguro y el del catálogo front) pueden divergir con el tiempo → mitigación: el front sólo mapea códigos con intención (botón/jerga/swallow); para el resto muestra el `message` del backend tal cual.

---

## 2. Reglas transversales del plan

- **Vocabulario (regla del dueño):** *Cancelar* = el cliente ABONA EL TOTAL. *Anular* = dejar sin efecto. Sacar un servicio de una reserva = **anular** (no "cancelar"). Los guards que dicen "cancelar este servicio / los servicios no se cancelan" se reescriben a "anular". (La UI de servicios ya se renombró el 2026-07-16; falta el backend.)
- **Nada de jerga al usuario:** cero IDs/GUIDs, cero enums en inglés, cero nombres de clase/tabla/estado interno. El único enum-leak confirmado (B6a) se mata reusando la constante limpia que YA existe.
- **Cada rechazo dice QUÉ HACER AHORA en criollo.**
- **Sin feature flags.** Las tandas salen directas.
- **Gates obligatorios (CLAUDE.md):** toda tanda cambia superficie visible → **gate UX (`ux-ui-disenador` + `docs/ux/guia-ux-gaston.md`) ANTES de implementar**, y **`data-exposure-reviewer` como último paso**. Marcado tanda por tanda.
- **Una sola fuente de verdad:** todo pre-chequeo nuevo espeja el guard real y lleva test cruzado (que la capacidad no mienta). **Si el pre-chequeo depende de estado en DB** (recibo Issued, factura CAE viva, pagos al operador, Payer) el cross-check va como **test de INTEGRACIÓN Postgres**, no como test puro — el harness puro C2 sólo cruza lo que depende de estado/flags en memoria. Regla dura de merge (aplica a T6 y T7).
- **Cierre end-to-end REAL antes de deploy (regla global, no negociable):** ninguna tanda se da por terminada con la suite verde. Antes de cada deploy se **corre la app real** y se camina el flujo de esa tanda a mano (lección 2026-07-02 y 2026-07-17: reviews + suites verdes NO alcanzaron). Se reporta qué se verificó end-to-end y qué no, con esas palabras.
- **Acople de orden T4→T7:** T4 corrige el vocabulario del texto del candado R1; T7 le agrega el `code` sobre ese texto ya corregido. **No es invertible: T4 antes que T7.**

---

## 3. Las tandas (ordenadas por dolor del dueño)

Cada tanda es deployable, cierra callejones enteros, y trae su caminata E2E. "Peor #N" refiere a la lista de los 10 peores del inventario.

### Tanda 1 — "Pagar al proveedor" deja de comerse los mensajes  (peor #1 + #10)
**Dolor:** el peor callejón del inventario. El backend calculó exactamente qué está mal (moneda equivocada, cargo ya liquidado, servicio de otro operador…) y el controller lo tira a la basura en 4 `catch`, dejando un genérico sin causa. Además "Nueva factura del proveedor" se ofrece a operadores que facturan directo al cliente.
**Cierra filas:** C5 completo (7 guards ocultos), C4 guard `:1727-1729` (InvoicingMode), C6 `:303-305` tope real (mismo patrón de input `max`).
**Alcance:**
- Backend `SuppliersController.cs:363-414`: `AddSupplierPayment` / `UpdateSupplierPayment` dejan de swallowear `ex.Message`; propagan como ya hace `DeleteSupplierPayment` (409/400 con el mensaje real, que ya es criollo-con-camino en `SupplierService.cs`). Genérico sólo para lo verdaderamente inesperado.
- Front `PagarProveedorInline.jsx`: pre-chequeo de los 2 sin aviso — filtrar el selector de servicio por la moneda del pago (`:2521-2523`), y avisar "esta reserva no tiene servicios de este proveedor" (`:2412-2413`) antes de enviar.
- Front `SupplierInvoicesSection.jsx:160`: apagar/explicar "Nueva factura" cuando el operador factura directo al cliente (`InvoicingMode`), en vez de dejar explotar.
- Front `UsarSaldoOperadorInline.jsx`: input de monto con tope real (deuda del destino), no `max={saldoDisponible}`.
**Archivos:** `SuppliersController.cs`, `PagarProveedorInline.jsx`, `SupplierInvoicesSection.jsx`, `UsarSaldoOperadorInline.jsx`. (Sin cambio en `SupplierService.cs`: sus mensajes ya son buenos.)
**Fija con:** E2E real (correr la app) — pagar al operador con moneda equivocada → cartel específico ("La moneda del pago no coincide…"); operador que factura directo → "Nueva factura" explica antes del clic. Test de integración: los 4 `catch` propagan el mensaje real.
**Gate UX:** SÍ (cambia visibilidad de botón y filtro de selector). **Decisión del dueño:** no (puro arreglo).

### Tanda 2 — Se puede pedir autorización desde la ficha  (peor #2)
**Dolor:** anular el comprobante de un cobro puede pedir autorización; la MISMA acción desde Cobranzas→Movimientos abre el modal para pedirla, pero desde la ficha —la pantalla que el dueño usa el 90% del tiempo— el cartel "necesitás autorización" no tiene ningún botón al lado.
**Cierra filas:** C3 (gap `requiresApproval`).
**Alcance:** Front `ReservaDetailPage.jsx` `handleVoidReceipt` (`:960-976`) y `handleIssueReceipt` (`:949-958`): al ver 409 `requiresApproval`, abrir el `RequestApprovalModal` ya existente, reusando el patrón `onApprovalRequired` de `src/TravelWeb/src/features/payments/hooks/useFinanceActions.js:95-129`. Cero backend (el backend ya manda el 409 estructurado correcto).
**Archivos:** `ReservaDetailPage.jsx` (y el hook/modal ya existentes).
**Fija con:** E2E real (correr la app) — Vendedor sin permiso anula un comprobante desde la ficha → aparece el modal para pedir autorización ahí mismo.
**Gate UX:** SÍ (agrega flujo de modal a una pantalla). **Decisión del dueño:** no.

### Tanda 3 — "Anular reserva" dice el motivo real  (peor #3 + #8)
**Dolor:** el swallow deliberado de `CancelarReservaInline` (correcto como intención de seguridad) se llevó puestos 5 mensajes buenos + el de multi-operador (INV-152), cuyo texto bueno vive en un componente MUERTO que nadie importa. Reintentar no sirve porque la causa no cambia.
**Cierra filas:** B1a, B1b, B1c, B1e, B1f, B1g.
**Alcance (introduce el envelope de código de la Decisión C):**
- Backend: los endpoints de anulación (`draft`/`confirm`/`annul-with-credit`) ya lanzan con códigos (INV-152, INV-081, INV-100, INV-093). Agregar `code` (aditivo) al body 409, manteniendo `message`.
- Front `CancelarReservaInline.jsx`: reemplazar el swallow ciego por un **mapa código→criollo propio del front** (multi-operador, factura CAE viva→camino NC, factura ya anulada, "actualizá la página", no-firme, sin pagador). Fallback neutro SÓLO para códigos no mapeados (mantiene la política de no exponer texto crudo).
- Retirar el componente muerto `CancelReservaModal.jsx`.
**Archivos:** `CancellationsController.cs`, `CancelarReservaInline.jsx`, borrar `CancelReservaModal.jsx`.
**Fija con:** E2E real (correr la app) — anular reserva multi-operador → mensaje específico y accionable; anular con factura CAE → mensaje con el camino de la NC. Test: cada código mapea a su texto; código desconocido cae al neutro.
**Gate UX:** SÍ (cambia el contenido de los carteles). **Decisión del dueño:** confirma la Decisión C (envelope aditivo).

### Tanda 4 — "Anular varios servicios" ayuda igual que anular uno + vocabulario  (peor #4 + los 3 VOCABULARIO-ROTO)
**Dolor:** elegir "en lote" (lo más natural con varios servicios) es lo que MENOS ayuda: cuando una fila falla muestra "Bloqueo fiscal — …" sin botón, mientras el flujo individual sí ofrece "Ver facturas de la reserva". Y todo el flujo habla en "cancelar" cuando la regla exige "anular".
**Cierra filas:** A2 (paridad de camino), y las 6 apariciones de VOCABULARIO-ROTO de A1/A3.
**Alcance:**
- Front `CancelarVariosServiciosInline.jsx:496-498`: por fila fallida, mostrar la causa real + la acción "Ver facturas de la reserva" (paridad con el flujo individual de `ServiceList.jsx:558-644`).
- Backend + front: renombrar "cancelar servicio / los servicios no se cancelan" → "anular" en `ReservaCapabilityPolicy.ServiceNotCancellableStatusReason` y en los mensajes de `CancelServiceAsync`/`MutationGuards` (incluye el mensaje del candado R1, que se termina de pulir en la Tanda 7).
- Corregir el hallazgo transversal de la sección A: `serviceCancellationBlockReason` se setea en `ReservaService.cs:2747-2748` desde `MutationGuards.GetReservaCancellationBlockReasonAsync` (`Infrastructure/Services/Reservations/MutationGuards.cs:398`), que usa la regla vieja (CAE **o** voucher) y frena de más. **Es un SWAP, no lógica nueva:** cambiar esa llamada por el método voucher-only ya existente `GetReservaVoucherOnlyBlockReasonAsync` (`MutationGuards.cs:419`), que es exactamente el que el guard real usa (`CancelServiceAsync:413`, ADR-044 T5). Así la pantalla deja de mostrar "bloqueo fiscal" en reservas que el motor ya aceptaría.
  - **Heads-up al dueño:** tras el swap, servicios de reservas facturadas-sin-voucher pasarán a ofrecerse como anulables. No es un cambio de política del motor —ya lo permitía—, es la pantalla dejando de frenar de más. Va como decisión informativa en §6.
**Archivos:** `src/TravelWeb/src/features/reservas/components/CancelarVariosServiciosInline.jsx`, `ServiceList.jsx`, `src/TravelApi.Domain/Reservations/ReservaCapabilities.cs`, `src/TravelApi.Infrastructure/Services/Reservations/MutationGuards.cs` (swap de llamada en `ReservaService.cs:2747-2748`), `BookingCancellationService.cs` (textos).
**Fija con:** E2E real (correr la app) — anular un lote donde un servicio tiene factura viva → la fila muestra causa + botón "Ver facturas de la reserva"; una reserva con factura pero sin voucher NO aparece pre-bloqueada. Test de integración Postgres del swap (reserva facturada-sin-voucher → block reason null).
**Gate UX:** SÍ. **Decisión del dueño:** confirma el rename cancelar→anular (su regla; se ejecuta) + toma nota del heads-up (más servicios anulables). **Acople:** T4 fija el vocabulario del texto del candado R1; T7 le agrega el `code` sobre ese texto ya corregido — **T4 va antes que T7, no es invertible.**

### Tanda 5 — "Emitir factura" sin jerga y apagada por estado  (peor #6)
**Dolor:** el único enum crudo confirmado que puede llegar a la pantalla del dueño (`'InManagement'` interpolado en una frase en español), y el botón no se apaga por estado de la reserva.
**Cierra filas:** B6a.
**Alcance:**
- Backend `InvoiceService.cs:377-379`: dejar de interpolar `{reserva.Status}`; reusar la constante limpia que YA existe `ReservaCapabilityPolicy.NotInvoiceableStatusReason` ("…se emite desde Confirmada en adelante, salvo en reservas anuladas.").
- Front `EmitirFacturaInline.jsx`: apagar el botón por `capabilities.CanInvoiceSale` (la capacidad YA existe, `EvaluateInvoiceSale`), no sólo por permiso.
**Archivos:** `InvoiceService.cs`, `EmitirFacturaInline.jsx`.
**Fija con:** E2E real (correr la app) — intentar facturar en un estado no facturable → botón apagado con motivo en criollo, sin ningún token en inglés. Test: el mensaje no contiene nombres de estado internos.
**Gate UX:** SÍ (apaga un botón). **Decisión del dueño:** no.

### Tanda 6 — Editar/Eliminar cobro mira el PAGO, no sólo la reserva  (peor #9)
**Dolor:** un cobro con recibo ya emitido o atado a factura con CAE sigue mostrando "Editar"/"Eliminar"; el rechazo llega recién después de llenar el formulario entero.
**Cierra filas:** C2 (los 4 guards a nivel pago).
**Alcance (Decisión B, granularidad por pago):**
- Backend: agregar al DTO de cada pago flags de capacidad por fila (recibo Issued / factura CAE viva), reusando `MutationGuards.cs:133-159` y `DeleteGuards.cs:343-353` como fuente única.
- Front `PaymentReceiptActions` (`ReservaDetailPage.jsx:390-550`): apagar "Editar"/"Eliminar" por esos flags del pago, con motivo, en vez de sólo por el estado de la reserva.
**Archivos:** DTO de pago + su armado en el service; `ReservaDetailPage.jsx`.
**Fija con:** E2E real (correr la app) — cobro con recibo emitido → "Editar"/"Eliminar" apagados con motivo antes de tocar el formulario. **Test de INTEGRACIÓN Postgres (regla dura de merge):** los flags per-pago dependen de estado en DB (recibo Issued, factura CAE viva), que el harness puro C2 no puede cruzar; el cross-check contra los guards reales (`MutationGuards.cs:133-159`, `DeleteGuards.cs:343-353`) va como integración, no como test puro. Sin ese test, no mergea.
**Gate UX:** SÍ. **Decisión del dueño:** no.

### Tanda 7 — Anular un servicio: pre-chequeo + candado R1 con camino servido  (Decisión A) 
**Dolor:** la papelera se ofrece sin avisar de voucher emitido, pago-sin-factura (R1) o falta de cliente; y cuando explota, el modal ofrece "gestionar nota de crédito" aunque el motivo real sea otro (voucher / cliente sin asignar).
**Cierra filas:** A1 (d) voucher, (e) R1 pago-sin-factura, (f) sin cliente; A3 (b) candado Confirmada, (d) CAE/voucher al editar.
**Acople:** va **después de T4** (T4 corrige el vocabulario del texto del candado R1; T7 le agrega el `code` sobre ese texto ya corregido — no invertible).
**Alcance (Decisión A = A.1 + Decisión B):**
- **Cómo se computa el pre-chequeo R1 en el GET de la ficha (declaración obligada — B1 del review):**
  - **Short-circuit a nivel reserva, UNA vez:** calcular `hasLiveSaleInvoice` con `ReservaHasLiveSaleInvoiceAsync` (el MISMO predicado que ancla R1 en `CancelServiceAsync:435` y en `EnsurePaidServiceCancellationHasReceivableAnchorAsync`). Si hay factura viva → **todos** los servicios quedan "no bloqueados por R1" y NO se reconstruye nada (R1 sólo muerde sin factura).
  - **Reconstrucción per-servicio SÓLO en el caso sin-factura:** si no hay factura viva, para cada servicio con plata pagada al operador se evalúa su RefundCap reusando el mismo núcleo read-only que el guard real (`ComputeUnanchoredOperatorRefundCapAsync`). Servicios impagos al operador se saltan (RefundCap 0, no muerde).
  - **Impacto en el GET:** el GET de la ficha ya paga `GetOperatorPenaltySituationsAsync` (`ReservaService.cs:2822`) y `MutationGuards` (`:2748`). El costo NUEVO (reconstrucción per-servicio) cae únicamente en la minoría: reserva **sin** factura viva **y** con servicios pagados al operador. En el caso normal (con factura) es un solo predicado booleano. Esto es lo que hace "aditivo y barato" verdadero para R1; sin el short-circuit no lo sería.
- Front `ServiceList.jsx`: pre-chequear voucher emitido / falta de cliente / R1 (plata al operador sin factura) antes de ofrecer la papelera, usando los flags nuevos de capabilities — apagar con tooltip que dice el camino.
- Front: cuando igual explota por carrera, el modal muestra el mensaje según el **motivo real** (mapeo por código, no el texto fijo de "nota de crédito"), y para R1 trae el botón **"Emitir factura"** (camino servido). Para (d) voucher y (f) cliente, el botón correcto (no "Ver facturas").
- Backend: el candado R1 se **mantiene** (Decisión A). Sólo se pule su texto (vocabulario "anular", ya iniciado en T4) y se le asigna `code` para el mapeo del front.
- A3(b): linkear el error del formulario de edición al botón "Pedí autorización" que ya existe en la franja superior (hoy el usuario lo tiene que encontrar solo).
**Archivos:** `src/TravelWeb/src/features/reservas/components/ServiceList.jsx`, `ServiceInlineCard.jsx`, `src/TravelApi.Domain/Reservations/ReservaCapabilities.cs` (+ contexto en `ReservaService.cs:2859`), `BookingCancellationService.cs` (texto+code del candado + reuso de `ComputeUnanchoredOperatorRefundCapAsync` para el pre-chequeo), `CancellationsController.cs`.
**Fija con:** E2E real (correr la app) — servicio pagado al operador sin factura → papelera apagada con tooltip "emití la factura primero"; si se fuerza, cartel con botón "Emitir factura". Servicio con voucher → mensaje correcto de voucher (no de NC). **Test de INTEGRACIÓN Postgres (regla dura de merge):** el flag R1 y los de voucher/cliente dependen de estado en DB (pagos al operador, factura viva, voucher emitido, Payer); el cross-check contra el guard real (`EnsurePaidServiceCancellationHasReceivableAnchorAsync`) va como integración, no como test puro. Sin ese test, no mergea.
**Gate UX:** SÍ. **Decisión del dueño:** INFORMATIVA — el freno de plata se mantiene (no es elegible sacarlo); lo que valida es el camino servido (botón "Emitir factura"). Ver §6.1.

### Tanda 8 — "Deshacer multa" con impuestos: dar un camino  (peor #5) — DESPRIORIZADA A PROPÓSITO
**Por qué va acá y no antes:** es el peor #5, pero **depende de una decisión de negocio del dueño** (a dónde mandar el caso, D3). No se puede codear sin esa respuesta; por eso queda parada abajo, no por poco dolor.
**Dolor:** "El comprobante de esta multa tiene impuestos asociados. Este caso lo tiene que revisar una persona." — sin decir quién, cómo, ni desde dónde. Caso real de negocio, cero camino.
**Cierra filas:** B2d.
**Alcance:** mensaje B2d (`BookingCancellationService.cs:8427-8429`) + front `DeshacerMultaEmitidaInline.jsx`: cambiar "una persona" por un camino concreto **que el dueño debe definir** (ver Decisiones). Patrón de referencia: el mismo componente ya resuelve bien B2c con el botón "Ir a la cuenta del cliente".
**Archivos:** `BookingCancellationService.cs` (texto), `DeshacerMultaEmitidaInline.jsx`.
**Fija con:** E2E real (correr la app) — deshacer multa con IIBB → el usuario ve a dónde ir a resolverlo.
**Gate UX:** SÍ. **Decisión del dueño:** SÍ — a dónde mandarlo (bandeja de revisión / Cobranzas / etc.).

### Tanda 9 — "Ver PDF" de la devolución (T5) da un camino  (peor #7) — DESPRIORIZADA A PROPÓSITO
**Por qué va última:** es el peor #7, pero de **baja frecuencia relativa** frente a los #1–#4 (falla de apertura de PDF en un banner puntual, no un flujo diario). Se atiende, pero después de lo que el dueño toca todos los días.
**Dolor:** en un flujo de mandarle la devolución al cliente (urgencia alta), "No se pudo abrir la nota de crédito." descarta cualquier detalle y no ofrece siguiente paso.
**Cierra filas:** B5a (y de paso B5c, mismo componente).
**Alcance:** Front `PartialCreditNoteEmissionPanel.jsx` (`pdf()` `:81-91`, `send()` `:93-107`): mostrar el detalle real / un siguiente paso concreto en vez del genérico hardcodeado.
**Archivos:** `PartialCreditNoteEmissionPanel.jsx`.
**Fija con:** E2E real (correr la app) — forzar un fallo al abrir el PDF de la NC → el usuario ve qué pasó y qué hacer (reintentar / avisar), no un cartel muerto.
**Gate UX:** SÍ. **Decisión del dueño:** no.

---

## 4. Qué queda fuera (y por qué)

### 4.1 Descartado del alcance (con fundamento)

- **Casos "prácticamente inalcanzables" o "código muerto"** (A1 a/b, A4 d, `CatalogCreates.cs:150-152`): no se tocan salvo que emerjan; el inventario los marcó no-confirmados. No gastar tandas ahí.
- **Ley 25.345 (C1 devolución en efectivo)**: el aviso no-bloqueante actual es correcto de negocio; el neto lo calcula el server. Convertirlo en pre-chequeo duro es zona fiscal — no entra en este plan de coherencia de contrato; queda anotado.
- **`AfipService.cs` (+3000 líneas, leído parcial)**: zona de riesgo no cerrada. No hay hallazgo confirmado de leak por el camino auditado; auditarla entera es un trabajo aparte, no una tanda de este plan.
- **Migrar los 96 mensajes a catálogo front**: descartado por Decisión C (sobre-ingeniería; el 90% ya es criollo-con-camino).

### 4.2 Aceptados COMO ESTÁN / backlog (M1 del review — nada en limbo)

Estos ~24 guards **explotan-al-clic pero con mensaje entendible** (criollo, se entiende qué pasó aunque no haya aviso previo). NO son callejones sin salida ni jerga; su costo de arreglo (pre-chequeo) no justifica una tanda propia hoy. Se aceptan tal cual y quedan en backlog explícito. Si el dueño reporta dolor real en alguno, se promueve a tanda.

| Ref inventario | Acción | Por qué se acepta hoy |
|---|---|---|
| A6 (c) | Confirmar costo con costo/impuesto negativo | Mensaje exacto ("El costo no puede ser menor a cero"), inputs editables en la misma celda; se corrige en el momento. Falta sólo `min="0"` — mejora cosmética, no callejón. |
| C1 aplicar-saldo (`:1587-1592`, `:1596-1601` INV-097) | Monto > saldo / monto > deuda del destino | Mensaje claro; explota pero se entiende. El picker ya limita al saldo; falta el tope real de la deuda del destino. Mismo patrón `max` que C6/T1. |
| C1 aplicar-saldo (`:1551-1557`, `:1562-1568`) | Reserva destino no firme / sin deuda en esa moneda | Mensaje con camino ("Pasala a En gestión primero"). El endpoint de deuda-por-moneda para clientes no existe (limitación conocida); se acepta hasta que se construya. |
| C1 ownership (`:1544-1546`, `:2237-2239`) | Reserva de otro usuario | 403 con mensaje entendible; el front no conoce el responsable de antemano. Bajo dolor. |
| C4 (`:1738-1739`) | Factura de proveedor con número duplicado | "Ya existe una factura con ese número…" — claro; sólo falta chequeo previo al `required` HTML. |
| C4 (`:1763-1766`, `:1774-1775`) | Servicio no confirmado / importe > costo | Ya pre-chequeados parcial; mensaje claro. |
| D1 | Archivar con datos obsoletos (otra pestaña cambió la reserva) | Ya pre-chequeado doble (tooltip + re-check antes del modal); sólo alcanzable por carrera. El término "Operativo" vs "En viaje" (inconsistencia de glosario) se anota como fix cosmético de una línea, no tanda. |
| C2 (los 4, ya en Tanda 6) | — | Cubiertos por T6; no backlog. |

**Nota:** los ítems C1 comparten el patrón "input con `max` al tope equivocado". Si se junta dolor, se podría hacer UNA mini-tanda de "topes reales en inputs de saldo" (C1 + lo ya cubierto en T1/C6). No se agenda todavía.

---

## 5. Riesgos del plan y cómo se controlan

| Riesgo | Control |
|---|---|
| Un pre-chequeo nuevo (capability) diverge del guard real y miente | Regla dura: toda `Cap`/flag nuevo lleva **test cruzado** contra el guard real (gate de merge). Si depende de estado en memoria → harness puro C2 (ya existe). **Si depende de estado en DB (T6 flags per-pago, T7 R1) → test de INTEGRACIÓN Postgres** (el puro no puede cruzarlo). |
| El pre-chequeo R1 en el GET encarece la ficha | Short-circuit a nivel reserva: `hasLiveSaleInvoice` una vez → con factura, cero reconstrucción; la reconstrucción per-servicio sólo en el caso sin-factura y sólo para servicios pagados al operador (minoría). Ver Tanda 7. |
| Al des-swallowear (T1, T3) se filtra texto interno crudo | El `data-exposure-reviewer` corre como último paso de CADA tanda; en T3 el front mapea por `code`, no muestra texto crudo. |
| Dos textos para el mismo caso (message backend + catálogo front) divergen | El front sólo mapea códigos con intención (botón/jerga/swallow); el resto muestra el `message` seguro del backend tal cual. |
| Cambiar textos rompe la guía UX firmada | Gate `ux-ui-disenador` ANTES de cada tanda (todas cambian superficie visible). |
| Vocabulario: renombrar en backend rompe algún test que matchea el texto viejo | Barrer seeds/tests de integración al cambiar textos (lección 2026-07-16: CI Postgres). |

**Rollback general:** cada tanda es aditiva y deployable sola. T1/T3 (des-swallow) revierten en el controller/componente sin tocar el motor. Ningún cambio de esquema ni migración en todo el plan (Decisión A mantiene el ancla; Decisión B/C son aditivas).

---

## 6. Para el dueño (en criollo)

Separo lo que **te aviso** (ya está decidido por seguridad de la plata o por tus propias reglas — no elegís) de lo que **elegís vos** (moldea el producto).

### 6.A — Informativas (te aviso; no hay nada que elegir)

**I1. El freno de "anular un servicio ya pagado al operador cuando todavía no hay factura" se QUEDA.**
Si lo sacáramos, el sistema podría mostrarte como "plata a favor tuya" algo que en realidad el operador te tiene que devolver — un agujero de plata. Ese freno no está en discusión. Lo que hacemos es que **deje de ser molesto**: la papelera va a aparecer apagada avisándote antes, y si igual lo intentás, el cartel te va a poner el botón **"Emitir factura"** al lado para resolverlo en el momento. (Lo único que elegís es que ese camino servido te parezca bien — ver D1.)

**I2. Vocabulario "cancelar → anular" en servicios: se corrige (es tu propia regla).**
Sacar un servicio de una reserva es **anular** (dejarlo sin efecto), no "cancelar". La pantalla ya lo dice bien; corregimos los carteles que vienen de adentro del sistema. No hay nada que decidir, sólo te aviso que se hace.

**I3. Heads-up (Tanda 4): vas a ver más servicios "anulables" que antes.**
Hoy la pantalla te frena de anular servicios en reservas que ya facturaste, aunque el sistema por dentro sí te dejaría (mientras no hayas entregado el voucher). Vamos a corregir esa traba de más. Resultado: **más servicios de reservas facturadas te van a aparecer como anulables.** No es un permiso nuevo — el motor ya lo permitía; la pantalla dejará de frenarte de más.

### 6.B — Elegís vos (moldea el producto) — ✅ LAS 4 FIRMADAS POR GASTON (2026-07-18, todas con la recomendada)

D1: botón "Emitir factura" en el cartel. D2: mejorar solo los rotos. D3: bandeja
de revisión en Cobranzas con aviso y enlace. D4: arrancar por "pagar al
proveedor", después autorización desde la ficha, y el resto en orden del plan.
Las 3 informativas (I1 freno se queda, I2 vocabulario, I3 heads-up) comunicadas.

**D1. El camino servido del freno I1: ¿te sirve el botón "Emitir factura" al lado del cartel?**
→ **Recomiendo: sí, botón "Emitir factura" en el cartel** para resolverlo ahí mismo. (Si preferís que te mande a otro lado, decilo.)

**D2. Los mensajes de error: ¿mejoramos sólo los que hacen falta?**
Hoy la mayoría ya están bien escritos. Te propongo **NO reescribirlos todos** (son casi 100 y romperíamos cosas que andan): el sistema manda siempre un texto seguro, y mejoramos a mano sólo los pocos donde falta un botón de "qué hacer" o donde hoy sale una palabra técnica.
→ **Recomiendo: mejorar sólo los que lo necesitan, no reescribir todo.**

**D3. "Deshacer multa" cuando tiene impuestos (Tanda 8): ¿a dónde te mando?**
Hoy dice "esto lo tiene que revisar una persona" y no dice quién ni dónde. Necesito el camino real: ¿bandeja de "casos a revisar" en Cobranzas con aviso "resolvelo desde ahí", u otro lugar? Sin tu respuesta la Tanda 8 queda parada (está despriorizada a propósito).
→ **Recomiendo: bandeja de revisión en Cobranzas con aviso claro** — pero es tu operación, decidilo vos.

**D4. El orden de arranque.**
→ **Recomiendo: arrancar por "pagar al proveedor"** (el que más te duele), después "pedir autorización desde la ficha", y seguir el orden del plan. Si hay otro que te urge más, lo movemos.

---

## 7. Autocrítica (lo que un revisor podría objetar)

- **Verifiqué en el repo las 3 decisiones**, no sólo en el inventario (§0). Lo que NO reverifiqué línea por línea: las citas de secciones que el inventario ya marcó "verificadas a mano" (ej. `PaymentService.IssueReceiptAsync` primeros 4 guards, `AfipService.cs`). Las tomo del inventario, que es documento verificado; si una tanda toca esos puntos, el senior reverifica antes de codear.
- **Acoplamiento oculto que introduzco:** cada flag nuevo de capability acopla el DTO al guard. Mitigado con el test cruzado obligatorio (misma disciplina que las 9 capabilities actuales).
- **Posible sub-diseño:** Tanda 8 depende de una decisión de negocio del dueño; sin su respuesta, queda a medias. Por eso está marcada como decisión, no como "listo para codear".
- **Posible sobre-diseño evitado:** rechacé el catálogo total de mensajes y el pre-flight genérico; ambos habrían sido superficie enorme para poco retorno.
- **Lo que este plan NO arregla:** la zona `AfipService.cs` y Ley 25.345 quedan explícitamente afuera (§4). No las doy por cerradas.
- **Testeable:** cada tanda trae E2E real + test cruzado; ninguna es "confiá en que anda". El cierre end-to-end (correr la app) es regla global, no opcional.
- **Cambios del review incorporados (§Cambios tras review):** los 2 bloqueantes (cómputo R1 en el GET con short-circuit; cross-check por integración Postgres para flags con estado en DB) están resueltos, más el cubo backlog (§4.2), el swap confirmado de M4, la reformulación informativa/fork de §6, y las rutas corregidas. Verifiqué en el repo lo que el review pidió confirmar: `MutationGuards` en `Infrastructure/Services/Reservations/` con `GetReservaVoucherOnlyBlockReasonAsync` en `:419`, y `ServiceCancellationBlockReason` seteado en `ReservaService.cs:2747-2748` — el realineo de T4 es un swap, no lógica nueva.

---

## 8. Addendum (2026-07-23) — Se reabre Camino A: la premisa de la Decisión A cambió

**Este addendum NO borra nada de arriba** (F-6): la Decisión A (§1, "Decisión A") documentó correctamente el estado del código y el razonamiento **al 2026-07-18**, con la premisa de negocio vigente en ese momento. Ese razonamiento sigue siendo válido *para esa premisa*. Lo que cambió es la premisa, no el análisis técnico.

### Qué cambió

El dueño (Gastón) tomó una decisión de negocio nueva el 2026-07-23, con respaldo fiscal explícito: **la factura de venta deja de ser requisito para anular un servicio o una reserva con plata ya pagada al operador.** Fundamento fiscal (Ley de IVA, art. 5 inc. b): el hecho imponible de una operación de servicios nace con la prestación o el cobro AL CLIENTE — nunca con el pago que la agencia le hace a SU operador. Exigir una factura de venta como condición para registrar un movimiento que es, en esencia, una cuenta a cobrar contra el operador, no tiene sustento fiscal: son dos hechos económicos distintos (venta al cliente vs. compra al operador) que la Decisión A original había atado por una razón estructural del modelo de datos, no por una razón fiscal.

Esto invalida la premisa central de la Decisión A original: *"Sin esa factura no hay `BookingCancellation` (su FK `OriginatingInvoiceId` es no-null) → no hay línea que represente el receivable → hay que BLOQUEAR."* La premisa "sin factura no puede existir el ancla" era una limitación de diseño (FK no-null), no una regla de negocio ni una regla fiscal. El dueño, con el respaldo fiscal de arriba, decidió que esa limitación se levanta.

### Qué se reabrió (Camino A, antes descartado en A.2)

`docs/architecture/2026-07-18-contrato-pantalla-motor-PLAN-remediacion.md` §1 recomendaba **A.1 — no reabrir Camino A** (no hacer nullable `OriginatingInvoiceId`) por el riesgo de "reintroduce exactamente la fuga que R1 vino a cerrar". Esa fuga (el reconciler mintiendo un negativo de caja del operador como saldo a favor gastable) se sigue evitando, pero con **Camino A endurecido**: en vez de mantener el ancla obligatoria, `BookingCancellation.OriginatingInvoiceId` pasa a ser **opcional** (`int?`), y la línea (`BookingCancellationLine`, con su `RefundCap`) que representa el receivable del operador se crea **SIEMPRE** — con factura como ancla fiscal si existe, o sin ancla (`OriginatingInvoiceId` null) si no existe. La fuga se cierra por la existencia de la LÍNEA, no por la existencia de la FACTURA: son dos cosas distintas que el diseño original de 2026-06-30 había fusionado.

Un BC sin ancla fiscal (`OriginatingInvoiceId` null) queda con un candado duro nuevo ("guard R4"): **jamás emite Nota de Crédito ni Nota de Débito, ni transiciona a ningún estado fiscal** (permanece en `Drafted` para siempre) — pero su línea sigue anclando el receivable del operador para que `SupplierCreditReconciler` nunca lo confunda con saldo a favor consumible. Esto es exactamente lo que la Decisión A original temía perder ("la cadena receivable se deriva de ahí"), solo que ahora esa cadena arranca en la LÍNEA, no en la FACTURA.

### Qué NO cambió (siguen bloqueando, decisión del dueño 2026-07-23)

De los 4 candados de la familia R1 (§ Decisión A original + extensiones posteriores: reasignación 2026-07-01, downgrade 2026-07-21), **2 se eliminaron** (cancelar un servicio suelto, anular la reserva entera — ahora SIEMPRE anclan en vez de bloquear) y **2 se mantienen** (reasignar el operador/moneda de un servicio, bajar el estado de un servicio de Confirmado a no-confirmado). El dueño decidió mantener estos dos porque, a diferencia de cancelar/anular, esas dos acciones NO dejan ningún rastro del receivable (no crean una `BookingCancellationLine`) — ahí la fuga original sigue siendo real y sigue sin tener un camino servido distinto de "resolvé el reembolso con el operador primero". Sus mensajes se actualizaron para ya no pedir "emití la factura" (dejó de ser el camino), sino a gestionar el reembolso con el operador.

### Ver también

- Detalle completo del diseño e implementación: obra "anular servicio/reserva con pagos al operador SIN factura de venta" (2026-07-23), `src/TravelApi.Domain/Entities/BookingCancellation.cs`, `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` (bloque comentado "Obra 'anular sin factura'" antes de `EnsureOperatorReceivableAnchorLinesAsync`), migración `Adr049_AnnulWithoutInvoice_MakeOriginatingInvoiceIdOptional`.
- Reglas de la Constitución del producto citadas en el brief de esta obra: F-1, F-2, F-5, F-6, F-11, T-1, T-2, T-6, T-7, T-8, T-13, PR-6, PR-12 (`docs/estandares/2026-07-22-constitucion-producto-v1.md`).
