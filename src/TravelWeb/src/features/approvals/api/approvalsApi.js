import { api } from "../../../api";

// B1.15 Fase B' Parte 2 (2026-05-11): wrapper de los 5 endpoints del workflow
// de aprobaciones. Centraliza el shape del payload para que UI y modales no
// repliquen strings.

export const approvalsApi = {
  create: (payload) => api.post("/approvals", payload),
  getPending: () => api.get("/approvals/pending"),
  getMyRequests: () => api.get("/approvals/my-requests"),
  getByPublicId: (publicId) => api.get(`/approvals/${publicId}`),
  approve: (publicId, notes) => api.post(`/approvals/${publicId}/approve`, { notes }),
  reject: (publicId, notes) => api.post(`/approvals/${publicId}/reject`, { notes }),
};

// Labels para mostrar enums del backend sin tocar el dominio.
export const REQUEST_TYPE_LABELS = {
  InvoiceAnnulment: "Anulación de factura",
  ReceiptVoidance: "Anulación de comprobante de pago",
  ReservationCancellationWithPayment: "Cancelar reserva con cobros",
  DiscountAboveThreshold: "Descuento sobre umbral",
  PaymentDeadlineOverride: "Saltar bloqueo 20 días",
  ReservationTransfer: "Transferir reserva",
  FrozenEntityMutation: "Editar entidad congelada por CAE",
};

export const STATUS_LABELS = {
  Pending: { label: "Pendiente", color: "amber" },
  Approved: { label: "Aprobada", color: "emerald" },
  Rejected: { label: "Rechazada", color: "rose" },
  Consumed: { label: "Consumida", color: "slate" },
  Expired: { label: "Expirada", color: "slate" },
};
