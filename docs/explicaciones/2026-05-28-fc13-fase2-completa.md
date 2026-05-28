# 2026-05-28 — FC1.3 Fase 2 cerrada (F2.0 → F2.3)

> Para vos cuando retomes (o cualquier developer trainee que se sume).
> Nivel: explicación criolla, sin tecnicismos cuando se puede.

## TL;DR

Hoy terminamos la **Fase 2 entera del módulo de Nota de Crédito parcial**. El sistema ahora puede emitir NC parcial real al ARCA cuando se aprueba una cancelación que pierde causa fiscal parcialmente. **El flag sigue apagado en prod** — antes de prenderlo hay que pasarle 4 preguntas al contador, homologar con AFIP, y desplegar F2.5 si se opera multimoneda.

HEAD final del día: `8d77fd1`. Commits del día: 4 (F2.2 Etapa 5 + cuadre fix + BRUTO + F2.3 + checklist + doc).

## ¿Qué problema resolvimos?

**Antes de hoy**: cuando un cliente cancelaba parte de una reserva ya facturada, el sistema **anulaba la factura completa** y emitía una nueva por el remanente. Eso funciona pero es ruidoso fiscalmente — el AFIP ve "anularon una factura y emitieron otra" en vez de "acreditaron una porción de la factura original".

**Hoy**: el sistema emite una **NC parcial real** — un comprobante de crédito que dice "de la factura X, acreditemos $750 y dejemos vivo $250". El AFIP la reconoce como tal (vía `<CbtesAsoc>` que la liga a la factura original). Es lo que pide la RG 4540 de ARCA.

### Ejemplo pelotudo

Vendiste un hotel a $1.000 (factura B). El cliente cancela 75%. La gestión te cobra $50 no reembolsable y el operador te cobra $200 de penalidad. Te quedan $750 a "devolverle fiscalmente".

- **Antes**: anulabas la factura completa (NC por $1.000) y emitías nueva factura por $250. Dos comprobantes para mover $750.
- **Hoy**: emitís una NC parcial por $750 sobre la factura original. Un solo comprobante, fiscalmente fiel.

## ¿Qué construimos? (5 sub-fases)

### F2.0 — Settings y flag
Agregamos 5 settings configurables (`OperationalFinanceSettings`) y el flag maestro `EnablePartialCreditNoteRealEmission` que arranca en `false`. Mientras esté apagado, el sistema sigue haciendo el flujo viejo (NC total + factura nueva). Cuando lo prendamos, empieza a emitir NC parcial real.

### F2.1 — VO FiscalLiquidation
Agregamos a la entidad `BookingCancellation` un objeto valor `FiscalLiquidation` con 4 montos: `OriginalInvoiceAmount`, `FiscalAmountToCredit`, `NonRefundableItemsAmount`, `OperatorPenaltyAmount`. Un CHECK constraint en Postgres garantiza que la suma cuadra. Backfill seguro de cancelaciones viejas.

### F2.2 — Pipeline de emisión (job + idempotencia)
Construimos el camino completo: endpoint → cola → job → AFIP. Lo más delicado fue la **idempotencia**: si el sistema se cae después de mandar la NC al AFIP pero antes de actualizar internamente, al reintentar **no se duplica el comprobante**. Usamos un hash SHA256 del request como llave única + recuperación automática vía `FECompConsultar`. Además decidimos que `line.Total` es BRUTO (con IVA incluido, como el cliente lo ve facturado).

### F2.3 — Conexión con el flujo BC
Hoy: cuando se aprueba una cancelación con `CreditNoteKind = PartialOnOriginal` y el flag está prendido, el `BookingCancellationService` deja de mandar el fallback FC1.2 (NC TOTAL) y arma las líneas de la NC parcial llamando al pipeline F2.2. Si el flag está apagado, todo sigue como hasta ayer (regresión cero).

## Las 3 ramas del armado de líneas

`BuildPartialCreditNoteLines` (en `BookingCancellationService.cs`) tiene 3 ramas según el caso:

| Caso | Qué hace |
|---|---|
| **Items no reintegrables** (gestión, seguro, anticipo no reembolsable) | Filtra los `IsRefundable=false` y prorratea los restantes con factor `FiscalAmountToCredit / SUM(refundable.Total)`. La última línea absorbe el residuo de redondeo para que la suma cuadre exacto. |
| **Multi-alícuota** (factura con ítems al 10.5% + 21%, por ejemplo) | Una línea por alícuota con prorrateo proporcional al `GroupTotal`. Cada línea hereda la `Description` del item representativo del grupo (trazabilidad fiscal). |
| **Mono-alícuota** | Una sola línea con la `Description` armada desde el template configurable `PartialNcDescriptionTemplate` (incluye `{invoiceType}`, `{fiscalAmount}`, `{customerTaxId}`). |

## Decisiones que tomamos (no las olvides)

1. **`line.Total` es BRUTO**, no neto. El cliente lo facturó así, el sistema extrae el IVA por dentro al armar el XML para AFIP.
2. **NC parcial NO cascade-voida receipts**. Si la factura $1.000 estaba pagada en 3 recibos ($300+$300+$400), no hay "el" recibo a anular. Creamos un Payment de reversa con `OriginalPaymentId=null` + audit explícito con los IDs de los recibos vivos para que un admin reconcilie manualmente vía UI Fase 3.
3. **Guard multimoneda activo**: hasta que F2.5 (XML SOAP multimoneda) esté desplegada, el sistema rechaza emitir NC parcial sobre facturas no-ARS. Se quita cuando F2.5 lista.
4. **Modo CommissionOnly (intermediario)** queda diferido a manual hasta Fase futura — no tocamos eso en F2.3.

