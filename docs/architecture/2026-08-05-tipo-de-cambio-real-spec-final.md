# SPEC DEFINITIVA — Tipo de cambio real (fuente ARCA)

**Fecha:** 2026-08-05
**Estado:** aprobada con cambios por `software-architect-reviewer`; cambios bloqueantes ya incorporados. **Lista para implementar.**
**Enmienda:** este documento **enmienda ADR-011** (`docs/architecture/adr/ADR-011-authoritative-exchange-rate-source.md`, 2026-05-29) en tres puntos de fondo: (a) **sin feature flag** (regla T-11), (b) **fuente primaria = ARCA `FEParamGetCotizacion`**, no BCRA ni scraping, (c) el camino interactivo **no llama a proveedores externos**. Ver §12.

> **Cómo leer esto:** es un cuerpo único y autosuficiente. No hay "deltas" ni versiones previas que consultar. Todo lo que hace falta para implementar está acá.

> ## ⚠️ ADENDA NOCTURNA (2026-08-05, firmada por el dueño) — pisa §11/§12.1/D-4 donde choquen
> La verificación EN VIVO demostró que `FEParamGetCotizacion` de HOMOLOGACIÓN devuelve una
> cotización VIEJA (~14 meses: 1.152 cuando el real era ~1.496) — el sistema factura en modo
> práctica, así que ese número solo sirve para facturas de prueba (validaciones 10038/10119/
> **10240: MonCotiz ≤ oficial ARCA + $1**, manual WSFEv1 v4.7). Cambios firmados:
> (a) SÍ se construyó `OficialPorApi = 7` + `OfficialDollarPublicApiService` (dolarapi +
> argentinadatos, contratos verificados con curl 2026-08-05) como fuente de DÓLAR REAL —
> lo que esta spec descartaba en §12.1/D-4 quedó revertido porque sin él no hay ningún dato
> real mientras AFIP corra en homologación; (b) el resolver ganó `excludePracticeOfficialData`
> (default false — facturar sigue sirviendo el AfipOficial del ambiente, CON leyenda "dólar de
> prueba" cuando IsProductionSource=false); (c) el dashboard muestra DOS tarjetas: "Dólar
> Banco Nación (venta)" (solo datos reales) y "Dólar para facturar (ARCA)" (lo que sugiere la
> factura, con badge ámbar de prueba). Fundamento fiscal completo: agent-memory del
> travel-agency-accountant (adr011-tc-fiscal...) — 10240 acota el criterio "TC realmente usado".

> ## ⚠️ ADENDA "EL DOLAR NUNCA FALTA" (2026-08-05, aprobada por el dueño) — Tanda 2
> Con el dueño viendo el dashboard EN VIVO, el pedido fue: el numero del dolar oficial **nunca**
> tiene que faltar. Cambios de esta tanda:
> (a) **CINCO proveedores publicos** en vez de dos: dolarapi.com, monedapi.ar (BNA especifico,
> `GET /api/v2/usd/bna`), criptoya.com (`GET /api/bancostodos`, clave `"bna"`), argentinadatos.com
> (sin cambios, ahora TAMBIEN cubre "hoy" ademas del backfill) y bluelytics.com.ar (`GET /v2/latest`,
> campo `oficial.value_sell` — es un PROMEDIO de mercado, no el BNA puntual, por eso va ULTIMO).
> Los cinco quedan implementados en `OfficialDollarPublicApiService`, todos bajo el mismo enum
> `OficialPorApi = 7` (se distinguen por `ProviderName`, no se agrega ningun valor de enum nuevo).
> Contratos verificados con `curl` real contra las cinco APIs el 2026-08-05.
> (b) **Escalera del dia** ampliada: ARCA -> dolarapi -> monedapi -> criptoya -> argentinadatos ->
> bluelytics -> scraper BNA (el scraper pasa a ser el ULTIMO respaldo de toda la cadena, ya no el
> unico). Corta en el primer proveedor que conteste un valor util.
> (c) **Cadencia**: el recurring de `ExchangeRateSyncJob` pasa de 1 vez/dia (`0 15 * * *`) a
> **cada hora** (`0 * * * *`). Guard barato al inicio de cada corrida
> (`IsTodayAlreadyFullyCoveredAsync`): si ya hay fila `OficialPorApi` de HOY Y (solo si el ambiente
> es productivo) fila `AfipOficial` de hoy con `IsProductionSource=true`, la corrida corta sin
> llamar a nadie. En homologacion NO se exige la fila `AfipOficial` de practica para este guard —
> volver a pedirle a ARCA cada hora en ese entorno es barato y no aporta nada distinto.
> (d) **On-demand**: `ExchangeRateResolver.GetSuggestionAsync` (unico punto de lectura de toda la
> libreta, usado tanto por el dashboard como por facturar) ahora, si no hay fila de HOY para la
> moneda pedida, encola `ExchangeRateSyncJob` en Hangfire (`IBackgroundJobClient.Enqueue`,
> fire-and-forget — el request que disparo la pregunta NUNCA espera a que el job termine). Debounce
> de 5 minutos por moneda (`IMemoryCache`) para no encolar de mas.
> (e) **Defensa de coherencia**: si el valor que se esta por guardar difiere mas de 5% de la fila
> mas reciente ya existente para el mismo dia (de cualquier otra fuente), se deja un `Warning` en el
> log — nunca bloquea el guardado (P-21/T-12).
> Detalle tecnico completo en el comentario de clase de `ExchangeRateSyncJob`,
> `ExchangeRateResolver` e `IOfficialDollarPublicApiService`.

---

## Estado de implementación (actualizado 2026-08-05, por `backend-dotnet-senior`)

**Backend: implementado y con la suite unit completa verde (0 errores, 0 tests rotos).**

Construido: tabla `ExchangeRateQuotes` (migración `Adr011_M1_AddExchangeRateQuotes`) + las dos
columnas nuevas de `Invoices`; `IExchangeRateResolver`/`ExchangeRateResolver` (escalera §5.1,
precedencia §4.3, caché §5.2); `IAfipService.GetOfficialExchangeRateAsync` +
`AfipService.IsAuthTicketValid`/`ParseCotizacionResponse`; `ExchangeRateSyncJob` (Hangfire,
`0 15 * * *`); los 4 guards de `InvoiceService.ValidateMultiCurrencyInvoicingAsync` (§8.1/§8.2,
solo para factura de venta genuina — NC/ND siguen heredando sin recotizar); endpoint
`GET /api/exchange-rates/suggestion`. Detalle completo, desvíos declarados y qué falta: ver el
informe de la tanda en el historial de conversación del agente (2026-08-05).

**Frontend de facturar: IMPLEMENTADO Y DEPLOYADO el mismo 2026-08-05 (deploy 2, `71a7e3de`)**:
los dos formularios precargan la sugerencia (hook `useTipoCambioSugerido`), la justificación es
condicional a que el número difiera (lib compartida `exchangeRateSuggestion.js`), y dejaron de
mandar `exchangeRateSource`/`exchangeRateFetchedAt` (la etiqueta la pone el servidor). Verificado
en PROD con keeper (campo precargado + leyenda del motor). Este párrafo reemplaza al aviso viejo
de "NO implementado" que quedó desactualizado.

