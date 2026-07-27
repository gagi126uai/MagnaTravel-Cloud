import { api } from "../../../api";
import { showConfirm, showError, showSuccess, showTextPrompt } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
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
    // H6 (2026-07-25): el motivo de la anulación queda auditado, igual que el motivo
    // de reversa de reservas (RevertStatusModal) y el de facturas de proveedor
    // (SupplierInvoicesSection) — por eso pedimos un texto de al menos 10 caracteres
    // en vez de un simple Sí/No. showTextPrompt ya valida el mínimo y solo devuelve
    // el texto (trimeado) cuando el usuario confirma; si cancela devuelve null.
    // Conservamos el aviso fiscal de IVA por período que antes mostraba el swal Sí/No.
    const reason = await showTextPrompt({
      title: "Anular factura",
      text: "Se emitirá una nota de crédito por el importe total. La nota de crédito impacta IVA en el período fiscal de su emisión, no en el de la factura origen (Ley IVA 23.349, art. 12). Si la factura pertenece a un período ya declarado, verificá el impacto antes de continuar.",
      placeholder: "Motivo de la anulación (mín. 10 caracteres)",
      confirmText: "Anular",
      minLength: 10,
    });

    if (!reason) {
      return;
    }

    try {
      const response = await api.post(`/invoices/${getPublicId(invoice)}/annul`, { reason });
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

  const handleVoidReceipt = async (payment) => {
    const confirmed = await showConfirm({
      title: "Anular comprobante",
      text: "Esta accion marcara el comprobante como anulado. El pago sigue vigente.",
      confirmText: "Si, anular",
      confirmColor: "red",
    });

    if (!confirmed) {
      return;
    }

    try {
      await api.post(`/payments/${getPublicId(payment)}/receipt/void`, { reason: null });
      showSuccess("Comprobante anulado.");
      await loadData();
    } catch (error) {
      // 409 con requiresApproval=true → abrir RequestApprovalModal (Vendedor sin permiso).
      const payload = error?.payload;
      if (error?.status === 409 && payload?.requiresApproval && typeof options.onApprovalRequired === "function") {
        options.onApprovalRequired({
          requestType: payload.requestType,   // "ReceiptVoidance"
          entityType: payload.entityType,     // "PaymentReceipt"
          entityId: payload.entityId,
          invoice: payment,
        });
        return;
      }
      showError(error?.payload?.message ?? error?.message ?? "Error al anular comprobante.");
    }
  };

  const handleCreateManualMovement = async (payload) => {
    try {
      await api.post("/treasury/manual-movements", payload);
      showSuccess("Movimiento manual registrado.");
      await loadData();
    } catch (error) {
      // Hallazgo menor (barrido de estándares, 2026-07-27): error.message puede traer un
      // texto técnico crudo del cliente HTTP ("Not Found"/"Forbidden") en vez del motivo
      // real que manda el backend — getApiErrorMessage ya sabe leer el payload del error
      // primero (T-5).
      //
      // Fix del reviewer a este comentario (2026-07-27): un 404/403 "pelado" (sin body
      // del servidor) NO cae en el texto en criollo de acá abajo — errors.js reconoce
      // "Not Found"/"Forbidden" como bare HTTP statusText (ver
      // HTTP_STATUSTEXT_EXACT/esErrorDeTransporteOStatusText en lib/errors.js) y los
      // reemplaza por el genérico de red ("No se pudo conectar. Revisá tu conexión e
      // intentá de nuevo."), no por "No se pudo registrar el movimiento.". Este segundo
      // texto solo se usa cuando NO hay ningún mensaje aprovechable en absoluto (error
      // sin message ni payload). El comportamiento en sí no cambia con este fix, solo se
      // corrige lo que el comentario prometía.
      showError(getApiErrorMessage(error, "No se pudo registrar el movimiento."));
      throw error;
    }
  };

  const handleUpdateManualMovement = async (id, payload) => {
    try {
      await api.put(`/treasury/manual-movements/${id}`, payload);
      showSuccess("Movimiento manual actualizado.");
      await loadData();
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo actualizar el movimiento."));
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
      showError(getApiErrorMessage(error, "No se pudo anular el movimiento."));
    }
  };

  return {
    handleDownloadPdf,
    handleViewPdf,
    handleDownloadReceiptPdf,
    handleIssueReceipt,
    handleVoidReceipt,
    handleRetryInvoice,
    handleAnnulInvoice,
    handleCreateManualMovement,
    handleUpdateManualMovement,
    handleDeleteManualMovement,
  };
}
