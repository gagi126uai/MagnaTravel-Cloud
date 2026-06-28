/**
 * Ficha de pago AL PROVEEDOR, en línea (sin modal).
 *
 * Reemplaza SupplierPaymentModal en SupplierAccountPage.
 * Sigue el patrón visual de RegistrarCobroInline (ADR-035).
 *
 * Modos de uso:
 *   - Nuevo pago (paymentToEdit=null): POST /suppliers/{id}/payments
 *   - Editar pago (paymentToEdit=objeto): PUT /suppliers/{id}/payments/{paymentId}
 *     • Si el pago a editar era cruzado, solo se pueden cambiar método, referencia y notas.
 *       El monto/moneda/TC quedan congelados (igual que RegistrarCobroInline).
 *
 * Multimoneda:
 *   - Modo simple: "Pagás en $ — deuda $X" sin selectores extra.
 *   - Modo completo: al tocar "pagar en otra moneda", aparecen "Moneda del pago" e "Imputar a".
 *     Si las monedas difieren → recuadro de tipo de cambio (TC + fuente + fecha, los tres obligatorios).
 *
 * Si el guardado falla: el formulario queda con todos los datos cargados + cartel rojo.
 * El usuario puede reintentar en el mismo botón sin volver a llenar todo.
 *
 * REGLA (último commit): pagar de más al proveedor NO está bloqueado — queda como saldo a favor.
 *
 * Props:
 *   - supplierId: string — publicId del proveedor (para el endpoint)
 *   - balancesByCurrency: Array<{ currency, balance, confirmedPurchases, totalPaid }>
 *   - paymentToEdit: object|null — si es un objeto, la ficha se abre en modo edición
 *   - onGuardado: () => void — callback al guardar exitosamente
 *   - onCancelar: () => void — callback al cerrar la ficha
 */

import { useState, useEffect, useCallback } from "react";
import { CreditCard, Layers, X, Banknote, Copy, Check, ChevronDown, AlertCircle } from "lucide-react";
import { api } from "../../../api";
import { hasPermission } from "../../../auth";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import {
    resolverMonedaPrincipalProveedor,
    calcularEquivalenteProveedor,
    construirPayloadPagoProveedor,
} from "../lib/supplierPageLogic";
import { OWNER_TYPE, ACCOUNT_TYPE, resolverCuentaPrincipalPorMoneda } from "../../bank-accounts/lib/bankAccountLogic";

// Mapa de int (enum del backend) → etiqueta visible para el tipo de cuenta bancaria.
// Se usa en RecuadroDatosTransferencia para mostrar "Caja de Ahorro" / "Cuenta Corriente".
// IMPORTANTE: usar != null para no omitir el 0 (falsy en JS, pero es un valor válido).
const LABEL_TIPO_CUENTA = {
    [ACCOUNT_TYPE.CajaAhorro]: "Caja de Ahorro",
    [ACCOUNT_TYPE.CuentaCorriente]: "Cuenta Corriente",
};

// Fuentes de tipo de cambio (enum ExchangeRateSource del backend).
// Los mismos valores que en RegistrarCobroInline.
const FUENTES_TC = [
    { value: 5, label: "Manual" },
    { value: 6, label: "BNA vendedor divisa" },
    { value: 1, label: "BCRA mayorista A3500" },
];

// Métodos de pago del proveedor: valores enum del backend (inglés, igual que el modal viejo).
// IMPORTANTE: no cambiar estos valores sin cambiar también los registros existentes en base de datos.
const METODOS_PAGO_PROVEEDOR = [
    { value: "Transfer", label: "Transferencia" },
    { value: "Cash", label: "Efectivo" },
    { value: "Check", label: "Cheque" },
    { value: "Card", label: "Tarjeta" },
];

const fechaHoy = () => new Date().toISOString().split("T")[0];

// ─── Recuadro "Datos para transferir" ────────────────────────────────────────

/**
 * Muestra la cuenta bancaria del proveedor para que el cajero pueda copiar
 * el CBU o alias al homebanking sin tipear manualmente (evita errores de transcripción).
 *
 * Flujo:
 *   1. Carga la lista de cuentas del proveedor (CBU/alias tapados).
 *   2. Preselecciona la principal de la moneda del pago (o la primera si no hay principal).
 *   3. El cajero puede cambiar a otra cuenta del mismo proveedor desde un desplegable.
 *   4. Al tocar "Copiar CBU"/"Copiar alias", se llama al endpoint de detalle para obtener
 *      el dato completo (dispara auditoría de lectura), se copia al portapapeles y se muestra "Copiado ✓".
 *
 * Props:
 *   - supplierId: string — publicId del proveedor
 *   - monedaPago: "ARS" | "USD" — moneda actual del formulario de pago
 */
