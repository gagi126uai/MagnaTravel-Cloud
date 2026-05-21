# Plan FC1.3 Fase 1 — NC parcial en cancelaciones de turismo (solo Hotel)

> **Autor**: `travel-agency-accountant-argentina` (primer trabajo del subagente integrado).
> **Fecha**: 2026-05-21.
> **Para**: Gaston (review) → `software-architect` (diseño técnico) → `backend-dotnet-senior` (impl).
> **Idioma**: rioplatense, nivel trainee/junior, con ejemplos pelotudos.
> **Status**: Propuesta funcional fiscal+contable+negocio. NO incluye arquitectura técnica (capas, DI, dispatcher).
> **Scope**: solo Hotel. Vuelo/Paquete/Traslado/Asistencia siguen el flujo legacy y aparte rechazan el flujo FC1.3 con mensaje claro.
> **Modo Plan activo**: no se editaron archivos del repo; este documento es el único output editable.

---

## 1. Resumen ejecutivo

FC1.3 Fase 1 introduce **NC parcial fiscalmente correcta** para cancelaciones Hotel. Hoy ADR-002 / FC1.2 modela bien el flujo operativo (T0..T3, deducciones, multimoneda, audit), pero **emite siempre NC total** (decisión cerrada del contador previo). El criterio nuevo del contador (2026-05-21) cambia la regla: **la NC refleja la parte del comprobante original que pierde causa fiscal**, no el dinero que se devuelve.

La fase 1 agrega:

1. **Flag `IsRefundable` por item** al cargar la reserva (decisión D4 2026-05-21).
2. **Flag de modo de facturación por `Supplier`** (`InvoicingMode` = `TotalToCustomer` reseller / `CommissionOnly` intermediario, decisión D1).
3. **Tabla de penalidades por operador** + override manual del vendedor (decisión D2).
4. **"Liquidación fiscal" calculada** con la estructura textual del contador (Factura original / Monto cancelado / Penalidad retenida / Items no reintegrables / Importe fiscal a acreditar / Importe a devolver / Neto facturado final).
5. **Decisión fiscal automática** "NC parcial vs NC total + nueva factura": clasifica cada caso contra la matriz de 8 casos del contador. Si cae en "revisión manual obligatoria", el BC pasa a `ManualReviewPending` y bloquea emisión hasta aprobación admin.
6. **Estados `RequiresManualReview` / `ManualReviewPending` / `ManualReviewApproved` / `ManualReviewRejected`** insertados antes de `AwaitingFiscalConfirmation`.
7. **Umbrales parametrizados** ($500k auto / $500k-$2M admin reforzada / >$2M manual contable) en `OperationalFinanceSettings`.

La Fase 1 **no implementa UI** ni emisión real de NC parcial al ARCA — eso queda para Fase 2 (emisión NC parcial + plumbing AfipService) y Fase 3 (UI modal + bandeja).

---

## 2. Ejemplo pelotudo

Tenés una fiambrería. Cliente paga 4 milanesas a $250 c/u = $1.000. Después dice "uy, me llevo 3, no 4". Tenés tres opciones:

- **Anular la cuenta entera y hacer una nueva por 3 milanesas** → NC total + factura nueva por $750. Funciona, pero contablemente tu cuaderno explota: pasaste por dos eventos donde el primero ya no existe.
- **Hacer una nota de crédito por $250** (una milanesa) → NC parcial. La factura original queda viva por las 3 que se quedó el cliente. Cuaderno limpio.
- **El cliente además te dejó $50 de seña por el envoltorio del regalo y eso NO se devuelve** → ese item se marca `IsRefundable=false` al cargarlo. La NC parcial cuenta ese $50 como "ítem no reintegrable" y NO entra en el monto a acreditar al cliente.

Caso turismo: cliente paga $1.000.000 por un hotel (factura A). Cancela. Operador retiene $200.000 de penalidad. Además había contratado un seguro de cancelación de $50.000 marcado `IsRefundable=false`.

- Devolvemos al cliente: $750.000 (lo financiero).
- **NC fiscal**: $750.000 (lo que perdió causa fiscal sobre la factura A).
- Neto facturado final de la operación: $250.000 (el hotel se quedó con $200k de penalidad + nosotros con el seguro $50k que se consumió al activarse).
- Como es Factura A → **revisión manual obligatoria** (caso 8 de la matriz). El sistema calcula la propuesta, el admin confirma o decide ir por NC total + nueva factura por $250k.

---

## 3. Hechos verificados en el repo

Antes de proponer, releve estos artefactos reales (rutas absolutas):

- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-002-cancellation-refund.md` — ADR-002 vigente (T0..T3, 3 aggregate roots, multimoneda, sistema impositivo configurable, 25 invariantes Bucket G). Confirmé que la decisión "NC siempre por total facturado" (§2.2 punto 1) es la que esta fase **reemplaza** con el criterio nuevo del contador 2026-05-21.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\Invoice.cs` — entidad activa con `OriginalInvoiceId` self-reference para NC, `AnnulmentStatus` enum (None/Pending/Succeeded/Failed), `LastArcaAttemptAt`, `AnnulmentApprovalRequestId`. **No tiene** ningún campo de "porcentaje anulado" o "tipo de NC (parcial/total)" — hay que agregarlo.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\InvoiceItem.cs` — items planos (`Description`, `Quantity`, `UnitPrice`, `Total`, `AlicuotaIvaId`, `ImporteIva`). **No tiene** `IsRefundable`, `ItemCategory`, ni vínculo al `ServicioReserva` o `HotelBooking` origen. Hay que agregar todo eso.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\BookingCancellation.cs` — BC con FK a `Reserva`, `OriginatingInvoice`, `CreditNoteInvoice`, snapshot fiscal, estados T0..T3. **No tiene** estados de revisión manual, ni "tipo NC propuesto", ni "liquidación fiscal calculada".
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\BookingCancellationStatus.cs` — enum con `Drafted`, `AwaitingFiscalConfirmation`, `AwaitingOperatorRefund`, `ClientCreditApplied`, `Closed`, `AbandonedByOperator`, `Aborted`, `ArcaRejected`. Falta insertar los 4 nuevos de revisión manual.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\OperationalFinanceSettings.cs` — ya tiene `OperatorRefundTimeoutDays`, `OnePerReservaInvoicePolicy`, `Ley25345ThresholdAmount`, `PhysicalRefundAlertThreshold`, `EnableNewCancellationFlow`. **Falta**: umbrales de monto (auto / admin reforzada / manual contable), defaults de mensajes NC parcial.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\Supplier.cs` — sin `InvoicingMode`, sin `PenaltyTable`. Hay que agregar ambos.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\HotelBooking.cs` — sin `IsRefundable` por item ni por línea. Solo tiene `NetCost`/`SalePrice`/`Commission` global. Hay que agregar conceptos no reintegrables.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\Customer.cs` — `TaxCondition` string ("Consumidor Final" default) + `TaxConditionId` (1=RI, 4=Exento, 5=CF, 6=Mono). Usable para detección "Factura A" (cliente RI o cuando agencia se vuelve RI).
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Infrastructure\Services\AfipService.cs` líneas 827-838 — `<CbtesAsoc>` ya emitido. **RG 4540 vínculo NC↔factura cubierto**. La emisión de NC con montos parciales requiere ajustes en `<ImpTotal>`/`<ImpNeto>`/`<ImpIVA>` (lo veré en Fase 2, no en Fase 1).
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Infrastructure\Services\BookingCancellationService.cs` — service vigente FC1.2 con `DraftAsync`, `ConfirmAsync`, `AbortAsync`, `ForceArcaConfirmationAsync`. Listo para extenderse con la fase de revisión manual.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\ApprovalRequestType.cs` — `InvariantOverride=7`, `ProviderRefundRequest=8`, `ClientRefundReversal=9`, `MisassociationReversal=10` ya existen. **Falta**: `PartialCreditNoteApproval` (admin reforzada) y eventualmente `ManualAccountingReview` (>$2M).
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md` — matriz 8 casos + 2 escenarios + 7 excepciones documentados.
- `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-2.md` — plan FC1.2 v3 ya commiteado. Las 7 fases FC1.2.0..FC1.2.7 completaron implementación + 94 tests verdes (memoria del usuario).

---

## 4. Suposiciones

Para que el plan avance sin trabarme, asumo lo siguiente. Cualquier suposición que se rompa cambia el alcance.

1. **`Invoice.OriginalInvoiceId` self-reference es suficiente** para vincular la NC parcial a la factura origen (RG 4540 vía `<CbtesAsoc>`). No hay que cambiar el modelo de Invoice para soportar vínculo distinto en NC parcial vs total — solo agregar metadata.
2. **El monto IVA en NC parcial se prorratea proporcionalmente al monto fiscal acreditado**, no al monto financiero devuelto. Ej: factura $1.000.000 con IVA 21% sobre $826.446,28 (neto) → NC parcial de $750.000 lleva IVA de $130.165,29 (proporción 75%). Hay que confirmar esto con el contador en Fase 2.
3. **HoyMagnaTravel = Monotributista** (factura tipo 11/C). El flujo de NC parcial Hotel se ejercita en este régimen primero. Factura A (RI) entra a la matriz en caso 8 = revisión manual hasta que migremos.
4. **Hotel es el único tipo de servicio que entra al flujo FC1.3 Fase 1**. Vuelo/Paquete/Traslado/Asistencia siguen FC1.2 (NC total) hasta que se decida sino lo contrario. El sistema debe **rechazar explícitamente** una cancelación FC1.3 sobre reserva con servicios mixtos.
5. **Una reserva = una factura activa** sigue rigiendo (`OnePerReservaInvoicePolicy=true`). FC1.3 no levanta esa restricción.
6. **El vendedor marca `IsRefundable=false` al cargar la reserva** (al momento de crear el `HotelBooking` o agregar conceptos sueltos). Por defecto los items son refundables. El cambio post-carga requiere edición de la reserva en estado `Budget`/`Confirmed` con permisos.
7. **Multimoneda Fase 1**: el cálculo de la liquidación fiscal usa el `ExchangeRateAtOriginalInvoice` ya capturado en `FiscalSnapshot` (T0). No introducimos lógica nueva de TC en esta fase.
8. **No hay precedente** de NC parcial en el repo. Es la primera. Hay que decidir si reutilizar `InvoiceItem` con cantidades fraccionadas o introducir un campo nuevo. Mi propuesta: campo nuevo (ver §10).
9. **Los items "no reintegrables" no son una `DeductionLine`** de `OperatorRefundAllocation`. Son **un concepto distinto** que vive en `InvoiceItem` (Fase 1 los modela ahí) o en `HotelBooking` como conceptos adicionales (ej: cargo gestión cobrado a parte). `DeductionLine` sigue siendo lo que retiene el operador en T2.

---

## 5. Alcance impositivo (ARCA)

### 5.1 NC parcial — RG 4540 (referencia normativa)

`Hecho verificado:` el repo ya emite `<CbtesAsoc>` con el vínculo a factura original. La NC parcial requiere lo mismo (vínculo) + montos coherentes con la "parte que pierde causa fiscal".

`Necesita confirmación profesional:` la **regla de prorrateo del IVA** en NC parcial. La práctica más común es proporcional al neto, pero hay agencias que aplican criterio "ítem por ítem" cuando la factura discriminó bien. Esto cambia la matriz contable. Lo confirmamos con el contador antes de Fase 2.

### 5.2 Plazo 15 días RG 4540

`Hecho verificado:` el "hecho documentable" para FC1.2 ya está cerrado en T2 (acuerdo de devolución con el operador). Esa decisión cubre FC1.3 sin cambios — el reloj de 15 días arranca cuando el operador confirma el refund, no cuando el cliente cancela.

`Riesgo fiscal:` si el caso cae a `ManualReviewPending` y el admin demora más de 15 días desde T2 en aprobar, **caemos en mora fiscal**. Hay que agregar alerta al panel admin "BC en revisión manual + ventana RG 4540 a punto de vencer".

### 5.3 Detección "Factura A" (caso 8 matriz)

`Hecho verificado:` el repo persiste `Customer.TaxCondition` y `Customer.TaxConditionId` (1=RI). Cuando el cliente es RI, la factura emitida fue tipo 1/A. Eso disparara revisión manual automática.

`Suposición:` actualmente MagnaTravel es Monotributo, así que emite Factura C (tipo 11). Si el cliente es Consumidor Final → tipo 11 emite C, no A. **La condición real es "factura tipo 1 (A) emitida"**, no "cliente RI". Hay que mirar `Invoice.TipoComprobante`.

### 5.4 Items no reintegrables — tratamiento fiscal por tipo

| Concepto | Tratamiento NC parcial | Riesgo fiscal si se mezcla |
|---|---|---|
| Cargo administrativo (gestión agencia) | NO entra en NC: es ingreso reconocido de la agencia. Si está en la factura original, queda como "neto facturado final". | Si NC parcial lo incluye, la agencia se autoreduce ingreso falsamente. |
| Seguro de cancelación contratado | NO entra en NC: pasa al asegurador, ya consumido al activarse. | Si NC lo incluye, IVA descontado a ARCA que no corresponde. |
| Anticipo no reembolsable del operador | NO entra en NC: cuenta corriente con operador, no afecta cliente fiscalmente. | Si NC lo incluye, distorsiona base imponible. |
| Penalidad operador (tabla antelación) | Tratamiento **dual** según D1 del operador: si modo reseller, NO entra en NC (es retención operador, no de la agencia); si intermediario, depende. | Confusión típica: tratar penalidad como `DeductionLine` AdministrativeFee cuando en realidad cambia naturaleza fiscal. |

`Necesita confirmación profesional:` confirmar con contador el tratamiento de cada concepto cuando agencia es Monotributo vs RI. Hoy la matriz cruzada del ADR-002 §2.8 ya cubre el régimen general; FC1.3 agrega el **prorrateo dentro de la factura**.

### 5.5 Modelo de facturación por operador

`Hecho verificado por decisión:` cada `Supplier` lleva flag `InvoicingMode` (D1 2026-05-21). Impacto fiscal:

- `TotalToCustomer` (reseller): factura al cliente por el total del servicio. NC parcial calculada sobre ese total. La penalidad operador NO reduce la NC al cliente; es costo de la agencia (queda en su balance contra el ingreso reconocido).
- `CommissionOnly` (intermediario): factura al cliente solo la comisión. NC parcial calculada sobre la comisión. Si el operador devuelve cero, la NC puede ser cero pero la operación financiera distinta sigue.

`Riesgo fiscal integrado:` un operador puede cambiar `InvoicingMode` con el tiempo. Si cancelo hoy una factura emitida hace 6 meses cuando el operador estaba en `CommissionOnly`, **tengo que usar el snapshot del momento de emisión**, no el actual. Esto requiere persistir `InvoicingMode` en el `FiscalSnapshot` (campo nuevo, ver §10).

### 5.6 Penalidades — distinción crítica

`Hecho verificado por decisión:` D2 2026-05-21 — tabla por antelación + override manual. Implicancia fiscal por escenario:

- **Si la penalidad la cobra el operador (modo reseller)**: la agencia recibe menos plata del operador. La NC al cliente sigue siendo por el monto fiscal a acreditar (no se reduce por la penalidad). La penalidad aparece como `DeductionLine` con `Kind=CancellationPenalty` en la allocation T2. Esto **ya está modelado** en FC1.2.
- **Si la penalidad la cobra la agencia (modo intermediario, o cobro propio)**: el ingreso de la agencia por penalidad es ingreso reconocido, NO entra en NC. La factura original cubre comisión + penalidad? Depende. Caso 7 matriz: si cambia la naturaleza fiscal del retenido → **NC total + nueva factura por penalidad**.

---

## 6. Alcance contable

### 6.1 Asientos sugeridos por escenario

`Suposición:` MagnaTravel HOY es Monotributo, asientos manuales (decisión ADR-002 §2.12 — `AccountingEntry` out-of-scope FC1). Por lo tanto FC1.3 Fase 1 NO crea asientos automáticos, **pero sí debe enriquecer el export CSV/Excel mensual** con columnas para que el contador asiente bien.

#### Escenario A (factura A, retenciones, items no reintegrables) — apunte sugerido en T0 (NC parcial)

| Cuenta (sugerida) | Debe | Haber |
|---|---|---|
| Ingresos por venta de servicios turísticos | 619.834,71 | |
| IVA débito fiscal | 130.165,29 | |
| Deudores comerciales — Cliente X | | 750.000,00 |

(El neto facturado final de $250.000 sigue vivo en la factura original).

#### En T2 (operador devuelve, deducciones)

| Cuenta | Debe | Haber |
|---|---|---|
| Banco (o caja) | 800.000,00 | |
| Gasto por penalidad operador | 200.000,00 | |
| Cuenta corriente operador / Operadores a cobrar | | 1.000.000,00 |

(Numerada simplificada — depende plan cuentas real del contador).

#### En T3 (cliente retira $750.000)

| Cuenta | Debe | Haber |
|---|---|---|
| Deudores comerciales — Cliente X | 750.000,00 | |
| Banco / Caja | | 750.000,00 |

### 6.2 Revenue recognition

`Riesgo contable integrado:` en modelo reseller (D1=TotalToCustomer), la cancelación parcial reduce el ingreso reconocido en el monto fiscal acreditado ($750.000 en el ejemplo), **no en el monto financiero devuelto** ni en el total facturado. El neto facturado final de $250.000 sigue como ingreso de la agencia.

En modelo intermediario (D1=CommissionOnly), la NC parcial reduce el ingreso por comisión solo en la parte que pierde causa. Es importante: **el grueso del servicio no estaba como ingreso de la agencia**, así que no afecta su revenue. Solo afecta la cuenta corriente con operador.

### 6.3 Multimoneda

`Hecho verificado:` FC1.2 ya captura los 3 TC (T0/T2/T3) en `FiscalSnapshot`. FC1.3 Fase 1 **no cambia esto**.

`Riesgo:` la diferencia de cambio en NC parcial USD → ARS tiene una sutileza: la NC se emite con el TC de T0 (RG AFIP coherencia) pero el monto fiscal acreditado puede no coincidir con lo que se devuelve al cliente si hay items no reintegrables. Hay que **validar que la NC en USD * TC T0 = monto fiscal acreditado en ARS** y que la diferencia con lo devuelto al cliente quede como ajuste financiero. Lo confirmamos con el contador antes de Fase 2.

---

## 7. Alcance de negocio (agencia)

### 7.1 Workflow nuevo

```
Vendedor en pantalla cancelación
        │
        │ marca/confirma items refundables (default true)
        │ ingresa penalidades operador (tabla o override manual)
        │
        ▼
   Sistema calcula LIQUIDACION FISCAL
   - Factura original
   - Monto cancelado
   - Penalidad retenida
   - Items no reintegrables (suma items con IsRefundable=false)
   - Importe fiscal a acreditar  ← propuesta NC parcial
   - Importe a devolver al cliente
   - Neto facturado final
        │
        ▼
   Sistema clasifica caso (matriz 8) y aplica reglas:
   - Caso 1, 3, 5: NC parcial → si monto < $500k auto-emite
   - Caso 2, 6: NC total → auto-emite
   - Caso 4, 7, 8: NC total + nueva factura → ManualReviewPending
   - Si Factura A: ManualReviewPending siempre
   - Si monto entre $500k-$2M: ManualReviewPending (admin reforzada)
   - Si monto > $2M: ManualReviewPending (manual contable)
        │
        ├── auto-emite → AwaitingFiscalConfirmation (T0, ADR-002 §2.4)
        │
        └── ManualReviewPending
                │
                │ Admin abre, ve liquidación, ajusta penalidades si quiere
                │
                ├── aprueba → ManualReviewApproved → AwaitingFiscalConfirmation
                │
                └── rechaza → ManualReviewRejected (vuelve a Drafted o se aborta)
