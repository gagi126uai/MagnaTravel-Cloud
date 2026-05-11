import { api } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";

// B1.15 Fase D (2026-05-11): options.onApprovalRequired (opcional). Cuando
// AnnulInvoice devuelve 409 con requiresApproval=true, en lugar de mostrar
// error se invoca el callback con { requestType, entityType, entityId, invoice }
// para que el caller abra el RequestApprovalModal.
export function useFinanceActions(loadData, options = {}) {
  const handleDownloadPdf = async (invoice) => {
    try {
      const response = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response]));
      const link = document.createElement("a");
      link.href = url;
      link.setAttribute("download", `Factura-${invoice.tipoComprobante}-${invoice.numeroComprobante}.pdf`);
      document.body.appendChild(link);
      link.click();
      link.remove();
    } catch (error) {
      showError("Error al descargar PDF");
    }
  };

  const handleViewPdf = async (invoice) => {
    try {
      const response = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
    } catch (error) {
      showError("Error al abrir PDF");
    }
  };

  const handleDownloadReceiptPdf = async (payment) => {
    try {
      const response = await api.get(`/payments/${getPublicId(payment)}/receipt/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
    } catch (error) {
      showError(error.message || "No se pudo abrir el comprobante.");
    }
  };

  const handleIssueReceipt = async (payment) => {
    try {
      await api.post(`/payments/${getPublicId(payment)}/receipt`);
      showSuccess("Comprobante emitido.");
      await loadData();
    } catch (error) {
      showError(error.message || "No se pudo emitir el comprobante.");
    }
  };

  const handleRetryInvoice = async (invoice) => {
    try {
      await api.post(`/invoices/${getPublicId(invoice)}/retry`);
      showSuccess("Reintento encolado.");
      await loadData();
    } catch (error) {
      showError("Error al reintentar.");
    }
  };

  const handleAnnulInvoice = async (invoice) => {
    const confirmed = await showConfirm(
      "Anular factura",
      "Se generara una nota de credito para dejar trazabilidad fiscal.",
      "Si, anular"
    );

    if (!confirmed) {
      return;
    }

    try {
      const response = await api.post(`/invoices/${getPublicId(invoice)}/annul`);
      showSuccess(response?.message || response?.Message || "Anulacion encolada.");
      await loadData();
    } catch (error) {
      // B1.15 Fase D: 409 con requiresApproval=true → abrir RequestApprovalModal.
      const payload = error?.payload;
      if (error?.status === 409 && payload?.requiresApproval && typeof options.onApprovalRequired === "function") {
        options.onApprovalRequired({
          requestType: payload.requestType,
          entityType: payload.entityType,
          entityId: payload.entityId,
          invoice,
        });
        return;
      }
      showError(error.message || "Error al anular");
    }
  };

  const handleCreateManualMovement = async (payload) => {
    try {
      await api.post("/treasury/manual-movements", payload);
      showSuccess("Movimiento manual registrado.");
      await loadData();
    } catch (error) {
      showError(error.message || "No se pudo registrar el movimiento.");
      throw error;
    }
  };

  const handleUpdateManualMovement = async (id, payload) => {
    try {
      await api.put(`/treasury/manual-movements/${id}`, payload);
      showSuccess("Movimiento manual actualizado.");
      await loadData();
    } catch (error) {
      showError(error.message || "No se pudo actualizar el movimiento.");
      throw error;
    }
  };

  const handleDeleteManualMovement = async (movement) => {
    const confirmed = await showConfirm(
      "Anular movimiento manual",
      "El movimiento dejara de impactar en caja.",
      "Si, anular"
    );

    if (!confirmed) {
      return;
    }

    try {
      await api.delete(`/treasury/manual-movements/${movement.sourcePublicId}`);
      showSuccess("Movimiento anulado.");
      await loadData();
    } catch (error) {
      showError(error.message || "No se pudo anular el movimiento.");
    }
  };

  return {
    handleDownloadPdf,
    handleViewPdf,
    handleDownloadReceiptPdf,
    handleIssueReceipt,
    handleRetryInvoice,
    handleAnnulInvoice,
    handleCreateManualMovement,
    handleUpdateManualMovement,
    handleDeleteManualMovement,
  };
}
