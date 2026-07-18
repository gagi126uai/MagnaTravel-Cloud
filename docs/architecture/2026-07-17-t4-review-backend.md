# Review backend — Tanda 4 (ADR-048, modelo de estados derivados) — 2026-07-17

Revisor: backend-dotnet-reviewer. Alcance: SOLO las adiciones backend nuevas de T4 en el diff sin
commitear. T3 (ya aprobada) y el frontend quedan fuera. No se tocó código.

## Veredicto

**Changes required.**

Dos motivos, ambos accionables:

- **B1** — La etiqueta por servicio puede afirmar "Multa cobrada" en falso en reservas con más de una
  anulación que comparten operador (la fuente de "cobrada" solo mira UNA anulación, pero se estampa sobre
  las líneas de TODAS). El límite documentado en el código NO cubre este caso; y viola el default seguro
  que la propia spec pide (ante duda, "Con multa" pendiente, nunca "cobrada" falsa).
- **B2** — La proyección por servicio (`StampCancellationPenaltyPerServiceAsync`) no tiene NINGÚN test
  backend. Es lógica nueva, adyacente a plata, y su parte riesgosa (la correlación) queda sin fijar.

El resto de T4 (booleano `IsVoided`, delegación de `IsCancelledLikeStatus`, los 6 DTOs, el test del
helper de dominio) está bien y es la parte segura de la tanda.

---

## Hechos verificados

- `EstadoReserva.VoidedStatuses` = `{ Cancelled, PendingOperatorRefund }` es la única definición del par
  (Reserva.cs:201-205). `IsVoidedStatus` delega en `ContainsStatus` (Reserva.cs:212-213).
- `IsCancelledLikeStatus` (ReservaService.cs:5379-5380) ahora delega en `EstadoReserva.IsVoidedStatus`;
  antes era `status == Cancelled || status == PendingOperatorRefund`. Callers: CustomerService.cs:1690,
  ReservaService.cs:5437 (guard de `DeriveCancelledMoneyContextAsync`) y ReservaService.cs:5590. Los tres
  reciben el `Status` canónico desde la DB.
- `ReservaDto.IsVoided` y `ReservaListDto.IsVoided` se setean con la MISMA expresión
  (`EstadoReserva.IsVoidedStatus(dto.Status)`) en los dos overloads de `ApplyEconomicFlags`
  (ReservaService.cs:5193 detalle, :5225 listado). Consistente detalle-vs-listado.
- `CancellationPenaltyState` agregado a los 6 DTOs de servicio como `public string? ... { get; set; }`
  (Hotel/Flight/Transfer/Package/Assistance/ServicioReserva). Naming/patrón/ubicación consistentes;
  nullable → null-safe en serialización.
- `StampCancellationPenaltyPerServiceAsync` corre SOLO en el detalle (llamada única ReservaService.cs:2749),
  gateada por `tieneAlgunServicioAnulado`. El listado NO lo ejecuta → no castiga el listado.
- Sin N+1 de DB: una sola query (`lineasConMulta`, ReservaService.cs:5269). El resto (mapa
  supplierId↔publicId, `BuscarPublicId`) se resuelve sobre colecciones ya cargadas en memoria.
- `EstadoReservaVoidedStatusTests` cubre lo que dice: ambos estados voided → true; 7 estados
  vivos/terminales-no-anulados → false; null/empty → false; el set contiene exactamente el par
  (EstadoReservaVoidedStatusTests.cs:15-57).

---

## Blocking issues

### B1 — "Multa cobrada" en falso: mismatch de alcance entre las líneas y la fuente de "cobrada"

- `StampCancellationPenaltyPerServiceAsync` trae las líneas con multa de **todas** las anulaciones no
  abortadas de la reserva: `l.BookingCancellation.ReservaId == file.Id && Status != Aborted`
  (ReservaService.cs:5269-5276).