```

### 7.2 Voucher vs factura — riesgo de confusión

`Riesgo integrado:` un cliente Hotel típicamente recibe vouchers del operador (no fiscales) + factura agencia (fiscal). La cancelación FC1.3 debe revocar vouchers (no son comprobantes fiscales, solo se anulan operativamente) **y separadamente** emitir NC parcial fiscal. El sistema **NO debe confundir voucher con factura**. Confirmé que el repo ya tiene `Voucher` separado y `Invoice` separado, y FC1.2 los maneja como módulos distintos. FC1.3 no cambia eso.

### 7.3 Comisión del vendedor con tope cero

`Hecho verificado:` política MagnaTravel cerrada — comisión del vendedor sobre cancelación puede ir a cero, **nunca negativa**. FC1.3 Fase 1 no toca esto, pero hay que asegurar que el cálculo de comisión use el "neto facturado final" (no el monto fiscal acreditado), porque el vendedor cobra sobre lo que efectivamente quedó facturado.

`Detalle a confirmar con Gaston:` ¿el vendedor cobra sobre el neto facturado final post-cancelación, o sobre el "monto fiscal acreditado al cliente"? Mi propuesta fiscal: sobre el neto facturado final (lo que la agencia se quedó). Pero esto es decisión de política de comisiones, no fiscal pura.

### 7.4 Multioperador en una reserva

`Riesgo:` una reserva Hotel típica tiene un solo operador, pero podría tener servicios adicionales con otros operadores (ej: transfer). Si el alcance Fase 1 es "solo Hotel", **el sistema debe rechazar cancelación FC1.3 sobre reservas con servicios no-Hotel**. Mensaje claro: "Esta reserva tiene servicios distintos a Hotel. Usar flujo legacy o esperar fases siguientes".

---

## 8. Riesgos integrados (donde fiscal + contable + negocio chocan)

| # | Riesgo | Por qué duele | Mitigación propuesta Fase 1 |
|---|---|---|---|
| R1 | Sistema emite NC parcial con monto = devuelto al cliente, **violando el criterio del contador** ("la NC refleja causa fiscal, no plata"). | El contador lo dijo textual: "no permitiría que el sistema diga NC = monto devuelto". | El sistema calcula los DOS montos por separado en la liquidación fiscal. UI obliga al admin ver ambos. Test obligatorio. |
| R2 | Items no reintegrables traídos como `DeductionLine` Operator confunde el balance. | `DeductionLine` es lo que retiene el operador en T2. Item no reintegrable es algo que **nunca se devuelve al cliente** desde T0 (es cargo conocido). Son cosas distintas. | Modelar `IsRefundable` en `InvoiceItem` / `HotelBooking`, **separado** del `DeductionLine`. |
| R3 | Modo facturación operador cambia entre emisión y cancelación. | Si emití factura con modo X y al cancelar el operador está en modo Y, el cálculo NC parcial diverge. | Persistir `InvoicingModeAtEvent` en `FiscalSnapshot`. Snapshot fiscal extendido. |
| R4 | Cliente Factura A se auto-emite NC parcial sin revisión. | Caso 8 matriz: Factura A es revisión manual obligatoria. | Estado `ManualReviewPending` automático cuando `OriginatingInvoice.TipoComprobante = 1`. |
| R5 | Plazo RG 4540 (15 días desde T2) se vence mientras BC está en `ManualReviewPending`. | Caemos en mora fiscal. NC fuera de plazo puede ser observada por ARCA. | Alerta automática "BC en revisión > 10 días". Reporte diario admin. Auto-escalado a manual contable si > 13 días. |
| R6 | Multioperador en una reserva: cancelación parcial Hotel queda inconsistente. | El operador del Hotel devuelve, los del transfer no. Sistema no sabe qué hacer con el resto. | Rechazo explícito Fase 1: solo Hotel puro. Si reserva tiene > 1 servicio con distinto `ProductType`, bloquear FC1.3. |
| R7 | Prorrateo IVA en NC parcial mal calculado. | Si IVA no se prorratea proporcional al neto acreditado, la base imponible queda inconsistente. | Reglas explícitas Fase 1 (definir cálculo) + test obligatorio. Confirmar con contador antes Fase 2. |
| R8 | Penalidad operador clasificada como `withholding` cuando es `administrativeFee`. | Confusión típica: el operador "retiene" plata pero NO es retención fiscal — es cargo propio. Si el sistema lo trata como withholding, le da crédito fiscal a la agencia que no corresponde. | FC1.2 ya tiene `DeductionKind` con `CancellationPenalty` separado de `IvaWithholding`. Mantener separación. Validar en tests. |
| R9 | Vendedor marca todo refundable=true por desidia, y el cargo administrativo termina devuelto. | El sistema acredita al cliente algo que no debía. | UI obliga marcar `IsRefundable` en items "sensibles" (seguro, gestión, anticipo no reembolsable). Default `false` para estos tipos preestablecidos. |
| R10 | Multimoneda + items no reintegrables → diferencia de cambio mal aplicada al monto fiscal. | NC USD * TC T0 = monto fiscal en ARS, **pero** lo devuelto al cliente puede tener TC T2 distinto. | Documentar explícitamente en `FiscalSnapshot` los TC usados en cada parte del cálculo. |

---

## 9. Datos requeridos por el sistema (campos mínimos)

Esta es la lista de **lo que necesito que persista el modelo** para que la operación sea fiscalmente correcta. Detallo nivel lógico (no SQL ni EF aún).

### 9.1 En `Supplier` (nuevo)

- `InvoicingMode`: enum `TotalToCustomer` (reseller) | `CommissionOnly` (intermediario). Default `TotalToCustomer` (conservador). Editable por admin.
- `PenaltyPolicyJson`: JSON con tabla de penalidades por antelación. Ejemplo:
  ```json
  {
    "tiers": [
      { "minDaysBefore": 60, "penaltyPercent": 0 },
      { "minDaysBefore": 30, "penaltyPercent": 10 },
      { "minDaysBefore": 15, "penaltyPercent": 30 },
      { "minDaysBefore": 0,  "penaltyPercent": 50 }
    ],
    "currency": "USD"
  }
  ```
  Nivel lógico — el formato exacto lo cerramos con `software-architect`.

### 9.2 En `InvoiceItem` (nuevo)

- `IsRefundable`: bool, default `true`. Cuando `false`, el ítem NO entra en monto fiscal acreditable de la NC parcial. **Inmutable** post-emisión de factura.
- `ItemCategory`: enum opcional `Service` | `AdministrativeFee` | `Insurance` | `OperatorAdvance` | `Penalty` | `Other`. Sirve para alertas UI (ej: si categoría es `AdministrativeFee` y `IsRefundable=true`, warn al vendedor).
- `SourceServicioReservaId`: FK nullable al `ServicioReserva` que originó esta línea (trazabilidad para Hotel: línea ↔ booking).

### 9.3 En `HotelBooking` (nuevo)

- `NonRefundableConceptsJson`: lista de conceptos adicionales no reintegrables (ej: cargo gestión $5.000, seguro cancelación $20.000) que se imputan al cliente fuera del costo neto/venta. Cada concepto entra como `InvoiceItem` con `IsRefundable=false` al facturar.

### 9.4 En `BookingCancellation` (extensión)

- `FiscalLiquidation`: owned value object con la **liquidación fiscal calculada**:
  - `OriginalInvoiceAmount`
  - `CancellationAmount` (en general = OriginalInvoiceAmount, podría diferir si cancelación parcial)
  - `OperatorPenaltyAmount`
  - `NonRefundableItemsAmount`
  - `FiscalAmountToCredit` = OriginalInvoiceAmount - NonRefundableItemsAmount - (penalty si aplica modo)
  - `AmountToRefundCustomer` (en general = FiscalAmountToCredit, podría diferir si retenciones del operador no se trasladan al cliente)
  - `FinalNetInvoiced` = OriginalInvoiceAmount - FiscalAmountToCredit
  - `ComputedAt`, `ComputedByUserId`, `ComputedRule` (case 1..8)
- `CreditNoteKind`: enum `PartialOnOriginal` | `TotalPlusNewInvoice`. Set por el clasificador automático, **modificable solo en revisión manual**.
- `ReviewRequiredReason`: enum bitflag `CustomerIsRiOrFacturaA` | `HasNonRefundableItems` | `AmountAboveAdminThreshold` | `AmountAboveAccountingThreshold` | `RetentionChangesNature` | `OriginalInvoiceUnclear` | `MultiCurrency` | `LegacyInvoice` | `Other`. Null si auto-procesado.
- `ManualReviewerUserId`, `ManualReviewedAt`, `ManualReviewDecision` (Approved/Rejected/RequiresRetry), `ManualReviewComment` (min 20 chars).
- `PartialCreditNoteApprovalRequestId`: FK a `ApprovalRequest` que aprueba la liquidación.

### 9.5 En `FiscalSnapshot` (extensión)

- `InvoicingModeAtEvent`: snapshot del modo del operador al momento T0. **Crítico** para que el cálculo NC parcial sea consistente con el contexto de emisión.
- `OriginalInvoiceTypeAtEvent`: redundante con `OriginatingInvoice.TipoComprobante` pero permite consultas sin join. Conveniencia.

### 9.6 En `OperationalFinanceSettings` (extensión)

- `PartialNcAutoApprovalThreshold` (default $500.000 ARS): por debajo de este monto, NC parcial se auto-emite si no hay otros disparadores manuales.
- `PartialNcAdminReviewThreshold` (default $2.000.000 ARS): entre auto y este monto, admin reforzada (admin distinto al vendedor + comentario obligatorio min 20 chars).
- `PartialNcAccountingReviewThreshold` (default null = sin límite superior): por encima de admin review, manual contable obligatorio (rol contador o equivalente, fuera de scope Fase 1 — usar admin con flag distinto).
- `PartialNcDescriptionTemplate` (string, default `"NC parcial s/Fc {invoiceNumber}. Concepto: {reason}. Monto fiscal acreditado: {fiscalAmount} {currency}."`). Editable por admin desde panel.
- `ManualReviewMaxDaysBeforeRg4540Alert` (default 10): días desde T2 después de los cuales se alerta al admin que el plazo RG 4540 está a punto de vencerse.

### 9.7 En `BookingCancellationStatus` (extensión enum)

- `RequiresManualReview = 8` — clasificador identificó caso que necesita revisión humana, pero todavía no se abrió el ticket de aprobación.
- `ManualReviewPending = 9` — `ApprovalRequest` abierto, esperando admin.
- `ManualReviewApproved = 10` — admin aprobó la liquidación. Siguiente paso: emitir NC (parcial o total según `CreditNoteKind`).
- `ManualReviewRejected = 11` — admin rechazó. BC vuelve a `Drafted` (con audit) o se aborta.

### 9.8 En `ApprovalRequestType` (extensión)

- `PartialCreditNoteApproval = 11` — admin aprueba la liquidación calculada (incluyendo override de penalidades si el admin las modificó).

### 9.9 En `Invoice` (extensión opcional, podríamos diferir a Fase 2)

- `IsPartialCreditNote`: bool, default `false`. Marca para queries / reportes contables. Solo aplica a NCs.
- `PartialAmountPercentage`: decimal (0..100), opcional. Para reporting "cuánto % se acreditó".

---

## 10. Maquina de estados del flujo NC parcial Fase 1

### 10.1 Nodos (estados)

Extiende `BookingCancellationStatus` actual con 4 estados nuevos **insertados antes** de `AwaitingFiscalConfirmation`:

```
Drafted (0)
   │
   │ confirmCancellation() — vendedor confirma con cliente
   │ + sistema calcula FiscalLiquidation
   │ + sistema clasifica caso (matriz 8)
   │
   ├── caso 2 o 6 (NC total simple) → AwaitingFiscalConfirmation (1) ──→ flujo FC1.2 vigente
   │
   ├── caso 1, 3, 5 + monto < auto threshold + sin disparadores manuales
   │   → AwaitingFiscalConfirmation (1) ──→ flujo FC1.2 vigente (con NC parcial en Fase 2)
   │
   └── cualquier disparador de revisión manual
       → RequiresManualReview (8)
            │
            │ submitForReview() — vendedor confirma envío a revisión
            │
            ▼
       ManualReviewPending (9) ←─── ApprovalRequest.PartialCreditNoteApproval abierto
            │
            │ approveLiquidation(comment) — admin aprueba
            │
            ▼
       ManualReviewApproved (10)
            │
            │ emitCreditNote() — automático tras approval
            │
            ▼
       AwaitingFiscalConfirmation (1) ──→ flujo FC1.2 vigente

       Brazo rechazo:
       ManualReviewPending (9)
            │
            │ rejectLiquidation(comment) — admin rechaza
            │
            ▼
       ManualReviewRejected (11)
            │
            │ resetToDraft() — vendedor corrige
            │
            ▼
       Drafted (0)  [con audit del rejection]

       Brazo abort:
       Cualquier estado de revisión manual + abort() = Aborted (6)
