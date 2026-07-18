# Data-exposure review — Tandas 3+4 (ADR-048), 2026-07-17

Alcance: diff completo sin commitear al momento de la revisión (eje `FullyReturned` en
`ReservaInvoicingStatus`, campo `IsVoided` en `ReservaDto`/`ReservaListDto`, campo
`CancellationPenaltyState` en los 6 DTOs de servicio, y todo el frontend que los consume).

## Verdict

**Approved**

## Hechos verificados

- `src/TravelApi.Application/DTOs/ReservaInvoicingStatus.cs:30-91` — cuatro valores posibles del
  campo `InvoicingStatus`: `NotInvoiced`, `PartiallyInvoiced`, `FullyInvoiced`, `FullyReturned`
  (tokens en inglés, viajan en el DTO al navegador — es el patrón ya existente en el proyecto para
  este tipo de eje derivado, no un dato nuevo en su forma).
- `src/TravelWeb/src/features/reservas/components/ReservaStatusChips.jsx:35-60,141` — mapa
  `INVOICING_CHIP` traduce los 4 valores a español; línea 141
  `INVOICING_CHIP[reserva.invoicingStatus] || INVOICING_CHIP.NotInvoiced` tiene fallback seguro:
  cualquier valor no mapeado (incluido `undefined`/token futuro) cae en "Sin facturar", nunca
  pinta el string crudo.
- `src/TravelWeb/src/features/reservas/components/EstadoCuentaResumen.jsx:458-495` — función
  `ChipInvoicingStatus` cubre explícitamente los 4 valores con `if` en cascada y termina en
  `return null` para cualquier valor no reconocido (no hay caso donde el string en inglés se
  imprima).
- `src/TravelApi.Application/DTOs/HotelBookingDto.cs:64-71` + los 5 DTOs hermanos
  (`AssistanceBookingDto.cs:72-74`, `FlightSegmentDto.cs:78-81`, `PackageBookingDto.cs:66-68`,
  `TransferBookingDto.cs:66-68`, `ServicioReservaDto.cs:48-50`) — campo nuevo
  `CancellationPenaltyState` (`string?`), valores backend: `"Pending"` / `"Collected"` / `null`
  (ver `ReservaService.cs:5286-5290`, función local `EstadoParaLinea`).
- `src/TravelWeb/src/features/reservas/components/CancellationPenaltyLabel.jsx:20-48` — únicos
  tres caminos: `"Pending"` → "Con multa" (ámbar), `"Collected"` → "✓ Multa cobrada" (gris),
  **cualquier otro valor (incluido el string crudo `"Pending"`/`"Collected"` mal tipeado, `null`,
  `undefined`, o un quinto valor futuro) → `return null`**. No hay rama que imprima
  `cancellationPenaltyState` interpolado en el JSX. Verificado también por
  `t4TachadoYMultaPorServicio.test.mjs:60-93`, incluido el test que confirma que el texto nunca
  lleva números interpolados.
- `src/TravelWeb/src/features/reservas/components/ServiceList.jsx:1389-1394,1765-1770` — el
  componente se monta pasando `svc.cancellationPenaltyState` directo (sin transformación previa
  que pudiera inyectar un valor no controlado).
- `src/TravelApi.Application/DTOs/ReservaDto.cs:277-284` y `ReservaListDto.cs:31-35` — campo nuevo
  `IsVoided` (`bool`). Un booleano no tiene superficie de fuga de token técnico (no hay string que
  mapear ni fallback que se salte).
- `src/TravelWeb/src/features/reservas/moneyStatus.js:53-61` — `isReservaAnulada` lee
  `reserva.isVoided` cuando es `boolean`; si no está, cae a un `Set` de fallback por nombre de
  estado (`Cancelled`/`PendingOperatorRefund`) — esto es lógica de control (true/false), nunca
  imprime el string de estado a pantalla.
- `src/TravelApi.Infrastructure/Services/ReservaService.cs:5188-5193,5220-5224` — `dto.IsVoided =
  EstadoReserva.IsVoidedStatus(dto.Status)` en los dos overloads (detalle y listado); mismo booleano,
  misma fuente (`EstadoReserva.VoidedStatuses`), sin cálculo divergente.
