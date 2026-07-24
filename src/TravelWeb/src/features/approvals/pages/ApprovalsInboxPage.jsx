import { useMemo, useState } from "react";
import { Check, RefreshCw, ShieldCheck, X } from "lucide-react";
import { approvalsApi, REQUEST_TYPE_LABELS } from "../api/approvalsApi";
import { useApprovalsList } from "../hooks/useApprovals";
import { showError, showSuccess } from "../../../alerts";
import ApprovalStatusPill from "../components/ApprovalStatusPill";
import { formatDateTime } from "../../../lib/utils";

// B1.15 Fase B' Parte 2 (2026-05-11): bandeja del reviewer (Admin/Colaborador).
// Lista todos los ApprovalRequest en estado Pending y permite aprobar o rechazar
// inline con campo de motivo.
export default function ApprovalsInboxPage() {
  const { items, loading, error, reload } = useApprovalsList("pending");
  const [typeFilter, setTypeFilter] = useState("all");

  const filtered = useMemo(() => {
    if (typeFilter === "all") return items;
    return items.filter((it) => it.requestType === typeFilter);
  }, [items, typeFilter]);

  const typeOptions = useMemo(() => {
    const set = new Set(items.map((it) => it.requestType));
    return Array.from(set);
  }, [items]);

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-amber-100 p-2 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
          <ShieldCheck className="h-5 w-5" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">Aprobaciones pendientes</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Mirá los pedidos de tus vendedores y aprobalos o rechazalos.
          </p>
        </div>
      </div>

      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs font-bold uppercase tracking-wider text-slate-400">Filtrar por tipo</span>
            <select
              value={typeFilter}
              onChange={(event) => setTypeFilter(event.target.value)}
              className="rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
            >
              <option value="all">Todos ({items.length})</option>
              {typeOptions.map((type) => (
                <option key={type} value={type}>
                  {REQUEST_TYPE_LABELS[type] || type}
                </option>
              ))}
            </select>
          </div>
          <button
            type="button"
            onClick={reload}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800"
          >
            <RefreshCw className="h-3.5 w-3.5" />
            Refrescar
          </button>
        </div>

        {loading ? (
          <div className="px-6 py-10 text-center text-sm text-slate-500">Cargando…</div>
        ) : error ? (
          <div className="px-6 py-10 text-center text-sm text-rose-600">No se pudo cargar la bandeja.</div>
        ) : filtered.length === 0 ? (
          <div className="px-6 py-10 text-center text-sm text-slate-500">
            {items.length === 0 ? "No hay solicitudes pendientes." : "No hay solicitudes del tipo seleccionado."}
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {filtered.map((request) => (
              <ApprovalInboxRow key={request.publicId} request={request} onResolved={reload} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function ApprovalInboxRow({ request, onResolved }) {
  const [notes, setNotes] = useState("");
  const [busy, setBusy] = useState(null); // "approve" | "reject" | null

  const handleApprove = async () => {
    setBusy("approve");
    try {
      await approvalsApi.approve(request.publicId, notes.trim() || null);
      showSuccess("Solicitud aprobada.");
      onResolved?.();
    } catch (err) {
      showError(err?.message || "No se pudo aprobar.");
    } finally {
      setBusy(null);
    }
  };

  const handleReject = async () => {
    const trimmed = notes.trim();
    if (trimmed.length < 5) {
      showError("Para rechazar indicá un motivo de al menos 5 caracteres.");
      return;
    }
    setBusy("reject");
    try {
      await approvalsApi.reject(request.publicId, trimmed);
      showSuccess("Solicitud rechazada.");
      onResolved?.();
    } catch (err) {
      showError(err?.message || "No se pudo rechazar.");
    } finally {
      setBusy(null);
    }
  };

  const requestedAtFmt = formatDateTime(request.requestedAt);
  const expiresAtFmt = formatDateTime(request.expiresAt);

  return (
    <div className="px-6 py-5 space-y-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <div className="flex items-center gap-2">
            <span className="font-semibold text-slate-900 dark:text-white">
              {REQUEST_TYPE_LABELS[request.requestType] || request.requestType}
            </span>
            <ApprovalStatusPill status={request.status} />
          </div>
          <div className="text-xs text-slate-500 dark:text-slate-400">
            Solicitada por <span className="font-medium">{request.requestedByUserName || request.requestedByUserId}</span> · {requestedAtFmt}
          </div>
          <div className="text-xs text-slate-500 dark:text-slate-400">
            Entidad: <span className="font-mono">{request.entityType} #{request.entityId}</span> · Expira {expiresAtFmt}
          </div>
        </div>
      </div>

      {request.reason ? (
        <div className="rounded-lg bg-slate-50 dark:bg-slate-800/60 px-3 py-2 text-sm">
          <span className="block text-[10px] font-semibold uppercase tracking-wider text-slate-400">Motivo del solicitante</span>
          <span className="text-slate-800 dark:text-slate-200">{request.reason}</span>
        </div>
      ) : null}

      <div className="space-y-2">
        <label className="block text-[10px] font-semibold uppercase tracking-wider text-slate-400">
          Tus notas (opcional al aprobar, requerido al rechazar)
        </label>
        <textarea
          value={notes}
          onChange={(event) => setNotes(event.target.value)}
          rows={2}
          placeholder="Comentario para auditoría / aviso al solicitante."
          className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm"
        />
      </div>

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={handleApprove}
          disabled={busy !== null}
          className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-50"
        >
          <Check className="h-4 w-4" />
          {busy === "approve" ? "Aprobando…" : "Aprobar"}
        </button>
        <button
          type="button"
          onClick={handleReject}
          disabled={busy !== null}
          className="inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-semibold text-white hover:bg-rose-700 disabled:opacity-50"
        >
          <X className="h-4 w-4" />
          {busy === "reject" ? "Rechazando…" : "Rechazar"}
        </button>
      </div>
    </div>
  );
}
