import { useEffect, useState, useCallback } from "react";
import { useNavigate } from "react-router-dom";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    Plus, Phone, Mail, MapPin, DollarSign, Calendar,
    MessageSquare, ArrowRight, Loader2, X, Check, Send, FileText,
    Smartphone, Search, Filter, TrendingUp, Users, Clock, Eye, Trash2, ChevronRight
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

function getStatusConfig(status) {
    return STATUSES.find(s => s.value === status) || STATUSES[0];
}

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
    const [pipeline, setPipeline] = useState(null);
    const [allLeads, setAllLeads] = useState([]);
    const [loading, setLoading] = useState(true);
    const [showModal, setShowModal] = useState(false);
    const [detailLead, setDetailLead] = useState(null);
    const [detailLoading, setDetailLoading] = useState(false);
    const [showActivityModal, setShowActivityModal] = useState(false);
    const [searchTerm, setSearchTerm] = useState("");
    const [filterStatus, setFilterStatus] = useState("all");
    const [filterSource, setFilterSource] = useState("all");

    const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0 })}`;

    const loadPipeline = useCallback(async () => {
        setLoading(true);
        try {
            const [pipelineData, leadsData] = await Promise.all([
                api.get("/leads/pipeline"),
                api.get("/leads"),
            ]);
            setPipeline(pipelineData);
            setAllLeads(leadsData || []);
        } catch (e) {
            console.error("Load error:", e);
            showError("Error al cargar datos del CRM");
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { loadPipeline(); }, [loadPipeline]);

    const loadDetail = async (id) => {
        setDetailLoading(true);
        try {
            const lead = await api.get(`/leads/${id}`);
            setDetailLead(lead);
        } catch (e) {
            console.error("Detail error:", e);
            showError("Error al cargar detalle");
        } finally {
            setDetailLoading(false);
        }
    };

    const handleCreate = async (data) => {
        try { await api.post("/leads", data); showSuccess("Lead creado"); setShowModal(false); loadPipeline(); }
        catch { showError("Error al crear lead"); }
    };

    const handleStatusChange = async (id, status) => {
        try {
            await api.patch(`/leads/${id}/status`, { status });
            showSuccess(`Estado: ${status}`);
            loadPipeline();
            if (detailLead?.id === id) loadDetail(id);
        } catch { showError("Error al cambiar estado"); }
    };

    const handleConvert = async (id) => {
        const { isConfirmed } = await Swal.fire({ title: "¿Convertir a cliente?", text: "Se creará un cliente nuevo con los datos de este lead.", icon: "question", showCancelButton: true, confirmButtonColor: "#4f46e5" });
        if (!isConfirmed) return;
        try {
            const res = await api.post(`/leads/${id}/convert`);
            showSuccess(`Cliente creado: ID ${res.customerId}`);
            loadPipeline();
            if (detailLead?.id === id) loadDetail(id);
        } catch (e) { showError(e.message || "Error al convertir"); }
    };

    const handleAddActivity = async (activity) => {
        try {
            await api.post(`/leads/${detailLead.id}/activities`, activity);
            showSuccess("Actividad registrada");
            setShowActivityModal(false);
            loadDetail(detailLead.id);
        } catch { showError("Error al registrar actividad"); }
    };

    const handleDelete = async (id) => {
        const { isConfirmed } = await Swal.fire({ title: "¿Eliminar lead?", icon: "warning", showCancelButton: true, confirmButtonColor: "#ef4444" });
        if (!isConfirmed) return;
        try { await api.delete(`/leads/${id}`); showSuccess("Lead eliminado"); loadPipeline(); setDetailLead(null); }
        catch { showError("Error al eliminar"); }
    };

    // ── Filtrar leads ──
    const filteredLeads = allLeads.filter(lead => {
        if (filterStatus !== "all" && lead.status !== filterStatus) return false;
        if (filterSource !== "all" && lead.source !== filterSource) return false;
        if (searchTerm) {
            const q = searchTerm.toLowerCase();
            return (lead.fullName?.toLowerCase().includes(q) ||
                    lead.phone?.toLowerCase().includes(q) ||
                    lead.email?.toLowerCase().includes(q) ||
                    lead.interestedIn?.toLowerCase().includes(q));
        }
        return true;
    });

    if (loading) {
        return (
            <div className="flex items-center justify-center h-[60vh]">
                <Loader2 className="w-8 h-8 animate-spin text-indigo-500" />
            </div>
        );
    }

    // ─── DETAIL PANEL (slide-over) ───────────────────────
    const DetailPanel = () => {
        if (!detailLead) return null;

        const whatsappTranscript = (detailLead.activities || []).find(
            a => a.type === "WhatsApp" && a.createdBy === "WhatsApp Bot"
        );
        const sc = getStatusConfig(detailLead.status);

        return (
            <div className="fixed inset-0 z-50 flex justify-end bg-black/40 backdrop-blur-sm" onClick={() => setDetailLead(null)}>
                <div className="w-full max-w-lg bg-white dark:bg-slate-900 h-full overflow-y-auto shadow-2xl animate-slide-in-right" onClick={e => e.stopPropagation()}>
                    {detailLoading ? (
                        <div className="flex items-center justify-center h-full">
                            <Loader2 className="w-8 h-8 animate-spin text-indigo-500" />
                        </div>
                    ) : (
                        <div className="p-6 space-y-5">
                            {/* Header */}
                            <div className="flex items-start justify-between">
                                <div className="flex-1 min-w-0">
                                    <div className="flex items-center gap-2 mb-1">
                                        <span className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[11px] font-bold ${sc.badge}`}>
                                            <span className={`w-1.5 h-1.5 rounded-full ${sc.dot}`}></span>
                                            {sc.label}
                                        </span>
                                        {detailLead.source === "WhatsApp" && (
                                            <span className="inline-flex items-center gap-1 px-2 py-1 rounded-full bg-emerald-100 dark:bg-emerald-900/30 text-emerald-700 dark:text-emerald-400 text-[11px] font-bold">
                                                <Smartphone className="w-3 h-3" /> WhatsApp Bot
                                            </span>
                                        )}
                                    </div>
                                    <h2 className="text-xl font-black text-slate-900 dark:text-white">{detailLead.fullName}</h2>
                                    <p className="text-sm text-slate-400 mt-0.5">{detailLead.interestedIn || "Sin destino definido"}</p>
                                </div>
                                <button onClick={() => setDetailLead(null)} className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800">
                                    <X className="w-5 h-5 text-slate-400" />
                                </button>
                            </div>

                            {/* Contact Info */}
                            <div className="grid grid-cols-2 gap-3">
                                {detailLead.phone && (
                                    <a href={`tel:${detailLead.phone}`} className="flex items-center gap-2 p-3 rounded-xl bg-slate-50 dark:bg-slate-800/50 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors">
                                        <Phone className="w-4 h-4 text-indigo-500" />
                                        <span className="text-sm font-medium text-slate-700 dark:text-slate-300 truncate">{detailLead.phone}</span>
                                    </a>
                                )}
                                {detailLead.email && (
                                    <a href={`mailto:${detailLead.email}`} className="flex items-center gap-2 p-3 rounded-xl bg-slate-50 dark:bg-slate-800/50 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors">
                                        <Mail className="w-4 h-4 text-indigo-500" />
                                        <span className="text-sm font-medium text-slate-700 dark:text-slate-300 truncate">{detailLead.email}</span>
                                    </a>
                                )}
                                {detailLead.estimatedBudget > 0 && (
                                    <div className="flex items-center gap-2 p-3 rounded-xl bg-slate-50 dark:bg-slate-800/50">
                                        <DollarSign className="w-4 h-4 text-emerald-500" />
                                        <span className="text-sm font-bold text-emerald-600">{fmt(detailLead.estimatedBudget)}</span>
                                    </div>
                                )}
                                {detailLead.nextFollowUp && (
                                    <div className="flex items-center gap-2 p-3 rounded-xl bg-amber-50 dark:bg-amber-900/10">
                                        <Calendar className="w-4 h-4 text-amber-600" />
                                        <span className="text-sm font-medium text-amber-700 dark:text-amber-400">{new Date(detailLead.nextFollowUp).toLocaleDateString("es-AR")}</span>
                                    </div>
                                )}
                            </div>

                            {detailLead.notes && (
                                <div className="bg-amber-50 dark:bg-amber-900/10 border border-amber-200 dark:border-amber-800 rounded-xl p-3 text-sm text-amber-800 dark:text-amber-300">{detailLead.notes}</div>
                            )}

                            {/* Actions */}
                            <div className="flex gap-2 flex-wrap">
                                {detailLead.status === "Nuevo" && <ActionBtn onClick={() => handleStatusChange(detailLead.id, "Contactado")} className="bg-blue-600 text-white hover:bg-blue-700" icon={Send} label="Marcar Contactado" />}
                                {detailLead.status === "Contactado" && <ActionBtn onClick={() => handleStatusChange(detailLead.id, "Cotizado")} className="bg-violet-600 text-white hover:bg-violet-700" icon={FileText} label="Marcar Cotizado" />}
                                {detailLead.status === "Cotizado" && (
                                    <>
                                        <ActionBtn onClick={() => handleStatusChange(detailLead.id, "Ganado")} className="bg-emerald-600 text-white hover:bg-emerald-700" icon={Check} label="Ganado" />
                                        <ActionBtn onClick={() => handleStatusChange(detailLead.id, "Perdido")} className="bg-rose-600 text-white hover:bg-rose-700" icon={X} label="Perdido" />
                                    </>
                                )}
                                {detailLead.status === "Ganado" && !detailLead.convertedCustomerId && (
                                    <ActionBtn onClick={() => handleConvert(detailLead.id)} className="bg-indigo-600 text-white hover:bg-indigo-700" icon={ArrowRight} label="Convertir a Cliente" />
                                )}
                                {detailLead.convertedCustomerId && (
                                    <span className="flex items-center gap-1.5 px-3 py-2 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400 rounded-lg text-xs font-bold">
                                        <Check className="w-3.5 h-3.5" /> Cliente #{detailLead.convertedCustomerId}
                                    </span>
                                )}
                                <ActionBtn onClick={() => setShowActivityModal(true)} className="bg-slate-900 dark:bg-white text-white dark:text-slate-900 hover:opacity-90" icon={MessageSquare} label="Registrar Actividad" />
                                <ActionBtn onClick={() => handleDelete(detailLead.id)} className="text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20" icon={Trash2} label="Eliminar" />
                            </div>

                            {/* WhatsApp Transcript */}
                            {whatsappTranscript && (
                                <div className="bg-emerald-50 dark:bg-emerald-900/10 border border-emerald-200 dark:border-emerald-800 rounded-2xl overflow-hidden">
                                    <div className="px-4 py-2.5 border-b border-emerald-200 dark:border-emerald-800 flex items-center gap-2">
                                        <Smartphone className="w-4 h-4 text-emerald-600" />
                                        <h3 className="text-xs font-black text-emerald-800 dark:text-emerald-300 uppercase tracking-wider">Conversación WhatsApp</h3>
                                    </div>
                                    <div className="px-4 py-3 space-y-1.5 max-h-60 overflow-y-auto">
                                        {whatsappTranscript.description?.split("\n").filter(l => l.trim()).map((line, i) => {
                                            const isBot = line.includes("[Bot]");
                                            const isClient = line.includes("[Cliente]");
                                            const cleanLine = line.replace(/^\[(Bot|Cliente)\]:\s*/, "").replace(/^Conversación capturada por bot:\s*/, "");
                                            if (!cleanLine.trim()) return null;
                                            return (
                                                <div key={i} className={`flex ${isBot ? "justify-start" : "justify-end"}`}>
                                                    <div className={`max-w-[80%] px-3 py-1.5 rounded-xl text-xs ${
                                                        isBot
                                                            ? "bg-white dark:bg-slate-800 text-slate-700 dark:text-slate-300 rounded-bl-sm"
                                                            : isClient
                                                                ? "bg-emerald-600 text-white rounded-br-sm"
                                                                : "bg-slate-200 dark:bg-slate-700 text-slate-800 dark:text-slate-200"
                                                    }`}>
                                                        {cleanLine}
                                                    </div>
                                                </div>
                                            );
                                        })}
                                    </div>
                                </div>
                            )}

                            {/* Activities Timeline */}
                            <div>
                                <h3 className="text-xs font-black text-slate-900 dark:text-white uppercase tracking-wider mb-3">Actividades</h3>
                                {(!detailLead.activities || detailLead.activities.length === 0) ? (
                                    <p className="text-sm text-slate-400 text-center py-4">Sin actividades</p>
                                ) : (
                                    <div className="space-y-2">
                                        {detailLead.activities.map(a => (
                                            <div key={a.id} className="flex gap-3 p-3 rounded-xl bg-slate-50 dark:bg-slate-800/50">
                                                <div className={`w-7 h-7 rounded-full flex items-center justify-center text-[9px] font-black flex-shrink-0 ${
                                                    a.type === "WhatsApp" ? "bg-emerald-100 dark:bg-emerald-900/30 text-emerald-600" : "bg-indigo-100 dark:bg-indigo-900/30 text-indigo-600"
                                                }`}>
                                                    {a.type === "WhatsApp" ? "WA" : a.type?.substring(0, 2).toUpperCase()}
                                                </div>
                                                <div className="flex-1 min-w-0">
                                                    <div className="text-xs text-slate-700 dark:text-slate-300 whitespace-pre-wrap line-clamp-3">{a.description}</div>
                                                    <div className="text-[10px] text-slate-400 mt-1">{a.type} · {timeAgo(a.createdAt)} · {a.createdBy || "Sistema"}</div>
                                                </div>
                                            </div>
                                        ))}
                                    </div>
                                )}
                            </div>
                        </div>
                    )}
                </div>
                {showActivityModal && <ActivityModal types={ACTIVITY_TYPES} onSave={handleAddActivity} onClose={() => setShowActivityModal(false)} />}
            </div>
        );
    };

    // ── KPI counts ──
    const counts = {
        nuevo: allLeads.filter(l => l.status === "Nuevo").length,
        contactado: allLeads.filter(l => l.status === "Contactado").length,
        cotizado: allLeads.filter(l => l.status === "Cotizado").length,
        ganado: allLeads.filter(l => l.status === "Ganado").length,
        perdido: allLeads.filter(l => l.status === "Perdido").length,
    };
    const whatsappCount = allLeads.filter(l => l.source === "WhatsApp").length;

    // ─── MAIN VIEW ──────────────────────────────────────
    return (
        <div className="space-y-6 pb-12">
            {/* Header */}
            <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-black text-slate-900 dark:text-white tracking-tight">CRM Pipeline</h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
                        {allLeads.length} leads · {fmt(pipeline?.totalBudget)} potencial · {pipeline?.conversionRate || 0}% conversión
                    </p>
                </div>
                <button onClick={() => setShowModal(true)} className="flex items-center gap-2 px-4 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold shadow-lg hover:bg-indigo-700 transition-colors">
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

            {/* Search & Filters */}
            <div className="flex flex-col sm:flex-row gap-3">
                <div className="relative flex-1">
                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
                    <input
                        type="text" value={searchTerm} onChange={e => setSearchTerm(e.target.value)}
                        placeholder="Buscar por nombre, teléfono, email o destino..."
                        className="w-full pl-10 pr-4 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent transition-all"
                    />
                </div>
                {(filterStatus !== "all" || filterSource !== "all" || searchTerm) && (
                    <button onClick={() => { setFilterStatus("all"); setFilterSource("all"); setSearchTerm(""); }}
                        className="flex items-center gap-1.5 px-4 py-2.5 rounded-xl text-sm font-medium text-slate-500 hover:text-slate-700 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors">
                        <X className="w-4 h-4" /> Limpiar filtros
                    </button>
                )}
            </div>

            {/* Leads Table */}
            <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden">
                {filteredLeads.length === 0 ? (
                    <div className="px-6 py-16 text-center">
                        <Users className="w-10 h-10 text-slate-300 dark:text-slate-600 mx-auto mb-3" />
                        <p className="text-sm font-medium text-slate-500">No se encontraron leads</p>
                        <p className="text-xs text-slate-400 mt-1">Probá ajustando los filtros o creá uno nuevo</p>
                    </div>
                ) : (
                    <div className="divide-y divide-slate-100 dark:divide-slate-800">
                        {/* Table Header */}
                        <div className="hidden md:grid md:grid-cols-[1fr_120px_140px_120px_100px_50px] gap-4 px-6 py-3 bg-slate-50 dark:bg-slate-800/50 text-[10px] font-black text-slate-400 uppercase tracking-wider">
                            <span>Lead</span>
                            <span>Estado</span>
                            <span>Interés</span>
                            <span>Fuente</span>
                            <span>Ingreso</span>
                            <span></span>
                        </div>

                        {/* Rows */}
                        {filteredLeads.map(lead => {
                            const sc = getStatusConfig(lead.status);
                            return (
                                <div key={lead.id} onClick={() => loadDetail(lead.id)}
                                    className="grid grid-cols-1 md:grid-cols-[1fr_120px_140px_120px_100px_50px] gap-2 md:gap-4 px-6 py-4 items-center cursor-pointer hover:bg-indigo-50/50 dark:hover:bg-indigo-900/10 transition-colors group">
                                    {/* Lead Name + Contact */}
                                    <div className="flex items-center gap-3 min-w-0">
                                        <div className={`w-9 h-9 rounded-full flex items-center justify-center text-xs font-black flex-shrink-0 ${
                                            lead.source === "WhatsApp"
                                                ? "bg-emerald-100 dark:bg-emerald-900/30 text-emerald-600"
                                                : "bg-indigo-100 dark:bg-indigo-900/30 text-indigo-600"
                                        }`}>
                                            {lead.source === "WhatsApp"
                                                ? <Smartphone className="w-4 h-4" />
                                                : lead.fullName?.split(" ").map(w => w[0]).join("").substring(0, 2).toUpperCase()
                                            }
                                        </div>
                                        <div className="min-w-0">
                                            <div className="text-sm font-bold text-slate-900 dark:text-white truncate">{lead.fullName}</div>
                                            <div className="text-[11px] text-slate-400 truncate">
                                                {lead.phone || lead.email || "Sin contacto"}
                                            </div>
                                        </div>
                                    </div>

                                    {/* Status Badge */}
                                    <div>
                                        <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-bold ${sc.badge}`}>
                                            <span className={`w-1.5 h-1.5 rounded-full ${sc.dot}`}></span>
                                            {sc.label}
                                        </span>
                                    </div>

                                    {/* Interest */}
                                    <div className="text-xs text-slate-600 dark:text-slate-400 truncate">
                                        {lead.interestedIn ? (
                                            <span className="flex items-center gap-1"><MapPin className="w-3 h-3 flex-shrink-0 text-indigo-400" />{lead.interestedIn}</span>
                                        ) : "—"}
                                    </div>

                                    {/* Source */}
                                    <div className="text-xs text-slate-500">
                                        {lead.source === "WhatsApp" ? (
                                            <span className="flex items-center gap-1 text-emerald-600 font-medium"><Smartphone className="w-3 h-3" />WhatsApp</span>
                                        ) : (lead.source || "—")}
                                    </div>

                                    {/* Time */}
                                    <div className="text-[11px] text-slate-400 flex items-center gap-1">
                                        <Clock className="w-3 h-3" />{timeAgo(lead.createdAt)}
                                    </div>

                                    {/* Action */}
                                    <div className="flex justify-end">
                                        <ChevronRight className="w-4 h-4 text-slate-300 group-hover:text-indigo-500 transition-colors" />
                                    </div>
                                </div>
                            );
                        })}
                    </div>
                )}
            </div>

            {/* Modals */}
            {showModal && <LeadFormModal sources={SOURCES} onSave={handleCreate} onClose={() => setShowModal(false)} />}
            <DetailPanel />
        </div>
    );
}

// ─── Sub-components ──────────────────────────────────────

function KPICard({ label, count, color, icon: Icon, onClick, active }) {
    const colors = {
        slate: "border-slate-200 dark:border-slate-700",
        blue: "border-blue-200 dark:border-blue-800",
        violet: "border-violet-200 dark:border-violet-800",
        emerald: "border-emerald-200 dark:border-emerald-800",
        rose: "border-rose-200 dark:border-rose-800",
        green: "border-emerald-200 dark:border-emerald-800",
    };
    const iconColors = {
        slate: "text-slate-400", blue: "text-blue-500", violet: "text-violet-500",
        emerald: "text-emerald-500", rose: "text-rose-500", green: "text-emerald-500",
    };

    return (
        <button onClick={onClick}
            className={`relative p-4 rounded-xl border-2 text-left transition-all hover:shadow-md ${
                active ? "ring-2 ring-indigo-500 border-indigo-300 dark:border-indigo-700 bg-indigo-50/50 dark:bg-indigo-900/10" : `${colors[color]} bg-white dark:bg-slate-900 hover:border-indigo-200`
            }`}>
            <Icon className={`w-4 h-4 ${iconColors[color]} mb-2`} />
            <div className="text-2xl font-black text-slate-900 dark:text-white">{count}</div>
            <div className="text-[10px] font-bold text-slate-400 uppercase tracking-wider">{label}</div>
        </button>
    );
}

function ActionBtn({ onClick, className, icon: Icon, label }) {
    return (
        <button onClick={onClick} className={`flex items-center gap-1.5 px-3 py-2 rounded-lg text-xs font-bold transition-colors ${className}`}>
            <Icon className="w-3.5 h-3.5" /> {label}
        </button>
    );
}

function LeadFormModal({ sources, onSave, onClose }) {
    const [form, setForm] = useState({ fullName: "", email: "", phone: "", source: "", interestedIn: "", estimatedBudget: 0, notes: "", nextFollowUp: "" });
    const set = (k, v) => setForm(p => ({ ...p, [k]: v }));

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-2xl w-full max-w-lg max-h-[90vh] overflow-y-auto p-6 space-y-4" onClick={e => e.stopPropagation()}>
                <h2 className="text-lg font-black text-slate-900 dark:text-white">Nuevo Lead</h2>
                <input value={form.fullName} onChange={e => set("fullName", e.target.value)} placeholder="Nombre completo *" className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" />
                <div className="grid grid-cols-2 gap-3">
                    <input value={form.email} onChange={e => set("email", e.target.value)} placeholder="Email" className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" />
                    <input value={form.phone} onChange={e => set("phone", e.target.value)} placeholder="Teléfono" className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" />
                </div>
                <div className="grid grid-cols-2 gap-3">
                    <select value={form.source} onChange={e => set("source", e.target.value)} className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent">
                        <option value="">Fuente</option>
                        {sources.map(s => <option key={s} value={s}>{s}</option>)}
                    </select>
                    <input value={form.interestedIn} onChange={e => set("interestedIn", e.target.value)} placeholder="Interesado en..." className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" />
                </div>
                <div className="grid grid-cols-2 gap-3">
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Presupuesto Estimado</label><input type="number" value={form.estimatedBudget} onChange={e => set("estimatedBudget", parseFloat(e.target.value) || 0)} className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" /></div>
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Próximo Seguimiento</label><input type="date" value={form.nextFollowUp} onChange={e => set("nextFollowUp", e.target.value)} className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" /></div>
                </div>
                <textarea value={form.notes} onChange={e => set("notes", e.target.value)} placeholder="Notas" rows={2} className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" />
                <div className="flex gap-3 justify-end pt-2">
                    <button onClick={onClose} className="px-4 py-2.5 rounded-xl text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.fullName} className="px-5 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold hover:bg-indigo-700 disabled:opacity-40 transition-colors">Crear Lead</button>
                </div>
            </div>
        </div>
    );
}

function ActivityModal({ types, onSave, onClose }) {
    const [form, setForm] = useState({ type: "Nota", description: "" });
    return (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-2xl w-full max-w-md p-6 space-y-4" onClick={e => e.stopPropagation()}>
                <h2 className="text-lg font-black text-slate-900 dark:text-white">Registrar Actividad</h2>
                <select value={form.type} onChange={e => setForm(p => ({ ...p, type: e.target.value }))} className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent">
                    {types.map(t => <option key={t} value={t}>{t}</option>)}
                </select>
                <textarea value={form.description} onChange={e => setForm(p => ({ ...p, description: e.target.value }))} placeholder="¿Qué pasó? *" rows={3} className="w-full px-3 py-2.5 rounded-xl border border-slate-200 dark:border-slate-700 bg-transparent text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent" />
                <div className="flex gap-3 justify-end">
                    <button onClick={onClose} className="px-4 py-2.5 rounded-xl text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.description} className="px-5 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold hover:bg-indigo-700 disabled:opacity-40 transition-colors">Guardar</button>
                </div>
            </div>
        </div>
    );
}
