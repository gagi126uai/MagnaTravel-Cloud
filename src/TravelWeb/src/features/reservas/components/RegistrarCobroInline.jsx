/**
 * Ficha de cobro EN LÍNEA — reemplaza el modal de pago dentro de la solapa Estado de Cuenta.
 *
 * Decisión de UX (2026-06-09, P3 y 2026-06-10): "el modal me parece horrible; el cobro
 * se carga en línea, debajo, igual que la carga de servicios (propuesta C aprobada)."
 *
 * ADR-035 (2026-06-19): la ficha ahora arranca en la moneda principal de la reserva
 * (campo `monedaPrincipal` del DTO), no en ARS fijo. Si la reserva es multimoneda, el
 * vendedor ve un link "pagar en otra moneda" que revela los selectores completos.
 *
 * Modos de uso:
 *   - Mono-moneda: muestra "Cobrás en X — saldo X Y" sin ningún selector.
 *   - Multimoneda-modo-simple: muestra la línea de moneda principal + link "pagar en otra moneda".
 *   - Multimoneda-modo-completo: al tocar el link, aparecen "Moneda del cobro" e "Imputar a".
 *     Si la moneda del pago ≠ saldo imputado → recuadro de tipo de cambio (cobro cruzado).
 *   - Editar cruzado: solo notas y método son editables (decisión C, 2026-06-11).
 *
 * Contrato del backend:
 *   - Cobro normal: POST/PUT /payments { currency, amount, method, paidAt, notes }
 *   - Cobro cruzado (nueva carga): agrega { imputedCurrency, exchangeRate, exchangeRateSource, exchangeRateAt, imputedAmount }
 *   - Editar cruzado ya guardado: solo method y notes (backend rechaza cambiar plata/TC)
 */

import { useState, useEffect } from "react";
import { formatCurrency } from "../../../lib/utils";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { X, CreditCard } from "lucide-react";
import { RecuadroCuentaBancaria } from "../../bank-accounts/components/RecuadroCuentaBancaria";
import { OWNER_TYPE } from "../../bank-accounts/lib/bankAccountLogic";

