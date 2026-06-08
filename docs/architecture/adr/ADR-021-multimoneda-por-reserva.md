# ADR-021: Multimoneda en TODO el sistema (totales separados por moneda + cobro cruzado con tipo de cambio)

- Estado: **Accepted / Ready to build**. Resueltos los 4 bloqueantes del primer review (B1/B2/B3/B4, ver §0bis) **y el bloqueante B5 + el riesgo de alcance de backfill de la segunda re-revision** (ver §0ter). El diseno esta cerrado para construir por capas (§9). **Antes de habilitar el frontend (paso 7)** siguen pendientes, como gates de ejecucion (no de diseno): `ux-ui-disenador` (todas las vistas de plata, contra `docs/ux/guia-ux-gaston.md`) y el set de items "requiere contador matriculado" (§13/§15.10). NO implementado.
- Fecha: 2026-06-08 (ampliado el mismo dia con 2 decisiones del dueno + criterio del contador; 3ra ampliacion: eje DEUDA CON PROVEEDORES/OPERADORES §15; segunda vuelta: resolucion de los 4 bloqueantes del review — persistencia por moneda en tabla hija, top-N por moneda, reversa cruzada, precision/convencion TC; **tercera vuelta: B5 — consolidacion de los TRES puntos de escritura del escalar en `ReservaMoneyPersister.PersistAsync` — + endurecimiento del alcance del backfill a `saldo != 0` sin filtro de estado**)
- Autor: software-architect
- Relacionados: ADR-020 (ciclo de vida de la reserva), ADR-012 (multimoneda en facturacion: `Invoice.MonId`/`MonCotiz`/`ExchangeRateSource`), FC1.3.F2.5 (`ArcaCurrencyMapper`), FC1 (ADR-002, `FiscalSnapshot` + `ExchangeRateSource`)

---

## 0. Cambios respecto de la version "Propuesto" (registro de la ampliacion)

Esta version reemplaza dos puntos que la version anterior dejaba como deuda diferida:

1. **DECISION 1 DEL DUENO — cobro cruzado con TC**: el cliente puede pagar en una moneda distinta a la del saldo (ej. pagar en pesos un saldo en dolares). Era R-7 "caso futuro / pendiente contador". Ahora se disena (§2.7), con criterio conservador validado por el contador. El pago siempre declara su moneda y monto reales; si difiere de la moneda del saldo imputado, TC + fuente + fecha son obligatorios y se persisten, reusando el patron `ExchangeRateSource` ya existente.
2. **DECISION 2 DEL DUENO — alcance "todo de una"**: las dos monedas tienen que funcionar en TODO el sistema, no solo en la reserva. Era R-2 "reportes/tesoreria/cuenta corriente suman crudo, deuda posterior". Ahora se mapea cada punto que hoy suma plata sin separar moneda y se disena su adaptacion (§4 y §6).
3. **DECISION 3 DEL DUENO — el otro lado del mostrador**: la DEUDA CON PROVEEDORES/OPERADORES (cuentas por pagar de la agencia) tambien se separa por moneda, en el MISMO trabajo. El costo (`NetCost`) de cada servicio ya lleva la moneda del servicio (`Currency`), asi que lo que la agencia le debe al operador por ese servicio ya esta en esa moneda; falta que el pago saliente al proveedor declare su moneda (+ TC cruzado, igual que el cobro del cliente) y que la cuenta corriente / alertas / tesoreria de egresos / exports de proveedor dejen de sumar monedas mezcladas. Se disena en **§15** (eje proveedor), reusando exactamente el patron del eje cliente. Esto **cierra** el item que la version anterior dejo "Abierto — definir alcance con el dueno" en §13.

Ademas se responde la **autocritica** de la version anterior (§5): el surrogate escalar de `Balance` enga​na a los consumidores que lo leen como MONTO adeudado (no como flag). Hay al menos uno confirmado (`CustomerService.CurrentBalance`) y se listan todos.

## 0bis. Resolucion de los 4 bloqueantes del primer review (segunda vuelta)

| # | Bloqueante | Como se resuelve | Donde |
|---|---|---|---|
| **B1** | "PorMoneda solo on-read sin columnas" inviable para agregados cross-reserva en SQL (CustomerService, ReportService, TreasuryService, AlertService suman/ordenan en SQL) | Se **persiste** el detalle por moneda en **tabla hija materializada** `ReservaMoneyByCurrency` (cliente) y `SupplierBalanceByCurrency` (proveedor). Reabierta y elegida la Alternativa C; rechazada B (columnas por moneda) con justificacion. Escalar `Balance`/`CurrentBalance` queda solo de semaforo para los lectores booleanos. Los `Sum`/`OrderBy`/`Where` por moneda corren en SQL contra la hija. | §2.3.0, §2.3, §5.2, §15.3, §14 (C elegida), §7 |
| **B2** | Top-N por escalar mezcla USD+ARS (ReportService top-5 deudoras, AlertService top-10 proveedores) | Top-N **por moneda** contra la tabla hija: dos listas (USD y ARS) ordenadas dentro de cada moneda, indice `(Currency, Balance)`. DTO gana campo `Currency`. | §6bis, §4.4 |
| **B3** | Reversa/anulacion de pago cruzado no disenada | La reversa es **self-healing** via recalculo (soft-delete + `Recalculate...`): el saldo de `ImputedCurrency` sube por `ImputedAmount`, la caja de `Currency` baja por `Amount`. Editar un pago cruzado se **rechaza** (anular+recrear). **Bug latente corregido**: `DeleteSupplierPaymentAsync` hace `currentDebt + payment.Amount` (caja) en vez de recalcular -> pasa a recalcular. | §2.8, §15.6bis |
| **B4** | Precision/convencion: `Payment.Amount` NO es 18,2 (Fluent lo pisa a 12,2); TC sin convencion | Corregido el hecho (Payment.Amount = `(12,2)` por Fluent; SupplierPayment.Amount = `(18,2)`). Campos nuevos: `ExchangeRate` = `decimal(18,6)` (alineado con `Invoice.MonCotiz`), `ImputedAmount` = `decimal(18,2)`. **Convencion TC FIJA en el ADR**: ARS por 1 USD, con formula de imputacion explicita. | §2.2 (nota), §2.2bis |
| extra | Generico `ServicioReserva` no aporta su moneda al eje proveedor (`BuildSupplierServicesQuery`) | **Se incluye en este trabajo** (no se difiere): `Currency` a los 6 branches del `Select` + columna en `ServicioReserva`. | §15.4, §15.5 |

## 0ter. Resolucion del bloqueante B5 y del riesgo de backfill (tercera vuelta)

| # | Hallazgo | Como se resuelve | Donde |
|---|---|---|---|
| **B5** | **TERCER** punto de escritura del escalar de la `Reserva` no contemplado como sincronizador de la hija: `AfipService.RecalculateReservaBalanceAsync` (AfipService.cs:1686-1709), disparado por la reversa de NC (AfipService.cs:1440). Bajo el plan anterior actualizaba el escalar pero NO la hija -> tras facturar/anular NC, la hija quedaba con saldo viejo y cuenta corriente/reportes/top-N mostraban datos desactualizados (la desincronizacion silenciosa que R-16 dice neutralizar). Verificado: las 3 rutinas (ReservaService:2512-2537, PaymentService:797-820, AfipService:1686-1709) son byte-identicas (15 lineas = 3 x 5 escalares); grep confirma que NO hay otro `.Balance =`/`.ConfirmedSale =` sobre `Reserva`. | **CONSOLIDAR las 3 copias en una unica rutina compartida `ReservaMoneyPersister.PersistAsync`** (helper de infra estatico; las 3 comparten `AppDbContext`) que persiste escalar + hija JUNTOS en la misma `SaveChangesAsync` atomica. Unico punto de escritura -> escalar y hija nunca divergen; desaparece la clase entera de bug. Las 3 rutinas conservan su nombre/firma y delegan el cuerpo. | §4.1, §5.2, §9 (paso 3), §12 (R-16 + camino AFIP), R-19 |
| **backfill** | §7.2.6(a) recorria solo estados "vivos" -> una reserva/proveedor legacy con saldo != 0 que nadie toca post-deploy quedaba con hija vacia = dato silencioso falso (deuda mostrada en cero) | Barrido **FIJO por `saldo != 0` sin filtro de estado**: TODAS las reservas con `Balance != 0` y TODOS los proveedores con `CurrentBalance != 0`. Estados excluidos (si los hubiera) se listan y se prueba que ningun consumidor migrado los agrega. Criterio de exito `sum(hija)==escalar legacy` sobre el **universo completo** (`count saldo != 0` == `count distinct padre en la hija`). | §7.2.6, R-18 |

---

## 1. Contexto

Hoy la plata de una reserva es **mono-moneda implicita en ARS**:

- `Reserva` (src/TravelApi.Domain/Entities/Reserva.cs:130-150) tiene `TotalCost`, `TotalSale`, `ConfirmedSale`, `Balance`, `TotalPaid` como `decimal` **sin moneda**.
- Los 5 servicios tipados (`HotelBooking`, `FlightSegment`, `TransferBooking`, `PackageBooking`, `AssistanceBooking`) ya tienen `string? Currency` desde la migracion `AddBookingCurrencyTraceability` (2026-05-29) pero **solo como trazabilidad**: `ReservaMoneyCalculator` lo ignora. `null` = legacy = ARS.
- El servicio generico `ServicioReserva` (src/TravelApi.Domain/Entities/ServicioReserva.cs:25-89) **no tiene** `Currency`.
- `Payment` (src/TravelApi.Domain/Entities/Payment.cs:11-58) **no tiene** moneda. Solo `Amount` (decimal 18,2). Se asume ARS.
- `ReservaMoneyCalculator.Calculate(Reserva)` (src/TravelApi.Domain/Reservations/ReservaMoneyCalculator.cs:37-79) suma **todos** los servicios y pagos sin separar moneda y devuelve un `ReservaMoneySummary` con 5 escalares (src/TravelApi.Domain/Reservations/ReservaMoneySummary.cs).
- La facturacion ARCA **ya es multimoneda** (`Invoice.MonId` PES/DOL en src/TravelApi.Domain/Entities/Invoice.cs:48, `MonCotiz` :54, `ExchangeRateSource?` :77, flag `EnableMultiCurrencyInvoicing`). Una factura = una moneda. Esto **NO se toca**.

### Problema

La agencia opera servicios en USD y en ARS dentro de la misma reserva, y el cliente a veces **paga en una moneda distinta** a la del saldo. Hoy todo se suma como si fuera una sola moneda (USD + ARS sumados como numeros pelados) y no hay forma de registrar un pago cruzado. El dueno decidio:

1. Cada servicio va **entero** en UNA moneda (costo y venta en la misma; USD o ARS). `Currency` pasa de trazabilidad a **operativo**.
2. Cada pago declara su **moneda real y monto real** (lo que entro a caja; la caja NO se convierte). Si paga en moneda distinta al saldo imputado, se captura TC + fuente + fecha y el saldo de **esa** moneda baja por el equivalente convertido (§2.7).
3. Totales **separados por moneda, SIN conversion a moneda base** en las pantallas operativas. Columna USD y columna ARS para costo/inversion, venta, saldo a cobrar y recaudado. Aplica a la lista de servicios, al header de la reserva, **y a reportes, tesoreria y cuenta corriente del cliente** (decision 2).
4. La facturacion no se toca. **Y el medio/moneda de pago no altera la factura emitida**: si se facturo en USD a un TC y el cliente paga en ARS a otro TC, la factura NO se reescribe. La diferencia de cambio existe contablemente pero **la reconoce el contador en el cierre**; el sistema solo deja los datos crudos (§2.7, limite contable).

### Limite contable (critico, decision del contador)

El sistema **SOLO captura datos y convierte para imputar**. NO calcula, muestra ni registra "diferencia de cambio" como resultado en ninguna pantalla operativa. La diferencia (factura USD a un TC vs cobro ARS a otro TC) es un reconocimiento contable de cierre; el sistema deja todo crudo y reconstruible (pago: moneda+monto reales, moneda imputada, TC, fuente, fecha, equivalente). Cualquier pantalla que muestre "diferencia de cambio" queda **fuera de este ADR** y requiere contador (§13).

---

## 2. Decision

### 2.1 Catalogo de monedas: una sola fuente de verdad

Crear una constante de dominio para reservas (ISO 4217), alineada con `ArcaCurrencyMapper` pero sin acoplar el saldo al detalle fiscal:

```
// src/TravelApi.Domain/Entities/Monedas.cs (nuevo)
public static class Monedas
{
    public const string ARS = "ARS";
    public const string USD = "USD";
    public static readonly IReadOnlyList<string> Soportadas = new[] { ARS, USD };
    public static bool EsSoportada(string? iso) =>
        !string.IsNullOrWhiteSpace(iso) &&
        Soportadas.Any(m => string.Equals(m, iso, StringComparison.OrdinalIgnoreCase));
    public static string Normalizar(string? iso) =>
        string.IsNullOrWhiteSpace(iso) ? ARS : iso.Trim().ToUpperInvariant();
}
```

`ArcaCurrencyMapper` (src/TravelApi.Domain/Helpers/ArcaCurrencyMapper.cs) habla codigo ARCA ("PES"/"DOL") para el XML SOAP; el dominio de reservas habla ISO ("ARS"/"USD"). Ambos soportan exactamente ARS/USD hoy. La validacion cruzada con `ArcaCurrencyMapper.IsSupported` se queda en el boundary de facturacion (ya existe). `Monedas` es el catalogo del dominio de reservas/pagos.

### 2.2 Modelo de datos

**`ServicioReserva`** — agregar:
```
[MaxLength(3)]
public string? Currency { get; set; }   // null = legacy = ARS (se normaliza al leer)
```
Para los 5 tipados, `Currency` (ya `string?`) pasa de trazabilidad a operativo **sin** cambiar la nulabilidad del modelo. El calculo normaliza `null -> ARS` via `Monedas.Normalizar`. Evita una migracion NOT NULL sobre columnas con datos.

**`Payment`** — agregar moneda del pago + bloque de TC (§2.7):
```
[MaxLength(3)]
public string Currency { get; set; } = Monedas.ARS;          // moneda REAL del pago (lo que entro a caja). NOT NULL default ARS.

// --- Imputacion cruzada (solo se completa si Currency != ImputedCurrency) ---
[MaxLength(3)]
public string? ImputedCurrency { get; set; }                  // moneda del SALDO al que se imputa. null = se imputa a su propia moneda.

[Column(TypeName = "decimal(18,6)")]
public decimal? ExchangeRate { get; set; }                    // TC aplicado. Convencion FIJA: ARS por 1 USD (ver 2.2bis). null si no hubo conversion.

public ExchangeRateSource? ExchangeRateSource { get; set; }   // reusa el enum de ADR-012/ADR-002. null si no hubo conversion.

public DateTime? ExchangeRateAt { get; set; }                 // fecha del TC. null si no hubo conversion.

[Column(TypeName = "decimal(18,2)")]
public decimal? ImputedAmount { get; set; }                   // monto EQUIVALENTE imputado al saldo de ImputedCurrency. null si no hubo conversion (entonces imputa Amount sobre Currency).
```

