/**
 * Ficha de pago AL PROVEEDOR, en línea (sin modal).
 *
 * Reemplaza SupplierPaymentModal en SupplierAccountPage.
 * Sigue el patrón visual de RegistrarCobroInline (ADR-035).
 *
 * REDISEÑO 2026-07-20 (aprobado por el dueño, análisis erp-systems-expert
 * `docs/architecture/2026-07-20-analisis-cuenta-proveedor-vs-erps.md` sección 5):
 * un pago NUEVO ahora se arma en DOS PASOS, para que el cajero primero diga A QUÉ
 * le está pagando y recién después complete el monto — al revés del formulario viejo,
 * que pedía el monto primero y dejaba "imputar a una reserva" como un desplegable
 * opcional escondido al final (la queja real de Gaston: "no te dice después dónde
 * impacta"). Los dos pasos, en el mismo componente (sin navegar de pantalla):
 *
 *   Paso "elegir" — grilla con las reservas/servicios que tienen deuda con este
 *     proveedor (mismo dato de siempre, GET .../account/debt-by-reserva), un botón
 *     "Pagar" por fila, y un botón aparte con NOMBRE PROPIO "Pago a cuenta (sin
 *     imputar)" para el caso de pagar sin destino puntual — antes era un desplegable
 *     vacío, ahora es una decisión visible.
 *   Paso "pagar" — el formulario de siempre (monto/método/fecha/referencia/nota),
 *     pero con el destino ya FIJADO arriba ("Pagás la reserva 1051 — Hotel
 *     Bariloche [Cambiar]") y el monto/moneda pre-cargados con la deuda de esa fila
 *     (editable hacia abajo, igual que ya hacía "Liquidar cargo facturado aparte").
 *
 * Al guardar un pago NUEVO, el backend devuelve `impact` (a qué reserva bajó la
 * deuda y cuánto queda pendiente) y esta ficha muestra un CARTEL DE ÉXITO con ese
 * dato en vez de cerrarse en silencio — antes, el único lugar donde se podía "ver"
 * el impacto era yendo a mano a la solapa "Deuda por reserva" a comparar de memoria.
 *
 * IMPORTANTE — alcance del rediseño: el flujo de 2 pasos es SOLO para pagos NUEVOS.
 * Editar un pago existente (paymentToEdit != null) sigue exactamente igual que antes
 * (un solo paso, con el desplegable "Imputar a una reserva / servicio (opcional)"),
 * porque la spec del rediseño no tocó ese modo.
 *
 * Modos de uso:
 *   - Nuevo pago (paymentToEdit=null): POST /suppliers/{id}/payments (2 pasos)
 *   - Editar pago (paymentToEdit=objeto): PUT /suppliers/{id}/payments/{paymentId} (1 paso, sin cambios)
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
 *   - onGuardado: () => void — REFRESCA los datos del padre (overview/extracto/deuda).
 *     Se llama apenas el guardado resuelve OK del lado del servidor, SIEMPRE — no espera
 *     a que el cajero cierre el cartel de éxito. FIX bloqueante (review 2026-07-21): antes
 *     se llamaba recién al cerrar el cartel, y si el cajero cerraba la ficha desde OTRO
 *     lado (el botón de arriba de la página) mientras el cartel estaba abierto, el pago
 *     había quedado guardado pero la pantalla nunca se refrescaba.
 *   - onCancelar: () => void — CIERRA la ficha (sin refrescar nada por su cuenta). Se usa
 *     tanto para cancelar sin haber guardado nada, como para cerrar el cartel de éxito
 *     ("Cerrar"/"Ver cuenta") — en ese caso el refresco ya ocurrió vía onGuardado().
 *   - onSavingChange: (saving: boolean) => void — opcional. Avisa al padre cuando hay un
 *     guardado en curso, para que pueda deshabilitar SUS PROPIOS botones que también
 *     puedan cerrar esta ficha (ej. el botón "Registrar pago / Cerrar" de la página).
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
    filtrarServiciosPorMonedaDePago,
    hayServiciosDelProveedorEnReserva,
    armarFilasDeudaPorReserva,
    agruparServiciosPorReserva,
    construirDetalleFilaDeuda,
    construirMensajeExitoPago,
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

const PAYMENT_CURRENCIES = ["ARS", "USD"];

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

// Mapeo de tipo (backend en español) → recordKind (string que manda el frontend al backend)
function tipoARecordKind(tipo) {
    const mapa = { "Hotel": "hotel", "Vuelo": "flight", "Aereo": "flight", "Traslado": "transfer", "Paquete": "package", "Asistencia": "assistance" };
    return mapa[tipo] || "generic";
}

/**
 * Permite imputar un pago a un servicio concreto de una reserva del proveedor.
 * Componente de presentación puro: la carga de servicios y el filtro por moneda del
 * pago (pre-chequeo (a) de la Tanda 1) viven en el padre `PagarProveedorInline`, porque
 * ese mismo dato también decide si se muestra el aviso "esta reserva no tiene servicios
 * de este proveedor" (pre-chequeo (b)) — cargarlo una sola vez evita pedidos duplicados.
 *
 * Si `servicios` llega vacío (después del filtro por moneda) pero SÍ hay servicios de
 * este proveedor en la reserva en OTRA moneda, se lo decimos: no es el mismo caso que
 * "esta reserva no tiene servicios de este proveedor" (ese lo bloquea el padre).
 */
