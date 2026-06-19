# ADR-035 — Política de capacidades por estado y cobro multimoneda

## Encabezado

- **Número:** 035
- **Título:** Política de capacidades por estado de la reserva y cobro multimoneda (qué se puede hacer en cada estado, expuesto como botón apagado + motivo; el cobro arranca en la moneda de la reserva con opción de pagar en otra)
- **Status:** Aceptado (con observaciones del review incorporadas) — pendiente UX gate para las pantallas
- **Fecha:** 2026-06-19

### Decisiones del dueño ya tomadas (no se reabren)

1. **Acción no permitida = botón apagado + motivo.** Nunca se esconde la acción ni se deja "fallar" al click. El botón se ve, está deshabilitado, y al pasar/tocar muestra por qué no se puede ahora.
2. **El cobro arranca en la moneda de la reserva,** con un link "pagar en otra moneda" para el caso menos común. No se obliga a elegir moneda en cada cobro.
3. **El voucher se puede emitir desde Confirmada en adelante** (Confirmed, Traveling, ToSettle, Closed). No antes.
4. **Cancelar una reserva Finalizada (Closed) no se permite.** No es una acción válida del ciclo.
5. **Sin feature flags.** La política sale directa (regla del dueño 2026-06-07).
6. **Producto en desarrollo, sin clientes reales.** No hay datos productivos de usuarios a migrar; el único usuario hoy es el dueño construyendo el producto.

---

## 1. Título

Política de capacidades por estado de la reserva y cobro multimoneda.

## 2. Status

Aceptado (con observaciones del review de arquitectura incorporadas — "Approved with comments", C1..C6) — pendiente UX gate (`ux-ui-disenador` sobre `docs/ux/guia-ux-gaston.md`) antes de implementar las pantallas (los dos frontends de cobro y la barra de acciones). Sin feature flag.

Este ADR **no** vuelve a discutir la regla de cobrabilidad por deuda de [ADR-033](ADR-033-cobro-desacoplado-del-estado.md): la asume implementada en HEAD `ba2ed42`. Lo que agrega es: (a) una **fuente única, declarativa y consultable** de "qué acción se puede en qué estado y por qué no", que el backend usa como gate y el frontend usa para apagar botones con motivo; y (b) el **arranque del cobro en la moneda de la reserva**.

## 3. Context (estado actual verificado)

HEAD `ba2ed42`. Hechos verificados en código (no asumidos):

### 3.1 Las capacidades por estado están dispersas y son inconsistentes

