# ADR-048 T5 — Review de seguridad y riesgo de datos (migración sobre tabla productiva)

**Revisor:** security-data-risk-reviewer
**Fecha:** 2026-07-17
**Alcance revisado:** diff sin commitear (git status/diff) de la Tanda 5 — migración
`20260718012634_Adr048_M2_AddDerivedStatusColumnsToReserva` (2 columnas derivadas + 4 UPDATEs de
backfill sobre `TravelFiles`), escritor único en `ReservaMoneyPersister` + `ReservaDerivedAxesProjector`,
y wiring de lectura en `ReservaService`.
**Mi terreno (foco pedido):** la migración sobre datos productivos.

---

## VEREDICTO: APROBADO CON COMENTARIOS (0 bloqueantes)

La migración es **aditiva pura**: dos columnas `nullable` sin default (cambio metadata-only en Postgres,
sin reescritura de tabla), el backfill **solo RELLENA las dos columnas nuevas** y nunca toca ningún otro
dato, es **idempotente**, corre en **una sola transacción EF** (atómica ante corte del migrador), y el
`Down` dropea información **derivada y reconstruible** (no hay pérdida irreversible). El criterio SQL del
backfill **replica fielmente** —verificado línea por línea contra el C#— la lógica ya validada del listado
en vivo, para ambos ejes y para los dos fallbacks (con/sin filas hijas). No abre ninguna superficie de
exposición nueva de dato sensible.

No es bloqueante, pero hay riesgos y verificaciones abajo que **deben** hacerse antes de considerar el
deploy "verificado de verdad" (no corrí la migración, ni CI, ni la app).

---

## HECHOS VERIFICADOS (file:line)

### La migración no pisa datos existentes
- `20260718012634_...cs:53-65` — `AddColumn` de las dos columnas con `nullable: true`, **sin default**.
  Nacen `null`; en PG 11+ es cambio de catálogo (sin table rewrite).
- Los 4 `migrationBuilder.Sql` (`:82-100`, `:114-125`, `:133-151`, `:163-170`) hacen **solo**
  `SET "DerivedCollectionStatus"/"DerivedInvoicingStatus"`. Ningún UPDATE toca `Status`, plata, ni otra
  columna. (Contraste explícito con M1 que sí toca `Status`; ver `:43-46`.)

### Idempotencia y atomicidad ante corte del migrador
- Los 4 UPDATEs son **deterministas y sobreescriben** (computan desde tablas fuente, no acumulan): correr
  dos veces da el mismo resultado. EF además la corre exactamente una vez (`__EFMigrationsHistory`).
- La migración **no** contiene `SuppressTransaction`/`CREATE INDEX CONCURRENTLY` (grep: 0). Todas las
  operaciones (AddColumn, CreateIndex no-concurrente, Sql) son transaccionales en Postgres → el `Up()`
  entero es **una transacción**. Si el migrador se mata a la mitad (stop-then-start), la tx aborta, no se
  escribe la fila de historia, y el próximo arranque re-corre limpio. **No queda estado a medias.**
- OQ-3 del diseño (`docs/.../DISENO-implementacion.md:649-650`): "stop-then-start con migrador dedicado
  antes de la app; sin rolling → sin concurrencia en la reparación". Confirma que **ninguna app escribe
  `TravelFiles` durante el backfill** → los locks del full-table UPDATE + build de índices no compiten con
  nadie.

### El backfill NO clasifica mal (equivalencia SQL ↔ C#, verificada línea por línea)

**Eje de COBRO, con filas hijas** (`:82-100`) vs `ReservaCollectionStatus.Derive(lines)`
(`ReservaCollectionStatus.cs:104-130`) y el proyector (`ReservaDerivedAxesProjector.cs:42-50`):
- `any_debt = bool_or(Balance > 0.005)` = `Balance > Epsilon` → `ConDeuda` (gana). Igual.
- `any_credit = bool_or(Balance < -0.005)` = `Balance < -Epsilon` → `SaldoAFavor`. Igual.
- `any_activity = bool_or(ConfirmedSale > 0 OR TotalPaid > 0)` = `HasCharges||HasPayments`
  (`hasCharges: ConfirmedSale>0`, `hasPayments: TotalPaid>0`). Igual → `Saldado` / `SinMovimientos`.