```

### 10.2 Transiciones tabuladas

| Estado origen | Trigger | Estado destino | Condiciones | Quien dispara |
|---|---|---|---|---|
| `Drafted` | `confirmCancellation()` (caso auto) | `AwaitingFiscalConfirmation` | Caso 2 o 6 OR (Caso 1/3/5 + monto < threshold + sin Factura A + sin items no reintegrables + sin multicurrency) | Vendedor con permiso `cobranzas.invoice_annul` |
| `Drafted` | `confirmCancellation()` (requiere review) | `RequiresManualReview` | Cualquier disparador `ReviewRequiredReason != Null` | Vendedor (sistema fuerza) |
| `Drafted` | `abort()` | `Aborted` | — | Vendedor o admin |
| `RequiresManualReview` | `submitForReview()` | `ManualReviewPending` | `FiscalLiquidation` no nula + `ApprovalRequest.PartialCreditNoteApproval` creado | Vendedor |
| `RequiresManualReview` | `abort()` | `Aborted` | — | Vendedor o admin |
| `ManualReviewPending` | `approveLiquidation(comment)` | `ManualReviewApproved` | Admin distinto al vendedor (4-eyes) + comentario min 20 chars + `ApprovalRequest` approved | Admin |
| `ManualReviewPending` | `rejectLiquidation(comment)` | `ManualReviewRejected` | Comentario min 20 chars | Admin |
| `ManualReviewPending` | `editLiquidation(...)` | `ManualReviewPending` (idem) | Admin reforzada: modifica `OperatorPenaltyAmount` o `NonRefundableItemsAmount` o `CreditNoteKind` con justificación | Admin |
| `ManualReviewApproved` | `emitCreditNote()` (automático) | `AwaitingFiscalConfirmation` | Inmediato post-approval, idempotente | Sistema (service) |
| `ManualReviewRejected` | `resetToDraft()` | `Drafted` | Auto inmediato post-rejection | Sistema |
| `ManualReviewRejected` | `abort()` | `Aborted` | — | Vendedor o admin |
| `AwaitingFiscalConfirmation` y siguientes | — | Igual que FC1.2 vigente | — | — |

### 10.3 Estados terminales

- `Closed` (igual FC1.2).
- `AbandonedByOperator` (igual FC1.2).
- `Aborted` (igual FC1.2).
- `ArcaRejected` (igual FC1.2).

### 10.4 Invariantes de la máquina de estados Fase 1

- **INV-FC1.3-001**: BC no puede pasar a `AwaitingFiscalConfirmation` directamente si `FiscalLiquidation.ReviewRequiredReason != Null`. Debe pasar por `ManualReviewPending` + `ManualReviewApproved`. **No admite override.**
- **INV-FC1.3-002**: `ManualReviewPending` requiere `ApprovalRequest.PartialCreditNoteApproval` abierto. **No admite override.**
- **INV-FC1.3-003**: `ManualReviewApproved` requiere `ApprovalRequest` en estado `Approved`. **No admite override.**
- **INV-FC1.3-004**: `approveLiquidation()` requiere que el admin sea distinto al vendedor que cargó el BC (`DraftedByUserId != approverUserId`). 4-eyes. **No admite override.**
- **INV-FC1.3-005**: `FiscalLiquidation.FiscalAmountToCredit + FiscalLiquidation.NonRefundableItemsAmount + FiscalLiquidation.OperatorPenaltyAmount = OriginalInvoiceAmount` (con tolerancia $0.01 por redondeo). **No admite override.**
- **INV-FC1.3-006**: Items con `IsRefundable=false` en la `OriginatingInvoice` deben sumar exactamente `FiscalLiquidation.NonRefundableItemsAmount`. **No admite override.**
- **INV-FC1.3-007**: BC FC1.3 solo se acepta si todos los `Servicios` de la reserva tienen `ProductType = "Hotel"`. **Admite override de admin** con justificación 50+ chars (en caso de servicios mixtos puros Hotel donde algo está mal clasificado).
- **INV-FC1.3-008**: `FiscalLiquidation.CreditNoteKind` no cambia tras `ManualReviewApproved`. Si admin lo cambió durante revisión, queda fijo. **No admite override.**

---

## 11. Cierre de las 4 preguntas abiertas

### 11.1 ¿Cómo detecta el sistema "cambia naturaleza fiscal del retenido"? (caso 7 matriz)

**Propuesta**: combinar 3 señales detectables sin intervención humana, más una manual.

Señales automáticas:

1. **`DeductionKind` Mix Heterogéneo**: si la suma de `DeductionLine` de tipo `IvaWithholding`/`IvaPerception`/`IIBBWithholding`/`IIBBPerception` (retenciones fiscales) > 0 **Y** suma de `CancellationPenalty`/`AdministrativeFee` > 0 → flag "mixed nature".
2. **`InvoicingMode` mismatch**: si `Supplier.InvoicingMode` actual ≠ `FiscalSnapshot.InvoicingModeAtEvent` → flag "mode changed".
3. **`Customer.TaxCondition` mismatch**: si `Customer.TaxConditionId` actual ≠ `FiscalSnapshot.CustomerTaxConditionAtEvent` → flag "customer condition changed".

Señal manual:

4. **Checkbox vendedor**: "¿La retención tiene naturaleza distinta a la factura original?" — opcional, default false.

Si alguna señal es positiva → `ReviewRequiredReason |= RetentionChangesNature` → `ManualReviewPending`.

`Fundamento`: el contador identificó este caso como ambiguo y dijo "depende del concepto retenido". El sistema NO puede decidir por sí solo en estos casos — la propuesta es **detectar el caso y mandarlo a revisión**, NO auto-clasificarlo. RG 4540 no resuelve este caso explícitamente — es práctica fiscal.

`Necesita confirmación profesional:` validar con el contador estas 3 señales automáticas + si la lista cubre lo que él considera "cambio de naturaleza". Probable que sumemos más.

### 11.2 ¿Cómo detecta "factura original confusa"? (caso 4 matriz)

**Propuesta**: heurísticas conservadoras + auto-flag.

Heurísticas automáticas:

1. **InvoiceItem genérico único**: si la factura original tiene 1 sola línea con `Description` que coincide regex `/^(servicio|concepto|importe|operacion|reserva)/i` (lista configurable en `OperationalFinanceSettings.GenericDescriptionPatterns`) → flag "generic single line".
2. **Items sin `SourceServicioReservaId`**: si > 50% de `Total` de la factura tiene items sin FK al `ServicioReserva` → flag "items orphan".
3. **Discriminación IVA inconsistente**: si la suma de `InvoiceItem.ImporteIva` no cuadra con `Invoice.ImporteIva` (tolerancia $0.50) → flag "vat mismatch".
4. **Facturas legacy pre-FC1.3**: si la factura fue emitida antes de la fecha de deploy FC1.3 (setting `Fc13DeployDate`) → flag "legacy invoice" (porque no garantizamos que el modelo viejo discriminó bien).

Señal manual:

5. **Checkbox vendedor**: "La factura original no discrimina conceptos claramente" — opcional.

Si alguna señal positiva → `ReviewRequiredReason |= OriginalInvoiceUnclear` → `ManualReviewPending` con caso 4 sugerido (NC total + nueva factura).

`Fundamento`: el contador habló de "factura confusa o mal discriminada" como criterio. Imposible de cuantificar al 100% sin reglas. Las heurísticas atrapan los casos típicos; el resto va a revisión humana.

`Riesgo:` falsos positivos (facturas viejas bien hechas que el sistema flagea como "legacy"). Mitigación: el admin puede aprobar igual con justificación.

### 11.3 ¿Factura A siempre va a revisión manual? (caso 8 matriz)

**Respuesta cerrada: SÍ.**

Justificación:

- El contador lo dijo textual: "Factura A / RI / caso sensible — revisión manual obligatoria".
- Factura A (`Invoice.TipoComprobante = 1`) implica cliente RI con derecho a cómputo de crédito fiscal de IVA. Una NC parcial mal calculada le rompe el cómputo al cliente y nos expone a reclamo.
- El monto fiscal es discutible (prorrateo IVA, claridad de conceptos retenidos) y la consecuencia de error es grave.

Implementación: cualquier BC cuya `OriginatingInvoice.TipoComprobante = 1` → `ReviewRequiredReason |= CustomerIsRiOrFacturaA` → directo a `RequiresManualReview` sin importar el monto.

**No hay condiciones de auto-emisión para Factura A en Fase 1.** Si en el futuro queremos automatizar casos triviales (NC total por cancelación 100% sin retenciones sobre Factura A), lo discutimos como Fase 2+ con el contador.

`Necesita confirmación profesional:` solo confirmar que el contador no tiene casos triviales de Factura A que quiera automatizar.

### 11.4 ¿Mensaje en NC parcial: parametrizable o hardcodeado?

**Respuesta cerrada: parametrizable**, con template default + variables sustituibles.

Justificación:

- Distintos contadores prefieren distinto wording legal.
- ARCA permite cualquier descripción razonable en el detalle de la NC.
- Hardcodearlo nos obliga a deploy cada vez que el contador quiere cambiar el texto.

Implementación:

- Setting `OperationalFinanceSettings.PartialNcDescriptionTemplate` (string editable desde panel admin).
- Default sugerido: `"NC parcial s/Fc {invoiceType} {invoiceNumber} (PV {pointOfSale}). Monto fiscal acreditado: {fiscalAmount} {currency}. Concepto: {cancellationReason}. Items no reintegrables retenidos: {nonRefundableAmount} {currency}."`.
- Variables soportadas (fijas, validadas al guardar setting): `{invoiceType}`, `{invoiceNumber}`, `{pointOfSale}`, `{fiscalAmount}`, `{currency}`, `{cancellationReason}`, `{nonRefundableAmount}`, `{operatorPenaltyAmount}`, `{customerName}`, `{customerTaxId}`.
- Validación al guardar template: rechazar si referencia variable no soportada (typo protection).

`Necesita confirmación profesional:` ver con contador qué wording prefiere por default y si quiere distintos templates por tipo de comprobante (A vs B vs C).

---

## 12. Casos de prueba explícitos (cubrir matriz 8 + escenarios A y B)

### 12.1 Caso 1 — Factura total + devolución parcial sin penalidad

**Input**:
- `OriginatingInvoice`: $1.000.000 (Factura C tipo 11, cliente Consumidor Final, monotributo agencia).
- `Supplier.InvoicingMode = TotalToCustomer`.
- Vendedor cancela. Cliente quiere devolver 50% por cambio de plan. Sin penalidad operador. Sin items no reintegrables.
- Liquidación: `OriginalInvoiceAmount=1.000.000`, `CancellationAmount=500.000` (cancelación parcial), `OperatorPenalty=0`, `NonRefundable=0`.

**Estado intermedio**: clasificador → caso 1 (NC parcial sobre factura total). Monto $500.000 < threshold $500k (asumo $500k es **límite superior** estricto < no >, ver §14 ambigüedad). Si entra en auto: `AwaitingFiscalConfirmation` directo.

**Output esperado**:
- `FiscalLiquidation.FiscalAmountToCredit = 500.000`.
- `FiscalLiquidation.AmountToRefundCustomer = 500.000`.
- `FiscalLiquidation.FinalNetInvoiced = 500.000`.
- `CreditNoteKind = PartialOnOriginal`.
- NC esperada: monto $500.000, tipo igual a `OriginatingInvoice.TipoComprobante` (NC tipo asociado), `OriginalInvoiceId` apunta a factura origen.
- `BC.Status` final: `AwaitingFiscalConfirmation`.

### 12.2 Caso 2 — Factura total + cancelación 100% sin retenciones

**Input**:
- Factura $300.000 (tipo 11 Monotributo).
- Cancelación 100%. Sin penalidad. Sin items no reintegrables.

**Estado intermedio**: clasificador → caso 2 (NC total). Auto.

**Output esperado**:
- `FiscalLiquidation.FiscalAmountToCredit = 300.000`.
- `FiscalLiquidation.NonRefundable = 0`.
- `FiscalLiquidation.FinalNetInvoiced = 0`.
- `CreditNoteKind = PartialOnOriginal` (porque acreditamos el 100% — el contador llama a esto "NC total" pero técnicamente es la misma NC vinculada a la factura, solo que cubre el 100% del monto. **No es NC total + nueva factura** — eso es `TotalPlusNewInvoice`).
- NC esperada: monto $300.000.
- `BC.Status` final: `AwaitingFiscalConfirmation`.

`Aclaración importante`: la matriz del contador usa "NC total" en dos sentidos distintos:
- Caso 2 "NC total" = NC por el 100% del monto facturado (vínculo a factura). Acción técnica: una sola NC.
- Caso 4/7 "NC total + nueva factura" = NC por el 100% **y emitir factura nueva** por el retenido. Dos comprobantes.

Mi enum `CreditNoteKind` los distingue:
- `PartialOnOriginal` cubre caso 1, 2, 3, 5, 6 (siempre una sola NC).
- `TotalPlusNewInvoice` cubre caso 4, 7 (NC + nueva factura).

### 12.3 Caso 3 — Factura total + penalidad operador + fees válidos

**Input**:
- Factura $800.000 (tipo 11 Monotributo).
- Penalidad operador $100.000 (modo reseller: la penalidad reduce el monto fiscal acreditable porque el operador se quedó con esa parte).
- Sin items no reintegrables.

**Estado intermedio**: caso 3 (NC parcial, neto facturado por retenido).

**Output esperado**:
- `FiscalLiquidation.FiscalAmountToCredit = 700.000` ($800k - $100k penalidad).
- `FiscalLiquidation.AmountToRefundCustomer = 700.000`.
- `FiscalLiquidation.FinalNetInvoiced = 100.000`.
- `CreditNoteKind = PartialOnOriginal`.
- NC esperada: $700.000.
- `BC.Status` final: `AwaitingFiscalConfirmation` (auto, asumiendo monto < threshold).

`Riesgo`: si monto > $500k threshold → `ManualReviewPending` (admin reforzada). Test debe cubrir AMBOS umbrales.

`Pregunta abierta para Gaston/contador:` cuando modo es `CommissionOnly` y penalidad es del operador, ¿la penalidad reduce el monto a acreditar al cliente de la comisión, o sigue por separado? Hipótesis Fase 1: la penalidad del operador NO afecta la NC de comisión, solo afecta la cuenta corriente con operador. Confirmar.

### 12.4 Caso 4 — Factura original confusa / mal discriminada

**Input**:
- Factura $500.000 (tipo 11) con 1 sola línea descripción "Servicio turístico".
- Cancelación 100%.

**Estado intermedio**: heurística "Generic single line" dispara → `ReviewRequiredReason |= OriginalInvoiceUnclear` → `RequiresManualReview` → `ManualReviewPending`.

**Output esperado pre-aprobación**:
- `FiscalLiquidation.FiscalAmountToCredit = 500.000` (propuesta default).
- `FiscalLiquidation.CreditNoteKind = TotalPlusNewInvoice` (propuesta del clasificador).
- `BC.Status` = `ManualReviewPending`.
- `ApprovalRequest` tipo `PartialCreditNoteApproval` abierto.

**Output post-aprobación admin**:
- Admin elige `CreditNoteKind = TotalPlusNewInvoice` (NC total + factura nueva por $0 — pero como cancelación 100%, no hay nueva factura realmente).
- NC esperada: $500.000.
- `BC.Status` = `ManualReviewApproved` → `AwaitingFiscalConfirmation`.

### 12.5 Caso 5 — Solo comisión + devuelve parte

**Input**:
- `Supplier.InvoicingMode = CommissionOnly`.
- Factura comisión $50.000 (tipo 11).
- Cliente cancela 60% del servicio. Comisión "perdida" = $30.000 (60% de la comisión).

**Estado intermedio**: caso 5 (NC parcial sobre comisión). Auto si monto < threshold.

**Output esperado**:
- `FiscalLiquidation.FiscalAmountToCredit = 30.000`.
- `FiscalLiquidation.FinalNetInvoiced = 20.000`.
- `CreditNoteKind = PartialOnOriginal`.
- NC esperada: $30.000.
- `BC.Status` final: `AwaitingFiscalConfirmation`.

### 12.6 Caso 6 — Solo comisión + devuelve toda

**Input**:
- `Supplier.InvoicingMode = CommissionOnly`.
- Factura comisión $40.000.
- Cliente cancela 100%. Comisión devuelta entera.

**Estado intermedio**: caso 6 (NC total sobre comisión). Auto.

**Output esperado**:
- `FiscalLiquidation.FiscalAmountToCredit = 40.000`.
- `CreditNoteKind = PartialOnOriginal` (una NC cubriendo 100%).
- NC esperada: $40.000.
- `BC.Status`: `AwaitingFiscalConfirmation`.

### 12.7 Caso 7 — Cambia naturaleza fiscal del retenido

**Input**:
- Factura $1.500.000 (tipo 11).
- Operador retiene $300.000 inicialmente como "cargo administrativo" (`AdministrativeFee`) pero también suma $50.000 `IvaWithholding`. Mix heterogéneo.

**Estado intermedio**: señal "DeductionKind Mix Heterogéneo" dispara → `ReviewRequiredReason |= RetentionChangesNature` → `ManualReviewPending`.

**Output esperado pre-aprobación**:
- Liquidación propuesta: `FiscalAmountToCredit = 1.150.000` (1.500.000 - 300.000 - 50.000).
- `CreditNoteKind` propuesto: `TotalPlusNewInvoice` (caso 7).
- `BC.Status`: `ManualReviewPending`.

**Output post-aprobación**: admin valida la propuesta, elige NC total + factura nueva por $350.000. BC → `ManualReviewApproved` → `AwaitingFiscalConfirmation`.

### 12.8 Caso 8 — Factura A (cliente RI)

**Input**:
- `OriginatingInvoice.TipoComprobante = 1` (Factura A, cliente RI). Monto $700.000.
- Cancelación 100%, sin penalidad, sin items no reintegrables.

**Estado intermedio**: regla "Factura A siempre revisión" → `ReviewRequiredReason |= CustomerIsRiOrFacturaA` → `ManualReviewPending`. **Sin importar el monto.**

**Output esperado pre-aprobación**:
- Liquidación propuesta: `FiscalAmountToCredit = 700.000`.
- `CreditNoteKind` propuesto: `PartialOnOriginal` (cubriendo 100%).
- `BC.Status`: `ManualReviewPending`.

**Output post-aprobación**: admin valida. NC tipo 3 (NC de A) por $700.000. `BC.Status`: `AwaitingFiscalConfirmation`.

### 12.9 Escenario A del contador (Factura A, penalidad, item no reintegrable)

**Input** (textual del contador):
- Cliente paga $1.000.000 con Factura A (tipo 1, cliente RI).
- Cancela con 20 días de antelación.
- Operador retiene $200.000 de penalidad.
- 1 item no reintegrable: $50.000.

**Estado intermedio**:
- Liquidación: `OriginalInvoiceAmount=1.000.000`, `OperatorPenalty=200.000`, `NonRefundableItems=50.000`, `FiscalAmountToCredit=750.000`, `AmountToRefundCustomer=750.000`, `FinalNetInvoiced=250.000`.
- `ReviewRequiredReason = CustomerIsRiOrFacturaA | HasNonRefundableItems` (al menos 2 disparadores).
- `ManualReviewPending` obligatorio.

**Output post-aprobación**:
- Admin confirma `CreditNoteKind = PartialOnOriginal`, monto $750.000.
- NC esperada: tipo 3 (NC de A), monto $750.000, `OriginalInvoiceId` apunta a la Factura A.
- Audit: `ApprovalRequest.PartialCreditNoteApproval` aprobado por admin distinto al vendedor, comentario min 20 chars.
- `BC.Status`: `AwaitingFiscalConfirmation` → flujo FC1.2 sigue.

### 12.10 Escenario B del contador (solo comisión, retención)

**Input**:
- `Supplier.InvoicingMode = CommissionOnly`.
- Factura comisión $100.000 (tipo 1 Factura A — cliente RI).
- Operador retiene $50.000.

**Estado intermedio**:
- Liquidación: `OriginalInvoiceAmount=100.000`, `OperatorPenalty=50.000` (asumiendo penalidad = retención), `NonRefundableItems=0`, `FiscalAmountToCredit=50.000`, `AmountToRefundCustomer=50.000`, `FinalNetInvoiced=50.000`.
- `ReviewRequiredReason = CustomerIsRiOrFacturaA` (porque es Factura A).
- `ManualReviewPending` obligatorio.

**Output post-aprobación**:
- Admin confirma NC parcial $50.000 sobre la comisión.
- `CreditNoteKind = PartialOnOriginal`.
- `BC.Status`: `AwaitingFiscalConfirmation`.

### 12.11 Casos negativos (tests de invariantes)

- **INV-FC1.3-001 violado**: intento pasar BC con `ReviewRequiredReason != Null` a `AwaitingFiscalConfirmation` sin aprobación → `BusinessInvariantViolationException` (HTTP 409).
- **INV-FC1.3-004 violado**: mismo usuario que cargó BC intenta aprobar liquidación → rechaza (4-eyes).
- **INV-FC1.3-005 violado**: liquidación con `FiscalAmountToCredit + NonRefundable + Penalty ≠ Original` → rechaza al guardar (CHECK SQL).
- **INV-FC1.3-007 violado**: cancelación FC1.3 sobre reserva con `Servicios` mixtos (Hotel + Vuelo) sin override → rechaza.
- **`OnePerReservaInvoicePolicy` violado**: cancelación sobre reserva con 2+ facturas activas → rechaza (INV-100 ya existe).

---

## 13. Criterios de aceptación (verificables como tests)

1. Dada una reserva Hotel pura con factura $300k, cancelación 100% sin penalidad ni items no reintegrables, **cuando** vendedor confirma cancelación, **entonces** BC pasa a `AwaitingFiscalConfirmation` automáticamente con `FiscalLiquidation.FiscalAmountToCredit=300.000`, `CreditNoteKind=PartialOnOriginal`, `ReviewRequiredReason=Null`.
2. Dada una reserva Hotel pura con factura $600k, cancelación 100% sin items, **cuando** vendedor confirma, **entonces** BC pasa a `ManualReviewPending` por superar threshold $500k (admin reforzada).
3. Dada una reserva con factura $1.000.000 Factura A, **cuando** vendedor confirma cancelación con cualquier composición, **entonces** BC va a `ManualReviewPending` con `ReviewRequiredReason` incluyendo `CustomerIsRiOrFacturaA`.
4. Dada una reserva con item `IsRefundable=false` por $50k, **cuando** vendedor calcula liquidación, **entonces** `FiscalLiquidation.NonRefundableItemsAmount = 50.000` y `FiscalAmountToCredit` excluye ese monto.
5. Dada una reserva con `Servicios` que incluyen Hotel + Vuelo, **cuando** vendedor intenta abrir BC FC1.3, **entonces** rechazo con mensaje "FC1.3 Fase 1 solo soporta reservas 100% Hotel".
6. Dado un BC en `ManualReviewPending`, **cuando** el mismo usuario que lo creó intenta aprobar liquidación, **entonces** rechazo con `BusinessInvariantViolationException` código `INV-FC1.3-004`.
7. Dado un BC en `ManualReviewApproved`, **cuando** se invoca `emitCreditNote()`, **entonces** BC pasa a `AwaitingFiscalConfirmation`, `Invoice.AnnulmentApprovalRequestId` = id del PartialCreditNoteApproval aprobado, audit `BookingCancellationManualReviewApproved` registrado.
8. Dado un BC con `Supplier.InvoicingMode` cambiado tras la emisión de factura, **cuando** se calcula liquidación, **entonces** `FiscalSnapshot.InvoicingModeAtEvent` mantiene el valor original y el cálculo usa ese valor (no el actual).
9. Dado el setting `PartialNcDescriptionTemplate` con variable inexistente `{foo}`, **cuando** admin guarda setting, **entonces** rechazo con mensaje "variable `{foo}` no soportada".
10. Dado un BC en `ManualReviewPending` durante 11 días sin aprobación (umbral default 10), **cuando** corre job nocturno de alertas, **entonces** se emite notificación al admin "BC X en revisión + ventana RG 4540 a punto de vencer".
11. Dada una liquidación con `FiscalAmountToCredit + NonRefundable + Penalty ≠ Original` (ej: por bug en cálculo), **cuando** se persiste, **entonces** la BD rechaza con CHECK SQL violado (HTTP 409).
12. Dado un BC con `OriginatingInvoice.OriginalInvoiceId` ya apuntando (factura original es a su vez una NC en cadena — caso raro), **cuando** se confirma cancelación, **entonces** `ReviewRequiredReason |= Other` y BC va a `ManualReviewPending` con comentario auto "factura origen ya es NC, requiere revisión".
13. Dado un BC en `RequiresManualReview` que el vendedor decide abortar, **cuando** se invoca `abort()`, **entonces** BC pasa a `Aborted`, sin emitir NC, sin afectar `Invoice.AnnulmentStatus`.

---

## 14. Citas normativas

- **RG 4540 ARCA** (vínculo NC↔factura via `<CbtesAsoc>`): cubierto en `AfipService.cs:827-838`. Plazo 15 días desde "hecho documentable" — política FC1.2 fijó "hecho documentable" = T2 (acuerdo de devolución operador).
- **Ley 25.345**: bancarización para refunds físicos sobre umbral. Cubierto en `OperationalFinanceSettings.Ley25345ThresholdAmount` (default $1.000.000). FC1.3 no cambia.
- **Decreto 953/2024**: ARCA es continuadora de AFIP. Sin impacto operativo para FC1.3 Fase 1.

`No verificado:` vigencia actual de RG 4540 + alícuotas IVA actuales + umbral Ley 25.345 vigente. Validar con fuente oficial ARCA/Boletín Oficial/contador antes de Fase 2.

---

## 15. Necesita confirmación profesional

1. **Prorrateo IVA en NC parcial**: confirmar si es proporcional al neto acreditado o ítem por ítem cuando la factura discriminó bien (impacto Fase 2).
2. **Tratamiento penalidad operador en modo `CommissionOnly`**: ¿reduce monto NC al cliente o solo afecta cuenta corriente con operador?
3. **Casos triviales de Factura A** que el contador quiera automatizar (NC total por cancelación 100% sin retenciones). Hoy asumido NO.
4. **Wording default del template `PartialNcDescriptionTemplate`** + si quiere distintos por tipo de comprobante.
5. **3 señales automáticas de "cambia naturaleza fiscal del retenido"** (caso 7): validar que cubren los casos reales que ve en la práctica.
6. **Heurísticas "factura original confusa"** (caso 4): validar que no producen demasiados falsos positivos.
7. **Umbrales monetarios** (auto $500k / admin $2M / contable >$2M): confirmar los valores son razonables para MagnaTravel.
8. **Comportamiento al vencer plazo RG 4540** con BC en `ManualReviewPending`: ¿alerta + auto-aprobar?, ¿alerta + bloquear?, ¿alerta + escalar a contador externo?

---

## 16. No verificado / pendiente actualizar

### 16.1 Inconsistencia detectada en el system prompt del agente

El system prompt me dice (sección "Project-specific context (MagnaTravel)" / "Para FC1.3 estan cerradas estas 4 decisiones adicionales (2026-05-21)"):

> Y queda **1 pregunta abierta** al contador real:
> - ¿NC parcial sobre factura original vs NC total + nueva factura por retenido? Criterio fiscal.

**Esto está desactualizado.** La memoria del usuario (`MEMORY.md` líneas "CRITERIO CONTADOR NC PARCIAL (2026-05-21)" y "5 preguntas Hotel (2026-05-19, RESPONDIDAS 2026-05-21)") y el doc `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md` confirman que **el contador YA respondió** ese criterio hoy con la matriz 8 casos + 2 escenarios + estructura pantalla liquidación.

Acción sugerida: actualizar `.claude/agents/travel-agency-accountant-argentina.md` línea "Y queda 1 pregunta abierta" para reflejar que la pregunta ya fue respondida y el plan ahora **implementa** ese criterio.

### 16.2 Otras pendientes

- No verifiqué que existe service `IOperationalFinanceSettingsService` con un método `GetEntityAsync(ct)` aunque el `BookingCancellationService.cs:90` lo usa — asumo existe.
- No verifiqué que `ApprovalRequest` admite `EntityType="BookingCancellation"` con `EntityId=int` para el approval del partial NC — asumo que sí (FC1.2 lo usa para `InvariantOverride`).
- No verifiqué que existen permissions `cancellations.partial_nc_approve` o equivalente — hay que sumar al seeding de roles en `software-architect`.

---

## 17. Interacción necesaria con otros agentes

### 17.1 Para `software-architect` (siguiente paso)

Mi output cubre el **QUÉ** fiscal/contable/negocio. Lo que **no decido y le paso al architect**:

- **Bounded context y nombres**: ¿se llama `FiscalLiquidation` o `CancellationFiscalLedger`? ¿Owned VO de `BookingCancellation` o entidad propia? Mi recomendación: owned VO (similar a `FiscalSnapshot`), pero el architect tiene la palabra final.
- **Cómo se persiste `Supplier.PenaltyPolicyJson`**: JSON column de Postgres vs tabla normalizada `SupplierPenaltyTier`. Trade-off rendimiento/queryability.
- **Servicio nuevo `PartialCreditNoteCalculatorService`** vs método en `BookingCancellationService.ConfirmAsync`. Mi instinto: extraer servicio con interface `IFiscalLiquidationCalculator` para testear el clasificador (matriz 8 casos) aislado.
- **Dispatcher / orquestación de aprobaciones**: ¿se aprovecha el `ApprovalRequestService` existente con tipo nuevo `PartialCreditNoteApproval`, o se introduce un módulo de "review queue" separado? FC1.2 ya tiene patrón reutilizable.
- **Migraciones EF**: orden y dependencias. Mi orden tentativo: (1) `Supplier.InvoicingMode` + `PenaltyPolicyJson`, (2) `InvoiceItem.IsRefundable` + `ItemCategory` + `SourceServicioReservaId`, (3) `OperationalFinanceSettings` nuevos thresholds + template, (4) `BookingCancellationStatus` nuevos valores + CHECK constraint, (5) `BookingCancellation.FiscalLiquidation` owned VO, (6) `FiscalSnapshot.InvoicingModeAtEvent` + `OriginalInvoiceTypeAtEvent`, (7) `ApprovalRequestType.PartialCreditNoteApproval` + seed, (8) índices y CHECK constraints.
- **CHECK SQL para INV-FC1.3-005** (suma componentes = original) — definir tolerancia exacta de redondeo.
- **Feature flag**: ¿FC1.3 entra detrás de un flag nuevo `EnablePartialCreditNotes` o detrás del existente `EnableNewCancellationFlow`? Mi recomendación: flag nuevo, para que Fase 1 puedan mergearse sin abrir NC parcial real en prod.

### 17.2 Para `arca-tax-expert-argentina`

No necesario para Fase 1 si el contador ya firmó la matriz. Sí lo necesitaremos para Fase 2 (emisión real de NC parcial al AFIP) para validar:

- Prorrateo IVA exacto.
- Comportamiento `<CbtesAsoc>` cuando hay múltiples NCs sobre la misma factura origen (en cadena).
- Tipo de comprobante NC correcto por tipo de factura origen (mapping 1→3, 6→8, 11→13 estándar).

### 17.3 Para `accounting-expert-argentina`

No necesario para Fase 1 — los asientos son manuales (decisión ADR-002 §2.12). Sí para definir las columnas del export CSV/Excel que enriquezca con `FiscalLiquidation` desglosada.

### 17.4 Para `travel-agency-domain-expert`

No necesario — el alcance Hotel y las 4 decisiones FC1.3 ya están cerradas.

### 17.5 Para `security-data-risk-reviewer`

Sí, como reviewer adicional cuando `backend-dotnet-senior` implemente. Tocamos cancelaciones + invoices + refunds + approvals.

### 17.6 Para `qa-automation-senior`

Sí, para definir los 13 criterios de aceptación de §13 como tests E2E sobre `PostgresIntegrationFixture` (patrón FC1.2 ya establecido).

---

## 18. Nuevas preguntas detectadas (a cerrar antes de implementar)

### 18.1 Fiscales (al contador real)

- **F1**: ¿el threshold $500k auto / $2M admin es **estricto menor** o **menor igual**? Detalle de borde, pero hay que definir.
- **F2**: cuando el operador devuelve en **cuotas** (regla 12 policy), ¿la NC parcial se emite por el total fiscal a acreditar en T0, o se va emitiendo NCs parciales sucesivas a medida que llega plata? Mi propuesta default: una sola NC en T0 por el total fiscal a acreditar (independiente de los flujos de plata). Validar.
- **F3**: si una NC parcial ya emitida necesita corrección posterior (admin se equivocó), ¿se emite ND complementaria, o se anula la NC original y se emite una nueva NC parcial corregida? Impacto fiscal serio. Hoy ADR-002 dice "no ND en flow normal" — FC1.3 puede cambiar eso.

### 18.2 De negocio (a Gaston)

- **G1**: ¿el vendedor puede marcar `IsRefundable=false` por ítem en pantalla, o se preselecciona automáticamente para items de categorías específicas (Insurance, AdministrativeFee, OperatorAdvance)? Mi propuesta: preselección automática + permite override del vendedor con confirmación.
- **G2**: ¿la "bandeja de pendientes" del Admin (decisión D3 2026-05-19) es vista nueva o aprovechamos el módulo existente de `ApprovalRequestsController`? Probablemente lo segundo, con filtro por tipo `PartialCreditNoteApproval`.
- **G3**: ¿qué pasa si el admin que aprueba la liquidación quiere modificar la `OperatorPenaltyAmount` antes de aprobar (porque el operador mandó nueva info)? Mi propuesta: `editLiquidation(...)` permite ajuste, queda audit, requiere comentario distinto.
- **G4**: ¿el cliente RI debe recibir una **ND complementaria** por el neto facturado final ($250k en Escenario A), o esa parte sigue cubierta por la Factura A original sin modificación? Hoy ADR-002 dice "no ND" — esto puede chocar con cliente RI que necesita claridad para crédito fiscal (caso 7 excepciones del contador).
- **G5**: ¿hay un rol "contador" o "revisor contable" distinto al "admin" para los casos > $2M (manual contable)? Hoy no veo ese rol en el repo. Si no existe, el `PartialNcAccountingReviewThreshold` queda sin sujeto. Definir.
- **G6**: ¿la comisión del vendedor se calcula sobre `FinalNetInvoiced` (mi propuesta) o sobre `OriginalInvoiceAmount - AmountToRefundCustomer`? En casos sin items no reintegrables coinciden; en casos con items la diferencia importa.

---

## 19. Auto-crítica honesta

1. **¿Verifiqué todo en el repo?** Sí — leí ADR-002 completo, Invoice/InvoiceItem actuales, BookingCancellation, OperatorRefundReceived, OperatorRefundAllocation, DeductionLine, ClientCreditEntry, ClientCreditWithdrawal, FiscalSnapshot, HotelBooking, ServicioReserva, Reserva, Customer, Supplier, OperationalFinanceSettings, ApprovalRequestType, AfipService líneas 800-900, plan táctico FC1.2, doc explicativo 2026-05-21.
2. **¿Asumí algo sin decirlo?** El prorrateo de IVA proporcional al neto (suposición §4.2). El tratamiento de penalidad en modo `CommissionOnly` (suposición §12.3 caso 3). La detección de "Factura A" via `Invoice.TipoComprobante = 1` (vs Cliente RI). Estos están marcados como "necesita confirmación".
3. **¿Diseñé arquitectura técnica?** Intenté NO. Marqué explícitamente §17.1 lo que le paso al `software-architect`. Lo que sí diseñé es **modelo de datos lógico**, **máquina de estados**, **invariantes**, **casos de prueba**, **criterios aceptación** — eso es alcance del agente integrado.
4. **¿Estoy seguro de la matriz?** Sí — copié textual del doc del contador. Verifiqué que la matriz cubre los 8 casos sin solapes. Caso 8 (Factura A) gana sobre cualquier otro caso.
5. **¿Edge cases que me faltan?**
   - Cancelación parcial donde queda saldo a favor del cliente (FC1.2 modela `ClientCreditEntry`) + nueva venta posterior sobre la misma reserva. ¿Aplica NC parcial sobre la factura del saldo a favor? Lo dejé fuera de Fase 1.
   - Caso de NC en cadena (factura origen es a su vez una NC). Lo levanté en caso de prueba 12.12 como "Other" → manual.
   - Caso `Voucher` ya emitido y revocado: voucher no es comprobante fiscal, pero el flujo puede dejar voucher activo si vendedor olvida revocar. Hoy FC1.2 ya maneja revocación de vouchers automática — confirmar que sigue activa en FC1.3.
6. **¿Tests cubren los invariantes?** Sí, §12.11 lista los negativos. Cada `INV-FC1.3-XXX` tiene su test correspondiente.
7. **¿Riesgo de regresión sobre FC1.2?** Bajo si los nuevos estados (`RequiresManualReview`..`ManualReviewRejected`) se insertan antes de `AwaitingFiscalConfirmation` sin cambiar transiciones existentes. La máquina FC1.2 queda intacta para los casos auto (caso 1/2/3/5/6 con monto bajo y sin disparadores). Hay que validar tests FC1.2 verdes después de migraciones.
8. **¿Reviewer podría rechazar?** Puntos débiles:
   - El threshold $500k es **mi propuesta** — no está fijado por el contador. Si Gaston/contador prefiere $1M auto, hay que ajustar.
   - El template `PartialNcDescriptionTemplate` con variables tipo `{foo}` es decisión de software design, pero la mecanización (qué variables soportar) la propuse yo. Confirmar con contador wording final.
   - Las heurísticas de "factura original confusa" (caso 4) son arbitrarias. Probable que el contador agregue/quite reglas.
   - El flujo Fase 1 NO emite NC al ARCA real — eso queda para Fase 2. Confirmar con Gaston que esto es aceptable scope split.
9. **¿Mantenible por otros?** Sí — sigo el patrón ADR-002, el plan táctico FC1.2 v3, el lenguaje rioplatense + ejemplos pelotudos, y nomenclatura consistente (FC1.3 = misma familia que FC1.2).
10. **¿Algo importante que se me escapó?** Falta detalle de cómo se notifica al admin que hay un BC en `ManualReviewPending` (email? push? badge en navbar?). Probablemente lo resuelve la implementación existente de `ApprovalRequestService` (FC1.2 ya emite notificaciones). Lo doy por cubierto pero podría faltar polish UX.

---

## 20. Trazabilidad

- Doc explicativo contador: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md`.
- ADR-002 base: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-002-cancellation-refund.md`.
- Plan táctico FC1.2 v3: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-2.md`.
- Entidades existentes inspeccionadas en `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\`: `BookingCancellation.cs`, `BookingCancellationStatus.cs`, `OperatorRefundReceived.cs`, `OperatorRefundAllocation.cs`, `DeductionLine.cs`, `ClientCreditEntry.cs`, `ClientCreditWithdrawal.cs`, `FiscalSnapshot.cs`, `Invoice.cs`, `InvoiceItem.cs`, `HotelBooking.cs`, `ServicioReserva.cs`, `Reserva.cs`, `Customer.cs`, `Supplier.cs`, `OperationalFinanceSettings.cs`, `ApprovalRequestType.cs`.
- AfipService NC parcial vínculo: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Infrastructure\Services\AfipService.cs:827-838`.
- Service vigente BC: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Infrastructure\Services\BookingCancellationService.cs`.

---

**Fin del plan funcional Fase 1 FC1.3.** Plan tactico tecnico abajo.

---
---

# Plan tactico tecnico FC1.3 Fase 1 (software-architect ROUND 2)

**Fecha**: 2026-05-21 (round 2)
**Autor**: `software-architect` agent (round 2, cierra 13 hallazgos reviewer round 1 + 6 decisiones Gaston GR-001..GR-006)
**Status**: Propuesta para `software-architect-reviewer` round 2
**Sigue formato del plan FC1.2 v3** (sin estimaciones de horas — regla operativa de Gaston).
**ADR asociado**: [ADR-009 Partial Credit Note round 2](adr/ADR-009-partial-credit-note.md).

> **CORRECCION CRITICA respecto round 1**: stack es **PostgreSQL** (Npgsql 8.x). La sesion principal me indico SQL Server por error. Round 1 usaba sintaxis T-SQL. Round 2 corrige a Postgres en TODA seccion SQL/EF: identificadores con comillas dobles, tipos `numeric/jsonb/text/timestamptz`, `xmin` shadow + `UseXminAsConcurrencyToken()`, `PostgresException.SqlState='23514'`. Verificado en `AppDbContext.cs:1180`, `PostgresIntegrationFixture`, migracion `FC1_AddCancellationModule.cs`.

> **6 decisiones nuevas de Gaston (GR-001..GR-006)** integradas. Ver ADR §1.5.

---

## T1. Resumen del diseno tecnico (cierre §17.1 del plan funcional + RH-001..RH-022)

Las 8 decisiones que pidio el agente integrado (§17.1) y los 13 hallazgos del reviewer se resuelven asi:

| # | Item / hallazgo | Decision architect round 2 | Justificacion (detalle ADR-009 §) |
|---|---|---|---|
| 1 | Bounded context y naming | `FiscalLiquidationDto` transitorio (NO persistido Fase 1 — GR-004) | Blast radius reducido. Solo summary persiste. ADR-009 §2.4. |
| 2 | Persistencia `Supplier.PenaltyPolicyJson` | **`jsonb`** (RH-014), no `text`, no tabla normalizada | Validacion sintactica + CHECK `jsonb_typeof = 'object'`. ADR-009 §2.5. |
| 3 | Servicio nuevo vs metodo en `BookingCancellationService` | `IFiscalLiquidationCalculator` separado (puro, sin DbContext) | Testeable aislado. ADR-009 §2.6. |
| 4 | Mapping al `ApprovalRequest` existente | `Metadata` JSON + FK `BC.PartialCreditNoteApprovalRequestId` + **`xmin` via M0** (RH-006) | Convencion + concurrency safe. ADR-009 §2.7. |
| 5 | Plan de migraciones EF | **5 migraciones + 1 pre-requisito M0** (RH-006/RH-007), agrupadas por aggregate | M0 ApprovalRequest xmin, M1 Supplier, M2 InvoiceItem, M3 Settings, M4 BC+FiscalSnapshot, M5 HotelBooking. ADR-009 §5.1. |
| 6 | CHECK SQL invariantes | Sintaxis Postgres (`"BookingCancellations"`, `jsonb_typeof`, etc.). **NO chk_fiscalliq_sum** porque NO persistimos componentes Fase 1 (GR-004). | 2 CHECKs en M4 + 1 CHECK Suppliers en M1. ADR-009 §2.3.5. |
| 7 | Feature flag | `EnablePartialCreditNotes` separado + **pre-condicion startup** (GR-002): si FC1.3 ON + FC1.2 OFF -> rechaza arranque | Composabilidad + rollback granular. ADR-009 §2.10. |
| 8 | Sub-fases ejecutables | **FC1.3.0 a FC1.3.7** + nueva FC1.3.6b (RH-011) | Cada sub-fase = atomica, con dependencias explicitas, sin horas. |

---

## T2. Orden de implementacion + dependencias

```
FC1.3.0a Migracion M0 pre-requisito (RH-006)                           ─┐
         (ApprovalRequest.xmin + UseXminAsConcurrencyToken)              │
         Puede mergear SEPARADA, antes de mainline FC1.3.                │
                                                                         │
