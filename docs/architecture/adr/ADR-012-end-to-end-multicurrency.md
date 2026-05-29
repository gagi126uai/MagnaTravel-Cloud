# ADR-012 — Multimoneda de punta a punta (facturar / NC / ND en USD o ARS + tarifario en USD)

- **Status**: Propuesto (Draft) — pendiente de review del `software-architect-reviewer`, de definicion fiscal del contador (`travel-agency-accountant-argentina`) y de homologacion ARCA. **NO Accepted**. La activacion (prender el flag) depende de signoff fiscal explicito + homologacion en el ambiente de pruebas de ARCA (ver §9 y §11).
- **Date**: 2026-05-29.
- **Author(s)**: software-architect agent.
- **Related**:
  - [ADR-011 Fuente confiable de tipo de cambio](ADR-011-authoritative-exchange-rate-source.md) — **capa POSTERIOR, NO prerequisito del MVP** (corregido en review 2026-05-29). ADR-011 automatiza y valida el TC contra fuente + politica de staleness + historico. El MVP de este ADR NO lo necesita: el operador carga el TC a mano con fuente + fecha + justificacion (patron `Manual`/INV-120 que ya existe). ADR-011 se enchufa despues para automatizar ese numero. **No esta implementado** (busque `IExchangeRateResolver`/`ExchangeRateQuotes` en `src/` -> 0 resultados).
  - [ADR-009 NC parcial fiscal](ADR-009-partial-credit-note.md) — la NC **parcial** ya emite en moneda extranjera y congela el TC del origen + guard de incoherencia (`InvoiceService.cs:1884`). La NC/ND **TOTAL**, en cambio, HOY esta **bloqueada** si la factura no esta en pesos (guard de moneda en `EnqueueAnnulmentAsync` y en `ProcessAnnulmentJob`, ambos en `InvoiceService.cs`): aborta con error. Generalizar a NC/ND total NO es cosmetico — es REEMPLAZAR ese guard de rechazo por un camino de emision nuevo (ver §3.3).
  - [ADR-002 Cancelacion / Refund](ADR-002-cancellation-refund.md) — de aca sale `FiscalSnapshot`, el enum `ExchangeRateSource` y el patron de congelar TC + fuente + fecha.

---

## 1. Contexto

### 1.1 El ejemplo pelotudo (la pizzeria que cobra en dolares)

Imaginate una pizzeria que hasta hoy SIEMPRE cobro en pesos. Manana el dueno quiere poder hacer
algunas facturas en dolares (porque un cliente paga en dolares). Para eso necesita tres cosas:

1. **Una casilla en la pantalla de facturar** que diga "esta la cobro en pesos o en dolares".
2. Que cuando elija dolares, el sistema **ponga solo cuanto vale el dolar hoy** (no que lo escriba a
   mano y se mande cualquiera), y que deje anotado de donde saco ese numero.
3. Que si despues tiene que hacer una **nota de credito** (devolverle plata a ese cliente), la nota
   salga **automaticamente en dolares con el mismo valor del dolar que tenia la factura original** —
   que el cajero NO tenga que volver a elegir la moneda ni el numero, porque si elige distinto se
   arma un quilombo fiscal.

Y todo esto tiene que andar igual si la pizzeria es **monotributista** (hace factura C) o si manana
se hace **responsable inscripto** (hace factura A/B). La moneda no tiene nada que ver con el tipo de
factura: son dos perillas separadas.

### 1.2 Lo que YA esta hecho (verificado en el repo — NO hay que reinventarlo)

Sorpresa importante para el lector: **una buena parte del riel multimoneda ya existe** porque la NC
parcial (FC1.3 / ADR-009) lo necesito. No partimos de cero.

- **El campo de moneda ya viaja de punta a punta en el backend.** `CreateInvoiceRequest.MonId`/
  `MonCotiz` (default `"PES"`/`1m`) -> `AfipService.CreatePendingInvoice` los copia a
  `Invoice.MonId`/`MonCotiz` (`AfipService.cs:737-738`) -> `ProcessInvoiceJob` los relee y arma el
  fragmento SOAP `<MonId>/<MonCotiz>` con `BuildMonedaSoapFragment` (`AfipService.cs:962-977`, blindado
  por `AfipServiceMonedaSoapFormatTests`). **VERIFICADO.** O sea: el backend ya sabe mandar una factura
  en DOL al ARCA. Lo unico que falta del lado backend para la factura directa es **poblar** esos
  campos con criterio (hoy los pobla solo la NC parcial).

- **El catalogo de monedas ya esta centralizado.** `ArcaCurrencyMapper` (`Domain/Helpers/ArcaCurrencyMapper.cs`)
  traduce ISO ("USD"/"ARS") al codigo ARCA ("DOL"/"PES"), valida el codigo en el boundary del job
  (`AfipService.cs:1001`), y arranca chico (solo ARS+USD) a proposito. **VERIFICADO.** Sumar EUR/BRL es
  una linea + un test + homologar. No hay mapeo duplicado.

- **La validacion defensiva del boundary ya existe.** Si alguien manda "USD" en vez de "DOL",
  `ProcessInvoiceJob` aborta con mensaje claro ANTES de POSTear a ARCA (`AfipService.cs:1001-1010`).
  **VERIFICADO.**

- **El tarifario YA tiene moneda.** `Rate.Currency` (`Domain/Entities/Rate.cs:148-149`) es un
  `MaxLength(3)` con **default `"USD"`**. **VERIFICADO.** O sea, el requisito 4 ("tarifario en USD")
  esta *parcialmente modelado* — la columna existe. **A VERIFICAR (ver §3.4)**: si ese `Currency` HOY
  fluye hacia la reserva/factura o es decorativo. No encontre que `InvoiceService` lea `Rate.Currency`
  (busqueda sin match relevante), lo que sugiere que **hoy es informativo y no se propaga**. Hay que
  confirmarlo en el flujo reserva -> facturacion antes de implementar.

- **El comportamiento A/B/C ya es ortogonal a la moneda (casi).** `CreatePendingInvoice` decide el
  tipo segun `AfipSettings.TaxCondition` + condicion del cliente (`AfipService.cs:597-607`): RI ->
  A o B; Mono/Exento -> C. Esa decision **no mira la moneda para nada**. **VERIFICADO.** Un Mono puede
  emitir C en DOL y un RI puede emitir A/B en DOL sin tocar esa logica. El IVA: factura C -> `ImpIVA=0`;
  A/B -> IVA discriminado (`AfipService.cs:1043, 1121-1122`). Eso tampoco depende de la moneda (el IVA
  se calcula sobre el neto, sea en pesos o dolares).

