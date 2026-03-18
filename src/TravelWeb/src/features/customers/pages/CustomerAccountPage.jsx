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
    ArrowUpRight
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

    // --- Unified Ledger Logic (Cuenta Corriente Real) ---
    // 1. Map Files (Ventas) -> DEBITS
    // 2. Map Payments (Cobros) -> CREDITS
    // 3. Sort Chronologically
    // 4. Calculate Running Balance

    const debitMovements = (reservas || []).map(r => ({
        id: r.id,
        type: 'RESERVA',
        date: r.startDate || r.createdAt, // Use startDate as fallback for transaction date
        concept: `Reserva ${r.numeroReserva} - ${r.name}`,
        debit: r.totalSale,
        credit: 0,
        originalData: r
    }));

    const creditMovements = (payments || []).map(p => ({
        id: p.id,
        type: 'PAYMENT',
        date: p.paymentDate,
        concept: `Pago (${methodLabels[p.method] || p.method})`,
        debit: 0,
        credit: p.amount,
        originalData: p
    }));

    // Merge and Sort Oldest to Newest to calculate balance
    const allMovements = [...debitMovements, ...creditMovements].sort((a, b) => new Date(a.date) - new Date(b.date));

    // Calculate Running Balance
    let runningBalance = 0;
    const ledger = allMovements.map(m => {
        runningBalance += (m.debit - m.credit);
        return { ...m, balance: runningBalance };
    });

    // Reverse for UI (Newest First) if preferred, or keep Oldest First.
    // For "Statement" view, usually Oldest First is better to read the story.
    // For "Quick Check", Newest First is better.
    // Let's do Newest First but display it clearly.
    const displayLedger = [...ledger].reverse();

    return (
        <div className="space-y-6 animate-in fade-in duration-500">
            {/* Header */}
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4">
                <div className="flex items-center gap-4">
                    <Button variant="ghost" size="icon" onClick={() => navigate("/customers")}>
                        <ArrowLeft className="h-5 w-5" />
                    </Button>
                    <div>
                        <h1 className="text-2xl font-bold tracking-tight">Cuenta Corriente</h1>
                        <p className="text-muted-foreground">{customer.fullName}</p>
                    </div>
                </div>
                <Button onClick={() => handleOpenModal(null)} className="gap-2 bg-emerald-600 hover:bg-emerald-700 text-white shadow-lg shadow-emerald-500/20">
                    <Plus className="h-4 w-4" />
                    Nueva Cobranza
                </Button>
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
                    <div className="text-xs text-slate-400 mt-2">
                        {summary.totalBalance > 0 ? "Deuda Pendiente" : "Al día / A favor"}
                    </div>
                </div>
            </div>

            <div className="grid gap-6 lg:grid-cols-3">
                <div className="lg:col-span-2">
                    {/* Unified Ledger Table */}
                    <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800">
                        <div className="bg-slate-50/50 px-6 py-4 border-b border-slate-200 dark:bg-slate-800/20 dark:border-slate-800">
                            <h3 className="font-semibold flex items-center gap-2 text-slate-900 dark:text-white">
                                <FileText className="h-5 w-5 text-indigo-500" />
                                Movimientos (Cuenta Corriente)
                            </h3>
                        </div>

                        {displayLedger.length > 0 ? (
                            <>
                                {/* Desktop Table */}
                                <div className="hidden md:block overflow-x-auto">
                                    <table className="w-full text-sm text-left">
                                        <thead className="bg-slate-50 text-slate-500 border-b border-slate-200 dark:bg-slate-950 dark:border-slate-800 dark:text-slate-400">
                                            <tr>
                                                <th className="px-6 py-3 font-medium w-[120px]">Fecha</th>
                                                <th className="px-6 py-3 font-medium">Concepto</th>
                                                <th className="px-6 py-3 font-medium text-right w-[150px]">Debe (Venta)</th>
                                                <th className="px-6 py-3 font-medium text-right w-[150px]">Haber (Pago)</th>
                                                <th className="px-6 py-3 font-medium text-right w-[150px]">Saldo Parcial</th>
                                                <th className="px-6 py-3 w-[50px]"></th>
                                            </tr>
                                        </thead>
                                        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                            {displayLedger.map((move) => (
                                                <tr key={`${move.type}-${move.id}`} className="hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors">
                                                    <td className="px-6 py-4 text-slate-600 dark:text-slate-400">
                                                        {formatDate(move.date)}
                                                    </td>
                                                    <td className="px-6 py-4">
                                                        <div className="font-medium text-slate-900 dark:text-slate-200">
                                                            {move.concept}
                                                        </div>
                                                        <div className="text-xs text-slate-500 mt-0.5">
                                                            {move.type === 'RESERVA' ? (
                                                                <span className="inline-flex items-center gap-1 text-rose-500/80">
                                                                    <ArrowUpRight className="h-3 w-3" /> Nuevo Cargo
                                                                </span>
                                                            ) : (
                                                                <span className="inline-flex items-center gap-1 text-emerald-500/80">
                                                                    <ArrowDownLeft className="h-3 w-3" /> Pago Recibido
                                                                </span>
                                                            )}
                                                        </div>
                                                    </td>
                                                    <td className="px-6 py-4 text-right font-medium text-rose-600/90 dark:text-rose-400/90">
                                                        {move.debit > 0 ? formatCurrency(move.debit) : "-"}
                                                    </td>
                                                    <td className="px-6 py-4 text-right font-medium text-emerald-600/90 dark:text-emerald-400/90">
                                                        {move.credit > 0 ? formatCurrency(move.credit) : "-"}
                                                    </td>
                                                    <td className="px-6 py-4 text-right font-bold text-slate-700 dark:text-slate-300">
                                                        {formatCurrency(move.balance)}
                                                    </td>
                                                    <td className="px-6 py-4 text-right">
                                                        <div className="flex justify-end gap-1">
                                                            {move.type === 'PAYMENT' && (
                                                                <>
                                                                    <button onClick={() => handleOpenModal(move.originalData)} className="p-1 hover:bg-slate-100 rounded text-slate-400 hover:text-indigo-600 transition-colors" title="Editar">
                                                                        <Pencil className="h-3.5 w-3.5" />
                                                                    </button>
                                                                    <button onClick={() => handleDeletePayment(move.originalData)} className="p-1 hover:bg-slate-100 rounded text-slate-400 hover:text-rose-600 transition-colors" title="Eliminar">
                                                                        <Trash2 className="h-3.5 w-3.5" />
                                                                    </button>
                                                                </>
                                                            )}
                                                            {move.type === 'RESERVA' && (
                                                                <Link to={`/reservas/${move.originalData.id}`} className="p-1 hover:bg-slate-100 rounded text-slate-400 hover:text-indigo-600 transition-colors inline-block" title="Ver Reserva">
                                                                    <FileText className="h-3.5 w-3.5" />
                                                                </Link>
                                                            )}
                                                        </div>
                                                    </td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>

                                {/* Mobile Cards */}
                                <div className="md:hidden divide-y divide-slate-100 dark:divide-slate-800">
                                    {displayLedger.map((move) => (
                                        <div key={`${move.type}-${move.id}`} className="p-4 space-y-2">
                                            <div className="flex justify-between items-start">
                                                <div className="text-sm text-slate-500">{formatDate(move.date)}</div>
                                                <div className="font-bold text-slate-900 dark:text-white">{formatCurrency(move.balance)}</div>
                                            </div>
                                            <div className="font-medium text-slate-800 dark:text-slate-200 leading-snug">
                                                {move.concept}
                                            </div>
                                            <div className="flex justify-between items-center pt-1">
                                                <div className={`text-sm font-semibold flex items-center gap-1 ${move.debit > 0 ? "text-rose-600" : "text-emerald-600"}`}>
                                                    {move.debit > 0 ? (
                                                        <>Cargo: {formatCurrency(move.debit)}</>
                                                    ) : (
                                                        <>Pago: {formatCurrency(move.credit)}</>
                                                    )}
                                                </div>
                                                <div className="flex gap-2">
                                                    {move.type === 'RESERVA' ? (
                                                        <Link to={`/reservas/${move.originalData.id}`} className="text-xs bg-slate-100 dark:bg-slate-800 px-2 py-1 rounded text-slate-600">
                                                            Ver Reserva
                                                        </Link>
                                                    ) : (
                                                        <button onClick={() => handleOpenModal(move.originalData)} className="text-xs bg-slate-100 dark:bg-slate-800 px-2 py-1 rounded text-slate-600">
                                                            Editar
                                                        </button>
                                                    )}
                                                </div>
                                            </div>
                                        </div>
                                    ))}
                                </div>
                            </>
                        ) : (
                            <div className="px-6 py-12 text-center text-muted-foreground">
                                <div className="mx-auto h-12 w-12 rounded-full bg-slate-100 dark:bg-slate-800 flex items-center justify-center mb-3">
                                    <FileText className="h-6 w-6 opacity-30" />
                                </div>
                                <p>No hay movimientos registrados.</p>
                            </div>
                        )}
                    </div>
                </div>

                {/* Vertical Sidebar: Invoices */}
                <div className="space-y-4">
                    <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800">
                        <div className="bg-slate-50/50 px-6 py-4 border-b border-slate-200 dark:bg-slate-800/20 dark:border-slate-800 flex items-center justify-between">
                            <h3 className="font-semibold flex items-center gap-2 text-slate-900 dark:text-white text-sm">
                                <CreditCard className="h-4 w-4 text-emerald-500" />
                                Facturación AFIP
                            </h3>
                            <span className="text-[10px] font-bold bg-emerald-100 text-emerald-700 px-2 py-0.5 rounded-full dark:bg-emerald-900/30 dark:text-emerald-400">
                                {invoices.length} docs
                            </span>
                        </div>
                        <div className="p-4 space-y-3">
                            {invoices.length > 0 ? (
                                invoices.map(inv => (
                                    <div key={inv.id} className="p-3 rounded-lg border border-slate-100 bg-slate-50/50 dark:border-slate-800 dark:bg-slate-800/30 space-y-1">
                                        <div className="flex justify-between items-start">
                                            <span className="text-[10px] font-black uppercase text-slate-400">
                                                {inv.tipoComprobante === 1 ? 'Factura A' : inv.tipoComprobante === 6 ? 'Factura B' : 'Factura C'}
                                            </span>
                                            <span className="text-xs font-bold text-slate-900 dark:text-white">
                                                {formatCurrency(inv.importeTotal)}
                                            </span>
                                        </div>
                                        <div className="text-xs font-medium text-slate-600 dark:text-slate-300">
                                            #{String(inv.puntoDeVenta).padStart(5, '0')}-{String(inv.numeroComprobante).padStart(8, '0')}
                                        </div>
                                        <div className="flex justify-between items-center pt-1 mt-1 border-t border-slate-200/50 dark:border-slate-700/50">
                                            <span className="text-[10px] text-slate-400 font-medium">
                                                {formatDate(inv.createdAt)}
                                            </span>
                                            <span className={`text-[9px] font-bold px-1.5 py-0.5 rounded ${inv.resultado === 'A' ? 'bg-emerald-100 text-emerald-700' : 'bg-rose-100 text-rose-700'}`}>
                                                {inv.resultado === 'A' ? 'Aprobada' : 'Error'}
                                            </span>
                                        </div>
                                    </div>
                                ))
                            ) : (
                                <p className="text-xs text-center text-slate-400 py-4">No hay facturas emitidas para este cliente.</p>
                            )}
                        </div>
                    </div>
                </div>
            </div>

            <CustomerPaymentModal
                isOpen={isModalOpen}
                onClose={() => setIsModalOpen(false)}
                paymentToEdit={paymentToEdit}
                customerId={id}
                availableReservas={reservas.filter(f => f.status !== "Cancelado")}
                onSave={loadAccount}
            />

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
