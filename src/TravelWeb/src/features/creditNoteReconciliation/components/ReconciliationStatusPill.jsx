import { RECONCILIATION_STATUS_LABELS, RECEIPT_STATUS_LABELS } from "../api/creditNoteReconciliationApi";
import { StatusChip } from "../../../components/ui/badge";

/**
 * Pill de estado para un caso de reconciliacion (Pending / Resolved).
 * Sigue el mismo patron visual que ApprovalStatusPill (molde StatusChip, B.5).
 */

// El color viejo ("amber"/"emerald"/etc, definido en *_LABELS del api) se traduce
// al tono equivalente de StatusChip.
const TONE_BY_COLOR = {
  amber: "ambar",
  emerald: "verde",
  rose: "rojo",
  slate: "neutro",
};

export function ReconciliationStatusPill({ status }) {
  const entry = RECONCILIATION_STATUS_LABELS[status] || { label: status, color: "slate" };
  const tone = TONE_BY_COLOR[entry.color] || "neutro";
  return (
    <StatusChip tone={tone} data-testid="reconciliation-status-pill">
      {entry.label}
    </StatusChip>
  );
}

/**
 * Pill de estado vigente de un recibo individual (Issued = vivo / Voided = anulado).
 */
export function ReceiptStatusPill({ status }) {
  const entry = RECEIPT_STATUS_LABELS[status] || { label: status, color: "slate" };
  const tone = TONE_BY_COLOR[entry.color] || "neutro";
  return (
    <StatusChip tone={tone} data-testid="receipt-status-pill">
      {entry.label}
    </StatusChip>
  );
}
