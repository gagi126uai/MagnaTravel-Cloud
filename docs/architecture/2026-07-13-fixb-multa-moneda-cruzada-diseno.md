# Fix B — Salida real para "multa en moneda X ≠ moneda de la factura original"

**Fecha:** 2026-07-13 · **Autor:** software-architect (Fable) · **Estado:** PROPUESTO — pendiente
`software-architect-reviewer`, gate UX con Gaston (`ux-ui-disenador`), y firma de negocio del dueño.
**Contexto:** ADR-044 (multas por operador, T0–T5 en producción). Caso real: reserva **F-2026-1033**,
`BookingCancellation Id=4` (legacy, sin cargos tipificados T2), multa del operador **USD 200**, factura
original tipo C **en pesos**.

> Regla de la casa: esto NO se implementa hasta cerrar el gate UX con Gaston (cambia la pantalla de
> "Corregir monto y moneda") y la revisión del reviewer de arquitectura. Este documento es el diseño.

---

## 1. Qué pasa hoy (verificado en el código, no asumido)

El "loop infinito" es real. Cadena confirmada leyendo el código:

1. `EvaluateDebitNoteGating`, bloque **(A) COHERENCIA** — `BookingCancellationService.cs:9273-9291`.
   Compara la moneda **declarada** de la multa (`bc.PenaltyCurrencyAtEvent`, "USD"→ARCA "DOL") contra la
   moneda de la factura ("PES"). Como difieren, devuelve el motivo
   *"La multa se cargó en dólares (US$) pero la factura original está en pesos: lo tiene que revisar una
   persona."* y NO emite. El comentario del guard es explícito: *"NO convertimos con TC"*.

2. `TryEmitCancellationDebitNoteAsync` (`:8272-8281`) toma ese motivo → `RouteDebitNoteToManualReviewAsync`
   → `DebitNoteStatus = ManualReview`.

3. `OperatorPenaltySituationRules.Derive` (`OperatorPenaltySituationRules.cs:67`) mapea `ManualReview` →
   `DebitNoteNeedsAmountCurrency` (estado 4).

4. La ficha (`operatorPenaltyBanner.js:303-321`, rama sin cargo-trasladable-sin-factura) ofrece
   **"Corregir monto y moneda"** → `CorrectPenaltyAsync`.

5. `CorrectPenaltyAsync` (`:6533-6534`) re-graba `PenaltyAmountAtEvent`/`PenaltyCurrencyAtEvent` con el
   mismo USD, re-encola (`:6583`) → **vuelve al paso 1**. **Loop.**

**Por qué la maquinaria T3b no lo salva (el punto clave).** `BuildCancellationDebitNoteItemsAsync`
(que SÍ sabe convertir un cargo en moneda distinta a la de su factura, `:8698-8751`) corre **después**
del gating, y para un BC legacy **sin cargos tipificados** (`allCharges.Count == 0`, `:8578`) cae a
`LegacySingleItem` (`:8876`), que usa `bc.PenaltyAmountAtEvent` **sin conversión**. Es decir: aunque el
gating dejara pasar, se emitiría un renglón de "200" en una factura en pesos → **escala equivocada**
(200 pesos en vez de ~240.000). El guard (A) está protegiendo exactamente contra eso. La conversión T3b
solo existe **por cargo tipificado**; el renglón legacy singular nunca la toca.

**Por qué el confirm/correct NO crea un cargo tipificado para este caso (evidencia contra la opción a).**
`AllocateConfirmedPenaltyToLinesAsync` (`:7773`): para USD 200 sobre una **línea de servicio en ARS**,
`candidateLines` = líneas cuya `Currency == penaltyCurrency(USD)` = **vacío** (`:7877-7880`) →
`totalCapBeforePenalty == 0` → **retorna en `:7890` sin crear ningún cargo**. Además el cargo se construye
SIEMPRE con `Currency = Monedas.Normalizar(line.Currency)` (`:7959`), o sea nunca puede nacer en USD sobre
una línea ARS: es la invariante **B2** del Addendum T2 (charge.Currency == Line.Currency), firmada.
**Conclusión: la opción (a) NO está "a medio cablear" — está estructuralmente ausente**, y cablearla
exige reabrir B2 y la semántica de neteo del `RefundCap`.

**Matiz de honestidad (no sobreactuar la severidad).** La salida "sin salida" no es un deadlock absoluto:
hoy el usuario PUEDE escapar cargando la multa directamente en **pesos** (la moneda de la factura),
convirtiendo de cabeza. El propio pedido lo llama *"mentir la moneda"*. El defecto real es doble:
(1) la pantalla no **guía** la conversión (deja re-elegir USD y volver a trabar), y (2) **no queda
auditado** con qué TC se convirtió. El fix convierte esa "mentira manual" en una conversión **calculada
por el servidor y auditada**.

**El confirm del Día-0 tiene el mismo defecto.** `ConfirmPenaltyAsync` también dispara `TryEmit`
(`:6199`), así que una anulación NUEVA con multa USD sobre factura ARS también se traba (y después pide
"Corregir"). El fix debe cubrir **confirm y correct** con el mismo helper.

---

## 2. Opciones

**(a) Rutear por la maquinaria T3b (crear el cargo tipificado en USD y convertir en `BuildCancellation…`).**
Exige reabrir la invariante firmada B2 (charge.Currency == Line.Currency), inventar un cargo USD que NO
netea el `RefundCap` ARS, capturar el TC estimado, y aflojar el gating para dejar pasar el cruce. Es una
**tanda entera** en área de plata, no un fix mínimo; reabre una decisión firmada. **Rechazada para este
arreglo** (sí es la dirección correcta a mediano plazo — ver §7).

**(b) Convertir el renglón legacy en el gating/emisión con el mismo TC + banda de sanidad, sin cargos.**
Requiere **relajar el guard (A)** (de "bloquear todo cruce" a "bloquear salvo que haya TC"), agregar un
hogar para el TC en el BC padre (migración), y meter conversión en `LegacySingleItem`. **Debilita la
última línea de defensa** contra la escala equivocada justo en el path de emisión. Rechazada por riesgo.

**(c) Mantener el bloqueo intacto y cambiar la SALIDA: capturar el monto YA convertido con un TC sugerido,
auditado.** El guard (A) queda como **invariante dura** (moneda declarada == moneda de la factura al
emitir); se satisface **convirtiendo antes de guardar**, en el momento de confirmar/corregir, con TC
explícito y auditable. Cero cambios en gating/emisión; reusa la maquinaria existente. **Recomendada.**