**Pendiente de verificar en homologación (§14, honestidad):** si `FEParamGetCotizacion` devuelve
cotizaciones reales en homologación, el formato exacto de `FchCotiz`, si `EnableMultiCurrencyInvoicing`
e `IsProduction` están prendidos en el sistema vivo. Sin eso, la tabla puede seguir vacía después del
deploy hasta que el job corra contra un entorno que realmente conteste.

---

---

## 1. Qué se construye, en criollo

Hoy, cuando alguien factura en dólares, escribe el valor del dólar **a mano**, sin que el sistema lo contraste contra nada. Peor: la pantalla le manda al servidor una **etiqueta de origen inventada** — dice "este número salió del Banco Nación" cuando en realidad lo tipeó una persona. Ese origen falso queda pegado a un comprobante con CAE, que no se puede borrar.

Lo que construimos:

1. Una **libreta de cotizaciones** (tabla) donde se anota, todos los días, cuánto dijo ARCA que valía el dólar.
2. Un **cartero automático** (job diario) que le pregunta a ARCA y anota en la libreta.
3. Cuando alguien factura en dólares, la pantalla **le muestra el número de la libreta ya escrito** en el casillero, y le aclara de qué día es. **Puede cambiarlo** — si lo cambia, queda registrado como "a mano" con su explicación.
4. El **servidor** decide y guarda de dónde salió el número. La pantalla deja de opinar sobre eso.

**Lo que NO se construye hoy:** ver §11 (alcance).

---

## 2. Reglas de la constitución que aplican

De `docs/estandares/2026-07-22-constitucion-producto-v1.md` (vinculante). Citar estos números en el commit y en el review:

| Regla | Qué exige acá |
|---|---|
| **P-21** | El sistema **sugiere**, no decide. El casillero del TC queda **siempre editable**. Nunca se bloquea al usuario por falta de sugerencia. |
| **F-4** | El snapshot fiscal lo calcula **el servidor**, no el request. La fuente del TC la determina el backend. |
| **F-5** | Toda operación de plata multi-paso es atómica. |
| **F-6** | Lo que registra plata deja rastro; **nada se borra, se tacha**. Aplica a la corrección de una cotización mal traída (§4.4). |
| **F-15** | La validación fiscal final la da un contador. El sistema no es autoridad. |
| **T-5** | Nombres internos jamás llegan a una respuesta de API ni a un texto de usuario. |
| **T-8** | Se preserva compatibilidad de datos y de API. Migraciones = alto riesgo. |
| **T-11** | **Sin feature flags nuevos.** La obra sale directa. |
| **T-12** | Integración externa **diseñada para el fallo**: timeout, degradación, reconciliación. |
| **T-13** | El front **recibe** lo derivado calculado; no lo deduce. |
| **PR-2** | Decisiones de pantalla/plata/fiscal se le preguntan a Gastón (§13). |

---

## 3. Hechos verificados en el repo (2026-08-05)

Todo lo de esta tabla lo leí en HEAD hoy. Lo que **no** verifiqué está en §14.

| # | Hecho | Ancla |
|---|---|---|
| V1 | `FEParamGetCotizacion` existe en el XSD del repo; `complexType Cotizacion` con `MonId`/`MonCotiz`/`FchCotiz`. | `src/TravelApi.Tests/Resources/wsfev1.xsd:535` y `:558-563` |
| V2 | `ExchangeRateSource`: `Unset=0, BCRA_A3500=1, BNA_Mayorista=2, BNA_Minorista=3, AfipOficial=4, Manual=5, BNA_VendedorDivisa=6`. **`AfipOficial=4` está declarado y NO se usa en ninguna línea de `src/`.** | `src/TravelApi.Domain/Entities/ExchangeRateSource.cs:34` |
| V3 | `InvoiceService` tiene **4 guards** para `MonId != "PES"`: cotización incoherente (`<=0` o `==1`), fuente no nula/no `Unset`, `FetchedAt` no nulo, **justificación siempre obligatoria**. | `src/TravelApi.Infrastructure/Services/InvoiceService.cs:722`, `:733`, `:740`, `:746` |
| V4 | `EnsureAuth` guarda el ticket de producción en **`ProdTokenExpiration`** y el de homologación en `TokenExpiration`. | `src/TravelApi.Infrastructure/Services/AfipService.cs:616-628` |
| V5 | **Bug latente**: `GetStatus` lee `settings.TokenExpiration` sin mirar `IsProduction` → en producción evalúa el campo equivocado. **No copiar esa lógica.** | `AfipService.cs:183-191` |
| V6 | `EnsureAuth` es privado. `IAfipService` ya expone consultas de **sólo lectura** a ARCA: `GetVoucherDetails`, `GetLastAuthorizedNumeroAsync`, `QueryLastAuthorizedWithDetailsAsync`. | `src/TravelApi.Application/Interfaces/IAfipService.cs:20,41,58` |
| V7 | `ArgentinaTime.GetArgentinaNow()` y `GetArgentinaToday()` existen. | `src/TravelApi.Domain/Helpers/ArgentinaTime.cs:82,89` |
| V8 | Scheduler = **Hangfire**. 8 `RecurringJob.AddOrUpdate` dentro de `if (hangfireSchedulerEnabled)`. El "vigía" es `CoherenceWatchdogJob`, `Cron.Daily(6)`. | `src/TravelApi/Program.cs:857-940` |
| V9 | Migraciones EF vivas en `Persistence/Migrations/App/`. Última: `20260803224441_Adr053_DniExpiryAlert_...`. | `src/TravelApi.Infrastructure/Persistence/Migrations/App/` |
| V10 | `DatabaseSchemaUpdater.RunBootstrappersAsync` corre 3 bootstrappers de SQL crudo **antes** de `MigrateAsync`. Uno es `BnaExchangeRateSchemaBootstrapper`. | `src/TravelApi.Infrastructure/Services/DatabaseSchemaUpdater.cs:131,192-207` |
| V11 | `BnaExchangeRateSnapshots` es **singleton: una sola fila** que se pisa. Ése es el defecto de raíz. | `src/TravelApi.Infrastructure/Persistence/BnaExchangeRateSchemaBootstrapper.cs` |
| V12 | Ya existe un contrato "TC sugerido editable" en producción: `GET /api/cancellations/bna-usd-rate?date=` → `200 {rate,rateDate}` / `204`, hook `useBnaUsdRateForDate.js`, texto `textoEstadoDolarBna`. Ventana de walk-back = **5 días** (`RateForDateWindowDays`). | `CancellationsController.cs:1128-1142`; `BnaExchangeRateService.cs:131,138` |
| V13 | **Los dos formularios de factura mandan una fuente inventada**: `payload.exchangeRateSource = EXCHANGE_RATE_SOURCE_BNA_VENDEDOR_DIVISA` sin haber consultado nada. | `EmitirFacturaInline.jsx:764`; `CreateInvoiceModal.jsx:320` |
| V14 | Ambos fronts **exigen justificación siempre** para USD. | `CreateInvoiceModal.jsx:812` (deshabilita el botón); `EmitirFacturaInline.jsx:111-124` (`validarCamposUSD`) |
| V15 | `CanMisMonExtResolver` devuelve `null` para pesos y **`"N"` siempre** para divisa. | `src/TravelApi.Domain/Reservations/CanMisMonExtResolver.cs:36-40` |
| V16 | `Payment` **ya modela el TC comercial**: `Currency`, `ImputedCurrency`, `ExchangeRate`, `ExchangeRateSource`, `ExchangeRateAt`, `ImputedAmount` (ADR-021). | `src/TravelApi.Domain/Entities/Payment.cs:32-66` |
| V17 | `OperatorRefundService` hardcodea `ExchangeRateAtReceipt = 1m`. | `src/TravelApi.Infrastructure/Services/OperatorRefundService.cs:151` |
| V18 | `CreateInvoiceRequest.MonCotiz` tiene default `1m`. | `src/TravelApi.Application/DTOs/CreateInvoiceRequest.cs:54` |
| V19 | El hardcode de TC en `penaltyPayload.js` **ya no existe** (`buildSnapshotData` eliminada 2026-07-16; el archivo tiene 127 líneas y no toca TC). | `src/TravelWeb/src/features/cancellations/lib/penaltyPayload.js` |
| V20 | `EnableMultiCurrencyInvoicing` default `false` en código. Es un setting de base. | `src/TravelApi.Domain/Entities/OperationalFinanceSettings.cs:367` |

