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
    Eye,
    History
} from "lucide-react";
import CustomerPaymentModal from "../../../components/CustomerPaymentModal";
import Swal from "sweetalert2";
import { Button } from "../../../components/ui/button";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { getPublicId, getRelatedPublicId } from "../../../lib/publicIds";

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

const formatInvoiceNumber = (invoice) => {
    if (!invoice) return "";
    return `${String(invoice.puntoDeVenta ?? 0).padStart(5, "0")}-${String(invoice.numeroComprobante ?? 0).padStart(8, "0")}`;
};

const formatInvoiceType = (invoice) => {
    if (!invoice) return "";

    switch (invoice.tipoComprobante) {
        case 1:
            return "Factura A";
        case 6:
            return "Factura B";
        case 11:
            return "Factura C";
        default:
            return `Tipo ${invoice.tipoComprobante}`;
    }
};

const escapeHtml = (value) => String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");

const renderInvoiceTab = (previewWindow, { title, body }) => {
    if (!previewWindow || previewWindow.closed) {
        return;
    }

    previewWindow.document.open();
    previewWindow.document.write(`<!doctype html>
<html lang="es">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>${escapeHtml(title)}</title>
    <style>
        :root {
            color-scheme: light;
            font-family: Inter, system-ui, sans-serif;
            background: #e2e8f0;
            color: #0f172a;
        }
        * {
            box-sizing: border-box;
        }
        body {
            margin: 0;
            min-height: 100vh;
            background: linear-gradient(180deg, #f8fafc 0%, #e2e8f0 100%);
        }
        .shell {
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }
        .header {
            padding: 16px 20px;
            border-bottom: 1px solid #cbd5e1;
            background: rgba(255, 255, 255, 0.96);
            backdrop-filter: blur(10px);
        }
        .eyebrow {
            margin: 0 0 6px;
            font-size: 11px;
            font-weight: 800;
            letter-spacing: 0.14em;
            text-transform: uppercase;
            color: #6366f1;
        }
        .title {
            margin: 0;
            font-size: 20px;
            font-weight: 700;
        }
        .subtitle {
            margin: 6px 0 0;
            font-size: 14px;
            color: #475569;
        }
        .content {
            flex: 1;
            padding: 20px;
        }
        .panel {
            height: calc(100vh - 117px);
            border: 1px solid #cbd5e1;
            border-radius: 18px;
            overflow: hidden;
            background: #ffffff;
            box-shadow: 0 20px 50px rgba(15, 23, 42, 0.15);
        }
        .state {
            height: 100%;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 12px;
            padding: 24px;
            text-align: center;
        }
        .state-title {
            margin: 0;
            font-size: 18px;
            font-weight: 700;
        }
        .state-text {
            margin: 0;
            max-width: 480px;
            color: #475569;
            line-height: 1.5;
        }
        .spinner {
            width: 42px;
            height: 42px;
            border: 4px solid #cbd5e1;
            border-top-color: #4f46e5;
            border-radius: 999px;
            animation: spin 0.9s linear infinite;
        }
        iframe {
            width: 100%;
            height: 100%;
            border: 0;
            background: #ffffff;
        }
        @keyframes spin {
            to {
                transform: rotate(360deg);
            }
        }
        @media (max-width: 768px) {
            .header {
                padding: 14px 16px;
            }
            .content {
                padding: 12px;
            }
            .panel {
                height: calc(100vh - 101px);
                border-radius: 14px;
            }
        }
    </style>
</head>
<body>
    ${body}
</body>
</html>`);
    previewWindow.document.close();
};

