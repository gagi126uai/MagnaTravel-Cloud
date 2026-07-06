# 2026-07-05 — Ejecución de la lista post-saneamiento: estado al corte

> Sesión cortada por cuota. Este doc guarda TODO el estado para retomar sin perder
> nada. El plan maestro aprobado está en
> `C:\Users\Thermaltake\.claude\plans\fizzy-toasting-hickey.md` (8 tandas).

## Qué se hizo hoy (en criollo)

Gaston dictó 14 problemas después del saneamiento. Se auditó TODO con especialistas,
se cerraron 20 decisiones de producto con él, y se empezó a construir.

## 1. Tanda 1 — "Multa fantasma" (IMPLEMENTADA, sin commitear)

**El bug**: el cartel "Multa por anulación pendiente de cobro" quedaba pegado aunque
decidiste no cobrar, porque (a) contaba como cobrables multas cuya nota de débito
FALLÓ o está en revisión (sin comprobante válido), y (b) una multa confirmada no
tenía vuelta atrás.

**Lo construido** (backend + front, todo en working tree local SIN commitear):
- Predicado nuevo compartido `CancellationPenaltyRules` (Infrastructure/Reservations,
  archivo UNTRACKED — hacer `git add`): cartel solo con ND emitida y válida, o
  confirmada con monto>0 durante la ventana de emisión (ADR-014).
- Contexto nuevo "MultaEnRevision" (ND fallada/en revisión): SIN cartel al vendedor;
  el caso vive en la bandeja de NDs pendientes (verificado que la bandeja cubre
  Failed y ManualReview — nada queda invisible).
- Waive extendido: "no cobrar" una multa YA CONFIRMADA cuando no hay comprobante
  fiscal en juego. Solo Admin (INV-WAIVE-005), con INV-WAIVE-004 si hay ND
  emitida/en emisión, restauración de RefundCaps de las líneas del operador
  (método espejo ReverseConfirmedPenaltyFromLinesAsync) y auditoría completa.
- DTOs con CancelledPenaltyAmount/Currency; moneyStatus.js muestra el monto de la
  multa (fallback al balance para DTOs viejos).

**Reviews**: los 4 gates APROBADOS sin bloqueantes (backend, frontend, seguridad,
exposición de datos).

**Pase de correcciones menores — ESTABA CORRIENDO al corte** (backend-dotnet-senior):
1. Rama 2 del predicado acotada a NotApplicable/Pending (borde Issued+ND anulada).
2. Reversa: siempre PenaltyAmount=null si HasValue, cap solo si >0.
3. Audit con previousDebitNoteStatus + clearedArcaErrorMessage.
4. CancelledPenaltyAmount = lo PENDIENTE de cobro (multa menos cobrado contra la
   ND), no el bruto — coherente con el label "pendiente de cobro".
5. Doc-comment ReservaListDto con el token nuevo.
6. 403 temprano en el controller (consistencia con RevertWaived).
7. Tests: token desconocido en moneyStatus + sanitización de INV-WAIVE-004/005.
Además debía intentar la suite de integración Postgres (validar SQL del predicado
con la navegación DebitNoteInvoice).

**Falta para cerrar T1**: verificar que el pase terminó (git diff) → smoke en APP
REAL → git add + commit + push (CI deploya) → corrida manual del vigía
(POST /api/admin/maintenance/coherence/run-watchdog).

## 2. ADR-043 — Mesa de revisión de cambios (diseño listo, esperando re-review)

- Reglas de dominio: `docs/explicaciones/2026-07-06-reglas-mesa-revision-cambios.md`
  (matriz E1-E5). ADR: `docs/architecture/adr/ADR-043-mesa-revision-cambios.md`.
- Reviewer de arquitectura: APROBADO CON CAMBIOS → el arquitecto YA incorporó todo:
  - B1: pase manual a viaje = aviso+constancia SOLO si todos los renglones son
    menores; bloqueo DURO si hay Major o pendiente fiscal (evita reserva varada en
    viaje); vía de escape: emitir NC/ND invocable en Traveling.
  - B2: concurrencia determinista (Version/xmin, 409-recargar, Accept recomputa el
    "después" vigente).
  - B3: atomicidad (ChangeReviewService escribe al tracker del caller, jamás commit
    propio).
  - M1-M4: backfill resumible con completitud, bandeja de reintento + aviso de
    envejecimiento, cleanup rules como dependencia dura de Fase 2, regla explícita
    E4 (se retira si nunca se confirmó / pasa a E1 si se confirmó).
  - Phasing: F1 gate FACTURAR (independiente, primera) → F2 renglones E3 + accept →
    F3 disparador E2 fecha + Substitute/Correct → F4 fiscal 2 pasos + pase refinado
    + W6 + backfill.
- **Re-review del reviewer: APROBADO** (B1-B3 y M1-M4 cerrados; nit: reconciliar
  §4.2 con §11.0 al implementar Fase 2). Listo para construir la Fase 1.
- Hallazgos del dominio verificados en código: facturar HOY no está bloqueado por la
  marca; reprogramar fecha confirmada HOY no prende la marca; "Dar OK" borra a
  ciegas ReservaPendingChange.

## 3. Las 20 decisiones de Gaston cerradas hoy (NO REABRIR)

