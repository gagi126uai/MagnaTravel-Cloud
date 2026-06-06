# ADR-017 — El tarifario se arma solo desde la venta (find-or-create) + fechas límite con alertas

- **Estado**: **APROBADO PARA CONSTRUIR — v3.1** (round 3 = READY / Approved with comments; §13 traza las 2 condiciones + 5 notas incorporadas; Q4 cerrada por la decisión D8 del dueño — sin preguntas abiertas)
- **Fecha**: 2026-06-05 (v3: rounds 1 y 2 resueltos + D6/D7; v3.1 mismo día: round 3 READY + D8 incorporadas — §13)
- **Decisores**: Gastón (dueño del producto — UX ya aprobada e inamovible), software-architect
- **Especificación de UX (fuente de verdad, NO modificable sin repreguntar)**:
  - `docs/ux/mockups/2026-06-05-agregar-servicio-detalle-C.html` (4 momentos + tabla de campos por tipo)
  - `docs/ux/guia-ux-gaston.md` (reglas generales + decisiones del 2026-06-05, incluidas las 5 de la ronda arquitectura)

---

## 1. Contexto

### 1.1 El problema de negocio

Hoy el tarifario (`Rate`) se carga en una pantalla aparte y nadie lo hace ("a la gente le da paja"). Resultado: el tarifario está vacío o desactualizado, y cada carga de servicio arranca de cero. La decisión del dueño (aprobada, textual: "Sí, me encantó") es invertir el flujo: **el tarifario se construye como subproducto de vender**. El primer campo al cargar un servicio es un buscador find-or-create con tolerancia a errores de tipeo; si el producto existe se precargan operador y precio de la última venta como sugerencia editable; si no existe se crea inline en la misma transacción que el servicio, marcado "creado en venta".

Alcance: los 5 tipos (Hotel, Aéreo, Traslado, Paquete, Asistencia) desde el arranque. Además: fechas límite de seña/pago al operador (hotel/paquete) y de emisión (aéreo), visibles como etiquetas en la fila del servicio y con avisos cuando se acercan.

La UI aprobada es la propuesta C "carga en línea", que **reemplaza** al modal actual (`ServiceFormModal.jsx`, ~2500 líneas, rechazado explícitamente por el dueño).

### 1.2 Estado actual del código (verificado)

| Pieza | Estado verificado |
|---|---|
| `Rate` (`src/TravelApi.Domain/Entities/Rate.cs`) | Entidad única para los 6 tipos con campos dinámicos por tipo. `SupplierId` **ya es nullable**. Tiene `NetCost/Tax/SalePrice/Commission`, `Currency` con **default hardcodeado `"USD"` (Rate.cs:149)**, `PriceUnit` (`"servicio"` default, Rate.cs:36), `HotelPriceType` (`"base_doble"` default, Rate.cs:84), `IsActive`, `ValidFrom/ValidTo`. |
| pg_trgm | **Ya instalado** por migración `20260530120000_AddRateFuzzyMatching`: `CREATE EXTENSION pg_trgm` + índices GIN trigram sobre `lower("HotelName")` y `lower("ProductName")` en `Rates`. |
| Búsqueda difusa existente | `RateService.FindDuplicateCandidatesAsync` (RateService.cs:547): umbral `similarity >= 0.4`, top 5, SQL crudo parametrizado, fallback ILIKE si pg_trgm no está. La query trigram (RateService.cs:745-757) combina **a propósito** dos condiciones: `lower(col) % lower(@name)` (pega contra el índice GIN; corta por el GUC `pg_trgm.similarity_threshold`, default 0.3) **y** `similarity(...) >= @threshold` (0.4 paramétrico). Expuesta solo en `POST /api/rates/duplicate-check` con `[Authorize(Roles="Admin")]` (RatesController.cs:104-105). |
| Identidad existente del Rate (duplicate-check) | `IsExactHuellaMatch` (RateService.cs:635-): identidad = **proveedor + nombre + componentes por tipo** (Hotel: HotelName+RoomType+MealPlan+RoomCategory, RateService.cs:649-653; el SupplierId es obligatorio y filtra antes, RateService.cs:551-553). Es una identidad supplier-scoped de "duplicado exacto de tarifa". |
| Normalizador existente | `TextNormalizer.NormalizeForMatch` (`src/TravelApi.Domain/Helpers/TextNormalizer.cs:36`): trim + lower invariant + sin tildes (NFD, descarta NonSpacingMark) + colapsa espacios. Lo usa `FindExactMatchAsync`. **No se crea un normalizador nuevo: se extiende este** (§2.4). |
| Búsqueda no difusa — **FUGA DE COSTOS** | `GET /api/rates/search` (RatesController.cs:84-97 → `RateService.SearchAsync`, RateService.cs:318-356) devuelve `NetCost` y `Tax` a **cualquier usuario logueado** (controller `[Authorize]` a nivel clase, sin permiso de costos). `GET /api/rates` (`GetAllAsync` → `RateListItemDto`) y los demás GETs del controller también devuelven DTOs con NetCost. El dueño ordenó taparla (decisión D3, §1.4) — entra al alcance como fase propia (§2.7). |
| Punteros a Rate | Los 5 bookings + `ServicioReserva` + `QuoteItem` ya tienen `RateId int?` → la relación servicio→producto **ya existe**, no hay que migrarla. |
| Snapshot al crear — **PISA EL REQUEST** | Con `RateId`, el flujo actual **sobrescribe lo que vino en el request con los valores del Rate** en 4 de 5 tipos: Flight (BookingService.cs:303-306: NetCost/SalePrice/Commission/Tax + Currency :309), Package (:714-721), Transfer (:909-915), Assistance (`ApplyAssistanceRateSnapshot` :108-123, que **también pisa SupplierId** :120-123). Hotel no pisa precios pero sí SupplierId (resuelto desde el rate ANTES que el request, :495) y HotelName/City/RoomType/MealPlan/StarRating (`ApplyHotelRateSnapshot` :126-159). Esto **contradice la UX aprobada** ("amarillo = sugerido, lo pisás si cambió") — ver fix en §2.3.b. |
| Creates de bookings | `CreateHotelAsync` (:488), `CreateFlightAsync` (:281), `CreateTransferAsync`, `CreatePackageAsync` (:700), `CreateAssistanceAsync`: hacen **varios `SaveChanges` sin transacción explícita** (AddAsync → UpdateBalance supplier → RecalculateSchedule → UpdateBalance reserva). |
| Conversión de presupuesto | `QuoteService.ConvertToFileAsync` (QuoteService.cs:244): crea Reserva + servicios desde los items del quote. **No es atómico** (SaveChanges en :288 y :445). Cubre Hotel/Aéreo/Traslado/Paquete; **Asistencia NO** (cae al `ServicioReserva` genérico, :408-429). Usa `SupplierId = item.SupplierId ?? (rate?.SupplierId ?? 0)` → **el fallback es `0`** (:308 y equivalentes). |
| Transacciones | Patrón de la casa en `ReservaService.cs:1307-1311`: `CreateExecutionStrategy()` + `BeginTransactionAsync(IsolationLevel.Serializable)`. **Retry YA configurado (verificado v3)**: `Program.cs:161-165` registra `EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null)` → `CreateExecutionStrategy()` devuelve la estrategia reintentante de Npgsql (que trata 40001/40P01 como transient). **Contracara**: `PostgresIntegrationFixture.cs:305` y `:340` configuran `UseNpgsql(ConnectionString)` **SIN** retry → los tests de integración hoy NO ejercitan el retry (ver §2.3.b.2). |
| Fechas límite | **No existen** campos de deadline en ningún booking (verificado por grep en Entities). Solo existe `ApprovalRequestType.PaymentDeadlineOverride` (otro contexto: aprobaciones). |
| Aéreo: PNR y confirmación | `FlightSegment.PNR` (record locator, **preexistente**, FlightSegment.cs:62, nullable). La migración `20260530015242_AddFlightSegmentConfirmationAndPaxCount` agregó `ConfirmationNumber` (comprobante del proveedor, distinto del PNR) y `PaxCount`. **La clave de agrupación de alertas es `PNR`**, no ConfirmationNumber (§2.2). `ConvertToFileAsync` crea segmentos con `FlightNumber = "TBD"` y PNR null. |
| Alertas | `AlertService.GetAlertsAsync` (compute-on-read, sin entidades persistidas) devuelve `{UrgentTrips, SupplierDebts, TotalCount}`. **No recibe identidad del caller** (sin filtrado por usuario). Usa `DateTime.UtcNow.Date` como "hoy" (AlertService.cs:22). El front (`AlertsContext.jsx:13`) la consume **solo si `isAdmin()`**, polling 30s — el gating de los buckets es client-side. Aparte existe `/notifications` persistido que ven todos los usuarios. **FUGA EXPLOTABLE HOY (M-R2-1, verificada v3)**: `AlertsController.cs:7` es `[Authorize]` plano y `GetAlerts` (:19-24) no chequea rol ni identidad → cualquier usuario logueado lee `SupplierDebts`/`UrgentTrips` (información financiera global) con un curl, hoy mismo, sin esperar este ADR. Se tapa en F1b sin flag (§2.7). |
| Responsable de la reserva | `Reserva.ResponsibleUserId` existe (Reserva.cs:111, `string?`). Hay backfill histórico pendiente (memoria de proyecto FC1.2): reservas viejas pueden tenerlo null. |
| Enmascarado de costos | `CostMasking.MaskHotelAsync` / `MaskFlightAsync` ponen el costo en `0m` si el caller no tiene `cobranzas.see_cost` (BookingService.cs:214-217, :527). Precedente: el fix B1.15 de enmascarado se shipeó **sin flag** (fix de seguridad). |
| Vendedor sin permiso de costos — **el request llega con costo `0`** (insumo de B-R2-1/D7) | El front oculta costo y ganancia a quien no tiene `cobranzas.see_cost` (`ServiceFormModal.jsx:1775` `canSeeCost = hasPermission("cobranzas.see_cost")`; :1779, :1933). Los 5 create/update requests tienen `NetCost`/`Tax` como `decimal` **NO-nullable** (Requests.cs — Flight :9/:28, Hotel :41/:55, Transfer :68/:82, Assistance :98/:112, Package :128): "no lo vi" llega como `0`, **indistinguible de "vale 0"**. Hoy el snapshot repone el costo desde el Rate; con la regla "request manda" (flag ON) ese `0` se persistiría y el upsert pisaría `LastNetCost` bueno con `0` para todos. Fix: regla D7 server-side (§2.3.b regla 3-bis + §2.8). |
| **Los updates SÍ clobberean campos ausentes** (insumo de B-R2-2) | Los maps de update NO tienen `Condition` anti-null (`src/TravelApi.Application/Mappings/MappingProfile.cs` — Flight :64-67, Hotel :86-91, Transfer :106-109, Package :125-129; verificado v3) y los 5 updates usan `_mapper.Map(req, entity)` (BookingService.cs:365, :574, :775, :969, :1191): AutoMapper escribe el null/0 del request sobre la entidad. Cualquier campo nuevo agregado a un Update request que un cliente viejo no mande **se borra en silencio** al editar. Gobierna el diseño de deadlines en updates (§2.2). |
| Campos para unitarizar | `HotelBooking.Nights/Rooms/Adults/Children` (:42-53), `FlightSegment.PaxCount`, `TransferBooking.Passengers`, `PackageBooking.Adults/Children` (:42-43), `AssistanceBooking.Adults/Children` (:69-70). |
| Flags | `OperationalFinanceSettings` es el hogar de los flags. Convención: default `false`, OFF = byte-idéntico, editables desde panel admin (`PUT /api/settings/operational-finance`). |
| Guards existentes en create/update | `ReservaCapacityRules` (fuerza "Solicitado" en Presupuesto), `MutationGuards` (inmutabilidad post-CAE/voucher), validaciones de fechas y `SalePrice > 0`. El nuevo flujo NO los toca: se inserta antes/alrededor. |

### 1.3 Pendiente conocido relacionado

El backend podría no rechazar `supplierId` vacío en Aéreo/Traslado/Paquete/Asistencia (candado solo en front). No bloqueante, pero el nuevo path crea la oportunidad de cerrar la validación server-side (ver §2.3.b regla 4).

### 1.4 Decisiones del dueño (Gastón, 2026-06-05, ronda arquitectura — REQUISITOS FIRMES)

| # | Decisión (registrada también en `docs/ux/guia-ux-gaston.md`) | Dónde aterriza en este ADR |
|---|---|---|
| D1 | Usuarios sin permiso `cobranzas.see_cost`: en el buscador ven el **precio de VENTA** de la última vez (nunca costo, nunca vacío). | §2.3.a (resuelve ex-Q1) |
| D2 | Avisos de fechas límite: **cada vendedor ve los de SUS reservas; el admin ve todos** (cambia el gating actual solo-admin de la campanita). | §2.5 (resuelve ex-Q2) |
| D3 | **Fuga vieja de costos: taparla.** `GET /api/rates/search` (y el resto de los GETs de Rates) debe respetar `cobranzas.see_cost`. | §2.7, fase F1b |
| D4 | Precio de referencia de hotel: **por noche POR HABITACIÓN** (recordar el valor de 1 habitación × 1 noche; multiplicar por noches × habitaciones la próxima). | §2.1 tabla de unitarización |
| D5 | Moneda del producto creado en venta: "**debe soportar tanto pesos como dólares**" (textual). El producto y su precio de referencia nacen con la moneda de ESA venta (ARS o USD), **nada de default USD**. | §2.1 y §2.3.b (flujo de Currency) |
| **D6** (2026-06-05, ronda 2) | **Ciudad OBLIGATORIA al crear un hotel desde la venta.** Cierra la ex-Q3 y desbloquea F2-Hotel. | §2.3.b (`NewCatalogProductRequest`), §8 |
| **D7** (2026-06-05, ronda 2) | **Costo cuando el caller NO tiene `cobranzas.see_cost`**: el sistema completa el costo server-side por detrás (cadena: `RateSupplierSale` del supplier elegido → campos del `Rate` → `0`) y marca "**costo a confirmar**" SOLO los casos dudosos: (a) producto nuevo sin costo conocido (quedó en 0), o (b) costo de referencia más viejo que un umbral configurable (default 60 días). Los no-dudosos pasan derecho sin marca. Un servicio "a confirmar" **NO actualiza `RateSupplierSale`** hasta que alguien con permiso confirme/corrija el costo. | §2.3.b regla 3-bis + §2.8 (cierra B-R2-1) |
| **D8** (2026-06-05, ronda 3 — cierra Q4) | **UX de "costo a confirmar"**: (a) el vendedor sin permiso que generó la marca **no ve nada** — la pill ámbar la ven solo usuarios con `cobranzas.see_cost`; (b) **sí** hay aviso en la campanita: bucket "Costos a confirmar" para quienes ven costos; (c) confirmación **EXPLÍCITA con botón "Confirmar costo"** (confirma o corrige el número) — **NO implícita al guardar** (reemplaza la decisión propia de v3). Asentada en `docs/ux/guia-ux-gaston.md`, sección "UI del costo a confirmar". | §2.8 (mecanismo de confirmación reescrito), §8 (Q4 cerrada), §13 |

