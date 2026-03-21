import { useEffect, useState, useCallback } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    ArrowRight,
    Calendar,
    Check,
    Edit,
    FileText,
    Loader2,
    Plus,
    Search,
    Send,
    Trash2,
    Users,
    X
} from "lucide-react";
import Swal from "sweetalert2";

const SERVICE_TYPES = ["Hotel", "Vuelo", "Transfer", "Paquete", "Excursion", "Seguro", "Otro"];
const STATUS_COLORS = {
    Borrador: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300",
    Enviada: "bg-blue-50 text-blue-700 dark:bg-blue-900/20 dark:text-blue-400",
    Aceptada: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400",
    Vencida: "bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-400",
    Rechazada: "bg-rose-50 text-rose-700 dark:bg-rose-900/20 dark:text-rose-400"
};

export default function QuotesPage() {
    const navigate = useNavigate();
    const location = useLocation();
    const [quotes, setQuotes] = useState([]);
    const [customers, setCustomers] = useState([]);
    const [loading, setLoading] = useState(true);
    const [search, setSearch] = useState("");
    const [showModal, setShowModal] = useState(false);
    const [editingQuote, setEditingQuote] = useState(null);
    const [detailQuote, setDetailQuote] = useState(null);
    const [showItemModal, setShowItemModal] = useState(false);

    const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0 })}`;

    const loadQuotes = useCallback(async () => {
        setLoading(true);
        try {
            const [quoteData, customerData] = await Promise.all([api.get("/quotes"), api.get("/customers")]);
            setQuotes(quoteData || []);
            setCustomers(customerData || []);
        } catch {
            showError("Error al cargar cotizaciones");
        } finally {
            setLoading(false);
        }
    }, []);

    const loadDetail = useCallback(async (id) => {
        try {
            const quote = await api.get(`/quotes/${id}`);
            setDetailQuote(quote);
        } catch {
            showError("Error al cargar detalle");
        }
    }, []);

    useEffect(() => {
        loadQuotes();
    }, [loadQuotes]);

    useEffect(() => {
        const openQuoteId = location.state?.openQuoteId;
        if (!openQuoteId) return;

        loadDetail(openQuoteId);
        navigate(location.pathname, { replace: true, state: {} });
    }, [loadDetail, location.pathname, location.state, navigate]);

    const filtered = quotes.filter((quote) =>
        quote.title.toLowerCase().includes(search.toLowerCase()) ||
        quote.quoteNumber.toLowerCase().includes(search.toLowerCase()) ||
        (quote.destination || "").toLowerCase().includes(search.toLowerCase()) ||
        (quote.customer?.fullName || "").toLowerCase().includes(search.toLowerCase())
    );

    const sanitizeQuote = (data) => ({
        ...data,
        customerId: data.customerId || null,
        travelStartDate: data.travelStartDate || null,
        travelEndDate: data.travelEndDate || null,
        validUntil: data.validUntil || null,
        adults: parseInt(data.adults) || 2,
        children: parseInt(data.children) || 0
    });

    const handleCreate = async (data) => {
        try {
            await api.post("/quotes", sanitizeQuote(data));
            showSuccess("Cotizacion creada");
            setShowModal(false);
            loadQuotes();
        } catch {
            showError("Error al crear cotizacion");
        }
    };

    const handleUpdate = async (data) => {
        try {
            await api.put(`/quotes/${editingQuote.id}`, sanitizeQuote(data));
            showSuccess("Cotizacion actualizada");
            setEditingQuote(null);
            setShowModal(false);
            loadQuotes();
            if (detailQuote?.id === editingQuote.id) {
                loadDetail(editingQuote.id);
            }
        } catch {
            showError("Error al actualizar");
        }
    };

    const handleDelete = async (id) => {
        const { isConfirmed } = await Swal.fire({
            title: "Eliminar cotizacion?",
            icon: "warning",
            showCancelButton: true,
            confirmButtonColor: "#ef4444"
        });

        if (!isConfirmed) return;

        try {
            await api.delete(`/quotes/${id}`);
            showSuccess("Cotizacion eliminada");
            if (detailQuote?.id === id) {
                setDetailQuote(null);
            }
            loadQuotes();
        } catch {
            showError("Error al eliminar");
        }
    };

    const handleStatusChange = async (id, status) => {
        try {
            await api.patch(`/quotes/${id}/status`, { status });
            showSuccess(`Estado: ${status}`);
            loadQuotes();
            if (detailQuote?.id === id) {
                loadDetail(id);
            }
        } catch {
            showError("Error al cambiar estado");
        }
    };

    const handleConvert = async (id) => {
        const { isConfirmed } = await Swal.fire({
            title: "Convertir a reserva?",
            text: "Se creara una reserva nueva con los datos de esta cotizacion.",
            icon: "question",
            showCancelButton: true,
            confirmButtonColor: "#4f46e5"
        });

        if (!isConfirmed) return;

        try {
            const res = await api.post(`/quotes/${id}/convert`);
            showSuccess(`Reserva creada: ID ${res.reservaId}`);
            loadQuotes();
            navigate(`/reservas/${res.reservaId}`);
        } catch (error) {
            showError(error.message || "Error al convertir");
        }
    };

    const handleAddItem = async (item) => {
        try {
            const quote = await api.post(`/quotes/${detailQuote.id}/items`, item);
            setDetailQuote(quote);
            setShowItemModal(false);
            showSuccess("Item agregado");
            loadQuotes();
        } catch {
            showError("Error al agregar item");
        }
    };

    const handleRemoveItem = async (itemId) => {
        try {
            await api.delete(`/quotes/${detailQuote.id}/items/${itemId}`);
            loadDetail(detailQuote.id);
            loadQuotes();
            showSuccess("Item eliminado");
        } catch {
            showError("Error al eliminar item");
        }
    };

    if (loading) {
        return (
            <div className="flex items-center justify-center h-[60vh]">
                <Loader2 className="w-8 h-8 animate-spin text-indigo-500" />
            </div>
        );
    }

    if (detailQuote) {
        return (
            <div className="space-y-6 pb-12">
                <div className="flex items-center gap-3">
                    <button onClick={() => setDetailQuote(null)} className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800">
                        <X className="w-5 h-5" />
                    </button>
                    <div className="flex-1">
                        <h1 className="text-xl font-black text-slate-900 dark:text-white">
                            {detailQuote.quoteNumber} - {detailQuote.title}
                        </h1>
                        <p className="text-xs text-slate-400">
                            {detailQuote.destination || "Sin destino"} | {detailQuote.adults} adultos, {detailQuote.children} menores
                        </p>
                    </div>
                    <span className={`px-3 py-1 rounded-full text-xs font-bold ${STATUS_COLORS[detailQuote.status] || STATUS_COLORS.Borrador}`}>
                        {detailQuote.status}
                    </span>
                </div>

                <div className="grid grid-cols-1 lg:grid-cols-3 gap-3">
                    <OriginCard
                        label="Cliente"
                        value={detailQuote.customer?.fullName || (detailQuote.customerId ? `Cliente #${detailQuote.customerId}` : "Sin cliente")}
                        disabled={!detailQuote.customerId}
                        onClick={() => detailQuote.customerId && navigate(`/customers/${detailQuote.customerId}/account`)}
                    />
                    <OriginCard
                        label="Lead origen"
                        value={detailQuote.lead?.fullName || (detailQuote.leadId ? `Lead #${detailQuote.leadId}` : "Sin lead")}
                        disabled={!detailQuote.leadId}
                        onClick={() => detailQuote.leadId && navigate("/crm", { state: { openLeadId: detailQuote.leadId } })}
                    />
                    <OriginCard
                        label="Reserva"
                        value={detailQuote.convertedReservaId ? `Reserva #${detailQuote.convertedReservaId}` : "Todavia no convertida"}
                        disabled={!detailQuote.convertedReservaId}
                        onClick={() => detailQuote.convertedReservaId && navigate(`/reservas/${detailQuote.convertedReservaId}`)}
                    />
                </div>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                    <SummaryCard label="Costo total" value={fmt(detailQuote.totalCost)} />
                    <SummaryCard label="Venta total" value={fmt(detailQuote.totalSale)} valueClassName="text-indigo-600" />
                    <SummaryCard
                        label="Margen"
                        value={fmt(detailQuote.grossMargin)}
                        valueClassName={detailQuote.grossMargin >= 0 ? "text-emerald-600" : "text-rose-600"}
                    />
                </div>

                <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden">
                    <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center justify-between">
                        <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider">Servicios</h3>
                        <button
                            onClick={() => setShowItemModal(true)}
                            className="flex items-center gap-1.5 px-3 py-1.5 bg-indigo-600 text-white rounded-lg text-xs font-bold hover:bg-indigo-700 transition-colors"
                        >
                            <Plus className="w-3 h-3" /> Agregar
                        </button>
                    </div>
                    <div className="divide-y divide-slate-50 dark:divide-slate-800/50">
                        {(!detailQuote.items || detailQuote.items.length === 0) ? (
                            <div className="px-6 py-8 text-center text-sm text-slate-400">
                                No hay servicios. Agrega items para armar la cotizacion.
                            </div>
                        ) : detailQuote.items.map((item) => (
                            <div key={item.id} className="px-6 py-3 flex items-center gap-4 hover:bg-slate-50/50 dark:hover:bg-slate-800/20">
                                <div className="w-8 h-8 rounded-lg bg-indigo-50 dark:bg-indigo-900/20 text-indigo-600 flex items-center justify-center text-[10px] font-black">
                                    {item.serviceType?.substring(0, 3).toUpperCase()}
                                </div>
                                <div className="flex-1 min-w-0">
                                    <div className="text-sm font-bold text-slate-900 dark:text-white truncate">{item.description}</div>
                                    <div className="text-[10px] text-slate-400">
                                        {item.quantity}x | Costo: {fmt(item.unitCost)} | Venta: {fmt(item.unitPrice)}
                                    </div>
                                </div>
                                <span className="text-sm font-black text-slate-900 dark:text-white">{fmt(item.unitPrice * item.quantity)}</span>
                                <button onClick={() => handleRemoveItem(item.id)} className="p-1.5 rounded hover:bg-rose-50 dark:hover:bg-rose-900/20 text-rose-500">
                                    <Trash2 className="w-3.5 h-3.5" />
                                </button>
                            </div>
                        ))}
                    </div>
                </div>

                <div className="flex gap-3 flex-wrap">
                    {detailQuote.status === "Borrador" && (
                        <button onClick={() => handleStatusChange(detailQuote.id, "Enviada")} className="flex items-center gap-2 px-4 py-2.5 bg-blue-600 text-white rounded-xl text-sm font-bold hover:bg-blue-700">
                            <Send className="w-4 h-4" /> Marcar como enviada
                        </button>
                    )}
                    {detailQuote.status === "Enviada" && (
                        <>
                            <button onClick={() => handleStatusChange(detailQuote.id, "Aceptada")} className="flex items-center gap-2 px-4 py-2.5 bg-emerald-600 text-white rounded-xl text-sm font-bold hover:bg-emerald-700">
                                <Check className="w-4 h-4" /> Aceptada
                            </button>
                            <button onClick={() => handleStatusChange(detailQuote.id, "Rechazada")} className="flex items-center gap-2 px-4 py-2.5 bg-rose-600 text-white rounded-xl text-sm font-bold hover:bg-rose-700">
                                <X className="w-4 h-4" /> Rechazada
                            </button>
                        </>
                    )}
                    {detailQuote.status === "Aceptada" && !detailQuote.convertedReservaId && (
                        <button onClick={() => handleConvert(detailQuote.id)} className="flex items-center gap-2 px-4 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold hover:bg-indigo-700">
                            <ArrowRight className="w-4 h-4" /> Convertir a reserva
                        </button>
                    )}
                    {detailQuote.convertedReservaId && (
                        <button
                            onClick={() => navigate(`/reservas/${detailQuote.convertedReservaId}`)}
                            className="flex items-center gap-2 px-4 py-2.5 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400 rounded-xl text-sm font-bold"
                        >
                            <Check className="w-4 h-4" /> Abrir reserva #{detailQuote.convertedReservaId}
                        </button>
                    )}
                </div>

                {showItemModal && <ItemModal serviceTypes={SERVICE_TYPES} onSave={handleAddItem} onClose={() => setShowItemModal(false)} />}
            </div>
        );
    }

    return (
        <div className="space-y-6 pb-12">
            <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-black text-slate-900 dark:text-white tracking-tight">Cotizaciones</h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">Motor de cotizacion inteligente</p>
                </div>
                <button onClick={() => { setEditingQuote(null); setShowModal(true); }} className="flex items-center gap-2 px-4 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold shadow-lg hover:bg-indigo-700 transition-all">
                    <Plus className="w-4 h-4" /> Nueva cotizacion
                </button>
            </div>

            <div className="relative">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
                <input
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                    placeholder="Buscar por titulo, numero, destino o cliente..."
                    className="w-full pl-10 pr-4 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm"
                />
            </div>

            <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
                <div className="overflow-x-auto">
                    <table className="min-w-full text-left border-collapse">
                        <thead>
                            <tr className="border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-800/30">
                                <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Cotizacion</th>
                                <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Cliente / titulo</th>
                                <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Destino / pax</th>
                                <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Estado</th>
                                <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider text-right">Monto total</th>
                                <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider text-right pr-6">Acciones</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-50 dark:divide-slate-800/50">
                            {filtered.length === 0 ? (
                                <tr>
                                    <td colSpan="6" className="px-4 py-12 text-center">
                                        <div className="flex flex-col items-center justify-center text-slate-400 dark:text-slate-600">
                                            <FileText className="h-10 w-10 mb-3 opacity-20" />
                                            <p className="text-sm font-medium">No se encontraron cotizaciones</p>
                                        </div>
                                    </td>
                                </tr>
                            ) : (
                                filtered.map((quote) => (
                                    <tr key={quote.id} onClick={() => loadDetail(quote.id)} className="group hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors cursor-pointer">
                                        <td className="px-4 py-3 align-middle whitespace-nowrap">
                                            <span className="text-[11px] font-black text-indigo-500 bg-indigo-50 dark:bg-indigo-900/20 px-2 py-1 rounded-md">
                                                {quote.quoteNumber}
                                            </span>
                                        </td>
                                        <td className="px-4 py-3 align-middle">
                                            <div className="font-semibold text-sm text-slate-900 dark:text-white truncate">{quote.title}</div>
                                            <div className="text-xs text-slate-400 truncate">
                                                {quote.customer?.fullName || "Sin cliente"}
                                                {quote.leadId ? ` · Lead #${quote.leadId}` : ""}
                                            </div>
                                            <div className="text-xs text-slate-400 flex items-center gap-1 mt-1">
                                                <Calendar className="w-3 h-3" />
                                                {quote.createdAt ? new Date(quote.createdAt).toLocaleDateString() : "-"}
                                            </div>
                                        </td>
                                        <td className="px-4 py-3 align-middle">
                                            <div className="text-sm text-slate-600 dark:text-slate-300">
                                                {quote.destination || <span className="text-slate-400 italic">Sin destino</span>}
                                            </div>
                                            <div className="text-xs text-slate-400 flex items-center gap-1">
                                                <Users className="w-3 h-3" />
                                                {quote.adults + quote.children} pax
                                            </div>
                                        </td>
                                        <td className="px-4 py-3 align-middle whitespace-nowrap">
                                            <span className={`px-2 py-1 rounded-full text-[10px] font-bold ${STATUS_COLORS[quote.status] || STATUS_COLORS.Borrador}`}>
                                                {quote.status}
                                            </span>
                                        </td>
                                        <td className="px-4 py-3 align-middle text-right whitespace-nowrap">
                                            <div className="text-sm font-black text-indigo-600 dark:text-indigo-400">{fmt(quote.totalSale)}</div>
                                            <div className="text-[10px] text-slate-400">Neto: {fmt(quote.totalCost)}</div>
                                        </td>
                                        <td className="px-4 py-3 align-middle text-right pr-4 whitespace-nowrap">
                                            <div className="flex justify-end gap-1 transition-opacity">
                                                <button
                                                    onClick={(event) => {
                                                        event.stopPropagation();
                                                        setEditingQuote(quote);
                                                        setShowModal(true);
                                                    }}
                                                    className="p-1.5 text-slate-400 hover:text-indigo-600 hover:bg-slate-100 dark:hover:bg-slate-800 rounded"
                                                >
                                                    <Edit className="w-4 h-4" />
                                                </button>
                                                <button
                                                    onClick={(event) => {
                                                        event.stopPropagation();
                                                        handleDelete(quote.id);
                                                    }}
                                                    className="p-1.5 text-slate-400 hover:text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20 rounded"
                                                >
                                                    <Trash2 className="w-4 h-4" />
                                                </button>
                                            </div>
                                        </td>
                                    </tr>
                                ))
                            )}
                        </tbody>
                    </table>
                </div>
            </div>

            {showModal && (
                <QuoteFormModal
                    customers={customers}
                    initial={editingQuote}
                    onSave={editingQuote ? handleUpdate : handleCreate}
                    onClose={() => {
                        setShowModal(false);
                        setEditingQuote(null);
                    }}
                />
            )}
        </div>
    );
}