> **Correccion de hecho (B4 del review).** La version anterior afirmaba que `Payment.Amount` es `decimal(18,2)`. **Es falso**: el atributo de la entidad dice `decimal(18,2)` (Payment.cs:16-17) pero el Fluent lo **pisa** con `HasPrecision(12,2)` (AppDbContext.cs:527), y en EF Core el Fluent gana sobre el atributo. La precision efectiva de `Payment.Amount` es **`decimal(12,2)`**. En cambio `SupplierPayment.Amount` no tiene Fluent override (AppDbContext.cs:1139-1149) y queda en su atributo `decimal(18,2)` (SupplierPayment.cs:23-24). Esta asimetria es preexistente y este ADR **no la unifica** (cambiar la precision de `Payment.Amount` es una migracion de tipo sobre columna con datos = fuera de alcance, riesgo M2). Lo que SI hace este ADR es fijar la precision de los **campos nuevos** de forma coherente: ver 2.2bis.

#### 2.2bis Precision y convencion del tipo de cambio (resuelve B4)

Decisiones fijas, NO "en implementacion":

- **`ImputedAmount`** (cliente y proveedor): `decimal(18,2)`. Es plata imputada al saldo; misma escala que `SupplierPayment.Amount`. Nota: como la caja `Payment.Amount` es `(12,2)`, el `ImputedAmount` de un pago de cliente nunca excedera en magnitud lo representable por la caja convertida; `(18,2)` da margen y es la escala canonica de plata del sistema. No se fuerza `(12,2)` en el campo nuevo para no arrastrar el limite del legacy.
- **`ExchangeRate`** (cliente y proveedor): `decimal(18,6)`. Se alinea **exactamente** con `Invoice.MonCotiz` (AppDbContext.cs:630-632, `HasPrecision(18,6)`), que es la cotizacion que ya usa la facturacion. Seis decimales cubren cotizaciones con fraccion fina sin perdida.
- **Convencion del TC (FIJA en el ADR):** `ExchangeRate` se expresa como **unidades de ARS por 1 USD** (ej. 1 USD = 1000 ARS -> `ExchangeRate = 1000.000000`). Es la misma orientacion que `Invoice.MonCotiz` para facturas en USD (ARS por unidad de moneda extranjera), de modo que el dato es consistente con facturacion y reconstruible por el contador.
  - **Formula de imputacion (deriva de la convencion):**
    - Pago ARS imputado a saldo USD: `ImputedAmount[USD] = round2(Amount_ARS / ExchangeRate)`.
    - Pago USD imputado a saldo ARS: `ImputedAmount[ARS] = round2(Amount_USD * ExchangeRate)`.
  - Como hoy solo hay ARS y USD, no hace falta TC USD->USD ni triangulacion. Si en el futuro se suma una tercera moneda, la convencion (ARS por 1 unidad-extranjera) y la formula se revisan en ese ADR.
  - `round2` = `EconomicRulesHelper.RoundCurrency` (la misma redondeo que ya usa el resto del calculo).

Convencion de imputacion (decision del contador): **un pago se imputa a UNA sola moneda de saldo por registro**. Si el cliente paga un combo (parte a saldo ARS, parte a saldo USD), son **dos pagos** distintos. `Amount`+`Currency` son sagrados (la caja real). Cuando `Currency == ImputedCurrency` (o `ImputedCurrency` null), el pago imputa `Amount` sobre el saldo de su propia moneda y los campos de TC quedan null. Cuando difieren, `ImputedAmount` (equivalente convertido segun 2.2bis) es lo que baja del saldo de `ImputedCurrency`, y TC+fuente+fecha son **obligatorios**.

Reuso del enum `ExchangeRateSource` (src/TravelApi.Domain/Entities/ExchangeRateSource.cs): `BCRA_A3500`, `BNA_Mayorista`, `BNA_Minorista`, `AfipOficial`, `Manual`, `BNA_VendedorDivisa`. En el MVP el TC lo ingresa el usuario a mano (`Manual` es el caso tipico; el resto quedan disponibles como en facturacion). `Unset=0` es centinela: un pago cruzado NUNCA se persiste con `ExchangeRateSource = Unset` ni null (§8).

### 2.3 `ReservaMoneyCalculator`: de escalar a por-moneda

> **B1 del review (CENTRAL) — el "PorMoneda solo on-read, sin columnas nuevas" es INVIABLE para los agregados cross-reserva.** El reviewer verifico que varios consumidores **suman/ordenan/filtran montos por moneda en SQL** entre muchas reservas (o muchos proveedores), y un diccionario en memoria no es queryable en SQL:
> - `CustomerService.cs:43-45, 69-71, 283-285, 300-302`: cuenta corriente del cliente — `Sum(r => r.Balance)` y `SumAsync(TotalSale/TotalPaid/Balance)` traducidos a SQL.
> - `ReportService.cs:67-77`: `SumAsync(Balance/TotalSale/TotalCost)`; `:100-103`: `OrderByDescending(f => f.Balance).Take(5)` en SQL (top deudores).
> - `TreasuryService.cs:35-37`: `Where(r.Balance > 0).SumAsync(r.Balance)`.
> - `AlertService.cs:127, 135`: `Where(s.CurrentBalance > 100).OrderByDescending(s.CurrentBalance)` (top proveedores).
>
> No se puede `SumAsync` ni `OrderBy` sobre `PorMoneda` en memoria sin traer **todas** las reservas/proveedores a la app y agrupar a mano (N+1 masivo, rompe paginacion y el top-N de SQL). **Decision: los montos por moneda se PERSISTEN.** Se reabren las Alternativas B/C de §14: se elige la **Alternativa C — tabla hija materializada** (`ReservaMoneyByCurrency` / `SupplierBalanceByCurrency`), porque deja los `Sum`/`OrderBy`/`Where`/top-N **por moneda** ejecutandose en SQL (que es justo lo que B1 y B2 piden), sin multiplicar columnas en la entidad raiz por cada moneda futura (defecto de la Alternativa B). El escalar `Balance`/`CurrentBalance` se conserva **solo como semaforo** (flag si/no debe) para los lectores booleanos (`ReservationEconomicPolicy.cs:7`, `ReservaLifecycleAutomationService.cs:241`) — esto el reviewer lo valido como correcto. Justificacion completa de la eleccion C vs B en §14.

#### 2.3.0 Tabla hija materializada `ReservaMoneyByCurrency` (NUEVO — fuente queryable por moneda)

```
// src/TravelApi.Domain/Entities/ReservaMoneyByCurrency.cs (nueva entidad)
public class ReservaMoneyByCurrency
{
    public int Id { get; set; }
    public int ReservaId { get; set; }            // FK a Reserva (mapea a columna real, ver migracion §7)
    public Reserva? Reserva { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;   // "ARS" | "USD" — una fila por moneda presente

    [Column(TypeName = "decimal(18,2)")] public decimal TotalSale { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal ConfirmedSale { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal TotalCost { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal TotalPaid { get; set; }     // imputado a ESTA moneda (ImputedAmount o Amount)
    [Column(TypeName = "decimal(18,2)")] public decimal Balance { get; set; }       // ConfirmedSale - TotalPaid de ESTA moneda (puede ser negativo = a favor)
}
```

- **Relacion**: `Reserva` 1 — N `ReservaMoneyByCurrency`. Indice unico `(ReservaId, Currency)` (a lo sumo 2 filas por reserva hoy: ARS y USD). Indice por `(Currency, Balance)` para que los top-N por moneda (B2) y los `Where(Balance > 0)` por moneda corran indexados.
- **Quien la escribe**: la MISMA rutina que hoy persiste los escalares — `ReservaService.RecalculateMoneyAsync`/`UpdateBalanceAsync` y `PaymentService.RecalculateReservaBalanceAsync`. Tras `ReservaMoneyCalculator.Calculate`, ademas de setear los 5 escalares de la `Reserva` (con `Balance` = surrogate), **sincroniza** las filas hijas: upsert de una fila por cada moneda presente en `summary.PorMoneda`, y borra las filas de monedas que ya no estan. Es un materializado derivado: se reescribe en cada recalculo, igual que hoy se reescribe el escalar. **No** hay una segunda fuente de verdad: el calculator sigue siendo la unica logica; la tabla es su proyeccion persistida.
- **Cascade delete**: al borrar una `Reserva`, sus filas hijas se borran (FK `OnDelete(Cascade)`).
- **Consistencia**: las filas se reescriben dentro de la MISMA `SaveChangesAsync` que persiste los escalares (misma transaccion), de modo que escalar-surrogate y filas-por-moneda nunca divergen. (Detalle de la sincronizacion en §7.)

Nuevo shape del summary in-memory (sigue funcion pura, sin EF):

```
public sealed class ReservaMoneySummary
{
    // NUEVO: una linea por moneda presente en la reserva (servicios + pagos).
    public IReadOnlyDictionary<string, ReservaMoneyLine> PorMoneda { get; }

    // COMPAT (escalares heredados, ver 2.4 y la autocritica 5): se mantienen.
    public decimal TotalSale { get; }
    public decimal ConfirmedSale { get; }
    public decimal TotalCost { get; }
    public decimal TotalPaid { get; }
    public decimal Balance { get; }       // SURROGATE (2.4): flag de saldo pendiente, NO monto.
    public bool EsMultimoneda { get; }    // PorMoneda.Count > 1
}

public sealed class ReservaMoneyLine
{
    public string Currency { get; }       // "ARS" | "USD"
    public decimal TotalSale { get; }
    public decimal ConfirmedSale { get; }
    public decimal TotalCost { get; }
    public decimal TotalPaid { get; }     // suma de ImputedAmount (o Amount si no hubo conversion) imputado a ESTA moneda
    public decimal Balance { get; }       // ConfirmedSale - TotalPaid de ESTA moneda
}
```

`Calculate` agrupa cada **servicio** por `Monedas.Normalizar(servicio.Currency)` y cada **pago** por la **moneda imputada** (`ImputedCurrency ?? Currency`), usando `ImputedAmount ?? Amount` como monto que baja del saldo de esa moneda. Los predicados `IsQuoted*` / `IsResolved` NO cambian. El `Balance` de cada linea es `ConfirmedSale - TotalPaid` de esa moneda.

> Importante: el pago cruzado imputa su **equivalente** a la moneda del saldo. La caja (`Amount`+`Currency`) se contabiliza aparte en tesoreria por su moneda real (§4.2/§6). Son dos lecturas distintas del mismo pago: cuanto bajo la deuda (imputado) vs cuanto entro a caja (real). No se mezclan.

### 2.4 Agregado de compatibilidad: el escalar `Balance` es un SURROGATE (decision central)

Los escalares `TotalSale/ConfirmedSale/TotalCost/TotalPaid` de la `Reserva` se conservan como **suma cruda de todas las monedas** (numeros mezclados; pierden sentido contable en multimoneda, se mantienen solo para no romper lectura legacy hasta migrar cada consumidor — ver §5).

El escalar `Balance` se redefine como **flag de "tiene saldo pendiente"**, NO como monto:
```
Balance (escalar) = sum_por_moneda( max(0, line.Balance) )
```
Es `0` **si y solo si** ninguna moneda tiene saldo pendiente positivo; si alguna moneda debe, queda `> 0`. El saldo a favor de una moneda **no compensa** la deuda de otra (correcto: deber USD no se cancela por sobrepago ARS).

**Por que** (lo que esta decision protege): el gate `ReservationEconomicPolicy.IsEconomicallySettled` (src/TravelApi.Domain/Entities/ReservationEconomicPolicy.cs:5-8) es `RoundCurrency(Balance) <= 0`, y el **job de auto-cierre** (src/TravelApi.Infrastructure/Services/ReservaLifecycleAutomationService.cs:241) filtra `r.Balance <= 0` **en SQL** contra la columna. SQL no puede evaluar un diccionario por moneda. Con el surrogate, el gate y el job **siguen funcionando sin tocarse**: una reserva con deuda en cualquier moneda no se considera saldada ni se auto-cierra. La columna `Balance` deja de significar "cuanto debe" y pasa a significar "marcador de saldo pendiente"; el detalle real vive en `PorMoneda`.

> Una reserva totalmente a favor (sobrepago) da escalar `Balance = 0` -> sigue contando como saldada, igual que hoy (hoy `Balance` puede ser negativo y el gate usa `<= 0`).

**Limite del surrogate (la pieza mas riesgosa, ver §5)**: el surrogate sirve para los consumidores que leen `Balance` como **booleano de saldo** (`<= 0` / `> 0`). **Enga​na** a cualquier consumidor que lo lea como **monto adeudado real** y peor aun a los que lo **suman/ordenan entre reservas** (cuenta corriente del cliente, tesoreria AR, reportes, top-N de deudores). Esos consumidores **no** pueden leer un diccionario en memoria (corren en SQL); migran a leer la **tabla hija `ReservaMoneyByCurrency`** (§2.3.0), que persiste el monto por moneda y es queryable: `dbContext.ReservaMoneyByCurrency.Where(x => x.Currency == "USD").Sum(x => x.Balance)` corre en SQL. El escalar queda solo de semaforo. Lista completa en §5.1.

### 2.5 DTO y API

`ReservaDto` / `ReservaListDto` (src/TravelApi.Application/DTOs/ReservaDto.cs):
- **Conservar** los escalares actuales con la semantica de compat de 2.4 (no romper el front existente de golpe).
- **Agregar** `List<ReservaMoneyLineDto> PorMoneda` (Currency + los 5 numeros) y `bool EsMultimoneda`.
- El mapeo se arma en `MappingProfile` (src/TravelApi.Application/Mappings/MappingProfile.cs) desde el summary recalculado on-read (igual que hoy). `ApplyEconomicFlags` (src/TravelApi.Infrastructure/Services/ReservaService.cs:2539-2553) sigue usando el escalar `Balance` surrogate para los flags booleanos (`IsEconomicallySettled`, `IsFullyPaid = Balance == 0`, `HasOverdueDebt = Balance > 0`) — todos correctos con el surrogate.

### 2.6 Frontend (gate UX obligatorio con Gaston, ULTIMO paso)