- **Cobrar** se gatea en el dominio: `Reserva.EnsureCollectable()` / `IsCollectable()` → `EstadoReserva.ActiveCollectionStatuses` y, tras ADR-033, `SaleFirmStatuses` para deuda. Definiciones en `src/TravelApi.Domain/Entities/Reserva.cs:90` (`ActiveCollectionStatuses`), `:117` (`SaleFirmStatuses`), `:141` (`IsCollectableStatus`).
- **Editar/borrar cobro** se gatea por guards fiscales/puente propios (recibo/CAE, sobrepago, puente de saldo a favor) y, tras ADR-033, ya **no** por estado.
- **Confirmar/emitir servicios** NO pide candado aunque la reserva esté Confirmed (decisión #8): `BookingService` `*StatusAsync`.
- **Candado** (`ReservaLockGuard`) = {Confirmed, Traveling, ToSettle, Closed}; NO incluye InManagement.
- **Servicios que exigen estar resueltos**: `ReservaCapacityRules.ReservaStatusesRequiringConfirmedServices` en `src/TravelApi.Infrastructure/Services/ReservaCapacityRules.cs:37` y `:62`.
- **Transiciones de estado** (la única tabla declarativa que sí existe hoy):
  - Forward: `ReservaService.cs:928` (`AllowedForwardTransitions`). Verificado: `InManagement→{Cancelled}`, `Confirmed→{Traveling,Cancelled}`, `Traveling→{Closed,ToSettle,Cancelled}`, `ToSettle→{Closed,Cancelled}`. **Closed NO tiene salida forward** (no se puede cancelar una Finalizada — coincide con la decisión 4 del dueño).
  - Revert: `ReservaService.cs:947` (`AllowedRevertTransitions`), con gate fiscal duro en `RevertStatusAsync`.
- **Voucher**: `src/TravelApi.Infrastructure/Services/VoucherService.cs` gatea **solo** el ciclo del propio voucher (Draft/PendingAuthorization/Issued/Revoked, `:320`, `:419`, `:495`). **NO valida `Reserva.Status`.** Hoy, en principio, se puede iniciar un voucher en cualquier estado de la reserva. Esto contradice la decisión 3 del dueño (voucher solo desde Confirmed+).

**Conclusión:** hoy "qué puedo hacer con esta reserva" está repartido entre el dominio, varios services y reglas inline. No hay una respuesta única que el backend imponga y el frontend lea para apagar botones con motivo. El frontend infiere capacidades replicando lógica de estado, lo que duplica reglas de negocio (anti-patrón que el estándar fullstack prohíbe).

### 3.2 El cobro no arranca en la moneda de la reserva

- La moneda del cobro la resuelve `PaymentCurrencyResolver.Resolve(...)` desde `request.Currency` / `request.ImputedCurrency` (`PaymentService.cs:541`). La reserva ya es multimoneda (`ReservaMoneyByCurrency`, ADR-021).
- **Hay DOS frontends de cobro distintos** (verificado en código):
  - **Caso común — cobro desde el detalle de la reserva:** `src/TravelWeb/src/features/reservas/components/RegistrarCobroInline.jsx` (usado por `ReservaDetailPage.jsx:1194`). **Este es el frontend principal a arreglar.** Hoy no preselecciona la moneda de la reserva como default ni ofrece un camino claro "pagar en otra moneda" como secundario.
  - **Segundo frontend — worklist de cobranza:** `src/TravelWeb/src/components/PaymentModal.jsx`, usado por `src/TravelWeb/src/features/payments/pages/PaymentsCollectionsPage.jsx:101`. Tiene el mismo problema y debe recibir el mismo fix de default de moneda.

## 4. Problem

1. No existe una **autoridad única** de capacidades por estado. Cada pantalla decide por su cuenta si muestra/oculta un botón, y el backend valida en lugares distintos con listas distintas. Resultado: divergencias front/back ya observadas (ej.: worklist de cobranza vs gate de cobro) y reglas de negocio duplicadas en el cliente.
2. El comportamiento de UI cuando una acción no aplica es inconsistente (a veces oculta, a veces deja fallar el request). El dueño quiere **siempre** botón visible + apagado + motivo.
3. El voucher no respeta la regla "desde Confirmed+".
4. El cobro no parte de la moneda de la reserva.

## 5. Decisión

### Decisión 1 — Una fuente única declarativa de capacidades por estado: `ReservaCapabilityPolicy`

Crear en el dominio (`src/TravelApi.Domain/Reservations/ReservaCapabilityPolicy.cs`) una política **pura, sin dependencias de infraestructura**, que responda para una reserva dada (su `Status`, y los datos mínimos ya cargados: balance, si tiene CAE, si tiene voucher emitido, si tiene servicios resueltos) **qué capacidades están permitidas y, si no, con qué motivo**.

- Conjunto cerrado de capacidades (enum/const): `RecordPayment`, `EditPayment`, `IssueVoucher`, `Invoice`, `CancelReserva`, `CancelService`, `EditServices`, `Refund`, `ApplyCreditToOther` (FC4).
- Cada capacidad devuelve un resultado `CapabilityDecision { bool Allowed; string? ReasonCode; }` donde `ReasonCode` es un **código estable** (ej. `RESERVA_NO_FIRME`, `CERRADA_NO_CANCELABLE`, `SALDO_CERO`, `BLOQUEO_FISCAL_CAE`, `VOUCHER_ANTES_DE_CONFIRMAR`). El texto humano lo resuelve una tabla de mensajes en frontend desde el código (no se manda prosa desde el backend; así se internacionaliza y se mantiene en un solo lugar de UI).
- La política **reusa** las listas existentes (`ActiveCollectionStatuses`, `SaleFirmStatuses`, `AllowedForwardTransitions`, `ReservaStatusesRequiringConfirmedServices`); no inventa una nueva taxonomía de estados. Es una **fachada de lectura** sobre reglas que ya existen, más las dos reglas nuevas (voucher Confirmed+, Closed no cancelable, que de hecho ya está en la tabla forward).
- Los **gates de escritura existentes no se borran**: `EnsureCollectable`, los guards fiscales y la matriz de transición siguen siendo la última línea de defensa. La política es la fuente que (a) el frontend consulta para pintar y (b) los call-sites consultan en lugar de repetir condiciones inline. La regla queda en **un** lugar; los `Ensure*` la invocan.

### Decisión 2 — El cobro arranca en la moneda de la reserva, con "pagar en otra moneda" como secundario

- Backend: exponer en el DTO de la reserva la **moneda principal** (la moneda del saldo dominante / la moneda base de la reserva, derivada de `ReservaMoneyByCurrency`; no se persiste columna nueva). La **decisión de cuál es la moneda principal cuando hay saldo en dos monedas vive en `ReservaMoneyCalculator` / el armado del DTO — un único lugar backend, nunca en el front** (ver §12 pregunta 1 para el criterio de priorización, que es lo único abierto; el *lugar* de implementación queda fijado aquí). `PaymentCurrencyResolver` ya sabe resolver; el cambio es que el **default** que ofrece la API/el front es la moneda de la reserva.
- Frontend — **caso común: `RegistrarCobroInline.jsx`** (cobro desde el detalle de la reserva): la moneda viene **preseleccionada** con la moneda principal de la reserva que entrega el backend; el formulario no obliga a elegirla. En reserva mono-moneda el **selector de moneda se oculta** y se muestra el texto "Cobrás en US$ — saldo US$X" con un link secundario "pagar en otra moneda" que recién ahí abre el selector + tipo de cambio. **Requiere UX gate.**
- Frontend — **segundo: `PaymentModal.jsx`** (worklist de cobranza, `PaymentsCollectionsPage.jsx:101`): mismo fix de default de moneda (preselección de la moneda principal entregada por el backend; en mono-moneda mismo tratamiento de ocultar selector + link "otra moneda"). **Requiere UX gate.**
- No cambia la lógica fiscal ni de caja del cobro: sigue todo por `PaymentService.CreatePaymentAsync` → `PaymentCurrencyResolver`. Es default + presentación.

### Decisión 3 — Voucher solo desde Confirmed en adelante

- El gate de `Reserva.Status ∈ {Confirmed, Traveling, ToSettle, Closed}` debe **centralizarse en UN único helper privado** (ej. `EnsureReservaAllowsVoucher(reserva)`) que devuelva/lance con `ReasonCode = VOUCHER_ANTES_DE_CONFIRMAR` y reuse el mismo conjunto que la capacidad `IssueVoucher` de la política (Decisión 1), para que front y back coincidan.
- Ese helper se invoca desde **TODAS las entradas de creación/subida de voucher**, no en una sola zona: `VoucherService.GenerateVoucherRecordAsync` (`:144`), `VoucherService.UploadExternalVoucherAsync` (`:198`) y cualquier otra ruta de alta de voucher que exista o se agregue. No duplicar la condición inline en cada método.
- El ciclo interno del voucher (Draft→PendingAuthorization→Issued→Revoked) no cambia.

### Decisión 4 — Cancelar Finalizada (Closed) no se permite

- Ya está garantizado por `AllowedForwardTransitions` (Closed no tiene `Cancelled` como destino, `ReservaService.cs:935-936`). La capacidad `CancelReserva` de la política devuelve `Allowed=false, ReasonCode=CERRADA_NO_CANCELABLE` cuando `Status==Closed`. No se agrega ninguna transición. Solo se hace **visible el motivo** en el botón apagado.

### Decisión 4-bis — Reabrir una Finalizada a "A liquidar" (Closed → ToSettle)

- Hoy `AllowedRevertTransitions[Closed]` permite **solo** `{Traveling}` (`ReservaService.cs:954`). Se **agrega `ToSettle`** a ese conjunto: `AllowedRevertTransitions[Closed] = { Traveling, ToSettle }`. Es la única transición nueva que introduce este ADR; no cambia ninguna otra.
- La reapertura **reusa el camino de revert de estado existente** (`RevertStatusAsync`), por lo tanto:
  - **Exige actor + razón obligatoria** registrados en el mismo log de cambio de estado que ya usa el revert (no se crea un canal nuevo de auditoría).
  - **Exige autorización de supervisor** (mismo control que el resto de los revert sensibles; reabrir una venta ya cerrada es una operación de excepción, no de uso diario).
- **Saldo al reabrir:** la reapertura **no recalcula ni toca importes**; el balance/saldo de la reserva queda exactamente como estaba en Closed (mismas líneas, mismos cobros, misma deuda). Solo cambia el estado a ToSettle, lo que vuelve a habilitar las acciones de liquidación según la política. El código de revert de Closed ya limpia `ClosedAt` (`ReservaService.cs:1187-1188`); ese comportamiento se mantiene para el destino ToSettle.

### Decisión 5 — El frontend nunca decide capacidades por su cuenta; las lee del backend

- El DTO de la reserva incluye un bloque `capabilities`: un diccionario `{ capability → { allowed, reasonCode } }` calculado por `ReservaCapabilityPolicy`.
- Cada botón de acción en el detalle de la reserva (`ReservaHeader.jsx` / barra de acciones) y en cobranza renderiza: **siempre visible**, `disabled` cuando `!allowed`, con tooltip/cartel = texto resuelto desde `reasonCode`. El frontend **no** vuelve a evaluar el `Status` para decidir habilitación.
- Esto elimina la duplicación de reglas de negocio en el cliente (estándar fullstack) y hace que la regla viva en un solo lugar testeable.

## 6. Consequences

**Positivas:**
- Una sola autoridad de capacidades, testeable en aislamiento (política pura de dominio).
- Front y back dejan de divergir; el frontend deja de replicar reglas de estado.
- Comportamiento de UI uniforme y explicativo (botón apagado + motivo), tal como pidió el dueño.
- Voucher y cancelación quedan alineados con las decisiones del dueño.
- Cobro más rápido en el caso común (moneda preseleccionada).

**Negativas / costos:**
- El DTO de la reserva crece (bloque `capabilities`). Es aditivo; no rompe consumidores actuales.
- Hay que mantener sincronizados el enum de capacidades, los `ReasonCode` y la tabla de textos del front. Mitigación: tests que verifican que todo `ReasonCode` emitido tiene texto en el front.
- La política es una fachada: si alguien agrega un gate de escritura nuevo y olvida reflejarlo en la política, el front mostraría un botón habilitado que el back rechaza. Mitigación: regla de revisión + test que cruza cada `Ensure*` con su capacidad.

## 7. Alternatives considered

- **A) Dejar la lógica de capacidades en el frontend (status quo).** Rechazada: duplica reglas de negocio en el cliente, ya causó divergencias, viola el estándar fullstack.
- **B) Calcular capacidades en cada controller ad-hoc.** Rechazada: dispersa la regla, difícil de testear, propenso a divergir entre endpoints.
- **C) Una máquina de estados formal completa (state machine library).** Rechazada por sobreingeniería: ya existe `AllowedForwardTransitions`/`AllowedRevertTransitions` y los `Ensure*`; introducir un motor genérico agrega complejidad sin remover duplicación real. La política como fachada de lectura es lo mínimo que resuelve el problema.
- **D) Mandar el texto del motivo desde el backend.** Rechazada: acopla idioma/UX al backend; mejor `ReasonCode` estable + tabla de textos en front.