---

## 4. Tabla histórica `ExchangeRateQuotes`

Un registro por **moneda + fecha + fuente + entorno**. Migración **aditiva**: no modifica ni borra nada existente (T-8).

### 4.1 Esquema

```sql
CREATE TABLE "ExchangeRateQuotes" (
    "Id"                  integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    "Currency"            character varying(3)      NOT NULL,  -- ISO: "USD" (no el código ARCA "DOL")
    "QuoteDate"           date                      NOT NULL,  -- fecha que PEDIMOS
    "Source"              integer                   NOT NULL,  -- ExchangeRateSource (4=AfipOficial, 3=BNA_Minorista)
    "Rate"                numeric(18,6)             NOT NULL,
    "ProviderName"        character varying(60)     NOT NULL,  -- origen técnico REAL: "ARCA_WSFEv1" | "BNA_Scraper"
    "FetchedAt"           timestamp with time zone  NOT NULL,
    "ArcaFchCotiz"        date                      NULL,      -- fecha que CONTESTÓ ARCA
    "IsProductionSource"  boolean                   NOT NULL,  -- entorno ARCA del que salió el dato
    "SupersededByQuoteId" integer                   NULL,      -- corrección por reemplazo (F-6)

    CONSTRAINT "ck_ExchangeRateQuotes_rate_positive"
        CHECK ("Rate" > 0),
    CONSTRAINT "ux_ExchangeRateQuotes_currency_date_source_env"
        UNIQUE ("Currency","QuoteDate","Source","IsProductionSource"),
    CONSTRAINT "fk_ExchangeRateQuotes_superseded_by"
        FOREIGN KEY ("SupersededByQuoteId") REFERENCES "ExchangeRateQuotes"("Id")
);

CREATE INDEX "ix_ExchangeRateQuotes_lookup"
    ON "ExchangeRateQuotes" ("Currency","IsProductionSource","QuoteDate" DESC);
```

**`QuoteDate` vs `ArcaFchCotiz`** — no son lo mismo y las dos hacen falta. `QuoteDate` es *lo que preguntamos*; `ArcaFchCotiz` es *lo que ARCA contestó*. Para un domingo difieren, y el número defendible ante una inspección es el que ARCA devolvió. El único índice va sobre `QuoteDate`, así que sábado, domingo y lunes pueden ser tres filas apuntando al mismo `ArcaFchCotiz`: es correcto y mantiene el upsert idempotente.

**`IsProductionSource`** — el sistema hoy factura contra **homologación** de ARCA (regla vigente: pruebas en PROD sólo con homologación). Sin esta columna pasaría una de dos cosas malas: o no persistimos nada en homologación y la libreta queda vacía para siempre (feature muerta), o mezclamos números de juguete con números reales y uno de juguete termina citado en un comprobante real. Con la columna, se persiste siempre y **el resolver sólo ofrece filas cuyo entorno coincide con el `AfipSettings.IsProduction` de este momento**. El día que se pase a productivo, la libreta arranca vacía en ese entorno y se llena sola (§14, NB3).

### 4.2 Upsert idempotente

```sql
INSERT INTO "ExchangeRateQuotes" (...) VALUES (...)
ON CONFLICT ("Currency","QuoteDate","Source","IsProductionSource") DO NOTHING;
```

**`DO NOTHING`, nunca `DO UPDATE`.** Razón dura: una cotización publicada de una fecha pasada **no cambia**, y si la fila fuera mutable, el `ExchangeRateQuoteId` que guardó un comprobante dejaría de apuntar al número que ese comprobante usó — se rompería el snapshot (F-4). La fila es **inmutable una vez escrita**. Correr el job diez veces el mismo día no duplica ni altera nada.

### 4.3 Precedencia de lectura

Cuando hay más de una fila para la misma moneda y fecha (porque el respaldo BNA se guardó un día que ARCA falló y ARCA se completó después), el resolver elige con este orden **explícito**:

```
ORDER BY CASE "Source"
           WHEN 4 THEN 0                    -- AfipOficial: la fuente fiscal
           WHEN 3 THEN 1  WHEN 2 THEN 1  WHEN 6 THEN 1   -- BNA_*: respaldo
           ELSE 2
         END,
         "QuoteDate" DESC,
         "Id" DESC
```

Sin este `ORDER BY`, el resultado depende del plan de Postgres y la misma pantalla podría mostrar dos números distintos en dos recargas.

### 4.4 Corrección de una cotización mal traída — por reemplazo, nunca por edición (F-6)

Si algún día hay que corregir una fila (ARCA devolvió mal, o se detecta un error), **está prohibido `UPDATE` y está prohibido `DELETE`**. El procedimiento es:

1. Insertar una **fila nueva** con el valor correcto.
2. Setear `SupersededByQuoteId` de la fila vieja apuntando a la nueva. *(Éste es el único `UPDATE` permitido, y sólo sobre esa columna — no toca `Rate`.)*
3. El resolver **ignora** toda fila con `SupersededByQuoteId IS NOT NULL`.

Así la fila vieja sigue existiendo y un comprobante que la citó sigue pudiendo explicar de dónde sacó su número. Es exactamente el criterio F-6: nada se borra, se tacha.

