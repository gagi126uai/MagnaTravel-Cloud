# 2026-05-21 - FC1.3 Fase 1: plan funcional cerrado + 6 decisiones G

> Nivel trainee. Ejemplos pelotudos incluidos. Documento explicativo de sesion (continuacion 2026-05-21).

## Que paso hoy (parte 2 de la sesion 2026-05-21)

Hoy mismo, mas temprano, cerramos:
- 4 decisiones FC1.3 nuevas (facturacion por operador, penalidades tabla+override, fee 5 dimensiones, items no reintegrables).
- Criterio del contador para NC parcial (matriz 8 casos + 2 escenarios A/B).
- Creacion del subagente nuevo `travel-agency-accountant-argentina`.

Despues de reiniciar la sesion para que el subagente nuevo este disponible, lo lanzamos con su primer trabajo: **disenar la Fase 1 de FC1.3 - especificacion funcional fiscal+contable+negocio**. Devolvio un plan de 20 secciones con modelo de datos logico, maquina de estados, 13 criterios de aceptacion, 11 casos de prueba y 9 preguntas nuevas (3 fiscales + 6 negocio).

De esas 9 preguntas, las 6 de negocio las cerramos ahora con Gaston. Las 3 fiscales + 8 confirmaciones profesionales van al contador en un mensaje round 3 unico antes de implementar Fase 2.

## Ejemplo pelotudo del problema

Tenes una fiambreria. Cliente compro 4 milanesas a $250 c/u = $1.000 + un envoltorio de regalo de $50 que **NO se devuelve** (es trabajo hecho).

Cliente vuelve y dice "uy, me llevo 3, no 4".

**Sistema viejo (FC1.2)**: anula la cuenta entera ($1.050) y hace una nueva por $800 (3 milanesas + envoltorio). Confuso contablemente, dos eventos donde el primero ya no existe.

**Sistema nuevo (FC1.3)**:
1. Calcula la **liquidacion fiscal**: cuanto perdio causa fiscal la cuenta original. Respuesta: $250 (la milanesa que no se llevo). El envoltorio sigue valido ($50 sigue facturado).
2. Emite **NC parcial** por $250 sobre la cuenta original. Cuenta original sigue viva por $800.
3. Si la cuenta era Factura A (cliente con responsabilidad inscripta), o si el envoltorio es de mucha plata, o si tenemos dudas, **pasa por revision manual del admin antes de emitir**.

Esa es FC1.3 Fase 1: la maquinaria que calcula la liquidacion, decide si auto-emite o pide revision, y persiste todo. La emision real al ARCA queda para Fase 2.

## Las 6 decisiones G cerradas hoy

| # | Decision | En 1 oracion |
|---|---|---|
| G1 | **Preseleccion auto `IsRefundable=false`** para items categoria `Insurance` / `AdministrativeFee` / `OperatorAdvance`, vendedor puede destildar con confirmacion. | Por default los items "sensibles" (seguro, gestion, anticipo no reembolsable) ya vienen como "no se devuelven". |
| G2 | **Reutilizar `ApprovalRequestsController`** existente con tipo nuevo `PartialCreditNoteApproval`. NO crear bandeja separada. | Aprovechamos el modulo que ya tenemos en lugar de inventar otra pantalla. |
| G3 | **Admin puede editar** la liquidacion durante revision (montos penalidad, items no reintegrables, tipo NC) con audit + comentario obligatorio. | Si el operador manda nueva info mientras el admin revisa, puede ajustar antes de aprobar. |
| G4 | **NO ND complementaria** para cliente RI por neto facturado. La factura A original + la NC parcial alcanzan. | Respetamos el ADR-002 (no usar notas de debito en flow normal). Confirmar con contador. |
| G5 | **Sin rol "contador" nuevo** en Fase 1. Admin actual aprueba casos >$2M con comentario min 100 chars + flag `AccountingReviewRequired=true`. | Postergamos crear un rol nuevo de seguridad. Por ahora el admin actual cubre. |
| G6 | Comision vendedor sobre `FinalNetInvoiced` (lo que la agencia se quedo facturado). | El vendedor cobra sobre el ingreso real post-cancelacion, no sobre lo que se devolvio. |

### Ejemplo pelotudo de G4 (la mas conceptual)

Imaginate factura A (cliente RI) por $1.000.000. Cancela y le devolvemos $750.000 (retenemos $200k penalidad + $50k seguro).

**Opcion A (la que NO elegimos)**: emitir NC parcial $750k + ND complementaria $250k. Total: 3 comprobantes. El cliente RI tiene super claro su credito fiscal.

