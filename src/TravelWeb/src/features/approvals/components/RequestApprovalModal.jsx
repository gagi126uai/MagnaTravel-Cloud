import { useState } from "react";
import { AlertCircle, ShieldCheck, X } from "lucide-react";
import { approvalsApi, REQUEST_TYPE_LABELS } from "../api/approvalsApi";
import { showError, showSuccess } from "../../../alerts";

// B1.15 Fase B' Parte 2 (2026-05-11): modal reutilizable para solicitar
// aprobacion. Cualquier accion bloqueada en UI puede dispararlo pasando:
//
//   <RequestApprovalModal
//     isOpen={open}
//     onClose={() => setOpen(false)}
//     onCreated={(approval) => { ... }}
//     requestType="InvoiceAnnulment"
//     entityType="Invoice"
//     entityId={invoice.id}                  // legacy int id
//     entityLabel="Factura B 00001-00000027"  // texto humano para mostrar
//     metadata={...}                           // opcional, JSON serializable
//   />
//
// Idempotencia: si el backend devuelve un Pending preexistente (mismo combo),
// el modal lo trata igual que un Create exitoso — la UI lo refleja.
export default function RequestApprovalModal({
  isOpen,
  onClose,
  onCreated,
  requestType,
  entityType,
  entityId,
  entityLabel,
  metadata = null,
}) {
  const [reason, setReason] = useState("");
  const [submitting, setSubmitting] = useState(false);

  if (!isOpen) return null;

  const handleSubmit = async (event) => {
    event.preventDefault();
    const trimmed = reason.trim();
    if (trimmed.length < 10) {
      showError("Indicá un motivo de al menos 10 caracteres.");
      return;
    }

    setSubmitting(true);
    try {
      const created = await approvalsApi.create({
        requestType,
        entityType,
        entityId: Number(entityId),
        reason: trimmed,
        metadata: metadata ? JSON.stringify(metadata) : null,
      });
      showSuccess("Solicitud enviada. El back-office la va a revisar.");
      onCreated?.(created);
      setReason("");
      onClose();
    } catch (err) {
      if (err?.status === 429) {
        showError(err?.message || "Ya enviaste una solicitud similar hace poco. Esperá el cooldown.");
      } else {
        showError(err?.message || "No se pudo enviar la solicitud.");
      }
    } finally {
      setSubmitting(false);
    }
  };

  const typeLabel = REQUEST_TYPE_LABELS[requestType] || requestType;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm">
      <div className="bg-white dark:bg-slate-900 rounded-xl shadow-2xl w-full max-w-lg overflow-hidden border border-gray-200 dark:border-slate-700">
        <div className="flex items-start justify-between gap-4 border-b border-slate-100 dark:border-slate-800 px-6 py-5">
          <div className="flex items-start gap-3">
            <div className="rounded-lg bg-amber-100 p-2 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
              <ShieldCheck className="h-5 w-5" />
            </div>
            <div>
              <h2 className="text-lg font-bold text-slate-900 dark:text-white">Solicitar aprobación</h2>
              <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">{typeLabel}</p>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="px-6 py-5 space-y-4">
          {entityLabel ? (
            <div className="rounded-lg bg-slate-50 dark:bg-slate-800/50 px-3 py-2 text-sm">
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-400">Sobre</span>
              <div className="font-medium text-slate-900 dark:text-white">{entityLabel}</div>
            </div>
          ) : null}

          <div>
            <label className="block text-xs font-semibold uppercase tracking-wide text-slate-500 mb-1.5">
              Motivo (mínimo 10 caracteres)
            </label>
            <textarea
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              rows={4}
              placeholder="Explicá por qué necesitás esta autorización."
              className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm"
            />
          </div>

          <div className="flex items-start gap-2 text-xs text-slate-500 dark:text-slate-400">
            <AlertCircle className="h-4 w-4 mt-0.5 flex-shrink-0" />
            <p>Un Administrador o Colaborador va a revisar la solicitud. Te notificamos cuando se resuelva.</p>
          </div>

          <div className="flex justify-end gap-2 pt-2 border-t border-slate-100 dark:border-slate-800">
            <button
              type="button"
              onClick={onClose}
              disabled={submitting}
              className="rounded-lg border border-slate-300 dark:border-slate-600 px-4 py-2 text-sm font-medium text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
            >
              Cancelar
            </button>
            <button
              type="submit"
              disabled={submitting || reason.trim().length < 10}
              className="rounded-lg bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
            >
              {submitting ? "Enviando…" : "Enviar solicitud"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