- La fuente de "cobrada por completo" es `dto.OperatorPenaltySituations`, que viene de
  `GetOperatorPenaltySituationsAsync` → esa lista se arma sobre **una sola** anulación: la más reciente
  no abortada (`OrderByDescending(b => b.DraftedAt).FirstOrDefaultAsync`,
  BookingCancellationService.cs:7023-7039). `IsFullyCollected` = estado de la ND de ESA anulación
  (BookingCancellationService.cs:7247, y su gemelo en el camino secundario).
- Consecuencia real (no teórica): reserva con DOS anulaciones separadas (ADR-025 permite anular servicio A
  ahora y servicio B después → dos `BookingCancellation`) que comparten el mismo operador:
  - `cobradaPorCompletoPorSupplierId[operador]` toma el estado de la anulación **más reciente**.
  - `lineasConMulta` incluye la línea del servicio A (anulación vieja) y la del B (anulación nueva).
  - Ambas se estampan con el estado de la anulación nueva (ReservaService.cs:5306-5311 + el switch).
  - Si la ND nueva está cobrada y la vieja NO, el servicio A muestra **"Multa cobrada"** siendo falso.
- El `<summary>` documenta un "límite conocido" DISTINTO: "dos servicios del mismo operador en pasos
  distintos dentro de la MISMA cancelación" (ReservaService.cs:5242-5249). Ese caso en realidad es benigno:
  dentro de un mismo BC la ND es **compartida**, así que `IsFullyCollected` es idéntico para todas sus
  líneas. El caso que SÍ miente es el **cross-BC** (dos anulaciones distintas), y ese no está documentado
  ni contemplado.
- Rompe el default seguro que pide la spec y que el owner exige: ante duda, mostrar "Con multa" pendiente,
  nunca "cobrada" en falso. En la única dirección donde la correlación por-operador es benigna
  (understatement) el código está bien; en esta dirección sobre-afirma cobro.

Fix sugerido (chico, dentro del alcance de la tanda):
- Acotar `lineasConMulta` a la MISMA anulación que refleja `OperatorPenaltySituations` (la más reciente no
  abortada), para que ambas fuentes tengan el mismo alcance. Las líneas de anulaciones viejas quedan sin
  etiqueta (no mienten) en vez de heredar cobro ajeno; o
- Traer también `l.BookingCancellationId` en la proyección y forzar `"Pending"` para toda línea cuya
  anulación ≠ la que alimenta las situaciones. Garantiza el default seguro sin recortar la señal "Con
  multa".

### B2 — Sin test backend de `StampCancellationPenaltyPerServiceAsync`

- No hay ningún test backend que ejerza la proyección: ni el happy path (una multa confirmada → "Pending";
  operador cobrado → "Collected"; servicio sin multa → null), ni el borde multi-anulación/multi-operador de
  B1, ni la degradación silenciosa cuando no se puede correlacionar el operador (Genérico sin `Supplier`).
- Lo único nuevo que corre es el test frontend `t4TachadoYMultaPorServicio.test.mjs` (no valida el backend)
  y el de integración `Adr048T3ListInvoicingStatusIntegrationTests` (es T3: `InvoicingStatus`/
  `FullyReturned`, no toca `CancellationPenaltyState`).
- Es comportamiento nuevo, adyacente a plata, y su parte riesgosa (la correlación) queda sin fijar. Con el
  bug B1 vivo, un test del caso multi-anulación lo habría atrapado.

---

## Non-blocking improvements

- **N1 — `IsCancelledLikeStatus` cambió de ordinal a case-insensitive.** El `==` viejo era exacto; el nuevo
  pasa por `ContainsStatus`, que compara `OrdinalIgnoreCase` (Reserva.cs:216-228). Los tres callers
  alimentan el `Status` canónico de la DB, así que es inocuo (incluso algo más robusto), pero es un cambio
  de comportamiento real, no una identidad. El nuevo XML-doc ya no afirma "cuerpo idéntico" (bien); solo
  dejarlo anotado.
