/**
 * Ficha EN LÍNEA para registrar un reembolso que el operador devolvió, imputándolo
 * a una anulación que estaba esperando esa plata.
 *
 * Se despliega debajo de los botones "Registrar pago" / "Usar saldo a favor" en la
 * solapa "Cuenta corriente" de la ficha del operador (spec §4, 2026-07-01).
 *
 * Flujo:
 *   1. Al abrir: carga los reembolsos pendientes de ESTE operador y los aplana a
 *      filas seleccionables (una fila = una anulación + una moneda).
 *   2. El usuario elige UNA fila obligatoriamente (no se permite un monto suelto
 *      sin destino). La moneda queda fijada por la fila elegida.
 *   3. Completa monto, fecha (requerida), método y referencia (opcionales).
 *   4. Al confirmar: UNA sola llamada atómica a POST /operator-refunds/record-and-allocate
 *      (registra el ingreso Y lo imputa; si la imputación falla, el ingreso tampoco queda).
 *
 * Idempotencia (crítico, es plata): la llave se genera UNA vez al montar este componente
 * (osea, al abrir la ficha) y se REUSA en cada reintento de la MISMA acción — si falla la
 * red y el usuario reintenta con el mismo botón, va la misma llave y el servidor no duplica
 * el reembolso. Como el padre desmonta este componente al cancelar o al tener éxito, la
 * PRÓXIMA vez que se abre la ficha (para otro reembolso) se genera una llave NUEVA
 * automáticamente, sin necesidad de resetear nada a mano.
 *
 * Props:
 *   - supplierId: string — publicId del proveedor
 *   - onRegistrado: () => void — callback al éxito (el padre refresca extracto + recuadros + solapa Reembolsos)
 *   - onCancelar: () => void — callback al cerrar la ficha sin guardar
 */

import { useCallback, useEffect, useState } from "react";
import { Loader2, RotateCcw, X } from "lucide-react";
import { hasPermission } from "../../../auth";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { operatorRefundsApi } from "../api/operatorRefundsApi";
import {
    aplanarReembolsosPendientesPorMoneda,
    validarFormularioReembolsoRecibido,
    construirTextoCuentaReembolso,
} from "../lib/supplierPageLogic";

// Mismo set de métodos que PagarProveedorInline: el reembolso también es un movimiento
// de plata físico (transferencia, efectivo, cheque, tarjeta), así que reusamos las
// mismas etiquetas para que el cajero no tenga que aprender un vocabulario nuevo.
const METODOS_REEMBOLSO = [
    { value: "Transfer", label: "Transferencia" },
    { value: "Cash", label: "Efectivo" },
    { value: "Check", label: "Cheque" },
    { value: "Card", label: "Tarjeta" },
];

const fechaHoy = () => new Date().toISOString().split("T")[0];

