# ADR-011 — Fuente confiable de tipo de cambio (USD->ARS) para el circuito fiscal

- **Status**: Propuesto (Draft) — pendiente de review del `software-architect-reviewer` y de definicion fiscal del contador. **NO Accepted**: la activacion (prender el flag) depende de un signoff fiscal explicito (ver §10).
- **Date**: 2026-05-29.
- **Author(s)**: software-architect agent.
- **Related**:
  - [ADR-002 Cancelacion / Refund](ADR-002-cancellation-refund.md) (de aca sale `FiscalSnapshot`, el enum `ExchangeRateSource`, los momentos T0/T2/T3 y el invariante INV-120 del TC manual).
  - [ADR-009 NC parcial fiscal Hotel](ADR-009-partial-credit-note.md) (multimoneda en NC parcial; la NC reusa el TC congelado del origen).
  - [ADR-010 Bandeja de reconciliacion de NC parciales](ADR-010-partial-credit-note-receipt-reconciliation-inbox.md) (mismo patron de habilitacion segura por flag + signoff contador).
  - Plan tactico FC1.3 Fase 2 ([plan-tactico-fc1-3-fase2.md](../plan-tactico-fc1-3-fase2.md)).

---

## 1. Contexto

### 1.1 El ejemplo pelotudo (el cambista de la esquina)

Imaginate que vendes algo en dolares y tenes que anotar en la factura "hoy el dolar vale X".
Hoy ese numero **lo escribe a mano la persona que carga la venta**: lo mira en una pizarra, en
el celular, donde sea, y lo tipea. Si se equivoca y pone 950 en vez de 1050, el papel sale con
el numero equivocado y **ese papel ya tiene sello oficial** (CAE de ARCA) — no lo podes borrar.
Aparte, el unico lugar del sistema que "sabe solo" cuanto vale el dolar es un cartelito del
tablero (dashboard) que **copia el numero de la pagina del Banco Nacion leyendo el HTML como
quien lee un diario** — y si el banco cambia el diseno de la pagina, deja de funcionar y muestra
el ultimo numero viejo que tenia guardado.

Este ADR propone que el sistema **tenga su propia fuente confiable del dolar**, que el operador
**proponga** el numero pero el sistema lo **valide** contra esa fuente, y que quede guardado un
historico por dia y por fuente para que el contador pueda reconstruir cualquier comprobante.

### 1.2 Hechos verificados en el repo (anclajes del diseno)

- **El TC fiscal viene del request del caller, no de una fuente oficial automatica.**
  `CreateInvoiceRequest.MonCotiz` tiene **default `1m`** y lo setea quien arma el request
  (pantalla / operador). VERIFICADO en `src/TravelApi.Application/DTOs/CreateInvoiceRequest.cs:42`.
  **Ojo con dos campos distintos del comprobante (NO son intercambiables):**
  - **`MonId`** = el **codigo de la moneda** ("PES" para pesos, "DOL" para dolares). Dice *en que
    moneda* esta el comprobante.
  - **`MonCotiz`** = la **cotizacion** (un numero) de esa moneda contra el peso. Dice *cuanto vale*
    un dolar el dia de la factura.

  Este ADR ataca el problema del **`MonCotiz`** (el numero del TC), no el del `MonId` (el codigo de
  moneda, que el operador ya elige bien). Ese `MonCotiz` es el que termina en el campo `MonCotiz` del
  comprobante electronico que mira ARCA (junto al `MonId` que indica la moneda).
  El default 1 del `MonCotiz` es correcto para pesos (`MonId` = "PES", cotizacion 1), pero para una
  factura en USD (`MonId` = "DOL") el numero del `MonCotiz` lo pone el humano sin que nada lo contraste
  contra una referencia.

