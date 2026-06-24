/**
 * Panel EN LÍNEA para confirmar la multa del operador después de anular una reserva.
 *
 * ADR-014: este es el paso DIFERIDO del flujo de anulación. La NC total ya se emitió
 * cuando se anuló la reserva. Ahora — días después, cuando el operador informa cuánto
 * retiene — el usuario carga el monto y se emite la Nota de Débito por ese monto.
 *
 * IMPORTANTE — diferencia con ConfirmPenaltyModal:
 *   ConfirmPenaltyModal = cargo PROPIO de la agencia (conceptKind 1 o 2: fee de gestión).
 *   Este componente = multa del OPERADOR (pass-through, conceptKind: null).
 *   En el pass-through la agencia actúa como intermediaria: le traslada la multa del
 *   operador al cliente vía ND, sin cobrar nada propio.
 *
 * Flujo de errores 409:
 *   - INV-ADR014-001: la NC todavía no tiene CAE → "esperá unos minutos".
 *   - INV-ADR014-003: la multa ya fue confirmada o la ND ya está en juego.
 *   - requiresApproval: el sistema requiere 4-eyes (no hay respaldo documental o
 *     el monto supera un umbral). Se avisa y se ofrece reintentar con approvalRequestPublicId.
 *   - 400: fecha inválida (futura o anterior a la cancelación).
 *
 * Props:
 *   - cancellationPublicId: GUID del BookingCancellation (obtenido de GET by-reserva).
 *   - reservaNumero: número de reserva (para mostrar en el header).
 *   - onConfirmado: callback luego de confirmar exitosamente.
 *   - onCerrar: callback para cerrar el panel sin confirmar.
 */

import { useState, useEffect } from "react";
import { AlertTriangle, Loader2, FileCheck2, X } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";

// Fecha de hoy en formato YYYY-MM-DD para el atributo max del input[type=date].
function getTodayString() {
    return new Date().toISOString().split("T")[0];
}

// Opciones de moneda para la multa del operador.
// Default USD porque los operadores turísticos suelen facturar en dólares.
const MONEDAS_MULTA = [
    { value: "USD", label: "Dólares (USD)" },
    { value: "ARS", label: "Pesos (ARS)" },
];

/**
 * Valida los campos del mini-form de multa del operador.
 *
 * Se exporta para poder testearse sin DOM (lógica pura).
 *
 * @param {{ montoStr: string, fecha: string }} campos
 * @returns {{ montoError: string|null, fechaError: string|null }}
 */
export function validarCamposMulta({ montoStr, fecha }) {
    const monto = parseFloat(montoStr);
    let montoError = null;
    let fechaError = null;

    if (!montoStr || isNaN(monto) || monto <= 0) {
        montoError = "El monto debe ser mayor a cero.";
    }

    if (!fecha) {
        fechaError = "La fecha es obligatoria.";
    } else if (fecha > getTodayString()) {
        // La fecha no puede ser futura: el operador tiene que haberla comunicado YA.
        fechaError = "La fecha no puede ser futura.";
    }

    return { montoError, fechaError };
}

/**
 * Determina si el formulario puede enviarse (sin errores y sin llamada en curso).
 *
 * Se exporta para testearse sin DOM.
 *
 * @param {{ montoStr: string, fecha: string, submitting: boolean }} estado
 * @returns {boolean}
 */
export function puedeEnviar({ montoStr, fecha, submitting }) {
    if (submitting) return false;
    const { montoError, fechaError } = validarCamposMulta({ montoStr, fecha });
    return montoError === null && fechaError === null;
}

