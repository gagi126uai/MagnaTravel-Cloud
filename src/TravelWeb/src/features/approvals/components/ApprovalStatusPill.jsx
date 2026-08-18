import { STATUS_LABELS } from "../api/approvalsApi";
import { StatusChip } from "../../../components/ui/badge";

// Molde del estandar visual (B.5): el color viejo de cada estado ("amber"/"emerald"/etc,
// definido en STATUS_LABELS) se traduce al tono equivalente de StatusChip.
const TONE_BY_COLOR = {
  amber: "ambar",
  emerald: "verde",
  rose: "rojo",
  slate: "neutro",
};

export default function ApprovalStatusPill({ status }) {
  const entry = STATUS_LABELS[status] || { label: status, color: "slate" };
  const tone = TONE_BY_COLOR[entry.color] || "neutro";
  return <StatusChip tone={tone}>{entry.label}</StatusChip>;
}