- **El unico servicio que trae el dolar hace scraping HTML y solo alimenta el dashboard.**
  `BnaExchangeRateService` (`src/TravelApi.Infrastructure/Services/BnaExchangeRateService.cs`):
  - Pega un GET a `https://www.bna.com.ar/personas` y **parsea el HTML con regex** (`DOLAR U.S.A.`,
    `EURO`, `REAL`, fecha y hora de actualizacion). Es el **dolar billete minorista vendedor**.
    VERIFICADO (`ParseSnapshot`, lineas 155-244).
  - Si el parseo falla (porque cambio la pagina, o el banco devuelve "PAGINA NO DISPONIBLE"),
    cae a un **fallback marcado `IsStale = true`**: primero el cache en memoria, sino el ultimo
    valor persistido. VERIFICADO (`GetUsdSellerRateAsync`, lineas 37-80).
  - **Persistencia singleton**: guarda en `BnaExchangeRateSnapshots` con un unico `SingletonId`
    via `INSERT ... ON CONFLICT ("Id") DO UPDATE` — es decir, **un solo registro que se pisa**,
    sin historico por dia. VERIFICADO (`SavePersistedSnapshotAsync`, lineas 137-153).
  - **Solo se inyecta en `ReportService`** (dashboard). VERIFICADO: el unico registro DI es
    `Program.cs:490` (`AddScoped<IBnaExchangeRateService, BnaExchangeRateService>()`) y los unicos
    consumidores reales son `ReportService` y sus tests. NO lo consume `InvoiceService` ni
    `BookingCancellationService`. **El circuito fiscal hoy ni siquiera mira este servicio.**

- **La NC reusa el TC congelado de la factura origen — esto esta BIEN y NO se toca.**
  `FiscalSnapshot.ExchangeRateAtOriginalInvoice` (`src/TravelApi.Domain/Entities/FiscalSnapshot.cs:78`)
  guarda el TC del momento de la factura original. La nota de credito posterior reusa ese valor
  congelado (no re-cotiza). VERIFICADO. Este ADR **no modifica** ese comportamiento.

- **Ya existe el enum `ExchangeRateSource` y los invariantes asociados.**
  `src/TravelApi.Domain/Entities/ExchangeRateSource.cs`: valores
  `Unset(0) / BCRA_A3500(1) / BNA_Mayorista(2) / BNA_Minorista(3) / AfipOficial(4) / Manual(5)`.
  `Manual` exige `FiscalSnapshot.ManualJustification` (INV-120). VERIFICADO.
  El CHECK `chk_BookingCancellations_fiscalsnapshot_consistent` (INV-118) exige, para estados
  post-borrador (`>= AwaitingFiscalConfirmation`), que el TC sea `> 0`, `Source != Unset` y
  `Currency != null`. VERIFICADO (referenciado en ADR-002 §2.7 y en los comentarios del enum).

- **Hangfire ya esta en el stack.** `Program.cs:765-806` registra ~5 `RecurringJob.AddOrUpdate`
  diarios / cada-30-min (unpaid reservas, lifecycle, alertas y reconciliaciones FC1.3), todos
  no-op cuando su flag esta apagado. VERIFICADO. **Hay patron para sumar un job diario nuevo.**

- **Stack PostgreSQL** (Npgsql, `xmin` como concurrency token, comillas dobles, CHECK constraints).
  VERIFICADO en ADR-009 §1.3 y en las migraciones del modulo.

- **Patron de habilitacion segura por flag.** `OperationalFinanceSettings.EnablePartialCreditNoteRealEmission`
  (default `false`, `OperationalFinanceSettings.cs:270`) demuestra el patron: feature nueva detras
  de flag OFF por default, validacion de pre-condiciones en startup, y no se prende en prod hasta
  signoff del contador. VERIFICADO. Este ADR copia ese patron.

### 1.3 Riesgos del estado actual

1. **(Grave) TC fiscal sin fuente autoritativa.** El numero que va al comprobante depende de lo que
   escriba la pantalla. La etiqueta `Source` que se guarda en `FiscalSnapshot` **no garantiza que el
   valor numerico realmente corresponda a esa fuente** — es solo una etiqueta declarada por el caller.
   Un error de tipeo queda fijado en un comprobante con CAE (no se puede corregir, solo emitir NC).

2. **(Alto) Scraping fragil y fuente probablemente incorrecta.** El parseo por regex del HTML del
   BNA se rompe si cambia la pagina (cae a `IsStale`). Ademas es **single-source** (un solo origen,
   sin contrato ni SLA) y el **billete minorista vendedor probablemente NO es el TC fiscalmente
   correcto** para liquidaciones (eso lo define el contador — ver §10).