---

## 3. Opción elegida: (c) refinada — "convertir al capturar, guard intacto"

**Idea en una línea:** cuando la moneda de la multa ≠ la moneda de la factura destino, el **servidor**
convierte a la moneda de la factura **al confirmar/corregir**, usando un TC provisto por el usuario
(sugerido para la **fecha en que el operador cobró**, `OperatorPenaltyConfirmedDate`; manual con
justificación, como toda factura USD hoy), guarda el monto ya convertido en la moneda de la factura, y
deja registrado en auditoría el monto/moneda original + TC + fuente + fecha + justificación. De ahí en
más **todo el pipeline existente funciona sin tocarse**: el gating (A) pasa porque las monedas coinciden,
y la emisión usa el camino de siempre.

**Por qué (c) y no (a)/(b), en 3 líneas cada una:**
- vs (a): (a) reabre B2 + neteo del cap + gating + treasury FX = una tanda en plata; (c) no toca ninguna
  invariante firmada ni el motor de emisión. (c) es genuinamente el arreglo mínimo.
- vs (b): (b) convierte el guard de escala en un gate condicional (lo debilita); (c) lo deja como
  invariante dura — aun un error de TC del usuario da un monto ARS equivocado (visible y corregible),
  **nunca un comprobante en la escala/moneda equivocada**. (c) es más seguro.
- (c) honra decisión #1 (TC del día que cobró el operador, manual con justificación como hoy), decisión #3
  (banda de sanidad ya existe) y la regla "esconder complejidad con defaults" (el campo de TC solo aparece
  cuando hay cruce).

**Propiedad de seguridad central:** como la conversión ocurre ANTES de guardar y el guard (A) sigue
exigiendo monedas iguales al emitir, un TC mal cargado produce a lo sumo un **monto** ARS incorrecto
(que el usuario ve en el preview y puede corregir re-ejecutando "Corregir"), pero **es imposible** emitir
un comprobante cuyo número esté en la escala de otra moneda. El riesgo original del guard queda cubierto.

**Qué pasa con la factura destino:** para F-2026-1033 hay 1 sola factura activa → se convierte a ESA
(la `OriginatingInvoice`, ARS). El caso 2+ facturas de distinta moneda es el terreno T3b multi-factura,
fuera de alcance de este fix (sigue su propia ruta a revisión manual).

---

## 4. Cambios por archivo/método

### 4.1 Backend — servicio

`src/TravelApi.Infrastructure/Services/BookingCancellationService.cs`

- **Nuevo helper privado** `ConvertDeclaredPenaltyToInvoiceCurrency(...)` (o inline en un método
  compartido): dada la moneda declarada, la moneda de la factura destino, el monto, y un TC
  (valor+fuente+fecha+justificación):
  - Si declarada == factura → devuelve el monto tal cual (sin TC).
  - Si difieren → valida TC con **`IsUnreliableExchangeRate`** (ya existe, `:8845`) y convierte con
    **`ConvertArsUsdAmount`** (ya existe, `:8856`). Reusar estos dos, no reimplementar la matemática.
  - Si el par de monedas no es ARS/USD, o falta TC, o el TC es no confiable → devuelve un resultado
    "no convertible con motivo" (el caller responde 400 / mensaje claro; NUNCA inventa un número).
- **`CorrectPenaltyAsync`** (`:6425`): antes de `bc.PenaltyAmountAtEvent = amount` (`:6533`), si la moneda
  pedida ≠ la moneda de la factura destino, llamar al helper; guardar el **monto convertido** y
  `PenaltyCurrencyAtEvent = <moneda de la factura>` (ISO). Agregar al detail del audit
  (`OperatorPenaltyCorrected`, `:6559`) los campos: `declaredOriginalAmount`, `declaredOriginalCurrency`,
  `conversionExchangeRate`, `conversionExchangeRateSource`, `conversionExchangeRateDate`,
  `conversionExchangeRateJustification`. Todo lo demás (lock, reverse, allocate, re-emit) queda igual.
- **`ConfirmPenaltyAsync`** (path Día-0, alrededor de `:6191`): aplicar la MISMA conversión antes de
  imputar/emitir, con el mismo helper. (Si hay que recortar alcance, correct-penalty es lo mínimo que
  destraba F-2026-1033 y cualquier caso ya trabado; confirm es fast-follow para no dejar el mismo pozo
  a las anulaciones nuevas.)
- **NO se toca** `EvaluateDebitNoteGating`, `BuildCancellationDebitNoteItemsAsync`, `LegacySingleItem`,
  ni `AllocateConfirmedPenaltyToLinesAsync` (esta última seguirá su curso: con la multa ya en ARS,
  neteará el `RefundCap` ARS y creará un cargo ARS — comportamiento existente, sin cambios de firma).

### 4.2 Backend — API

`src/TravelApi/Controllers/CancellationsController.cs`

- Extender **`CorrectPenaltyRequest`** (`:1200`) con 4 campos opcionales:
  `decimal? ExchangeRate`, `string? ExchangeRateSource`, `DateTime? ExchangeRateDate`,
  `string? ExchangeRateJustification`. Validación: **requeridos cuando** `Currency` ≠ moneda de la
  factura destino (el service revalida server-side; no confiar en el front). Mapeo de errores: sumar
  400 "falta el tipo de cambio para convertir" cuando corresponda.
- Mismo tratamiento en el request de confirm si se cubre el path Día-0.

### 4.3 Backend — DTO de situación (mínimo, para que el front sepa cuándo mostrar el TC)

`src/TravelApi.Application/DTOs/OperatorPenaltySituationDto.cs`

- Exponer, si no está ya disponible en el contexto de la ficha, **la moneda de la factura destino** y la
  **fecha sugerida de TC** (`OperatorPenaltyConfirmedDate`). Campos **aditivos**, nullable: no rompen el
  contrato del front (`operatorPenaltyBanner.js` degrada seguro ante campos ausentes). Si el modal ya
  tiene la moneda de la factura por otra vía (revisar `ConfirmarMultaOperadorInline.jsx`), este cambio
  puede ser cero. Verificar antes de agregar.

### 4.4 Frontend (detrás del gate UX de Gaston)

`src/TravelWeb/src/features/cancellations/components/ConfirmarMultaOperadorInline.jsx` (modal de
"Corregir monto y moneda") + `api/cancellationsApi.js`:

