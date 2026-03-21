import { useEffect, useState } from "react";
import { AlertTriangle, Settings2, ShieldAlert } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { Button } from "./ui/button";

const defaultSettings = {
  requireFullPaymentForOperativeStatus: true,
  requireFullPaymentForVoucher: true,
  afipInvoiceControlMode: "AllowAgentOverrideWithReason",
  enableUpcomingUnpaidReservationNotifications: true,
  upcomingUnpaidReservationAlertDays: 7,
};

export default function OperationalFinanceSettingsTab() {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState(defaultSettings);

  useEffect(() => {
    const loadSettings = async () => {
      setLoading(true);
      try {
        const response = await api.get("/settings/operational-finance");
        setForm({ ...defaultSettings, ...response });
      } catch (error) {
        console.error("Error loading operational finance settings:", error);
        showError("No se pudo cargar la configuración operativa.");
      } finally {
        setLoading(false);
      }
    };

    loadSettings();
  }, []);

  const updateField = (field, value) => {
    setForm((current) => ({ ...current, [field]: value }));
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    setSaving(true);
    try {
      await api.put("/settings/operational-finance", {
        ...form,
        upcomingUnpaidReservationAlertDays: Number(form.upcomingUnpaidReservationAlertDays || 0),
      });
      showSuccess("Configuración operativa guardada.");
    } catch (error) {
      showError(error.message || "No se pudo guardar la configuración.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <form onSubmit={handleSubmit} className="space-y-6">
      <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
        <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
          <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg text-indigo-600 dark:text-indigo-400">
            <Settings2 className="h-5 w-5" />
          </div>
          <div>
            <h3 className="font-semibold text-slate-900 dark:text-white">Operativa, Cobranzas y Facturación</h3>
            <p className="text-xs text-slate-500">Reglas económicas que gobiernan operativo, vouchers, AFIP y alertas.</p>
          </div>
        </div>

        <div className="p-6 space-y-8">
          <div className="grid gap-6 md:grid-cols-2">
            <label className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
              <input
                type="checkbox"
                checked={form.requireFullPaymentForOperativeStatus}
                onChange={(event) => updateField("requireFullPaymentForOperativeStatus", event.target.checked)}
                className="mt-1 rounded border-slate-300"
                disabled={loading}
              />
              <div>
                <div className="text-sm font-semibold text-slate-900 dark:text-white">Exigir pago total para pasar a Operativo</div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  Si está activo, una reserva con deuda no puede pasar al estado operativo.
                </div>
              </div>
            </label>

            <label className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 flex items-start gap-3">
              <input
                type="checkbox"
                checked={form.requireFullPaymentForVoucher}
                onChange={(event) => updateField("requireFullPaymentForVoucher", event.target.checked)}
                className="mt-1 rounded border-slate-300"
                disabled={loading}
              />
              <div>
                <div className="text-sm font-semibold text-slate-900 dark:text-white">Exigir pago total para emitir voucher</div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  Afecta tanto el PDF como el envío del voucher por WhatsApp.
                </div>
              </div>
            </label>
          </div>

          <div className="grid gap-6 md:grid-cols-2">
            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4">
              <div className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                <ShieldAlert className="w-4 h-4 text-amber-500" />
                Control de AFIP
              </div>
              <p className="text-xs text-slate-500 dark:text-slate-400 mt-1 mb-4">
                Define si AFIP requiere pago total o si el agente puede tener la última palabra con motivo obligatorio.
              </p>
              <select
                value={form.afipInvoiceControlMode}
                onChange={(event) => updateField("afipInvoiceControlMode", event.target.value)}
                className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm"
                disabled={loading}
              >
                <option value="FullPaymentRequired">Pago total obligatorio</option>
                <option value="AllowAgentOverrideWithReason">Permitir override del agente con motivo</option>
              </select>
            </div>

            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 p-4 space-y-4">
              <label className="flex items-start gap-3">
                <input
                  type="checkbox"
                  checked={form.enableUpcomingUnpaidReservationNotifications}
                  onChange={(event) => updateField("enableUpcomingUnpaidReservationNotifications", event.target.checked)}
                  className="mt-1 rounded border-slate-300"
                  disabled={loading}
                />
                <div>
                  <div className="text-sm font-semibold text-slate-900 dark:text-white">Alertas por reservas próximas con deuda</div>
                  <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                    Notifica al responsable de la reserva y a los administradores.
                  </div>
                </div>
              </label>

              <div>
                <label className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5">
                  Días previos para alertar
                </label>
                <input
                  type="number"
                  min="1"
                  max="60"
                  value={form.upcomingUnpaidReservationAlertDays}
                  onChange={(event) => updateField("upcomingUnpaidReservationAlertDays", event.target.value)}
                  className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2 text-sm"
                  disabled={loading}
                />
              </div>
            </div>
          </div>

          <div className="rounded-2xl border border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-900/20 p-4 text-sm text-amber-900 dark:text-amber-200">
            <div className="flex items-start gap-3">
              <AlertTriangle className="w-5 h-5 mt-0.5 flex-shrink-0" />
              <div className="space-y-1">
                <div className="font-semibold">Reglas duras de esta etapa</div>
                <p>
                  El override del agente aplica solo a AFIP. Operativo y voucher siguen bloqueados si la reserva tiene deuda.
                </p>
              </div>
            </div>
          </div>
        </div>

        <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
          <Button type="submit" disabled={saving || loading} className="bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg px-6">
            {saving ? "Guardando..." : "Guardar configuración"}
          </Button>
        </div>
      </div>
    </form>
  );
}
