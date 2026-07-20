# Review del plan de remediación contrato pantalla-motor — 2026-07-18

## Veredicto: APROBADO CON CAMBIOS

Premisas centrales verificadas contra código real: ancla R1 estructural
(BookingCancellation.OriginatingInvoiceId int no-null, entidad :46; candado en
BookingCancellationService.cs:892-910/:931+; cadena "sin factura → mint de
saldo fantasma" documentada :918-923); capabilities ya cargan 9 señales
(ReservaCapabilities.cs:60-69, política pura con cross-check
ReservaCapabilitiesCrossCheckTests); getApiErrorMessage ya lee payload
(errors.js:139-146 — envelope aditivo backward-compatible); precedente 409
estructurado vivo (useFinanceActions.js:95-107,126-131); swallow de pagar
proveedor NO deliberado (SuppliersController.cs:377-383/:406-412; BIVE sí
propaga :385-388); gap requiresApproval en ReservaDetailPage confirmado
(0 matches); leak 'InManagement' real (InvoiceService.cs:377-379, constante
limpia disponible ReservaCapabilities.cs:130).

## Bloqueantes
- B1: el pre-chequeo R1 como capability entra al GET caliente de la ficha; hoy
  R1 solo corre al accionar. Exigir: short-circuit hasLiveInvoice a nivel
  reserva (una vez); reconstrucción per-servicio SOLO sin-factura; declarar
  impacto en el GET. Sin esto, "aditivo y barato" es falso para R1.
- B2: los flags de T6 (per-pago) y T7 (R1) dependen de estado en DB — el
  cross-check debe ser de INTEGRACIÓN Postgres, no el harness puro C2.

## Mejoras fuertes
- M1: ~24 guards explota-al-clic con mensaje entendible fuera de las 9 tandas
  sin destino declarado (C1 aplicar-saldo INV-097, C4 factura duplicada
  SupplierService.cs:1738, A6 negativos, D1 archive) → cubo explícito backlog.
- M2: T4 des-bloquea visiblemente anular en reservas facturadas-sin-voucher
  (motor ya lo permite, ADR-044 T5; probable swap al método voucher-only
  MutationGuards.cs:409+) → confirmar swap + heads-up al dueño.
- M3: la Decisión 1 de §6 ofrecía como elegible sacar el freno R1 (fuga de
  plata) → reformular como informativa; solo forks reales de producto van al
  dueño (la 3 y la 5 sí lo son; la 4 es ratificación de vocabulario).

## Menores
Acople de orden T4→T7 (mismo texto del candado; no invertible); peores #5/#7
despriorizados a propósito (declararlo); rutas imprecisas (Domain\Reservations\,
Infrastructure\Services\Reservations\, features\payments\hooks\); cada tanda
cierra con verificación end-to-end real, no solo suite verde.

## Sin conflictos con ADRs
Coherente con ADR-002/015/025/037/042/044. Cero migración de esquema en las 9
tandas; rollback por tanda. El único movimiento con sabor a plata es M2
(percepción/UX, no corrupción).

## No verificado por el reviewer
BuildCancellationLinesAsync completo (costo exacto per-servicio), índice único
de OriginatingInvoiceId en config EF, citas heredadas del inventario ya
marcadas "verificadas a mano" (reverificar al ejecutar cada tanda).
