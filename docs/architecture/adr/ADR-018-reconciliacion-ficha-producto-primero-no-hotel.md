# ADR-018 — Reconciliación de la ficha "producto-primero" (F2) con el esquema estructurado de los bookings no-Hotel

**Estado:** APROBADO PARA CONSTRUIR (software-architect-reviewer: Changes Required → correcciones incorporadas abajo; la migración DROP NOT NULL fue aprobada como segura/metadata-only).
**Extiende:** ADR-017 (mismo flag `EnableCatalogFindOrCreate`). Forward-only.
**Fecha:** 2026-06-06.

## Problema

La ficha inline F2 identifica cada servicio no-Hotel con UN SOLO campo de búsqueda (decisión del dueño, guía UX ronda 3): Aéreo = "Ruta/aerolínea", Traslado = "Trayecto", Paquete = "Nombre del paquete", Asistencia = "Plan". Pero las entidades de booking (previas al catálogo) exigen campos estructurados NOT NULL: FlightSegment `Origin`(varchar3)/`Destination`(varchar3)/`AirlineCode`/`FlightNumber`/`CabinClass`; TransferBooking `PickupLocation`/`DropoffLocation`/`VehicleType`; PackageBooking `PackageName`/`Destination`/`EndDate`. AssistanceBooking `PlanType` ya es nullable. Resultado actual: crear vuelo/traslado/paquete desde la ficha → HTTP 500 (NOT NULL violation); la fila no muestra el nombre del producto.

## Decisión del dueño (firme)

El servicio se identifica por el TEXTO del buscador (un campo). El vendedor NO carga origen/destino/aerolínea/nº de vuelo por separado. El dato fino opcional va en "Más detalles". La fila muestra ese texto como identidad.

## Decisiones de diseño

### 1. Identidad visible = snapshot en el booking (no resolver desde el Rate)
- **Paquete:** reusar `PackageName` (ya existe, varchar200). Sigue NOT NULL (la ficha siempre lo llena).
- **Asistencia:** reusar `PlanType` (ya nullable). Sin cambio de esquema.
- **Aéreo y Traslado:** agregar columna nueva `ProductName` (nullable, varchar200). No hay columna natural ancha (varchar3 no aloja "AEP–IGR LATAM").
- **Por qué snapshot y NO resolver del Rate:** preserva el principio de snapshot de ADR-017 §6 (merge/rename del producto no cambia reservas históricas), consistente con `HotelBooking.HotelName`.
- **Front:** `normalizeReservaServices` ya prefiere `rawService.name`; se expone `productName` en los DTOs y se antepone en los fallbacks. Con flag OFF, `ProductName` null → derivación actual → display idéntico.

### 2. Relajar NOT NULL (solo donde no hay default de negocio)
- **Hacer nullable (DROP NOT NULL):** Flight `Origin/Destination/AirlineCode/FlightNumber`; Transfer `PickupLocation/DropoffLocation`; Package `Destination/EndDate`. Cambio en entidad (`string?`, sin `[Required]`), fluent solo Flight (`AppDbContext.cs:570-573` `.IsRequired(false)`), y migración `ALTER COLUMN DROP NOT NULL`.
- **NO relajar — coalescer al default en el path catálogo:** Flight `CabinClass`→`"Economy"`, Transfer `VehicleType`→`"Sedan"` (ya tienen default de negocio).
- **varchar(3) NO se ensancha** (la identidad va a `ProductName(200)`; en el path catálogo Origin/Destination quedan null).
- **`ALTER COLUMN DROP NOT NULL` es metadata-only** en Postgres (no reescribe la tabla; ACCESS EXCLUSIVE lock de milisegundos; no afecta filas existentes; no hay CHECK sobre estas columnas). No afecta al modal viejo (siempre manda valor).

### 3. EndDate del paquete
`EndDate` → `DateTime?`. Cuando falta, NO se inventa: se coalesce a `StartDate` en los cálculos. `MappingProfile.cs:165/177` `Nights = ((EndDate ?? StartDate) - StartDate).Days` (=0 cuando falta). `ReservaScheduleCalculator.cs:66-69` `.Select(p => p.EndDate ?? p.StartDate)` (mismo patrón que el transfer existente). `PackageBookingDto.EndDate` → nullable (evita 0001-01-01 en el front).

