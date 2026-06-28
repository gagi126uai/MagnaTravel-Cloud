import { useCallback, useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { hasPermission } from "../../../auth";
import {
    ArrowLeft,
    Building2,
    Phone,
    Mail,
    FileText,
    Plus,
    Pencil,
    Trash2,
    Search,
    Filter,
    Check,
    X,
    Layers,
    ExternalLink,
    TrendingDown,
    TrendingUp,
} from "lucide-react";
import { api } from "../../../api";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import {
    DataGrid,
    DataGridBody,
    DataGridCell,
    DataGridEmptyState,
    DataGridHeader,
    DataGridHeaderCell,
    DataGridHeaderRow,
    DataGridRow,
} from "../../../components/ui/DataGrid";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { showSuccess, showError, showConfirm } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";
import { useDebounce } from "../../../hooks/useDebounce";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { SupplierExtractoSection } from "../components/SupplierExtractoSection";
import { PagarProveedorInline } from "../components/PagarProveedorInline";
import { UsarSaldoOperadorInline } from "../components/UsarSaldoOperadorInline";
import { ListaCuentasBancarias } from "../../../features/bank-accounts/components/ListaCuentasBancarias";
import { OperatorRefundsPendingSection } from "../components/OperatorRefundsPendingSection";

const emptyPage = {
    items: [],
    page: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0,
    hasPreviousPage: false,
    hasNextPage: false,
};

// ─── Franja de saldo separada por moneda ──────────────────────────────────────

/**
 * Tarjetas de resumen financiero del proveedor, una por moneda.
 *
 * Regla multimoneda: NUNCA se suman pesos con dólares en un solo número.
 * Cada moneda tiene su propia tarjeta con el saldo corriente.
 *
 * Semáforo visual:
 *   - Balance > 0 (le debemos): rojo → "Le debo en $"
 *   - Balance = 0 (sin deuda):  gris → "Sin deuda en $"
 *   - Balance < 0 (pagamos de más): verde → "A favor con este proveedor: $"
 *
 * Enmascarado: si el usuario no tiene permiso cobranzas.see_cost, los montos
 * se muestran como "—" (el backend ya devuelve 0, pero mostramos "—" para que
 * no confunda "sin permiso" con "no hay deuda").
 *
 * Props:
 *   - balancesByCurrency: Array<{ currency, confirmedPurchases, totalPaid, balance }>
 *   - serviceCount: number — cantidad de servicios comprados al proveedor
 *   - onRegistrarPago: () => void — abre la ficha de pago en línea
 *   - mostrandoPago: boolean — true cuando la ficha de pago ya está abierta
 *   - onUsarSaldo: (currency: string) => void — abre la ficha de "Usar saldo a favor" para esa moneda
 *   - monedaUsandoSaldo: string|null — moneda cuya ficha de saldo está abierta (para el toggle del botón)
 */
function FranjaSaldoPorMoneda({ balancesByCurrency, serviceCount, onRegistrarPago, mostrandoPago, onUsarSaldo, monedaUsandoSaldo }) {
    // hasPermission("cobranzas.see_cost") ya evalúa isAdmin internamente
    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    const balances = Array.isArray(balancesByCurrency) ? balancesByCurrency : [];

    return (
        <div className="flex flex-col gap-4 sm:flex-row sm:flex-wrap sm:items-stretch">

            {/* Tarjeta de conteo de servicios (siempre visible, sin permiso de costo) */}
            <div className="rounded-xl border bg-card p-4 shadow-sm flex-shrink-0">
                <div className="flex items-center gap-2 text-muted-foreground mb-1">
                    <FileText className="h-4 w-4" />
                    <span className="text-sm">Servicios</span>
                </div>
                <p className="text-2xl font-bold">{serviceCount ?? 0}</p>
            </div>

            {/* Una tarjeta por moneda.
                Si balancesByCurrency está vacío (endpoint viejo o proveedor sin movimientos),
                no mostramos nada extra y el cajero puede registrar el primer pago igual. */}
            {balances.map((balance) => {
                const deuda = balance.balance ?? 0;
                const esAFavor = deuda < 0;
                const esCero = deuda === 0;

                return (
                    <div
                        key={balance.currency}
                        className={`rounded-xl border p-4 shadow-sm flex-1 min-w-[200px] ${
                            esAFavor
                                ? "border-emerald-200 bg-emerald-50 dark:border-emerald-900/40 dark:bg-emerald-950/20"
                                : esCero
                                ? "border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/20"
                                : "border-rose-200 bg-rose-50/60 dark:border-rose-900/40 dark:bg-rose-950/20"
                        }`}
                        data-testid={`saldo-moneda-${balance.currency}`}
                    >
                        {/* Etiqueta de estado */}
                        <div className={`flex items-center gap-1.5 text-xs font-bold uppercase tracking-wider mb-1 ${
                            esAFavor ? "text-emerald-700 dark:text-emerald-400"
                            : esCero ? "text-slate-500"
                            : "text-rose-700 dark:text-rose-400"
                        }`}>
                            {esAFavor
                                ? <><TrendingUp className="h-3.5 w-3.5" /> A favor con este proveedor en {balance.currency === "USD" ? "US$" : "$"}</>
                                : esCero
                                ? `Sin deuda en ${balance.currency === "USD" ? "US$" : "$"}`
                                : <><TrendingDown className="h-3.5 w-3.5" /> Le debo en {balance.currency === "USD" ? "US$" : "$"}</>
                            }
                        </div>

                        {/* Monto principal */}
                        <p className={`text-2xl font-bold ${
                            esAFavor ? "text-emerald-700 dark:text-emerald-400"
                            : esCero ? "text-slate-500"
                            : "text-rose-700 dark:text-rose-400"
                        }`}>
                            {puedeVerMontos
                                ? formatCurrency(Math.abs(deuda), balance.currency)
                                : "—"
                            }
                        </p>

                        {/* Desglose compras / pagado: visible solo con permiso */}
                        {puedeVerMontos && (
                            <div className="mt-2 flex gap-3 text-[10px] text-muted-foreground">
                                <span>Compras: {formatCurrency(balance.confirmedPurchases ?? 0, balance.currency)}</span>
                                <span>Pagado: {formatCurrency(balance.totalPaid ?? 0, balance.currency)}</span>
                            </div>
                        )}

                        {/* Aviso sin permiso */}
                        {!puedeVerMontos && (
                            <p className="mt-1 text-[10px] text-muted-foreground">
                                Sin permiso para ver montos de costo
                            </p>
                        )}

                        {/* Botón "Usar saldo a favor": solo aparece en carteles verdes (a favor)
                            y solo si el usuario tiene el permiso para gestionar pagos.
                            Alterna entre "Usar saldo" y "Cerrar" si la ficha ya está abierta. */}
                        {esAFavor && hasPermission("tesoreria.supplier_payments") && (
                            <div className="mt-3 border-t border-emerald-100 dark:border-emerald-900/30 pt-2">
                                <button
                                    type="button"
                                    onClick={() => onUsarSaldo && onUsarSaldo(balance.currency)}
                                    className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-semibold transition-colors ${
                                        monedaUsandoSaldo === balance.currency
                                            ? "bg-emerald-200 text-emerald-800 hover:bg-emerald-300 dark:bg-emerald-900/60 dark:text-emerald-200"
                                            : "bg-emerald-600 text-white hover:bg-emerald-700 shadow-sm"
                                    }`}
                                    data-testid={`btn-usar-saldo-${balance.currency}`}
                                >
                                    <TrendingUp className="h-3.5 w-3.5" />
                                    {monedaUsandoSaldo === balance.currency ? "Cerrar" : "Usar saldo a favor"}
                                </button>
                            </div>
                        )}
                    </div>
                );
            })}

            {/* Botón de registrar pago: solo visible con permiso tesoreria.supplier_payments.
                Sin ese permiso el cajero puede VER la cuenta corriente pero no registrar pagos. */}
            {hasPermission("tesoreria.supplier_payments") && (
                <div className="flex items-center sm:ml-auto">
                    <button
                        type="button"
                        onClick={onRegistrarPago}
                        className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors ${
                            mostrandoPago
                                ? "bg-slate-500 hover:bg-slate-600 shadow-slate-500/20"
                                : "bg-emerald-600 hover:bg-emerald-700 shadow-emerald-500/20"
                        }`}
                        data-testid="btn-registrar-pago"
                    >
                        <Plus className="h-4 w-4" />
                        {mostrandoPago ? "Cerrar pago" : "Registrar pago"}
                    </button>
                </div>
            )}
        </div>
    );
}

// ─── Tabla de deuda por expediente ───────────────────────────────────────────

/**
 * Tabla de deuda al proveedor abierta POR EXPEDIENTE (reserva) y por moneda.
 *
 * Regla clave: NUNCA sumar pesos con dólares. Cada moneda ocupa su propio renglón
 * dentro de cada reserva y en el bloque de "Anticipos a cuenta".
 *
 * Enmascarado de montos: si el usuario NO tiene permiso cobranzas.see_cost,
 * el backend devuelve 0 para todos los montos. En ese caso mostramos "—" en gris
 * (no verde ni rojo) y un aviso en el encabezado para que quede claro que es
 * falta de permiso, no que la deuda es cero.
 *
 * Se usa en la pagina de cuenta corriente del proveedor (SupplierAccountPage),
 * debajo del extracto.
 *
 * Props:
 * - publicId: string — publicId del proveedor (para el endpoint)
 */
function SupplierDebtByReservaSection({ publicId }) {
    const [data, setData] = useState(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState(null);

    // hasPermission("cobranzas.see_cost") ya incluye el check de isAdmin internamente.
    // Si es false, el backend devuelve 0 en todos los montos → mostramos "—" en gris
    // para distinguir "sin permiso" de "no hay deuda".
    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    // Carga la deuda por expediente al montar (o al cambiar el proveedor).
    // El backend ya filtra por permiso: si no tiene see_cost, los montos llegan enmascarados.
    useEffect(() => {
        let cancelled = false;
        setLoading(true);
        setError(null);

        api.get(`/suppliers/${publicId}/account/debt-by-reserva`)
            .then((response) => {
                if (!cancelled) {
                    setData(response);
                }
            })
            .catch((err) => {
                if (!cancelled) {
                    setError(err?.message || "No se pudo cargar la deuda por expediente.");
                }
            })
            .finally(() => {
                if (!cancelled) setLoading(false);
            });

        // Limpieza: si el componente se desmonta antes de que llegue la respuesta,
        // no actualizamos el estado para evitar memory leaks y warnings de React.
        return () => { cancelled = true; };
    }, [publicId]);

    if (loading) {
        return (
            <div
                className="overflow-hidden rounded-xl border bg-card shadow-sm"
                data-testid="deuda-loading"
            >
                <div className="border-b p-4 flex items-center gap-2">
                    <Layers className="h-5 w-5" />
                    <h2 className="font-semibold">Deuda por expediente</h2>
                </div>
                <div className="p-6 text-sm text-muted-foreground text-center">Cargando deuda por expediente...</div>
            </div>
        );
    }

    if (error) {
        return (
            <div
                className="overflow-hidden rounded-xl border bg-card shadow-sm"
                data-testid="deuda-error"
            >
                <div className="border-b p-4 flex items-center gap-2">
                    <Layers className="h-5 w-5" />
                    <h2 className="font-semibold">Deuda por expediente</h2>
                </div>
                <div className="p-6 text-sm text-rose-600 text-center">
                    No se pudo cargar la información. Intentá recargar la página.
                </div>
            </div>
        );
    }

    // Si no hay reservas ni anticipos, tampoco hay deuda.
    const reservas = data?.reservas || [];
    const anticipos = data?.advancesToAccount || [];
    const globales = data?.globalTotals || [];
    const hayDatos = reservas.length > 0 || anticipos.length > 0;

    return (
        <div
            className="overflow-hidden rounded-xl border bg-card shadow-sm"
            data-testid="deuda-por-expediente-section"
        >
            <div className="border-b p-4 flex items-center justify-between flex-wrap gap-2">
                <div className="flex flex-col gap-1">
                    <h2
                        className="flex items-center gap-2 font-semibold"
                        data-testid="deuda-por-expediente-title"
                    >
                        <Layers className="h-5 w-5" />
                        Deuda por expediente
                    </h2>
                    {/* Aviso de sin permiso: el backend manda 0 en todos los montos cuando
                        el usuario no tiene cobranzas.see_cost. Mostramos el aviso para que
                        no se confunda "sin permiso" con "no hay deuda". */}
                    {!puedeVerMontos && (
                        <p
                            className="text-xs text-muted-foreground"
                            data-testid="deuda-sin-permiso-aviso"
                        >
                            No tenés permiso para ver los montos de deuda.
                        </p>
                    )}
                </div>
                {/* Total global por moneda: reconcilia con el saldo de la cuenta corriente general.
                    Si no hay permiso, mostramos "—" en gris en vez de "$0,00" en verde (que parecería
                    que no se debe nada). */}
                {globales.length > 0 && (
                    <div className="flex items-center gap-3 text-sm" data-testid="deuda-global-totals">
                        <span className="text-muted-foreground text-xs uppercase tracking-wider">Total deuda</span>
                        {globales.map((global) => (
                            <span
                                key={global.currency}
                                className={
                                    !puedeVerMontos
                                        ? "font-bold font-mono text-muted-foreground"
                                        : `font-bold font-mono ${global.amount > 0 ? "text-red-600" : "text-emerald-600"}`
                                }
                                data-testid={`deuda-global-${global.currency}`}
                            >
                                {puedeVerMontos ? formatCurrency(global.amount, global.currency) : "—"}
                            </span>
                        ))}
                    </div>
                )}
            </div>

            {!hayDatos ? (
                <div
                    className="p-6 text-center text-sm text-muted-foreground"
                    data-testid="deuda-empty"
                >
                    No hay deuda registrada con este proveedor.
                </div>
            ) : (
                <div className="divide-y">
                    {/* =========================================================
                        Bloque de reservas: una fila por reserva + moneda.
                        Regla anti-mezcla: cada moneda ocupa su propia celda
                        (nunca sumamos ARS con USD en una misma linea).
                    ========================================================= */}
                    {reservas.map((reserva) => (
                        <div
                            key={reserva.reservaPublicId}
                            className="p-4 space-y-2"
                            data-testid={`deuda-reserva-${reserva.reservaPublicId}`}
                        >
                            {/* Cabecera de la reserva: Link real en vez de button+navigate
                                para que funcione Ctrl+click / abrir en pestaña nueva. */}
                            <div className="flex items-center gap-2">
                                <Link
                                    to={`/reservas/${reserva.reservaPublicId}`}
                                    className="inline-flex items-center gap-1 font-bold text-primary hover:underline text-sm"
                                    title="Ir a la reserva"
                                    data-testid={`link-reserva-${reserva.reservaPublicId}`}
                                >
                                    {reserva.numeroReserva || "Ver reserva"}
                                    <ExternalLink className="h-3 w-3" />
                                </Link>
                                {reserva.fileName && (
                                    <span className="text-xs text-muted-foreground">
                                        — {reserva.fileName}
                                    </span>
                                )}
                            </div>

                            {/* Detalle por moneda: cada moneda va en su propia fila.
                                Si no hay permiso: mostramos "—" en gris en Compras/Pagado/Saldo,
                                sin color rojo/verde que induzca a error. */}
                            <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                                {(reserva.currencies || []).map((linea) => (
                                    <div
                                        key={linea.currency}
                                        className="flex items-center justify-between rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 dark:border-slate-800 dark:bg-slate-800/40"
                                        data-testid={`deuda-reserva-${reserva.reservaPublicId}-${linea.currency}`}
                                    >
                                        <div className="space-y-0.5">
                                            <div className="text-[10px] font-black uppercase tracking-wider text-muted-foreground">
                                                {linea.currency}
                                            </div>
                                            <div className="text-[10px] text-muted-foreground">
                                                Compras:{" "}
                                                <span className="font-mono">
                                                    {puedeVerMontos
                                                        ? formatCurrency(linea.confirmedPurchases, linea.currency)
                                                        : "—"}
                                                </span>
                                            </div>
                                            <div className="text-[10px] text-muted-foreground">
                                                Pagado:{" "}
                                                <span className="font-mono">
                                                    {puedeVerMontos
                                                        ? formatCurrency(linea.totalPaid, linea.currency)
                                                        : "—"}
                                                </span>
                                            </div>
                                        </div>
                                        {/* Saldo: si no hay permiso se muestra "Sin permiso" en gris neutro,
                                            no en verde (que parecería "no se debe nada"). */}
                                        {puedeVerMontos ? (
                                            <div
                                                className={`font-bold font-mono text-sm ${linea.balance > 0 ? "text-red-600" : linea.balance < 0 ? "text-emerald-600" : "text-muted-foreground"}`}
                                                title={linea.balance > 0 ? "Deuda pendiente" : linea.balance < 0 ? "Saldo a favor" : "Sin saldo"}
                                            >
                                                {formatCurrency(linea.balance, linea.currency)}
                                            </div>
                                        ) : (
                                            <div
                                                className="text-muted-foreground text-sm"
                                                title="Sin permiso para ver montos"
                                            >
                                                Sin permiso
                                            </div>
                                        )}
                                    </div>
                                ))}
                            </div>
                        </div>
                    ))}

                    {/* =========================================================
                        Bloque de anticipos: pagos que NO estan imputados a ninguna
                        reserva concreta. Restan del total adeudado pero no se saben
                        a que expediente asignar.
                    ========================================================= */}
                    {anticipos.length > 0 && (
                        <div
                            className="p-4 space-y-2"
                            data-testid="deuda-anticipos-section"
                        >
                            <div className="flex items-center gap-2">
                                <span className="text-sm font-semibold text-muted-foreground">
                                    Anticipos a cuenta
                                </span>
                                <span className="text-xs text-muted-foreground italic">
                                    (pagos sin reserva imputada — restan del total)
                                </span>
                            </div>
                            <div className="flex flex-wrap gap-2">
                                {anticipos.map((anticipo) => (
                                    <div
                                        key={anticipo.currency}
                                        className="inline-flex items-center gap-2 rounded-lg border border-emerald-100 bg-emerald-50 px-3 py-1.5 dark:border-emerald-900/30 dark:bg-emerald-950/20"
                                        data-testid={`anticipo-${anticipo.currency}`}
                                    >
                                        <span className="text-[10px] font-black uppercase text-emerald-700 dark:text-emerald-300">
                                            {anticipo.currency}
                                        </span>
                                        {/* Anticipos reducen la deuda → se muestran como negativo (resta) */}
                                        <span className="font-bold font-mono text-sm text-emerald-700 dark:text-emerald-300">
                                            − {formatCurrency(anticipo.amount, anticipo.currency)}
                                        </span>
                                    </div>
                                ))}
                            </div>
                        </div>
                    )}
                </div>
            )}
        </div>
    );
}

// ─── Editor de estado del servicio ───────────────────────────────────────────

// Mapeo de Type (en espanol, viene del backend) -> endpoint de status update.
// Si no esta mapeado (servicios genericos), no se permite editar inline aca.
const STATUS_ENDPOINT_BY_TYPE = {
    "Hotel": "hotel-bookings",
    "Vuelo": "flight-segments",
    "Traslado": "transfer-bookings",
    "Paquete": "package-bookings",
};

const STATUS_OPTIONS = ["Solicitado", "Confirmado", "Cancelado"];

function ServiceStatusEditor({ service, onUpdated }) {
    const endpoint = STATUS_ENDPOINT_BY_TYPE[service.type];
    const [value, setValue] = useState(service.status || "Solicitado");
    const [saving, setSaving] = useState(false);

    if (!endpoint) {
        // Servicio generico — no editable desde aca, mostramos texto plano
        return <span className="text-sm">{service.status || "-"}</span>;
    }

    const handleChange = async (e) => {
        const newStatus = e.target.value;
        if (newStatus === value) return;
        const previous = value;
        setValue(newStatus);
        setSaving(true);
        try {
            await api.patch(`/${endpoint}/${service.publicId}/status`, { status: newStatus });
            showSuccess(`Estado actualizado a "${newStatus}"`);
            if (onUpdated) onUpdated();
        } catch (error) {
            // Revertir en UI
            setValue(previous);
            const message = error?.response?.data?.message || error?.message || "No se pudo actualizar el estado.";
            showError(message, "No se pudo cambiar el estado");
        } finally {
            setSaving(false);
        }
    };

    const colorClass = value === "Confirmado"
        ? "bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-950/30 dark:text-emerald-300 dark:border-emerald-800"
        : value === "Cancelado"
            ? "bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-950/30 dark:text-rose-300 dark:border-rose-800"
            : "bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-950/30 dark:text-amber-300 dark:border-amber-800";

    return (
        <select
            value={value}
            onChange={handleChange}
            disabled={saving}
            className={`rounded-md border text-xs font-bold px-2 py-1 ${colorClass} disabled:opacity-50`}
            title="Cambiar estado del servicio"
        >
            {STATUS_OPTIONS.map((opt) => (
                <option key={opt} value={opt}>{opt}</option>
            ))}
        </select>
    );
}

// ─── Editor del código de confirmación del servicio ──────────────────────────

// Editor inline del codigo de confirmacion del proveedor (PNR para vuelos,
// ConfirmationNumber para el resto). Misma logica de edicion que el status:
// click para entrar en modo edit, blur o Enter para guardar, Esc para cancelar.
function ServiceConfirmationEditor({ service, onUpdated }) {
    const endpoint = STATUS_ENDPOINT_BY_TYPE[service.type];
    const [editing, setEditing] = useState(false);
    const [value, setValue] = useState(service.confirmation || "");
    const [saving, setSaving] = useState(false);

    // Mantener sincronizado si cambia el dato externo (refresh)
    useEffect(() => {
        if (!editing) setValue(service.confirmation || "");
    }, [service.confirmation, editing]);

    if (!endpoint) {
        return <span className="font-mono text-xs">{service.confirmation || "-"}</span>;
    }

    const save = async () => {
        const trimmed = value.trim();
        const previous = service.confirmation || "";
        if (trimmed === previous) {
            setEditing(false);
            return;
        }
        setSaving(true);
        try {
            await api.patch(`/${endpoint}/${service.publicId}/status`, {
                status: service.status || "Solicitado",
                confirmationNumber: trimmed,
            });
            showSuccess(trimmed ? `Codigo guardado: ${trimmed}` : "Codigo eliminado");
            setEditing(false);
            if (onUpdated) onUpdated();
        } catch (error) {
            const message = error?.response?.data?.message || error?.message || "No se pudo guardar el codigo.";
            showError(message, "No se pudo guardar el codigo");
        } finally {
            setSaving(false);
        }
    };

    const cancel = () => {
        setValue(service.confirmation || "");
        setEditing(false);
    };

    if (!editing) {
        return (
            <button
                type="button"
                onClick={() => setEditing(true)}
                className="font-mono text-xs text-left hover:bg-accent px-1.5 py-0.5 rounded transition-colors"
                title="Editar codigo de confirmacion"
            >
                {service.confirmation || <span className="text-muted-foreground italic">(agregar)</span>}
            </button>
        );
    }

    return (
        <div className="inline-flex items-center gap-1">
            <input
                type="text"
                autoFocus
                value={value}
                disabled={saving}
                onChange={(e) => setValue(e.target.value)}
                onKeyDown={(e) => {
                    if (e.key === "Enter") save();
                    if (e.key === "Escape") cancel();
                }}
                placeholder="Codigo..."
                className="rounded border border-input bg-background px-1.5 py-0.5 text-xs font-mono w-28 focus:outline-none focus:ring-1 focus:ring-ring"
            />
            <button
                type="button"
                onClick={save}
                disabled={saving}
                className="rounded p-0.5 text-emerald-600 hover:bg-emerald-50 disabled:opacity-50"
                title="Guardar"
            >
                <Check className="h-3 w-3" />
            </button>
            <button
                type="button"
                onClick={cancel}
                disabled={saving}
                className="rounded p-0.5 text-slate-500 hover:bg-slate-100 disabled:opacity-50"
                title="Cancelar"
            >
                <X className="h-3 w-3" />
            </button>
        </div>
    );
}

// ─── Página principal ─────────────────────────────────────────────────────────

/**
 * Pantalla de cuenta corriente del proveedor.
 *
 * Secciones (de arriba a abajo):
 *   1. Encabezado: nombre, datos de contacto, CUIT.
 *   2. Franja de saldo por moneda: una tarjeta por moneda con el saldo corriente.
 *      Botón "Registrar pago" abre la ficha en línea (sin modal).
 *   3. Ficha de pago en línea (cuando está abierta): debajo de la franja.
 *   4. Extracto de cuenta: libro mayor cronológico, un bloque por moneda.
 *   5. Deuda por expediente: saldo desglosado por reserva y moneda.
 *   6. Servicios comprados: lista operativa con estado y código de confirmación.
 */
export default function SupplierAccountPage() {
    const { publicId } = useParams();
    const navigate = useNavigate();

    const [overview, setOverview] = useState(null);
    const [loadingOverview, setLoadingOverview] = useState(true);
    const [databaseUnavailable, setDatabaseUnavailable] = useState(false);

    const [servicesPage, setServicesPage] = useState(emptyPage);
    const [servicesPaging, setServicesPaging] = useState({ page: 1, pageSize: 25 });
    const [servicesLoading, setServicesLoading] = useState(true);
    const [serviceSearch, setServiceSearch] = useState("");
    const [serviceType, setServiceType] = useState("all");
    const debouncedServiceSearch = useDebounce(serviceSearch, 300);

    // extractoRefreshKey se incrementa al registrar un pago nuevo para que
    // el extracto y el overview se recarguen automáticamente sin que el usuario
    // tenga que refrescar la página.
    const [extractoRefreshKey, setExtractoRefreshKey] = useState(0);

    // showPagoInline: controla si la ficha de pago en línea está abierta o cerrada.
    const [showPagoInline, setShowPagoInline] = useState(false);

    // paymentToEdit: cuando no es null, la ficha de pago se abre en modo edición
    // con los datos de este pago pre-cargados.
    const [paymentToEdit, setPaymentToEdit] = useState(null);

    // allPayments: lista plana de todos los pagos del proveedor (hasta 200 registros).
    // Se usa para cruzar con sourcePublicId de cada línea del extracto y poder
    // ofrecer el botón "Editar" con el objeto completo (monto, método, TC, etc.).
    const [allPayments, setAllPayments] = useState([]);

    // supplierCreditOverview: resultado de GET /suppliers/{id}/credit.
    // Contiene Currencies[].AvailableBalance y ActiveApplications[].
    // Independiente del overview general porque el saldo a favor puede diferir
    // si hay aplicaciones pendientes de amortizar.
    const [supplierCreditOverview, setSupplierCreditOverview] = useState(null);

    // monedaUsandoSaldo: qué moneda tiene la ficha "Usar saldo a favor" abierta.
    // null = ninguna abierta. Solo una moneda puede estar abierta a la vez.
    const [monedaUsandoSaldo, setMonedaUsandoSaldo] = useState(null);

    const loadOverview = useCallback(async () => {
        setLoadingOverview(true);
        try {
            const response = await api.get(`/suppliers/${publicId}/account`);
            setOverview(response);
            setDatabaseUnavailable(false);
        } catch (error) {
            console.error("Error loading supplier account:", error);
            setOverview(null);
            setDatabaseUnavailable(isDatabaseUnavailableError(error));
        } finally {
            setLoadingOverview(false);
        }
    }, [publicId]);

    // Carga hasta 200 pagos del proveedor para poder hacer el cross-reference en el extracto.
    // Si hay más de 200 pagos los más viejos no tendrán botón Editar, pero sí Eliminar (tienen sourcePublicId).
    // Esta cantidad es un límite práctico; no se pagina (son datos de soporte, no de listado).
    const loadAllPayments = useCallback(async () => {
        if (!publicId) return;
        try {
            const response = await api.get(`/suppliers/${publicId}/account/payments?page=1&pageSize=200&sortBy=paidAt&sortDir=desc`);
            setAllPayments(response?.items || []);
        } catch (error) {
            // No bloqueante: si falla, el extracto sigue funcionando sin botones de edición.
            console.warn("[SupplierAccountPage] No se pudo cargar la lista de pagos para el extracto:", error?.message);
            setAllPayments([]);
        }
    }, [publicId]);

    // Carga el overview de crédito del proveedor (saldo a favor y aplicaciones activas).
    // Se llama al montar y al revertir una aplicación para que la lista se actualice.
    const loadSupplierCredit = useCallback(async () => {
        try {
            const creditData = await api.get(`/suppliers/${publicId}/credit`);
            setSupplierCreditOverview(creditData);
        } catch (error) {
            // No bloqueante: si falla, los carteles de saldo a favor siguen visibles
            // pero no se podrá ver la lista de aplicaciones activas ni usar el botón.
            console.warn("[SupplierAccountPage] No se pudo cargar el overview de crédito del proveedor:", error?.message);
            setSupplierCreditOverview(null);
        }
    }, [publicId]);

    const loadServices = useCallback(async () => {
        setServicesLoading(true);
        try {
            const params = new URLSearchParams({
                page: String(servicesPaging.page),
                pageSize: String(servicesPaging.pageSize),
                sortBy: "date",
                sortDir: "desc",
            });

            if (debouncedServiceSearch.trim()) {
                params.set("search", debouncedServiceSearch.trim());
            }

            if (serviceType !== "all") {
                params.set("type", serviceType);
            }

            const response = await api.get(`/suppliers/${publicId}/account/services?${params.toString()}`);
            setServicesPage({ ...emptyPage, ...(response || {}) });
            setDatabaseUnavailable(false);
        } catch (error) {
            console.error("Error loading supplier services:", error);
            setServicesPage(emptyPage);
            setDatabaseUnavailable(isDatabaseUnavailableError(error));
        } finally {
            setServicesLoading(false);
        }
    }, [debouncedServiceSearch, publicId, serviceType, servicesPaging.page, servicesPaging.pageSize]);

    // Refresca overview, pagos y extracto después de guardar un pago (nuevo o editado).
    // El extracto se recarga solo porque extractoRefreshKey es su dependencia de efecto.
    const handlePagoGuardado = useCallback(async () => {
        setShowPagoInline(false);
        setPaymentToEdit(null);
        setExtractoRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadAllPayments(), loadSupplierCredit()]);
    }, [loadOverview, loadAllPayments, loadSupplierCredit]);

    // Se llama al completar una aplicación de saldo a favor: recarga todo lo relacionado.
    const handleSaldoAplicado = useCallback(async () => {
        setMonedaUsandoSaldo(null);
        setExtractoRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadSupplierCredit()]);
    }, [loadOverview, loadSupplierCredit]);

    // Se llama al revertir una aplicación: recarga el extracto y el credit overview.
    const handleRevertirAplicacionTerminada = useCallback(async () => {
        setExtractoRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadSupplierCredit()]);
    }, [loadOverview, loadSupplierCredit]);

    // Abre la ficha de pago en modo edición con el pago seleccionado.
    // Si la ficha ya estaba abierta para un pago diferente, la reemplaza.
    const handleEditarPago = useCallback((payment) => {
        setPaymentToEdit(payment);
        setShowPagoInline(true);
        // Scroll al principio para que la ficha quede visible
        window.scrollTo({ top: 0, behavior: "smooth" });
    }, []);

    // Solicita confirmación y luego elimina un pago del proveedor.
    // Recarga el extracto y el resumen de saldos automáticamente.
    const handleEliminarPago = useCallback(async (payment) => {
        const paymentId = getPublicId(payment) || payment?.publicId;
        if (!paymentId) return;

        const result = await showConfirm({
            title: "Eliminar pago",
            text: "¿Seguro que querés eliminar este pago? El saldo del proveedor se va a restaurar.",
            confirmText: "Sí, eliminar",
            confirmColor: "rose",
        });

        if (!result?.isConfirmed) return;

        try {
            await api.delete(`/suppliers/${publicId}/payments/${paymentId}`);
            setExtractoRefreshKey((k) => k + 1);
            await Promise.all([loadOverview(), loadAllPayments()]);
            showSuccess("Pago eliminado.");
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo eliminar el pago."), "Error al eliminar");
        }
    }, [publicId, loadOverview, loadAllPayments]);

    // Resetear estado al cambiar de proveedor (navegación directa entre cuentas)
    useEffect(() => {
        setServicesPaging({ page: 1, pageSize: 25 });
        setServiceSearch("");
        setServiceType("all");
        setShowPagoInline(false);
        setPaymentToEdit(null);
        setAllPayments([]);
        setExtractoRefreshKey(0);
        setMonedaUsandoSaldo(null);
        setSupplierCreditOverview(null);
    }, [publicId]);

    useEffect(() => {
        loadOverview();
    }, [loadOverview]);

    // Carga el overview de crédito al montar y cada vez que cambia el proveedor.
    // useEffect con dependencia en loadSupplierCredit (que ya incluye publicId).
    useEffect(() => {
        loadSupplierCredit();
    }, [loadSupplierCredit]);

    useEffect(() => {
        loadServices();
    }, [loadServices]);

    // Carga la lista de pagos al montar y cada vez que cambia el proveedor.
    useEffect(() => {
        loadAllPayments();
    }, [loadAllPayments]);

    // Al cambiar filtros de búsqueda o tipo, volvemos a la página 1
    useEffect(() => {
        setServicesPaging((current) => ({ ...current, page: 1 }));
    }, [debouncedServiceSearch, serviceType, servicesPaging.pageSize]);

    if (loadingOverview) {
        return <AccountPageSkeleton />;
    }

    if (!overview && !databaseUnavailable) {
        return (
            <div className="p-6">
                <p className="text-muted-foreground">No se encontró el proveedor.</p>
            </div>
        );
    }

    if (databaseUnavailable) {
        return <DatabaseUnavailableState />;
    }

    const supplier = overview?.supplier;
    const services = servicesPage.items || [];

    // balancesByCurrency: array de { currency, confirmedPurchases, totalPaid, balance }.
    // Es el reemplazo del summary escalar (que sumaba ARS+USD incorrectamente).
    // Puede venir de overview.balancesByCurrency (campo nuevo del backend Tanda 1).
    const balancesByCurrency = overview?.balancesByCurrency || [];

    // serviceCount puede venir del summary anterior o calcularse desde balances
    const serviceCount = overview?.summary?.serviceCount ?? 0;

    // Permiso para ver montos de costo: controla la columna "Costo" en Servicios Comprados.
    // El mismo permiso que en FranjaSaldoPorMoneda y PagarProveedorInline.
    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    // Permiso para editar/eliminar pagos al proveedor desde el extracto.
    // El mismo permiso que se usa para registrar nuevos pagos.
    const puedeEditarEliminarPago = hasPermission("tesoreria.supplier_payments");

    // Aplicaciones de saldo a favor vigentes: vienen del credit overview.
    // Si el endpoint aún no respondió (o falló), usamos array vacío.
    const activeApplications = supplierCreditOverview?.activeApplications ?? [];

    // Saldo disponible para la moneda que está usando la ficha de "Usar saldo".
    // Viene de SupplierCreditOverviewDto.Currencies[].AvailableBalance.
    const getSaldoDisponible = (moneda) => {
        const creditCurrencyLine = (supplierCreditOverview?.currencies ?? []).find(
            (c) => c.currency === moneda
        );
        // Si el overview de crédito no cargó todavía, usamos el valor del overview general
        // como fallback (Math.abs del balance cuando es a favor).
        if (creditCurrencyLine != null) {
            return Number(creditCurrencyLine.availableBalance ?? 0);
        }
        const balanceLine = balancesByCurrency.find((b) => b.currency === moneda);
        const deuda = balanceLine?.balance ?? 0;
        return deuda < 0 ? Math.abs(deuda) : 0;
    };

    return (
        <div className="p-6 space-y-6 max-w-7xl mx-auto">

            {/* ── Encabezado ─────────────────────────────────────────────────── */}
            <div className="flex items-center gap-4">
                <button
                    onClick={() => navigate("/suppliers")}
                    className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-input bg-background/50 hover:bg-accent"
                    aria-label="Volver al listado de proveedores"
                >
                    <ArrowLeft className="h-5 w-5" />
                </button>
                <div>
                    <h1 className="text-2xl font-bold">Cuenta corriente: {supplier?.name}</h1>
                    <p className="text-muted-foreground">
                        {supplier?.contactName && `${supplier.contactName} · `}
                        {supplier?.taxId && `CUIT: ${supplier.taxId}`}
                    </p>
                </div>
            </div>

            {/* Datos de contacto */}
            <div className="flex flex-wrap gap-4 text-sm text-muted-foreground">
                {supplier?.phone && (
                    <span className="flex items-center gap-1">
                        <Phone className="h-4 w-4" /> {supplier.phone}
                    </span>
                )}
                {supplier?.email && (
                    <span className="flex items-center gap-1">
                        <Mail className="h-4 w-4" /> {supplier.email}
                    </span>
                )}
            </div>

            {/* ── Franja de saldo por moneda + botón "Registrar pago" ─────────── */}
            <FranjaSaldoPorMoneda
                balancesByCurrency={balancesByCurrency}
                serviceCount={serviceCount}
                onRegistrarPago={() => setShowPagoInline((prev) => !prev)}
                mostrandoPago={showPagoInline}
                onUsarSaldo={(moneda) => setMonedaUsandoSaldo((prev) => (prev === moneda ? null : moneda))}
                monedaUsandoSaldo={monedaUsandoSaldo}
            />

            {/* ── Ficha "Usar saldo a favor" en línea ───────────────────────── */}
            {monedaUsandoSaldo && (
                <UsarSaldoOperadorInline
                    supplierId={getPublicId(supplier)}
                    moneda={monedaUsandoSaldo}
                    saldoDisponible={getSaldoDisponible(monedaUsandoSaldo)}
                    onAplicado={handleSaldoAplicado}
                    onCancelar={() => setMonedaUsandoSaldo(null)}
                />
            )}

            {/* ── Ficha de pago en línea (nuevo o edición) ──────────────────── */}
            {showPagoInline && (
                <PagarProveedorInline
                    supplierId={getPublicId(supplier)}
                    balancesByCurrency={balancesByCurrency}
                    paymentToEdit={paymentToEdit}
                    onGuardado={handlePagoGuardado}
                    onCancelar={() => {
                        setShowPagoInline(false);
                        setPaymentToEdit(null);
                    }}
                />
            )}

            {/* ── Extracto de cuenta (libro mayor) + aplicaciones de saldo ───── */}
            <SupplierExtractoSection
                supplierPublicId={publicId}
                refreshKey={extractoRefreshKey}
                allPayments={allPayments}
                canEditarEliminar={puedeEditarEliminarPago}
                onEditarPago={handleEditarPago}
                onEliminarPago={handleEliminarPago}
                activeApplications={activeApplications}
                canRevertir={puedeEditarEliminarPago}
                onRevertirTerminado={handleRevertirAplicacionTerminada}
            />

            {/* ── Deuda desglosada por reserva ──────────────────────────────── */}
            <SupplierDebtByReservaSection publicId={publicId} />

            {/* ── Reembolsos a cobrar de este proveedor ─────────────────────── */}
            {/* ADR-041 Tanda 4: plata que este operador nos debe devolver por anulaciones.
                showSupplierColumn=false porque el operador ya está en el encabezado de la página.
                El componente se autogate con tesoreria.supplier_payments — si el usuario no tiene
                ese permiso, no se renderiza nada (ni el encabezado de la sección). */}
            <OperatorRefundsPendingSection
                supplierPublicId={publicId}
                showSupplierColumn={false}
            />

            {/* ── Datos bancarios del proveedor ─────────────────────────────── */}
            {/* ownerType="Supplier", ownerId=publicId del proveedor.
                Permiso de edición: tesoreria.supplier_payments (mismo equipo que gestiona pagos).
                Suposición: permiso no confirmado por Gastón — marcar si cambia. */}
            <ListaCuentasBancarias
                ownerType="Supplier"
                ownerId={publicId}
                title="Datos bancarios"
                canEdit={hasPermission("proveedores.edit")}
            />

            {/* ── Servicios comprados: lista operativa ──────────────────────── */}
            <div className="overflow-hidden rounded-xl border bg-card shadow-sm">
                <div className="border-b p-4 space-y-4">
                    <div className="flex items-center justify-between gap-3">
                        <h2 className="flex items-center gap-2 font-semibold">
                            <Building2 className="h-5 w-5" />
                            Servicios Comprados
                        </h2>
                        <span className="text-sm text-muted-foreground">{servicesPage.totalCount || 0} resultados</span>
                    </div>

                    <ListToolbar
                        className="border-slate-200/80 shadow-none dark:border-slate-800"
                        searchSlot={
                            <div className="relative flex-1">
                                <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                                <input
                                    type="text"
                                    placeholder="Buscar descripcion, expediente o archivo..."
                                    value={serviceSearch}
                                    onChange={(event) => setServiceSearch(event.target.value)}
                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
                                />
                            </div>
                        }
                        filterSlot={
                            <div className="relative lg:w-56">
                                <Filter className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                                <select
                                    value={serviceType}
                                    onChange={(event) => setServiceType(event.target.value)}
                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
                                >
                                    <option value="all">Todos los tipos</option>
                                    <option value="Aereo">Aereo</option>
                                    <option value="Hotel">Hotel</option>
                                    <option value="Traslado">Traslado</option>
                                    <option value="Paquete">Paquete</option>
                                    <option value="Otro">Otros</option>
                                </select>
                            </div>
                        }
                    />
                </div>

                <DataGrid density="compact" minWidth="1000px">
                    <DataGridHeader>
                        <DataGridHeaderRow>
                            <DataGridHeaderCell>Tipo</DataGridHeaderCell>
                            <DataGridHeaderCell>Descripcion</DataGridHeaderCell>
                            <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                            <DataGridHeaderCell>Estado</DataGridHeaderCell>
                            <DataGridHeaderCell>Codigo</DataGridHeaderCell>
                            <DataGridHeaderCell align="right">Costo</DataGridHeaderCell>
                            <DataGridHeaderCell align="right">Venta</DataGridHeaderCell>
                        </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                        {servicesLoading ? (
                            <DataGridEmptyState colSpan={8} title="Cargando servicios..." />
                        ) : services.length === 0 ? (
                            <DataGridEmptyState colSpan={8} title="No hay servicios para este filtro." />
                        ) : (
                            services.map((service) => (
                                <DataGridRow key={getPublicId(service)}>
                                    <DataGridCell>
                                        <span className="rounded bg-primary/10 px-2 py-1 text-xs font-medium text-primary">
                                            {service.type}
                                        </span>
                                    </DataGridCell>
                                    <DataGridCell>
                                        <div className="font-medium">{service.description || "-"}</div>
                                        {service.fileName ? <div className="text-xs text-muted-foreground">{service.fileName}</div> : null}
                                    </DataGridCell>
                                    <DataGridCell>
                                        {service.reservaPublicId ? (
                                            <Link to={`/reservas/${service.reservaPublicId}`} className="font-medium text-primary hover:underline">
                                                {service.numeroReserva || "Ver reserva"}
                                            </Link>
                                        ) : (
                                            service.numeroReserva || "-"
                                        )}
                                    </DataGridCell>
                                    <DataGridCell>{formatDate(service.date)}</DataGridCell>
                                    <DataGridCell>
                                        <ServiceStatusEditor
                                            service={service}
                                            onUpdated={() => { loadServices(); loadOverview(); }}
                                        />
                                    </DataGridCell>
                                    <DataGridCell>
                                        <ServiceConfirmationEditor
                                            service={service}
                                            onUpdated={() => { loadServices(); loadOverview(); }}
                                        />
                                    </DataGridCell>
                                    {/* Costo neto: enmascarado sin permiso cobranzas.see_cost.
                                        Sin currency el formatCurrency muestra "$0,00" para USD — bug bloqueante. */}
                                    <DataGridCell align="right" className="font-mono">
                                        {puedeVerMontos
                                            ? formatCurrency(service.netCost, service.currency)
                                            : <span className="text-muted-foreground">—</span>
                                        }
                                    </DataGridCell>
                                    <DataGridCell align="right" className="font-mono">
                                        {formatCurrency(service.salePrice, service.currency)}
                                    </DataGridCell>
                                </DataGridRow>
                            ))
                        )}
                    </DataGridBody>
                </DataGrid>

                {servicesLoading ? (
                    <div className="p-4 text-center text-sm text-muted-foreground md:hidden">Cargando servicios...</div>
                ) : services.length === 0 ? (
                    <ListEmptyState
                        title="No hay servicios para este filtro."
                        className="md:hidden rounded-none border-t border-dashed border-slate-200 dark:border-slate-800"
                    />
                ) : (
                    <MobileRecordList className="p-4 md:hidden">
                        {services.map((service) => (
                            <MobileRecordCard
                                key={getPublicId(service)}
                                title={service.description || "Sin descripcion"}
                                subtitle={service.type}
                                meta={
                                    <>
                                        <div className="text-xs text-slate-500 dark:text-slate-400">Fecha {formatDate(service.date)}</div>
                                        <div className="text-xs text-slate-500 dark:text-slate-400">
                                            {service.reservaPublicId ? (
                                                <Link to={`/reservas/${service.reservaPublicId}`} className="text-primary hover:underline">
                                                    {service.numeroReserva || "Ver reserva"}
                                                </Link>
                                            ) : (
                                                service.numeroReserva || "Sin expediente"
                                            )}
                                        </div>
                                        <div className="flex flex-wrap items-center gap-2 mt-1">
                                            <ServiceStatusEditor
                                                service={service}
                                                onUpdated={() => { loadServices(); loadOverview(); }}
                                            />
                                        </div>
                                        <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                                            Codigo:{" "}
                                            <ServiceConfirmationEditor
                                                service={service}
                                                onUpdated={() => { loadServices(); loadOverview(); }}
                                            />
                                        </div>
                                    </>
                                }
                                footer={
                                    // Costo: enmascarado sin permiso y currency requerido para multimoneda
                                    <span className="text-xs text-slate-500">
                                        Costo{" "}
                                        {puedeVerMontos
                                            ? formatCurrency(service.netCost, service.currency)
                                            : <span className="text-muted-foreground">—</span>
                                        }
                                    </span>
                                }
                                footerActions={
                                    <span className="text-xs font-semibold text-slate-700 dark:text-slate-200">
                                        Venta {formatCurrency(service.salePrice, service.currency)}
                                    </span>
                                }
                            />
                        ))}
                    </MobileRecordList>
                )}

                <div className="border-t p-4">
                    <PaginationFooter
                        page={servicesPage.page || servicesPaging.page}
                        pageSize={servicesPage.pageSize || servicesPaging.pageSize}
                        totalCount={servicesPage.totalCount || 0}
                        totalPages={servicesPage.totalPages || 0}
                        hasPreviousPage={Boolean(servicesPage.hasPreviousPage)}
                        hasNextPage={Boolean(servicesPage.hasNextPage)}
                        onPageChange={(page) => setServicesPaging((current) => ({ ...current, page }))}
                        onPageSizeChange={(pageSize) => setServicesPaging({ page: 1, pageSize })}
                    />
                </div>
            </div>
        </div>
    );
}