FC1.3.0  Migraciones M1..M5 + enums + settings                          │ depende M0
         (Supplier.InvoicingMode/PenaltyPolicyJson jsonb,                │
          InvoiceItem.IsRefundable/ItemCategory/SourceServicioReservaId, │
          OperationalFinanceSettings 8 nuevos settings,                  │
          BC: 7 columnas summary (CreditNoteKind, ReviewRequiredReason,  │
           LiquidationComputedAt/By*, PartialCreditNoteApprovalRequestId,│
           ManualReviewer*) + FiscalSnapshot 2 nuevos,                   │
          HotelBooking.NonRefundableConceptsJson jsonb,                  │
          BookingCancellationStatus 8..11,                                │
          ApprovalRequestType.PartialCreditNoteApproval=11,              │
          CHECK constraints Postgres)                                    │
                                                                ─────────┤
                                                                         │
FC1.3.1  IFiscalLiquidationCalculator + unit tests                       │ depende M1..M5
         (logica matriz 8 con GR-003/GR-006 short-circuits,              │
          DTO transitorio FiscalLiquidationDto, ~24 unit tests)          │
                                                                ─────────┤
                                                                         │
FC1.3.2  IPartialCreditNoteApprovalBridge interface + impl               │ depende calculator
         (callback ApprovalService -> BC service)                        │
         + DI wiring + comentario explicativo                             │
                                                                ─────────┤
                                                                         │
FC1.3.3  BookingCancellationService extension:                           │ depende 1.3.1 + 1.3.2
           - ConfirmAsync: kill switch FC1.2 -> flag FC1.3 -> calculator │
             -> decidir flujo (GR-001 rechazo TotalPlusNewInvoice,        │
             GR-003 manual review CommissionOnly, GR-006 manual review   │
             penalty + TotalToCustomer, FC1.2 path normal, ManualReview) │
           - SubmitForReviewAsync (privado, serializa DTO a Metadata)     │
           - EditLiquidationAsync (G3 self-loop + diff completo Changes) │
           - ApproveLiquidationAsync (bridge handler + GR-005 bypass)    │
           - RejectLiquidationAsync (bridge handler)                     │
           - EmitCreditNoteAsync Fase 1 (solo PartialOnOriginal)         │
           - ResetToDraftAsync (post-reject)                              │
           - Validacion INV-FC1.3-001..010 inline                         │
           - Pre-condicion startup GR-002                                 │
         + integration tests (~27 tests)                          ──────┤
                                                                         │
FC1.3.4  ApprovalRequestService callbacks:                               │ depende 1.3.3
           - ApproveAsync invoca bridge.OnApprovedAsync para tipo 11     │
           - RejectAsync invoca bridge.OnRejectedAsync                   │
         + integration tests (~5 tests)                            ─────┤
                                                                         │
FC1.3.5  Endpoint POST /api/cancellations/{publicId}/edit-               │ depende 1.3.3
         liquidation (G3 admin edit, permiso reusa                        │
         cobranzas.invoice_annul) + 3 tests                       ─────┤
                                                                         │
FC1.3.6  Default seeding ApprovalRequestPolicy para tipo 11              │ depende M3/M4
         + alerta job nocturno PartialCreditNoteReviewAlertJob           │
           (BCs en ManualReviewPending > N dias) + 2 tests          ───┤
                                                                         │
FC1.3.6b PartialCreditNoteBridgeReconciliationJob (RH-011)               │ depende 1.3.3
         (job 30min: AR Approved + BC ManualReviewPending stuck          │
          -> force callback bridge) + endpoint admin force-callback      │
         + 3 tests                                                  ─────┤
                                                                         │
FC1.3.7  E2E tests (~3 happy paths/casos rechazo) + doc explicativo     │ depende todo
         docs/explicaciones/2026-05-XX-fc1-3-fase1-implementacion.md     │
         + actualizar MEMORY.md con cierre Fase 1                  ─────┘
```

---

## T3. Sub-fases detalladas con criterios de aceptacion

### FC1.3.0a — Migracion M0 pre-requisito (`ApprovalRequest.xmin`)

**Tareas atomicas**:

1. En `AppDbContext.OnModelCreating`, agregar `entity.UseXminAsConcurrencyToken()` al bloque de `ApprovalRequest`.
2. Generar migracion EF `FC1_3_PRE_AddApprovalRequestConcurrencyToken`. La migracion **no agrega columna fisica** (xmin es pseudo-columna Postgres), pero EF necesita registrarla como rowVersion: true en el snapshot del modelo.
3. Build verde.

**Criterio de aceptacion FC1.3.0a**:

- `dotnet build` verde.
- Migracion compila y aplica contra BD vacia + contra dump de staging sin errores.
- Test integration `EditLiquidation_ConcurrentEdit_xminConflict_ThrowsConcurrencyException`: 2 admins editan el mismo `ApprovalRequest` en paralelo, el segundo recibe `DbUpdateConcurrencyException`.
- Rollback de M0 no rompe datos (xmin queda intacto).

**Dependencias**: ninguna (puede mergear como hotfix post-FC1.2).

---

### FC1.3.0 — Migraciones M1..M5 + enums + settings

**Tareas atomicas**:

1. Crear archivo `src/TravelApi.Domain/Entities/SupplierInvoicingMode.cs` (enum). Agregar 2 props a `Supplier.cs` (con `[Column(TypeName = "jsonb")]` en `PenaltyPolicyJson`).
2. Crear `src/TravelApi.Domain/Entities/InvoiceItemCategory.cs`. Agregar 3 props a `InvoiceItem.cs` (incluyendo navigation a `ServicioReserva`).
3. Crear `src/TravelApi.Domain/Entities/PartialCreditNoteCase.cs`, `CreditNoteKind.cs`, `ReviewRequiredReason.cs` (bitflag con `InvoicingModeCommissionOnly` y `PenaltyResetUncertainInResellerMode` agregados).
4. **NO** crear `FiscalLiquidation` (Owned VO) — GR-004 elimina persistencia Fase 1.
5. Extender `BookingCancellationStatus.cs` con valores 8..11.
6. Extender `ApprovalRequestType.cs` con `PartialCreditNoteApproval=11`.
7. Agregar a `BookingCancellation.cs`: **7 columnas summary** (`CreditNoteKind`, `ReviewRequiredReason`, `LiquidationComputedAt`, `LiquidationComputedByUserId/Name`, `PartialCreditNoteApprovalRequestId` FK, `ManualReviewer*`).
8. Agregar a `FiscalSnapshot.cs`: `InvoicingModeAtEvent`, `OriginalInvoiceTypeAtEvent`.
9. Agregar a `OperationalFinanceSettings.cs`: **10 settings nuevos** (8 originales + 2 round 3 `BridgeReconciliationStalenessMinutes` por Q2 + `BridgeReconciliationMaxRetries` por N-003). Incluye `Allow4EyesBypassWhenSingleAdmin` por GR-005. Defaults segun ADR-009 §2.3.4 (`Fc13DeployDate=null`, `GenericDescriptionPatterns=""` por RH-008, `BridgeReconciliationStalenessMinutes=30`, `BridgeReconciliationMaxRetries=5`).
10. Agregar a `HotelBooking.cs`: `NonRefundableConceptsJson` con `[Column(TypeName = "jsonb")]`.
11. Configurar mapeos en `AppDbContext.OnModelCreating` (FK + index para `PartialCreditNoteApprovalRequestId`, `OnDelete: Restrict`).
12. Crear **5 migraciones EF separadas** (RH-007) agrupadas por aggregate:
    - `FC1_3_1_AddSupplierInvoicingModeAndPenaltyPolicy` + CHECK `chk_Suppliers_penaltypolicy_object`.
    - `FC1_3_2_AddInvoiceItemRefundabilityAndCategory` + index.
    - `FC1_3_3_AddOperationalFinanceSettingsThresholdsAndTemplate` + sentencia SQL auto-set `Fc13DeployDate` (RH-013) si `EnablePartialCreditNotes=true`.
    - `FC1_3_4_AddBcPartialCreditNoteFieldsAndFiscalSnapshotExtras` + CHECK `chk_BookingCancellations_manualreview_approvalref` + CHECK `chk_BookingCancellations_creditnotekind_consistent` + extension de CHECK `Status` si existe (verificar antes en migracion FC1).
    - `FC1_3_5_AddHotelBookingNonRefundableConcepts`.
13. Sintaxis Postgres en TODOS los `migrationBuilder.Sql`: identificadores entre comillas dobles (`"Suppliers"`, `"BookingCancellations"`), tipos `jsonb/text/numeric(18,2)/timestamp with time zone`. NO `nvarchar(max)`, NO corchetes `[]`.

**Criterio de aceptacion FC1.3.0**:

- `dotnet build` verde.
- Las 5 migraciones compilan y aplican contra BD vacia + contra dump de staging sin errores.
- Rollback M5..M1 revierte sin errores ni perdida.
- BCs preexistentes FC1.2 quedan con `CreditNoteKind=NULL`, `ReviewRequiredReason=0`, `LiquidationComputedAt=NULL` post-migracion (verificable SQL: `SELECT COUNT(*) FROM "BookingCancellations" WHERE "CreditNoteKind" IS NOT NULL` = 0).
- Smoke test SQL: intentar UPDATE `Supplier` con `PenaltyPolicyJson = '[1,2,3]'` (array, no object) -> CHECK violado, UPDATE rechaza.
- Smoke test SQL: intentar INSERT BC con `Status=9` y `PartialCreditNoteApprovalRequestId=NULL` -> CHECK violado.
- Unit test entity mapping: crear BC sin summary, persistir, recuperar -> campos summary null. Persistir con summary -> recuperar trae los valores.

**Dependencias**: FC1.3.0a.

---

### FC1.3.1 — `IFiscalLiquidationCalculator` + unit tests

**Tareas atomicas**:

1. Crear `src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs` con la interface + record `FiscalLiquidationInput` (ADR-009 §2.6).
2. Crear `src/TravelApi.Application/DTOs/Cancellation/FiscalLiquidationDto.cs` (record con todos los campos del calculo + narrativa).
3. Crear `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` con la logica (matriz 8, STEP 0..7 ADR-009 §2.9). **STEP 0 short-circuit para CommissionOnly (GR-003)**. **STEP 3 flag PenaltyResetUncertain para TotalToCustomer + penalty>0 (GR-006)**. **STEP 7 (en service caller)**: si `TotalPlusNewInvoice` -> throw (GR-001).
4. Try/catch + log warning al deserializar `PenaltyPolicyJson` (RH-014).
5. Try/catch + log warning al evaluar regex `GenericDescriptionPatterns` si malformed.
6. Registrar `services.AddScoped<IFiscalLiquidationCalculator, FiscalLiquidationCalculator>()` en `Program.cs`.
7. Escribir `src/TravelApi.Tests/Unit/FiscalLiquidationCalculatorTests.cs` con ~24 unit tests (ADR-009 §6.1).

**Criterio de aceptacion FC1.3.1**:

- Los 24 unit tests pasan en < 1 segundo total.
- Cobertura del calculator >= 95%.
- Smoke test: instanciar calculator manualmente, ejecutar 100 inputs, latencia < 10ms p99.
- Test `RH-008`: ejecutar calculator con default settings (`GenericDescriptionPatterns=""`, `Fc13DeployDate=null`) sobre 100 inputs simulando facturas legacy -> NUNCA flag `OriginalInvoiceUnclear` ni `LegacyInvoice`.

**Dependencias**: FC1.3.0.

---

### FC1.3.2 — `IPartialCreditNoteApprovalBridge` + DI wiring

**Tareas atomicas**:

1. Crear `src/TravelApi.Application/Interfaces/IPartialCreditNoteApprovalBridge.cs`:

```csharp
public interface IPartialCreditNoteApprovalBridge
{
    /// <summary>
    /// Callback invocado por ApprovalRequestService.ApproveAsync cuando RequestType
    /// es PartialCreditNoteApproval. Transiciona BC ManualReviewPending -> ManualReviewApproved
    /// y dispara emitCreditNote. Idempotente: si BC ya esta Approved, log + return.
    /// </summary>
    Task OnApprovedAsync(int approvalRequestId, string resolverUserId, string? resolverUserName, string? resolverNotes, CancellationToken ct);

