import { Button } from "../../../../components/ui/button";

const inputClass =
  "w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-950 dark:text-white";

export function CountryModal({ open, form, saving, onChange, onClose, onSubmit }) {
  if (!open) {
    return null;
  }

  const isEditing = Boolean(form?.publicId);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/60 p-4 backdrop-blur-sm">
      <div className="w-full max-w-md rounded-lg border border-slate-200 bg-white shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="border-b border-slate-200 px-5 py-4 dark:border-slate-800">
          <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
            {isEditing ? "Editar pais" : "Nuevo pais"}
          </h2>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Usa un nombre claro para ordenar el catalogo y mantener el sitio consistente.
          </p>
        </div>

        <form onSubmit={onSubmit} className="space-y-5 p-5">
          <label className="block space-y-2">
            <span className="text-sm font-medium text-slate-700 dark:text-slate-200">Nombre del pais</span>
            <input
              type="text"
              value={form?.name || ""}
              onChange={(event) => onChange(event.target.value)}
              className={inputClass}
              placeholder="Ejemplo: Brasil"
              autoFocus
            />
          </label>

          <div className="flex justify-end gap-2 border-t border-slate-200 pt-4 dark:border-slate-800">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancelar
            </Button>
            <Button type="submit" disabled={saving}>
              {saving ? "Guardando..." : isEditing ? "Guardar cambios" : "Crear pais"}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}