- `src/TravelApi.Infrastructure/Services/ReservaService.cs:5225-5360` (`StampCancellationPenaltyPerServiceAsync`)
  — la proyección por servicio arma `CancellationPenaltyState` solo con los literales `"Collected"`/
  `"Pending"` (línea ~5286-5290, función local `EstadoParaLinea`); no hay camino donde se
  interpole `ex.Message`, `PublicId`, `SupplierId` interno, ni ningún otro dato crudo en ese campo.
- `src/TravelApi.Domain/Entities/Reserva.cs:401-410` — los dos mensajes de excepción de dominio
  tocados indirectamente (`NotSaleFirmForChargeMessage`, `NoPendingBalanceForChargeMessage`) no
  cambiaron en este diff; siguen siendo texto en español sin datos sensibles (confirmado por el
  comentario explícito en el código "Sin datos sensibles").
- `src/TravelWeb/src/features/customers/pages/CustomerAccountPage.jsx:836`,
  `src/TravelWeb/src/features/reservas/components/ReservaSummaryStrip.jsx:29`,
  `src/TravelWeb/src/features/reservas/pages/ReservaDetailPage.jsx:35,105,130,1368` — todos los
  call-sites migrados de comparar `reserva.status === "Cancelled"` a `isReservaAnulada(reserva)`
  son refactors de condición interna (booleanos), no tocan ningún texto user-facing nuevo.
- `src/TravelApi.Domain/Reservations/ReservaInvoicingCuadreCalculator.cs` — `BrutoEmitido` es un
  `decimal` interno de cálculo, nunca se serializa directo a un DTO expuesto (el front solo ve el
  `InvoicingStatus` ya derivado).

## Exposiciones bloqueantes

Ninguna encontrada.

## Exposiciones no bloqueantes

- `src/TravelWeb/src/features/reservas/components/ReservaStatusChips.jsx:184` —
  `data-testid={`chip-factura-${reserva.invoicingStatus || 'NotInvoiced'}`}` mete el token técnico
  en un atributo `data-testid` (incluido `FullyReturned` nuevo). Es un `data-testid`, no texto
  visible en pantalla, y sigue el mismo patrón que ya usaba el resto del componente para los otros
  tres valores (no es un patrón nuevo introducido por este diff). Lo anoto porque es visible en
  devtools/DOM inspector, pero no cumple el catálogo de "user-visible surface" del gate (no es
  texto que el agente lea). No bloqueante.
- `src/TravelApi.Application/DTOs/HotelBookingDto.cs:64-71` (y hermanos) — el campo se llama
  `CancellationPenaltyState` con valores en inglés (`Pending`/`Collected`) igual que el resto de
  los ejes de estado del proyecto (`InvoicingStatus`, `CollectionStatus`). Es el patrón
  arquitectónico ya establecido y aceptado en este codebase (el frontend siempre traduce), no una
  desviación de esta tanda. Si algún día se agrega un consumidor nuevo de este campo (ej. un
  export, un PDF, un mensaje de WhatsApp) que no pase por `CancellationPenaltyLabel.jsx`, ese
  consumidor nuevo SÍ tendría que mapearlo — dejar la advertencia para revisiones futuras.

## Backend: respuestas y errores revisados

- `ReservaService.cs` — `FillInvoicingStatusForListAsync` (listado) y el cálculo del detalle
  (`dto.InvoicingStatus = ReservaInvoicingStatus.Derive(...)`, línea ~2564): ambos caminos success
  llenan el campo con uno de los 4 tokens controlados por la clase estática
  `ReservaInvoicingStatus`. No hay camino de error propio en este cálculo (es aritmética pura sobre
  datos ya cargados) — no se agregó ningún `try/catch` nuevo que pudiera filtrar una excepción a
  este campo.
- `StampCancellationPenaltyPerServiceAsync` — método nuevo, sin manejo de excepción propio (no
  atrapa nada); si algo revienta acá, sube al catch genérico existente del endpoint de detalle de
  reserva (no tocado en este diff, fuera de alcance de esta revisión puntual pero ya cubierto por
  el gate en revisiones previas de ese controller).