- `formatCurrency` (src/TravelWeb/src/lib/utils.js) hoy esta hardcodeado a una sola moneda. Pasa a `formatCurrency(amount, currency)` con `currency` explicito (default "ARS"). Cambio transversal: revisar TODOS los call sites.
- Selector de moneda por servicio (ARS/USD) y por pago.
- En el pago: si la moneda del pago != moneda del saldo elegido, aparecen TC + fuente + fecha (obligatorios) y se muestra el equivalente que se imputa.
- Header y lista de servicios, **y reportes/tesoreria/cuenta corriente**, muestran columnas/bloques por moneda. Si la reserva (o el cliente) es mono-moneda, se ve igual que hoy.
- ES UN CAMBIO VISUAL TRANSVERSAL -> **gate UX obligatorio con Gaston** (CLAUDE.md): el `ux-ui-disenador` define contra `docs/ux/guia-ux-gaston.md` como se muestran dos monedas en cada vista de plata (header reserva, lista servicios, registro de pago, dashboard financiero, arqueo de caja, cuenta corriente del cliente, exports). Nada de frontend se implementa sin sus respuestas. El sistema NUNCA muestra "diferencia de cambio" (limite contable §1).

### 2.7 Cobro cruzado con tipo de cambio (DECISION 1 del dueno, criterio contador)

Flujo de un pago cruzado (ej: cliente debe USD 100, paga ARS 100.000):
1. El usuario registra el pago con su **moneda real** (`Currency = ARS`) y **monto real** (`Amount = 100000`). Esto es lo que entra a caja; **inviolable**, no se convierte.
2. Elige la **moneda del saldo** a la que imputa (`ImputedCurrency = USD`). Si difiere de `Currency`, el sistema exige **TC + fuente (`ExchangeRateSource`) + fecha (`ExchangeRateAt`)**, todos obligatorios y persistidos. TC manual en el MVP.
3. El sistema calcula `ImputedAmount` = equivalente en `ImputedCurrency` (ej: USD 100 si TC 1000) y **baja el saldo USD por ese equivalente**. El pago guarda los 7 datos: `Amount`, `Currency`, `ImputedCurrency`, `ExchangeRate`, `ExchangeRateSource`, `ExchangeRateAt`, `ImputedAmount`.
4. **No** se toca ninguna factura ya emitida. **No** se calcula ni muestra diferencia de cambio en ninguna pantalla operativa.

Doble lectura del mismo pago, ambas crudas y reconstruibles:
- **Imputacion al saldo** (cuanto bajo la deuda): `ImputedAmount` sobre `ImputedCurrency` -> alimenta `ReservaMoneyLine[USD].TotalPaid`.
- **Caja real** (cuanto entro): `Amount` sobre `Currency` -> alimenta tesoreria/arqueo por su moneda real (§4.2/§6).

La diferencia de cambio entre la valuacion de la factura (si la hubo) y la del cobro queda implicita en los datos crudos; el contador la reconoce en el cierre. El sistema no la materializa.

### 2.8 Reversa / anulacion / edicion de un pago cruzado (resuelve B3)

> **B3 del review.** Al anular o editar un pago cruzado hay que revertir el **equivalente imputado** (`ImputedAmount`) sobre la **moneda imputada**, NO el `Amount` real de caja; y la caja real debe reflejar que ese `Amount` (en su moneda real) ya no entro. Esto toca plata; se disena explicito, no se difiere.

**Como funciona hoy la reversa (verificado).** No hay matematica de reversa imperativa: `DeletePaymentAsync` (PaymentService.cs:888-916) hace **soft-delete** (`IsDeleted = true`) y llama `RecalculateReservaBalanceAsync`; `UpdatePaymentAsync` (PaymentService.cs:856-886) muta el pago y llama el mismo recalculo. `RecalculateReservaBalanceAsync` (PaymentService.cs:797-820) **recorre los pagos vivos** (el query filter `!IsDeleted`, AppDbContext.cs:534, excluye los borrados) y re-ejecuta `ReservaMoneyCalculator.Calculate`. Es **self-healing**: el saldo se reconstruye desde los pagos que quedan, no se "deshace" a mano.

**Diseno de la reversa cruzada (se apoya en ese mecanismo, sin matematica nueva de resta):**

1. **Imputacion al saldo (lo que pide B3).** Como el calculator agrupa cada pago por su **moneda imputada** usando `ImputedAmount ?? Amount` (§2.3), al soft-deletear un pago cruzado ese pago **desaparece del recorrido** y su `ImputedAmount` deja de sumarse a `PorMoneda[ImputedCurrency].TotalPaid`. Resultado: el saldo de **esa** moneda sube exactamente por el equivalente convertido que se habia imputado — que es justo lo que B3 exige ("devolver ese equivalente al saldo de esa moneda"). No se devuelve el `Amount` de caja al saldo: el saldo nunca vio el `Amount`, vio el `ImputedAmount`. La tabla hija `ReservaMoneyByCurrency` se reescribe en el mismo recalculo, asi que el materializado por moneda tambien queda correcto.
2. **Caja real / arqueo.** El arqueo lee `Payment.Amount` + `Payment.Currency` filtrando `!IsDeleted` (§4.5/§6). Al soft-deletear, el pago sale del arqueo: la caja de **su moneda real** baja por el `Amount` real (no por el imputado). Es lo correcto: si entraron ARS 100.000 y se anula, la caja ARS baja 100.000; el saldo USD sube su equivalente. Los dos ejes se mueven cada uno en su moneda, nunca cruzados.
3. **Editar.** `UpdatePaymentAsync` hoy permite cambiar `Amount/Method/Reference/Notes` y recalcula. Para un pago **cruzado**, editar el `Amount` sin recomputar `ImputedAmount` dejaria la imputacion inconsistente con la caja. **Regla MVP (alineada con §8.8): un pago cruzado es inmutable en su bloque de moneda/TC/imputacion.** `UpdatePaymentAsync` **rechaza** editar `Amount`, `Currency`, `ImputedCurrency`, `ExchangeRate` o `ImputedAmount` de un pago cruzado (`ImputedCurrency != null && != Currency`); para corregirlo se **anula y se recrea** (igual que no se reescribe una factura). Editar `Method/Reference/Notes` (datos no economicos) sigue permitido. Esto evita tener que recalcular el equivalente en el update y mantiene `Amount`+`Currency` sagrados.
4. **Guardas existentes se respetan.** La anulacion sigue pasando por `DeleteGuards.GetPaymentDeleteBlockReasonAsync` (PaymentService.cs:899): si el pago tiene recibo emitido o factura AFIP viva, **no** se puede anular (regla preexistente, no se relaja). Un pago cruzado con recibo emitido se corrige por los mismos canales fiscales que cualquier pago con recibo.

**Consecuencia para tests (B3):** un test de "anular pago cruzado" debe verificar las DOS lecturas: (a) `PorMoneda[ImputedCurrency].Balance` vuelve al valor previo al pago (sube por `ImputedAmount`); (b) la caja de `Currency` baja por `Amount`; (c) ninguna pantalla muestra diferencia de cambio. Y un test de "editar Amount de pago cruzado" debe verificar que **se rechaza** (forzar anular+recrear).

**Espejo proveedor:** la reversa del pago saliente cruzado (`SupplierPayment`, ya con soft-delete + auditoria, SupplierPayment.cs:40-42) sigue el mismo patron contra `CalculateSupplierDebt`/`RecalculateAllBalancesAsync`. Detalle en §15.6bis.

---

## 3. Shape de datos nuevos (resumen)

| Entidad | Campo nuevo | Tipo | Default | Migracion |
|---|---|---|---|---|
| `ServicioReserva` | `Currency` | `string?` (3) | null (se lee como ARS) | aditiva |
| `Payment` | `Currency` | `string` (3) NOT NULL | `'ARS'` BD | aditiva + backfill |
| `Payment` | `ImputedCurrency` | `string?` (3) | null | aditiva |
| `Payment` | `ExchangeRate` | `decimal?(18,6)` | null | aditiva (alineado con `Invoice.MonCotiz`, ver 2.2bis) |
| `Payment` | `ExchangeRateSource` | `int?` (enum) | null | aditiva |
| `Payment` | `ExchangeRateAt` | `datetime?` | null | aditiva |
| `Payment` | `ImputedAmount` | `decimal?(18,2)` | null | aditiva |
| (Dominio) `Monedas` | clase nueva | — | — | sin migracion |
| `ReservaMoneySummary` | `PorMoneda`, `EsMultimoneda` | dict / bool | — | sin migracion (in-memory) |
| **`ReservaMoneyByCurrency`** | **tabla nueva** (Id, ReservaId, Currency, TotalSale, ConfirmedSale, TotalCost, TotalPaid, Balance) | — | — | **aditiva (CREATE TABLE)** + backfill |
| **`SupplierBalanceByCurrency`** | **tabla nueva** (Id, SupplierId, Currency, ConfirmedPurchases, TotalPaid, Balance) | — | — | **aditiva (CREATE TABLE)** + backfill (ver §15.3) |
| `ReservaDto`/`ReservaListDto` | `PorMoneda`, `EsMultimoneda` | list / bool | — | contrato aditivo |

Los 5 servicios tipados **ya tienen** la columna `Currency` (migracion `AddBookingCurrencyTraceability`). Solo se cambia su tratamiento (de trazabilidad a operativo) y se normaliza `null -> ARS` al leer.

---

## 4. Mapa COMPLETO de puntos de ruptura por capa (con file:line)

Grep `\.Balance|\.ConfirmedSale|\.TotalPaid|\.TotalSale|\.TotalCost` = 26 archivos. Sumando los aggregates de `Payment.Amount` cross-reserva, los puntos reales son los de abajo.

### 4.0 Dominio / calculator
- `ReservaMoneyCalculator.Calculate` (src/TravelApi.Domain/Reservations/ReservaMoneyCalculator.cs:37-79): agrupa por moneda; produce `PorMoneda` + surrogate. Punto central del cambio.
- `ReservaMoneySummary` (src/TravelApi.Domain/Reservations/ReservaMoneySummary.cs:12-44): nuevo shape.
- `ReservationEconomicPolicy.IsEconomicallySettled` (src/TravelApi.Domain/Entities/ReservationEconomicPolicy.cs:5-8): **NO se toca**, el surrogate lo preserva.

### 4.1 Persistencia del escalar + materializado por moneda — RUTINA CONSOLIDADA (resuelve B5)

> **B5 del review (verificado en codigo).** Hay **TRES** rutinas que recalculan y persisten los escalares de la `Reserva` (`TotalSale/ConfirmedSale/TotalCost/TotalPaid/Balance`), **byte-identicas** entre si (5 asignaciones + `SaveChangesAsync`), no dos como decia la version anterior:
> 1. `ReservaService.RecalculateMoneyAsync` (src/TravelApi.Infrastructure/Services/ReservaService.cs:2512-2537; asignaciones en :2530-2534).
> 2. `PaymentService.RecalculateReservaBalanceAsync` (src/TravelApi.Infrastructure/Services/PaymentService.cs:797-820; asignaciones en :813-817).
> 3. **`AfipService.RecalculateReservaBalanceAsync` (src/TravelApi.Infrastructure/Services/AfipService.cs:1686-1709; asignaciones en :1702-1706)** — NO contemplada por la version anterior. Se dispara tras la **reversa de nota de credito** (AfipService.cs:1440, dentro de `ReverseInvoiceEconomicEffectAsync` post NC total/parcial) y persiste `ConfirmedSale + Balance` (entre otros). Bajo el plan anterior, esta ruta actualizaba el **escalar** pero NO reescribia la **tabla hija** `ReservaMoneyByCurrency` -> despues de facturar/anular una NC, la hija quedaba con el saldo viejo y la cuenta corriente / reportes / top-N (que leen la hija) mostraban datos desactualizados. Es **exactamente** la desincronizacion silenciosa que R-16 dice neutralizar, abierta por una tercera puerta.
>
> Grep exhaustivo confirmatorio (`reserva.Balance =` / `file.Balance =` / `.ConfirmedSale =` en `src/`): **solo** esas 3 rutinas escriben el escalar de la `Reserva` (15 lineas = 3 x 5 escalares; ningun otro `.Balance =` / `.ConfirmedSale =` sobre `Reserva` fuera de ellas). Las escrituras de `Supplier.CurrentBalance` son del eje proveedor (§15.5/§15.6bis), aparte.

**Remediacion (la mejor): CONSOLIDAR las tres copias en UNA sola rutina compartida** que persista escalar + tabla hija JUNTOS en el mismo `SaveChangesAsync` (atomico). Asi escalar y hija **nunca** pueden divergir y desaparece la clase entera de bug: cualquiera que en el futuro necesite recalcular el saldo llama a la rutina y obtiene escalar + hija sincronizados, sin poder "olvidarse" de la hija.

**Donde vive y firma.** Las tres rutinas comparten el MISMO `AppDbContext` (verificado: `ReservaService._context`, `AfipService._context`, `PaymentService._dbContext`, todos `AppDbContext`) y el mismo patron load-Includes -> `Calculate` -> asignar -> `SaveChangesAsync`. Se extrae a un helper de infraestructura estatico (no necesita estado de instancia; opera sobre el `AppDbContext` que el caller ya tiene), para no acoplar tres servicios entre si ni inflar `ReservaService`:

```
// src/TravelApi.Infrastructure/Reservations/ReservaMoneyPersister.cs (nuevo)
internal static class ReservaMoneyPersister
{
    // Carga la reserva con TODOS los Includes economicos, recalcula con el calculator,
    // persiste los 5 escalares (Balance = surrogate §2.4) Y sincroniza ReservaMoneyByCurrency
    // (upsert por Currency desde summary.PorMoneda, borra monedas ausentes), todo en UNA
    // sola SaveChangesAsync. Es el UNICO punto de escritura del escalar y de la hija.
    public static async Task PersistAsync(AppDbContext db, int reservaId, CancellationToken ct = default);
}
```