3. **(Medio) Sin historico para reconstruccion fiscal.** La persistencia singleton pisa el unico
   registro cada vez. No hay forma de responder "que valor tenia la fuente X el dia D" para
   reconstruir un comprobante viejo en una inspeccion.

4. **(Conocido, fuera de este ADR pero relacionado) Factura USD legacy sin snapshot -> NC con TC=1.**
   Una factura USD anterior al modulo, sin `FiscalSnapshot`, podria derivar una NC con `TC=1`. Regla:
   ese caso **debe ir a revision manual, nunca emitir con TC=1**. Lo registramos aca como riesgo
   colindante; su tratamiento corresponde al circuito de NC (ADR-009 / bandeja ADR-010).

5. **(Problema SEPARADO, NO lo resuelve este ADR) La diferencia de cambio no esta implementada.**
   Ejemplo pelotudo: facturas en USD un dia que el dolar vale 1000 (fecha de factura, T0), y la plata
   recien entra/sale dos meses despues cuando el dolar vale 1100 (T2/T3). Esa diferencia de 100 por
   dolar es un **resultado por diferencia de cambio** que contablemente hay que registrar en algun
   lado. **Hoy no se registra en ningun lado:**
   - **No existe modulo de asientos contables.** Cero clases `Journal` / `Ledger` / `AccountingEntry`
     en el repo. VERIFICADO (busqueda sin resultados en `src/`).
   - **`OperatorRefundService` hoy hardcodea el TC de recepcion a `1`** — MVP, no captura el TC real
     del momento del ingreso. VERIFICADO: `ExchangeRateAtReceipt = 1m` en
     `src/TravelApi.Infrastructure/Services/OperatorRefundService.cs:119` (con comentario que dice que
     "una FC futura agrega FetchedAt + fuente").
   - **El comentario de `FiscalSnapshot.cs:27`** que dice que la diferencia de cambio entre T0/T2/T3
     "genera asientos contables propios" describe una **intencion NO implementada**, no codigo que
     exista. VERIFICADO (es un comentario de diseno, no hay asientos atras).

   **Que provee este ADR y que NO**: este ADR provee la **FUENTE del tipo de cambio** (de donde sale el
   numero y como se valida). **NO** provee el **tratamiento contable de la diferencia de cambio**. Tener
   buenos TC en T0/T2/T3 es *precondicion* para calcular esa diferencia, pero el calculo y el asiento
   son otro laburo. La diferencia de cambio requiere un **ADR aparte** que depende de **(a)** este ADR
   (para tener los TC confiables en cada momento) y **(b)** un **modulo de asientos contables hoy
   inexistente**. Mientras ese modulo no exista, la diferencia de cambio simplemente no se registra.

---

## 2. Que entra y que NO entra

### 2.1 Entra (a lo largo de las fases, todo detras del flag)

1. Un componente de cotizaciones con la **fuente fiscal CONFIGURABLE** (no hardcodeada), en tres piezas:
   `IExchangeRateProvider`, `IExchangeRateResolver`, `ExchangeRateSyncJob` (ver §3).
2. Una **tabla historica nueva** `ExchangeRateQuotes` (un registro por moneda + dia + fuente), aditiva.
3. Una **FK nullable** opcional `FiscalSnapshot.ExchangeRateQuoteId` para trazabilidad fuerte.
4. **Settings nuevos** en `OperationalFinanceSettings`: fuente fiscal configurable + politica de staleness.
5. El **flag `EnableAuthoritativeExchangeRate`** (OFF por default). Con OFF, comportamiento
   **byte-identico** al actual (el `MonCotiz` del request sigue funcionando igual).

### 2.2 NO entra (scope cut explicito)

- **Re-cotizar la NC.** La nota de credito sigue reusando el TC congelado del origen
  (`ExchangeRateAtOriginalInvoice`). NO se toca.
- **Reemplazar el dashboard.** El `BnaExchangeRateService` singleton del dashboard sigue como esta.
  No lo migramos en este ADR (ver §7, single-source persiste hasta Fase 3).
- **Elegir/afirmar la fuente fiscalmente correcta.** Eso lo define el contador (§10). El diseno deja
  la fuente **enchufable y configurable**; no decide cual es la buena.