## 8. Lista ordenada de cambios por archivo

> Orden de implementación pensado para que cada paso compile y los tests pasen antes del siguiente.

1. `src/TravelApi.Domain/Reservations/ReservaCapabilityPolicy.cs` **(nuevo).** Política pura: enum de capacidades, `CapabilityDecision`, método `Evaluate(reserva, context)` que devuelve el diccionario. Reusa `EstadoReserva.*`, las listas de transición (expuestas como `internal`/`public static` si hace falta leerlas desde el dominio) y `ReservaCapacityRules`.
2. `src/TravelApi.Domain/Entities/Reserva.cs` — exponer (si no lo están) los predicados que la política necesita (`IsSaleFirmStatus`, `IsCollectableStatus`) sin cambiar su semántica. No tocar `EnsureCollectable`.
3. `src/TravelApi.Infrastructure/Services/ReservaService.cs` — **PASO AISLADO Y PURO (sin cambio de comportamiento), antes de que la política las consuma.** Si las tablas `AllowedForwardTransitions`/`AllowedRevertTransitions` (`:928`, `:947`) están `private`, moverlas a un tipo accesible por el dominio (una sola fuente: mover la definición al dominio y que `ReservaService` la importe). Este movimiento se hace **como commit separado**, NO mezclado con la introducción de la política ni con la transición nueva Closed→ToSettle de la Decisión 4-bis. **La suite de transición existente debe quedar verde ANTES** de que `ReservaCapabilityPolicy` (paso 1) empiece a leer estas tablas. Solo después de ese verde se agrega la transición Closed→ToSettle (Decisión 4-bis) y se conecta la política. Sin cambio de comportamiento en este paso.
4. `src/TravelApi.Infrastructure/Services/VoucherService.cs` — agregar el gate de estado (Decisión 3) como **UN helper único** (`EnsureReservaAllowsVoucher`) e invocarlo desde **todas** las entradas de alta de voucher: `GenerateVoucherRecordAsync` (`:144`), `UploadExternalVoucherAsync` (`:198`) y cualquier otra ruta. Reusa el mismo conjunto que la capacidad `IssueVoucher`. No replicar la condición inline en cada método.
4-bis. `src/TravelApi.Infrastructure/Services/ReservaService.cs` — **recién después del verde del paso 3**, agregar `ToSettle` a `AllowedRevertTransitions[Closed]` (`:954`, hoy solo `{Traveling}`) para habilitar Closed→ToSettle (Decisión 4-bis). La reapertura pasa por `RevertStatusAsync` (actor + razón obligatoria + autorización de supervisor; el saldo no se recalcula). Tests de transición nuevos para este camino.
5. `src/TravelApi.Infrastructure/Services/PaymentService.cs` — sin cambio de lógica de resolución; solo asegurar que la API ofrece la moneda de la reserva como default (Decisión 2). El resolver (`:541`) queda igual.
6. DTO de la reserva (`src/TravelApi.Application/DTOs/…ReservaDto`/`ReservaDetailDto`) — agregar bloque `capabilities` (aditivo) y la `moneda principal` para el cobro.
7. **`ReservaMoneyCalculator` / armado del DTO** (un solo lugar backend) — decide cuál es la **moneda principal** cuando hay saldo en más de una moneda (criterio en §12 pregunta 1, pendiente de Gastón) e invoca `ReservaCapabilityPolicy.Evaluate(...)` para poblar `capabilities`. **El front nunca decide la moneda principal; la consume del DTO.**
8. **Cobro multimoneda — DOS frontends, mismo fix de default de moneda. UX gate.**
   - **(a) Caso común:** `src/TravelWeb/src/features/reservas/components/RegistrarCobroInline.jsx` — default a la moneda principal de la reserva; en mono-moneda ocultar el selector y mostrar "Cobrás en US$ — saldo US$X" + link "pagar en otra moneda" (secundario).
   - **(b) Segundo frontend:** `src/TravelWeb/src/components/PaymentModal.jsx` (usado por `src/TravelWeb/src/features/payments/pages/PaymentsCollectionsPage.jsx:101`) — mismo fix de default de moneda.