- Umbral `0.005` = `Epsilon` (`ReservaCollectionStatus.cs:65`). Igual.

**Eje de COBRO, SIN filas hijas** (`:114-125`) vs fallback en vivo `FillPorMonedaForListAsync`
(`ReservaService.cs:2359-2362`):
- Live: `hasCharges = Balance != 0`, `hasPayments = TotalPaid > 0`. Tercera rama (|Balance|≤0.005):
  `anyActivity = (Balance != 0) || (TotalPaid > 0)`.
- SQL: `Balance <> 0 OR TotalPaid > 0 → 'Saldado'`, si no `'SinMovimientos'`. **Idéntico.**
- Las ramas `>0.005`/`<-0.005` también idénticas.

**Disyunción correcta:** backfill 1 aplica a reservas con fila en `ReservaMoneyByCurrency`; 1b usa
`NOT EXISTS` (sin filas). Conjuntos **disjuntos** → sin doble-escritura; **cobertura total** → ninguna
reserva queda `null` en el eje de cobro.

**Eje de FACTURACIÓN, con comprobantes 'A'** (`:133-151`) vs `ReservaInvoicingStatus.Derive`
(`ReservaInvoicingStatus.cs:71-90`) + `ReservaInvoicingCuadreCalculator` (`:65-93`):
- `facturado_neto`: NC (3/8/13/53) restan, resto suma. `bruto_emitido`: NC=0, resto suma. Mismo orden de
  chequeos: `neto<=0.005` → (`bruto>0.005`?`FullyReturned`:`NotInvoiced`); `neto>=TotalSale-0.005` →
  `FullyInvoiced`; si no `PartiallyInvoiced`. **Idéntico** al C#.
- `Resultado='A'` = `CountsInNetBilled` (`ReservaInvoicingCuadreCalculator.cs:156-157`). Igual.
- Tipos NC `3/8/13/53` = `InvoiceComprobanteHelpers.IsCreditNote` (`:57-58`). Igual.

**Eje de FACTURACIÓN, sin comprobantes 'A'** (`:163-170`): `NotInvoiced` directo. Coincide con el default
del cálculo (neto 0, bruto 0). Disjunto de backfill 2 (uno usa el join a `invoice_agg`, el otro
`NOT EXISTS`). Cobertura total.

### Bordes pedidos — todos cubiertos correctamente
- **Facturas huérfanas (`TravelFileId` null):** en backfill 2 el grupo con `reserva_id=NULL` nunca matchea
  `tf."Id" = ia.reserva_id` (NULL no iguala nada) → inocuo. En 2b `NOT EXISTS (inv."TravelFileId" = tf."Id")`
  tampoco matchea con NULL → correctamente excluida. Uso de **`NOT EXISTS` en vez de `NOT IN`** (`:107-112`,
  `:157-161`) evita la trampa del NULL que rompería el UPDATE en silencio. Verificado que `Invoices` mapea
  la FK como columna `"TravelFileId"` (no `"ReservaId"`) — `ReservaService.cs:2462` confirma la propiedad C#
  `ReservaId` → columna `TravelFileId`; el SQL usa el nombre de columna correcto.
- **Umbral 0.005:** usado en las 4 ramas, igual al `Epsilon`/`epsilon` del dominio.
- **Reservas del par terminal `{Cancelled, PendingOperatorRefund}`:** el backfill computa **solo** desde
  saldo/comprobantes, **nunca** lee `Status` — ambos miembros del par pasan por la misma cuenta. No se
  materializa el eje "anulada"/operativo (ese sigue siendo `Status`), así que el riesgo B3 (materializar
  la anulada dejando el par miembro con valor viejo) **no aplica por construcción**
  (`ReservaDerivedAxesProjector.cs:28-33`).
