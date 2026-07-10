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
 *   - INV-ADR044-OPERATOR-REQUIRED (2026-07-10): la cancelación tiene servicios de MÁS de
 *     un operador y no se mandó `supplierPublicId` — el backend no puede adivinar a cuál
 *     corresponde. El backend ya manda el detalle en español limpio ("Esta anulación
 *     tiene multas de más de un operador. Indicá cuál operador estás resolviendo.");
 *     getApiErrorMessage lo muestra tal cual, sin mapeo extra acá.
 *   - INV-ADR044-OPERATOR-NOT-FOUND (2026-07-10): el `supplierPublicId` mandado no
 *     corresponde a ningún servicio de esta cancelación. Mismo criterio: el detalle del
 *     backend ya es claro, no se duplica el mapeo.
 *   - requiresApproval: el sistema requiere 4-eyes (no hay respaldo documental o
 *     el monto supera un umbral). Se avisa y se ofrece reintentar con approvalRequestPublicId.
 *   - 400: fecha inválida (futura o anterior a la cancelación).
 *
 * MODO "corregir" (spec "el paso de multa vive en la ficha", 2026-07-08): cuando la
 * multa quedó trabada en revisión manual porque el monto o la moneda originales eran
 * incorrectos (operatorPenaltySituation.state === "DebitNoteNeedsAmountCurrency"), este
 * mismo componente se reutiliza con `modo="corregir"`: mismos campos (monto, moneda,
 * motivo), pero el submit llama a PATCH /cancellations/:id/correct-penalty en vez de
 * a confirm-penalty. No emite una ND nueva "desde cero": corrige la que ya está en
 * curso para que pueda terminar de salir.
 *
 * Props:
 *   - cancellationPublicId: GUID del BookingCancellation (obtenido de GET by-reserva).
 *   - reservaNumero: número de reserva (para mostrar en el header).
 *   - monedaSugerida: moneda ("ARS"|"USD") con la que arranca el selector — viene de la
 *     factura/porMoneda de la reserva. Sigue siendo editable; es solo el valor inicial.
 *   - montoInicial: número opcional con el que arranca el campo "Monto" — se usa en modo
 *     "corregir" (2026-07-08), donde ya existe un monto cargado (operatorPenaltySituation.
 *     amount) que el usuario viene a CORREGIR, no a cargar desde cero. Sigue siendo editable.
 *   - modo: "confirmar" (default) | "corregir". Ver arriba.
 *   - supplierPublicId (ADR-044 T1, 2026-07-10, opcional): GUID del operador al que
 *     corresponde ESTA confirmación. Solo hace falta cuando la cancelación tiene
 *     servicios de más de un operador (ADR-025) — en el caso mono-operador de siempre
 *     no se pasa y el payload de confirm-penalty sale exactamente igual que antes.
 *     Se usa SOLO en modo "confirmar": correct-penalty (modo "corregir") no lo necesita
 *     porque ya opera sobre una Nota de Débito puntual que el backend identifica sin
 *     ambigüedad por el propio cancellationPublicId.
 *   - onConfirmado: callback luego de confirmar/corregir exitosamente.
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
 * Valida solo el monto (regla compartida por los dos modos del panel: "confirmar" y
 * "corregir"). Se separó de validarCamposMulta para no duplicar esta regla.
 *
 * Se exporta para poder testearse sin DOM (lógica pura).
 *
 * @param {string} montoStr
 * @returns {string|null}
 */
export function validarMonto(montoStr) {
    const monto = parseFloat(montoStr);
    if (!montoStr || isNaN(monto) || monto <= 0) {
        return "El monto debe ser mayor a cero.";
    }
    return null;
}

/**
 * Valida los campos del mini-form de multa del operador (modo "confirmar").
 *
 * Se exporta para poder testearse sin DOM (lógica pura).
 *
 * @param {{ montoStr: string, fecha: string }} campos
 * @returns {{ montoError: string|null, fechaError: string|null }}
 */
export function validarCamposMulta({ montoStr, fecha }) {
    const montoError = validarMonto(montoStr);
    let fechaError = null;

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
 * Modo "confirmar" únicamente — ver puedeEnviarCorregir para el modo "corregir".
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

// Límites del campo motivo en modo "corregir" — mismo criterio que el resto de los
// paneles de cancelación (CerrarSinMultaInline, DeshacerCierreSinMultaInline): 5..500.
const MOTIVO_CORREGIR_MIN = 5;
const MOTIVO_CORREGIR_MAX = 500;

/**
 * Valida el motivo del modo "corregir" (por qué se corrige el monto/moneda de la multa).
 * Se exporta para testearse sin DOM.
 *
 * @param {string} motivo
 * @returns {string|null}
 */
export function validarMotivoCorregir(motivo) {
    const trimmed = (motivo ?? "").trim();
    if (trimmed.length < MOTIVO_CORREGIR_MIN) {
        return `El motivo debe tener al menos ${MOTIVO_CORREGIR_MIN} caracteres.`;
    }
    if (trimmed.length > MOTIVO_CORREGIR_MAX) {
        return `El motivo no puede superar los ${MOTIVO_CORREGIR_MAX} caracteres.`;
    }
    return null;
}

/**
 * Valida los campos del modo "corregir": monto + motivo (no pide fecha, a diferencia
 * del modo "confirmar" — correct-penalty no usa operatorConfirmationDate).
 * Se exporta para testearse sin DOM.
 *
 * @param {{ montoStr: string, motivo: string }} campos
 * @returns {{ montoError: string|null, motivoError: string|null }}
 */
export function validarCamposCorregir({ montoStr, motivo }) {
    return {
        montoError: validarMonto(montoStr),
        motivoError: validarMotivoCorregir(motivo),
    };
}

/**
 * Determina si el formulario del modo "corregir" puede enviarse.
 * Se exporta para testearse sin DOM.
 *
 * @param {{ montoStr: string, motivo: string, submitting: boolean }} estado
 * @returns {boolean}
 */
export function puedeEnviarCorregir({ montoStr, motivo, submitting }) {
    if (submitting) return false;
    const { montoError, motivoError } = validarCamposCorregir({ montoStr, motivo });
    return montoError === null && motivoError === null;
}

export function ConfirmarMultaOperadorInline({
    cancellationPublicId,
    reservaNumero,
    monedaSugerida,
    montoInicial,
    modo = "confirmar",
    supplierPublicId,
    onConfirmado,
    onCerrar,
}) {
    const esModoCorregir = modo === "corregir";

    // 2026-07-08: en modo "corregir" arranca con el monto que ya estaba cargado
    // (operatorPenaltySituation.amount) — el usuario viene a CORREGIRLO, no a tipearlo
    // de cero. Sigue siendo 100% editable.
    const [montoStr, setMontoStr] = useState(montoInicial != null ? String(montoInicial) : "");
    // B1 (2026-07-08): arranca con la moneda de la factura/porMoneda de la reserva si
    // el padre la pasó; si no vino (reserva sin factura todavía), cae a USD como antes.
    // Sigue siendo 100% editable — esto solo cambia el valor con el que abre el select.
    const [moneda, setMoneda] = useState(monedaSugerida ?? "USD");
    const [fecha, setFecha] = useState(getTodayString());
    const [referencia, setReferencia] = useState("");
    const [submitting, setSubmitting] = useState(false);
    const [conflictMessage, setConflictMessage] = useState(null);

    // 409 requiresApproval: flujo 4-eyes — abre RequestApprovalModal.
    const [approvalContext, setApprovalContext] = useState(null);

    // Resetea el formulario al montar el componente o si cambia la cancelación.
    // useEffect con [cancellationPublicId]: útil si el componente se reutiliza.
    useEffect(() => {
        setMontoStr(montoInicial != null ? String(montoInicial) : "");
        setMoneda(monedaSugerida ?? "USD");
        setFecha(getTodayString());
        setReferencia("");
        setConflictMessage(null);
        setApprovalContext(null);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [cancellationPublicId]);

    // Las reglas de validación difieren según el modo: "confirmar" pide fecha (y no pide
    // motivo), "corregir" pide motivo (y no pide fecha — correct-penalty no usa esa fecha).
    const { montoError, fechaError } = esModoCorregir
        ? { montoError: validarMonto(montoStr), fechaError: null }
        : validarCamposMulta({ montoStr, fecha });
    const motivoError = esModoCorregir ? validarMotivoCorregir(referencia) : null;
    const montoTocado = montoStr.length > 0;
    const canSubmit = esModoCorregir
        ? puedeEnviarCorregir({ montoStr, motivo: referencia, submitting })
        : puedeEnviar({ montoStr, fecha, submitting });

    const handleConfirmar = async () => {
        if (!canSubmit) return;

        setSubmitting(true);
        setConflictMessage(null);

        const monto = parseFloat(montoStr);

        try {
            let resultado;

            if (esModoCorregir) {
                // Corrige el monto/moneda de una multa que quedó trabada en revisión manual
                // (DebitNoteNeedsAmountCurrency). No confirma una multa nueva: ajusta la que
                // ya está en curso para que el backend pueda reintentar su emisión.
                resultado = await cancellationsApi.correctPenalty(cancellationPublicId, {
                    amount: monto,
                    currency: moneda,
                    reason: referencia.trim(),
                });
            } else {
                // conceptKind: null = OperatorPenaltyPassThrough (regla fiscal cerrada ADR-014).
                // La agencia es intermediaria: NO emite un cargo propio, solo traslada la multa
                // del operador al cliente vía ND. El backend lo identifica por conceptKind=null.
                resultado = await cancellationsApi.confirmPenalty(cancellationPublicId, {
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
                }, supplierPublicId);
            }

            if (esModoCorregir) {
                showSuccess(
                    "Listo. Se está cobrando la multa al cliente.",
                    "Corrección guardada"
                );
            } else {
                // Mostramos el resultado según debitNoteStatus del DTO devuelto.
                // "Pending" = encolada para procesar, "Issued" = ya emitida, "ManualReview" = fue a revisión.
                const estado = resultado?.debitNoteStatus;
                if (estado === "ManualReview") {
                    showSuccess(
                        "El monto quedó registrado, pero el cobro al cliente quedó en revisión manual.",
                        "Multa registrada — en revisión"
                    );
                } else {
                    showSuccess(
                        "Listo. Se está cobrando la multa al cliente.",
                        "Cobro en proceso"
                    );
                }
            }

            onConfirmado();
        } catch (error) {
            const errorPayload = error?.payload;

            // 409 requiresApproval: redirige al flujo de 4-eyes. Solo aplica al modo
            // "confirmar" — correct-penalty no tiene ese contrato de 4-eyes.
            if (!esModoCorregir && error?.status === 409 && errorPayload?.requiresApproval) {
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

                if (esModoCorregir) {
                    humanMessage = code === "CONCURRENT_EDIT"
                        ? "Otro usuario modificó esta cancelación al mismo tiempo. Esperá unos segundos y volvé a intentar."
                        : getApiErrorMessage(error, "No se pudo guardar la corrección del monto y la moneda. Intentá de nuevo.");
                } else if (invariantCode === "INV-ADR014-001") {
                    // La NC todavía no tiene CAE aprobado: hay que esperar antes de emitir la ND.
                    // Regla de voz (2026-07-08): sin "CAE"/"AFIP/ARCA" en un cartel de la ficha.
                    humanMessage = "La devolución al cliente todavía se está confirmando. Probá de nuevo en un rato.";
                } else if (invariantCode === "INV-ADR014-003") {
                    humanMessage = "Esta multa ya está cargada o su cobro ya está en curso. Mirá el estado en la ficha.";
                } else if (invariantCode === "INV-ADR014-002") {
                    humanMessage = "Este cargo es propio de la agencia, no es una multa del operador. Se cobra desde la sección de facturación de la ficha, no desde acá.";
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
                        esModoCorregir
                            ? "El monto o la moneda ingresados no son válidos."
                            : "La fecha de confirmación es inválida. Tiene que ser una fecha pasada o de hoy, no anterior a la cancelación."
                    )
                );
            } else {
                showError(getApiErrorMessage(error, esModoCorregir ? "No se pudo guardar la corrección." : "No se pudo confirmar la multa del operador."));
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
                        <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                            {esModoCorregir ? "Corregir el monto y la moneda de la multa" : "Confirmar multa del operador"}
                        </h4>
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

                {/* ── Explicación del flujo (UX: el usuario tiene que entender qué va a pasar) ──
                    B3 (2026-07-08): la copy vieja hablaba de "la nota de crédito" sin aclarar
                    que la devolución al cliente ya había pasado — sonaba a que faltaba algo del
                    lado del cliente. Reescrita para separar las dos plata en juego: lo que ya se
                    le devolvió al cliente (hecho) vs. lo que el operador retiene (a cargo ahora). */}
                <div
                    className="rounded-lg border border-orange-200 bg-orange-50 p-3.5 text-xs text-orange-800 dark:bg-orange-950/30 dark:border-orange-800 dark:text-orange-200 space-y-1"
                    data-testid="multa-explicacion-banner"
                >
                    {esModoCorregir ? (
                        <p>
                            <strong>¿Qué va a pasar?</strong> Vas a corregir el monto o la moneda que quedaron mal cargados.
                            Al guardar, el sistema reintenta cobrarle la <strong>multa</strong> al cliente con estos datos corregidos.
                        </p>
                    ) : (
                        <p>
                            <strong>¿Qué va a pasar?</strong> La devolución total al cliente ya se hizo cuando anulaste.
                            Ahora cargás la multa que te cobró el operador: se le cobra ese mismo monto al cliente como un <strong>cargo</strong>.
                        </p>
                    )}
                    <p className="text-orange-700 dark:text-orange-300">
                        Esta acción no se puede deshacer. Solo {esModoCorregir ? "guardá" : "confirmá"} si {esModoCorregir ? "estás seguro del dato correcto" : "el operador ya te informó el monto definitivo"}.
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
                        {/* B2 (2026-07-08): la aclaración anterior decía que la moneda de la ND la
                            definía "la configuración fiscal" — contradecía lo que en realidad hace
                            el backend (usa esta misma moneda). Corregido para no confundir. */}
                        <div className="mt-1.5 text-xs text-slate-400 dark:text-slate-500">
                            El cargo al cliente sale en esta misma moneda.
                        </div>
                    </div>
                </div>

                {/* ── Fecha en que el operador confirmó el monto ──
                    Solo en modo "confirmar": correct-penalty no usa esta fecha. */}
                {!esModoCorregir && (
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
                )}

                {/* ── Referencia documental (modo "confirmar", opcional) o motivo de la
                    corrección (modo "corregir", obligatorio) — mismo campo, distinto propósito. */}
                <div>
                    <label
                        className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                        htmlFor="multa-referencia"
                    >
                        {esModoCorregir ? (
                            <>¿Por qué corregís el monto o la moneda? <span className="text-rose-500" aria-hidden="true">*</span></>
                        ) : (
                            <>Referencia del aviso del operador <span className="text-slate-400 font-normal">(opcional)</span></>
                        )}
                    </label>
                    <input
                        id="multa-referencia"
                        type="text"
                        value={referencia}
                        onChange={(e) => setReferencia(e.target.value)}
                        maxLength={500}
                        disabled={submitting}
                        placeholder={esModoCorregir
                            ? "El operador informó el monto en dólares, no en pesos..."
                            : "Número de nota, email, referencia del PDF del operador..."}
                        data-testid="multa-referencia-input"
                        className={`w-full rounded-xl border px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 dark:bg-slate-800 dark:text-white disabled:opacity-50 ${
                            esModoCorregir && referencia.length > 0 && motivoError
                                ? "border-rose-400"
                                : "border-slate-300 dark:border-slate-600"
                        }`}
                    />
                    {esModoCorregir && referencia.length > 0 && motivoError && (
                        <div className="mt-1 text-xs text-rose-600" role="alert" data-testid="multa-motivo-error">
                            {motivoError}
                        </div>
                    )}
                    {/* Sin respaldo documental el backend puede exigir aprobación de 4-eyes.
                        Solo aplica al modo "confirmar" — correct-penalty no tiene ese flujo. */}
                    {!esModoCorregir && !referencia.trim() && (
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
                        {esModoCorregir
                            ? (submitting ? "Guardando..." : "Guardar corrección")
                            : (submitting ? "Confirmando..." : "Confirmar y cobrarle la multa al cliente")}
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
                        "Solicitud enviada al administrador. El cobro al cliente quedará pendiente hasta que lo autoricen.",
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