---

## 2. Decisión

### 2.1 Modelo de datos: `Rate` ES el producto del catálogo (no se crea entidad nueva de producto)

**Decisión**: el "producto del tarifario" se materializa sobre la entidad `Rate` existente. No se crea una entidad `CatalogProduct` separada.

Razones:
- Los 7 punteros (`RateId`) ya existen en bookings/quotes; una entidad nueva obligaría a duplicar FKs o a migrar relaciones — todo lo contrario de "aditivo".
- `Rate.SupplierId` ya es nullable: un Rate puede representar el producto "a secas" sin operador fijo.
- Los índices trigram y la infraestructura de fuzzy search ya están construidos sobre `Rates`.
- El RateSelector del front y la pantalla de tarifario back-office ya operan sobre Rates.

**Cambios aditivos sobre `Rate`** (migración 1):

```csharp
// Rate.cs — campos nuevos, todos con default que no altera filas existentes
public bool CreatedInSale { get; set; } = false;   // pill violeta "Creado en venta"
public int? CreatedFromReservaId { get; set; }      // FK nullable a Reservas (trazabilidad, ON DELETE SET NULL)
[MaxLength(200)]
public string? SearchName { get; set; }             // nombre normalizado para catálogo (ver §2.4)
```

`SearchName` se calcula **en la app al escribir** (no computed column) con `TextNormalizer.NormalizeForCatalog` (§2.4): lowercase + trim + colapsar espacios + quitar acentos + colapsar puntuación repetida. Evita depender de la extensión `unaccent` de Postgres (no verificada como instalada).

**Fuente de `SearchName` por tipo (regla ÚNICA, idéntica en backfill y en escritura de app)** — esto cierra el hallazgo B3 del review:

| Tipo | Fuente de SearchName |
|---|---|
| Hotel | `HotelName` si no es null/blanco; si no, `ProductName` (`COALESCE(NULLIF(trim("HotelName"),''), "ProductName")` en el backfill SQL; `string.IsNullOrWhiteSpace` en app — **misma semántica en ambos lados**) |
| Aéreo / Traslado / Paquete / Asistencia / otros | `ProductName` |

Por qué: en los hoteles legacy el nombre real del hotel está en `HotelName` y `ProductName` puede ser genérico ("Tarifa hotel doble"). Si el backfill usara `ProductName` a secas, el anti-duplicados nacería roto para hoteles: el buscador no encontraría "Maitei" y el vendedor crearía un duplicado en la primera venta. El backfill de la migración aplica la regla por tipo (`CASE WHEN "ServiceType" ILIKE 'hotel' THEN ... ELSE ... END`), des-acentuando legacy best-effort con `translate()` para el set español (áéíóúüñ); cualquier residuo de normalización distinta entre SQL y app se corrige solo en la primera escritura de app sobre esa fila, y el matching es difuso (tolerante a residuos).

Índice nuevo:

```sql
CREATE INDEX IF NOT EXISTS "IX_Rates_SearchName_trgm"
    ON "Rates" USING GIN ("SearchName" gin_trgm_ops);
```

Los dos índices trigram existentes se conservan (los usa el duplicate-check actual).

**Entidad nueva: `RateSupplierSale`** (migración 1) — la memoria "producto + operador + última venta":

```csharp
public class RateSupplierSale
{
    public int Id { get; set; }
    public int RateId { get; set; }          // FK Rates, CASCADE
    public int SupplierId { get; set; }      // FK Suppliers, RESTRICT
    public DateTime LastSoldAt { get; set; } // UTC del último create de servicio
    public decimal LastNetCost { get; set; }     // decimal(18,2) - UNITARIO según tabla de unitarización
    public decimal LastTax { get; set; }         // decimal(18,2) - UNITARIO (impuesto INCLUIDO en el costo, feature 67d202a; sin esto la cadena D7 no puede reponer Tax)
    public decimal LastSalePrice { get; set; }   // decimal(18,2) - UNITARIO
    public string? LastCurrency { get; set; }    // MaxLength(3) - la moneda de ESA venta (D5)
    public string LastPriceUnit { get; set; }    // ver tabla de unitarización (valores cerrados)
    public int SalesCount { get; set; }          // veces vendida esta combinación
}
// UNIQUE (RateId, SupplierId); índice por (RateId, LastSoldAt DESC)
```

**Upsert SIEMPRE atómico en SQL** (cierra B2 — la carrera jamás aborta una venta):

```sql
INSERT INTO "RateSupplierSale" ("RateId","SupplierId","LastSoldAt","LastNetCost","LastTax","LastSalePrice","LastCurrency","LastPriceUnit","SalesCount")
VALUES (@rateId, @supplierId, @soldAt, @netCost, @tax, @salePrice, @currency, @priceUnit, 1)
ON CONFLICT ("RateId","SupplierId") DO UPDATE SET
    "LastSoldAt"    = EXCLUDED."LastSoldAt",
    "LastNetCost"   = EXCLUDED."LastNetCost",
    "LastTax"       = EXCLUDED."LastTax",
    "LastSalePrice" = EXCLUDED."LastSalePrice",
    "LastCurrency"  = EXCLUDED."LastCurrency",
    "LastPriceUnit" = EXCLUDED."LastPriceUnit",
    "SalesCount"    = "RateSupplierSale"."SalesCount" + 1;
```

Nunca se hace read-modify-write de `SalesCount` en EF (perdería incrementos concurrentes) ni un upsert ingenuo find-then-insert (la carrera tira 23505 dentro de la transacción y abortaría la venta). El helper único de upsert (`Infrastructure`) **se saltea silenciosamente si `SupplierId <= 0`** (el fallback `?? 0` de `ConvertToFileAsync`, QuoteService.cs:308, no debe generar filas basura ni violar la FK a Suppliers) y si `RateId` es null. **Además se saltea si el servicio quedó marcado "costo a confirmar"** (regla D7, §2.8: la fila no se escribe hasta que alguien con permiso confirme el costo). **Nota del helper (round 3, nota 6)**: `LastCurrency` puede quedar `null` por el path best-effort de `ConvertToFileAsync` (ítems sin moneda); la cadena D7 ya lo trata sin caso especial — `null ≠ moneda de la venta` → esa referencia no se usa y se sigue al fallback. Detalles de transacción/aislamiento en §2.3.b.