- **Integrar una API externa concreta (BCRA/AFIP).** Es condicional a verificar que exista y a la
  definicion fiscal (Fase 3). Este ADR NO afirma endpoints (ver §4).
- **Backfill de cotizaciones historicas.** La tabla arranca vacia y se puebla hacia adelante.

---

## 3. Decision: componente de cotizaciones con fuente fiscal configurable

Tres piezas con responsabilidades separadas. La idea central: **el circuito fiscal no le habla a
una fuente concreta; le habla a un resolver que sabe cual es la fuente fiscal de hoy** (leida de
settings). Asi, cambiar de fuente es configuracion, no redeploy.

### 3.1 `IExchangeRateProvider` (un implementador por fuente externa)

Cada fuente externa es **un** provider. Contrato minimo:

```
interface IExchangeRateProvider {
    ExchangeRateSource Source { get; }                 // que fuente representa este provider
    Task<ExchangeRateQuoteResult?> TryGetQuoteAsync(   // null/IsStale si no pudo traer fresco
        string currency, DateOnly date, CancellationToken ct);
}
```

- Implementadores iniciales:
  - **`BnaMinoristaExchangeRateProvider`**: reempaqueta el scraping existente como provider. NO
    duplica la logica de parseo; envuelve / reusa lo que ya hace `BnaExchangeRateService`. Queda
    como **provider de respaldo**, NO como fuente fiscal por default.
  - **`ManualExchangeRateProvider`**: el override manual (ya modelado por `ExchangeRateSource.Manual`
    + `ManualJustification` / INV-120). No "trae" un valor; lo recibe del operador con justificacion.
  - **(Futuro, condicional) provider de una API estable** (BCRA A3500 / AFIP oficial) cuando se
    verifique cual existe y el contador defina la correcta (ver §4 y Fase 3).
- Responsabilidad del provider: traer el numero de SU fuente, mapear errores a `IsStale`/null,
  timeout y reintentos propios. No sabe nada del circuito fiscal.

### 3.2 `IExchangeRateResolver` (lo que consume el circuito fiscal)

Es **lo unico que el circuito fiscal conoce**. Sabe cual provider es la fuente fiscal de hoy
(leido de settings) y aplica la politica de staleness.

```
interface IExchangeRateResolver {
    Task<ExchangeRateResolution> ResolveFiscalRateAsync(
        string currency, DateOnly date, CancellationToken ct);
}

record ExchangeRateResolution(
    decimal Value,
    ExchangeRateSource Source,
    DateTime FetchedAt,
    bool IsStale,
    bool RequiresManualFallback,   // true si no hay valor fresco/aceptable -> exige Manual+justificacion
    int? ExchangeRateQuoteId);     // puntero al historico, para trazabilidad fuerte (§3.4)
```

- Lee primero el **historico** (`ExchangeRateQuotes`) para la fuente fiscal + moneda + fecha.
- Si no hay registro fresco segun la politica de staleness -> `IsStale = true` y
  `RequiresManualFallback = true`. El circuito fiscal entonces **exige `Manual` + justificacion**
  (no inventa un numero ni usa silenciosamente uno viejo).
- Para `ARS` devuelve PES / `1` sin tocar providers (caso trivial, no se cotiza el peso contra si mismo).

### 3.3 `ExchangeRateSyncJob` (Hangfire, diario, idempotente)

- Job recurrente nuevo registrado igual que los de `Program.cs:765-806`. Corre 1 vez/dia (horario a
  definir con ops; sugerido despues de que las fuentes publiquen).
- Por cada fuente configurada y cada moneda activa: llama al provider, y hace **upsert idempotente**
  en `ExchangeRateQuotes` por `(Currency, QuoteDate, Source, RateType)`.
- Idempotente: correr dos veces el mismo dia no duplica filas (re-upsert del mismo registro).
- No-op si el flag `EnableAuthoritativeExchangeRate` esta OFF (igual que los demas jobs del modulo).
- Marca `IsStale = true` cuando el provider no pudo traer fresco (no borra el dato viejo; lo deja
  con la marca para que el resolver decida).