- **Las 3 llamadas actuales pasan por ella**: `ReservaService.RecalculateMoneyAsync`, `PaymentService.RecalculateReservaBalanceAsync` y `AfipService.RecalculateReservaBalanceAsync` se reducen a `await ReservaMoneyPersister.PersistAsync(_context/_dbContext, reservaId, ct);` (cada una conserva su nombre/firma publica-interna para no tocar sus call-sites; solo delegan el cuerpo). Los Includes (Payments + 5 tipados + generico `Servicios`) se centralizan dentro del helper, asi las tres cargan exactamente el mismo grafo (hoy ya coinciden, pero la centralizacion elimina el riesgo de que una se desincronice de las otras al agregar un tipo de servicio).
- **Sincronizacion de la hija (dentro de `PersistAsync`)**: tras `Calculate`, para cada moneda en `summary.PorMoneda` se hace upsert de la fila `ReservaMoneyByCurrency (ReservaId, Currency)` con las 5 metricas de la linea; las filas de monedas ya no presentes se borran. El escalar (`Balance` surrogate) y las filas hijas se persisten en la **misma** `SaveChangesAsync` (atomico): o se graban ambos o ninguno.
- **`UpdateBalanceAsync`** de `ReservaService` (que tambien dispara el motor de estados, ReservaService.cs:2503-2505) llama a `PersistAsync` para la parte de plata y conserva su `EvaluateAndApplyAsync` despues.
- `PaymentService.RegisterPayment` (PaymentService.cs:449-464): el `new Payment{...}` debe setear `Currency`/`ImputedCurrency`/TC desde el request (§8). Validar server-side. (No persiste el escalar el mismo; dispara el recalculo via `RecalculateReservaBalanceAsync` -> ahora `PersistAsync`.)

### 4.2 Auto-cierre / gates (siguen con surrogate, NO tocar logica)
- `ReservaLifecycleAutomationService.cs:241` (`r.Balance <= 0` en SQL): surrogate lo preserva. **No tocar.**
- `EconomicRulesHelper` (operative/voucher/AFIP/archive gates): derivan de `IsEconomicallySettled` o `Balance > 0`. Correctos con surrogate. **No tocar logica.**

### 4.3 Cuenta corriente del cliente (RUPTURA REAL — el surrogate NO alcanza)
- `CustomerService` (src/TravelApi.Infrastructure/Services/CustomerService.cs:43-45, 69-71, 283-285, 300-302): `CurrentBalance = customer.Reservas...Sum(r => r.Balance)` y `TotalBalance/TotalSales/TotalPaid` por `SumAsync`, **traducido a SQL** (confirmado por B1). **Suma el escalar `Balance` entre reservas e interpreta el total como MONTO adeudado del cliente.** Con el surrogate, sumar "flags 0-o-positivos" entre reservas y monedas da un numero sin significado (ni ARS ni USD). **Este es el consumidor que la autocritica advirtio.** Migra a **agregar contra `ReservaMoneyByCurrency` en SQL**: el saldo del cliente por moneda es `ReservaMoneyByCurrency.Where(x => x.Reserva.PayerId == id && estados vivos).GroupBy(x => x.Currency).Select(g => new { g.Key, Balance = g.Sum(x => x.Balance) })`. El DTO del cliente expone `List<{Currency, Balance}>` en vez de un escalar mezclado (ver §6).
- `CustomerService` linea 345-349 y 378: por reserva expone `TotalSale/Balance/Paid` (escalares) y `Amount` de pagos. Tambien pasa a por-moneda en la vista de cuenta corriente.

### 4.4 Reportes / dashboards (RUPTURA REAL — sumas cross-reserva y cross-payment en SQL)
- `ReportService` (src/TravelApi.Infrastructure/Services/ReportService.cs):
  - :65, :81, :191, :228, :232 `Payments...SumAsync(p => p.Amount)` -> mezcla monedas. Agrupar por `Currency`.
  - :67-77 `Reservas.SumAsync(f => f.Balance / TotalSale / TotalCost)` -> mezcla. El `Balance` ademas es surrogate (ni siquiera es monto). Pasar a agregados por moneda.
  - :88 `Where(f => f.Balance > 0)` (decidir si entra a la lista de pendientes): el filtro booleano sigue OK con surrogate.
  - :100-103 **top-5 reservas deudoras** `OrderByDescending(f => f.Balance).Take(5)` en SQL: **rota por B2**. Ordenar por el escalar surrogate mezcla USD+ARS y da un ranking sin sentido. Pasa a **top-N por moneda** contra `ReservaMoneyByCurrency` (ver §6bis). El monto mostrado va con su moneda.
  - :121-122, :248-264, :339-341, :462-498, :613-635 (sales/margin/seller/excel): suman `TotalSale/TotalCost` cross-reserva -> mezcla. Por moneda.
  - :538-541 (por producto) suma `SalePrice/NetCost` de bookings -> agrupar por `Currency` del booking.
  - :559-599 (cash flow): suma `p.Amount` por dia -> separar por moneda o declarar el reporte como "solo ARS" hasta el rediseno UX (decision dueno: NO queda como deuda; entra al alcance).
- `SupplierService` aparece en el grep por `CurrentBalance` de proveedores (cuenta por pagar); ese eje es de proveedores, no del cliente. Verificar en impl si la moneda del proveedor entra al mismo alcance (probable; ver §13 abierto).

### 4.5 Tesoreria / caja / arqueo (RUPTURA REAL)
- `TreasuryService` (src/TravelApi.Infrastructure/Services/TreasuryService.cs):
  - :36-40 `Reservas.Where(r.Balance > 0).SumAsync(r.Balance)` (AccountsReceivable) -> surrogate + mezcla. Por moneda.
  - :64-67 `afipEligiblePending` usa `TotalSale - alreadyInvoiced` -> mezcla; por moneda.
  - :90-104 cash in/out (`p.Amount`, `m.Amount`) -> **caja real**: agrupar por la **moneda real** del pago (`Payment.Currency`), no por la imputada. Es el arqueo de lo que efectivamente entro/salio.
  - :132-296 (listas/movimientos): exponer `Amount` con su moneda.

### 4.6 Alertas y cobranzas
- `AlertService` (src/TravelApi.Infrastructure/Services/AlertService.cs:112-119): `f.Balance > 0` (dispara alerta de deuda). El **disparo** sigue OK con surrogate (cualquier moneda con saldo -> Balance > 0 -> alerta). El **monto** mostrado y el texto deben decir **en que moneda** debe -> leer `PorMoneda`. (`s.CurrentBalance` :127-135 es proveedores.)
- `OperationalFinanceMonitorService`, `MessageService`: idem; el disparo es correcto, el detalle por moneda mejora el texto.
- `PaymentService` :129, :155 (`pendingReservations.Sum(r.Balance)`, `urgentReservations.Sum(r.Balance)`): suma el surrogate entre reservas para un total de "pendiente de cobro" -> mezcla. Por moneda.

### 4.7 Facturacion (NO se toca, decision dueno #4)
- `InvoiceService`, `AfipService`: ya manejan su moneda (`Invoice.MonId`). El gate AFIP usa `IsEconomicallySettled` (surrogate, OK). El medio/moneda de pago **no** altera la factura emitida (§2.7).

### 4.8 Frontend
- `formatCurrency` (src/TravelWeb/src/lib/utils.js) y todos sus call sites; vistas de plata enumeradas en §2.6. Gate UX.

### 4.9 Tests
- `ReservaMoneyCalculatorTests`, `FaseDStateSetTests`, `AssistanceBalanceRecalcTests`, `*CostMaskingTests`, `PaymentServiceRegistrationTests`, `CancellationDeferredPenaltyTests`: agregar casos multimoneda + cobro cruzado; verificar que el caso ARS puro da **identico** resultado que hoy (regresion byte-equivalente).

---

## 5. Respuesta a la autocritica de la version anterior

### 5.1 ¿El surrogate de `Balance` enga​na a algun consumidor que lo lee como MONTO? — SI, listado completo

Validado contra todos los consumidores del alcance ampliado. Clasificacion:

**Lo leen como FLAG booleano (`<= 0` / `> 0`) — el surrogate es CORRECTO, no migran:**
- `ReservationEconomicPolicy.IsEconomicallySettled` (Balance <= 0).
- `ReservaLifecycleAutomationService.cs:241` (auto-cierre, Balance <= 0 en SQL).
- `EconomicRulesHelper` (gates voucher/AFIP/operative/archive).
- `ReservaService.ApplyEconomicFlags` (`IsFullyPaid = Balance == 0`, `HasOverdueDebt = Balance > 0`).
- `AlertService.cs:112`, `ReportService.cs:88`, `TreasuryService.cs:36/40` (filtros `Balance > 0` / `<= 0` para decidir si listar): el **predicado** es correcto con surrogate.

**Lo leen/suman como MONTO adeudado — el surrogate los ENGA​NA, migran a `PorMoneda`:**
- `CustomerService.cs:43-45,69-71,283-285,302` — `CurrentBalance`/`TotalBalance` del cliente = `Sum(r.Balance)` entre reservas. **Confirmado: interpreta el total como deuda en plata.** Bajo surrogate sumaria flags entre monedas = sin sentido. **Pieza mas riesgosa.** -> saldo del cliente **por moneda**.
- `ReportService.cs:69,103,194,250` — `outstandingBalance`/`PendingBalance` y el monto en la lista de pendientes = `f.Balance`. -> por moneda.
- `TreasuryService.cs:37` — `AccountsReceivable = Sum(r.Balance)`. -> por moneda.
- `PaymentService.cs:129,155` — `pendingAmount`/`urgentPendingAmount = Sum(r.Balance)`. -> por moneda.

**Decision**: estos cuatro NO pueden quedar leyendo el escalar. Se reescriben para **agregar contra la tabla hija `ReservaMoneyByCurrency`** (`Sum`/`OrderBy`/`Where` por `Currency` en SQL). En el plan incremental (§9) su migracion es parte del alcance (decision 2 del dueno: "todo de una"), no deuda diferida. Hasta que se migren, exponen el surrogate **solo como flag** (¿tiene deuda si/no?) y el **monto** se oculta o se marca "ver detalle por moneda", nunca un total mezclado enga​noso.

### 5.2 ¿Como se persiste el detalle por moneda? — TABLA HIJA MATERIALIZADA (corregido por B1)

> La version anterior afirmaba que `PorMoneda` se arma **solo on-read sin tabla hija**. **B1 del review demostro que es inviable** para los agregados cross-reserva que corren en SQL (§2.3). Esta version lo corrige: el detalle por moneda **se persiste** en `ReservaMoneyByCurrency` (§2.3.0).

Puntos de escritura del materializado — **UNO solo tras consolidar (B5)**:
- Las **TRES** rutinas que hoy recalculan y persisten el escalar de la `Reserva` — `ReservaService.RecalculateMoneyAsync` (ReservaService.cs:2512-2537), `PaymentService.RecalculateReservaBalanceAsync` (PaymentService.cs:797-820) **y `AfipService.RecalculateReservaBalanceAsync` (AfipService.cs:1686-1709, disparada por la reversa de NC en AfipService.cs:1440)** — se consolidan en `ReservaMoneyPersister.PersistAsync` (§4.1). Esa unica rutina llama `ReservaMoneyCalculator.Calculate(reserva)`, persiste los 5 escalares (con `Balance` = surrogate) **y sincroniza las filas hijas** de `ReservaMoneyByCurrency` desde `summary.PorMoneda` (upsert por `Currency`, borrar monedas ausentes), todo en la **misma** `SaveChangesAsync`. Asi el materializado se escribe por UN solo camino: escalar y hija nunca divergen, y el path AFIP (facturar / anular NC) deja de actualizar el escalar sin reescribir la hija (el bug que B5 detecto).

Dos lecturas distintas, ambas coherentes:
- **Lectura del DTO de una reserva** (`MappingProfile`): sigue armandose del `summary` recalculado en el read (el objeto `ReservaMoneySummary` con `PorMoneda` en memoria). Para una sola reserva no hace falta tocar la tabla hija; el calculator ya tiene el detalle.
- **Lectura cross-reserva / cross-proveedor** (cuenta corriente, reportes, tesoreria, top-N, alertas): **consultan la tabla hija en SQL**. Es la unica forma de `Sum`/`OrderBy` por moneda entre muchas filas sin traer todo a memoria.

Confirmado: el surrogate `Balance` escalar SI se persiste (lo necesitan el job SQL y los gates), **y** el detalle por moneda tambien (tabla hija), porque los consumidores cross-entidad lo agregan en SQL. La fuente de verdad de la **logica** sigue siendo el calculator; la tabla hija es su proyeccion persistida, reescrita en cada recalculo (no diverge: misma transaccion).

---

## 6. Diseno de la adaptacion "por moneda" de cada consumidor cross-entidad

Patron general: donde hoy hay `SumAsync(x => x.Monto)` que mezcla, pasa a `GroupBy(Currency).Select(g => new { Currency, Total = g.Sum(...) })`. La clave es **que moneda usar** en cada eje:

- **Cuenta corriente del cliente** (`CustomerService`): saldo del cliente **por moneda**, agregado **en SQL contra `ReservaMoneyByCurrency`** (no contra un dict en memoria). `CurrentBalance` deja de ser un escalar mezclado; el DTO de cliente expone `List<{ Currency, Balance }>`. El saldo de cada moneda = `Sum(ReservaMoneyByCurrency.Balance)` filtrando por `Currency` y por las reservas vivas del cliente (solo positivos para "debe", o crudo para "saldo a favor", a definir con UX/contador).
- **Reportes financieros** (`ReportService`): ventas/costos/margen/pendiente **por moneda**. Un reporte mono-moneda se ve igual; multimoneda muestra columnas ARS/USD separadas. NO se convierte a base. El cash flow (:559) separa entradas/salidas por moneda real del pago.
- **Tesoreria / arqueo** (`TreasuryService`): dos ejes distintos, ambos por moneda:
  - **Cuentas por cobrar** (AccountsReceivable, AfipEligiblePending): por **moneda del saldo** (imputada).
  - **Caja real** (cash in/out): por **moneda real del pago** (`Payment.Currency`). Es lo que entro/salio fisicamente; el cobro cruzado entra a caja en su moneda real, no en la imputada.
- **Alertas/cobranzas** (`AlertService`, `MessageService`, `OperationalFinanceMonitorService`): disparo sin cambio (surrogate); texto/monto por moneda desde `PorMoneda`.
- **Exports contables/financieros** (`ReportService` Excel :339-389): columnas por moneda; jamas un total cross-moneda en una sola celda. El export es justamente lo que el contador usa para reconstruir; debe dejar los datos crudos por moneda (y, para pagos cruzados, las columnas de TC).

Decision para todos: **ningun agregado cross-reserva o cross-payment produce un unico total mezclando monedas**. O se separa por moneda, o (si una vista vieja no se migra todavia) se filtra a una sola moneda y se rotula. El default visual y el detalle exacto los define el gate UX.

### 6bis Top-N de deudores POR MONEDA (resuelve B2)

