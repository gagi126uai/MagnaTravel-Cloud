# Diseño: Cuenta corriente del cliente + Descuento con tope y autorización

Fecha: 2026-06-26
Autor: Software Architect (propuesta — NO implementado)
Estado: PROPUESTO. Pendiente de decisiones de Gastón (negocio/UX) y de firma del contador (lo fiscal).

Este documento NO escribe código. Es el plano de las dos features que pidió Gastón, apoyado en
el código real, con los puntos exactos donde se enchufan, los riesgos contra el prepago puro
(ADR-036) y una lista de preguntas para cerrar antes de construir.

> Nota de método: separo SIEMPRE lo que es **decisión de negocio/UX (para Gastón)** de lo que es
> **decisión fiscal/contable (para el contador)**. No mezclo. Donde hay más de un camino válido,
> dejo UNA recomendación.

---

# FEATURE A — Cuenta corriente además de prepago

## A.0 Qué hay hoy (verificado en el código)

Hoy el sistema es **prepago puro** (ADR-036). El cliente tiene que quedar saldado ANTES de viajar:

- El candado duro vive en una función pura:
  `ReservationEconomicPolicy.IsClientFullyPaid(decimal balance)` →
  `src/TravelApi.Domain/Entities/ReservationEconomicPolicy.cs:34` (es `balance <= 0`).
- Se aplica en DOS lugares, con la misma regla, para que no diverjan:
  - Pase **manual** a "En viaje": `ReservaService.EnsureCanStartTravelingAsync` →
    `src/TravelApi.Infrastructure/Services/ReservaService.cs:4621` (lanza si el cliente debe).
  - Pase **automático** del job nocturno: `AutoTransitionConfirmedToTravelingAsync` →
    `src/TravelApi.Infrastructure/Services/ReservaLifecycleAutomationService.cs:309`.
- El candado es **incondicional**: NO mira la llave `RequireFullPaymentForOperativeStatus`
  (`src/TravelApi.Domain/Entities/OperationalFinanceSettings.cs:15`). En prepago el cliente paga
  el 100% siempre (comentario explícito en `ReservationEconomicPolicy.cs:24-32`).

Campos de cliente relevantes:

- `Customer.CreditLimit` → `src/TravelApi.Domain/Entities/Customer.cs:25`. **Campo MUERTO**: se
  carga pero nunca valida nada (lo confirma este diseño; no encontré ningún lector).
- `Customer.CurrentBalance` → `src/TravelApi.Domain/Entities/Customer.cs:29`. **ZOMBIE**: nunca se
  escribe ni se lee desde ADR-023. El saldo a cobrar del cliente se DERIVA en
  `FinancePositionService` (fuente única). **No lo resucito** (sería una cuarta fuente de verdad).

La verdad del saldo del cliente (cuánto te debe, por moneda) ya existe y es la fuente única:
- `FinancePositionService.GetCustomerReceivableByCurrencyAsync(customerId)` →
  `src/TravelApi.Infrastructure/Services/FinancePositionService.cs:74`.
- Sale de `ReservaMoneyByCurrency` (saldo materializado por reserva y moneda), filtrando
  `ReceivableDebtStatuses` (= `EstadoReserva.SaleFirmStatuses` = InManagement/Confirmed/Closed) y
  `Balance > 0` (`FinancePositionService.cs:37,51`).

El saldo por reserva lo calcula `ReservaMoneyCalculator` (puro, por moneda) →
`src/TravelApi.Domain/Reservations/ReservaMoneyCalculator.cs:37`.

## A.1 La decisión de raíz: ¿dónde vive el "modo de cobro"?

Tres opciones: por agencia (global), por cliente, por reserva.

**Recomendación: POR CLIENTE, con un default que pone la agencia.** Es lo que hacen los ERP
(Odoo, SAP) y es lo que pide el caso real de Gastón ("agencias que se manejan de las 2 formas"):
una misma agencia tiene clientes prepago (mostrador, ocasional) y clientes a cuenta (empresas,
mayoristas, corporativo). El modo es una propiedad del CLIENTE, no del tenant ni de cada venta.

