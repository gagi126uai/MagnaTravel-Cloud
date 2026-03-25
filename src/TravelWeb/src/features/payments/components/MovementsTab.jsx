import { useEffect, useState } from "react";
import {
  ArrowDownLeft,
  ArrowUpRight,
  Landmark,
  Pencil,
  Plus,
  Trash2,
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
  relatedReservaPublicId: "",
  relatedSupplierPublicId: "",
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
            relatedReservaPublicId: movement.reservaPublicId || movement.relatedReservaPublicId || "",
            relatedSupplierPublicId: movement.supplierPublicId || movement.relatedSupplierPublicId || "",
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
        relatedReservaPublicId: form.relatedReservaPublicId || null,
        relatedSupplierPublicId: form.relatedSupplierPublicId || null,
      });
      onClose();
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4 backdrop-blur-sm">
      <div className="w-full max-w-2xl overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
          <div>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
              {movement ? "Editar ajuste manual" : "Nuevo ajuste manual"}
            </h3>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Este movimiento impacta caja pero no modifica balances de reservas.
            </p>
          </div>
          <button type="button" onClick={onClose} className="text-slate-400 hover:text-slate-600">
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="grid gap-4 p-6 md:grid-cols-2">
          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Direccion</label>
            <select
              value={form.direction}
              onChange={(event) => handleChange("direction", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
            >
              <option value="Income">Ingreso</option>
              <option value="Expense">Egreso</option>
            </select>
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Monto</label>
            <input
              type="number"
              step="0.01"
              min="0.01"
              value={form.amount}
              onChange={(event) => handleChange("amount", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
              required
            />
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Fecha</label>
            <input
              type="datetime-local"
              value={form.occurredAt}
              onChange={(event) => handleChange("occurredAt", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
            />
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Metodo</label>
            <input
              type="text"
              value={form.method}
              onChange={(event) => handleChange("method", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
              required
            />
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Categoria</label>
            <input
              type="text"
              value={form.category}
              onChange={(event) => handleChange("category", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
              required
            />
          </div>

          <div>
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Referencia</label>
            <input
              type="text"
              value={form.reference}
              onChange={(event) => handleChange("reference", event.target.value)}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
            />
          </div>

          <div className="md:col-span-2">
            <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">Descripcion</label>
            <textarea
              value={form.description}
              onChange={(event) => handleChange("description", event.target.value)}
              rows={3}
              className="w-full rounded-xl border border-slate-300 px-3 py-2 dark:border-slate-700 dark:bg-slate-950 dark:text-white"
              required
            />
          </div>

          <div className="md:col-span-2 flex justify-end gap-3 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="rounded-xl border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 dark:border-slate-700 dark:text-slate-200"
            >
              Cancelar
            </button>
            <button
              type="submit"
              disabled={saving}
              className="rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:opacity-60"
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
  showHeader = true,
}) {
  const [editingMovement, setEditingMovement] = useState(null);
  const [showModal, setShowModal] = useState(false);

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
      await onUpdateManualMovement(editingMovement.sourcePublicId, payload);
      return;
    }

    await onCreateManualMovement(payload);
  };

  return (
    <div className="space-y-6">
      {showHeader && (
        <div className="flex flex-col justify-between gap-4 md:flex-row md:items-center">
          <div>
            <h2 className="text-lg font-semibold text-slate-900 dark:text-white">Caja</h2>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Libro de caja con ingresos por cobranzas, egresos a proveedores y ajustes manuales.
            </p>
          </div>
          {isAdmin && (
            <button
              type="button"
              onClick={openCreate}
              className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800"
            >
              <Plus className="h-4 w-4" />
              Nuevo ajuste manual
            </button>
          )}
        </div>
      )}

      {!showHeader && isAdmin && (
        <div className="flex justify-end">
          <button
            type="button"
            onClick={openCreate}
            className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800"
          >
            <Plus className="h-4 w-4" />
            Nuevo ajuste manual
          </button>
        </div>
      )}

      <div className="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900 md:block">
        <table className="w-full border-collapse text-left">
          <thead>
            <tr className="border-b border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-800/50">
              <th className="px-6 py-4 text-[11px] font-semibold uppercase tracking-wider text-slate-500">Fecha</th>
              <th className="px-6 py-4 text-[11px] font-semibold uppercase tracking-wider text-slate-500">Origen</th>
              <th className="px-6 py-4 text-[11px] font-semibold uppercase tracking-wider text-slate-500">Detalle</th>
              <th className="px-6 py-4 text-[11px] font-semibold uppercase tracking-wider text-slate-500">Metodo</th>
              <th className="px-6 py-4 text-right text-[11px] font-semibold uppercase tracking-wider text-slate-500">Monto</th>
              <th className="px-6 py-4 text-right text-[11px] font-semibold uppercase tracking-wider text-slate-500">Accion</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {movements.map((movement) => {
              const isIncome = movement.direction === "Income";
              const isManual = movement.isManual;

              return (
                <tr key={`${movement.sourceType}-${movement.sourcePublicId}`} className="transition-colors hover:bg-slate-50 dark:hover:bg-slate-800/40">
                  <td className="px-6 py-4 text-sm text-slate-600 dark:text-slate-400">
                    {new Date(movement.occurredAt).toLocaleString("es-AR")}
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex items-center gap-3">
                      <div className={`rounded-lg p-2 ${isIncome ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400" : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"}`}>
                        {isIncome ? <ArrowDownLeft className="h-4 w-4" /> : <ArrowUpRight className="h-4 w-4" />}
                      </div>
                      <div>
                        <div className="text-sm font-semibold text-slate-900 dark:text-white">
                          {sourceLabels[movement.sourceType] || movement.sourceType}
                        </div>
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          {movement.numeroReserva ? `Reserva ${movement.numeroReserva}` : movement.supplierName || "Sin vinculo"}
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
                          className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          onClick={() => onDeleteManualMovement(movement)}
                          className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-rose-600 dark:hover:bg-slate-800"
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    ) : (
                      <span className="text-xs font-semibold uppercase tracking-wider text-slate-400">Automatico</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div className="space-y-3 md:hidden">
        {movements.map((movement) => {
          const isIncome = movement.direction === "Income";
          return (
            <div key={`${movement.sourceType}-${movement.sourcePublicId}`} className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
              <div className="flex items-start justify-between gap-3">
                <div className="flex items-start gap-3">
                  <div className={`rounded-xl p-2 ${isIncome ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400" : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"}`}>
                    {isIncome ? <ArrowDownLeft className="h-4 w-4" /> : <ArrowUpRight className="h-4 w-4" />}
                  </div>
                  <div>
                    <div className="text-sm font-bold text-slate-900 dark:text-white">
                      {sourceLabels[movement.sourceType] || movement.sourceType}
                    </div>
                    <div className="text-xs text-slate-500 dark:text-slate-400">
                      {new Date(movement.occurredAt).toLocaleDateString("es-AR")} · {movement.method}
                    </div>
                    <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">{movement.description}</div>
                  </div>
                </div>
                <div className={`text-sm font-black ${isIncome ? "text-emerald-600 dark:text-emerald-400" : "text-rose-600 dark:text-rose-400"}`}>
                  {isIncome ? "+" : "-"}
                  {currency.format(movement.amount)}
                </div>
              </div>
              {(movement.numeroReserva || movement.supplierName) && (
                <div className="mt-3 border-t border-slate-100 pt-3 text-xs text-slate-500 dark:border-slate-800 dark:text-slate-400">
                  {movement.numeroReserva ? `Reserva ${movement.numeroReserva}` : movement.supplierName}
                </div>
              )}
            </div>
          );
        })}
      </div>

      {movements.length === 0 && (
        <div className="rounded-3xl border-2 border-dashed border-slate-200 bg-slate-50/50 py-20 text-center dark:border-slate-800 dark:bg-slate-800/20">
          <div className="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full border border-slate-100 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <Landmark className="h-8 w-8 text-slate-300" />
          </div>
          <h3 className="mb-1 text-lg font-bold text-slate-900 dark:text-white">Caja sin movimientos</h3>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Todavia no hay ingresos o egresos registrados en caja.
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