**La FK desde `Invoice` es `ON DELETE RESTRICT`, no `SET NULL`** (§6.1): una fila citada por un comprobante **no se puede borrar jamás**, y el motor lo impone. `SET NULL` habría dejado que un borrado silencioso le sacara la procedencia a un comprobante con CAE.

### 4.5 Migración

- EF migration nueva en `src/TravelApi.Infrastructure/Persistence/Migrations/App/`, nombre sugerido **`Adr011_M1_AddExchangeRateQuotes`**. Incluye la tabla, el índice, y las dos columnas nuevas de `Invoices` (§6.1).
- **NO tocar `BnaExchangeRateSchemaBootstrapper` ni ningún bootstrapper.** Es tabla nueva, sin filas legacy en PROD. Los bootstrappers corren **antes** de `MigrateAsync` (V10): duplicar ahí lo que crea una migración EF es exactamente la receta del error `42701` (columna duplicada), que ya mordió a este repo.
- Sin backfill. La tabla arranca vacía y se llena hacia adelante.

---

## 5. Resolver — escalera de fallback

`IExchangeRateResolver` en `src/TravelApi.Application/Interfaces/`, implementación en `src/TravelApi.Infrastructure/Services/`. Es **lo único** que consumen los servicios de negocio; ningún servicio habla con un proveedor directo.

```csharp
Task<ExchangeRateSuggestion?> GetSuggestionAsync(
    string currency, DateOnly date, CancellationToken ct);

public record ExchangeRateSuggestion(
    decimal Rate,
    DateOnly RateDate,          // fecha REAL del dato (puede ser anterior a la pedida)
    ExchangeRateSource Source,
    string ProviderName,
    DateOnly? ArcaFchCotiz,
    bool IsStale,               // true = es de otra fecha o viene del respaldo
    int QuoteId);
```

### 5.1 La escalera (camino interactivo)

**El camino interactivo NO le pega a ARCA ni a ninguna red externa. Lee la tabla y nada más.** Ésta es la simplificación de mayor retorno del review, y elimina de un saque: el throttle contra WSAA, los timeouts de 3 s, el riesgo de quemar el ticket de la facturación, y la mitad de la lógica de tickets.

0. **`ARS` / `PES`** → `Rate = 1`. Sin base, sin red. Corta acá.
1. **Match exacto en la tabla**: `Currency`, `QuoteDate = date`, `IsProductionSource = settings.IsProduction`, `SupersededByQuoteId IS NULL`, con la precedencia de §4.3. HIT → devolver con `IsStale = false`.
2. **Walk-back**: misma consulta con `QuoteDate <= date`, orden desc, **ventana ≤ 5 días** (reusar el `RateForDateWindowDays = 5` que ya existe en `BnaExchangeRateService.cs:131`; no inventar otro número). HIT → devolver con `IsStale = true` y el `RateDate` **real**.
3. **Nada** → devolver `null`. El endpoint responde `204` y la pantalla cae a **carga manual**. Nunca se inventa un número, nunca se traba al usuario (P-21).

**Días no hábiles = caminar hacia atrás**: se resuelve solo en el paso 2. Un domingo no hay fila propia → devuelve la del viernes con su fecha real, y la leyenda lo dice. No hace falta calendario de feriados.

**Consecuencia honesta de esta simplificación:** para una fecha que el job nunca cubrió (y que queda fuera de los 5 días de walk-back), **no hay sugerencia** — el usuario carga a mano, como hoy. Es aceptable: el job va a cubrir todos los días hacia adelante, y cargar a mano es exactamente el comportamiento actual. No se pierde nada; se gana en los casos normales.

### 5.2 Caché

`IMemoryCache` (ya está inyectado en el proyecto), clave `fx:{currency}:{isProduction}:{date:yyyy-MM-dd}`:

- Fechas **pasadas** (`date < hoyArgentina`): TTL 12 h — el dato es inmutable.
- **Hoy** (`date == hoyArgentina`): TTL 30 min — el job puede haberlo escrito recién.

Cachear también el **miss** (`null`) con TTL corto (5 min) para que una fecha sin dato no golpee la base en cada tecla.

### 5.3 "Hoy" es siempre hora argentina (obligatorio, 4 lugares)

Usar **`ArgentinaTime.GetArgentinaToday()` / `GetArgentinaNow()`** (V7). **Nunca `DateTime.Today`, nunca `DateTime.UtcNow.Date`.** Entre las 21:00 y las 24:00 hora argentina, UTC ya está en el día siguiente: usar UTC haría que a las 21:30 el sistema pida la cotización de mañana, que no existe, y la pantalla quede sin sugerencia todas las noches.

Los cuatro lugares donde aplica:

1. **`QuoteDate` que escribe el job** (§7.2).
2. **La rama "sólo hoy"** de cualquier decisión de frescura.
3. **Los TTL de caché** (§5.2), al decidir si la fecha es pasada o es hoy.
4. **El endpoint** (§9), al interpretar un `date` ausente como "hoy".

**Test obligatorio:** con el reloj fijado a las **23:30 ART** (= 02:30 UTC del día siguiente), el resolver debe pedir y devolver la fecha **argentina**, no la UTC.

---

## 6. Snapshot del TC en cada documento

### 6.1 Factura (`Invoice`)

**Ya existen y no se tocan:** `MonId`, `MonCotiz` (el TC fiscal congelado), `ExchangeRateSource`, `ExchangeRateFetchedAt`, `ExchangeRateJustification`.

**Se agregan dos columnas nullable** en la misma migración `Adr011_M1`:

```sql
ALTER TABLE "Invoices"
    ADD COLUMN "ExchangeRateQuoteId"  integer NULL,
    ADD COLUMN "ExchangeRateFchCotiz" date    NULL;

ALTER TABLE "Invoices"
    ADD CONSTRAINT "fk_Invoices_exchange_rate_quote"
    FOREIGN KEY ("ExchangeRateQuoteId")
    REFERENCES "ExchangeRateQuotes"("Id")
    ON DELETE RESTRICT;   -- una fila citada por un comprobante NO se borra jamás (§4.4)
```

- `ExchangeRateQuoteId`: puntero de procedencia — "este comprobante usó exactamente esta fila". `NULL` cuando el TC fue manual o el comprobante es viejo.
- `ExchangeRateFchCotiz`: el `FchCotiz` que devolvió ARCA. Es el dato que defiende el número ante una inspección.

Quién los llena: **el servidor**, en `InvoiceService` (F-4). Nunca el request.

### 6.2 Alcance del puntero de procedencia (explícito)

- El puntero `ExchangeRateQuoteId` **existe sólo en `Invoice`**. No se agrega a `FiscalSnapshot`, ni a `Payment`, ni a ninguna otra entidad en esta obra.
- **Nota de crédito:** sigue como está — **copia `MonCotiz` del comprobante original y hereda de él `ExchangeRateSource` y `ExchangeRateQuoteId`**; no re-cotiza y no consulta el resolver. Es una línea de comportamiento que ya está construida así y que esta obra **no debe romper**.

