import Swal from "sweetalert2";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";

export function useFinanceActions(loadData) {
  const handleDownloadPdf = async (invoice) => {
    try {
      const response = await api.get(`/invoices/${invoice.id}/pdf`, { responseType: "blob" });
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
      const response = await api.get(`/invoices/${invoice.id}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
    } catch (error) {
      showError("Error al abrir PDF");
    }
  };

  const handleDownloadReceiptPdf = async (payment) => {
    try {
      const response = await api.get(`/payments/${payment.id}/receipt/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
    } catch (error) {
      showError(error.message || "No se pudo abrir el comprobante.");
    }
  };

  const handleIssueReceipt = async (payment) => {
    try {
      await api.post(`/payments/${payment.id}/receipt`);
      showSuccess("Comprobante emitido.");
      await loadData();
    } catch (error) {
      showError(error.message || "No se pudo emitir el comprobante.");
    }
  };

  const handleRetryInvoice = async (invoice) => {
    try {
      await api.post(`/invoices/${invoice.id}/retry`);
      showSuccess("Reintento encolado.");
      await loadData();
    } catch (error) {
      showError("Error al reintentar.");
    }
  };

  const handleAnnulInvoice = async (invoice) => {
    const result = await Swal.fire({
      title: "Anular factura",
      text: "Se generara una Nota de Credito. Continuar?",
      icon: "warning",
      showCancelButton: true,
      confirmButtonText: "Si, anular",
      cancelButtonText: "Cancelar",
      confirmButtonColor: "#0f172a",
    });

    if (!result.isConfirmed) {
      return;
    }

    try {
      const response = await api.post(`/invoices/${invoice.id}/annul`);
      showSuccess(response?.message || response?.Message || "Anulacion encolada.");
      await loadData();
    } catch (error) {
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
    const result = await Swal.fire({
      title: "Anular movimiento manual",
      text: "El movimiento dejara de impactar en caja.",
      icon: "warning",
      showCancelButton: true,
      confirmButtonText: "Si, anular",
      cancelButtonText: "Cancelar",
      confirmButtonColor: "#0f172a",
    });

    if (!result.isConfirmed) {
      return;
    }

    try {
      await api.delete(`/treasury/manual-movements/${movement.sourceId}`);
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