**Opcion B (la que elegimos - G4)**: emitir NC parcial $750k. La factura A original queda viva por los $250k que efectivamente facturamos. Total: 2 comprobantes. Mas simple, sigue el criterio del contador "NC = parte que pierde causa fiscal", el RI sigue teniendo trazabilidad (factura original + NC en su contabilidad).

Lo confirmamos con el contador igual (es excepcion 7 de su criterio: "cliente RI necesita claridad para credito fiscal"). Si el contador insiste, cambiamos.

## Las 9 preguntas que van al contador (round 3)

**3 fiscales nuevas:**
- F1: threshold $500k auto/admin es `<` estricto o `<=`.
- F2: NC parcial cuando operador devuelve en cuotas: una sola NC en T0 o multiples sucesivas.
- F3: correccion de NC parcial ya emitida: ND complementaria vs anular+rehacer.

**8 confirmaciones profesionales:**
1. Prorrateo IVA en NC parcial (proporcional vs item por item).
2. Tratamiento penalidad operador en modo `CommissionOnly`.
3. Casos triviales Factura A que se quiera automatizar (default NO).
4. Wording default del template NC parcial.
5. Validar 3 senales para "cambia naturaleza fiscal del retenido".
6. Validar heuristicas "factura original confusa".
7. Umbrales $500k / $2M razonables para MagnaTravel.
8. Que hacer cuando se vence plazo RG 4540 con BC en revision pendiente.

**1 confirmacion de G4:**
9. Confirmar que cliente RI con factura A + NC parcial no necesita ND complementaria.

Mensaje completo en `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md`.

## Handoff al `software-architect`

Lo que tiene que decidir el architect (no decide el contador integrado ni Gaston):

- Naming y bounded context (`FiscalLiquidation` vs `CancellationFiscalLedger`, owned VO vs entity propia).
- Persistencia de `Supplier.PenaltyPolicyJson`: `nvarchar(max)` con JSON serializer vs tabla normalizada `SupplierPenaltyTier`.
- Calculator service aislado vs metodo en `BookingCancellationService.ConfirmAsync`.
- Mapping al `ApprovalRequestService` existente con tipo nuevo `PartialCreditNoteApproval`.
- Plan de migraciones EF en orden (8 migraciones tentativas listadas en el plan funcional §17.1).
- CHECK SQL exacto para INV-FC1.3-005 (tolerancia redondeo $0.01).
- Feature flag separado `EnablePartialCreditNotes` vs reusar `EnableNewCancellationFlow`.

Output esperado del architect: ADR-003 (Architecture Decision Record sobre NC parcial) + plan tactico FC1.3.0..FC1.3.X con sub-fases ejecutables por backend-dotnet-senior.

### Atencion para el architect

El plan funcional del subagente menciono "Postgres" por reflejo - **este repo usa SQL Server con bootstrapper EF**, no Postgres. Aclarar en el brief.

## Que viene despues

1. Architect devuelve ADR-003 + plan tactico FC1.3.0..N.
2. `software-architect-reviewer` critica.
3. `backend-dotnet-senior` implementa por sub-fases con `backend-dotnet-reviewer` + `security-data-risk-reviewer` al final de cada una.
4. `qa-automation-senior` con los 13 criterios + 11 casos del plan funcional.
5. Cuando contador responda round 3, incorporar al plan + actualizar defaults `OperationalFinanceSettings`.
6. Recien ahi se puede prender `EnablePartialCreditNotes` (o el flag que decida architect) en prod.

## Archivos modificados/creados hoy (parte 2)

- `.claude/agents/travel-agency-accountant-argentina.md` (gitignored): system prompt actualizado, elimina referencia obsoleta a pregunta abierta sobre NC parcial.
- `docs/architecture/plan-tactico-fc1-3.md`: copia versionada del plan funcional del subagente (847 lineas).
- `docs/explicaciones/2026-05-21-fc1-3-fase-1-plan-funcional.md`: este archivo.
- `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md`: mensaje round 3 para contador (Gaston lo manda).

## Total decisiones FC1.3 cerradas a la fecha

- **4 decisiones (2026-05-19)**: scope solo Hotel, T0=acuerdo operador, bandeja con bloqueo, umbrales parametrizados.
- **4 decisiones (2026-05-21 manana)**: facturacion por operador, penalidades tabla+override, fee 5 dimensiones, items flag por item.
- **6 decisiones (2026-05-21 tarde - este doc)**: las 6 G.

**Total: 14 decisiones cerradas + criterio NC parcial del contador (matriz 8 casos).**