export default function CustomerAccountPage() {
    const { publicId } = useParams();
    const navigate = useNavigate();
    const [data, setData] = useState(null);
    const [loading, setLoading] = useState(true);
    const [isModalOpen, setIsModalOpen] = useState(false);
    const [paymentToEdit, setPaymentToEdit] = useState(null);
    const [activeTab, setActiveTab] = useState("ledger");

    useEffect(() => {
        loadAccount();
    }, [publicId]);

    const loadAccount = async () => {
        setLoading(true);
        try {
            const result = await api.get(`/customers/${publicId}/account`);
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

    const handleOpenInvoicePreview = async (invoice) => {
        const previewWindow = window.open("", "_blank");

        if (!previewWindow) {
            showError("El navegador bloqueo la apertura de la factura.");
            return;
        }

        previewWindow.opener = null;

        const invoiceTitle = `${formatInvoiceType(invoice)} ${formatInvoiceNumber(invoice)}`;

        renderInvoiceTab(previewWindow, {
            title: invoiceTitle,
            body: `
                <div class="shell">
                    <div class="header">
                        <p class="eyebrow">Facturacion AFIP</p>
                        <h1 class="title">${escapeHtml(invoiceTitle)}</h1>
                        <p class="subtitle">Preparando la factura para mostrarla en esta pestaña.</p>
                    </div>
                    <div class="content">
                        <div class="panel">
                            <div class="state">
                                <div class="spinner"></div>
                                <p class="state-title">Cargando factura...</p>
                                <p class="state-text">Estamos obteniendo el PDF autenticado para abrirlo fuera de la cuenta corriente.</p>
                            </div>
                        </div>
                    </div>
                </div>
            `
        });

        try {
            const blob = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });

            if (!(blob instanceof Blob) || blob.size === 0) {
                throw new Error("La factura no devolvio un PDF valido.");
            }

            const pdfUrl = URL.createObjectURL(blob);

            if (previewWindow.closed) {
                URL.revokeObjectURL(pdfUrl);
                return;
            }

            const releaseTimer = window.setInterval(() => {
                if (previewWindow.closed) {
                    URL.revokeObjectURL(pdfUrl);
                    window.clearInterval(releaseTimer);
                }
            }, 1000);

            renderInvoiceTab(previewWindow, {
                title: invoiceTitle,
                body: `
                    <div class="shell">
                        <div class="header">
                            <p class="eyebrow">Facturacion AFIP</p>
                            <h1 class="title">${escapeHtml(invoiceTitle)}</h1>
                            <p class="subtitle">Vista de la factura emitida en AFIP.</p>
                        </div>
                        <div class="content">
                            <div class="panel">
                                <iframe src="${pdfUrl}" title="${escapeHtml(invoiceTitle)}"></iframe>
                            </div>
                        </div>
                    </div>
                `
            });
        } catch (error) {
            renderInvoiceTab(previewWindow, {
                title: invoiceTitle,
                body: `
                    <div class="shell">
                        <div class="header">
                            <p class="eyebrow">Facturacion AFIP</p>
                            <h1 class="title">${escapeHtml(invoiceTitle)}</h1>
                            <p class="subtitle">No fue posible abrir la factura.</p>
                        </div>
                        <div class="content">
                            <div class="panel">
                                <div class="state">
                                    <p class="state-title">No se pudo cargar la factura</p>
                                    <p class="state-text">${escapeHtml(error?.message || "El servidor no devolvio un PDF valido.")}</p>
                                </div>
                            </div>
                        </div>
                    </div>
                `
            });
        }
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
                await api.delete(`/reservas/${getRelatedPublicId(payment, "reservaPublicId", "reservaId")}/payments/${getPublicId(payment)}`);
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

    // --- Ledger Calculation (Debits & Credits only) ---
    const debitMovements = (reservas || []).map(r => ({
        id: getPublicId(r),
        trackId: `res-${getPublicId(r)}`,
        type: 'RESERVA',
        date: r.createdAt || r.startDate,
        concept: `Reserva ${r.numeroReserva} - ${r.name}`,
        debit: r.totalSale,
        credit: 0,
        originalData: r
    }));

    const creditMovements = (payments || []).map(p => ({
        id: getPublicId(p),
        trackId: `pay-${getPublicId(p)}`,
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
                                                    <div className="flex justify-end gap-1 transition-opacity">
                                                        {move.type === 'PAYMENT' && (
                                                            <button onClick={() => handleDeletePayment(move.originalData)} className="p-1.5 hover:bg-rose-50 rounded-lg text-rose-400 hover:text-rose-600 transition-colors" title="Anular Pago">
                                                                <Trash2 className="h-4 w-4" />
                                                            </button>
                                                        )}
                                                        {move.type === 'RESERVA' && (
                                                            <Link to={`/reservas/${getPublicId(move.originalData)}`} className="p-1.5 hover:bg-indigo-50 rounded-lg text-indigo-400 hover:text-indigo-600 transition-colors inline-block" title="Ver Detalles">
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
                                            <tr key={getPublicId(inv)} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors group">
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
                                                    <button
                                                        type="button"
                                                        onClick={() => handleOpenInvoicePreview(inv)}
                                                        className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 dark:hover:text-white"
                                                        title="Ver factura"
                                                    >
                                                        <Eye className="h-4 w-4" />
                                                        Ver
                                                    </button>
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
                customerId={publicId}
                availableReservas={reservas.filter(f => f.status !== "Cancelado")}
                onSave={loadAccount}
            />
        </div>
    );
}