> **B2 del review.** Hoy los top-N ordenan por el escalar, que en multimoneda mezcla USD+ARS y produce un ranking sin sentido: `ReportService.cs:101` (top 5 reservas deudoras) y `AlertService.cs:135` (top 10 proveedores). Un escalar surrogate ni siquiera es monto (es semaforo). Decision: **el top-N es POR MONEDA, calculado contra la tabla hija materializada** (§2.3.0 / §15.3), no contra el escalar.

**Top-5 reservas deudoras (`ReportService.cs:100-103`)** pasa a:
```
// pseudo-LINQ contra la tabla hija; corre en SQL, indexado por (Currency, Balance)
var topUsd = await _context.ReservaMoneyByCurrency
    .Where(x => x.Currency == "USD" && x.Balance > 0
                && x.Reserva.Status != Cancelled && x.Reserva.Status != Budget && x.Reserva.Status != "Archived")
    .OrderByDescending(x => x.Balance)
    .Take(5)
    .Select(x => new PendingReservaDto(x.Reserva.PublicId, x.Reserva.NumeroReserva, x.Reserva.Name, x.Balance, "USD", x.Reserva.Status.ToString()))
    .ToListAsync(ct);
// idem topArs con Currency == "ARS"
```
El DTO `PendingReservaDto` gana un campo `Currency` (contrato aditivo). El reporte expone **dos listas** (top deudores USD y top deudores ARS) en vez de una mezclada. Si la instalacion es 100% ARS, la lista USD viene vacia y se ve igual que hoy. **El filtro de propietario** (`ResponsibleUserId == ownerFilter`, ReportService.cs:96) se preserva uniendo a `Reserva` en el `Where`.

**Top-10 proveedores deudores (`AlertService.cs:126-137`)** pasa a:
```
var topProvUsd = await _context.SupplierBalanceByCurrency
    .Where(x => x.Currency == "USD" && x.Balance > 100 && x.Supplier.IsActive)   // umbral: ver nota
    .OrderByDescending(x => x.Balance)
    .Take(10)
    .Select(x => new { x.Supplier.PublicId, x.Supplier.Name, Balance = x.Balance, Currency = "USD", x.Supplier.Phone })
    .ToListAsync(ct);
// idem ARS
```
Dos listas (proveedores con deuda USD / con deuda ARS). **Umbral `> 100`**: el `100` hoy esta en ARS implicito; con dos monedas el umbral por moneda **no es equivalente** (100 USD != 100 ARS). MVP: aplicar el mismo numero literal por moneda (`> 100` en cada una) y marcarlo como **decision abierta** para dueno/contador (¿umbral por moneda, o un unico umbral en una moneda de referencia?) — ya listado en §15.10. No se inventa una conversion.

**Regla general B2**: ningun top-N, ranking ni "mayor deudor" se calcula sobre el escalar surrogate. Siempre se filtra `Currency` y se ordena por `Balance` de esa moneda contra la tabla hija. Asi el orden es real dentro de cada moneda y nunca compara peras con manzanas.

---

## 7. Migracion

### 7.1 Reglas duras (leccion M2 que rompio produccion)
- La migracion M2 rompio prod por usar **nombre de propiedad** en SQL crudo cuando la columna real era otra: `Payment.ReservaId` mapea a columna `TravelFileId` y `ServicioReservaId` a `ReservationId` (verificado en AppDbContext.cs:483,497,525-526,574-575). El mismo trap aplica a las tablas de servicios.
- Las columnas NUEVAS de este ADR (`Currency`, `ImputedCurrency`, `ExchangeRate`, `ExchangeRateSource`, `ExchangeRateAt`, `ImputedAmount`) **no llevan `HasColumnName`** -> la columna se llama igual que la propiedad. Aun asi:
  - **Migracion generada por EF** (`dotnet ef migrations add Adr021_M1_AddMultiCurrency`), NO SQL crudo a mano.
  - Si hace falta backfill con SQL, usar **nombres de columna reales** verificados contra AppDbContext y `AppDbContextModelSnapshot`, y **probar la migracion contra Postgres**, no InMemory (la leccion M2: el raw SQL solo se probaba InMemory).
- Mapeo en AppDbContext con `HasDefaultValue("ARS")` para `Payment.Currency`, patron identico a `FiscalLiquidation.Currency` (AppDbContext.cs:1426) y a `Invoice.MonId` (:629). `ExchangeRateSource` se persiste como `int` (mismo patron que `FiscalSnapshot.Source`).

### 7.2 M1 — aditiva, byte-safe
1. `ServicioReserva.Currency` `string?` (sin default forzado; null = ARS al leer). Las filas existentes quedan null = ARS.
2. `Payment.Currency` `string` NOT NULL default BD `'ARS'`. Filas existentes -> ARS automaticamente. `SupplierPayment.Currency` idem (default BD `'ARS'`).
3. `Payment` y `SupplierPayment`: `ImputedCurrency` (string? 3) / `ExchangeRate` (`decimal?(18,6)`) / `ExchangeRateSource` (int?) / `ExchangeRateAt` (datetime?) / `ImputedAmount` (`decimal?(18,2)`) -> todas NULLABLE. Filas existentes quedan null (= pago no cruzado, imputa su propia moneda ARS).
4. **Tablas hijas nuevas** `ReservaMoneyByCurrency` y `SupplierBalanceByCurrency` (CREATE TABLE; FK + indices unico `(padre, Currency)` y `(Currency, Balance)`). Son tablas nuevas vacias -> creacion aditiva pura, sin tocar datos existentes.
5. **Sin** cambio de tipo ni nulabilidad sobre columnas con datos -> aditiva pura. (No se toca la precision de `Payment.Amount`/`SupplierPayment.Amount` — §2.2bis.)
6. **Backfill de las tablas hijas (obligatorio, no opcional):** tras crear las tablas, hay que **poblarlas** para los datos legacy, si no las cuentas corrientes/reportes (que ahora leen la hija) verian saldos en cero hasta el primer recalculo de cada entidad.

   > **Riesgo de alcance del barrido (endurecido).** Si el backfill recorre solo reservas/proveedores en **estados activos**, una entidad legacy con **saldo != 0 que nadie toca post-deploy** y que NO entre en ese barrido deja su hija **vacia** -> los consumidores que ahora leen la hija (cuenta corriente, reportes, top-N) la ven en **0**. Eso no es un error visible: es un **dato silencioso falso** (un cliente que debe aparece sin deuda), el peor tipo de bug porque no rompe nada, miente. **El surrogate escalar legacy seguiria mostrando deuda (> 0), pero los consumidores migrados ya no lo leen para el monto -> la hija vacia gana.**

   **Regla del barrido (FIJA):** el backfill recorre **TODAS las reservas con `Balance != 0` (escalar legacy) y TODOS los proveedores con `CurrentBalance != 0`**, sin filtrar por estado. El criterio no es "esta activa" sino "tiene plata pendiente". Una entidad con saldo `0` no necesita fila hija (no aporta a ningun agregado de deuda); una con saldo `!= 0` la necesita aunque este en un estado que hoy nadie mira, porque cualquier reporte/cuenta corriente que la agregue debe verla con su monto real, no en cero.
   - **Estados explicitamente excluidos (si los hay): se listan y se justifica por que no rompen reportes.** Hoy NO se excluye ninguno por estado: el filtro es puramente `saldo != 0`. Si en impl se decide excluir alguno (ej. reservas `Cancelled`/`Archived` con saldo residual que los reportes ya excluyen *por su propio Where de estado*), debe verificarse que **ningun** consumidor migrado de §6/§6bis los agregue; si alguno los agrega, NO se excluyen del backfill. La carga de la prueba es del que excluye.

   Dos opciones de implementacion, elegir en impl:
   - (a) **Recalculo programatico post-migracion (preferida)**: un paso de arranque/seed que recorre **todas** las reservas con `Balance != 0` y **todos** los proveedores con `CurrentBalance != 0` (sin filtro de estado) y llama `ReservaMoneyPersister.PersistAsync` / `RecalculateAllBalancesAsync` (que ahora sincronizan la hija). Es la mas segura: reusa la unica logica de calculo, no duplica formulas en SQL, y al pasar por `PersistAsync` garantiza que escalar y hija quedan coherentes de entrada (mismo invariante R-16 que en operacion normal).
   - (b) SQL de backfill directo: solo si (a) no es viable; con **nombres de columna reales** verificados (el trap M2: `Payment.ReservaId`->`TravelFileId`, `ServicioReservaId`->`ReservationId`; AppDbContext.cs:525-526, 1141-1142) y **probado contra Postgres**, no InMemory.
   - Como todo legacy es ARS, el backfill genera una sola fila ARS por reserva/proveedor con saldo `!= 0` (ninguna si esta saldado en `0`).
   - **Criterio de exito (cubre el universo completo):** para **toda** reserva con `Balance != 0` y **todo** proveedor con `CurrentBalance != 0`, `sum(filas hijas de esa entidad)` reproduce el escalar legacy de esa entidad. La verificacion recorre el **mismo universo `saldo != 0`** que el barrido (no una muestra de activos): si quedo una sola entidad con saldo legacy sin fila hija que lo reproduzca, la migracion NO es exitosa. Conteo de control: `count(reservas con Balance != 0)` debe igualar `count(distinct ReservaId en ReservaMoneyByCurrency)` (idem proveedor).
7. Normalizar `ServicioReserva.Currency = null -> 'ARS'` en datos es opcional (el calculo ya normaliza null); si se hace, mismas reglas de columna real + Postgres.

### 7.3 Compatibilidad
- API: contrato **aditivo** (`PorMoneda`, `EsMultimoneda` nuevos; escalares se mantienen). Front viejo sigue leyendo escalares.
- Datos: todo legacy queda en ARS = comportamiento identico al actual.

---

## 8. Validaciones de negocio (reglas minimas)