Mesa de revisión:
1. Aceptar-todo-junto solo si ningún cambio toca plata/fechas.
2. Aceptar cambio de precio = muestra viejo→nuevo, recalcula, traza; UN paso.
3. Facturar BLOQUEADO con revisión abierta; cobrar/pasar-a-viaje aviso+constancia
   (refinado: bloqueo duro también al viaje si hay Major/pendiente fiscal — avisado).
4. Ya facturada + cambio de plata = DOS pasos (aceptar recalcula; emitir NC/ND botón
   aparte; el renglón no cierra hasta emitir).
5. Dos cambios mismo servicio = UN renglón colapsado original→final.
6. Fecha+precio juntos = UN renglón con ambas columnas.
7. En viaje = solo "aceptar como constancia".
8. Facturada/cobrada que quedó vacía: NO cierra sin agregar servicio o anular.
9. Comisiones del vendedor: fuera de alcance.
10. Reprogramación masiva PROPIA no abre mesa (acción directa con rastro).
11. E4 agregado-y-cancelado: se retira si nunca se confirmó; E1 si se confirmó.
12. Constancia al cobrar: automática (quién/cuándo), SIN motivo escrito.

Plata:
13. Camino B: saldo a favor del cliente nace AL ANULAR (también con factura+NC).
    Contador validó: sano; 6 condiciones duras en el plan (idempotencia por BC, dos
    carriles sin netear, bruto hasta multa Confirmed, sin inventar FX, reescribir
    tests del modelo viejo, reusar seam OperatorRefundAllocationId nullable).
14. Legacy pre-24/06: mostrar historia SIN mintear crédito retroactivo (4b).
15. Multa confirmada sin ND emitida: se puede "no cobrar" (implementado T1).

Motivos (inventario completo del Explore: 12 fiscales + 12 internos + opcionales):
16. Quedan obligatorios: los 12 fiscales/plata + "cerrar sin multa" + su deshacer.
    PASAN A NOTA OPTATIVA (10): sacar de viaje, autorizar edición, revert-status
    no-admin, voucher reject/revoke/exceptional, mensaje con saldo, solicitar y
    rechazar aprobación. (El inventario con file:line está en el resultado del
    agente; los principales: ReservaService.cs:1837/1921/1720, VoucherService.cs:
    549/569/1055, MessagesPage.jsx:162, RequestApprovalModal/ApprovalsInbox.)

Pantallas (specs UX cerradas en docs/ux/2026-07-06-*.md + guia actualizada):
17. Pestaña "Anuladas" aparte (con las PendingOperatorRefund adentro); cartelito
    alcanza (sin atenuar fila).
18. Buscador global (todas las reservas, cualquier estado/fecha) + placeholder
    "Buscar por N° de reserva o cliente…".
19. Servicios cancelados OCULTOS por defecto ("Ver también los cancelados (N)"
    arriba junto al título) + "Ver historial" desplegable por servicio (requiere
    entidad ServiceStatusHistory nueva en backend).
20. Pasajeros: alta/edición EN LÍNEA (la fila se abre en el lugar; nombre+documento
    a la vista, resto en "Más detalles"; conserva doble autocompletado; muere el
    modal PassengerFormModal).

## 4. Auditorías completas disponibles (resultados en la conversación/memoria)

- Saldo a favor/extracto: causa dominante = el extracto FILTRA las anuladas
  (CustomerService.cs:679-684 → SaleFirmStatuses) y nunca lista ClientCreditEntries.
  Camino A (sin factura) funciona; camino B (con factura) mintea recién al reembolso
  del operador (OperatorRefundService.cs:572). Fix diseñado (Tanda 3 del plan, con
  las correcciones del arquitecto: líneas informativas AffectsBalance=false, etc.).
- Reembolsos: sin rotura estructural; el síntoma = no se puede registrar hasta que
  AFIP confirma la NC + mensaje vago (OperatorRefundReadModelService.cs:207-209).
  Fix diseñado (Tanda 2: NotRegistrableReason + link al reintento + precarga $0 +
  mensaje INV-042 + ELIMINAR flag EnableNewCancellationFlow con lista completa de
  call-sites en el plan).
- Servicios: cancelados SÍ se muestran; lo que se pierde son los BORRADOS duros de
  solicitados (ReservaService.cs:3415 Remove). Fix = soft-cancel siempre (Tanda 5).
- Modal pasajeros: existió siempre (wiring 1f5d3d0 de marzo); lo que cambió fue el
  autocompletado (a7859c8, 23/06). Se migra a inline (spec cerrada).

## 5. Pendiente de Gaston (cuando vuelva)

- Refresh Ctrl+F5 y síntomas CONCRETOS de notificaciones (item E14 de su lista).
- Nros de reserva: el de la multa fantasma y el de la anulada sin saldo a favor
  (para confirmar la fila de la matriz de cada una; los fixes salen igual).

## 6. Working tree al corte (nada commiteado)

- Código Tanda 1 (backend+front+tests) modificado + `CancellationPenaltyRules.cs`
  UNTRACKED + posibles fixes del pase menor a medio aplicar.
- Docs nuevos: 4 specs UX (docs/ux/2026-07-06-*.md), guia-ux-gaston.md actualizada,
  ADR-043, reglas mesa (docs/explicaciones/2026-07-06-*.md), este doc.
- Regla de oro: NUNCA force-push; deploy = push a main ff-only.
