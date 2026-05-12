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
      const tipoComprobante = invoice.tipoComprobante;
      const numeroComprobante = invoice.numeroComprobante;
      let baseFilename;
      if (tipoComprobante && numeroComprobante) {
        baseFilename = `Factura-${tipoComprobante}-${numeroComprobante}`;
      } else if (invoice.reference) {
        baseFilename = invoice.reference.replace(/[^a-z0-9-]/gi, "_");
      } else {
        baseFilename = `Factura-${getPublicId(invoice)}`;
      }
      const filename = `${baseFilename}.pdf`;
      link.setAttribute("download", filename);
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
      // 409 cuando la factura ya está en proceso (Resultado == "PENDING").
      // El backend devuelve { message } con texto accionable para el usuario.
      showError(error?.payload?.message ?? error?.message ?? "Error al reintentar.");
    }
  };

  const handleAnnulInvoice = async (invoice) => {
    const confirmed = await showConfirm({
      title: "Anular factura",
      text: "Se emitirá una nota de crédito por el importe total.",
      details: "La nota de crédito impacta IVA en el período fiscal de su emisión, no en el de la factura origen (Ley IVA 23.349, art. 12). Si la factura pertenece a un período ya declarado, verificar el impacto antes de continuar.",
      confirmText: "Sí, anular",
      confirmColor: "red",
    });

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