    /// <summary>
    /// Callback simetrico para reject. Transiciona BC a ManualReviewRejected y
    /// auto-reset a Drafted con audit. Idempotente.
    /// </summary>
    Task OnRejectedAsync(int approvalRequestId, string resolverUserId, string? resolverUserName, string? resolverNotes, CancellationToken ct);
}
```

2. `BookingCancellationService` implementa `IBookingCancellationService` (existente FC1.2) + `IInvoiceAnnulmentBcBridge` (existente FC1.2) + `IPartialCreditNoteApprovalBridge` (nuevo).
3. Registrar en `Program.cs`:

```csharp
services.AddScoped<BookingCancellationService>();
services.AddScoped<IBookingCancellationService>(sp => sp.GetRequiredService<BookingCancellationService>());
services.AddScoped<IInvoiceAnnulmentBcBridge>(sp => sp.GetRequiredService<BookingCancellationService>());
services.AddScoped<IPartialCreditNoteApprovalBridge>(sp => sp.GetRequiredService<BookingCancellationService>());
```

4. **Pre-condicion GR-002 enforce DUAL (N-004 round 3)**:

   4a. **Runtime canonico**: `OperationalFinanceSettingsService.UpdateAsync` rechaza guardado de combinacion invalida.

   ```csharp
   // Dentro de OperationalFinanceSettingsService.UpdateAsync, tras aplicar req sobre current:
   if (current.EnablePartialCreditNotes && !current.EnableNewCancellationFlow)
       throw new ValidationException(
           "Combinacion de flags invalida (GR-002): para tener EnablePartialCreditNotes=true se requiere " +
           "EnableNewCancellationFlow=true. Si quiere apagar FC1.2, primero apague FC1.3 " +
           "(EnablePartialCreditNotes=false) en el mismo UPDATE o en uno anterior.");
   ```

   4b. **FluentValidation del request DTO** (defense-in-depth, mejor mensaje en endpoint):

   ```csharp
   RuleFor(x => x)
       .Must(req => !(req.EnablePartialCreditNotes && !req.EnableNewCancellationFlow))
       .WithMessage("EnablePartialCreditNotes=true requiere EnableNewCancellationFlow=true (GR-002).");
   ```

   4c. **Startup defense-in-depth** en `Program.cs` post-build, antes de `app.Run()` (cubre restore/UPDATE manual saltearia el service):

```csharp
using (var scope = app.Services.CreateScope())
{
    var settings = await scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>().GetEntityAsync();
    if (settings.EnablePartialCreditNotes && !settings.EnableNewCancellationFlow)
        throw new InvalidOperationException(
            "Configuracion invalida: EnablePartialCreditNotes=true requiere EnableNewCancellationFlow=true (GR-002). " +
            "Apague FC1.3 o prenda FC1.2 antes de arrancar. " +
            "El runtime UpdateAsync ya rechaza esta combinacion: si llegaste aca, hubo UPDATE manual a BD o restore de backup.");

    // RH-013: auto-set Fc13DeployDate si flag on + null
    if (settings.EnablePartialCreditNotes && settings.Fc13DeployDate is null) {
        // log warning + UPDATE
    }
}
```

5. Agregar comentario explicativo (patron heredado FC1.2 MR-V2-02) en `Program.cs`.

**Criterio de aceptacion FC1.3.2**:

- `dotnet build` verde.
- Smoke test `BuildServiceProvider_ResolvesAllServices` (heredado FC1.2.5a) sigue verde con las nuevas interfaces.
- Unit test: resolver `IPartialCreditNoteApprovalBridge` y `IBookingCancellationService` desde IServiceProvider -> misma instancia.
- Test integration `Startup_Fc13OnWithoutFc12_RejectsWithError`: configurar settings con combinacion invalida directamente en BD (UPDATE manual), app falla al arrancar.
- Test integration **`Settings_AdminTriesToDisableFc12WhileFc13On_Rejects`** (N-004 round 3): setup FC12=ON + FC13=ON. Admin invoca `UpdateAsync` con FC12=OFF. Verificar `ValidationException` (HTTP 400). Verificar que en BD queda FC12=ON (no se persistio cambio invalido). Tx rollback.

**Dependencias**: FC1.3.1.

---

### FC1.3.3 — `BookingCancellationService` extension

**Tareas atomicas**:

1. Inyectar `IFiscalLiquidationCalculator` en constructor.
2. Extender `IBookingCancellationService` con metodos publicos nuevos:
   - `Task<BookingCancellationDto> EditLiquidationAsync(Guid publicId, EditLiquidationRequest req, string userId, string? userName, CancellationToken ct);`
   - Los `Approve`/`Reject` se exponen via bridge interno, no como metodos publicos.
3. Extender `ConfirmAsync`:
   - `EnsureFeatureFlagOnAsync` (kill switch FC1.2, ya existe).
   - Leer `settings.EnablePartialCreditNotes`. Si false: continuar flujo FC1.2 (NC total — codigo actual).
   - Si true:
     - Validar `Reserva.Servicios` todos `ProductType == ServiceTypes.Hotel` con `StringComparison.OrdinalIgnoreCase` (INV-FC1.3-007). Si no, validar override `ApprovalRequest` tipo `InvariantOverride=7` con justificacion >= 50 chars y distinta del `ManualReviewComment` futuro (RH-016) — patron real lineas 261-281 de `BookingCancellationService`.
     - Construir `FiscalLiquidationInput` (load Items de OriginatingInvoice + Supplier + InvocingModeAtEvent del snapshot).
     - Invocar `calculator.Calculate(input, settings)`.
     - **GR-001**: si `result.CreditNoteKind == TotalPlusNewInvoice`, **THROW `InvalidOperationException`** con mensaje "Caso fiscal requiere FC1.3 Fase 2 - use flujo legacy o esperar". BC NO se persiste con datos FC1.3. **Test cubre que se rechaza ANTES de cualquier persistencia FC1.3.**
     - Persistir summary: `BC.CreditNoteKind`, `BC.ReviewRequiredReason`, `BC.LiquidationComputedAt`, `BC.LiquidationComputedByUserId/Name`.
     - Si `result.ReviewRequiredReason == None`: BC -> `AwaitingFiscalConfirmation` (FC1.2 path).
     - Si `!= None`: BC -> `RequiresManualReview` -> llamar `SubmitForReviewAsync` (atomico misma tx).
4. Implementar `SubmitForReviewAsync` (privado):
   - Crear `ApprovalRequest` tipo `PartialCreditNoteApproval`, `EntityType="BookingCancellation"`, `EntityId=bc.Id`, `Metadata=JsonSerializer.Serialize(...DTO...)` con schemaVersion=1 (ADR-009 §2.7), expiration desde policy.
   - Persistir `BC.PartialCreditNoteApprovalRequestId`.
   - BC -> `ManualReviewPending`.
   - Audit `BookingCancellationSubmittedForReview` con metadata del case.
5. Implementar `EditLiquidationAsync` (G3):
   - Validar BC.Status == ManualReviewPending.
   - **Validar 4-eyes (INV-FC1.3-004)** con **GR-005 bypass**: si admin != vendedor OK. Si admin == vendedor: si `settings.Allow4EyesBypassWhenSingleAdmin == true` AND solo existe 1 usuario con rol admin AND comentario >= 100 chars -> OK + flag `selfApprovedDueToSingleAdmin=true` en Metadata + AuditLog. Si no -> rechazo.
   - Validar comentario min 20 chars (o 100 si caso GR-005).
   - **RH-016**: validar que el comentario del edit es distinto del comentario futuro de aprobacion (este check ocurre al aprobar, no al editar — aca solo guardamos en el edits array).
   - Reconstruir `FiscalLiquidationInput` con overrides del request.
   - Llamar calculator -> obtener nueva DTO.
   - Validar INV-FC1.3-005 + INV-FC1.3-006 (en calculator).
   - **RH-006**: leer `approval` con xmin, apend a `Metadata.edits[]`, SaveChanges. Si concurrent edit -> `DbUpdateConcurrencyException`, caller reintenta.
   - **RH-012**: registrar `AuditLog` con `Changes` JSON shape `{"FiscalAmountToCredit": {"Old": "750000.00", "New": "700000.00"}, ...}` por cada campo modificado.
   - Actualizar `BC` summary (`CreditNoteKind`, `ReviewRequiredReason` si cambian).
   - Audit `BookingCancellationLiquidationEdited`.
   - BC se queda en ManualReviewPending (self-loop).
6. Implementar `IPartialCreditNoteApprovalBridge.OnApprovedAsync`:
   - Load BC by `PartialCreditNoteApprovalRequestId = approvalId`.
   - Validar `BC.Status == ManualReviewPending` (idempotente).
   - **Validar 4-eyes (INV-FC1.3-004) con GR-005 bypass** (misma logica que EditLiquidation).
   - Validar `resolverNotes >= 20 chars` (o 100 si AccountingThreshold) + setear `Metadata.accountingReviewRequired=true` si aplica.
   - BC -> ManualReviewApproved + setear `ManualReviewer*` fields.
   - Audit `BookingCancellationManualReviewApproved`.
   - Invocar `EmitCreditNoteAsync` automatico inmediato:
     - Si `CreditNoteKind == PartialOnOriginal`: BC -> AwaitingFiscalConfirmation. **Fase 1**: sigue FC1.2 path (AfipService emite NC total real). Log warning explicito: "Fase 1: NC parcial calculada pero AfipService emite NC total. Fase 2 implementa parcial real."
     - **`TotalPlusNewInvoice` NO LLEGA aca** (GR-001 lo rechaza en Confirm).
7. Implementar `IPartialCreditNoteApprovalBridge.OnRejectedAsync`:
   - Load BC por FK. Idempotente.
   - Validar resolverNotes >= 20 chars.
   - BC -> ManualReviewRejected.
   - Auto-reset inmediato:
     - `BC.CreditNoteKind = null`.
     - `BC.ReviewRequiredReason = 0`.
     - `BC.LiquidationComputedAt = null`. `LiquidationComputedBy* = null`.
     - `BC.PartialCreditNoteApprovalRequestId = null`.
     - `BC.Status = Drafted`.
   - Audit `BookingCancellationManualReviewRejected`.
8. Escribir ~32 integration tests (ADR-009 §6.2 actualizado round 3). **Incluye RH-009 test**: `Confirm_FlagToggledOffMidFlight_BCsInManualReviewStillProcessNormally`. **Tests adicionales round 3**:
   - `Confirm_NewStatusInsertedWithoutFiscalSnapshot_RejectedByCheckConstraint` (N-001): garantiza que el CHECK heredado `chk_BookingCancellations_fiscalsnapshot_consistent` (FC1.2) cubre como red de seguridad si el service tiene bug de orden. INSERT raw de BC con Status=9 + FiscalSnapshot_Source=0 -> `BusinessInvariantViolationException` (interceptor mapea Postgres 23514).
   - `SingleAdminCheck_IgnoresInactiveUsers_AndPendingDeletion` (N-002): seed con 1 admin activo + 1 admin desactivado, GR-005 bypass aplica.
   - `Calculator_LegacyInvoiceWithNullSnapshot_AndSupplierModeChanged_UsesNullDefaultBehavior` (cobertura adicional): factura legacy snapshot null + supplier cambio TotalToCustomer -> CommissionOnly, calculator usa Supplier actual como fallback y dispara InvoicingModeCommissionOnly.

9. **Validacion orden snapshot ANTES de transicionar Status >= 8 (N-001 round 3)**: el codigo de `ConfirmAsync` debe construir/setear `bc.FiscalSnapshot = new FiscalSnapshot {...}` con `Source != 0`, `ExchangeRateAtOriginalInvoice > 0`, `CurrencyAtEvent != NULL` ANTES de cualquier asignacion de `bc.Status = ManualReviewPending`. Si el service intenta `SaveChanges` con `Status=9` y snapshot incompleto, el CHECK heredado (no extendido) reventara con `PostgresException 23514`. Aplica el mismo patron de FC1.2 (verificado en `BookingCancellationService:313-330`). El estado intermedio `RequiresManualReview=8` queda como marker semantico del enum pero **el service nunca lo persiste**: salta directo Drafted -> ManualReviewPending dentro de la misma tx, con snapshot completo.

**Criterio de aceptacion FC1.3.3**:

- Los 32 integration tests pasan.
- Re-correr los 94 tests FC1.2 existentes — todos verdes (sin regresion).
- Test de bridge: aprobar approval con `ApprovalRequestService.ApproveAsync` directamente -> callback invocado y BC transiciona.

**Dependencias**: FC1.3.1 + FC1.3.2.

---

### FC1.3.4 — `ApprovalRequestService` callbacks

**Tareas atomicas**:

1. Inyectar `IPartialCreditNoteApprovalBridge` en `ApprovalRequestService`.
2. En `ApproveAsync`, post-SaveChanges del approval, si `approval.RequestType == PartialCreditNoteApproval`, invocar `bridge.OnApprovedAsync(approval.Id, ..., ct)`. Try/catch + log error si falla (sin rollback del approval — el bridge es idempotente, y el job FC1.3.6b reconcilia).
3. Idem en `RejectAsync`.
4. Escribir 5 integration tests:
   - Approve PartialCreditNoteApproval -> BC transiciona.
   - Reject -> BC transiciona.
   - Approve mismo approval dos veces -> segunda llamada no-op (idempotencia).
   - Approve con bridge throw -> approval queda Approved + log error + reconciliacion job lo levanta.
   - Approve approval de tipo OTRO (ej. InvoiceAnnulment) -> bridge NO se invoca.

**Criterio de aceptacion FC1.3.4**:

- 5 tests pasan.
- Tests existentes de ApprovalRequestService FC1.2 siguen verdes.

**Dependencias**: FC1.3.3.

---

### FC1.3.5 — Endpoint `edit-liquidation`

**Tareas atomicas**:

1. Crear DTO `EditLiquidationRequest` en `src/TravelApi.Application/DTOs/Cancellation/EditLiquidationRequest.cs`.
2. FluentValidation para `EditLiquidationRequest`: comentario min 20 chars (o 100 si pasa flag accountingReview, validado en service).
3. En `BookingCancellationsController` (existente), agregar:

```csharp
[HttpPost("{publicId:guid}/edit-liquidation")]
[RequirePermission(Permissions.CobranzasInvoiceAnnul)]
public async Task<ActionResult<BookingCancellationDto>> EditLiquidation(Guid publicId, [FromBody] EditLiquidationRequest req, CancellationToken ct)
{
    try { return Ok(await _service.EditLiquidationAsync(publicId, req, _userId, _userName, ct)); }
    catch (KeyNotFoundException) { return NotFound(); }
    catch (BusinessInvariantViolationException ex) { return Conflict(new { code = ex.Code, message = ex.Message }); }
    catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    catch (DbUpdateConcurrencyException) { return Conflict(new { code = "CONCURRENT_EDIT", message = "Otra edicion fue procesada primero, reintente." }); }
}
```

4. 3 controller tests: sin permiso -> 403, BC en estado invalido -> 409, happy path -> 200 con DTO actualizado.

**Criterio de aceptacion FC1.3.5**:

- 3 tests pasan.

**Dependencias**: FC1.3.3.

---

### FC1.3.6 — Defaults seeding + job alerta RG 4540

**Tareas atomicas**:

1. En seeding inicial de `ApprovalRequestPolicy` (existente B1.15 Fase B'), agregar entrada para `PartialCreditNoteApproval`: `DefaultExpirationDays = 5` (menor que default 7 para forzar atencion).
2. Crear `src/TravelApi.Infrastructure/Jobs/PartialCreditNoteReviewAlertJob.cs` siguiendo patron `ArcaAnnulmentReconciliationJob` (FC1.2):

```csharp
public class PartialCreditNoteReviewAlertJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);
        var alertDays = settings.ManualReviewMaxDaysBeforeRg4540Alert;
        var threshold = DateTime.UtcNow.AddDays(-alertDays);

        var stale = await _db.BookingCancellations
            .Where(bc => bc.Status == BookingCancellationStatus.ManualReviewPending
                      && bc.ConfirmedWithClientAt < threshold)
            .Include(bc => bc.OriginatingInvoice)
            .ToListAsync(ct);

        foreach (var bc in stale) {
            _logger.LogWarning("BC {PublicId} en ManualReviewPending desde {Date} - riesgo RG 4540", bc.PublicId, bc.ConfirmedWithClientAt);
            await _notificationService.NotifyAdminsAsync($"BC {bc.PublicId} requiere revision (NC parcial pendiente)", ct);
        }
    }
}
```

3. En `Program.cs`, registrar job recurrente diario (`Hangfire` u homologo del repo):

```csharp
RecurringJob.AddOrUpdate<PartialCreditNoteReviewAlertJob>(
    "PartialCreditNoteReviewAlert",
    job => job.RunAsync(CancellationToken.None),
    "0 8 * * *");  // 8am UTC diario
```

**Criterio de aceptacion FC1.3.6**:

- Test integration: BC en ManualReviewPending con `ConfirmedWithClientAt = NOW - 11 dias`, correr job manual -> log warning + notification mock invocada.
- Test integration: BC con `ConfirmedWithClientAt = NOW - 5 dias` -> job no alerta.

**Dependencias**: FC1.3.3.

---

### FC1.3.6b — Job reconciliacion bridge + endpoint admin force-callback (NUEVA, RH-011 + N-003 round 3 + Q2/Q3 round 3)

**Tareas atomicas**:

1. **N-003 round 3**: agregar 3 columnas a `ApprovalRequest` (entidad + migracion):
   - `BridgeRetryCount int NOT NULL DEFAULT 0`.
   - `BridgeLastError varchar(2000) NULL`.
   - `BridgeLastAttemptAt timestamp with time zone NULL`.
   - Migracion: puede sumarse a M0 (junto con `xmin`) o crear M0b separada. Recomendado: M0b para aislar el cambio de N-003 vs el de RH-006.

2. **Q2 round 3**: agregar setting `BridgeReconciliationStalenessMinutes` (int, default 30) en `OperationalFinanceSettings`. Job lo lee dinamicamente (no hardcoded).

3. **N-003 round 3**: agregar setting `BridgeReconciliationMaxRetries` (int, default 5) en `OperationalFinanceSettings`. Job lo lee.

4. Crear `src/TravelApi.Infrastructure/Jobs/PartialCreditNoteBridgeReconciliationJob.cs` (ADR-009 §2.12). Logica:
   - Detecta `ApprovalRequest.Status=Approved AND RequestType=PartialCreditNoteApproval AND ResolvedAt < UtcNow - BridgeReconciliationStalenessMinutes AND BC.Status=ManualReviewPending AND ApprovalRequest.BridgeRetryCount < BridgeReconciliationMaxRetries` -> invoca `bridge.OnApprovedAsync(...)`.
   - Setea `BridgeLastAttemptAt = UtcNow` siempre.
   - Si bridge throw: incrementa `BridgeRetryCount`, setea `BridgeLastError = TruncateTo2000(ex.Message)`, **SI llego al maxRetries y antes NO** -> notifica admin UNA VEZ. Sino, solo loguea.
   - Si bridge OK: limpia `BridgeLastError = null` (no toca `BridgeRetryCount`).
   - Idempotente.

5. Idem para `OnRejectedAsync` (caso simetrico).

6. En `Program.cs`, registrar job recurrente cada `BridgeReconciliationStalenessMinutes` (default 30 min):

```csharp
RecurringJob.AddOrUpdate<PartialCreditNoteBridgeReconciliationJob>(
    "PartialCreditNoteBridgeReconciliation",
    job => job.RunAsync(CancellationToken.None),
    "*/30 * * * *");  // cron fijo, el FILTRO de staleness es por setting (decoupling: el job corre cada 30 min pero ignora approvals "frescos")
