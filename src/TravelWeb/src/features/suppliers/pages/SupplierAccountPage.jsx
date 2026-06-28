import { useCallback, useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { hasPermission } from "../../../auth";
import {
    ArrowLeft,
    Building2,
    Phone,
    Mail,
    Plus,
    Search,
    Filter,
    Check,
    X,
    Layers,
    ExternalLink,
    TrendingUp,
    CreditCard,
    RotateCcw,
    Landmark,
    Settings,
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
import { useOperatorRefundsPending } from "../hooks/useOperatorRefundsPending";

// Estado inicial vacío para la paginación de servicios.
const emptyPage = {
    items: [],
    page: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0,
    hasPreviousPage: false,
    hasNextPage: false,
};

// Etiquetas en español para el enum de condición fiscal del backend.
// El backend almacena strings como "IVA_RESP_INSCRIPTO"; acá los hacemos legibles.
const TAX_CONDITION_LABELS = {
    IVA_RESP_INSCRIPTO: "Resp. Inscripto",
    MONOTRIBUTISTA: "Monotributista",
    IVA_EXENTO: "Exento",
    CONSUMIDOR_FINAL: "Cons. Final",
};

// ─── Chips de saldo en vivo del encabezado ────────────────────────────────────

/**
 * Chips compactos que muestran el saldo con el proveedor por moneda.
 *
 * Siempre visibles en el encabezado, sobre las solapas.
 * Reglas clave:
 *   - NUNCA suma ARS + USD (multimoneda dura). Un chip por moneda.
 *   - Sin permiso cobranzas.see_cost → todo gris, monto "—", SIN verde.
 *     No revelar que hay saldo a favor ni saldo a pagar a quien no tiene permiso.
 *   - Con permiso → rojo si le debemos, verde si pagamos de más (a favor).
 *
 * Los botones de acción ("Registrar pago", "Usar saldo a favor") NO van acá;
 * van en la solapa "Cuenta corriente" para no saturar el encabezado.
 */
function BalanceHeaderChips({ balancesByCurrency }) {
    const puedeVerMontos = hasPermission("cobranzas.see_cost");
    const balances = Array.isArray(balancesByCurrency) ? balancesByCurrency : [];

    if (balances.length === 0) return null;

    return (
        <div className="flex flex-wrap gap-2 mt-3">
            {balances.map((balance) => {
                const deuda = balance.balance ?? 0;
                const esAFavor = deuda < 0;
                const esCero = deuda === 0;

                // Sin permiso: colores neutros para no revelar el estado del saldo.
                // Con permiso: rojo=le debo, verde=a favor, gris=sin deuda.
                const chipStyle = !puedeVerMontos
                    ? "border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/20"
                    : esAFavor
                        ? "border-emerald-200 bg-emerald-50 dark:border-emerald-900/40 dark:bg-emerald-950/20"
                        : esCero
                            ? "border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/20"
                            : "border-rose-200 bg-rose-50/60 dark:border-rose-900/40 dark:bg-rose-950/20";

                const textStyle = !puedeVerMontos
                    ? "text-slate-500"
                    : esAFavor
                        ? "text-emerald-700 dark:text-emerald-400"
                        : esCero
                            ? "text-slate-500"
                            : "text-rose-700 dark:text-rose-400";

                const simboloMoneda = balance.currency === "USD" ? "US$" : "$";

                // Etiqueta según el estado (sin permiso: solo indica la moneda, sin revelar deuda/favor)
                const etiqueta = !puedeVerMontos
                    ? `En ${simboloMoneda}`
                    : esAFavor
                        ? `A favor en ${simboloMoneda}`
                        : esCero
                            ? `Sin deuda en ${simboloMoneda}`
                            : `Le debo en ${simboloMoneda}`;

                return (
                    <div
                        key={balance.currency}
                        className={`inline-flex flex-col rounded-lg border px-3 py-2 ${chipStyle}`}
                        data-testid={`header-saldo-${balance.currency}`}
                    >
                        <span className={`text-xs font-bold uppercase tracking-wider ${textStyle}`}>
                            {etiqueta}
                        </span>
                        <span className={`font-mono font-bold text-xl ${textStyle}`}>
                            {puedeVerMontos
                                ? formatCurrency(Math.abs(deuda), balance.currency)
                                : "—"}
                        </span>
                    </div>
                );
            })}
        </div>
    );
}

// ─── Formulario de edición del proveedor (solapa "Datos") ─────────────────────

/**
 * Formulario en línea para editar los datos de identidad del proveedor.
 *
 * Reemplaza el modal "Editar proveedor": el contenido se muestra directamente
 * dentro de la solapa "Datos" sin ninguna ventana flotante encima.
 * Regla de Gastón: "el modal me parece horrible" (guia-ux-gaston.md).
 *
 * Campos: razón social, CUIT, condición fiscal, contacto, teléfono, email,
 * dirección, y estado activo/inactivo. Iguales al SupplierFormModal actual.
 * No se agregan campos nuevos (moneda por defecto, escape fiscal) sin aprobación.
 *
 * Props:
 *   - supplier: objeto del proveedor (viene del overview de la página).
 *   - onGuardado: callback al guardar exitosamente (recarga el overview para
 *     que el encabezado muestre el nombre/CUIT actualizado).
 */
function SupplierInlineEditForm({ supplier, onGuardado }) {
    const [formData, setFormData] = useState({
        name: "",
        contactName: "",
        taxId: "",
        taxCondition: "",
        address: "",
        email: "",
        phone: "",
        isActive: true,
        // defaultPaymentTermDays: campo del modelo (ADR-041) que NO se muestra en la UI
        // pero se incluye en el PUT para no pisarlo con null en un FULL overwrite.
        defaultPaymentTermDays: null,
    });
    const [saving, setSaving] = useState(false);

    // Inicializa el formulario con los datos del proveedor cuando llegan del servidor.
    // Cada vez que el proveedor se recarga (handlePagoGuardado llama loadOverview, etc.)
    // el formulario vuelve a los valores guardados. Esto está comentado para que quede claro.
    useEffect(() => {
        if (!supplier) return;
        setFormData({
            name: supplier.name || "",
            contactName: supplier.contactName || "",
            taxId: supplier.taxId || "",
            taxCondition: supplier.taxCondition || "",
            address: supplier.address || "",
            email: supplier.email || "",
            phone: supplier.phone || "",
            isActive: supplier.isActive ?? true,
            // Round-trip: preservamos el plazo de pago acordado (ADR-041) aunque no
            // lo mostremos en este form. Sin esto, el PUT lo pierde (full overwrite).
            defaultPaymentTermDays: supplier.defaultPaymentTermDays ?? null,
        });
    }, [supplier]);

    const handleChange = (campo) => (event) => {
        setFormData((anterior) => ({ ...anterior, [campo]: event.target.value }));
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        setSaving(true);
        try {
            await api.put(`/suppliers/${getPublicId(supplier)}`, formData);
            showSuccess("Datos del operador guardados correctamente.");
            if (onGuardado) onGuardado();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudieron guardar los datos del operador."), "Error al guardar");
        } finally {
            setSaving(false);
        }
    };

    const inputClass =
        "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white";
    const labelClass = "text-sm font-medium text-slate-700 dark:text-slate-300";

    return (
        <form onSubmit={handleSubmit} className="space-y-5">
            <div className="grid gap-4 sm:grid-cols-2">

                {/* Razón social: único campo obligatorio para guardar */}
                <div className="space-y-2 sm:col-span-2">
                    <label className={labelClass}>Razón social *</label>
                    <input
                        type="text"
                        required
                        value={formData.name}
                        onChange={handleChange("name")}
                        placeholder="Ej: Despegar Argentina S.A."
                        className={inputClass}
                        data-testid="supplier-datos-name"
                    />
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>CUIT</label>
                    <input
                        type="text"
                        value={formData.taxId}
                        onChange={handleChange("taxId")}
                        placeholder="20-12345678-9"
                        className={inputClass}
                        data-testid="supplier-datos-taxId"
                    />
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Condición fiscal</label>
                    <select
                        value={formData.taxCondition}
                        onChange={handleChange("taxCondition")}
                        className={inputClass}
                        data-testid="supplier-datos-taxCondition"
                    >
                        <option value="">Seleccionar...</option>
                        <option value="IVA_RESP_INSCRIPTO">Resp. Inscripto</option>
                        <option value="MONOTRIBUTISTA">Monotributista</option>
                        <option value="IVA_EXENTO">Exento</option>
                        <option value="CONSUMIDOR_FINAL">Cons. Final</option>
                    </select>
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Contacto</label>
                    <input
                        type="text"
                        value={formData.contactName}
                        onChange={handleChange("contactName")}
                        placeholder="Nombre de la persona de contacto"
                        className={inputClass}
                        data-testid="supplier-datos-contactName"
                    />
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Teléfono</label>
                    <input
                        type="text"
                        value={formData.phone}
                        onChange={handleChange("phone")}
                        placeholder="+54 11 ..."
                        className={inputClass}
                        data-testid="supplier-datos-phone"
                    />
                </div>

                <div className="space-y-2 sm:col-span-2">
                    <label className={labelClass}>Email</label>
                    <input
                        type="email"
                        value={formData.email}
                        onChange={handleChange("email")}
                        placeholder="contacto@operador.com"
                        className={inputClass}
                        data-testid="supplier-datos-email"
                    />
                </div>

                <div className="space-y-2 sm:col-span-2">
                    <label className={labelClass}>Dirección</label>
                    <input
                        type="text"
                        value={formData.address}
                        onChange={handleChange("address")}
                        placeholder="Calle y número, ciudad"
                        className={inputClass}
                        data-testid="supplier-datos-address"
                    />
                </div>

                {/* Toggle activo/inactivo: inactivo = no aparece en buscadores, pero mantiene historial */}
                <div className="sm:col-span-2 flex items-center gap-3 rounded-lg border border-slate-100 dark:border-slate-800 bg-slate-50 dark:bg-slate-900/30 p-3">
                    <input
                        type="checkbox"
                        id="supplier-isActive"
                        checked={formData.isActive}
                        onChange={(event) =>
                            setFormData((anterior) => ({ ...anterior, isActive: event.target.checked }))
                        }
                        className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                        data-testid="supplier-datos-isActive"
                    />
                    <label htmlFor="supplier-isActive" className={labelClass + " cursor-pointer"}>
                        Operador activo
                    </label>
                    <span className="text-xs text-muted-foreground">
                        {formData.isActive
                            ? "Activo — aparece en buscadores y se le pueden asignar servicios."
                            : "Inactivo — no aparece en buscadores, pero mantiene su historial."}
                    </span>
                </div>
            </div>

            <div className="flex items-center gap-3 pt-4 border-t border-slate-100 dark:border-slate-800">
                <button
                    type="submit"
                    disabled={saving}
                    className="rounded-lg bg-indigo-600 px-5 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 shadow-lg shadow-indigo-500/25 transition-all disabled:opacity-50"
                    data-testid="supplier-datos-submit"
                >
                    {saving ? "Guardando..." : "Guardar cambios"}
                </button>
            </div>
        </form>
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
 * Se usa en la solapa "Cuenta corriente" de SupplierAccountPage.
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
            // Revertir el valor optimista en la UI antes de mostrar el error.
            // Usamos getApiErrorMessage para evitar que strings de red en inglés
            // ("Failed to fetch", "Internal Server Error") lleguen al usuario.
            setValue(previous);
            showError(getApiErrorMessage(error, "No se pudo actualizar el estado."), "No se pudo cambiar el estado");
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
// ConfirmationNumber para el resto). Click para editar, Enter/blur para guardar,
// Esc para cancelar.
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
            // getApiErrorMessage normaliza el error y evita strings en inglés del runtime.
            showError(getApiErrorMessage(error, "No se pudo guardar el código."), "No se pudo guardar el código");
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
 * Página de ficha del proveedor (operador), rediseñada como encabezado + solapas.
 *
 * Estructura:
 *   ENCABEZADO (siempre visible): nombre, "Operador", CUIT, condición fiscal,
 *   datos de contacto, y chips de saldo EN VIVO por moneda.
 *
 *   SOLAPAS (mismo patrón visual que la ficha de reserva):
 *     1. Cuenta corriente  — acciones pago/saldo + extracto + deuda por expediente
 *     2. Servicios comprados — grilla operativa con estado y código de confirmación
 *     3. Reembolsos         — plata que el operador debe devolver por anulaciones
 *     4. Datos bancarios    — cuentas (CBU/alias) del operador
 *     5. Datos              — edición de identidad (razón social, CUIT, etc.) SIN modal
 */
export default function SupplierAccountPage() {
    const { publicId } = useParams();
    const navigate = useNavigate();

    // ─── Solapa activa ────────────────────────────────────────────────────────
    const [activeTab, setActiveTab] = useState("cuenta-corriente");

    // ─── Overview del proveedor ───────────────────────────────────────────────
    const [overview, setOverview] = useState(null);
    const [loadingOverview, setLoadingOverview] = useState(true);
    const [databaseUnavailable, setDatabaseUnavailable] = useState(false);

    // ─── Grilla de servicios comprados ────────────────────────────────────────
    const [servicesPage, setServicesPage] = useState(emptyPage);
    const [servicesPaging, setServicesPaging] = useState({ page: 1, pageSize: 25 });
    const [servicesLoading, setServicesLoading] = useState(true);
    const [serviceSearch, setServiceSearch] = useState("");
    const [serviceType, setServiceType] = useState("all");
    const debouncedServiceSearch = useDebounce(serviceSearch, 300);

    // ─── Control de fichas en línea (Cuenta corriente) ───────────────────────
    // extractoRefreshKey: incrementar fuerza al extracto a recargar sin que el usuario
    // refresque la página manualmente.
    const [extractoRefreshKey, setExtractoRefreshKey] = useState(0);
    const [showPagoInline, setShowPagoInline] = useState(false);
    const [paymentToEdit, setPaymentToEdit] = useState(null);
    // monedaUsandoSaldo: qué moneda tiene la ficha "Usar saldo a favor" abierta.
    // null = ninguna abierta. Solo una moneda puede estar abierta a la vez.
    const [monedaUsandoSaldo, setMonedaUsandoSaldo] = useState(null);

    // ─── Datos de soporte para el extracto ───────────────────────────────────
    // allPayments: lista de hasta 200 pagos del proveedor. Se usa para cruzar
    // con sourcePublicId de cada línea del extracto y ofrecer el botón "Editar".
    const [allPayments, setAllPayments] = useState([]);
    // supplierCreditOverview: saldo a favor y aplicaciones activas.
    const [supplierCreditOverview, setSupplierCreditOverview] = useState(null);

    // ─── Badge de reembolsos pendientes (numerito en la solapa) ──────────────
    // Cargamos el conteo de reembolsos al montar para poder mostrar el badge.
    // OperatorRefundsPendingSection también carga sus propios datos internamente
    // cuando el usuario entra a la solapa — este call paralelo es intencional.
    const { items: pendingRefundsItems } = useOperatorRefundsPending(publicId);
    // Solo mostramos el badge si el usuario tiene permiso (de lo contrario el
    // endpoint habría devuelto 403 o vacío, y no tiene sentido mostrarlo).
    const cantidadReembolsosPendientes = hasPermission("tesoreria.supplier_payments")
        ? pendingRefundsItems.length
        : 0;

    // ─── Funciones de carga ───────────────────────────────────────────────────

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

    // Carga hasta 200 pagos del proveedor para hacer cross-reference en el extracto.
    // Si hay más de 200 pagos los más viejos no tendrán botón Editar (sí Eliminar).
    const loadAllPayments = useCallback(async () => {
        if (!publicId) return;
        try {
            const response = await api.get(
                `/suppliers/${publicId}/account/payments?page=1&pageSize=200&sortBy=paidAt&sortDir=desc`
            );
            setAllPayments(response?.items || []);
        } catch (error) {
            // No bloqueante: si falla, el extracto sigue funcionando sin botones de edición.
            console.warn("[SupplierAccountPage] No se pudo cargar la lista de pagos para el extracto:", error?.message);
            setAllPayments([]);
        }
    }, [publicId]);

    // Carga el saldo a favor y aplicaciones activas del proveedor.
    const loadSupplierCredit = useCallback(async () => {
        try {
            const creditData = await api.get(`/suppliers/${publicId}/credit`);
            setSupplierCreditOverview(creditData);
        } catch (error) {
            // No bloqueante: los carteles de saldo a favor siguen visibles
            // pero sin lista de aplicaciones activas ni botón para usar el saldo.
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

    // ─── Handlers ─────────────────────────────────────────────────────────────

    // Refresca overview, pagos y extracto después de guardar un pago.
    const handlePagoGuardado = useCallback(async () => {
        setShowPagoInline(false);
        setPaymentToEdit(null);
        setExtractoRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadAllPayments(), loadSupplierCredit()]);
    }, [loadOverview, loadAllPayments, loadSupplierCredit]);

    // Se llama al completar una aplicación de saldo a favor.
    const handleSaldoAplicado = useCallback(async () => {
        setMonedaUsandoSaldo(null);
        setExtractoRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadSupplierCredit()]);
    }, [loadOverview, loadSupplierCredit]);

    // Se llama al revertir una aplicación de saldo a favor.
    const handleRevertirAplicacionTerminada = useCallback(async () => {
        setExtractoRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadSupplierCredit()]);
    }, [loadOverview, loadSupplierCredit]);

    // Abre la ficha de pago en modo edición con el pago seleccionado.
    const handleEditarPago = useCallback((payment) => {
        setPaymentToEdit(payment);
        setShowPagoInline(true);
        // Scroll al principio para que la ficha de pago quede visible
        window.scrollTo({ top: 0, behavior: "smooth" });
    }, []);

    // Confirma y elimina un pago del proveedor.
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

    // ─── Efectos ──────────────────────────────────────────────────────────────

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
        // Volvemos siempre a la primera solapa al cambiar de proveedor
        setActiveTab("cuenta-corriente");
    }, [publicId]);

    // useEffect con dependencias [loadOverview], etc.: cada función loadXxx ya
    // incluye publicId en su useCallback, así que cambiar el proveedor redefine
    // la función y dispara el efecto automáticamente.
    useEffect(() => { loadOverview(); }, [loadOverview]);
    useEffect(() => { loadSupplierCredit(); }, [loadSupplierCredit]);
    useEffect(() => { loadServices(); }, [loadServices]);
    useEffect(() => { loadAllPayments(); }, [loadAllPayments]);

    // Al cambiar filtros de búsqueda o tipo, volvemos a la página 1 de servicios.
    useEffect(() => {
        setServicesPaging((current) => ({ ...current, page: 1 }));
    }, [debouncedServiceSearch, serviceType, servicesPaging.pageSize]);

    // ─── Guardas de estado ────────────────────────────────────────────────────

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

    // ─── Datos derivados ──────────────────────────────────────────────────────

    const supplier = overview?.supplier;
    const services = servicesPage.items || [];

    // balancesByCurrency: array de { currency, confirmedPurchases, totalPaid, balance }.
    // Reemplaza el resumen escalar anterior que sumaba ARS+USD incorrectamente.
    const balancesByCurrency = overview?.balancesByCurrency || [];

    const puedeVerMontos = hasPermission("cobranzas.see_cost");
    const puedeEditarEliminarPago = hasPermission("tesoreria.supplier_payments");

    // Aplicaciones de saldo a favor vigentes (para el extracto).
    const activeApplications = supplierCreditOverview?.activeApplications ?? [];

    // Saldo disponible para la moneda cuya ficha de "Usar saldo" está abierta.
    const getSaldoDisponible = (moneda) => {
        const creditCurrencyLine = (supplierCreditOverview?.currencies ?? []).find(
            (c) => c.currency === moneda
        );
        if (creditCurrencyLine != null) {
            return Number(creditCurrencyLine.availableBalance ?? 0);
        }
        // Fallback: usamos el balance negativo del overview general (deuda < 0 = a favor)
        const balanceLine = balancesByCurrency.find((b) => b.currency === moneda);
        const deuda = balanceLine?.balance ?? 0;
        return deuda < 0 ? Math.abs(deuda) : 0;
    };

    // Monedas con saldo a favor: son las que muestran el botón "Usar saldo" en la solapa.
    const monedasAFavor = balancesByCurrency.filter((b) => (b.balance ?? 0) < 0);

    // Etiqueta de la solapa "Reembolsos" con el badge numérico si hay pendientes.
    const labelReembolsos = cantidadReembolsosPendientes > 0
        ? `Reembolsos (${cantidadReembolsosPendientes})`
        : "Reembolsos";

    // Condición fiscal en español para el subtítulo del encabezado.
    // Si el valor del backend no está en nuestro mapeo, lo omitimos:
    // la alternativa anterior (?? supplier?.taxCondition) exponía el enum interno
    // al usuario (ej: "IVA_RESP_INSCRIPTO").
    const taxConditionLabel = TAX_CONDITION_LABELS[supplier?.taxCondition] ?? null;

    // ─── Definición de solapas (patrón igual que ReservaDetailPage) ──────────
    const solapas = [
        { id: "cuenta-corriente",    label: "Cuenta corriente",    icon: CreditCard  },
        { id: "servicios-comprados", label: "Servicios comprados", icon: Building2   },
        { id: "reembolsos",          label: labelReembolsos,        icon: RotateCcw   },
        { id: "datos-bancarios",     label: "Datos bancarios",      icon: Landmark    },
        { id: "datos",               label: "Datos",                icon: Settings    },
    ];

    return (
        <div className="p-6 space-y-6 max-w-7xl mx-auto">

            {/* ── Encabezado: identidad + chips de saldo ────────────────────────
                Siempre visible, arriba de las solapas.
                Chips enmascarados sin permiso cobranzas.see_cost (nunca mostrar verde sin permiso).
            ─────────────────────────────────────────────────────────────────── */}
            <div className="flex items-start gap-4">
                <button
                    onClick={() => navigate("/suppliers")}
                    className="mt-1 inline-flex h-10 w-10 items-center justify-center rounded-lg border border-input bg-background/50 hover:bg-accent flex-shrink-0"
                    aria-label="Volver al listado de operadores"
                >
                    <ArrowLeft className="h-5 w-5" />
                </button>

                <div className="min-w-0 flex-1">
                    {/* Nombre del proveedor */}
                    <h1 className="text-2xl font-bold truncate">{supplier?.name}</h1>

                    {/* Subtítulo: tipo + CUIT + condición fiscal */}
                    <p className="text-muted-foreground text-sm mt-0.5">
                        Operador
                        {supplier?.taxId && ` · CUIT ${supplier.taxId}`}
                        {taxConditionLabel && ` · ${taxConditionLabel}`}
                    </p>

                    {/* Datos de contacto opcionales */}
                    {(supplier?.phone || supplier?.email) && (
                        <div className="flex flex-wrap gap-3 mt-1.5 text-sm text-muted-foreground">
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
                    )}

                    {/* Chips de saldo en vivo por moneda */}
                    <BalanceHeaderChips balancesByCurrency={balancesByCurrency} />
                </div>
            </div>

            {/* ── Solapas (mismo patrón visual que la ficha de la reserva) ─────── */}
            <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">

                {/* Barra de navegación entre solapas */}
                <div className="border-b border-slate-100 bg-slate-50/30 px-4 dark:border-slate-800 dark:bg-slate-800/20 sm:px-6">
                    <nav className="scrollbar-hide flex gap-8 overflow-x-auto" role="tablist">
                        {solapas.map((solapa) => (
                            <button
                                key={solapa.id}
                                role="tab"
                                aria-selected={activeTab === solapa.id}
                                aria-controls={`panel-${solapa.id}`}
                                onClick={() => setActiveTab(solapa.id)}
                                data-testid={`supplier-tab-${solapa.id}`}
                                className={`relative flex items-center gap-2 whitespace-nowrap py-4 text-sm font-semibold transition-all ${
                                    activeTab === solapa.id
                                        ? "text-indigo-600 dark:text-indigo-400"
                                        : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
                                }`}
                            >
                                <solapa.icon className={`h-4 w-4 ${activeTab === solapa.id ? "animate-bounce" : ""}`} />
                                {solapa.label}
                                {/* Línea azul inferior del tab activo */}
                                {activeTab === solapa.id && (
                                    <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-t-full bg-indigo-600 dark:bg-indigo-400" />
                                )}
                            </button>
                        ))}
                    </nav>
                </div>

                {/* Contenido de la solapa activa */}
                <div className="p-4 sm:p-6 lg:p-8">

                    {/* ── SOLAPA 1: Cuenta corriente ────────────────────────────────────
                        Acciones + extracto + deuda por expediente.
                        Los botones abren fichas en línea debajo (sin ventanas flotantes).
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "cuenta-corriente" && (
                        <div className="space-y-6" id="panel-cuenta-corriente" role="tabpanel">

                            {/* Botones de acción: solo visibles con permiso tesoreria.supplier_payments */}
                            {hasPermission("tesoreria.supplier_payments") && (
                                <div className="flex flex-wrap items-center gap-3">

                                    {/* "Registrar pago": alterna la ficha de pago en línea */}
                                    <button
                                        type="button"
                                        onClick={() => setShowPagoInline((prev) => !prev)}
                                        data-testid="btn-registrar-pago"
                                        className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors ${
                                            showPagoInline
                                                ? "bg-slate-500 hover:bg-slate-600"
                                                : "bg-emerald-600 hover:bg-emerald-700 shadow-emerald-500/20"
                                        }`}
                                    >
                                        <Plus className="h-4 w-4" />
                                        {showPagoInline ? "Cerrar" : "Registrar pago"}
                                    </button>

                                    {/* "Usar saldo a favor": un botón por cada moneda con saldo verde.
                                        Si no hay saldo a favor en ninguna moneda, no aparece ningún botón.
                                        Si hay dos monedas a favor, ambos botones muestran su símbolo. */}
                                    {monedasAFavor.map((balance) => {
                                        const simbolo = balance.currency === "USD" ? "US$" : "$";
                                        const estaAbierto = monedaUsandoSaldo === balance.currency;
                                        return (
                                            <button
                                                key={balance.currency}
                                                type="button"
                                                onClick={() =>
                                                    setMonedaUsandoSaldo((prev) =>
                                                        prev === balance.currency ? null : balance.currency
                                                    )
                                                }
                                                data-testid={`btn-usar-saldo-${balance.currency}`}
                                                className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium shadow-sm transition-colors ${
                                                    estaAbierto
                                                        ? "bg-emerald-200 text-emerald-800 hover:bg-emerald-300 dark:bg-emerald-900/60 dark:text-emerald-200"
                                                        : "bg-emerald-600 text-white hover:bg-emerald-700 shadow-emerald-500/20"
                                                }`}
                                            >
                                                <TrendingUp className="h-4 w-4" />
                                                {estaAbierto
                                                    ? `Cerrar saldo ${simbolo}`
                                                    : monedasAFavor.length > 1
                                                        ? `Usar saldo en ${simbolo}`
                                                        : "Usar saldo a favor"}
                                            </button>
                                        );
                                    })}
                                </div>
                            )}

                            {/* Ficha "Usar saldo a favor" en línea (debajo de los botones) */}
                            {monedaUsandoSaldo && (
                                <UsarSaldoOperadorInline
                                    supplierId={getPublicId(supplier)}
                                    moneda={monedaUsandoSaldo}
                                    saldoDisponible={getSaldoDisponible(monedaUsandoSaldo)}
                                    onAplicado={handleSaldoAplicado}
                                    onCancelar={() => setMonedaUsandoSaldo(null)}
                                />
                            )}

                            {/* Ficha de pago en línea (nuevo pago o edición de uno existente) */}
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

                            {/* Extracto de cuenta: libro mayor cronológico por moneda */}
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

                            {/* Deuda desglosada por reserva/expediente */}
                            <SupplierDebtByReservaSection publicId={publicId} />
                        </div>
                    )}

                    {/* ── SOLAPA 2: Servicios comprados ────────────────────────────────
                        Grilla operativa con búsqueda, filtro, paginación,
                        y editores inline de estado y código de confirmación.
                        Contenido idéntico al que estaba antes en la página apilada.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "servicios-comprados" && (
                        <div id="panel-servicios-comprados" role="tabpanel">
                            <div className="overflow-hidden rounded-xl border bg-card shadow-sm">
                                <div className="border-b p-4 space-y-4">
                                    <div className="flex items-center justify-between gap-3">
                                        <h2 className="flex items-center gap-2 font-semibold">
                                            <Building2 className="h-5 w-5" />
                                            Servicios comprados
                                        </h2>
                                        <span className="text-sm text-muted-foreground">
                                            {servicesPage.totalCount || 0} resultados
                                        </span>
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

                                {/* Grilla desktop */}
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
                                                        {service.fileName
                                                            ? <div className="text-xs text-muted-foreground">{service.fileName}</div>
                                                            : null}
                                                    </DataGridCell>
                                                    <DataGridCell>
                                                        {service.reservaPublicId ? (
                                                            <Link
                                                                to={`/reservas/${service.reservaPublicId}`}
                                                                className="font-medium text-primary hover:underline"
                                                            >
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
                                                    {/* Costo: enmascarado sin permiso cobranzas.see_cost */}
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

                                {/* Vista mobile */}
                                {servicesLoading ? (
                                    <div className="p-4 text-center text-sm text-muted-foreground md:hidden">
                                        Cargando servicios...
                                    </div>
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
                                                        <div className="text-xs text-slate-500 dark:text-slate-400">
                                                            Fecha {formatDate(service.date)}
                                                        </div>
                                                        <div className="text-xs text-slate-500 dark:text-slate-400">
                                                            {service.reservaPublicId ? (
                                                                <Link
                                                                    to={`/reservas/${service.reservaPublicId}`}
                                                                    className="text-primary hover:underline"
                                                                >
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

                                {/* Paginación */}
                                <div className="border-t p-4">
                                    <PaginationFooter
                                        page={servicesPage.page || servicesPaging.page}
                                        pageSize={servicesPage.pageSize || servicesPaging.pageSize}
                                        totalCount={servicesPage.totalCount || 0}
                                        totalPages={servicesPage.totalPages || 0}
                                        hasPreviousPage={Boolean(servicesPage.hasPreviousPage)}
                                        hasNextPage={Boolean(servicesPage.hasNextPage)}
                                        onPageChange={(page) =>
                                            setServicesPaging((current) => ({ ...current, page }))
                                        }
                                        onPageSizeChange={(pageSize) =>
                                            setServicesPaging({ page: 1, pageSize })
                                        }
                                    />
                                </div>
                            </div>
                        </div>
                    )}

                    {/* ── SOLAPA 3: Reembolsos ──────────────────────────────────────────
                        Plata que el operador nos debe devolver por anulaciones.
                        OperatorRefundsPendingSection se autogate con tesoreria.supplier_payments;
                        si el usuario no tiene ese permiso, no se renderiza nada.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "reembolsos" && (
                        <div id="panel-reembolsos" role="tabpanel">
                            <OperatorRefundsPendingSection
                                supplierPublicId={publicId}
                                showSupplierColumn={false}
                            />
                        </div>
                    )}

                    {/* ── SOLAPA 4: Datos bancarios ─────────────────────────────────────
                        Lista de cuentas bancarias del operador (CBU/alias).
                        Edición gateada por proveedores.edit.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "datos-bancarios" && (
                        <div id="panel-datos-bancarios" role="tabpanel">
                            <ListaCuentasBancarias
                                ownerType="Supplier"
                                ownerId={publicId}
                                title="Datos bancarios del operador"
                                canEdit={hasPermission("proveedores.edit")}
                            />
                        </div>
                    )}

                    {/* ── SOLAPA 5: Datos ───────────────────────────────────────────────
                        Edición de identidad del proveedor en línea (sin ventana flotante).
                        Reemplaza el modal "Editar proveedor" anterior.
                        Solo lectura para quien no tiene proveedores.edit.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "datos" && (
                        <div id="panel-datos" role="tabpanel" className="max-w-2xl">
                            <h2 className="text-lg font-semibold mb-6">Datos del operador</h2>

                            {hasPermission("proveedores.edit") ? (
                                <SupplierInlineEditForm
                                    supplier={supplier}
                                    onGuardado={loadOverview}
                                />
                            ) : (
                                // Vista de solo lectura para quien no tiene permiso de editar
                                <div className="space-y-4 text-sm">
                                    <div className="grid sm:grid-cols-2 gap-4">
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Razón social</p>
                                            <p className="font-medium">{supplier?.name || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">CUIT</p>
                                            <p className="font-medium">{supplier?.taxId || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Condición fiscal</p>
                                            <p className="font-medium">{taxConditionLabel || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Contacto</p>
                                            <p className="font-medium">{supplier?.contactName || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Teléfono</p>
                                            <p className="font-medium">{supplier?.phone || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Email</p>
                                            <p className="font-medium">{supplier?.email || "—"}</p>
                                        </div>
                                        {supplier?.address && (
                                            <div className="sm:col-span-2">
                                                <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Dirección</p>
                                                <p className="font-medium">{supplier.address}</p>
                                            </div>
                                        )}
                                    </div>
                                    <p className="text-xs text-muted-foreground mt-2">
                                        No tenés permiso para editar los datos del operador.
                                    </p>
                                </div>
                            )}
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
}