export function ConfirmarMultaOperadorInline({
    cancellationPublicId,
    reservaNumero,
    onConfirmado,
    onCerrar,
}) {
    const [montoStr, setMontoStr] = useState("");
    // Default USD: los operadores turísticos suelen retener en dólares.
    const [moneda, setMoneda] = useState("USD");
    const [fecha, setFecha] = useState(getTodayString());
    const [referencia, setReferencia] = useState("");
    const [submitting, setSubmitting] = useState(false);
    const [conflictMessage, setConflictMessage] = useState(null);

    // 409 requiresApproval: flujo 4-eyes — abre RequestApprovalModal.
    const [approvalContext, setApprovalContext] = useState(null);

    // Resetea el formulario al montar el componente o si cambia la cancelación.
    // useEffect con [cancellationPublicId]: útil si el componente se reutiliza.
    useEffect(() => {
        setMontoStr("");
        setMoneda("USD"); // reset a default USD cada vez que se abre para una cancelación nueva
        setFecha(getTodayString());
        setReferencia("");
        setConflictMessage(null);
        setApprovalContext(null);
    }, [cancellationPublicId]);

    const { montoError, fechaError } = validarCamposMulta({ montoStr, fecha });
    const montoTocado = montoStr.length > 0;
    const canSubmit = puedeEnviar({ montoStr, fecha, submitting });

    const handleConfirmar = async () => {
        if (!canSubmit) return;

        setSubmitting(true);
        setConflictMessage(null);

        const monto = parseFloat(montoStr);

        // conceptKind: null = OperatorPenaltyPassThrough (regla fiscal cerrada ADR-014).
        // La agencia es intermediaria: NO emite un cargo propio, solo traslada la multa
        // del operador al cliente vía ND. El backend lo identifica por conceptKind=null.
        const payload = {
            conceptKind: null,
            confirmedPenaltyAmount: monto,
            // penaltyCurrency: campo nuevo — contrato PATCH /cancellations/{id}/confirm-penalty.
            // El backend lo acepta como opcional; si no llega, asume ARS (legado).
            penaltyCurrency: moneda,
            // El input type=date devuelve "YYYY-MM-DD". El backend espera DateTime:
            // se agrega "T00:00:00Z" para que el parsing no falle (igual que ConfirmPenaltyModal).
            operatorConfirmationDate: fecha + "T00:00:00Z",
            debitNotePurpose: null, // el backend usa PenaltyOrCancellationCharge por default
            supportingDocumentReference: referencia.trim() || null,
            overrideReason: null,
            approvalRequestPublicId: null,
        };

        try {
            const resultado = await cancellationsApi.confirmPenalty(cancellationPublicId, payload);

            // Mostramos el resultado según debitNoteStatus del DTO devuelto.
            // "Pending" = encolada para procesar, "Issued" = ya emitida, "ManualReview" = fue a revisión.
            const estado = resultado?.debitNoteStatus;
            if (estado === "ManualReview") {
                showSuccess(
                    "El monto quedó registrado, pero la nota de débito fue derivada a revisión manual por el equipo de administración.",
                    "Multa registrada — en revisión"
                );
            } else {
                showSuccess(
                    "Multa del operador confirmada. La nota de débito se está generando en AFIP/ARCA.",
                    "Nota de débito en proceso"
                );
            }

            onConfirmado();
        } catch (error) {
            const errorPayload = error?.payload;

            // 409 requiresApproval: redirige al flujo de 4-eyes.
            // Esto ocurre cuando no hay respaldo documental o el monto supera un umbral.
            if (error?.status === 409 && errorPayload?.requiresApproval) {
                setApprovalContext({
                    requestType: errorPayload.requestType,
                    entityType: errorPayload.entityType,
                    entityId: errorPayload.entityId,
                    entityLabel: `Multa del operador — Reserva #${reservaNumero}`,
                });
                setSubmitting(false);
                return;
            }

            if (error?.status === 409) {
                const invariantCode = errorPayload?.invariantCode || "";
                const code = errorPayload?.code || "";
                let humanMessage;

                if (invariantCode === "INV-ADR014-001") {
                    // La NC todavía no tiene CAE aprobado: hay que esperar antes de emitir la ND.
                    humanMessage = "La nota de crédito todavía no tiene CAE aprobado en AFIP/ARCA. Esperá unos minutos y volvé a intentar.";
                } else if (invariantCode === "INV-ADR014-003") {
                    humanMessage = "La multa de este operador ya fue confirmada o la nota de débito ya está en proceso. Si hay un problema, consultá con administración.";
                } else if (invariantCode === "INV-ADR014-002") {
                    humanMessage = "Esta penalidad está configurada como cargo propio de la agencia, no como pass-through del operador. No se puede emitir desde acá.";
                } else if (code === "CONCURRENT_EDIT") {
                    humanMessage = "Otro usuario modificó esta cancelación al mismo tiempo. Esperá unos segundos y volvé a intentar.";
                } else {
                    humanMessage = getApiErrorMessage(error, "No se pudo confirmar la multa del operador. Intentá de nuevo.");
                }

                setConflictMessage(humanMessage);
            } else if (error?.status === 400) {
                setConflictMessage(
                    getApiErrorMessage(
                        error,
                        "La fecha de confirmación es inválida. Tiene que ser una fecha pasada o de hoy, no anterior a la cancelación."
                    )
                );
            } else {
                showError(getApiErrorMessage(error, "No se pudo confirmar la multa del operador."));
            }

            setSubmitting(false);
        }
    };

    return (
        <>
            <div
                className="rounded-xl border-2 border-orange-200 bg-orange-50/40 dark:border-orange-900/40 dark:bg-orange-950/10 p-5 space-y-4"
                data-testid="confirmar-multa-operador-inline"
            >
                {/* ── Cabecera del panel ── */}
                <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <FileCheck2 className="w-4 h-4 text-orange-600" aria-hidden="true" />
                        <h4 className="text-sm font-bold text-slate-900 dark:text-white">Confirmar multa del operador</h4>
                        <span className="text-xs text-slate-500 dark:text-slate-400">
                            Reserva #{reservaNumero}
                        </span>
                    </div>
                    <button
                        type="button"
                        onClick={onCerrar}
                        disabled={submitting}
                        className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-40"
                        aria-label="Cerrar sin confirmar la multa"
                    >
                        <X className="w-4 h-4" />
                    </button>
                </div>

                {/* ── Explicación del flujo (UX: el usuario tiene que entender qué va a pasar) ── */}
                <div
                    className="rounded-lg border border-orange-200 bg-orange-50 p-3.5 text-xs text-orange-800 dark:bg-orange-950/30 dark:border-orange-800 dark:text-orange-200 space-y-1"
                    data-testid="multa-explicacion-banner"
                >
                    <p>
                        <strong>¿Qué va a pasar?</strong> La nota de crédito total ya fue emitida cuando se anuló la reserva.
                        Ahora confirmás la multa que el operador te comunicó: se va a emitir una <strong>Nota de Débito</strong> en AFIP/ARCA por ese monto.
                    </p>
                    <p className="text-orange-700 dark:text-orange-300">
                        Esta acción no se puede deshacer. Solo confirmá si el operador ya te informó el monto definitivo.
                    </p>
                </div>

                {/* ── Banner de error de conflicto ── */}
                {conflictMessage && (
                    <div
                        role="alert"
                        className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2"
                        data-testid="multa-conflict-msg"
                    >
                        <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                        <span>{conflictMessage}</span>
                    </div>
                )}

                {/* ── Monto + Moneda de la multa (en la misma fila) ── */}
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    {/* Campo: monto que retiene el operador */}
                    <div>
                        <label
                            className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                            htmlFor="multa-monto"
                        >
                            Monto que retiene el operador <span className="text-rose-500" aria-hidden="true">*</span>
                        </label>
                        <input
                            id="multa-monto"
                            type="number"
                            min="0.01"
                            step="0.01"
                            value={montoStr}
                            onChange={(e) => setMontoStr(e.target.value)}
                            placeholder="0.00"
                            disabled={submitting}
                            data-testid="multa-monto-input"
                            className={`w-full rounded-xl border px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 dark:bg-slate-800 dark:text-white disabled:opacity-50 ${
                                montoTocado && montoError ? "border-rose-400" : "border-slate-300 dark:border-slate-600"
                            }`}
                        />
                        {montoTocado && montoError && (
                            <div className="mt-1 text-xs text-rose-600" role="alert" data-testid="multa-monto-error">
                                {montoError}
                            </div>
                        )}
                    </div>

                    {/* Campo: moneda en la que el operador retiene la multa.
                        Default USD porque los operadores turísticos suelen retener en dólares. */}
                    <div>
                        <label
                            className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                            htmlFor="multa-moneda"
                        >
                            Moneda <span className="text-rose-500" aria-hidden="true">*</span>
                        </label>
                        <select
                            id="multa-moneda"
                            value={moneda}
                            onChange={(e) => setMoneda(e.target.value)}
                            disabled={submitting}
                            data-testid="multa-moneda-select"
                            className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 disabled:opacity-50"
                        >
                            {MONEDAS_MULTA.map((opcion) => (
                                <option key={opcion.value} value={opcion.value}>
                                    {opcion.label}
                                </option>
                            ))}
                        </select>
                        {/* Aclaración importante: la moneda elegida acá es solo para registrar
                            cómo informó el operador la multa. La moneda de emisión de la Nota
                            de Débito al cliente la define la configuración fiscal (no este campo). */}
                        <div className="mt-1.5 text-xs text-slate-400 dark:text-slate-500">
                            Indicá la moneda en la que el operador te informó la multa. La nota de débito al cliente se emite según la configuración fiscal vigente.
                        </div>
                    </div>
                </div>

                {/* ── Fecha en que el operador confirmó el monto ── */}
                <div>
                    <label
                        className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                        htmlFor="multa-fecha"
                    >
                        Fecha en que el operador te informó el monto <span className="text-rose-500" aria-hidden="true">*</span>
                    </label>
                    <input
                        id="multa-fecha"
                        type="date"
                        value={fecha}
                        onChange={(e) => setFecha(e.target.value)}
                        max={getTodayString()}
                        disabled={submitting}
                        data-testid="multa-fecha-input"
                        className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 disabled:opacity-50"
                    />
                    {fechaError && (
                        <div className="mt-1 text-xs text-rose-600" role="alert" data-testid="multa-fecha-error">
                            {fechaError}
                        </div>
                    )}
                    <div className="mt-1 text-xs text-slate-400">
                        Podés ingresar una fecha anterior a hoy si el operador te avisó antes.
                    </div>
                </div>

                {/* ── Referencia documental (opcional pero recomendada) ── */}
                <div>
                    <label
                        className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                        htmlFor="multa-referencia"
                    >
                        Referencia del aviso del operador <span className="text-slate-400 font-normal">(opcional)</span>
                    </label>
                    <input
                        id="multa-referencia"
                        type="text"
                        value={referencia}
                        onChange={(e) => setReferencia(e.target.value)}
                        maxLength={500}
                        disabled={submitting}
                        placeholder="Número de nota, email, referencia del PDF del operador..."
                        data-testid="multa-referencia-input"
                        className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 disabled:opacity-50"
                    />
                    {/* Sin respaldo documental el backend puede exigir aprobación de 4-eyes. */}
                    {!referencia.trim() && (
                        <div className="mt-1.5 text-xs text-amber-700 dark:text-amber-300 flex items-start gap-1">
                            <AlertTriangle className="h-3 w-3 flex-shrink-0 mt-0.5" />
                            <span>Sin referencia documental puede requerirse autorización adicional del administrador.</span>
                        </div>
                    )}
                </div>

                {/* ── Acciones ── */}
                <div className="flex justify-end gap-3 pt-1">
                    <button
                        type="button"
                        onClick={onCerrar}
                        disabled={submitting}
                        className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                    >
                        Volver
                    </button>
                    <button
                        type="button"
                        onClick={handleConfirmar}
                        disabled={!canSubmit}
                        data-testid="multa-confirmar-btn"
                        className="rounded-lg bg-orange-600 px-4 py-2 text-sm font-bold text-white hover:bg-orange-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                    >
                        {submitting && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                        {submitting ? "Confirmando..." : "Confirmar y emitir nota de débito"}
                    </button>
                </div>
            </div>

            {/* Modal de solicitud de aprobación 4-eyes.
                Se activa cuando el backend responde 409 requiresApproval (sin respaldo o monto grande). */}
            <RequestApprovalModal
                isOpen={Boolean(approvalContext)}
                onClose={() => setApprovalContext(null)}
                onCreated={() => {
                    setApprovalContext(null);
                    showSuccess(
                        "Solicitud enviada al administrador. La nota de débito quedará pendiente hasta que lo autoricen.",
                        "Solicitud enviada"
                    );
                    onCerrar();
                }}
                requestType={approvalContext?.requestType}
                entityType={approvalContext?.entityType}
                entityId={approvalContext?.entityId}
                entityLabel={approvalContext?.entityLabel}
            />
        </>
    );
}
