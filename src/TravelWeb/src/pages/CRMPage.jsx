import { useEffect, useState, useCallback, useRef } from "react";
import { useNavigate } from "react-router-dom";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    Plus, Phone, Mail, MapPin, DollarSign, Calendar,
    MessageSquare, ArrowRight, Loader2, X, Check, Send, FileText,
    Smartphone, Search, Users, Clock, Trash2, ChevronRight, RefreshCw,
    User, CalendarRange, Users2, Info, Copy
} from "lucide-react";
import Swal from "sweetalert2";

const STATUSES = [
    { value: "Nuevo", label: "Nuevo", dot: "bg-slate-400", badge: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300" },
    { value: "Contactado", label: "Contactado", dot: "bg-blue-500", badge: "bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400" },
    { value: "Cotizado", label: "Cotizado", dot: "bg-violet-500", badge: "bg-violet-50 text-violet-700 dark:bg-violet-900/30 dark:text-violet-400" },
    { value: "Ganado", label: "Ganado", dot: "bg-emerald-500", badge: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400" },
    { value: "Perdido", label: "Perdido", dot: "bg-rose-500", badge: "bg-rose-50 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400" },
];

const ACTIVITY_TYPES = ["Llamada", "Email", "WhatsApp", "Reunión", "Nota", "Cotización"];
const SOURCES = ["Web", "WhatsApp", "Referido", "Teléfono", "Instagram", "Otro"];

function getStatusConfig(status) { return STATUSES.find(s => s.value === status) || STATUSES[0]; }

function timeAgo(dateStr) {
    if (!dateStr) return "";
    const diff = Date.now() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return "ahora";
    if (mins < 60) return `hace ${mins}m`;
    const hours = Math.floor(mins / 60);
    if (hours < 24) return `hace ${hours}h`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `hace ${days}d`;
    return new Date(dateStr).toLocaleDateString("es-AR");
}

export default function CRMPage() {
    const navigate = useNavigate();
    const [allLeads, setAllLeads] = useState([]);
    const [loading, setLoading] = useState(true);
    const [showModal, setShowModal] = useState(false);
    const [detailLead, setDetailLead] = useState(null);
    const [detailLoading, setDetailLoading] = useState(false);
    const [showActivityModal, setShowActivityModal] = useState(false);
    const [searchTerm, setSearchTerm] = useState("");
    const [filterStatus, setFilterStatus] = useState("all");
    const [filterSource, setFilterSource] = useState("all");

    // Chat
    const [chatMessage, setChatMessage] = useState("");
    const [sendingChat, setSendingChat] = useState(false);

    // Front-end pagination
    const [visibleCount, setVisibleCount] = useState(50);
    const handleLoadMore = () => setVisibleCount(v => v + 50);

    const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0 })}`;

    const loadLeads = useCallback(async () => {
        setLoading(true);
        try {
            const data = await api.get("/leads");
            setAllLeads(data || []);
        } catch (e) { console.error("Load error:", e); showError("Error al cargar leads"); }
        finally { setLoading(false); }
    }, []);

    useEffect(() => { loadLeads(); }, [loadLeads]);

    // Polling para "tiempo real" (cada 30s)
    useEffect(() => {
        const interval = setInterval(() => {
            loadLeads();
        }, 30000);
        return () => clearInterval(interval);
    }, [loadLeads]);

    const loadDetail = async (id) => {
        // Clear previous detail to avoid "flicker" of old data
        // but only if it's a different lead
        if (detailLead?.id !== id) setDetailLoading(true);
        try {
            const lead = await api.get(`/leads/${id}`);
            setDetailLead(lead);
        } catch (e) { console.error("Detail error:", e); showError("Error al cargar detalle"); }
        finally { setDetailLoading(false); }
    };

    const handleCreate = async (data) => {
        try { await api.post("/leads", data); showSuccess("Lead creado"); setShowModal(false); loadLeads(); }
        catch { showError("Error al crear lead"); }
    };

    const handleStatusChange = async (id, status) => {
        try { await api.patch(`/leads/${id}/status`, { status }); showSuccess(`Estado: ${status}`); loadLeads(); if (detailLead?.id === id) loadDetail(id); }
        catch { showError("Error al cambiar estado"); }
    };

    const handleConvert = async (id) => {
        const { isConfirmed } = await Swal.fire({ title: "¿Convertir a cliente?", text: "Se creará un cliente nuevo.", icon: "question", showCancelButton: true, confirmButtonColor: "#4f46e5" });
        if (!isConfirmed) return;
        try { const res = await api.post(`/leads/${id}/convert`); showSuccess(`Cliente creado: #${res.customerId}`); loadLeads(); if (detailLead?.id === id) loadDetail(id); }
        catch (e) { showError(e.message || "Error al convertir"); }
    };

    const handleAddActivity = async (activity) => {
        try { await api.post(`/leads/${detailLead.id}/activities`, activity); showSuccess("Actividad registrada"); setShowActivityModal(false); loadDetail(detailLead.id); }
        catch { showError("Error al registrar actividad"); }
    };

    const handleDelete = async (id) => {
        const { isConfirmed } = await Swal.fire({ title: "¿Eliminar lead?", icon: "warning", showCancelButton: true, confirmButtonColor: "#ef4444" });
        if (!isConfirmed) return;
        try { await api.delete(`/leads/${id}`); showSuccess("Lead eliminado"); loadLeads(); setDetailLead(null); }
        catch { showError("Error al eliminar"); }
    };

    const handleSendChat = async () => {
        if (!chatMessage.trim() || !detailLead?.id) return;
        setSendingChat(true);
        try {
            await api.post(`/leads/${detailLead.id}/whatsapp-message`, { message: chatMessage.trim() });
            setChatMessage("");
            loadDetail(detailLead.id);
        } catch (e) {
            showError(e.message || "Error al enviar mensaje");
        } finally {
            setSendingChat(false);
        }
    };

    const copyToClipboard = (text, label) => {
        navigator.clipboard.writeText(text);
        showSuccess(`${label} copiado`);
    };

    // ── Filtrar leads ──
    const filteredLeads = allLeads.filter(lead => {
        if (filterStatus !== "all" && lead.status !== filterStatus) return false;
        if (filterSource !== "all" && lead.source !== filterSource) return false;
        if (searchTerm) {
            const q = searchTerm.toLowerCase();
            return (lead.fullName?.toLowerCase().includes(q) || lead.phone?.toLowerCase().includes(q) || lead.email?.toLowerCase().includes(q) || lead.interestedIn?.toLowerCase().includes(q));
        }
        return true;
    });

    const visibleLeads = filteredLeads.slice(0, visibleCount);

    if (loading) {
        return <div className="flex items-center justify-center h-[60vh]"><Loader2 className="w-8 h-8 animate-spin text-indigo-500" /></div>;
    }

    const counts = {
        nuevo: allLeads.filter(l => l.status === "Nuevo").length,
        contactado: allLeads.filter(l => l.status === "Contactado").length,
        cotizado: allLeads.filter(l => l.status === "Cotizado").length,
        ganado: allLeads.filter(l => l.status === "Ganado").length,
        perdido: allLeads.filter(l => l.status === "Perdido").length,
    };
    const whatsappCount = allLeads.filter(l => l.source === "WhatsApp").length;

    return (
        <div className="space-y-6 pb-12">
            {/* Header */}
            <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-black text-slate-900 dark:text-white tracking-tight">CRM Pipeline</h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">{allLeads.length} leads totales encontrados</p>
                </div>
                <button onClick={() => setShowModal(true)} className="flex items-center gap-2 px-5 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold shadow-lg shadow-indigo-500/20 hover:bg-indigo-700 hover:-translate-y-0.5 active:translate-y-0 transition-all">
                    <Plus className="w-4 h-4" /> Nuevo Lead
                </button>
            </div>

            {/* KPI Cards */}
            <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
                <KPICard label="Nuevos" count={counts.nuevo} color="slate" icon={Users} onClick={() => setFilterStatus(filterStatus === "Nuevo" ? "all" : "Nuevo")} active={filterStatus === "Nuevo"} />
                <KPICard label="Contactados" count={counts.contactado} color="blue" icon={Send} onClick={() => setFilterStatus(filterStatus === "Contactado" ? "all" : "Contactado")} active={filterStatus === "Contactado"} />
                <KPICard label="Cotizados" count={counts.cotizado} color="violet" icon={FileText} onClick={() => setFilterStatus(filterStatus === "Cotizado" ? "all" : "Cotizado")} active={filterStatus === "Cotizado"} />
                <KPICard label="Ganados" count={counts.ganado} color="emerald" icon={Check} onClick={() => setFilterStatus(filterStatus === "Ganado" ? "all" : "Ganado")} active={filterStatus === "Ganado"} />
                <KPICard label="Perdidos" count={counts.perdido} color="rose" icon={X} onClick={() => setFilterStatus(filterStatus === "Perdido" ? "all" : "Perdido")} active={filterStatus === "Perdido"} />
                <KPICard label="WhatsApp" count={whatsappCount} color="green" icon={Smartphone} onClick={() => setFilterSource(filterSource === "WhatsApp" ? "all" : "WhatsApp")} active={filterSource === "WhatsApp"} />
            </div>

            {/* Search */}
            <div className="flex flex-col sm:flex-row gap-3">
                <div className="relative flex-1 group">
                    <Search className="absolute left-4 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400 group-focus-within:text-indigo-500 transition-colors" />
                    <input type="text" value={searchTerm} onChange={e => setSearchTerm(e.target.value)} placeholder="Buscar por nombre, teléfono o destino..."
                        className="w-full pl-11 pr-4 py-3 rounded-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition-all shadow-sm" />
                </div>
                {(filterStatus !== "all" || filterSource !== "all" || searchTerm) && (
                    <button onClick={() => { setFilterStatus("all"); setFilterSource("all"); setSearchTerm(""); }}
                        className="flex items-center gap-1.5 px-4 py-3 rounded-xl text-sm font-bold text-slate-500 hover:text-slate-700 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors">
                        <X className="w-4 h-4" /> Limpiar Filtros
                    </button>
                )}
            </div>

            {/* Leads Table */}
            <div className="bg-white dark:bg-slate-900 rounded-3xl border border-slate-200/60 dark:border-slate-800/60 overflow-hidden shadow-xl shadow-slate-200/50 dark:shadow-none">
                {filteredLeads.length === 0 ? (
                    <div className="px-6 py-20 text-center">
                        <div className="w-16 h-16 bg-slate-50 dark:bg-slate-800 rounded-full flex items-center justify-center mx-auto mb-4">
                            <Users className="w-8 h-8 text-slate-300 dark:text-slate-600" />
                        </div>
                        <p className="text-base font-bold text-slate-400">No hay leads que coincidan con la búsqueda</p>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full text-left border-collapse">
                            <thead>
                                <tr className="bg-slate-50/50 dark:bg-slate-800/30 text-[11px] font-black text-slate-400 uppercase tracking-widest border-b border-slate-100 dark:border-slate-800">
                                    <th className="px-6 py-4">Lead</th>
                                    <th className="px-4 py-4">Estado</th>
                                    <th className="px-4 py-4">Interés</th>
                                    <th className="px-4 py-4">Fuente</th>
                                    <th className="px-4 py-4 text-right">Ingreso</th>
                                    <th className="px-6 py-4"></th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100/60 dark:divide-slate-800/60">
                                {visibleLeads.map(lead => {
                                    const sc = getStatusConfig(lead.status);
                                    return (
                                        <tr key={lead.id} onClick={() => loadDetail(lead.id)}
                                            className="group cursor-pointer hover:bg-indigo-50/30 dark:hover:bg-indigo-900/5 transition-colors">
                                            <td className="px-6 py-4">
                                                <div className="flex items-center gap-3">
                                                    <div className={`w-10 h-10 rounded-2xl flex items-center justify-center text-xs font-black flex-shrink-0 shadow-sm ${lead.source === "WhatsApp" ? "bg-emerald-100 dark:bg-emerald-900/30 text-emerald-600" : "bg-indigo-100 dark:bg-indigo-900/30 text-indigo-600"}`}>
                                                        {lead.source === "WhatsApp" ? <Smartphone className="w-5 h-5" /> : lead.fullName?.[0].toUpperCase()}
                                                    </div>
                                                    <div className="min-w-0">
                                                        <div className="text-sm font-bold text-slate-900 dark:text-white truncate group-hover:text-indigo-600 transition-colors">{lead.fullName}</div>
                                                        <div className="text-[11px] text-slate-400 truncate">{lead.phone || lead.email || "Sin contacto"}</div>
                                                    </div>
                                                </div>
                                            </td>
                                            <td className="px-4 py-4">
                                                <span className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[10px] font-bold ${sc.badge}`}>
                                                    <span className={`w-1.5 h-1.5 rounded-full ${sc.dot}`}></span>
                                                    {sc.label}
                                                </span>
                                            </td>
                                            <td className="px-4 py-4 text-xs font-medium text-slate-600 dark:text-slate-400">
                                                {lead.interestedIn ? <span className="flex items-center gap-1.5"><MapPin className="w-3.5 h-3.5 text-indigo-400" />{lead.interestedIn}</span> : "—"}
                                            </td>
                                            <td className="px-4 py-4">
                                                <span className={`inline-flex items-center gap-1.5 text-xs font-bold ${lead.source === "WhatsApp" ? "text-emerald-500" : "text-slate-500"}`}>
                                                    {lead.source === "WhatsApp" && <Smartphone className="w-3.5 h-3.5" />}
                                                    {lead.source || "Directo"}
                                                </span>
                                            </td>
                                            <td className="px-4 py-4 text-right text-[11px] text-slate-400 font-medium">
                                                {timeAgo(lead.createdAt)}
                                            </td>
                                            <td className="px-6 py-4 text-right">
                                                <div className="flex justify-end">
                                                    <div className="w-8 h-8 rounded-full flex items-center justify-center bg-white dark:bg-slate-800 shadow-sm transition-all">
                                                        <ChevronRight className="w-4 h-4 text-indigo-600" />
                                                    </div>
                                                </div>
                                            </td>
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    </div>
                )}
                {filteredLeads.length > visibleCount && (
                    <div className="p-4 border-t border-slate-200/60 dark:border-slate-800/60 bg-white dark:bg-slate-900 text-center rounded-b-3xl">
                        <button 
                            onClick={handleLoadMore}
                            className="text-sm font-semibold text-slate-600 dark:text-slate-300 w-full sm:w-auto px-4 py-2 border rounded-md hover:bg-slate-50 dark:hover:bg-slate-800 transition-colors"
                        >
                            Cargar más resultados ({filteredLeads.length - visibleCount} restantes)
                        </button>
                    </div>
                )}
            </div>

            {showModal && <LeadFormModal sources={SOURCES} onSave={handleCreate} onClose={() => setShowModal(false)} />}

            {/* DETAIL MODAL (Centered 2 Columns) */}
            {detailLead && (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 backdrop-blur-md p-4 animate-in fade-in duration-300" onClick={() => setDetailLead(null)}>
                    <div className="bg-white dark:bg-slate-900 rounded-[2.5rem] w-full max-w-6xl h-[85vh] overflow-hidden shadow-2xl flex flex-col md:flex-row shadow-indigo-500/10 border border-white/20 dark:border-slate-800" onClick={e => e.stopPropagation()}>
                        
                        {detailLoading ? (
                            <div className="flex items-center justify-center w-full bg-white dark:bg-slate-900"><Loader2 className="w-10 h-10 animate-spin text-indigo-500" /></div>
                        ) : (
                            <>
                                {/* LEFT: Information (60%) */}
                                <div className="md:w-[55%] flex flex-col bg-white dark:bg-slate-900 border-r border-slate-100 dark:border-slate-800">
                                    <div className="p-8 flex-1 overflow-y-auto space-y-8 custom-scrollbar">
                                        {/* Name & Status */}
                                        <div className="flex items-start justify-between">
                                            <div className="space-y-3">
                                                <div className="flex items-center gap-2">
                                                    <span className={`inline-flex items-center gap-2 px-3 py-1 rounded-full text-[11px] font-black uppercase tracking-wider ${getStatusConfig(detailLead.status).badge}`}>
                                                        <span className={`w-2 h-2 rounded-full ${getStatusConfig(detailLead.status).dot} animate-pulse`}></span>
                                                        {getStatusConfig(detailLead.status).label}
                                                    </span>
                                                    {detailLead.source === "WhatsApp" && (
                                                        <span className="inline-flex items-center gap-2 px-3 py-1 rounded-full bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 text-[11px] font-black uppercase tracking-wider border border-emerald-500/20">
                                                            <Smartphone className="w-3.5 h-3.5" /> WhatsApp Lead
                                                        </span>
                                                    )}
                                                </div>
                                                <h2 className="text-4xl font-black text-slate-900 dark:text-white leading-tight">{detailLead.fullName}</h2>
                                                <div className="flex items-center gap-4 text-slate-400 text-sm font-medium">
                                                    <span className="flex items-center gap-1.5"><Clock className="w-4 h-4" /> Ingresó {timeAgo(detailLead.createdAt)}</span>
                                                    <span className="w-1 h-1 bg-slate-300 rounded-full"></span>
                                                    <span className="flex items-center gap-1.5"><Info className="w-4 h-4" /> CRM ID: #{detailLead.id}</span>
                                                </div>
                                            </div>
                                            <button onClick={() => setDetailLead(null)} className="p-3 bg-slate-50 dark:bg-slate-800 rounded-2xl hover:bg-slate-100 dark:hover:bg-slate-700 transition-colors group">
                                                <X className="w-6 h-6 text-slate-400 group-hover:scale-110 transition-transform" />
                                            </button>
                                        </div>

                                        {/* New Section: Detalle del Viaje */}
                                        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                                            <DetailBox icon={MapPin} label="Destino de Interés" value={detailLead.interestedIn || "No especificado"} sub="Sugerido por el bot" />
                                            <DetailBox icon={CalendarRange} label="Fechas Estimadas" value={detailLead.travelDates || "A definir"} sub="Capturado por bot" color="blue" />
                                            <DetailBox icon={Users2} label="Viajeros" value={detailLead.travelers || "1 viajero"} sub="Capturado por bot" color="violet" />
                                            <DetailBox icon={DollarSign} label="Presupuesto Est." value={detailLead.estimatedBudget > 0 ? fmt(detailLead.estimatedBudget) : "Sin definir"} sub="Valor potencial" color="emerald" />
                                        </div>

                                        {/* Contact Bar */}
                                        <div className="flex flex-wrap gap-3">
                                            {detailLead.phone && <ContactChip icon={Phone} text={detailLead.phone} type="Telefono" onCopy={() => copyToClipboard(detailLead.phone, "Teléfono")} href={`tel:${detailLead.phone}`} />}
                                            {detailLead.email && <ContactChip icon={Mail} text={detailLead.email} type="Email" onCopy={() => copyToClipboard(detailLead.email, "Email")} href={`mailto:${detailLead.email}`} />}
                                        </div>

                                        {/* Notes Section */}
                                        <div className="space-y-3">
                                            <h3 className="text-xs font-black text-slate-400 uppercase tracking-widest pl-1">Notas y Observaciones</h3>
                                            <div className="bg-slate-50 dark:bg-slate-800/10 rounded-3xl p-5 text-sm text-slate-600 dark:text-slate-300 border border-slate-100 dark:border-slate-800 leading-relaxed min-h-[100px] shadow-inner">
                                                {detailLead.notes || "No hay notas adicionales."}
                                            </div>
                                        </div>

                                        {/* Primary Actions */}
                                        <div className="flex gap-3 flex-wrap items-center">
                                            {detailLead.status === "Nuevo" && <ActionBtnLg onClick={() => handleStatusChange(detailLead.id, "Contactado")} bg="bg-blue-600" text="Marcar Contactado" icon={Send} />}
                                            {detailLead.status === "Contactado" && <ActionBtnLg onClick={() => handleStatusChange(detailLead.id, "Cotizado")} bg="bg-violet-600" text="Subir Cotización" icon={FileText} />}
                                            {detailLead.status === "Cotizado" && (
                                                <div className="flex gap-2">
                                                    <ActionBtnLg onClick={() => handleStatusChange(detailLead.id, "Ganado")} bg="bg-emerald-600" text="Ganado" icon={Check} />
                                                    <ActionBtnLg onClick={() => handleStatusChange(detailLead.id, "Perdido")} bg="bg-rose-500" text="Perdido" icon={X} />
                                                </div>
                                            )}
                                            {detailLead.status === "Ganado" && !detailLead.convertedCustomerId && <ActionBtnLg onClick={() => handleConvert(detailLead.id)} bg="bg-indigo-600" text="Convertir a Cliente" icon={User} />}
                                            {detailLead.convertedCustomerId && <span className="px-6 py-3 bg-emerald-100/50 text-emerald-700 rounded-2xl text-xs font-black uppercase tracking-widest flex items-center gap-2"><Check className="w-4 h-4" /> Cliente Registrado</span>}
                                            
                                            <div className="flex-1"></div>
                                            <button onClick={() => handleDelete(detailLead.id)} className="p-3 text-rose-400 hover:text-rose-600 hover:bg-rose-50 dark:hover:bg-rose-900/10 rounded-2xl transition-all">
                                                <Trash2 className="w-5 h-5" />
                                            </button>
                                        </div>
                                    </div>
                                </div>

                                {/* RIGHT: Live Chat (40%) */}
                                <div className="md:w-[45%] flex flex-col bg-slate-50 dark:bg-slate-950/20">
                                    {/* Chat Header */}
                                    <div className="px-8 py-6 flex items-center justify-between border-b border-white dark:border-slate-800/50">
                                        <div className="flex items-center gap-3">
                                            <div className="w-10 h-10 rounded-2xl bg-emerald-500 flex items-center justify-center text-white shadow-lg shadow-emerald-500/20">
                                                <Smartphone className="w-5 h-5" />
                                            </div>
                                            <div>
                                                <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider">Chat WhatsApp</h3>
                                                <div className="flex items-center gap-1.5 mt-0.5">
                                                    <span className="w-1.5 h-1.5 rounded-full bg-emerald-500 pulse"></span>
                                                    <span className="text-[10px] text-slate-400 font-bold uppercase tracking-widest">En línea con {detailLead.phone}</span>
                                                </div>
                                            </div>
                                        </div>
                                        <div className="flex items-center gap-2">
                                            <button onClick={() => setShowActivityModal(true)} className="p-2.5 bg-white dark:bg-slate-800 rounded-xl shadow-sm text-slate-400 hover:text-indigo-500 transition-colors" title="Registrar Actividad Manual">
                                                <MessageSquare className="w-4 h-4" />
                                            </button>
                                            <button onClick={() => loadDetail(detailLead.id)} className="p-2.5 bg-white dark:bg-slate-800 rounded-xl shadow-sm text-slate-400 hover:text-indigo-500 transition-colors" title="Actualizar Chat">
                                                <RefreshCw className="w-4 h-4" />
                                            </button>
                                        </div>
                                    </div>

                                    {/* Chat Messages */}
                                    <div className="flex-1 relative">
                                        <ChatMessages activities={detailLead.activities || []} leadName={detailLead.fullName} />
                                    </div>

                                    {/* Chat Input */}
                                    {detailLead.phone && detailLead.source === "WhatsApp" ? (
                                        <div className="p-6 bg-white dark:bg-slate-900 border-t border-slate-100 dark:border-slate-800">
                                            <div className="bg-slate-50 dark:bg-slate-950 p-2 rounded-3xl flex gap-2 items-center focus-within:ring-2 focus-within:ring-emerald-500/20 transition-all border border-slate-100 dark:border-slate-800">
                                                <input
                                                    type="text"
                                                    value={chatMessage}
                                                    onChange={e => setChatMessage(e.target.value)}
                                                    onKeyDown={e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); handleSendChat(); } }}
                                                    placeholder="Escribir respuesta oficial..."
                                                    className="flex-1 bg-transparent border-none focus:ring-0 px-4 py-3 text-sm text-slate-700 dark:text-slate-200"
                                                    disabled={sendingChat}
                                                />
                                                <button onClick={handleSendChat} disabled={sendingChat || !chatMessage.trim()}
                                                    className={`w-12 h-12 rounded-2xl flex items-center justify-center transition-all ${chatMessage.trim() ? "bg-emerald-600 text-white shadow-lg shadow-emerald-500/20 hover:scale-105" : "bg-slate-200 dark:bg-slate-800 text-slate-400 cursor-not-allowed"}`}>
                                                    {sendingChat ? <Loader2 className="w-5 h-5 animate-spin" /> : <Send className="w-5 h-5" />}
                                                </button>
                                            </div>
                                            <div className="mt-3 flex items-center justify-center gap-2">
                                                <div className="px-3 py-1 bg-emerald-50 dark:bg-emerald-900/20 text-emerald-600 text-[9px] font-black uppercase tracking-widest rounded-full border border-emerald-100 dark:border-emerald-800/30">
                                                    Envío via MagnaBot Pro
                                                </div>
                                            </div>
                                        </div>
                                    ) : (
                                        <div className="p-6 bg-slate-50 dark:bg-slate-900 border-t border-slate-100 dark:border-slate-800 text-center">
                                            <p className="text-xs text-slate-400 font-medium">Chat reservado para leads de WhatsApp</p>
                                        </div>
                                    )}
                                </div>
                            </>
                        )}
                    </div>
                    {showActivityModal && <ActivityModal types={ACTIVITY_TYPES} onSave={handleAddActivity} onClose={() => setShowActivityModal(false)} />}
                </div>
            )}
        </div>
    );
}

// ─── Sub-Components ──────────────────────────────────────

function DetailBox({ icon: Icon, label, value, sub, color = "indigo" }) {
    const colors = {
        indigo: "bg-indigo-50 dark:bg-indigo-900/10 text-indigo-600 dark:text-indigo-400 border-indigo-100/50",
        blue: "bg-blue-50 dark:bg-blue-900/10 text-blue-600 dark:text-blue-400 border-blue-100/50",
        violet: "bg-violet-50 dark:bg-violet-900/10 text-violet-600 dark:text-violet-400 border-violet-100/50",
        emerald: "bg-emerald-50 dark:bg-emerald-900/10 text-emerald-600 dark:text-emerald-400 border-emerald-100/50",
    };
    return (
        <div className={`p-4 rounded-3xl border ${colors[color]} space-y-2`}>
            <div className="flex items-center gap-2">
                <Icon className="w-3.5 h-3.5 opacity-70" />
                <span className="text-[10px] font-black uppercase tracking-widest opacity-70">{label}</span>
            </div>
            <div>
                <div className="text-sm font-black truncate">{value}</div>
                <div className="text-[10px] opacity-60 font-medium">{sub}</div>
            </div>
        </div>
    );
}

function ContactChip({ icon: Icon, text, type, onCopy, href }) {
    return (
        <div className="flex items-center gap-1 group">
            <a href={href} className="flex items-center gap-2 pl-3 pr-4 py-2 bg-slate-50 dark:bg-slate-800 rounded-2xl hover:bg-slate-100 dark:hover:bg-slate-700 transition-colors border border-slate-100 dark:border-slate-800">
                <Icon className="w-3.5 h-3.5 text-indigo-500" />
                <span className="text-xs font-bold text-slate-600 dark:text-slate-300">{text}</span>
            </a>
            <button onClick={onCopy} className="p-2 text-slate-300 hover:text-indigo-500 transition-colors" title={`Copiar ${type}`}>
                <Copy className="w-3.5 h-3.5" />
            </button>
        </div>
    );
}

function ActionBtnLg({ onClick, bg, text, icon: Icon }) {
    return (
        <button onClick={onClick} className={`flex items-center gap-2 px-5 py-3 ${bg} text-white rounded-2xl text-[11px] font-black uppercase tracking-widest shadow-lg hover:brightness-110 active:scale-95 transition-all`}>
            <Icon className="w-4 h-4" /> {text}
        </button>
    );
}

function ChatMessages({ activities, leadName }) {
    const containerRef = useRef(null);
    const messages = [];

    activities.filter(a => a.type === "WhatsApp").forEach(a => {
        const lines = a.description?.split("\n").filter(l => l.trim()) || [];
        const isTranscript = a.createdBy === "WhatsApp Bot";

        if (isTranscript) {
            lines.forEach(line => {
                const botMatch = line.match(/^\[Bot\]:\s*(.*)/);
                const clientMatch = line.match(/^\[Cliente\]:\s*(.*)/);
                if (botMatch) { messages.push({ text: botMatch[1], sender: "bot", time: a.createdAt }); }
                else if (clientMatch) { messages.push({ text: clientMatch[1], sender: "client", time: a.createdAt }); }
                else if (!line.includes(":") && line.length > 5 && !line.includes("capturada")) {
                    messages.push({ text: line, sender: "bot", time: a.createdAt });
                }
            });
        } else {
            const isAgent = a.createdBy && !a.createdBy.startsWith("WhatsApp (");
            messages.push({ text: a.description, sender: isAgent ? "agent" : "client", time: a.createdAt, by: a.createdBy });
        }
    });

    activities.filter(a => a.type !== "WhatsApp").forEach(a => {
        messages.push({ text: `[${a.type}] ${a.description}`, sender: "system", time: a.createdAt, by: a.createdBy });
    });

    messages.sort((a, b) => new Date(a.time) - new Date(b.time));

    useEffect(() => { if (containerRef.current) containerRef.current.scrollTop = containerRef.current.scrollHeight; }, [messages.length]);

    if (messages.length === 0) {
        return (
            <div className="absolute inset-0 flex items-center justify-center p-8">
                <div className="text-center space-y-3">
                    <div className="w-16 h-16 bg-slate-100 dark:bg-slate-800/50 rounded-full flex items-center justify-center mx-auto mb-4">
                        <MessageSquare className="w-8 h-8 text-slate-300" />
                    </div>
                    <p className="text-xs font-black text-slate-400 uppercase tracking-widest">Inicio de Conversación</p>
                    <p className="text-[11px] text-slate-400 max-w-[200px] leading-relaxed">Los mensajes que envíe el cliente por WhatsApp aparecerán aquí automáticamente.</p>
                </div>
            </div>
        );
    }

    return (
        <div ref={containerRef} className="absolute inset-0 overflow-y-auto p-6 space-y-4 custom-scrollbar">
            {messages.map((msg, i) => {
                if (msg.sender === "system") {
                    return (
                        <div key={i} className="flex justify-center">
                            <span className="bg-slate-100 dark:bg-slate-800 text-slate-500 px-3 py-1 rounded-full text-[9px] font-black uppercase tracking-widest">{msg.text} · {timeAgo(msg.time)}</span>
                        </div>
                    );
                }
                const isAgent = msg.sender === "agent";
                const isBot = msg.sender === "bot";
                const isClient = msg.sender === "client";

                return (
                    <div key={i} className={`flex ${isAgent || isClient ? "justify-end" : "justify-start"}`}>
                        <div className={`max-w-[85%] space-y-1 ${isAgent || isClient ? "items-end" : "items-start"}`}>
                            <div className={`relative px-4 py-3 rounded-[1.5rem] text-sm shadow-sm leading-relaxed ${
                                isAgent ? "bg-indigo-600 text-white rounded-br-none" : 
                                isClient ? "bg-emerald-600 text-white rounded-br-none" :
                                "bg-white dark:bg-slate-800 text-slate-700 dark:text-slate-200 rounded-bl-none border border-slate-100 dark:border-slate-700"
                            }`}>
                                {msg.text}
                            </div>
                            <div className={`text-[9px] font-black uppercase tracking-widest text-slate-400 ${isAgent || isClient ? "text-right mr-1" : "ml-1"}`}>
                                {isBot ? "🤖 MagnaBot" : isAgent ? `👤 ${msg.by || "Agente"}` : `📱 ${leadName || "Cliente"}`} · {timeAgo(msg.time)}
                            </div>
                        </div>
                    </div>
                );
            })}
        </div>
    );
}

function KPICard({ label, count, color, icon: Icon, onClick, active }) {
    const variants = {
        slate: "bg-slate-50 dark:bg-slate-800/10 border-slate-200/60 text-slate-600",
        blue: "bg-blue-50 dark:bg-blue-900/10 border-blue-200/60 text-blue-600",
        violet: "bg-violet-50 dark:bg-violet-900/10 border-violet-200/60 text-violet-600",
        emerald: "bg-emerald-50 dark:bg-emerald-900/10 border-emerald-200/60 text-emerald-600",
        rose: "bg-rose-50 dark:bg-rose-900/10 border-rose-200/60 text-rose-600",
        green: "bg-emerald-50 dark:bg-emerald-900/10 border-emerald-200/60 text-emerald-600"
    };
    return (
        <button onClick={onClick} className={`relative p-5 rounded-3xl border-2 text-left transition-all hover:-translate-y-1 hover:shadow-lg ${active ? "ring-4 ring-indigo-500/10 border-indigo-500 bg-white dark:bg-slate-900" : `${variants[color]} border-transparent`}`}>
            <div className={`w-8 h-8 rounded-xl flex items-center justify-center mb-3 ${active ? "bg-indigo-600 text-white" : "bg-white dark:bg-slate-800 shadow-sm"}`}>
                <Icon className="w-4 h-4" />
            </div>
            <div className="text-2xl font-black text-slate-900 dark:text-white leading-none">{count}</div>
            <div className="text-[10px] font-black uppercase tracking-widest text-slate-400 mt-1">{label}</div>
        </button>
    );
}

function LeadFormModal({ sources, onSave, onClose }) {
    const [form, setForm] = useState({ fullName: "", email: "", phone: "", source: "Web", interestedIn: "", travelDates: "", travelers: "", estimatedBudget: 0, notes: "" });
    const set = (k, v) => setForm(p => ({ ...p, [k]: v }));
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 backdrop-blur-md p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-[2rem] w-full max-w-xl max-h-[90vh] overflow-y-auto p-8 space-y-6 shadow-2xl border border-white/20" onClick={e => e.stopPropagation()}>
                <div className="flex items-center justify-between">
                    <h2 className="text-2xl font-black text-slate-900 dark:text-white tracking-tight">Nuevo Lead a Pipeline</h2>
                    <button onClick={onClose} className="p-2 bg-slate-50 dark:bg-slate-800 rounded-xl"><X className="w-5 h-5" /></button>
                </div>
                <div className="space-y-4">
                    <div className="grid grid-cols-1 gap-4">
                        <InputGroup label="Nombre Apellido" value={form.fullName} onChange={v => set("fullName", v)} icon={User} required />
                        <div className="grid grid-cols-2 gap-4">
                            <InputGroup label="WhatsApp / Telefono" value={form.phone} onChange={v => set("phone", v)} icon={Smartphone} />
                            <InputGroup label="Email" value={form.email} onChange={v => set("email", v)} icon={Mail} />
                        </div>
                    </div>
                    <div className="grid grid-cols-2 gap-4">
                        <div className="space-y-1.5">
                            <label className="text-[10px] font-black text-slate-400 uppercase tracking-widest ml-1">Origen</label>
                            <select value={form.source} onChange={e => set("source", e.target.value)} className="w-full px-4 py-3 rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-950 text-sm font-bold focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition-all">
                                {sources.map(s => <option key={s} value={s}>{s}</option>)}
                            </select>
                        </div>
                        <InputGroup label="Interés / Destino" value={form.interestedIn} onChange={v => set("interestedIn", v)} icon={MapPin} />
                    </div>
                    <div className="grid grid-cols-2 gap-4">
                        <InputGroup label="Fechas Estimadas" value={form.travelDates} onChange={v => set("travelDates", v)} icon={CalendarRange} />
                        <InputGroup label="Viajeros" value={form.travelers} onChange={v => set("travelers", v)} icon={Users2} />
                    </div>
                </div>
                <div className="flex gap-4 pt-4">
                    <button onClick={onClose} className="flex-1 py-4 px-6 rounded-2xl text-sm font-black uppercase tracking-widest text-slate-500 hover:bg-slate-50 transition-colors">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.fullName} className="flex-[2] py-4 px-6 bg-indigo-600 text-white rounded-2xl text-sm font-black uppercase tracking-widest shadow-lg shadow-indigo-500/20 hover:brightness-110 active:scale-95 transition-all disabled:opacity-40">Crear Lead</button>
                </div>
            </div>
        </div>
    );
}

function InputGroup({ label, value, onChange, icon: Icon, required, type="text" }) {
    return (
        <div className="space-y-1.5 focus-within:z-10 group">
            <label className="text-[10px] font-black text-slate-400 uppercase tracking-widest ml-1 group-focus-within:text-indigo-500 transition-colors">
                {label} {required && "*"}
            </label>
            <div className="relative">
                <Icon className="absolute left-4 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
                <input type={type} value={value} onChange={e => onChange(e.target.value)} className="w-full pl-11 pr-4 py-3 rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-950 text-sm font-bold focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition-all" />
            </div>
        </div>
    );
}

function ActivityModal({ types, onSave, onClose }) {
    const [form, setForm] = useState({ type: "Nota", description: "" });
    return (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-slate-900/40 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-[2rem] w-full max-w-md p-8 space-y-6 shadow-2xl border border-white/10" onClick={e => e.stopPropagation()}>
                <h2 className="text-xl font-black text-slate-900 dark:text-white tracking-tight">Nueva Actividad Manual</h2>
                <div className="space-y-4">
                    <select value={form.type} onChange={e => setForm(p => ({ ...p, type: e.target.value }))} className="w-full px-4 py-3 rounded-2xl border border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-950 text-sm font-extrabold focus:ring-2 focus:ring-indigo-500/20 transition-all">
                        {types.map(t => <option key={t} value={t}>{t}</option>)}
                    </select>
                    <textarea value={form.description} onChange={e => setForm(p => ({ ...p, description: e.target.value }))} placeholder="Detalles del seguimiento..." rows={3} className="w-full px-5 py-4 rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-950 text-sm font-medium focus:ring-2 focus:ring-indigo-500/20 transition-all" />
                </div>
                <div className="flex gap-4">
                    <button onClick={onClose} className="flex-1 py-3 px-6 rounded-xl text-xs font-black uppercase tracking-widest text-slate-500 hover:bg-slate-50 transition-colors">Cerrar</button>
                    <button onClick={() => onSave(form)} disabled={!form.description} className="flex-1 py-3 px-6 bg-slate-900 dark:bg-indigo-600 text-white rounded-xl text-xs font-black uppercase tracking-widest shadow-lg shadow-black/20 hover:scale-105 active:scale-95 transition-all disabled:opacity-40">Guardar</button>
                </div>
            </div>
        </div>
    );
}
