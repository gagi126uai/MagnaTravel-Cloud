import { useCallback, useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { api } from "../api";
import { showConfirm, showError, showSuccess } from "../alerts";
import { ArrowRight, Calendar, Check, Edit, FileText, Loader2, Plus, Search, Send, Trash2, Users, X } from "lucide-react";
import { MobileRecordCard, MobileRecordList } from "../components/ui/MobileRecordCard";
import { getPublicId, getRelatedPublicId } from "../lib/publicIds";

const SERVICE_TYPES = ["Hotel", "Vuelo", "Transfer", "Paquete", "Excursion", "Seguro", "Otro"];
const STATUS_COLORS = {
    Borrador: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300",
    Enviada: "bg-blue-50 text-blue-700 dark:bg-blue-900/20 dark:text-blue-400",
    Aceptada: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400",
    Vencida: "bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-400",
    Rechazada: "bg-rose-50 text-rose-700 dark:bg-rose-900/20 dark:text-rose-400"
};

const buildForm = (initial = null, defaults = null) => ({
    title: initial?.title || defaults?.title || "",
    description: initial?.description || defaults?.description || "",
    customerPublicId: initial?.customerPublicId || initial?.customer?.publicId || defaults?.customerPublicId || "",
    leadPublicId: initial?.leadPublicId || initial?.lead?.publicId || defaults?.leadPublicId || "",
    destination: initial?.destination || defaults?.destination || "",
    adults: initial?.adults ?? defaults?.adults ?? 2,
    children: initial?.children ?? defaults?.children ?? 0,
    travelStartDate: initial?.travelStartDate?.substring(0, 10) || defaults?.travelStartDate || "",
    travelEndDate: initial?.travelEndDate?.substring(0, 10) || defaults?.travelEndDate || "",
    validUntil: initial?.validUntil?.substring(0, 10) || defaults?.validUntil || "",
    notes: initial?.notes || defaults?.notes || ""
});

function Field({ label, children }) {
    return <div className="space-y-1.5"><label className="text-[10px] font-black uppercase tracking-[0.24em] text-slate-400">{label}</label>{children}</div>;
}

function QuoteFormModal({ customers, initial, defaults, contextLead, onSave, onClose }) {
    const [form, setForm] = useState(() => buildForm(initial, defaults));
    const set = (key, value) => setForm((prev) => ({ ...prev, [key]: value }));
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm" onClick={onClose}>
            <div className="max-h-[90vh] w-full max-w-2xl space-y-5 overflow-y-auto rounded-[2rem] border border-slate-200 bg-white p-6 shadow-2xl dark:border-slate-800 dark:bg-slate-900" onClick={(event) => event.stopPropagation()}>
                <div><h2 className="text-2xl font-black text-slate-900 dark:text-white">{initial ? "Editar cotizacion" : "Nueva cotizacion"}</h2><p className="mt-1 text-sm text-slate-500 dark:text-slate-400">Propuesta lista para seguir despues como reserva.</p></div>
                {form.leadPublicId && !initial && <div className="rounded-2xl border border-violet-200 bg-violet-50 px-4 py-3 text-sm text-violet-700 dark:border-violet-900/50 dark:bg-violet-900/10 dark:text-violet-300"><div className="text-[11px] font-black uppercase tracking-[0.22em]">Posible cliente asociado</div><div className="mt-1 font-semibold">{contextLead?.fullName || "Gestion comercial vinculada"}</div></div>}
                <div className="grid gap-4 md:grid-cols-2">
                    <Field label="Titulo"><input value={form.title} onChange={(event) => set("title", event.target.value)} placeholder="Ej: Escapada a Bariloche en familia" className="w-full rounded-2xl border border-slate-200 bg-transparent px-4 py-3 text-sm font-medium dark:border-slate-700" /></Field>
                    <Field label="Cliente"><select value={form.customerPublicId || ""} onChange={(event) => set("customerPublicId", event.target.value)} className="w-full rounded-2xl border border-slate-200 bg-transparent px-4 py-3 text-sm font-medium dark:border-slate-700"><option value="">Cliente (opcional)</option>{customers.map((customer) => <option key={getPublicId(customer)} value={getPublicId(customer)}>{customer.fullName}</option>)}</select></Field>
                    <Field label="Destino"><input value={form.destination} onChange={(event) => set("destination", event.target.value)} placeholder="Destino principal" className="w-full rounded-2xl border border-slate-200 bg-transparent px-4 py-3 text-sm font-medium dark:border-slate-700" /></Field>
                    <Field label="Valida hasta"><input type="date" value={form.validUntil} onChange={(event) => set("validUntil", event.target.value)} className="w-full rounded-2xl border border-slate-200 bg-transparent px-4 py-3 text-sm font-medium dark:border-slate-700" /></Field>
                </div>
                <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
                    <Field label="Adultos"><input type="number" value={form.adults} onChange={(event) => set("adults", parseInt(event.target.value, 10) || 0)} className="w-full rounded-xl border border-slate-200 bg-transparent px-3 py-2 text-sm dark:border-slate-700" /></Field>
                    <Field label="Menores"><input type="number" value={form.children} onChange={(event) => set("children", parseInt(event.target.value, 10) || 0)} className="w-full rounded-xl border border-slate-200 bg-transparent px-3 py-2 text-sm dark:border-slate-700" /></Field>
                    <Field label="Salida"><input type="date" value={form.travelStartDate} onChange={(event) => set("travelStartDate", event.target.value)} className="w-full rounded-xl border border-slate-200 bg-transparent px-3 py-2 text-sm dark:border-slate-700" /></Field>
                    <Field label="Regreso"><input type="date" value={form.travelEndDate} onChange={(event) => set("travelEndDate", event.target.value)} className="w-full rounded-xl border border-slate-200 bg-transparent px-3 py-2 text-sm dark:border-slate-700" /></Field>
                </div>
                <div className="grid gap-4 md:grid-cols-2">
                    <Field label="Resumen comercial"><textarea value={form.description} onChange={(event) => set("description", event.target.value)} rows={3} placeholder="Resumen corto para el equipo." className="w-full rounded-2xl border border-slate-200 bg-transparent px-4 py-3 text-sm dark:border-slate-700" /></Field>
                    <Field label="Notas"><textarea value={form.notes} onChange={(event) => set("notes", event.target.value)} rows={3} placeholder="Aclaraciones internas o vigencia." className="w-full rounded-2xl border border-slate-200 bg-transparent px-4 py-3 text-sm dark:border-slate-700" /></Field>
                </div>
                <div className="flex justify-end gap-3"><button onClick={onClose} className="rounded-lg px-4 py-2 text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button><button onClick={() => onSave(form)} disabled={!form.title} className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white hover:bg-indigo-700 disabled:opacity-40">Guardar</button></div>
            </div>
        </div>
    );
}