### 6.3 Sobre el "par fiscal / comercial"

No van los dos en la misma fila y **el comercial ya está construido**. Una factura tiene **un** TC fiscal y **N** cobros, cada uno con el TC del día que entró la plata:

- **TC fiscal** → `Invoice.MonCotiz` + `ExchangeRateSource` + `ExchangeRateFetchedAt` + `ExchangeRateFchCotiz` + `ExchangeRateQuoteId`.
- **TC comercial** → `Payment.ExchangeRate` / `ExchangeRateSource` / `ExchangeRateAt` / `ImputedCurrency` / `ImputedAmount` (V16, ADR-021, **ya existen**).

La **diferencia de cambio** queda derivable, pero su **tratamiento contable sigue fuera de alcance**: no hay módulo de asientos en el repo.

### 6.4 `CanMisMonExt = "S"` — contemplado, no construido

`CanMisMonExtResolver` sigue devolviendo `"N"` siempre (V15). **No se toca.** El modelo ya banca el caso futuro **sin cambio de esquema**: el día que se prenda `"S"`, no se manda `MonCotiz` (ARCA lo asigna), se leen `MonCotiz` y `FchCotiz` **de la respuesta** de `FECAESolicitar`, y se congelan en `Invoice.MonCotiz` + `Invoice.ExchangeRateFchCotiz` con `Source = AfipOficial` y `ExchangeRateQuoteId = NULL` (correcto: ese número no salió de nuestra tabla). Cero migración adicional. El cambio sería en el armado del envelope y el parseo de `AfipService` — camino de emisión, por eso no es de hoy.

---

## 7. Job diario — el único que le pega a ARCA

### 7.1 Registro

`ExchangeRateSyncJob` en `src/TravelApi.Infrastructure/Services/`, registrado junto a los demás en `Program.cs`, dentro de `if (hangfireSchedulerEnabled)` (V8):

```csharp
RecurringJob.AddOrUpdate<ExchangeRateSyncJob>(
    "exchange-rate-sync",
    job => job.RunAsync(CancellationToken.None),
    "0 15 * * *");
```

**15:00 UTC ≈ 12:00 ART, deliberadamente no las 6am del vigía.** 6am UTC son las 3am en Argentina: la cotización del día todavía no está publicada y el job traería siempre el día anterior. Lo que se reusa es el **mecanismo** (Hangfire `RecurringJob`), que es lo que corresponde; la hora la manda el negocio.

**Sin flag (T-11).** El job sale prendido. No existe `EnableAuthoritativeExchangeRate`; esa parte de ADR-011 queda anulada.

### 7.2 Qué hace cada corrida (para `USD`)

1. Upsert de **hoy** (`ArgentinaTime.GetArgentinaToday()`, §5.3) desde ARCA.
2. **Backfill de huecos** de los últimos **7 días**: para cada fecha sin fila, pedir a ARCA. El `DO NOTHING` hace esto trivial e idempotente. Ésta es la reconciliación que pide T-12: si el job no corrió o ARCA estuvo caído, **al día siguiente se auto-repara solo**.
3. Si ARCA falla para hoy, intenta el **respaldo BNA** (`BnaExchangeRateService`, que ya existe) sólo para hoy, y lo persiste con `Source = BNA_Minorista`, `ProviderName = "BNA_Scraper"`.
4. **Nunca tira excepción hacia afuera.** Un job que explota deja de correr y nadie se entera. Cualquier fallo se loguea `Warning` con moneda/fecha/proveedor y **sin** token, sign ni CUIT.

### 7.3 El proveedor ARCA

Se agrega **un método a `IAfipService`**. No se extrae un cliente SOAP, no se crea un `HttpClient` nuevo.

```csharp
// src/TravelApi.Application/Interfaces/IAfipService.cs
Task<ArcaExchangeRate?> GetOfficialExchangeRateAsync(
    string monId, DateOnly fchCotiz, CancellationToken ct);

public record ArcaExchangeRate(string MonId, decimal MonCotiz, DateOnly FchCotiz);
```

Implementado en `AfipService` reusando lo que ya está: selección de URL `WsfeUrlProd`/`WsfeUrlDev`, `GetAuthToken`/`GetAuthSign` (`:72-80`), `settings.Cuit`, y el mismo patrón de envelope-por-string + parseo con `XDocument` que usa `FECAESolicitar` (`:1539-1607`).

**Por qué un método en la interfaz y no un refactor:** `AfipService` es el archivo de mayor riesgo del repo (~2.700 líneas, emite comprobantes con CAE). Extraer un cliente SOAP para una consulta de lectura es riesgo puro sin beneficio. Además hay **precedente directo**: `GetVoucherDetails`, `GetLastAuthorizedNumeroAsync` y `QueryLastAuthorizedWithDetailsAsync` ya son consultas de sólo lectura colgadas de esta misma interfaz (V6).

Detalles:
- `SOAPAction: "http://ar.gov.afip.dif.FEV1/FEParamGetCotizacion"`.
- `MonId = "DOL"`; `FchCotiz` en `yyyyMMdd` (mismo formato que `CbteFch`). **A confirmar contra homologación** (§14).
- Timeout **15 s** con **2 reintentos con backoff** — es el job, no hay nadie esperando.
- Devuelve `null` ante cualquier fallo, timeout, o `Errors` no vacío en la respuesta. **Nunca propaga excepción** al job.

### 7.4 Guard del ticket de acceso — helper nuevo, no copiar `GetStatus`

```csharp
private static bool IsAuthTicketValid(AfipSettings settings)
{
    var expiration = settings.IsProduction
        ? settings.ProdTokenExpiration
        : settings.TokenExpiration;

    return expiration.HasValue && expiration.Value > DateTime.UtcNow;
}
```

**Esto es obligatorio y hay que hacerlo bien.** `EnsureAuth` guarda el ticket de producción en `ProdTokenExpiration` y el de homologación en `TokenExpiration` (V4). **`GetStatus` (`:183-191`) lee `TokenExpiration` sin mirar `IsProduction`** — en producción evalúa el campo equivocado (V5). Es un **bug latente existente**: no copiar esa lógica, y **no arreglarlo dentro de esta obra** (ver §15, ticket aparte).

Uso: dentro de `GetOfficialExchangeRateAsync`, si `!IsAuthTicketValid(settings)` → `await EnsureAuth(settings)` y seguir. Como **sólo el job** llama a este método (§5.1), no hay riesgo de ráfaga contra WSAA ni de quemar el ticket que necesita la facturación.

### 7.5 Parseo y validación antes de persistir