### 4. Impacto downstream (mitigación OBLIGATORIA misma fase)
- **R-D1 (alto) — vouchers**: `VoucherService.cs` debe usar fallbacks. Flight (:1216-1217/:1331): título `firstNonBlank(ProductName, "{AirlineCode} {FlightNumber}")`, ruta `firstNonBlank(ruta, ProductName)`. Transfer (:1227/:1343): `firstNonBlank("{Pickup} -> {Dropoff}" siHayAmbos, ProductName)`. Package (:1237-1238/:1356): `Destination ?? ""`, rango `StartDate..(EndDate ?? StartDate)`. El `firstNonBlank` es inocuo para datos viejos (toman siempre el primero no-vacío).
- **R-D2 (medio) — listados**: cubierto por `productName` en el front.
- **R-D3 (barrido COMPLETADO por el reviewer — sin NullRef; 2 huecos a cubrir en la MISMA fase)**:
  - **`AlertService.cs:254/:262/:313`** (labels de la campanita): hoy emite `"Aereo -"` / `"Aereo  ()"` para servicios de catálogo (Origin/Destination/AirlineCode/FlightNumber null). FIX: usar fallback a `ProductName` (mismo helper displayName, ver M2).
  - **`ReportService.cs:515-529`** (ranking de destinos top-15): paquetes/vuelos de catálogo con `Destination` null quedan EXCLUIDOS silenciosamente del ranking (su revenue desaparece del reporte). FIX: coalescer a `ProductName`/`PackageName` para que aparezcan (no se pierde revenue; el label puede ser nombre de producto). Decisión de negocio: se prefiere NO perder revenue del reporte.
  - **Verificados SEGUROS (no tocar):** VoucherService HTML rows (EscapeHtml null-safe :1398), SupplierService:728/764 (`?? ""`), ReservaCapacityRules:329/336 (`?? "sin nombre"`), TimelineService:216/241 (labels, no leen valor), ReservaService:1119 (es Reserva.EndDate, ya nullable). Igual aplicar fallback en los TÍTULOS del voucher (Flight :1216-1217/:1331, Transfer :1227/:1343, Package :1237-1238/:1356) por calidad de display.

### 4-bis. Fuente única de ProductName (M1 — REGLA, no supuesto)
`ProductName` = **el texto que el vendedor vio/tipeó**, enviado EXPLÍCITO en el request (`req.ProductName`). Path `NewCatalogProduct` → `NewCatalogProduct.Name`; path `RateId` → el nombre del producto elegido, copiado al booking AL ESCRIBIR. **NUNCA re-derivar del Rate después** (rompería el snapshot de ADR-017 §6).

### 4-ter. Contrato único "displayName por tipo" (M2 — evita reabrir R-D3)
Para que ningún consumidor futuro reintroduzca un hueco, definir UN helper server-side `ResolveServiceDisplayName(tipo, booking)` y su espejo en front (`normalizeReservaServices`), con la regla por tipo: Hotel→HotelName; Aéreo/Traslado→`ProductName ?? derivación estructurada`; Paquete→`PackageName`; Asistencia→`PlanType`. Vouchers, alertas, reportes y listados usan ESE helper, no cada uno su propia derivación.

### 5. Flag y rollback (CORREGIDO por el reviewer — B1)
- El cambio de esquema NO es gateable por flag (estructural, forward-only).
- **NO es "byte-idéntico OFF" — es forward-only.** Verificado en código (`ServiceFormModal.jsx:2290-2291/:2363`): el modal viejo YA manda `airlineCode`/`flightNumber`/`Destination`(paquete) como `null` cuando están vacíos. Hoy eso da **500 (NOT NULL)**; tras la migración **persiste null**. O sea: la única divergencia observable en OFF es que esos 3 casos (que hoy fallan con 500) pasan a guardar — **es el fix buscado, no una regresión**. Los demás (`Origin/Destination/Pickup/Dropoff/EndDate`) salen como `""` o son exigidos por el modal → idénticos. (Verificar init de `form.origin/destination`: si arrancan `undefined` en vez de `""`, la divergencia se extiende a esos 2 — chequear al implementar.)
- Aceptar identificadores de vuelo vacíos NO tiene impacto de integridad/seguridad (facturación manual, cancelación sobre montos, vouchers con fallback). Verificado.
- **Down-migration: VACÍA con comentario "forward-only" (NO re-imponer NOT NULL)** — re-imponerlo fallaría si ya hay filas de catálogo con null.

## Migración (aditiva, encolada detrás de M4 `20260606054040`)
`Adr017_M5_RelaxNonHotelStructuredFieldsAndAddProductName`: (1) `ADD COLUMN ProductName varchar(200) NULL` en FlightSegments y TransferBookings; (2) `DROP NOT NULL` en los campos de Clase A (§2); (3) sin backfill. Todo metadata-only. No aplicar al VPS; encolar detrás de la cola pendiente.

## Fases de implementación
**Backend:** migración M5 → entidades (add ProductName, relajar, EndDate nullable) → AppDbContext fluent Flight → Requests (ProductName opcional Flight/Transfer, EndDate? Package) → MappingProfile (Nights guard, mapear ProductName) → CatalogCreates (setear ProductName desde el request; coalescer CabinClass/VehicleType; manejar EndDate null) → DTOs → VoucherService fallbacks → ReservaScheduleCalculator coalesce → grep R-D3.
**Frontend:** mandar el texto del buscador al campo correcto (Aéreo/Traslado→productName; Paquete→packageName; Asistencia→planType); permitir omitir endDate en Paquete; añadir productName a normalizeReservaServices.
**Tests:** crear los 3 tipos por catálogo sin estructurados → persiste, no 500, identidad poblada; Nights=0/schedule OK con EndDate null; voucher con estructurados null → muestra ProductName, no " -> "; flag OFF byte-idéntico (snapshot DTO+voucher); migración metadata-only e idempotente.

## Supuestos NO verificados (cerrar antes de construir)
- Verificar contra el mockup qué fechas captura Paquete (¿StartDate sí?).
- Barrido R-D3 completo (no solo Voucher/Schedule/Mapping).
- Confirmar que el modal viejo manda siempre los estructurados no-null (condición del byte-idéntico OFF).
- Fuente única de `ProductName` (recomendado: `req.ProductName` = lo que el vendedor vio).