function ItemModal({ serviceTypes, onSave, onClose }) {
    const [form, setForm] = useState({ serviceType: "Hotel", description: "", quantity: 1, unitCost: 0, unitPrice: 0, markupPercent: 20, ratePublicId: null });
    const [ratesResults, setRatesResults] = useState([]);
    const [isSearching, setIsSearching] = useState(false);
    const set = (key, value) => setForm((prev) => ({ ...prev, [key]: value }));
    useEffect(() => {
        const run = async () => {
            if (form.description.length < 2 && !form.serviceType) { setRatesResults([]); return; }
            setIsSearching(true);
            try { setRatesResults((await api.get(`/rates/search?serviceType=${form.serviceType}&query=${form.description}`)) || []); } catch { setRatesResults([]); } finally { setIsSearching(false); }
        };
        const timeoutId = setTimeout(run, 300);
        return () => clearTimeout(timeoutId);
    }, [form.description, form.serviceType]);
    const selectRate = (rate) => { setForm((prev) => ({ ...prev, description: rate.productName || rate.hotelName || rate.description || "Servicio seleccionado", unitCost: rate.netCost || 0, unitPrice: rate.salePrice || 0, ratePublicId: getPublicId(rate), markupPercent: rate.netCost ? Math.round(((rate.salePrice - rate.netCost) / rate.netCost) * 100) : 0 })); setRatesResults([]); };
    const recalcSale = () => { if (form.unitCost > 0) set("unitPrice", Math.round(form.unitCost * (1 + (form.markupPercent || 0) / 100))); };
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm" onClick={onClose}>
            <div className="w-full max-w-md space-y-4 rounded-2xl border border-slate-200 bg-white p-6 shadow-xl dark:border-slate-800 dark:bg-slate-900" onClick={(event) => event.stopPropagation()}>
                <h2 className="border-b border-slate-100 pb-2 text-lg font-black text-slate-900 dark:border-slate-800 dark:text-white">Agregar servicio</h2>
                <Field label="Tipo de servicio"><select value={form.serviceType} onChange={(event) => { set("serviceType", event.target.value); set("ratePublicId", null); }} className="w-full rounded-xl border border-slate-200 bg-slate-50 px-4 py-2.5 text-sm font-medium dark:border-slate-700 dark:bg-slate-800">{serviceTypes.map((type) => <option key={type} value={type}>{type}</option>)}</select></Field>
                <Field label="Descripcion o tarifa"><div className="relative"><input value={form.description} onChange={(event) => { set("description", event.target.value); set("ratePublicId", null); }} placeholder="Ej: Hotel Hilton base doble" className="w-full rounded-xl border border-slate-200 bg-white py-2.5 pl-4 pr-10 text-sm dark:border-slate-700 dark:bg-slate-900" />{isSearching && <Loader2 className="absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-slate-400" />}{ratesResults.length > 0 && !form.ratePublicId && <div className="absolute z-10 mt-1 max-h-48 w-full overflow-y-auto rounded-xl border border-slate-200 bg-white shadow-lg dark:border-slate-700 dark:bg-slate-800">{ratesResults.map((rate) => <button key={getPublicId(rate)} type="button" onClick={() => selectRate(rate)} className="block w-full border-b border-slate-100 p-3 text-left last:border-b-0 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700/50"><div className="text-sm font-bold text-slate-900 dark:text-white">{rate.productName || rate.hotelName || rate.description}</div><div className="mt-1 text-xs text-slate-500">{rate.supplierName || "Tarifario"} - ${rate.salePrice}</div></button>)}</div>}</div></Field>
                <div className="grid grid-cols-3 gap-3"><Field label="Cant"><input type="number" min="1" value={form.quantity} onChange={(event) => set("quantity", parseInt(event.target.value, 10) || 1)} className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm font-bold dark:border-slate-700 dark:bg-slate-900" /></Field><Field label="Costo ($)"><input type="number" min="0" value={form.unitCost} onChange={(event) => set("unitCost", parseFloat(event.target.value) || 0)} onBlur={recalcSale} className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm font-bold dark:border-slate-700 dark:bg-slate-900" /></Field><Field label="Markup (%)"><input type="number" min="0" value={form.markupPercent} onChange={(event) => set("markupPercent", parseFloat(event.target.value) || 0)} onBlur={recalcSale} className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm font-bold dark:border-slate-700 dark:bg-slate-900" /></Field></div>
                <Field label="Precio venta ($)"><input type="number" min="0" value={form.unitPrice} onChange={(event) => set("unitPrice", parseFloat(event.target.value) || 0)} className="w-full rounded-xl border-2 border-indigo-200 bg-white px-4 py-3 text-lg font-black text-indigo-700 dark:border-indigo-800 dark:bg-slate-900 dark:text-indigo-400" /></Field>
                <div className="flex justify-end gap-3 border-t border-slate-100 pt-4 dark:border-slate-800"><button onClick={onClose} className="rounded-xl px-5 py-2.5 text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button><button onClick={() => onSave(form)} disabled={!form.description} className="flex items-center gap-2 rounded-xl bg-indigo-600 px-6 py-2.5 text-sm font-bold text-white hover:bg-indigo-700 disabled:opacity-50"><Plus className="h-4 w-4" /> Agregar item</button></div>
            </div>
        </div>
    );
}