9. `src/TravelWeb/src/features/reservas/components/ReservaHeader.jsx` (y demás barras de acción que correspondan según el UX gate) — botones siempre visibles, `disabled` por `capabilities`, tooltip por `reasonCode`. **UX gate.**
10. Tabla de textos de `reasonCode` en frontend (un solo archivo de mensajes) — un texto por cada código que emite la política.

## 9. Migraciones

**Ninguna.** No hay cambio de esquema. La política deriva de datos ya presentes; la moneda principal se deriva de `ReservaMoneyByCurrency` (sin columna nueva); `capabilities` es un campo calculado del DTO; el gate de voucher lee `Reserva.Status` existente. (Producto en desarrollo, sin clientes; aun así no haría falta migrar nada.)

## 10. Riesgos / qué NO tocar

- **NO** cambiar la regla de cobrabilidad de ADR-033 (cobrable = venta firme + Balance>0). La política la **lee**, no la redefine.
- **NO** borrar los gates de escritura (`EnsureCollectable`, guards fiscales CAE/recibo/puente, matriz de transición). La política es fachada de lectura; la defensa final sigue en su lugar. Si la política y el `Ensure*` discrepan, manda el `Ensure*` (el back rechaza) — por eso el test cruzado (§11).
- **NO** introducir feature flags (regla del dueño 2026-06-07).
- **NO** mandar texto humano del motivo desde el backend; solo `ReasonCode`.
- **NO** tocar el ciclo interno del voucher ni la lógica fiscal/caja del cobro.
- **Riesgo de desincronización** política↔gate de escritura: mitigado por el **test cruzado, que es GATE BLOQUEANTE DE MERGE** (§11): la política nunca puede decir `allowed=true` para algo que un guard fiscal/de puente (CAE vivo, recibo emitido, puente de sobrepago, puente FC4, `EnsureCollectable`) rechaza.
- **Riesgo UX**: el bloque `capabilities` no debe filtrar datos sensibles (ej. motivos de costo a usuarios sin permiso de ver costo). Los `reasonCode` deben ser neutros; cualquier motivo ligado a costo respeta el enmascarado `see_cost` existente.