- **N2 — Doc drift en los DTOs.** El XML-doc de `HotelBookingDto.CancellationPenaltyState` remite a
  "`CancellationPenaltyStateProjector` en ReservaService", pero el método real se llama
  `StampCancellationPenaltyPerServiceAsync`. Quien lo busque por ese nombre no lo encuentra. Alinear el
  nombre en el doc.
- **N3 — Tokens mágicos "Pending"/"Collected".** Se producen como string literal en ReservaService
  (5308, 5309) y se consumen en el front. Es consistente con la convención de tokens-string ya usada por
  `InvoicingStatus`/`CollectionStatus`, así que es aceptable, pero sin una constante compartida front/back
  pueden divergir en silencio. Una constante en el DTO o un `const` evitaría el riesgo.
- **N4 — `CancellationToken.None` en la llamada.** ReservaService.cs:2749 invoca la proyección con
  `CancellationToken.None` aunque el método acepta un `ct` y hace I/O (la query de líneas). Si el método
  contenedor tiene un `ct` real, esta query extra no honra la cancelación. Menor; pasar el `ct` del
  contenedor si está disponible.

---

## Security / data risks

- No se detecta fuga de PII/costo nueva: `CancellationPenaltyState` viaja "Pending"/"Collected"/null, sin
  montos ni datos de proveedor. La lista `Charges` (que sí lleva costo) ya se enmascara por `canSeeCost`
  aguas arriba (BookingCancellationService.cs:7060-7085) y no se toca acá.
- Riesgo de dato (no de seguridad): B1 es una afirmación de plata (cobro de multa) potencialmente falsa en
  pantalla. En el contexto del owner (modelo de estados que no debe mentir) cuenta como riesgo de datos de
  cara al usuario, no solo cosmético.

---

## Missing tests

- Backend: happy path de la proyección (Pending / Collected / null por servicio) — ver B2.
- Backend: caso multi-anulación mismo operador con cobro divergente (el que expone B1).
- Backend: degradación silenciosa a "Pending" cuando falta correlación (Genérico sin `Supplier` cargado,
  o operador ausente en `OperatorPenaltySituations`) — la spec afirma que nunca debe caer en "Collected"
  sin poder confirmarlo; hoy eso no está fijado por ningún test.

---

## Domain concerns

- El par {Cancelled, PendingOperatorRefund} como única fuente de "sin efecto" es correcto y coherente con el
  circuito de anulación (PendingOperatorRefund = paso intermedio mientras el operador termina de devolver).
  Esta parte es sólida y es exactamente el tipo de unificación que pide el "modelo de estados coherente".
- La etiqueta por servicio mezcla dos granularidades distintas (línea de cancelación por servicio vs.
  situación por operador de UNA anulación). Mientras esas dos granularidades no se alineen en alcance (B1),
  la coherencia que persigue la tanda no se cumple del todo para reservas con múltiples anulaciones.

---

## Suggested fixes

1. (B1) Alinear el alcance: acotar `lineasConMulta` a la anulación reflejada por `OperatorPenaltySituations`,
   o traer `BookingCancellationId` y forzar "Pending" para líneas de otras anulaciones. Actualizar el
   "límite conocido" del `<summary>` para que describa el caso cross-BC real.
2. (B2) Agregar los tests backend listados en "Missing tests".
3. (N2) Corregir el nombre del método en el XML-doc de los DTOs.
4. (N3/N4) Opcional: constante compartida para los tokens; pasar el `ct` real.

---

## Commands that should be run

- `dotnet test` del proyecto de Unit tests (incluye `EstadoReservaVoidedStatusTests` nuevo) para confirmar
  verde tras los fixes.
- La suite de integración Postgres (CI) para `Adr048T3ListInvoicingStatusIntegrationTests` — es T3, pero
  corre en el mismo push; conviene verificar que no rompió.
- No corrí ninguno de estos: reporto lo que se debería correr, no resultados.

---

## No verificado

