import { useEffect, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { ArrowLeft, Building2, Phone, Mail, CreditCard, DollarSign, TrendingUp, FileText, Plus } from "lucide-react";
import { api } from "../api";
import { formatCurrency, formatDate } from "../lib/utils";
import Swal from "sweetalert2";

export default function SupplierAccountPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [data, setData] = useState(null);
    const [loading, setLoading] = useState(true);
    const [showPaymentModal, setShowPaymentModal] = useState(false);
    const [paymentForm, setPaymentForm] = useState({
        amount: "",
        method: "Transfer",
        reference: "",
        notes: ""
    });

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

    const handleAddPayment = async () => {
        if (!paymentForm.amount || parseFloat(paymentForm.amount) <= 0) {
            Swal.fire("Error", "El monto debe ser mayor a 0", "error");
            return;
        }

        try {
            await api.post(`/suppliers/${id}/payments`, {
                amount: parseFloat(paymentForm.amount),
                method: paymentForm.method,
                reference: paymentForm.reference,
                notes: paymentForm.notes
            });

            Swal.fire("Éxito", "Pago registrado correctamente", "success");
            setShowPaymentModal(false);
            setPaymentForm({ amount: "", method: "Transfer", reference: "", notes: "" });
            fetchData();
        } catch (error) {
            Swal.fire("Error", error.response?.data || "Error al registrar el pago", "error");
        }
    };

    if (loading) {
        return (
            <div className="flex items-center justify-center h-full">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary"></div>
            </div>
        );
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
                    onClick={() => setShowPaymentModal(true)}
                    className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-700"
                >
                    <Plus className="h-4 w-4" />
                    Registrar Pago
                </button>
            </div>

            {/* Services Table */}
            <div className="rounded-xl border bg-card shadow-sm">
                <div className="p-4 border-b">
                    <h2 className="font-semibold flex items-center gap-2">
                        <Building2 className="h-5 w-5" />
                        Servicios Comprados ({services.length})
                    </h2>
                </div>
                <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="border-b bg-muted/50">
                                <th className="p-3 text-left font-medium">Tipo</th>
                                <th className="p-3 text-left font-medium">Descripción</th>
                                <th className="p-3 text-left font-medium">Expediente</th>
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
                                                {service.serviceType}
                                            </span>
                                        </td>
                                        <td className="p-3">{service.description || "-"}</td>
                                        <td className="p-3">
                                            {service.fileNumber ? (
                                                <span className="text-primary font-medium">{service.fileNumber}</span>
                                            ) : "-"}
                                        </td>
                                        <td className="p-3">{formatDate(service.departureDate)}</td>
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
            </div>

            {/* Payments History */}
            <div className="rounded-xl border bg-card shadow-sm">
                <div className="p-4 border-b">
                    <h2 className="font-semibold flex items-center gap-2">
                        <CreditCard className="h-5 w-5" />
                        Historial de Pagos ({payments.length})
                    </h2>
                </div>
                <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="border-b bg-muted/50">
                                <th className="p-3 text-left font-medium">Fecha</th>
                                <th className="p-3 text-left font-medium">Método</th>
                                <th className="p-3 text-left font-medium">Referencia</th>
                                <th className="p-3 text-left font-medium">Expediente</th>
                                <th className="p-3 text-left font-medium">Notas</th>
                                <th className="p-3 text-right font-medium">Monto</th>
                            </tr>
                        </thead>
                        <tbody>
                            {payments.length === 0 ? (
                                <tr>
                                    <td colSpan={6} className="p-4 text-center text-muted-foreground">
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
                                        <td className="p-3">{payment.fileNumber || "-"}</td>
                                        <td className="p-3 text-muted-foreground">{payment.notes || "-"}</td>
                                        <td className="p-3 text-right font-mono text-green-600 font-medium">
                                            {formatCurrency(payment.amount)}
                                        </td>
                                    </tr>
                                ))
                            )}
                        </tbody>
                    </table>
                </div>
            </div>

            {/* Payment Modal */}
            {showPaymentModal && (
                <div className="fixed inset-0 bg-black/50 backdrop-blur-sm flex items-center justify-center z-50">
                    <div className="bg-background border rounded-xl p-6 w-full max-w-md space-y-4">
                        <h3 className="text-lg font-semibold">Registrar Pago a {supplier.name}</h3>

                        <div className="space-y-3">
                            <div>
                                <label className="text-sm font-medium">Monto *</label>
                                <input
                                    type="number"
                                    step="0.01"
                                    value={paymentForm.amount}
                                    onChange={(e) => setPaymentForm({ ...paymentForm, amount: e.target.value })}
                                    className="w-full mt-1 rounded-md border border-input bg-background px-3 py-2"
                                    placeholder="0.00"
                                />
                            </div>

                            <div>
                                <label className="text-sm font-medium">Método de Pago</label>
                                <select
                                    value={paymentForm.method}
                                    onChange={(e) => setPaymentForm({ ...paymentForm, method: e.target.value })}
                                    className="w-full mt-1 rounded-md border border-input bg-background px-3 py-2"
                                >
                                    <option value="Transfer">Transferencia</option>
                                    <option value="Cash">Efectivo</option>
                                    <option value="Card">Tarjeta</option>
                                    <option value="Check">Cheque</option>
                                </select>
                            </div>

                            <div>
                                <label className="text-sm font-medium">Referencia (Nro. transferencia, cheque, etc.)</label>
                                <input
                                    type="text"
                                    value={paymentForm.reference}
                                    onChange={(e) => setPaymentForm({ ...paymentForm, reference: e.target.value })}
                                    className="w-full mt-1 rounded-md border border-input bg-background px-3 py-2"
                                    placeholder="Nro. de comprobante"
                                />
                            </div>

                            <div>
                                <label className="text-sm font-medium">Notas</label>
                                <textarea
                                    value={paymentForm.notes}
                                    onChange={(e) => setPaymentForm({ ...paymentForm, notes: e.target.value })}
                                    className="w-full mt-1 rounded-md border border-input bg-background px-3 py-2"
                                    rows={2}
                                    placeholder="Notas adicionales..."
                                />
                            </div>
                        </div>

                        <div className="flex justify-end gap-2">
                            <button
                                onClick={() => setShowPaymentModal(false)}
                                className="px-4 py-2 text-sm font-medium rounded-md border hover:bg-accent"
                            >
                                Cancelar
                            </button>
                            <button
                                onClick={handleAddPayment}
                                className="px-4 py-2 text-sm font-medium rounded-md bg-emerald-600 text-white hover:bg-emerald-700"
                            >
                                Registrar Pago
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}
