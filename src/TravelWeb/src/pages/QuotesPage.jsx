import { useEffect, useState, useCallback } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    Plus, FileText, Trash2, Eye, ArrowRight, Loader2, Search,
    DollarSign, Calendar, MapPin, Users, Edit, Check, X, Send
} from "lucide-react";
import Swal from "sweetalert2";

const SERVICE_TYPES = ["Hotel", "Vuelo", "Transfer", "Paquete", "Excursión", "Seguro", "Otro"];
const STATUS_COLORS = {
    Borrador: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300",
    Enviada: "bg-blue-50 text-blue-700 dark:bg-blue-900/20 dark:text-blue-400",
    Aceptada: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400",
    Vencida: "bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-400",
    Rechazada: "bg-rose-50 text-rose-700 dark:bg-rose-900/20 dark:text-rose-400",
};

export default function QuotesPage() {
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
            const [q, c] = await Promise.all([api.get("/quotes"), api.get("/customers")]);
            setQuotes(q || []);
            setCustomers(c || []);
        } catch { showError("Error al cargar cotizaciones"); }
        finally { setLoading(false); }
    }, []);

    useEffect(() => { loadQuotes(); }, [loadQuotes]);

    const filtered = quotes.filter(q =>
        q.title.toLowerCase().includes(search.toLowerCase()) ||
        q.quoteNumber.toLowerCase().includes(search.toLowerCase()) ||
        (q.destination || "").toLowerCase().includes(search.toLowerCase())
    );

    const sanitizeQuote = (data) => ({
        ...data,
        customerId: data.customerId || null,
        travelStartDate: data.travelStartDate || null,
        travelEndDate: data.travelEndDate || null,
        validUntil: data.validUntil || null,
        adults: parseInt(data.adults) || 2,
        children: parseInt(data.children) || 0,
    });

    const handleCreate = async (data) => {
        try {
            await api.post("/quotes", sanitizeQuote(data));
            showSuccess("Cotización creada");
            setShowModal(false);
            loadQuotes();
        } catch { showError("Error al crear cotización"); }
    };

    const handleUpdate = async (data) => {
        try {
            await api.put(`/quotes/${editingQuote.id}`, sanitizeQuote(data));
            showSuccess("Cotización actualizada");
            setEditingQuote(null);
            setShowModal(false);
            loadQuotes();
        } catch { showError("Error al actualizar"); }
    };

    const handleDelete = async (id) => {
        const { isConfirmed } = await Swal.fire({ title: "¿Eliminar cotización?", icon: "warning", showCancelButton: true, confirmButtonColor: "#ef4444" });
        if (!isConfirmed) return;
        try { await api.delete(`/quotes/${id}`); showSuccess("Cotización eliminada"); loadQuotes(); }
        catch { showError("Error al eliminar"); }
    };

    const handleStatusChange = async (id, status) => {
        try { await api.patch(`/quotes/${id}/status`, { status }); showSuccess(`Estado: ${status}`); loadQuotes(); if (detailQuote?.id === id) loadDetail(id); }
        catch { showError("Error al cambiar estado"); }
    };

    const handleConvert = async (id) => {
        const { isConfirmed } = await Swal.fire({ title: "¿Convertir a expediente?", text: "Se creará un expediente nuevo con los datos de esta cotización.", icon: "question", showCancelButton: true, confirmButtonColor: "#4f46e5" });
        if (!isConfirmed) return;
        try {
            const res = await api.post(`/quotes/${id}/convert`);
            showSuccess(`Expediente creado: ID ${res.fileId}`);
            loadQuotes();
        } catch (e) { showError(e.message || "Error al convertir"); }
    };

    const loadDetail = async (id) => {
        try { const q = await api.get(`/quotes/${id}`); setDetailQuote(q); }
        catch { showError("Error al cargar detalle"); }
    };

    const handleAddItem = async (item) => {
        try {
            const q = await api.post(`/quotes/${detailQuote.id}/items`, item);
            setDetailQuote(q);
            setShowItemModal(false);
            showSuccess("Item agregado");
        } catch { showError("Error al agregar item"); }
    };

    const handleRemoveItem = async (itemId) => {
        try {
            await api.delete(`/quotes/${detailQuote.id}/items/${itemId}`);
            loadDetail(detailQuote.id);
            showSuccess("Item eliminado");
        } catch { showError("Error al eliminar item"); }
    };

    if (loading) {
        return (
            <div className="flex items-center justify-center h-[60vh]">
                <Loader2 className="w-8 h-8 animate-spin text-indigo-500" />
            </div>
        );
    }

    // DETAIL VIEW
    if (detailQuote) {
        return (
            <div className="space-y-6 pb-12">
                <div className="flex items-center gap-3">
                    <button onClick={() => setDetailQuote(null)} className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800">
                        <X className="w-5 h-5" />
                    </button>
                    <div className="flex-1">
                        <h1 className="text-xl font-black text-slate-900 dark:text-white">{detailQuote.quoteNumber} — {detailQuote.title}</h1>
                        <p className="text-xs text-slate-400">{detailQuote.destination || "Sin destino"} | {detailQuote.adults} adultos, {detailQuote.children} menores</p>
                    </div>
                    <span className={`px-3 py-1 rounded-full text-xs font-bold ${STATUS_COLORS[detailQuote.status] || STATUS_COLORS.Borrador}`}>{detailQuote.status}</span>
                </div>

                {/* Financial Summary */}
                <div className="grid grid-cols-3 gap-4">
                    <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 p-4 text-center">
                        <div className="text-[10px] font-bold text-slate-400 uppercase">Costo Total</div>
                        <div className="text-lg font-black text-slate-900 dark:text-white">{fmt(detailQuote.totalCost)}</div>
                    </div>
                    <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 p-4 text-center">
                        <div className="text-[10px] font-bold text-slate-400 uppercase">Venta Total</div>
                        <div className="text-lg font-black text-indigo-600">{fmt(detailQuote.totalSale)}</div>
                    </div>
                    <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 p-4 text-center">
                        <div className="text-[10px] font-bold text-slate-400 uppercase">Margen</div>
                        <div className={`text-lg font-black ${detailQuote.grossMargin >= 0 ? "text-emerald-600" : "text-rose-600"}`}>{fmt(detailQuote.grossMargin)}</div>
                    </div>
                </div>

                {/* Items */}
                <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden">
                    <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center justify-between">
                        <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider">Servicios</h3>
                        <button onClick={() => setShowItemModal(true)} className="flex items-center gap-1.5 px-3 py-1.5 bg-indigo-600 text-white rounded-lg text-xs font-bold hover:bg-indigo-700 transition-colors">
                            <Plus className="w-3 h-3" /> Agregar
                        </button>
                    </div>
                    <div className="divide-y divide-slate-50 dark:divide-slate-800/50">
                        {(!detailQuote.items || detailQuote.items.length === 0) ? (
                            <div className="px-6 py-8 text-center text-sm text-slate-400">No hay servicios. Agregá ítems para armar la cotización.</div>
                        ) : detailQuote.items.map(item => (
                            <div key={item.id} className="px-6 py-3 flex items-center gap-4 hover:bg-slate-50/50 dark:hover:bg-slate-800/20">
                                <div className="w-8 h-8 rounded-lg bg-indigo-50 dark:bg-indigo-900/20 text-indigo-600 flex items-center justify-center text-[10px] font-black">{item.serviceType?.substring(0, 3).toUpperCase()}</div>
                                <div className="flex-1 min-w-0">
                                    <div className="text-sm font-bold text-slate-900 dark:text-white truncate">{item.description}</div>
                                    <div className="text-[10px] text-slate-400">{item.quantity}x | Costo: {fmt(item.unitCost)} | Venta: {fmt(item.unitPrice)}</div>
                                </div>
                                <span className="text-sm font-black text-slate-900 dark:text-white">{fmt(item.unitPrice * item.quantity)}</span>
                                <button onClick={() => handleRemoveItem(item.id)} className="p-1.5 rounded hover:bg-rose-50 dark:hover:bg-rose-900/20 text-rose-500"><Trash2 className="w-3.5 h-3.5" /></button>
                            </div>
                        ))}
                    </div>
                </div>

                {/* Actions */}
                <div className="flex gap-3 flex-wrap">
                    {detailQuote.status === "Borrador" && (
                        <button onClick={() => handleStatusChange(detailQuote.id, "Enviada")} className="flex items-center gap-2 px-4 py-2.5 bg-blue-600 text-white rounded-xl text-sm font-bold hover:bg-blue-700"><Send className="w-4 h-4" /> Marcar como Enviada</button>
                    )}
                    {detailQuote.status === "Enviada" && (
                        <>
                            <button onClick={() => handleStatusChange(detailQuote.id, "Aceptada")} className="flex items-center gap-2 px-4 py-2.5 bg-emerald-600 text-white rounded-xl text-sm font-bold hover:bg-emerald-700"><Check className="w-4 h-4" /> Aceptada</button>
                            <button onClick={() => handleStatusChange(detailQuote.id, "Rechazada")} className="flex items-center gap-2 px-4 py-2.5 bg-rose-600 text-white rounded-xl text-sm font-bold hover:bg-rose-700"><X className="w-4 h-4" /> Rechazada</button>
                        </>
                    )}
                    {(detailQuote.status === "Aceptada") && !detailQuote.convertedFileId && (
                        <button onClick={() => handleConvert(detailQuote.id)} className="flex items-center gap-2 px-4 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold hover:bg-indigo-700"><ArrowRight className="w-4 h-4" /> Convertir a Expediente</button>
                    )}
                    {detailQuote.convertedFileId && (
                        <span className="flex items-center gap-2 px-4 py-2.5 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400 rounded-xl text-sm font-bold">
                            <Check className="w-4 h-4" /> Convertida → Exp #{detailQuote.convertedFileId}
                        </span>
                    )}
                </div>

                {showItemModal && <ItemModal serviceTypes={SERVICE_TYPES} onSave={handleAddItem} onClose={() => setShowItemModal(false)} />}
            </div>
        );
    }

    // LIST VIEW
    return (
        <div className="space-y-6 pb-12">
            <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-black text-slate-900 dark:text-white tracking-tight">Cotizaciones</h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">Motor de cotización inteligente</p>
                </div>
                <button onClick={() => { setEditingQuote(null); setShowModal(true); }} className="flex items-center gap-2 px-4 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold shadow-lg hover:bg-indigo-700 transition-all">
                    <Plus className="w-4 h-4" /> Nueva Cotización
                </button>
            </div>

            <div className="relative">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
                <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Buscar por título, número o destino..." className="w-full pl-10 pr-4 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm" />
            </div>

            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
                {filtered.length === 0 ? (
                    <div className="col-span-full py-16 text-center text-sm text-slate-400">No hay cotizaciones.</div>
                ) : filtered.map(q => (
                    <div key={q.id} className="group bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 p-5 hover:shadow-md hover:border-indigo-200 dark:hover:border-indigo-800 transition-all cursor-pointer" onClick={() => loadDetail(q.id)}>
                        <div className="flex items-center justify-between mb-3">
                            <span className="text-[10px] font-black text-indigo-500">{q.quoteNumber}</span>
                            <span className={`px-2 py-0.5 rounded-full text-[10px] font-bold ${STATUS_COLORS[q.status] || STATUS_COLORS.Borrador}`}>{q.status}</span>
                        </div>
                        <h3 className="text-sm font-bold text-slate-900 dark:text-white mb-1 truncate">{q.title}</h3>
                        <div className="flex items-center gap-3 text-[10px] text-slate-400 mb-3">
                            {q.destination && <span className="flex items-center gap-1"><MapPin className="w-3 h-3" />{q.destination}</span>}
                            <span className="flex items-center gap-1"><Users className="w-3 h-3" />{q.adults + q.children} pax</span>
                        </div>
                        <div className="flex items-center justify-between pt-3 border-t border-slate-100 dark:border-slate-800">
                            <span className="text-lg font-black text-indigo-600">{fmt(q.totalSale)}</span>
                            <div className="flex gap-1">
                                <button onClick={e => { e.stopPropagation(); setEditingQuote(q); setShowModal(true); }} className="p-1.5 rounded hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-400"><Edit className="w-3.5 h-3.5" /></button>
                                <button onClick={e => { e.stopPropagation(); handleDelete(q.id); }} className="p-1.5 rounded hover:bg-rose-50 dark:hover:bg-rose-900/20 text-rose-400"><Trash2 className="w-3.5 h-3.5" /></button>
                            </div>
                        </div>
                    </div>
                ))}
            </div>

            {showModal && <QuoteFormModal customers={customers} initial={editingQuote} onSave={editingQuote ? handleUpdate : handleCreate} onClose={() => { setShowModal(false); setEditingQuote(null); }} />}
        </div>
    );
}