### 3.4 Trazabilidad fuerte opcional: `FiscalSnapshot.ExchangeRateQuoteId`

FK **nullable** desde `FiscalSnapshot` al registro de `ExchangeRateQuotes` que se uso. Permite
responder "este comprobante uso EXACTAMENTE este registro de cotizacion, traido tal dia de tal
fuente". Nullable porque: (a) los snapshots viejos no lo tienen, (b) con el flag OFF no se setea,
(c) el caso `Manual` puede no tener un registro de historico atras.

---

## 4. Opciones de fuente (honesto: REAL vs A VERIFICAR)

| Fuente | Estado | Rol propuesto |
|---|---|---|
| **BNA minorista (scraping)** | **REAL hoy** (existe `BnaExchangeRateService`, fragil) | Provider de **respaldo**, NO fuente fiscal. |
| **API estadisticas BCRA (A3500)** | **A VERIFICAR** | Candidata a fuente fiscal (Fase 3). NO afirmo endpoint/URL/auth/rate-limit ni que publique A3500 de forma consumible. Hay que verificarlo. |
| **AFIP/ARCA TC para liquidaciones** | **A VERIFICAR** | Candidata a fuente fiscal (Fase 3). NO afirmo que exista un endpoint consumible. Hay que verificarlo. |
| **Proveedor de datos pago (3rd party)** | REAL en el mercado | Probablemente **sobre-ingenieria hoy**. Queda como opcion futura si las gratuitas no alcanzan. |
| **Manual** | **REAL hoy** (modelado: `ExchangeRateSource.Manual` + INV-120) | **Override** con justificacion obligatoria. Se mantiene siempre. |

**Recomendacion**: disenar para **N providers detras de la interfaz** y **arrancar con BNA (respaldo)
+ Manual (override)**. Dejar **enchufable** la API oficial (BCRA/AFIP) para cuando se verifique cual
existe de verdad y el contador defina cual es la fiscalmente correcta. **No se afirma ningun endpoint
externo en este ADR.**

---

## 5. Donde se enchufa en el circuito fiscal

### 5.1 Factura en USD (`InvoiceService`)

- El resolver **alimenta y valida** `MonCotiz`: el operador **propone** el numero, el sistema lo
  **valida** contra la fuente fiscal.
- Si el valor propuesto **coincide** (dentro de tolerancia configurable) con la fuente fiscal -> se
  acepta con `Source` = fuente fiscal + `FetchedAt` + (opcional) `ExchangeRateQuoteId`.
- Si **difiere** de la fuente fiscal -> el sistema exige `ExchangeRateSource.Manual` + justificacion
  (INV-120). El operador puede igual cargar su numero, pero queda explicitamente marcado como Manual
  con razon escrita y audit. **No se acepta un numero que dice ser "BNA_Minorista" pero no coincide
  con BNA_Minorista.**
- `ARS` sigue `PES` / `1` sin pasar por providers.

### 5.2 Cancelacion (`BookingCancellationService`)

- La NC **sigue reusando el TC congelado del origen** (`ExchangeRateAtOriginalInvoice`). **NO se toca.**
- El resolver **solo** entra para capturar **TC nuevos** en los momentos donde efectivamente hay una
  conversion nueva: T2 (reembolso del operador) / T3 (retiro del cliente), segun ADR-002 §2.3.
  Esos momentos hoy tambien capturan `ExchangeRateSource` + `FetchedAt`; el resolver pasa a ser quien
  propone/valida ese numero, con la misma logica de la §5.1 (proponer + validar + fallback Manual).

---

## 6. Fases (incremental, reversible por flag)

> Sin estimaciones de tiempo. Cada fase es independientemente testeable y reversible apagando el flag.