## Cosas que pasaron en el camino (lecciones)

### Decisión BRUTO vs NETO
A mitad de F2.2 nos encontramos con que `line.Total` significaba cosas distintas en distintos lugares del código (a veces neto, a veces bruto). Te puse 2 opciones, elegiste BRUTO ("lo que ve el cliente"). Reescribimos el calculator con extracción de IVA por dentro (`base = bruto / (1+tasa), iva = bruto - base`). El residuo de redondeo va al IVA, no al neto — garantiza cuadre exacto contra `ImpTotal` que ve AFIP.

### Bug del cuadre ARCA (B1)
El job validaba la suma con los montos redondeados del calculator, pero después recalculaba al armar el envelope final con números levemente distintos. Resultado: el envelope viajaba con un gap chiquito vs lo que validó. Fix: agregamos `TotalsOverride` opcional al `CreateInvoiceRequest` — el caller de NC parcial llena este campo con los totales pre-calculados (incluido el IVA extraído línea por línea). El path FC1.2 lo deja en null y queda byte-idéntico.

### El test del DROP CONSTRAINT
Un test (F2.3 sum mismatch) dropea un CHECK constraint con `ALTER TABLE` para forzar el escenario inválido. Si no lo restaurábamos en `finally`, los tests siguientes perdían la protección. Lo envolvimos en try/finally con el constraint exacto que define la migración M1.

### El guard multimoneda (R1 del contador)
El review integrado del contador encontró que el snapshot fiscal guarda `Currency` (USD/ARS), pero el XML SOAP al ARCA sigue hardcoded en `MonId=PES, MonCotiz=1`. Sin guard, si prendíamos el flag y había una factura USD, la NC salía al AFIP en pesos = desfasaje fiscal alto. Agregamos guard en `EmitRealPartialCreditNoteAsync` que aborta + audit si Currency != "ARS". Se quita en F2.5.

## Archivos clave (si querés mirar el código)

| Archivo | Qué tiene |
|---|---|
| `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` | OnApprovedAsync con branch flag ON/OFF + helpers F2.3 (líneas ~1572-1640 y ~2030-2360) |
| `src/TravelApi.Infrastructure/Services/AfipService.cs` | ApplyCreditNoteEconomicReversalAsync split parcial vs total (líneas ~1216-1430) |
| `src/TravelApi.Infrastructure/Services/InvoiceService.cs` | EnqueuePartialCreditNoteAsync + job + cuadre ARCA exacto |
| `src/TravelApi.Infrastructure/Services/PartialCreditNoteIvaCalculator.cs` | Extracción IVA por dentro del bruto |
| `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` | Clasifica casos + dispara revisión manual cuando hace falta |
| `src/TravelApi.Domain/Entities/FiscalLiquidation.cs` | VO con 4 montos + CHECK constraint en DB |
| `docs/operations/fase2-deploy-checklist.md` | Procedimiento Docker para deployar y prender flag |
| `tools/sql/fase2-m1-prevalidation-metadata.sql` | Script SQL de prevalidación (solo lectura) |
| `scripts/ops/run-tests-fc13.sh` | Corre todos los tests FC1.3 en VPS |

## Qué falta antes de prender flag en prod

**No prendas el flag sin esto.**

1. Pasarle al contador las 4 preguntas del checklist (`docs/operations/fase2-deploy-checklist.md` paso 4A) + las del review integrado:
   - Política prorrateo con penalty + items no reintegrables (escalado proporcional vs líneas full).
   - Política multi-alícuota (preservar todas con prorrateo vs colapsar a dominante).
   - Descripción multi-alícuota (item representativo del grupo).
   - Estado transitorio recibos vivos + Payment reversal hasta UI Fase 3.
2. Homologación ARCA: emitir 1 NC parcial real en test AFIP con multi-alícuota, confirmar CAE aprobado + `<CbtesAsoc>` serializado correctamente en XML.
3. Si vas a operar multimoneda: desplegar F2.5 (XML SOAP con `MonId` y `MonCotiz` reales) antes de prender F2.3 sobre facturas USD. Mientras no esté F2.5, el guard te frena automático.
4. Correr `bash scripts/ops/run-tests-fc13.sh` en el VPS y confirmar 100% verde.

## Próximas fases (cuando retomes)

- **F2.4**: UI Fase 3 — bandeja "NC parciales con receipts vivos pendientes de reconciliar" para que el admin decida cuál Payment/Receipt anular manualmente.
- **F2.5**: XML SOAP multimoneda (`MonId` + `MonCotiz` reales). Saca el guard.
- **Follow-up no-bloqueantes**:
  - Refactor tabla alícuotas duplicada (`AfipService.GetVatMultiplier` + `PartialCreditNoteIvaCalculator.GetVatMultiplier`).
  - Atomizar las 2 `SaveChangesAsync` de `ApplyCreditNoteEconomicReversalAsync` con `BeginTransactionAsync` (riesgo pre-existente, no regresión).
  - Test integration de `RecalculateReservaBalanceAsync` post NC parcial multi-payment.
  - Métrica `metric:Fc13.PartialCreditNote.Emitted` con originalInvoiceId, fiscalAmountToCredit, currency.