function QuoteFormModal({ customers, initial, onSave, onClose }) {
    const [form, setForm] = useState({
        title: initial?.title || "", description: initial?.description || "",
        customerId: initial?.customerId || "", destination: initial?.destination || "",
        adults: initial?.adults || 2, children: initial?.children || 0,
        travelStartDate: initial?.travelStartDate?.substring(0, 10) || "",
        travelEndDate: initial?.travelEndDate?.substring(0, 10) || "",
        validUntil: initial?.validUntil?.substring(0, 10) || "", notes: initial?.notes || "",
    });
    const set = (k, v) => setForm(p => ({ ...p, [k]: v }));

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-2xl w-full max-w-lg max-h-[90vh] overflow-y-auto p-6 space-y-4" onClick={e => e.stopPropagation()}>
                <h2 className="text-lg font-black text-slate-900 dark:text-white">{initial ? "Editar Cotización" : "Nueva Cotización"}</h2>
                <input value={form.title} onChange={e => set("title", e.target.value)} placeholder="Título *" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <select value={form.customerId} onChange={e => set("customerId", e.target.value ? parseInt(e.target.value) : null)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm">
                    <option value="">Cliente (opcional)</option>
                    {customers.map(c => <option key={c.id} value={c.id}>{c.fullName}</option>)}
                </select>
                <input value={form.destination} onChange={e => set("destination", e.target.value)} placeholder="Destino" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="grid grid-cols-2 gap-3">
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Adultos</label><input type="number" value={form.adults} onChange={e => set("adults", parseInt(e.target.value) || 0)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Menores</label><input type="number" value={form.children} onChange={e => set("children", parseInt(e.target.value) || 0)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                </div>
                <div className="grid grid-cols-2 gap-3">
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Salida</label><input type="date" value={form.travelStartDate} onChange={e => set("travelStartDate", e.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Regreso</label><input type="date" value={form.travelEndDate} onChange={e => set("travelEndDate", e.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                </div>
                <div><label className="text-[10px] font-bold text-slate-400 uppercase">Válida Hasta</label><input type="date" value={form.validUntil} onChange={e => set("validUntil", e.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                <textarea value={form.notes} onChange={e => set("notes", e.target.value)} placeholder="Notas" rows={2} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="flex gap-3 justify-end pt-2">
                    <button onClick={onClose} className="px-4 py-2 rounded-lg text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.title} className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-bold hover:bg-indigo-700 disabled:opacity-40">Guardar</button>
                </div>
            </div>
        </div>
    );
}

function ItemModal({ serviceTypes, onSave, onClose }) {
    const [form, setForm] = useState({ serviceType: "Hotel", description: "", quantity: 1, unitCost: 0, unitPrice: 0, markupPercent: 20, notes: "" });
    const set = (k, v) => setForm(p => ({ ...p, [k]: v }));

    const handleMarkupCalc = () => {
        if (form.unitCost > 0 && form.markupPercent > 0) {
            set("unitPrice", Math.round(form.unitCost * (1 + form.markupPercent / 100)));
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-2xl w-full max-w-md p-6 space-y-4" onClick={e => e.stopPropagation()}>
                <h2 className="text-lg font-black text-slate-900 dark:text-white">Agregar Servicio</h2>
                <select value={form.serviceType} onChange={e => set("serviceType", e.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm">
                    {serviceTypes.map(t => <option key={t} value={t}>{t}</option>)}
                </select>
                <input value={form.description} onChange={e => set("description", e.target.value)} placeholder="Descripción *" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="grid grid-cols-3 gap-3">
                    <div><label className="text-[10px] font-bold text-slate-400">CANTIDAD</label><input type="number" value={form.quantity} onChange={e => set("quantity", parseInt(e.target.value) || 1)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                    <div><label className="text-[10px] font-bold text-slate-400">COSTO</label><input type="number" value={form.unitCost} onChange={e => set("unitCost", parseFloat(e.target.value) || 0)} onBlur={handleMarkupCalc} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                    <div><label className="text-[10px] font-bold text-slate-400">MARKUP %</label><input type="number" value={form.markupPercent} onChange={e => set("markupPercent", parseFloat(e.target.value) || 0)} onBlur={handleMarkupCalc} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                </div>
                <div><label className="text-[10px] font-bold text-slate-400">PRECIO VENTA</label><input type="number" value={form.unitPrice} onChange={e => set("unitPrice", parseFloat(e.target.value) || 0)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                <div className="flex gap-3 justify-end pt-2">
                    <button onClick={onClose} className="px-4 py-2 rounded-lg text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.description} className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-bold hover:bg-indigo-700 disabled:opacity-40">Agregar</button>
                </div>
            </div>
        </div>
    );
}