## 11. Plan de tests

**Unit (dominio, política pura):**
- Para cada estado del ciclo (Quotation, Budget, InManagement, Confirmed, Traveling, ToSettle, Closed, Cancelled, Lost, PendingOperatorRefund) × cada capacidad: assert de `allowed` y `reasonCode` esperado. Incluye:
  - Closed → `CancelReserva` no permitido, `CERRADA_NO_CANCELABLE`.
  - Voucher no permitido antes de Confirmed (`VOUCHER_ANTES_DE_CONFIRMAR`); permitido en Confirmed/Traveling/ToSettle/Closed.
  - Cobro permitido en venta firme con deuda (incl. Closed con Balance>0, coherente con ADR-033); no permitido con saldo 0 / estado no firme.
- **Test cruzado de coherencia — GATE BLOQUEANTE DE MERGE (no recomendación):** para cada capacidad de escritura, verificar que cuando la política dice `allowed=false`, el `Ensure*`/transición correspondiente también rechaza (y viceversa para los casos `allowed=true` representativos). **En particular, la política NUNCA puede devolver `allowed=true` para una acción que un guard fiscal/de puente rechaza:** CAE vivo, recibo emitido, puente de sobrepago, puente FC4 (saldo a favor aplicado a otra reserva) y `EnsureCollectable`. Si este test no está verde, el merge no procede. Evita la desincronización del §10.

