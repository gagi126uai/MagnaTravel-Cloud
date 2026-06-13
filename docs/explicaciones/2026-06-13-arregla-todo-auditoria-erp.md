# 2026-06-13 — "Arreglá todo de la auditoría ERP, de una sentada"

Gastón pidió arreglar TODOS los hallazgos de la auditoría de negocio (travel-agency-domain-expert), preguntándole las decisiones de negocio y haciéndolo de corrido. Respondió las decisiones y se construyeron 6 features. Todo backend; el frontend va después con UX gate.

## Decisiones de negocio de Gastón (selladas)
- **Comisiones**: incluir ahora, % sobre la GANANCIA, con interruptor on/off del dueño (ajuste de negocio en Config, NO feature flag).
- **Cancelación**: completa (un servicio + multi-operador), diseñada y mostrada antes de construir.
- **Confirmado con cambios**: avisar + ajustar saldo + pedir OK.
- **Factura**: armada sola desde los servicios + avisar si no cuadra (no bloquear).
- **Cancelación, 4 finos**: servicio tachado sin estado nuevo de reserva; reembolso agregado por moneda; NC de servicio limpio en revisión manual hasta firma del contador; diferencia de cambio USD entra al alcance.

## Lo terminado y guardado en main (HEAD `3d4af10`, pusheado, NO desplegado)
Cada una con cadena completa (build → backend-reviewer → security donde aplica), sin bloqueantes:

1. **Vencimientos operativos** (`cb6f6ff`, migración Adr025_M1): 3 alarmas (pago al operador, time-limit aéreo, vencimiento de pasaporte) reintroduciendo campos que ADR-019 había sacado + PassportExpiry. Gating y PII cuidados. Fix del servicio genérico (faltaba el campo en el alta).
2. **Deuda al proveedor por expediente** (`ce57572`, sin migración): saber qué se le debe a cada operador por cada reserva, por moneda, + anticipos a cuenta. Reconciliación garantizada con el total global (misma fuente).
3. **Comisiones de vendedor** (`940ec0c`, migración Adr026_M1): % sobre la ganancia, atribuida al vendedor responsable, devengada al cobrar, con tope cero en cancelación, detrás del interruptor del dueño (default apagado).
4. **Factura armada desde los servicios** (`7d534da`, sin migración): los renglones salen de los servicios confirmados; aviso no bloqueante si el total no coincide con lo vendido. No cambia el cálculo fiscal.
5. **Confirmada con cambios** (`3d4af10`, migración Adr027_M1): si se edita el precio/costo de un servicio en una reserva viva, queda marcada "con cambios", el saldo se ajusta solo, y hay un botón para dar el OK (auditado).

## La 6ª — Cancelación parcial / multi-operador: construida pero EN RAMA APARTE
`wip/adr025-cancelacion-parcial` (commit `0d9cdf5`, pusheada, migración Adr028_M1, 1698/1698). NO se mergeó a main porque la revisión de seguridad dejó **Changes Required** con un agujero fiscal real:
- **Crítico**: el camino de "cancelar un servicio suelto" se saltea el candado que protege un servicio ya facturado con CAE vivo → podría bajar la venta sin emitir la nota de crédito. Falta el candado.
- El camino parcial no deja registro de cancelación (solo cambia el estado) ni arma el borrador de nota de crédito para revisión manual.
- El tope de devolución por operador quedó sin conectar.
- Bug latente de moneda en el reembolso multimoneda multi-operador.

El camino TOTAL/multi-operador y el reparto del reembolso quedaron OK. La emisión fiscal automática NO se construyó (queda manual hasta firma del contador). Se retoma mañana: aplicar los fixes, re-revisar seguridad, y recién ahí pasar a main.

## Pendientes
- Terminar la cancelación (rama wip) → fixes → re-review → merge.
- **Frontend de TODO** (UX gate con Gastón): pantallas de cuenta por moneda, cobranza, deuda proveedor por expediente, liquidación de comisiones + interruptor en Config, modal de factura que prellena desde servicios, carga de fechas de vencimiento y alarmas, badge "confirmada con cambios" + botón OK, pantallas de cancelación, botón "crear presupuesto desde lead", Caja "este mes".
- **Contador**: ADR-024 §10 + ADR-025 (cómo facturar extranjero con pasaporte; nota de crédito por un pedazo de factura; nota de débito agregada o por operador; plazo; asiento de diferencia de cambio). Verificar Config ARCA = Monotributo.
- **Deploy** acumulado de main: migraciones Adr021/Adr022/Adr024/Adr025/Adr026/Adr027 (la Adr028 de cancelación sigue en la rama). Correr tests de integración Postgres en el VPS antes.

## Tests
Día completo: de ~1620 a **1698** unit verde (cancelación incluida en la rama). Integración Postgres NO corrida (pendiente pre-deploy).

## Cierre de la 6ª (cancelación) — mergeada a main
Al retomar se verificó que los 4 arreglos de seguridad (SEC-B1 candado fiscal, SEC-B1b rastro, SEC-B2 RefundCap, INV-118 moneda) **ya estaban implementados** en el commit base de la rama (el doc de "pendientes" había quedado viejo respecto al código). Se agregaron **11 tests de regresión** que los sellan y se corrió la **re-review de seguridad: Approved with comments**. La cancelación se **mergeó a main** (merge `0af620a`, suite **1709/1709** unit verde).

- **SEC-B1b — limitación aceptada (dictamen de seguridad):** el candado y el ancla del rastro miran la misma condición (factura viva), así que en parcial nunca se materializa el registro `BookingCancellation`+`Line`. No bloquea: la deuda al operador se recalcula igual (sin pérdida de plata) y el evento queda en el audit log. Si se quiere ancla fiscal del parcial → diseño nuevo (desacoplar ancla del candado).
- **Pendiente pre-deploy:** integración Postgres del `Adr028_M1` + backfill en el VPS antes de desplegar.

Con esto, **las 6 features del "arreglá todo" están terminadas y en main.** Quedan los gates humanos/operativos: frontend (UX gate), firma del contador, y deploy.
