/**
 * Modal de cobro usado desde la bandeja de cobranza (PaymentsCollectionsPage).
 *
 * ADR-035 (2026-06-19): el modal ahora recibe `monedaPrincipal` y `porMoneda`
 * del item de la worklist (CollectionWorkItemDto). Con esos datos:
 *   - Arranca en la moneda real de la reserva (no en ARS fijo).
 *   - Si la reserva es multimoneda (porMoneda.length > 1), muestra el link
 *     "pagar en otra moneda" igual que RegistrarCobroInline.
 *   - Si la reserva es monomoneda, oculta el selector (no hay otra moneda).
 *
 * La lógica de moneda es idéntica a RegistrarCobroInline para que el usuario
 * vea el mismo patrón visual sin importar desde dónde registra el cobro.
 *
 * Contratos del backend:
 *   - Nuevo cobro: POST /payments { reservaId, amount, currency, method, notes, [campos TC] }
 *   - Editar cobro: PUT /payments/:id { amount, method, notes, currency }
 */

import { useState, useEffect } from "react";
import { api } from "../api";
import { DollarSign, X } from "lucide-react";
import { showError, showSuccess } from "../alerts";
import { getApiErrorMessage } from "../lib/errors";
import { getPublicId } from "../lib/publicIds";
import { formatCurrency } from "../lib/utils";

// Fuentes de tipo de cambio (mismas que RegistrarCobroInline — enum ExchangeRateSource).
const FUENTES_TC = [
    { value: 5, label: "Manual" },
    { value: 6, label: "BNA vendedor divisa" },
    { value: 1, label: "BCRA mayorista A3500" },
];

const METODOS_PAGO = [
    { value: "Transferencia", label: "Transferencia" },
    { value: "Efectivo", label: "Efectivo" },
    { value: "Tarjeta Crédito", label: "Tarjeta Crédito" },
    { value: "Tarjeta Débito", label: "Tarjeta Débito" },
    { value: "Cheque", label: "Cheque" },
    { value: "Deposito", label: "Depósito" },
];

const fechaHoy = () => new Date().toISOString().split("T")[0];

/**
 * Determina si un cobro existente es cruzado (pagó en moneda distinta al saldo imputado).
 * Misma lógica que RegistrarCobroInline.
 */
function esCobroCruzado(payment) {
    if (!payment) return false;
    return payment.imputedCurrency != null && payment.imputedCurrency !== payment.currency;
}