- Cuando la moneda elegida ≠ la moneda de la factura, mostrar un campo de **tipo de cambio** (fecha por
  defecto = día que cobró el operador) + **justificación**, calcular y **previsualizar el monto
  convertido** (misma orientación ARS-por-1-USD), y enviarlo. Para moneda == factura, nada cambia (no se
  pide TC). Mensajes en criollo, sin jerga (gate data-exposure).
- Este es un cambio de flujo visible → **gate UX obligatorio con Gaston ANTES de implementar**
  (`ux-ui-disenador` sobre `docs/ux/guia-ux-gaston.md`; lo que no esté cubierto se le pregunta verbatim).

---

## 5. Tests obligatorios

Unit (`BookingCancellationService`, InMemory):
1. `CorrectPenaltyAsync` cruce USD→ARS con TC válido → `PenaltyCurrencyAtEvent=ARS`,
   `PenaltyAmountAtEvent=convertido`, el gating (A) pasa, ND encolada.
2. Cruce con TC ≤ 0 o == 1 (banda de sanidad) → rechazo/mensaje claro, **no** persiste.
3. Cruce sin TC → 400/mensaje claro, no persiste.
4. Mismo-moneda (ARS/ARS) → comportamiento **byte-idéntico** a hoy (no pide TC).
5. Audit `OperatorPenaltyCorrected` registra original USD + moneda + TC + fuente + fecha + justificación.
6. Invariante de plata tras la conversión: `AllocateConfirmedPenaltyToLinesAsync` netea el ARS convertido
   del cap ARS y se conserva `RefundCap + RetainedDeductionAmount == capBeforePenalty`.
7. Regresión del guard: USD-vs-ARS **sigue** trabando cuando NO hubo conversión (guard intacto); y pasa
   cuando ya se convirtió.
8. Anti-doble-cobro **INV-ADR013-001** preservado (no se crea deducción `CancellationPenalty`).
9. Si se cubre Día-0: `ConfirmPenaltyAsync` con multa USD sobre factura ARS + TC → emite en ARS sin
   pasar por el trap.

Integración (real DB, la corre QA/reviewer): correct-penalty cruce end-to-end → ND emitida con renglón en
la escala ARS correcta y comprobante `MonId=PES`.

Frontend (mjs): el modal muestra el campo de TC **solo** cuando la moneda ≠ la de la factura; calcula el
preview; lo oculta para mismo-moneda.

Gate **data-exposure** obligatorio (regla de la casa): mensajes del modal y del backend sin IDs/enums/
jerga; el JSON de auditoría queda interno.

---

## 6. Riesgos y mitigación

- **R1 — TC mal cargado → monto ARS incorrecto.** Mitigación: conversión server-side (fuente única),
  banda de sanidad (rechaza TC ≤0 o ==1), justificación obligatoria, preview antes de confirmar, y
  **corregible** (correct-penalty se puede re-ejecutar). El guard (A) sigue garantizando que **la escala
  del comprobante nunca es la equivocada** (a lo sumo el monto).
- **R2 — Cambio de comportamiento: el cruce ahora netea el `RefundCap`.** Antes (USD) no neteaba nada
  (`candidateLines` vacío); ahora (ARS) netea el equivalente. Es **más correcto** (el operador retuvo
  valor), pero es un cambio → cubierto por el test 6.
- **R3 — `PenaltyCurrencyAtEvent` pasa a ARS, ya no refleja "el operador cobró USD".** El extracto del
  operador se arma por línea (`SupplierCancellationCircuitReader`), no de este campo; el USD original
  queda en auditoría. Aceptable para el path legacy; se anota como límite.