- **El campo RG 5616 del receptor YA se emite.** `<CondicionIVAReceptorId>` ya va en el envelope
  (`AfipService.cs:1166`, via `GetConditionIvaId`). **VERIFICADO.** Esto es relevante porque la
  RG 5616/2024 hizo ese campo obligatorio (ver §1.4).

- **El enum de condicion fiscal canonica ya existe.** `TaxConditionCanonical` +
  `TaxConditionNormalizer` (`Domain/Helpers/TaxConditionNormalizer.cs`) normalizan los strings legacy
  (Mono/RI/Exento/CF/Extranjero). **VERIFICADO.** Pero hoy se usan **solo** en el modulo de
  cancelacion (FiscalSnapshot), NO en `AfipSettings.TaxCondition` (que sigue siendo texto libre, ver
  §1.3 punto 2).

- **El patron de flag OFF por default ya esta probado.** `OperationalFinanceSettings.EnablePartialCreditNoteRealEmission`
  (default `false`, `OperationalFinanceSettings.cs:276`) es el molde: feature nueva detras de flag,
  validacion en startup, no se prende en prod sin signoff contador. **VERIFICADO.** Este ADR copia ese
  patron con un flag nuevo.

### 1.3 Lo que NO esta hecho (los agujeros reales que ataca este ADR)

1. **El frontend NO deja elegir la moneda.** `CreateInvoiceModal.jsx` arma el payload SIN `monId`/
   `monCotiz` (`CreateInvoiceModal.jsx:163-181`) -> el backend usa los defaults -> **siempre sale PES/1**.
   Ademas toda la UI esta hardcodeada a `currency: "ARS"` (`toLocaleString`, lineas 254-477). **VERIFICADO.**
   Este es el agujero #1: aunque el backend sepa facturar en DOL, no hay forma de pedirlo desde la pantalla.

2. **`AfipSettings.TaxCondition` se persiste como texto libre, pero la pantalla NO permite typear.**
   En la base es un `MaxLength(50)` string (`AfipSettings.cs:48-49`) y la logica A/B/C lo compara por
   **string exacto** `== "Responsable Inscripto"` / `== "Monotributo"` (`AfipService.cs:597, 363`).
   **CORRECCION DEL REVIEW (2026-05-29):** la UI **NO es texto libre**. `AfipSettingsTab.jsx:174-183`
   es un `<select>` con **exactamente las 3 opciones validas** que matchean string-por-string con el
   backend (`"Responsable Inscripto"` / `"Monotributo"` / `"Exento"`). **VERIFICADO.** O sea: **desde la
   pantalla NO hay riesgo de typo** — el operador solo puede elegir uno de los 3 valores correctos. El
   unico vector residual es **edicion directa por SQL** (alguien hace `UPDATE` a mano en la base) o el
   **soporte futuro de RI** si se suman variantes. Por eso el endurecimiento (aplicar
   `TaxConditionNormalizer` + validacion en startup) es **defensa en profundidad de BAJO valor**, no un
   riesgo presente desde la app. Queda como hardening **opcional, fuera del camino critico** (§3.5).

3. **La factura normal NO congela el TC con trazabilidad.** El `FiscalSnapshot` (con TC + fuente + fecha)
   es SOLO del modulo de cancelacion (`FiscalSnapshot.cs`). La **factura directa** persiste `MonId`/
   `MonCotiz` pero **no guarda de donde salio ese TC ni cuando** — no hay `Source`/`FetchedAt` en
   `Invoice`. **VERIFICADO.** Esto es lo que ADR-011 §6.1 llama "la asimetria": la cancelacion te obliga
   a declarar el origen del TC, la factura directa no. Este ADR cierra esa asimetria reusando ADR-011.

4. **El enum `ExchangeRateSource` NO tiene el valor que pide la RG 5616.** El enum
   (`ExchangeRateSource.cs:13-38`) tiene `Unset` / `BCRA_A3500` / `BNA_Mayorista` / `BNA_Minorista` /
   `AfipOficial` / `Manual`. **VERIFICADO.** La RG 5616 exige el **dolar tipo VENDEDOR DIVISA del Banco
   Nacion** (§1.4), que **no es** mayorista ni minorista — es un TC distinto que **no esta en el enum**.
   O sea: el ADR NO puede "reusar `ExchangeRateSource` transparentemente"; hace falta **agregar un valor
   nuevo** (propuesta: `BNA_VendedorDivisa`). El nombre exacto de la etiqueta y su uso fiscal los
   **confirma el contador** (§11). Hasta entonces el MVP usa `Manual` (el operador carga el TC a mano y
   declara que viene de "BNA vendedor divisa"); el valor de enum dedicado se suma cuando el contador lo
   valide.

5. **El campo RG 5616 `CanMisMonExt` NO existe en el envelope.** La RG 5616/2024 sumo un campo nuevo
   (`CanMisMonExt`) que indica si el comprobante en moneda extranjera **se cobra en esa misma moneda**.
   Busque `CanMisMonExt` en todo `src/` -> **0 resultados**. **VERIFICADO (ausencia).** Solo esta
   `<CondicionIVAReceptorId>`. Emitir un comprobante en DOL al ARCA hoy, en produccion, podria rebotar
   o comportarse distinto sin este campo (ver §1.4 — esto lo confirma el contador + homologacion).

### 1.4 Tipo de cambio y RG 5616/2024 (normativa — VERIFICADO con fuentes, vigencia a confirmar por contador)

- **RG 5616/2024 (ARCA)**: para comprobantes emitidos en moneda extranjera y **cancelados en la misma
  moneda**, el TC a consignar es el **dolar tipo VENDEDOR DIVISA del Banco Nacion del DIA HABIL ANTERIOR**
  a la emision. Alcanza comprobantes clase A, B, C, E y T. Fuentes en §13.
- **Dos campos nuevos obligatorios por web service (WSFEv1)**: (a) la **moneda de cancelacion**
  (`CanMisMonExt` = si se paga en la misma moneda extranjera) y (b) la **condicion frente al IVA del
  receptor** (`CondicionIVAReceptorId`, este ultimo YA implementado — §1.2). Cronograma: opcional desde
  abril 2025, **obligatorio / rechazo si falta desde ~julio 2025** (fechas exactas a confirmar por el
  contador — hubo prorrogas). Fuentes en §13.