function RecuadroDatosTransferencia({ supplierId, monedaPago }) {
    const [cuentas, setCuentas] = useState([]);
    const [cargandoCuentas, setCargandoCuentas] = useState(true);
    // errorCuentas: distingue "falló la carga" de "no hay cuentas para esta moneda"
    const [errorCuentas, setErrorCuentas] = useState(null);
    const [cuentaSeleccionada, setCuentaSeleccionada] = useState(null);

    // Estado del botón copiar: null | "copiando" | "copiado" | "error"
    const [estadoCopia, setEstadoCopia] = useState(null);

    // Función separada para poder reutilizarla en el botón "Reintentar"
    const cargarCuentas = useCallback(() => {
        if (!supplierId) return;
        let cancelled = false;
        setCargandoCuentas(true);
        setErrorCuentas(null);
        // OWNER_TYPE.Supplier = 2 (el backend espera el int, no el string)
        const params = new URLSearchParams({ ownerType: String(OWNER_TYPE.Supplier), ownerId: String(supplierId) });
        api.get(`/bank-accounts?${params.toString()}`)
            .then((data) => {
                if (cancelled) return;
                const lista = Array.isArray(data) ? data : (data?.items ?? []);
                setCuentas(lista);
            })
            .catch((err) => {
                if (!cancelled) {
                    setErrorCuentas(getApiErrorMessage(err, "No se pudieron cargar los datos bancarios del proveedor."));
                }
            })
            .finally(() => { if (!cancelled) setCargandoCuentas(false); });
        return () => { cancelled = true; };
    }, [supplierId]);

    // Carga las cuentas del proveedor al montar (una sola vez: el listado no cambia durante el pago)
    // eslint-disable-next-line react-hooks/exhaustive-deps
    useEffect(() => {
        const cleanup = cargarCuentas();
        return cleanup;
    }, [supplierId]);

    // Cuando cambia la moneda del pago, recalculamos cuál cuenta mostrar
    // (dependencia: cuentas y monedaPago)
    useEffect(() => {
        if (cuentas.length === 0) return;
        const principal = resolverCuentaPrincipalPorMoneda(cuentas, monedaPago);
        setCuentaSeleccionada(principal ?? null);
        setEstadoCopia(null);
    }, [cuentas, monedaPago]);

    const cuentasDeEstaMoneda = cuentas.filter((c) => c.isActive !== false && c.currency === monedaPago);

    // Si falló la carga, mostramos el error con opción de reintentar (no silenciar el error)
    if (!cargandoCuentas && errorCuentas) {
        return (
            <div
                className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-950/10 p-4 space-y-2"
                data-testid="recuadro-datos-transferencia-error"
                role="alert"
            >
                <div className="flex items-center gap-2 text-sm font-semibold text-rose-700 dark:text-rose-300">
                    <AlertCircle className="h-4 w-4 flex-shrink-0" />
                    No se pudieron cargar los datos bancarios del proveedor.
                </div>
                <button
                    type="button"
                    onClick={cargarCuentas}
                    className="rounded-lg border border-rose-200 bg-white px-3 py-1.5 text-xs font-medium text-rose-700 hover:bg-rose-50 dark:border-rose-900/40 dark:bg-slate-900 dark:text-rose-400 transition-colors"
                >
                    Reintentar
                </button>
            </div>
        );
    }

    // Si no hay cuentas para esta moneda (y no hubo error), ocultamos el recuadro
    if (!cargandoCuentas && cuentasDeEstaMoneda.length === 0) return null;

    const handleCambiarCuenta = (publicId) => {
        const elegida = cuentas.find((c) => c.publicId === publicId);
        setCuentaSeleccionada(elegida ?? null);
        setEstadoCopia(null);
    };

    const handleCopiar = async () => {
        if (!cuentaSeleccionada) return;
        setEstadoCopia("copiando");

        try {
            // Llamamos al detalle para obtener el CBU/alias completo (dispara auditoría de lectura)
            const detalle = await api.get(`/bank-accounts/${cuentaSeleccionada.publicId}`);
            const textoCopiar = detalle?.cbu || detalle?.alias || "";
            if (!textoCopiar) { setEstadoCopia("error"); return; }

            try {
                await navigator.clipboard.writeText(textoCopiar);
                setEstadoCopia("copiado");
            } catch {
                // Fallback para contextos sin soporte de Clipboard API
                const textarea = document.createElement("textarea");
                textarea.value = textoCopiar;
                textarea.style.position = "fixed";
                textarea.style.opacity = "0";
                document.body.appendChild(textarea);
                textarea.focus();
                textarea.select();
                const exito = document.execCommand("copy");
                document.body.removeChild(textarea);
                setEstadoCopia(exito ? "copiado" : "error");
            }
        } catch (err) {
            console.warn("[RecuadroDatosTransferencia] Error al obtener detalle para copiar:", err?.message);
            setEstadoCopia("error");
        }

        setTimeout(() => setEstadoCopia(null), 2500);
    };

    const textoCopiarBoton =
        estadoCopia === "copiado"   ? "Copiado ✓" :
        estadoCopia === "error"     ? "Error al copiar" :
        estadoCopia === "copiando"  ? "Copiando…" :
        cuentaSeleccionada?.cbuMasked ? "Copiar CBU" :
        cuentaSeleccionada?.aliasMasked ? "Copiar alias" :
        "Copiar";

    return (
        <div
            className="rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/30 p-4 space-y-3"
            data-testid="recuadro-datos-transferencia"
        >
            <div className="flex items-center gap-2">
                <Banknote className="h-4 w-4 text-slate-500" />
                <span className="text-sm font-semibold text-slate-700 dark:text-slate-300">
                    Datos para transferir
                </span>
                <span className="text-xs text-slate-400">({monedaPago === "USD" ? "US$" : "$"})</span>
            </div>

            {cargandoCuentas ? (
                <p className="text-xs text-muted-foreground italic">Cargando datos bancarios del proveedor…</p>
            ) : cuentaSeleccionada ? (
                <div className="space-y-2">
                    {/* Datos resumidos de la cuenta seleccionada */}
                    <div className="text-sm text-slate-800 dark:text-slate-200 space-y-0.5">
                        <p className="font-semibold">{cuentaSeleccionada.holderName}</p>
                        <p className="font-mono text-xs text-slate-500 dark:text-slate-400">
                            {cuentaSeleccionada.cbuMasked
                                ? "CBU " + cuentaSeleccionada.cbuMasked
                                : cuentaSeleccionada.aliasMasked
                                ? "alias " + cuentaSeleccionada.aliasMasked
                                : "Sin CBU ni alias guardado"
                            }
                        </p>
                        {cuentaSeleccionada.bank && (
                            // accountType es int (0=CajaAhorro, 1=CuentaCorriente).
                            // Usamos != null para no omitir el 0 (falsy en JS).
                            <p className="text-xs text-slate-400">
                                {cuentaSeleccionada.bank}
                                {cuentaSeleccionada.accountType != null
                                    ? ` — ${LABEL_TIPO_CUENTA[cuentaSeleccionada.accountType] ?? ""}`
                                    : ""}
                            </p>
                        )}
                    </div>

                    {/* Fila de acciones: cambiar cuenta + copiar */}
                    <div className="flex flex-wrap items-center gap-2">
                        {/* Desplegable para cambiar de cuenta (solo si hay más de una) */}
                        {cuentasDeEstaMoneda.length > 1 && (
                            <div className="relative">
                                <select
                                    value={cuentaSeleccionada.publicId}
                                    onChange={(e) => handleCambiarCuenta(e.target.value)}
                                    className="appearance-none rounded-lg border border-slate-200 bg-white pr-7 pl-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 outline-none focus:ring-2 focus:ring-indigo-400"
                                    data-testid="selector-cuenta-proveedor"
                                    aria-label="Cambiar cuenta bancaria del proveedor"
                                >
                                    {cuentasDeEstaMoneda.map((c) => (
                                        <option key={c.publicId} value={c.publicId}>
                                            {c.holderName}{c.isPrimary ? " ⭐" : ""}
                                            {c.bank ? ` — ${c.bank}` : ""}
                                            {" ("}{c.cbuMasked || c.aliasMasked || "sin CBU/alias"}{")"}
                                        </option>
                                    ))}
                                </select>
                                <ChevronDown className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-400" />
                            </div>
                        )}

                        {/* Botón copiar: llama al detalle para obtener el dato completo */}
                        <button
                            type="button"
                            onClick={handleCopiar}
                            disabled={estadoCopia === "copiando" || (!cuentaSeleccionada?.cbuMasked && !cuentaSeleccionada?.aliasMasked)}
                            className={`inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-xs font-semibold transition-colors disabled:opacity-50 ${
                                estadoCopia === "copiado"
                                    ? "border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-900/40 dark:bg-emerald-950/20 dark:text-emerald-400"
                                    : estadoCopia === "error"
                                    ? "border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-900/40 dark:text-rose-400"
                                    : "border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300"
                            }`}
                            data-testid="btn-copiar-datos-transferencia"
                        >
                            {estadoCopia === "copiado"
                                ? <Check className="h-3.5 w-3.5" />
                                : <Copy className="h-3.5 w-3.5" />
                            }
                            {textoCopiarBoton}
                        </button>
                    </div>
                </div>
            ) : (
                <p className="text-xs text-muted-foreground italic">
                    Este proveedor no tiene cuentas bancarias cargadas en {monedaPago === "USD" ? "dólares" : "pesos"}.
                </p>
            )}
        </div>
    );
}