**Unit (voucher):** alta de voucher en estado no permitido lanza con `VOUCHER_ANTES_DE_CONFIRMAR`; alta en Confirmed pasa.

**Unit (cobro multimoneda):** el default ofrecido es la moneda de la reserva; cobro "en otra moneda" sigue resolviendo por `PaymentCurrencyResolver` igual que hoy (sin regresión de caja/fiscal).

**Frontend:** snapshot/render del detalle: botón visible + `disabled` + tooltip por `reasonCode`; `PaymentModal` con moneda preseleccionada y camino "otra moneda". Test que verifica que todo `reasonCode` tiene texto.

**No regresión:** suite unit completa verde (hoy en `ba2ed42`). Integración Postgres en VPS para el armado del DTO con datos reales (la deriva de moneda principal).

## 12. Preguntas abiertas al dueño

1. **Moneda principal de la reserva para el default de cobro:** cuando una reserva tiene saldo en más de una moneda (ej. debe USD y debe ARS), ¿cuál se ofrece preseleccionada en el cobro? Propuesta: la moneda con mayor saldo pendiente; si empatan o solo hay una, esa. ¿Confirmás este criterio o preferís otro (ej. la moneda base/declarada de la reserva)? **(El lugar de implementación ya está decidido y no es pregunta: la regla vive en `ReservaMoneyCalculator` / el armado del DTO, un único lugar backend; el front solo consume el valor. Lo único abierto es el criterio de priorización.)**
2. **Alcance de los botones con motivo:** ¿esta política de "botón visible + apagado + motivo" se aplica solo en la pantalla de detalle de la reserva, o también en los listados (tabla/tarjetas) y en la pantalla de cobranza? Esto define cuántas pantallas entran al UX gate.

---

> Próximo paso: review de `software-architect-reviewer` y, antes de tocar pantallas (PaymentModal, barra de acciones, listados), UX gate con `ux-ui-disenador` sobre `docs/ux/guia-ux-gaston.md`, relayando a Gastón las dos preguntas abiertas verbatim.