- **Lo que HOY hace el sistema con el TC**: el unico servicio que trae el dolar es `BnaExchangeRateService`,
  que scrapea el **billete minorista** del BNA y **solo alimenta el dashboard** (no el circuito fiscal).
  ADR-011 §1.2 lo documenta. "Billete minorista" NO es lo mismo que "vendedor divisa" que pide la norma.
- **Criterio interno (NO es norma citable)**: "la NC/ND usa el MISMO TC que la factura origen". Esto es
  una **decision de diseno interna** coherente con que la NC ajusta un comprobante ya emitido — **no es
  una regla AFIP que pueda citar**. Lo registro como tal para no inventar normativa. El contador debe
  confirmar que es el tratamiento correcto.

---

## 2. Que entra y que NO entra

### 2.1 Entra (a lo largo de las fases, todo detras del flag `EnableMultiCurrencyInvoicing`)

1. **MVP — Seleccion de moneda en la factura directa con TC cargado a mano** (frontend `CreateInvoiceModal`
   + poblado backend de `MonId`/`MonCotiz`). En el MVP el operador **carga el TC a mano** con fuente
   (`BNA_VendedorDivisa`, valor de enum nuevo a confirmar) + fecha + justificacion obligatoria + audit,
   reusando el patron `Manual`/INV-120 que **ya existe**. **NO depende de ADR-011.**
2. **Congelar el TC con trazabilidad en la factura directa** (Source + FetchedAt + justificacion), cerrando
   la asimetria de ADR-011 §6.1. Modelo: columnas nuevas en `Invoice` (ver §3.1), con `MonCotiz` en
   `numeric(18,6)` para no divergir del SOAP (§3.1).
3. **Herencia automatica de moneda + TC en NC/ND TOTAL** desde el comprobante origen. **Importante:** hoy la
   NC/ND total **rechaza** facturas en moneda extranjera (§1.3, §3.3). Esto **reemplaza ese guard de
   rechazo** por un camino de emision nuevo, replicando el guard de incoherencia de la NC parcial. **Capa
   posterior**, no MVP.
4. **Automatizar el TC (ADR-011)** — validar el numero cargado contra una fuente confiable + staleness +
   historico. **Capa POSTERIOR al MVP**, no prerequisito.
5. **Tarifario en USD que se propaga a la factura** (definir si `Rate.Currency` fluye y como se convierte
   o se respeta al facturar — §3.4). Capa posterior, **verificar el flujo Rate->Invoice antes**.
6. **Hardening de `AfipSettings.TaxCondition`** (normalizer + validacion startup) — **opcional, fuera del
   camino critico** (la UI ya es un `<select>` cerrado, §1.3 punto 2). Defensa en profundidad para edicion
   directa por SQL + soporte futuro RI (§3.5).
7. **Campo `CanMisMonExt` en el envelope SOAP** (RG 5616), condicional a homologacion ARCA (§3.6).
8. El **flag `EnableMultiCurrencyInvoicing`** (OFF por default). Con OFF, comportamiento **byte-identico**
   al actual (todo PES/1; el frontend sigue sin selector).
9. **Invariante "UNA factura = UNA moneda"** (§3.7): una factura lleva una sola moneda. Si una reserva
   tiene items de `Rate` en monedas distintas, el operador resuelve la moneda ANTES de facturar — no se
   mezclan monedas en un mismo comprobante.

### 2.2 NO entra (scope cut explicito — honestidad sobre sobre-ingenieria)

- **El MVP NO construye ni depende de la fuente automatica del TC.** Eso es ADR-011, que pasa a ser una
  **capa POSTERIOR** (corregido en review 2026-05-29). El MVP factura en USD con el operador cargando el
  TC **a mano** (fuente + fecha + justificacion + audit, patron `Manual`/INV-120). ADR-011 se enchufa
  despues para automatizar/validar ese numero — **no es prerequisito del MVP**. (Verificado: ADR-011 NO
  esta implementado.)
- **NO trata la diferencia de cambio contable** (T0 vs T2/T3). ADR-011 §1.3 punto 5 ya explica que eso
  requiere un modulo de asientos contables **que hoy no existe** (cero clases `Journal`/`Ledger`/
  `AccountingEntry`). Fuera de scope total.
- **NO re-cotiza la NC/ND.** Hereda el TC congelado del origen. Sin excepciones.
- **NO multi-tenant.** Una instalacion por cliente (requisito del dueno). No se disena aislamiento por
  tenant ni settings por agencia multiple. `AfipSettings`/`OperationalFinanceSettings` son singleton.
- **NO afirma endpoints externos.** Igual que ADR-011, la fuente oficial del TC queda enchufable.
- **NO backfill de facturas viejas.** Las facturas existentes quedan en PES/1 (que es correcto: eran en
  pesos). Migracion 100% aditiva.

---

## 3. Decision

### 3.1 Modelo de datos — donde vive la moneda y como se congela el TC

**Principio**: la moneda y el TC del comprobante son **inmutables una vez emitido** (igual que el resto
del comprobante con CAE). Se congelan en el momento de crear la factura.

**Punto de congelamiento del TC (decision de diseno, M3, a confirmar por contador):** hoy el TC se
congela al **crear la factura "pending"** (`CreatePendingInvoice` copia `MonId`/`MonCotiz` al `Invoice`),
**no** al emitir el job async. O sea: el numero queda fijado en el momento que el operador arma el
comprobante, no cuando ARCA devuelve el CAE. Lo declaramos como decision explicita. **Sujeto a confirmacion
del contador**: si fiscalmente el TC debe ser el de la **fecha de operacion** vs el de la **fecha de
emision** (§11 Q4), puede haber que mover ese punto.

**Precision de `MonCotiz` (M2, punto de diseno a verificar en la migracion):** el fragmento SOAP emite el
TC con **6 decimales** (`BuildMonedaSoapFragment` formatea `"0.######"`, verificado). La columna
`Invoice.MonCotiz` debe declararse como **`numeric(18,6)`** para que el valor persistido **no diverja** del
que se manda a ARCA (si la columna tuviera menos decimales, redondearia y el comprobante no cuadraria con
lo guardado). Verificar el tipo actual de la columna en la migracion y ajustarlo si hace falta.