```

7. **Q3 round 3**: endpoint admin con InvariantOverride: `POST /api/approval-requests/{publicId}/force-bridge-callback` (en `ApprovalRequestsController` — NO en `BookingCancellationsController`, porque el target es el approval). Permiso: `Permissions.CobranzasInvoiceAnnul`. Body: `{ approvalRequestOverridePublicId: guid, reason: string }`. Logica:
   - Load `targetApproval` por `publicId`, validar `RequestType == PartialCreditNoteApproval`, `Status IN (Approved, Rejected)`.
   - Load `overrideApproval` por `approvalRequestOverridePublicId`. Validar `RequestType=InvariantOverride=7`, `Status=Approved`, `EntityType="ApprovalRequest"`, `EntityId=targetApproval.Id`, `Reason.Length >= 50`, `Reason != targetApproval.ResolverNotes`, `RequestedByUserId == currentUserId`, `ExpiresAt > UtcNow`.
   - Invocar `bridge.OnApprovedAsync` o `OnRejectedAsync` segun `targetApproval.Status`.
   - Idempotente: si `BC.Status != ManualReviewPending`, no-op + log warning + audit `BookingCancellationForceApprovalCallbackNoop`.
   - Resetea `targetApproval.BridgeRetryCount = 0` y `BridgeLastError = null` al exito.
   - Audit reforzado: `BookingCancellationForceApprovalCallback` con `Changes` JSON `{ ForcedBy, OverrideApprovalId, TargetApprovalId, TargetApprovalStatusAtForce }`.

**Criterio de aceptacion FC1.3.6b**:

- Test integration `BridgeReconciliationJob_OrphanBCInManualReviewPendingWithApprovedAR_ForcesCallback`: setup BC stuck + AR Approved hace > staleness min -> job detecta + invoca callback + BC transiciona.
- Test integration `BridgeReconciliationJob_NotStaleEnough_DoesNotForceCallback`: AR Approved hace 10 min con `BridgeReconciliationStalenessMinutes=30` -> job no actua.
- Test integration **`BridgeReconciliationJob_PersistentFailure_DoesNotSpamNotifications`** (N-003 round 3): bridge mockeado tira siempre. Tras 5 ejecuciones del job: `BridgeRetryCount=5`, `BridgeLastError` poblado, `NotifyAdminsAsync` invocado EXACTAMENTE 1 vez. En las ejecuciones 6 y 7 el job NO llama al bridge (filtra por `BridgeRetryCount < maxRetries`).
- Test integration endpoint force-callback: admin invoca con InvariantOverride scoped + reason 60 chars distinta de ResolverNotes -> bridge corre -> BC transiciona + audit log + counter reset.
- Test integration endpoint force-callback **falla por override invalido**: invariantOverride EntityType="BookingCancellation" (mal scoped, debe ser "ApprovalRequest") -> rechaza con HTTP 409 BusinessInvariantViolation.
- Test integration endpoint force-callback **falla por reason duplicada**: invariantOverride.Reason == targetApproval.ResolverNotes -> rechaza.
- Test integration endpoint force-callback **idempotente**: BC ya esta ManualReviewApproved (caso raro), invocar endpoint -> no-op + audit `Noop`.

**Dependencias**: FC1.3.3 + FC1.3.4 + (migracion M0b o extension de M0 para columnas N-003).

---

### FC1.3.7 — E2E + doc explicativo + memoria

**Tareas atomicas**:

1. E2E test happy path Case 8 Factura A (ADR-009 §6.3).
2. E2E test Case 5 CommissionOnly admin acepta y procesa manualmente (GR-003).
3. E2E test Case 4 TotalPlusNewInvoice rechaza con error explicito (GR-001).
4. Crear `docs/explicaciones/2026-05-XX-fc1-3-fase1-implementacion.md` nivel trainee con:
   - Que cambio (NC parcial vs total).
   - Ejemplo pelotudo (kiosco fiambreria).
   - Las 6 decisiones de Gaston que cambiaron alcance (GR-001..GR-006).
   - Como usar (vendedor confirma -> sistema decide auto vs manual -> admin aprueba).
   - Como apagar (`EnablePartialCreditNotes=false`).
   - Que NO hace Fase 1 (NC parcial real al ARCA, CommissionOnly automatico, penalty + TotalToCustomer automatico, TotalPlusNewInvoice).
5. Actualizar `MEMORY.md` con cierre Fase 1 + apuntar a Fase 2.

**Criterio de aceptacion FC1.3.7**:

- 3 tests E2E pasan.
- Doc explicativo revisado por Gaston.
- Memoria proxima sesion actualizada.

**Dependencias**: FC1.3.3, FC1.3.4, FC1.3.5, FC1.3.6, FC1.3.6b.

---

## T4. Archivos a tocar / crear (con rationale)

### T4.1 Archivos NUEVOS

| Archivo | Sub-fase | Rationale |
|---|---|---|
| `src/TravelApi.Domain/Entities/SupplierInvoicingMode.cs` | FC1.3.0 | Enum nuevo. Default TotalToCustomer. |
| `src/TravelApi.Domain/Entities/InvoiceItemCategory.cs` | FC1.3.0 | Enum nuevo para defaults G1 + alertas UI. |
| `src/TravelApi.Domain/Entities/PartialCreditNoteCase.cs` | FC1.3.0 | Enum case 1..8. |
| `src/TravelApi.Domain/Entities/CreditNoteKind.cs` | FC1.3.0 | Enum PartialOnOriginal vs TotalPlusNewInvoice. |
| `src/TravelApi.Domain/Entities/ReviewRequiredReason.cs` | FC1.3.0 | Bitflag disparadores (incluye `InvoicingModeCommissionOnly` y `PenaltyResetUncertainInResellerMode`). |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{timestamp}_FC1_3_PRE_AddApprovalRequestConcurrencyToken.cs` | FC1.3.0a | M0 pre-requisito (RH-006). |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{timestamp}_FC1_3_1_AddSupplierInvoicingModeAndPenaltyPolicy.cs` | FC1.3.0 | M1 (RH-007: 5 migraciones agrupadas). |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{timestamp}_FC1_3_2_AddInvoiceItemRefundabilityAndCategory.cs` | FC1.3.0 | M2. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{timestamp}_FC1_3_3_AddOperationalFinanceSettingsThresholdsAndTemplate.cs` | FC1.3.0 | M3. Incluye auto-set `Fc13DeployDate` (RH-013) + setting GR-005. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{timestamp}_FC1_3_4_AddBcPartialCreditNoteFieldsAndFiscalSnapshotExtras.cs` | FC1.3.0 | M4. 7 columnas summary BC (NO Owned VO entero por GR-004) + FiscalSnapshot extras + CHECKs. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{timestamp}_FC1_3_5_AddHotelBookingNonRefundableConcepts.cs` | FC1.3.0 | M5. |
| `src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs` | FC1.3.1 | Interface puro service clasificador. |
| `src/TravelApi.Application/DTOs/Cancellation/FiscalLiquidationDto.cs` | FC1.3.1 | DTO transitorio devuelto por calculator (NO persistido entidad — GR-004). Tambien usado para exponer en `BookingCancellationDto` (subset si BC tiene approval con metadata). |
| `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` | FC1.3.1 | Impl matriz 8 + STEP 0..7. |
| `src/TravelApi.Tests/Unit/FiscalLiquidationCalculatorTests.cs` | FC1.3.1 | 24 unit tests sin DbContext. |
| `src/TravelApi.Application/Interfaces/IPartialCreditNoteApprovalBridge.cs` | FC1.3.2 | Bridge callback. |
| `src/TravelApi.Application/DTOs/Cancellation/EditLiquidationRequest.cs` | FC1.3.5 | DTO endpoint G3. |
| `src/TravelApi.Infrastructure/Jobs/PartialCreditNoteReviewAlertJob.cs` | FC1.3.6 | Job alerta RG 4540. |
| `src/TravelApi.Infrastructure/Jobs/PartialCreditNoteBridgeReconciliationJob.cs` | FC1.3.6b | Job reconciliacion bridge (RH-011). |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteFlowTests.cs` | FC1.3.3 | 27 integration tests. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteApprovalBridgeTests.cs` | FC1.3.4 | 5 tests bridge. |
| `src/TravelApi.Tests/Cancellation/Integration/EditLiquidationEndpointTests.cs` | FC1.3.5 | 3 controller tests. |
| `src/TravelApi.Tests/Cancellation/Integration/BridgeReconciliationJobTests.cs` | FC1.3.6b | 3 tests job + endpoint. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteE2ETests.cs` | FC1.3.7 | 3 E2E. |
| `docs/explicaciones/2026-05-XX-fc1-3-fase1-implementacion.md` | FC1.3.7 | Doc trainee post-cierre. |

### T4.2 Archivos MODIFICADOS

| Archivo | Sub-fase | Que cambia |
|---|---|---|
| `src/TravelApi.Domain/Entities/Supplier.cs` | FC1.3.0 | + `InvoicingMode`, `PenaltyPolicyJson` (con `[Column(TypeName="jsonb")]`). |
| `src/TravelApi.Domain/Entities/InvoiceItem.cs` | FC1.3.0 | + `IsRefundable`, `ItemCategory`, `SourceServicioReservaId` + navigation. |
| `src/TravelApi.Domain/Entities/HotelBooking.cs` | FC1.3.0 | + `NonRefundableConceptsJson` (`jsonb`). |
| `src/TravelApi.Domain/Entities/FiscalSnapshot.cs` | FC1.3.0 | + `InvoicingModeAtEvent`, `OriginalInvoiceTypeAtEvent`. |
| `src/TravelApi.Domain/Entities/BookingCancellation.cs` | FC1.3.0 | + 7 columnas summary (CreditNoteKind, ReviewRequiredReason, LiquidationComputedAt/By*, PartialCreditNoteApprovalRequestId FK, ManualReviewer*). **NO Owned VO FiscalLiquidation** (GR-004). |
| `src/TravelApi.Domain/Entities/BookingCancellationStatus.cs` | FC1.3.0 | + valores 8..11 con comentarios. |
| `src/TravelApi.Domain/Entities/ApprovalRequestType.cs` | FC1.3.0 | + `PartialCreditNoteApproval = 11`. |
| `src/TravelApi.Domain/Entities/OperationalFinanceSettings.cs` | FC1.3.0 | + 8 settings nuevos (incluyendo `Allow4EyesBypassWhenSingleAdmin` por GR-005). |
| `src/TravelApi.Infrastructure/Persistence/AppDbContext.cs` | FC1.3.0a + FC1.3.0 | + `entity.UseXminAsConcurrencyToken()` para `ApprovalRequest` (M0). + mapeos para `Supplier.PenaltyPolicyJson` jsonb, `HotelBooking.NonRefundableConceptsJson` jsonb, FK `BC.PartialCreditNoteApprovalRequestId` con `OnDelete: Restrict`. |
| `src/TravelApi.Application/Interfaces/IBookingCancellationService.cs` | FC1.3.3 | + `EditLiquidationAsync`. |
| `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` | FC1.3.3 | Implementa `IPartialCreditNoteApprovalBridge`. Extiende `ConfirmAsync` (con kill switch + flag FC1.3 + GR-001/GR-003/GR-006 paths). Agrega `EditLiquidationAsync`, `SubmitForReviewAsync` (privado), handlers de bridge, `ForceApprovalCallbackAsync` (FC1.3.6b). |
| `src/TravelApi.Infrastructure/Services/ApprovalRequestService.cs` | FC1.3.4 | Inyecta bridge. En Approve/Reject, si tipo PartialCreditNoteApproval, invoca callback. |
| `src/TravelApi/Controllers/BookingCancellationsController.cs` | FC1.3.5 + FC1.3.6b | + endpoint `POST /{publicId}/edit-liquidation` + endpoint `POST /{publicId}/force-approval-callback`. |
| `src/TravelApi/Program.cs` | FC1.3.2 + FC1.3.6 + FC1.3.6b | + DI registration de calculator + bridge + pre-condicion startup GR-002 + auto-set Fc13DeployDate (RH-013) + recurring jobs. |
| `src/TravelApi.Application/DTOs/Cancellation/BookingCancellationDto.cs` | FC1.3.0 | + campos summary FC1.3 (CreditNoteKind, ReviewRequiredReason, etc.) cuando aplique. |
| `src/TravelApi.Application/Constants/AuditActions.cs` | FC1.3.3 + FC1.3.6b | + `BookingCancellationSubmittedForReview`, `BookingCancellationLiquidationEdited`, `BookingCancellationManualReviewApproved`, `BookingCancellationManualReviewRejected`, `BookingCancellationForceApprovalCallback`. |

### T4.3 Archivos NO TOCADOS (Fase 1)

- `src/TravelApi.Infrastructure/Services/AfipService.cs`: **NO se toca Fase 1**. Sigue emitiendo NC total. Fase 2 implementa NC parcial real.
- `src/TravelApi.Infrastructure/Services/InvoiceService.cs`: NO toca Fase 1 (cadena FC1.2 con `IInvoiceAnnulmentBcBridge` ya cubre callback ARCA). FC1.3 reusa.
- Frontend: NO toca Fase 1. UI modal admin + flujo vendedor con `IsRefundable` = Fase 3.

---

## T5. Diagrama de la maquina de estados

Ver ADR-009 §2.8.1 + §2.8.2 (diagramas con paths GR-001/GR-003/GR-006 actualizados).

Resumen visual condensado:

```
Drafted (0)
   │ confirmCancellation()
   │ → EnsureFeatureFlagOnAsync (FC1.2 kill switch - rechaza si OFF)
   │ → si flag FC1.3 ON: calculator.Calculate()
   │
   ├─[flag FC1.3 OFF | reason=None]→ AwaitingFiscalConfirmation (1) → FC1.2 vigente
   │
   ├─[CreditNoteKind=TotalPlusNewInvoice]→ THROW InvalidOperationException (GR-001)
   │                                       BC queda en Drafted, sin persistir FC1.3
   │
   └─[flag FC1.3 ON | CreditNoteKind=PartialOnOriginal | reason != None]
                    → RequiresManualReview (8) [transitorio misma tx]
                                    │ submitForReview() (auto)
                                    │ + create ApprovalRequest type 11
                                    │ + Metadata JSON DTO completo (con xmin proteccion)
                                    ↓
                              ManualReviewPending (9) ←─┐ editLiquidation()
                                    │                  │ (self-loop, audit Changes JSON)
                                    │                  │ (RH-006: xmin previene race)
                                    │                  │
                                    ├─[approve]→ ManualReviewApproved (10)
                                    │             │ (4-eyes con GR-005 bypass)
                                    │             │
                                    │             │ emitCreditNote() auto
                                    │             │   PartialOnOriginal → AwaitingFiscalConfirmation
                                    │             │   (Fase 1: FC1.2 path emite NC total)
                                    │             │
                                    │             │   (TotalPlusNewInvoice nunca llega - GR-001)
                                    │             ↓
                                    │     AwaitingFiscalConfirmation (1)
                                    │
                                    └─[reject]→ ManualReviewRejected (11)
                                                  │ auto resetToDraft()
                                                  │ (nulea CreditNoteKind, ReviewRequiredReason,
                                                  │  LiquidationComputed*, FK approval)
                                                  ↓
                                                Drafted (0)
```

---

## T6. Feature flag + rollback estrategia (GR-002)

| Flag | Default prod | Que hace si OFF | Que hace si ON |
|---|---|---|---|
| `EnableNewCancellationFlow` (FC1.2 existente) | `false` (hasta signoff OPS-FISCAL-001) | **Kill switch FC1.2**: services FC1.2 rechazan con `InvalidOperationException` ("modulo de cancelacion/refund no esta habilitado") | FC1.2 activo |
| `EnablePartialCreditNotes` (FC1.3 nuevo) | `false` | BC service ignora calculator + flujo FC1.2 vigente sin cambios | Calculator corre + estados manual review se pueden activar |

Combinaciones validas (GR-002):

| FC1.2 | FC1.3 | Resultado | Cuando |
|---|---|---|---|
| OFF | OFF | Sistema legacy. Kill switch FC1.2 rechaza Confirm. | Pre-merge FC1.2 / rollback total |
| ON | OFF | FC1.2 vigente sin NC parcial. **Estado actual pos-merge FC1.2.** | Default tras signoff OPS-FISCAL-001 |
| ON | ON | FC1.3 activo, NC parcial calculada cuando aplica. **Combinacion target.** | Tras signoff FC1.3 |
| OFF | ON | **RECHAZO al arranque** (GR-002 pre-condicion). | Detectado por startup validation |

**Secuencia de prendido (GR-002)**:

1. FC1.2 ON ya hace dias (post OPS-FISCAL-001 signoff).
2. Mergeamos PR FC1.3 (con flag default OFF). App arranca normal — no toca nada.
3. QA staging: admin actualiza setting `EnablePartialCreditNotes=true`. App detecta cambio + setea `Fc13DeployDate=NOW()` auto.
4. QA ejecuta tests E2E + scenarios. Si todo OK, admin prod actualiza setting prod.

**Secuencia de rollback**:

| Escenario | Accion |
|---|---|
| FC1.3 explota | Apagar `EnablePartialCreditNotes`. FC1.2 sigue funcional. BCs en estados 8..11 quedan en limbo. Script de migracion manual mueve a Drafted/Aborted segun audit. |
| FC1.2 explota (post-FC1.3 prendido) | Apagar `EnableNewCancellationFlow`. En proximo arranque, pre-condicion GR-002 exige apagar FC1.3 tambien. Operador debe apagar ambos. |
| Reverse migraciones | Orden inverso M5 -> M4 -> M3 -> M2 -> M1 -> M0. Antes de reverse M4: script bloquea si hay BCs en estados 8..11. |

---

## T7. Decisiones rechazadas con justificacion

| Alternativa rechazada | Por que NO | A que se prefirio |
|---|---|---|
| Persistir `FiscalLiquidation` entera Owned VO Fase 1 | GR-004: blast radius + riesgo divergencia (RH-004). | Persistir solo summary (7 campos). Detalle en `ApprovalRequest.Metadata`. |
| Auto-procesar `TotalPlusNewInvoice` con BC en `ManualReviewApproved` colgado | GR-001 + RH-005: BC huerfano. | Rechazar Confirm con `InvalidOperationException`. |
| Hipotesis "penalidad en `CommissionOnly` no reduce NC" | GR-003 + RH-015: sin confirmacion contador. | Manual review obligatorio Fase 1. Calculator early-exit. |
| Hipotesis "penalty resta del fiscal en `TotalToCustomer`" sin confirmacion | GR-006: contradiccion interna §5.5 vs §12.3 plan funcional. | Manual review obligatorio Fase 1 con flag `PenaltyResetUncertainInResellerMode`. |
| `Supplier.PenaltyPolicyJson` como `text` | RH-014: sin validacion sintactica. | `jsonb` + CHECK `jsonb_typeof = 'object'`. |
| `MutationContext.HasOverrideForInvariant` | RH-003: ADR-001 rechazado. | Patron real `ApprovalRequest` tipo `InvariantOverride=7` (lineas 261-281 de BC service). |
| `ApprovalRequest` sin concurrency token | RH-006: race en edicion admin. | M0 pre-requisito agrega `xmin`. |
| 1 migracion sola con M1..M5 | RH-007: agrupar es patron FC1.2 v3. | 5 migraciones separadas por aggregate. |
| Heuristicas factura confusa ON por default | RH-008: falsos positivos masivos. | Defaults OFF. Activar solo si contador pide + test seed 100 facturas. |
| 4-eyes estricto sin escape valvula | RH-010: bloquea agencias 1-persona. | GR-005 setting opt-in con safeguards. |
| Sin job reconciliacion bridge | RH-011: BCs huerfanos. | FC1.3.6b job + endpoint admin force-callback. |
| `Fc13DeployDate` set manual | RH-013: human error. | Auto-set en M3 + startup validation. |
| Audit solo en `ApprovalRequest.Metadata` JSON | RH-012: difícil queries auditoria. | `AuditLog.Changes` con diff JSON completo + Metadata para historia. |
| `FiscalLiquidation` como entidad propia | YAGNI Fase 1. | DTO transitorio. Fase 2 promueve si hace falta. |
| Tabla `SupplierPenaltyTier` normalizada | Sin caso de uso queries cross-supplier. | `jsonb` columna. |
| Logica clasificador dentro de `ConfirmAsync` | Acopla testing. | Service aislado. |
| Tabla nueva `PartialCreditNoteApprovalDetails` | Convencion existente Metadata. | `Metadata` JSON. |
| Bandeja UI nueva | G2 cerrada. | `ApprovalRequestsController` con filtro. |
| Reusar `EnableNewCancellationFlow` para FC1.3 | Acopla rollback. | Flag separado + pre-condicion GR-002. |
| Sin enum `CreditNoteKind` | Casos 4/7 vs 2/6 mismo monto. | Enum explicito. |
| ND complementaria para cliente RI | G4 cerrada Gaston: NO. | Sin cambios. |
| 8 migraciones separadas (sugerencia §17.1 plan funcional) | El argumento "ruido en `__EFMigrationsHistory`" del round 1 no era valido. | **5 migraciones + M0** (round 2 corrige). |
| Persistir matriz 8 en BD | YAGNI. | Hardcoded en calculator. |
| AfipService NC parcial real Fase 1 | Bigger blast radius + bloqueo por F1. | Fase 2. |
| Job reconciliacion ARCA completo Fase 1 | YAGNI Fase 1 — caso raro. | Job alerta + reconciliacion bridge. |
| Permiso nuevo `cancellations.partial_nc_approve` | G5 cerrada: sin rol nuevo. | Reusar `approvals.review`. |

---

## T8. Open questions del architect (CERRADAS por reviewer round 2 + aplicadas round 3)

