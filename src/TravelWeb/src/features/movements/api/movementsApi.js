import { api } from "../../../api";

// B1.15 Fase D' (2026-05-11): cliente del endpoint /api/movements.
//
// kinds: array de strings "payment" | "invoice" | "credit_note" | "credit_note_reversal".
// null = todos (default backend).
export const movementsApi = {
  list: ({ reservaId, customerId, kinds, dateFrom, dateTo, search, page = 1, pageSize = 25 } = {}) => {
    const params = new URLSearchParams();
    if (reservaId) params.set("reservaId", String(reservaId));
    if (customerId) params.set("customerId", String(customerId));
    if (Array.isArray(kinds) && kinds.length > 0) params.set("kinds", kinds.join(","));
    if (dateFrom) params.set("dateFrom", dateFrom);
    if (dateTo) params.set("dateTo", dateTo);
    if (search) params.set("search", search);
    params.set("page", String(page));
    params.set("pageSize", String(pageSize));
    return api.get(`/movements?${params.toString()}`);
  },
};

// Etiquetas humanizadas por kind.
export const KIND_LABELS = {
  payment: "Cobro",
  invoice: "Factura",
  credit_note: "Nota de crédito",
  credit_note_reversal: "Reversión NC",
};

// Color por kind (consistente con el resto del sistema).
export const KIND_COLORS = {
  payment: "emerald",
  invoice: "indigo",
  credit_note: "amber",
  credit_note_reversal: "slate",
};

// Status humanizado por estado del movement.
export const STATUS_LABELS = {
  Paid: { label: "Pagado", color: "emerald" },
  Pending: { label: "Pendiente", color: "amber" },
  Cancelled: { label: "Cancelado", color: "slate" },
  Approved: { label: "Aprobada", color: "emerald" },
  Rejected: { label: "Rechazada", color: "rose" },
  InProgress: { label: "En proceso", color: "slate" },
  Annulled: { label: "Anulada", color: "rose" },
};