**Factura (`Invoice`)** — ya tiene `MonId`/`MonCotiz`. Se le suman columnas de **trazabilidad del TC**
(nullable, aditivas), espejando lo que `FiscalSnapshot` ya hace para cancelaciones:

| Columna nueva (propuesta) | Tipo | Para que |
|---|---|---|
| `ExchangeRateSource` | enum `ExchangeRateSource` de ADR-002 **+ valor nuevo `BNA_VendedorDivisa`** (B2 — el enum hoy NO tiene "vendedor divisa"; nombre/uso a confirmar por contador, §1.3 punto 4) | de donde salio el TC (BNA vendedor divisa / Manual / ...) |
| `ExchangeRateFetchedAt` | `DateTime?` | cuando se obtuvo / con que fecha lo cargo el operador |
| `ExchangeRateJustification` | `string?` | en el MVP, justificacion **obligatoria** cuando se factura en USD con TC `Manual` (patron INV-120) |
| `ExchangeRateQuoteId` | `int?` FK a `ExchangeRateQuotes` (de ADR-011) — **capa POSTERIOR** | trazabilidad fuerte cuando ADR-011 exista: "uso exactamente esta cotizacion". En el MVP queda `null`. |

En el **MVP** se pueblan `ExchangeRateSource` (= `Manual` o `BNA_VendedorDivisa`), `ExchangeRateFetchedAt`
y `ExchangeRateJustification` (obligatoria en USD). `ExchangeRateQuoteId` queda `null` hasta que ADR-011
exista. Todas **nullable**: las facturas en PES no las necesitan (TC=1 trivial); las facturas viejas no las
tienen; con el flag OFF no se setean. **Opcion alternativa considerada y descartada**: meter un VO
`FiscalSnapshot` tambien en la factura directa. **Descartada** porque `FiscalSnapshot` esta acoplado al
ciclo de cancelacion (T0/T2/T3, owned por `BookingCancellation`); reusarlo en la factura mezclaria dos
agregados. Tres columnas planas en `Invoice` son mas simples y suficientes (la agencia es chica).

**NC / ND**: NO se les agrega nada. Heredan `MonId`/`MonCotiz` del `OriginalInvoice` (relacion que ya
existe, `Invoice.OriginalInvoiceId` -> `OriginalInvoice`). Ver §3.3.

**Tarifario (`Rate`)**: ya tiene `Currency`. NO se le agrega nada al modelo; el trabajo es de
**propagacion** (§3.4), no de schema.

**Migraciones**: 100% aditivas (3 columnas nullable en `Invoice` + el campo `CanMisMonExt` si §3.6 lo
requiere + settings nuevos). Postgres (comillas dobles, `xmin` como concurrency token ya existente).
Sin backfill.

### 3.2 Seleccion de moneda en la factura directa (backend + frontend)

**MVP (TC a mano, SIN ADR-011):**

**Frontend (`CreateInvoiceModal.jsx`)**:
- Agregar un **selector ARS / USD** (radio o dropdown chico) arriba de los items. Default **ARS**
  (comportamiento actual). El `toLocaleString(..., currency)` hoy hardcodeado a "ARS" pasa a usar la
  moneda elegida (cosmetico, para que los montos se muestren con el simbolo correcto).
- Cuando se elige **USD**: el operador **carga el TC a mano** + la **fecha** del TC + una **justificacion
  obligatoria** (ej. "Dolar BNA vendedor divisa del dia habil anterior"). Esto reusa el patron
  `ExchangeRateSource.Manual` + el invariante INV-120 (justificacion obligatoria) de ADR-002. **No hay
  numero auto-propuesto en el MVP** — eso lo agrega ADR-011 despues.
- El payload pasa a incluir `monId` (codigo ISO "USD"/"ARS", el backend mapea a ARCA) + el TC + la fuente
  + la fecha + la justificacion.

**Backend (`InvoiceService.CreateAsync` / `CreatePendingInvoice`)**:
- Poblar `MonId`/`MonCotiz` desde el request (hoy ya se copian, solo que el front no los manda).
- **Validar coherencia minima del TC** (no fuente, solo sanidad): para USD, exigir `MonCotiz > 1`
  (un dolar no vale 1 peso) y **justificacion no vacia**. Si falta justificacion en USD -> rechazar.
- Setear las columnas de trazabilidad (§3.1): `ExchangeRateSource` = `BNA_VendedorDivisa` o `Manual`,
  `ExchangeRateFetchedAt`, `ExchangeRateJustification`, audit.
- **ARS sigue el camino trivial**: `MonId="PES"`, `MonCotiz=1`, sin justificacion. Byte-identico a hoy.

**Capa POSTERIOR (ADR-011, cuando exista):** en vez de cargar el TC a mano, el modal lo pide al backend, el
operador **ve** el TC propuesto y lo confirma; el backend **valida** el numero contra la fuente (tolerancia)
y solo cae a `Manual`+justificacion si el resolver esta stale/ausente. **Esto NO es necesario para el MVP**
y no debe atar la primera entrega (el dueno pidio "facturar en USD", no "validar el TC automaticamente").

### 3.3 Herencia de moneda + TC en NC / ND (el requisito "inteligente")

**Regla**: al crear una NC o ND, la moneda (`MonId`) y la cotizacion (`MonCotiz`) se toman
**automaticamente del `OriginalInvoice`**. El operador **NO las elige**. La UI las muestra como
solo-lectura ("Esta nota sale en USD, dolar congelado de la factura: 1.050,00 — no editable").

