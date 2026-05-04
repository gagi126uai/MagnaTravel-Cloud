import { useEffect, useState } from "react";
import { X, Calendar } from "lucide-react";

// ISO yyyy-mm-dd para inputs type=date a partir de un valor que puede ser ISO/Date.
function toDateInputValue(value) {
    if (!value) return "";
    try {
        const d = new Date(value);
        if (isNaN(d.getTime())) return "";
        const yyyy = d.getFullYear();
        const mm = String(d.getMonth() + 1).padStart(2, "0");
        const dd = String(d.getDate()).padStart(2, "0");
        return `${yyyy}-${mm}-${dd}`;
    } catch {
        return "";
    }
}

/**
 * Modal compacto para editar StartDate y EndDate de una reserva. Pensado para
 * reparar reservas viejas sin fecha o para overridear las fechas computadas
 * automaticamente desde los servicios.
 *
 * onSave({ startDate, endDate, clearStartDate, clearEndDate }) — el padre se
 * encarga de hacer el PATCH al backend.
 */
export function EditReservaDatesModal({ isOpen, reserva, onClose, onSave }) {
    const [startDate, setStartDate] = useState("");
    const [endDate, setEndDate] = useState("");
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState(null);

    useEffect(() => {
        if (isOpen) {
            setStartDate(toDateInputValue(reserva?.startDate));
            setEndDate(toDateInputValue(reserva?.endDate));
            setError(null);
            setSaving(false);
        }
    }, [isOpen, reserva?.startDate, reserva?.endDate]);

    if (!isOpen) return null;

    const originalStart = toDateInputValue(reserva?.startDate);
    const originalEnd = toDateInputValue(reserva?.endDate);

    const handleSubmit = async (e) => {
        e.preventDefault();
        if (startDate && endDate && endDate < startDate) {
            setError("La fecha de regreso no puede ser anterior a la de salida.");
            return;
        }
        setError(null);
        setSaving(true);
        try {
            await onSave({
                startDate: startDate || null,
                endDate: endDate || null,
                // Si el campo viene vacio Y antes habia algo, hay que pedirle al backend
                // que lo borre (sino, null sin clear no toca).
                clearStartDate: !startDate && Boolean(originalStart),
                clearEndDate: !endDate && Boolean(originalEnd),
            });
        } catch (err) {
            setError(err?.message || "No se pudieron guardar las fechas.");
        } finally {
            setSaving(false);
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
            <div
                className="w-full max-w-md rounded-2xl bg-white shadow-2xl dark:bg-slate-900"
                onClick={(e) => e.stopPropagation()}
            >
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <h3 className="flex items-center gap-2 text-lg font-bold text-slate-900 dark:text-white">
                        <Calendar className="w-5 h-5 text-indigo-500" />
                        Editar fechas del viaje
                    </h3>
                    <button onClick={onClose} className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800">
                        <X className="w-4 h-4" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="px-6 py-5 space-y-4">
                    <p className="text-sm text-slate-500 dark:text-slate-400">
                        Estas fechas tambien se recalculan cuando agregas o editas servicios. Editarlas aca tiene sentido cuando los servicios no tienen fechas claras o queres un override manual.
                    </p>

                    <div>
                        <label className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-600 dark:text-slate-400">
                            Salida
                        </label>
                        <input
                            type="date"
                            value={startDate}
                            onChange={(e) => setStartDate(e.target.value)}
                            disabled={saving}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                        />
                    </div>

                    <div>
                        <label className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-600 dark:text-slate-400">
                            Regreso
                        </label>
                        <input
                            type="date"
                            value={endDate}
                            onChange={(e) => setEndDate(e.target.value)}
                            disabled={saving}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                        />
                        <p className="mt-1 text-xs text-slate-400">Dejar vacio borra la fecha.</p>
                    </div>

                    {error && (
                        <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-900 dark:bg-rose-950/30 dark:text-rose-300">
                            {error}
                        </div>
                    )}

                    <div className="flex flex-col-reverse gap-2 pt-2 sm:flex-row sm:justify-end">
                        <button
                            type="button"
                            onClick={onClose}
                            disabled={saving}
                            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={saving}
                            className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white hover:bg-indigo-700 disabled:opacity-50"
                        >
                            {saving ? "Guardando..." : "Guardar fechas"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