function OriginCard({ label, value, disabled, onClick }) {
    return (
        <button
            type="button"
            onClick={onClick}
            disabled={disabled}
            className="text-left rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 p-4 disabled:opacity-50 disabled:cursor-not-allowed"
        >
            <div className="text-[10px] font-black uppercase tracking-widest text-slate-400">{label}</div>
            <div className="mt-2 text-sm font-bold text-slate-900 dark:text-white">{value}</div>
        </button>
    );
}

function SummaryCard({ label, value, valueClassName = "text-slate-900 dark:text-white" }) {
    return (
        <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 p-4 text-center">
            <div className="text-[10px] font-bold text-slate-400 uppercase">{label}</div>
            <div className={`text-lg font-black ${valueClassName}`}>{value}</div>
        </div>
    );
}

function QuoteFormModal({ customers, initial, onSave, onClose }) {
    const [form, setForm] = useState({
        title: initial?.title || "",
        description: initial?.description || "",
        customerId: initial?.customerId || "",
        destination: initial?.destination || "",
        adults: initial?.adults || 2,
        children: initial?.children || 0,
        travelStartDate: initial?.travelStartDate?.substring(0, 10) || "",
        travelEndDate: initial?.travelEndDate?.substring(0, 10) || "",
        validUntil: initial?.validUntil?.substring(0, 10) || "",
        notes: initial?.notes || ""
    });

    const set = (key, value) => setForm((previous) => ({ ...previous, [key]: value }));

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-2xl w-full max-w-lg max-h-[90vh] overflow-y-auto p-6 space-y-4" onClick={(event) => event.stopPropagation()}>
                <h2 className="text-lg font-black text-slate-900 dark:text-white">{initial ? "Editar cotizacion" : "Nueva cotizacion"}</h2>
                <input value={form.title} onChange={(event) => set("title", event.target.value)} placeholder="Titulo *" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <select value={form.customerId} onChange={(event) => set("customerId", event.target.value ? parseInt(event.target.value) : null)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm">
                    <option value="">Cliente (opcional)</option>
                    {customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.fullName}</option>)}
                </select>
                <input value={form.destination} onChange={(event) => set("destination", event.target.value)} placeholder="Destino" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="grid grid-cols-2 gap-3">
                    <div>
                        <label className="text-[10px] font-bold text-slate-400 uppercase">Adultos</label>
                        <input type="number" value={form.adults} onChange={(event) => set("adults", parseInt(event.target.value) || 0)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                    </div>
                    <div>
                        <label className="text-[10px] font-bold text-slate-400 uppercase">Menores</label>
                        <input type="number" value={form.children} onChange={(event) => set("children", parseInt(event.target.value) || 0)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                    </div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                    <div>
                        <label className="text-[10px] font-bold text-slate-400 uppercase">Salida</label>
                        <input type="date" value={form.travelStartDate} onChange={(event) => set("travelStartDate", event.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                    </div>
                    <div>
                        <label className="text-[10px] font-bold text-slate-400 uppercase">Regreso</label>
                        <input type="date" value={form.travelEndDate} onChange={(event) => set("travelEndDate", event.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                    </div>
                </div>
                <div>
                    <label className="text-[10px] font-bold text-slate-400 uppercase">Valida hasta</label>
                    <input type="date" value={form.validUntil} onChange={(event) => set("validUntil", event.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                </div>
                <textarea value={form.notes} onChange={(event) => set("notes", event.target.value)} placeholder="Notas" rows={2} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="flex gap-3 justify-end pt-2">
                    <button onClick={onClose} className="px-4 py-2 rounded-lg text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.title} className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-bold hover:bg-indigo-700 disabled:opacity-40">Guardar</button>
                </div>
            </div>
        </div>
    );
}

function ItemModal({ serviceTypes, onSave, onClose }) {
    const [form, setForm] = useState({ serviceType: "Hotel", description: "", quantity: 1, unitCost: 0, unitPrice: 0, markupPercent: 20, notes: "", rateId: null });
    const [ratesResults, setRatesResults] = useState([]);
    const [isSearching, setIsSearching] = useState(false);

    const set = (key, value) => setForm((previous) => ({ ...previous, [key]: value }));

    const handleMarkupCalc = () => {
        if (form.unitCost > 0 && form.markupPercent > 0) {
            set("unitPrice", Math.round(form.unitCost * (1 + form.markupPercent / 100)));
        }
    };

    useEffect(() => {
        const fetchRates = async () => {
            if (form.description.length < 2 && !form.serviceType) {
                setRatesResults([]);
                return;
            }

            setIsSearching(true);
            try {
                const res = await api.get(`/rates/search?serviceType=${form.serviceType}&query=${form.description}`);
                setRatesResults(res || []);
            } catch (error) {
                console.error(error);
                setRatesResults([]);
            } finally {
                setIsSearching(false);
            }
        };

        const timeoutId = setTimeout(fetchRates, 300);
        return () => clearTimeout(timeoutId);
    }, [form.serviceType, form.description]);

    const selectRate = (rate) => {
        setForm((previous) => ({
            ...previous,
            description: rate.productName || rate.hotelName || rate.description || "Servicio seleccionado",
            unitCost: rate.netCost || 0,
            unitPrice: rate.salePrice || 0,
            rateId: rate.id,
            markupPercent: rate.netCost ? Math.round(((rate.salePrice - rate.netCost) / rate.netCost) * 100) : 0
        }));
        setRatesResults([]);
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-2xl w-full max-w-md p-6 space-y-4 shadow-xl border border-slate-200 dark:border-slate-800" onClick={(event) => event.stopPropagation()}>
                <h2 className="text-lg font-black text-slate-900 dark:text-white pb-2 border-b border-slate-100 dark:border-slate-800">Agregar servicio a cotizacion</h2>
                <div>
                    <label className="text-[10px] font-bold text-slate-400 uppercase tracking-wider mb-1 block">Tipo de servicio</label>
                    <select value={form.serviceType} onChange={(event) => { set("serviceType", event.target.value); set("rateId", null); }} className="w-full px-4 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-slate-50 dark:bg-slate-800 text-sm font-medium focus:outline-none focus:ring-2 focus:ring-indigo-500/50">
                        {serviceTypes.map((type) => <option key={type} value={type}>{type}</option>)}
                    </select>
                </div>

                <div className="relative">
                    <label className="text-[10px] font-bold text-slate-400 uppercase tracking-wider mb-1 block">Descripcion o nombre de tarifa</label>
                    <div className="relative">
                        <input value={form.description} onChange={(event) => { set("description", event.target.value); set("rateId", null); }} placeholder="Ej: Hotel Hilton base doble" className="w-full pl-4 pr-10 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm font-medium focus:outline-none focus:ring-2 focus:ring-indigo-500/50" />
                        {isSearching && <Loader2 className="absolute right-3 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400 animate-spin" />}
                    </div>

                    {ratesResults.length > 0 && !form.rateId && (
                        <div className="absolute z-10 w-full mt-1 bg-white dark:bg-slate-800 border border-slate-200 dark:border-slate-700 rounded-xl shadow-lg max-h-48 overflow-y-auto divide-y divide-slate-100 dark:divide-slate-700">
                            {ratesResults.map((rate) => (
                                <div key={rate.id} onClick={() => selectRate(rate)} className="p-3 cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-700/50">
                                    <div className="font-bold text-sm text-slate-900 dark:text-white">{rate.productName || rate.hotelName || rate.description}</div>
                                    <div className="flex justify-between mt-1 text-xs">
                                        <span className="text-slate-500">{rate.supplierName || "Tarifario"}</span>
                                        <span className="font-bold text-indigo-600 bg-indigo-50 px-1.5 py-0.5 rounded">${rate.salePrice}</span>
                                    </div>
                                </div>
                            ))}
                        </div>
                    )}
                </div>

                {form.rateId && (
                    <div className="bg-emerald-50 dark:bg-emerald-900/10 border border-emerald-200 dark:border-emerald-800/30 rounded-lg p-3 flex justify-between items-center">
                        <div className="flex items-center gap-2 text-emerald-700 dark:text-emerald-400 text-sm font-medium">
                            <Check className="w-4 h-4" /> Vinculado a tarifario (#{form.rateId})
                        </div>
                        <button onClick={() => set("rateId", null)} className="text-xs text-emerald-600 hover:text-emerald-800 underline">Desvincular</button>
                    </div>
                )}

                <div className="grid grid-cols-3 gap-3">
                    <div>
                        <label className="text-[10px] font-bold text-slate-400 uppercase tracking-wider mb-1 block">Cant</label>
                        <input type="number" min="1" value={form.quantity} onChange={(event) => set("quantity", parseInt(event.target.value) || 1)} className="w-full px-3 py-2.5 rounded-lg border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm font-bold text-center" />
                    </div>
                    <div>
                        <label className="text-[10px] font-bold text-slate-400 uppercase tracking-wider mb-1 block">Costo ($)</label>
                        <input type="number" min="0" value={form.unitCost} onChange={(event) => set("unitCost", parseFloat(event.target.value) || 0)} onBlur={handleMarkupCalc} className="w-full px-3 py-2.5 rounded-lg border border-slate-200 dark:border-slate-700 bg-slate-50 dark:bg-slate-800 text-sm font-bold text-right" />
                    </div>
                    <div>
                        <label className="text-[10px] font-bold text-slate-400 uppercase tracking-wider mb-1 block">Markup (%)</label>
                        <input type="number" min="0" value={form.markupPercent} onChange={(event) => set("markupPercent", parseFloat(event.target.value) || 0)} onBlur={handleMarkupCalc} className="w-full px-3 py-2.5 rounded-lg border border-slate-200 dark:border-slate-700 bg-slate-50 dark:bg-slate-800 text-sm font-bold text-center" />
                    </div>
                </div>

                <div>
                    <label className="text-[10px] font-bold text-indigo-500 uppercase tracking-wider mb-1 block">Precio venta ($)</label>
                    <input type="number" min="0" value={form.unitPrice} onChange={(event) => set("unitPrice", parseFloat(event.target.value) || 0)} className="w-full px-4 py-3 rounded-xl border-2 border-indigo-200 dark:border-indigo-800 bg-white dark:bg-slate-900 text-lg font-black text-indigo-700 dark:text-indigo-400 text-right focus:border-indigo-500 focus:outline-none" />
                </div>

                <div className="flex gap-3 justify-end pt-4 mt-2 border-t border-slate-100 dark:border-slate-800">
                    <button onClick={onClose} className="px-5 py-2.5 rounded-xl text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.description} className="px-6 py-2.5 bg-indigo-600 text-white shadow-lg shadow-indigo-500/30 rounded-xl text-sm font-bold hover:bg-indigo-700 disabled:opacity-50 disabled:shadow-none transition-all flex items-center gap-2">
                        <Plus className="w-4 h-4" /> Agregar item
                    </button>
                </div>
            </div>
        </div>
    );
}
