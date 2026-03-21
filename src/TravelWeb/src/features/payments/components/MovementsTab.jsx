import { useEffect, useState } from "react";
import {
  ArrowDownLeft,
  ArrowUpRight,
  Landmark,
  Pencil,
  Plus,
  Trash2,
  Wallet,
  X,
} from "lucide-react";

const currency = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 2,
});

const sourceLabels = {
  CustomerPayment: "Cobranza",
  SupplierPayment: "Pago a proveedor",
  ManualAdjustment: "Ajuste manual",
};

const emptyForm = {
  direction: "Income",
  amount: "",
  occurredAt: "",
  method: "Transferencia",
  category: "",
  description: "",
  reference: "",
  relatedReservaId: "",
  relatedSupplierId: "",
};

const toLocalDateTime = (value) => {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  const offset = date.getTimezoneOffset();
  return new Date(date.getTime() - offset * 60000).toISOString().slice(0, 16);
};

function ManualMovementModal({ open, onClose, onSubmit, movement }) {
  const [form, setForm] = useState(emptyForm);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!open) {
      return;
    }

    setForm(
      movement
        ? {
            direction: movement.direction || "Income",
            amount: movement.amount || "",
            occurredAt: toLocalDateTime(movement.occurredAt),
            method: movement.method || "Transferencia",
            category: movement.category || "",
            description: movement.description || "",
            reference: movement.reference || "",
            relatedReservaId: movement.reservaId || "",
            relatedSupplierId: movement.supplierId || "",
          }
        : emptyForm
    );
  }, [movement, open]);

  if (!open) {
    return null;
  }

  const handleChange = (field, value) => {
    setForm((current) => ({ ...current, [field]: value }));
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    setSaving(true);
    try {
      await onSubmit({
        direction: form.direction,
        amount: Number(form.amount),
        occurredAt: form.occurredAt ? new Date(form.occurredAt).toISOString() : new Date().toISOString(),
        method: form.method,
        category: form.category,
        description: form.description,
        reference: form.reference || null,
        relatedReservaId: form.relatedReservaId ? Number(form.relatedReservaId) : null,
        relatedSupplierId: form.relatedSupplierId ? Number(form.relatedSupplierId) : null,
      });
      onClose();
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 bg-black/40 backdrop-blur-sm flex items-center justify-center p-4">
      <div className="w-full max-w-2xl rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-2xl overflow-hidden">
        <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center justify-between">
          <div>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
              {movement ? "Editar ajuste manual" : "Nuevo ajuste manual"}
            </h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Este movimiento impacta caja pero no modifica balances de reservas.
            </p>
          </div>
          <button type="button" onClick={onClose} className="text-slate-400 hover:text-slate-600">
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 grid gap-4 md:grid-cols-2">
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Dirección</label>
            <select
              value={form.direction}
              onChange={(event) => handleChange("direction", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
            >
              <option value="Income">Ingreso</option>
              <option value="Expense">Egreso</option>
            </select>
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Monto</label>
            <input
              type="number"
              step="0.01"
              min="0.01"
              value={form.amount}
              onChange={(event) => handleChange("amount", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
              required
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Fecha</label>
            <input
              type="datetime-local"
              value={form.occurredAt}
              onChange={(event) => handleChange("occurredAt", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Método</label>
            <input
              type="text"
              value={form.method}
              onChange={(event) => handleChange("method", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
              required
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Categoría</label>
            <input
              type="text"
              value={form.category}
              onChange={(event) => handleChange("category", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
              placeholder="Caja, ajuste, retiro, reposición..."
              required
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Referencia</label>
            <input
              type="text"
              value={form.reference}
              onChange={(event) => handleChange("reference", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
            />
          </div>

          <div className="md:col-span-2">
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Descripción</label>
            <textarea
              value={form.description}
              onChange={(event) => handleChange("description", event.target.value)}
              rows={3}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
              required
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Reserva vinculada</label>
            <input
              type="number"
              min="1"
              value={form.relatedReservaId}
              onChange={(event) => handleChange("relatedReservaId", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1">Proveedor vinculado</label>
            <input
              type="number"
              min="1"
              value={form.relatedSupplierId}
              onChange={(event) => handleChange("relatedSupplierId", event.target.value)}
              className="w-full rounded-xl border border-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-white px-3 py-2"
            />
          </div>

          <div className="md:col-span-2 flex justify-end gap-3 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 rounded-xl border border-slate-300 dark:border-slate-700 text-sm font-medium text-slate-700 dark:text-slate-200"
            >
              Cancelar
            </button>
            <button
              type="submit"
              disabled={saving}
              className="px-4 py-2 rounded-xl bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60"
            >
              {saving ? "Guardando..." : movement ? "Guardar cambios" : "Registrar movimiento"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

export function MovementsTab({
  movements,
  isAdmin,
  onCreateManualMovement,
  onUpdateManualMovement,
  onDeleteManualMovement,
}) {
  const [visibleCount, setVisibleCount] = useState(25);
  const [editingMovement, setEditingMovement] = useState(null);
  const [showModal, setShowModal] = useState(false);

  const visibleMovements = movements.slice(0, visibleCount);

  const openCreate = () => {
    setEditingMovement(null);
    setShowModal(true);
  };

  const openEdit = (movement) => {
    setEditingMovement(movement);
    setShowModal(true);
  };

  const closeModal = () => {
    setEditingMovement(null);
    setShowModal(false);
  };

  const handleSubmit = async (payload) => {
    if (editingMovement) {
      await onUpdateManualMovement(Number(editingMovement.sourceId), payload);
      return;
    }

    await onCreateManualMovement(payload);
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold text-slate-900 dark:text-white">Movimientos de caja</h2>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Ingresos por cobranzas, egresos a proveedores y ajustes manuales.
          </p>
        </div>
        {isAdmin && (
          <button
            type="button"
            onClick={openCreate}
            className="inline-flex items-center gap-2 px-4 py-2 rounded-xl bg-slate-900 text-white text-sm font-medium hover:bg-slate-800"
          >
            <Plus className="w-4 h-4" />
            Nuevo ajuste manual
          </button>
        )}
      </div>

      <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm">
        <table className="w-full text-left border-collapse">
          <thead>
            <tr className="bg-slate-50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Fecha</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Origen</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Detalle</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Método</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Monto</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Acción</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {visibleMovements.map((movement) => {
              const isIncome = movement.direction === "Income";
              const isManual = movement.isManual;

              return (
                <tr key={`${movement.sourceType}-${movement.sourceId}`} className="hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors">
                  <td className="px-6 py-4 text-sm text-slate-600 dark:text-slate-400">
                    {new Date(movement.occurredAt).toLocaleString("es-AR")}
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex items-center gap-3">
                      <div
                        className={`p-2 rounded-lg ${
                          isIncome
                            ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400"
                            : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"
                        }`}
                      >
                        {isIncome ? <ArrowDownLeft className="w-4 h-4" /> : <ArrowUpRight className="w-4 h-4" />}
                      </div>
                      <div>
                        <div className="text-sm font-semibold text-slate-900 dark:text-white">
                          {sourceLabels[movement.sourceType] || movement.sourceType}
                        </div>
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          {movement.numeroReserva ? `Reserva ${movement.numeroReserva}` : movement.supplierName || "Sin vínculo"}
                        </div>
                      </div>
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="text-sm font-medium text-slate-900 dark:text-white">{movement.description}</div>
                    {movement.reference && (
                      <div className="text-xs text-slate-500 dark:text-slate-400">Ref. {movement.reference}</div>
                    )}
                  </td>
                  <td className="px-6 py-4 text-sm text-slate-600 dark:text-slate-400">{movement.method}</td>
                  <td className="px-6 py-4 text-right">
                    <span className={`text-sm font-bold ${isIncome ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400"}`}>
                      {isIncome ? "+" : "-"}
                      {currency.format(movement.amount)}
                    </span>
                  </td>
                  <td className="px-6 py-4 text-right">
                    {isManual && isAdmin ? (
                      <div className="flex items-center justify-end gap-2">
                        <button
                          type="button"
                          onClick={() => openEdit(movement)}
                          className="p-2 rounded-lg text-slate-500 hover:text-indigo-600 hover:bg-slate-100 dark:hover:bg-slate-800"
                        >
                          <Pencil className="w-4 h-4" />
                        </button>
                        <button
                          type="button"
                          onClick={() => onDeleteManualMovement(movement)}
                          className="p-2 rounded-lg text-slate-500 hover:text-rose-600 hover:bg-slate-100 dark:hover:bg-slate-800"
                        >
                          <Trash2 className="w-4 h-4" />
                        </button>
                      </div>
                    ) : (
                      <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">
                        Automático
                      </span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div className="md:hidden space-y-3">
        {visibleMovements.map((movement) => {
          const isIncome = movement.direction === "Income";
          return (
            <div key={`${movement.sourceType}-${movement.sourceId}`} className="bg-white dark:bg-slate-900 rounded-2xl p-4 border border-slate-200 dark:border-slate-800 shadow-sm">
              <div className="flex justify-between items-start gap-3">
                <div className="flex items-start gap-3">
                  <div
                    className={`p-2 rounded-xl ${
                      isIncome
                        ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400"
                        : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"
                    }`}
                  >
                    {isIncome ? <ArrowDownLeft className="w-4 h-4" /> : <ArrowUpRight className="w-4 h-4" />}
                  </div>
                  <div>
                    <div className="text-sm font-bold text-slate-900 dark:text-white">
                      {sourceLabels[movement.sourceType] || movement.sourceType}
                    </div>
                    <div className="text-xs text-slate-500 dark:text-slate-400">
                      {new Date(movement.occurredAt).toLocaleDateString("es-AR")} · {movement.method}
                    </div>
                    <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">{movement.description}</div>
                  </div>
                </div>
                <div className={`text-sm font-black ${isIncome ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400"}`}>
                  {isIncome ? "+" : "-"}
                  {currency.format(movement.amount)}
                </div>
              </div>
              {(movement.numeroReserva || movement.supplierName) && (
                <div className="mt-3 pt-3 border-t border-slate-100 dark:border-slate-800 text-xs text-slate-500 dark:text-slate-400">
                  {movement.numeroReserva ? `Reserva ${movement.numeroReserva}` : movement.supplierName}
                </div>
              )}
            </div>
          );
        })}
      </div>

      {movements.length > visibleCount && (
        <div className="p-4 text-center">
          <button
            type="button"
            onClick={() => setVisibleCount((current) => current + 25)}
            className="px-6 py-2 text-sm font-medium text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-white transition-colors"
          >
            Cargar más movimientos ({movements.length - visibleCount} restantes)
          </button>
        </div>
      )}

      {movements.length === 0 && (
        <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
          <div className="w-16 h-16 bg-white dark:bg-slate-900 rounded-full flex items-center justify-center mx-auto mb-4 shadow-sm border border-slate-100 dark:border-slate-800">
            <Landmark className="w-8 h-8 text-slate-300" />
          </div>
          <h3 className="text-lg font-bold text-slate-900 dark:text-white mb-1">Sin movimientos</h3>
          <p className="text-slate-500 dark:text-slate-400 text-sm">
            Todavía no hay ingresos o egresos registrados en caja.
          </p>
        </div>
      )}

      <ManualMovementModal
        open={showModal}
        onClose={closeModal}
        onSubmit={handleSubmit}
        movement={editingMovement}
      />
    </div>
  );
}