- El `MonCotiz` de ARCA viene como `s:double` en XML: parsear **siempre con `CultureInfo.InvariantCulture`**. Con la cultura del servidor, `"1234.56"` podría leerse como `123456`. Un error de escala de 100× en un comprobante fiscal.
- **Antes de persistir**, rechazar `Rate <= 0` y `Rate == 1` para moneda extranjera, y loguear `Warning`. Es el mismo criterio que ya aplican los guards de `InvoiceService` (V3) y de la NC; sin esto, un `1` de ARCA entraría a la libreta y de ahí a un comprobante.

---

## 8. Cambios en la emisión de factura USD

### 8.1 Los 4 guards de `InvoiceService` (V3)

Los cuatro guards actuales para `MonId != "PES"`:

| Línea | Guard hoy | Qué pasa a ser |
|---|---|---|
| `:722` | `MonCotiz <= 0 \|\| == 1` → error | **Sin cambios.** Sigue validando el número que llega. |
| `:733` | `ExchangeRateSource` nula o `Unset` → error | Valida el **valor resuelto por el servidor**, no el del request. |
| `:740` | `ExchangeRateFetchedAt` nulo → error | Valida el **valor resuelto por el servidor**, no el del request. |
| `:746` | Justificación vacía → error **siempre** | Pasa a ser **condicional: sólo si `Source == Manual`**. |

**Por qué `:746` tiene que volverse condicional:** si el sistema ahora sugiere el número oficial y el usuario lo acepta tal cual, **no hay nada que justificar** — el origen es ARCA y quedó registrado. Exigirle una explicación escrita por usar el número que el propio sistema le propuso es fricción sin valor, y empuja a la gente a escribir "ok" para poder avanzar, que es peor que no pedir nada. La justificación conserva todo su sentido en el único caso donde importa: **cuando el usuario pisó el número sugerido**.

### 8.2 Cómo el servidor determina `Source` (regla de igualdad exacta)

En `InvoiceService`, para `MonId != "PES"`:

1. Llamar al resolver con la **fecha de emisión** del comprobante y la moneda.
2. Comparar `request.MonCotiz` con `suggestion.Rate` por **igualdad decimal EXACTA**.
   - **Iguales** → `Source = AfipOficial` (o la del respaldo), `ExchangeRateFetchedAt = suggestion.FetchedAt`, `ExchangeRateQuoteId = suggestion.QuoteId`, `ExchangeRateFchCotiz = suggestion.ArcaFchCotiz`. **Justificación no requerida.**
   - **Distintos, o no hubo sugerencia** → `Source = Manual`, `ExchangeRateQuoteId = NULL`, `ExchangeRateFetchedAt = ahora`. **Justificación obligatoria** (INV-120).

**Igualdad EXACTA, sin tolerancia y sin `round(2)`.** Nada de "si difieren en menos de X centavos lo tomamos por bueno". La razón: cualquier tolerancia significa etiquetar como "número oficial de ARCA" un número que **no es** el de ARCA, que es precisamente el problema que esta obra vino a cerrar (V13). No hace falta tolerancia porque **el front precarga el valor exacto de la sugerencia**: si el usuario no lo toca, empata solo. Si lo toca, es una decisión deliberada y merece quedar marcada como manual. La columna es `numeric(18,6)` y el front manda el mismo decimal que recibió, así que el camino normal empata sin trampa.

### 8.3 Los fronts dejan de inventar el origen

Los dos formularios (V13) hoy mandan `exchangeRateSource` y `exchangeRateFetchedAt` inventados. **Dejan de mandarlos.** Mandan sólo `monId`, `monCotiz` y —cuando corresponde— `exchangeRateJustification`. El servidor decide el resto (F-4, T-13).

**Los dos** fronts deben cambiar el gate de justificación **en el mismo deploy** que el backend (V14):

- `CreateInvoiceModal.jsx:812` — la condición que deshabilita "Emitir" incluye `!exchangeRateJustification.trim()` incondicionalmente. Pasa a exigir justificación **sólo cuando el usuario editó el TC sugerido**.
- `EmitirFacturaInline.jsx:111-124` (`validarCamposUSD`) — misma corrección: la rama `if (!String(justificacion ?? "").trim())` pasa a ser condicional al mismo criterio.

> Si se cambia el backend sin los fronts, el usuario queda **trabado**: la pantalla le exige una justificación que el backend ya no pide, y no hay forma de avanzar sin escribir texto de relleno. Si se cambian los fronts sin el backend, el backend rechaza la emisión por falta de justificación. **Van juntos** — ver el orden de deploy en §10.

### 8.4 `CreateInvoiceRequest.MonCotiz = 1m` — NO SE TOCA

El default `1` es **correcto** para `MonId = "PES"` y lo usan todos los llamadores en pesos (FC1.2, NC total). Cambiarlo rompe compatibilidad de API sin ganar nada (T-8). El agujero nunca fue el default: era que en USD nadie contrastaba el número, y eso lo cierra §8.2 del lado del servidor. Los guards existentes ya impiden que el `1` se cuele en una factura USD.

---

## 9. Contrato de API

Endpoint nuevo, en un `ExchangeRatesController` (controller fino: valida entrada, llama al resolver, mapea la respuesta).

```
GET /api/exchange-rates/suggestion?currency=USD&date=2026-08-05
[RequirePermission(Permissions.ReservasView)]
```

`date` opcional; si falta, es **hoy en hora argentina** (§5.3).

**200:**
```json
{
  "tipoCambio": 1234.500000,
  "fecha": "2026-08-04",
  "esDeOtraFecha": true,
  "leyenda": "Dólar oficial del 4 de agosto. Si ponés otro número, lo tomamos a mano."
}
```

**204** — no hay dato útil. La pantalla muestra el casillero vacío y "Escribí el tipo de cambio a mano". **Sin toast de error**: es un caso esperado, no una falla.
**400** — `currency` inválida.

Reglas del contrato:

- **T-5**: la respuesta **no** lleva enteros de enum, ni `ProviderName`, ni `QuoteId`, ni `IsStale`, ni `IsProductionSource`, ni nombres de clase/tabla. `esDeOtraFecha` es un concepto de negocio, no interno.
- **T-13**: la **`leyenda` la arma el servidor**. El front no deduce el texto ni compara fechas.
- **P-21**: es una sugerencia. El campo queda siempre editable.
- **T-8**: `GET /api/cancellations/bna-usd-rate` **sigue vivo con la misma forma de respuesta**, delegando internamente al nuevo resolver. La pantalla de multas no se toca y gana precisión gratis (deja de depender del singleton).

**Front:** generalizar `useBnaUsdRateForDate` → `useTipoCambioSugerido(currency, date, {enabled})`, **conservando** el debounce de 300 ms y las dos capas anti-respuesta-tardía que ya tiene (cleanup por closure + `debeAplicarRespuestaBna`). Consumirlo en `EmitirFacturaInline.jsx` y `CreateInvoiceModal.jsx` cuando la moneda es USD, con la fecha de emisión del comprobante. El valor recibido se **precarga** en el casillero (requisito de §8.2).

