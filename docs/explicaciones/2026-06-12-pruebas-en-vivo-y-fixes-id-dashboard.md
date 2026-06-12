# 2026-06-12 — Pruebas en vivo del libro de caja + fixes (id leak, dashboard)

## Contexto

El ADR-022 (circuito ERP / libro de caja `CashLedgerEntry`) ya estaba commiteado y pusheado (HEAD `ea1150c`). Esta sesión Gastón **desplegó en el VPS y probó en pantalla** por primera vez.

Log del contenedor `migrate` (confirmación de deploy sano):
- `EF Core Migrations applied successfully.`
- `ADR-022 cash ledger backfill done. Cobros=45, PagosProveedor=7, Manuales=0.`

O sea: la migración entró y el libro quedó poblado con los movimientos históricos.

## Hallazgos de la prueba en vivo (Gastón, como Admin)

1. **Caja: se ven egresos pero no ingresos.**
2/3. **El saldo a favor muestra un "id" (GUID) que no debería verse.**
4. **Pago a proveedor: funciona.**
5. **Dashboard se queda cargando.**

Además Gastón observó que **"Cobranza y Facturación" no entró en el circuito nuevo** (correcto) y pidió **conectarla al libro**.

## Diagnóstico

### Punto 1 — Caja sin ingresos (NO es bug de datos)
Los 45 cobros están en el libro, pero su `OccurredAt = PaidAt` es histórico (meses anteriores). `GetMovementsAsync` ordena por fecha descendente (lo nuevo arriba) y pagina de a 25 → los egresos de prueba de hoy copan la página 1 y los cobros caen abajo. Encima la tarjeta "Ingresos del mes" filtra solo el mes corriente (`OccurredAt >= startOfMonth`), así que muestra ≈0. **Descartado** que sea el query filter de Payment (la FK `CashLedgerEntry.Payment` es opcional → LEFT JOIN, no descarta filas).
**Decisión:** Caja pasa a ser arqueo del mes corriente con flecha para meses anteriores. **Pendiente (item 3).**

### Puntos 2/3 — Leak del GUID (HECHO)
El sobrepago crea un `Payment` "puente" interno (`Method="SaldoAFavor"`, `AffectsCash=false`, `OriginalPaymentId!=null`, monto negativo, `Notes` con un GUID). Ya estaba excluido en `PaymentService.GetPaymentsForReservaAsync` pero **no** en la cuenta del cliente ni en la lista global.

### Punto 5 — Dashboard colgado (HECHO)
`ReportService.GetDashboardAsync` awaiteaba la cotización del Banco Nación (`GetUsdSellerRateAsync`, fetch HTTP con timeout interno de 10s) → bloqueaba toda la respuesta. El front no tiene timeout de cliente, así que el skeleton giraba.

## Cambios implementados (working tree, SIN commitear al cierre)

**Fix id leak:**
- `CustomerService.GetCustomerAccountPaymentsAsync`: excluye el puente (predicado inline `Method==BridgeMethod && !AffectsCash && OriginalPaymentId!=null`).
- `CustomerService` `PaymentCount`: mismo predicado, para que el badge "Pagos: N" coincida con las filas visibles.
- `PaymentService.GetAllPaymentsAsync` (GET /payments): excluye el puente (proyecta a `PaymentDto`, que expone `Notes` con el GUID). Lo encontró el revisor de seguridad.

**Fix dashboard BNA:**
- `IBnaExchangeRateService` + `BnaExchangeRateService`: nuevo `GetPersistedUsdSellerRateAsync` (lee solo el snapshot persistido, sin red).
- `ReportService`: helper `GetDashboardBnaRateAsync` con timeout 2s (CancellationTokenSource linkeado) + fallback a snapshot persistido/null. Nunca propaga excepción ni bloquea. Contrato `DashboardResponse` sin cambios.

**Tests:** `CustomerServiceTests` (puente excluido) + `Adr022DashboardBnaDegradationTests` (3 tests de degradación). Suite unit completa: **1394/1394 verde**.

## Reviews
- `backend-dotnet-reviewer`: Approved with comments (M1 PaymentCount → aplicado; M2 doble lectura snapshot → opcional, no aplicado).
- `security-data-risk-reviewer`: Approved with comments. Cerró el leak de GET /payments. Marcó leaks residuales que resuelve la conexión Cobranza→libro (`GetHistoryAsync` muestra una fila negativa fantasma del puente) y un **hueco de autorización pre-existente**: `/customers/{id}/account/*` solo bajo `[Authorize]` de clase, sin permisos finos.

## Pendientes
1. Commitear los fixes de hoy.
2. **Item 3:** Caja = "este mes" con flecha (UX gate → backend filtro fecha → front `MonthNavigator`). Deploy de 1+2+3 juntos.
3. **Item 4:** conectar "Cobranza y Facturación" al libro. La parte de plata sale del libro (misma fuente que Caja); las facturas/NC siguen leyendo `Invoices`. Necesita ADR + cadena completa + cuidado con masking see_cost y multimoneda.
4. **Tema aparte:** permisos finos en `/customers/{id}/account/*`.