- **R4 — Treasury FX (decisión #3) no se calcula para el cruce legacy.** El path legacy nunca lo hizo;
  este fix no lo regresiona ni lo resuelve. Se defiere (ver §7). Explícito, no silencioso.
- **R5 — Idempotencia del retry.** `CorrectPenaltyAsync` ya es atómico bajo lock + reverse + re-emit;
  guardar ARS fluye por el mismo camino. Sin cambios de idempotencia.

---

## 7. Qué NO tocar / deuda futura

**NO tocar en este fix:**
- El guard `EvaluateDebitNoteGating` bloque (A) — es la última línea de defensa, queda dura.
- La invariante **B2** (charge.Currency == Line.Currency) ni el neteo del `RefundCap`.
- `BuildCancellationDebitNoteItemsAsync` / conversión T3b / `LegacySingleItem`.
- Migraciones ya aplicadas. **Este fix no requiere migración nueva** (el original USD + TC viven en el
  JSON de auditoría; no se agregan columnas persistidas). Guardar el original en columnas estructuradas
  es una mejora **opcional**, solo necesaria cuando se construya treasury FX para el cruce.

**Deuda futura (fuera de alcance, anotada):** la dirección "correcta" de largo plazo es la opción (a):
modelar la retención genuinamente cross-currency como un **cargo tipificado no-neteante** en la moneda
del operador, con TC estimado, relajando B2 SOLO para ese caso y cableando treasury FX (decisión #3).
Eso subsume el path legacy singular y unifica todo bajo la maquinaria de cargos. Es una tanda propia con
firma fiscal, no un fix.

---

## 8. F-2026-1033 y demás BCs ya trabados en prod

**Se destraban solos con el fix desplegado, sin migración de datos.** Flujo: el usuario abre la ficha
(estado `DebitNoteNeedsAmountCurrency`) → "Corregir monto y moneda" → el modal detecta moneda USD ≠
factura ARS → pide TC (fecha por defecto = día que cobró el operador) + justificación → previsualiza
≈ ARS 240.000 → confirmar → el servidor convierte, guarda ARS, re-emite → el gating (A) pasa (ARS==ARS)
→ ND emitida en pesos. El **retry/correct existente** es el que los destraba; no hace falta backfill ni
tocar la base.

---

## Desafío (software-architect-reviewer, 2026-07-13)

**Veredicto: APROBADO CON CAMBIOS.** La dirección es correcta y la propiedad de seguridad central se
sostiene contra el código: convertir ANTES de guardar + dejar el guard (A) duro hace **imposible** emitir
un comprobante en la escala equivocada (a lo sumo un monto ARS erróneo, visible y corregible). Todas las
citas archivo:línea del diseño se verificaron y son correctas. Pero la **§6 (Riesgos) describe mal lo que
realmente hace el código** en el caso cross-currency genuino, y la instrucción del path confirm es
autocontradictoria. Hay que cerrar los bloqueantes antes de construir.

### Hechos verificados (no asumidos)
- Guard (A) COHERENCIA: `BookingCancellationService.cs:9273-9291`. Compara `PenaltyCurrencyAtEvent` (ISO→ARCA)
  vs `originatingInvoice.MonId`; si difieren → motivo → revisión manual. Confirmado.
- `LegacySingleItem` (`:8876-8888`) usa `bc.PenaltyAmountAtEvent!.Value` **sin conversión** → un renglón
  "200" en factura ARS. El guard protege exactamente eso. Confirmado.
- Helpers a reusar existen: `IsUnreliableExchangeRate` (`:8845`, `rate<=0 || rate==1`) y `ConvertArsUsdAmount`
  (`:8856`, sólo ARS/USD, devuelve `null` si no es par ARS/USD o TC no confiable). Confirmado — reusarlos es correcto.
- `CorrectPenaltyAsync` (`:6425`): orden real = Reverse (`:6529`) → set `PenaltyAmountAtEvent`/`Currency`
  (`:6533-6534`) → `PersistPenaltyCurrencyOnLinesAsync` (`:6540`) → `Allocate` (`:6541`) → reset huella ND
  (`:6547-6549`) → audit staged (`:6555`) → una `SaveChanges` (`:6576`) → `TryEmit` (`:6583`). Atómico bajo
  `RunUnderParentLockAsync`, con re-chequeo de Waived post-Reload. La conversión entra limpia antes de `:6533`.
- Anti-doble-cobro INV-ADR013-001: disyunción dura en gating (`:8283-8300`) y `OperatorRefundService.cs:374-420`.
  La conversión no toca deducciones `CancellationPenalty` → **se preserva** (el test 8 es legítimo).
- `CorrectPenaltyRequest` es un `record` posicional (`CancellationsController.cs:1200-1214`).

### B1 (BLOQUEANTE) — La §6-R2 es FALSA para el cross-currency genuino; el diseño confunde 3 monedas y trata sólo 2
El diseño dispara la conversión con "**moneda de la multa ≠ moneda de la factura**" y afirma (R2) que "ahora
el cruce **netea el RefundCap**" y crea un cargo ARS. Eso **sólo es cierto si la LÍNEA del operador está en
ARS**. El neteo real depende de la moneda de la **línea**, no de la factura:
- `AllocateConfirmedPenaltyToLinesAsync` filtra `candidateLines` por `line.Currency == penaltyCurrency`
  (`:7877-7880`), y `ResolvePenaltyAllocationCurrency` (`:8140-8152`) devuelve la moneda **pedida** (ARS tras
  convertir).
- **Caso A** (línea del operador en ARS, multa declarada USD): tras convertir a ARS, `candidateLines` = líneas
  ARS ≠ ∅ → netea, crea cargo ARS. Funciona. (Es el caso que asume el diseño.)
- **Caso B** (línea del operador en **USD** — el operador internacional retuvo USD 200 sobre servicio USD, cliente
  facturado ARS: el cross-currency de verdad): tras convertir la multa a ARS, `candidateLines` (`Currency==ARS`)
  = **∅** → `totalCapBeforePenalty==0` → **`return` en `:7890` sin netear y SIN crear cargo**. El `RefundCap`
  USD del operador queda **sobreestimado**, la retención USD **invisible** del lado operador, y sin embargo la ND
  al cliente **sí** emite en ARS. R2, tal como está escrita, es incorrecta en este caso.
- Peor: `PersistPenaltyCurrencyOnLinesAsync` (`:6540`, `:7717-7719`) estampa `line.PenaltyCurrency = ARS` en la
  línea **aunque `line.Currency` sea USD** → la línea queda internamente inconsistente (`PenaltyCurrency=ARS` ≠
  `Currency=USD`).

**Remediación (elegir una, explícita):** (i) verificar contra prod la moneda de la(s) línea(s) del operador de
F-2026-1033 y declarar en el doc a cuál caso pertenece; y (ii) definir el comportamiento de Caso B de forma
explícita: o se **rutea a revisión manual** (no se auto-convierte cuando `line.Currency == moneda declarada ≠
moneda factura`, porque el lado operador no se puede resolver con una sola conversión), o se documenta con
precisión que en Caso B el lado operador NO se netea y la retención USD sólo sobrevive en auditoría. Lo que NO
puede quedar es R2 afirmando incondicionalmente que "netea el RefundCap".
**Matiz de honestidad:** esto **no es una regresión** respecto del escape manual de hoy ("cargar la multa en
pesos") — hoy pasa lo mismo. El bloqueo es que el diseño **afirma resolver** algo que en Caso B no resuelve.

### B2 (BLOQUEANTE para el alcance confirm; no bloquea el MVP correct-only) — El punto de inserción del path confirm es autocontradictorio
§4.1 dice: `ConfirmPenaltyAsync` "**alrededor de :6191**: aplicar la conversión **antes de imputar/emitir**".
Pero en el código, cuando la ejecución llega a `:6191` la imputación y las escrituras **ya ocurrieron**:
`CaptureDebitNoteClassification` fija `PenaltyAmountAtEvent` (`:6076-6077`), `PenaltyCurrencyAtEvent` se setea
en `:6100-6101`, `PersistPenaltyCurrencyOnLinesAsync` en `:6109`, y `AllocateConfirmedPenaltyToLinesAsync` en
`:6118`. `:6191` es donde **emite** (`TryEmit`, `:6199`). Convertir en `:6191` dejaría líneas + cap +
`PenaltyAmountAtEvent` en **USD** y sólo la emisión en ARS → estado inconsistente.
**Remediación:** en confirm la conversión debe hacerse **arriba de todo**, antes de `:6076`, convirtiendo los
valores del `request` (`ConfirmedPenaltyAmount`/`PenaltyCurrency`) una sola vez; hay **≥4 sitios** que consumen
esos valores del request directamente. Corregir el número de línea del diseño (`:6191`→ antes de `:6076`) y
listar los 4 sitios. El diseño ya difiere confirm como fast-follow, así que esto bloquea la construcción del
**path confirm**, no el MVP correct-only.

### M1 (MAYOR) — R3 está al revés: el extracto del operador queda peor, no mejor
R3 dice que la pérdida "el operador cobró USD" es tolerable porque "el extracto del operador se arma **por
línea** (`SupplierCancellationCircuitReader`), **no de este campo**". Es exactamente lo contrario: en Caso A el
fix **crea** un `BookingCancellationLineOperatorCharge` `Kind=AdministrativeFee` en **ARS** (`:7953-7965`) y
setea `line.RetainedDeductionAmount` en ARS — y el extracto del operador se arma **justo** con
`RetainedDeductionAmount`/datos de línea (ADR-044 T2 B1). O sea: el dato de línea es precisamente lo que pasa a
decir "retuvo ARS 240.000" en vez de "USD 200". Para un operador cuya cuenta corriente es en USD, esto
**corrompe la moneda de su cuenta**. Reescribir R3 con esta realidad y decidir si es aceptable (para operador
ARS sí; para operador USD no — se cruza con B1).

### M2 (MAYOR) — Puerta de una sola vía: el USD original + TC sólo viven en un JSON de auditoría
§7 dice que guardar el original en columnas estructuradas es "**opcional**". Pero el cargo ARS que crea el fix
nace con `EstimatedExchangeRate*` = **null** (porque `Charge.Currency == moneda factura == ARS`, ver T3b: esos
campos sólo se llenan cuando hay conversión de renglón). Entonces el USD 200 + TC + fecha quedan **sólo** en el
blob JSON del audit `OperatorPenaltyCorrected`. La deuda futura (opción (a): retención cross-currency como cargo
no-neteante en moneda del operador + treasury FX, Decisión 3) **no podrá reconstruir** el origen USD desde el
cargo. Es un one-way door barato de evitar: persistir el `declaredOriginalAmount`/`Currency`/TC en columnas
estructuradas del BC o del charge **ahora** (no en el JSON), aunque el resto se defiera. Recomiendo subir esto
de "opcional" a "hacerlo en este fix".

### M3 (MAYOR) — El "TC sugerido del día que cobró el operador" puede no existir para los BCs legacy que este fix apunta
El default de fecha propuesto es `OperatorPenaltyConfirmedDate`. Ese campo se setea en el path diferido y sólo
para el operador **principal** (`:6088`). Para un BC legacy confirmado antes de ADR-014 (como el objetivo del
fix), es plausible que sea **null** → el modal no tendría fecha por defecto. Verificar y definir el fallback
(entrada manual de fecha, sin romper ni dejar el campo en blanco silencioso). No inventar una fecha.

### M4 (MAYOR) — El diseño afirma "camino de siempre / LegacySingleItem" pero el fix cambia de rama de emisión
§3/§4.1 dicen "de ahí en más todo el pipeline funciona sin tocarse … la emisión usa el camino de siempre
(`LegacySingleItem`)". Falso en Caso A: una vez que `Allocate` crea un cargo, `BuildCancellationDebitNoteItemsAsync`
ve `allCharges.Count > 0` (`:8578`) y **NO** entra a `LegacySingleItem` — toma la rama de **cargos tipificados
T3b** (`:8598+`), que resuelve `TargetInvoiceId` y podría convertir. Con 1 factura activa resuelve a
`originatingInvoice` (T3a) y el cargo ya está en ARS, así que el resultado es correcto — **pero es otra rama**.
El código no se edita, pero el **flujo de ejecución cambia**. El test 1 debe afirmar explícitamente que la rama
tipificada produce el **único renglón ARS** correcto y resuelve la factura destino bien (no asumir LegacySingleItem).

### Menores
- `CorrectPenaltyRequest` es `record` posicional: sumar 4 campos cambia la firma posicional. Agregarlos
  **al final**, nullable con default, para no romper llamadas posicionales (interno, pero prolijo).
- Frontend (`operatorPenaltyBanner.js`, `ConfirmarMultaOperadorInline.jsx`) **no verificado** en esta revisión;
  el gate UX con Gaston es obligatorio de todos modos.

### Idempotencia / concurrencia (revisado, sin objeción)
`CorrectPenaltyAsync` corre bajo `RunUnderParentLockAsync` con Reload + re-chequeo de Waived + re-evaluación del
guard de ND-en-vuelo dentro del lock; la conversión es aritmética pura antes del `SaveChanges` único. No cambia
el modelo de idempotencia/atomicidad. La guarda de idempotencia interna de `Allocate` (`:7855-7862`, no re-netear
si `PenaltyAmount.HasValue`) sigue válida porque `Reverse` (`:6529`) limpia antes. OK.

### No verificado
- Moneda real de las líneas del operador de F-2026-1033 (Caso A vs Caso B): **requiere lectura de prod** — es
  el dato que decide B1. No lo pude verificar en esta revisión.
- Comportamiento del frontend (banner/modal) y si el DTO de situación ya expone la moneda de la factura destino.

### Bloqueantes a cerrar antes de construir
1. **B1** — verificar Caso A/B en prod para F-2026-1033 y definir explícitamente el Caso B (rutear a manual o
   documentar con precisión que el lado operador no se netea). Corregir R2.
2. **B2** — corregir el punto de inserción del path confirm (antes de `:6076`, no `:6191`) y enumerar los 4
   sitios que consumen los valores del request. (Bloquea sólo el alcance confirm.)
3. **M2** — subir a obligatorio: persistir `declaredOriginalAmount`/`Currency`/TC en columnas estructuradas
   (no sólo JSON), para no cerrar la puerta a la opción (a)/treasury FX.
4. Reescribir **R3 (M1)** y las afirmaciones "LegacySingleItem / camino de siempre" **(M4)** con lo que el
   código realmente hace; ajustar los tests 1 y 6 en consecuencia.

---

## Revisión 2 (software-architect, 2026-07-13) — cierre del desafío

**Estado: APROBADO CON CAMBIOS → cerrado.** Acepto el desafío entero: era correcto en los 6 puntos.
El error de raíz de la Rev 1 fue disparar la conversión por "**moneda de la multa ≠ moneda de la
factura**" cuando el eje que gobierna el neteo es la **moneda de la LÍNEA del operador**. Rev 2 corrige
el disparador, parte el caso en A/B explícitos, y sube M2/M3 a obligatorios.

### Dato de prod que cierra B1 (provisto por el coordinador)
Línea del operador de **F-2026-1033** (BC 4, línea Id=4): `Currency=ARS`, `PenaltyCurrency=USD`,
`SupplierId=5`, `LineSaleAmount=480000`. → Es **CASO A** (línea en pesos, multa declarada en USD). El
diseño lo resuelve bien. No hay que verificar nada más para este BC.

### B1 (cerrado) — Disparador correcto + Caso A y Caso B explícitos

El fix ya **no** dispara por "moneda multa ≠ moneda factura". Dispara por el eje real de neteo, y
distingue dos casos **antes de convertir nada**:

Definiciones: `declaredCurrency` = moneda pedida de la multa (ISO); `invoiceCurrency` = moneda de la
factura destino; `operatorLines` = líneas del BC de ese operador.

- **Mismo-moneda** (`declaredCurrency == invoiceCurrency`): comportamiento de hoy, sin conversión, sin TC.
- **CASO A — convertible** (`declaredCurrency != invoiceCurrency` **Y todas** las `operatorLines` tienen
  `Currency == invoiceCurrency`): es F-2026-1033. Convertir `declaredCurrency → invoiceCurrency` con el TC
  provisto, guardar el monto convertido en `PenaltyAmountAtEvent` + `PenaltyCurrencyAtEvent = invoiceCurrency`.
  `PersistPenaltyCurrencyOnLinesAsync` estampa `PenaltyCurrency = invoiceCurrency` sobre líneas que **ya
  están** en esa moneda → **coherente**. `Allocate` netea el cap (todas las líneas están en la moneda
  convertida), crea el cargo `AdministrativeFee/Retenida` en `invoiceCurrency`. La ND emite un renglón en
  `invoiceCurrency`. Correcto punta a punta.
- **CASO B — no convertible con una sola conversión** (`declaredCurrency != invoiceCurrency` **Y existe**
  alguna `operatorLine` con `Currency != invoiceCurrency` — típicamente el operador internacional retuvo
  USD sobre servicio USD, cliente facturado en ARS): **NO se convierte, NO se estampa nada, NO se crea
  cargo → se rutea a revisión manual** con mensaje claro. Razón dura: una sola conversión no puede resolver
  a la vez el lado cliente (renglón ARS de la ND) **y** el lado operador (cap/retención en USD); mezclar
  las dos moneda-líneas corrompería `line.PenaltyCurrency` (`:7739-7744` estampa parejo) y dejaría el
  `RefundCap` USD sobreestimado con la retención invisible. Caso B es el terreno de la deuda futura
  (opción (a): cargo no-neteante en moneda del operador + treasury FX). Hasta entonces, **manual con
  mensaje honesto**, nunca una auto-conversión que miente el lado operador.

**Guard de detección (dónde vive):** en `CorrectPenaltyAsync`, tras leer las `operatorLines` y ANTES de
tocar `PenaltyAmountAtEvent`. Regla: `esConvertible = operatorLines.Count > 0 && operatorLines.All(l =>
Monedas.Normalizar(l.Currency) == invoiceCurrencyIso)`. Si `declaredCurrency != invoiceCurrency` y
`!esConvertible` → mensaje de manual y corte (no re-graba, no re-emite auto). La factura destino con 1
sola factura activa es la `OriginatingInvoice`; con 2+ facturas de distinta moneda ya cae a la ruta
T3b/multi-factura existente (fuera de alcance).

**R2 corregida (reemplaza la R2 de la §6):** *"El neteo del `RefundCap` y la creación del cargo ARS
ocurren SOLO en Caso A (todas las líneas del operador en la moneda de la factura). En Caso B el fix no
convierte: rutea a revisión manual. El diseño ya no afirma que 'el cruce netea el RefundCap' de forma
incondicional."*

### B2 (cerrado como decisión de alcance) — MVP = SOLO correct-penalty; confirm Día-0 queda como está

**Decisión de alcance:** el MVP de este fix cubre **únicamente el path `correct-penalty`**. El path
`ConfirmPenaltyAsync` (Día-0) **no se toca**: sigue con su guard actual (una multa USD sobre factura ARS
se traba y va a manual), y la salida es justamente `correct-penalty` (que ahora resuelve el Caso A).

**Por qué (no es pereza, es riesgo):** en confirm, cuando la ejecución llega a `:6191` (donde emite), la
imputación y las escrituras **ya ocurrieron** — `CaptureDebitNoteClassification` fijó `PenaltyAmountAtEvent`
(`:6076-6077`), `PenaltyCurrencyAtEvent` (`:6100-6101`), `PersistPenaltyCurrencyOnLinesAsync` (`:6109`),
`AllocateConfirmedPenaltyToLinesAsync` (`:6118`) y `OperatorPenaltyConfirmedDate` (`:6114`). Convertir en
`:6191` dejaría líneas + cap + monto en USD y sólo la emisión en ARS = inconsistente. Para hacerlo bien
hay que convertir **arriba de todo**, antes de `:6076`, sobre los valores del `request`
(`ConfirmedPenaltyAmount`/`PenaltyCurrency`) una sola vez — y hay **≥4 consumidores** de esos valores del
request aguas abajo (`CaptureDebitNoteClassification` :6076, set de `PenaltyCurrencyAtEvent` :6100,
`PersistPenaltyCurrencyOnLinesAsync` :6109, `AllocateConfirmedPenaltyToLinesAsync` :6118). Es un punto de
inserción de alto riesgo en el circuito de plata. **No entra en este MVP.** Fast-follow con su propio
diseño+review. El costo de diferirlo es nulo: toda anulación nueva que caiga en Caso A se destraba con
correct-penalty, exactamente como F-2026-1033.

### M1 (cerrado) — R3 reescrita: el extracto del operador en Caso A queda en la moneda de la línea (ARS), coherente

R3 de la Rev 1 estaba al revés. Realidad verificada: en Caso A el fix crea un
`BookingCancellationLineOperatorCharge Kind=AdministrativeFee` en **la moneda de la línea** (`:7959`
`Currency = Monedas.Normalizar(line.Currency)`) y setea `line.RetainedDeductionAmount` (`:7965`), que es
**justo** lo que lee el extracto del operador (ADR-044 T2 B1, `SupplierCancellationCircuitReader`). Como
en Caso A la línea **ya está en ARS** y la factura también, la retención se registra en ARS sobre una
cuenta cuya moneda de servicio es ARS → **coherente, sin corrupción de moneda**. La cuenta del operador
de F-2026-1033 es en pesos (línea ARS), así que "retuvo ARS 240.000" es la lectura correcta, no una
pérdida. El "USD 200" que declaró el operador se preserva estructurado (ver M2). **Caso B nunca llega a
crear cargo** (se cortó en el guard B1), así que no existe el escenario "retención ARS sobre cuenta USD".

**R3 corregida (reemplaza la R3 de la §6):** *"En Caso A la retención se registra en la moneda de la
línea del operador (ARS), coherente con su cuenta. El monto original declarado por el operador (USD 200) y
el TC usado se conservan en columnas estructuradas del BC (M2). Caso B no crea cargo (va a manual), así
que no hay riesgo de estampar una moneda ajena sobre la cuenta del operador."*

### M2 (cerrado, subido a OBLIGATORIO) — persistir el original + TC en columnas estructuradas, no en JSON

Confirmado el one-way door: el cargo creado en Caso A nace con `EstimatedExchangeRate*` = **null** (porque
`charge.Currency == invoiceCurrency`, no hay conversión de renglón T3b), así que el USD 200 + TC quedarían
sólo en el blob de auditoría. **Se sube a obligatorio en este fix.** Migración **aditiva nueva** (jamás
editar una aplicada):

**`Adr044_M_FixB_AddDeclaredPenaltyConversionToBookingCancellation`** — columnas nullable en
`BookingCancellation` (hogar natural, al lado de `PenaltyAmountAtEvent`/`PenaltyCurrencyAtEvent`):
```
DeclaredPenaltyOriginalAmount        decimal(18,2)?   -- monto tal como lo declaró el operador (ej. 200)
DeclaredPenaltyOriginalCurrency      varchar(3)?      -- moneda declarada ISO (ej. "USD")
PenaltyConversionExchangeRate        decimal(18,6)?   -- TC usado (ARS por 1 USD), convención Payment.ExchangeRate
PenaltyConversionExchangeRateSource  int?             -- reusa el enum ExchangeRateSource
PenaltyConversionExchangeRateAt      timestamptz?     -- fecha del TC (= día que cobró el operador)
PenaltyConversionExchangeRateJustification varchar(500)? -- obligatoria cuando Source=Manual (INV-120)
```
Todas `null` cuando no hubo conversión (mismo-moneda) → comportamiento y forma idénticos a hoy para el
99% de los BCs. Sin backfill (los BCs legacy sin conversión quedan en null). `Down()` = drop de columnas.
Sin token de concurrencia propio (el BC ya usa `xmin`). El JSON de auditoría `OperatorPenaltyCorrected`
**además** sigue registrando el antes/después (rastro humano), pero la **fuente estructurada** son estas
columnas. Con esto, la opción (a)/treasury FX de mañana puede reconstruir el origen USD sin arqueología
de blobs.

### M3 (cerrado) — fallback de fecha cuando `OperatorPenaltyConfirmedDate` es null

Verificado: `OperatorPenaltyConfirmedDate` sólo se setea en el path diferido (`:6114`, con `operatorDate`);
para un BC legacy confirmado antes de ADR-014 es plausiblemente **null**. **El TC no cuelga de un default
adivinado.** Regla: en Caso A, el modal **exige** la fecha del TC (el usuario la conoce: es el día en que
el operador cobró la multa). Si el sistema tiene `OperatorPenaltyConfirmedDate`, la **propone** como
default editable; si es null, el campo arranca vacío y es **obligatorio**. El TC se sugiere/valida para
ESA fecha. **Sin fecha y sin TC no se puede confirmar la conversión** (backend: cross-currency Caso A sin
`ExchangeRate` o sin `ExchangeRateDate` → 400 con mensaje claro; el service revalida, no confía en el
front). Nunca se inventa una fecha ni un TC.

### M4 (cerrado) — la emisión toma la rama de cargos tipificados T3b, NO LegacySingleItem

Corregido en el diseño: en Caso A, una vez que `Allocate` crea el cargo,
`BuildCancellationDebitNoteItemsAsync` ve `allCharges.Count > 0` (`:8578`) y entra a la **rama de cargos
tipificados T3b** (`:8598+`), no a `LegacySingleItem`. Con 1 factura activa resuelve `resolvedInvoice =
originatingInvoice` (T3a) y, como el cargo ya está en la moneda de la factura (ARS), no hay conversión de
renglón: produce **un único renglón ARS** con el monto correcto. El código no se edita, pero **el flujo de
ejecución cambia de rama** — y los tests deben afirmar la rama real (ver test 1 y 6 abajo, reescritos). La
frase "camino de siempre / LegacySingleItem" de la Rev 1 queda **derogada**.

### Menores (aceptados)
- `CorrectPenaltyRequest` es `record` posicional: los 4 campos nuevos van **al final**, nullable con
  default, para no romper la firma posicional.
- Frontend no verificado en la review: el **gate UX con Gaston es obligatorio** de todos modos antes de
  tocar el modal.

---

## Lista final de cambios por archivo/método (Rev 2 — lista para backend-dotnet-senior)

**Alcance MVP: SOLO `correct-penalty`. `ConfirmPenaltyAsync` NO se toca (decisión B2).**

1. **Migración (nueva, aditiva)** `Adr044_M_FixB_AddDeclaredPenaltyConversionToBookingCancellation` — las 6
   columnas nullable de M2 en `BookingCancellation`. Mapear en el `DbContext`/configuración de la entidad
   (mismo patrón que los campos de TC de `Invoice`/`Payment`). No editar migraciones aplicadas.

2. **`src/TravelApi.Domain/Entities/BookingCancellation.cs`** — agregar las 6 propiedades de M2, con
   XML-doc (código didáctico) explicando que sólo se llenan en una conversión Caso A.

3. **`src/TravelApi.Infrastructure/Services/BookingCancellationService.cs`**
   - **Helper nuevo** `TryConvertDeclaredPenaltyToInvoiceCurrency(operatorLines, declaredCurrencyIso,
     invoiceCurrencyIso, declaredAmount, rate, source, date, justification)` → devuelve un resultado
     discriminado: `SameCurrency(amount)` | `Converted(amount, rate, source, date, justification)` |
     `NotConvertible(motivoClaro)`. Adentro:
     - mismo-moneda → `SameCurrency`.
     - `esConvertible = operatorLines.Count > 0 && operatorLines.All(l => Monedas.Normalizar(l.Currency)
       == invoiceCurrencyIso)`. Si false → `NotConvertible` (Caso B → manual).
     - valida TC con **`IsUnreliableExchangeRate`** (reusar, `:8845`); convierte con **`ConvertArsUsdAmount`**
       (reusar, `:8856`); si `ConvertArsUsdAmount` devuelve null (par no ARS/USD) → `NotConvertible`.
   - **`CorrectPenaltyAsync`** (`:6425`): dentro del lock, tras `Reverse` (`:6529`) y ANTES de setear
     `PenaltyAmountAtEvent` (`:6533`), leer las `operatorLines` del BC, resolver `invoiceCurrencyIso` de
     `bc.OriginatingInvoice.MonId`, llamar al helper con los datos del request. Según el resultado:
     - `SameCurrency` → flujo actual sin cambios.
     - `Converted` → guardar el **monto convertido** en `PenaltyAmountAtEvent`, `PenaltyCurrencyAtEvent =
       invoiceCurrencyIso`, y **poblar las 6 columnas M2** con el original + TC. Seguir con
       `PersistPenaltyCurrencyOnLinesAsync(invoiceCurrencyIso)` + `Allocate` + reset huella ND + re-emit
       (todo igual que hoy).
     - `NotConvertible` → **no** re-grabar monto/moneda ni re-imputar; dejar/rutear a **revisión manual**
       con el mensaje claro y **retornar** (no re-emitir auto). Auditar el intento (motivo).
   - **Validación de entrada** (defensa server-side): si el request es cross-currency Caso A y falta
     `ExchangeRate` o `ExchangeRateDate` → `ArgumentException` → 400 con mensaje sin jerga.
   - **Auditoría** `OperatorPenaltyCorrected` (`:6559`): sumar `declaredOriginalAmount`,
     `declaredOriginalCurrency`, `conversionExchangeRate`, `conversionExchangeRateSource`,
     `conversionExchangeRateDate`, `conversionExchangeRateJustification`.
   - **NO tocar**: `EvaluateDebitNoteGating`, `BuildCancellationDebitNoteItemsAsync`, `LegacySingleItem`,
     `AllocateConfirmedPenaltyToLinesAsync`, `PersistPenaltyCurrencyOnLinesAsync`, la invariante B2, ni
     `ConfirmPenaltyAsync`.

4. **`src/TravelApi/Controllers/CancellationsController.cs`** — extender `CorrectPenaltyRequest` (`:1200`)
   con 4 campos **al final**, nullable: `decimal? ExchangeRate`, `int? ExchangeRateSource`,
   `DateTime? ExchangeRateDate`, `string? ExchangeRateJustification` (MaxLength 500). Pasar al service.
   Sumar 400 al mapeo de errores para "falta el tipo de cambio / la fecha para convertir".

5. **DTO de situación** `OperatorPenaltySituationDto` — exponer, si no está ya, `InvoiceCurrency` (moneda
   de la factura destino, ISO) y `SuggestedExchangeRateDate` (= `OperatorPenaltyConfirmedDate`, nullable)
   para que el modal sepa cuándo mostrar el campo de TC y qué fecha proponer. Campos aditivos nullable.
   **Verificar primero** si el modal ya tiene la moneda de la factura por otra vía; si sí, este cambio es cero.

6. **Frontend** (detrás del **gate UX obligatorio con Gaston**): `ConfirmarMultaOperadorInline.jsx` +
   `api/cancellationsApi.js` — cuando la moneda elegida ≠ moneda de la factura, mostrar campo de TC (fecha
   obligatoria, default `SuggestedExchangeRateDate` si viene) + justificación, previsualizar el monto
   convertido y enviarlo. Mismo-moneda → sin cambios. Copy en criollo (gate data-exposure).

## Tests obligatorios (Rev 2)

Unit (`BookingCancellationService`, InMemory) — **afirman la rama real de emisión**:
1. **Caso A** (F-2026-1033: línea ARS, multa declarada USD, factura ARS) + TC válido → `PenaltyCurrencyAtEvent
   =ARS`, `PenaltyAmountAtEvent`=convertido, columnas M2 pobladas (USD original + TC), `Allocate` crea **un
   cargo tipificado ARS**, y `BuildCancellationDebitNoteItemsAsync` emite por la **rama T3b de cargos**
   (allCharges>0, resuelve `originatingInvoice`) un **único renglón ARS** correcto — **NO** LegacySingleItem.
2. **Caso B** (línea del operador en USD, factura ARS) + intento de corregir cross-currency → **revisión
   manual** con mensaje claro; **no** re-graba `PenaltyAmountAtEvent`/`Currency`, **no** estampa
   `line.PenaltyCurrency`, **no** crea cargo, **no** toca `RefundCap`. Idempotente (se puede reintentar).
3. Caso A con TC ≤ 0 o == 1 (banda de sanidad, `IsUnreliableExchangeRate`) → rechazo/mensaje claro, no persiste.
4. Caso A sin TC o sin fecha → 400 con mensaje claro, no persiste.
5. **Mismo-moneda** (ARS/ARS) → comportamiento **byte-idéntico** a hoy (no pide TC, columnas M2 en null).
6. **Invariante de plata (Caso A)**: tras `Allocate`, `RefundCap + RetainedDeductionAmount == capBeforePenalty`,
   con el cargo y el `RetainedDeductionAmount` en **ARS** (moneda de la línea).
7. Auditoría `OperatorPenaltyCorrected` registra original USD + moneda + TC + fuente + fecha + justificación;
   y las **columnas M2** quedan pobladas (fuente estructurada, no sólo JSON).
8. **Anti-doble-cobro INV-ADR013-001** preservado: la conversión no crea deducción `CancellationPenalty`.
9. Regresión del guard (A): USD-vs-ARS **sigue** trabando cuando NO hubo conversión; pasa cuando ya se convirtió a ARS.

Integración (real DB, la corre QA/reviewer): correct-penalty Caso A end-to-end → ND emitida por la rama de
cargos tipificados, renglón en escala ARS correcta, comprobante `MonId=PES`; y correct-penalty Caso B →
queda en revisión manual sin comprobante.

Frontend (mjs): el modal muestra el campo de TC (con fecha obligatoria) **sólo** cuando moneda ≠ factura;
previsualiza el convertido; lo oculta para mismo-moneda.

Gate **data-exposure** obligatorio: mensajes de Caso B y de validación sin IDs/enums/jerga; columnas y JSON internos.

## Qué NO se toca (Rev 2, consolidado)
- El guard `EvaluateDebitNoteGating` bloque (A) — última línea de defensa, queda dura.
- La invariante **B2** (charge.Currency == Line.Currency) y el neteo del `RefundCap`.
- `BuildCancellationDebitNoteItemsAsync` / conversión T3b / `LegacySingleItem` / `Allocate` /
  `PersistPenaltyCurrencyOnLinesAsync`.
- `ConfirmPenaltyAsync` (path Día-0) — fuera de alcance MVP (decisión B2); fast-follow aparte.
- Migraciones ya aplicadas (la de M2 es **nueva y aditiva**).