> ⚠️ **Gate UX obligatorio.** Esto toca pantallas (casillero precargado + leyenda nueva en dos formularios de factura). Antes de que `frontend-senior` escriba una línea: `ux-ui-disenador`, y las preguntas van a Gastón.

---

## 10. Orden de deploy

El backend tiene que **tolerar las dos formas de request** antes de que cambien los fronts. Si no, cualquier ventana entre deploys deja la emisión USD rota.

1. **Deploy 1 — backend, compatible hacia atrás.**
   Migración `Adr011_M1` + tabla + resolver + job + endpoint nuevo + cambios de `InvoiceService`. El backend **ignora** `exchangeRateSource` y `exchangeRateFetchedAt` si vienen en el request (los fronts viejos los siguen mandando) y **resuelve todo él**. La justificación pasa a condicional. Con los fronts viejos todo sigue funcionando: mandan justificación siempre, y el backend la acepta aunque no la exija.
2. **Esperar una corrida del job** (o dispararlo a mano desde el panel de Hangfire) para que la tabla tenga al menos una fila. Verificar que hay datos **antes** de tocar las pantallas.
3. **Deploy 2 — fronts.** Los dos formularios dejan de mandar el origen inventado, consumen el endpoint, precargan el número y hacen condicional el gate de justificación.

Cada deploy se verifica en el navegador contra PROD antes de pasar al siguiente.

---

## 11. Alcance — qué NO entra en esta obra

| Fuera de alcance | Por qué |
|---|---|
| **`OperatorRefundService.cs:151` (`ExchangeRateAtReceipt = 1m`)** | **Se recorta a obra aparte.** Depende de una decisión de negocio sin firmar (con qué fecha se toma el TC de la plata que entra: la del depósito, la de la acreditación, la de la imputación) y toca el circuito de reembolso del operador. Meterlo acá mezcla dos riesgos distintos en un deploy. **Queda anotado como obra propia.** |
| `penaltyPayload.js` | El hardcode **ya no existe** (V19). Nada que hacer. No abrir el archivo. |
| `CreateInvoiceRequest.MonCotiz = 1m` | Se queda (§8.4). |
| Re-cotizar la NC | La NC copia `MonCotiz` del original y hereda `Source`/`QuoteId` (§6.2). No se toca. |
| `CanMisMonExt = "S"` | Contemplado en el modelo, no construido (§6.4). |
| argentinadatos.com / dolarapi.com / BCRA | No se construyen (decisión D-4). ARCA + respaldo BNA + manual alcanzan. |
| Diferencia de cambio / asientos contables | No hay módulo de asientos en el repo. |
| Arreglar el bug de `GetStatus` (V5) | Ticket aparte (§15). |
| "Neteo fase 2" | Obra propia. Reabre la invariante firmada B2 y el neteo del `RefundCap`; el propio diseño `docs/architecture/2026-07-13-fixb-multa-moneda-cruzada-diseno.md` §7 la clasifica como "tanda propia con firma fiscal". **Depende** de esta obra, no al revés. |
| "Deshacer ND-based" | **Ya está construido** (entidad `BookingCancellationDebitNoteAnnulment`, migración `20260714051653`, servicio `DebitNoteAnnulmentReconciliation`, endpoint `POST /api/cancellations/{publicId}/undo-debit-note`). Su NC hereda `MonId`/`MonCotiz` de la ND. Nada que decidir. |

---

## 12. Enum, rollback y ADR-011

### 12.1 Enum: no se agrega ningún valor

Se usa **`AfipOficial = 4`**, que está declarado y **no se usa en ninguna línea de `src/`** (V2). Su documentación ya dice *"TC oficial publicado por AFIP/ARCA para liquidaciones"*, que con este diseño pasa a ser **literalmente cierto**. El respaldo usa `BNA_Minorista = 3`, también honesto y existente.

**No agregar `OficialPorApi = 7` ni ningún valor nuevo.** No hace falta (sólo servía para etiquetar un proxy tipo argentinadatos, que no se construye), y carga un riesgo gratuito: **no leí el cuerpo completo** del CHECK `chk_BookingCancellations_fiscalsnapshot_consistent` (sólo verifiqué que exige `FiscalSnapshot_Source <> 0`), y si enumerara enteros permitidos, un valor 7 rompería inserts.

La etiqueta honesta para una inspección sale del **trío**, no del nombre del enum: `Source = AfipOficial` + `ProviderName = "ARCA_WSFEv1"` + `ArcaFchCotiz`.

### 12.2 Rollback

- **Comportamiento:** no hay flag (T-11). El rollback es revertir el commit. Como el backend del Deploy 1 es compatible hacia atrás, revertir el Deploy 2 (fronts) sola es seguro.
- **Esquema:** la migración es aditiva. Revertirla = drop de `ExchangeRateQuotes` + drop de las dos columnas de `Invoices`. **Cuidado**: con la FK `RESTRICT`, si ya hay facturas citando filas, hay que limpiar `Invoices.ExchangeRateQuoteId` antes del drop. Ningún comprobante depende de la tabla **para existir**: el TC ya quedó materializado en `Invoice.MonCotiz`. Se pierde la procedencia, no el número.

### 12.3 ADR-011

En **el mismo commit** que la implementación, marcar `docs/architecture/adr/ADR-011-authoritative-exchange-rate-source.md` como **enmendado por esta spec**, con nota al principio indicando los tres cambios de fondo: sin flag (T-11), fuente ARCA `FEParamGetCotizacion`, camino interactivo sin llamadas externas. Si no, el próximo que lea el ADR va a construir el flag y el resolver multi-proveedor que este documento descarta.

---

## 13. Decisiones que necesitan a Gastón (PR-2)

Cada una con una **recomendación única**.

**D-1 — ¿Qué se muestra cuando ARCA no trajo el dólar de ese día?**
*Recomendación:* mostrar el **último conocido dentro de 5 días**, con la leyenda diciendo de qué día es (*"Dólar oficial del 4 de agosto"*). Si no hay nada, casillero vacío y "escribilo a mano". Nunca un cartel de error: no es una falla, es que ese día no se publicó.

**D-2 — ¿Con qué fecha se pide el dólar de una factura: la de emisión o el día hábil anterior?**
*Recomendación:* **la fecha de emisión del comprobante**, y que ARCA decida con su propio `FchCotiz` (viene en la respuesta y lo guardamos). Es su número contra su propia validación: es lo que menos rebota. *Confirmación del contador pendiente (F-15).*

**D-3 — ¿Se avisa en pantalla que el dólar sugerido es de otra fecha?**
*Recomendación:* **sí, en la leyenda debajo del casillero**, con el mismo tono que ya usa la pantalla de multas. Sin cartel amarillo ni modal: es información, no un problema.

**D-4 — ¿Sumamos argentinadatos.com u otra fuente además de ARCA?**
*Recomendación:* **no**. Con ARCA + el scraper BNA que ya existe + carga manual, la escalera está cubierta. Un origen más es una dependencia externa más para mantener sin resolver ningún problema real. Si en homologación resulta que ARCA no sirve fechas viejas, se reabre.