1. **Moneda del servicio**: costo y venta SIEMPRE en la misma moneda (decision dueno #1). No hay modelo ni UI para costo USD / venta ARS en un mismo servicio.
2. **Editar la moneda de un servicio**: permitida solo si la reserva NO esta bajo candado (estados Confirmed+ de ADR-020 requieren `ReservaEditAuthorization`). En estados abiertos (Quotation/Budget/InManagement) es libre.
3. **Cambiar la moneda de un servicio con pagos imputados a su moneda**: **bloquear** (dejaria pagos imputados a una moneda que el servicio ya no tiene). Regla MVP: bloquear si el servicio tiene pagos vivos imputados en otra moneda.
4. **Moneda del pago y de imputacion**: ambas deben estar en `Monedas.Soportadas`. Validar server-side (no confiar en el front).
5. **Pago cruzado** (`Currency != ImputedCurrency`): `ExchangeRate > 0`, `ExchangeRateSource != Unset` y `!= null`, `ExchangeRateAt != null`, `ImputedAmount > 0` son **obligatorios**. Rechazar el pago si falta cualquiera (espejo del CHECK `chk_..._fiscalsnapshot_consistent` de FC1: no se persiste TC sin fuente/justificacion).
6. **Pago no cruzado** (`Currency == ImputedCurrency` o `ImputedCurrency` null): el bloque TC debe quedar **null** (no se acepta TC en un pago de misma moneda). `ImputedAmount` null -> imputa `Amount`.
7. **Pago en moneda sin saldo** (ej. pago USD imputado a USD en reserva 100% ARS): permitido (queda saldo a favor USD); NO compensa otra moneda (surrogate §2.4).
8. **Caja vs imputacion**: `Amount`+`Currency` (caja real) son inmutables una vez creado el pago; corregir un pago cruzado = anular y recrear (no editar el TC de un pago ya aplicado, igual que no se reescribe una factura). A confirmar con contador (§13).

---

## 9. Orden de implementacion (por capas, incremental)

Cada paso es mergeable y deja el sistema verde. Pasos 1-5 NO cambian nada visible (todo sigue en ARS) hasta el frontend (paso 7).

1. **Dominio + migracion**: clase `Monedas`; `Currency` en `ServicioReserva`; `Currency` + bloque TC (`ExchangeRate` 18,6; `ImputedAmount` 18,2) en `Payment` **y `SupplierPayment`**; **tablas hijas `ReservaMoneyByCurrency` + `SupplierBalanceByCurrency`** (con indices `(padre,Currency)` unico y `(Currency,Balance)`); mapeo AppDbContext (`HasDefaultValue("ARS")`, `ExchangeRateSource` como int, FK Cascade); migracion `Adr021_M1_AddMultiCurrency` (EF, aditiva) + **backfill de las hijas por recalculo programatico** (§7.2.6). **Probar contra Postgres.** Sin cambio de comportamiento (nadie carga USD ni cruza).
2. **Calculator**: `ReservaMoneyLine` + `PorMoneda`/`EsMultimoneda`; `Calculate` agrupa servicios por su moneda y pagos por moneda imputada (`ImputedAmount ?? Amount`, convencion TC 2.2bis); escalares = compat (Balance = surrogate). Espejo proveedor: `CalculateSupplierDebtPorMoneda` + `Currency` en el `Select` de `BuildSupplierServicesQuery` (los 6 branches, §15.4). Tests: ARS puro identico a hoy + multimoneda + cobro/pago cruzado.
3. **Persistencia del materializado (rutina CONSOLIDADA, B5)**: extraer `ReservaMoneyPersister.PersistAsync` (§4.1) y enrutar por ella las **TRES** copias del recompute del escalar — `ReservaService.RecalculateMoneyAsync` (:2512-2537), `PaymentService.RecalculateReservaBalanceAsync` (:797-820) **y `AfipService.RecalculateReservaBalanceAsync` (:1686-1709)** — para que las tres persistan escalar surrogate **y sincronicen `ReservaMoneyByCurrency`** en la misma `SaveChangesAsync`. Eje proveedor: `RecalculateAllBalancesAsync`/`UpdateBalanceAsync`/`Delete...` sincronizan `SupplierBalanceByCurrency`, **eliminando el add-back imperativo `currentDebt + payment.Amount`** (§15.6bis). Test de invariante R-16 incluyendo el camino AFIP (emitir/anular factura -> verificar sum(hija)==PorMoneda==escalar surrogate).
4. **Registro de pago (backend)**: `RegisterPayment` setea `Currency`/imputacion/TC desde el request; validaciones §8 server-side. Sin UI todavia (default ARS, no cruzado -> identico a hoy).
5. **DTO/API** (aditivo): `PorMoneda`/`EsMultimoneda` en `ReservaDto`/`ReservaListDto` + `MappingProfile`.
6. **Consumidores cross-entidad por moneda** (alcance ampliado, decision 2): migrar `CustomerService` (cuenta corriente), `ReportService` (reportes/cash flow/exports), `TreasuryService` (AR + caja real), alertas/cobranzas a leer `PorMoneda` / agrupar por `Currency`. Backend solo (los DTOs nuevos por moneda existen; el front viejo los ignora). Aca se cierra la autocritica §5.1.
7. **Frontend** (ULTIMO, tras gate UX con Gaston): `formatCurrency(amount, currency)`; selector de moneda en servicio y en pago; bloque TC cuando el pago cruza; columnas por moneda en header, lista, reportes, arqueo, cuenta corriente. **Nada de "diferencia de cambio".**

Incremental real: 1-6 pueden ir a prod sin cambio visible (todo ARS). El cambio operativo aparece en el paso 7, cuando el selector de moneda y de cobro cruzado se habilita en el front.

---

## 10. Rollback

- **Codigo**: calculo por-moneda, surrogate e imputacion son deterministas; revertir el commit restaura el calculo escalar. Una reserva mono-moneda ARS sin pagos cruzados da el mismo `Balance` antes y despues -> rollback no corrompe datos ARS. La tabla hija es un materializado **derivado**: revertir el codigo deja de leerla/escribirla; el escalar surrogate sigue siendo correcto para los datos ARS.
- **Migracion**: las columnas y las **tablas hijas** (`ReservaMoneyByCurrency`, `SupplierBalanceByCurrency`) son aditivas; el down-migration dropea las columnas y hace DROP TABLE de las hijas. Los importes en `Reserva`/`Supplier`/pagos nunca se movieron de columna -> dropear las hijas no pierde datos crudos (la hija es derivable por recalculo).
- **Punto de no retorno**: una vez que existan servicios/pagos en USD **o pagos cruzados** (con TC persistido en `Payment`/`SupplierPayment`), el rollback de schema **pierde** esa info (moneda + TC + imputado), que son **datos crudos** no reconstruibles. La tabla hija SI es reconstruible (recalculo), pero el bloque de moneda/TC del pago no. Rollback seguro solo **antes del primer dato USD / primer pago cruzado**. Documentar y avisar.

---

## 11. Riesgos

| ID | Riesgo | Severidad | Mitigacion |
|---|---|---|---|
| R-1 | Romper auto-cierre / gates de saldo | Alto | Surrogate (§2.4) preserva `IsEconomicallySettled` y el query SQL. Test: reserva multimoneda con deuda USD NO se auto-cierra. |
| R-2 | **Surrogate enga​na a consumidores que leen `Balance` como monto** (cuenta corriente, reportes, tesoreria, top-N) | **Alto** | Identificados todos (§5.1): `CustomerService`, `ReportService`, `TreasuryService`, `PaymentService`. Migran a **agregar contra la tabla hija `ReservaMoneyByCurrency` en SQL** (B1) en el paso 6 (alcance, no deuda). Hasta entonces exponen el surrogate solo como flag, nunca como total. |
| R-16 | **Tabla hija materializada se desincroniza del calculator** (un recalculo no reescribe la hija, o falla parcial) | **Alto** | La hija se reescribe en la **misma `SaveChangesAsync`** que el escalar, desde la **misma rutina** (`RecalculateMoneyAsync`/`RecalculateReservaBalanceAsync`/`CalculateSupplierDebt`), nunca por otro camino. Test de invariante: tras cualquier mutacion (pago alta/baja/edit, servicio alta/baja/cambio moneda), `Sum(hija.Balance por moneda)` reproduce el `PorMoneda` del calculator recalculado en el momento, y `surrogate == sum(max(0, hija.Balance))`. Backfill por recalculo programatico (§7.2.6a) garantiza estado inicial coherente. |
| R-17 | **Backfill de la hija deja saldos en cero** para datos legacy hasta el primer recalculo | Medio | Backfill obligatorio post-migracion (§7.2.6), preferentemente por recalculo programatico que reusa la unica logica. Verificacion: `sum(hija)` == escalar legacy antes de dar la migracion por exitosa. |
| R-18 | **Backfill recorre solo estados activos -> entidad legacy con saldo != 0 que nadie toca deja su hija vacia = dato silencioso falso** (deuda que aparece en cero en reportes/cuenta corriente) | **Alto** | Barrido FIJO por **`saldo != 0` sin filtro de estado** (§7.2.6): TODAS las reservas con `Balance != 0` y TODOS los proveedores con `CurrentBalance != 0`. Cualquier estado excluido se lista y se prueba que ningun consumidor migrado lo agrega. Criterio de exito sobre el **universo completo** (count reservas con saldo != 0 == count distinct ReservaId en la hija). |
| R-19 | **Tercer punto de escritura del escalar (AfipService, path reversa de NC) no sincroniza la hija -> desincronizacion silenciosa post-facturacion/NC** | **Alto** | **B5 resuelto:** las 3 rutinas (`ReservaService`/`PaymentService`/`AfipService.RecalculateReservaBalanceAsync`) se consolidan en `ReservaMoneyPersister.PersistAsync` (§4.1), unico punto de escritura de escalar + hija en una `SaveChangesAsync` atomica. Grep confirma que no queda `.Balance =`/`.ConfirmedSale =` sobre `Reserva` fuera del persister. Test R-16 incluye el camino AFIP (emitir/anular NC -> hija sincronizada). |
| R-3 | Migracion rompe prod como M2 | Alto | Columnas sin `HasColumnName`; migracion EF, no SQL; columnas reales verificadas; probar contra Postgres. |
| R-4 | `formatCurrency` hardcodeado a una moneda | Medio | Parametrizar revisando TODOS los call sites. Gate UX + reviewer que verifique guia-ux-gaston. |
| R-5 | Saldo a favor de una moneda compensando deuda de otra | Medio | Surrogate usa `max(0, line.Balance)` por moneda: deber USD nunca se cancela con sobrepago ARS. |
| R-6 | Servicio editado de ARS a USD con pagos imputados ARS | Medio | Validacion §8.3: bloquear cambio de moneda si hay pagos imputados en otra moneda. |
| R-7 | **Cobro cruzado mal capturado** (TC sin fuente/fecha, o pago que se "reparte") | Alto | Validaciones §8.5/§8.6 server-side (TC+fuente+fecha obligatorios si cruza; null si no cruza); un pago = una sola moneda imputada (combo = 2 pagos). Reusa el rigor del CHECK fiscal de FC1. |
| R-8 | Mezclar "caja real" con "imputado al saldo" en tesoreria | Alto | Dos ejes separados (§6): arqueo por `Payment.Currency`; cuentas por cobrar por `ImputedCurrency`. Nunca el mismo numero. |
| R-9 | Diferencia de cambio mostrada/calculada por error | Alto | Limite contable (§1): el sistema NUNCA materializa diferencia de cambio. Reviewer UX y backend lo verifican. La reconoce el contador en el cierre. |

---

## 12. Testing strategy

- **Regresion ARS puro** (critico): toda reserva 100% ARS sin pagos cruzados da `Balance/ConfirmedSale/TotalPaid` **identicos** a hoy. Parametrizado en `ReservaMoneyCalculatorTests`.
- **Multimoneda**: reserva con servicios ARS + USD; `PorMoneda["ARS"]`/`["USD"]` independientes; surrogate `Balance` = 0 sii ambas saldadas.
- **Cobro cruzado**: pago ARS imputado a saldo USD con TC manual (convencion 2.2bis: `ImputedAmount[USD] = round2(Amount_ARS / ExchangeRate)`) -> baja `PorMoneda["USD"].TotalPaid` por `ImputedAmount`; caja registra `Amount` en ARS; ninguna pantalla muestra diferencia de cambio. Caso simetrico USD->ARS (`ImputedAmount[ARS] = round2(Amount_USD * ExchangeRate)`).
- **Reversa/anulacion cruzada (B3)**: anular un pago cruzado -> `PorMoneda[ImputedCurrency].Balance` vuelve al valor previo (sube por `ImputedAmount`, NO por `Amount`); la caja de `Currency` baja por `Amount`; ningun "diferencia de cambio". Editar `Amount` de un pago cruzado -> **rechazado** (forzar anular+recrear, §2.8.3). Espejo proveedor: anular `SupplierPayment` cruzado sube la deuda de `ImputedCurrency` por `ImputedAmount` (verificar que NO se usa el viejo `currentDebt + payment.Amount`, §15.6bis).
- **Tabla hija materializada (B1, invariante R-16)**: tras cada mutacion (pago alta/baja/edit, servicio alta/baja/cambio de moneda), `Sum(ReservaMoneyByCurrency.Balance por moneda)` == `PorMoneda` del calculator recalculado; `Reserva.Balance` (escalar) == `sum(max(0, hija.Balance))`. Idem `SupplierBalanceByCurrency` vs `CalculateSupplierDebtPorMoneda`.
- **Camino AFIP / reversa de NC en la rutina consolidada (B5)**: emitir una factura y luego **anular** la NC (que dispara `AfipService.RecalculateReservaBalanceAsync` -> `ReservaMoneyPersister.PersistAsync` via AfipService.cs:1440) -> verificar que `sum(ReservaMoneyByCurrency.Balance por moneda) == PorMoneda del calculator == Reserva.Balance` (escalar surrogate); es decir, que la reversa de NC NO deja la hija con saldo viejo. Caso multimoneda: reserva con saldo USD+ARS, factura+anula NC -> ambas filas hijas quedan sincronizadas con el calculator. Es la regresion directa del bug que B5 cerro (escalar actualizado pero hija desactualizada por el path AFIP).
- **Punto de escritura unico (B5)**: test/guard que confirme que el escalar `Reserva.Balance`/`ConfirmedSale` solo se escribe via `ReservaMoneyPersister.PersistAsync` (las tres rutinas delegan; no queda ningun `reserva.Balance =`/`.ConfirmedSale =` suelto fuera del persister). Verificable por grep en CI o por revision.
- **Top-N por moneda (B2)**: con reservas deudoras en ARS y USD, el top-5 USD y el top-5 ARS rankean **dentro** de su moneda (no mezclan); idem top-10 proveedores. Regresion: instalacion 100% ARS -> lista USD vacia, lista ARS identica al top-5 de hoy.
- **Validaciones §8**: pago cruzado sin TC/fuente/fecha -> rechazado; pago no cruzado con TC -> rechazado; cambiar moneda de servicio con pagos imputados en otra moneda -> rechazado; moneda no soportada -> rechazada.
- **Surrogate / auto-cierre**: reserva Traveling con EndDate pasada y saldo USD pendiente NO se auto-cierra (`ReservaLifecycleAutomationService`); con todo saldado, si.
- **Consumidores cross-entidad** (paso 6): `CustomerService` devuelve saldo del cliente por moneda **agregado contra la tabla hija** (no un total mezclado); `ReportService`/`TreasuryService` agrupan por moneda; arqueo usa `Payment.Currency`/`SupplierPayment.Currency`, cuentas por cobrar/pagar usan `ImputedCurrency`. Deuda de proveedor incluye servicios **genericos** (`ServicioReserva`) con su `Currency` (§15.4).
- **Gates**: voucher/AFIP/operative bloqueados si cualquier moneda debe.
- **Saldo a favor cruzado** (R-5): sobrepago ARS no cancela deuda USD.
- **Migracion contra Postgres** (no InMemory): aplicar M1 sobre base con datos legacy y verificar que ningun importe se movio y que el default `'ARS'` quedo en `Payment.Currency`.

---

## 13. Lo que este ADR NO cubre / requiere contador matriculado (explicito)

- **Diferencia de cambio** como resultado contable (factura USD a un TC vs cobro ARS a otro): el sistema deja datos crudos; el reconocimiento es del contador en el cierre. NO se materializa en el sistema. **Requiere contador.**
- **Base imponible IVA/IIBB en moneda extranjera**, percepciones/retenciones sobre operaciones USD, y como impacta el cobro cruzado en esos calculos. **Requiere contador.**
- **Bancarizacion / medios de pago** (limites de efectivo, registro AFIP del medio) cuando el pago cruza moneda. **Requiere contador.**
- **Convencion del TC** (cual cotizacion aplica al cobro cruzado: comprador/vendedor, dia, fuente) mas alla de "manual con fuente registrada". El enum ofrece las opciones; cual es la correcta por caso lo valida el contador. **Requiere contador.**
- **Inmutabilidad del pago cruzado** (§8.8: anular+recrear vs editar TC). A confirmar con contador.
- **Moneda en cuentas por pagar a proveedores** (`SupplierService.CurrentBalance`): **RESUELTO** — el dueno confirmo que entra al mismo alcance (decision 3, §0). Mapeado en profundidad en **§15**. Lo que queda "requiere contador" de ese eje (diferencia de cambio en pago a proveedor USD, tratamiento de la deuda en ME, retenciones al pagar) esta listado al final de §15.
- **Multimoneda en facturacion**: ya existe (`Invoice.MonId`/`MonCotiz`/`ExchangeRateSource`), no se toca (decision dueno #4).
- **EUR/BRL u otras monedas**: el catalogo es solo ARS/USD. Sumar una moneda es una linea en `Monedas` + `ArcaCurrencyMapper` + homologacion ARCA, fuera de este ADR.

---

## 14. Alternativas consideradas

- **A) Convertir todo a una moneda base (ARS) con TC**: rechazada por decision dueno #3 (totales separados, sin conversion en pantalla) y porque centraliza la diferencia de cambio en el sistema, que el contador quiere reconocer en el cierre, no en la operativa.
- **B) Columnas por moneda en `Reserva` (BalanceArs, BalanceUsd, ConfirmedSaleArs, ConfirmedSaleUsd, TotalPaidArs/Usd, TotalCostArs/Usd, ...)**: **considerada y rechazada en esta segunda vuelta (B1)**. Resuelve el problema de SQL (los agregados por moneda serian columnas reales sumables), pero: (1) multiplica el ancho de la tabla raiz por cada metrica x cada moneda (hoy 8 columnas ARS/USD, y cada moneda futura suma otras 4-5 columnas + migracion sobre tabla grande); (2) los top-N por moneda (B2) necesitarian `OrderByDescending(BalanceUsd)` con una columna distinta por moneda — no generaliza; (3) un `Where(... > 0)` "en cualquier moneda" se vuelve un OR de N columnas. Escala mal con monedas y con metricas.
- **C) Tabla hija `ReservaMoneyByCurrency` materializada**: **ELEGIDA (B1)**. Una fila por `(reserva, moneda)` con las 5 metricas. Los agregados cross-reserva por moneda corren en SQL contra la hija (`Where(Currency=='USD').Sum(Balance)`, `OrderByDescending(Balance).Take(5)` por moneda — exactamente lo que B1/B2 piden), con indice `(Currency, Balance)`. Agregar una moneda futura es **filas**, no columnas ni migracion de esquema. El "riesgo de desincronizacion" que la version anterior temia se neutraliza porque la hija **se reescribe en la misma transaccion y desde la misma rutina** que ya persiste el escalar (`RecalculateMoneyAsync`/`RecalculateReservaBalanceAsync`); no es una segunda fuente de verdad sino una proyeccion del calculator (igual que el escalar hoy). Espejo proveedor: `SupplierBalanceByCurrency` (§15.3).
  - **Por que C y no B**: B paga el costo en ancho de tabla y en queries que no generalizan (una columna por moneda); C lo paga en una tabla hija chica (2 filas/reserva) con queries uniformes por `Currency`. Para el eje del top-N por moneda (B2), C es estrictamente mas simple. Ambas persisten, que es lo que B1 exige; C es la que escala.