export default function QuotesPage() {
    const navigate = useNavigate();
    const location = useLocation();
    const [quotes, setQuotes] = useState([]);
    const [customers, setCustomers] = useState([]);
    const [loading, setLoading] = useState(true);
    const [search, setSearch] = useState("");
    const [showModal, setShowModal] = useState(false);
    const [editingQuote, setEditingQuote] = useState(null);
    const [createDefaults, setCreateDefaults] = useState(null);
    const [contextLead, setContextLead] = useState(null);
    const [detailQuote, setDetailQuote] = useState(null);
    const [showItemModal, setShowItemModal] = useState(false);
    const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0 })}`;

    const loadQuotes = useCallback(async () => { setLoading(true); try { const [quoteData, customerData] = await Promise.all([api.get("/quotes"), api.get("/customers?page=1&pageSize=100&sortBy=fullName&sortDir=asc")]); setQuotes(quoteData || []); setCustomers(customerData?.items || []); } catch { showError("Error al cargar cotizaciones"); } finally { setLoading(false); } }, []);
    const loadDetail = useCallback(async (id) => { try { setDetailQuote(await api.get(`/quotes/${id}`)); } catch { showError("Error al cargar detalle"); } }, []);
    const loadLeadContext = useCallback(async (leadPublicId) => { if (!leadPublicId) { setContextLead(null); return; } try { setContextLead(await api.get(`/leads/${leadPublicId}`)); } catch { setContextLead(null); } }, []);
    const openCreateModal = useCallback((defaults = {}) => { setEditingQuote(null); setCreateDefaults(buildForm(null, defaults)); setShowModal(true); }, []);
    const closeModal = () => { setShowModal(false); setEditingQuote(null); setCreateDefaults(null); setContextLead(null); };
    useEffect(() => { loadQuotes(); }, [loadQuotes]);
    useEffect(() => {
        const openQuoteId = location.state?.openQuoteId;
        const stateDefaults = location.state?.createQuoteDefaults || {};
        const params = new URLSearchParams(location.search);
        const defaults = { customerPublicId: params.get("customerPublicId") || stateDefaults.customerPublicId || "", leadPublicId: params.get("leadPublicId") || stateDefaults.leadPublicId || "" };
        if (openQuoteId) { loadDetail(openQuoteId); navigate(location.pathname, { replace: true, state: {} }); return; }
        if (params.get("create") === "1" || location.state?.createQuoteDefaults) { openCreateModal(defaults); loadLeadContext(defaults.leadPublicId); navigate(location.pathname, { replace: true, state: {} }); }
    }, [loadDetail, loadLeadContext, location.pathname, location.search, location.state, navigate, openCreateModal]);

    const filtered = quotes.filter((quote) => [quote.title, quote.quoteNumber, quote.destination, quote.customer?.fullName, quote.customerName, quote.leadName, quote.convertedReservaNumeroReserva].filter(Boolean).join(" ").toLowerCase().includes(search.toLowerCase()));
    const sanitizeQuote = (data) => ({ ...data, customerPublicId: data.customerPublicId || null, leadPublicId: data.leadPublicId || null, travelStartDate: data.travelStartDate || null, travelEndDate: data.travelEndDate || null, validUntil: data.validUntil || null, adults: parseInt(data.adults, 10) || 2, children: parseInt(data.children, 10) || 0 });
    const handleCreate = async (data) => { try { await api.post("/quotes", sanitizeQuote(data)); showSuccess("Cotizacion creada"); closeModal(); loadQuotes(); } catch { showError("Error al crear cotizacion"); } };
    const handleUpdate = async (data) => { try { await api.put(`/quotes/${getPublicId(editingQuote)}`, sanitizeQuote(data)); showSuccess("Cotizacion actualizada"); closeModal(); loadQuotes(); if (getPublicId(detailQuote) === getPublicId(editingQuote)) loadDetail(getPublicId(editingQuote)); } catch { showError("Error al actualizar"); } };
    const handleDelete = async (id) => { const confirmed = await showConfirm("Eliminar cotizacion", "La propuesta se eliminara por completo junto con sus servicios.", "Si, eliminar", "red"); if (!confirmed) return; try { await api.delete(`/quotes/${id}`); showSuccess("Cotizacion eliminada"); if (getPublicId(detailQuote) === id) setDetailQuote(null); loadQuotes(); } catch { showError("Error al eliminar"); } };
    const handleStatusChange = async (id, status) => { try { await api.patch(`/quotes/${id}/status`, { status }); showSuccess(`Estado actualizado: ${status}`); loadQuotes(); if (getPublicId(detailQuote) === id) loadDetail(id); } catch { showError("Error al cambiar estado"); } };
    const handleConvert = async (id) => { const confirmed = await showConfirm("Convertir a reserva", "Se creara una reserva nueva con los datos de esta propuesta comercial.", "Si, convertir"); if (!confirmed) return; try { const res = await api.post(`/quotes/${id}/convert`); showSuccess("Reserva creada"); loadQuotes(); navigate(`/reservas/${res.reservaPublicId}`); } catch (error) { showError(error.message || "Error al convertir"); } };
    const handleAddItem = async (item) => { try { const quote = await api.post(`/quotes/${getPublicId(detailQuote)}/items`, item); setDetailQuote(quote); setShowItemModal(false); showSuccess("Servicio agregado"); loadQuotes(); } catch { showError("Error al agregar servicio"); } };
    const handleRemoveItem = async (itemId) => { try { await api.delete(`/quotes/${getPublicId(detailQuote)}/items/${itemId}`); await loadDetail(getPublicId(detailQuote)); await loadQuotes(); showSuccess("Servicio eliminado"); } catch { showError("Error al eliminar servicio"); } };

    if (loading) return <div className="flex h-[60vh] items-center justify-center"><Loader2 className="h-8 w-8 animate-spin text-indigo-500" /></div>;

    if (detailQuote) return (
        <div className="space-y-6 pb-12">
            <div className="flex items-center gap-3"><button onClick={() => setDetailQuote(null)} className="rounded-lg p-2 hover:bg-slate-100 dark:hover:bg-slate-800"><X className="h-5 w-5" /></button><div className="flex-1"><h1 className="text-xl font-black text-slate-900 dark:text-white">{detailQuote.quoteNumber} - {detailQuote.title}</h1><p className="text-xs text-slate-400">{detailQuote.destination || "Sin destino"} | {detailQuote.adults} adultos, {detailQuote.children} menores</p></div><span className={`rounded-full px-3 py-1 text-xs font-bold ${STATUS_COLORS[detailQuote.status] || STATUS_COLORS.Borrador}`}>{detailQuote.status}</span></div>
            <div className="grid grid-cols-1 gap-3 lg:grid-cols-3">
                <Origin label="Cliente" value={detailQuote.customer?.fullName || detailQuote.customerName || "Sin cliente"} disabled={!getRelatedPublicId(detailQuote, "customerPublicId", "customerId")} onClick={() => { const customerPublicId = getRelatedPublicId(detailQuote, "customerPublicId", "customerId"); if (customerPublicId) navigate(`/customers/${customerPublicId}/account`); }} />
                <Origin label="Posible cliente asociado" value={detailQuote.lead?.fullName || detailQuote.leadName || "Sin gestion asociada"} disabled={!getRelatedPublicId(detailQuote, "leadPublicId", "leadId")} onClick={() => { const leadPublicId = getRelatedPublicId(detailQuote, "leadPublicId", "leadId"); if (leadPublicId) navigate("/crm", { state: { openLeadId: leadPublicId } }); }} />
                <Origin label="Reserva" value={detailQuote.convertedReserva?.numeroReserva || detailQuote.convertedReservaNumeroReserva || "Todavia no convertida"} disabled={!getRelatedPublicId(detailQuote, "convertedReservaPublicId", "convertedReservaId")} onClick={() => { const reservaPublicId = getRelatedPublicId(detailQuote, "convertedReservaPublicId", "convertedReservaId"); if (reservaPublicId) navigate(`/reservas/${reservaPublicId}`); }} />
            </div>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-3"><SummaryCard label="Costo total" value={fmt(detailQuote.totalCost)} /><SummaryCard label="Venta total" value={fmt(detailQuote.totalSale)} valueClassName="text-indigo-600" /><SummaryCard label="Margen" value={fmt(detailQuote.grossMargin)} valueClassName={detailQuote.grossMargin >= 0 ? "text-emerald-600" : "text-rose-600"} /></div>
            <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900"><div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800"><h3 className="text-sm font-black uppercase tracking-wider text-slate-900 dark:text-white">Servicios</h3><button onClick={() => setShowItemModal(true)} className="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-indigo-700"><Plus className="h-3 w-3" /> Agregar</button></div>{(!detailQuote.items || detailQuote.items.length === 0) ? <div className="px-6 py-8 text-center text-sm text-slate-400">No hay servicios cargados. Agrega items para completar la propuesta.</div> : <div className="divide-y divide-slate-50 dark:divide-slate-800/50">{detailQuote.items.map((item) => <div key={getPublicId(item)} className="flex items-center gap-4 px-6 py-3 hover:bg-slate-50/50 dark:hover:bg-slate-800/20"><div className="flex h-8 w-8 items-center justify-center rounded-lg bg-indigo-50 text-[10px] font-black text-indigo-600 dark:bg-indigo-900/20">{item.serviceType?.substring(0, 3).toUpperCase()}</div><div className="min-w-0 flex-1"><div className="truncate text-sm font-bold text-slate-900 dark:text-white">{item.description}</div><div className="text-[10px] text-slate-400">{item.quantity}x | Costo: {fmt(item.unitCost)} | Venta: {fmt(item.unitPrice)}</div></div><span className="text-sm font-black text-slate-900 dark:text-white">{fmt(item.totalPrice ?? item.unitPrice * item.quantity)}</span><button onClick={() => handleRemoveItem(getPublicId(item))} className="rounded p-1.5 text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20"><Trash2 className="h-3.5 w-3.5" /></button></div>)}</div>}</div>
            <div className="flex flex-wrap gap-3">{detailQuote.status === "Borrador" && <button onClick={() => handleStatusChange(getPublicId(detailQuote), "Enviada")} className="flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-bold text-white hover:bg-blue-700"><Send className="h-4 w-4" /> Marcar como enviada</button>}{detailQuote.status === "Enviada" && <><button onClick={() => handleStatusChange(getPublicId(detailQuote), "Aceptada")} className="flex items-center gap-2 rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-bold text-white hover:bg-emerald-700"><Check className="h-4 w-4" /> Aceptada</button><button onClick={() => handleStatusChange(getPublicId(detailQuote), "Rechazada")} className="flex items-center gap-2 rounded-xl bg-rose-600 px-4 py-2.5 text-sm font-bold text-white hover:bg-rose-700"><X className="h-4 w-4" /> Rechazada</button></>}{!getRelatedPublicId(detailQuote, "convertedReservaPublicId", "convertedReservaId") && detailQuote.status === "Aceptada" && <button onClick={() => handleConvert(getPublicId(detailQuote))} className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-bold text-white hover:bg-indigo-700"><ArrowRight className="h-4 w-4" /> Convertir a reserva</button>}{getRelatedPublicId(detailQuote, "convertedReservaPublicId", "convertedReservaId") && <button onClick={() => navigate(`/reservas/${getRelatedPublicId(detailQuote, "convertedReservaPublicId", "convertedReservaId")}`)} className="flex items-center gap-2 rounded-xl bg-emerald-50 px-4 py-2.5 text-sm font-bold text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400"><Check className="h-4 w-4" /> Abrir reserva {detailQuote.convertedReserva?.numeroReserva || detailQuote.convertedReservaNumeroReserva}</button>}</div>
            {showItemModal && <ItemModal serviceTypes={SERVICE_TYPES} onSave={handleAddItem} onClose={() => setShowItemModal(false)} />}
        </div>
    );

    return (
        <div className="space-y-6 pb-12">
            <div className="flex flex-col justify-between gap-4 md:flex-row md:items-center"><div><h1 className="text-2xl font-black tracking-tight text-slate-900 dark:text-white">Cotizaciones</h1><p className="mt-1 text-sm text-slate-500 dark:text-slate-400">Propuestas comerciales conectadas con clientes, posibles clientes y reservas.</p></div><button onClick={() => { setContextLead(null); openCreateModal(); }} className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-bold text-white shadow-lg hover:bg-indigo-700"><Plus className="h-4 w-4" /> Nueva cotizacion</button></div>
            <div className="relative"><Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" /><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Buscar por numero, cliente, destino, reserva o posible cliente..." className="w-full rounded-xl border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm dark:border-slate-700 dark:bg-slate-900" /></div>
            {filtered.length === 0 ? (
                <div className="rounded-2xl border border-slate-200 bg-white px-4 py-12 text-center shadow-sm dark:border-slate-800 dark:bg-slate-900">
                    <div className="flex flex-col items-center justify-center text-slate-400 dark:text-slate-600">
                        <FileText className="mb-3 h-10 w-10 opacity-20" />
                        <p className="text-sm font-medium">No se encontraron cotizaciones</p>
                    </div>
                </div>
            ) : (
                <>
                    <MobileRecordList>
                        {filtered.map((quote) => (
                            <QuoteMobileCard
                                key={getPublicId(quote)}
                                quote={quote}
                                fmt={fmt}
                                onOpen={() => loadDetail(getPublicId(quote))}
                                onEdit={() => { setCreateDefaults(null); setContextLead(null); setEditingQuote(quote); setShowModal(true); }}
                                onDelete={() => handleDelete(getPublicId(quote))}
                            />
                        ))}
                    </MobileRecordList>

                    <div className="hidden overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900 md:block">
                        <div className="overflow-x-auto">
                            <table className="min-w-full border-collapse text-left">
                                <thead>
                                    <tr className="border-b border-slate-100 bg-slate-50/50 dark:border-slate-800 dark:bg-slate-800/30">
                                        <th className="px-4 py-3 text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Cotizacion</th>
                                        <th className="px-4 py-3 text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Cliente / titulo</th>
                                        <th className="px-4 py-3 text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Destino / pax</th>
                                        <th className="px-4 py-3 text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Estado</th>
                                        <th className="px-4 py-3 text-right text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Monto total</th>
                                        <th className="px-4 py-3 pr-6 text-right text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Acciones</th>
                                    </tr>
                                </thead>
                                <tbody className="divide-y divide-slate-50 dark:divide-slate-800/50">
                                    {filtered.map((quote) => (
                                        <tr key={getPublicId(quote)} onClick={() => loadDetail(getPublicId(quote))} className="group cursor-pointer transition-colors hover:bg-slate-50/50 dark:hover:bg-slate-800/20">
                                            <td className="whitespace-nowrap px-4 py-3 align-middle"><span className="rounded-md bg-indigo-50 px-2 py-1 text-[11px] font-black text-indigo-500 dark:bg-indigo-900/20">{quote.quoteNumber}</span></td>
                                            <td className="px-4 py-3 align-middle"><div className="truncate text-sm font-semibold text-slate-900 dark:text-white">{quote.title}</div><div className="truncate text-xs text-slate-400">{quote.customer?.fullName || quote.customerName || "Sin cliente asociado"}</div>{quote.leadName && <div className="truncate text-[11px] text-indigo-500">Posible cliente: {quote.leadName}</div>}<div className="mt-1 flex items-center gap-1 text-xs text-slate-400"><Calendar className="h-3 w-3" />{quote.createdAt ? new Date(quote.createdAt).toLocaleDateString() : "-"}</div></td>
                                            <td className="px-4 py-3 align-middle"><div className="text-sm text-slate-600 dark:text-slate-300">{quote.destination || <span className="italic text-slate-400">Sin destino</span>}</div><div className="flex items-center gap-1 text-xs text-slate-400"><Users className="h-3 w-3" />{(quote.adults || 0) + (quote.children || 0)} pax</div></td>
                                            <td className="whitespace-nowrap px-4 py-3 align-middle"><span className={`rounded-full px-2 py-1 text-[10px] font-bold ${STATUS_COLORS[quote.status] || STATUS_COLORS.Borrador}`}>{quote.status}</span></td>
                                            <td className="whitespace-nowrap px-4 py-3 text-right align-middle"><div className="text-sm font-black text-indigo-600 dark:text-indigo-400">{fmt(quote.totalSale)}</div><div className="text-[10px] text-slate-400">Neto: {fmt(quote.totalCost)}</div></td>
                                            <td className="whitespace-nowrap px-4 py-3 pr-4 text-right align-middle"><div className="flex justify-end gap-1"><button onClick={(event) => { event.stopPropagation(); setCreateDefaults(null); setContextLead(null); setEditingQuote(quote); setShowModal(true); }} className="rounded p-1.5 text-slate-400 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800"><Edit className="h-4 w-4" /></button><button onClick={(event) => { event.stopPropagation(); handleDelete(getPublicId(quote)); }} className="rounded p-1.5 text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-900/20"><Trash2 className="h-4 w-4" /></button></div></td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    </div>
                </>
            )}
            {showModal && <QuoteFormModal customers={customers} initial={editingQuote} defaults={createDefaults} contextLead={contextLead} onSave={editingQuote ? handleUpdate : handleCreate} onClose={closeModal} />}
        </div>
    );
}

function Origin({ label, value, disabled, onClick }) {
    return <button type="button" onClick={onClick} disabled={disabled} className="rounded-2xl border border-slate-200 bg-white p-4 text-left disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:bg-slate-900"><div className="text-[10px] font-black uppercase tracking-widest text-slate-400">{label}</div><div className="mt-2 text-sm font-bold text-slate-900 dark:text-white">{value}</div></button>;
}

function SummaryCard({ label, value, valueClassName = "" }) {
    return (
        <div className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900">
            <div className="text-[10px] font-black uppercase tracking-widest text-slate-400">{label}</div>
            <div className={`mt-2 text-xl font-black text-slate-900 dark:text-white ${valueClassName}`}>{value}</div>
        </div>
    );
}

function QuoteMobileCard({ quote, fmt, onOpen, onEdit, onDelete }) {
    return (
        <MobileRecordCard
            onClick={onOpen}
            accentSlot={<span className="rounded-md bg-indigo-50 px-2 py-1 text-[11px] font-black text-indigo-500 dark:bg-indigo-900/20">{quote.quoteNumber}</span>}
            statusSlot={<span className={`rounded-full px-2 py-1 text-[10px] font-bold ${STATUS_COLORS[quote.status] || STATUS_COLORS.Borrador}`}>{quote.status}</span>}
            title={quote.title}
            subtitle={quote.customer?.fullName || quote.customerName || "Sin cliente asociado"}
            meta={
                <>
                    <span className="flex items-center gap-2 text-xs"><Calendar className="h-3.5 w-3.5 text-slate-400" />{quote.createdAt ? new Date(quote.createdAt).toLocaleDateString() : "-"}</span>
                    <span className="flex items-center gap-2 text-xs"><Users className="h-3.5 w-3.5 text-slate-400" />{quote.destination || "Sin destino"} · {(quote.adults || 0) + (quote.children || 0)} pax</span>
                    {quote.leadName ? <span className="text-[11px] text-indigo-500">Posible cliente: {quote.leadName}</span> : null}
                </>
            }
            footer={
                <div>
                    <div className="text-sm font-black text-indigo-600 dark:text-indigo-400">{fmt(quote.totalSale)}</div>
                    <div className="text-[10px] text-slate-400">Neto: {fmt(quote.totalCost)}</div>
                </div>
            }
            footerActions={
                <>
                    <button onClick={(event) => { event.stopPropagation(); onEdit(); }} className="rounded p-1.5 text-slate-400 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800">
                        <Edit className="h-4 w-4" />
                    </button>
                    <button onClick={(event) => { event.stopPropagation(); onDelete(); }} className="rounded p-1.5 text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-900/20">
                        <Trash2 className="h-4 w-4" />
                    </button>
                </>
            }
        />
    );
}