**D-5 — ¿Cuándo el usuario escribe un TC distinto del sugerido, se lo frena?**
*Recomendación:* **no se frena**. Se acepta, se marca como "a mano" y se le pide la explicación escrita que ya se pide hoy. Nada de bandas ni topes nuevos: el sistema sugiere, no decide (P-21), y ARCA tiene su propia validación que rebota lo imposible.

> **Anotada aparte, fuera de esta obra:** el TC de la plata que devuelve el operador (`OperatorRefundService`, hoy clavado en 1). Necesita una decisión previa de Gastón + contador sobre **con qué fecha** se valúa esa plata. Es **obra propia**, no entra hoy (§11).

---

## 14. Pendientes de verificación (honestidad)

Nada de esta spec está implementado ni probado. **No corrí ningún test.** Lo que además **no pude verificar**:

1. **`FEParamGetCotizacion` en homologación**: si devuelve una cotización real o un valor de juguete, si acepta `FchCotiz` de fechas pasadas arbitrarias, y qué devuelve para un día no hábil. **Es lo primero a probar.** De ahí depende cuánto se usa el walk-back y si D-4 se reabre.
2. **Formato exacto de `FchCotiz`** en el request (asumí `yyyyMMdd` por analogía con `CbteFch`).
3. **Si `EnableMultiCurrencyInvoicing` está prendido en PROD**: el default en código es `false` (V20) pero es un setting de base y no tengo acceso a la base del VPS. **Si está apagado, toda la validación USD de `InvoiceService` es no-op y esta obra no se ve hasta prenderlo.**
4. **Si `AfipSettings.IsProduction` es `true` o `false` en el sistema vivo.** Determina qué entorno se persiste en `IsProductionSource` y, por lo tanto, qué ve el usuario el día uno.
5. **El cuerpo completo del CHECK `chk_BookingCancellations_fiscalsnapshot_consistent`.** Verifiqué que exige `FiscalSnapshot_Source <> 0`; no leí si enumera valores. Esta spec **no agrega valores al enum**, así que en principio no lo afecta.

### Qué se ve el día uno (expectativa realista)

Después del Deploy 1 la tabla está **vacía** hasta que el job corra a las 15:00 UTC. Después de esa corrida habrá **una fila** (hoy) más lo que consiga del backfill de 7 días — **si ARCA responde en el entorno configurado**. Si el sistema apunta a homologación y homologación no devuelve cotizaciones útiles (pendiente 1), **la tabla puede quedar vacía y la pantalla no va a sugerir nada**: se comporta exactamente como hoy (carga manual), sin romper nada. **No reportar la obra como "funcionando" hasta ver una fila real en la tabla y el número precargado en el navegador contra PROD.**

---

## 15. Tests obligatorios

Unitarios (InMemory + mocks) salvo donde diga integración; los de integración corren contra Postgres en el VPS, no local.

**Resolver**
1. `ARS` → devuelve `1` sin tocar la base.
2. Match exacto → `IsStale = false`, `RateDate == QuoteDate`.
3. Sin fila del día, sí de 3 días atrás → `IsStale = true` y `RateDate` real.
4. Sin fila dentro de 5 días → `null`.
5. **Precedencia (§4.3)**: con fila `AfipOficial` y fila `BNA_Minorista` para el mismo día, devuelve la `AfipOficial`.
6. **Entorno**: con `IsProduction = true`, no devuelve filas de `IsProductionSource = false`.
7. **Supersede**: fila con `SupersededByQuoteId` no nula **no** se devuelve.
8. **23:30 ART (§5.3)**: con el reloj a las 23:30 hora argentina, la fecha resuelta es la argentina, no la UTC.

**Job**
9. Idempotencia: dos corridas seguidas no duplican filas **ni alteran `Rate`**.
10. ARCA devuelve `Errors` no vacío → degrada sin excepción y el job termina OK.
11. ARCA caído → intenta respaldo BNA, persiste con `Source = BNA_Minorista`.
12. Backfill sólo llena huecos; no pisa filas existentes.
13. **`Rate` inválido** (`0`, `1`, negativo) → **no se persiste** y se loguea.
14. **Parseo `InvariantCulture`**: `"1234.56"` → `1234.56`, nunca `123456`.
15. **Ticket**: con `IsProduction = true` y `ProdTokenExpiration` vencida pero `TokenExpiration` vigente, `IsAuthTicketValid` devuelve **false** (es la trampa del bug V5).

**Emisión**
16. Factura USD con `MonCotiz` **exactamente igual** a la sugerencia → `Source = AfipOficial`, `ExchangeRateQuoteId` seteado, **sin** justificación requerida.
17. Factura USD con `MonCotiz` distinto (aunque sea por 0.000001) → `Source = Manual` y **justificación exigida**.
18. Factura USD sin sugerencia disponible → `Source = Manual` + justificación exigida.
19. El request manda `exchangeRateSource` inventado → **el servidor lo ignora** y pone el suyo.
20. **No-regresión ARS**: factura en pesos con payload y `MonCotiz` byte-idénticos a antes.
21. **NC**: sigue copiando `MonCotiz` del original y heredando `Source`/`QuoteId`; no llama al resolver.

**API**
22. `204` cuando no hay dato; el cuerpo no lleva enteros de enum, ni `ProviderName`, ni `QuoteId` (T-5).
23. `GET /api/cancellations/bna-usd-rate` conserva la forma de respuesta (T-8).

---

## 16. Tickets aparte que salen de esta obra

1. **Bug latente `GetStatus` (V5)**: `AfipService.cs:183-191` lee `settings.TokenExpiration` sin mirar `IsProduction`, y `EnsureAuth` guarda el de producción en `ProdTokenExpiration`. En productivo, el diagnóstico de estado de ARCA evalúa el campo equivocado. **No se arregla dentro de esta obra** (toca el diagnóstico del circuito fiscal y merece su propia verificación), pero queda registrado. El helper `IsAuthTicketValid` de §7.4 hace lo correcto y puede ser la base del arreglo.
2. **TC de la plata del operador** (`OperatorRefundService.cs:151`, §11): obra propia, requiere decisión de Gastón + contador sobre la fecha de valuación.
3. **"Neteo fase 2"**: obra propia con firma fiscal (§11).

---

## 17. Reviewers antes de mergear

`software-architect-reviewer` (ya dictaminó: aprobada con cambios; estos cambios están incorporados) → `backend-dotnet-reviewer` → `security-data-risk-reviewer` (toca facturas y TC fiscal) → `data-exposure-reviewer` (**gate obligatorio**: hay endpoint nuevo, o sea superficie de API nueva) → `qa-automation-senior` / `qa-automation-reviewer`.

Para la definición fiscal: `travel-agency-accountant-argentina` (D-2, y confirmación de que el TC de `FEParamGetCotizacion` es el fiscalmente correcto para facturar en USD — F-15).