function SelectorServicioImputacion({ servicios, hayServiciosEnOtraMoneda, servicioSeleccionado, onServicioChange }) {
    if (servicios.length === 0) {
        return (
            <div className="text-xs text-muted-foreground italic">
                {hayServiciosEnOtraMoneda
                    ? "Este proveedor no tiene servicios en esta reserva en la moneda del pago."
                    : "Este proveedor no tiene servicios en esta reserva."}
            </div>
        );
    }

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

export function PagarProveedorInline({ supplierId, balancesByCurrency, openInvoicedCharges = [], paymentToEdit = null, onGuardado, onCancelar, onSavingChange }) {
    // Determina la moneda con deuda activa (o la primera de la lista si todo está saldado)
    const monedaPrincipal = resolverMonedaPrincipalProveedor(balancesByCurrency);

    const esMultimoneda = Array.isArray(balancesByCurrency) && balancesByCurrency.length > 1;

    // true cuando estamos editando un pago existente (vs. creando uno nuevo)
    const esEdicion = paymentToEdit != null;

    // En edición: si el pago original era cruzado, solo se puede cambiar método, referencia y notas.
    // Regla igual a RegistrarCobroInline: el monto y el TC ya generaron asientos contables.
    const estaEditandoCruzado = esEdicion && (
        paymentToEdit.imputedCurrency != null &&
        paymentToEdit.imputedCurrency !== paymentToEdit.currency
    );
    const estaEditandoLiquidacion = esEdicion && Boolean(paymentToEdit.isOperatorChargeSettlement);
    const edicionEconomicaBloqueada = estaEditandoCruzado || estaEditandoLiquidacion;

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
    const [cargoSeleccionadoId, setCargoSeleccionadoId] = useState("");

    // ── Rediseño 2026-07-20: flujo de 2 pasos para pagos NUEVOS ───────────────
    // "elegir" = grilla "¿Qué estás pagando?" (Paso 1) | "pagar" = formulario con
    // el destino ya fijado (Paso 2). En modo edición este estado no se usa: el
    // formulario de edición sigue siendo de un solo paso, como antes del rediseño.
    const [paso, setPaso] = useState("elegir");
    // true cuando el cajero eligió explícitamente "Pago a cuenta (sin imputar)"
    // en el Paso 1 — reemplaza al viejo default silencioso de dejar todo vacío.
    const [esPagoACuenta, setEsPagoACuenta] = useState(false);
    // Cartel de éxito que se muestra después de guardar un pago NUEVO (reemplaza
    // el cierre silencioso). null = todavía no se guardó nada en esta apertura de la ficha.
    const [resultadoExito, setResultadoExito] = useState(null);

    const [saving, setSaving] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    // Avisa al padre cada vez que `saving` cambia (fix bloqueante review 2026-07-21,
    // security-data-risk-reviewer): SupplierAccountPage tiene su PROPIO botón "Registrar
    // pago / Cerrar" arriba de esta ficha, que hasta ahora no sabía si había un guardado en
    // curso — si el cajero lo tocaba justo mientras el POST estaba en vuelo, podía cerrar
    // la ficha en el peor momento. `onSavingChange` le permite al padre deshabilitar ESE
    // botón también, igual que ya hacen "Cancelar" y la X de acá adentro.
    // Deps: solo `saving` — `onSavingChange` puede venir como función nueva en cada render
    // del padre y no necesitamos re-disparar el aviso por eso, solo cuando el valor cambia.
    useEffect(() => {
        onSavingChange?.(saving);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [saving]);

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
        setCargoSeleccionadoId("");
        // Rediseño 2026-07-20: cada vez que se abre la ficha (nuevo pago o a editar
        // otro), se vuelve a arrancar en el Paso 1 sin destino elegido ni cartel de
        // éxito colgado de una apertura anterior.
        setPaso("elegir");
        setEsPagoACuenta(false);
        setResultadoExito(null);
    }, [paymentToEdit]); // eslint-disable-line react-hooks/exhaustive-deps

    // FIX bloqueante (review 2026-07-21, frontend-reviewer #3 + data-exposure-reviewer): si
    // este pedido fallaba, antes solo quedaba un console.warn (invisible para el cajero) y
    // la grilla se mostraba VACÍA — indistinguible del caso legítimo "este proveedor no
    // tiene deuda". Ahora se guarda el error aparte (`reservasError`) para mostrar un cartel
    // con botón "Reintentar" (mismo patrón que `RecuadroDatosTransferencia` de más arriba en
    // este archivo), y `cargarReservasConDeuda` queda como función reusable para ese botón.
    const [reservasError, setReservasError] = useState(null);

    const cargarReservasConDeuda = useCallback(() => {
        if (!supplierId) return undefined;
        let cancelled = false;
        setReservasLoading(true);
        setReservasError(null);
        api.get(`/suppliers/${supplierId}/account/debt-by-reserva`)
            .then((response) => {
                if (!cancelled) setReservasConDeuda(response?.reservas || []);
            })
            .catch((err) => {
                if (!cancelled) {
                    setReservasError(
                        getApiErrorMessage(err, "No se pudieron cargar las reservas con deuda de este proveedor.")
                    );
                    setReservasConDeuda([]);
                }
            })
            .finally(() => { if (!cancelled) setReservasLoading(false); });

        return () => { cancelled = true; };
    }, [supplierId]);

    // Carga las reservas con deuda al montar la ficha (o si cambia de proveedor)
    useEffect(() => {
        const cleanup = cargarReservasConDeuda();
        return cleanup;
    }, [cargarReservasConDeuda]);

    // Cuando cambia la reserva elegida, limpiamos el servicio (puede no existir en la nueva reserva)
    useEffect(() => {
        setServicioSeleccionado(null);
    }, [reservaSeleccionada]);

    // ── Detalle de servicios para la grilla del Paso 1 (rediseño 2026-07-20) ───────────
    // El endpoint de deuda por reserva (debt-by-reserva) solo trae el TOTAL por moneda,
    // no el desglose por servicio. Para poder mostrar la columna "Detalle" de la grilla
    // (ej. "Hotel — Bariloche") sin pedirle un endpoint nuevo al backend, traemos acá los
    // servicios recientes de este proveedor (mismo endpoint que ya usa "Nueva factura")
    // y los agrupamos por reserva del lado del cliente. Es solo texto de presentación:
    // la deuda real que se paga sigue viniendo 100% de debt-by-reserva.
    const [serviciosProveedorParaGrilla, setServiciosProveedorParaGrilla] = useState([]);

    useEffect(() => {
        // Solo hace falta en el Paso 1 de un pago NUEVO — en edición no hay grilla.
        if (!supplierId || esEdicion) return;
        let cancelled = false;
        api.get(`/suppliers/${supplierId}/account/services?pageSize=100&sortBy=date&sortDir=desc`)
            .then((response) => { if (!cancelled) setServiciosProveedorParaGrilla(response?.items || []); })
            .catch((err) => {
                console.warn("[PagarProveedorInline] No se pudo cargar el detalle de servicios para la grilla:", err?.message);
                if (!cancelled) setServiciosProveedorParaGrilla([]);
            });
        return () => { cancelled = true; };
    }, [supplierId, esEdicion]);

    // Filas de la grilla "¿Qué estás pagando?" (una por reserva+moneda con deuda > 0) y el
    // mapa reservaPublicId → servicios que arma el texto de "Detalle" de cada fila.
    // FIX bloqueante (review 2026-07-21, frontend-reviewer #2): le pasamos `puedeVerMontos`
    // para que, sin ese permiso, la función NO filtre por saldo (el backend lo manda
    // enmascarado en 0 para TODAS las líneas por igual) — ver el comentario de la función.
    const filasDeuda = armarFilasDeudaPorReserva(reservasConDeuda, { puedeVerMontos });
    const serviciosPorReservaParaGrilla = agruparServiciosPorReserva(serviciosProveedorParaGrilla);

    // ── Servicios del proveedor en la reserva elegida (Tanda 1, 2026-07-18) ────────────
    // Se cargan ACÁ (no dentro del selector de servicio) porque el mismo dato alimenta
    // DOS pre-chequeos a la vez:
    //   (a) el selector de servicio filtra por la moneda del pago (filtrarServiciosPorMonedaDePago)
    //   (b) si la reserva no tiene NINGÚN servicio de este proveedor, se avisa antes de
    //       habilitar "Confirmar" (hayServiciosDelProveedorEnReserva)
    // Sin esto habría que duplicar el pedido al backend o levantar el estado del hijo,
    // que es más frágil.
    const [serviciosReserva, setServiciosReserva] = useState([]);
    const [serviciosReservaLoading, setServiciosReservaLoading] = useState(false);
    const [serviciosReservaError, setServiciosReservaError] = useState(null);

    useEffect(() => {
        if (!supplierId || !reservaSeleccionada?.reservaPublicId) {
            setServiciosReserva([]);
            setServiciosReservaError(null);
            return;
        }

        let cancelled = false;
        setServiciosReservaLoading(true);
        setServiciosReservaError(null);

        // Buscamos por número de reserva para reducir el resultado, luego filtramos por publicId.
        const params = new URLSearchParams({ pageSize: "100", sortBy: "date", sortDir: "asc" });
        if (reservaSeleccionada.numeroReserva) {
            params.set("search", reservaSeleccionada.numeroReserva);
        }

        api.get(`/suppliers/${supplierId}/account/services?${params.toString()}`)
            .then((response) => {
                if (cancelled) return;
                const items = response?.items || [];
                // Filtro client-side: solo los servicios de ESTA reserva concreta (todas las monedas)
                const deEstaReserva = items.filter(
                    (s) => String(s.reservaPublicId || "").toLowerCase() ===
                           String(reservaSeleccionada.reservaPublicId || "").toLowerCase()
                );
                setServiciosReserva(deEstaReserva);
            })
            .catch((err) => {
                if (cancelled) return;
                console.warn("[PagarProveedorInline] Error cargando servicios de la reserva:", err?.message);
                setServiciosReservaError("No se pudieron cargar los servicios de esta reserva.");
            })
            .finally(() => { if (!cancelled) setServiciosReservaLoading(false); });

        return () => { cancelled = true; };
    }, [supplierId, reservaSeleccionada?.reservaPublicId, reservaSeleccionada?.numeroReserva]);

    // Pre-chequeo (b): reserva elegida sin NINGÚN servicio de este proveedor.
    // Solo lo afirmamos cuando terminó de cargar sin error (mientras carga, no mostramos
    // el aviso para no asustar al vendedor con un "no tiene servicios" que todavía no se sabe).
    const reservaSinServiciosDelProveedor =
        Boolean(reservaSeleccionada) &&
        !serviciosReservaLoading &&
        !serviciosReservaError &&
        !hayServiciosDelProveedorEnReserva(serviciosReserva);

    // Pre-chequeo (a): el selector de servicio solo lista los que están en la moneda del pago.
    // Se recalcula solo cada vez que monedaPago cambia (el link "pagar en otra moneda").
    const serviciosFiltradosPorMoneda = filtrarServiciosPorMonedaDePago(serviciosReserva, monedaPago);

    // Si el cajero cambia la moneda del pago y el servicio que tenía elegido queda fuera
    // de la lista filtrada, lo deseleccionamos. Sin esto, el payload podría mandar un
    // servicio cuya moneda ya NO coincide con la del pago — justo el error #5 del backend
    // ("La moneda del pago no coincide con la del costo del servicio") que este pre-chequeo
    // existe para evitar. Deps: solo monedaPago (no serviciosFiltradosPorMoneda, que es un
    // array nuevo en cada render y dispararía el efecto todo el tiempo).
    useEffect(() => {
        if (!servicioSeleccionado) return;
        const sigueDisponible = serviciosFiltradosPorMoneda.some(
            (s) => String(s.publicId) === servicioSeleccionado.servicePublicId
        );
        if (!sigueDisponible) setServicioSeleccionado(null);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [monedaPago]);

    // Un pago cruza de moneda si el cajero elige pagar en una moneda distinta al saldo imputado.
    // En ese caso hay que informar el tipo de cambio para convertir correctamente.
    const esCruzado = mostrarOtraMoneda && monedaPago !== saldoImputado;

    // El equivalente se calcula en tiempo real para mostrárselo al cajero antes de confirmar
    const montoEquivalente = calcularEquivalenteProveedor(monto, tipoCambio, monedaPago, saldoImputado);

    const cargoSeleccionado = openInvoicedCharges.find(
        (charge) => String(charge.publicId) === String(cargoSeleccionadoId)
    ) || null;
    const monedaBanner = cargoSeleccionado?.currency || monedaPrincipal;
    const saldoBanner = balancesByCurrency?.find(
        (balance) => balance.currency === monedaBanner
    )?.balance ?? null;

    // El formulario no se puede enviar si en un pago cruzado faltan el TC o la fecha del TC
    const camposIncompletosParaCruzado = esCruzado && (!tipoCambio || parseFloat(tipoCambio) <= 0 || !fechaTC);

    const handleCargoChange = (publicId) => {
        setCargoSeleccionadoId(publicId);
        const charge = openInvoicedCharges.find((item) => String(item.publicId) === String(publicId));
        if (!charge) {
            setMonto("");
            setMonedaPago(monedaPrincipal);
            setSaldoImputado(monedaPrincipal);
            setMostrarOtraMoneda(false);
            setTipoCambio("");
            setFechaTC(fechaHoy());
            return;
        }

        setMonto(String(charge.amount));
        setMonedaPago(charge.currency);
        setSaldoImputado(charge.currency);
        setMostrarOtraMoneda(false);
        setReservaSeleccionada(null);
        setServicioSeleccionado(null);
    };

    const handleReservaChange = (e) => {
        const publicId = e.target.value;
        if (!publicId) { setReservaSeleccionada(null); return; }
        const encontrada = reservasConDeuda.find((r) => String(r.reservaPublicId) === publicId);
        setReservaSeleccionada(encontrada || { reservaPublicId: publicId, numeroReserva: publicId });
    };

    // ── Handlers del Paso 1 "¿Qué estás pagando?" (rediseño 2026-07-20) ───────────────

    // El cajero tocó "Pagar" en una fila de la grilla: fijamos el destino y pre-cargamos
    // monto/moneda con la deuda de ESA fila (editable hacia abajo, igual que ya hacía
    // "Liquidar cargo facturado aparte"). Sin permiso de ver costos no pre-cargamos un
    // monto enmascarado en 0 — dejamos el campo vacío para que lo tipeen a mano.
    const handleElegirFila = (fila) => {
        setReservaSeleccionada({
            reservaPublicId: fila.reservaPublicId,
            numeroReserva: fila.numeroReserva,
            fileName: fila.fileName,
            currency: fila.currency,
            debe: fila.balance,
            detalle: construirDetalleFilaDeuda(fila, serviciosPorReservaParaGrilla),
        });
        setServicioSeleccionado(null);
        setEsPagoACuenta(false);
        setCargoSeleccionadoId("");
        setMonto(puedeVerMontos ? String(fila.balance) : "");
        setMonedaPago(fila.currency);
        setSaldoImputado(fila.currency);
        setMostrarOtraMoneda(false);
        setTipoCambio("");
        setFechaTC(fechaHoy());
        setErrorGuardar(null);
        setPaso("pagar");
    };

    // "Pago a cuenta (sin imputar)": antes era dejar todo vacío por omisión, ahora es
    // una decisión explícita con su propio botón (spec 5.2.2 punto 1 del rediseño).
    const handleElegirPagoACuenta = () => {
        setReservaSeleccionada(null);
        setServicioSeleccionado(null);
        setEsPagoACuenta(true);
        setCargoSeleccionadoId("");
        setMonto("");
        setMonedaPago(monedaPrincipal);
        setSaldoImputado(monedaPrincipal);
        setMostrarOtraMoneda(false);
        setErrorGuardar(null);
        setPaso("pagar");
    };

    // Elegir "Liquidar cargo facturado aparte" desde el Paso 1: reusa exactamente la
    // misma lógica de pre-carga que ya tenía `handleCargoChange` y solo agrega la
    // transición al Paso 2.
    const handleElegirCargoDesdeGrilla = (publicId) => {
        handleCargoChange(publicId);
        setEsPagoACuenta(false);
        setErrorGuardar(null);
        if (publicId) setPaso("pagar");
    };

    // Botón "Cambiar" del encabezado del Paso 2: vuelve a la grilla y limpia el destino
    // elegido, para no arrastrar un monto que correspondía a OTRA reserva/cargo.
    // Fix menor (review 2026-07-21): también limpia el modo "pagar en otra moneda" y el
    // tipo de cambio — si no, un pago cruzado armado para la reserva ANTERIOR podía quedar
    // a medio configurar al elegir una reserva nueva desde cero.
    const handleCambiarDestino = () => {
        setPaso("elegir");
        setReservaSeleccionada(null);
        setServicioSeleccionado(null);
        setCargoSeleccionadoId("");
        setEsPagoACuenta(false);
        setMonto("");
        setMostrarOtraMoneda(false);
        setTipoCambio("");
        setFuenteTC(5);
        setFechaTC(fechaHoy());
        setErrorGuardar(null);
    };

    // ── Handlers del cartel de éxito (rediseño 2026-07-20) ─────────────────────────────
    // El pago YA se guardó Y el padre YA se refrescó (onGuardado() se llamó apenas el POST
    // resolvió OK, ver handleSubmit) — estos dos botones solo CIERRAN la ficha, no refrescan
    // de nuevo. "Ver cuenta" además baja la pantalla hasta el extracto para que se vea el
    // movimiento nuevo.
    const handleCerrarExito = () => {
        setResultadoExito(null);
        onCancelar();
    };

    const handleVerCuentaExito = () => {
        setResultadoExito(null);
        onCancelar();
        // El extracto ya está actualizado desde que se guardó el pago (onGuardado() se
        // llamó en ese momento, no ahora) — este timeout es solo para darle a React un
        // instante para terminar de cerrar esta ficha antes de mover el scroll.
        setTimeout(() => {
            document.querySelector('[data-testid="extracto-section"]')
                ?.scrollIntoView({ behavior: "smooth", block: "start" });
        }, 300);
    };

    // En el flujo nuevo, cuando el destino ya es una reserva puntual elegida desde la
    // grilla, el banner global "Pagás en $ — deuda $X" queda redundante con el encabezado
    // "Pagás la reserva… / Debe: $X" del Paso 2 — lo ocultamos para no mostrarle al cajero
    // dos números de "deuda" distintos a la vez (el global del proveedor vs. el de esta
    // reserva puntual). Para "pago a cuenta" y "liquidar cargo" el banner global sigue
    // siendo la referencia correcta, así que ahí no se toca.
    const ocultarBannerGlobalPorDestinoFijado = !esEdicion && Boolean(reservaSeleccionada);

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
        // Defensa en profundidad: el botón ya queda deshabilitado en este caso (pre-chequeo (b),
        // Tanda 1), pero repetimos la validación acá por si el estado cambió mientras la ficha
        // estaba abierta (ej. otra pestaña canceló el último servicio de esa reserva).
        if (reservaSinServiciosDelProveedor) {
            setErrorGuardar("Esta reserva no tiene servicios de este proveedor para imputar el pago.");
            return;
        }

        setSaving(true);
        try {
            if (esEdicion && edicionEconomicaBloqueada) {
                // El endpoint valida el bloque económico completo aunque solo cambien datos auxiliares.
                // Reenviamos la foto original para que monto, moneda, TC e imputación sigan inmutables.
                const payloadInmutable = {
                    amount: Number(paymentToEdit.amount),
                    currency: paymentToEdit.currency,
                    imputedCurrency: paymentToEdit.imputedCurrency,
                    exchangeRate: paymentToEdit.exchangeRate,
                    exchangeRateSource: paymentToEdit.exchangeRateSource,
                    exchangeRateAt: paymentToEdit.exchangeRateAt,
                    imputedAmount: paymentToEdit.imputedAmount,
                    paidAt: paymentToEdit.paidAt,
                    reservaId: paymentToEdit.reservaPublicId || null,
                    isAdvanceToAccount: Boolean(paymentToEdit.isAdvanceToAccount),
                    serviceRecordKind: paymentToEdit.serviceRecordKind || null,
                    servicePublicId: paymentToEdit.servicePublicId || null,
                    method: metodo,
                    reference: referencia.trim() || null,
                    notes: notas.trim() || null,
                };
                await api.put(`/suppliers/${supplierId}/payments/${getPublicId(paymentToEdit)}`, payloadInmutable);
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
                    settlesOperatorChargePublicId: cargoSeleccionado?.publicId || null,
                });
                const respuesta = await api.post(`/suppliers/${supplierId}/payments`, payload);

                // FIX bloqueante (review 2026-07-21, dos revisores convergentes): el POST YA
                // resolvió OK del lado del servidor en esta línea — el refresco del padre
                // (overview/extracto/deuda-por-reserva) tiene que dispararse ACÁ, siempre,
                // sin esperar a que el cajero cierre el cartel. Si esperáramos, un cajero que
                // cierra la ficha desde OTRO botón (el de arriba de la página) mientras el
                // cartel está abierto dejaba la pantalla desactualizada con un pago que SÍ se
                // guardó. El cartel de acá abajo queda como AVISO nada más, no como gate.
                // Si el refresco falla, el pago igual quedó guardado: solo lo registramos
                // en consola para no dejar un rechazo sin manejar.
                Promise.resolve(onGuardado()).catch((err) =>
                    console.warn("No se pudo refrescar la cuenta tras el pago:", err?.message)
                );

                // Rediseño 2026-07-20: reemplaza el cierre silencioso. `impact` viene del
                // backend (recalculado con el mismo motor que "Deuda por reserva"), así que
                // el cartel de éxito nunca inventa un saldo restante de memoria — el monto
                // imputado es el equivalente en la moneda del saldo si el pago fue cruzado,
                // o el monto tal cual si no. construirMensajeExitoPago SIEMPRE devuelve un
                // mensaje (nunca null): el pago YA se guardó, así que siempre hay que
                // mostrar el cartel — dejar que el formulario reaparezca acá sería el bug
                // más grave posible (un segundo clic en "Confirmar" duplicaría el pago).
                const montoImputado = esCruzado ? montoEquivalente : parseFloat(monto);
                setResultadoExito(
                    construirMensajeExitoPago({
                        impact: respuesta?.impact,
                        montoImputado,
                        monedaImputada: saldoImputado,
                    })
                );
                // return acá: el bloque `finally` de abajo igual corre y apaga `saving`.
                // El toast global no hace falta (el cartel ya avisa); el cierre de la ficha
                // (onCancelar) se dispara recién cuando el cajero toca "Cerrar"/"Ver cuenta".
                return;
            }

            showSuccess("Pago actualizado.");
            onGuardado();
            onCancelar();
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
                    // Con el cartel de éxito abierto, la X cierra la ficha (el refresco del
                    // padre ya ocurrió al guardar, no acá) — sin cartel, es un cancelar normal.
                    onClick={resultadoExito ? handleCerrarExito : onCancelar}
                    // Fix bloqueante (review 2026-07-21, security-data-risk-reviewer): mientras
                    // el POST/PUT está en vuelo (`saving`), esta X tiene que quedar bloqueada
                    // igual que el botón "Cancelar" de más abajo — si no, el cajero podía
                    // cerrar la ficha a mitad de un guardado y perder de vista si terminó bien
                    // o mal (o, peor, volver a abrirla y reintentar sobre un pedido que seguía
                    // en curso).
                    disabled={saving}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-50 disabled:cursor-not-allowed"
                    aria-label="Cerrar ficha de pago"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            {/* Aviso informativo en modo edición de pago cruzado:
                el monto/TC no son editables porque ya están contabilizados. */}
            {edicionEconomicaBloqueada && (
                <div className="rounded-lg bg-amber-50 border border-amber-200 dark:bg-amber-950/20 dark:border-amber-900/40 px-4 py-2 text-xs text-amber-700 dark:text-amber-300">
                    Este pago tiene datos contables vinculados. Solo podés cambiar el método, la referencia y las notas.
                </div>
            )}

            {/* Paso 1 "¿Qué estás pagando?" (rediseño 2026-07-20): solo para pagos NUEVOS.
                Reemplaza al viejo desplegable opcional "Imputar a una reserva" que vivía
                escondido al final del formulario. */}
            {!esEdicion && paso === "elegir" && (
                <PasoElegirDestinoPago
                    reservasLoading={reservasLoading}
                    reservasError={reservasError}
                    onReintentarReservas={cargarReservasConDeuda}
                    filasDeuda={filasDeuda}
                    serviciosPorReservaParaGrilla={serviciosPorReservaParaGrilla}
                    puedeVerMontos={puedeVerMontos}
                    openInvoicedCharges={openInvoicedCharges}
                    onElegirFila={handleElegirFila}
                    onElegirCargo={handleElegirCargoDesdeGrilla}
                    onElegirPagoACuenta={handleElegirPagoACuenta}
                />
            )}

            {(esEdicion || paso === "pagar") && !resultadoExito && (
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

                {/* Encabezado "destino fijado" (rediseño 2026-07-20): solo para pagos NUEVOS,
                    reemplaza al viejo desplegable de reserva escondido al final del form.
                    El botón "Cambiar" vuelve al Paso 1 sin perder nada más de lo cargado
                    (por ahora limpia el destino, que es lo único que dejó de ser válido). */}
                {!esEdicion && (reservaSeleccionada || esPagoACuenta || cargoSeleccionado) && (
                    <div
                        className="flex items-start justify-between gap-3 rounded-lg border border-emerald-300 bg-white dark:bg-slate-800 dark:border-emerald-800 p-3"
                        data-testid="pago-destino-fijado"
                    >
                        <div className="min-w-0 space-y-1">
                            {esPagoACuenta ? (
                                <>
                                    <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                                        Pago a cuenta (sin imputar)
                                    </p>
                                    <p className="text-xs text-slate-500 dark:text-slate-400">
                                        Este pago no se imputa a ninguna reserva; queda como saldo a favor para usar después.
                                    </p>
                                </>
                            ) : cargoSeleccionado ? (
                                <>
                                    <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                                        Liquidás el cargo {cargoSeleccionado.documentRef || "sin referencia"} — {cargoSeleccionado.numeroReserva}
                                    </p>
                                    <p className="text-xs text-slate-500 dark:text-slate-400">
                                        El monto y la moneda quedan fijados por el documento del operador.
                                    </p>
                                </>
                            ) : (
                                <>
                                    <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                                        Pagás la reserva {reservaSeleccionada.numeroReserva}
                                        {reservaSeleccionada.detalle ? ` — ${reservaSeleccionada.detalle}` : ""}
                                    </p>
                                    <p className="text-xs text-slate-500 dark:text-slate-400">
                                        Debe:{" "}
                                        {puedeVerMontos
                                            ? formatCurrency(reservaSeleccionada.debe, reservaSeleccionada.currency)
                                            : <span title="Sin permiso para ver montos">—</span>
                                        }
                                    </p>
                                    {/* Link "pagar en otra moneda": reubicado acá (antes vivía en el banner
                                        global, que ahora se oculta cuando el destino es una reserva puntual). */}
                                    {esMultimoneda && !mostrarOtraMoneda && (
                                        <button
                                            type="button"
                                            onClick={() => setMostrarOtraMoneda(true)}
                                            className="text-xs font-medium text-emerald-600 hover:text-emerald-800 dark:text-emerald-400 dark:hover:text-emerald-200 underline underline-offset-2"
                                            data-testid="pago-link-otra-moneda"
                                        >
                                            pagar en otra moneda
                                        </button>
                                    )}
                                </>
                            )}
                        </div>
                        <button
                            type="button"
                            onClick={handleCambiarDestino}
                            disabled={saving}
                            className="flex-shrink-0 text-xs font-semibold text-emerald-700 hover:text-emerald-900 underline underline-offset-2 dark:text-emerald-400 disabled:opacity-50"
                            data-testid="pago-cambiar-destino"
                        >
                            Cambiar
                        </button>
                    </div>
                )}

                {/* Banner: moneda principal + saldo actual.
                    Le confirma al cajero en qué moneda va a pagar antes de cualquier selector.
                    Enmascarado: si no tiene permiso cobranzas.see_cost, muestra "—" en vez del saldo.
                    Rediseño 2026-07-20: se oculta cuando el destino ya es una reserva puntual
                    (ese caso ya muestra su propio "Debe: $X" en el encabezado de arriba). */}
                {saldoBanner !== null && !ocultarBannerGlobalPorDestinoFijado && (
                    <div
                        className="flex items-center justify-between rounded-lg bg-emerald-50 border border-emerald-200 dark:bg-emerald-950/20 dark:border-emerald-900/40 px-4 py-2"
                        data-testid="pago-banner-moneda-principal"
                    >
                        <span className="text-sm font-semibold text-emerald-800 dark:text-emerald-300">
                            Pagás en {monedaBanner === "USD" ? "US$" : "$"} —{" "}
                            deuda{" "}
                            {puedeVerMontos
                                ? formatCurrency(saldoBanner, monedaBanner)
                                : <span className="text-slate-400" title="Sin permiso para ver montos">—</span>
                            }
                        </span>
                        {/* Link "pagar en otra moneda": solo en proveedores con deuda en ambas monedas.
                            No se muestra en modo edición de pago cruzado (ya está determinada la moneda). */}
                        {esMultimoneda && !mostrarOtraMoneda && !edicionEconomicaBloqueada && !cargoSeleccionado && (
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
                            disabled={saving || edicionEconomicaBloqueada || Boolean(cargoSeleccionado)}
                            onChange={(e) => setMonto(e.target.value)}
                            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                            placeholder="0,00"
                            data-testid="pago-monto"
                        />
                    </div>

                    {/* Moneda del pago: solo visible cuando el usuario activó "pagar en otra moneda" */}
                    {(esMultimoneda || edicionEconomicaBloqueada) && mostrarOtraMoneda && !cargoSeleccionado && (
                        <div className="space-y-1">
                            <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Moneda del pago</label>
                            <select
                                value={monedaPago}
                                disabled={saving || edicionEconomicaBloqueada}
                                onChange={(e) => setMonedaPago(e.target.value)}
                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
                                data-testid="pago-moneda"
                            >
                                {PAYMENT_CURRENCIES.map((currency) => (
                                    <option key={currency} value={currency}>
                                        {currency === "USD" ? "US$ Dólares" : "$ Pesos"}
                                    </option>
                                ))}
                            </select>
                        </div>
                    )}

                    {/* Imputar a: qué saldo se reduce. Solo en multimoneda con modo completo. */}
                    {(esMultimoneda || edicionEconomicaBloqueada) && mostrarOtraMoneda && !cargoSeleccionado && (
                        <div className="space-y-1">
                            <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">Imputar a</label>
                            <select
                                value={saldoImputado}
                                disabled={saving || edicionEconomicaBloqueada}
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
                            disabled={saving || edicionEconomicaBloqueada}
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
                                    disabled={saving || edicionEconomicaBloqueada}
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
                                    disabled={saving || edicionEconomicaBloqueada}
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
                                    disabled={saving || edicionEconomicaBloqueada}
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

                {/* Sección: imputar a reserva / servicio (opcional) — SOLO EN EDICIÓN.
                    En edición sigue siendo un desplegable (la spec del rediseño 2026-07-20
                    no tocó el modo edición). No se muestra en edición de pago cruzado para
                    simplificar (el pago ya tiene sus imputaciones registradas). */}
                {esEdicion && !edicionEconomicaBloqueada && !cargoSeleccionado && (
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

                                {/* Pre-chequeo (b), Tanda 1 (2026-07-18): aviso ANTES de habilitar
                                    "Confirmar" si la reserva elegida no tiene ningún servicio de
                                    este proveedor. Mismo lugar donde van el resto de los avisos
                                    de la ficha (ej. "El monto tiene que ser mayor a 0"). */}
                                {reservaSinServiciosDelProveedor && (
                                    <p
                                        className="text-xs text-amber-600 dark:text-amber-400"
                                        role="alert"
                                        data-testid="pago-reserva-sin-servicios"
                                    >
                                        Esta reserva no tiene servicios de este proveedor para imputar el pago.
                                    </p>
                                )}
                            </div>
                        )}

                        {/* Selector de servicio: aparece solo cuando el usuario eligió una reserva
                            que SÍ tiene servicios de este proveedor. Si no tiene ninguno, ya se
                            avisó arriba y no hay nada para elegir. */}
                        {reservaSeleccionada && !reservaSinServiciosDelProveedor && (
                            serviciosReservaLoading ? (
                                <div className="text-xs text-muted-foreground italic">Cargando servicios…</div>
                            ) : serviciosReservaError ? (
                                <div className="text-xs text-amber-600 dark:text-amber-400">{serviciosReservaError}</div>
                            ) : (
                                <SelectorServicioImputacion
                                    servicios={serviciosFiltradosPorMoneda}
                                    hayServiciosEnOtraMoneda={serviciosReserva.length > 0}
                                    servicioSeleccionado={servicioSeleccionado}
                                    onServicioChange={setServicioSeleccionado}
                                />
                            )
                        )}
                    </div>
                )}

                {/* Servicio puntual (opcional) — SOLO EN PAGO NUEVO con una reserva elegida
                    desde la grilla del Paso 1 (rediseño 2026-07-20). La reserva ya está fija
                    arriba (encabezado "Pagás la reserva…"); esto es un refinamiento opcional
                    para imputar el pago a UN servicio puntual de esa reserva, no a toda ella.
                    Reusa el mismo componente y las mismas dos validaciones de la Tanda 1
                    (2026-07-18) que ya tenía el formulario viejo. */}
                {!esEdicion && reservaSeleccionada && !cargoSeleccionado && !esPagoACuenta && (
                    <div className="space-y-2 rounded-xl border border-slate-200 dark:border-slate-700 p-4 bg-slate-50/50 dark:bg-slate-800/30">
                        {reservaSinServiciosDelProveedor ? (
                            <p
                                className="text-xs text-amber-600 dark:text-amber-400"
                                role="alert"
                                data-testid="pago-reserva-sin-servicios"
                            >
                                Esta reserva no tiene servicios de este proveedor para imputar el pago.
                            </p>
                        ) : serviciosReservaLoading ? (
                            <div className="text-xs text-muted-foreground italic">Cargando servicios…</div>
                        ) : serviciosReservaError ? (
                            <div className="text-xs text-amber-600 dark:text-amber-400">{serviciosReservaError}</div>
                        ) : (
                            <SelectorServicioImputacion
                                servicios={serviciosFiltradosPorMoneda}
                                hayServiciosEnOtraMoneda={serviciosReserva.length > 0}
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
                        disabled={saving || (!esEdicion && camposIncompletosParaCruzado) || reservaSinServiciosDelProveedor}
                        className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white hover:bg-emerald-700 shadow-sm transition-colors disabled:opacity-50 flex items-center gap-2"
                        data-testid="pago-confirmar"
                    >
                        {saving ? "Guardando…" : esEdicion ? "Guardar cambios" : "Confirmar pago"}
                    </button>
                </div>
            </form>
            )}

            {/* Cartel de éxito (rediseño 2026-07-20): reemplaza el cierre silencioso que tenía
                la ficha antes. Solo aparece para pagos NUEVOS (la edición sigue usando el
                toast "Pago actualizado." de siempre). El texto sale de `construirMensajeExitoPago`,
                que arma la frase a partir de `impact` — nunca de un cálculo hecho acá. */}
            {!esEdicion && resultadoExito && (
                <div
                    className="rounded-xl border-2 border-emerald-300 bg-emerald-50 dark:bg-emerald-950/20 dark:border-emerald-800 p-5 space-y-3"
                    data-testid="pago-exito"
                    role="status"
                >
                    <div className="flex items-center gap-2 text-sm font-bold text-emerald-800 dark:text-emerald-300">
                        <Check className="h-4 w-4" />
                        Pago registrado
                    </div>
                    <div className="space-y-1 text-sm text-slate-700 dark:text-slate-300">
                        {resultadoExito.lineas.map((linea, indice) => (
                            <p key={indice}>{linea}</p>
                        ))}
                    </div>
                    <div className="flex justify-end gap-3 pt-1">
                        <button
                            type="button"
                            onClick={handleVerCuentaExito}
                            className="rounded-lg border border-emerald-300 bg-white px-4 py-2 text-sm font-semibold text-emerald-700 hover:bg-emerald-50 dark:bg-slate-800 dark:border-emerald-800 dark:text-emerald-400 transition-colors"
                            data-testid="pago-exito-ver-cuenta"
                        >
                            Ver cuenta
                        </button>
                        <button
                            type="button"
                            onClick={handleCerrarExito}
                            className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white hover:bg-emerald-700 shadow-sm transition-colors"
                            data-testid="pago-exito-cerrar"
                        >
                            Cerrar
                        </button>
                    </div>
                </div>
            )}
        </div>
    );
}

// ─── Paso 1: grilla "¿Qué estás pagando?" ─────────────────────────────────────

/**
 * Primer paso del rediseño "Registrar pago" (2026-07-20): en vez de pedir el monto y
 * dejar la reserva como un desplegable opcional escondido al final, esta pantalla le
 * pregunta al cajero PRIMERO a qué le está pagando. Tres caminos posibles, los tres
 * llevan al Paso 2 con el destino ya fijado:
 *   1. Tocar "Pagar" en una fila de la grilla → paga la deuda de esa reserva/moneda.
 *   2. Elegir un cargo del operador facturado aparte → liquida ESE documento puntual.
 *   3. "Pago a cuenta (sin imputar)" → decisión explícita de no imputar a nada, con
 *      su propio botón (antes era el default silencioso de dejar todo vacío).
 *
 * Componente de presentación puro: no llama a la API, solo avisa al padre qué eligió
 * el cajero a través de los callbacks `onElegirFila` / `onElegirCargo` / `onElegirPagoACuenta`.
 */
function PasoElegirDestinoPago({
    reservasLoading,
    reservasError,
    onReintentarReservas,
    filasDeuda,
    serviciosPorReservaParaGrilla,
    puedeVerMontos,
    openInvoicedCharges,
    onElegirFila,
    onElegirCargo,
    onElegirPagoACuenta,
}) {
    return (
        <div className="space-y-4" data-testid="pago-paso-elegir">
            <div className="flex items-center gap-2">
                <Layers className="h-4 w-4 text-slate-500" />
                <h5 className="text-sm font-bold text-slate-700 dark:text-slate-300">¿Qué estás pagando?</h5>
            </div>

            {reservasLoading ? (
                <div className="text-xs text-muted-foreground italic">Cargando reservas con deuda…</div>
            ) : reservasError ? (
                // FIX bloqueante (review 2026-07-21, frontend-reviewer #3 + data-exposure):
                // antes un error acá se tragaba en un console.warn y la grilla quedaba VACÍA,
                // indistinguible de "este proveedor no tiene deuda" (que es un estado normal,
                // no un error). Mismo patrón visual que RecuadroDatosTransferencia de más
                // arriba en este archivo: cartel rojo + botón para reintentar el mismo pedido.
                <div
                    className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-950/10 p-4 space-y-2"
                    data-testid="pago-reservas-error"
                    role="alert"
                >
                    <div className="flex items-center gap-2 text-sm font-semibold text-rose-700 dark:text-rose-300">
                        <AlertCircle className="h-4 w-4 flex-shrink-0" />
                        {reservasError}
                    </div>
                    <button
                        type="button"
                        onClick={onReintentarReservas}
                        className="rounded-lg border border-rose-200 bg-white px-3 py-1.5 text-xs font-medium text-rose-700 hover:bg-rose-50 dark:border-rose-900/40 dark:bg-slate-900 dark:text-rose-400 transition-colors"
                        data-testid="pago-reservas-reintentar"
                    >
                        Reintentar
                    </button>
                </div>
            ) : filasDeuda.length === 0 ? (
                <div className="text-xs text-muted-foreground italic" data-testid="pago-sin-deuda-por-reserva">
                    Este proveedor no tiene reservas con deuda pendiente.
                </div>
            ) : (
                <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
                    <table className="w-full text-sm" data-testid="pago-grilla-deuda">
                        <thead className="bg-slate-50 dark:bg-slate-800/50 text-xs uppercase tracking-wide text-slate-500 dark:text-slate-400">
                            <tr>
                                <th scope="col" className="px-3 py-2 text-left font-semibold">Reserva</th>
                                <th scope="col" className="px-3 py-2 text-left font-semibold">Detalle</th>
                                <th scope="col" className="px-3 py-2 text-left font-semibold">Moneda</th>
                                <th scope="col" className="px-3 py-2 text-right font-semibold">Debe</th>
                                <th scope="col" className="px-3 py-2 text-center font-semibold">Acción</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                            {filasDeuda.map((fila) => (
                                <tr key={`${fila.reservaPublicId}-${fila.currency}`}>
                                    <td className="px-3 py-2 font-semibold text-slate-800 dark:text-slate-200">
                                        {fila.numeroReserva}
                                    </td>
                                    <td className="px-3 py-2 text-slate-600 dark:text-slate-400">
                                        {construirDetalleFilaDeuda(fila, serviciosPorReservaParaGrilla)}
                                    </td>
                                    <td className="px-3 py-2 text-slate-600 dark:text-slate-400">
                                        {fila.currency === "USD" ? "US$" : "$"}
                                    </td>
                                    <td className="px-3 py-2 text-right font-semibold text-slate-800 dark:text-slate-200">
                                        {puedeVerMontos
                                            ? formatCurrency(fila.balance, fila.currency)
                                            : <span className="text-slate-400" title="Sin permiso para ver montos">—</span>
                                        }
                                    </td>
                                    <td className="px-3 py-2 text-center">
                                        <button
                                            type="button"
                                            onClick={() => onElegirFila(fila)}
                                            className="rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-emerald-700 transition-colors"
                                            data-testid={`pago-elegir-fila-${fila.reservaPublicId}-${fila.currency}`}
                                        >
                                            Pagar
                                        </button>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}

            {/* "Liquidar cargo facturado aparte": mismo concepto de siempre, reubicado acá
                (antes vivía escondido dentro del formulario, después del monto). */}
            {puedeVerMontos && openInvoicedCharges.length > 0 && (
                <div className="space-y-1 rounded-lg border border-amber-200 bg-amber-50/60 p-3 dark:border-amber-900/40 dark:bg-amber-950/10">
                    <label className="text-xs font-semibold text-amber-800 dark:text-amber-300">
                        Liquidar cargo facturado aparte (opcional)
                    </label>
                    <select
                        value=""
                        onChange={(event) => onElegirCargo(event.target.value)}
                        className="w-full rounded-lg border border-amber-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-amber-400 dark:border-amber-800 dark:bg-slate-800 dark:text-white"
                        data-testid="pago-elegir-cargo"
                    >
                        <option value="">Elegí un cargo del operador…</option>
                        {openInvoicedCharges.map((charge) => (
                            <option key={charge.publicId} value={charge.publicId}>
                                {charge.documentRef || "Sin referencia"} · {charge.numeroReserva} · {formatCurrency(charge.amount, charge.currency)}
                            </option>
                        ))}
                    </select>
                </div>
            )}

            {/* "Pago a cuenta (sin imputar)": decisión explícita con nombre propio (spec 5.2.2
                punto 1) — reemplaza al viejo default silencioso de dejar todo vacío. */}
            <div className="rounded-xl border-2 border-dashed border-slate-300 bg-slate-50/60 dark:border-slate-700 dark:bg-slate-800/20 p-4 space-y-2">
                <p className="text-xs text-slate-600 dark:text-slate-400">¿No es para ninguna reserva puntual?</p>
                <button
                    type="button"
                    onClick={onElegirPagoACuenta}
                    className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-100 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-200 transition-colors"
                    data-testid="pago-a-cuenta-boton"
                >
                    Pago a cuenta (sin imputar)
                </button>
            </div>
        </div>
    );
}
