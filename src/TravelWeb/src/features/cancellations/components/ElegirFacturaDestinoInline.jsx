/**
 * "Elegir la factura" — corrección EN LÍNEA de un cargo del operador que quedó trabado
 * porque la reserva tiene 2+ facturas activas y todavía nadie eligió a cuál corresponde
 * (ADR-044 T3b/T4, spec `docs/ux/2026-07-10-t4-multas-pantallas.md` secciones 2.3 y 5.2).
 *
 * Mismo patrón visual que "Corregir monto y moneda" (ConfirmarMultaOperadorInline modo
 * "corregir"): reemplaza al cartel de acción trabada mientras está abierto. Llama a
 * `PATCH /cancellations/{id}/operator-charges/{chargePublicId}/target-invoice`.
 *
 * Cartel bloqueado (P6): si el cargo YA generó su nota de débito (o la factura destino
 * se anuló entremedio), el backend rechaza con 409 INV-ADR044-TARGETINVOICE-003 y este
 * componente muestra el cartel fijo de la spec, sin números ni jerga.
 */

import { useEffect, useState } from "react";
import { AlertTriangle, Loader2, X } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { FacturaDestinoSelect } from "./FacturaDestinoSelect";
import { hayFacturaDestinoAmbigua } from "../lib/facturaDestinoLogic";

/**
 * Props:
 *   - reservaPublicId: GUID de la reserva (para buscar la cancelación vigente y sus
 *     facturas activas al abrir la ficha).
 *   - reservaNumero: número de negocio, para el header.
 *   - chargePublicId: GUID del cargo puntual a corregir (OperatorChargeDto.PublicId,
 *     resuelto por el padre con `primerCargoTrasladableSinFacturaDestino`).
 *   - onResuelto: callback tras corregir con éxito (el padre refresca la reserva).
 *   - onCerrar: callback para cerrar sin corregir.
 */
export function ElegirFacturaDestinoInline({ reservaPublicId, reservaNumero, chargePublicId, onResuelto, onCerrar }) {
    const [cargando, setCargando] = useState(true);
    const [cancellationPublicId, setCancellationPublicId] = useState(null);
    const [saleInvoices, setSaleInvoices] = useState([]);
    const [errorCarga, setErrorCarga] = useState(null);

    const [targetInvoicePublicId, setTargetInvoicePublicId] = useState("");
    const [submitting, setSubmitting] = useState(false);
    const [conflictMessage, setConflictMessage] = useState(null);

    // Al montar, buscamos la cancelación vigente + sus facturas de venta activas — el
    // GUID de la cancelación no viaja en el DTO de la reserva (mismo patrón que el
    // resto de los paneles de multa).
    useEffect(() => {
        let cancelado = false;
        (async () => {
            setCargando(true);
            setErrorCarga(null);
            try {
                const cancelacion = await cancellationsApi.getByReserva(reservaPublicId);
                if (cancelado) return;
                if (!cancelacion?.publicId) {
                    setErrorCarga("No se encontró la cancelación de esta reserva. Actualizá la página y volvé a intentar.");
                    return;
                }
                setCancellationPublicId(cancelacion.publicId);
                setSaleInvoices(Array.isArray(cancelacion.saleInvoices) ? cancelacion.saleInvoices : []);
            } catch (error) {
                if (!cancelado) {
                    setErrorCarga(getApiErrorMessage(error, "No se pudo cargar los datos de la cancelación. Intentá de nuevo."));
                }
            } finally {
                if (!cancelado) setCargando(false);
            }
        })();
        return () => {
            cancelado = true;
        };
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [reservaPublicId]);

    const canSubmit = !submitting && Boolean(targetInvoicePublicId) && Boolean(cancellationPublicId);

    const handleGuardar = async () => {
        if (!canSubmit) return;
        setSubmitting(true);
        setConflictMessage(null);
        try {
            await cancellationsApi.setOperatorChargeTargetInvoice(cancellationPublicId, chargePublicId, targetInvoicePublicId);
            showSuccess("Listo. Se asignó la factura y se reintenta el cargo al cliente.", "Factura elegida");
            onResuelto();
        } catch (error) {
            const invariantCode = error?.payload?.invariantCode || "";
            // P6 de la spec: copy fijo, sin números ni jerga — la ND ya salió (o la
            // factura destino se anuló entremedio) y no se puede volver a elegir.
            const humanMessage =
                invariantCode === "INV-ADR044-TARGETINVOICE-003"
                    ? "El cargo de esta multa ya se emitió; no se puede cambiar la factura."
                    : getApiErrorMessage(error, "No se pudo guardar la factura elegida. Intentá de nuevo.");
            setConflictMessage(humanMessage);
            setSubmitting(false);
        }
    };

    return (
        <div
            className="rounded-xl border-2 border-orange-200 bg-orange-50/40 dark:border-orange-900/40 dark:bg-orange-950/10 p-5 space-y-4"
            data-testid="elegir-factura-destino-inline"
        >
            <div className="flex items-center justify-between">
                <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                    Elegir la factura del cargo — Reserva #{reservaNumero}
                </h4>
                <button
                    type="button"
                    onClick={onCerrar}
                    disabled={submitting}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-40"
                    aria-label="Cerrar sin elegir la factura"
                >
                    <X className="h-4 w-4" />
                </button>
            </div>

            {cargando ? (
                <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 py-2">
                    <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
                    Cargando…
                </div>
            ) : errorCarga ? (
                <div className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200" role="alert">
                    {errorCarga}
                </div>
            ) : (
                <>
                    {conflictMessage && (
                        <div
                            role="alert"
                            className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2"
                            data-testid="elegir-factura-conflict-msg"
                        >
                            <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                            <span>{conflictMessage}</span>
                        </div>
                    )}

                    {hayFacturaDestinoAmbigua(saleInvoices) ? (
                        <FacturaDestinoSelect
                            saleInvoices={saleInvoices}
                            value={targetInvoicePublicId}
                            onChange={setTargetInvoicePublicId}
                            disabled={submitting}
                            testId="elegir-factura-destino-select"
                        />
                    ) : (
                        // Defensivo: si para cuando el usuario llega a corregir la reserva
                        // quedó con 0 o 1 factura activa, no hay nada que elegir.
                        <p className="text-xs text-slate-500 dark:text-slate-400">
                            Esta reserva ya no tiene más de una factura activa — no hace falta elegir nada.
                        </p>
                    )}

                    <div className="flex justify-end gap-3 pt-1">
                        <button
                            type="button"
                            onClick={onCerrar}
                            disabled={submitting}
                            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 disabled:opacity-50"
                        >
                            Volver
                        </button>
                        <button
                            type="button"
                            onClick={handleGuardar}
                            disabled={!canSubmit}
                            data-testid="elegir-factura-guardar-btn"
                            className="rounded-lg bg-orange-600 px-4 py-2 text-sm font-bold text-white hover:bg-orange-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                        >
                            {submitting && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                            {submitting ? "Guardando…" : "Guardar factura"}
                        </button>
                    </div>
                </>
            )}
        </div>
    );
}
