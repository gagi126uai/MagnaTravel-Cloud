import { CheckCircle2, AlertTriangle } from "lucide-react";
import {
    RESTORE_MODO_PRUEBA,
    construirResumenExitoPruebaRestore,
    construirResumenExitoRealRestore,
} from "../lib/dangerRestoreLogic";

/**
 * Resultado de "Ver qué contiene" / "Reponer configuración", DENTRO de la ficha (retoque en
 * vivo de Gastón, 2026-07-30: verificó el flujo real y vio que el resultado REEMPLAZABA toda
 * la ficha — frase, contraseña y motivo ya tipeados se perdían, rozando P-7). Ahora es un
 * panel MÁS, arriba de los campos, que no desmonta nada: "Cerrar" solo cierra este panel, la
 * ficha entera sigue montada con todo lo cargado y el botón "Volver a esta copia" sigue a
 * mano sin tener que volver a escribir nada.
 *
 * Se separó de RestoreBackupFicha.jsx (antes vivía inline ahí) para que ese archivo no crezca
 * más de lo necesario — este panel tiene su propia complejidad real (dos formas de resumen
 * bien distintas según el modo).
 */
export function RestoreResultadoInline({ resultadoInline, onCerrar }) {
    const esPrueba = resultadoInline.modo === RESTORE_MODO_PRUEBA;
    const resumen = esPrueba
        ? construirResumenExitoPruebaRestore(resultadoInline.data)
        : construirResumenExitoRealRestore(resultadoInline.data);

    return (
        <div
            className="space-y-3 rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900/60"
            data-testid="restore-resultado-inline"
        >
            <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-2 text-sm font-bold text-emerald-800 dark:text-emerald-300">
                    <CheckCircle2 className="h-4 w-4" />
                    {esPrueba ? "Ver qué contiene" : "Configuración restaurada"}
                </div>
                <button
                    type="button"
                    onClick={onCerrar}
                    data-testid="restore-resultado-inline-cerrar"
                    className="text-xs font-bold text-slate-500 hover:underline dark:text-slate-400"
                >
                    Cerrar
                </button>
            </div>

            {esPrueba ? (
                <>
                    <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100">
                        {/* El título de esta caja lo arma el helper (resumen.encabezado), no un texto
                            hardcodeado acá que diría casi lo mismo que la línea de arriba. */}
                        <p className="mb-1.5 font-semibold">{resumen.encabezado}</p>
                        {resumen.sinDatos ? (
                            <p>{resumen.mensajeSinDatos}</p>
                        ) : (
                            <ul className="space-y-0.5">
                                {resumen.filas.map((fila) => (
                                    <li key={fila.clave}>{fila.cantidad} {fila.etiqueta}</li>
                                ))}
                            </ul>
                        )}
                        <p className="mt-2 text-xs text-emerald-800 dark:text-emerald-200">
                            Esto se hizo en una base de prueba separada: no se tocó ningún dato real.
                        </p>
                    </div>
                    {/* Hallazgo del dueño ("no deja en claro cómo lo hace"): explica el proceso en
                        criollo, separado del resultado. */}
                    <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300">
                        {resumen.comoSeHizo}
                    </div>
                </>
            ) : (
                <div
                    className={`rounded-xl border p-4 text-sm whitespace-pre-line ${
                        resumen.incluyeAfip
                            ? "border-amber-300 bg-amber-50 text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-100"
                            : "border-emerald-200 bg-emerald-50 text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100"
                    }`}
                >
                    {resumen.incluyeAfip && (
                        <p className="mb-1.5 flex items-center gap-1.5 font-bold uppercase text-xs tracking-wide">
                            <AlertTriangle className="h-3.5 w-3.5" />
                            Se tocó la conexión con AFIP
                        </p>
                    )}
                    <p className="font-semibold">{resumen.mensaje}</p>
                </div>
            )}

            {/* La advertencia del motor (ej. "backup de una versión anterior, no se pudieron
                calcular todos los conteos") aplica a las DOS acciones. */}
            {resumen.advertencia && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-300">
                    {resumen.advertencia}
                </div>
            )}
        </div>
    );
}