- **Fase 0 — Fundacion (NO toca el circuito fiscal).**
  Modelo (`ExchangeRateQuotes` + FK nullable en `FiscalSnapshot`), providers (BNA reempaquetado +
  Manual), resolver, job de sync diario, settings, flag. El job puebla el historico. El circuito
  fiscal **todavia no consume el resolver**. **Bajo riesgo. NO depende del contador.** Se puede
  mergear y dejar el job poblando datos mientras el contador define la fuente fiscal.
  > **Importante — la Fase 0 NO cierra el agujero fiscal.** La Fase 0 entrega **trazabilidad +
  > historico de cotizaciones**, pero **NO valida** el TC que va al comprobante ni cierra el riesgo
  > fiscal: el peligro de que el operador tipee un TC sin contraste contra una fuente **persiste hasta
  > la Fase 1** (cuando el circuito de facturacion efectivamente consume el resolver y valida el
  > numero). Mientras estemos en Fase 0, el `MonCotiz` lo sigue poniendo el humano sin validar; lo
  > unico que ganamos es que ya hay un historico al lado para comparar *despues*, no *en el momento*
  > de emitir. Que no se lea como que la Fase 0 ya aporta integridad fiscal: aporta los cimientos, no
  > la integridad.

- **Fase 1 — Resolver en facturacion USD (detras del flag).**
  `InvoiceService` propone/valida `MonCotiz` con el resolver. **Flag OFF = comportamiento actual**
  (el `MonCotiz` del request manda, byte-identico). **Flag ON** = el sistema valida y exige Manual
  ante divergencia. Tests de **equivalencia** flag OFF (no-regresion).

- **Fase 2 — Captura de TC nuevo en cancelacion (T2/T3) via resolver.**
  `BookingCancellationService` usa el resolver para los TC **nuevos** de T2/T3. **NO** toca el TC
  congelado del origen. Detras del mismo flag.

- **Fase 3 (CONDICIONAL) — Segundo provider con API estable.**
  Sumar el provider BCRA/AFIP **una vez verificado cual existe** y **cual define el contador** como
  fuente fiscal. Recien aca se puede dejar de depender del scraping single-source. Esta fase queda
  bloqueada hasta tener la verificacion tecnica del endpoint + la definicion fiscal.

### 6.1 Fase 0-bis (quick win) — exigir origen del TC en factura USD, SIN depender del contador

> Nota recomendada por el `software-architect-reviewer`. **No reescribe las 4 fases**; es un paso
> chico de **mayor valor inmediato** que se puede hacer aparte, antes de tener el resolver completo y
> **sin esperar la definicion fiscal del contador**.

Ejemplo pelotudo: hoy, cuando cancelas, el sistema te **obliga** a decir de donde sacaste el dolar
(no te deja avanzar sin eso). Pero cuando emitis una factura en dolares directa, **no te obliga**:
podes mandar el numero sin decir de donde salio. Esto es asimetrico y sin sentido.

- **Que hace**: en la **emision de factura en moneda extranjera** (`MonId != "PES"`, es decir factura
  en USD), exigir que se declare **`Source != Unset`** + un **`FetchedAt` real** + **audit** del
  origen del TC. Es lo mismo que la **cancelacion ya exige hoy** por el CHECK
  `chk_BookingCancellations_fiscalsnapshot_consistent` (INV-118), pero que la **factura directa NO
  exige**. Esto **cierra la asimetria**.
- **Que NO hace**: NO valida que el numero sea *correcto* contra una fuente (eso es la Fase 1, que si
  necesita el resolver y la definicion fiscal). Solo exige que quede **registrado de donde dice venir**
  el TC y cuando se obtuvo. Es trazabilidad obligatoria del origen, no validacion del valor.
- **Por que vale la pena**: da **trazabilidad obligatoria del TC en facturacion** ya mismo, con poco
  codigo (reusa el enum `ExchangeRateSource` + `FiscalSnapshot` que ya existen), sin construir el
  resolver y **sin bloquearse en el contador**.
- **Riesgo a chequear antes**: si hay facturas USD en curso que hoy se emiten con `Source = Unset`,
  hay que ver como conviven con la nueva exigencia (default/transicion), igual que cualquier endurecida
  de validacion. A confirmar con `backend-dotnet-senior` antes de implementar.

---

## 7. Pendiente de definicion del contador (NO bloquea Fase 0; SI bloquea la activacion)

Estas preguntas **no impiden** construir y mergear la Fase 0 (modelo + historico + job, todo con flag
OFF). **Si** son condicion para prender el flag y para elegir la fuente fiscal:

1. **¿Cual es el TC fiscalmente correcto** para facturar en USD: minorista vendedor, BCRA A3500
   (mayorista), AFIP oficial, u otro?