export function RegistrarReembolsoRecibidoInline({ supplierId, onRegistrado, onCancelar }) {
    // ─── Idempotencia: la llave se genera UNA vez cuando el componente se monta
    // (es decir, cuando el usuario abre la ficha) y no cambia mientras siga montado.
    // useState con función inicializadora garantiza que crypto.randomUUID() se llame
    // una sola vez, no en cada render. ────────────────────────────────────────────
    const [idempotencyKey] = useState(() => crypto.randomUUID());

    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    // ─── Carga de reembolsos pendientes ────────────────────────────────────────
    const [pendientes, setPendientes] = useState([]);
    const [loadingPendientes, setLoadingPendientes] = useState(true);
    const [errorCarga, setErrorCarga] = useState(null);

    // ─── Estado del formulario ──────────────────────────────────────────────────
    const [filaSeleccionada, setFilaSeleccionada] = useState(null);
    const [monto, setMonto] = useState("");
    const [fecha, setFecha] = useState(fechaHoy());
    const [metodo, setMetodo] = useState("Transfer");
    const [referencia, setReferencia] = useState("");
    const [errorValidacion, setErrorValidacion] = useState(null);

    // ─── Estado del submit (anti-doble-click) ──────────────────────────────────
    const [guardando, setGuardando] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    const cargarPendientes = useCallback(async () => {
        setLoadingPendientes(true);
        setErrorCarga(null);
        try {
            const items = await operatorRefundsApi.getPendingBySupplier(supplierId);
            setPendientes(Array.isArray(items) ? items : []);
        } catch (error) {
            setErrorCarga(
                getApiErrorMessage(error, "No se pudo cargar la lista de reembolsos pendientes.")
            );
        } finally {
            setLoadingPendientes(false);
        }
    }, [supplierId]);

    // Carga al montar la ficha.
    useEffect(() => {
        cargarPendientes();
    }, [cargarPendientes]);

    // Filas seleccionables: una fila = una anulación + una moneda (ver lógica en supplierPageLogic).
    const filas = aplanarReembolsosPendientesPorMoneda(pendientes);

    const handleSeleccionarFila = (fila) => {
        setFilaSeleccionada(fila);
        setErrorValidacion(null);
        setErrorGuardar(null);
        // Precargamos el monto con el estimado (el usuario puede ajustarlo si el
        // operador devolvió un poco distinto por redondeo o deducciones).
        setMonto(fila.amountsMasked ? "" : String(fila.estimatedAmount));
    };

    const handleConfirmar = async () => {
        setErrorGuardar(null);

        const mensajeValidacion = validarFormularioReembolsoRecibido({
            filaSeleccionada,
            monto,
            fecha,
        });
        if (mensajeValidacion) {
            setErrorValidacion(mensajeValidacion);
            return;
        }
        setErrorValidacion(null);

        setGuardando(true);
        try {
            await operatorRefundsApi.recordAndAllocate({
                supplierPublicId: supplierId,
                bookingCancellationPublicId: filaSeleccionada.bookingCancellationPublicId,
                receivedAmount: parseFloat(monto),
                currency: filaSeleccionada.currency,
                receivedAt: new Date(fecha).toISOString(),
                method: metodo || null,
                reference: referencia.trim() || null,
                idempotencyKey,
            });

            showSuccess(
                `Reembolso registrado e imputado a la reserva ${filaSeleccionada.numeroReserva || ""}.`
            );
            onRegistrado();
        } catch (error) {
            // La ficha queda abierta con todo lo cargado (incluida la misma llave de
            // idempotencia) para que el usuario pueda reintentar sin duplicar el reembolso.
            setErrorGuardar(
                getApiErrorMessage(
                    error,
                    "No se pudo registrar el reembolso. Revisá la conexión y probá de nuevo."
                )
            );
        } finally {
            setGuardando(false);
        }
    };

    // ─── Render: cargando pendientes ───────────────────────────────────────────
    if (loadingPendientes) {
        return (
            <div
                className="rounded-xl border-2 border-indigo-200 bg-indigo-50/40 dark:border-indigo-900/40 dark:bg-indigo-950/10 p-5"
                data-testid="registrar-reembolso-inline"
                data-state="loading"
            >
                <div className="flex items-center gap-2 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Cargando reembolsos pendientes de este operador...
                </div>
            </div>
        );
    }

    // ─── Render: error al cargar ────────────────────────────────────────────────
    if (errorCarga) {
        return (
            <div
                className="rounded-xl border-2 border-rose-200 bg-rose-50/40 dark:border-rose-900/40 dark:bg-rose-950/10 p-5 space-y-3"
                data-testid="registrar-reembolso-inline"
                data-state="error"
            >
                <p className="text-sm text-rose-700 dark:text-rose-300" role="alert">{errorCarga}</p>
                <div className="flex justify-end gap-2">
                    <button
                        type="button"
                        onClick={cargarPendientes}
                        className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 transition-colors"
                    >
                        Reintentar
                    </button>
                    <button
                        type="button"
                        onClick={onCancelar}
                        className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 transition-colors"
                    >
                        Cerrar
                    </button>
                </div>
            </div>
        );
    }

    // ─── Render: sin reembolsos pendientes (estado vacío, no deja continuar) ───
    if (filas.length === 0) {
        return (
            <div
                className="rounded-xl border-2 border-slate-200 bg-slate-50/40 dark:border-slate-700 dark:bg-slate-900/20 p-5 space-y-3"
                data-testid="registrar-reembolso-inline"
                data-state="empty"
            >
                <p className="text-sm text-slate-600 dark:text-slate-400">
                    No hay reembolsos pendientes de este operador.
                </p>
                <div className="flex justify-end">
                    <button
                        type="button"
                        onClick={onCancelar}
                        className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 transition-colors"
                    >
                        Cerrar
                    </button>
                </div>
            </div>
        );
    }

    // ─── Render: formulario principal ──────────────────────────────────────────
    return (
        <div
            className="rounded-xl border-2 border-indigo-200 bg-indigo-50/40 dark:border-indigo-900/40 dark:bg-indigo-950/10 p-5 space-y-4"
            data-testid="registrar-reembolso-inline"
            data-state="ready"
        >
            {/* Cabecera */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <RotateCcw className="h-4 w-4 text-indigo-600" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                        Registrar reembolso recibido
                    </h4>
                </div>
                <button
                    type="button"
                    onClick={onCancelar}
                    disabled={guardando}
                    className="rounded p-1 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 disabled:opacity-50"
                    aria-label="Cerrar ficha de reembolso recibido"
                >
                    <X className="h-4 w-4" />
                </button>
            </div>

            {/* Selector obligatorio: a qué anulación se imputa. No se permite un monto
                suelto sin destino (regla dura de la spec). */}
            <div className="space-y-1">
                <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">
                    ¿A qué reembolso pendiente corresponde? *
                </label>
                {/* Aviso ÚNICO por pantalla cuando los montos están enmascarados (regla de la guía
                    2026-06-05): los renglones muestran "—", esto explica el porqué una sola vez. */}
                {!puedeVerMontos && (
                    <p className="text-[10px] text-muted-foreground" data-testid="aviso-montos-enmascarados">
                        No tenés permiso para ver los montos.
                    </p>
                )}
                <div
                    className="max-h-48 overflow-y-auto rounded-lg border border-slate-200 dark:border-slate-700 divide-y divide-slate-100 dark:divide-slate-800"
                    role="listbox"
                    aria-label="Reembolsos pendientes de este operador"
                >
                    {filas.map((fila, idx) => {
                        const estaSeleccionada = filaSeleccionada?.key === fila.key;
                        // Enmascarado defensivo: si por algún motivo amountsMasked no llegó pero
                        // el usuario tampoco tiene el permiso, igual ocultamos (mismo criterio
                        // que el resto de la app: nunca confiar solo en lo que manda el backend).
                        const filaParaTexto = { ...fila, amountsMasked: fila.amountsMasked || !puedeVerMontos };
                        // Decisión "RESTOS" (2026-07-03): una fila puede ser informativa nada más
                        // (todavía no admite el registro directo, ej. abandonada sin reabrir, o
                        // cerrada/en proceso). Se ve igual mismo, pero no se puede elegir.
                        const noRegistrable = fila.canRegisterRefund === false;
                        return (
                            <button
                                key={fila.key}
                                type="button"
                                onClick={() => handleSeleccionarFila(fila)}
                                disabled={guardando || noRegistrable}
                                role="option"
                                aria-selected={estaSeleccionada}
                                title={noRegistrable ? "Este reembolso todavía no se puede registrar." : undefined}
                                className={`w-full text-left px-4 py-2.5 flex flex-col gap-1 transition-colors ${
                                    estaSeleccionada
                                        ? "bg-indigo-100 dark:bg-indigo-900/30"
                                        : "bg-white dark:bg-slate-800 hover:bg-slate-50 dark:hover:bg-slate-700/50"
                                } disabled:opacity-50 disabled:cursor-not-allowed`}
                                data-testid={`reembolso-pendiente-${idx}`}
                            >
                                <div className="flex items-center justify-between gap-3">
                                    <div className="min-w-0">
                                        <p className="text-sm font-semibold text-slate-800 dark:text-slate-200 truncate">
                                            Reserva {fila.numeroReserva || "—"}
                                            {fila.clienteNombre && (
                                                <span className="font-normal text-slate-500 dark:text-slate-400"> · {fila.clienteNombre}</span>
                                            )}
                                        </p>
                                    </div>
                                    <span className="flex-shrink-0 text-[10px] font-black uppercase tracking-wider text-muted-foreground">
                                        {fila.currency}
                                    </span>
                                </div>
                                {/* Decisiones 1 y 4 (P3=A / P4=A): la cuenta completa, o el motivo
                                    en criollo cuando el estimado da $0. Nunca el monto pelado. */}
                                <p className="text-xs text-slate-600 dark:text-slate-400">
                                    {construirTextoCuentaReembolso(filaParaTexto)}
                                </p>
                                {noRegistrable && (
                                    <p className="text-[10px] text-amber-600 dark:text-amber-400" data-testid={`reembolso-no-registrable-${idx}`}>
                                        Todavía no se puede registrar (revisá el estado de la anulación).
                                    </p>
                                )}
                            </button>
                        );
                    })}
                </div>
            </div>

            {/* Elegido: confirmación visual */}
            {filaSeleccionada && (
                <p className="text-xs text-indigo-700 dark:text-indigo-400">
                    Reembolso elegido: <strong>reserva {filaSeleccionada.numeroReserva || "—"}</strong>
                    {" "}en {filaSeleccionada.currency === "USD" ? "dólares" : "pesos"}.
                </p>
            )}

            {/* Campos: monto (moneda fijada por la fila elegida) + fecha + método + referencia */}
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
                <div className="space-y-1">
                    <label htmlFor="reembolso-monto" className="text-xs font-semibold text-slate-600 dark:text-slate-400">
                        Monto recibido
                        {filaSeleccionada && (
                            <span className="ml-1 font-normal text-slate-400">
                                ({filaSeleccionada.currency === "USD" ? "US$" : "$"})
                            </span>
                        )}
                    </label>
                    <input
                        id="reembolso-monto"
                        type="number"
                        step="0.01"
                        min="0.01"
                        value={monto}
                        disabled={guardando || !filaSeleccionada}
                        onChange={(e) => {
                            setMonto(e.target.value);
                            setErrorValidacion(null);
                            setErrorGuardar(null);
                        }}
                        placeholder="0,00"
                        className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                        data-testid="reembolso-monto"
                    />
                </div>

                <div className="space-y-1">
                    <label htmlFor="reembolso-fecha" className="text-xs font-semibold text-slate-600 dark:text-slate-400">
                        Fecha *
                    </label>
                    <input
                        id="reembolso-fecha"
                        type="date"
                        value={fecha}
                        disabled={guardando}
                        onChange={(e) => {
                            setFecha(e.target.value);
                            setErrorValidacion(null);
                            setErrorGuardar(null);
                        }}
                        className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                        data-testid="reembolso-fecha"
                    />
                </div>

                <div className="space-y-1">
                    <label htmlFor="reembolso-metodo" className="text-xs font-semibold text-slate-600 dark:text-slate-400">
                        Método (opcional)
                    </label>
                    <select
                        id="reembolso-metodo"
                        value={metodo}
                        disabled={guardando}
                        onChange={(e) => setMetodo(e.target.value)}
                        className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                        data-testid="reembolso-metodo"
                    >
                        {METODOS_REEMBOLSO.map((m) => (
                            <option key={m.value} value={m.value}>{m.label}</option>
                        ))}
                    </select>
                </div>

                <div className="space-y-1">
                    <label htmlFor="reembolso-referencia" className="text-xs font-semibold text-slate-600 dark:text-slate-400">
                        Referencia (opcional)
                    </label>
                    <input
                        id="reembolso-referencia"
                        type="text"
                        value={referencia}
                        disabled={guardando}
                        onChange={(e) => setReferencia(e.target.value)}
                        placeholder="# Comprobante"
                        className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                        data-testid="reembolso-referencia"
                    />
                </div>
            </div>

            {/* Error de validación local */}
            {errorValidacion && (
                <p className="text-xs text-rose-600 dark:text-rose-400" role="alert" data-testid="reembolso-error-validacion">
                    {errorValidacion}
                </p>
            )}

            {/* Error del backend: la ficha queda con todo lo cargado para reintentar */}
            {errorGuardar && (
                <div
                    className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-xs text-rose-700 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
                    role="alert"
                    data-testid="reembolso-error-guardar"
                >
                    {errorGuardar}
                </div>
            )}

            {/* Botones: anti-doble-submit (deshabilitados mientras se envía) */}
            <div className="flex justify-end gap-3 pt-1">
                <button
                    type="button"
                    onClick={onCancelar}
                    disabled={guardando}
                    className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700 disabled:opacity-50 transition-colors"
                >
                    Cancelar
                </button>
                <button
                    type="button"
                    onClick={handleConfirmar}
                    disabled={guardando}
                    className="flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white shadow-sm hover:bg-indigo-700 disabled:opacity-50 transition-colors"
                    data-testid="reembolso-confirmar"
                >
                    {guardando ? (
                        <>
                            <Loader2 className="h-4 w-4 animate-spin" />
                            Registrando…
                        </>
                    ) : (
                        "Confirmar"
                    )}
                </button>
            </div>
        </div>
    );
}
