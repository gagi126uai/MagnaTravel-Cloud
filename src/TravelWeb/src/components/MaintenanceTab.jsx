import { useState } from "react";
import { Wrench, Play, ArrowUpRight, CheckCircle2, AlertCircle, Clock } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { getApiErrorMessage } from "../lib/errors";

/**
 * Solapa de Mantenimiento del panel Admin: agrupa acciones manuales que el
 * sistema normalmente corre en background (Hangfire). Pensada para que un
 * admin pueda intervenir sin esperar al schedule diario.
 *
 * Por ahora incluye:
 * - Lifecycle de reservas (Reservado -> Operativo y Operativo -> Cerrado).
 *
 * Si en el futuro se suman mas jobs (limpieza de pendientes, recompute de
 * balances, etc.), se agregan como nuevas "Cards" con la misma estructura.
 */
export default function MaintenanceTab() {
    const [running, setRunning] = useState(false);
    const [lastResult, setLastResult] = useState(null); // { promoted, closed, ranAt, error? }

    const runLifecycle = async () => {
        setRunning(true);
        try {
            const result = await api.post("/admin/maintenance/lifecycle-run");
            const repaired = result?.repaired ?? 0;
            const promoted = result?.promoted ?? 0;
            const closed = result?.closed ?? 0;
            setLastResult({
                repaired,
                promoted,
                closed,
                ranAt: new Date(),
                error: null,
            });
            const total = repaired + promoted + closed;
            if (total === 0) {
                showSuccess("Lifecycle ejecutado: no habia reservas para reparar, promover ni cerrar");
            } else {
                const parts = [];
                if (repaired > 0) parts.push(`${repaired} reparadas`);
                if (promoted > 0) parts.push(`${promoted} promovidas`);
                if (closed > 0) parts.push(`${closed} cerradas`);
                showSuccess(`Lifecycle ejecutado: ${parts.join(", ")}`);
            }
        } catch (error) {
            const message = getApiErrorMessage(error, "No se pudo ejecutar el lifecycle.");
            setLastResult({ repaired: 0, promoted: 0, closed: 0, ranAt: new Date(), error: message });
            showError(message, "Error en lifecycle");
        } finally {
            setRunning(false);
        }
    };

    return (
        <div className="space-y-6">
            <div className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
                <div className="flex items-start gap-3">
                    <div className="rounded-xl bg-indigo-50 p-2 dark:bg-indigo-950/40">
                        <Wrench className="h-5 w-5 text-indigo-600 dark:text-indigo-400" />
                    </div>
                    <div>
                        <h2 className="text-lg font-bold text-slate-900 dark:text-white">Mantenimiento</h2>
                        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                            Tareas que el sistema corre en segundo plano automaticamente. Aca podes ejecutarlas a mano si necesitas que se apliquen ya, sin esperar al horario programado.
                        </p>
                    </div>
                </div>
            </div>

            <div className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
                <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                    <div className="flex-1">
                        <div className="flex items-center gap-2">
                            <ArrowUpRight className="h-5 w-5 text-emerald-500" />
                            <h3 className="text-base font-bold text-slate-900 dark:text-white">
                                Lifecycle de reservas
                            </h3>
                        </div>
                        <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
                            Repara fechas faltantes computandolas desde los servicios cargados (vuelos, hoteles, transfers), promueve <span className="font-semibold">Reservado &rarr; Operativo</span> cuando arranca el viaje o se cobro toda la reserva, y cierra <span className="font-semibold">Operativo &rarr; Cerrado</span> para reservas cuyo viaje ya termino.
                        </p>
                        <div className="mt-3 inline-flex items-center gap-1.5 rounded-lg bg-slate-100 px-2.5 py-1 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                            <Clock className="h-3 w-3" />
                            Programado diariamente a las 3:00 UTC
                        </div>
                    </div>

                    <button
                        type="button"
                        onClick={runLifecycle}
                        disabled={running}
                        className="inline-flex items-center justify-center gap-2 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-bold text-white shadow-lg shadow-indigo-200 transition-all hover:bg-indigo-700 active:scale-95 disabled:cursor-not-allowed disabled:opacity-60 dark:shadow-none lg:min-w-[200px]"
                    >
                        <Play className={`h-4 w-4 ${running ? "animate-pulse" : ""}`} />
                        {running ? "Ejecutando..." : "Ejecutar ahora"}
                    </button>
                </div>

                {lastResult && (
                    <div className={`mt-5 rounded-xl border p-4 ${lastResult.error
                        ? "border-rose-200 bg-rose-50 dark:border-rose-900 dark:bg-rose-950/30"
                        : "border-emerald-200 bg-emerald-50 dark:border-emerald-900 dark:bg-emerald-950/30"
                    }`}>
                        <div className="flex items-start gap-3">
                            {lastResult.error ? (
                                <AlertCircle className="h-5 w-5 flex-shrink-0 text-rose-600 dark:text-rose-400" />
                            ) : (
                                <CheckCircle2 className="h-5 w-5 flex-shrink-0 text-emerald-600 dark:text-emerald-400" />
                            )}
                            <div className="flex-1">
                                {lastResult.error ? (
                                    <>
                                        <p className="text-sm font-bold text-rose-900 dark:text-rose-200">Falló la ejecución</p>
                                        <p className="mt-1 text-xs text-rose-700 dark:text-rose-300">{lastResult.error}</p>
                                    </>
                                ) : (
                                    <>
                                        <p className="text-sm font-bold text-emerald-900 dark:text-emerald-200">
                                            {(lastResult.repaired + lastResult.promoted + lastResult.closed) === 0
                                                ? "No habia nada para procesar"
                                                : "Lifecycle aplicado"}
                                        </p>
                                        <div className="mt-2 grid grid-cols-3 gap-3 text-xs">
                                            <div className="rounded-lg bg-white/70 px-3 py-2 dark:bg-slate-900/40">
                                                <div className="text-emerald-700 dark:text-emerald-300">Fechas reparadas</div>
                                                <div className="mt-0.5 text-lg font-black text-emerald-900 dark:text-emerald-100">
                                                    {lastResult.repaired}
                                                </div>
                                            </div>
                                            <div className="rounded-lg bg-white/70 px-3 py-2 dark:bg-slate-900/40">
                                                <div className="text-emerald-700 dark:text-emerald-300">Promovidas a Operativo</div>
                                                <div className="mt-0.5 text-lg font-black text-emerald-900 dark:text-emerald-100">
                                                    {lastResult.promoted}
                                                </div>
                                            </div>
                                            <div className="rounded-lg bg-white/70 px-3 py-2 dark:bg-slate-900/40">
                                                <div className="text-emerald-700 dark:text-emerald-300">Cerradas</div>
                                                <div className="mt-0.5 text-lg font-black text-emerald-900 dark:text-emerald-100">
                                                    {lastResult.closed}
                                                </div>
                                            </div>
                                        </div>
                                    </>
                                )}
                                <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">
                                    Ejecutado {lastResult.ranAt.toLocaleTimeString("es-AR")}
                                </p>
                            </div>
                        </div>
                    </div>
                )}
            </div>

            <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-xs text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-300">
                <strong className="font-bold">Nota:</strong> el cierre automatico solo aplica a reservas que tengan <span className="font-semibold">fecha de regreso</span> cargada. Si una reserva quedo en Operativo de un viaje viejo, revisa primero que la fecha de regreso este completa desde el detalle de la reserva.
            </div>
        </div>
    );
}
