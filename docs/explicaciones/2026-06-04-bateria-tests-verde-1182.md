# 2026-06-04 — Batería completa de tests en verde (1182/1182) + causa raíz del flaky

## TL;DR
Primera vez que la batería **completa** (`run-tests-all.sh`, unit + integración con
Testcontainers) corre entera en el VPS. Arrancó en **1110/1182** (72 rojos) y terminó en
**1182/1182**. Ningún rojo era código de producción: todos eran huecos de infraestructura
de tests o tests obsoletos que nadie había corrido porque antes solo se usaban scripts
filtrados (`run-tests-adr013.sh`, `run-tests-fc13.sh`).

HEAD final: `2f79fc8`.

## Qué se arregló, en orden (cada uno destapaba al siguiente)

1. **`IFiscalLiquidationCalculator` no registrado en `PostgresIntegrationFixture`**
   (~60 rojos). El ctor de `BookingCancellationService` lo exige desde FC1.3; los tests
   que lo resuelven por DI explotaban. Commit `0ae8d3f`.

2. **`IAdminUserCountService` no registrado** (~55 rojos). Estaba *tapado* por el #1:
   recién al registrar el calculator apareció el siguiente dependency faltante. La impl
   real necesita `UserManager`/Identity que el fixture mínimo no arma → se registró un
   stub `Mock`. Commit `9901781`.

3. **Seeds de mis tests B1** (`BookingCancellationDraftRetryPolicyTests`): snapshot fiscal
   en `Unset` chocaba el CHECK `chk_..._fiscalsnapshot_consistent`; y un test daba INV-100
   en vez de INV-081 porque sembraba factura original + NC viva ambas activas (el guard
   multi-factura disparaba antes del blindaje). Se anuló la original en el seed (una NC
   total viva anula la factura por definición). Commits `9901781`.

4. **`ManualCashMovementBuilderTests`** (2 rojos unit): asserts del 2026-05-17 vs fix del
   builder del 2026-05-18 (navigation property vs FK escalar). Test obsoleto ~2 semanas.
   Commit `0ae8d3f`.

5. **`DraftAsync_..._INV081`**: reescrito (con B1 un 2do draft puro REUSA, no rechaza).
   Commit `0ae8d3f`.

## El flaky de los 100s — la trampa que costó 2 intentos

Quedaban 4 tests HTTP que cortaban **exacto a los 100s** (`HttpClient.Timeout`).

**Intento fallido (`28c8fc8`):** agregar `xunit.runner.json` con `maxParallelThreads=4`.
NO funcionó. El log seguía mostrando ~30 Postgres creados en el mismo segundo.

**Por qué no funcionó (aprendizaje clave):** `maxParallelThreads` limita **hilos de CPU**,
no operaciones async. Los contenedores se levantan en `InitializeAsync` (async) del fixture.
Cada `await _container.StartAsync()` **libera el hilo**, y xunit aprovecha para arrancar la
init de otra collection → las ~30 inits async corren igual a la vez. **El tope de hilos no
gatea I/O async.**

**Causa raíz real:** los tests HTTP que fallaban usan **InMemory** (`CustomWebApplicationFactory`),
NO crean contenedor propio. Se morían porque corrían **en paralelo** con los tests de
`PostgresIntegrationFixture` que SÍ levantan ~30 Postgres reales y **saturaban el VPS**
(7.76 GB: CPU/IO/Docker) → las requests InMemory se colgaban >100s.

**Fix real (`2f79fc8`):**
- **`SemaphoreSlim` estático (máx 4)** alrededor de `_container.StartAsync()` en
  `PostgresIntegrationFixture`. Solo gatea el arranque (pull/boot/healthcheck); se libera
  apenas el contenedor está listo → la **ejecución** sigue 100% paralela. Mata el storm
  en la fuente.
- **Red de seguridad:** `ConfigureClient` en `CustomWebApplicationFactory` sube
  `HttpClient.Timeout` a 5 min.

## Lección para próximas veces
- Testcontainers + xunit: para evitar el storm de arranque hay que **throttlear con
  semáforo en el fixture**, NO con `maxParallelThreads` (no aplica a init async).
- Tests InMemory y tests con contenedor real comparten el host: la contención de unos
  mata a los otros aunque no estén relacionados.
- Antes de afirmar "es flaky por timeout", leer el fixture: el que falla puede no ser el
  que crea el contenedor.

## Estado / próximo
- 1182/1182 en VPS. Nada de esto tocó código de producción.
- Pendiente real (lo que Gastón quería desde el inicio): **terminar la reserva**. Según
  `docs/producto/inventario-reserva-2026-06-03.md`, los próximos pedacitos son
  **Vouchers (editar interno/externo + simplificar)** y **Servicios (simplificar el form
  de 2141 líneas)**.