- **No se corrió la app real ni ningún test** en esta revisión (solo lectura estática del diff). No afirmo
  que compile ni que la suite esté verde.
- No verifiqué el frontend (`ServiceList.jsx`, `CancellationPenaltyLabel.jsx`, `moneyStatus.js`) — lo revisa
  otro agente; en particular, cómo pinta el front un `CancellationPenaltyState` = null vs "Pending" vs
  "Collected" no está validado acá.
- No reproduje el caso B1 con datos reales (dos anulaciones mismo operador con cobro divergente). El análisis
  es por lectura del código de ambas fuentes; recomiendo confirmarlo con un test antes de descartar o
  aceptar el riesgo.
- Frecuencia real de reservas con ≥2 anulaciones que comparten operador: no medida contra la base de PROD.

---

## Re-review (2026-07-17) — B1/B2 corregidos

**Veredicto: APROBADO.**

Verifiqué por lectura estática del código nuevo (`StampCancellationPenaltyPerServiceAsync`,
ReservaService.cs:5280-5454) y del test nuevo (`StampCancellationPenaltyPerServiceTests.cs`). No corrí la
app ni los tests.

- **(a) ¿"Multa cobrada" en falso ya es imposible? SÍ.** Trazado el caso multi-BC mismo operador: "Collected"
  ahora exige que la línea pertenezca EXACTAMENTE a `bcCorrelacionadaId` (Pass A, ReservaService.cs:5408-5415)
  Y que ese operador tenga `IsFullyCollected` — que proviene de esa misma anulación (ND compartida). La línea
  de la anulación vieja (`BookingCancellationId != bcCorrelacionadaId`) cae a "Pending" sin excepción. La
  única vía a "Collected" refiere siempre a la propia ND de la línea; el sobre-conteo cruzado desapareció.

- **(b) ¿Criterio de correlación espejado EXACTO? SÍ.** Ambas queries usan el mismo filtro
  (`Status != Aborted`) + el mismo orden (`OrderByDescending(b => b.DraftedAt)`) + `FirstOrDefault`:
  ReservaService.cs:5300-5305 vs BookingCancellationService.cs:7023-7039. Residual menor no bloqueante: si
  dos anulaciones no abortadas comparten `DraftedAt` idéntico, el desempate queda indefinido y las dos
  queries SEPARADAS podrían resolverlo distinto (reabriría el desalineo). Improbable con timestamp de alta
  resolución; si se quiere blindar, agregar un desempate secundario común (ej. `.ThenByDescending(b => b.Id)`)
  en AMBOS lugares.

- **(c) ¿Los 7 tests fijan el comportamiento sin mockear de más? SÍ.** Corren el `ReservaService` REAL contra
  un `AppDbContext` InMemory REAL con `BookingCancellation`/`Lines`/`HotelBooking` sembrados de verdad; el
  único mock es `IBookingCancellationService.GetOperatorPenaltySituationsAsync`, que es el seam correcto (es
  un colaborador separado con sus propios tests — `CancellationCorrectPenaltyAndSituationTests` cubre
  `IsFullyCollected`). La query de correlación, `lineasConMultaConfirmada`, `BuscarPublicId` y los dos passes
  se ejecutan reales. El test B1 (`MultiCancellation_OlderLineNeverInheritsNewerCancellationCollectedStatus`,
  líneas 258-324) usa `DraftedAt` distintos (hace un mes vs hoy) y asserta vieja→"Pending", nueva→"Collected":
  atrapa el bug original. DC1 en-trámite y la degradación segura ("nunca Collected sin correlación") también
  quedan fijados.

Nota: la cobertura es InMemory (no Postgres). La forma de la query de correlación es traducible trivialmente,
así que el riesgo de divergencia SQL es bajo; aun así, no está verificada contra Postgres en esta re-review.

Cierro B1 y B2. Los menores N2 (doc drift del nombre del método), N3 (tokens sin constante) y el residual de
desempate `DraftedAt` quedan como no bloqueantes.
