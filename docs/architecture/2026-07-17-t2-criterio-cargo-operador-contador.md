# Validación contable/fiscal — criterio "cargo real del operador" para servicios anulados (T2)

Fecha: 2026-07-17
Autor: agente `travel-agency-accountant-argentina`
Alcance: pregunta única y acotada. NO se reabren decisiones firmadas (ADR-044).

## La pregunta

La pantalla "deuda al operador" de una reserva dejó de mostrar como deuda los servicios
ANULADOS, salvo que tengan un "cargo real del operador", definido por el implementador como:

- (a) `BookingCancellationLine.RetainedDeductionAmount` — lo que el operador RETUVO del reembolso
  como penalidad confirmada.
- (b) `OperatorCharges` con `CollectionMode == FacturadaAparte` — cargos que el operador factura
  con su propio documento.

Ambos se setean sólo POST-confirmación (`AllocateConfirmedPenaltyToLinesAsync`).

¿Ese par captura TODO lo que legítimamente es "plata que se le debe/reconoce al operador por un
servicio anulado"? ¿La exclusión de `Withholding` es correcta?

---

## VEREDICTO CORTO

**VALIDADO para la deuda FIRME (confirmada), con una salvedad de diseño y dos flags de código.**

- El par (a)+(b) particiona de forma EXHAUSTIVA el universo de cargos del operador ya confirmados.
  No queda ningún cargo confirmado afuera.
- La exclusión de `Withholding`: **VALIDADA, es correcta.**
- Salvedad: la penalidad DECLARADA pero AÚN NO confirmada (`PenaltyStatus.Estimated`) es invisible
  para esta pantalla. Es correcto POR DISEÑO, pero el contador debe saber que existe esa ventana ciega.
- Dos flags menores de código (abajo): ajuste por dólar fuera del disparador; falta guarda de escritura
  para `Withholding + FacturadaAparte`.

---

## Por qué el par es exhaustivo sobre los cargos CONFIRMADOS (Hecho verificado)

`Hecho verificado:` El universo de un cargo del operador son dos ejes ortogonales
(`OperatorChargeKind.cs`, `PenaltyCollectionMode.cs`):

- Kind ∈ { AdministrativeFee, Tax, Withholding, Other }
- CollectionMode ∈ { Retenida, FacturadaAparte }

`Hecho verificado:` (`BookingCancellationLineOperatorCharge.cs:16-20` y `ADR-044:340`)
`RetainedDeductionAmount` = SUM(Kind != Withholding **AND** CollectionMode == Retenida).

`Hecho verificado:` (`SupplierCancellationCircuitReader.cs:151-153`, `OperatorChargeInvoicedReader.cs:56-59`)
(b) = SUM(CollectionMode == FacturadaAparte), cualquier Kind.

Cruzando los dos ejes, todo cargo cae exactamente en un casillero:

| Kind \ Modo        | Retenida               | FacturadaAparte |
|--------------------|------------------------|-----------------|
| AdministrativeFee  | (a)                    | (b)             |
| Tax                | (a)                    | (b)             |
| Other              | (a)                    | (b)             |
| Withholding        | EXCLUIDO a propósito   | (b) ← ver flag 2|

Conclusión: **no existe un cargo confirmado que quede fuera de (a)∪(b)**, salvo el
`Retenida+Withholding`, que se excluye deliberadamente. Partición completa.

---

## La exclusión de Withholding: VALIDADA (Alcance impositivo ARCA)

`Hecho verificado:` (`OperatorChargeKind.cs:38-44`) `Withholding` = retención fiscal que el operador
practica (IVA/Ganancias/IIBB) sobre el pago que le hizo la agencia.

Análisis fiscal: una retención NO es plata que la agencia le deba al operador. Es plata que el
operador retiene para ingresarla al fisco EN NOMBRE de la agencia, y le entrega a la agencia un
certificado de retención. Ese certificado es **crédito fiscal** de la agencia (art. del régimen de
retenciones respectivo — el operador actúa como agente de retención). Contablemente va a una cuenta de
activo "Retenciones sufridas a cuenta de impuesto", NO a "Proveedores/CxP".

Por lo tanto:
- No es deuda al operador (no se la debe al operador). ✓ Correcto excluirla de esta pantalla.
- No es pérdida de la agencia (se recupera contra impuesto propio). ✓
- Confundirla con `AdministrativeFee` haría que la agencia le "cobre" al cliente (vía ND) o registre
  como costo algo que en realidad es un activo fiscal recuperable. Ese es el error que la regla evita.

**Veredicto: la exclusión de Withholding de "deuda al operador" es correcta y está bien fundada.**

---

## Repaso de los candidatos que planteó la consulta