**Nota de comportamiento bajo Serializable (checklist round 2 #1)**: dentro de una transacción Serializable, el `ON CONFLICT` elimina el 23505 pero **puede aflorar como 40001** (serialization failure) en vez de ejecutar el `DO UPDATE` cuando dos transacciones tocan la misma fila — es comportamiento esperado de Postgres, **no una anomalía del diseño**, y lo absorbe el retry de §2.3.b.2. El test R10 debe aceptar ambas resoluciones (DO UPDATE directo o 40001+retry) siempre que el resultado final sea 1 fila con `SalesCount = 2`.

**Dónde vive cada cosa pedida por el dueño**:

| Concepto del dueño | Dónde vive |
|---|---|
| El producto (hotel/ruta/trayecto/paquete/plan) | Fila de `Rate` (una por producto; los campos por tipo ya existen) |
| Flag "creado en venta" | `Rate.CreatedInSale` + `CreatedFromReservaId` |
| Precio de referencia que "se actualiza solo con cada venta" | `RateSupplierSale.LastNetCost/LastSalePrice` (upsert en cada create de servicio) |
| "Si lo venden con otro operador, esa combinación también se recuerda" | Una fila de `RateSupplierSale` por combinación (RateId, SupplierId) |
| Sugerencia del dropdown "última vez: operador · $X/noche · hace N semanas" | Top-1 de `RateSupplierSale` por `LastSoldAt DESC` del Rate |
| Moneda de la venta (D5) | `RateSupplierSale.LastCurrency` + `Rate.Currency` al nacer (ver flujo en §2.3.b) |

**Decisión deliberada**: los campos de precio del `Rate` (NetCost/SalePrice) **NO se pisan** con cada venta. El Rate conserva el valor curado por back-office (o el de nacimiento si fue creado en venta); la sugerencia dinámica sale SIEMPRE de `RateSupplierSale`, con fallback a los campos del Rate cuando todavía no hay ventas registradas.

**Tabla de unitarización por tipo** (cierra M3; incorpora D4) — el upsert guarda SIEMPRE el precio unitario, calculado sobre los montos TOTALES del servicio tal como quedan persistidos en el booking (los totales son los que muestra la fila del Momento 4 del mockup; verificación puntual al implementar). La misma fórmula unitariza `NetCost`, `Tax` (→ `LastTax`) y `SalePrice`; la inversa (unitario × multiplicador del tipo) es la que usa la cadena D7 para reponer el costo TOTAL en el booking (§2.3.b regla 3-bis):

| Tipo | `LastPriceUnit` | Fórmula del unitario | Divisor cero / guardas |
|---|---|---|---|
| Hotel | `"noche_habitacion"` | `total / (Nights × Rooms)` — **decisión D4**: 1 habitación × 1 noche | `Nights >= 1` ya lo garantiza `ValidateHotelStay`; si `Rooms <= 0` se trata como 1 |
| Aéreo | `"pasajero"` | `total / PaxCount` | `PaxCount <= 0` → se trata como 1 |
| Traslado | `"servicio"` | `total` tal cual (el trayecto completo es la unidad) | — |
| Paquete | `"pasajero"` | `total / (Adults + Children)` | suma `<= 0` → 1. **Definición**: los niños cuentan como persona entera. Es una simplificación deliberada: el valor es una referencia editable (amarillo), no una tarifa firme; partir por `ChildrenPayPercent` agregaría precisión falsa |
| Asistencia | `"pasajero_dia"` | `total / ((Adults + Children) × días de vigencia)` | pax o días `<= 0` → 1. Los planes de asistencia se cotizan por pax por día; guardar solo "por pasajero" haría inservible la sugerencia cuando cambia la duración |

**Redondeo único**: `Math.Round(valor, 2, MidpointRounding.AwayFromZero)` (alineado con el `roundMoney` del front). Al re-multiplicar en la próxima venta (noches × habitaciones para hotel — D4) puede haber deriva de centavos: aceptable y documentado, porque la sugerencia es editable y nunca un precio firme.

**Nota `HotelPriceType`**: `Rate.HotelPriceType` (Rate.cs:84, `por_persona`/`base_doble`) describe la semántica de los precios CURADOS del Rate, no de la sugerencia. La sugerencia se interpreta SOLO por `RateSupplierSale.LastPriceUnit`. En el **fallback** (producto sin ventas registradas → precios del Rate), el front recibe `priceUnit` del Rate (`Rate.PriceUnit`) y, para Hotel, `hotelPriceType`, y debe interpretarlos como hace hoy el RateSelector — el contrato del DTO distingue explícitamente de cuál de las dos fuentes salió la sugerencia (`lastSale` vs `rateFallback`).

**Flujo de moneda (cierra M2 + D5)**: los 5 create requests de bookings hoy NO llevan `Currency` (verificado: el booking la recibe solo copiada del Rate — feature `AddBookingCurrencyTraceability`). Se agrega `Currency` (`string?`, MaxLength 3) a los 5 create requests:
- **Flag ON**: `Currency` es obligatoria (400 si falta — la ficha siempre la manda, mockup campo "Moneda"). El booking persiste `Currency = request.Currency` (la moneda REAL de esta venta; el Rate solo sugiere — regla request-manda de §2.3.b). El Rate nuevo nace con `Currency = request.Currency` (se asigna SIEMPRE explícitamente: **el default `"USD"` de Rate.cs:149 no decide nunca** — D5). `RateSupplierSale.LastCurrency = request.Currency`.
- **Flag OFF**: el campo nuevo es opcional y se ignora si viene null → byte-idéntico al comportamiento actual de `AddBookingCurrencyTraceability` (booking.Currency copiada del Rate o null).

### 2.2 Fechas límite (migración 2, aditiva)

Campos nuevos, todos `DateTime?` (date-only por convención, igual que `CheckIn`):

```csharp
// HotelBooking.cs
public DateTime? OperatorPaymentDeadline { get; set; }  // fecha límite de seña/pago al operador

// PackageBooking.cs
public DateTime? OperatorPaymentDeadline { get; set; }

// FlightSegment.cs
public DateTime? TicketingDeadline { get; set; }        // fecha límite de emisión
```

Traslado y Asistencia NO llevan deadline (la tabla de campos por tipo del mockup no los incluye — no inventar alcance).

**Path de edición (cierra M7; corregido en v3 por B-R2-2)**: los deadlines también se agregan a los Update requests correspondientes (`UpdateHotelRequest`, `UpdatePackageRequest`, `UpdateFlightRequest`) — sin esto, un deadline mal cargado sería incorregible desde la UI.

**CORRECCIÓN v3 — los updates SÍ clobberean** (la v2 atribuía al reviewer una verificación de "no-clobbering" que era **falsa**; verificado ahora en código): los maps de update no tienen `Condition` anti-null (`src/TravelApi.Application/Mappings/MappingProfile.cs:64-67/:86-91/:106-109/:125-129`) y los 5 updates usan `_mapper.Map(req, entity)` (BookingService.cs:365/:574/:775/:969/:1191) → AutoMapper escribe null sobre el destino. Si el deadline se mapeara por convención, **cualquier edición desde el modal viejo (que convive hasta F4 y no manda el campo) borraría el deadline en silencio**.

**Mitigación obligatoria** (la única segura con el modal viejo conviviendo):
1. En los maps de update, los miembros de deadline se excluyen del mapeo automático: `ForMember(dest => dest.OperatorPaymentDeadline, opt => opt.Ignore())` (ídem `TicketingDeadline` en Flight).
2. La asignación es **manual en el service**, gobernada por un discriminador explícito en el request: `bool DeadlinesSpecified = false` (parámetro nuevo con default, no rompe callers posicionales — mismo truco que `Tax = 0`). Regla: `DeadlinesSpecified == false` (modal viejo, clientes legacy) → **se preserva** el valor persistido; `DeadlinesSpecified == true` (ficha nueva, que siempre manda el bloque) → se asigna lo que vino, **incluido null = borrar el deadline** (sin el discriminador, "no lo mandé" y "borralo" serían indistinguibles en un `DateTime?`).
3. Tests obligatorios en §7 (por Hotel, Paquete y Aéreo): update SIN el campo → el deadline persistido NO cambia; update con `DeadlinesSpecified=true` y deadline null → se borra; con valor → se actualiza.

**Zona horaria del corte (cierra M7)**: los deadlines se guardan como fecha "de pared" a medianoche con `DateTimeKind.Utc` SIN conversión — mismo patrón que `NormalizeAirportWallClock` (BookingService.cs:274-279): lo que el usuario carga es lo que se guarda. Para decidir "vence hoy / venció", **"hoy" se calcula como la fecha local de Argentina** (`TimeZoneInfo` IANA `America/Argentina/Buenos_Aires`, helper único, una constante): comparar contra `DateTime.UtcNow.Date` (como hace el bucket existente, AlertService.cs:22) correría el flip a "vencido" 3 horas antes (a las 21:00 del día anterior en ART), confundiendo al usuario. Los buckets existentes NO se tocan (siguen con UtcNow.Date: cambiarles el corte no es parte de este ADR). Supuesto a verificar al implementar: el runtime resuelve el id IANA tanto en el VPS (Linux) como en dev Windows (.NET 6+ con ICU lo hace).

**Nota Aéreo (cierra M8)**: el deadline conceptual es del PNR, pero la entidad es por segmento. El campo real de agrupación es **`FlightSegment.PNR`** (preexistente, FlightSegment.cs:62 — NO `ConfirmationNumber`, que es el comprobante del proveedor agregado por la migración `20260530015242`). Decisión pragmática: columna en `FlightSegment`; la ficha de carga escribe el mismo valor en los segmentos que crea en esa operación; la alerta agrupa por `(ReservaId, PNR)` tomando `MIN(TicketingDeadline)`. **Fallback definido**: si `PNR` es null, vacío o un placeholder ("TBD" case-insensitive — `ConvertToFileAsync` y cargas manuales generan placeholders), el segmento NO se agrupa: emite su propio aviso individual. Si en el futuro Aéreo se modela como "servicio" agregado, el campo migra con él.

### 2.3 API

Los endpoints nuevos quedan **gateados por flag** (OFF → `404 NotFound`, indistinguible de inexistente). La fase F1b (fuga de costos, §2.7) NO va detrás de flag: es un fix de seguridad.

#### a) Búsqueda unificada find-or-create

```
GET /api/rates/catalog-search?serviceType={Hotel|Aereo|Traslado|Paquete|Asistencia}&q={texto}
[Authorize]  (mismo gate que los creates de bookings; NO Admin-only)
```

Comportamiento:
- `q` mínimo 2 caracteres; devuelve hasta 8 resultados.
- Fuzzy con pg_trgm sobre `SearchName` (reusa/generaliza `RunTrigramFuzzyQueryAsync` y su fallback ILIKE, RateService.cs:734/778). Para Hotel matchea también contra `lower(HotelName)` (índice existente). **Al generalizar se conservan LAS DOS condiciones de la query existente** (RateService.cs:753-754): `lower(col) % lower(@name)` (usa el índice GIN; su corte lo da el GUC `pg_trgm.similarity_threshold`, default 0.3) **y** `similarity(...) >= @threshold` (0.4 paramétrico). Quitar la primera pierde el índice; quitar la segunda baja el umbral efectivo a 0.3.
- **Dedupe (forma exacta, cierra m1)**: subquery con `DISTINCT ON` cuyo `ORDER BY` arranca por la(s) clave(s) del DISTINCT, y ordenamiento final por score en la query exterior:

```sql
SELECT * FROM (
    SELECT DISTINCT ON ("SearchName")            -- Hotel: DISTINCT ON ("SearchName", norm_city)
           r.*, similarity(...) AS score, s."LastSoldAt", s."SalesCount"
    FROM "Rates" r LEFT JOIN LATERAL (último RateSupplierSale del rate) s ON TRUE
    WHERE <ServiceType> AND <las dos condiciones trigram> AND r."IsActive"
    ORDER BY "SearchName",                       -- Hotel: "SearchName", norm_city,
             s."LastSoldAt" DESC NULLS LAST, r."Id" DESC
) t
ORDER BY t.score DESC, t."LastSoldAt" DESC NULLS LAST, t."SalesCount" DESC NULLS LAST, t."Id" ASC
LIMIT 8;

-- norm_city = lower(translate(trim("City"), 'áéíóúüñÁÉÍÓÚÜÑ', 'aeiouunAEIOUUN'))
-- (checklist round 2 #5: MISMO criterio sin-tildes que la normalización de app, ver abajo)
```

  Para **Hotel** el `DISTINCT ON` incluye la City normalizada: dos hoteles homónimos de ciudades distintas son productos distintos y deben aparecer ambos. El tarifario legacy puede tener N Rates por hotel (room types / proveedores): el dropdown muestra el producto UNA vez, eligiendo la fila con venta más reciente.

  **Criterio ÚNICO de comparación de City (cierra checklist round 2 #5)**: la v2 era inconsistente — el reuso defensivo usaba la normalización de app (`NormalizeForCatalog`, sin tildes) pero el `DISTINCT ON` usaba `lower("City")` (con tildes) → "Córdoba" y "Cordoba" eran la misma ciudad en un lado y dos en el otro. Regla v3: **la normalización de app (`NormalizeForCatalog`) es la autoritativa**; se aplica así:
  - **Reuso defensivo (§2.4)**: la query SQL filtra solo por igualdad de `SearchName`; la comparación de City se hace **en memoria de la app** con `NormalizeForCatalog` sobre los pocos candidatos devueltos (a escala single-tenant son unidades, no miles). Así el reuso usa la normalización completa (NFD, cualquier alfabeto), sin depender de `unaccent`.
  - **`DISTINCT ON` del dropdown**: usa `norm_city` = `lower(translate(...))` con el set español (mismo best-effort que el backfill de `SearchName`, §2.1). Es solo agrupación visual del dropdown; un residuo no-español a lo sumo muestra el producto dos veces, nunca fusiona mal (la decisión de reuso no pasa por acá).
- Respuesta por ítem:

```jsonc
{
  "ratePublicId": "…",
  "serviceType": "Hotel",
  "name": "Hotel Maitei Posadas",
  "subtitle": "Posadas, Misiones",          // City / ruta / destino / plan según tipo
  "createdInSale": false,
  "score": 0.82,                             // similarity; null si vino del fallback ILIKE
  "lastSale": {                              // null si nunca se vendió (entonces viene rateFallback)
    "supplierPublicId": "…",
    "supplierName": "Ola Mayorista",
    "soldAt": "2026-05-22T…",
    "netCost": 48000.00,                     // NULL si el caller no tiene cobranzas.see_cost
    "salePrice": 60000.00,                   // SIEMPRE presente
    "currency": "ARS",
    "priceUnit": "noche_habitacion"
  },
  "rateFallback": { /* netCost (enmascarable), salePrice, currency, priceUnit, hotelPriceType */ }
}
```

- **Enmascarado + decisión D1 (ex-Q1, RESUELTA por el dueño)**: `netCost` (en `lastSale` y `rateFallback`) se anula para usuarios sin `cobranzas.see_cost` (mismo resolver de permisos que `CostMasking`); `salePrice` viaja SIEMPRE. El front muestra: con permiso → el costo (como el dibujo); sin permiso → el **precio de VENTA** de la última vez. Nunca costo, nunca vacío.

#### b) Create-inline transaccional (servicio + producto)

NO se crea endpoint nuevo: se **extienden los 5 create requests existentes** (`CreateHotelRequest`, `CreateFlightRequest`, `CreateTransferRequest`, `CreatePackageRequest`, `CreateAssistanceRequest`) con `Currency` (§2.1) y un sub-objeto opcional:

```csharp
public class NewCatalogProductRequest
{
    [Required, MaxLength(200)] public string Name { get; set; }
    [MaxLength(100)] public string? City { get; set; }       // Hotel: OBLIGATORIA (decisión D6 — 400 si falta o viene en blanco); otros tipos: destino/ruta, opcional
    [Required] public string SupplierPublicId { get; set; }  // operador: obligatorio al crear producto
}
```

Reglas en `BookingService.CreateXAsync` **con flag ON** (con flag OFF: byte-idéntico al comportamiento actual, incluidos los snapshots que pisan — ver regla 3):

1. `NewCatalogProduct` y `RateId` son **mutuamente excluyentes** (400 si vienen ambos).
2. **Transacción única para AMBOS paths** (cierra M5 — sin asimetría): con flag ON, todo el create (venga `NewCatalogProduct`, `RateId` o ninguno) se envuelve en el patrón de la casa `CreateExecutionStrategy()` + `BeginTransactionAsync(IsolationLevel.Serializable)` (ReservaService.cs:1307-1311). Dentro de la transacción: find-or-create del Rate (si aplica) → path de create existente intacto (snapshot según regla 3, `ReservaCapacityRules`, balances, `RecalculateReservationScheduleAsync`) → upsert `ON CONFLICT` de `RateSupplierSale` (misma conexión, participa de la transacción) → commit. Si cualquier paso falla → rollback total: no queda Rate huérfano, ni servicio sin producto, ni fila de venta fantasma. ("Guardar servicio y hotel" del mockup = una sola operación.)
   - **Concurrencia (cierra B2; actualizado v3 por M-R2-2 y checklist #2/#3/#4)**: la carrera del upsert la elimina el `ON CONFLICT` (jamás 23505; bajo Serializable puede aflorar como 40001 — esperado, ver nota en §2.1). La carrera de **creación del Rate** (dos vendedores crean "Hotel X" a la vez) bajo Serializable produce un serialization failure (40001) en uno de los dos; Npgsql lo marca transient y la `ExecutionStrategy` lo reintenta, y el reintento ENCUENTRA el Rate del ganador y lo reusa → la carrera se resuelve a favor del find-or-create.
     - **HECHO VERIFICADO (ya no precondición)**: el provider YA está configurado con `EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null)` (`Program.cs:161-165`) → `CreateExecutionStrategy()` devuelve la estrategia reintentante. No hay que agregar retry en producción.
     - **Delegate re-ejecutable (checklist #3)**: todo lo que va dentro del `ExecuteAsync` de la estrategia debe poder correr de nuevo desde cero — en el reintento el `ChangeTracker` puede arrastrar entidades del intento fallido. Regla: el delegate **limpia/reconstruye su estado al entrar** (entidades nuevas se instancian DENTRO del delegate; `ChangeTracker.Clear()` al inicio del reintento si se reusa el DbContext). Lección ya pagada en este repo: commit `723a905` (tests FC1.3 rotos por estado residual del ChangeTracker).
     - **Paridad del fixture (checklist #4, contracara de M-R2-2)**: `PostgresIntegrationFixture.cs:305` y `:340` usan `UseNpgsql(ConnectionString)` **sin** retry → tal cual está, el test R10 no ejercita la regla que dice probar. Resolución elegida: **paridad de fixture** — el fixture agrega el mismo `EnableRetryOnFailure` que `Program.cs` (preferida: prueba la configuración real). Alternativa solo si la paridad rompe otros tests del fixture: retry explícito acotado sobre 40001/40P01 alrededor del `ExecuteAsync` también en código de producción (documentando por qué).
     - **Regla reescrita (checklist #2)**: un serialization failure **nunca aborta la venta sin reintentar**. Agotados los reintentos de la estrategia (5, con la transacción siempre rolleada back — sin estado parcial), el usuario recibe un **error claro y re-presentable** ("no se pudo guardar por concurrencia, volvé a intentar") — la operación es re-submitible tal cual porque el find-or-create es idempotente respecto del Rate ya creado por el ganador. La formulación absoluta de la v2 ("NUNCA aborta") era incumplible y por eso se reescribe.
     - Si en la práctica el Serializable genera fricción medible, el fallback documentado es ReadCommitted + aceptar el duplicado de carrera (lo captura R3/merge) — pero se arranca con Serializable porque es el patrón de la casa y resuelve la carrera a favor.
3. **El REQUEST manda; el Rate solo sugiere (cierra B1 — cambio obligatorio respecto del comportamiento actual)**. La UX aprobada dice "amarillo = sugerido, lo pisás si cambió" y "si lo vendés con OTRO operador, lo cambiás y el sistema recuerda esa combinación". El snapshot actual hace lo contrario (pisa precios en Flight/Package/Transfer/Assistance, SupplierId en Assistance y Hotel, y atributos en Hotel — citas en §1.2). Regla nueva, SOLO con flag ON:
   - Con `RateId`, el Rate aporta **identidad** (`booking.RateId = rate.Id`) y nada más que **relleno de huecos**: un campo del Rate se copia al booking ÚNICAMENTE si el request no trajo valor para ese campo (null/vacío; los precios siempre vienen — `SalePrice > 0` ya es obligatorio; supplier siempre viene — regla 4; `Currency` siempre viene — §2.1).
   - Precios, SupplierId, Currency y atributos editados (HotelName/City/RoomType/MealPlan/StarRating/etc.) **persisten lo que mandó el request**, que es lo que el vendedor vio y pudo pisar en la ficha (el front precarga la sugerencia EN los campos del request; si el usuario no tocó nada, request == sugerencia).
   - **Con flag OFF el snapshot actual queda byte-idéntico** (sigue pisando como hoy): el modal viejo depende de ese comportamiento y la convención de la casa lo exige.

   **3-bis. EXCEPCIÓN a "request manda": costo de un caller SIN `cobranzas.see_cost` (decisión D7 — cierra B-R2-1).** "Request manda" presupone que el vendedor VIO el valor que manda. A quien no tiene `cobranzas.see_cost` el front le oculta costo y ganancia (ServiceFormModal.jsx:1775) y los requests llevan `NetCost`/`Tax` decimal no-nullable (§1.2) → su request llega SIEMPRE con costo `0` que no significa nada. Regla server-side, solo flag ON:
   - **Create**: si el caller no tiene `cobranzas.see_cost`, el server **ignora `NetCost` y `Tax` del request** y los completa por detrás con esta cadena, en orden: **(1)** `RateSupplierSale` del `(RateId efectivo, supplier elegido en ESTA venta)` — `LastNetCost`/`LastTax` unitarios × multiplicador del tipo (inversa de la tabla §2.1), solo si `LastCurrency == request.Currency` (una referencia en otra moneda no es comparable; regla conservadora de diseño, no del dueño — marcada para el reviewer); **(2)** fallback: campos `NetCost`/`Tax` del `Rate`, solo si `Rate.Currency == request.Currency` (ojo unidades: interpretar `Rate.PriceUnit`/`HotelPriceType` como hace el RateSelector hoy); **(3)** si no hay nada utilizable → `0`. La **ganancia persistida se recalcula canónica server-side** con el costo repuesto (`Commission = SalePrice − NetCost − Tax`, mismo precedente del fix B2 "ganancia canónica al guardar" de la feature de impuesto incluido; fórmula exacta por tipo a confirmar contra el código al implementar).
   - **Marca "costo a confirmar" SOLO en los casos dudosos** (textuales de D7): **(a)** producto nuevo sin costo conocido — la cadena terminó en `0`; **(b)** la referencia usada es más vieja que `StaleCostReferenceDays` (setting nuevo, default 60 — edad medida sobre `RateSupplierSale.LastSoldAt` en el paso 1 o `Rate.UpdatedAt ?? Rate.CreatedAt` en el paso 2; campos verificados, Rate.cs:161-162). Los no-dudosos pasan derecho sin marca y con upsert normal.
   - **Un servicio marcado NO upsertea `RateSupplierSale`** (ni costo, ni precio, ni `SalesCount`): la combinación queda sin registrar hasta la confirmación (§2.8), que dispara el upsert diferido. Esto es lo que impide envenenar `LastNetCost` con ceros para todos.
   - **Update**: si el caller no tiene `cobranzas.see_cost`, `NetCost`/`Tax` del request se ignoran y **se preservan los valores persistidos** de la entidad (preservación, no cadena: el costo bueno ya está en la fila). Misma mecánica anti-clobber que los deadlines (§2.2): `Ignore()` en el map + asignación manual condicionada por permiso. **CORRECCIÓN v3.1 (round 3, condición 1): esta preservación sale en F1b SIN flag** — el bug que tapa es real HOY (ver bullet siguiente), no un riesgo introducido por el flag ON. Bajo `EnableCatalogFindOrCreate` solo queda lo demás de 3-bis (cadena en create, marca, upsert diferido).
   - Con caller CON permiso: regla 3 intacta (request manda, sin cadena ni marca).
   - **Bug preexistente CONFIRMADO en round 3, con evidencia (ya no "posible")**: HOY un usuario sin `cobranzas.see_cost` SÍ puede invocar los UPDATE de bookings — `HotelBookingsController.cs:74-77` gatea el PUT solo con `Permissions.ReservasEdit` + ownership, sin gate de costos (verificado por el reviewer round 3 y re-verificado por el architect en v3.1); el GET le devuelve `NetCost` enmascarado a `0` (`CostMasking`); `ServiceFormModal.jsx:2122` puebla el form con ese `0` y el submit manda `netCost=0`; `_mapper.Map` lo clobberea sobre el costo persistido (§1.2). El fix de preservación va en **F1b SIN flag** (precedente B1.15) — §2.7 "Fuga 3". Test obligatorio: update de caller sin permiso con `NetCost=0` → costo persistido intacto, **CON FLAG OFF**.

4. **Validación de supplier server-side**: en el path flag ON, supplier es obligatorio en los 5 tipos (cierra el pendiente conocido §1.3 para el flujo nuevo; el path flag OFF queda byte-idéntico, el pendiente legacy se cierra aparte).
5. Si viene `NewCatalogProduct`:
   - **Find-or-create defensivo**: si ya existe un Rate activo con mismo `ServiceType` + `SearchName` igual → se REUSA en vez de crear duplicado. Reglas exactas en §2.4 (para Hotel exige además misma City; el supplier NO participa de la identidad).
   - **En el path `NewCatalogProduct` → reuso NO hay relleno de huecos desde el Rate reusado (checklist round 2 #6)**: el usuario eligió "crear nuevo" y completó la ficha entera → **la ficha manda todo**; del Rate reusado se toma SOLO la identidad (`booking.RateId`). El relleno de huecos de la regla 3 aplica únicamente al path `RateId` (donde el front precargó sugerencias del producto elegido). Mezclar atributos de un Rate que el usuario nunca vio sería sorpresa silenciosa. **Excepción (round 3, nota 3): para `NetCost`/`Tax` de un caller SIN `cobranzas.see_cost`, la regla 3-bis le gana a "la ficha manda todo" — la cadena D7 aplica en TODOS los paths de create, incluido `NewCatalogProduct`→reuso** (ese caller nunca vio el costo que "completó": llega como `0` sin significado).
   - Si no existe → `new Rate { CreatedInSale = true, CreatedFromReservaId = reservaId, SupplierId = operador de esta venta, NetCost/SalePrice/Tax = los de esta venta, Currency = request.Currency (D5 — nunca el default), PriceUnit = el de la tabla §2.1, campos por tipo según request }`.
   - **Producto creado inline por un caller SIN `cobranzas.see_cost` (round 3, nota 4)**: la cadena D7 no tiene de dónde reponer (producto nuevo) → el Rate nace con `NetCost = 0` **y QUEDA así** — es la regla deliberada de §2.1 de NO pisar los campos de precio del Rate con ventas. `rateFallback` mostrará costo `0` a quienes ven costos hasta la primera `RateSupplierSale` confirmada (botón D8, §2.8). **Al implementador: NO "arreglar" esto pisando el Rate en la confirmación** — la fuente viva de la sugerencia es `RateSupplierSale`, no el Rate.
6. Upsert de `RateSupplierSale` (RateId efectivo, SupplierId efectivo de esta venta) con los valores UNITARIZADOS efectivamente persistidos en el booking — `NetCost`, `Tax` (→ `LastTax`) y `SalePrice` (tabla §2.1): aplica tanto al path `NewCatalogProduct` como al path `RateId` — el operador efectivo puede diferir del sugerido y eso es exactamente lo que registra la tabla. Skip si `SupplierId <= 0` **o si el servicio quedó marcado "costo a confirmar"** (regla 3-bis / §2.8: el upsert se difiere a la confirmación).
7. **Conversión de presupuesto (cierra M4 — datos corregidos)**: el método real es `QuoteService.ConvertToFileAsync` (QuoteService.cs:244). Hechos verificados: NO es atómico (SaveChanges en :288 y :445 — ese diseño NO se cambia en este ADR), cubre Hotel/Aéreo/Traslado/Paquete pero **Asistencia cae al `ServicioReserva` genérico** (:408-429, sin upsert), y el SupplierId efectivo puede ser `0` (fallback `?? 0`, :308). Regla: tras el `SaveChanges` final exitoso (:445), se llama al helper de upsert por cada servicio especializado creado **que tenga `RateId` y `SupplierId > 0`** (skip-supplier-0). Como `ConvertToFileAsync` no es transaccional, el upsert acá es **post-éxito y best-effort**: si el upsert falla se loguea warning y NO se revierte la conversión (la tabla es estadística de sugerencia; la query de reconciliación de R7 detecta el faltante). Asimetría con el create transaccional: **deliberada y documentada** (cierra M5), no accidental.

El response de los creates ya existente no cambia de forma; se agrega `createdRatePublicId` (nullable) para que el front pueda mostrar el pill "creado en venta" sin re-fetch.

#### c) Lo que NO se hace en esta fase

- No se toca `ServicioReserva` genérico (no está en los 5 tabs del mockup y no snapshotea Rate).
- No se construye la pantalla de merge (ver §6 — el modelo la deja preparada).
- No se vuelve atómico `ConvertToFileAsync` (preexistente, fuera de alcance; queda anotado como deuda conocida).

### 2.4 Identidad de producto y estrategia anti-duplicados (cierra B3 y M1)

**Dos identidades coexisten y NO compiten** (esta sección faltaba y el review la exigió):

| Identidad | Qué es | Dónde vive | Para qué sirve |
|---|---|---|---|
| **Huella** (existente, NO se toca) | Duplicado exacto de TARIFA: proveedor + nombre + componentes por tipo (Hotel: habitación+pensión+categoría; RateService.cs:551-553, 649-653) | `FindDuplicateCandidatesAsync` / `POST /rates/duplicate-check` (Admin) | Back-office: evitar cargar dos veces la MISMA tarifa del MISMO proveedor |
| **SearchName** (nueva) | Agrupador de CATÁLOGO supplier-agnóstico: "el producto a secas" | `Rate.SearchName` + índice trigram | El buscador de venta y el find-or-create defensivo: el vendedor busca "el hotel", no "la tarifa del proveedor X" |

Son niveles de granularidad distintos del mismo agregado: la huella distingue tarifas DENTRO de un producto; el SearchName agrupa Rates EN un producto. Ninguna reemplaza a la otra; el duplicate-check Admin sigue funcionando igual.

**Regla de reuso defensivo (find-or-create), acotada y explícita**:
- Igualdad EXACTA normalizada de `SearchName` dentro del mismo `ServiceType`, sobre Rates activos.
- **Hotel: exige además misma `City`** (normalizada): dos "Hotel Costanera" de ciudades distintas son productos distintos. Si el Rate candidato tiene City null/vacía y el request trae City (o viceversa), NO matchea (en la duda, crear y dejar que el merge una — más barato que contaminar un producto ajeno).
- **Supplier distinto: SE REUSA IGUAL.** El producto es supplier-agnóstico por diseño; que esta venta sea con otro operador es información que va a `RateSupplierSale` (la combinación nueva se recuerda), no un motivo para duplicar el producto. El `Rate.SupplierId` del producto existente NO se modifica.
- El matching difuso (similarity) NO participa del reuso defensivo — solo del dropdown. Reusar por parecido (no igualdad) podría fusionar productos distintos sin intervención humana; eso es trabajo de la pantalla de merge (candado 3).

**Normalizador (cierra M1)**: NO se crea `CatalogNameNormalizer`. Se reusa el existente `TextNormalizer` (`src/TravelApi.Domain/Helpers/TextNormalizer.cs`): se agrega un método `NormalizeForCatalog(string?)` que delega en `NormalizeForMatch` (:36 — lower + trim + sin tildes NFD + colapsa espacios, ya testeado y usado por `FindExactMatchAsync`) y suma el colapso de puntuación repetida. Se agrega como método NUEVO y no como cambio dentro de `NormalizeForMatch` para no alterar silenciosamente la semántica del duplicate-check existente. Tests unitarios nuevos junto a los del helper.

**Los "tres candados" del mockup**:

**Candado 1 — el buscador encuentra parecidos aunque escribas mal**: normalización en escritura (`SearchName` por `NormalizeForCatalog`, aplicada al crear/editar Rate por CUALQUIER vía); umbral de candidatos `similarity >= 0.4` (constante compartida `FuzzyMatchSimilarityThreshold`, RateService.cs:30) + condición `%` con su GUC (§2.3.a); match fuerte (resaltado `.hit` del mockup) con `score >= 0.65` (constante separada `StrongMatchThreshold`); empates y orden determinístico según la SQL de §2.3.a.

**Candado 2 — crear nuevo es SIEMPRE la última opción**: regla de front (el dropdown renderiza la opción crear al final). Refuerzo server-side: el find-or-create defensivo reusa ante igualdad exacta normalizada — aunque el usuario haga clic en "crear", si es textualmente el mismo producto no se duplica.

**Candado 3 — lo creado en venta queda marcado y se puede unir**: `CreatedInSale` + diseño de merge en §6.

**Carrera entre dos vendedores**: no se agrega UNIQUE sobre `SearchName` (nombres iguales pueden ser legítimos entre ciudades distintas; un falso positivo bloquearía la venta, violando la regla del dueño). La resolución de la carrera quedó definida en §2.3.b regla 2 (Serializable + retry resuelve a favor del reuso); el residuo improbable lo captura la pantalla de revisión. Riesgo R3.

### 2.5 Alertas de fechas límite (incorpora D2 — ex-Q2 RESUELTA por el dueño)

**Modelo**: ninguna entidad nueva. Las fechas viven en los bookings (§2.2); las alertas se computan on-read, igual que las existentes.

**Quién ve qué (decisión D2, cambia el gating actual)**: cada vendedor ve los avisos de SUS reservas; el admin ve todos.

- **Backend**: `GetAlertsAsync` pasa a recibir la identidad del caller (hoy no la recibe — verificado; **el cambio de firma + el gating admin-only de los buckets financieros se adelantan a F1b como fix de seguridad, M-R2-1/§2.7** — esta fase solo agrega el bucket nuevo encima). El bucket nuevo `ServiceDeadlines` se filtra **server-side**: admin → todas las reservas; no-admin → solo reservas con `Reserva.ResponsibleUserId == callerId` (campo verificado, Reserva.cs:111). Los buckets existentes (`UrgentTrips`, `SupplierDebts` — información financiera global) se devuelven **solo a admin**: hoy su gating era puramente client-side (`AlertsContext.jsx:13`), y al abrir el polling a no-admins ese gating DEBE subir al server (no confiar en el cliente). Para un caller no-admin el payload trae solo `ServiceDeadlines` + `TotalCount` acorde.
- **Limitación conocida y honesta**: reservas históricas con `ResponsibleUserId` null (backfill pendiente, memoria de proyecto) no le suenan a ningún vendedor — solo al admin. Se documenta al dueño; el backfill de responsables es un pendiente preexistente fuera de este ADR.
- **Frontend**: `AlertsContext` deja de gatearse por `isAdmin()` cuando `EnableServiceDeadlineAlerts` está ON: pollea para todos los usuarios (`isAdmin() || flags.enableServiceDeadlineAlerts`). Con flag OFF, comportamiento idéntico al actual (solo admin pollea).

**Enchufe backend**: bucket `ServiceDeadlines` (clave nueva en el objeto anónimo — JSON aditivo, no rompe consumidores; misma convención PascalCase):

```jsonc
"ServiceDeadlines": [
  {
    "reservaPublicId": "…", "numeroReserva": "…",
    "serviceKind": "Hotel" | "Aereo" | "Paquete",
    "serviceLabel": "Hotel Maitei Posadas",
    "deadlineKind": "OperatorPayment" | "Ticketing",
    "deadline": "2026-07-10",
    "isOverdue": false
  }
]
```

Criterio de inclusión: `deadline <= hoy + ServiceDeadlineAlertDays` (setting nuevo `int`, default 7, junto a `UpcomingUnpaidReservationAlertDays`), reserva en estado activo (no Cancelled/Closed), servicio no cancelado, con "hoy" = fecha local Argentina (§2.2). Los vencidos (`deadline < hoy`) se incluyen con `isOverdue=true` mientras la reserva siga activa y el viaje no haya empezado. Aéreo agrupa por `(ReservaId, PNR)` con `MIN(TicketingDeadline)`; segmentos sin PNR utilizable, individuales (§2.2).

**Enchufe frontend**:
- **Etiquetas en la fila** (Momento 4 del mockup): NO usan el sistema de alertas — salen directo del DTO del servicio (los deadlines viajan en los DTOs de booking). Pill ámbar si el deadline está dentro de la ventana, rojo si venció. Cero dependencia de `/alerts`.
- **Campanita global**: render del bucket nuevo + el cambio de gating de arriba.

### 2.6 Feature flags

Dos flags nuevos en `OperationalFinanceSettings` (convención de la casa, default `false`, editables en panel admin):

| Flag | Gobierna | OFF = |
|---|---|---|
| `EnableCatalogFindOrCreate` | `catalog-search`, `NewCatalogProduct` + `Currency` obligatoria en creates, regla "request manda" (§2.3.b.3) **+ su excepción D7 (regla 3-bis: cadena de costo server-side, marca "costo a confirmar", upsert diferido — §2.8)**, **acción explícita "Confirmar costo" (D8c, §2.8)**, transacción del create, upsert `RateSupplierSale`, ficha inline en front. **OJO v3.1: la preservación de `NetCost`/`Tax` en UPDATE para callers sin permiso NO va acá — sale en F1b SIN flag (§2.7 Fuga 3, round 3 condición 1)** | Byte-idéntico: endpoints 404, requests con `NewCatalogProduct` → 400, snapshot actual pisa como hoy, sin transacción nueva, sin upsert, sin marcas D7, modal actual intacto (la preservación F1b queda activa igual: es fix de seguridad, no feature) |
| `EnableServiceDeadlineAlerts` | Bucket `ServiceDeadlines` en `/alerts` + setting de ventana + apertura del polling a no-admins | `/alerts` byte-idéntico al actual; `AlertsContext` solo-admin como hoy |

**Explícito (cierra m2)**: con flag OFF la API **acepta y persiste** los campos de deadline (`OperatorPaymentDeadline`/`TicketingDeadline` en create/update requests y DTOs) aunque no haya UI que los cargue — mismo precedente que `AddBookingCurrencyTraceability` (columnas y passthrough aditivos; sin UI que los escriba no cambian nada observable). Lo mismo vale para `Currency` en los create requests (opcional/ignorada-si-null con flag OFF, §2.1). La **UI** que carga deadlines vive en la ficha inline (gateada por `EnableCatalogFindOrCreate`); las etiquetas en fila solo aparecen si hay dato → sin dato, render idéntico.

La fase F1b (§2.7) **no lleva flag**: son fixes de seguridad (precedente B1.15, shipeado sin flag) — el masking de costos, el gating server-side de `/alerts` **y la preservación de `NetCost`/`Tax` en updates de callers sin permiso (Fuga 3, v3.1)**.

Settings nuevos (no flags, `int` editables en panel admin): `ServiceDeadlineAlertDays` (default 7, §2.5) y `StaleCostReferenceDays` (default 60, §2.8).

Dos flags y no uno: las fechas límite son independientes del find-or-create y pueden prenderse antes (riesgo mucho menor). Evita acoplar el rollout.

**N1 — el bucket `CostsToConfirm` cuelga del flag del catálogo, NO del de deadlines (decisión del dueño, 2026-06-06).** La marca "costo a confirmar" (D7) solo la produce el path catálogo (§2.3.b.3-bis), así que el bucket `CostsToConfirm` en `/alerts` se activa con `EnableCatalogFindOrCreate` (además del permiso `cobranzas.see_cost`, §2.8/D8b), **no** con `EnableServiceDeadlineAlerts`. Consecuencia operativa: al prender el catálogo, a los callers con `cobranzas.see_cost` les empieza a aparecer `costsToConfirm` en `/alerts` aunque `EnableServiceDeadlineAlerts` siga OFF (y `serviceDeadlines` ausente). Con ambos flags en su default `false` el endpoint sigue byte-idéntico (objeto histórico de 3 claves camelCase). El casing del contrato es camelCase en todos los paths (DTO tipado `AlertsResponse`, no `Dictionary<string,object>`).

### 2.7 Tapar las fugas preexistentes: costos en tarifario (D3) + `/alerts` sin gating (M-R2-1) + update que destruye el costo (round 3) — fase F1b, sin flag

Verificado: `GET /api/rates/search` (RatesController.cs:84-97 → `SearchAsync`, RateService.cs:318-356) devuelve `NetCost` y `Tax` a cualquier usuario logueado. La ficha nueva NO consume ese endpoint (usa `catalog-search`, que nace enmascarado), pero la fuga existe HOY con el RateSelector actual y el dueño ordenó taparla.

Alcance (fase F1b, sin flag, deployable independiente y ANTES que el resto):
- `SearchAsync`: anular `NetCost`/`Tax` (convención de la casa: `0m`, como `CostMasking`) si el caller no tiene `cobranzas.see_cost`.
- **Barrido del resto del `RatesController`**: `GET /api/rates` (`GetAllAsync`/`RateListItemDto`), `/groups`, `/hotels`, `/summary`, `/{publicId}` — todo DTO que exponga `NetCost`/`Tax` pasa por la misma regla. (El detalle endpoint-por-endpoint lo cierra el implementador con el reviewer de seguridad; la regla es una sola: **ninguna búsqueda/lectura de tarifario muestra costo sin `cobranzas.see_cost`**.)
- `POST /rates/duplicate-check` ya es Admin-only (no fuga).
- Impacto front verificable: el RateSelector existente y la pantalla de tarifario muestran costos — para usuarios sin permiso pasarán a ver `0`/oculto. Es el comportamiento que el dueño pidió; revisar que la UI no muestre "$0" confuso (mostrar venta u ocultar la columna, criterio D1).

**Fuga 2 — `/alerts` sin gating server-side (M-R2-1, verificada v3, explotable HOY)**: `AlertsController.cs:7` es `[Authorize]` plano y `GetAlertsAsync` no recibe identidad → cualquier logueado lee `SupplierDebts`/`UrgentTrips` (deudas a proveedores y viajes urgentes de TODA la agencia) con un curl; el gating "solo admin" vive únicamente en `AlertsContext.jsx:13` (client-side). Entra a **F1b, SIN flag** (mismo precedente B1.15 que la fuga 1):
- `GetAlertsAsync` recibe la identidad/rol del caller (el mismo cambio de firma que §2.5 necesita después — se hace UNA vez acá).
- Caller no-admin → `UrgentTrips`/`SupplierDebts` vacíos y `TotalCount = 0` (payload con la misma forma, no 403: deja el contrato listo para que F3 agregue `ServiceDeadlines` per-vendedor sin otro cambio de contrato).
- Caller admin → byte-idéntico a hoy.
- Comportamiento observable para usuarios legítimos: ninguno (hoy solo el front de admin llama). Test de integración: usuario no-admin recibe buckets vacíos; admin recibe payload idéntico al snapshot actual.

**Fuga 3 — el update de un caller sin `cobranzas.see_cost` destruye el costo persistido HOY (round 3, condición 1 — CONFIRMADA con evidencia, ya no "posible")**: `HotelBookingsController.cs:74-77` gatea el PUT solo con `Permissions.ReservasEdit` + ownership, sin gate de costos; el GET le enmascara `NetCost` a `0` (`CostMasking`); `ServiceFormModal.jsx:2122` puebla el form de edición con ese `0` (`unitNetCost` derivado de `serviceToEdit.netCost`); el submit manda `netCost=0` y `_mapper.Map` lo clobberea sobre el costo bueno (§1.2). Es destrucción silenciosa de datos financieros en cada edición legítima. Fix en **F1b SIN flag** (precedente B1.15): `Ignore()` de `NetCost`/`Tax` en los maps de UPDATE + asignación manual condicionada por permiso (caller sin `cobranzas.see_cost` → se preservan los persistidos), en los 5 tipos. Test obligatorio: update de caller sin permiso con `NetCost=0` → costo persistido intacto, **CON FLAG OFF**. (Esta es la misma mecánica que la regla 3-bis reusa después con flag ON — se construye UNA vez acá.)

### 2.8 "Costo a confirmar" (decisión D7) — dónde vive la marca, cómo se confirma, umbral

**Dónde vive la marca — campo en los bookings, NO estado del workflow.** Columnas aditivas en las 5 entidades (`HotelBooking`, `FlightSegment`, `TransferBooking`, `PackageBooking`, `AssistanceBooking`), misma migración que los deadlines:

```csharp
public bool CostToConfirm { get; set; } = false;          // default false: filas existentes no cambian
[MaxLength(30)]
public string? CostToConfirmReason { get; set; }          // "NoKnownCost" | "StaleReference" (null si no hay marca)
```

Por qué campo y no estado: `Status` es el eje del rediseño de estados en curso (`EnableSoldToSettleStates`, matrices de transición, CHECK de 9 valores) — meterle un valor más acoplaría dos features independientes y rompería esas matrices. La marca es **ortogonal al workflow**: un servicio "a confirmar" se confirma, factura y viaja igual; lo único que bloquea es el upsert de `RateSupplierSale`. Una entidad aparte (cola de pendientes) sería overengineering: la bandeja se computa on-read con un `WHERE CostToConfirm`, igual que las alertas existentes.

**Quién marca**: solo el server, en el create con caller sin `cobranzas.see_cost`, según los dos casos dudosos de la regla 3-bis. Un caller CON permiso nunca genera marca.

**Quién y cómo confirma (decisión D8 — REEMPLAZA la confirmación implícita de v3)**:
- **Quién**: un usuario con `cobranzas.see_cost` (y el gate de edición existente del servicio).
- **Cómo (D8c)**: acción **EXPLÍCITA con botón "Confirmar costo"** — el usuario confirma el número tal cual o lo corrige. NO hay confirmación implícita: **un update normal (guardar) de un caller con permiso sobre un servicio marcado NO limpia la marca ni dispara el upsert** — solo el botón lo hace. Diseño del trigger: **capa fina sobre el update handler existente (round 3, nota 5)** — endpoint mínimo por servicio (p. ej. `POST .../bookings/{id}/confirm-cost`, ruta exacta a alinear con las convenciones de los controllers de bookings al implementar), gateado por `EnableCatalogFindOrCreate` (OFF → 404) + `cobranzas.see_cost` + el gate de edición; body opcional con el costo corregido (`NetCost`/`Tax`). Server: si vino corrección → setea el costo definitivo y recalcula la ganancia canónica (misma fórmula del precedente "impuesto incluido"); limpia `CostToConfirm`/`CostToConfirmReason`; **dispara el upsert diferido** de `RateSupplierSale` con los valores confirmados y `LastSoldAt` = **fecha de creación del servicio** (la venta ocurrió entonces; usar la fecha de confirmación mentiría el "hace N semanas" del dropdown). Idempotente: confirmar un servicio sin marca → no-op (200, sin upsert duplicado).
- **Confirmar costo `0` desde el botón SÍ vale**: es una aserción humana deliberada ("este servicio realmente costó 0"), no un accidente — limpia la marca y upsertea `LastNetCost = 0` como dato real. El guard anti-confirmación-accidental que pedía el round 3 (ítem 2) pierde urgencia: el botón elimina ese riesgo por construcción (nadie confirma sin querer al guardar otra cosa).
- **Dónde lo ve quien confirma (D8a/D8b — APROBADO por el dueño, asentado en `docs/ux/guia-ux-gaston.md`, "UI del costo a confirmar")**: (1) pill ámbar "Costo a confirmar" en la fila del servicio, visible SOLO para usuarios con `cobranzas.see_cost` — **el vendedor sin permiso que generó la marca NO ve nada (D8a)**; viaja como campo nuevo en los DTOs de booking, igual que los deadlines, enmascarado/omitido para callers sin permiso; (2) bucket `CostsToConfirm` en `/alerts`, gateado **server-side** por `cobranzas.see_cost` (la firma con identidad del caller ya queda hecha en F1b) — items: reserva, tipo, etiqueta del servicio, razón, fecha (D8b). Entra en F3 junto con el bucket de deadlines.

**Umbral configurable**: `StaleCostReferenceDays` (`int`, default `60`) en `OperationalFinanceSettings` (hogar verificado de settings análogos: `OperatorRefundTimeoutDays = 60`, `UpcomingUnpaidReservationAlertDays = 7`), editable desde el panel admin como los demás.

**Interacción con flags**: la regla D7 de CREATE (cadena, marca, upsert diferido) y el botón "Confirmar costo" viven bajo `EnableCatalogFindOrCreate` — con flag OFF el snapshot actual ya repone el costo desde el Rate y el upsert no existe, así que no hay nada que proteger (byte-idéntico). **Excepción v3.1: la preservación de `NetCost`/`Tax` en UPDATE sale en F1b SIN flag** (round 3, condición 1 — el bug existe hoy, §2.7 Fuga 3). Las columnas se crean igual (aditivas, default false/null: invisibles con flag OFF). La pill y el bucket además requieren su fase (F2/F3).

---

## 3. Alternativas consideradas

**A1 — Entidad nueva `CatalogProduct` + `Rate.ProductId`** (producto separado de tarifa). Más pura conceptualmente, pero: requiere FK nueva o migrar los 7 punteros existentes, duplica la búsqueda, duplica pantallas back-office, y no aporta nada que `Rate` con `SupplierId` nullable no cubra hoy. **Rechazada: overengineering para una agencia single-tenant.**

**A2 — Pisar `Rate.NetCost/SalePrice` con cada venta** (sin tabla `RateSupplierSale`). Más simple, pero pierde la memoria por-operador (requisito textual del dueño), corrompe el tarifario curado y hace imposible auditar la sugerencia. **Rechazada.**

**A3 — Calcular "última venta" on-the-fly desde los bookings** (sin tabla). Sin migración extra, pero la consulta une 5 tablas + ServicioReserva por cada keystroke — frágil y lento. La tabla denormalizada se actualiza en el único lugar donde nacen ventas y se puede **reconstruir** desde los bookings (query de reconciliación en §7). **Elegida la tabla.**

**A4 — Endpoint separado `POST /api/catalog/products` + create de servicio en segunda llamada**. Viola la atomicidad ("Guardar servicio y hotel" es UNA operación); deja productos huérfanos ante fallos del segundo paso. **Rechazada.**

**A5 — Entidad persistida de Alertas con estado (visto/descartado)**. El sistema actual es compute-on-read y funciona; estado persistido es complejidad sin pedido del dueño. **Rechazada para MVP.**

**A6 — UNIQUE sobre `SearchName` para matar la carrera de creación**. Bloquearía ventas legítimas (homónimos entre ciudades en tipos no-Hotel) con un 23505 → viola "no frena la venta". **Rechazada**; en su lugar: Serializable + retry (§2.3.b.2) + merge.

**A7 — ReadCommitted + aceptar el duplicado de carrera** (en vez de Serializable + retry). Cero serialization failures, pero la carrera de creación produce duplicados sistemáticos en vez de excepcionales. **Queda como fallback documentado** si Serializable genera fricción medible en producción; se arranca con el patrón de la casa.

---

## 4. Consecuencias

**Positivas**: tarifario vivo sin trabajo extra; carga de servicio repetido en segundos; anti-duplicados en tres capas; cero cambio para instalaciones con flag OFF; fuga de costos histórica tapada; el modelo deja el merge preparado sin construirlo.

**Negativas / deudas asumidas**:
- `RateSupplierSale` es denormalizada: puede divergir si alguien crea servicios por una vía que no llame al helper (mitigación: helper único + reconciliación en tests). El upsert post-éxito de `ConvertToFileAsync` es best-effort por diseño (§2.3.b.7).
- La regla "request manda" (flag ON) convive con el snapshot "rate manda" (flag OFF) en el mismo service hasta F4: dos semánticas detrás de un if de flag. Deliberado (byte-idéntico OFF) y temporal; se documenta inline.
- El dedupe por `SearchName` puede agrupar homónimos legítimos en tipos no-Hotel (Aéreo/Traslado usan ruta como nombre — colisión improbable). Hotel desambigua por City.
- La sugerencia de precio es "última venta", no "mejor precio" ni "precio vigente" — referencia editable (amarillo del mockup), no tarifa firme. La unitarización redondeada puede derivar centavos al re-multiplicar: aceptado.
- `ServiceFormModal.jsx` convive con la ficha nueva hasta F4 (duplicación temporal deliberada para rollback).
- Usuarios sin `cobranzas.see_cost` que HOY ven costos en el tarifario dejarán de verlos (F1b): comportamiento ordenado por el dueño, comunicarlo en el deploy.
- D7: un servicio "costo a confirmar" que nadie confirma nunca deja su combinación (Rate, Supplier) sin registrar en `RateSupplierSale` — la sugerencia para el próximo vendedor no mejora. Con la confirmación explícita por botón (D8c) este riesgo SUBE un poco respecto de la implícita de v3 (confirmar requiere una acción dedicada, no pasa "de paso" al editar). Mitigado por la pill + el bucket `CostsToConfirm` (§2.8) y detectable por la query de reconciliación R7; aceptado como costo de no envenenar la tabla con ceros.
- **Regla de moneda conservadora de la cadena D7 (round 3, nota 7)**: una referencia en otra moneda no se usa → tarifario cargado en USD + venta en ARS por un vendedor sin permiso = más marcas "a confirmar" de las estrictamente necesarias. Aceptado como costo de no mezclar monedas en un costo invisible para quien vende; **revisitar cuando ADR-011 (automatización del tipo de cambio) habilite comparar entre monedas**.

---

## 5. Fases de implementación (sin big-bang) + esfuerzo estimado

La ficha inline reemplaza un modal de ~2500 líneas. Regla: **el modal NO se refactoriza ni se toca** — la ficha nueva es un árbol de componentes aparte; el modal sigue siendo el camino con flag OFF hasta F4.

| Fase | Contenido | Esfuerzo (relativo) |
|---|---|---|
| **F1b — Tapar fugas preexistentes** (puede ir PRIMERA, independiente, sin flag) | Masking `cobranzas.see_cost` en `SearchAsync` + barrido `RatesController` (§2.7) + ajuste UI del RateSelector/tarifario + **gating server-side de `/alerts` (M-R2-1)**: firma con identidad del caller, buckets financieros solo-admin + **preservación de `NetCost`/`Tax` en los UPDATE de los 5 tipos para callers sin permiso (Fuga 3, round 3 condición 1: `Ignore()` en maps + asignación condicionada por permiso)** + tests (incluido: update sin permiso con `NetCost=0` → costo intacto, CON FLAG OFF) | **S-M** (2-3 días; sube respecto de v3 por la Fuga 3 × 5 tipos) |
| **F1 — Backend foundation** (flag OFF, deployable en cualquier momento) | Migración 1 (Rate: 3 columnas + backfill type-aware de SearchName + índice; tabla RateSupplierSale **con LastTax**) y migración 2 (3 columnas deadline + **CostToConfirm/CostToConfirmReason × 5 entidades** + Update requests con discriminador `DeadlinesSpecified`) — ambas aditivas, **encoladas DETRÁS de las migraciones pendientes del VPS sin tocarlas**. `TextNormalizer.NormalizeForCatalog`. `catalog-search`. `Currency` + `NewCatalogProductRequest` (City obligatoria Hotel, D6) + transacción + regla request-manda **+ regla 3-bis D7 (cadena de costo + marca + upsert diferido; la preservación en update YA quedó hecha en F1b — v3.1)** + upsert en los 5 creates + post-éxito en `ConvertToFileAsync`. **Acción explícita "Confirmar costo" (D8c, §2.8): endpoint capa fina sobre el update handler, gateado por flag + permiso**. Anti-clobber de updates (`Ignore()` + asignación manual, §2.2). Flags + 2 settings + panel admin. Bucket `ServiceDeadlines` + filtrado por caller. Tests de F1 (§7) | **L** (5-8 días; sube respecto de v2: regla request-manda × 5 tipos + D7 × 5 tipos + anti-clobber + tests de concurrencia; el endpoint de confirmación compensa lo que se movió a F1b) |
| **F2 — Ficha inline, tipo por tipo** (flag OFF en prod durante toda la fase) | `ServiceInlineCard` (shell) + `ProductSearchField` + campos por tipo × 5, contra el mockup ESTRICTO. Orden: Hotel (desbloqueado por D6) → Aéreo → Traslado → Paquete → Asistencia. Pill ámbar "costo a confirmar" en fila + botón "Confirmar costo" para usuarios con permiso (**D8 aprobada**). El flag no se prende hasta que los 5 estén | **XL — la fase más grande** (2-3 semanas: árbol nuevo × 5 tipos, buscador con dropdown, sugerencias amarillas, revelado progresivo, permisos de costo) |
| **F3 — Deadlines visibles + alertas** | Etiquetas en fila (DTOs), render campanita + apertura per-vendedor (D2) + **bucket `CostsToConfirm` gateado por `cobranzas.see_cost`** (§2.8, **D8b aprobada**). Prender `EnableServiceDeadlineAlerts` | **M** (2-3 días; la base server-side del gating ya quedó hecha en F1b) |
| **F4 — Switch y limpieza** | Prender `EnableCatalogFindOrCreate` → validación de Gastón → 2+ semanas de observación (rollback = apagar flag) → borrar `ServiceFormModal.jsx` (el flag queda como kill-switch una versión más) | **S** |
| **F5 — Pantalla de revisión/merge** | Futuro, fuera de este MVP (§6, mini-ADR propio) | — |

---

## 6. Diseño previsto del merge (futuro, NO MVP — el modelo lo deja preparado)

- Pantalla admin: lista Rates con `CreatedInSale = true` (revisar/aprobar/renombrar) y detector de pares sospechosos (`similarity(SearchName) >= 0.65` dentro del mismo tipo).
- Operación `MergeRates(loserId, winnerId)`, transaccional: repuntar `RateId` en las 7 tablas; fusionar `RateSupplierSale` (por supplier, gana `LastSoldAt` mayor, `SalesCount` se suma); el perdedor queda `IsActive = false` + columna futura `MergedIntoRateId` (se agrega recién en esa fase, aditiva). **No se borra nada**: los snapshots en bookings son copias; las reservas históricas no cambian ni un centavo.
- Requiere su propio mini-ADR cuando se priorice (permisos, auditoría, reversibilidad).

---

## 7. Riesgos top y tests que exige cada uno

| # | Riesgo | Mitigación | Tests requeridos |
|---|---|---|---|
| R1 | **Fuga de costos**: `catalog-search` expone costo a usuarios sin `cobranzas.see_cost`; ídem la fuga legacy de `/rates/search` (D3) | Masking en service (`netCost` null en catalog-search; `0m` en los endpoints legacy) | Unit: con y sin permiso, `netCost` null/presente y `salePrice` SIEMPRE presente (D1). Integración: usuario vendedor real contra `catalog-search` Y contra `/rates/search` + `GET /rates` post-F1b |
| R2 | **Atomicidad rota**: falla el create después de insertar el Rate → producto huérfano | Transacción única flag ON (ambos paths, §2.3.b.2) | Integración (VPS, Postgres real): inyectar fallo post-creación de Rate → ni Rate ni booking ni RateSupplierSale persistidos |
| R3 | **Duplicados pese a todo**: carrera entre vendedores o usuario que ignora el dropdown | Find-or-create defensivo (§2.4) + Serializable+retry + futura pantalla merge | Unit: `NormalizeForCatalog` (acentos, espacios, mayúsculas, puntuación; "Maitey"→"maitei" NO es igualdad exacta → crea). Integración: dos creates secuenciales mismo nombre normalizado → un solo Rate; Hotel con misma SearchName y distinta City → DOS Rates; supplier distinto sobre mismo producto → un Rate, dos filas RateSupplierSale |
| R4 | **Flag OFF no byte-idéntico** | Gating estricto en service layer | Integración con flag OFF: `catalog-search` 404; create con `NewCatalogProduct` 400; **create con `RateId` se comporta EXACTAMENTE como hoy (el snapshot pisa) y NO escribe RateSupplierSale**; `Currency` null ignorada; `/alerts` payload idéntico (snapshot test); deadline fields aceptados y persistidos sin UI (m2) |
| R5 | **Calidad de búsqueda**: umbral mal calibrado | Constantes calibrables; conservar las DOS condiciones trigram (§2.3.a / m3); corpus de typos | Integración (pg_trgm real): corpus typo→esperado; orden determinístico; dedupe de N rates legacy del mismo hotel → 1 resultado; hoteles homónimos de 2 ciudades → 2 resultados (m1) |
| R6 | **Sugerencia de precio en unidad equivocada** → vendedor cotiza mal | Tabla de unitarización §2.1 + `LastPriceUnit` explícito + redondeo único | Unit por tipo: hotel 7 noches × 2 habitaciones → `LastNetCost` = costo por noche POR HABITACIÓN (D4); paquete 2+1 pax → por persona; asistencia pax×días; guardas de divisor cero |
| R7 | **Divergencia de RateSupplierSale** vs bookings | Helper único de upsert; `ConvertToFileAsync` post-éxito best-effort documentado | Integración: query de reconciliación (última venta real desde bookings == fila de la tabla); conversión de presupuesto upsertea solo servicios con RateId y supplier > 0; **supplier 0 → skip sin error** |
| R8 | **Cola de migraciones del VPS**: hay migraciones pendientes sin aplicar | Solo aditivo, `IF NOT EXISTS` en índices, backfill liviano (tabla Rates chica en single-tenant), **encoladas detrás de la cola pendiente sin tocarla** | Integración: migrar desde snapshot de schema actual; idempotencia (re-run no falla); backfill type-aware verificado (ver gap B3 abajo) |
| R9 | **Alertas con ruido** o visibilidad equivocada (D2) | Filtros de estado + filtrado server-side por ResponsibleUserId + buckets admin-only en server | Unit/integración: reserva Cancelled / servicio cancelado / viaje empezado → fuera; overdue con `isOverdue=true`; **vendedor A no ve deadlines de reservas de B; admin ve todo; no-admin NO recibe UrgentTrips/SupplierDebts**; PNR null/"TBD" → avisos individuales |
| R10 | **Carrera de creación aborta la venta sin reintentar** (serialization failure) | `ON CONFLICT` para el upsert + retry YA configurado (Program.cs:161-165, hecho verificado) + delegate re-ejecutable (ChangeTracker, checklist #3) + **paridad de retry en el fixture** (PostgresIntegrationFixture.cs:305/:340 hoy SIN retry — sin paridad este test no prueba la regla) (§2.3.b.2) | Integración (fixture con retry): **dos creates CONCURRENTES reales** (no secuenciales) mismo (Rate, Supplier) → 1 fila RateSupplierSale, `SalesCount = 2` (aceptando DO UPDATE directo o 40001+retry, §2.1), **ninguna venta abortada**; dos creates concurrentes con `NewCatalogProduct` igual → ambas ventas OK y un solo Rate; reintentos agotados → error claro re-presentable y CERO estado parcial |
| R11 | **B-R2-1: vendedor sin `cobranzas.see_cost` destruye el costo y envenena `RateSupplierSale` con 0** | Regla 3-bis (D7): cadena server-side + marca "a confirmar" en dudosos + upsert diferido (§2.3.b.3-bis, §2.8) + **preservación en update en F1b SIN flag (§2.7 Fuga 3 — v3.1)** + **confirmación EXPLÍCITA por botón (D8c)** | **Por cada uno de los 5 tipos**: create con caller SIN permiso y `NetCost=0` en el request → el booking persiste el costo de la cadena (no 0) y `LastNetCost` NO se pisa con 0 — **incluido el path `NewCatalogProduct`→reuso (nota 3 round 3)**; caso sin referencia → `CostToConfirm=true` + razón + NINGUNA fila/actualización en RateSupplierSale; referencia más vieja que `StaleCostReferenceDays` → marcado; referencia fresca → sin marca y upsert normal; **update de caller sin permiso con `NetCost=0` → costo persistido intacto, CON FLAG OFF (test de F1b)**; **botón "Confirmar costo"** → marca limpia + upsert diferido con `LastSoldAt` = fecha de la venta; **update normal (guardar) de caller CON permiso sobre servicio marcado → la marca NO se limpia ni se upsertea** (solo el botón confirma); **confirmar costo `0` desde el botón → vale**: marca limpia + upsert con `LastNetCost=0` |
| R12 | **B-R2-2: una edición desde el modal viejo borra el deadline en silencio** (los updates clobberean — verificado §1.2) | `Ignore()` en maps de update + asignación manual gobernada por `DeadlinesSpecified` (§2.2) | **Por Hotel, Paquete y Aéreo**: update SIN el campo deadline (request estilo modal viejo) → el deadline persistido NO cambia; `DeadlinesSpecified=true` + null → borra; + valor → actualiza |

**Tests de regresión obligatorios adicionales (gaps del review)**:
1. **(B1)** Create con `RateId` + precios/supplier/atributos DISTINTOS a los del Rate → con flag ON persiste lo del request; con flag OFF persiste lo del Rate (comportamiento actual). Por cada uno de los 5 tipos.
2. **(B2)** La carrera real de upsert: dos creates concurrentes (tareas paralelas contra Postgres real), no secuenciales — assert de R10.
3. **(B3)** Backfill type-aware: hotel legacy con `ProductName` genérico y nombre real en `HotelName` → encontrable por `catalog-search` con el nombre del hotel tras la migración.
4. **(R4 ampliado)** Con flag OFF, create con `RateId` no pisa nada distinto a hoy y NO escribe `RateSupplierSale` (byte-idéntico verificado contra snapshot del comportamiento actual).
5. **(B-R2-1)** Los tests de R11, por los 5 tipos, con caller sin `cobranzas.see_cost` real (no mock del resolver de permisos en integración).
6. **(B-R2-2)** Los tests de R12 (update sin campo deadline NO borra el deadline persistido), por Hotel/Paquete/Aéreo.
7. **(M-R2-1)** `/alerts` post-F1b: no-admin → buckets financieros vacíos; admin → payload idéntico a snapshot.
8. **(checklist #7)** Drift de `SearchName`: renombrar un hotel desde back-office (`RateService.UpdateAsync`) → `SearchName` recalculado y el producto encontrable por el nombre nuevo en `catalog-search` (y ya no por huella vieja exacta).

Además: la suite existente de creates de bookings (con flag OFF) debe quedar verde sin modificar ningún test existente.

---

## 8. Preguntas abiertas para Gastón

1. ~~Q1 — Dropdown sin permiso de costos~~ **RESUELTA (D1)**: precio de VENTA de la última vez; nunca costo, nunca vacío.
2. ~~Q2 — Quién ve los avisos de deadline~~ **RESUELTA (D2)**: cada vendedor los de sus reservas; admin todos.
3. ~~Q3 — Ciudad obligatoria al crear hotel inline~~ **RESUELTA (D6, 2026-06-05)**: ciudad OBLIGATORIA al crear un hotel desde la venta (400 si falta). F2-Hotel desbloqueada.
4. ~~Q4 — UX de "costo a confirmar" (D7)~~ **RESUELTA (D8, 2026-06-05, ronda 3)**:
   - **(a)** Pill ámbar SOLO para usuarios con `cobranzas.see_cost`; **el vendedor sin permiso que la generó no ve nada**.
   - **(b)** **Sí** hay aviso en la campanita: bucket "Costos a confirmar" para quienes ven costos.
   - **(c)** Confirmación **EXPLÍCITA con botón "Confirmar costo"** (confirma o corrige el número) — NO implícita al guardar.
   - **Corrección v3.1**: la frase de v3 "el backend de D7 NO depende de esta respuesta" era **inexacta para (c)** — la respuesta del dueño cambió el mecanismo backend de confirmación (de implícita-en-el-update a endpoint/acción explícita, §2.8). (a) y (b) sí eran solo-front.

**No quedan preguntas abiertas** (Q1–Q4 cerradas por D1/D2/D6/D8).

---

## 9. Supuestos NO verificados en código (honestidad ante todo)

- ~~`CreatePackageAsync` / `CreateAssistanceAsync`~~ **VERIFICADOS en v2** (BookingService.cs:700-739 y `ApplyAssistanceRateSnapshot` :108-123): simetría confirmada, incluido que Assistance pisa SupplierId.
- ~~`QuoteService.ConvertQuoteToReserva`~~ **VERIFICADO en v2**: el método real es `ConvertToFileAsync` (QuoteService.cs:244); hechos en §1.2 y §2.3.b.7.
- **No verifiqué** los permisos exactos de los endpoints de create de bookings — `catalog-search` debe alinearse a ese mismo gate, no inventar uno.
- **No verifiqué** si `CostMasking` tiene variantes para los 5 tipos (vi `MaskHotelAsync` y `MaskFlightAsync`); si falta alguna, el masking se implementa con el mismo resolver (`_permissionResolver` + `cobranzas.see_cost`).
- ~~`EnableRetryOnFailure`~~ **VERIFICADO en v3 (M-R2-2)**: `Program.cs:161-165` lo configura (`5` intentos, `10s`) → hecho, no precondición. Contracara nueva: `PostgresIntegrationFixture.cs:305/:340` SIN retry → paridad de fixture exigida en §2.3.b.2/R10.
- ~~Autorización de `/alerts`~~ **VERIFICADA en v3 (M-R2-1)**: `AlertsController.cs:7` es `[Authorize]` plano sin chequeo de rol → fuga real, entra a F1b (§2.7).
- ~~Campos de vigencia de `AssistanceBooking`~~ **VERIFICADOS en v3**: `ValidFrom`/`ValidTo` (AssistanceBooking.cs:65-66, date-only como CheckIn/CheckOut) y `Adults`/`Children` (:69-70) existen — la fórmula de unitarización de Asistencia (§2.1) tiene sus insumos.
- **No verifiqué** que `NetCost/SalePrice` de los 5 bookings sean montos TOTALES del servicio — trivial de confirmar al implementar; las fórmulas de §2.1 se expresan sobre totales.
- ~~Si un usuario sin `cobranzas.see_cost` puede hoy invocar los UPDATE de bookings~~ **VERIFICADO en round 3 (v3.1) — el bug es REAL HOY**: `HotelBookingsController.cs:74-77` (PUT solo con `ReservasEdit` + ownership, sin gate de costos), el GET enmascara `NetCost` a `0`, `ServiceFormModal.jsx:2122` puebla el form con ese `0`, el submit manda `netCost=0` y el mapper clobberea. Verificado por el reviewer round 3 y re-verificado por el architect en v3.1 (ambos archivos leídos). El fix de preservación va en **F1b SIN flag** (§2.7 Fuga 3). Pendiente menor del implementador: confirmar que los otros 4 controllers de booking gatean igual que Hotel (esperable por simetría; solo Hotel fue leído línea por línea).
- **No verifiqué** la fórmula canónica exacta de ganancia por tipo (para el recálculo post-cadena D7) — existe precedente verificado en la feature "impuesto incluido" (fix B2, ganancia canónica al guardar); el implementador reusa ESA fórmula, no inventa una.
- **No verifiqué** el contenido exacto de `ServiceFormModal.jsx` (~2500 líneas) — la decisión "no tocar el modal" lo hace de bajo riesgo.
- **No verifiqué** que la extensión `unaccent` NO esté instalada (por eso el diseño no depende de ella).
- **No verifiqué** que `TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires")` resuelva en el contenedor del VPS y en dev Windows (esperable en .NET 6+ con ICU; smoke test en F1).
- **Asumo** que `Rates` tiene volumen chico (single-tenant) — backfill y `DISTINCT ON` triviales a esa escala; si alguna instalación tuviera >100k rates, `EXPLAIN` antes de prender.
- **Asumo** que el objeto anónimo de `GetAlertsAsync` se serializa con claves PascalCase (visto en el front) — el bucket nuevo sigue la convención.

---

## 10. Archivos clave para el implementador

**Backend — modificar**:
- `src/TravelApi.Domain/Entities/Rate.cs` (3 campos nuevos)
- `src/TravelApi.Domain/Entities/HotelBooking.cs`, `PackageBooking.cs`, `FlightSegment.cs` (deadlines) + las 5 entidades de booking (`CostToConfirm`/`CostToConfirmReason`, §2.8)
- `src/TravelApi.Domain/Helpers/TextNormalizer.cs` (**método nuevo `NormalizeForCatalog`** — NO tocar `NormalizeForMatch`)
- `src/TravelApi.Domain/Entities/OperationalFinanceSettings.cs` (+ DTO + service + controller del panel) — 2 flags + 1 setting de ventana
- `src/TravelApi.Infrastructure/Services/RateService.cs` (generalizar fuzzy → catalog-search conservando las dos condiciones trigram; masking F1b en `SearchAsync` y demás lecturas; **hooks de recálculo de `SearchName` en `CreateAsync` Y `UpdateAsync` — checklist round 2 #7: si back-office renombra un hotel (`HotelName`/`ProductName`), `SearchName` se recalcula en la MISMA escritura; sin esto el buscador de venta queda buscando el nombre viejo. Test de drift en §7 punto 8**)
- `src/TravelApi.Application/Interfaces/IRateService.cs` y `src/TravelApi.Application/DTOs/RateDtos.cs` (DTOs de catalog-search con `lastSale`/`rateFallback`)
- `src/TravelApi/Controllers/RatesController.cs` (endpoint `catalog-search`; barrido F1b)
- `src/TravelApi.Infrastructure/Services/BookingService.cs` (transacción flag ON ambos paths + regla request-manda + find-or-create + upsert en los 5 creates; `Currency` + deadlines en requests/updates; **preservación de NetCost/Tax en los 5 updates para callers sin permiso — F1b SIN flag, §2.7 Fuga 3**; **acción "Confirmar costo" como capa fina sobre el update handler — D8c, §2.8**)
- Controllers de bookings (`HotelBookingsController.cs` y simétricos): endpoint del botón "Confirmar costo" (D8c) + confirmar la simetría de gating de los 5 PUT (§9)
- `src/TravelApi.Infrastructure/Services/QuoteService.cs` (`ConvertToFileAsync`: upsert post-éxito best-effort, skip supplier 0, sin Asistencia)
- `src/TravelApi.Infrastructure/Services/AlertService.cs` + `src/TravelApi/Controllers/AlertsController.cs` (F1b: identidad del caller + buckets financieros admin-only server-side; F3: buckets `ServiceDeadlines` y `CostsToConfirm`)
- DTOs/requests de bookings (create + update: `Currency`, deadlines + `DeadlinesSpecified`, exposición de `CostToConfirm`) y `src/TravelApi.Application/Mappings/MappingProfile.cs` (**`Ignore()` para deadlines y costos en maps de UPDATE** — §2.2/§2.3.b.3-bis; los maps NO protegen solos: verificado :64-67/:86-91/:106-109/:125-129)
- `src/TravelApi.Tests/Fixtures/PostgresIntegrationFixture.cs` (:305 y :340 — paridad `EnableRetryOnFailure` con Program.cs, checklist #4)

**Backend — crear**:
- `src/TravelApi.Domain/Entities/RateSupplierSale.cs`
- Helper único de upsert `ON CONFLICT` (Infrastructure) con skip supplier <= 0
- Helper "hoy en Argentina" (corte de deadlines, §2.2)
- 2 migraciones aditivas en `src/TravelApi.Infrastructure/Persistence/Migrations/App/` (**detrás de la cola pendiente del VPS, sin tocarla**)

**Frontend — crear** (árbol nuevo, NO tocar el modal):
- `src/TravelWeb/src/components/serviceInlineCard/ServiceInlineCard.jsx` + `ProductSearchField.jsx` + campos por tipo (5) — siguiendo el mockup al pie de la letra (incluida la regla D1 de precio visible)
- Integración en la página de detalle de reserva (montaje condicional por flag vía `OperationalFlagsContext.jsx`)

**Frontend — modificar**:
- `src/TravelWeb/src/contexts/AlertsContext.jsx` (gating `isAdmin() || enableServiceDeadlineAlerts`) / componente de campanita (render del bucket)
- Fila de servicio en la lista de la reserva (pills de deadline y "creado en venta")
- RateSelector / pantalla de tarifario (F1b: no mostrar "$0" confuso a quien no ve costos)

**Frontend — borrar (recién en F4)**:
- `src/TravelWeb/src/components/ServiceFormModal.jsx`

**Referencia (leer, no tocar)**:
- `src/TravelApi.Infrastructure/Persistence/Migrations/App/20260530120000_AddRateFuzzyMatching.cs` (patrón trigram)
- `src/TravelApi.Infrastructure/Services/ReservaService.cs:1307-1311` (patrón de transacción Serializable)
- `docs/ux/mockups/2026-06-05-agregar-servicio-detalle-C.html` + `docs/ux/guia-ux-gaston.md` (especificación UX)

---

## 11. Resolución del review round 1 (trazabilidad hallazgo → fix)

### Bloqueantes

| Hallazgo | Resolución |
|---|---|
| **B1 — El path con RateId pisa lo que cargó el vendedor** | §1.2 documenta el comportamiento actual verificado (Flight :303-306, Package :714-721, Transfer :909-915, Assistance :108-123 incl. SupplierId; Hotel :126-159 + :495). §2.3.b regla 3: con flag ON el Rate aporta identidad + relleno de huecos y **el request manda** en precios/supplier/moneda/atributos; con flag OFF byte-idéntico (snapshot actual intacto). Test de regresión nuevo en §7 (gap 1): create con RateId + valores distintos → persiste request (ON) / Rate (OFF), por los 5 tipos. |
| **B2 — Concurrencia del upsert sin especificar; la carrera aborta la venta** | §2.1: upsert SQL atómico `INSERT ... ON CONFLICT ("RateId","SupplierId") DO UPDATE ... SalesCount+1` (nunca 23505, nunca read-modify-write); skip si `SupplierId <= 0` (fallback 0 de ConvertToFileAsync). §2.3.b.2: aislamiento DECIDIDO — se hereda Serializable del patrón de la casa (ReservaService.cs:1311) + retry sobre 40001 (precondición: verificar `EnableRetryOnFailure`, si no, retry explícito); la carrera de creación de Rate se resuelve a favor del reuso; regla "un serialization failure nunca aborta la venta". A7 documenta el fallback ReadCommitted. Test R10/gap 2: dos creates CONCURRENTES → 1 fila, SalesCount=2, ninguna venta abortada. |
| **B3 — Identidades en conflicto; fuente de SearchName sin definir** | §2.4 nueva tabla "dos identidades coexisten": huella (duplicado exacto supplier-scoped, back-office, intacta) vs SearchName (agrupador de catálogo supplier-agnóstico). §2.1: fuente de SearchName por tipo — Hotel = `HotelName ?? ProductName`, resto = `ProductName`, con la MISMA regla en backfill SQL (type-aware, COALESCE/NULLIF) y escritura de app (evita que el anti-duplicados nazca roto en hoteles legacy). §2.4: regla de reuso defensivo acotada — igualdad exacta normalizada + misma City para Hotel; **supplier distinto reusa igual** (el producto es supplier-agnóstico; la combinación va a RateSupplierSale); el difuso nunca reusa solo. Test gap 3: backfill type-aware. |

### Mayores

| Hallazgo | Resolución |
|---|---|
| **M1 — No crear `CatalogNameNormalizer`** | Eliminado del diseño y de §10. §2.4: se reusa `TextNormalizer` (Domain/Helpers/TextNormalizer.cs:36) agregando `NormalizeForCatalog` (delega en `NormalizeForMatch` + colapso de puntuación repetida); método nuevo para no alterar la semántica del duplicate-check existente. |
| **M2 — La moneda de la venta no fluye al producto nuevo** | §2.1 "Flujo de moneda": `Currency` se agrega a los 5 create requests; flag ON la hace obligatoria; Rate nuevo nace con `Currency = request.Currency` (el default `"USD"` de Rate.cs:149 no decide nunca — decisión D5); `LastCurrency` = moneda de la venta; interacción con `AddBookingCurrencyTraceability` documentada (ON: booking.Currency del request; OFF: byte-idéntico). |
| **M3 — Fórmulas de unitarización sin definir** | §2.1 tabla por tipo con fórmula, divisor-cero y redondeo único (`AwayFromZero`, 2 decimales): Hotel = total/(noches×habitaciones) según D4 (`noche_habitacion`); Paquete = total/(Adults+Children) con niños como persona entera (definido, con justificación); Asistencia = pax×días; nota sobre `HotelPriceType`/`PriceUnit` legacy en el fallback. |
| **M4 — §2.3.b.5 factualmente incorrecto** | Corregido en §1.2 y §2.3.b.7: método `ConvertToFileAsync` (QuoteService.cs:244); Asistencia NO cubierta (cae a ServicioReserva :408-429); no atómico (SaveChanges :288/:445); regla skip-supplier-0 explícita. |
| **M5 — Asimetría de atomicidad con flag ON** | Decidido y documentado (§2.3.b.2 y .7): en BookingService AMBOS paths (RateId y NewCatalogProduct) van dentro de la transacción con flag ON; en `ConvertToFileAsync` el upsert es post-éxito best-effort con tolerancia a fallo explícita (log + reconciliación R7) — asimetría deliberada, no accidental. |
| **M6 — Fuga `/rates/search` (ahora requisito D3)** | §2.7 + fase F1b propia, sin flag (fix de seguridad, precedente B1.15): masking en `SearchAsync` + barrido completo del `RatesController`; nota explícita de que la ficha nueva NO consume `/rates/search`. |
| **M7 — Path de edición y zona horaria de deadlines** | §2.2: deadlines agregados a los Update requests; corte con "hoy" = fecha local Argentina (helper único, IANA tz), guardado wall-clock según precedente `NormalizeAirportWallClock` (BookingService.cs:274-279); buckets existentes no se tocan. **CORRECCIÓN v3**: la frase de v2 "mapper verificado por el reviewer como no-clobbering" era **falsa** — los updates SÍ clobberean (B-R2-2, §12); la mitigación real está en §2.2. |
| **M8 — Campo real de agrupación aéreo** | Verificado y corregido (§1.2, §2.2): la migración `20260530015242` agregó `ConfirmationNumber` + `PaxCount`; la clave de agrupación es el preexistente `FlightSegment.PNR` (:62). Fallback definido: PNR null/vacío/"TBD" (case-insensitive) → sin agrupar, un aviso por segmento. |

### Menores

| Hallazgo | Resolución |
|---|---|
| **m1 — DISTINCT ON sin City ni ORDER BY correcto** | §2.3.a: SQL explícita — `DISTINCT ON ("SearchName", lower("City"))` para Hotel; el `ORDER BY` interior arranca por las claves del DISTINCT; el orden por score va en la query exterior. Test en R5. |
| **m2 — Flag OFF y campos deadline** | §2.6 explícito: con flag OFF la API **acepta y persiste** los deadlines (y `Currency` opcional) sin UI — precedente Currency traceability. Test en R4. |
| **m3 — GUC pg_trgm.similarity_threshold** | §1.2 y §2.3.a: las DOS condiciones (`%` con GUC 0.3 + `similarity >= 0.4` paramétrico) se conservan al generalizar, con el porqué de cada una. |
| **m4 — Dimensionamiento por fase** | §5 con esfuerzo por fase; F2 marcada como la más grande (XL, árbol nuevo × 5 tipos contra mockup estricto). |

### Testing gaps

Los 4 tests pedidos quedaron en §7 ("Tests de regresión obligatorios adicionales") + R10 nuevo para la carrera concurrente.

### Decisiones del dueño incorporadas

D1→§2.3.a (ex-Q1 cerrada); D2→§2.5 (ex-Q2 cerrada, gating server-side); D3→§2.7/F1b; D4→§2.1 (unitarización hotel por noche por habitación); D5→§2.1 (moneda de la venta, default USD neutralizado). ~~Solo queda abierta Q3~~ Q3 cerrada en v3 por D6 (§12).

---

## 12. Resolución del review round 2 (trazabilidad hallazgo → fix)

El reviewer confirmó en round 2 que B1/B2/B3 y los 8 mayores del round 1 quedaron bien cerrados. Round 2 dejó 2 bloqueantes + 2 mayores + 7 checklist items; el dueño bajó además 2 decisiones nuevas (D6, D7). Todo resuelto así:

### Bloqueantes

| Hallazgo | Resolución |
|---|---|
| **B-R2-1 — "Request manda" destruye el costo cuando vende un usuario sin `cobranzas.see_cost` y envenena `RateSupplierSale`** (front oculta el costo, ServiceFormModal.jsx:1775; `NetCost`/`Tax` decimal NO-nullable en los 5 requests, Requests.cs — ausente indistinguible de 0; con flag ON el 0 se persistiría y el upsert pisaría `LastNetCost` bueno) | Cerrado con la **decisión D7 del dueño** → regla 3-bis en §2.3.b (cadena server-side `RateSupplierSale` del supplier elegido → campos del Rate → 0; marca "costo a confirmar" SOLO dudosos: sin costo conocido o referencia > `StaleCostReferenceDays`, default 60; servicio marcado NO upsertea hasta confirmación; en UPDATE de caller sin permiso se preservan los valores persistidos) + §2.8 (diseño de la marca: campo `CostToConfirm`+`CostToConfirmReason` en las 5 entidades — no estado, para no acoplar al rediseño de Status; confirmación implícita al guardar con permiso; bandeja vía pill + bucket `CostsToConfirm` gateado por permiso — UX PENDIENTE OK DEL DUEÑO, Q4; setting en `OperationalFinanceSettings`). `RateSupplierSale` suma `LastTax` para que la cadena reponga costo+impuesto coherentes. Ganancia recalculada canónica (precedente fix B2 de "impuesto incluido"). **Tests de regresión por tipo con caller sin permiso: R11 + §7 punto 5.** Hallazgo colateral honesto: posible bug preexistente equivalente en el path UPDATE actual (flag-independiente) — a verificar en F1, candidato a F1b (§2.3.b.3-bis, §9). **[Actualización v3.1: este registro es histórico del round 2 — en round 3 la Q4 quedó cerrada por D8 (confirmación EXPLÍCITA por botón, ya no implícita) y el bug colateral quedó CONFIRMADO con evidencia, con su fix movido a F1b sin flag. Ver §13.]** |
| **B-R2-2 — §2.2 se apoyaba en una verificación FALSA: los updates SÍ clobberean** (maps sin `Condition` — MappingProfile.cs:64-67/:86-91/:106-109/:125-129; `_mapper.Map(req, entity)` en BookingService.cs:365/:574/:775/:969/:1191 — re-verificado en v3; cualquier edición desde el modal viejo borraría el deadline en silencio) | §2.2 reescrita: **eliminada la atribución de verificación al reviewer** (era falsa; también corregida la fila M7 de §11) y reemplazada por la mitigación real: `ForMember(..., opt => opt.Ignore())` en los maps de update + asignación manual en el service gobernada por el discriminador `bool DeadlinesSpecified = false` (default no rompe callers posicionales; resuelve además la ambigüedad "no lo mandé" vs "borralo" de un `DateTime?`). La misma mecánica anti-clobber se reusa para la preservación de costos de D7. **Test obligatorio R12 + §7 punto 6**: update sin el campo NO borra el deadline persistido, por Hotel/Paquete/Aéreo. |

### Mayores

| Hallazgo | Resolución |
|---|---|
| **M-R2-1 — Fuga de `/alerts` preexistente y explotable hoy** (AlertsController.cs:7 `[Authorize]` plano; gating admin solo client-side, AlertsContext.jsx) | Verificado en v3 (controller completo leído). Incorporado a **F1b como fix de seguridad SIN flag** (precedente B1.15): §2.7 "Fuga 2" — firma de `GetAlertsAsync` con identidad del caller (cambio que §2.5 iba a necesitar igual, se hace una vez), no-admin → buckets financieros vacíos con `TotalCount=0` (misma forma de payload, contrato listo para F3), admin → byte-idéntico. §1.2 actualizada; test en §7 punto 7. |
| **M-R2-2 — `EnableRetryOnFailure` ya está configurado; resolver pendientes de §9** | §1.2/§2.3.b.2/§9/R10 actualizados: `EnableRetryOnFailure(5, 10s, null)` es **hecho verificado** (Program.cs:161-165), ya no precondición. Contracara incorporada: `PostgresIntegrationFixture.cs:305/:340` SIN retry → **paridad de fixture** elegida (alternativa documentada: retry explícito acotado en código) para que R10 pruebe la regla real. `AssistanceBooking.ValidFrom/ValidTo` (:65-66) y `Adults/Children` (:69-70) verificados → pendiente de §9 resuelto a favor de la fórmula de Asistencia. |

### Checklist round 2 (notas §7/§10, sin rediseño)

| # | Ítem | Dónde quedó |
|---|---|---|
| 1 | Bajo Serializable el `ON CONFLICT` puede tirar 40001 en vez del DO UPDATE (lo cubre el retry; no es anomalía) | Nota en §2.1 (tras el SQL del upsert) + criterio de aceptación de R10 |
| 2 | "NUNCA aborta la venta" → "nunca sin reintentar; agotados N intentos, error claro re-presentable" | §2.3.b.2 (regla reescrita; rollback total garantiza re-presentable) + R10 |
| 3 | Delegate del `ExecutionStrategy` re-ejecutable: limpiar/reconstruir ChangeTracker en retry (lección commit `723a905`) | §2.3.b.2 (bullet "Delegate re-ejecutable") |
| 4 | Paridad retry en fixture de integración | §2.3.b.2 + R10 + §10 (PostgresIntegrationFixture.cs:305/:340) |
| 5 | Unificar criterio City (app sin tildes vs `lower("City")` con tildes → "Córdoba"/"Cordoba" inconsistentes) | §2.3.a: `NormalizeForCatalog` autoritativa; reuso defensivo compara City **en app** sobre candidatos por SearchName; `DISTINCT ON` usa `norm_city` con `translate()` (mismo best-effort del backfill; solo agrupación visual, nunca decide reuso) |
| 6 | En `NewCatalogProduct`→reuso NO hay relleno de huecos desde el Rate reusado (la ficha manda todo) | §2.3.b regla 5, bullet nuevo |
| 7 | Hooks de recálculo de `SearchName` en `RateService.CreateAsync`/`UpdateAsync` + test de drift | §10 (línea de RateService) + §7 punto 8 |

### Decisiones nuevas del dueño

| Decisión | Dónde aterriza |
|---|---|
| **D6 — Ciudad obligatoria al crear hotel desde la venta** | §1.4, §2.3.b (`NewCatalogProductRequest`: 400 si falta), §8 (Q3 cerrada), §5 (F2-Hotel desbloqueada) |
| **D7 — Costo server-side para callers sin `cobranzas.see_cost` + marca "costo a confirmar"** | §1.4, §2.3.b regla 3-bis, §2.8, §2.6 (gating por flag + setting `StaleCostReferenceDays`), R11, Q4 (agregados de UX pendientes de OK) |

### Decisiones de diseño propias de v3 (no vienen del dueño ni del reviewer — el re-reviewer debe desafiarlas)

1. **`LastTax` en `RateSupplierSale`**: sin ella, la cadena D7 repondría costo sin impuesto y la ganancia canónica quedaría inflada para productos con impuesto incluido. Columna aditiva diseñada ANTES de que exista la migración (costo cero de agregarla ahora).
2. **Moneda en la cadena D7**: una referencia con `LastCurrency` distinta de la moneda de la venta NO se usa (se sigue al fallback / 0 + marca). Regla conservadora de diseño — D7 no la menciona; preferí marcar de más antes que mezclar monedas en un costo invisible para quien vende.
3. **Confirmación implícita al guardar con permiso** (sin botón dedicado): quien guarda viendo el costo lo valida. Alternativa explícita ofrecida al dueño en Q4(c). **SUPERSEDED en v3.1: el dueño eligió la alternativa explícita (D8c — botón "Confirmar costo", §2.8/§13); el guardar normal ya NO confirma.**
4. **`DeadlinesSpecified` como discriminador** en updates (vs sentinel o endpoint de borrado): el único mecanismo que distingue "no lo mandé" de "borralo" sin romper callers posicionales mientras el modal viejo convive hasta F4.

---

## 13. Resolución del review round 3 (v3 → v3.1) — veredicto READY, APROBADO PARA CONSTRUIR

El round 3 dio **READY (Approved with comments)** con 2 condiciones obligatorias + 5 notas; el dueño además cerró Q4 con la decisión **D8**. Todo incorporado en esta v3.1 — cambios acotados, sin rediseño.

### Condiciones obligatorias

| # | Condición | Resolución en v3.1 |
|---|---|---|
| 1 | **La preservación de costo en UPDATE sale en F1b SIN flag.** El reviewer VERIFICÓ que el bug es real HOY: `HotelBookingsController.cs:74-77` (PUT sin gate de `cobranzas.see_cost`), el GET enmascara `NetCost` a `0`, `ServiceFormModal.jsx:2122` puebla el form con ese `0` y el submit manda `netCost=0`; el mapper clobberea. v3 lo tenía solo bajo `EnableCatalogFindOrCreate` (§2.3.b.3-bis) y como "posible" bug. | §2.3.b.3-bis corregida (preservación → F1b, precedente B1.15); §2.7 "Fuga 3" nueva; §5 (F1b ampliada, F1 descargada); §9 (de "no verifiqué" a VERIFICADO); R11 actualizado. Test: update de caller sin permiso con `NetCost=0` → costo persistido intacto, **CON FLAG OFF**. Evidencia re-verificada por el architect en v3.1 (controller y form leídos). |
| 2 | Guard anti-confirmación-accidental para la confirmación implícita. | **Reemplazada por construcción por D8c**: el dueño eligió botón explícito "Confirmar costo" → ya no existe confirmación implícita que proteger (guardar normal NO confirma). Constancia: el guard pierde urgencia porque el botón elimina el riesgo por diseño; confirmar costo `0` desde el botón SÍ vale (aserción humana deliberada, §2.8). |

### Notas incorporadas

| # | Nota | Dónde quedó |
|---|---|---|
| 3 | La regla 3-bis aplica en TODOS los paths de create, incluido `NewCatalogProduct`→reuso: para `NetCost`/`Tax` de caller sin permiso, la cadena le gana a "la ficha manda todo" | §2.3.b.5 |
| 4 | Producto creado inline por caller sin permiso → el Rate nace con `NetCost=0` y QUEDA así (regla deliberada de no pisar el Rate); `rateFallback` mostrará `0` hasta la primera `RateSupplierSale` confirmada — el implementador NO debe "arreglarlo" pisando el Rate | §2.3.b.5 |
| 5 | El trigger de confirmación es una capa fina sobre el update handler (botón explícito D8c) | §2.8 |
| 6 | `LastCurrency` puede ser `null` por el path best-effort de `ConvertToFileAsync`; la cadena D7 ya lo trata (`null ≠` moneda de la venta → fallback) | §2.1 (nota del helper de upsert) |
| 7 | Consecuencia de la regla de moneda conservadora: tarifario USD + venta ARS por vendedor sin permiso = más marcas "a confirmar"; revisitar tras ADR-011 | §4 |

### Decisión nueva del dueño

| Decisión | Dónde aterriza |
|---|---|
| **D8 — UX de "costo a confirmar" (cierra Q4)**: (a) el vendedor sin permiso que generó la marca **no ve nada** — pill ámbar solo con `cobranzas.see_cost`; (b) **sí** hay bucket "Costos a confirmar" en la campanita para quienes ven costos; (c) confirmación **EXPLÍCITA con botón "Confirmar costo"** (confirma o corrige) — reemplaza la confirmación implícita de v3 (decisión propia 3 de §12, superseded). Asentada en `docs/ux/guia-ux-gaston.md`, sección "UI del costo a confirmar". | §1.4 (tabla), §2.8 (mecanismo reescrito + endpoint), §2.6 (flag), §8 (Q4 cerrada + corrección de la frase "el backend no depende de esta respuesta" — era inexacta para (c)), §5 (F2/F3) |

### Estado final

**APROBADO PARA CONSTRUIR.** Sin preguntas abiertas (Q1–Q4 cerradas por D1/D2/D6/D8). Orden recomendado: **F1b primero** (3 fixes de seguridad sin flag: masking tarifario, gating `/alerts`, preservación de costo en update) → F1 (flag OFF) → F2 → F3 → F4. Siguen vigentes las restricciones operativas preexistentes: migraciones solo aditivas encoladas DETRÁS de la cola pendiente del VPS (R8) y convención OFF = byte-idéntico.
