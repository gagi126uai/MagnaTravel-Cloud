import { useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { api } from "../../../api";
import { showError } from "../../../alerts";
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
    Building,
    Plus,
    Pencil,
    Trash2,
    ArrowDownLeft,
    ArrowUpRight,
    Receipt,
    Wallet,
    Info,
    Download,
    Eye,
    History
} from "lucide-react";
import CustomerPaymentModal from "../../../components/CustomerPaymentModal";
import Swal from "sweetalert2";
import { Button } from "../../../components/ui/button";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";

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
    const [isModalOpen, setIsModalOpen] = useState(false);
    const [paymentToEdit, setPaymentToEdit] = useState(null);

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

    const handleOpenModal = (payment = null) => {
        setPaymentToEdit(payment);
        setIsModalOpen(true);
    };

    const handleDeletePayment = async (payment) => {
        const result = await Swal.fire({
            title: "¿Eliminar pago?",
            text: `Se anulará el pago de ${formatCurrency(payment.amount)} y la deuda volverá a la reserva.`,
            icon: "warning",
            showCancelButton: true,
            confirmButtonText: "Sí, eliminar",
            cancelButtonText: "Cancelar",
            confirmButtonColor: "#ef4444"
        });

        if (result.isConfirmed) {
            try {
                await api.delete(`/reservas/${payment.reservaId}/payments/${payment.id}`);
                Swal.fire("Eliminado", "El pago ha sido eliminado.", "success");
                loadAccount();
            } catch (error) {
                console.error(error);
                showError("No se pudo eliminar el pago.");
            }
        }
    };

    if (loading) {
        return <AccountPageSkeleton />;
    }

    if (!data) {
        return (
            <div className="text-center py-12 text-muted-foreground">
                No se encontraron datos del cliente
            </div>
        );
    }
    const { customer, reservas = [], payments = [], summary = {}, invoices = [] } = data;
    const [activeTab, setActiveTab] = useState("ledger");

    // --- Ledger Calculation (Debits & Credits only) ---
    const debitMovements = (reservas || []).map(r => ({
        id: r.id,
        trackId: `res-${r.id}`,
        type: 'RESERVA',
        date: r.startDate || r.createdAt,
        concept: `Reserva ${r.numeroReserva} - ${r.name}`,
        debit: r.totalSale,
        credit: 0,
        originalData: r
    }));

    const creditMovements = (payments || []).map(p => ({
        id: p.id,
        trackId: `pay-${p.id}`,
        type: 'PAYMENT',
        date: p.paymentDate,
        concept: `Pago (${methodLabels[p.method] || p.method})`,
        debit: 0,
        credit: p.amount,
        originalData: p
    }));

    // Merge and Sort for Financial Balance
    const financialMovements = [...debitMovements, ...creditMovements].sort((a, b) => new Date(a.date) - new Date(b.date));

    // Calculate Running Balance
    let runningBalance = 0;
    const ledger = financialMovements.map(m => {
        runningBalance += (m.debit - m.credit);
        return { ...m, balance: runningBalance };
    });

    const displayLedger = [...ledger].reverse();

    // --- Invoices Tab (Pure History) ---
    const displayInvoices = (invoices || []).sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));

    return (
        <div className="space-y-6 animate-in fade-in duration-500">
            {/* Header */}
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4">
                <div className="flex items-center gap-4">
                    <Button variant="ghost" size="icon" onClick={() => navigate("/customers")}>
                        <ArrowLeft className="h-5 w-5" />
                    </Button>
                    <div>
                        <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Cuenta Corriente</h1>
                        <p className="text-muted-foreground">{customer.fullName}</p>
                    </div>
                </div>
                <div className="flex gap-2">
                    <Button onClick={() => handleOpenModal(null)} className="gap-2 bg-emerald-600 hover:bg-emerald-700 text-white shadow-lg shadow-emerald-500/20">
                        <Plus className="h-4 w-4" />
                        Nueva Cobranza
                    </Button>
                </div>
            </div>

            {/* Customer Info Card & Summary */}
            <div className="grid gap-4 md:grid-cols-3">
                <div className="md:col-span-2 rounded-xl border border-slate-200 bg-white dark:bg-slate-900/50 p-6 shadow-sm">
                    <div className="flex items-start justify-between">
                        <div className="flex items-center gap-4">
                            <div className="h-16 w-16 rounded-full bg-gradient-to-br from-indigo-500 to-purple-600 flex items-center justify-center text-white text-2xl font-bold shadow-md">
                                {customer.fullName?.charAt(0)?.toUpperCase() || "?"}
                            </div>
                            <div>
                                <h2 className="text-xl font-bold text-slate-900 dark:text-white">{customer.fullName}</h2>
                                <div className="flex flex-wrap gap-x-4 gap-y-1 mt-2 text-sm text-slate-500">
                                    {customer.email && (
                                        <span className="flex items-center gap-1">
                                            <Mail className="h-3.5 w-3.5" /> {customer.email}
                                        </span>
                                    )}
                                    {customer.phone && (
                                        <span className="flex items-center gap-1">
                                            <Phone className="h-3.5 w-3.5" /> {customer.phone}
                                        </span>
                                    )}
                                    {customer.taxId && (
                                        <span className="flex items-center gap-1">
                                            <Building className="h-3.5 w-3.5" /> {customer.taxId}
                                        </span>
                                    )}
                                </div>
                            </div>
                        </div>
                    </div>
                </div>

                <div className={`rounded-xl border p-6 shadow-sm flex flex-col justify-center ${summary.totalBalance > 0 ? "bg-rose-50 border-rose-100 dark:bg-rose-900/10 dark:border-rose-900/30" : "bg-emerald-50 border-emerald-100 dark:bg-emerald-900/10 dark:border-emerald-900/30"}`}>
                    <div className="text-sm font-medium text-slate-500 dark:text-slate-400 mb-1">Saldo Actual</div>
                    <div className={`text-3xl font-bold ${summary.totalBalance > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                        {formatCurrency(summary.totalBalance)}
                    </div>
                    <div className="text-xs text-slate-400 mt-2 font-medium">
                        {summary.totalBalance > 0 ? "Deuda Pendiente" : "Al día / A favor"}
                    </div>
                </div>
            </div>

            {/* Tabs Control */}
            <div className="flex border-b border-slate-200 dark:border-slate-800 gap-8">
                <button
                    onClick={() => setActiveTab("ledger")}
                    className={`pb-4 text-sm font-semibold transition-all relative ${activeTab === "ledger" ? "text-indigo-600 dark:text-indigo-400" : "text-slate-500 hover:text-slate-700 dark:hover:text-slate-300"}`}
                >
                    <div className="flex items-center gap-2">
                        <Wallet className="h-4 w-4" /> Movimientos
                    </div>
                    {activeTab === "ledger" && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-indigo-600 dark:bg-indigo-400 rounded-full" />}
                </button>
                <button
                    onClick={() => setActiveTab("invoices")}
                    className={`pb-4 text-sm font-semibold transition-all relative ${activeTab === "invoices" ? "text-indigo-600 dark:text-indigo-400" : "text-slate-500 hover:text-slate-700 dark:hover:text-slate-300"}`}
                >
                    <div className="flex items-center gap-2">
                        <Receipt className="h-4 w-4" /> Facturación AFIP
                        <span className="bg-slate-100 dark:bg-slate-800 text-[10px] px-1.5 py-0.5 rounded-full ml-1">
                            {invoices.length}
                        </span>
                    </div>
                    {activeTab === "invoices" && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-indigo-600 dark:bg-indigo-400 rounded-full" />}
                </button>
            </div>

            {/* Content per Tab */}
            <div className="grid gap-6">
                {activeTab === "ledger" ? (
                    <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800 animate-in fade-in slide-in-from-bottom-2 duration-300">
                        {displayLedger.length > 0 ? (
                            <div className="overflow-x-auto">
                                <table className="w-full text-sm text-left">
                                    <thead className="bg-slate-50 text-slate-500 border-b border-slate-200 dark:bg-slate-950 dark:border-slate-800 dark:text-slate-400">
                                        <tr>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px]">Fecha</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px]">Concepto / Detalle</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px] text-right w-[150px]">Debe (Venta)</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px] text-right w-[150px]">Haber (Cobro)</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px] text-right w-[150px]">Saldo Parcial</th>
                                            <th className="px-6 py-3 w-[80px]"></th>
                                        </tr>
                                    </thead>
                                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                        {displayLedger.map((move) => (
                                            <tr key={move.trackId} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors group">
                                                <td className="px-6 py-4 text-slate-600 dark:text-slate-400 font-medium">
                                                    {formatDate(move.date)}
                                                </td>
                                                <td className="px-6 py-4">
                                                    <div className="font-bold text-slate-900 dark:text-white group-hover:text-indigo-600 transition-colors">
                                                        {move.concept}
                                                    </div>
                                                    <div className="text-[10px] font-black uppercase mt-0.5 tracking-tighter">
                                                        {move.type === 'RESERVA' ? (
                                                            <span className="text-rose-500 flex items-center gap-1">
                                                                <ArrowUpRight className="h-3 w-3" /> Cargo por Servicios
                                                            </span>
                                                        ) : (
                                                            <span className="text-emerald-500 flex items-center gap-1">
                                                                <ArrowDownLeft className="h-3 w-3" /> Cobranza Recibida
                                                            </span>
                                                        )}
                                                    </div>
                                                </td>
                                                <td className="px-6 py-4 text-right font-black text-rose-600 dark:text-rose-400">
                                                    {move.debit > 0 ? formatCurrency(move.debit) : "-"}
                                                </td>
                                                <td className="px-6 py-4 text-right font-black text-emerald-600 dark:text-emerald-400">
                                                    {move.credit > 0 ? formatCurrency(move.credit) : "-"}
                                                </td>
                                                <td className="px-6 py-4 text-right font-black text-slate-700 dark:text-slate-300 bg-slate-50/30 dark:bg-slate-800/10">
                                                    {formatCurrency(move.balance)}
                                                </td>
                                                <td className="px-6 py-4 text-right">
                                                    <div className="flex justify-end gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                                                        {move.type === 'PAYMENT' && (
                                                            <button onClick={() => handleDeletePayment(move.originalData)} className="p-1.5 hover:bg-rose-50 rounded-lg text-rose-400 hover:text-rose-600 transition-colors" title="Anular Pago">
                                                                <Trash2 className="h-4 w-4" />
                                                            </button>
                                                        )}
                                                        {move.type === 'RESERVA' && (
                                                            <Link to={`/reservas/${move.originalData.id}`} className="p-1.5 hover:bg-indigo-50 rounded-lg text-indigo-400 hover:text-indigo-600 transition-colors inline-block" title="Ver Detalles">
                                                                <Eye className="h-4 w-4" />
                                                            </Link>
                                                        )}
                                                    </div>
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        ) : (
                            <div className="px-6 py-16 text-center text-slate-400 italic bg-slate-50/50 dark:bg-slate-800/10">
                                No se registran movimientos financieros en la cuenta.
                            </div>
                        )}
                    </div>
                ) : (
                    <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800 animate-in fade-in slide-in-from-bottom-2 duration-300">
                        {displayInvoices.length > 0 ? (
                            <div className="overflow-x-auto">
                                <table className="w-full text-sm text-left">
                                    <thead className="bg-slate-50 text-slate-500 border-b border-slate-200 dark:bg-slate-950 dark:border-slate-800 dark:text-slate-400">
                                        <tr>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px]">Fecha Emisión</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px]">Nro de Comprobante</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px]">Tipo</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px] text-right">Importe Total</th>
                                            <th className="px-6 py-3 font-bold uppercase text-[10px] text-center">Estado</th>
                                            <th className="px-6 py-3 w-[100px]"></th>
                                        </tr>
                                    </thead>
                                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                        {displayInvoices.map((inv) => (
                                            <tr key={inv.id} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors group">
                                                <td className="px-6 py-4 text-slate-600 dark:text-slate-400">
                                                    {formatDate(inv.createdAt)}
                                                </td>
                                                <td className="px-6 py-4 font-black tracking-tight text-slate-900 dark:text-white">
                                                    {String(inv.puntoDeVenta).padStart(5, '0')}-{String(inv.numeroComprobante).padStart(8, '0')}
                                                </td>
                                                <td className="px-6 py-4">
                                                    <div className="flex items-center gap-2">
                                                        <Receipt className="h-4 w-4 text-indigo-400" />
                                                        <span className="font-semibold">{inv.tipoComprobante === 1 ? 'Factura A' : inv.tipoComprobante === 6 ? 'Factura B' : inv.tipoComprobante === 11 ? 'Factura C' : `Tipo ${inv.tipoComprobante}`}</span>
                                                    </div>
                                                </td>
                                                <td className="px-6 py-4 text-right font-black text-slate-900 dark:text-white">
                                                    {formatCurrency(inv.importeTotal)}
                                                </td>
                                                <td className="px-6 py-4 text-center">
                                                    <span className={`px-2 py-0.5 rounded-full text-[10px] font-black uppercase ${inv.cae ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400' : 'bg-slate-100 text-slate-600'}`}>
                                                        {inv.cae ? 'Autorizado' : 'Pendiente'}
                                                    </span>
                                                </td>
                                                <td className="px-6 py-4 text-right">
                                                    <div className="flex justify-end gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                                                        <button className="p-1.5 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-lg text-slate-400 hover:text-slate-900 transition-colors" title="Descargar PDF">
                                                            <Download className="h-4 w-4" />
                                                        </button>
                                                        <Link to={`/reservas/${inv.reservaId}`} className="p-1.5 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-lg text-slate-400 hover:text-slate-900 transition-colors inline-block" title="Ir a Reserva">
                                                            <ArrowUpRight className="h-4 w-4" />
                                                        </Link>
                                                    </div>
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        ) : (
                            <div className="px-6 py-16 text-center text-slate-400 italic bg-slate-50/50 dark:bg-slate-800/10">
                                No se registran facturas emitidas ante AFIP para este cliente.
                            </div>
                        )}
                    </div>
                )}
            </div>

            <CustomerPaymentModal
                isOpen={isModalOpen}
                onClose={() => setIsModalOpen(false)}
                paymentToEdit={paymentToEdit}
                customerId={id}
                availableReservas={reservas.filter(f => f.status !== "Cancelado")}
                onSave={loadAccount}
            />
        </div>
    );
}
