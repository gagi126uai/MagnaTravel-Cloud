import { useCallback, useEffect, useRef, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
    ArrowRight,
    CalendarRange,
    Check,
    ChevronRight,
    Clock,
    Copy,
    DollarSign,
    FileText,
    Info,
    Loader2,
    Mail,
    MapPin,
    MessageSquare,
    Phone,
    Plus,
    RefreshCw,
    Search,
    Send,
    Smartphone,
    Trash2,
    User,
    Users,
    Users2,
    X
} from "lucide-react";
import { api } from "../api";
import { showConfirm, showError, showSuccess } from "../alerts";
import { PaginationFooter } from "../components/ui/PaginationFooter";
import { MobileRecordCard, MobileRecordList } from "../components/ui/MobileRecordCard";
import { getPublicId, getRelatedPublicId } from "../lib/publicIds";

const STATUSES = [
    { value: "Nuevo", label: "Consulta nueva", dot: "bg-slate-400", badge: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300" },
    { value: "Contactado", label: "En seguimiento", dot: "bg-blue-500", badge: "bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400" },
    { value: "Cotizado", label: "Cotizacion enviada", dot: "bg-violet-500", badge: "bg-violet-50 text-violet-700 dark:bg-violet-900/30 dark:text-violet-400" },
    { value: "Ganado", label: "Reserva confirmada", dot: "bg-emerald-500", badge: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400" },
    { value: "Perdido", label: "No continuo", dot: "bg-rose-500", badge: "bg-rose-50 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400" },
];

const ACTIVITY_TYPES = ["Llamada", "Email", "WhatsApp", "Reunion", "Nota", "Cotizacion"];
const SOURCES = ["Web", "WhatsApp", "Referido", "Telefono", "Instagram", "Otro"];
const EMPTY_PAGE = {
    items: [],
    page: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0,
    hasPreviousPage: false,
    hasNextPage: false,
};

function getStatusConfig(status) {
    return STATUSES.find((item) => item.value === status) || STATUSES[0];
}

function isClosedStatus(status) {
    return status === "Ganado" || status === "Perdido";
}

function getPipelineCounts(pipeline) {
    const groups = [
        ...(pipeline?.Nuevo || []),
        ...(pipeline?.Contactado || []),
        ...(pipeline?.Cotizado || []),
        ...(pipeline?.Ganado || []),
        ...(pipeline?.Perdido || [])
    ];

    return {
        nuevo: pipeline?.Nuevo?.length || 0,
        contactado: pipeline?.Contactado?.length || 0,
        cotizado: pipeline?.Cotizado?.length || 0,
        ganado: pipeline?.Ganado?.length || 0,
        perdido: pipeline?.Perdido?.length || 0,
        whatsapp: groups.filter((item) => item.source === "WhatsApp").length,
    };
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
    const location = useLocation();
    const [leadsPage, setLeadsPage] = useState(EMPTY_PAGE);
    const [counts, setCounts] = useState({ nuevo: 0, contactado: 0, cotizado: 0, ganado: 0, perdido: 0, whatsapp: 0 });
    const [loading, setLoading] = useState(true);
    const [showModal, setShowModal] = useState(false);
    const [detailLead, setDetailLead] = useState(null);
    const [detailJourney, setDetailJourney] = useState(null);
    const [detailLoading, setDetailLoading] = useState(false);
    const [showActivityModal, setShowActivityModal] = useState(false);
    const [searchTerm, setSearchTerm] = useState("");
    const [viewMode, setViewMode] = useState("active");
    const [filterStatus, setFilterStatus] = useState("all");
    const [filterSource, setFilterSource] = useState("all");
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);
    const [creatingQuote, setCreatingQuote] = useState(false);
    const [chatMessage, setChatMessage] = useState("");
    const [sendingChat, setSendingChat] = useState(false);

    const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0 })}`;

    const loadLeads = useCallback(async () => {
        setLoading(true);
        try {
            const params = new URLSearchParams({
                view: viewMode,
                page: String(page),
                pageSize: String(pageSize),
                sortBy: "createdAt",
                sortDir: "desc",
            });

            if (searchTerm.trim()) params.set("search", searchTerm.trim());
            if (filterStatus !== "all") params.set("status", filterStatus);
            if (filterSource !== "all") params.set("source", filterSource);

            const [pagedData, pipelineData] = await Promise.all([
                api.get(`/leads?${params.toString()}`),
                api.get("/leads/pipeline"),
            ]);

            setLeadsPage({ ...EMPTY_PAGE, ...(pagedData || {}) });
            setCounts(getPipelineCounts(pipelineData));
        } catch (error) {
            console.error("Error loading possible customers:", error);
            setLeadsPage(EMPTY_PAGE);
            showError("Error al cargar posibles clientes");
        } finally {
            setLoading(false);
        }
    }, [filterSource, filterStatus, page, pageSize, searchTerm, viewMode]);

    useEffect(() => { loadLeads(); }, [loadLeads]);
    useEffect(() => {
        const interval = setInterval(loadLeads, 30000);
        return () => clearInterval(interval);
    }, [loadLeads]);
    useEffect(() => { setPage(1); }, [searchTerm, filterStatus, filterSource, pageSize, viewMode]);

    const loadDetail = useCallback(async (id) => {
        setDetailLoading(true);
        try {
            const [lead, journey] = await Promise.all([
                api.get(`/leads/${id}`),
                api.get(`/leads/${id}/journey`).catch(() => null),
            ]);
            setDetailLead(lead);
            setDetailJourney(journey);
        } catch (error) {
            console.error("Detail error:", error);
            showError("Error al cargar detalle");
        } finally {
            setDetailLoading(false);
        }
    }, []);

    useEffect(() => {
        const openLeadId = location.state?.openLeadId;
        if (!openLeadId) return;
        loadDetail(openLeadId);
        navigate(location.pathname, { replace: true, state: {} });
    }, [loadDetail, location.pathname, location.state, navigate]);

    const handleCreate = async (data) => {
        try {
            await api.post("/leads", data);
            showSuccess("Posible cliente creado");
            setShowModal(false);
            loadLeads();
        } catch {
            showError("Error al crear el posible cliente");
        }
    };

    const handleStatusChange = async (id, status) => {
        try {
            await api.patch(`/leads/${id}/status`, { status });
            showSuccess(`Estado actualizado: ${getStatusConfig(status).label}`);
            loadLeads();
            if (getPublicId(detailLead) === id) loadDetail(id);
        } catch {
            showError("Error al cambiar estado");
        }
    };

    const handleConvert = async (id) => {
        const confirmed = await showConfirm(
            "Crear cliente",
            "Se abrira la ficha del cliente para continuar la gestion sin cerrar este posible cliente.",
            "Si, crear cliente"
        );
        if (!confirmed) return;

        try {
            const res = await api.post(`/leads/${id}/convert`);
            showSuccess("Cliente creado");
            loadLeads();
            if (getPublicId(detailLead) === id) loadDetail(id);
            if (res?.customerPublicId) navigate(`/customers/${res.customerPublicId}/account`);
        } catch (error) {
            showError(error.message || "Error al convertir");
        }
    };

    const handleAddActivity = async (activity) => {
        try {
            await api.post(`/leads/${getPublicId(detailLead)}/activities`, activity);
            showSuccess("Actividad registrada");
            setShowActivityModal(false);
            loadDetail(getPublicId(detailLead));
        } catch {
            showError("Error al registrar actividad");
        }
    };

    const handleDelete = async (id) => {
        const confirmed = await showConfirm("Eliminar posible cliente", "Esta gestion se eliminara por completo.", "Si, eliminar", "red");
        if (!confirmed) return;

        try {
            await api.delete(`/leads/${id}`);
            showSuccess("Posible cliente eliminado");
            loadLeads();
            setDetailLead(null);
            setDetailJourney(null);
        } catch {
            showError("Error al eliminar");
        }
    };

    const handleCreateQuoteDraft = async (id) => {
        try {
            setCreatingQuote(true);
            const res = await api.post(`/leads/${id}/quote-draft`);
            showSuccess(`Cotizacion lista: ${res.quoteNumber || "nueva"}`);
            setDetailLead(null);
            setDetailJourney(null);
            loadLeads();
            navigate("/quotes", { state: { openQuoteId: res.quotePublicId } });
        } catch (error) {
            showError(error.message || "Error al crear cotizacion");
        } finally {
            setCreatingQuote(false);
        }
    };

    const handleSendChat = async () => {
        if (!chatMessage.trim() || !getPublicId(detailLead)) return;
        setSendingChat(true);
        try {
            await api.post(`/leads/${getPublicId(detailLead)}/whatsapp-message`, { message: chatMessage.trim() });
            setChatMessage("");
            loadDetail(getPublicId(detailLead));
        } catch (error) {
            showError(error.message || "Error al enviar mensaje");
        } finally {
            setSendingChat(false);
        }
    };

    const copyToClipboard = (text, label) => {
        navigator.clipboard.writeText(text);
        showSuccess(`${label} copiado`);
    };

    const handleViewModeChange = (nextView) => {
        setViewMode(nextView);
        if (nextView === "active" && isClosedStatus(filterStatus)) setFilterStatus("all");
        if (nextView === "closed" && filterStatus !== "all" && !isClosedStatus(filterStatus)) setFilterStatus("all");
    };

    const handleStatusCardClick = (status) => {
        setViewMode(isClosedStatus(status) ? "closed" : "active");
        setFilterStatus((current) => current === status ? "all" : status);
    };

    if (loading && (leadsPage.items || []).length === 0) {
        return <div className="flex h-[60vh] items-center justify-center"><Loader2 className="h-8 w-8 animate-spin text-indigo-500" /></div>;
    }

    const leads = leadsPage.items || [];

    return (
        <div className="space-y-6 pb-12">
            <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
                <div>
                    <h1 className="text-2xl font-black tracking-tight text-slate-900 dark:text-white">Posibles clientes</h1>
                    <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{viewMode === "active" ? "Gestiones activas" : "Gestiones cerradas"} · {leadsPage.totalCount || 0} resultados</p>
                </div>
                <button onClick={() => setShowModal(true)} className="flex items-center gap-2 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-bold text-white shadow-lg shadow-indigo-500/20 transition-all hover:-translate-y-0.5 hover:bg-indigo-700 active:translate-y-0">
                    <Plus className="h-4 w-4" /> Nuevo posible cliente
                </button>
            </div>

            <div className="flex flex-wrap items-center gap-3">
                <SegmentedView value={viewMode} onChange={handleViewModeChange} />
                <select value={filterSource} onChange={(event) => setFilterSource(event.target.value)} className="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-200">
                    <option value="all">Todos los canales</option>
                    {SOURCES.map((source) => <option key={source} value={source}>{source}</option>)}
                </select>
                {(filterStatus !== "all" || filterSource !== "all" || searchTerm) && (
                    <button onClick={() => { setFilterStatus("all"); setFilterSource("all"); setSearchTerm(""); setViewMode("active"); }} className="rounded-xl px-4 py-2 text-sm font-bold text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800">
                        Limpiar filtros
                    </button>
                )}
            </div>

            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
                <KPICard label="Consultas nuevas" count={counts.nuevo} color="slate" icon={Users} onClick={() => handleStatusCardClick("Nuevo")} active={filterStatus === "Nuevo"} />
                <KPICard label="En seguimiento" count={counts.contactado} color="blue" icon={Send} onClick={() => handleStatusCardClick("Contactado")} active={filterStatus === "Contactado"} />
                <KPICard label="Cotizacion enviada" count={counts.cotizado} color="violet" icon={FileText} onClick={() => handleStatusCardClick("Cotizado")} active={filterStatus === "Cotizado"} />
                <KPICard label="Reserva confirmada" count={counts.ganado} color="emerald" icon={Check} onClick={() => handleStatusCardClick("Ganado")} active={filterStatus === "Ganado"} />
                <KPICard label="No continuo" count={counts.perdido} color="rose" icon={X} onClick={() => handleStatusCardClick("Perdido")} active={filterStatus === "Perdido"} />
                <KPICard label="WhatsApp" count={counts.whatsapp} color="green" icon={Smartphone} onClick={() => setFilterSource((current) => current === "WhatsApp" ? "all" : "WhatsApp")} active={filterSource === "WhatsApp"} />
            </div>

            <div className="relative group">
                <Search className="absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400 transition-colors group-focus-within:text-indigo-500" />
                <input type="text" value={searchTerm} onChange={(event) => setSearchTerm(event.target.value)} placeholder="Buscar por nombre, telefono, email o destino..." className="w-full rounded-xl border border-slate-200 bg-white py-3 pl-11 pr-4 text-sm shadow-sm transition-all focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-900" />
            </div>

            {leads.length === 0 ? (
                <div className="rounded-3xl border border-slate-200/60 bg-white px-6 py-16 text-center shadow-sm dark:border-slate-800/60 dark:bg-slate-900 md:hidden">
                    <div className="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-slate-50 dark:bg-slate-800">
                        <Users className="h-8 w-8 text-slate-300 dark:text-slate-600" />
                    </div>
                    <p className="text-base font-bold text-slate-400">No hay posibles clientes en esta vista</p>
                </div>
            ) : (
                <MobileRecordList className="space-y-3">
                    {leads.map((lead) => <LeadMobileCard key={getPublicId(lead)} lead={lead} onOpen={() => loadDetail(getPublicId(lead))} />)}
                </MobileRecordList>
            )}

            <div className="hidden overflow-hidden rounded-3xl border border-slate-200/60 bg-white shadow-xl shadow-slate-200/50 dark:border-slate-800/60 dark:bg-slate-900 dark:shadow-none md:block">
                {leads.length === 0 ? (
                    <div className="px-6 py-20 text-center">
                        <div className="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-slate-50 dark:bg-slate-800">
                            <Users className="h-8 w-8 text-slate-300 dark:text-slate-600" />
                        </div>
                        <p className="text-base font-bold text-slate-400">No hay posibles clientes en esta vista</p>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full border-collapse text-left">
                            <thead>
                                <tr className="border-b border-slate-100 bg-slate-50/50 text-[11px] font-black uppercase tracking-widest text-slate-400 dark:border-slate-800 dark:bg-slate-800/30">
                                    <th className="px-6 py-4">Posible cliente</th>
                                    <th className="px-4 py-4">Estado</th>
                                    <th className="px-4 py-4">Interes</th>
                                    <th className="px-4 py-4">Canal</th>
                                    <th className="px-4 py-4 text-right">Ingreso</th>
                                    <th className="px-6 py-4"></th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100/60 dark:divide-slate-800/60">
                                {leads.map((lead) => <LeadRow key={getPublicId(lead)} lead={lead} onOpen={() => loadDetail(getPublicId(lead))} />)}
                            </tbody>
                        </table>
                    </div>
                )}

                <div className="border-t border-slate-200/60 bg-white px-4 py-4 dark:border-slate-800/60 dark:bg-slate-900">
                    <PaginationFooter
                        page={leadsPage.page || page}
                        pageSize={leadsPage.pageSize || pageSize}
                        totalCount={leadsPage.totalCount || 0}
                        totalPages={leadsPage.totalPages || 0}
                        hasPreviousPage={Boolean(leadsPage.hasPreviousPage)}
                        hasNextPage={Boolean(leadsPage.hasNextPage)}
                        onPageChange={setPage}
                        onPageSizeChange={setPageSize}
                    />
                </div>
            </div>

            {leads.length > 0 ? (
                <div className="md:hidden">
                    <PaginationFooter
                        page={leadsPage.page || page}
                        pageSize={leadsPage.pageSize || pageSize}
                        totalCount={leadsPage.totalCount || 0}
                        totalPages={leadsPage.totalPages || 0}
                        hasPreviousPage={Boolean(leadsPage.hasPreviousPage)}
                        hasNextPage={Boolean(leadsPage.hasNextPage)}
                        onPageChange={setPage}
                        onPageSizeChange={setPageSize}
                    />
                </div>
            ) : null}

            {showModal && <LeadFormModal sources={SOURCES} onSave={handleCreate} onClose={() => setShowModal(false)} />}
            {detailLead && (
                <DetailModal
                    detailLead={detailLead}
                    detailJourney={detailJourney}
                    detailLoading={detailLoading}
                    chatMessage={chatMessage}
                    creatingQuote={creatingQuote}
                    sendingChat={sendingChat}
                    fmt={fmt}
                    onClose={() => { setDetailLead(null); setDetailJourney(null); }}
                    onLoadDetail={loadDetail}
                    onConvert={handleConvert}
                    onCreateQuoteDraft={handleCreateQuoteDraft}
                    onStatusChange={handleStatusChange}
                    onDelete={handleDelete}
                    onSetChatMessage={setChatMessage}
                    onSendChat={handleSendChat}
                    onCopyToClipboard={copyToClipboard}
                    onShowActivityModal={() => setShowActivityModal(true)}
                    navigate={navigate}
                />
            )}
            {showActivityModal && <ActivityModal types={ACTIVITY_TYPES} onSave={handleAddActivity} onClose={() => setShowActivityModal(false)} />}
        </div>
    );
}

function SegmentedView({ value, onChange }) {
    return (
        <div className="inline-flex rounded-2xl border border-slate-200 bg-slate-50 p-1 dark:border-slate-800 dark:bg-slate-900">
            {[
                { value: "active", label: "Activos" },
                { value: "closed", label: "Cerrados" },
            ].map((option) => (
                <button key={option.value} type="button" onClick={() => onChange(option.value)} className={`rounded-xl px-4 py-2 text-sm font-bold transition-colors ${value === option.value ? "bg-white text-slate-900 shadow-sm dark:bg-slate-800 dark:text-white" : "text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white"}`}>
                    {option.label}
                </button>
            ))}
        </div>
    );
}

function LeadRow({ lead, onOpen }) {
    const statusConfig = getStatusConfig(lead.status);

    return (
        <tr onClick={onOpen} className="group cursor-pointer transition-colors hover:bg-indigo-50/30 dark:hover:bg-indigo-900/5">
            <td className="px-6 py-4">
                <div className="flex items-center gap-3">
                    <div className={`flex h-10 w-10 flex-shrink-0 items-center justify-center rounded-2xl text-xs font-black shadow-sm ${lead.source === "WhatsApp" ? "bg-emerald-100 text-emerald-600 dark:bg-emerald-900/30" : "bg-indigo-100 text-indigo-600 dark:bg-indigo-900/30"}`}>
                        {lead.source === "WhatsApp" ? <Smartphone className="h-5 w-5" /> : lead.fullName?.[0]?.toUpperCase()}
                    </div>
                    <div className="min-w-0">
                        <div className="truncate text-sm font-bold text-slate-900 transition-colors group-hover:text-indigo-600 dark:text-white">{lead.fullName}</div>
                        <div className="truncate text-[11px] text-slate-400">{lead.phone || lead.email || "Sin contacto"}</div>
                    </div>
                </div>
            </td>
            <td className="px-4 py-4">
                <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[10px] font-bold ${statusConfig.badge}`}>
                    <span className={`h-1.5 w-1.5 rounded-full ${statusConfig.dot}`}></span>
                    {statusConfig.label}
                </span>
            </td>
            <td className="px-4 py-4 text-xs font-medium text-slate-600 dark:text-slate-400">
                {lead.interestedIn ? <span className="flex items-center gap-1.5"><MapPin className="h-3.5 w-3.5 text-indigo-400" />{lead.interestedIn}</span> : "Pendiente"}
            </td>
            <td className="px-4 py-4">
                <span className={`inline-flex items-center gap-1.5 text-xs font-bold ${lead.source === "WhatsApp" ? "text-emerald-500" : "text-slate-500"}`}>
                    {lead.source === "WhatsApp" && <Smartphone className="h-3.5 w-3.5" />}
                    {lead.source || "Directo"}
                </span>
            </td>
            <td className="px-4 py-4 text-right text-[11px] font-medium text-slate-400">{timeAgo(lead.createdAt)}</td>
            <td className="px-6 py-4 text-right"><div className="flex justify-end"><div className="flex h-8 w-8 items-center justify-center rounded-full bg-white shadow-sm dark:bg-slate-800"><ChevronRight className="h-4 w-4 text-indigo-600" /></div></div></td>
        </tr>
    );
}