**ESTADO REAL HOY (corregido en review 2026-05-29) — la NC/ND TOTAL en USD esta BLOQUEADA, no "casi
lista":** el codigo actual **ABORTA** si la factura no esta en pesos. Hay dos guards explicitos:
- Guard de moneda en `EnqueueAnnulmentAsync` (`InvoiceService.cs`) — fail-fast **sincrono** antes de
  encolar: si `invoice.MonId != "PES"`, lanza `InvalidOperationException` ("la anulacion total automatica
  solo emite NC en pesos...").
- Guard de moneda en `ProcessAnnulmentJob` (`InvoiceService.cs`) — en el **job**: si `original.MonId != "PES"`,
  marca `AnnulmentStatus.Failed`, loguea warning y notifica, **sin emitir**.

O sea: la Fase de NC/ND total **NO es generalizar cosmeticamente lo que la parcial ya hace**. Es
**REEMPLAZAR ese guard de rechazo por un camino de emision nuevo** que sepa emitir la NC/ND total en la
moneda del origen. La NC **parcial** (ADR-009) si sabe hacerlo y aporta el patron a copiar — incluido su
**guard de incoherencia** (`InvoiceService.cs:1884`: para moneda extranjera, `MonCotiz <= 0 || == 1` es
fallo terminal controlado, nunca llega a ARCA).

**Por que**: una NC/ND ajusta un comprobante ya emitido. Si la factura salio en USD a TC 1050 y la NC
saliera en pesos (o en USD a otro TC), el ajuste no cerraria contra el original.

**Orden de implementacion NO NEGOCIABLE (para no abrir una ventana de riesgo):**
1. **Primero**: agregar el **guard de incoherencia** al path de NC/ND total (replicar el de la NC parcial,
   `:1884`): para moneda extranjera, `MonCotiz <= 0 || == 1` -> fallo controlado, nunca emitir.
2. **Despues**: levantar el **fail-fast de rechazo** (guard de moneda en `EnqueueAnnulmentAsync` y en `ProcessAnnulmentJob`) y conectar el camino de
   emision que hereda `MonId`/`MonCotiz` del `originalInvoice`. Si se levantara el fail-fast ANTES de tener
   el guard de incoherencia, una factura USD legacy con TC=1 podria emitir una NC con TC=1 espurio.
3. **Test de no-regresion obligatorio**: con el flag **OFF**, una factura USD **sigue rechazando** la NC
   total exactamente como hoy (mismo error, mismo `Failed`). El cambio NO debe alterar el comportamiento
   flag-OFF.

**Implementacion**: en el path de NC/ND total, **ignorar** cualquier `MonId`/`MonCotiz` que venga del
request y **sobrescribir** con los del `originalInvoice` (que ya se carga, `AfipService.cs:591`), y
aplicar el guard de incoherencia antes de emitir.

**Caso borde (riesgo fiscal, lo marca ADR-011 §1.3 punto 4)**: factura USD **legacy** sin TC congelado
confiable (TC=1 espurio). Regla dura: **NO emitir la NC con TC=1**; mandar a **revision manual**. El
operador debe declarar el TC correcto de la factura original con justificacion. Esto aplica a facturas
USD emitidas ANTES de este ADR (si las hubiera) o a cualquier `OriginalInvoice` con `MonId="DOL"` y
`MonCotiz<=1`.

### 3.4 Tarifario en USD — como llega a la factura

**Estado**: `Rate.Currency` existe (default "USD"). **A VERIFICAR antes de implementar**: si ese
`Currency` se propaga hoy a la reserva/factura. Mi lectura (busqueda sin match en `InvoiceService`) es que
**hoy es decorativo** — el precio se carga en la reserva como numero y la factura lo toma sin moneda.

**Decision propuesta (la mas simple que cumple)**:
- El tarifario **sugiere** la moneda y el precio, pero **la factura no se ata ciegamente** al tarifario.
  El operador elige la moneda al facturar (§3.2). Si el item viene de un `Rate` en USD, la UI **propone**
  USD por default (mejora de UX), pero el operador puede cambiarla.
- **NO se hace conversion automatica de moneda en la factura.** Si el tarifario esta en USD y el operador
  factura en ARS, **el operador define el monto en ARS** (no inventamos un TC para convertir el precio del
  tarifario — eso seria un TC comercial, distinto del TC fiscal, y abre una lata de gusanos). La conversion
  comercial tarifario->precio de venta es un problema de **pricing**, separado del TC **fiscal** del
  comprobante. Mezclarlos es el error clasico.
- **Por que asi**: la agencia es chica y el dueno pidio "tarifario con precios en USD ademas de pesos" —
  eso es **poder cargar y mostrar** precios en USD, no necesariamente convertir automaticamente. Empezar
  simple (mostrar/proponer) y, si el negocio lo pide, sumar conversion despues. Marcar como **decision a
  confirmar con Gaston** (§12 Q3): "tarifario en USD" = ¿solo mostrar/proponer, o tambien convertir?

### 3.5 Compatibilidad Mono / RI — moneda ortogonal al tipo de comprobante

**Confirmado por codigo (§1.2)**: la decision A/B/C no mira la moneda. Un Mono factura **C en USD**; un RI
factura **A/B en USD**. El IVA se discrimina (A/B) o no (C) igual, sea en pesos o dolares. **No hay que
tocar esa logica** para que la moneda funcione — solo verificar con tests que A/B/C + USD cierra el
envelope (neto + IVA + tributos en la moneda elegida).

**Sub-tema (OPCIONAL, fuera del camino critico): hardening de `AfipSettings.TaxCondition`.**

**CORRECCION DEL REVIEW (2026-05-29):** el riesgo de typo **NO esta presente desde la app**. La UI
(`AfipSettingsTab.jsx:174-183`) **ya es un `<select>`** con exactamente los 3 valores validos que matchean
con el backend (`"Responsable Inscripto"` / `"Monotributo"` / `"Exento"`). **VERIFICADO.** El operador no
puede typear: solo elige uno de los 3. Por lo tanto este sub-tema **NO es de "mayor valor inmediato"** como
decia el borrador — es **defensa en profundidad de bajo valor** contra dos vectores residuales:
- **edicion directa por SQL** (alguien hace `UPDATE "AfipSettings"` a mano), y
- **soporte futuro de RI** si manana se agregan variantes/etiquetas nuevas.

Si se hace igual (opcional):
- Reemplazar las comparaciones por string exacto por el `TaxConditionNormalizer` ya existente (tolera
  capitalizacion/tildes/variantes) — util sobre todo si alguna vez el valor entra por fuera del `<select>`.
- Validar en startup que `AfipSettings.TaxCondition` normaliza a un valor != `Unknown`.

**Importante**: este sub-tema es **independiente de la moneda** y **NO bloquea nada**. Queda **fuera del
camino critico** de la multimoneda. Se puede hacer (o no) como mini-trabajo aparte, sin prioridad.

### 3.6 Campo `CanMisMonExt` (RG 5616) y homologacion

- **Agregar `<CanMisMonExt>` al envelope SOAP** para comprobantes en moneda extranjera (hoy ausente,
  §1.3 punto 4). El valor (¿"S" si se cobra en la misma moneda?) y si es obligatorio para nuestro caso
  los **define el contador + se confirma en homologacion** (no lo invento aca).
- **Homologacion obligatoria**: emitir comprobantes USD **nuevos** requiere probar primero en el
  **ambiente de homologacion de ARCA** y obtener un CAE aprobado. `AfipSettings.IsProduction`
  (`AfipSettings.cs:15`) ya separa homologacion/produccion con certificados distintos — el diseno **ya
  permite** homologar sin tocar produccion. La regla operativa: **no prender el flag en prod hasta tener
  un CAE de homologacion aprobado para A/B/C en DOL con `CanMisMonExt` + `CondicionIVAReceptorId`**.

### 3.7 Invariante "UNA factura = UNA moneda" (M4)

**Regla dura:** una factura lleva **una sola moneda**. No se mezclan items en USD con items en ARS dentro
del mismo comprobante. El campo de moneda vive a nivel **comprobante** (`Invoice.MonId`), no a nivel item.

**Por que:** ARCA emite el `<MonId>/<MonCotiz>` a nivel comprobante; el neto, el IVA y los tributos se
liquidan todos en esa unica moneda. Un comprobante con dos monedas no tiene representacion valida en el
envelope y rompe el cuadre fiscal.

**Que pasa si la reserva tiene items de `Rate` en monedas distintas:** el operador **resuelve la moneda
ANTES de facturar** (elige en que moneda factura, y los precios se expresan en esa moneda — sin conversion
fiscal automatica, ver §3.4). El sistema **no** mezcla monedas en una factura ni convierte solo. Si el
negocio necesitara facturar parte en USD y parte en ARS, son **dos facturas separadas** (una por moneda),
no una factura mixta. Esto se valida en el boundary de creacion de la factura.

---

## 4. Fases (orden recomendado por el review 2026-05-29: MVP-first, SIN depender de ADR-011)

Todo detras de `EnableMultiCurrencyInvoicing` (OFF por default = comportamiento actual byte-identico).

**Principio del nuevo orden:** la primera entrega real (MVP) debe darle al dueno lo que pidio —
**facturar en USD** — con el operador cargando el TC a mano. ADR-011 (automatizar/validar el TC) y la
herencia NC/ND total son **capas posteriores**, no prerequisitos.

### Fase MVP — Facturar en USD con TC a mano (entrega el requisito del dueno)

Backend:
- Columnas de trazabilidad en `Invoice` (§3.1): `ExchangeRateSource` (+ valor nuevo `BNA_VendedorDivisa`,
  nombre a confirmar contador), `ExchangeRateFetchedAt`, `ExchangeRateJustification`. Migracion aditiva
  nullable.
- Poblar `MonId`/`MonCotiz` desde el request (hoy ya se copian; falta que el front los mande).
- **Precision `MonCotiz` = `numeric(18,6)`** (§3.1, M2): verificar/ajustar en la migracion.
- **Justificacion obligatoria** cuando la factura es en USD + `MonCotiz > 1` (§3.2). Audit, patron
  `Manual`/INV-120.
- **Invariante "UNA factura = UNA moneda"** (§3.7): validar en el boundary de creacion.

Frontend:
- Selector **ARS / USD** en `CreateInvoiceModal` (default ARS). Carga manual del TC + fecha +
  justificacion cuando es USD. Montos con simbolo correcto.

Homologacion ARCA:
- Sumar `<CanMisMonExt>` al envelope (§3.6) + homologar A/B/C en DOL con `CanMisMonExt` +
  `CondicionIVAReceptorId` -> CAE aprobado. **Bloquea prender el flag en prod.**

Todo detras de `EnableMultiCurrencyInvoicing` OFF. Tests de equivalencia flag OFF (byte-identico).
**No toca ADR-011 ni la NC/ND total.**

### Fase posterior — Automatizar el TC (ADR-011) + herencia NC/ND total + tarifario

- **ADR-011 (automatizar/validar TC):** cuando exista, el modal propone el TC y el backend lo valida
  contra la fuente; el carga manual queda como fallback (stale/ausente). Setea `ExchangeRateQuoteId`.
  **No es prerequisito del MVP.**
- **Herencia de moneda/TC en NC/ND TOTAL (§3.3):** **reemplazar** el guard de rechazo
  (guard de moneda en `EnqueueAnnulmentAsync` y en `ProcessAnnulmentJob`, `InvoiceService.cs`) por el camino de emision, respetando el **orden no negociable**
  (primero el guard de incoherencia replicando `:1884`, despues levantar el fail-fast, + test de
  no-regresion flag OFF). NO es cosmetico. (La NC **parcial** ya emite en USD; esto es la **total**.)
- **Tarifario en USD propagado a la factura (§3.4, condicional):** **verificar antes el flujo
  Rate->Invoice** (hoy `Rate.Currency` parece decorativo). Solo si Gaston confirma propagacion/proposicion
  automatica. Si solo quiere "mostrar precios en USD", puede ser solo frontend del tarifario.

### Hardening opcional (fuera del camino critico)

- **`AfipSettings.TaxCondition` (§3.5):** la UI ya es un `<select>` cerrado, asi que el typo no es un
  riesgo presente. Normalizer + validacion startup = defensa en profundidad de bajo valor (edicion SQL
  directa + soporte futuro RI). Mini-trabajo opcional, sin prioridad, **fuera del camino critico**.

---

## 5. Consecuencias, compatibilidad y rollback

### 5.1 Compatibilidad
- **Migracion 100% aditiva**: columnas nullable de trazabilidad en `Invoice` (§3.1) + settings nuevos +
  (cond.) `CanMisMonExt`. **Excepcion a verificar:** ajustar la precision de `MonCotiz` a `numeric(18,6)`
  (M2) es un `ALTER COLUMN` de **ampliacion** de precision — no destructivo, pero hay que confirmar el tipo
  actual antes (es un cambio de tipo, no aditivo puro). Nada se borra. Sin backfill (las facturas viejas en
  PES quedan correctas).
- **Flag OFF = comportamiento actual**: el frontend no muestra selector, el backend usa PES/1, el envelope
  SOAP es el de hoy. Cero cambio observable.
- **El path PES es byte-identico**: `BuildMonedaSoapFragment` ya emite el `<MonCotiz>1</MonCotiz>` historico
  para pesos (verificado, blindado por test). No tocamos ese camino.

### 5.2 Rollback
- Apagar el flag revierte el comportamiento sin migracion inversa.
- Rollback de esquema (si hiciera falta): drop de las columnas nullable de trazabilidad. Limpio porque son
  aditivas y con flag OFF nadie las setea. Ningun comprobante con CAE depende de ellas para existir (el TC
  ya quedo en `MonCotiz`). El cambio de precision de `MonCotiz` no se revierte (no afecta valores PES=1).

### 5.3 Riesgo de activacion
- **No prender el flag en prod hasta**: (a) signoff del contador sobre cual TC y como (§11), (b) CAE de
  homologacion aprobado para A/B/C en DOL con los campos RG 5616 (§3.6). Mismo patron y disciplina que
  `EnablePartialCreditNoteRealEmission`.
- **ADR-011 NO es condicion para activar el MVP.** El MVP factura en USD con el TC cargado a mano +
  justificacion + audit. ADR-011 (automatizar/validar el TC) se prende **despues**, como mejora — no
  bloquea la primera entrega.

---

## 6. Estrategia de testing

> **Entorno** (regla del proyecto): la DB Postgres vive en el **VPS remoto, no local**. Unit corren local
> (InMemory + Moq); integration con TestContainers los corre el reviewer **en el VPS**.

- **Unit (local) — MVP**:
  - `BuildMonedaSoapFragment` ya esta cubierto (PES byte-identico, DOL 6 decimales).
  - `ArcaCurrencyMapper`: USD->DOL, ARS->PES, no soportada->null (ya existe; sumar si entra otra moneda).
  - Decision A/B/C **ortogonal a moneda**: Mono+USD->C, RI(cliente RI)+USD->A, RI(cliente CF)+USD->B,
    todos cerrando el envelope.
  - Factura USD sin justificacion -> rechaza (justificacion obligatoria, §3.2).
  - Factura USD con `MonCotiz <= 1` -> rechaza (incoherente, §3.2).
  - Invariante una-moneda: items de Rate en monedas distintas no se mezclan en una factura (§3.7).
- **Unit (local) — fase posterior**:
  - Herencia NC/ND TOTAL: origen DOL@1050 -> NC sale DOL@1050 aunque el request pida otra cosa.
  - Guard incoherencia NC/ND total: origen DOL con MonCotiz<=1 -> revision manual, NO emite (replicar `:1884`).
- **Integration (VPS)**:
  - Flag OFF: factura sin selector -> PES/1, envelope byte-identico (no-regresion).
  - **Flag OFF + factura USD -> NC/ND total sigue RECHAZANDO** (no-regresion del guard de moneda en `EnqueueAnnulmentAsync`/`ProcessAnnulmentJob`).
  - Flag ON (MVP): factura USD con TC manual + justificacion -> persiste Source/FetchedAt/Justification,
    queda auditada, emite DOL.
- **Homologacion ARCA (manual, fuera de CI)**: A/B/C en DOL con `CanMisMonExt` + `CondicionIVAReceptorId`
  -> CAE aprobado en homologacion ANTES de prod.

---

## 7. Riesgos

| # | Riesgo | Sev | Mitigacion |
|---|---|---|---|
| R1 | Emitir USD en prod sin `CanMisMonExt` (RG 5616) -> rechazo/observacion ARCA | Alto | Parte del MVP: sumar el campo + homologacion obligatoria antes de prod (§3.6) |
| R2 | TC tipeado mal congelado en comprobante con CAE (no se corrige, solo NC) | Alto | MVP: justificacion obligatoria + `MonCotiz > 1` + audit (§3.2). Capa posterior (ADR-011): validar contra fuente -> divergencia exige `Manual`+justificacion |
| R3 | Typo en `AfipSettings.TaxCondition` -> tipo de comprobante equivocado | **Bajo** (corregido en review) | La UI ya es un `<select>` cerrado con los 3 valores validos (`AfipSettingsTab.jsx:174-183`): no se puede typear. Vector residual solo por edicion SQL directa / futuro RI -> hardening §3.5 opcional |
| R4 | Levantar el fail-fast de NC/ND total ANTES del guard de incoherencia -> NC USD con TC=1 espurio | **Alto** | Orden NO negociable §3.3: primero guard de incoherencia (replicar `:1884`), DESPUES levantar fail-fast (guard de moneda en `EnqueueAnnulmentAsync`/`ProcessAnnulmentJob`), + test no-regresion flag OFF |
| R5 | Confundir TC fiscal (comprobante) con TC comercial (pricing tarifario) | Medio | §3.4: NO convertir auto; el operador define el monto. Separar pricing de fiscal |
| R6 | Asumir que el MVP depende de ADR-011 y atar la entrega a una pieza no implementada | Medio | Corregido: MVP usa TC a mano (`Manual`/INV-120). ADR-011 es capa posterior, no prerequisito (§2, §4, §5.3) |
| R7 | `Rate.Currency` resulta NO ser decorativo y ya fluye de forma oculta | Bajo | A VERIFICAR el flujo Rate->Invoice antes de la fase tarifario (§3.4); no asumir |
| R8 | Enum `ExchangeRateSource` sin valor "vendedor divisa" -> no se puede etiquetar el TC RG 5616 | Medio | Agregar valor nuevo `BNA_VendedorDivisa` (nombre/uso a confirmar contador, §1.3 punto 4). MVP usa `Manual` hasta entonces |
| R9 | `MonCotiz` con menos de 6 decimales diverge del SOAP -> comprobante no cuadra | Medio | Declarar columna `numeric(18,6)` (M2, §3.1); verificar tipo actual en la migracion |

---

## 8. Alternativas consideradas

1. **VO `FiscalSnapshot` en la factura directa (reusar el de cancelacion).** Descartada (§3.1): acopla la
   factura al ciclo de cancelacion (T0/T2/T3). 3 columnas planas son mas simples.
2. **Conversion automatica tarifario USD -> factura ARS con un TC.** Descartada por ahora (§3.4): mezcla
   TC comercial con TC fiscal. Empezar mostrando/proponiendo; sumar conversion solo si el negocio lo pide.
3. **Elegir la moneda en la NC/ND a mano.** Descartada (§3.3): rompe la coherencia con el origen y es lo
   que el dueno explicitamente NO quiere ("herencia automatica").
4. **Construir nuestra propia fuente de TC dentro de este ADR.** Descartada: es ADR-011. Duplicarlo seria
   sobre-ingenieria y dos fuentes de verdad.

---

## 9. Migracion / rollback

Ver §5. Resumen: aditiva, sin backfill, reversible por flag + drop de columnas nullable.

---

## 10. Dependencias

1. **ADR-011 (fuente de TC)** — **NO es dependencia del MVP** (corregido en review 2026-05-29). El MVP
   factura en USD con el TC cargado a mano (`Manual`/INV-120). ADR-011 es una **capa POSTERIOR** que
   automatiza/valida el numero. Verificado: busque `IExchangeRateResolver` y `ExchangeRateQuotes` en
   `src/` -> **0 resultados** = NO implementado. No bloquea el MVP.
2. **Enum condicion fiscal** — `TaxConditionCanonical`/`TaxConditionNormalizer` ya existen (verificado).
   Solo relevante para el hardening opcional (§3.5), fuera del camino critico.
3. **Valor nuevo de enum `ExchangeRateSource`** — falta `BNA_VendedorDivisa` (verificado, §1.3 punto 4).
   Nombre/uso a confirmar contador. MVP usa `Manual` hasta entonces.
4. **Definicion fiscal del contador** (§11) — bloquea la activacion, no la construccion del MVP.
5. **Homologacion ARCA** (§3.6) — bloquea prod.

---

## 11. Pendiente de definicion del contador (NO bloquea construir el MVP; SI bloquea activar el flag en prod)

(Derivar al `travel-agency-accountant-argentina` — caso sector turismo + fiscal + contable.)

1. ¿El TC correcto para facturar en USD es **vendedor divisa BNA dia habil anterior** (RG 5616) sin
   excepciones para turismo, o hay un regimen especifico del sector?
2. ¿Que valor lleva `CanMisMonExt` en nuestro caso (la agencia cobra en USD o convierte a pesos)? ¿Es
   obligatorio para todos nuestros comprobantes USD?
3. ¿El criterio interno "NC/ND hereda el TC del origen" es fiscalmente correcto, o la NC debe re-cotizar
   al TC del dia de la NC? (Esto define §3.3 — es el corazon del requisito "inteligente".)
4. ¿A que fecha se toma el TC: emision del comprobante u operacion? (Esto valida la decision de diseno
   M3 §3.1: hoy congelamos al crear la factura "pending", no al emitir el job. Si fiscalmente debe ser la
   fecha de operacion, hay que mover el punto de congelamiento.)
5. ¿Como se debe llamar / etiquetar el valor de enum nuevo para el TC vendedor divisa BNA
   (propuesta: `BNA_VendedorDivisa`)? ¿Es ese el TC correcto o aplica otro? (B2 / §1.3 punto 4.)
6. Transicion Mono->RI: ¿hay algo fiscal-especifico al cambiar de condicion con facturas USD en curso?

---

## 12. Preguntas abiertas para el reviewer / Gaston (en criollo)

**Q1 — Confirmacion de scope MVP (decision del review, ya tomada — solo confirmar):** ADR-011 NO esta
implementado y el MVP **no lo necesita**. El MVP factura en USD con el operador cargando el TC **a mano**
(fuente + fecha + justificacion + audit, patron `Manual`/INV-120). ADR-011 (que el sistema te proponga y
valide el numero solo) queda para **despues**. Ejemplo pelotudo: hoy el cajero escribe "el dolar vale 1.050
y lo saque del Banco Nacion de ayer"; manana el sistema se lo llena solo. Arrancamos por la version a mano,
que ya entrega lo que pediste (facturar en dolares), ¿de acuerdo?

**Q3 — "Tarifario en USD": ¿que significa exactamente?** Ejemplo pelotudo: en el tarifario cargas "Hotel X:
USD 100 la noche". Cuando facturas, ¿queres que (a) el sistema te muestre "este precio es en dolares" y vos
elegis la moneda de la factura, o (b) que el sistema **convierta solo** los 100 dolares a pesos con el
dolar de hoy y te ponga el numero en pesos? La (a) es mucho mas simple y segura; la (b) mete un TC comercial
que es otro tema. ¿Cual de las dos?

**Q4 — ¿Hay facturas USD ya emitidas en produccion (por la NC parcial F2.5 o de prueba)?** Esto define si
el guard del caso legacy (§3.3 / R4) es teorico o real, y si hay que mirar datos antes de prender nada.

**Q5 — Endpoint del TC para el frontend — SOLO aplica a la capa posterior (ADR-011), NO al MVP.** En el
MVP el operador escribe el TC a mano, asi que no hace falta ningun endpoint. Cuando se sume ADR-011 (que el
sistema proponga el numero), habra que definir el contrato HTTP del resolver (ADR-011 no expone un endpoint
todavia). Lo registro como sub-tarea de la fase posterior, no del MVP.

---

## 13. Fuentes (normativa, verificadas — vigencia exacta a confirmar por el contador)

- AFIP/ARCA — Se facilita la emision de facturas en moneda extranjera: https://servicioscf.afip.gob.ar/publico/sitio/contenido/novedad/ver.aspx?id=4468
- Argentina.gob.ar — Noticia oficial RG 5616/2024: https://www.argentina.gob.ar/noticias/se-facilita-la-emision-de-facturas-en-moneda-extranjera
- Argentina.gob.ar — Texto / resumen RG 5616/2024: https://www.argentina.gob.ar/normativa/nacional/resoluci%C3%B3n-5616-2024-407369
- Boletin Oficial — RG 5616/2024: https://www.boletinoficial.gob.ar/detalleAviso/primera/318374/20241218
- Facturante — Prorroga de vigencia RG 5616/2024 (cronograma): https://blog.facturante.com/facturacion-electronica-en-moneda-extranjera-se-prorroga-la-entrada-en-vigencia-de-la-rg-5616-2024/
- Sovos — TC a consignar en comprobante en moneda extranjera: https://sovos.com/es/cambios-regulatorios/iva/argentina-se-establece-el-tipo-de-cambio-a-consignar-cuando-el-comprobante-electronico-se-emita-en-moneda-extranjera/

> **Aviso profesional**: la validez vigente de la RG 5616/2024, las fechas exactas de obligatoriedad
> (hubo prorrogas), el valor de `CanMisMonExt` para turismo y el tratamiento del TC en NC/ND deben ser
> confirmados por un contador/asesor fiscal matriculado antes de produccion. Este ADR analiza y disena;
> NO es autoridad fiscal final.