2. **¿Se puede facturar con el TC del dia habil anterior** si hoy todavia no hay publicacion oficial?
   (Esto define la politica de staleness del resolver.)
3. **¿A que fecha se toma el TC**: fecha de emision del comprobante o fecha de la operacion?
4. **¿ARCA/AFIP expone un TC oficial consumible** por API que podamos usar como fuente fiscal? (A
   verificar tecnicamente + confirmar fiscalmente.)

---

## 8. Consecuencias, compatibilidad y rollback

### 8.1 Compatibilidad

- **Migracion 100% aditiva**: tabla nueva `ExchangeRateQuotes` + columna nullable
  `FiscalSnapshot.ExchangeRateQuoteId` + settings nuevos con defaults seguros. No se modifica ni se
  borra nada existente. Sin backfill.
- **Flag OFF = comportamiento actual**: con `EnableAuthoritativeExchangeRate = false`, el `MonCotiz`
  del request se usa tal cual hoy. No hay cambio de comportamiento observable.
- **La Fase 0 NO entrega integridad fiscal** (ver §6): solo trazabilidad + historico. El agujero
  fiscal (TC tipeado sin contraste) **sigue abierto hasta la Fase 1**, cuando el circuito de
  facturacion consume el resolver y valida el numero. Mergear la Fase 0 no reduce el riesgo fiscal
  presente; solo prepara el terreno.

### 8.2 Rollback

- Apagar el flag revierte el **comportamiento** sin necesidad de migracion inversa.
- Rollback de **esquema** (si hiciera falta): drop de `ExchangeRateQuotes` + drop de la columna
  nullable. Como es aditivo y nadie depende de la tabla con el flag OFF, es limpio. El historico de
  cotizaciones se perderia, pero **ningun comprobante fiscal depende de la tabla para existir** (el
  TC ya quedo materializado en `FiscalSnapshot`/comprobante).

### 8.3 Riesgo de activacion

- **No prender el flag en prod hasta el signoff del contador** (§7) — mismo patron y misma disciplina
  que `EnablePartialCreditNoteRealEmission` (ADR-009 / ADR-010).
- Mientras la API oficial no se verifique e integre (Fase 3), la fuente sigue siendo **single-source**
  (BNA scraping fragil). Esto es un riesgo **conocido y aceptado** para Fase 0-2: el valor pasa a estar
  validado y con historico, pero el origen de respaldo sigue siendo el mismo scraping hasta Fase 3.

### 8.4 Dependencia: diferencia de cambio (problema separado, NO incluido)

- Este ADR **no implementa** el tratamiento contable de la **diferencia de cambio** entre la fecha de
  factura (T0) y la fecha en que la plata realmente entra/sale (T2/T3) — ver §1.3 punto 5.
- Ese tratamiento requiere un **ADR aparte** que depende de **(a)** este ADR (fuente confiable de TC en
  cada momento) y **(b)** un **modulo de asientos contables que hoy NO existe** (cero clases
  `Journal`/`Ledger`/`AccountingEntry`; `OperatorRefundService.cs:119` hardcodea el TC de recepcion a
  `1`; el comentario de `FiscalSnapshot.cs:27` sobre "asientos contables propios" es una intencion no
  implementada). Mientras ese modulo no se construya, la diferencia de cambio no se registra.

---

## 9. Estrategia de testing

> **Entorno**: la DB Postgres vive en el **VPS remoto, no local** (no hay Postgres en la maquina de
> Gaston). Los **unit** corren local (InMemory + Moq); los **integration** con TestContainers los
> corre el reviewer **en el VPS**, no en la sesion principal.

- **Providers (unit, fixtures)**:
  - BNA: fixtures HTML reales + uno de **"pagina rota"** (debe devolver `IsStale`/null sin tirar).
  - Provider de API (cuando exista, Fase 3): fixtures JSON, incluido "respuesta vacia / 5xx".
- **Resolver (unit, providers mockeados)**:
  - cotizacion **fresca** -> devuelve valor + Source + `RequiresManualFallback=false`.
  - cotizacion **stale** segun politica -> `IsStale=true` + `RequiresManualFallback=true`.
  - cotizacion **ausente** -> `RequiresManualFallback=true`.
  - **multi-fuente**: elige la fuente fiscal configurada, no otra.
  - `ARS` -> devuelve `1` sin tocar providers.