| Q | Pregunta | Respuesta reviewer | Estado round 3 |
|---|---|---|---|
| Q1 | Persistir `OriginalInvoiceAmount` solo (un campo) para queries de reporting basico sin abrir Metadata JSON | **NO** — YAGNI. Si reporting Fase 1 lo pide, leer directo de `Invoice.ImporteTotal` via join | Aplicado: NO se agrega columna. |
| Q2 | Threshold del job reconciliacion bridge (30 min hardcoded) deberia ser setting configurable | **SI** | Aplicado: setting `OperationalFinanceSettings.BridgeReconciliationStalenessMinutes` (int, default 30). Job lo lee dinamicamente. Tests cubren valores bajos/altos. |
| Q3 | Endpoint admin force-callback deberia requerir `ApprovalRequest` tipo `InvariantOverride=7` adicional para auditoria reforzada | **SI** | Aplicado: endpoint exige InvariantOverride scoped al approval target, reason >= 50 chars distinta del ResolverNotes original (FC1.3.6b detalle). |
| Q4 | Heuristicas caso 4 OFF por default — podriamos eliminar el codigo de las heuristicas (mantener solo checkbox manual del vendedor) | **NO** | Aplicado: mantener heuristicas con default OFF (RH-008 ya las desactiva). Si contador las pide alguna vez, se activa via setting sin redeploy. Codigo dormido es barato. |
| Q5 | `Allow4EyesBypassWhenSingleAdmin` setting global o per-agency/per-user | **GLOBAL** Fase 1 (single-tenant) | Aplicado: setting global. Comentario doc indica revisitar si multi-tenant. |

---

## T9. Trazabilidad

- **ADR asociado**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-009-partial-credit-note.md` (round 2).
- **Plan funcional FC1.3**: arriba (§1..§16 de este documento).
- **Plan FC1.2 v3**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-2.md`.
- **ADR-002 base**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-002-cancellation-refund.md`.
- **Criterio contador**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md`.
- **Mensaje round 3** (con F4 nueva por GR-006): `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\operations\2026-05-21-mensaje-contador-fc1-3-round-3.md`.
- **Convencion naming `Permissions.CobranzasInvoiceAnnul`**: `src\TravelApi.Authorization\Permissions.cs` (verificado FC1.2).
- **Patron bridge `IInvoiceAnnulmentBcBridge`**: `src\TravelApi.Application\Interfaces\IInvoiceAnnulmentBcBridge.cs` + `src\TravelApi.Infrastructure\Services\BookingCancellationService.cs` (implementa ambas).
- **Patron CHECK constraints**: convencion `chk_<tabla>_<concepto>` heredada ADR-002 §2.3.3 (sintaxis Postgres).
- **Patron override real**: `src\TravelApi.Infrastructure\Services\BookingCancellationService.cs:261-281` (`ApprovalRequest` tipo `InvariantOverride=7`).
- **Patron kill switch FC1.2**: `src\TravelApi.Infrastructure\Services\BookingCancellationService.cs:980-987` (`EnsureFeatureFlagOnAsync`).
- **Patron `xmin` Postgres**: `src\TravelApi.Infrastructure\Persistence\AppDbContext.cs:1180` + migracion FC1 linea 117.
- **Patron Owned VO con prefijo**: `FiscalSnapshot` en `AppDbContext.cs:1170-1176` (referencia, FC1.3 NO crea Owned VO nuevo por GR-004).
- **`ServiceTypes.Hotel`** constante: `src\TravelApi.Domain\Entities\ServicioReserva.cs:8`.
- **`AuditLog.Changes` shape**: `src\TravelApi.Domain\Entities\AuditLog.cs:28`.

---

**Fin del plan tactico tecnico Fase 1 FC1.3 round 2.** Cierra 13 hallazgos reviewer + 6 decisiones Gaston + rewrite Postgres total. **Round 3 (N-001..N-004 + Q1..Q5 + N-005..N-008) aplicado directamente sobre el ADR-009 y sobre las sub-fases FC1.3.6b y FC1.3.7 de este plan (arriba). Ver ADR §11.1 y §11.2 para detalle.**

---
---

# SUPERSEDED ROUND 1 - NO IMPLEMENTAR

> # ATENCION: TODO LO QUE SIGUE ABAJO ES EL PLAN TACTICO ROUND 1 — **NO SE IMPLEMENTA**.
>
> # Razones:
> - Round 1 fue escrito asumiendo stack **SQL Server por error de brief de la sesion principal**. El stack real del repo es **PostgreSQL (Npgsql 8.x)**. Toda sintaxis SQL/CHECK aca abajo es T-SQL invalida en Postgres.
> - Round 1 NO incluye las 6 decisiones de Gaston **GR-001..GR-006** (round 2 las integro).
> - Round 1 NO cierra los 13 hallazgos del reviewer **RH-001..RH-022** (round 2 los cerro).
> - Round 1 NO cierra los 4 hallazgos del reviewer round 2 **N-001..N-004** (round 3 los cerro en el ADR + en las sub-fases FC1.3.6b y FC1.3.7 arriba).
>
> # Plan vigente: el de arriba (round 2 + parches round 3 aplicados al ADR).
> # Este bloque queda por historia y trazabilidad solamente (N-005 round 3).
> # Si estas leyendo esto buscando que codear, **scrolleas para arriba** hasta el header `# Plan tactico tecnico FC1.3 Fase 1 (software-architect ROUND 2)`.

---
---

**Fecha**: 2026-05-21
**Autor**: `software-architect` agent (round 1, basado en plan funcional arriba)
**Status**: **SUPERSEDED por round 2 + round 3 (ver arriba).** No implementar.
**Sigue formato del plan FC1.2 v3** (sin estimaciones de horas — regla operativa de Gaston).
**ADR asociado**: [ADR-009 Partial Credit Note](adr/ADR-009-partial-credit-note.md).

> **IMPORTANTE — stack es SQL Server**: el plan funcional menciona Postgres por reflejo del lenguaje ADR-002 (cambio de stack post-FC1.1). Las constraints/CHECKs aca son T-SQL.
>
> **NOTA SUPERSEDED ROUND 1 (N-005 round 3)**: esta linea es FALSA. El stack del repo siempre fue Postgres. Round 1 estaba mal informado. Mirar el plan round 2 arriba.

---

## T1. Resumen del diseno tecnico (cierre §17.1 del plan funcional)

Las 8 decisiones que pidio el agente integrado (§17.1) se resuelven asi:

| # | Item §17.1 plan funcional | Decision architect | Justificacion corta (detalle ADR-009 §2) |
|---|---|---|---|
| 1 | Bounded context y naming | `FiscalLiquidation` como **Owned VO** de `BookingCancellation`, no entidad propia | Cohesion + simetria con `FiscalSnapshot` + sin queries independientes. ADR-009 §2.4. |
| 2 | Persistencia `Supplier.PenaltyPolicyJson` | **JSON column `nvarchar(max)`**, no tabla normalizada | Acceso baja frecuencia + schema evoluciona + override manual vendedor + convencion repo. ADR-009 §2.5. |
| 3 | Servicio nuevo vs metodo en `BookingCancellationService` | **`IFiscalLiquidationCalculator` separado** (puro, sin DbContext) | Testeable aislado (matriz 8 = unit tests) + reuse para UI preview. ADR-009 §2.6. |
| 4 | Mapping al `ApprovalRequest` existente | **`Metadata` JSON con liquidacion + edicion admin** + FK `BC.PartialCreditNoteApprovalRequestId` | Convencion B1.15 existente + apend-only edits + cross-reference auditable. ADR-009 §2.7. |
| 5 | Plan de migraciones EF | **5 migraciones** (no 8) agrupadas por aggregate, todas aditivas | M1 Supplier, M2 InvoiceItem, M3 Settings, M4 BC+FiscalLiquidation+FiscalSnapshot, M5 HotelBooking. ADR-009 §5.1. |
| 6 | CHECK SQL INV-FC1.3-005 | **T-SQL con tolerancia 0.01** (no Postgres) + chk_nonneg + chk_manualreview_approvalref | 3 CHECKs en M4. ADR-009 §2.3.5. |
| 7 | Feature flag | **Nuevo `EnablePartialCreditNotes`** separado de `EnableNewCancellationFlow` | Composabilidad + rollback granular. ADR-009 §2.10. |
| 8 | Sub-fases ejecutables | **FC1.3.0 a FC1.3.7** (ver §T3 abajo) | Cada sub-fase = atomica, con dependencias explicitas, sin horas. |

---

## T2. Orden de implementacion + dependencias

```
FC1.3.0  Migraciones EF (M1..M5) + enums + settings
         (Supplier.InvoicingMode/PenaltyPolicyJson, InvoiceItem.IsRefundable/
          ItemCategory/SourceServicioReservaId, OperationalFinanceSettings 7
          nuevos, BC.FiscalLiquidation OwnedVO + FiscalSnapshot 2 nuevos +
          BC.PartialCreditNoteApprovalRequestId FK + ManualReviewer*,
          HotelBooking.NonRefundableConceptsJson, BookingCancellationStatus
          8..11, ApprovalRequestType.PartialCreditNoteApproval=11,
          CHECK constraints SQL)                                          ─┐
                                                                           │
FC1.3.1  IFiscalLiquidationCalculator interface + impl + unit tests       │ depende M1..M5
         (logica matriz 8, sin DbContext, ~20 unit tests)                 │ (necesita enums)
                                                                ──────────┤
                                                                           │
FC1.3.2  IPartialCreditNoteApprovalBridge interface + impl                │ depende calculator
         (callback del ApprovalService al BC service post-approve/reject) │ (no estricto, pero
         + DI wiring en Program.cs + comentario explicativo               │  ordena diseno)
                                                                ──────────┤
                                                                           │
FC1.3.3  BookingCancellationService extension:                            │ depende 1.3.1 + 1.3.2
           - ConfirmAsync: leer flag, llamar calculator, decidir flujo    │
             (FC1.2 path vs FC1.3 manual review path)                     │
           - SubmitForReviewAsync (interno, llamado por Confirm)           │
           - EditLiquidationAsync (G3 self-loop)                           │
           - ApproveLiquidationAsync (callback bridge handler)             │
           - RejectLiquidationAsync (callback bridge handler)              │
           - EmitCreditNoteAsync Fase 1 (PartialOnOriginal -> avanza,     │
             TotalPlusNewInvoice -> queda parado con log warning)          │
           - ResetToDraftAsync (post-reject)                               │
           - Validacion INV-FC1.3-001..010 inline                          │
         + integration tests (~20 tests)                          ────────┤
                                                                           │
FC1.3.4  ApprovalRequestService callbacks:                                │ depende 1.3.3
           - En ApproveAsync, si tipo == PartialCreditNoteApproval,       │
             invocar bridge.OnApprovedAsync(approval, ct)                  │
           - En RejectAsync, idem -> bridge.OnRejectedAsync                │
         + integration tests (~5 tests)                            ───────┤
                                                                           │
FC1.3.5  Endpoint nuevo: POST /api/cancellations/{publicId}/edit-         │ depende 1.3.3
         liquidation (G3 admin edit). Permiso reusa cobranzas.invoice_     │
         annul. Controller delgado, llama service.                          │
         + controller tests (~3 tests)                              ──────┤
                                                                           │
FC1.3.6  Default seeding ApprovalRequestPolicy para                       │ depende M3
         PartialCreditNoteApproval (expiration days = setting default)    │
         + alerta job nocturno BCs en ManualReviewPending > N dias        │
           (mismo patron ArcaAnnulmentReconciliationJob FC1.2)             │
                                                                ──────────┤
                                                                           │
FC1.3.7  E2E tests (~2 happy paths + edge case multiservicio)              │ depende todo
         + doc explicativo docs/explicaciones/2026-05-XX-fc1-3-          │
           fase1-implementacion.md                                          │
         + actualizar memoria MEMORY.md con cierre Fase 1            ─────┘
```

---

## T3. Sub-fases detalladas con criterios de aceptacion

### FC1.3.0 — Migraciones EF + enums + settings

**Tareas atomicas**:

1. Crear archivo `src/TravelApi.Domain/Entities/SupplierInvoicingMode.cs` (enum). Agregar 2 props a `Supplier.cs`.
2. Crear archivos `src/TravelApi.Domain/Entities/InvoiceItemCategory.cs`. Agregar 3 props a `InvoiceItem.cs` (incluyendo navigation a `ServicioReserva`).
3. Crear archivos `src/TravelApi.Domain/Entities/FiscalLiquidation.cs`, `PartialCreditNoteCase.cs`, `CreditNoteKind.cs`, `ReviewRequiredReason.cs` (bitflag).
4. Extender `BookingCancellationStatus.cs` con valores 8..11.
5. Extender `ApprovalRequestType.cs` con `PartialCreditNoteApproval=11`.
6. Agregar a `BookingCancellation.cs`: `FiscalLiquidation` (owned VO), `PartialCreditNoteApprovalRequestId` (FK), `ManualReviewer*` fields.
7. Agregar a `FiscalSnapshot.cs`: `InvoicingModeAtEvent`, `OriginalInvoiceTypeAtEvent`.
8. Agregar a `OperationalFinanceSettings.cs`: 7 nuevos settings (defaults segun ADR-009 §2.3.4).
9. Agregar a `HotelBooking.cs`: `NonRefundableConceptsJson`.
10. Configurar `OwnsOne(bc => bc.FiscalLiquidation)` en `AppDbContext.OnModelCreating`. Verificar que las columnas owned generen prefijo `FiscalLiquidation_*`.
11. Crear migracion EF unica `FC1_3_0_AddPartialCreditNoteSchema` que agrupa M1..M5 (no necesitan ser migraciones separadas — agrupar reduce ruido en `__EFMigrationsHistory`).
12. Agregar CHECK constraints T-SQL via `migrationBuilder.Sql(@"ALTER TABLE [BookingCancellations] ADD CONSTRAINT chk_BookingCancellations_fiscalliq_sum CHECK (...)")` para las 3 CHECKs de ADR-009 §2.3.5.
13. Verificar que el CHECK actual de `BookingCancellationStatus` (si existe en BD por FC1.2) incluye valores 8..11. Si no, extender.

**Criterio de aceptacion FC1.3.0**:

- `dotnet build` verde.
- Migracion compila y aplica contra BD vacia + contra dump de staging sin errores.
- Rollback (`dotnet ef database update <PrevMigration>`) revierte sin errores ni perdida.
- BCs preexistentes FC1.2 quedan con `FiscalLiquidation = null` post-migracion (verificable SQL).
- Unit test: crear un BC sin liquidacion, persistir, recuperar -> `FiscalLiquidation` es null. Persistir con liquidacion -> recuperar trae las columnas correctas.
- Smoke test SQL: intentar insertar BC con `FiscalLiquidation_OriginalInvoiceAmount=100` + suma componentes != 100 + tolerancia > 0.01 -> CHECK violado, INSERT rechaza.

**Dependencias**: ninguna.

---

### FC1.3.1 — `IFiscalLiquidationCalculator` + unit tests

**Tareas atomicas**:

1. Crear `src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs` con la interface + records `FiscalLiquidationInput` + `FiscalLiquidationResult` (ADR-009 §2.6).
2. Crear `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` con la logica (matriz 8, STEP 1..7 de ADR-009 §2.9). Sin DbContext, sin async, sin IO.
3. Registrar `services.AddScoped<IFiscalLiquidationCalculator, FiscalLiquidationCalculator>()` en `Program.cs`.
4. Escribir `src/TravelApi.Tests/Unit/FiscalLiquidationCalculatorTests.cs` con los ~20 tests de ADR-009 §6.1.

**Criterio de aceptacion FC1.3.1**:

- Los 20 unit tests pasan en < 1 segundo total.
- Cobertura del calculator >= 95% (mide con `coverlet` si esta configurado).
- Smoke test: instanciar calculator manualmente, ejecutar 100 inputs, latencia < 10ms p99.

**Dependencias**: FC1.3.0 (enums + settings).

---

### FC1.3.2 — `IPartialCreditNoteApprovalBridge` + DI wiring

**Tareas atomicas**:

1. Crear `src/TravelApi.Application/Interfaces/IPartialCreditNoteApprovalBridge.cs`:
   ```csharp
   public interface IPartialCreditNoteApprovalBridge
   {
       /// <summary>
       /// Callback invocado por ApprovalRequestService.ApproveAsync cuando el tipo de
       /// approval es PartialCreditNoteApproval. Transiciona el BC de
       /// ManualReviewPending a ManualReviewApproved y dispara emitCreditNote.
       /// </summary>
       Task OnApprovedAsync(int approvalRequestId, string resolverUserId, string? resolverUserName, string? resolverNotes, CancellationToken ct);

       /// <summary>
       /// Callback simetrico para reject. Transiciona BC a ManualReviewRejected
       /// y luego auto-reset a Drafted con audit.
       /// </summary>
       Task OnRejectedAsync(int approvalRequestId, string resolverUserId, string? resolverUserName, string? resolverNotes, CancellationToken ct);
   }
   ```
2. `BookingCancellationService` implementa ambos: `IBookingCancellationService` (existente FC1.2), `IInvoiceAnnulmentBcBridge` (existente FC1.2), `IPartialCreditNoteApprovalBridge` (nuevo).
3. Registrar en `Program.cs`:
   ```csharp
   services.AddScoped<BookingCancellationService>();
   services.AddScoped<IBookingCancellationService>(sp => sp.GetRequiredService<BookingCancellationService>());
   services.AddScoped<IInvoiceAnnulmentBcBridge>(sp => sp.GetRequiredService<BookingCancellationService>());
   services.AddScoped<IPartialCreditNoteApprovalBridge>(sp => sp.GetRequiredService<BookingCancellationService>());
   ```
4. Agregar comentario explicativo (patron MR-V2-02 de FC1.2) en `Program.cs`.

**Criterio de aceptacion FC1.3.2**:

- `dotnet build` verde.
- Smoke test `BuildServiceProvider_ResolvesAllServices` (heredado FC1.2.5a) sigue verde con las nuevas interfaces.
- Unit test: resolver `IPartialCreditNoteApprovalBridge` y `IBookingCancellationService` desde IServiceProvider -> misma instancia (verifica scope).

**Dependencias**: FC1.3.1 (calculator inyectado en BC service).

---

### FC1.3.3 — `BookingCancellationService` extension

**Tareas atomicas**:

1. Inyectar `IFiscalLiquidationCalculator` en constructor.
2. Extender `IBookingCancellationService` con metodos publicos nuevos:
   - `Task<BookingCancellationDto> EditLiquidationAsync(Guid publicId, EditLiquidationRequest req, string userId, string? userName, CancellationToken ct);`
   - (los `Approve`/`Reject` se exponen via bridge interno, no como metodos publicos del service)
3. Extender `ConfirmAsync`:
   - Despues del CHECK actual de FC1.2 (factura activa, snapshot fiscal), leer `settings.EnablePartialCreditNotes`.
   - Si flag off: continuar flujo FC1.2 (NC total — codigo actual).
   - Si flag on:
     - Validar `Reserva.Servicios` todos `ProductType=Hotel` (INV-FC1.3-007). Si no, rechazar (admite override admin con justificacion 50+ chars).
     - Construir `FiscalLiquidationInput` (load Items de OriginatingInvoice + Supplier + InvoicingModeAtEvent del snapshot).
     - Invocar `calculator.Calculate(input, settings)`.
     - Persistir `BC.FiscalLiquidation = result.Liquidation`.
     - Si `result.Liquidation.ReviewRequiredReason == ReviewRequiredReason.None`: BC -> `AwaitingFiscalConfirmation` (sigue FC1.2 path).
     - Si `!= None`: BC -> `RequiresManualReview` -> llamar `SubmitForReviewAsync` (atomico misma tx).
4. Implementar `SubmitForReviewAsync` (privado):
   - Crear `ApprovalRequest` tipo `PartialCreditNoteApproval`, `EntityType="BookingCancellation"`, `EntityId=bc.Id`, `Metadata=JsonSerializer.Serialize(...liquidation...)`, expiration default.
   - Persistir `BC.PartialCreditNoteApprovalRequestId`.
   - BC -> `ManualReviewPending`.
   - Audit `BookingCancellationSubmittedForReview` con metadata del case.
5. Implementar `EditLiquidationAsync` (G3):
   - Validar BC.Status == ManualReviewPending.
   - Validar admin != vendedor (INV-FC1.3-004) — el caller pasa userId, comparar con `bc.DraftedByUserId`.
   - Validar comentario min 20 chars.
   - Reconstruir `FiscalLiquidationInput` con overrides del request (penalty + non-refundable + checkbox manuales).
   - Llamar calculator -> obtener nueva liquidacion.
   - Validar INV-FC1.3-005 + INV-FC1.3-006 (suma componentes + items no refundable matching).
   - Apend a `approval.Metadata.edits[]` (deserializar, push entry, serializar).
   - Actualizar BC.FiscalLiquidation.
   - Audit `BookingCancellationLiquidationEdited`.
   - BC se queda en ManualReviewPending (self-loop).
6. Implementar `IPartialCreditNoteApprovalBridge.OnApprovedAsync`:
   - Load BC by `PartialCreditNoteApprovalRequestId = approvalId`.
   - Validar BC.Status == ManualReviewPending (idempotente: si ya esta Approved, log warning + return).
   - Validar resolverUserId != bc.DraftedByUserId (INV-FC1.3-004).
   - Validar resolverNotes >= 20 chars (o 100 si `bc.FiscalLiquidation.FiscalAmountToCredit > PartialNcAccountingReviewThreshold` per G5) + setear `metadata.accountingReviewRequired = true` si aplica.
   - BC -> ManualReviewApproved + setear `ManualReviewer*` fields.
   - Audit `BookingCancellationManualReviewApproved`.
   - Invocar `EmitCreditNoteAsync` automatico inmediato:
     - Si `CreditNoteKind == PartialOnOriginal`: BC -> AwaitingFiscalConfirmation. **Fase 1**: sigue path FC1.2 (NC total real al ARCA). Log warning explicito "Fase 1: NC parcial calculada pero AfipService emite NC total. Fase 2 emite parcial real."
     - Si `CreditNoteKind == TotalPlusNewInvoice`: BC **se queda en ManualReviewApproved**. Log warning "Fase 1: caso TotalPlusNewInvoice no avanza, esperar Fase 2 plumbing AfipService."