Modelo propuesto (aditivo, sin tocar lo existente):

- `OperationalFinanceSettings.DefaultCustomerBillingMode` (enum: `Prepaid` | `Account`), default
  `Prepaid` → byte-idéntico a hoy. Es el modo con el que nace un cliente nuevo.
- `Customer.BillingMode` (enum `Prepaid` | `Account`, **nullable**; null = "heredar el default de la
  agencia"). Nullable a propósito: no migramos a NOT NULL sobre clientes existentes y "heredar"
  es un estado real distinto de "fijado a mano".
- `Customer.CreditLimit` (YA EXISTE, `Customer.cs:25`): **se reutiliza** como el límite de la cuenta
  corriente. Hoy es 0 y muerto; pasa a tener significado SOLO para clientes en modo `Account`.
- `Customer.PaymentTermsDays` (NUEVO, int, default 0): días de plazo de la cuenta del cliente
  (vencimiento). 0 = sin plazo definido (ver A.4).

> Por qué NO por reserva: complica la cabeza del usuario ("¿esta venta es a cuenta o no?") y rompe
> la idea de "límite del cliente" (el límite es contra el TOTAL que te debe el cliente, no contra una
> venta suelta). Si más adelante aparece el caso "este cliente a cuenta, pero ESTA venta puntual la
> quiero prepaga", se modela como override por reserva en una v2; hoy sería sobre-ingeniería.

## A.2 El candado de pago para viajar (lo más delicado vs ADR-036)

Hoy: una reserva NO pasa a "En viaje" si el cliente debe (`IsClientFullyPaid`). En cuenta corriente,
el cliente **puede viajar debiendo**, dentro de su límite y al día con sus plazos.

**Diseño: el candado se vuelve CONDICIONAL al modo del cliente, sin romper el camino prepago.**

- Cliente `Prepaid` → la regla de hoy, sin cambios: `IsClientFullyPaid(balance)` debe ser true.
- Cliente `Account` → el candado de "saldado" se REEMPLAZA por un candado de **crédito**:
  - Bloquea si: (exposición total del cliente que viaja) supera `CreditLimit`, **o** el cliente está
    en **mora** (tiene saldo vencido según sus plazos — ver A.4). Configurable si esto avisa o frena
    (ver pregunta 4).

Problema técnico honesto (no lo escondo): el candado de hoy es una función **pura** que solo recibe
el `balance` de UNA reserva (`IsClientFullyPaid(decimal)`). El candado de cuenta corriente necesita
la **exposición TOTAL del cliente** (suma de todas sus reservas en firme), que es un dato de base
(`FinancePositionService`). Por eso:

- `IsClientFullyPaid` se queda **igual** (es el camino prepago, no se toca → cero riesgo de
  regresión sobre lo ya homologado).
- Se agrega un evaluador NUEVO, p. ej. `ClientCreditPolicy.EvaluateCanTravel(ClientCreditContext)`,
  que recibe: modo del cliente, límite, exposición total actual del cliente (por moneda), saldo
  vencido, y el balance de ESTA reserva. Devuelve `Allowed` + motivo legible (mismo patrón `Cap` de
  `ReservaCapabilities.cs:15`).
- `EnsureCanStartTravelingAsync` (`ReservaService.cs:4621`) y el job
  (`ReservaLifecycleAutomationService.cs:309`) bifurcan: si el cliente es `Prepaid`, llaman a
  `IsClientFullyPaid` (hoy); si es `Account`, llaman al evaluador nuevo (que ya hace una lectura de
  la exposición del cliente vía `FinancePositionService`).

Interacción con capacidades/estados ya construidos:

- `ReservaCapabilities.CanRegisterPayment` (`ReservaCapabilities.cs:447`) NO cambia: en cuenta
  corriente igual se cobra cuando hay deuda (`Balance > 0`) y la venta es firme. Cobrar más tarde es
  justamente el punto de la cuenta corriente.
- `CanInvoiceSale` (ADR-037, `ReservaCapabilities.cs:415`) NO cambia: facturar ya está desacoplado
  del cobro. Esto JUEGA A FAVOR: en cuenta corriente lo normal es facturar primero y cobrar después.
- El cierre por fin de viaje (`AutoTransitionTravelingToClosedAsync`,
  `ReservaLifecycleAutomationService.cs:348`) hoy exige `Balance <= 0` para cerrar. **Acá hay una
  decisión** (pregunta 5): en cuenta corriente una reserva puede TERMINAR el viaje y seguir debiendo.
  Si dejamos el gate como está, las reservas a cuenta quedarían "En viaje" eternamente con deuda. Hay
  que permitir cerrar con deuda para clientes `Account` (la deuda sigue viva en la cuenta del cliente
  igual; `FinancePositionService` ya incluye `Closed` con deuda en el AR — `FinancePositionService.cs:37`).

## A.3 Límite de crédito: ¿avisa o bloquea? ¿dónde se chequea?

**Recomendación: dos puntos, con severidad distinta.**

1. **Al dejar viajar** (pase a "En viaje", manual y job): es el candado FUERTE. Recomiendo que acá
   **bloquee** por default si supera el límite o está en mora — es el equivalente del candado prepago
   y es donde el riesgo de plata es mayor (el servicio se presta). Configurable a "solo avisa" para
   agencias que confían en su cartera (pregunta 4).
2. **Al confirmar la venta / al facturar**: acá recomiendo **solo avisar** (warning visible, no
   frena). Confirmar/facturar no es prestar el servicio todavía; frenar ahí molesta sin necesidad.

NO recomiendo chequear "al crear la reserva" (una cotización todavía no es deuda; en pre-venta el
cliente no debe nada). El chequeo entra recién cuando la venta es firme.

Punto de integración del aviso: el límite/mora se puede sumar como un bucket nuevo en
`AlertService` (junto a "impago próximo", `AlertService.cs:169` y "viajó y debe",
`AlertService.cs:194`), respetando la misma visibilidad por dueño (vendedor ve lo suyo) y el
enmascarado de costos.

## A.4 Términos de pago y aging (lo que te debe, vencido)

- **Términos**: `Customer.PaymentTermsDays` (NUEVO). El vencimiento de una deuda se calcula como
  `fecha base + PaymentTermsDays`. **Pregunta fiscal/negocio (pregunta 6)**: la fecha base, ¿es la
  fecha de la factura, la de confirmación de la venta, o la del fin de viaje? Cambia qué cuenta como
  "vencido". Recomiendo **fecha de factura** (es lo estándar en cuenta corriente y es un dato fiscal
  que ya existe).
- **Aging del cliente**: es un READ-MODEL derivado, NO un campo nuevo. Se construye sobre lo que ya
  da `FinancePositionService` (saldo por reserva/moneda) cruzado con la fecha de vencimiento de cada
  reserva. Buckets típicos: por vencer / vencido 1-30 / 31-60 / 61-90 / +90. Es una pantalla nueva
  de lectura (la cuenta corriente del cliente con su antigüedad de saldos), no toca la escritura.
- "En mora" (para el candado de A.2) = tiene al menos una reserva con saldo vencido (bucket >0 días).

## A.5 Reconocimiento del ingreso y KPIs (qué toca y qué NO)

- **Lo fiscal NO se rediseña** (instrucción explícita). En cuenta corriente la factura se emite igual
  (ADR-037 ya permite facturar sin cobrar). Lo único nuevo es que el COBRO llega después; eso ya está
  soportado (`CanRegisterPayment` no depende de haber viajado).
- **Reconocimiento de ingreso**: facturar sin cobrar genera una **cuenta por cobrar** (AR), que
  `FinancePositionService` YA modela. No cambia el devengo. **Marcar al contador** (pregunta C-1): en
  cuenta corriente, ¿el ingreso se reconoce al facturar (devengado) aunque no haya cobro? Es lo
  habitual, pero es decisión contable, no de software.
- **Comisión del vendedor**: hoy la comisión se devenga cuando la reserva queda **totalmente
  cobrada** (`Balance <= 0`), ver `OperationalFinanceSettings.cs:546` (EnableSellerCommissions). En
  cuenta corriente el cobro puede tardar. **Decisión (pregunta 7)**: ¿la comisión se devenga al
  cobrar (como hoy) o al confirmar/facturar la venta? Recomiendo dejar "al cobrar" para no tocar
  plata del vendedor antes de tiempo; pero hay que confirmarlo.
- **KPIs ya hechos**: "saldo pendiente total" (`ReservaService.cs:2185`) ya suma `Balance > 0` de
  estados no terminales; con cuenta corriente además habrá deuda en `Closed` (que `FinancePosition`
  ya cuenta). Sumar un KPI nuevo de "cartera vencida" (aging) es aditivo.

## A.6 Modelo de datos (resumen Feature A)

| Dónde | Campo | Tipo | Default | Nota |
|---|---|---|---|---|
| `OperationalFinanceSettings` | `DefaultCustomerBillingMode` | enum | `Prepaid` | modo con que nace un cliente |
| `Customer` | `BillingMode` | enum? (nullable) | null (=hereda) | por cliente |
| `Customer` | `CreditLimit` | decimal | 0 | **YA EXISTE**, se reutiliza |
| `Customer` | `PaymentTermsDays` | int | 0 | plazo de la cuenta |

Migración: aditiva (2 columnas nuevas + 1 setting). Sin backfill destructivo. `CreditLimit` ya tiene
datos (en su mayoría 0). Rollback: las columnas nuevas no rompen el camino prepago si quedan en
default; el modo `Account` no se activa para nadie hasta que se setee a mano por cliente.

## A.7 Riesgos Feature A

1. **Regresión del candado prepago**: el riesgo más alto. Mitigación: NO tocar `IsClientFullyPaid`;
   bifurcar por modo ANTES de llamarlo. Test cruzado: cliente `Prepaid` se comporta byte-idéntico a
   hoy (mismo set de tests de ADR-036 debe seguir verde).
2. **Multimoneda del límite**: `CreditLimit` es un solo decimal sin moneda; el AR del cliente es por
   moneda (ARS y USD). Ver pregunta 3 — hay que decidir si el límite es solo en ARS, o por moneda, o
   un escalar cross-moneda. Riesgo de comparar peras con manzanas si no se define.
3. **Reservas a cuenta que nunca cierran**: si no se permite cerrar con deuda (A.2 / pregunta 5),
   quedan colgadas "En viaje". Hay que resolverlo en el diseño del cierre.
4. **Exposición total = lectura de base en el candado**: el evaluador de crédito hace un query por
   cliente al pasar a viajar. Es O(reservas del cliente con deuda), trivial para el volumen actual;
   si la cartera crece, se cachea/paginates. No es bloqueante hoy.

## A.8 PREGUNTAS para Gastón — Feature A (negocio/UX)

**Pregunta 1 — ¿El modo es por cliente?**
Recomiendo: cada CLIENTE es "prepago" o "a cuenta", y la agencia elige con cuál nacen los nuevos.
Mockup de la ficha del cliente:

```
 Cliente: Viajes ACME S.A.
 ┌───────────────────────────────────────────────┐
 │ Forma de cobro:  ( ) Prepago   (•) Cuenta corriente
 │ Límite de crédito:  [  500.000 ] ARS
 │ Plazo de pago:      [ 30 ] días
 └───────────────────────────────────────────────┘
```
¿De acuerdo, o lo querés por venta?

**Pregunta 2 — ¿Un cliente a cuenta puede viajar debiendo?**
Recomiendo: SÍ, mientras esté dentro del límite y al día con sus plazos. (Es el sentido de la cuenta
corriente.) ¿Confirmás?

**Pregunta 3 — Límite de crédito y monedas.**
El cliente puede deber en pesos y en dólares. Opciones:
- (A) El límite es UN número en pesos y solo se controla la deuda en pesos (los dólares siempre
  prepago, por ahora). ← **recomendada** (simple, evita mezclar monedas).
- (B) Un límite por cada moneda (pesos y dólares por separado).
- (C) Un solo número que mezcla todo (no recomendado: suma peras con manzanas).
¿Cuál?

**Pregunta 4 — Cuando el cliente se pasa del límite o está en mora: ¿avisa o frena?**
Recomiendo: al momento de **dejarlo viajar**, FRENA (igual que hoy frena el prepago), con opción de
configurar "solo avisar" por si confiás en la cartera. Al confirmar/facturar, solo AVISA.
¿De acuerdo?

**Pregunta 5 — Cerrar el viaje con deuda (clientes a cuenta).**
Hoy una reserva no se cierra hasta que esté saldada. Un cliente a cuenta puede terminar el viaje
debiendo. Recomiendo: para clientes a cuenta, la reserva SÍ se cierra con deuda (la deuda sigue viva
en su cuenta corriente). ¿Confirmás?

**Pregunta 6 — ¿Desde cuándo cuentan los días de plazo?**
Para saber si una deuda está "vencida", ¿el plazo arranca desde la **fecha de la factura** (mi
recomendación), desde que se confirmó la venta, o desde el fin del viaje?

**Pregunta 7 — Comisión del vendedor en ventas a cuenta.**
Hoy la comisión se gana cuando la reserva queda **totalmente cobrada**. En cuenta corriente eso
tarda. ¿La dejamos así (se gana al cobrar) o querés que se gane al confirmar/facturar?
Recomiendo dejarla "al cobrar".

## A.9 Para el CONTADOR — Feature A (fiscal/contable)

- **C-1**: En cuenta corriente, ¿el ingreso se reconoce al **facturar** (devengado, lo habitual)
  aunque todavía no haya cobro? Confirmar el criterio de reconocimiento.
- **C-2**: La factura en cuenta corriente, ¿lleva alguna condición de venta específica (ej.
  "Cuenta corriente" como forma de pago en el comprobante ARCA) distinta del prepago? Confirmar si
  cambia algo del comprobante.
- **C-3**: Aging/cartera vencida: ¿hay algún requisito contable de previsión por incobrabilidad o
  reporte de morosidad que el read-model deba contemplar? (Solo si aplica a la operación.)

---

# FEATURE B — Descuento con tope y autorización (4 ojos)

## B.0 Qué hay hoy (verificado en el código)

El andamiaje existe pero está MUERTO — confirmado:

- Permiso `reservas.discount_above_threshold` → `Permissions.cs:16`. Hoy SOLO lo tiene Admin
  (`Permissions.cs:153`); explícitamente NO el Vendedor (`Permissions.cs:222`) ni el Colaborador
  (`Permissions.cs:194`).
- Umbral `OperationalFinanceSettings.MaxDiscountPercentWithoutOverride` (default 10%) →
  `OperationalFinanceSettings.cs:30`.
- Tipo de aprobación `ApprovalRequestType.DiscountAboveThreshold = 2` →
  `ApprovalRequestType.cs:19`. Su seed/migración existe
  (`20260508211303_AddDiscountThresholdAndBackfillReservaResponsible`).
- **Pero NINGÚN servicio crea esa aprobación, y NO hay campo "descuento".** El precio se baja a mano
  escribiendo un `SalePrice` menor en el servicio:
  - `ServicioReserva.SalePrice` → `src/TravelApi.Domain/Entities/ServicioReserva.cs:60` (genérico).
  - Los 5 servicios tipados tienen su propio `SalePrice`; se escribe en `BookingService` (ej. vuelo
    `BookingService.cs:698`, hotel `:1082`, paquete `:1306`, traslado `:1576`, asistencia `:431`),
    y cada cambio ya audita `OldSalePrice`/`NewSalePrice` (ej. `BookingService.cs:861`).

La maquinaria de aprobaciones está completa y lista para reusar:
- `ApprovalRequestService.CreateAsync` (`ApprovalRequestService.cs:76`) — idempotente por combo
  (tipo+entidad+usuario), con cooldown anti-spam.
- `FindActiveApprovedAsync` (`ApprovalRequestService.cs:217`) — buscar una aprobación viva aprobada.
- `MarkConsumedAsync` (`ApprovalRequestService.cs:235`) — consumirla al aplicar.
- Patrón de consumo ya usado por `InvoiceService.AnnulInvoice` (doc en `ApprovalRequestService.cs:21`).

## B.1 ¿Dónde vive el descuento: por servicio o por reserva?

**Recomendación: POR SERVICIO.** Porque:
- El precio (`SalePrice`) y el precio de referencia (la tarifa/`Rate`) son **por servicio**. El tope
  es un % "sobre precio de referencia" (lo dice el propio comentario en `OperationalFinanceSettings.cs:27`).
- Un descuento "por reserva" sobre el total no tiene un precio de referencia único contra el cual
  medir el % (la reserva mezcla servicios de distinta moneda — `ReservaMoneyCalculator` agrupa por
  moneda). Medir el tope se vuelve ambiguo.
- Conveniencia de UI: ofrecer un "aplicar el mismo % a todos los servicios" que internamente escribe
  el descuento en cada uno. Así el usuario que quiere "10% a toda la reserva" lo hace en un clic, pero
  el dato y el control viven por servicio.

## B.2 Modelo de datos (Feature B)

En cada servicio (los 5 tipados + el genérico `ServicioReserva`), agregar:

- `ListPrice` (decimal, nullable): el precio de **referencia** (sin descuento). Snapshot del precio
  de tarifa al momento de cargar el servicio. Si null = no hubo descuento (legacy / precio directo).
- `DiscountPercent` (decimal, default 0): el % de descuento aplicado sobre `ListPrice`.

`SalePrice` (el campo que YA existe) pasa a ser el **precio final** = `ListPrice * (1 - DiscountPercent/100)`.
Es decir: `SalePrice` sigue siendo lo que cobra y lo que ve todo el resto del sistema (cálculo de
saldo, factura, comisión) **sin cambios**. El descuento es metadata que EXPLICA cómo se llegó a ese
`SalePrice`. Esto es clave: no toca `ReservaMoneyCalculator`, ni la factura, ni nada aguas abajo.

> Alternativa considerada y descartada: guardar un `DiscountAmount` (monto fijo) en vez de %.
> Descartada para el dato persistido porque el tope se expresa en % y porque % sobrevive a cambios de
> moneda. En la UI SÍ se puede dejar cargar "monto" y convertirlo a % contra `ListPrice` (pregunta 9).

Migración: aditiva (2 columnas por cada tipo de servicio). `ListPrice` nullable evita backfill. Para
servicios viejos sin `ListPrice`, se asume `ListPrice = SalePrice` y `DiscountPercent = 0` (no hubo
descuento conocido). Rollback: las columnas nuevas son ignoradas por el resto del sistema (SalePrice
es la fuente), así que quitarlas no rompe plata.

## B.3 Cómo se calcula el precio final y se dispara la autorización

Centralizar en una política pura nueva, p. ej.
`DiscountPolicy.Evaluate(listPrice, discountPercent, settings, hasPermission, hasApproval)`:

1. Si `discountPercent <= MaxDiscountPercentWithoutOverride` → **permitido directo** (cualquiera con
   permiso de editar la reserva). Se aplica, se recalcula `SalePrice`, se audita (ya existe el audit
   Old/New SalePrice).
2. Si `discountPercent > MaxDiscountPercentWithoutOverride`:
   - Si el usuario **tiene** `reservas.discount_above_threshold` (hoy: Admin) → **permitido directo**
     (él ES el autorizador). Se aplica + audit reforzado.
   - Si el usuario **NO tiene** el permiso (hoy: Vendedor/Colaborador) → se requiere una
     **`ApprovalRequest` de tipo `DiscountAboveThreshold` APROBADA** para ese servicio. Sin ella, la
     escritura del precio se RECHAZA con un mensaje claro ("este descuento supera el tope; pedí
     autorización").

Flujo de 4 ojos (reusando lo que existe):
- El vendedor pide autorización → `ApprovalRequestService.CreateAsync` con
  `RequestType=DiscountAboveThreshold`, `EntityType="ReservaServiceDiscount"`,
  `EntityId = Id del servicio`, y en `Metadata` (JSON) el detalle: precio de referencia, % pedido,
  precio final resultante, moneda. (El `Metadata` ya se usa así en otros approvals, ver
  `OperationalFinanceSettings.cs` FC1.3.)
- Un Admin (que tiene el permiso) lo aprueba desde la bandeja genérica de aprobaciones que YA existe
  (no hay UI nueva de aprobación; mismo patrón que `PartialCreditNoteApproval`, `ApprovalRequestType.cs:78`).
- Al aplicar el descuento, el servicio busca la aprobación viva con `FindActiveApprovedAsync` y la
  consume con `MarkConsumedAsync`. La aprobación es para ESE servicio y ESE % (si después se cambia
  el %, hay que re-pedir — la idempotencia de `CreateAsync` y el % en Metadata lo cubren).

Punto de enforcement (importante para que no haya bypass): la validación tiene que estar en el
**servicio de dominio/aplicación**, en CADA punto donde se escribe `SalePrice`:
- Los métodos de update de `BookingService` (vuelo `:776`, hotel `:1082`, paquete `:1374`, traslado
  `:1643`, asistencia `:1950`) y el del servicio genérico.
- Recomendación de diseño: NO repetir el chequeo 6 veces. Encapsular en un único helper
  (`DiscountPolicy` + un guard `EnsureDiscountAuthorized`) que los 6 caminos llaman. Un solo lugar =
  no diverge (mismo principio que `ReservaCapabilities` es fuente única de las capacidades).

## B.4 Auditoría

- El cambio de precio YA se audita (`OldSalePrice`/`NewSalePrice`, ej. `BookingService.cs:861`). Se
  extiende ese mismo evento con: `OldDiscountPercent`/`NewDiscountPercent`, y si hubo aprobación, el
  Id de la `ApprovalRequest` consumida y quién la aprobó (la `ApprovalRequest` ya guarda solicitante +
  aprobador + timestamps).
- Así queda trazable: quién pidió, quién autorizó, qué % sobre qué precio de referencia, y cuándo.

## B.5 Interacción con capacidades / estados

- Editar el precio de un servicio ya está gateado por estado:
  `ReservaCapabilities.CanEditServices` (`ReservaCapabilities.cs:474`) — solo en estados vivos
  (pre-venta + Confirmed), nunca en terminales. El descuento hereda ese gate sin esfuerzo (es una
  edición de servicio).
- En estados firmes bajo candado (Confirmed+), la edición del servicio ya pide la autorización de
  edición bajo candado existente; el descuento se suma a eso (dos compuertas independientes, no se
  pisan).

## B.6 Riesgos Feature B

1. **Bypass por escribir `SalePrice` directo**: si el guard no está en TODOS los caminos de escritura
   de `SalePrice`, alguien baja el precio sin pasar por el tope. Mitigación: helper único + test que
   recorra los 6 caminos (5 tipados + genérico). Riesgo real, lo marco como gate de revisión.
2. **`SalePrice` y `ListPrice` se desincronizan**: si alguien edita `SalePrice` por un camino que no
   recalcula desde `ListPrice/DiscountPercent`. Mitigación: la fuente de `SalePrice` cuando hay
   descuento es siempre `ListPrice * (1 - %/100)`; los caminos que setean precio "directo" (sin
   descuento) ponen `DiscountPercent=0` y `ListPrice=SalePrice`.
3. **Aprobación reutilizada para otro %**: el % va en el Metadata y la aprobación es por servicio; al
   consumir, validar que el % aprobado == % aplicado. Sin esto, se aprueba 15% y se aplica 40%.
4. **Quién puede aprobar**: hoy solo Admin tiene el permiso. Si el único usuario es Admin (caso real
   de Gastón hoy, producto sin clientes), el 4-ojos no aplica y Admin aplica directo. Está bien — es
   coherente con el `Allow4EyesBypassWhenSingleAdmin` que ya existe para otros flujos
   (`OperationalFinanceSettings.cs:231`).

## B.7 PREGUNTAS para Gastón — Feature B (negocio/UX)

**Pregunta 8 — ¿El descuento es por servicio (con opción "aplicar a todos")?**
Recomiendo: el descuento se carga por servicio, y hay un botón "mismo descuento a todos los servicios"
para el caso "10% a toda la reserva". Mockup en la línea del servicio:

```
 Vuelo Buenos Aires → Madrid
   Precio:        [ 1.200.000 ] ARS   (precio de lista)
   Descuento:     [  10 ] %    → Precio final: 1.080.000 ARS
                  ▲ hasta 10% lo aplicás vos; más que eso pide autorización
```
¿De acuerdo, o lo querés como un único descuento sobre el total de la reserva?

**Pregunta 9 — ¿Se carga en % o en monto?**
El tope está en % (hoy 10%). Recomiendo cargar en **%** (es lo que se compara con el tope). Si querés,
dejamos cargar un monto y el sistema lo convierte a % solo para mostrar/controlar. ¿Cómo lo preferís?

**Pregunta 10 — Cuando supera el tope, ¿qué pasa exactamente?**
Recomiendo: si lo carga alguien sin permiso (vendedor), el sistema NO aplica el descuento todavía y
crea un pedido de autorización; un Admin lo aprueba desde la bandeja de autorizaciones que ya existe;
recién ahí se aplica. Si lo carga un Admin, se aplica directo. ¿De acuerdo?

**Pregunta 11 — ¿El tope de 10% es global, o querés poder cambiarlo?**
Ya es configurable (un solo número para toda la agencia). ¿Te alcanza un número global, o lo querés
por tipo de servicio / por vendedor? Recomiendo dejarlo global (lo más simple; es lo que hay).

**Pregunta 12 — ¿Quién autoriza además del Admin?**
Hoy solo el Admin puede autorizar descuentos grandes. ¿Querés que algún otro rol (ej. Colaborador)
también pueda? Recomiendo dejarlo Admin-only por ahora (es lo que ya está cableado).

## B.8 Para el CONTADOR — Feature B (fiscal)

- **C-4**: El descuento baja la base imponible de la factura. ¿Hay algún límite o requisito fiscal
  para descuentos (ej. descuento que deba figurar como tal en el comprobante ARCA, en vez de
  simplemente facturar el precio final menor)? Hoy se factura el `SalePrice` final; confirmar que con
  descuento explícito eso sigue siendo correcto fiscalmente. (Probablemente sí, porque el precio final
  es el precio real de venta — pero es decisión del contador.)

---

# Resumen ejecutivo (para Gastón, en simple)

**Cuenta corriente**: hoy el cliente paga TODO antes de viajar. Te propongo que cada cliente pueda ser
"prepago" (como ahora) o "a cuenta" (viaja debiendo, dentro de un límite y un plazo que vos le ponés).
El límite y el plazo viven en la ficha del cliente. Todo lo de hoy queda igual para los clientes
prepago. Necesito que me contestes 7 cosas (preguntas 1 a 7) y el contador 3 (C-1 a C-3).

**Descuento**: hoy para hacer un descuento bajás el precio a mano y nadie controla nada. Te propongo un
campo de descuento de verdad: hasta el tope (10%) lo hace cualquiera; si se pasa, el vendedor pide
permiso y un Admin lo aprueba (la maquinaria de permisos ya está, solo hay que enchufarla). Necesito
que me contestes 5 cosas (preguntas 8 a 12) y el contador 1 (C-4).

Ninguna de las dos rompe el prepago ni lo fiscal que ya está homologado: las dos son **aditivas** y el
camino de hoy queda intacto si no se activan.

---

# Lo que NO verifiqué / queda abierto

- No ejecuté tests ni build (esto es diseño, no implementación).
- No revisé el frontend de la ficha del cliente ni de la línea de servicio (los mockups son
  conceptuales; el detalle de UI pasa por el gate de UX con Gastón antes de construir).
- La fecha base del aging (pregunta 6) y el reconocimiento de ingreso (C-1) dependen del contador:
  hasta su firma, el diseño deja esos puntos como configurables/abiertos, no fijos.
- No verifiqué si existe ya una pantalla de "cuenta corriente del cliente" en curso (la memoria
  menciona un Estado de Cuenta por reserva); si existe, el aging se monta sobre esa base en vez de
  una pantalla nueva. A confirmar al implementar.