// ─── Selector de servicio de una reserva ─────────────────────────────────────

/**
 * Permite imputar un pago a un servicio concreto de una reserva del proveedor.
 * Carga los servicios del proveedor y filtra por la reserva elegida (client-side).
 * Solo aparece cuando el usuario ya seleccionó una reserva.
 */
function SelectorServicioImputacion({ supplierId, reservaSeleccionada, servicioSeleccionado, onServicioChange }) {
    const [servicios, setServicios] = useState([]);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);

    useEffect(() => {
        if (!supplierId || !reservaSeleccionada?.reservaPublicId) {
            setServicios([]);
            return;
        }

        let cancelled = false;
        setLoading(true);
        setError(null);

        // Buscamos por número de reserva para reducir el resultado, luego filtramos por publicId.
        const params = new URLSearchParams({ pageSize: "100", sortBy: "date", sortDir: "asc" });
        if (reservaSeleccionada.numeroReserva) {
            params.set("search", reservaSeleccionada.numeroReserva);
        }

        api.get(`/suppliers/${supplierId}/account/services?${params.toString()}`)
            .then((response) => {
                if (cancelled) return;
                const items = response?.items || [];
                // Filtro client-side: solo los servicios de ESTA reserva concreta
                const deEstaReserva = items.filter(
                    (s) => String(s.reservaPublicId || "").toLowerCase() ===
                           String(reservaSeleccionada.reservaPublicId || "").toLowerCase()
                );
                setServicios(deEstaReserva);
            })
            .catch((err) => {
                if (cancelled) return;
                console.warn("[SelectorServicioImputacion] Error cargando servicios:", err?.message);
                setError("No se pudieron cargar los servicios de esta reserva.");
            })
            .finally(() => { if (!cancelled) setLoading(false); });

        return () => { cancelled = true; };
    }, [supplierId, reservaSeleccionada?.reservaPublicId, reservaSeleccionada?.numeroReserva]);

    // Mapeo de tipo (backend en español) → recordKind (string que manda el frontend al backend)
    function tipoARecordKind(tipo) {
        const mapa = { "Hotel": "hotel", "Vuelo": "flight", "Aereo": "flight", "Traslado": "transfer", "Paquete": "package", "Asistencia": "assistance" };
        return mapa[tipo] || "generic";
    }

    if (loading) return <div className="text-xs text-muted-foreground italic">Cargando servicios…</div>;
    if (error) return <div className="text-xs text-amber-600 dark:text-amber-400">{error}</div>;
    if (servicios.length === 0) return <div className="text-xs text-muted-foreground italic">Este proveedor no tiene servicios en esta reserva.</div>;

    return (
        <div className="space-y-1">
            <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">
                Servicio de la reserva (opcional)
            </label>
            <select
                value={servicioSeleccionado?.servicePublicId || ""}
                onChange={(e) => {
                    const value = e.target.value;
                    if (!value) { onServicioChange(null); return; }
                    const elegido = servicios.find((s) => String(s.publicId) === value);
                    if (!elegido) return;
                    onServicioChange({
                        servicePublicId: String(elegido.publicId),
                        serviceRecordKind: tipoARecordKind(elegido.type),
                        descripcion: elegido.description || elegido.type,
                    });
                }}
                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
            >
                <option value="">Sin imputar a un servicio específico</option>
                {servicios.map((s) => (
                    <option key={String(s.publicId)} value={String(s.publicId)}>
                        {s.type}{s.description ? ` — ${s.description}` : ""}
                        {s.date ? ` (${new Date(s.date).toLocaleDateString("es-AR")})` : ""}
                    </option>
                ))}
            </select>
            {servicioSeleccionado && (
                <p className="text-[10px] text-emerald-600 dark:text-emerald-400">
                    El pago se imputará al servicio: <strong>{servicioSeleccionado.descripcion}</strong>
                </p>
            )}
        </div>
    );
}

