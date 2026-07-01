# Cuenta del operador: pantalla (dos números), extracto con saldo único y botón de reembolso recibido

Fecha: 2026-07-01
Estado: HECHO, revisado y **DESPLEGADO**. Commits `0a0b052` (extracto saldo único) + `5290a81` (reembolso recibido). Pipeline 28542260974 success (tests back 3071 + front 1631 + deploy al VPS, migración `Adr041_M6` aplicada).

> Antes de esto, en el mismo día, se desplegaron: la pantalla de los tres recuadros (§2) + el bloque de cancelación (§3) en `10d3d79`; y las dos fugas de plata (anular con crédito + cambio de operador/moneda) en `df928c3`. Ver `2026-07-01-fugas-anular-credito-y-cambio-operador.md`.

## En fácil (para Gastón)

1. **El extracto del proveedor ahora cuadra.** Antes, abajo el saldo decía una cosa (ej. −1000, "pagaste de más") y arriba el recuadro decía otra ("Me tiene que devolver 700"), y la multa que los unía estaba escondida. Ahora la multa y el reembolso entran como renglones del extracto, con un **saldo único** que da exactamente lo de arriba. En el caso mezclado (le debés por un viaje vivo Y te tiene que devolver por otro anulado) muestra el **neto** con una línea que lo explica, y cada movimiento de anulación lleva un cartel **"Anulación"**.

2. **Botón "Registrar reembolso recibido".** Cuando el operador te devuelve la plata de una anulación, lo anotás desde la cuenta del operador y se imputa solo, en **una sola operación** (no queda plata suelta). Con **candado contra el doble cobro**: aunque le des dos veces o se corte la red, se registra una sola vez.

## Extracto saldo único (técnico)

- El "Saldo" del extracto pasó de **caja pura** a **económico** (`ClosingBalance = EconomicClosingBalance = CashClosing + Σmulta + Σreembolso`), que reconcilia con los recuadros por la identidad `Econ = X − Y − Prepago`.
- **Invariante interno intacto**: se agregó `CashClosingBalance` (eco de `SupplierBalanceByCurrency.Balance`); el test invariante caja↔proyección ahora compara contra ese campo. `SupplierDebtCalculator`/`SupplierCreditReconciler`/`SupplierDebtPersister` NO se tocaron — es cambio de PRESENTACIÓN en el mapper.
- Las líneas de circuito (multa retenida / reembolso recibido) entran como Cargo (+) y ACUMULAN en el running balance.
- **Límite honesto (decidido con Gastón)**: un saldo único no puede representar tres números que a propósito NO se netean. Cuando coexisten X e Y (o Y y prepago), el saldo es el neto y se aclara con `ReconciliacionSaldoOperador`; el encabezado sigue siendo la verdad del desglose.
- Archivos: `SupplierService.cs` (merge `BuildMergedLines`), `SupplierReadDtos.cs` (DTO), `SupplierExtractoSection.jsx` (renglones inline + reconciliación + chip).

## Reembolso recibido atómico + idempotente (técnico)

- Endpoint `POST /operator-refunds/record-and-allocate`: `RecordReceived` + `Allocate` con `deductions=[]` (camino simple, Net==Gross) en UNA transacción. Las retenciones tipificadas siguen en el flujo avanzado de 2 pasos.
- **Idempotencia server-side (corregida de raíz)**: llave `IdempotencyKey` (Guid) **sellada en el INSERT** del ingreso + índice único parcial `IX_OperatorRefundsReceived_IdempotencyKey` (filtrado NOT NULL, migración `Adr041_M6`). Check-previo + resolución del 23505. El atajo corre con `allowInternalRetry=false`: un conflicto xmin NO se reintenta dentro de la transacción (evita escrituras parciales); propaga a rollback total → 409 → el reintento con la misma llave resuelve idempotente. La 1ra versión sellaba la llave en un UPDATE posterior que el `ChangeTracker.Clear()` del retry podía descartar (doble cobro); se arregló tras el review.
- Botón/ficha: `RegistrarReembolsoRecibidoInline.jsx` (elige el pendiente, genera la llave una vez y la reusa en reintentos, deshabilita al enviar, refresca extracto+recuadros+solapa Reembolsos). Gateado por `caja.edit` + `tesoreria.supplier_payments`.
- Fiscal (por investigación, sin contador): el reembolso del operador en turismo minorista normalmente viene neto (sin retenciones); el botón simple asume eso. Las retenciones son caso raro → flujo avanzado aparte.

## Reviews

- Extracto: backend-dotnet-reviewer (presentación Approved), frontend-reviewer (Approved), data-exposure (Approved).
- Reembolso: backend + security + data-exposure, **2 rondas** (la 1ra encontró el bug de idempotencia B1; se corrigió de raíz y la 2da dio Approved).

## Pendiente / follow-ups (NO bloquean)

1. **Test de integración Postgres** de la carrera real 23505+xmin del reembolso idempotente (dos requests misma llave mismo BC → un solo movimiento). No corre sin Docker local; documentado en el test file.
2. **Verificar en Postgres prod** que el índice `IX_OperatorRefundsReceived_IdempotencyKey` quedó vivo (la migración se aplicó vía deploy; confirmar por SSH cuando se pueda).
3. Menores: `SupplierAccountStatementLineDto` manda `Kind`/`SourcePublicId` al browser aunque la UI no los muestre (apretar backend); catch amplio de `InvalidOperationException`/`ArgumentException` en el controller podría reflejar mensajes de framework (pre-existente, module-wide); guard-test si se agrega un 3er kind de circuito (hoy solo 2); idempotencia también al flujo de 2 pasos; fecha con aritmética local en la ficha de reembolso.
