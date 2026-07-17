# Review de seguridad/datos — Tanda 2 (operador impago solo sobre vivos) — 2026-07-17

## Veredicto: APROBADO CON COMENTARIOS — 0 bloqueantes

Cambio 100% de lectura (sin SaveChanges; único Add es a la lista en memoria del DTO),
autorización intacta (RequirePermission ReservasView + RequireOwnership, ReservasController.cs:137-139),
enmascarado de montos por `see_cost` intacto (SupplierService.cs:3182-3188). El filtro va en la
dirección SEGURA: lo único que deja de mostrarse es el costo pleno de una compra anulada,
exactamente lo que mandan las reglas 6/7 aprobadas.

Verificado (file:line):
- AttributeSupplierCreditToServicesAsync NO muta asignaciones persistidas: lee con AsNoTracking
  (:3215-3219) y reparte en un Dictionary local; el cambio de FIFO (:3247-3254) afecta solo la vista.
- Multa Retenida se muestra saldada (el operador ya la neteó del reembolso, :3155/:3266);
  cargo FacturadaAparte queda Outstanding hasta registrar el pago — deuda nueva visible.
- Reserva totalmente anulada corta antes (supplierDebtApplies=false, :3070-3080); el filtro solo
  actúa en reservas vivas con anulación parcial.
- Penalidad no confirmada (PenaltyStatus=Estimated) → TotalRealCharge=0 → Applies=false.
- Exclusión de BC Aborted correcta (:3358-3361); centinela legacy ServiceId=0 no matchea.
- Sin migración, rollback trivial, sin necesidad de auditoría nueva (camino de lectura).

Riesgos NO bloqueantes:
- R1 (nuevo, solo display, necesita confirmación del contador): LoadCancelledServiceOperatorChargesAsync
  suma RetainedDeductionAmount + cargos FacturadaAparte SIN filtrar por moneda (:3384-3392) y la fila
  reporta Currency del servicio (:3173). Cargo en moneda distinta al servicio → número mezclado.
  No mueve plata. Coincide con el follow-up ya anotado "cruce de monedas = gate contador".
- R2 (preexistente, no introducido acá): un SupplierPayment de la compra original no anulado puede
  enmascarar un cargo FacturadaAparte como "pagado" (igual que antes del cambio).
- R3 (ventana defendible): entre la anulación y la confirmación de la penalidad, el servicio no
  aparece en este carril (no hay cargo confirmado aún); la penalidad pendiente sigue visible en BC/bandejas.

Tests faltantes sugeridos: ruta enmascarada see_cost=false con anulado-con-cargo; eje cross-currency;
multa mixta (Retenida + FacturadaAparte); interacción con pago original persistente (R2, documentar).

No verificado: suite de integración Postgres (corre en CI), app real end-to-end, moneda concreta de
RetainedDeductionAmount en datos reales de PROD.
