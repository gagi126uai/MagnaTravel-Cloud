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
} from "lucide-react";
import { api } from "../../../api";
import SupplierPaymentModal from "../../../components/SupplierPaymentModal";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { formatCurrency, formatDate } from "../../../lib/utils";
import Swal from "sweetalert2";
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

            <div className="rounded-xl border bg-card shadow-sm overflow-hidden">
                <div className="border-b p-4 space-y-4">
                    <div className="flex items-center justify-between gap-3">
                        <h2 className="font-semibold flex items-center gap-2">
                            <Building2 className="h-5 w-5" />
                            Servicios Comprados
                        </h2>
                        <span className="text-sm text-muted-foreground">{servicesPage.totalCount || 0} resultados</span>
                    </div>

                    <div className="flex flex-col gap-3 lg:flex-row">
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
                    </div>
                </div>

                <div className="hidden md:block overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="border-b bg-muted/50">
                                <th className="p-3 text-left font-medium">Tipo</th>
                                <th className="p-3 text-left font-medium">Descripcion</th>
                                <th className="p-3 text-left font-medium">Reserva</th>
                                <th className="p-3 text-left font-medium">Fecha</th>
                                <th className="p-3 text-left font-medium">Estado</th>
                                <th className="p-3 text-right font-medium">Costo</th>
                                <th className="p-3 text-right font-medium">Venta</th>
                            </tr>
                        </thead>
                        <tbody>
                            {servicesLoading ? (
                                <tr>
                                    <td colSpan={7} className="p-4 text-center text-muted-foreground">Cargando servicios...</td>
                                </tr>
                            ) : services.length === 0 ? (
                                <tr>
                                    <td colSpan={7} className="p-4 text-center text-muted-foreground">No hay servicios para este filtro.</td>
                                </tr>
                            ) : (
                                services.map((service) => (
                                    <tr key={getPublicId(service)} className="border-b hover:bg-muted/30">
                                        <td className="p-3">
                                            <span className="px-2 py-1 bg-primary/10 text-primary rounded text-xs font-medium">
                                                {service.type}
                                            </span>
                                        </td>
                                        <td className="p-3">
                                            <div className="font-medium">{service.description || "-"}</div>
                                            {service.fileName && <div className="text-xs text-muted-foreground">{service.fileName}</div>}
                                        </td>
                                        <td className="p-3">
                                            {service.reservaPublicId ? (
                                                <Link to={`/reservas/${service.reservaPublicId}`} className="text-primary font-medium hover:underline">
                                                    {service.numeroReserva || "Ver reserva"}
                                                </Link>
                                            ) : (
                                                service.numeroReserva || "-"
                                            )}
                                        </td>
                                        <td className="p-3">{formatDate(service.date)}</td>
                                        <td className="p-3">{service.status}</td>
                                        <td className="p-3 text-right font-mono">{formatCurrency(service.netCost)}</td>
                                        <td className="p-3 text-right font-mono">{formatCurrency(service.salePrice)}</td>
                                    </tr>
                                ))
                            )}
                        </tbody>
                    </table>
                </div>

                <div className="md:hidden divide-y">
                    {servicesLoading ? (
                        <div className="p-4 text-center text-muted-foreground text-sm">Cargando servicios...</div>
                    ) : services.length === 0 ? (
                        <div className="p-4 text-center text-muted-foreground text-sm">No hay servicios para este filtro.</div>
                    ) : (
                        services.map((service) => (
                            <div key={getPublicId(service)} className="p-4 space-y-2">
                                <div className="flex justify-between items-start gap-3">
                                    <div>
                                        <div className="text-xs font-semibold uppercase tracking-wide text-primary">{service.type}</div>
                                        <div className="text-sm font-medium">{service.description || "Sin descripcion"}</div>
                                    </div>
                                    <div className="text-xs text-muted-foreground">{formatDate(service.date)}</div>
                                </div>
                                <div className="text-xs text-muted-foreground">
                                    {service.reservaPublicId ? (
                                        <Link to={`/reservas/${service.reservaPublicId}`} className="text-primary hover:underline">
                                            {service.numeroReserva || "Ver reserva"}
                                        </Link>
                                    ) : (
                                        service.numeroReserva || "Sin expediente"
                                    )}
                                </div>
                                <div className="flex justify-between text-sm pt-2 border-t border-dashed">
                                    <span>Costo {formatCurrency(service.netCost)}</span>
                                    <span>Venta {formatCurrency(service.salePrice)}</span>
                                </div>
                            </div>
                        ))
                    )}
                </div>

                <div className="p-4 border-t">
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

            <div className="rounded-xl border bg-card shadow-sm overflow-hidden">
                <div className="border-b p-4 space-y-4">
                    <div className="flex items-center justify-between gap-3">
                        <h2 className="font-semibold flex items-center gap-2">
                            <CreditCard className="h-5 w-5" />
                            Historial de Pagos
                        </h2>
                        <span className="text-sm text-muted-foreground">{paymentsPage.totalCount || 0} resultados</span>
                    </div>

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
                </div>

                <div className="hidden md:block overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="border-b bg-muted/50">
                                <th className="p-3 text-left font-medium">Fecha</th>
                                <th className="p-3 text-left font-medium">Metodo</th>
                                <th className="p-3 text-left font-medium">Referencia</th>
                                <th className="p-3 text-left font-medium">Reserva</th>
                                <th className="p-3 text-left font-medium">Notas</th>
                                <th className="p-3 text-right font-medium">Monto</th>
                                <th className="p-3 text-center font-medium">Acciones</th>
                            </tr>
                        </thead>
                        <tbody>
                            {paymentsLoading ? (
                                <tr>
                                    <td colSpan={7} className="p-4 text-center text-muted-foreground">Cargando pagos...</td>
                                </tr>
                            ) : payments.length === 0 ? (
                                <tr>
                                    <td colSpan={7} className="p-4 text-center text-muted-foreground">No hay pagos para este filtro.</td>
                                </tr>
                            ) : (
                                payments.map((payment) => (
                                    <tr key={getPublicId(payment)} className="border-b hover:bg-muted/30">
                                        <td className="p-3">{formatDate(payment.paidAt)}</td>
                                        <td className="p-3">{payment.method}</td>
                                        <td className="p-3 font-mono text-xs">{payment.reference || "-"}</td>
                                        <td className="p-3">
                                            {payment.reservaPublicId ? (
                                                <Link to={`/reservas/${payment.reservaPublicId}`} className="text-primary hover:underline">
                                                    {payment.numeroReserva || "Ver reserva"}
                                                </Link>
                                            ) : (
                                                payment.numeroReserva || "-"
                                            )}
                                        </td>
                                        <td className="p-3 text-muted-foreground max-w-xs truncate">{payment.notes || "-"}</td>
                                        <td className="p-3 text-right font-mono text-green-600 font-medium">
                                            {formatCurrency(payment.amount)}
                                        </td>
                                        <td className="p-3 text-center">
                                            <div className="flex justify-center gap-2">
                                                <button
                                                    onClick={() => handleOpenPaymentModal(payment)}
                                                    className="p-1 text-blue-600 hover:bg-blue-50 rounded"
                                                    title="Editar"
                                                >
                                                    <Pencil className="h-4 w-4" />
                                                </button>
                                                <button
                                                    onClick={() => handleDeletePayment(payment)}
                                                    className="p-1 text-red-600 hover:bg-red-50 rounded"
                                                    title="Eliminar"
                                                >
                                                    <Trash2 className="h-4 w-4" />
                                                </button>
                                            </div>
                                        </td>
                                    </tr>
                                ))
                            )}
                        </tbody>
                    </table>
                </div>

                <div className="md:hidden divide-y">
                    {paymentsLoading ? (
                        <div className="p-4 text-center text-muted-foreground text-sm">Cargando pagos...</div>
                    ) : payments.length === 0 ? (
                        <div className="p-4 text-center text-muted-foreground text-sm">No hay pagos para este filtro.</div>
                    ) : (
                        payments.map((payment) => (
                            <div key={getPublicId(payment)} className="p-4 space-y-2">
                                <div className="flex justify-between items-center">
                                    <div>
                                        <div className="font-medium">{payment.method}</div>
                                        <div className="text-xs text-muted-foreground">{formatDate(payment.paidAt)}</div>
                                    </div>
                                    <span className="text-green-600 font-mono font-bold">{formatCurrency(payment.amount)}</span>
                                </div>
                                <div className="text-sm text-muted-foreground">
                                    {payment.reservaPublicId ? (
                                        <Link to={`/reservas/${payment.reservaPublicId}`} className="text-primary hover:underline">
                                            {payment.numeroReserva || "Ver reserva"}
                                        </Link>
                                    ) : (
                                        payment.numeroReserva || "Sin expediente"
                                    )}
                                </div>
                                {payment.notes && <div className="text-xs text-muted-foreground italic">{payment.notes}</div>}
                                <div className="flex justify-end gap-3 pt-2">
                                    <button
                                        onClick={() => handleOpenPaymentModal(payment)}
                                        className="text-xs px-3 py-1.5 bg-slate-100 text-slate-600 rounded-lg flex items-center gap-1"
                                    >
                                        <Pencil className="h-3 w-3" /> Editar
                                    </button>
                                    <button
                                        onClick={() => handleDeletePayment(payment)}
                                        className="text-xs px-3 py-1.5 bg-red-50 text-red-600 rounded-lg flex items-center gap-1"
                                    >
                                        <Trash2 className="h-3 w-3" /> Eliminar
                                    </button>
                                </div>
                            </div>
                        ))
                    )}
                </div>

                <div className="p-4 border-t">
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