**1) Penalidad informada pero AÚN NO confirmada (`PenaltyStatus.Estimated`).**
`Hecho verificado:` existe ese estado previo (`DeclaredPenaltyOriginalAmount`,
`PenaltyStatus.Estimated` vs `.Confirmed`; `BookingCancellationService.cs:7178-7181`, `8043-8044`).
(a) y (b) se escriben recién en confirmación.
Análisis: en prepago puro, un servicio anulado que YA se pagó al operador NO genera deuda al operador
— genera un **receivable** (el operador debe devolver; eje "me tiene que devolver" Y, ver
`SupplierCancellationCircuitReader.cs:239-253`). La penalidad estimada sólo REDUCE ese reembolso
esperado; no es deuda de la agencia. Por eso NO mostrarla como "deuda al operador" es **correcto**.
`Necesita confirmación profesional:` lo único a decidir por negocio/contador es si se quiere una
**provisión estimada** visible (criterio de prudencia/devengamiento) mientras la penalidad está
`Estimated`. Hoy no existe y mostrar un estimado como deuda firme contradiría el diseño firmado
(NC en confirmación de cancelación + ND complementaria al confirmar la multa). **No es defecto de este
criterio**; es una feature aparte si se la quiere.

**2) Gastos administrativos.** VALIDADO/cubierto: son `OperatorChargeKind.AdministrativeFee`; si
retenidos → (a), si facturados aparte → (b). No hay hueco de modelo.

**3) Diferencia de cambio sobre la penalidad.** `Hecho verificado:` se modela aparte como
`BookingCancellationLineTreasuryFxAdjustment` y se pinta como "Ajuste por el dólar"
(`SupplierCancellationCircuitReader.cs:172-220`). NO forma parte del disparador (a)+(b). Pero **no
oculta el servicio**: un ajuste FX sólo existe colgado de un cargo `FacturadaAparte`, que ya prende (b).
Ver flag 1 sobre el MONTO.

**4) ND del operador emitida antes de confirmar.** Se captura como cargo `FacturadaAparte` (exige
`DocumentRef`, `BookingCancellationService.cs:9013-9017`). No hay hueco de MODELO: una vez cargada la
ND, es deuda. El único riesgo es de **latencia de carga** (la ND es deuda devengada desde que el
operador la emite; el sistema la refleja cuando el usuario la registra). Es dato-entry, no modelo.

---

## Flags de código (menores, no invalidan el criterio)

`Riesgo contable (menor):` **Flag 1 — el "Ajuste por el dólar" no entra al saldo oficial persistido.**
`SupplierDebtCalculator.Calculate` sólo suma `operatorChargesInvoiced` (montos `FacturadaAparte`); el
delta de `TreasuryFxAdjustment` se PINTA en el extracto pero no se comprobó que sume al
`SupplierBalanceByCurrency`. El servicio igual queda visible (por (b)), pero el MONTO de deuda podría no
incluir el ajuste de valuación. `No verificado:` si el saldo oficial debe o no incorporar ese delta —
es decisión contable de valuación de la CxP en moneda extranjera. Fuera del alcance de esta pregunta,
pero se deja anotado.

`Riesgo fiscal (menor):` **Flag 2 — no hay guarda de escritura contra `Withholding + FacturadaAparte`.**
Tanto `OperatorChargeInvoicedReader.cs:56` como el circuito suman FacturadaAparte SIN filtrar
`Kind != Withholding`. Si por la UI se pudiera cargar un cargo `Withholding` con modo `FacturadaAparte`,
se contaría como deuda al operador — justo lo contrario de la regla del contador. Conceptualmente
`Withholding` siempre es retenida (viene con certificado, no se "factura aparte"), así que hoy
probablemente sea inalcanzable, pero conviene una validación dura en `AddOperatorChargeAsync`
(`BookingCancellationService.cs:8977+`) que rechace esa combinación, en vez de confiar en la UI.

---

## Cierre

- Criterio "cargo real = (a) RetainedDeductionAmount + (b) FacturadaAparte": **VALIDADO** para la deuda
  firme; partición exhaustiva del universo de cargos confirmados.
- Exclusión de `Withholding`: **VALIDADA** (crédito fiscal, no deuda al operador ni pérdida).
- Ventana ciega de la penalidad `Estimated`: correcta por diseño; sólo requiere decisión de negocio si
  se quiere provisión estimada visible.
- Dos flags de código para endurecer (ajuste FX en el monto oficial; guarda Withholding+FacturadaAparte).

`Necesita confirmación profesional:` firma del contador sobre (i) tratar la penalidad `Estimated` como
NO-deuda hasta confirmar, y (ii) si el saldo oficial de CxP en USD debe incorporar el ajuste de
valuación por diferencia de cambio (Flag 1).