- **Job (unit + integration)**: idempotencia (correr 2 veces no duplica), recovery (provider caido no
  rompe el job, marca stale).
- **Integracion fiscal**:
  - **Flag OFF**: `MonCotiz` byte-identico al actual en factura USD (test de **no-regresion**).
  - **Flag ON**: factura USD toma/valida el TC del resolver, queda **auditado**, y divergencia ->
    exige Manual + justificacion.
  - El **guard 0/1** existente (rechazo de TC invalido / Currency no soportada) sigue activo.

---

## 10. Plan de implementacion por capas (para backend-dotnet-senior)

Orden pensado para que cada capa sea testeable antes de la siguiente. **Solo Fase 0 esta lista para
arrancar sin esperar al contador.**

1. **Modelo + migracion (Fase 0)**: entidad `ExchangeRateQuote` + config EF + migracion aditiva
   Postgres (`ExchangeRateQuotes`, CHECK `Value > 0`, unico `(Currency, QuoteDate, Source, RateType)`,
   `xmin`) + columna nullable `FiscalSnapshot.ExchangeRateQuoteId`. Unit de mapeo.
2. **Providers + resolver (Fase 0)**: `IExchangeRateProvider` (BNA reempaquetado + Manual),
   `IExchangeRateResolver` con politica de staleness desde settings. Unit con providers mockeados (§9).
3. **Job de sync (Fase 0)**: `ExchangeRateSyncJob` + `RecurringJob.AddOrUpdate` en `Program.cs`
   (no-op con flag OFF). Unit de idempotencia + recovery.
4. **Settings + flag (Fase 0)**: fuente fiscal configurable + politica de staleness +
   `EnableAuthoritativeExchangeRate` (default false) + validacion de pre-condiciones en startup
   (mismo patron que los flags FC1.3).
5. **Resolver en facturacion USD (Fase 1, detras del flag)**: `InvoiceService` propone/valida
   `MonCotiz`. Tests de equivalencia flag OFF + tests flag ON.
6. **Captura T2/T3 en cancelacion (Fase 2, detras del flag)**: `BookingCancellationService` usa el
   resolver para TC nuevos, sin tocar el TC congelado del origen.
7. **(Condicional) Provider API estable (Fase 3)**: tras verificar endpoint + definicion fiscal.
8. **Reviewers**: `software-architect-reviewer` (este draft) -> `backend-dotnet-reviewer` ->
   `security-data-risk-reviewer` (toca facturas / TC fiscal / audit) -> `qa-automation`. Para la
   definicion fiscal: `arca-tax-expert-argentina` + `accounting-expert-argentina` (o el integrado
   `travel-agency-accountant-argentina`).

---

## 11. Preguntas abiertas para el reviewer / Gaston (en criollo)

**Q1 — ¿Tolerancia para "coincide con la fuente"?**
Ejemplo pelotudo: el sistema dice que hoy el dolar vale 1050 y el operador carga 1051. ¿Lo dejamos
pasar como "igual" o lo mandamos a Manual con justificacion? Propongo una tolerancia chica
configurable (ej. unos centavos / un porcentaje minimo) para no obligar a justificar por un redondeo,
pero el numero exacto lo confirma el contador.

**Q2 — ¿El job de sync corre aunque el flag fiscal este OFF?**
Propongo que **NO** corra con el flag OFF (igual que los demas jobs del modulo), para no pegarle a la
pagina del BNA sin necesidad. Pero si quisieras ir **juntando historico** antes de prender la feature,
podriamos darle un sub-flag propio para que el job pueble datos mientras el resto sigue apagado. ¿Que
preferis?

**Q3 — ¿`RateType` desde el dia uno o lo dejamos para despues?**
Lo dejo como columna **opcional** en el historico (para distinguir, ej., comprador/vendedor o
mayorista/minorista dentro de una misma fuente). No lo uso en Fase 0 mas que para el indice unico.
¿Te sirve tenerlo desde ahora o lo sacamos para no sumar campos que hoy no se usan?