- **Reservas sin filas por moneda:** backfill 1b, equivalente al fallback en vivo (arriba).

### Exposición de las columnas nuevas
- Grep `DerivedCollectionStatus|DerivedInvoicingStatus`: los únicos lectores son `ReservaService`
  (listado/detalle) que las mapea a los campos DTO **ya existentes** `CollectionStatus`/`InvoicingStatus`.
  No hay controller/DTO que serialice la entidad `Reserva` cruda con estas columnas. Los valores son
  códigos internos tipo enum ("ConDeuda", "FullyReturned"…) que el front **ya recibe hoy** en esos mismos
  campos DTO → **no hay superficie de exposición nueva** de PII/plata/fiscal. (La pátina técnica de esos
  strings es materia del `data-exposure-reviewer`, no de este eje.)

### Down / rollback
- `Down()` (`:174-191`) dropea los 2 índices + 2 columnas. Lo que se pierde es **derivado y
  reconstruible** (re-derivar desde saldo/comprobantes). No toca plata ni fiscal → rollback seguro. Un
  `Down`+`Up` recomputa desde las fuentes.

### Money math intacta
- `ReservaMoneyPersister.cs` agrega `Include(f => f.Invoices)` (`:78` en el diff) **solo** para alimentar
  al proyector; no cambia `SyncMoneyByCurrencyRowsAsync` ni el `summary`. Los `Invoices` se leen, no se
  mutan. La materialización de los dos ejes ocurre en la **misma `SaveChangesAsync`** que la plata
  (atómico). Comportamiento de plata byte-idéntico.

---

## BLOQUEANTES
Ninguno.

---

## RIESGOS (no bloqueantes)

### R1 — Divergencia por tipo de comprobante DESCONOCIDO (backfill/listado suma; proyector trata como 0)
El backfill (`:136-137`) y la query del listado en vivo (`ReservaService.cs:2477-2491`) suman **todo tipo
no-NC** como factura/ND. En cambio el **proyector go-forward** (`ReservaDerivedAxesProjector.cs:60-67` →
`ReservaInvoicingCuadreCalculator.SignedNetAmount`, `:164-185`) trata un tipo fuera de
`{1,2,3,6,7,8,11,12,13,51,52,53}` como `Unknown = 0`.
- **Consecuencia:** una reserva legacy con un comprobante de **CAE aprobado y tipo desconocido** quedaría
  backfilleada con un valor que **cambiaría (flip)** la próxima vez que pase por el persister. Además el
  DETALLE (cálculo de dominio, unknown=0) ya diverge hoy del LISTADO para ese caso — **pre-existente**,
  no lo introduce T5.
- **Probabilidad:** muy baja (exige un CAE aprobado de tipo fuera del set ARCA conocido — no debería
  existir en dato real). Ya documentado como "M2 del review backend" en `ReservaService.cs:2472-2476`.
- **Recomendación:** dejar registrado; opcionalmente un SELECT de diagnóstico post-deploy que cuente
  `Invoices` con `Resultado='A'` y `TipoComprobante NOT IN (1,2,3,6,7,8,11,12,13,51,52,53)`. Si es 0
  (esperado), el riesgo es teórico.

### R2 — El backfill SQL NO tiene test
El test de integración (`Adr048T5DerivedAxesIntegrationTests.cs`) ejercita el camino **go-forward**
(`ReservaMoneyPersister.PersistAsync` → proyector → listado → detalle coinciden en `FullyReturned`), **no
los 4 UPDATEs de la migración**. Lo que corre contra los datos productivos son los UPDATEs, y su
correctitud descansa en la equivalencia manual SQL↔C# (que verifiqué que se sostiene), pero **sin guarda
automática**. Los unit tests (`Adr048T5DerivedAxesPersisterTests`, `ReservaDerivedAxesProjectorTests`)
también prueban el proyector, no el SQL.
- **Recomendación:** verificación post-deploy (ver "Needs human confirmation"): comparar, para una muestra
  (y especialmente anuladas / multimoneda / saldo a favor), el valor materializado contra el recomputado
  en vivo. Si algún día se agrega test de migración, mejor.