export default function PaymentModal({
    isOpen,
    onClose,
    onSuccess,
    reservaId,
    maxAmount,
    paymentToEdit,
    // ADR-035: moneda principal de la reserva (CollectionWorkItemDto.monedaPrincipal, camelCase desde API).
    // Null en reservas legacy sin backfill → fallback a "ARS".
    monedaPrincipal: monedaPrincipalProp,
    // ADR-035: array de { currency, balance } de la worklist (CollectionWorkItemDto.porMoneda).
    porMoneda: porMonedaProp,
}) {
    // La moneda por defecto prioriza lo que manda el backend.
    // Si la reserva nunca tuvo backfill (campo null), usamos "ARS" como fallback seguro.
    const monedaPrincipalDefault =
        monedaPrincipalProp
        || porMonedaProp?.[0]?.currency
        || "ARS";

    // Una reserva es multimoneda si tiene más de un registro en porMoneda.
    const esMultimoneda = Array.isArray(porMonedaProp) && porMonedaProp.length > 1;

    const estaEditandoCruzado = paymentToEdit ? esCobroCruzado(paymentToEdit) : false;

    // --- Estado del formulario ---
    const [amount, setAmount] = useState("");
    const [monedaCobro, setMonedaCobro] = useState(monedaPrincipalDefault);
    const [saldoImputado, setSaldoImputado] = useState(monedaPrincipalDefault);
    const [method, setMethod] = useState("Transferencia");
    const [notes, setNotes] = useState("");
    const [paidAt, setPaidAt] = useState(fechaHoy());
    const [loading, setLoading] = useState(false);

    // ADR-035: en multimoneda el form arranca en modo simple.
    // El link "pagar en otra moneda" activa mostrarOtraMoneda para revelar los selectores.
    const [mostrarOtraMoneda, setMostrarOtraMoneda] = useState(false);

    // Tipo de cambio (solo para cobros cruzados)
    const [tipoCambio, setTipoCambio] = useState("");
    const [fuenteTC, setFuenteTC] = useState(5);
    const [fechaTC, setFechaTC] = useState(fechaHoy());

    // Inicializa el formulario cada vez que el modal se abre o cambia el cobro a editar.
    // Dependencia [isOpen, paymentToEdit]: corre al montar y al cambiar el cobro.
    useEffect(() => {
        if (!isOpen) return;

        if (paymentToEdit) {
            setAmount(paymentToEdit.amount?.toString() || "");
            setMonedaCobro(paymentToEdit.currency || monedaPrincipalDefault);
            setSaldoImputado(paymentToEdit.imputedCurrency || paymentToEdit.currency || monedaPrincipalDefault);
            setMethod(paymentToEdit.method || "Transferencia");
            setPaidAt(paymentToEdit.paidAt ? paymentToEdit.paidAt.split("T")[0] : fechaHoy());
            setNotes(paymentToEdit.notes || "");
            setTipoCambio(paymentToEdit.exchangeRate != null ? String(paymentToEdit.exchangeRate) : "");
            const fuenteGuardada = paymentToEdit.exchangeRateSource;
            setFuenteTC(fuenteGuardada && fuenteGuardada !== 0 ? fuenteGuardada : 5);
            setFechaTC(paymentToEdit.exchangeRateAt ? paymentToEdit.exchangeRateAt.split("T")[0] : fechaHoy());
            // Al editar un cobro cruzado ya guardado, mostrar los selectores directamente
            setMostrarOtraMoneda(esCobroCruzado(paymentToEdit));
        } else {
            // Nuevo cobro: arranca en la moneda principal de la reserva
            setAmount("");
            setMonedaCobro(monedaPrincipalDefault);
            setSaldoImputado(monedaPrincipalDefault);
            setMethod("Transferencia");
            setPaidAt(fechaHoy());
            setNotes("");
            setTipoCambio("");
            setFuenteTC(5);
            setFechaTC(fechaHoy());
            // Modo simple: sin selectores de moneda al inicio
            setMostrarOtraMoneda(false);
        }
        setLoading(false);
    }, [isOpen, paymentToEdit]);
    // Nota: monedaPrincipalDefault viene de las props del item (estable al abrir el modal).
    // No se incluye en deps para evitar re-runs si el padre re-renderiza con el mismo valor.

    // El recuadro de TC aparece SOLO cuando la moneda del cobro difiere del saldo imputado.
    const esCobroCruzadoEnCurso = esMultimoneda && monedaCobro !== saldoImputado;

    // Saldo de la moneda principal (para el banner "Cobrás en X — saldo X Y").
    const saldoMonedaPrincipal = Array.isArray(porMonedaProp)
        ? (porMonedaProp.find((pm) => pm.currency === monedaPrincipalDefault)?.balance ?? null)
        : null;

    // Monto equivalente en cobros cruzados (calculado en tiempo real, solo lectura).
    // Misma fórmula que RegistrarCobroInline.
    const montoEquivalente = (() => {
        if (!esCobroCruzadoEnCurso) return null;
        const tc = parseFloat(tipoCambio);
        const m = parseFloat(amount);
        if (isNaN(tc) || tc <= 0 || isNaN(m) || m <= 0) return null;
        if (monedaCobro === "ARS" && saldoImputado === "USD") return m / tc;
        if (monedaCobro === "USD" && saldoImputado === "ARS") return m * tc;
        return null;
    })();

    // Bloquea el submit si faltan datos de tipo de cambio en un cobro cruzado.
    const camposIncompletosParaCruzado = esCobroCruzadoEnCurso && (
        !tipoCambio || parseFloat(tipoCambio) <= 0 || !fechaTC
    );

    const handleSubmit = async (e) => {
        e.preventDefault();
        try {
            setLoading(true);

            const montoNumerico = parseFloat(amount);
            if (!montoNumerico || montoNumerico <= 0) {
                showError("El monto debe ser mayor a 0");
                return;
            }

            // Si editando, el máximo incluye el monto del cobro actual
            const currentMax = paymentToEdit ? (maxAmount + paymentToEdit.amount) : maxAmount;
            if (currentMax !== undefined && montoNumerico > currentMax) {
                showError(`El monto excede el saldo pendiente (${formatCurrency(currentMax, monedaCobro)})`);
                return;
            }

            if (camposIncompletosParaCruzado) {
                showError("Para cobros cruzados tenés que completar el tipo de cambio y la fecha.");
                return;
            }

            if (paymentToEdit) {
                let payload;
                if (estaEditandoCruzado) {
                    // Editar cobro cruzado: solo notas y método (el backend rechaza cambiar TC/monto).
                    payload = { method, notes };
                } else {
                    payload = {
                        amount: montoNumerico,
                        method,
                        paidAt: new Date(paidAt).toISOString(),
                        notes,
                        currency: monedaCobro,
                    };
                }
                await api.put(`/payments/${getPublicId(paymentToEdit)}`, payload);
                showSuccess("Pago actualizado correctamente");
            } else if (esCobroCruzadoEnCurso) {
                // Nuevo cobro cruzado: manda campos de TC
                await api.post("/payments", {
                    reservaId,
                    amount: montoNumerico,
                    currency: monedaCobro,
                    imputedCurrency: saldoImputado,
                    exchangeRate: parseFloat(tipoCambio),
                    exchangeRateSource: fuenteTC,
                    exchangeRateAt: new Date(fechaTC).toISOString(),
                    imputedAmount: montoEquivalente,
                    method,
                    paidAt: new Date(paidAt).toISOString(),
                    notes,
                });
                showSuccess("Pago registrado correctamente");
            } else {
                // Nuevo cobro normal: la moneda viene de la reserva (no ARS fijo)
                await api.post("/payments", {
                    reservaId,
                    amount: montoNumerico,
                    currency: monedaCobro,
                    method,
                    paidAt: new Date(paidAt).toISOString(),
                    notes,
                });
                showSuccess("Pago registrado correctamente");
            }

            await onSuccess?.();
            onClose();
        } catch (error) {
            console.error(error);
            showError(getApiErrorMessage(error, "Error al procesar el pago"));
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm">
            <div className="bg-white dark:bg-slate-800 rounded-xl shadow-xl w-full max-w-md border border-gray-200 dark:border-slate-700">
                <div className="flex items-center justify-between p-4 border-b border-gray-200 dark:border-slate-700">
                    <h3 className="text-lg font-medium text-gray-900 dark:text-white flex items-center gap-2">
                        <div className="p-2 bg-green-100 dark:bg-green-900/30 rounded-lg text-green-600 dark:text-green-400">
                            <DollarSign className="w-5 h-5" />
                        </div>
                        {paymentToEdit ? "Editar Pago" : "Registrar Pago"}
                    </h3>
                    <button
                        onClick={onClose}
                        className="text-gray-400 hover:text-gray-600 dark:text-slate-500 dark:hover:text-slate-300"
                        aria-label="Cerrar modal"
                    >
                        <X className="w-5 h-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="p-6 space-y-4">

                    {/* ── Banner de moneda principal (ADR-035) ──────────────────────────────────
                        Aparece en nuevo cobro cuando el DTO trae datos de plata.
                        Confirma al cajero en qué moneda va a cobrar antes de que toque nada.
                        Si la reserva es multimoneda: incluye el link "pagar en otra moneda". */}
                    {!paymentToEdit && monedaPrincipalDefault && saldoMonedaPrincipal !== null && (
                        <div
                            className="flex items-center justify-between rounded-lg bg-emerald-50 border border-emerald-200 dark:bg-emerald-950/20 dark:border-emerald-900/40 px-4 py-2"
                            data-testid="payment-modal-banner-moneda"
                        >
                            <span className="text-sm font-semibold text-emerald-800 dark:text-emerald-300">
                                {/* formatCurrency ya incluye el símbolo — no anteponer símbolo manual. */}
                                Cobrás en {monedaPrincipalDefault === "USD" ? "US$" : "$"} —{" "}
                                saldo {formatCurrency(saldoMonedaPrincipal, monedaPrincipalDefault)}
                            </span>
                            {/* Link "pagar en otra moneda": solo en multimoneda y antes de activarlo. */}
                            {esMultimoneda && !mostrarOtraMoneda && (
                                <button
                                    type="button"
                                    onClick={() => setMostrarOtraMoneda(true)}
                                    className="text-xs font-medium text-emerald-600 hover:text-emerald-800 dark:text-emerald-400 dark:hover:text-emerald-200 underline underline-offset-2"
                                    data-testid="payment-modal-link-otra-moneda"
                                >
                                    pagar en otra moneda
                                </button>
                            )}
                        </div>
                    )}

                    {/* ── Monto ── */}
                    <div>
                        <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">Monto</label>
                        <div className="relative">
                            <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3">
                                {/* Símbolo visual en el input: actualiza según la moneda del cobro */}
                                <span className="text-gray-500 dark:text-slate-400 sm:text-sm">
                                    {monedaCobro === "USD" ? "US$" : "$"}
                                </span>
                            </div>
                            <input
                                type="number"
                                step="0.01"
                                required
                                disabled={loading || (paymentToEdit && estaEditandoCruzado)}
                                className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white pl-10 focus:border-green-500 focus:ring-green-500 sm:text-sm py-2 disabled:opacity-50"
                                placeholder="0.00"
                                value={amount}
                                onChange={(e) => setAmount(e.target.value)}
                                data-testid="payment-modal-monto"
                            />
                        </div>
                        {/* Saldo pendiente: si hay datos por moneda, los usa; si no, el maxAmount crudo */}
                        {saldoMonedaPrincipal !== null ? (
                            <p className="text-xs text-gray-500 mt-1">
                                Saldo pendiente: {formatCurrency(saldoMonedaPrincipal, monedaPrincipalDefault)}
                            </p>
                        ) : maxAmount !== undefined && (
                            <p className="text-xs text-gray-500 mt-1">
                                Saldo pendiente: {formatCurrency(maxAmount, monedaCobro)}
                            </p>
                        )}
                    </div>

                    {/* ── Selectores de moneda: solo en multimoneda tras activar el link ── */}
                    {esMultimoneda && mostrarOtraMoneda && (
                        <div className="grid grid-cols-2 gap-3">
                            <div>
                                <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">
                                    Moneda del cobro
                                </label>
                                <select
                                    value={monedaCobro}
                                    disabled={loading || (paymentToEdit && estaEditandoCruzado)}
                                    onChange={(e) => setMonedaCobro(e.target.value)}
                                    className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white focus:border-green-500 focus:ring-green-500 sm:text-sm py-2 disabled:opacity-50"
                                    data-testid="payment-modal-moneda-cobro"
                                >
                                    {/* Solo monedas reales de la reserva (sin "US$ 0" fantasma) */}
                                    {porMonedaProp.map((pm) => (
                                        <option key={pm.currency} value={pm.currency}>
                                            {pm.currency === "USD" ? "US$ Dólares" : "$ Pesos"}
                                        </option>
                                    ))}
                                </select>
                            </div>
                            <div>
                                <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">
                                    Imputar a
                                </label>
                                <select
                                    value={saldoImputado}
                                    disabled={loading || (paymentToEdit && estaEditandoCruzado)}
                                    onChange={(e) => setSaldoImputado(e.target.value)}
                                    className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white focus:border-green-500 focus:ring-green-500 sm:text-sm py-2 disabled:opacity-50"
                                    data-testid="payment-modal-imputar-a"
                                >
                                    {porMonedaProp.map((pm) => (
                                        <option key={pm.currency} value={pm.currency}>
                                            Saldo en {pm.currency === "USD" ? "US$" : "$"} ({formatCurrency(pm.balance, pm.currency)})
                                        </option>
                                    ))}
                                </select>
                            </div>
                        </div>
                    )}

                    {/* ── Recuadro de tipo de cambio: solo cuando cruza de moneda ── */}
                    {esCobroCruzadoEnCurso && (
                        <div
                            className="rounded-lg border-2 border-dashed border-indigo-300 bg-indigo-50/50 dark:border-indigo-900/50 dark:bg-indigo-950/20 p-4 space-y-3"
                            data-testid="payment-modal-recuadro-tc"
                        >
                            <p className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">
                                {monedaCobro === "ARS"
                                    ? "↕ Pagás en pesos para bajar deuda en dólares: decinos el tipo de cambio"
                                    : "↕ Pagás en dólares para bajar deuda en pesos: decinos el tipo de cambio"
                                }
                            </p>
                            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                                <div>
                                    <label className="block text-xs font-semibold text-indigo-700 dark:text-indigo-300 mb-1">
                                        1 US$ = $ ___
                                    </label>
                                    <input
                                        type="number"
                                        step="0.01"
                                        min="0.01"
                                        value={tipoCambio}
                                        disabled={loading}
                                        onChange={(e) => setTipoCambio(e.target.value)}
                                        className="block w-full rounded-lg border-indigo-200 dark:border-indigo-800 dark:bg-slate-700 dark:text-white focus:ring-indigo-400 sm:text-sm py-2 px-3 disabled:opacity-50"
                                        placeholder="1.200,00"
                                        data-testid="payment-modal-tipo-cambio"
                                    />
                                </div>
                                <div>
                                    <label className="block text-xs font-semibold text-indigo-700 dark:text-indigo-300 mb-1">
                                        Fuente
                                    </label>
                                    <select
                                        value={fuenteTC}
                                        disabled={loading}
                                        onChange={(e) => setFuenteTC(Number(e.target.value))}
                                        className="block w-full rounded-lg border-indigo-200 dark:border-indigo-800 dark:bg-slate-700 dark:text-white focus:ring-indigo-400 sm:text-sm py-2 disabled:opacity-50"
                                        data-testid="payment-modal-fuente-tc"
                                    >
                                        {FUENTES_TC.map((f) => (
                                            <option key={f.value} value={f.value}>{f.label}</option>
                                        ))}
                                    </select>
                                </div>
                                <div>
                                    <label className="block text-xs font-semibold text-indigo-700 dark:text-indigo-300 mb-1">
                                        Fecha del TC
                                    </label>
                                    <input
                                        type="date"
                                        value={fechaTC}
                                        disabled={loading}
                                        onChange={(e) => setFechaTC(e.target.value)}
                                        className="block w-full rounded-lg border-indigo-200 dark:border-indigo-800 dark:bg-slate-700 dark:text-white focus:ring-indigo-400 sm:text-sm py-2 px-3 disabled:opacity-50"
                                        data-testid="payment-modal-fecha-tc"
                                    />
                                </div>
                            </div>
                            {/* Equivalente calculado en tiempo real */}
                            {montoEquivalente != null && (
                                <p className="text-xs font-medium text-indigo-700 dark:text-indigo-300 mt-1">
                                    → Se cancelan{" "}
                                    <strong>{formatCurrency(montoEquivalente, saldoImputado)}</strong>
                                    {" "}de la deuda en {saldoImputado === "USD" ? "dólares" : "pesos"}
                                </p>
                            )}
                        </div>
                    )}

                    {/* ── Aviso al editar cobro cruzado ── */}
                    {paymentToEdit && estaEditandoCruzado && (
                        <div className="rounded-lg bg-amber-50 border border-amber-200 dark:bg-amber-950/20 dark:border-amber-900/40 px-4 py-3 text-xs text-amber-800 dark:text-amber-300">
                            Este cobro cruzó de moneda. Para cambiar el monto, la moneda o el tipo de cambio, anulá este cobro y registrá uno nuevo.
                            Solo podés editar las notas y el método.
                        </div>
                    )}

                    {/* ── Método de pago ── */}
                    <div>
                        <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">Método de Pago</label>
                        <select
                            className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white focus:border-green-500 focus:ring-green-500 sm:text-sm py-2"
                            value={method}
                            disabled={loading}
                            onChange={(e) => setMethod(e.target.value)}
                            data-testid="payment-modal-metodo"
                        >
                            {METODOS_PAGO.map((m) => (
                                <option key={m.value} value={m.value}>{m.label}</option>
                            ))}
                        </select>
                    </div>

                    {/* ── Notas ── */}
                    <div>
                        <label className="block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1">Notas / Referencia</label>
                        <input
                            type="text"
                            className="block w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white focus:border-green-500 focus:ring-green-500 sm:text-sm py-2"
                            placeholder="Ej: Comprobante #1234"
                            value={notes}
                            disabled={loading}
                            onChange={(e) => setNotes(e.target.value)}
                            data-testid="payment-modal-notas"
                        />
                    </div>

                    <div className="pt-2 flex justify-end gap-3">
                        <button
                            type="button"
                            onClick={onClose}
                            className="px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-200 bg-white dark:bg-slate-700 border border-gray-300 dark:border-slate-600 rounded-lg hover:bg-gray-50 dark:hover:bg-slate-600"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={loading || !amount || camposIncompletosParaCruzado}
                            className="px-4 py-2 text-sm font-medium text-white bg-green-600 rounded-lg hover:bg-green-700 focus:ring-4 focus:ring-green-300 dark:focus:ring-green-800 shadow-sm flex items-center gap-2 disabled:opacity-50"
                            data-testid="payment-modal-confirmar"
                        >
                            {loading ? "Procesando..." : paymentToEdit ? "Guardar Cambios" : "Confirmar Pago"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