7. Implementar `IPartialCreditNoteApprovalBridge.OnRejectedAsync`:
   - Load BC por FK.
   - Idempotente.
   - Validar resolverNotes >= 20 chars.
   - BC -> ManualReviewRejected.
   - Auto-reset inmediato:
     - BC.FiscalLiquidation = null.
     - BC.PartialCreditNoteApprovalRequestId = null.
     - BC.Status = Drafted.
   - Audit `BookingCancellationManualReviewRejected`.
   - (Approval queda en Rejected, historico.)
8. Validacion `INV-FC1.3-007` (servicios mixtos) implementada en Confirm.
9. Escribir 20 integration tests segun ADR-009 §6.2.

**Criterio de aceptacion FC1.3.3**:

- Los 20 integration tests pasan.
- Manual review path no rompe FC1.2 (re-correr los 94 tests FC1.2 verdes existentes).
- Test de bridge: aprobar approval con `ApprovalRequestService.ApproveAsync` directamente -> verificar callback se invoca y BC transiciona.

**Dependencias**: FC1.3.1 (calculator) + FC1.3.2 (bridge interface registrada).

---

### FC1.3.4 — `ApprovalRequestService` callbacks

**Tareas atomicas**:

1. Inyectar `IPartialCreditNoteApprovalBridge` en `ApprovalRequestService` (opcional con `IEnumerable<...>` para no romper FC1.2 si no esta registrado — pero en FC1.3 se registra siempre).
2. En `ApproveAsync`, post-SaveChanges del approval, si `approval.RequestType == PartialCreditNoteApproval`, invocar `bridge.OnApprovedAsync(approval.Id, ..., ct)`. Try/catch + log error si falla (sin rollback del approval — el bridge debe ser idempotente).
3. Idem en `RejectAsync` invocando `OnRejectedAsync`.
4. Escribir 5 integration tests:
   - Approve PartialCreditNoteApproval -> BC transiciona.
   - Reject -> BC transiciona.
   - Approve mismo approval dos veces -> segunda llamada no-op (idempotencia).
   - Approve con bridge throw -> approval queda Approved + log error visible.
   - Approve approval de tipo OTRO (ej. InvoiceAnnulment) -> bridge NO se invoca.

**Criterio de aceptacion FC1.3.4**:

- 5 tests pasan.
- Tests existentes de ApprovalRequestService (los 94 de FC1.2) siguen verdes.

**Dependencias**: FC1.3.3.

---

### FC1.3.5 — Endpoint `edit-liquidation`

**Tareas atomicas**:

1. Crear DTO `EditLiquidationRequest` en `src/TravelApi.Application/DTOs/Cancellation/`.
2. En `BookingCancellationsController` (existente), agregar:
   ```csharp
   [HttpPost("{publicId:guid}/edit-liquidation")]
   [RequirePermission(Permissions.CobranzasInvoiceAnnul)]  // reusar permiso existente
   public async Task<ActionResult<BookingCancellationDto>> EditLiquidation(Guid publicId, [FromBody] EditLiquidationRequest req, CancellationToken ct)
   {
       try { /* extract userId/userName, llamar service */ return Ok(result); }
       catch (KeyNotFoundException) { return NotFound(); }
       catch (BusinessInvariantViolationException ex) { return Conflict(new { code = ex.Code, message = ex.Message }); }
       catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
   }
   ```
3. 3 controller tests: sin permiso -> 403, BC en estado invalido -> 409, happy path -> 200 con DTO actualizado.

**Criterio de aceptacion FC1.3.5**:

- 3 tests pasan.

**Dependencias**: FC1.3.3.

---

### FC1.3.6 — Defaults seeding + job alerta

**Tareas atomicas**:

1. En el seeding inicial de `ApprovalRequestPolicy` (existente B1.15 Fase B'), agregar entrada para `PartialCreditNoteApproval` con `DefaultExpirationDays = OperationalFinanceSettings.ApprovalDefaultExpirationDays` o un override especifico (ej. 5 dias — menor que el default 7 para forzar atencion del admin sobre BCs sensibles).
2. Crear `src/TravelApi.Infrastructure/Jobs/PartialCreditNoteReviewAlertJob.cs` siguiendo patron `ArcaAnnulmentReconciliationJob` (FC1.2):
   ```csharp
   public class PartialCreditNoteReviewAlertJob
   {
       public async Task RunAsync(CancellationToken ct)
       {
           var settings = await _settings.GetEntityAsync(ct);
           var alertDays = settings.ManualReviewMaxDaysBeforeRg4540Alert;
           var threshold = DateTime.UtcNow.AddDays(-alertDays);

           var stale = await _db.BookingCancellations
               .Where(bc => bc.Status == BookingCancellationStatus.ManualReviewPending
                         && bc.ConfirmedWithClientAt < threshold)
               .Include(bc => bc.OriginatingInvoice)
               .ToListAsync(ct);

           foreach (var bc in stale) {
               _logger.LogWarning("BC {PublicId} en ManualReviewPending desde {Date} — riesgo RG 4540", bc.PublicId, bc.ConfirmedWithClientAt);
               await _notificationService.NotifyAdminsAsync($"BC {bc.PublicId} requiere revision (NC parcial pendiente)", ct);
           }
       }
   }
   ```
3. En `Program.cs`, registrar el job recurrente diario:
   ```csharp
   RecurringJob.AddOrUpdate<PartialCreditNoteReviewAlertJob>(
       "PartialCreditNoteReviewAlert",
       job => job.RunAsync(CancellationToken.None),
       "0 8 * * *");  // 8am UTC diario
   ```

**Criterio de aceptacion FC1.3.6**:

- Test integration: crear BC en ManualReviewPending con `ConfirmedWithClientAt = NOW - 11 dias`, correr job manual -> log warning emitido + notification mock invocada.
- Test integration: BC con `ConfirmedWithClientAt = NOW - 5 dias` -> job no alerta.

**Dependencias**: FC1.3.3 (BC service genera ManualReviewPending).

---

### FC1.3.7 — E2E + doc explicativo + memoria

**Tareas atomicas**:

1. E2E test happy path Case 8 Factura A: ver ADR-009 §6.3.
2. E2E test Case 4 con reject + re-draft.
3. E2E test rechazo INV-FC1.3-007 (reserva mixta Hotel + Vuelo).
4. Crear `docs/explicaciones/2026-05-XX-fc1-3-fase1-implementacion.md` nivel trainee con:
   - Que cambio (NC parcial vs total).
   - Ejemplo pelotudo (kiosco fiambreria).
   - Como usar (vendedor confirma -> sistema decide auto vs manual -> admin aprueba).
   - Como apagar (flag `EnablePartialCreditNotes = false`).
   - Que NO hace Fase 1 (NC parcial real al ARCA — Fase 2).
5. Actualizar `MEMORY.md` con cierre Fase 1 + apuntar a Fase 2.

**Criterio de aceptacion FC1.3.7**:

- 3 tests E2E pasan.
- Doc explicativo revisado por Gaston (no por reviewer agent — es doc humana).
- Memoria proxima sesion actualizada.

**Dependencias**: FC1.3.3, FC1.3.4, FC1.3.5, FC1.3.6.

---

## T4. Archivos a tocar / crear (con rationale)

### T4.1 Archivos NUEVOS

| Archivo | Sub-fase | Rationale |
|---|---|---|
| `src/TravelApi.Domain/Entities/SupplierInvoicingMode.cs` | FC1.3.0 | Enum nuevo. Default `TotalToCustomer`. |
| `src/TravelApi.Domain/Entities/InvoiceItemCategory.cs` | FC1.3.0 | Enum nuevo para defaults G1 + alertas UI. |
| `src/TravelApi.Domain/Entities/FiscalLiquidation.cs` | FC1.3.0 | Owned VO con liquidacion fiscal calculada (ADR-009 §2.3.1). |
| `src/TravelApi.Domain/Entities/PartialCreditNoteCase.cs` | FC1.3.0 | Enum case 1..8 matriz contador. |
| `src/TravelApi.Domain/Entities/CreditNoteKind.cs` | FC1.3.0 | Enum PartialOnOriginal vs TotalPlusNewInvoice. |
| `src/TravelApi.Domain/Entities/ReviewRequiredReason.cs` | FC1.3.0 | Bitflag disparadores manual review. |
| `src/TravelApi.Infrastructure/Migrations/{timestamp}_FC1_3_0_AddPartialCreditNoteSchema.cs` | FC1.3.0 | Migracion EF unica con M1..M5 + CHECK constraints. |
| `src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs` | FC1.3.1 | Interface puro service clasificador. |
| `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` | FC1.3.1 | Impl matriz 8 + STEP 1..7. |
| `src/TravelApi.Tests/Unit/FiscalLiquidationCalculatorTests.cs` | FC1.3.1 | 20 unit tests sin DbContext. |
| `src/TravelApi.Application/Interfaces/IPartialCreditNoteApprovalBridge.cs` | FC1.3.2 | Bridge callback ApprovalService -> BC service. |
| `src/TravelApi.Application/DTOs/Cancellation/EditLiquidationRequest.cs` | FC1.3.5 | DTO endpoint G3. |
| `src/TravelApi.Application/DTOs/Cancellation/FiscalLiquidationDto.cs` | FC1.3.0 | DTO para exponer en `BookingCancellationDto` al frontend. |
| `src/TravelApi.Infrastructure/Jobs/PartialCreditNoteReviewAlertJob.cs` | FC1.3.6 | Job nocturno alerta RG 4540. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteFlowTests.cs` | FC1.3.3 | 20 integration tests. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteApprovalBridgeTests.cs` | FC1.3.4 | 5 tests bridge. |
| `src/TravelApi.Tests/Cancellation/Integration/EditLiquidationEndpointTests.cs` | FC1.3.5 | 3 controller tests. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteE2ETests.cs` | FC1.3.7 | 3 E2E. |
| `docs/explicaciones/2026-05-XX-fc1-3-fase1-implementacion.md` | FC1.3.7 | Doc trainee post-cierre. |

### T4.2 Archivos MODIFICADOS

| Archivo | Sub-fase | Que cambia |
|---|---|---|
| `src/TravelApi.Domain/Entities/Supplier.cs` | FC1.3.0 | + `InvoicingMode`, `PenaltyPolicyJson`. |
| `src/TravelApi.Domain/Entities/InvoiceItem.cs` | FC1.3.0 | + `IsRefundable`, `ItemCategory`, `SourceServicioReservaId` + navigation. |
| `src/TravelApi.Domain/Entities/HotelBooking.cs` | FC1.3.0 | + `NonRefundableConceptsJson`. |
| `src/TravelApi.Domain/Entities/FiscalSnapshot.cs` | FC1.3.0 | + `InvoicingModeAtEvent`, `OriginalInvoiceTypeAtEvent`. |
| `src/TravelApi.Domain/Entities/BookingCancellation.cs` | FC1.3.0 | + `FiscalLiquidation` (owned), `PartialCreditNoteApprovalRequestId` (FK), `ManualReviewer*` fields. |
| `src/TravelApi.Domain/Entities/BookingCancellationStatus.cs` | FC1.3.0 | + valores 8..11 con comentarios. |
| `src/TravelApi.Domain/Entities/ApprovalRequestType.cs` | FC1.3.0 | + `PartialCreditNoteApproval = 11`. |
| `src/TravelApi.Domain/Entities/OperationalFinanceSettings.cs` | FC1.3.0 | + 7 settings nuevos. |
| `src/TravelApi.Infrastructure/Persistence/AppDbContext.cs` | FC1.3.0 | + `OwnsOne(bc => bc.FiscalLiquidation)` config + index + FK config. |
| `src/TravelApi.Application/Interfaces/IBookingCancellationService.cs` | FC1.3.3 | + metodo `EditLiquidationAsync`. |
| `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` | FC1.3.3 | Implementa `IPartialCreditNoteApprovalBridge`. Extiende `ConfirmAsync`. Agrega `EditLiquidationAsync`, `SubmitForReviewAsync` (privado), handlers de bridge. |
| `src/TravelApi.Infrastructure/Services/ApprovalRequestService.cs` | FC1.3.4 | Inyecta bridge. En Approve/Reject, si tipo PartialCreditNoteApproval, invoca callback. |
| `src/TravelApi/Controllers/BookingCancellationsController.cs` | FC1.3.5 | + endpoint `POST /{publicId}/edit-liquidation`. |
| `src/TravelApi/Program.cs` | FC1.3.2, FC1.3.6 | + DI registration de calculator + bridge + comentario explicativo + recurring job. |
| `src/TravelApi.Application/DTOs/Cancellation/BookingCancellationDto.cs` | FC1.3.0 | + campo `FiscalLiquidation` (FiscalLiquidationDto) nullable. |
| `src/TravelApi.Application/Constants/AuditActions.cs` | FC1.3.3 | + `BookingCancellationSubmittedForReview`, `BookingCancellationLiquidationEdited`, `BookingCancellationManualReviewApproved`, `BookingCancellationManualReviewRejected`. |

### T4.3 Archivos NO TOCADOS (Fase 1)

- `src/TravelApi.Infrastructure/Services/AfipService.cs`: **NO se toca Fase 1**. Sigue emitiendo NC total por default. Fase 2 implementa NC parcial real (prorrateo IVA + `<ImpTotal>` parcial).
- `src/TravelApi.Infrastructure/Services/InvoiceService.cs`: NO toca Fase 1 (la integracion FC1.2 con `IInvoiceAnnulmentBcBridge` ya cubre el callback ARCA). FC1.3 reusa esa misma cadena.
- Frontend: NO toca Fase 1. UI modal admin para revisar liquidacion + flujo vendedor con `IsRefundable` checkbox = Fase 3.

---

## T5. Diagrama de la maquina de estados + interaccion ApprovalRequest

(Ver ADR-009 §2.8.1 + §2.8.2 — los diagramas estan ahi para no duplicar.)

Resumen visual condensado:

```
Drafted (0)
   │ confirmCancellation()
   │ + calculator.Calculate()
   │
   ├─[flag OFF | reason=None]→ AwaitingFiscalConfirmation (1) → FC1.2 vigente
   │
   └─[flag ON | reason != None]→ RequiresManualReview (8)
                                    │ submitForReview() (auto)
                                    │ + create ApprovalRequest type 11
                                    │ + Metadata JSON liquidacion
                                    ↓
                              ManualReviewPending (9) ←─┐ editLiquidation()
                                    │                  │ (self-loop, audit)
                                    │                  │
                                    ├─[approve]→ ManualReviewApproved (10)
                                    │             │
                                    │             │ emitCreditNote() auto
                                    │             │   PartialOnOriginal → AwaitingFiscalConfirmation (Fase 1: NC total via FC1.2)
                                    │             │   TotalPlusNewInvoice → stays here (Fase 2 termina)
                                    │             ↓
                                    │     AwaitingFiscalConfirmation (1)
                                    │
                                    └─[reject]→ ManualReviewRejected (11)
                                                  │ auto resetToDraft()
                                                  ↓
                                                Drafted (0)
```

---

## T6. Feature flag + rollback estrategia

| Flag | Default prod | Que hace si OFF | Que hace si ON |
|---|---|---|---|
| `EnableNewCancellationFlow` (FC1.2 existente) | `false` (hasta signoff OPS-FISCAL-001) | Todos los services FC1.2 rechazan operaciones | FC1.2 activo |
| `EnablePartialCreditNotes` (FC1.3 nuevo) | `false` | BC service ignora calculator + flujo FC1.2 vigente sin cambios | Calculator corre + estados manual review se pueden activar |

Combinaciones validas:

| FC1.2 | FC1.3 | Resultado |
|---|---|---|
| OFF | OFF | Sistema legacy (pre-FC1.2). Comportamiento original. |
| ON | OFF | FC1.2 vigente sin NC parcial (estado actual pos-merge). |
| ON | ON | FC1.3 activo, NC parcial calculada cuando aplica. |
| OFF | ON | **INVALIDA** — sin FC1.2, FC1.3 no tiene base. Validation en startup: si `EnablePartialCreditNotes=true && !EnableNewCancellationFlow`, log error + setear FC1.3=false con warning. |

**Rollback primario**: apagar `EnablePartialCreditNotes` deja FC1.2 funcional. BCs en estados 8..11 quedan en limbo — script de migracion manual (mover a Drafted o Aborted segun audit) antes de reverse de migracion.

**Rollback total**: reverse `FC1_3_0_AddPartialCreditNoteSchema` -> verifica BCs en estados 8..11 = 0 antes (script bloquea reverse si hay > 0).

---

## T7. Decisiones rechazadas con justificacion

| Alternativa rechazada | Por que NO | A que se prefirio |
|---|---|---|
| `FiscalLiquidation` como entidad propia con tabla nueva | Sin caso de uso de queries independientes. Owned VO simetria + cohesion. | Owned VO en `BookingCancellations`. |
| Tabla `SupplierPenaltyTier` normalizada | Baja frecuencia + override manual + schema evoluciona. | `nvarchar(max)` JSON. |
| Logica clasificador dentro de `ConfirmAsync` | Metodo ya largo, testeo obliga TestContainers para matriz 8. | Service aislado `IFiscalLiquidationCalculator`. |
| Tabla nueva `PartialCreditNoteApprovalDetails` para edits | Convencion `ApprovalRequest.Metadata` ya tipada JSON. Tabla overkill. | `Metadata` JSON con array `edits[]`. |
| Bandeja UI nueva separada | Decision G2 cerrada Gaston: reusar `ApprovalRequestsController`. | Filtro por `RequestType` en endpoint existente. |
| Reusar `EnableNewCancellationFlow` para FC1.3 | Acopla rollback. | Flag nuevo `EnablePartialCreditNotes`. |
| Sin enum `CreditNoteKind`, inferir | Casos 4/7 vs 2/6 tienen mismo monto, no se puede inferir. | Enum explicito. |
| ND complementaria para cliente RI | G4 cerrada Gaston: NO. | Sin cambios en Invoice ND. |
| 8 migraciones separadas (segun sugerencia §17.1 plan funcional) | Genera ruido en `__EFMigrationsHistory` sin beneficio. Todas son aditivas + columnas nullable. | 1 migracion unica `FC1_3_0_AddPartialCreditNoteSchema` agrupada. |
| Persistir matriz 8 en BD como tabla `PartialCreditNoteCaseRule` | YAGNI. Reglas son codigo. | Logica hardcoded en calculator + tests unit. |
| Implementar AfipService NC parcial real en Fase 1 | Bigger blast radius + bloqueo por F1 (prorrateo IVA). | Diferir a Fase 2. Fase 1 marca BC + log warning. |
| Job de reconciliacion completo en Fase 1 (analogamente a `ArcaAnnulmentReconciliationJob`) | YAGNI Fase 1 — el caso es raro (admin no aprueba). Solo alerta. | Job de alerta simple FC1.3.6. Reconciliation job propio queda en deuda Fase 2. |
| Permiso nuevo `cancellations.partial_nc_approve` | G5 cerrada: sin rol/permiso nuevo Fase 1. Admin existente con comment reforzado. | Reusar `approvals.review`. |
| Permiso nuevo `cancellations.partial_nc_edit_liquidation` | Idem G5. | Reusar `cobranzas.invoice_annul`. |
| Hipotesis "penalidad en `CommissionOnly` reduce NC al cliente" | Sin confirmacion contador (F1 round 3 pregunta 2). Hipotesis Fase 1 mas conservadora. | Hipotesis "NO reduce". Cambio aislado si contador rectifica. |

---

## T8. Open questions del architect (para reviewer)

1. **`FiscalLiquidation` como Owned VO**: el reviewer puede preferir entidad propia para flexibilidad futura (historial de versiones). Discutible.
2. **`Supplier.PenaltyPolicyJson` JSON**: el reviewer puede preferir tabla normalizada upfront. Discutible.
3. **Heuristicas caso 4 (factura confusa)**: 3 reglas + checkbox manual + regex configurable. Reviewer puede pedir suprimir heuristicas y dejar solo checkbox manual.
4. **`EmitCreditNoteAsync` Fase 1 para `TotalPlusNewInvoice`**: BC se queda en `ManualReviewApproved` indefinidamente con warning. Alternativa: rechazar Confirm si el clasificador sugiere `TotalPlusNewInvoice` y flag FC1.3 esta on pero plumbing Fase 2 no esta. Mas estricto pero rompe casos legitimos hoy.
5. **Job de reconciliacion completo vs solo alerta**: Fase 1 solo emite alerta diaria. Reviewer puede pedir auto-recovery cuando ApprovalRequest expira sin accion (mover BC a Aborted con audit). Yo prefiero NO automatizar eso porque pierde plata de cliente sin intervencion.

---

## T9. Trazabilidad

- **ADR asociado**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-009-partial-credit-note.md`.
- **Plan funcional FC1.3**: arriba (§1..§20 de este documento).
- **Plan FC1.2 v3 (referencia patrones bridge + flag + sub-fases)**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-2.md`.
- **ADR-002 base (cancelacion/refund)**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-002-cancellation-refund.md`.
- **Criterio contador**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md`.
- **Mensaje round 3**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\operations\2026-05-21-mensaje-contador-fc1-3-round-3.md`.
- **Convencion naming `Permissions.CobranzasInvoiceAnnul`**: `src\TravelApi.Authorization\Permissions.cs` (verificado existe FC1.2).
- **Patron bridge `IInvoiceAnnulmentBcBridge`**: `src\TravelApi.Application\Interfaces\IInvoiceAnnulmentBcBridge.cs` + `src\TravelApi.Infrastructure\Services\BookingCancellationService.cs:42` (implementa ambas).
- **Patron CHECK constraints**: convencion `chk_<tabla>_<concepto>` heredada ADR-002 §2.3.3 (adaptada a T-SQL en ADR-009 §2.3.5).

---

**Fin del plan tactico tecnico Fase 1 FC1.3.** Listo para `software-architect-reviewer` round 1.