### R3 — Índices de baja cardinalidad
`IX_TravelFiles_DerivedCollectionStatus` / `..._DerivedInvoicingStatus` (`:67-75`) indexan columnas de 4
valores distintos: selectividad casi nula, el planner probablemente los ignore para filtros amplios.
Inofensivos (no rompen nada), pero aportan poco. Nit de performance, no de riesgo.

### R4 — Lock/tamaño (no aplica en este contexto, pero anotado)
El `Up()` hace full-table UPDATE ×4 + build de 2 índices dentro de **una** transacción. En una tabla
grande con escrituras concurrentes esto extendería el downtime. **Acá no aplica**: migrador dedicado antes
de la app (OQ-3) + volumen chico (producto mono-usuario). Correcto para el contexto.

---

## RIESGOS DE INTEGRIDAD FISCAL / PLATA
- El backfill **no mueve plata ni emite/reversa comprobantes** — solo materializa una etiqueta de lectura.
  La mecánica NC/ND/CAE no se toca (`DISENO-implementacion.md:614-620`). Sin riesgo de doble-cobro,
  doble-emisión ni CAE duplicado en esta tanda.
- El eje de facturación materializado es **derivado del cuadre**, no una fuente fiscal: no reemplaza el
  comprobante ni el snapshot. Correcto.

## RIESGOS DE SEGURIDAD / PRIVACIDAD
- Sin endpoint nuevo. Sin autorización nueva que romper. Las columnas no exponen monto, CAE, ni PII; sus
  valores ya viajan hoy en el DTO. Sin fuga.

## RIESGOS DE MIGRACIÓN / ROLLBACK
- Aditiva pura, nullable sin default, idempotente, atómica (una tx), sin `NOT NULL` sobre tabla poblada,
  `CHECK` no aplica, `Down` seguro. **Sin objeciones.** (Ver R2/R4 arriba como matices, no bloqueos.)

## GAPS DE AUDITABILIDAD
- No aplica: la materialización de una etiqueta derivada no es una acción de negocio auditable (a
  diferencia de M1, que sí toca `Status` y lleva su rastro). El escritor go-forward es la misma
  `SaveChanges` de la plata, ya auditada por su ruta.

## TESTS FALTANTES
- Test que ejecute el **backfill SQL** de la migración y verifique valores contra el criterio en vivo
  (R2). Hoy el único test de integración cubre el proyector, no los UPDATEs.
- Caso `Traveling` en el backfill (el diseño §6 dice que la reparación "barre Traveling"): los ejes
  cobro/facturación no dependen de `Status`, así que se cubren igual, pero no hay aserción explícita.

## NEEDS HUMAN / PROFESSIONAL CONFIRMATION
- **Verificación post-deploy contra PROD (Gaston / operación):** después de correr la migración, un
  SELECT de control que confirme (a) que **ninguna** `TravelFiles` quedó con `DerivedCollectionStatus`
  o `DerivedInvoicingStatus` en `null`; (b) para una muestra —incluyendo anuladas, multimoneda y saldo a
  favor— que el valor materializado **coincide** con el recomputado en vivo; (c) el conteo de `Invoices`
  'A' con `TipoComprobante` fuera del set conocido (R1) es 0. Esto es fiscal/negocio-adyacente y debe
  mirarlo alguien con acceso a los datos reales, no yo.

## NO VERIFICADO (honestidad — no corrí nada de esto)
- **No corrí la migración** contra Postgres (real ni fixture). La equivalencia SQL↔C# es una **lectura de
  código**, no una ejecución.
- **No corrí CI** (unit + integración Postgres). No sé si la suite está verde.
- **No levanté la app real.** No hay caminata E2E ejecutada por mí sobre el listado filtrando por estos
  ejes.
- **No conozco los conteos reales de PROD** pre/post backfill (cuántas `TravelFiles`, cuántas con filas de
  moneda, cuántas con comprobantes 'A').