- **D) Reescribir el `Balance` escalar a per-moneda y migrar TODOS los consumidores de golpe**: rechazada. El surrogate preserva los gates y el job SQL con cambio minimo; los consumidores que leen monto (4 identificados) migran por capa en el paso 6, no todos a la vez.
- **E) Convertir el pago cruzado a la moneda del saldo y guardar solo el equivalente** (perder la moneda/monto real de caja): rechazada por el contador. La caja es sagrada: se guarda el monto y moneda reales y el equivalente imputado por separado, para que el arqueo cuadre con lo que entro fisicamente y la diferencia de cambio sea reconstruible.

---

## 15. Eje DEUDA CON PROVEEDORES / OPERADORES (cuentas por pagar) por moneda (DECISION 3 del dueno)

Este eje es el espejo del eje cliente: en vez de "cuanto nos debe el cliente" es "cuanto le debemos al operador". El diseno reusa exactamente el mismo patron (moneda derivada del servicio; pago saliente con moneda + TC cruzado; surrogate para el flag de saldo; agregados por moneda en lugar de sumas mezcladas).

### 15.1 Estado actual del modulo de proveedores (VERIFICADO en codigo)

**SI existe un modulo formal de cuentas por pagar** (no es solo derivado del NetCost; hay entidad de pago saliente, servicio dedicado, controller y vistas). Hechos:

- **`Supplier`** (src/TravelApi.Domain/Entities/Supplier.cs:36): tiene `decimal CurrentBalance` (comentario en :35 "what we owe them"; en :36 "Positive = they owe us" es del Customer, no de aca — el del Supplier es deuda de la agencia HACIA el proveedor). **Es un escalar SIN moneda**, exactamente el mismo problema que `Reserva.Balance`.
- **`SupplierPayment`** (src/TravelApi.Domain/Entities/SupplierPayment.cs): pago saliente al proveedor. Tiene `decimal Amount` (:24) **SIN `Currency` ni bloque TC**. Links opcionales a `ReservaId`/`ServicioReservaId`. Soft-delete con auditoria (:40-42). Es el gemelo de `Payment` del lado egreso.
- **`SupplierService.CalculateSupplierDebt`** (src/TravelApi.Infrastructure/Services/SupplierService.cs:927-937): **deuda = `CalculateSupplierConfirmedPurchasesAsync` (suma de `NetCost` de servicios confirmados, regla `WorkflowStatusHelper.CountsForSupplierDebtByType`) menos `SUM(SupplierPayments.Amount)`**. **Aca esta el cruce de monedas**: el `NetCost` de cada servicio ya tiene su `Currency` (los 5 tipados; ver §2.2), pero la resta suma todos los NetCost (USD + ARS) y les resta todos los pagos (USD + ARS) como numeros pelados. Mismo defecto que `ReservaMoneyCalculator` pre-ADR.
- **`SupplierService.CalculateSupplierConfirmedPurchasesAsync`** (:919-925): materializa los servicios del proveedor en memoria y suma `NetCost` con la regla por tipo. **Aca se agrupa por `Currency` del servicio** (igual que el calculator del cliente agrupa por moneda del servicio).
- **`OperatorRefundReceived`** (src/TravelApi.Domain/Entities/OperatorRefundReceived.cs): el ingreso que el operador devuelve en una cancelacion **YA es multimoneda** (`Currency` :61, `ExchangeRateAtReceipt` :64). Es del flujo de cancelacion (FC1/ADR-002), aparte de la cuenta corriente normal. **No se toca**; sirve de precedente (ya hay moneda + TC en el lado proveedor para refunds).
- **`ServicioReserva`** (servicio generico legacy): tiene `NetCost` (:56) pero **NO** `Currency` (verificado) — mismo gap que en el eje cliente; lo cubre la columna nueva de §2.2.

**Conclusion**: la deuda al proveedor **NO se deriva solo del NetCost**; hay un modulo real (Supplier + SupplierPayment + SupplierService) con su propio escalar de saldo cacheado (`Supplier.CurrentBalance`). El gap multimoneda es identico al del cliente y se resuelve con el mismo patron.

### 15.2 Moneda de la deuda y del pago saliente

- **Deuda por servicio**: esta en la **moneda del servicio** (`booking.Currency`, normalizado `null -> ARS`). La deuda total al operador es un dict por moneda, igual que `PorMoneda` del cliente. No hay decision nueva: la moneda ya vive en el servicio.
- **Pago saliente (`SupplierPayment`)**: necesita los mismos campos que `Payment` del cliente. Caso tipico: la agencia debe USD 500 al operador y le transfiere en USD (no cruzado) — o debe USD 500 y paga en ARS a un TC (cruzado, simetrico al cobro cruzado §2.7). La **caja real** (lo que efectivamente salio) es `Amount`+`Currency`; lo que **baja la deuda** del operador es `ImputedAmount` sobre `ImputedCurrency`.

### 15.3 Modelo de datos (eje proveedor)

**`SupplierPayment`** — agregar el mismo bloque que `Payment` (§2.2), con la misma convencion:
```
[MaxLength(3)]
public string Currency { get; set; } = Monedas.ARS;          // moneda REAL del egreso (lo que salio de caja). NOT NULL default ARS.

// --- Imputacion cruzada (solo si Currency != ImputedCurrency) ---
[MaxLength(3)]
public string? ImputedCurrency { get; set; }                  // moneda de la DEUDA al operador a la que se imputa. null = se imputa a su propia moneda.

[Column(TypeName = "decimal(18,2)")]
public decimal? ExchangeRate { get; set; }

public ExchangeRateSource? ExchangeRateSource { get; set; }

public DateTime? ExchangeRateAt { get; set; }

[Column(TypeName = "decimal(18,2)")]
public decimal? ImputedAmount { get; set; }                   // equivalente que baja de la deuda en ImputedCurrency.
```
Misma convencion §2.2: un pago saliente se imputa a UNA moneda de deuda; combo = dos pagos. `Amount`+`Currency` (caja real, egreso) son sagrados.

**`Supplier.CurrentBalance`** — se redefine como **surrogate gemelo** del `Reserva.Balance` (§2.4): flag de "tiene deuda pendiente con este proveedor", NO monto. Se mantiene escalar porque hay consumidores que lo leen como booleano/orden (alertas, ordenamiento de la lista, filtro `CurrentBalance > 0`) y un query SQL no puede evaluar un dict.

```
Supplier.CurrentBalance (escalar) = sum_por_moneda( max(0, deudaProveedor[moneda]) )
```
Es `0` sii ninguna moneda debe. Saldo a favor de una moneda (sobrepago al operador) no compensa deuda de otra.

> **B1 aplica al eje proveedor.** El reviewer verifico que `AlertService.cs:127/135` ordena/filtra `CurrentBalance` en SQL (top-10 proveedores) y `ReportService.cs:230-238` suma/ordena deuda de proveedor en SQL. El detalle por moneda del proveedor **NO** puede ser solo on-read (igual razon que el cliente). Se persiste en una **tabla hija materializada** espejo de `ReservaMoneyByCurrency`:

```
// src/TravelApi.Domain/Entities/SupplierBalanceByCurrency.cs (nueva entidad)
public class SupplierBalanceByCurrency
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;        // "ARS" | "USD"

    [Column(TypeName = "decimal(18,2)")] public decimal ConfirmedPurchases { get; set; }  // NetCost confirmado en esta moneda
    [Column(TypeName = "decimal(18,2)")] public decimal TotalPaid { get; set; }           // imputado a esta moneda
    [Column(TypeName = "decimal(18,2)")] public decimal Balance { get; set; }             // ConfirmedPurchases - TotalPaid de esta moneda
}
```
- Indice unico `(SupplierId, Currency)`; indice `(Currency, Balance)` para el top-N por moneda (B2) y los `Where(Balance > 0)` por moneda.
- **Quien la escribe**: `RecalculateAllBalancesAsync` (SupplierService.cs:356-366), `UpdateBalanceAsync` (:368-375), `Add/Update/DeleteSupplierPaymentAsync`. Tras `CalculateSupplierDebtPorMoneda`, ademas de setear el surrogate escalar, **sincroniza** las filas hijas (upsert por `Currency`, borrar monedas ausentes) en la misma `SaveChangesAsync`. Proyeccion del calculo, no segunda fuente de verdad.
- **Cascade delete** al borrar el `Supplier`.

`SupplierPaymentRequest` (record, src/TravelApi.Application/Interfaces/ISupplierService.cs:24-31) — agregar `Currency`, `ImputedCurrency`, `ExchangeRate`, `ExchangeRateSource`, `ExchangeRateAt`, `ImputedAmount` (los ultimos cinco opcionales; default no cruzado ARS).

#### Shape de datos nuevos (eje proveedor) — extiende la tabla de §3

| Entidad | Campo nuevo | Tipo | Default | Migracion |
|---|---|---|---|---|
| `SupplierPayment` | `Currency` | `string` (3) NOT NULL | `'ARS'` BD | aditiva + backfill |
| `SupplierPayment` | `ImputedCurrency` | `string?` (3) | null | aditiva |
| `SupplierPayment` | `ExchangeRate` | `decimal?(18,6)` | null | aditiva (alineado con `Invoice.MonCotiz`, ver 2.2bis) |
| `SupplierPayment` | `ExchangeRateSource` | `int?` (enum) | null | aditiva |
| `SupplierPayment` | `ExchangeRateAt` | `datetime?` | null | aditiva |
| `SupplierPayment` | `ImputedAmount` | `decimal?(18,2)` | null | aditiva |
| `Supplier.CurrentBalance` | (sin columna nueva) | redefine semantica a surrogate | — | sin migracion |
| **`SupplierBalanceByCurrency`** | **tabla nueva** (Id, SupplierId, Currency, ConfirmedPurchases, TotalPaid, Balance) | — | — | **aditiva (CREATE TABLE)** + backfill |
| `SupplierAccountSummaryDto` / read DTOs | `PorMoneda` | list `{Currency, ...}` | — | contrato aditivo |

`Supplier.CurrentBalance` ya existe (solo cambia su significado a surrogate). El detalle por moneda vive en la tabla hija `SupplierBalanceByCurrency` (queryable en SQL). Nota de hecho (B4): `SupplierPayment.Amount` ES `decimal(18,2)` (atributo en SupplierPayment.cs:23-24, sin Fluent override) — a diferencia de `Payment.Amount` que es `(12,2)` por el Fluent (§2.2). Los campos nuevos de `SupplierPayment` siguen la precision fijada en 2.2bis (`ImputedAmount` 18,2; `ExchangeRate` 18,6).

### 15.4 Calculo de la deuda por moneda (gemelo de `ReservaMoneyCalculator`)

- `CalculateSupplierConfirmedPurchasesAsync` (SupplierService.cs:919-925) pasa a devolver `Dictionary<string, decimal>` (compras confirmadas por moneda del servicio), agrupando `rows` por `Monedas.Normalizar(r.Currency)` antes de sumar `NetCost` con la regla por tipo.
- **`BuildSupplierServicesQuery` y la moneda (precision sobre el gap del review):** la query (SupplierService.cs:717-833) concatena los 5 tipados **y** el generico `Servicios` (`ServicioReserva`, branch en :809-825, concatenado en :832). El review lo enuncio como "el generico no entra en la query"; el hecho exacto es: **si entra**, pero (a) ningun branch del `Select` proyecta `Currency` al `SupplierAccountServiceListItemDto`, y (b) `ServicioReserva` ademas **no tiene** la columna `Currency` (la agrega §2.2). Acciones concretas, ambas en este trabajo:
  1. Agregar `Currency` al `SupplierAccountServiceListItemDto` y al `Select` de **los 6 branches** (5 tipados leen `x.Currency`; el generico lee `service.Currency` una vez exista la columna de §2.2), normalizando `null -> ARS` al agrupar.
  2. Con eso, `CalculateSupplierConfirmedPurchasesAsync` puede `GroupBy(Currency)` la deuda del proveedor incluyendo los servicios genericos. **No se deja el generico afuera**: se incluye en este ADR (la alternativa de dejarlo "anotado para despues" partiria la deuda del proveedor y es justo el caso que B1 quiere cerrar).
- `CalculateSupplierDebt` (:927-937) pasa a devolver `Dictionary<string, decimal>` = compras[moneda] - pagos[moneda imputada] por cada moneda presente. El escalar `Supplier.CurrentBalance` se deriva como surrogate (15.3).
- Los pagos se agrupan por **moneda imputada** (`ImputedCurrency ?? Currency`) usando `ImputedAmount ?? Amount`, idem cliente.

### 15.5 Puntos de ruptura (file:line) — donde hoy se suma/muestra deuda a proveedor SIN moneda

