import { useEffect, useState } from "react";
import { Loader2, RefreshCw, Save, ShieldCheck } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { REQUEST_TYPE_LABELS } from "../features/approvals/api/approvalsApi";
import { formatDateTime } from "../lib/utils";

// B1.15 Fase B'' (2026-05-11): editor de policies de workflow. Solo Admin.
//
// Renderea una fila por cada policy persistida en BD. El toggle RequiresApproval
// cambia inmediatamente al guardar; los campos override (expiration, cooldown,
// notes) son edit-then-save por fila para no spammear PUTs.
//
// Defaults globales que aplican cuando los overrides estan vacios se leen de
// OperationalFinanceSettings: ApprovalDefaultExpirationDays (7) y
// ApprovalRejectionCooldownHours (1). Se muestran como placeholder.
export default function ApprovalPoliciesTab() {
  const [policies, setPolicies] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await api.get("/approval-policies");
      setPolicies(Array.isArray(data) ? data : []);
    } catch (err) {
      setError(err);
      setPolicies([]);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, []);

  const handleSave = async (requestType, payload) => {
    try {
      const updated = await api.put(`/approval-policies/${requestType}`, payload);
      setPolicies((current) => current.map((p) => (p.requestType === requestType ? updated : p)));
      showSuccess("Workflow actualizado.");
    } catch (err) {
      showError(err?.message || "No se pudo actualizar el workflow.");
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-amber-100 p-2 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
            <ShieldCheck className="h-5 w-5" />
          </div>
          <div>
            <h2 className="text-lg font-bold text-slate-900 dark:text-white">Workflows de aprobación</h2>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Definí qué acciones del sistema requieren autorización previa del back-office.
            </p>
          </div>
        </div>
        <button
          type="button"
          onClick={load}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800"
        >
          <RefreshCw className="h-3.5 w-3.5" />
          Refrescar
        </button>
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-16 text-slate-400">
          <Loader2 className="h-6 w-6 animate-spin" />
        </div>
      ) : error ? (
        <div className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-900/20 px-4 py-3 text-sm text-rose-700 dark:text-rose-300">
          No se pudieron cargar las policies. ¿Sos Admin? El permiso requerido es <code>approvals.policies</code>.
        </div>
      ) : (
        <div className="overflow-hidden rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900 divide-y divide-slate-100 dark:divide-slate-800">
          {policies.map((policy) => (
            <PolicyRow key={policy.requestType} policy={policy} onSave={handleSave} />
          ))}
          {policies.length === 0 ? (
            <div className="px-6 py-10 text-center text-sm text-slate-500">No hay policies configuradas.</div>
          ) : null}
        </div>
      )}
    </div>
  );
}

function PolicyRow({ policy, onSave }) {
  const [requiresApproval, setRequiresApproval] = useState(policy.requiresApproval);
  const [expirationDays, setExpirationDays] = useState(policy.expirationDaysOverride ?? "");
  const [cooldownHours, setCooldownHours] = useState(policy.cooldownHoursOverride ?? "");
  const [notes, setNotes] = useState(policy.notes ?? "");
  const [saving, setSaving] = useState(false);

  const dirty =
    requiresApproval !== policy.requiresApproval ||
    String(expirationDays) !== String(policy.expirationDaysOverride ?? "") ||
    String(cooldownHours) !== String(policy.cooldownHoursOverride ?? "") ||
    (notes || "") !== (policy.notes || "");

  const handleSave = async () => {
    setSaving(true);
    try {
      await onSave(policy.requestType, {
        requiresApproval,
        expirationDaysOverride: expirationDays === "" ? null : Number(expirationDays),
        cooldownHoursOverride: cooldownHours === "" ? null : Number(cooldownHours),
        notes: notes.trim() || null,
      });
    } finally {
      setSaving(false);
    }
  };

  const label = REQUEST_TYPE_LABELS[policy.requestType] || policy.requestType;

  return (
    <div className="px-6 py-5 space-y-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1 flex-1 min-w-0">
          <div className="flex items-center gap-3">
            <span className="font-semibold text-slate-900 dark:text-white">{label}</span>
            <code className="text-[10px] font-mono text-slate-400">{policy.requestType}</code>
          </div>
          <div className="text-xs text-slate-500 dark:text-slate-400">
            Actualizada {formatDateTime(policy.updatedAt)}
            {policy.updatedByUserName ? ` por ${policy.updatedByUserName}` : ""}
          </div>
        </div>
        <label className="inline-flex items-center gap-2 cursor-pointer select-none">
          <input
            type="checkbox"
            checked={requiresApproval}
            onChange={(event) => setRequiresApproval(event.target.checked)}
            className="h-5 w-5 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
          />
          <span className="text-sm font-medium text-slate-700 dark:text-slate-200">Requiere aprobación</span>
        </label>
      </div>

      <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
        <div>
          <label className="block text-[10px] font-bold uppercase tracking-wider text-slate-400 mb-1">
            Expiración (días)
          </label>
          <input
            type="number"
            min={1}
            max={365}
            value={expirationDays}
            onChange={(event) => setExpirationDays(event.target.value)}
            placeholder="Default global"
            disabled={!requiresApproval}
            className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm disabled:opacity-50"
          />
        </div>
        <div>
          <label className="block text-[10px] font-bold uppercase tracking-wider text-slate-400 mb-1">
            Cooldown post-rechazo (horas)
          </label>
          <input
            type="number"
            min={0}
            max={720}
            value={cooldownHours}
            onChange={(event) => setCooldownHours(event.target.value)}
            placeholder="Default global"
            disabled={!requiresApproval}
            className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm disabled:opacity-50"
          />
        </div>
        <div>
          <label className="block text-[10px] font-bold uppercase tracking-wider text-slate-400 mb-1">
            Notas internas
          </label>
          <input
            type="text"
            value={notes}
            onChange={(event) => setNotes(event.target.value)}
            placeholder="Opcional"
            className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
          />
        </div>
      </div>

      <div className="flex justify-end">
        <button
          type="button"
          onClick={handleSave}
          disabled={!dirty || saving}
          className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-indigo-700 disabled:opacity-50"
        >
          <Save className="h-3.5 w-3.5" />
          {saving ? "Guardando…" : "Guardar"}
        </button>
      </div>
    </div>
  );
}
