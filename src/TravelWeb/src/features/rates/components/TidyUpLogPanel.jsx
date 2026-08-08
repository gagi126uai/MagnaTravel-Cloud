/**
 * "Ver qué ordenó": el rastro de todo lo que el bibliotecario unió o corrigió solo
 * (spec firmada 2026-08-07, §6 / Q3=B). Vive SOLO acá, dentro de la bandeja de
 * Repetidos — la lista normal del Tarifario nunca muestra de dónde salió un producto
 * (derogación 2026-06-08).
 *
 * Cada línea puede tener "Deshacer": la línea NUNCA se borra (2026-08-03), solo se
 * apaga su botón cuando ya se deshizo.
 *
 * Permiso `tarifario.edit` (fix ronda 2 de review, P-9): deshacer también pide ese
 * permiso en el servidor — sin él, el botón ni se muestra (mismo criterio que la
 * bandeja que lo contiene).
 */
import { useEffect, useState } from "react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { formatDate } from "../../../lib/utils";
import { puedeDeshacerse, marcarComoDeshecha } from "../lib/duplicatesTrayLogic";

export function TidyUpLogPanel({ onClose, onUndone }) {
    const puedeEditar = hasPermission("tarifario.edit");
    const [acciones, setAcciones] = useState([]);
    const [cargando, setCargando] = useState(true);
    const [error, setError] = useState(false);
    const [deshaciendoId, setDeshaciendoId] = useState(null);

    const cargar = async () => {
        setCargando(true);
        setError(false);
        try {
            const data = await api.get("/rates/tidy-up-log");
            setAcciones(data?.actions || []);
        } catch {
            setError(true);
        } finally {
            setCargando(false);
        }
    };

    useEffect(() => {
        cargar();
    }, []);

    const handleDeshacer = async (action) => {
        setDeshaciendoId(action.publicId);
        try {
            await api.post(`/rates/tidy-up-log/${action.publicId}/undo`);
            setAcciones((prev) => marcarComoDeshecha(prev, action.publicId));
            showSuccess("Deshecho.");
            // La bandeja de arriba puede tener un candidato para revisar de nuevo tras
            // este deshacer (fix ronda 2, hallazgo review: antes quedaba desactualizada).
            onUndone?.();
        } catch (err) {
            showError(err.payload?.message || "No se pudo deshacer. Probá de nuevo.");
        } finally {
            setDeshaciendoId(null);
        }
    };

    return (
        <div className="mt-3 rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900" data-testid="tidy-up-log-panel">
            <div className="mb-3 flex items-center justify-between">
                <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">Lo que ordenó el sistema</p>
                <button type="button" onClick={onClose} className="text-xs font-semibold text-slate-500 hover:text-slate-700 dark:text-slate-400">
                    Cerrar
                </button>
            </div>

            {cargando && <p className="text-sm text-slate-400">Cargando…</p>}

            {!cargando && error && (
                <p className="text-sm text-rose-600">
                    No se pudo traer el registro.{" "}
                    <button type="button" onClick={cargar} className="font-semibold underline">Probar de nuevo</button>
                </p>
            )}

            {!cargando && !error && acciones.length === 0 && (
                <p className="text-sm text-slate-400">Todavía no ordenó nada.</p>
            )}

            {!cargando && !error && acciones.length > 0 && (
                <ul className="space-y-2">
                    {acciones.map((accion) => (
                        <li key={accion.publicId} className="flex items-start justify-between gap-3 text-sm">
                            <div className="min-w-0">
                                <p className="text-slate-700 dark:text-slate-200">{accion.summary}</p>
                                {accion.detail && <p className="text-xs text-slate-500 dark:text-slate-400">{accion.detail}</p>}
                                <p className="text-xs text-slate-400">{formatDate(accion.performedAt)}</p>
                            </div>
                            {puedeEditar && puedeDeshacerse(accion) && (
                                <button
                                    type="button"
                                    onClick={() => handleDeshacer(accion)}
                                    disabled={deshaciendoId === accion.publicId}
                                    className="shrink-0 text-xs font-semibold text-indigo-600 hover:underline disabled:opacity-60 dark:text-indigo-400"
                                >
                                    {deshaciendoId === accion.publicId ? "Deshaciendo…" : "Deshacer"}
                                </button>
                            )}
                        </li>
                    ))}
                </ul>
            )}
        </div>
    );
}