function LeadMobileCard({ lead, onOpen }) {
    const statusConfig = getStatusConfig(lead.status);

    return (
        <MobileRecordCard
            onClick={onOpen}
            accentSlot={
                <div className={`flex h-10 w-10 items-center justify-center rounded-2xl text-xs font-black shadow-sm ${lead.source === "WhatsApp" ? "bg-emerald-100 text-emerald-600 dark:bg-emerald-900/30" : "bg-indigo-100 text-indigo-600 dark:bg-indigo-900/30"}`}>
                    {lead.source === "WhatsApp" ? <Smartphone className="h-5 w-5" /> : lead.fullName?.[0]?.toUpperCase()}
                </div>
            }
            statusSlot={
                <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[10px] font-bold ${statusConfig.badge}`}>
                    <span className={`h-1.5 w-1.5 rounded-full ${statusConfig.dot}`}></span>
                    {statusConfig.label}
                </span>
            }
            title={lead.fullName}
            subtitle={lead.phone || lead.email || "Sin contacto"}
            meta={
                <>
                    <span className="flex items-center gap-2 text-xs">
                        <MapPin className="h-3.5 w-3.5 text-indigo-400" />
                        {lead.interestedIn || "Interes pendiente"}
                    </span>
                    <span className={`inline-flex items-center gap-1.5 text-xs font-bold ${lead.source === "WhatsApp" ? "text-emerald-500" : "text-slate-500"}`}>
                        {lead.source === "WhatsApp" ? <Smartphone className="h-3.5 w-3.5" /> : null}
                        {lead.source || "Directo"}
                    </span>
                </>
            }
            footer={<span className="text-xs text-slate-500 dark:text-slate-400">Ingreso {timeAgo(lead.createdAt)}</span>}
            footerActions={
                <div className="flex h-8 w-8 items-center justify-center rounded-full bg-slate-100 dark:bg-slate-800">
                    <ChevronRight className="h-4 w-4 text-indigo-600" />
                </div>
            }
        />
    );
}

function DetailModal({ detailLead, detailJourney, detailLoading, chatMessage, creatingQuote, sendingChat, fmt, onClose, onLoadDetail, onConvert, onCreateQuoteDraft, onStatusChange, onDelete, onSetChatMessage, onSendChat, onCopyToClipboard, onShowActivityModal, navigate }) {
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 p-4 backdrop-blur-md animate-in fade-in duration-300" onClick={onClose}>
            <div className="flex h-[85vh] w-full max-w-6xl flex-col overflow-hidden rounded-[2.5rem] border border-white/20 bg-white shadow-2xl shadow-indigo-500/10 md:flex-row dark:border-slate-800 dark:bg-slate-900" onClick={(event) => event.stopPropagation()}>
                {detailLoading ? <div className="flex w-full items-center justify-center bg-white dark:bg-slate-900"><Loader2 className="h-10 w-10 animate-spin text-indigo-500" /></div> : (
                    <>
                        <div className="flex flex-col border-r border-slate-100 bg-white md:w-[55%] dark:border-slate-800 dark:bg-slate-900">
                            <div className="custom-scrollbar flex-1 space-y-8 overflow-y-auto p-8">
                                <div className="flex items-start justify-between">
                                    <div className="space-y-3">
                                        <div className="flex items-center gap-2">
                                            <span className={`inline-flex items-center gap-2 rounded-full px-3 py-1 text-[11px] font-black uppercase tracking-wider ${getStatusConfig(detailLead.status).badge}`}>
                                                <span className={`h-2 w-2 rounded-full ${getStatusConfig(detailLead.status).dot} animate-pulse`}></span>{getStatusConfig(detailLead.status).label}
                                            </span>
                                            {detailLead.source === "WhatsApp" && <span className="inline-flex items-center gap-2 rounded-full border border-emerald-500/20 bg-emerald-500/10 px-3 py-1 text-[11px] font-black uppercase tracking-wider text-emerald-600 dark:text-emerald-400"><Smartphone className="h-3.5 w-3.5" /> Consulta por WhatsApp</span>}
                                        </div>
                                        <h2 className="text-4xl font-black leading-tight text-slate-900 dark:text-white">{detailLead.fullName}</h2>
                                        <div className="flex items-center gap-4 text-sm font-medium text-slate-400"><span className="flex items-center gap-1.5"><Clock className="h-4 w-4" /> Ingreso {timeAgo(detailLead.createdAt)}</span><span className="h-1 w-1 rounded-full bg-slate-300"></span><span className="flex items-center gap-1.5"><Info className="h-4 w-4" /> Gestion comercial</span></div>
                                    </div>
                                    <button onClick={onClose} className="group rounded-2xl bg-slate-50 p-3 transition-colors hover:bg-slate-100 dark:bg-slate-800 dark:hover:bg-slate-700"><X className="h-6 w-6 text-slate-400 transition-transform group-hover:scale-110" /></button>
                                </div>

                                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                                    <DetailBox icon={MapPin} label="Destino de interes" value={detailLead.interestedIn || "No especificado"} sub="Informacion comercial" />
                                    <DetailBox icon={CalendarRange} label="Fechas estimadas" value={detailLead.travelDates || "A definir"} sub="Ventana de viaje" color="blue" />
                                    <DetailBox icon={Users2} label="Viajeros" value={detailLead.travelers || "1 viajero"} sub="Cantidad estimada" color="violet" />
                                    <DetailBox icon={DollarSign} label="Presupuesto" value={detailLead.estimatedBudget > 0 ? fmt(detailLead.estimatedBudget) : "Sin definir"} sub="Valor potencial" color="emerald" />
                                </div>

                                <div className="flex flex-wrap gap-3">
                                    {detailLead.phone && <ContactChip icon={Phone} text={detailLead.phone} type="Telefono" onCopy={() => onCopyToClipboard(detailLead.phone, "Telefono")} href={`tel:${detailLead.phone}`} />}
                                    {detailLead.email && <ContactChip icon={Mail} text={detailLead.email} type="Email" onCopy={() => onCopyToClipboard(detailLead.email, "Email")} href={`mailto:${detailLead.email}`} />}
                                </div>

                                <JourneyGrid detailLead={detailLead} detailJourney={detailJourney} creatingQuote={creatingQuote} onCreateQuoteDraft={onCreateQuoteDraft} navigate={navigate} />
                                <div className="flex flex-wrap items-center gap-3">
                                    {!getRelatedPublicId(detailLead, "convertedCustomerPublicId", "convertedCustomerId") && detailLead.status !== "Perdido" && <ActionBtnLg onClick={() => onConvert(getPublicId(detailLead))} bg="bg-indigo-600" text="Crear cliente" icon={User} />}
                                    {!detailJourney?.latestQuotePublicId && detailLead.status !== "Perdido" && <ActionBtnLg onClick={() => onCreateQuoteDraft(getPublicId(detailLead))} bg="bg-violet-600" text={creatingQuote ? "Creando..." : "Crear cotizacion"} icon={FileText} disabled={creatingQuote} />}
                                    {detailJourney?.latestQuotePublicId && <ActionBtnLg onClick={() => navigate("/quotes", { state: { openQuoteId: detailJourney.latestQuotePublicId } })} bg="bg-slate-900" text="Abrir cotizacion" icon={ArrowRight} />}
                                    {detailJourney?.latestReservaPublicId && <ActionBtnLg onClick={() => navigate(`/reservas/${detailJourney.latestReservaPublicId}`)} bg="bg-emerald-600" text="Abrir reserva" icon={ArrowRight} />}
                                </div>

                                <div className="flex flex-wrap items-center gap-3">
                                    {detailLead.status === "Nuevo" && <ActionBtnLg onClick={() => onStatusChange(getPublicId(detailLead), "Contactado")} bg="bg-blue-600" text="Marcar seguimiento" icon={Send} />}
                                    {detailLead.status === "Contactado" && detailJourney?.latestQuotePublicId && <ActionBtnLg onClick={() => onStatusChange(getPublicId(detailLead), "Cotizado")} bg="bg-violet-600" text="Marcar cotizacion enviada" icon={FileText} />}
                                    {!isClosedStatus(detailLead.status) && <ActionBtnLg onClick={() => onStatusChange(getPublicId(detailLead), "Perdido")} bg="bg-rose-500" text="Marcar no continuo" icon={X} />}
                                    {getRelatedPublicId(detailLead, "convertedCustomerPublicId", "convertedCustomerId") && <span className="flex items-center gap-2 rounded-2xl bg-emerald-100/50 px-6 py-3 text-xs font-black uppercase tracking-widest text-emerald-700"><Check className="h-4 w-4" /> Cliente registrado</span>}
                                    <div className="flex-1"></div>
                                    <button onClick={() => onDelete(getPublicId(detailLead))} className="rounded-2xl p-3 text-rose-400 transition-all hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/10"><Trash2 className="h-5 w-5" /></button>
                                </div>
                            </div>
                        </div>

                        <div className="flex flex-col bg-slate-50 md:w-[45%] dark:bg-slate-950/20">
                            <div className="flex items-center justify-between border-b border-white px-8 py-6 dark:border-slate-800/50">
                                <div className="flex items-center gap-3">
                                    <div className="flex h-10 w-10 items-center justify-center rounded-2xl bg-emerald-500 text-white shadow-lg shadow-emerald-500/20"><Smartphone className="h-5 w-5" /></div>
                                    <div><h3 className="text-sm font-black uppercase tracking-wider text-slate-900 dark:text-white">Chat WhatsApp</h3><div className="mt-0.5 flex items-center gap-1.5"><span className="pulse h-1.5 w-1.5 rounded-full bg-emerald-500"></span><span className="text-[10px] font-bold uppercase tracking-widest text-slate-400">Linea abierta con {detailLead.phone || "sin telefono"}</span></div></div>
                                </div>
                                <div className="flex items-center gap-2">
                                    <button onClick={onShowActivityModal} className="rounded-xl bg-white p-2.5 text-slate-400 shadow-sm transition-colors hover:text-indigo-500 dark:bg-slate-800" title="Registrar actividad"><MessageSquare className="h-4 w-4" /></button>
                                    <button onClick={() => onLoadDetail(getPublicId(detailLead))} className="rounded-xl bg-white p-2.5 text-slate-400 shadow-sm transition-colors hover:text-indigo-500 dark:bg-slate-800" title="Actualizar"><RefreshCw className="h-4 w-4" /></button>
                                </div>
                            </div>
                            <div className="relative flex-1"><ChatMessages activities={detailLead.activities || []} leadName={detailLead.fullName} /></div>
                            {detailLead.phone && detailLead.source === "WhatsApp" ? (
                                <div className="border-t border-slate-100 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
                                    <div className="flex items-center gap-2 rounded-3xl border border-slate-100 bg-slate-50 p-2 transition-all focus-within:ring-2 focus-within:ring-emerald-500/20 dark:border-slate-800 dark:bg-slate-950">
                                        <input type="text" value={chatMessage} onChange={(event) => onSetChatMessage(event.target.value)} onKeyDown={(event) => { if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); onSendChat(); } }} placeholder="Escribir respuesta oficial..." className="flex-1 border-none bg-transparent px-4 py-3 text-sm text-slate-700 focus:ring-0 dark:text-slate-200" disabled={sendingChat} />
                                        <button onClick={onSendChat} disabled={sendingChat || !chatMessage.trim()} className={`flex h-12 w-12 items-center justify-center rounded-2xl transition-all ${chatMessage.trim() ? "bg-emerald-600 text-white shadow-lg shadow-emerald-500/20 hover:scale-105" : "cursor-not-allowed bg-slate-200 text-slate-400 dark:bg-slate-800"}`}>{sendingChat ? <Loader2 className="h-5 w-5 animate-spin" /> : <Send className="h-5 w-5" />}</button>
                                    </div>
                                    <div className="mt-3 flex items-center justify-center"><div className="rounded-full border border-emerald-100 bg-emerald-50 px-3 py-1 text-[9px] font-black uppercase tracking-widest text-emerald-600 dark:border-emerald-800/30 dark:bg-emerald-900/20">Envio via MagnaBot</div></div>
                                </div>
                            ) : <div className="border-t border-slate-100 bg-slate-50 p-6 text-center dark:border-slate-800 dark:bg-slate-900"><p className="text-xs font-medium text-slate-400">Chat disponible solo para consultas por WhatsApp</p></div>}
                        </div>
                    </>
                )}
            </div>
        </div>
    );
}

function JourneyGrid({ detailLead, detailJourney, creatingQuote, onCreateQuoteDraft, navigate }) {
    return (
        <div className="space-y-3">
            <h3 className="pl-1 text-xs font-black uppercase tracking-widest text-slate-400">Recorrido comercial</h3>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                <JourneyCard label="Cliente" value={detailJourney?.convertedCustomerName || (getRelatedPublicId(detailLead, "convertedCustomerPublicId", "convertedCustomerId") ? "Cliente registrado" : "Pendiente")} meta={getRelatedPublicId(detailLead, "convertedCustomerPublicId", "convertedCustomerId") ? "Ficha disponible" : "Todavia no creado"} actionLabel={getRelatedPublicId(detailLead, "convertedCustomerPublicId", "convertedCustomerId") ? "Abrir cuenta" : "Pendiente"} disabled={!getRelatedPublicId(detailLead, "convertedCustomerPublicId", "convertedCustomerId")} onAction={() => { const customerPublicId = getRelatedPublicId(detailLead, "convertedCustomerPublicId", "convertedCustomerId"); if (customerPublicId) navigate(`/customers/${customerPublicId}/account`); }} />
                <JourneyCard label="Cotizacion" value={detailJourney?.quotes?.[0]?.quoteNumber || "Sin cotizacion"} meta={detailJourney?.quotes?.[0]?.status || "Todavia no creada"} actionLabel={detailJourney?.latestQuotePublicId ? "Abrir" : "Crear"} disabled={creatingQuote} onAction={() => { if (detailJourney?.latestQuotePublicId) { navigate("/quotes", { state: { openQuoteId: detailJourney.latestQuotePublicId } }); return; } onCreateQuoteDraft(getPublicId(detailLead)); }} />
                <JourneyCard label="Reserva" value={detailJourney?.reservas?.[0]?.numeroReserva || "Sin reserva"} meta={detailJourney?.reservas?.[0]?.status || "Todavia no confirmada"} actionLabel={detailJourney?.latestReservaPublicId ? "Abrir" : "Pendiente"} disabled={!detailJourney?.latestReservaPublicId} onAction={() => detailJourney?.latestReservaPublicId && navigate(`/reservas/${detailJourney.latestReservaPublicId}`)} />
            </div>
        </div>
    );
}

function DetailBox({ icon: Icon, label, value, sub, color = "indigo" }) {
    const colors = { indigo: "border-indigo-100/50 bg-indigo-50 text-indigo-600 dark:bg-indigo-900/10 dark:text-indigo-400", blue: "border-blue-100/50 bg-blue-50 text-blue-600 dark:bg-blue-900/10 dark:text-blue-400", violet: "border-violet-100/50 bg-violet-50 text-violet-600 dark:bg-violet-900/10 dark:text-violet-400", emerald: "border-emerald-100/50 bg-emerald-50 text-emerald-600 dark:bg-emerald-900/10 dark:text-emerald-400" };
    return <div className={`space-y-2 rounded-3xl border p-4 ${colors[color]}`}><div className="flex items-center gap-2"><Icon className="h-3.5 w-3.5 opacity-70" /><span className="text-[10px] font-black uppercase tracking-widest opacity-70">{label}</span></div><div><div className="truncate text-sm font-black">{value}</div><div className="text-[10px] font-medium opacity-60">{sub}</div></div></div>;
}

function ContactChip({ icon: Icon, text, type, onCopy, href }) {
    return <div className="group flex items-center gap-1"><a href={href} className="flex items-center gap-2 rounded-2xl border border-slate-100 bg-slate-50 pl-3 pr-4 py-2 transition-colors hover:bg-slate-100 dark:border-slate-800 dark:bg-slate-800 dark:hover:bg-slate-700"><Icon className="h-3.5 w-3.5 text-indigo-500" /><span className="text-xs font-bold text-slate-600 dark:text-slate-300">{text}</span></a><button onClick={onCopy} className="p-2 text-slate-300 transition-colors hover:text-indigo-500" title={`Copiar ${type}`}><Copy className="h-3.5 w-3.5" /></button></div>;
}

function ActionBtnLg({ onClick, bg, text, icon: Icon, disabled = false }) {
    return <button onClick={onClick} disabled={disabled} className={`flex items-center gap-2 rounded-2xl px-5 py-3 text-[11px] font-black uppercase tracking-widest text-white shadow-lg transition-all hover:brightness-110 active:scale-95 disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:brightness-100 ${bg}`}><Icon className="h-4 w-4" /> {text}</button>;
}

function JourneyCard({ label, value, meta, actionLabel, disabled, onAction }) {
    return <div className="space-y-3 rounded-3xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div><div className="text-[10px] font-black uppercase tracking-widest text-slate-400">{label}</div><div className="mt-2 text-sm font-black text-slate-900 dark:text-white">{value}</div><div className="mt-1 text-[11px] text-slate-500 dark:text-slate-400">{meta || "Sin vinculacion todavia"}</div></div><button onClick={onAction} disabled={disabled} className="w-full rounded-2xl bg-slate-100 px-3 py-2 text-xs font-black uppercase tracking-widest text-slate-700 disabled:cursor-not-allowed disabled:opacity-40 dark:bg-slate-800 dark:text-slate-200">{actionLabel}</button></div>;
}

function ChatMessages({ activities, leadName }) {
    const containerRef = useRef(null);
    const messages = [];
    activities.filter((activity) => activity.type === "WhatsApp").forEach((activity) => {
        const lines = activity.description?.split("\n").filter((line) => line.trim()) || [];
        const isTranscript = activity.createdBy === "WhatsApp Bot";
        if (isTranscript) {
            lines.forEach((line) => {
                const botMatch = line.match(/^\[Bot\]:\s*(.*)/);
                const clientMatch = line.match(/^\[Cliente\]:\s*(.*)/);
                if (botMatch) messages.push({ text: botMatch[1], sender: "bot", time: activity.createdAt });
                else if (clientMatch) messages.push({ text: clientMatch[1], sender: "client", time: activity.createdAt });
                else if (!line.includes(":") && line.length > 5 && !line.toLowerCase().includes("capturada")) messages.push({ text: line, sender: "bot", time: activity.createdAt });
            });
        } else {
            const isAgent = activity.createdBy && !activity.createdBy.startsWith("WhatsApp (");
            messages.push({ text: activity.description, sender: isAgent ? "agent" : "client", time: activity.createdAt, by: activity.createdBy });
        }
    });
    activities.filter((activity) => activity.type !== "WhatsApp").forEach((activity) => { messages.push({ text: `[${activity.type}] ${activity.description}`, sender: "system", time: activity.createdAt }); });
    messages.sort((a, b) => new Date(a.time) - new Date(b.time));
    useEffect(() => { if (containerRef.current) containerRef.current.scrollTop = containerRef.current.scrollHeight; }, [messages.length]);
    if (messages.length === 0) return <div className="absolute inset-0 flex items-center justify-center p-8"><div className="space-y-3 text-center"><div className="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-slate-100 dark:bg-slate-800/50"><MessageSquare className="h-8 w-8 text-slate-300" /></div><p className="text-xs font-black uppercase tracking-widest text-slate-400">Inicio de conversacion</p><p className="max-w-[220px] text-[11px] leading-relaxed text-slate-400">Los mensajes del cliente por WhatsApp apareceran aqui automaticamente.</p></div></div>;
    return <div ref={containerRef} className="custom-scrollbar absolute inset-0 space-y-4 overflow-y-auto p-6">{messages.map((msg, index) => { if (msg.sender === "system") return <div key={index} className="flex justify-center"><span className="rounded-full bg-slate-100 px-3 py-1 text-[9px] font-black uppercase tracking-widest text-slate-500 dark:bg-slate-800">{msg.text} · {timeAgo(msg.time)}</span></div>; const isAgent = msg.sender === "agent"; const isBot = msg.sender === "bot"; const isClient = msg.sender === "client"; return <div key={index} className={`flex ${isAgent || isClient ? "justify-end" : "justify-start"}`}><div className={`max-w-[85%] space-y-1 ${isAgent || isClient ? "items-end" : "items-start"}`}><div className={`relative rounded-[1.5rem] px-4 py-3 text-sm leading-relaxed shadow-sm ${isAgent ? "rounded-br-none bg-indigo-600 text-white" : isClient ? "rounded-br-none bg-emerald-600 text-white" : "rounded-bl-none border border-slate-100 bg-white text-slate-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200"}`}>{msg.text}</div><div className={`text-[9px] font-black uppercase tracking-widest text-slate-400 ${isAgent || isClient ? "mr-1 text-right" : "ml-1"}`}>{isBot ? "MagnaBot" : isAgent ? (msg.by || "Agente") : (leadName || "Cliente")} · {timeAgo(msg.time)}</div></div></div>; })}</div>;
}

function KPICard({ label, count, color, icon: Icon, onClick, active }) {
    const variants = { slate: "border-transparent bg-slate-50 text-slate-600 dark:bg-slate-800/10", blue: "border-transparent bg-blue-50 text-blue-600 dark:bg-blue-900/10", violet: "border-transparent bg-violet-50 text-violet-600 dark:bg-violet-900/10", emerald: "border-transparent bg-emerald-50 text-emerald-600 dark:bg-emerald-900/10", rose: "border-transparent bg-rose-50 text-rose-600 dark:bg-rose-900/10", green: "border-transparent bg-emerald-50 text-emerald-600 dark:bg-emerald-900/10" };
    return <button onClick={onClick} className={`relative rounded-3xl border-2 p-5 text-left transition-all hover:-translate-y-1 hover:shadow-lg ${active ? "border-indigo-500 bg-white ring-4 ring-indigo-500/10 dark:bg-slate-900" : variants[color]}`}><div className={`mb-3 flex h-8 w-8 items-center justify-center rounded-xl ${active ? "bg-indigo-600 text-white" : "bg-white shadow-sm dark:bg-slate-800"}`}><Icon className="h-4 w-4" /></div><div className="text-2xl font-black leading-none text-slate-900 dark:text-white">{count}</div><div className="mt-1 text-[10px] font-black uppercase tracking-widest text-slate-400">{label}</div></button>;
}

function LeadFormModal({ sources, onSave, onClose }) {
    const [form, setForm] = useState({ fullName: "", email: "", phone: "", source: "Web", interestedIn: "", travelDates: "", travelers: "", estimatedBudget: 0, notes: "" });
    const setField = (key, value) => setForm((previous) => ({ ...previous, [key]: value }));
    return <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 p-4 backdrop-blur-md" onClick={onClose}><div className="max-h-[90vh] w-full max-w-xl space-y-6 overflow-y-auto rounded-[2rem] border border-white/20 bg-white p-8 shadow-2xl dark:bg-slate-900" onClick={(event) => event.stopPropagation()}><div className="flex items-center justify-between"><h2 className="text-2xl font-black tracking-tight text-slate-900 dark:text-white">Nuevo posible cliente</h2><button onClick={onClose} className="rounded-xl bg-slate-50 p-2 dark:bg-slate-800"><X className="h-5 w-5" /></button></div><div className="space-y-4"><div className="grid grid-cols-1 gap-4"><InputGroup label="Nombre y apellido" value={form.fullName} onChange={(value) => setField("fullName", value)} icon={User} required /><div className="grid grid-cols-2 gap-4"><InputGroup label="WhatsApp / Telefono" value={form.phone} onChange={(value) => setField("phone", value)} icon={Smartphone} /><InputGroup label="Email" value={form.email} onChange={(value) => setField("email", value)} icon={Mail} /></div></div><div className="grid grid-cols-2 gap-4"><div className="space-y-1.5"><label className="ml-1 text-[10px] font-black uppercase tracking-widest text-slate-400">Origen</label><select value={form.source} onChange={(event) => setField("source", event.target.value)} className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 text-sm font-bold transition-all focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-800 dark:bg-slate-950">{sources.map((source) => <option key={source} value={source}>{source}</option>)}</select></div><InputGroup label="Interes / destino" value={form.interestedIn} onChange={(value) => setField("interestedIn", value)} icon={MapPin} /></div><div className="grid grid-cols-2 gap-4"><InputGroup label="Fechas estimadas" value={form.travelDates} onChange={(value) => setField("travelDates", value)} icon={CalendarRange} /><InputGroup label="Viajeros" value={form.travelers} onChange={(value) => setField("travelers", value)} icon={Users2} /></div></div><div className="flex gap-4 pt-4"><button onClick={onClose} className="flex-1 rounded-2xl px-6 py-4 text-sm font-black uppercase tracking-widest text-slate-500 transition-colors hover:bg-slate-50">Cancelar</button><button onClick={() => onSave(form)} disabled={!form.fullName} className="flex-[2] rounded-2xl bg-indigo-600 px-6 py-4 text-sm font-black uppercase tracking-widest text-white shadow-lg shadow-indigo-500/20 transition-all hover:brightness-110 active:scale-95 disabled:opacity-40">Crear gestion</button></div></div></div>;
}

function InputGroup({ label, value, onChange, icon: Icon, required, type = "text" }) {
    return <div className="group z-10 space-y-1.5"><label className="ml-1 text-[10px] font-black uppercase tracking-widest text-slate-400 transition-colors group-focus-within:text-indigo-500">{label} {required && "*"}</label><div className="relative"><Icon className="absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" /><input type={type} value={value} onChange={(event) => onChange(event.target.value)} className="w-full rounded-2xl border border-slate-200 bg-white py-3 pl-11 pr-4 text-sm font-bold transition-all focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-800 dark:bg-slate-950" /></div></div>;
}

function ActivityModal({ types, onSave, onClose }) {
    const [form, setForm] = useState({ type: "Nota", description: "" });
    return <div className="fixed inset-0 z-[60] flex items-center justify-center bg-slate-900/40 p-4 backdrop-blur-sm" onClick={onClose}><div className="w-full max-w-md space-y-6 rounded-[2rem] border border-white/10 bg-white p-8 shadow-2xl dark:bg-slate-900" onClick={(event) => event.stopPropagation()}><h2 className="text-xl font-black tracking-tight text-slate-900 dark:text-white">Nueva actividad manual</h2><div className="space-y-4"><select value={form.type} onChange={(event) => setForm((previous) => ({ ...previous, type: event.target.value }))} className="w-full rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-extrabold transition-all focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-800 dark:bg-slate-950">{types.map((type) => <option key={type} value={type}>{type}</option>)}</select><textarea value={form.description} onChange={(event) => setForm((previous) => ({ ...previous, description: event.target.value }))} placeholder="Detalles del seguimiento..." rows={3} className="w-full rounded-2xl border border-slate-200 bg-white px-5 py-4 text-sm font-medium transition-all focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-800 dark:bg-slate-950" /></div><div className="flex gap-4"><button onClick={onClose} className="flex-1 rounded-xl px-6 py-3 text-xs font-black uppercase tracking-widest text-slate-500 transition-colors hover:bg-slate-50">Cerrar</button><button onClick={() => onSave(form)} disabled={!form.description} className="flex-1 rounded-xl bg-slate-900 px-6 py-3 text-xs font-black uppercase tracking-widest text-white shadow-lg shadow-black/20 transition-all hover:scale-105 active:scale-95 disabled:opacity-40 dark:bg-indigo-600">Guardar</button></div></div></div>;
}
