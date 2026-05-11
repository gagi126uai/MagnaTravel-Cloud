import { useMemo, useState } from "react";
import { Inbox, RefreshCw } from "lucide-react";
import { REQUEST_TYPE_LABELS } from "../api/approvalsApi";
import { useApprovalsList } from "../hooks/useApprovals";
import ApprovalStatusPill from "../components/ApprovalStatusPill";

// B1.15 Fase B' Parte 2 (2026-05-11): vista del solicitante. Muestra todas sus
// solicitudes en cualquier estado, ordenadas por fecha descendente. El backend
// ya filtra por usuario (RequestedByUserId == currentUserId), no hace falta
// filter en frontend.
export default function MyApprovalRequestsPage() {
  const { items, loading, error, reload } = useApprovalsList("mine");
  const [statusFilter, setStatusFilter] = useState("all");

  const filtered = useMemo(() => {
    if (statusFilter === "all") return items;
    return items.filter((it) => it.status === statusFilter);
  }, [items, statusFilter]);

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-indigo-100 p-2 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
          <Inbox className="h-5 w-5" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">Mis solicitudes</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">Estado de las aprobaciones que solicitaste.</p>
        </div>
      </div>

      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs font-bold uppercase tracking-wider text-slate-400">Estado</span>
            <select
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value)}
              className="rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
            >
              <option value="all">Todas ({items.length})</option>
              <option value="Pending">Pendientes</option>
              <option value="Approved">Aprobadas</option>
              <option value="Rejected">Rechazadas</option>
              <option value="Consumed">Consumidas</option>
              <option value="Expired">Expiradas</option>
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
          <div className="px-6 py-10 text-center text-sm text-rose-600">No se pudo cargar.</div>
        ) : filtered.length === 0 ? (
          <div className="px-6 py-10 text-center text-sm text-slate-500">No hay solicitudes.</div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {filtered.map((request) => (
              <MyApprovalRow key={request.publicId} request={request} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function MyApprovalRow({ request }) {
  const requestedAt = new Date(request.requestedAt).toLocaleString("es-AR");
  const resolvedAt = request.resolvedAt ? new Date(request.resolvedAt).toLocaleString("es-AR") : null;
  const expiresAt = new Date(request.expiresAt).toLocaleString("es-AR");

  return (
    <div className="px-6 py-5 space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-semibold text-slate-900 dark:text-white">
          {REQUEST_TYPE_LABELS[request.requestType] || request.requestType}
        </span>
        <ApprovalStatusPill status={request.status} />
      </div>
      <div className="text-xs text-slate-500 dark:text-slate-400">
        <span className="font-mono">{request.entityType} #{request.entityId}</span> · solicitada {requestedAt} · expira {expiresAt}
      </div>
      {request.reason ? (
        <div className="text-sm text-slate-700 dark:text-slate-300">
          <span className="text-[10px] font-semibold uppercase tracking-wider text-slate-400">Motivo: </span>
          {request.reason}
        </div>
      ) : null}
      {resolvedAt ? (
        <div className="text-xs text-slate-500 dark:text-slate-400">
          Resuelta {resolvedAt} por {request.resolvedByUserName || request.resolvedByUserId}
          {request.resolverNotes ? <span className="ml-2 italic">— "{request.resolverNotes}"</span> : null}
        </div>
      ) : null}
    </div>
  );
}