// ─── Componente principal: formulario de pago en línea ────────────────────────

export function PagarProveedorInline({ supplierId, balancesByCurrency, paymentToEdit = null, onGuardado, onCancelar }) {
    // Determina la moneda con deuda activa (o la primera de la lista si todo está saldado)
    const monedaPrincipal = resolverMonedaPrincipalProveedor(balancesByCurrency);

    // El proveedor tiene multi-moneda si hay más de una entrada en balancesByCurrency
    const esMultimoneda = Array.isArray(balancesByCurrency) && balancesByCurrency.length > 1;

    // true cuando estamos editando un pago existente (vs. creando uno nuevo)
    const esEdicion = paymentToEdit != null;

    // En edición: si el pago original era cruzado, solo se puede cambiar método, referencia y notas.
    // Regla igual a RegistrarCobroInline: el monto y el TC ya generaron asientos contables.
    const estaEditandoCruzado = esEdicion && (
        paymentToEdit.imputedCurrency != null &&
        paymentToEdit.imputedCurrency !== paymentToEdit.currency
    );

    // Permiso para ver montos de costo: controla si el saldo del banner se muestra o se enmascara.
    // Nota: el backend ya devuelve el saldo real; el enmascarado es solo visual.
    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    // ── Estado del formulario ──────────────────────────────────────────────────
    const [monto, setMonto] = useState("");
    const [monedaPago, setMonedaPago] = useState(monedaPrincipal);
    const [saldoImputado, setSaldoImputado] = useState(monedaPrincipal);

    // En multimoneda, el formulario arranca en modo simple (solo moneda principal).
    // El link "pagar en otra moneda" activa el modo completo con selectores.
    const [mostrarOtraMoneda, setMostrarOtraMoneda] = useState(false);

    const [metodo, setMetodo] = useState("Transfer");
    const [fecha, setFecha] = useState(fechaHoy());
    const [referencia, setReferencia] = useState("");
    const [notas, setNotas] = useState("");

    // Tipo de cambio — solo para pagos cruzados (moneda de pago ≠ moneda imputada)
    const [tipoCambio, setTipoCambio] = useState("");
    const [fuenteTC, setFuenteTC] = useState(5); // Default Manual=5 (Unset=0 que el backend rechaza)
    const [fechaTC, setFechaTC] = useState(fechaHoy());

    // ── Selector opcional de reserva y servicio ───────────────────────────────
    const [reservasConDeuda, setReservasConDeuda] = useState([]);
    const [reservasLoading, setReservasLoading] = useState(false);
    const [reservaSeleccionada, setReservaSeleccionada] = useState(null);
    const [servicioSeleccionado, setServicioSeleccionado] = useState(null);

    const [saving, setSaving] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    // Cuando cambia paymentToEdit (nuevo pago vs. editar uno existente),
    // pre-cargamos el formulario con los datos del pago o limpiamos todo.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    useEffect(() => {
        if (paymentToEdit) {
            setMonto(String(paymentToEdit.amount ?? ""));
            setMonedaPago(paymentToEdit.currency || monedaPrincipal);
            setSaldoImputado(paymentToEdit.imputedCurrency || paymentToEdit.currency || monedaPrincipal);
            setMetodo(paymentToEdit.method || "Transfer");
            setFecha(paymentToEdit.paidAt ? paymentToEdit.paidAt.split("T")[0] : fechaHoy());
            setReferencia(paymentToEdit.reference || "");
            setNotas(paymentToEdit.notes || "");
            setTipoCambio(paymentToEdit.exchangeRate != null ? String(paymentToEdit.exchangeRate) : "");
            // exchangeRateSource del backend puede ser 0 (Unset) si es un pago simple; usamos 5 (Manual) como default
            const fuenteGuardada = paymentToEdit.exchangeRateSource;
            setFuenteTC(fuenteGuardada && fuenteGuardada !== 0 ? fuenteGuardada : 5);
            setFechaTC(paymentToEdit.exchangeRateAt ? paymentToEdit.exchangeRateAt.split("T")[0] : fechaHoy());
            // Si el pago editado era cruzado, mostramos el recuadro de TC precargado
            const eraCruzado = paymentToEdit.imputedCurrency != null && paymentToEdit.imputedCurrency !== paymentToEdit.currency;
            setMostrarOtraMoneda(eraCruzado);
        } else {
            // Nuevo pago: estado inicial limpio
            setMonto("");
            setMonedaPago(monedaPrincipal);
            setSaldoImputado(monedaPrincipal);
            setMetodo("Transfer");
            setFecha(fechaHoy());
            setReferencia("");
            setNotas("");
            setTipoCambio("");
            setFuenteTC(5);
            setFechaTC(fechaHoy());
            setMostrarOtraMoneda(false);
        }
        setErrorGuardar(null);
        setReservaSeleccionada(null);
        setServicioSeleccionado(null);
    }, [paymentToEdit]); // eslint-disable-line react-hooks/exhaustive-deps

    // Carga las reservas con deuda al montar la ficha, para el selector de imputación
    useEffect(() => {
        if (!supplierId) return;
        let cancelled = false;
        setReservasLoading(true);
        api.get(`/suppliers/${supplierId}/account/debt-by-reserva`)
            .then((response) => {
                if (!cancelled) setReservasConDeuda(response?.reservas || []);
            })
            .catch((err) => {
                console.warn("[PagarProveedorInline] No se pudo cargar lista de reservas:", err?.message);
                if (!cancelled) setReservasConDeuda([]);
            })
            .finally(() => { if (!cancelled) setReservasLoading(false); });

        return () => { cancelled = true; };
    }, [supplierId]);

    // Cuando cambia la reserva elegida, limpiamos el servicio (puede no existir en la nueva reserva)
    useEffect(() => {
        setServicioSeleccionado(null);
    }, [reservaSeleccionada]);

    // Un pago cruza de moneda si el cajero elige pagar en una moneda distinta al saldo imputado.
    // En ese caso hay que informar el tipo de cambio para convertir correctamente.
    const esCruzado = esMultimoneda && monedaPago !== saldoImputado;

    // El equivalente se calcula en tiempo real para mostrárselo al cajero antes de confirmar
    const montoEquivalente = calcularEquivalenteProveedor(monto, tipoCambio, monedaPago, saldoImputado);

    // El saldo de la moneda principal para el banner
    const saldoMonedaPrincipal = balancesByCurrency?.find(
        (b) => b.currency === monedaPrincipal
    )?.balance ?? null;

    // El formulario no se puede enviar si en un pago cruzado faltan el TC o la fecha del TC
    const camposIncompletosParaCruzado = esCruzado && (!tipoCambio || parseFloat(tipoCambio) <= 0 || !fechaTC);

    const handleReservaChange = (e) => {
        const publicId = e.target.value;
        if (!publicId) { setReservaSeleccionada(null); return; }
        const encontrada = reservasConDeuda.find((r) => String(r.reservaPublicId) === publicId);
        setReservaSeleccionada(encontrada || { reservaPublicId: publicId, numeroReserva: publicId });
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        setErrorGuardar(null);

        if (!esEdicion && (!monto || parseFloat(monto) <= 0)) {
            setErrorGuardar("El monto tiene que ser mayor a 0.");
            return;
        }
        if (!esEdicion && camposIncompletosParaCruzado) {
            setErrorGuardar("Para pagos cruzados tenés que completar el tipo de cambio y la fecha.");
            return;
        }

        setSaving(true);
        try {
            if (esEdicion && estaEditandoCruzado) {
                // Editar un pago cruzado: solo método, referencia y notas.
                // El monto y el TC generaron asientos en el libro mayor; no se pueden cambiar.
                const payloadParcial = {
                    method: metodo,
                    reference: referencia.trim() || null,
                    notes: notas.trim() || null,
                };
                await api.put(`/suppliers/${supplierId}/payments/${getPublicId(paymentToEdit)}`, payloadParcial);
            } else if (esEdicion) {
                // Editar un pago simple (no cruzado): se pueden cambiar todos los campos
                const payload = construirPayloadPagoProveedor({
                    monto, monedaPago, metodo, fecha, referencia, notas,
                    reservaId: reservaSeleccionada?.reservaPublicId || null,
                    serviceRecordKind: servicioSeleccionado?.serviceRecordKind || null,
                    servicePublicId: servicioSeleccionado?.servicePublicId || null,
                    esCruzado: false, // edición de pago simple → sin cruce
                    saldoImputado, tipoCambio, fuenteTC, fechaTC, montoEquivalente,
                });
                await api.put(`/suppliers/${supplierId}/payments/${getPublicId(paymentToEdit)}`, payload);
            } else {
                // Nuevo pago
                const payload = construirPayloadPagoProveedor({
                    monto, monedaPago, metodo, fecha, referencia, notas,
                    reservaId: reservaSeleccionada?.reservaPublicId || null,
                    serviceRecordKind: servicioSeleccionado?.serviceRecordKind || null,
                    servicePublicId: servicioSeleccionado?.servicePublicId || null,
                    esCruzado,
                    saldoImputado, tipoCambio, fuenteTC, fechaTC, montoEquivalente,
                });
                await api.post(`/suppliers/${supplierId}/payments`, payload);
            }

            showSuccess(esEdicion ? "Pago actualizado." : "Pago registrado.");
            onGuardado();
        } catch (error) {
            // La ficha queda abierta con todo lo cargado. El usuario puede reintentar.
            setErrorGuardar(
                getApiErrorMessage(error, "No se pudo guardar el pago. Revisá la conexión y probá de nuevo.")
            );
        } finally {
            setSaving(false);
        }
    };

    return (
        <div
            className="rounded-xl border-2 border-emerald-200 bg-emerald-50/40 dark:border-emerald-900/40 dark:bg-emerald-950/10 p-5 space-y-4"
            data-testid="pagar-proveedor-inline"
        >
            {/* Cabecera de la ficha */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <CreditCard className="w-4 h-4 text-emerald-600" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                        {esEdicion ? "Editar pago al proveedor" : "Registrar pago al proveedor"}
                    </h4>
                </div>
                <button
                    type="button"
                    onClick={onCancelar}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1"
                    aria-label="Cerrar ficha de pago"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            {/* Aviso informativo en modo edición de pago cruzado:
                el monto/TC no son editables porque ya están contabilizados. */}
            {estaEditandoCruzado && (
                <div className="rounded-lg bg-amber-50 border border-amber-200 dark:bg-amber-950/20 dark:border-amber-900/40 px-4 py-2 text-xs text-amber-700 dark:text-amber-300">
                    Este pago fue registrado en otra moneda. Solo podés cambiar el método, la referencia y las notas.
                </div>
            )}

            <form onSubmit={handleSubmit} className="space-y-4">

                {/* Recuadro "Datos para transferir": cuenta bancaria del proveedor en la moneda del pago.
                    Solo se muestra para nuevos pagos (en edición ya se registró el movimiento).
                    La monedaPago arranca como la moneda principal y puede cambiar si el cajero
                    activa "pagar en otra moneda". El recuadro se actualiza automáticamente. */}
                {!esEdicion && (
                    <RecuadroDatosTransferencia
                        supplierId={supplierId}
                        monedaPago={monedaPago}
                    />
                )}

                {/* Banner: moneda principal + saldo actual.
                    Le confirma al cajero en qué moneda va a pagar antes de cualquier selector.
                    Enmascarado: si no tiene permiso cobranzas.see_cost, muestra "—" en vez del saldo. */}
                {saldoMonedaPrincipal !== null && (
                    <div
                        className="flex items-center justify-between rounded-lg bg-emerald-50 border border-emerald-200 dark:bg-emerald-950/20 dark:border-emerald-900/40 px-4 py-2"
                        data-testid="pago-banner-moneda-principal"
                    >
                        <span className="text-sm font-semibold text-emerald-800 dark:text-emerald-300">
                            Pagás en {monedaPrincipal === "USD" ? "US$" : "$"} —{" "}
                            deuda{" "}
                            {puedeVerMontos
                                ? formatCurrency(saldoMonedaPrincipal, monedaPrincipal)
                                : <span className="text-slate-400" title="Sin permiso para ver montos">—</span>
                            }
                        </span>
                        {/* Link "pagar en otra moneda": solo en proveedores con deuda en ambas monedas.
                            No se muestra en modo edición de pago cruzado (ya está determinada la moneda). */}
                        {esMultimoneda && !mostrarOtraMoneda && !estaEditandoCruzado && (
                            <button
                                type="button"
                                onClick={() => setMostrarOtraMoneda(true)}
                                className="text-xs font-medium text-emerald-600 hover:text-emerald-800 dark:text-emerald-400 dark:hover:text-emerald-200 underline underline-offset-2"
                                data-testid="pago-link-otra-moneda"
                            >
                                pagar en otra moneda
                            </button>
                        )}
                    </div>
                )}

                {/* Fila principal: Monto + selectores de moneda (si multimoneda activado).
                    En modo edición de pago cruzado: todos estos campos son solo lectura. */}
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Monto</label>
                        <input
                            type="number"
                            step="0.01"
                            min="0.01"
                            required={!esEdicion} // en edición de cruzado puede ir sin monto
                            value={monto}
                            disabled={saving || estaEditandoCruzado}
                            onChange={(e) => setMonto(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                            placeholder="0,00"
                            data-testid="pago-monto"
                        />
                    </div>

                    {/* Moneda del pago: solo visible cuando el usuario activó "pagar en otra moneda" */}
                    {esMultimoneda && mostrarOtraMoneda && (
                        <div className="space-y-1">
                            <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Moneda del pago</label>
                            <select
                                value={monedaPago}
                                disabled={saving || estaEditandoCruzado}
                                onChange={(e) => setMonedaPago(e.target.value)}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                data-testid="pago-moneda"
                            >
                                {balancesByCurrency.map((b) => (
                                    <option key={b.currency} value={b.currency}>
                                        {b.currency === "USD" ? "US$ Dólares" : "$ Pesos"}
                                    </option>
                                ))}
                            </select>
                        </div>
                    )}

                    {/* Imputar a: qué saldo se reduce. Solo en multimoneda con modo completo. */}
                    {esMultimoneda && mostrarOtraMoneda && (
                        <div className="space-y-1">
                            <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Imputar a</label>
                            <select
                                value={saldoImputado}
                                disabled={saving || estaEditandoCruzado}
                                onChange={(e) => setSaldoImputado(e.target.value)}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                data-testid="pago-imputar-a"
                            >
                                {balancesByCurrency.map((b) => (
                                    <option key={b.currency} value={b.currency}>
                                        Deuda en {b.currency === "USD" ? "US$" : "$"} ({formatCurrency(b.balance, b.currency)})
                                    </option>
                                ))}
                            </select>
                        </div>
                    )}
                </div>

                {/* Fila secundaria: Método + Fecha + Referencia + Nota */}
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Método</label>
                        <select
                            value={metodo}
                            disabled={saving}
                            onChange={(e) => setMetodo(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                            data-testid="pago-metodo"
                        >
                            {METODOS_PAGO_PROVEEDOR.map((m) => (
                                <option key={m.value} value={m.value}>{m.label}</option>
                            ))}
                        </select>
                    </div>
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Fecha</label>
                        <input
                            type="date"
                            value={fecha}
                            disabled={saving || estaEditandoCruzado}
                            onChange={(e) => setFecha(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                        />
                    </div>
                    <div className="space-y-1">
                        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Referencia (opcional)</label>
                        <input
                            type="text"
                            value={referencia}
                            disabled={saving}
                            onChange={(e) => setReferencia(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                            placeholder="# Comprobante"
                            data-testid="pago-referencia"
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
                            placeholder="Notas internas…"
                            data-testid="pago-notas"
                        />
                    </div>
                </div>

                {/* Recuadro de tipo de cambio: solo cuando la moneda del pago ≠ saldo imputado.
                    Sin esta información no se puede convertir el monto correctamente. */}
                {esCruzado && (
                    <div
                        className="rounded-lg border-2 border-dashed border-indigo-300 bg-indigo-50/50 dark:border-indigo-900/50 dark:bg-indigo-950/20 p-4 space-y-3"
                        data-testid="recuadro-tipo-cambio"
                    >
                        <p className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">
                            {monedaPago === "ARS"
                                ? "↕ Pagás en pesos para bajar deuda en dólares: informá el tipo de cambio"
                                : "↕ Pagás en dólares para bajar deuda en pesos: informá el tipo de cambio"
                            }
                        </p>
                        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                            <div className="space-y-1">
                                <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">1 US$ = $ ___</label>
                                <input
                                    type="number"
                                    step="0.01"
                                    min="0.01"
                                    value={tipoCambio}
                                    disabled={saving || estaEditandoCruzado}
                                    onChange={(e) => setTipoCambio(e.target.value)}
                                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                    placeholder="1.200,00"
                                    data-testid="pago-tipo-cambio"
                                />
                            </div>
                            <div className="space-y-1">
                                <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">Fuente</label>
                                <select
                                    value={fuenteTC}
                                    disabled={saving || estaEditandoCruzado}
                                    onChange={(e) => setFuenteTC(Number(e.target.value))}
                                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                    data-testid="pago-fuente-tc"
                                >
                                    {FUENTES_TC.map((f) => (
                                        <option key={f.value} value={f.value}>{f.label}</option>
                                    ))}
                                </select>
                            </div>
                            <div className="space-y-1">
                                <label className="text-xs font-semibold text-indigo-700 dark:text-indigo-300">Fecha del TC</label>
                                <input
                                    type="date"
                                    value={fechaTC}
                                    disabled={saving || estaEditandoCruzado}
                                    onChange={(e) => setFechaTC(e.target.value)}
                                    className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-indigo-800 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                    data-testid="pago-fecha-tc"
                                />
                            </div>
                        </div>
                        {/* Monto equivalente calculado en tiempo real — se muestra cuando hay datos suficientes */}
                        {montoEquivalente != null && (
                            <p className="text-xs font-medium text-indigo-700 dark:text-indigo-300 mt-1">
                                → Se cancelan{" "}
                                <strong>{formatCurrency(montoEquivalente, saldoImputado)}</strong>
                                {" "}de la deuda en {saldoImputado === "USD" ? "dólares" : "pesos"}
                            </p>
                        )}
                    </div>
                )}

                {/* Sección: imputar a reserva / servicio (opcional).
                    No mostramos esto en edición de pago cruzado para simplificar
                    (el pago ya tiene sus imputaciones registradas). */}
                {!estaEditandoCruzado && (
                    <div className="space-y-3 rounded-xl border border-slate-200 dark:border-slate-700 p-4 bg-slate-50/50 dark:bg-slate-800/30">
                        <div className="flex items-center gap-2">
                            <Layers className="h-4 w-4 text-slate-500" />
                            <span className="text-sm font-semibold text-slate-700 dark:text-slate-300">
                                Imputar a una reserva / servicio
                            </span>
                            <span className="text-xs text-slate-400">(opcional)</span>
                        </div>

                        {reservasLoading ? (
                            <div className="text-xs text-muted-foreground italic">Cargando reservas…</div>
                        ) : (
                            <div className="space-y-1">
                                <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Reserva</label>
                                <select
                                    value={reservaSeleccionada?.reservaPublicId || ""}
                                    onChange={handleReservaChange}
                                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                    data-testid="pago-reserva"
                                >
                                    <option value="">Sin imputar a una reserva específica</option>
                                    {reservasConDeuda.map((r) => (
                                        <option key={String(r.reservaPublicId)} value={String(r.reservaPublicId)}>
                                            {r.numeroReserva || "Reserva"}{r.fileName ? ` — ${r.fileName}` : ""}
                                        </option>
                                    ))}
                                    {reservasConDeuda.length === 0 && (
                                        <option disabled>— No hay reservas con deuda —</option>
                                    )}
                                </select>
                            </div>
                        )}

                        {/* Selector de servicio: aparece solo cuando el usuario eligió una reserva */}
                        {reservaSeleccionada && (
                            <SelectorServicioImputacion
                                supplierId={supplierId}
                                reservaSeleccionada={reservaSeleccionada}
                                servicioSeleccionado={servicioSeleccionado}
                                onServicioChange={setServicioSeleccionado}
                            />
                        )}
                    </div>
                )}

                {/* Error: queda visible arriba de los botones para que sea lo primero que el usuario vea */}
                {errorGuardar && (
                    <div
                        className="rounded-lg bg-rose-50 border border-rose-200 dark:bg-rose-950/20 dark:border-rose-900/40 px-4 py-3 text-xs text-rose-700 dark:text-rose-300"
                        role="alert"
                        data-testid="pago-error"
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
                        disabled={saving || (!esEdicion && camposIncompletosParaCruzado)}
                        className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white hover:bg-emerald-700 shadow-sm transition-colors disabled:opacity-50 flex items-center gap-2"
                        data-testid="pago-confirmar"
                    >
                        {saving ? "Guardando…" : esEdicion ? "Guardar cambios" : "Confirmar pago"}
                    </button>
                </div>
            </form>
        </div>
    );
}
