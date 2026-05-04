import { useCallback, useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import {
    ArrowLeft,
    Building2,
    Phone,
    Mail,
    CreditCard,
    DollarSign,
    TrendingUp,
    FileText,
    Plus,
    Pencil,
    Trash2,
    Search,
    Filter,
    Receipt,
    Check,
    X,
} from "lucide-react";
import { api } from "../../../api";
import SupplierPaymentModal from "../../../components/SupplierPaymentModal";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import {
    DataGrid,
    DataGridActionCell,
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
import Swal from "sweetalert2";
import { showSuccess, showError } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";
import { useDebounce } from "../../../hooks/useDebounce";
import { isDatabaseUnavailableError } from "../../../lib/errors";

const emptyPage = {
    items: [],
    page: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0,
    hasPreviousPage: false,
    hasNextPage: false,
};

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

// Boton para abrir el PDF de la ultima factura AFIP de la reserva del servicio.
// Solo se renderiza si el backend nos devolvio un latestInvoicePublicId (i.e.
// ya hay una factura aprobada por AFIP para esa reserva).
function InvoicePdfButton({ invoicePublicId }) {
    const [loading, setLoading] = useState(false);

    if (!invoicePublicId) {
        return <span className="text-xs text-muted-foreground">-</span>;
    }

    const open = async () => {
        setLoading(true);
        try {
            const response = await api.get(`/invoices/${invoicePublicId}/pdf`, { responseType: "blob" });
            const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
            window.open(url, "_blank");
        } catch (error) {
            const message = error?.response?.data?.message || error?.message || "No se pudo abrir la factura.";
            showError(message, "Error al abrir factura");
        } finally {
            setLoading(false);
        }
    };

    return (
        <button
            type="button"
            onClick={open}
            disabled={loading}
            className="inline-flex items-center gap-1 rounded-md border border-blue-200 bg-blue-50 px-2 py-1 text-xs font-medium text-blue-700 hover:bg-blue-100 disabled:opacity-50 dark:border-blue-800 dark:bg-blue-950/30 dark:text-blue-300"
            title="Ver factura AFIP"
        >
            <Receipt className="h-3 w-3" />
            Factura
        </button>
    );
}

export default function SupplierAccountPage() {
    const { publicId } = useParams();
    const navigate = useNavigate();
    const [overview, setOverview] = useState(null);
    const [loadingOverview, setLoadingOverview] = useState(true);
    const [databaseUnavailable, setDatabaseUnavailable] = useState(false);

    const [servicesPage, setServicesPage] = useState(emptyPage);
    const [paymentsPage, setPaymentsPage] = useState(emptyPage);
    const [servicesPaging, setServicesPaging] = useState({ page: 1, pageSize: 25 });
    const [paymentsPaging, setPaymentsPaging] = useState({ page: 1, pageSize: 25 });
    const [servicesLoading, setServicesLoading] = useState(true);
    const [paymentsLoading, setPaymentsLoading] = useState(true);
    const [serviceSearch, setServiceSearch] = useState("");
    const [paymentSearch, setPaymentSearch] = useState("");
    const [serviceType, setServiceType] = useState("all");
    const debouncedServiceSearch = useDebounce(serviceSearch, 300);
    const debouncedPaymentSearch = useDebounce(paymentSearch, 300);

    const [showPaymentModal, setShowPaymentModal] = useState(false);
    const [editingPayment, setEditingPayment] = useState(null);

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

    const loadPayments = useCallback(async () => {
        setPaymentsLoading(true);
        try {
            const params = new URLSearchParams({
                page: String(paymentsPaging.page),
                pageSize: String(paymentsPaging.pageSize),
                sortBy: "paidAt",
                sortDir: "desc",
            });

            if (debouncedPaymentSearch.trim()) {
                params.set("search", debouncedPaymentSearch.trim());
            }

            const response = await api.get(`/suppliers/${publicId}/account/payments?${params.toString()}`);
            setPaymentsPage({ ...emptyPage, ...(response || {}) });
            setDatabaseUnavailable(false);
        } catch (error) {
            console.error("Error loading supplier payments:", error);
            setPaymentsPage(emptyPage);
            setDatabaseUnavailable(isDatabaseUnavailableError(error));
        } finally {
            setPaymentsLoading(false);
        }
    }, [debouncedPaymentSearch, paymentsPaging.page, paymentsPaging.pageSize, publicId]);

    const refreshAll = useCallback(async () => {
        await Promise.all([loadOverview(), loadServices(), loadPayments()]);
    }, [loadOverview, loadPayments, loadServices]);

    useEffect(() => {
        setServicesPaging({ page: 1, pageSize: 25 });
        setPaymentsPaging({ page: 1, pageSize: 25 });
        setServiceSearch("");
        setPaymentSearch("");
        setServiceType("all");
    }, [publicId]);

    useEffect(() => {
        loadOverview();
    }, [loadOverview]);

    useEffect(() => {
        loadServices();
    }, [loadServices]);

    useEffect(() => {
        loadPayments();
    }, [loadPayments]);

    useEffect(() => {
        setServicesPaging((current) => ({ ...current, page: 1 }));
    }, [debouncedServiceSearch, serviceType, servicesPaging.pageSize]);

    useEffect(() => {
        setPaymentsPaging((current) => ({ ...current, page: 1 }));
    }, [debouncedPaymentSearch, paymentsPaging.pageSize]);

    const handleOpenPaymentModal = (payment = null) => {
        setEditingPayment(payment);
        setShowPaymentModal(true);
    };

    const handlePaymentSuccess = async () => {
        setShowPaymentModal(false);
        await refreshAll();
    };

    const handleDeletePayment = async (payment) => {
        const result = await Swal.fire({
            title: "Eliminar pago?",
            text: `Se restaurara la deuda de ${formatCurrency(payment.amount)}. Esta accion no se puede deshacer.`,
            icon: "warning",
            showCancelButton: true,
            confirmButtonText: "Si, eliminar",
            cancelButtonText: "Cancelar",
        });

        if (!result.isConfirmed) {
            return;
        }

        try {
            await api.delete(`/suppliers/${publicId}/payments/${getPublicId(payment)}`);
            await refreshAll();
            Swal.fire("Eliminado", "El pago fue eliminado y el saldo restaurado.", "success");
        } catch (error) {
            Swal.fire("Error", "No se pudo eliminar el pago", "error");
        }
    };

    if (loadingOverview) {
        return <AccountPageSkeleton />;
    }

    if (!overview && !databaseUnavailable) {
        return (
            <div className="p-6">
                <p className="text-muted-foreground">No se encontro el proveedor.</p>
            </div>
        );
    }

    if (databaseUnavailable) {
        return <DatabaseUnavailableState />;
    }

    const supplier = overview?.supplier;
    const summary = overview?.summary || {};
    const services = servicesPage.items || [];
    const payments = paymentsPage.items || [];

    return (
        <div className="p-6 space-y-6 max-w-7xl mx-auto">
            <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
                <div className="flex items-center gap-4">
                    <button
                        onClick={() => navigate("/suppliers")}
                        className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-input bg-background/50 hover:bg-accent"
                    >
                        <ArrowLeft className="h-5 w-5" />
                    </button>
                    <div>
                        <h1 className="text-2xl font-bold">Cuenta Corriente: {supplier?.name}</h1>
                        <p className="text-muted-foreground">
                            {supplier?.contactName && `${supplier.contactName} · `}
                            {supplier?.taxId && `CUIT: ${supplier.taxId}`}
                        </p>
                    </div>
                </div>

                <button
                    onClick={() => handleOpenPaymentModal()}
                    className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-700 shadow-sm shadow-emerald-500/20"
                >
                    <Plus className="h-4 w-4" />
                    Registrar Pago
                </button>
            </div>

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

            <div className="grid grid-cols-1 md:grid-cols-5 gap-4">
                <div className="rounded-xl border bg-card p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-muted-foreground mb-1">
                        <FileText className="h-4 w-4" />
                        <span className="text-sm">Servicios</span>
                    </div>
                    <p className="text-2xl font-bold">{summary.serviceCount || 0}</p>
                </div>

                <div className="rounded-xl border bg-card p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-muted-foreground mb-1">
                        <CreditCard className="h-4 w-4" />
                        <span className="text-sm">Pagos</span>
                    </div>
                    <p className="text-2xl font-bold">{summary.paymentCount || 0}</p>
                </div>

                <div className="rounded-xl border bg-card p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-muted-foreground mb-1">
                        <DollarSign className="h-4 w-4" />
                        <span className="text-sm">Total Compras</span>
                    </div>
                    <p className="text-2xl font-bold">{formatCurrency(summary.totalPurchases)}</p>
                </div>

                <div className="rounded-xl border bg-card p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-muted-foreground mb-1">
                        <CreditCard className="h-4 w-4" />
                        <span className="text-sm">Total Pagado</span>
                    </div>
                    <p className="text-2xl font-bold text-green-600">{formatCurrency(summary.totalPaid)}</p>
                </div>

                <div className="rounded-xl border bg-gradient-to-br from-red-500/10 to-orange-500/10 border-red-500/30 p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-red-600 mb-1">
                        <TrendingUp className="h-4 w-4" />
                        <span className="text-sm font-medium">Saldo Pendiente</span>
                    </div>
                    <p className="text-2xl font-bold text-red-600">{formatCurrency(summary.balance)}</p>
                </div>
            </div>

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

                <DataGrid density="compact" minWidth="1080px">
                    <DataGridHeader>
                        <DataGridHeaderRow>
                            <DataGridHeaderCell>Tipo</DataGridHeaderCell>
                            <DataGridHeaderCell>Descripcion</DataGridHeaderCell>
                            <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                            <DataGridHeaderCell>Estado</DataGridHeaderCell>
                            <DataGridHeaderCell>Codigo</DataGridHeaderCell>
                            <DataGridHeaderCell>Factura</DataGridHeaderCell>
                            <DataGridHeaderCell align="right">Costo</DataGridHeaderCell>
                            <DataGridHeaderCell align="right">Venta</DataGridHeaderCell>
                        </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                        {servicesLoading ? (
                            <DataGridEmptyState colSpan={9} title="Cargando servicios..." />
                        ) : services.length === 0 ? (
                            <DataGridEmptyState colSpan={9} title="No hay servicios para este filtro." />
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
                                    <DataGridCell>
                                        <InvoicePdfButton invoicePublicId={service.latestInvoicePublicId} />
                                    </DataGridCell>
                                    <DataGridCell align="right" className="font-mono">{formatCurrency(service.netCost)}</DataGridCell>
                                    <DataGridCell align="right" className="font-mono">{formatCurrency(service.salePrice)}</DataGridCell>
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
                                            <InvoicePdfButton invoicePublicId={service.latestInvoicePublicId} />
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
                                footer={<span className="text-xs text-slate-500">Costo {formatCurrency(service.netCost)}</span>}
                                footerActions={<span className="text-xs font-semibold text-slate-700 dark:text-slate-200">Venta {formatCurrency(service.salePrice)}</span>}
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

            <div className="overflow-hidden rounded-xl border bg-card shadow-sm">
                <div className="border-b p-4 space-y-4">
                    <div className="flex items-center justify-between gap-3">
                        <h2 className="flex items-center gap-2 font-semibold">
                            <CreditCard className="h-5 w-5" />
                            Historial de Pagos
                        </h2>
                        <span className="text-sm text-muted-foreground">{paymentsPage.totalCount || 0} resultados</span>
                    </div>

                    <ListToolbar
                        className="border-slate-200/80 shadow-none dark:border-slate-800"
                        searchSlot={
                            <div className="relative">
                                <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                                <input
                                    type="text"
                                    placeholder="Buscar referencia, notas o expediente..."
                                    value={paymentSearch}
                                    onChange={(event) => setPaymentSearch(event.target.value)}
                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
                                />
                            </div>
                        }
                    />
                </div>

                <DataGrid density="compact" minWidth="920px">
                    <DataGridHeader>
                        <DataGridHeaderRow>
                            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                            <DataGridHeaderCell>Metodo</DataGridHeaderCell>
                            <DataGridHeaderCell>Referencia</DataGridHeaderCell>
                            <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                            <DataGridHeaderCell>Notas</DataGridHeaderCell>
                            <DataGridHeaderCell align="right">Monto</DataGridHeaderCell>
                            <DataGridHeaderCell align="center">Acciones</DataGridHeaderCell>
                        </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                        {paymentsLoading ? (
                            <DataGridEmptyState colSpan={7} title="Cargando pagos..." />
                        ) : payments.length === 0 ? (
                            <DataGridEmptyState colSpan={7} title="No hay pagos para este filtro." />
                        ) : (
                            payments.map((payment) => (
                                <DataGridRow key={getPublicId(payment)}>
                                    <DataGridCell>{formatDate(payment.paidAt)}</DataGridCell>
                                    <DataGridCell>{payment.method}</DataGridCell>
                                    <DataGridCell className="font-mono text-xs">{payment.reference || "-"}</DataGridCell>
                                    <DataGridCell>
                                        {payment.reservaPublicId ? (
                                            <Link to={`/reservas/${payment.reservaPublicId}`} className="text-primary hover:underline">
                                                {payment.numeroReserva || "Ver reserva"}
                                            </Link>
                                        ) : (
                                            payment.numeroReserva || "-"
                                        )}
                                    </DataGridCell>
                                    <DataGridCell className="max-w-xs truncate text-muted-foreground">{payment.notes || "-"}</DataGridCell>
                                    <DataGridCell align="right" className="font-mono font-medium text-green-600">
                                        {formatCurrency(payment.amount)}
                                    </DataGridCell>
                                    <DataGridActionCell align="center">
                                        <button
                                            onClick={() => handleOpenPaymentModal(payment)}
                                            className="rounded-lg p-2 text-blue-600 hover:bg-blue-50"
                                            title="Editar"
                                        >
                                            <Pencil className="h-4 w-4" />
                                        </button>
                                        <button
                                            onClick={() => handleDeletePayment(payment)}
                                            className="rounded-lg p-2 text-red-600 hover:bg-red-50"
                                            title="Eliminar"
                                        >
                                            <Trash2 className="h-4 w-4" />
                                        </button>
                                    </DataGridActionCell>
                                </DataGridRow>
                            ))
                        )}
                    </DataGridBody>
                </DataGrid>

                {paymentsLoading ? (
                    <div className="p-4 text-center text-sm text-muted-foreground md:hidden">Cargando pagos...</div>
                ) : payments.length === 0 ? (
                    <ListEmptyState
                        title="No hay pagos para este filtro."
                        className="md:hidden rounded-none border-t border-dashed border-slate-200 dark:border-slate-800"
                    />
                ) : (
                    <MobileRecordList className="p-4 md:hidden">
                        {payments.map((payment) => (
                            <MobileRecordCard
                                key={getPublicId(payment)}
                                title={payment.method}
                                subtitle={formatDate(payment.paidAt)}
                                meta={
                                    <>
                                        <div className="text-sm text-muted-foreground">
                                            {payment.reservaPublicId ? (
                                                <Link to={`/reservas/${payment.reservaPublicId}`} className="text-primary hover:underline">
                                                    {payment.numeroReserva || "Ver reserva"}
                                                </Link>
                                            ) : (
                                                payment.numeroReserva || "Sin expediente"
                                            )}
                                        </div>
                                        {payment.notes ? <div className="text-xs italic text-muted-foreground">{payment.notes}</div> : null}
                                    </>
                                }
                                footer={<span className="font-mono font-bold text-green-600">{formatCurrency(payment.amount)}</span>}
                                footerActions={
                                    <>
                                        <button
                                            onClick={() => handleOpenPaymentModal(payment)}
                                            className="flex items-center gap-1 rounded-lg bg-slate-100 px-3 py-1.5 text-xs text-slate-600"
                                        >
                                            <Pencil className="h-3 w-3" /> Editar
                                        </button>
                                        <button
                                            onClick={() => handleDeletePayment(payment)}
                                            className="flex items-center gap-1 rounded-lg bg-red-50 px-3 py-1.5 text-xs text-red-600"
                                        >
                                            <Trash2 className="h-3 w-3" /> Eliminar
                                        </button>
                                    </>
                                }
                            />
                        ))}
                    </MobileRecordList>
                )}

                <div className="border-t p-4">
                    <PaginationFooter
                        page={paymentsPage.page || paymentsPaging.page}
                        pageSize={paymentsPage.pageSize || paymentsPaging.pageSize}
                        totalCount={paymentsPage.totalCount || 0}
                        totalPages={paymentsPage.totalPages || 0}
                        hasPreviousPage={Boolean(paymentsPage.hasPreviousPage)}
                        hasNextPage={Boolean(paymentsPage.hasNextPage)}
                        onPageChange={(page) => setPaymentsPaging((current) => ({ ...current, page }))}
                        onPageSizeChange={(pageSize) => setPaymentsPaging({ page: 1, pageSize })}
                    />
                </div>
            </div>

            <SupplierPaymentModal
                isOpen={showPaymentModal}
                onClose={() => setShowPaymentModal(false)}
                onSuccess={handlePaymentSuccess}
                supplierId={getPublicId(supplier)}
                supplierName={supplier?.name}
                currentBalance={summary.balance}
                editingPayment={editingPayment}
            />
        </div>
    );
}
