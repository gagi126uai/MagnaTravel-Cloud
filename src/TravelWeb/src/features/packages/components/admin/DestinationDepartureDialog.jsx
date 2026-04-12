import { Button } from "../../../../components/ui/button";

const inputClass =
  "w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-950 dark:text-white";

export function DestinationDepartureDialog({ open, draft, editing, onChange, onClose, onSubmit }) {
  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/60 p-4 backdrop-blur-sm">
      <div className="w-full max-w-3xl rounded-lg border border-slate-200 bg-white shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="border-b border-slate-200 px-6 py-4 dark:border-slate-800">
          <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
            {editing ? "Editar salida" : "Nueva salida"}
          </h3>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Completa la informacion comercial de esta fecha.
          </p>
        </div>

        <form onSubmit={onSubmit} className="space-y-5 p-6">
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <Field label="Fecha de salida">
              <input
                type="date"
                value={draft.startDate}
                onChange={(event) => onChange("startDate", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Noches">
              <input
                type="number"
                min="1"
                value={draft.nights}
                onChange={(event) => onChange("nights", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Moneda">
              <select value={draft.currency} onChange={(event) => onChange("currency", event.target.value)} className={inputClass}>
                <option value="USD">USD</option>
                <option value="ARS">ARS</option>
                <option value="EUR">EUR</option>
              </select>
            </Field>

            <Field label="Tarifa">
              <input
                type="number"
                min="0"
                step="0.01"
                value={draft.salePrice}
                onChange={(event) => onChange("salePrice", event.target.value)}
                className={inputClass}
              />
            </Field>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Hotel">
              <input
                type="text"
                value={draft.hotelName}
                onChange={(event) => onChange("hotelName", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Transporte">
              <input
                type="text"
                value={draft.transportLabel}
                onChange={(event) => onChange("transportLabel", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Regimen">
              <input
                type="text"
                value={draft.mealPlan}
                onChange={(event) => onChange("mealPlan", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Base">
              <input
                type="text"
                value={draft.roomBase}
                onChange={(event) => onChange("roomBase", event.target.value)}
                className={inputClass}
              />
            </Field>
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            <label className="flex items-center gap-3 rounded-md border border-slate-200 px-4 py-3 dark:border-slate-800">
              <input
                type="checkbox"
                checked={draft.isPrimary}
                onChange={(event) => onChange("isPrimary", event.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
              />
              <div>
                <p className="text-sm font-medium text-slate-900 dark:text-white">Salida destacada</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">Se usa como referencia principal del destino.</p>
              </div>
            </label>

            <label className="flex items-center gap-3 rounded-md border border-slate-200 px-4 py-3 dark:border-slate-800">
              <input
                type="checkbox"
                checked={draft.isActive}
                onChange={(event) => onChange("isActive", event.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
              />
              <div>
                <p className="text-sm font-medium text-slate-900 dark:text-white">Visible en el sitio</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">Si queda apagada, la salida no se muestra al cliente.</p>
              </div>
            </label>
          </div>

          <div className="flex justify-end gap-2 border-t border-slate-200 pt-4 dark:border-slate-800">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancelar
            </Button>
            <Button type="submit">{editing ? "Guardar salida" : "Agregar salida"}</Button>
          </div>
        </form>
      </div>
    </div>
  );
}

function Field({ label, children }) {
  return (
    <label className="block space-y-2">
      <span className="text-sm font-medium text-slate-700 dark:text-slate-200">{label}</span>
      {children}
    </label>
  );
}