- No se tocó ningún controller en este diff (solo DTOs, entidad de dominio, calculador de dominio y
  el service de infraestructura que llena los DTOs). No hay endpoint nuevo ni catch nuevo que
  revisar.

## Frontend: superficies revisadas

- `ReservaStatusChips.jsx` — chip "Factura" (mapa `INVOICING_CHIP`, fallback seguro).
- `EstadoCuentaResumen.jsx` — chip "Factura" del estado de cuenta (`ChipInvoicingStatus`, fallback
  `null`).
- `CancellationPenaltyLabel.jsx` — etiqueta "Con multa"/"Multa cobrada" por servicio (fallback
  `null`, sin montos interpolados).
- `ServiceList.jsx` — monta `CancellationPenaltyLabel` en desktop y mobile; también agrega tachado
  visual (`line-through`) a costo/precio de servicios anulados — cambio puramente de estilo, sin
  texto nuevo.
- `moneyStatus.js` — `isReservaAnulada` ahora prioriza `reserva.isVoided` (booleano del backend)
  sobre el chequeo de string; no imprime nada, solo decide ramas.
- `CustomerAccountPage.jsx`, `ReservaSummaryStrip.jsx`, `ReservaDetailPage.jsx` — call-sites
  migrados a `isReservaAnulada(reserva)`; sin cambio de texto visible.

## Otras superficies

- PDF/voucher/recibo: no tocados en este diff.
- Mensajes WhatsApp/email: no tocados en este diff.
- Exports: no tocados en este diff.
- Logs: no se agregó ningún `_logger`/`console.log` nuevo en el diff revisado.

## Fallback amistoso presente?

No aplica en el sentido de "error del sistema" — este diff no agrega ningún catch de excepción ni
mensaje de error nuevo. Los "fallbacks" relevantes acá son de **mapeo de valor desconocido a
label**, y ambos están presentes y verificados:
- `ReservaStatusChips.jsx:141` → cae a "Sin facturar".
- `EstadoCuentaResumen.jsx:459,494` → cae a "Sin facturar" (valor vacío/`NotInvoiced`) o `null`
  (cualquier otro valor no reconocido).
- `CancellationPenaltyLabel.jsx:47` → cae a `null` (no se muestra nada) para cualquier valor no
  reconocido.

## Missing tests

Ninguno bloqueante. Cobertura ya existe para los casos de fallback:
- `adr037Facturacion.test.mjs` — valor ausente/desconocido → "Sin facturar" (ambas pantallas,
  ficha y estado de cuenta).
- `t4TachadoYMultaPorServicio.test.mjs` — `cancellationPenaltyState` null/undefined → no se
  muestra nada; test explícito de que el texto nunca lleva números interpolados.

Sugerencia no bloqueante para una tanda futura: agregar un test que cubra explícitamente un QUINTO
valor inventado (ej. `"Refunded"` no soportado aún) en `CancellationPenaltyLabel` y en
`ChipInvoicingStatus`, para blindar contra que un futuro ADR agregue un token backend sin agregar
el mapeo frontend en el mismo PR (hoy el fallback ya es seguro, pero no hay test que lo pruebe
para un valor "futuro" específicamente en estos dos componentes — sí lo hay para `INVOICING_CHIP`
vía el test `'Otra'`).

## No verificado

- No corrí la app en runtime (build/dev server) para confirmar visualmente que ningún otro
  componente no tocado en este diff (fuera del grep realizado) interpole `invoicingStatus` o
  `cancellationPenaltyState` sin pasar por los mapeos ya revisados. La búsqueda fue estática
  (lectura de diff + grep dirigido); no hice un grep exhaustivo de **todo** el repo por
  `invoicingStatus` fuera de los archivos tocados en este diff (el gate pidió revisar el diff, no
  un barrido total del código no tocado).
- No verifiqué el comportamiento del endpoint HTTP real (Swagger/Postman) para confirmar que el
  JSON serializado no incluye algún campo interno adicional no visto en el DTO (asumo que
  `System.Text.Json` serializa solo las propiedades públicas del DTO, que es lo que se leyó).
