import { useEffect, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { ArrowLeft, Building2, Phone, Mail, CreditCard, DollarSign, TrendingUp, FileText, Plus, Pencil, Trash2 } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess, showConfirm } from "../../../alerts";
import SupplierPaymentModal from "../../../components/SupplierPaymentModal";
import { Button } from "../../../components/ui/button";
import { Skeleton, AccountPageSkeleton } from "../../../components/ui/skeleton";
import { Badge } from "../../../components/ui/badge";
import { formatCurrency, formatDate } from "../../../lib/utils";
import Swal from "sweetalert2";

export default function SupplierAccountPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [data, setData] = useState(null);
    const [loading, setLoading] = useState(true);

    // Payment Modal State
    const [showPaymentModal, setShowPaymentModal] = useState(false);
    const [editingPayment, setEditingPayment] = useState(null);

    useEffect(() => {
        fetchData();
    }, [id]);

    const fetchData = async () => {
        try {
            const response = await api.get(`/suppliers/${id}/account`);
            setData(response);
        } catch (error) {
            console.error("Error loading supplier account:", error);
            Swal.fire("Error", "No se pudo cargar la cuenta del proveedor", "error");
        } finally {
            setLoading(false);
        }
    };

    const handleOpenPaymentModal = (payment = null) => {
        setEditingPayment(payment);
        setShowPaymentModal(true);
    };

    const handlePaymentSuccess = () => {
        setShowPaymentModal(false);
        fetchData();
        // Feedback is handled inside the modal (showSuccess)
    };

    const handleDeletePayment = async (payment) => {
        const result = await Swal.fire({
            title: "¿Eliminar pago?",
            text: `Se restaurará la deuda de ${formatCurrency(payment.amount)}. Esta acción no se puede deshacer.`,
            icon: "warning",
            showCancelButton: true,
            confirmButtonText: "Sí, eliminar",
            cancelButtonText: "Cancelar"
        });

        if (result.isConfirmed) {
            try {
                await api.delete(`/suppliers/${id}/payments/${payment.id}`);
                Swal.fire("Eliminado", "El pago ha sido eliminado y el saldo restaurado.", "success");
                fetchData();
            } catch (error) {
                Swal.fire("Error", "No se pudo eliminar el pago", "error");
            }
        }
    };

    if (loading) {
        return <AccountPageSkeleton />;
    }

    if (!data) {
        return (
            <div className="p-6">
                <p className="text-muted-foreground">No se encontró el proveedor</p>
            </div>
        );
    }

    const { supplier, services, payments, summary } = data;

    return (
        <div className="p-6 space-y-6 max-w-7xl mx-auto">
            {/* Header */}
            <div className="flex items-center gap-4">
                <button
                    onClick={() => navigate("/suppliers")}
                    className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-input bg-background/50 hover:bg-accent"
                >
                    <ArrowLeft className="h-5 w-5" />
                </button>
                <div>
                    <h1 className="text-2xl font-bold">Cuenta Corriente: {supplier.name}</h1>
                    <p className="text-muted-foreground">
                        {supplier.contactName && `${supplier.contactName} • `}
                        {supplier.taxId && `CUIT: ${supplier.taxId}`}
                    </p>
                </div>
            </div>

            {/* Contact Info */}
            <div className="flex flex-wrap gap-4 text-sm text-muted-foreground">
                {supplier.phone && (
                    <span className="flex items-center gap-1">
                        <Phone className="h-4 w-4" /> {supplier.phone}
                    </span>
                )}
                {supplier.email && (
                    <span className="flex items-center gap-1">
                        <Mail className="h-4 w-4" /> {supplier.email}
                    </span>
                )}
            </div>

            {/* Summary Cards */}
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                <div className="rounded-xl border bg-card p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-muted-foreground mb-1">
                        <FileText className="h-4 w-4" />
                        <span className="text-sm">Servicios</span>
                    </div>
                    <p className="text-2xl font-bold">{summary.serviceCount}</p>
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
                    <p className="text-xs text-muted-foreground">Lo que debemos</p>
                </div>
            </div>

            {/* Add Payment Button */}
            <div className="flex justify-end">
                <button
                    onClick={() => handleOpenPaymentModal()}
                    className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-700 shadow-sm shadow-emerald-500/20"
                >
                    <Plus className="h-4 w-4" />
                    Registrar Pago
                </button>
            </div>

            {/* Services Table */}
            <div className="rounded-xl border bg-card shadow-sm overflow-hidden">
                <div className="p-4 border-b">
                    <h2 className="font-semibold flex items-center gap-2">
                        <Building2 className="h-5 w-5" />
                        Servicios Comprados ({services.length})
                    </h2>
                </div>

                {/* Desktop Table - Hidden on Mobile */}
                <div className="hidden md:block overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="border-b bg-muted/50">
                                <th className="p-3 text-left font-medium">Tipo</th>
                                <th className="p-3 text-left font-medium">Descripción</th>
                                <th className="p-3 text-left font-medium">Reserva</th>
                                <th className="p-3 text-left font-medium">Fecha</th>
                                <th className="p-3 text-left font-medium">Estado</th>
                                <th className="p-3 text-right font-medium">Costo</th>
                                <th className="p-3 text-right font-medium">Venta</th>
                            </tr>
                        </thead>
                        <tbody>
                            {services.length === 0 ? (
                                <tr>
                                    <td colSpan={7} className="p-4 text-center text-muted-foreground">
                                        No hay servicios de este proveedor
                                    </td>
                                </tr>
                            ) : (
                                services.map((service) => (
                                    <tr key={service.id} className="border-b hover:bg-muted/30">
                                        <td className="p-3">
                                            <span className="px-2 py-1 bg-primary/10 text-primary rounded text-xs font-medium">
                                                {service.type}
                                            </span>
                                        </td>
                                        <td className="p-3">{service.description || "-"}</td>
                                        <td className="p-3">
                                            {service.numeroReserva ? (
                                                <span className="text-primary font-medium">{service.numeroReserva}</span>
                                            ) : "-"}
                                        </td>
                                        <td className="p-3">{formatDate(service.date)}</td>
                                        <td className="p-3">
                                            <span className={`px-2 py-1 rounded text-xs ${service.status === "Confirmado" ? "bg-green-500/10 text-green-600" :
                                                service.status === "Cancelado" ? "bg-red-500/10 text-red-600" :
                                                    "bg-yellow-500/10 text-yellow-600"
                                                }`}>
                                                {service.status}
                                            </span>
                                        </td>
                                        <td className="p-3 text-right font-mono">{formatCurrency(service.netCost)}</td>
                                        <td className="p-3 text-right font-mono">{formatCurrency(service.salePrice)}</td>
                                    </tr>
                                ))
                            )}
                        </tbody>
                    </table>
                </div>

                {/* Mobile Cards for Services */}
                <div className="md:hidden divide-y">
                    {services.length === 0 ? (
                        <div className="p-4 text-center text-muted-foreground text-sm">No hay servicios</div>
                    ) : (
                        services.map((service) => (
                            <div key={service.id} className="p-4 space-y-2">
                                <div className="flex justify-between items-start">
                                    <div className="flex items-center gap-2">
                                        <span className="px-2 py-0.5 bg-primary/10 text-primary rounded text-xs font-bold uppercase">
                                            {service.type}
                                        </span>
                                        <span className="text-sm font-medium">{service.description || "Sin descripción"}</span>
                                    </div>
                                    <span className={`px-2 py-0.5 rounded text-xs font-medium ${service.status === "Confirmado" ? "bg-green-50 text-green-700 dark:bg-green-900/30 dark:text-green-400" :
                                        service.status === "Cancelado" ? "bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-400" :
                                            "bg-yellow-50 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400"
                                        }`}>
                                        {service.status}
                                    </span>
                                </div>
                                <div className="flex justify-between items-center text-sm text-muted-foreground">
                                    <div>{service.numeroReserva} <span className="mx-1">•</span> {formatDate(service.date)}</div>
                                </div>
                                <div className="flex justify-between items-center text-sm pt-1 border-t border-dashed border-gray-100 dark:border-gray-800">
                                    <div className="flex flex-col">
                                        <span className="text-xs text-muted-foreground">Costo</span>
                                        <span className="font-mono font-medium">{formatCurrency(service.netCost)}</span>
                                    </div>
                                    <div className="flex flex-col text-right">
                                        <span className="text-xs text-muted-foreground">Venta</span>
                                        <span className="font-mono font-medium">{formatCurrency(service.salePrice)}</span>
                                    </div>
                                </div>
                            </div>
                        ))
                    )}
                </div>
            </div>

            {/* Payments History */}
            <div className="rounded-xl border bg-card shadow-sm overflow-hidden">
                <div className="p-4 border-b">
                    <h2 className="font-semibold flex items-center gap-2">
                        <CreditCard className="h-5 w-5" />
                        Historial de Pagos ({payments.length})
                    </h2>
                </div>

                {/* Desktop Table - Hidden on Mobile */}
                <div className="hidden md:block overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="border-b bg-muted/50">
                                <th className="p-3 text-left font-medium">Fecha</th>
                                <th className="p-3 text-left font-medium">Método</th>
                                <th className="p-3 text-left font-medium">Referencia</th>
                                <th className="p-3 text-left font-medium">Reserva</th>
                                <th className="p-3 text-left font-medium">Notas</th>
                                <th className="p-3 text-right font-medium">Monto</th>
                                <th className="p-3 text-center font-medium">Acciones</th>
                            </tr>
                        </thead>
                        <tbody>
                            {payments.length === 0 ? (
                                <tr>
                                    <td colSpan={7} className="p-4 text-center text-muted-foreground">
                                        No hay pagos registrados
                                    </td>
                                </tr>
                            ) : (
                                payments.map((payment) => (
                                    <tr key={payment.id} className="border-b hover:bg-muted/30">
                                        <td className="p-3">{formatDate(payment.paidAt)}</td>
                                        <td className="p-3">
                                            <span className="px-2 py-1 bg-blue-500/10 text-blue-600 rounded text-xs">
                                                {payment.method}
                                            </span>
                                        </td>
                                        <td className="p-3 font-mono text-xs">{payment.reference || "-"}</td>
                                        <td className="p-3">{payment.numeroReserva || "-"}</td>
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

                {/* Mobile Cards for Payments */}
                <div className="md:hidden divide-y">
                    {payments.length === 0 ? (
                        <div className="p-4 text-center text-muted-foreground text-sm">No hay pagos</div>
                    ) : (
                        payments.map((payment) => (
                            <div key={payment.id} className="p-4 space-y-2">
                                <div className="flex justify-between items-center">
                                    <div className="flex items-center gap-2">
                                        <span className="px-2 py-0.5 bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400 rounded text-xs font-bold">
                                            {payment.method}
                                        </span>
                                        <span className="text-xs text-muted-foreground">{formatDate(payment.paidAt)}</span>
                                    </div>
                                    <span className="text-green-600 font-mono font-bold">
                                        {formatCurrency(payment.amount)}
                                    </span>
                                </div>
                                <div className="text-sm">
                                    <div><span className="font-semibold">{payment.numeroReserva || "Sin exp."}</span> <span className="text-muted-foreground">Ref: {payment.reference || "-"}</span></div>
                                    {payment.notes && <div className="text-xs text-muted-foreground mt-1 italic">{payment.notes}</div>}
                                </div>
                                <div className="flex justify-end gap-3 pt-2">
                                    <button
                                        onClick={() => handleOpenPaymentModal(payment)}
                                        className="text-xs px-3 py-1.5 bg-slate-100 text-slate-600 rounded-lg flex items-center gap-1 dark:bg-slate-800 dark:text-slate-300"
                                    >
                                        <Pencil className="h-3 w-3" /> Editar
                                    </button>
                                    <button
                                        onClick={() => handleDeletePayment(payment)}
                                        className="text-xs px-3 py-1.5 bg-red-50 text-red-600 rounded-lg flex items-center gap-1 dark:bg-red-900/20 dark:text-red-400"
                                    >
                                        <Trash2 className="h-3 w-3" /> Eliminar
                                    </button>
                                </div>
                            </div>
                        ))
                    )}
                </div>
            </div>

            {/* New Professional Payment Modal */}
            <SupplierPaymentModal
                isOpen={showPaymentModal}
                onClose={() => setShowPaymentModal(false)}
                onSuccess={handlePaymentSuccess}
                supplierId={supplier.id}
                supplierName={supplier.name}
                currentBalance={summary.balance} // We pass the current Debt to validate overpayment
                editingPayment={editingPayment}
            />
        </div>
    );
}
