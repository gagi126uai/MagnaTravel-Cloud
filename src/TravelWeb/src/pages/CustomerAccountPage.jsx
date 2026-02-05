import { useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { api } from "../api";
import { showError } from "../alerts";
import {
    ArrowLeft,
    User,
    Phone,
    Mail,
    FileText,
    CreditCard,
    Calendar,
    DollarSign,
    TrendingUp,
    Building
} from "lucide-react";
import { Button } from "../components/ui/button";

const StatusBadge = ({ status }) => {
    const colors = {
        "Presupuesto": "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400",
        "Reservado": "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400",
        "Operativo": "bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400",
        "Cerrado": "bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300",
        "Cancelado": "bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400"
    };
    return (
        <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium ${colors[status] || "bg-gray-100 text-gray-800"}`}>
            {status}
        </span>
    );
};

const formatCurrency = (value) => {
    return new Intl.NumberFormat("es-AR", {
        style: "currency",
        currency: "ARS",
        minimumFractionDigits: 0
    }).format(value || 0);
};

const formatDate = (dateString) => {
    if (!dateString) return "-";
    return new Date(dateString).toLocaleDateString("es-AR");
};

const methodLabels = {
    "Cash": "Efectivo",
    "Transfer": "Transferencia",
    "Card": "Tarjeta"
};

export default function CustomerAccountPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [data, setData] = useState(null);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        loadAccount();
    }, [id]);

    const loadAccount = async () => {
        setLoading(true);
        try {
            const result = await api.get(`/customers/${id}/account`);
            setData(result);
        } catch (error) {
            showError("No se pudo cargar la cuenta corriente");
        } finally {
            setLoading(false);
        }
    };

    if (loading) {
        return (
            <div className="flex items-center justify-center h-64">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary"></div>
            </div>
        );
    }

    if (!data) {
        return (
            <div className="text-center py-12 text-muted-foreground">
                No se encontraron datos del cliente
            </div>
        );
    }

    const { customer, files, payments, summary } = data;

    return (
        <div className="space-y-6">
            {/* Header */}
            <div className="flex items-center gap-4">
                <Button variant="ghost" size="icon" onClick={() => navigate("/customers")}>
                    <ArrowLeft className="h-5 w-5" />
                </Button>
                <div>
                    <h1 className="text-2xl font-bold">Cuenta Corriente</h1>
                    <p className="text-muted-foreground">{customer.fullName}</p>
                </div>
            </div>

            {/* Customer Info Card */}
            <div className="rounded-xl border bg-gradient-to-r from-indigo-500/10 to-purple-500/10 p-6">
                <div className="flex items-start justify-between">
                    <div className="flex items-center gap-4">
                        <div className="h-16 w-16 rounded-full bg-indigo-600 flex items-center justify-center text-white text-2xl font-bold">
                            {customer.fullName?.charAt(0)?.toUpperCase() || "?"}
                        </div>
                        <div>
                            <h2 className="text-xl font-semibold">{customer.fullName}</h2>
                            <div className="flex flex-wrap gap-4 mt-2 text-sm text-muted-foreground">
                                {customer.email && (
                                    <span className="flex items-center gap-1">
                                        <Mail className="h-4 w-4" /> {customer.email}
                                    </span>
                                )}
                                {customer.phone && (
                                    <span className="flex items-center gap-1">
                                        <Phone className="h-4 w-4" /> {customer.phone}
                                    </span>
                                )}
                                {customer.taxId && (
                                    <span className="flex items-center gap-1">
                                        <Building className="h-4 w-4" /> CUIT: {customer.taxId}
                                    </span>
                                )}
                            </div>
                        </div>
                    </div>
                    {customer.creditLimit > 0 && (
                        <div className="text-right">
                            <div className="text-sm text-muted-foreground">Límite de Crédito</div>
                            <div className="text-lg font-semibold text-green-600">
                                {formatCurrency(customer.creditLimit)}
                            </div>
                        </div>
                    )}
                </div>
            </div>

            {/* Summary Cards */}
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                <div className="rounded-xl border bg-card p-5">
                    <div className="flex items-center gap-3">
                        <div className="p-2.5 rounded-lg bg-blue-100 dark:bg-blue-900/30">
                            <FileText className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                        </div>
                        <div>
                            <div className="text-sm text-muted-foreground">Expedientes</div>
                            <div className="text-2xl font-bold">{summary.fileCount}</div>
                        </div>
                    </div>
                </div>
                <div className="rounded-xl border bg-card p-5">
                    <div className="flex items-center gap-3">
                        <div className="p-2.5 rounded-lg bg-green-100 dark:bg-green-900/30">
                            <TrendingUp className="h-5 w-5 text-green-600 dark:text-green-400" />
                        </div>
                        <div>
                            <div className="text-sm text-muted-foreground">Total Vendido</div>
                            <div className="text-xl font-bold">{formatCurrency(summary.totalSales)}</div>
                        </div>
                    </div>
                </div>
                <div className="rounded-xl border bg-card p-5">
                    <div className="flex items-center gap-3">
                        <div className="p-2.5 rounded-lg bg-emerald-100 dark:bg-emerald-900/30">
                            <CreditCard className="h-5 w-5 text-emerald-600 dark:text-emerald-400" />
                        </div>
                        <div>
                            <div className="text-sm text-muted-foreground">Total Cobrado</div>
                            <div className="text-xl font-bold">{formatCurrency(summary.totalPaid)}</div>
                        </div>
                    </div>
                </div>
                <div className={`rounded-xl border p-5 ${summary.totalBalance > 0 ? "bg-rose-50 dark:bg-rose-900/20 border-rose-200 dark:border-rose-800" : "bg-green-50 dark:bg-green-900/20 border-green-200 dark:border-green-800"}`}>
                    <div className="flex items-center gap-3">
                        <div className={`p-2.5 rounded-lg ${summary.totalBalance > 0 ? "bg-rose-100 dark:bg-rose-900/30" : "bg-green-100 dark:bg-green-900/30"}`}>
                            <DollarSign className={`h-5 w-5 ${summary.totalBalance > 0 ? "text-rose-600 dark:text-rose-400" : "text-green-600 dark:text-green-400"}`} />
                        </div>
                        <div>
                            <div className="text-sm text-muted-foreground">Saldo Pendiente</div>
                            <div className={`text-xl font-bold ${summary.totalBalance > 0 ? "text-rose-600 dark:text-rose-400" : "text-green-600 dark:text-green-400"}`}>
                                {formatCurrency(summary.totalBalance)}
                            </div>
                        </div>
                    </div>
                </div>
            </div>

            {/* Files Table */}
            <div className="rounded-xl border overflow-hidden">
                <div className="bg-muted/50 px-6 py-4 border-b">
                    <h3 className="font-semibold flex items-center gap-2">
                        <FileText className="h-5 w-5 text-primary" />
                        Expedientes ({files.length})
                    </h3>
                </div>
                {files.length > 0 ? (
                    <div className="overflow-x-auto">
                        <table className="w-full">
                            <thead className="bg-muted/30 text-sm">
                                <tr>
                                    <th className="text-left px-6 py-3 font-medium">Expediente</th>
                                    <th className="text-left px-6 py-3 font-medium">Nombre</th>
                                    <th className="text-center px-6 py-3 font-medium">Estado</th>
                                    <th className="text-right px-6 py-3 font-medium">Venta</th>
                                    <th className="text-right px-6 py-3 font-medium">Pagado</th>
                                    <th className="text-right px-6 py-3 font-medium">Saldo</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y">
                                {files.map((file) => (
                                    <tr key={file.id} className="hover:bg-muted/30 transition-colors">
                                        <td className="px-6 py-4">
                                            <Link to={`/files/${file.id}`} className="text-primary hover:underline font-medium">
                                                {file.fileNumber}
                                            </Link>
                                        </td>
                                        <td className="px-6 py-4">{file.name}</td>
                                        <td className="px-6 py-4 text-center">
                                            <StatusBadge status={file.status} />
                                        </td>
                                        <td className="px-6 py-4 text-right font-medium">
                                            {formatCurrency(file.totalSale)}
                                        </td>
                                        <td className="px-6 py-4 text-right text-green-600 dark:text-green-400">
                                            {formatCurrency(file.paid)}
                                        </td>
                                        <td className={`px-6 py-4 text-right font-semibold ${file.balance > 0 ? "text-rose-600 dark:text-rose-400" : "text-green-600 dark:text-green-400"}`}>
                                            {formatCurrency(file.balance)}
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                            <tfoot className="bg-muted/50 font-semibold">
                                <tr>
                                    <td colSpan="3" className="px-6 py-3 text-right">Totales:</td>
                                    <td className="px-6 py-3 text-right">{formatCurrency(summary.totalSales)}</td>
                                    <td className="px-6 py-3 text-right text-green-600 dark:text-green-400">{formatCurrency(summary.totalPaid)}</td>
                                    <td className={`px-6 py-3 text-right ${summary.totalBalance > 0 ? "text-rose-600 dark:text-rose-400" : "text-green-600 dark:text-green-400"}`}>
                                        {formatCurrency(summary.totalBalance)}
                                    </td>
                                </tr>
                            </tfoot>
                        </table>
                    </div>
                ) : (
                    <div className="px-6 py-12 text-center text-muted-foreground">
                        Este cliente no tiene expedientes registrados.
                    </div>
                )}
            </div>

            {/* Payments History */}
            <div className="rounded-xl border overflow-hidden">
                <div className="bg-muted/50 px-6 py-4 border-b">
                    <h3 className="font-semibold flex items-center gap-2">
                        <CreditCard className="h-5 w-5 text-primary" />
                        Historial de Pagos ({payments.length})
                    </h3>
                </div>
                {payments.length > 0 ? (
                    <div className="divide-y max-h-96 overflow-y-auto">
                        {payments.map((payment) => (
                            <div key={payment.id} className="flex items-center justify-between px-6 py-4 hover:bg-muted/30 transition-colors">
                                <div className="flex items-center gap-4">
                                    <div className="p-2 rounded-lg bg-green-100 dark:bg-green-900/30">
                                        <CreditCard className="h-4 w-4 text-green-600 dark:text-green-400" />
                                    </div>
                                    <div>
                                        <div className="font-medium text-green-600 dark:text-green-400">
                                            +{formatCurrency(payment.amount)}
                                        </div>
                                        <div className="text-sm text-muted-foreground">
                                            {methodLabels[payment.method] || payment.method} • {payment.fileNumber}
                                        </div>
                                    </div>
                                </div>
                                <div className="text-right">
                                    <div className="flex items-center gap-1 text-sm text-muted-foreground">
                                        <Calendar className="h-3 w-3" />
                                        {formatDate(payment.paymentDate)}
                                    </div>
                                    {payment.notes && (
                                        <div className="text-xs text-muted-foreground truncate max-w-48">
                                            {payment.notes}
                                        </div>
                                    )}
                                </div>
                            </div>
                        ))}
                    </div>
                ) : (
                    <div className="px-6 py-12 text-center text-muted-foreground">
                        No hay pagos registrados para este cliente.
                    </div>
                )}
            </div>
        </div>
    );
}