// Fuentes de tipo de cambio del backend (enum ExchangeRateSource).
// VALORES REALES del enum (src/TravelApi.Domain/Entities/ExchangeRateSource.cs):
//   Unset=0 (backend rechaza, NUNCA enviar), BCRA_A3500=1, BNA_Mayorista=2, BNA_Minorista=3,
//   AfipOficial=4, Manual=5, BNA_VendedorDivisa=6.
// En el MVP de cobro cruzado exponemos solo las 3 opciones más comunes para el cajero.
const FUENTES_TC = [
    { value: 5, label: "Manual" },                    // Manual=5 → default para el MVP
    { value: 6, label: "BNA vendedor divisa" },       // BNA_VendedorDivisa=6 → el recomendado por el contador
    { value: 1, label: "BCRA mayorista A3500" },      // BCRA_A3500=1 → para asientos contables
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
 * Determina si un cobro ya guardado es cruzado (pagó en moneda distinta al saldo imputado).
 *
 * Regla A-4 (2026-06-11): la detección canónica es imputedCurrency != null && imputedCurrency !== currency.
 * No alcanza con solo exchangeRate != null, porque el backend puede guardar exchangeRate
 * sin que haya cruce de moneda real (campo legacy). La fuente de verdad es imputedCurrency.
 */
function esCobroCruzado(payment) {
    if (!payment) return false;
    return payment.imputedCurrency != null && payment.imputedCurrency !== payment.currency;
}

export function RegistrarCobroInline({
    reservaId,
    reserva,       // Se usa para saber si es multimoneda, los saldos por moneda y la moneda principal
    paymentToEdit, // null = nuevo cobro; objeto = editar cobro existente
    onGuardado,    // callback cuando se confirma el cobro exitosamente
    onCancelar,    // callback cuando el usuario cancela
}) {
    const esMultimoneda = reserva?.esMultimoneda && Array.isArray(reserva.porMoneda) && reserva.porMoneda.length > 1;
    const estaEditandoCruzado = paymentToEdit ? esCobroCruzado(paymentToEdit) : false;

    // ADR-035 (2026-06-19): la moneda principal la decide el backend; si no llega usamos
    // la primera moneda del porMoneda, o "ARS" como último fallback.
    // Nunca hardcodeamos "ARS": si la reserva es solo en dólares, arrancamos en USD.
    const monedaPrincipalDefault = reserva?.monedaPrincipal
        || reserva?.porMoneda?.[0]?.currency
        || "ARS";

    // --- Estado del formulario ---
    const [monto, setMonto] = useState("");
    const [monedaCobro, setMonedaCobro] = useState(monedaPrincipalDefault);
    const [saldoImputado, setSaldoImputado] = useState(monedaPrincipalDefault);  // moneda del saldo al que se imputa

    // ADR-035 (2026-06-19): en multimoneda el form arranca en modo simple (solo la moneda
    // principal, sin selectores). El link "pagar en otra moneda" activa mostrarOtraMoneda=true.
    const [mostrarOtraMoneda, setMostrarOtraMoneda] = useState(false);

    const [metodo, setMetodo] = useState("Transferencia");
    const [paidAt, setPaidAt] = useState(fechaHoy());
    const [notas, setNotas] = useState("");

    // Tipo de cambio (solo para cobros cruzados)
    const [tipoCambio, setTipoCambio] = useState("");
    // Default Manual=5 (no 0=Unset que el backend rechaza)
    const [fuenteTC, setFuenteTC] = useState(5);   // int del enum ExchangeRateSource
    const [fechaTC, setFechaTC] = useState(fechaHoy());

    const [saving, setSaving] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    // Inicializar el form cuando cambia paymentToEdit (o al montar para nuevo cobro).
    // Dependencia [paymentToEdit]: corre al montar y cada vez que se pasa un cobro distinto a editar.
    useEffect(() => {
        if (paymentToEdit) {
            setMonto(String(paymentToEdit.amount || ""));
            setMonedaCobro(paymentToEdit.currency || monedaPrincipalDefault);
            setSaldoImputado(paymentToEdit.imputedCurrency || paymentToEdit.currency || monedaPrincipalDefault);
            setMetodo(paymentToEdit.method || "Transferencia");
            setPaidAt(paymentToEdit.paidAt ? paymentToEdit.paidAt.split("T")[0] : fechaHoy());
            setNotas(paymentToEdit.notes || "");
            setTipoCambio(paymentToEdit.exchangeRate != null ? String(paymentToEdit.exchangeRate) : "");
            // Si el backend devuelve 0 (Unset) — registros legacy sin fuente — mostramos Manual=5
            // para que el usuario pueda editar sin quedar atrapado en un valor inválido.
            const fuenteGuardada = paymentToEdit.exchangeRateSource;
            setFuenteTC(fuenteGuardada && fuenteGuardada !== 0 ? fuenteGuardada : 5);
            setFechaTC(paymentToEdit.exchangeRateAt ? paymentToEdit.exchangeRateAt.split("T")[0] : fechaHoy());
            // Al editar un cobro ya guardado: si cruzó moneda, mostrar los selectores
            setMostrarOtraMoneda(esCobroCruzado(paymentToEdit));
        } else {
            // Nuevo cobro: defaults en la moneda principal de la reserva (ADR-035)
            setMonto("");
            setMonedaCobro(monedaPrincipalDefault);
            setSaldoImputado(monedaPrincipalDefault);
            setMetodo("Transferencia");
            setPaidAt(fechaHoy());
            setNotas("");
            setTipoCambio("");
            // Default Manual=5 (Unset=0 es centinela que el backend rechaza)
            setFuenteTC(5);
            setFechaTC(fechaHoy());
            // En nuevo cobro arrancamos en modo simple (sin selectores de moneda)
            setMostrarOtraMoneda(false);
        }
        setErrorGuardar(null);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [paymentToEdit]);
    // Nota: monedaPrincipalDefault se calcula de reserva (prop estable al montar);
    // incluirla causaría re-runs innecesarios si el padre re-renderiza con el mismo valor.

    // El recuadro de TC aparece SOLO cuando la moneda del cobro es distinta del saldo imputado.
    // Aunque esté en modo simple (sin selectores), si la moneda del cobro difiere de la imputada
    // (ej.: editando un cobro cruzado ya guardado) lo detectamos igual.
    const esCobroCruzadoEnCurso = esMultimoneda && monedaCobro !== saldoImputado;

    // Saldo de la moneda principal (para mostrar en el banner "Cobrás en X — saldo X Y").
    // Solo se calcula cuando la reserva tiene datos de plata.
    const saldoMonedaPrincipal = reserva?.porMoneda?.find(
        (pm) => pm.currency === monedaPrincipalDefault
    )?.balance ?? null;

    // Monto equivalente imputado (calculado en tiempo real, solo lectura para el usuario)
    // Fórmula: si pago en $ para cancelar US$, imputedAmount = monto / tipoCambio
    // Si pago en US$ para cancelar $, imputedAmount = monto * tipoCambio
    const montoEquivalente = (() => {
        if (!esCobroCruzadoEnCurso) return null;
        const tc = parseFloat(tipoCambio);
        const m = parseFloat(monto);
        if (isNaN(tc) || tc <= 0 || isNaN(m) || m <= 0) return null;

        // Si cobro en pesos y el saldo a bajar está en USD: divido por el TC (1 USD = $ TC)
        if (monedaCobro === "ARS" && saldoImputado === "USD") {
            return m / tc;
        }
        // Si cobro en USD y el saldo a bajar está en ARS: multiplico por el TC
        if (monedaCobro === "USD" && saldoImputado === "ARS") {
            return m * tc;
        }
        return null;
    })();

    // Validación inline: bloquea "Confirmar" si falta TC/fuente/fecha en cobro cruzado
    const camposIncompletosParaCruzado = esCobroCruzadoEnCurso && (
        !tipoCambio || parseFloat(tipoCambio) <= 0 || !fechaTC
    );

    const handleSubmit = async (e) => {
        e.preventDefault();
        setErrorGuardar(null);

        if (!monto || parseFloat(monto) <= 0) {
            setErrorGuardar("El monto tiene que ser mayor a 0.");
            return;
        }
        if (camposIncompletosParaCruzado) {
            setErrorGuardar("Para cobros cruzados tenés que completar el tipo de cambio y la fecha.");
            return;
        }

        setSaving(true);
        try {
            let payload;

            if (paymentToEdit && estaEditandoCruzado) {
                // Editar cobro cruzado: SOLO notas y método (decisión C, 2026-06-11).
                // El backend rechaza cambios en monto/moneda/TC; la UI lo respeta.
                payload = {
                    method: metodo,
                    notes: notas,
                };
            } else if (paymentToEdit) {
                // Editar cobro normal
                payload = {
                    amount: parseFloat(monto),
                    method: metodo,
                    paidAt: new Date(paidAt).toISOString(),
                    notes: notas,
                    currency: monedaCobro,
                };
            } else if (esCobroCruzadoEnCurso) {
                // Nuevo cobro cruzado: manda los campos de TC
                payload = {
                    amount: parseFloat(monto),
                    currency: monedaCobro,
                    imputedCurrency: saldoImputado,
                    exchangeRate: parseFloat(tipoCambio),
                    exchangeRateSource: fuenteTC,
                    exchangeRateAt: new Date(fechaTC).toISOString(),
                    imputedAmount: montoEquivalente,
                    method: metodo,
                    paidAt: new Date(paidAt).toISOString(),
                    notes: notas,
                };
            } else {
                // Nuevo cobro normal (misma moneda del saldo)
                payload = {
                    amount: parseFloat(monto),
                    currency: monedaCobro,
                    method: metodo,
                    paidAt: new Date(paidAt).toISOString(),
                    notes: notas,
                };
            }

            if (paymentToEdit) {
                await api.put(`/payments/${getPublicId(paymentToEdit)}`, payload);
                showSuccess("Cobro actualizado.");
            } else {
                await api.post("/payments", { reservaId, ...payload });
                showSuccess("Cobro registrado.");
            }

            onGuardado();
        } catch (error) {
            // La ficha queda abierta con todo lo cargado intacto + mensaje claro.
            // Regla UX (ronda 2): nunca se pierde lo cargado ante un error.
            setErrorGuardar(getApiErrorMessage(error, "No se pudo guardar el cobro. Revisá la conexión y probá de nuevo."));
        } finally {
            setSaving(false);
        }
    };

    const tituloCabecera = paymentToEdit ? "Editar cobro" : "Registrar cobro";

    return (
        <div
            className="rounded-xl border-2 border-emerald-200 bg-emerald-50/40 dark:border-emerald-900/40 dark:bg-emerald-950/10 p-5 space-y-4"
            data-testid="registrar-cobro-inline"
        >
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <CreditCard className="w-4 h-4 text-emerald-600" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">{tituloCabecera}</h4>
                </div>
                <button
                    type="button"
                    onClick={onCancelar}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1"
                    aria-label="Cerrar ficha de cobro"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            <form onSubmit={handleSubmit} className="space-y-4">

                {/* Datos bancarios de la agencia para que el cliente pueda transferir.
                    Decisión de Gaston: "mostrar datos bancarios en pantalla al cobrar".
                    Solo en nuevo cobro (en edición el movimiento ya está registrado)
                    y solo cuando el método es Transferencia: para Efectivo, Cheque, etc.
                    los datos bancarios no son relevantes y solo generan ruido visual.
                    Si la agencia no tiene cuenta en la moneda del cobro, o el backend
                    devuelve 403 (sin permiso configuracion.view), se oculta silenciosamente.
                    La moneda del cobro (monedaCobro) puede cambiar si el cajero activa
                    "pagar en otra moneda"; el recuadro se actualiza automáticamente. */}
                {!paymentToEdit && metodo === "Transferencia" && (
                    <RecuadroCuentaBancaria
                        ownerType={OWNER_TYPE.Agency}
                        ownerId={0}
                        moneda={monedaCobro}
                        titulo="Tus datos para que el cliente transfiera"
                    />
                )}

                {/* ── Banner de moneda principal (ADR-035) ──────────────────────────────────
                    Siempre visible cuando hay datos de plata en la reserva.
                    Confirma al cajero en qué moneda va a cobrar sin necesidad de abrir
                    ningún selector (caso normal, lo más común). */}
                {monedaPrincipalDefault && saldoMonedaPrincipal !== null && !paymentToEdit && (
                    <div
                        className="flex items-center justify-between rounded-lg bg-emerald-50 border border-emerald-200 dark:bg-emerald-950/20 dark:border-emerald-900/40 px-4 py-2"
                        data-testid="cobro-banner-moneda-principal"
                    >
                        <span className="text-sm font-semibold text-emerald-800 dark:text-emerald-300">
                            {/* formatCurrency ya incluye el símbolo de moneda ("US$" o "$").
                                No anteponer ningún símbolo manual, de lo contrario queda duplicado. */}
                            Cobrás en {monedaPrincipalDefault === "USD" ? "US$" : "$"} —{" "}
                            saldo {formatCurrency(saldoMonedaPrincipal, monedaPrincipalDefault)}
                        </span>

                        {/* Link "pagar en otra moneda": SOLO visible en reservas multimoneda.
                            Monomoneda: no hay "otra moneda" para elegir, no se muestra nada. */}
                        {esMultimoneda && !mostrarOtraMoneda && (
                            <button
                                type="button"
                                onClick={() => setMostrarOtraMoneda(true)}
                                className="text-xs font-medium text-emerald-600 hover:text-emerald-800 dark:text-emerald-400 dark:hover:text-emerald-200 underline underline-offset-2"
                                data-testid="cobro-link-otra-moneda"
                            >
                                pagar en otra moneda
                            </button>
                        )}
                    </div>
                )}

                {/* Fila principal: Monto + Moneda del cobro + Imputar a */}
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                    {/* Monto: siempre visible */}
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Monto</label>
                        <input
                            type="number"
                            step="0.01"
                            min="0.01"
                            required
                            value={monto}
                            disabled={saving || (paymentToEdit && estaEditandoCruzado)}
                            onChange={(e) => setMonto(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                            placeholder="0,00"
                            data-testid="cobro-monto"
                        />
                    </div>

                    {/* Moneda del cobro: visible SOLO en multimoneda Y cuando el usuario activó "pagar en otra moneda".
                        Si NO activó el link, cobra en la moneda principal (selector oculto, valor ya cargado en estado). */}
                    {esMultimoneda && mostrarOtraMoneda && (
                        <div className="space-y-1">
                            <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Moneda del cobro</label>
                            <select
                                value={monedaCobro}
                                disabled={saving || (paymentToEdit && estaEditandoCruzado)}
                                onChange={(e) => setMonedaCobro(e.target.value)}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                data-testid="cobro-moneda"
                            >
                                {/* Listamos SOLO las monedas reales de la reserva (nada de "US$ 0" fantasma).
                                    porMoneda viene del DTO ya filtrado por el backend con las monedas que usa. */}
                                {reserva?.porMoneda?.map((pm) => (
                                    <option key={pm.currency} value={pm.currency}>
                                        {pm.currency === "USD" ? "US$ Dólares" : "$ Pesos"}
                                    </option>
                                ))}
                            </select>
                        </div>
                    )}

                    {/* Imputar a: visible SOLO en multimoneda Y cuando el usuario activó "pagar en otra moneda". */}
                    {esMultimoneda && mostrarOtraMoneda && (
                        <div className="space-y-1">
                            <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Imputar a</label>
                            <select
                                value={saldoImputado}
                                disabled={saving || (paymentToEdit && estaEditandoCruzado)}
                                onChange={(e) => setSaldoImputado(e.target.value)}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                data-testid="cobro-imputar-a"
                            >
                                {reserva?.porMoneda?.map((pm) => (
                                    <option key={pm.currency} value={pm.currency}>
                                        Saldo en {pm.currency === "USD" ? "US$" : "$"} ({formatCurrency(pm.balance, pm.currency)})
                                    </option>
                                ))}
                            </select>
                        </div>
                    )}
                </div>

                {/* Fila secundaria: Método + Fecha + Nota */}
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Método</label>
                        <select
                            value={metodo}
                            disabled={saving}
                            onChange={(e) => setMetodo(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                            data-testid="cobro-metodo"
                        >
                            {METODOS_PAGO.map((m) => (
                                <option key={m.value} value={m.value}>{m.label}</option>
                            ))}
                        </select>
                    </div>
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Fecha</label>
                        <input
                            type="date"
                            value={paidAt}
                            disabled={saving || (paymentToEdit && estaEditandoCruzado)}
                            onChange={(e) => setPaidAt(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                        />
                    </div>
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Nota (opcional)</label>
                        <input
                            type="text"
                            value={notas}
                            disabled={saving}
                            onChange={(e) => setNotas(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                            placeholder="Referencia, nro. de comprobante..."
                            data-testid="cobro-notas"
                        />
                    </div>
                </div>

                {/* Recuadro de tipo de cambio: aparece SOLO cuando cruza de moneda.
                    Regla 2026-06-09 P4: si moneda del cobro = saldo imputado → no aparece nada. */}
                {esCobroCruzadoEnCurso && (
                    <div
                        className="rounded-lg border-2 border-dashed border-indigo-300 bg-indigo-50/50 dark:border-indigo-900/50 dark:bg-indigo-950/20 p-4 space-y-3"
                        data-testid="recuadro-tipo-cambio"
                    >
                        <p className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">
                            {monedaCobro === "ARS"
                                ? "↕ Pagás en pesos para bajar deuda en dólares: decinos el tipo de cambio"
                                : "↕ Pagás en dólares para bajar deuda en pesos: decinos el tipo de cambio"
                            }
                        </p>
                        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                            {/* Tipo de cambio */}
                            <div className="space-y-1">
                                <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">
                                    1 US$ = $ ___
                                </label>
                                <input
                                    type="number"
                                    step="0.01"
                                    min="0.01"
                                    value={tipoCambio}
                                    disabled={saving}
                                    onChange={(e) => setTipoCambio(e.target.value)}
                                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                    placeholder="1.200,00"
                                    data-testid="cobro-tipo-cambio"
                                />
                            </div>
                            {/* Fuente */}
                            <div className="space-y-1">
                                <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">Fuente</label>
                                <select
                                    value={fuenteTC}
                                    disabled={saving}
                                    onChange={(e) => setFuenteTC(Number(e.target.value))}
                                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                    data-testid="cobro-fuente-tc"
                                >
                                    {FUENTES_TC.map((f) => (
                                        <option key={f.value} value={f.value}>{f.label}</option>
                                    ))}
                                </select>
                            </div>
                            {/* Fecha del TC */}
                            <div className="space-y-1">
                                <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">Fecha del TC</label>
                                <input
                                    type="date"
                                    value={fechaTC}
                                    disabled={saving}
                                    onChange={(e) => setFechaTC(e.target.value)}
                                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                    data-testid="cobro-fecha-tc"
                                />
                            </div>
                        </div>

                        {/* Línea de resultado: monto equivalente calculado en tiempo real.
                            Solo se muestra cuando hay suficiente info para calcular. */}
                        {montoEquivalente != null && (
                            <p className="text-xs font-medium text-indigo-700 dark:text-indigo-300 mt-1">
                                → Se cancelan{" "}
                                {/* formatCurrency ya incluye el símbolo; no agregar CurrencyBadge ni símbolo manual. */}
                                <strong>{formatCurrency(montoEquivalente, saldoImputado)}</strong>
                                {" "}de la deuda en {saldoImputado === "USD" ? "dólares" : "pesos"}
                            </p>
                        )}
                    </div>
                )}

                {/* Aviso en modo editar-cruzado: monto/moneda/TC son de solo lectura */}
                {paymentToEdit && estaEditandoCruzado && (
                    <div className="rounded-lg bg-amber-50 border border-amber-200 dark:bg-amber-950/20 dark:border-amber-900/40 px-4 py-3 text-xs text-amber-800 dark:text-amber-300">
                        Este cobro cruzó de moneda. Para cambiar el monto, la moneda o el tipo de cambio, anulá este cobro y registrá uno nuevo.
                        Solo podés editar las notas y el método.
                    </div>
                )}

                {/* Mensaje de error: visible arriba de los botones para que sea lo primero que el usuario vea */}
                {errorGuardar && (
                    <div
                        className="rounded-lg bg-rose-50 border border-rose-200 dark:bg-rose-950/20 dark:border-rose-900/40 px-4 py-3 text-xs text-rose-700 dark:text-rose-300"
                        role="alert"
                        data-testid="cobro-error"
                    >
                        {errorGuardar}
                    </div>
                )}

                {/* Botones */}
                <div className="flex justify-end gap-3 pt-1">
                    <button
                        type="button"
                        onClick={onCancelar}
                        disabled={saving}
                        className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                    >
                        Cancelar
                    </button>
                    <button
                        type="submit"
                        disabled={saving || camposIncompletosParaCruzado}
                        className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white hover:bg-emerald-700 shadow-sm transition-colors disabled:opacity-50 flex items-center gap-2"
                        data-testid="cobro-confirmar"
                    >
                        {saving ? "Guardando…" : "Confirmar"}
                    </button>
                </div>
            </form>
        </div>
    );
}