- **`SupplierService.CalculateSupplierDebt`** (src/TravelApi.Infrastructure/Services/SupplierService.cs:927-937): resta `purchases - SUM(payments.Amount)` mezclando monedas. **Punto central del eje proveedor.** -> por moneda.
- **`SupplierService.CalculateSupplierConfirmedPurchasesAsync`** (:919-925): suma `NetCost` cross-servicio sin agrupar por `Currency`. -> agrupar por moneda del servicio. **Incluye `BuildSupplierServicesQuery` (:717-833):** el generico `ServicioReserva` YA esta en la query (branch :809-825, concat :832) pero sin `Currency` en el `Select` ni columna en la entidad; se agrega `Currency` a los 6 branches + a `ServicioReserva` (§2.2) y se incluye en la deuda por moneda. **Decision: el generico ENTRA en este trabajo** (no se difiere; dejarlo afuera partiria la deuda del proveedor).
- **`SupplierService.GetSupplierAccountOverviewAsync`** (:377-437): `TotalPurchases`, `TotalPaid`, `Balance` escalares (suman monedas). -> por moneda en el DTO.
- **`SupplierService.AddSupplierPaymentAsync`** (:508-568) y **`UpdateSupplierPaymentAsync`** (:570-616): el guard `request.Amount > currentDebt` (`:522`, `:592`) compara el monto del pago contra la deuda **mezclada**. -> comparar contra la deuda de la **moneda imputada** del pago. El `new SupplierPayment{...}` (:551-561) debe setear `Currency`/imputacion/TC desde el request (validaciones §8 espejadas). `supplier.CurrentBalance = currentDebt - request.Amount` (:564, :614, :640) pasa a recomputar el surrogate.
- **`SupplierService.CalculateSupplierDebt` lo consumen** `RecalculateAllBalancesAsync` (:356-366), `UpdateBalanceAsync` (:368-375), `DeleteSupplierPaymentAsync` (:639-640): persisten el surrogate recomputado.
- **`SupplierService.GetSupplierAccountServicesAsync`** (:439-478) y **`GetSupplierAccountPaymentsAsync`** (:480-506): listan `NetCost` y `Amount` sin moneda. -> exponer `Currency` por fila.
- **`SuppliersController.cs:270`** (`supplier.CurrentBalance` en alguna respuesta del controller): expone el escalar. -> con surrogate, solo flag; el monto va por moneda.
- **`AlertService.cs:126-137`** (`s.CurrentBalance > 100 && s.IsActive`, top 10 por deuda): el **filtro** (`> 100`) y el orden quedan sobre el surrogate (cualquier moneda con deuda lo hace `> 0`; el umbral 100 pierde sentido si mezcla monedas — **definir con UX/contador** si el umbral es por moneda). El **monto y texto** ("le debes X") deben decir la moneda. -> leer dict por moneda.
- **`ReportService.cs:230-238`** (`supplierPayments = SUM(SupplierPayments.Amount)` + `supplierDebts` ordenados por `CurrentBalance`): suma egresos mezclando moneda y ordena por surrogate/monto mezclado. -> `GroupBy(Currency)` en pagos; deuda por moneda.
- **`ReportService.cs:79, 175, 193, 205, 280`** (`supplierPaymentsThisMonth`, `totalSupplierPayments`, `PagosProveedores` en el summary): suma `SupplierPayments.Amount` cross-moneda. -> por moneda.
- **`ReportService.cs:374-394`** (export Excel "Cuentas por Pagar"): vuelca `creditor.CurrentBalance` en una sola columna `$`. **Es justo el export que el contador usa**; debe quedar por moneda (columna ARS / USD o filas con moneda), nunca un total mezclado. (Nota: el header dice "Saldo a Favor" en :378 — revisar la semantica con el dueno; aca `CurrentBalance` es deuda de la agencia, no saldo a favor.)
- **`ReportService.cs:562-563`** (`cashOutByDay` = `SUM(SupplierPayments.Amount)` por dia, cash flow): -> separar por **moneda real del egreso** (`SupplierPayment.Currency`).
- **`TreasuryService.cs:96-105`** (`cashOutSuppliers = SUM(SupplierPayments.Amount)` del mes, va al `CashOutThisMonth`): **caja real de egresos** -> agrupar por **moneda real del pago** (`SupplierPayment.Currency`), no por imputada. Es el espejo exacto del arqueo de ingresos §4.5/§6.
- **`TreasuryService.cs:145-200`** (lista de movimientos / `supplierMovements`): exponer `Amount` con su moneda.
- **Mapeo AppDbContext**: `HasDefaultValue("ARS")` para `SupplierPayment.Currency`; `ExchangeRateSource` como `int`; mismo patron que `Payment` (§7.1). **Verificar el `HasColumnName` real de las columnas de `SupplierPayment`** (`ReservaId`/`ServicioReservaId` mapean a `TravelFileId`/`ReservationId` segun el trap M2 §7.1) antes de cualquier SQL crudo de backfill.

### 15.6 Dos ejes de caja del lado proveedor (igual que el cliente)

Igual que en §6 (cliente), el pago saliente tiene **doble lectura**, ambas crudas:
- **Imputacion a la deuda** (cuanto baja lo que le debemos al operador): `ImputedAmount` sobre `ImputedCurrency` -> alimenta la deuda por moneda del proveedor.
- **Caja real / egreso** (cuanto salio fisicamente): `Amount` sobre `Currency` -> alimenta tesoreria de egresos / cash out por su moneda real.

Nunca el mismo numero mezcla ambos. El cobro/pago cruzado entra/sale de caja en su moneda real.

### 15.6bis Reversa / anulacion / edicion del pago saliente cruzado (espejo de §2.8, con UN bug a corregir)

El soft-delete del pago a proveedor ya existe (`DeleteSupplierPaymentAsync`, SupplierService.cs:618-) y la mayoria del recalculo es self-healing via `CalculateSupplierDebt`. **PERO hay un punto imperativo que B3 obliga a corregir:**

- **BUG latente confirmado (SupplierService.cs:639-640):** al anular un pago, hoy hace `supplier.CurrentBalance = currentDebt + payment.Amount`, es decir **devuelve a la deuda el `Amount` de caja**. Para un pago **no cruzado** (`Amount == ImputedAmount`) es correcto. Para un pago **cruzado**, esto es **incorrecto**: debe devolver `ImputedAmount` (el equivalente que bajo la deuda en la moneda imputada), no `Amount` (lo que salio de caja en otra moneda). Ademas, mezcla monedas en el escalar.
  - **Fix de este ADR:** al adaptar `CalculateSupplierDebt`/`UpdateBalanceAsync` a dict por moneda + surrogate (§15.4), `DeleteSupplierPaymentAsync` deja de hacer la suma imperativa `currentDebt + payment.Amount` y pasa a **recalcular** la deuda por moneda desde los pagos vivos (igual que el cliente con `RecalculateReservaBalanceAsync`): `supplier.CurrentBalance = SurrogateFrom(CalculateSupplierDebtPorMoneda(...))`, y reescribe `SupplierBalanceByCurrency`. Como el pago ya quedo `IsDeleted = true` (query filter `!IsDeleted`, AppDbContext.cs:1147), el recalculo lo excluye y la deuda de la moneda imputada sube por `ImputedAmount` automaticamente. **Se elimina el add-back manual de `Amount`.**
- **Imputacion a la deuda:** al recalcular, el pago anulado deja de restar su `ImputedAmount` de la deuda de `ImputedCurrency` -> la deuda de esa moneda vuelve a subir por el equivalente. Correcto.
- **Caja real / egreso:** el cash-out (`TreasuryService.cs:96-105`, §15.5) lee `SupplierPayment.Amount` + `Currency` con `!IsDeleted`: el pago anulado sale del cash-out por su `Amount` real en su moneda real.
- **Editar:** `UpdateSupplierPaymentAsync` (SupplierService.cs:570-616) — misma regla que §2.8.3: un pago saliente **cruzado** es inmutable en moneda/TC/imputacion; corregir = anular (soft-delete ya existe) + recrear. Editar campos no economicos sigue OK.

**Tests B3 proveedor:** anular pago saliente cruzado -> la deuda de `ImputedCurrency` sube por `ImputedAmount` (no por `Amount`); el cash-out de `Currency` baja por `Amount`; el escalar surrogate vuelve a reflejar "debe/no debe". Test de regresion: anular pago NO cruzado da el mismo resultado que hoy.

### 15.7 Validaciones de negocio (eje proveedor) — espejo de §8

1. **Moneda del pago saliente y de imputacion**: ambas en `Monedas.Soportadas`. Server-side.
2. **Pago saliente cruzado** (`Currency != ImputedCurrency`): `ExchangeRate > 0`, `ExchangeRateSource != Unset && != null`, `ExchangeRateAt != null`, `ImputedAmount > 0` obligatorios. Rechazar si falta alguno (mismo rigor que el cobro cruzado §8.5).
3. **Pago saliente NO cruzado**: bloque TC en null; imputa `Amount`.
4. **Guard "el pago excede la deuda"** (hoy global, SupplierService.cs:522/592): pasa a ser **por la moneda imputada** — el pago no puede exceder la deuda **de esa moneda**. Un pago USD no se valida contra deuda ARS.
5. **Sobrepago a favor**: pagar de mas en una moneda deja saldo a favor en esa moneda; no compensa deuda de otra (surrogate, max(0,...) por moneda).
6. **Inmutabilidad de la caja** del pago saliente: `Amount`+`Currency` inmutables una vez creado; corregir un cruzado = anular (soft-delete ya existe) y recrear. A confirmar con contador (igual que §8.8 cliente).

### 15.8 Plan de implementacion (integrado al orden de §9)

Este eje se intercala en el plan existente sin pasos nuevos sueltos — va pegado a cada paso del eje cliente para que una sola migracion y un solo recorrido por capa cubran ambos lados:

1. **Paso 1 (dominio + migracion)**: en la MISMA migracion `Adr021_M1_AddMultiCurrency`, agregar el bloque moneda+TC a `SupplierPayment` (espejo de `Payment`), la **tabla hija `SupplierBalanceByCurrency`** y mapeo AppDbContext. `Supplier.CurrentBalance` no cambia de columna (solo de semantica). Backfill de la hija por recalculo (§7.2.6). Aditiva, probada contra Postgres.
2. **Paso 2 (calculator)**: junto al `ReservaMoneyCalculator`, adaptar `CalculateSupplierDebt`/`CalculateSupplierConfirmedPurchasesAsync` a dict por moneda + surrogate **y sincronizar `SupplierBalanceByCurrency`**; agregar `Currency` al `Select` de `BuildSupplierServicesQuery` (6 branches, incluye el generico `ServicioReserva`). Reescribir `DeleteSupplierPaymentAsync` para recalcular (no `currentDebt + payment.Amount`). Tests: proveedor 100% ARS identico a hoy + multimoneda + pago saliente cruzado + anulacion cruzada (B3).
3. **Paso 4 (registro de pago)**: junto a `RegisterPayment` del cliente, `AddSupplierPaymentAsync`/`UpdateSupplierPaymentAsync` setean moneda/imputacion/TC y validan §15.7; el guard de exceso pasa a por-moneda. Sin UI (default ARS no cruzado = identico).
4. **Paso 5 (DTO/API)**: `SupplierAccountSummaryDto` + read DTOs exponen por moneda (aditivo); `SupplierPaymentRequest` recibe los campos nuevos.
5. **Paso 6 (consumidores cross-entidad)**: junto a `CustomerService`/`ReportService`/`TreasuryService` del cliente, adaptar el lado proveedor: `AlertService` (deuda proveedor por moneda), `ReportService` (pagos proveedor + export "Cuentas por Pagar" + cash out por moneda), `TreasuryService` (cash out por moneda real del egreso).
6. **Paso 7 (frontend, tras gate UX)**: cuenta corriente del proveedor con columnas por moneda; selector de moneda + bloque TC en el form de pago a proveedor; export por moneda. Mismo gate UX con Gaston que el resto (la cuenta corriente del proveedor es una vista de plata). Nada de "diferencia de cambio".

Incremental real: pasos 1-6 no cambian nada visible (todo ARS hasta que alguien cargue un servicio/pago en USD). Un proveedor 100% ARS da el mismo `CurrentBalance` antes y despues (regresion).

### 15.9 Riesgos (eje proveedor) — extiende la tabla de §11

| ID | Riesgo | Severidad | Mitigacion |
|---|---|---|---|
| R-10 | Surrogate de `Supplier.CurrentBalance` engana a quien lo lee como monto (alertas, export "Cuentas por Pagar", reportes) | **Alto** | Mismo tratamiento que R-2: identificados (`AlertService`, `ReportService` :230-238/:374-394, `SuppliersController`:270). El surrogate sirve solo de flag/orden; el monto va por moneda. El export del contador queda por moneda, nunca mezclado. |
| R-11 | Guard "el pago excede la deuda" valida contra deuda mezclada y deja pagar de mas en una moneda | Medio | Guard pasa a por-moneda imputada (§15.7.4). Test: pago USD contra deuda solo-ARS rechazado/permitido segun corresponda. |
| R-12 | `BuildSupplierServicesQuery` no expone `Currency` -> el agrupamiento por moneda lee mal la deuda | Medio | Agregar `Currency` al `Select` (15.4); test de regresion ARS puro. |
| R-13 | Cash out de tesoreria mezcla moneda real del egreso (arqueo cruzado mal) | Alto | `cashOutSuppliers` agrupa por `SupplierPayment.Currency` (moneda real), no por imputada (§15.6). |
| R-14 | Migracion de `SupplierPayment` repite el trap M2 (nombre de propiedad vs columna real) | Alto | Columnas nuevas sin `HasColumnName`; migracion EF; verificar `HasColumnName` de `ReservaId`/`ServicioReservaId` antes de cualquier SQL; probar contra Postgres (§7.1). |
| R-15 | `OperatorRefundReceived` (refund de cancelacion, ya multimoneda) se duplica o entra en conflicto con este eje | Bajo | Es un flujo aparte (FC1/ADR-002), NO la cuenta corriente normal. No se toca; sirve de precedente de moneda+TC del lado proveedor. |

### 15.10 Requiere contador matriculado (eje proveedor) — extiende §13

- **Diferencia de cambio en el pago a proveedor** (deuda USD valuada a un TC vs egreso ARS a otro): mismo limite contable que el cliente (§1) — el sistema deja datos crudos, el contador reconoce en el cierre. NO se materializa. **Requiere contador.**
- **Tratamiento de la deuda en moneda extranjera al cierre** (valuacion de cuentas por pagar en USD, ajuste por inflacion/TC). **Requiere contador.**
- **Retenciones/percepciones al pagar al proveedor** (Ganancias, IVA, IIBB sobre pagos a proveedores) y su interaccion con pago en ME / cruzado. El sistema hoy NO calcula retenciones al proveedor; sumarlas es fuera de este ADR. **Requiere contador.**
- **Umbral de alerta de deuda** (`AlertService` `CurrentBalance > 100`): si el umbral es por moneda o un equivalente. **Definir con dueno + contador.**
- **Semantica del header "Saldo a Favor"** en el export de Cuentas por Pagar (ReportService.cs:378): hoy vuelca `CurrentBalance` (deuda de la agencia) bajo un rotulo que dice "a favor". Revisar con dueno/contador al rehacer el export por moneda.

### 15.11 Lo que este eje NO incluye

- **Conciliacion automatica** entre la deuda calculada y un estado de cuenta del operador: no existe hoy y no se agrega.
- **Facturas del proveedor HACIA la agencia** (comprobante de compra) como entidad fiscal: hoy la deuda se deriva de `NetCost`, no de una factura de compra cargada. Fuera de alcance.
- **Retenciones al proveedor**: ver §15.10 (requiere contador, no se construye aca).
- **Refund del operador en cancelacion** (`OperatorRefundReceived`): ya multimoneda, flujo aparte, no se toca.
