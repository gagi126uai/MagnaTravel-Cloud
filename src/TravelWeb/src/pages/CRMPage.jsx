import { useEffect, useState, useCallback } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    Plus, User, Phone, Mail, MapPin, DollarSign, Calendar,
    MessageSquare, ArrowRight, Loader2, ChevronDown, X, Check, Send
} from "lucide-react";
import Swal from "sweetalert2";

const STAGES = [
    { key: "nuevo", label: "Nuevo", color: "border-t-slate-400", bg: "bg-slate-50 dark:bg-slate-800/30" },
    { key: "contactado", label: "Contactado", color: "border-t-blue-500", bg: "bg-blue-50/30 dark:bg-blue-900/10" },
    { key: "cotizado", label: "Cotizado", color: "border-t-violet-500", bg: "bg-violet-50/30 dark:bg-violet-900/10" },
    { key: "ganado", label: "Ganado", color: "border-t-emerald-500", bg: "bg-emerald-50/30 dark:bg-emerald-900/10" },
    { key: "perdido", label: "Perdido", color: "border-t-rose-500", bg: "bg-rose-50/30 dark:bg-rose-900/10" },
];

const ACTIVITY_TYPES = ["Llamada", "Email", "WhatsApp", "Reunión", "Nota", "Cotización"];
const SOURCES = ["Web", "WhatsApp", "Referido", "Teléfono", "Instagram", "Otro"];

export default function CRMPage() {
    const [pipeline, setPipeline] = useState(null);
    const [loading, setLoading] = useState(true);
    const [showModal, setShowModal] = useState(false);
    const [detailLead, setDetailLead] = useState(null);
    const [showActivityModal, setShowActivityModal] = useState(false);

    const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0 })}`;

    const loadPipeline = useCallback(async () => {
        setLoading(true);
        try {
            const data = await api.get("/leads/pipeline");
            setPipeline(data);
        } catch { showError("Error al cargar pipeline"); }
        finally { setLoading(false); }
    }, []);

    useEffect(() => { loadPipeline(); }, [loadPipeline]);

    const loadDetail = async (id) => {
        try { const lead = await api.get(`/leads/${id}`); setDetailLead(lead); }
        catch { showError("Error al cargar detalle"); }
    };

    const handleCreate = async (data) => {
        try { await api.post("/leads", data); showSuccess("Lead creado"); setShowModal(false); loadPipeline(); }
        catch { showError("Error al crear lead"); }
    };

    const handleStatusChange = async (id, status) => {
        try { await api.patch(`/leads/${id}/status`, { status }); showSuccess(`Estado: ${status}`); loadPipeline(); if (detailLead?.id === id) loadDetail(id); }
        catch { showError("Error al cambiar estado"); }
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

    if (loading) {
        return (
            <div className="flex items-center justify-center h-[60vh]">
                <Loader2 className="w-8 h-8 animate-spin text-indigo-500" />
            </div>
        );
    }

    // DETAIL VIEW
    if (detailLead) {
        return (
            <div className="space-y-6 pb-12 max-w-2xl mx-auto">
                <div className="flex items-center gap-3">
                    <button onClick={() => setDetailLead(null)} className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800"><X className="w-5 h-5" /></button>
                    <div className="flex-1">
                        <h1 className="text-xl font-black text-slate-900 dark:text-white">{detailLead.fullName}</h1>
                        <p className="text-xs text-slate-400">{detailLead.source || "Sin fuente"} | {detailLead.interestedIn || "Sin interés definido"}</p>
                    </div>
                </div>

                {/* Info Cards */}
                <div className="grid grid-cols-2 gap-3">
                    <InfoCard icon={Mail} label="Email" value={detailLead.email || "—"} />
                    <InfoCard icon={Phone} label="Teléfono" value={detailLead.phone || "—"} />
                    <InfoCard icon={DollarSign} label="Presupuesto Est." value={fmt(detailLead.estimatedBudget)} />
                    <InfoCard icon={Calendar} label="Próximo Seguimiento" value={detailLead.nextFollowUp ? new Date(detailLead.nextFollowUp).toLocaleDateString("es-AR") : "—"} />
                </div>

                {detailLead.notes && (
                    <div className="bg-amber-50 dark:bg-amber-900/10 border border-amber-200 dark:border-amber-800 rounded-xl p-4 text-sm text-amber-800 dark:text-amber-300">{detailLead.notes}</div>
                )}

                {/* Actions */}
                <div className="flex gap-2 flex-wrap">
                    {detailLead.status === "Nuevo" && <StatusButton onClick={() => handleStatusChange(detailLead.id, "Contactado")} color="blue" icon={Send} label="Contactado" />}
                    {detailLead.status === "Contactado" && <StatusButton onClick={() => handleStatusChange(detailLead.id, "Cotizado")} color="violet" icon={Send} label="Cotizado" />}
                    {detailLead.status === "Cotizado" && (
                        <>
                            <StatusButton onClick={() => handleStatusChange(detailLead.id, "Ganado")} color="emerald" icon={Check} label="Ganado" />
                            <StatusButton onClick={() => handleStatusChange(detailLead.id, "Perdido")} color="rose" icon={X} label="Perdido" />
                        </>
                    )}
                    {detailLead.status === "Ganado" && !detailLead.convertedCustomerId && (
                        <button onClick={() => handleConvert(detailLead.id)} className="flex items-center gap-2 px-3 py-2 bg-indigo-600 text-white rounded-lg text-xs font-bold hover:bg-indigo-700"><ArrowRight className="w-3.5 h-3.5" /> Convertir a Cliente</button>
                    )}
                    {detailLead.convertedCustomerId && (
                        <span className="flex items-center gap-2 px-3 py-2 bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400 rounded-lg text-xs font-bold"><Check className="w-3.5 h-3.5" /> Cliente #{detailLead.convertedCustomerId}</span>
                    )}
                    <button onClick={() => setShowActivityModal(true)} className="flex items-center gap-2 px-3 py-2 bg-slate-900 dark:bg-white text-white dark:text-slate-900 rounded-lg text-xs font-bold hover:opacity-90"><MessageSquare className="w-3.5 h-3.5" /> Registrar Actividad</button>
                    <button onClick={() => handleDelete(detailLead.id)} className="flex items-center gap-2 px-3 py-2 text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20 rounded-lg text-xs font-bold">Eliminar</button>
                </div>

                {/* Activities Timeline */}
                <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden">
                    <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800">
                        <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider">Timeline de Actividades</h3>
                    </div>
                    <div className="divide-y divide-slate-50 dark:divide-slate-800/50">
                        {(!detailLead.activities || detailLead.activities.length === 0) ? (
                            <div className="px-6 py-8 text-center text-sm text-slate-400">No hay actividades registradas.</div>
                        ) : detailLead.activities.map(a => (
                            <div key={a.id} className="px-6 py-3 flex gap-3">
                                <div className="w-8 h-8 rounded-full bg-indigo-50 dark:bg-indigo-900/20 text-indigo-600 flex items-center justify-center text-[10px] font-black flex-shrink-0 mt-0.5">
                                    {a.type?.substring(0, 2).toUpperCase()}
                                </div>
                                <div className="flex-1 min-w-0">
                                    <div className="text-sm text-slate-900 dark:text-white">{a.description}</div>
                                    <div className="text-[10px] text-slate-400 mt-1">{a.type} • {new Date(a.createdAt).toLocaleString("es-AR")} • {a.createdBy || "Sistema"}</div>
                                </div>
                            </div>
                        ))}
                    </div>
                </div>

                {showActivityModal && <ActivityModal types={ACTIVITY_TYPES} onSave={handleAddActivity} onClose={() => setShowActivityModal(false)} />}
            </div>
        );
    }

    // KANBAN VIEW
    return (
        <div className="space-y-6 pb-12">
            <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-black text-slate-900 dark:text-white tracking-tight">CRM Pipeline</h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
                        {pipeline?.totalLeads || 0} leads | {fmt(pipeline?.totalBudget)} potencial | {pipeline?.conversionRate || 0}% conversión
                    </p>
                </div>
                <button onClick={() => setShowModal(true)} className="flex items-center gap-2 px-4 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold shadow-lg hover:bg-indigo-700">
                    <Plus className="w-4 h-4" /> Nuevo Lead
                </button>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-3 lg:grid-cols-5 gap-4 items-start">
                {STAGES.map(stage => {
                    const leads = pipeline?.[stage.label] || [];
                    return (
                        <div key={stage.key} className={`rounded-2xl border-t-4 ${stage.color} ${stage.bg} border border-slate-200 dark:border-slate-800 min-h-[200px]`}>
                            <div className="px-4 py-3 flex items-center justify-between">
                                <span className="text-xs font-black text-slate-700 dark:text-slate-300 uppercase tracking-wider">{stage.label}</span>
                                <span className="text-[10px] font-bold bg-white dark:bg-slate-800 px-2 py-0.5 rounded-full text-slate-500">{leads.length}</span>
                            </div>
                            <div className="px-3 pb-3 space-y-2">
                                {leads.map(lead => (
                                    <div key={lead.id} onClick={() => loadDetail(lead.id)}
                                        className="bg-white dark:bg-slate-900 rounded-xl p-3 shadow-sm border border-slate-100 dark:border-slate-800 cursor-pointer hover:shadow-md hover:border-indigo-200 dark:hover:border-indigo-800 transition-all">
                                        <div className="text-sm font-bold text-slate-900 dark:text-white truncate">{lead.fullName}</div>
                                        {lead.interestedIn && <div className="text-[10px] text-indigo-500 font-bold mt-0.5 flex items-center gap-1"><MapPin className="w-3 h-3" />{lead.interestedIn}</div>}
                                        <div className="flex items-center justify-between mt-2">
                                            <span className="text-[10px] text-slate-400">{lead.source || "—"}</span>
                                            {lead.estimatedBudget > 0 && <span className="text-[10px] font-black text-emerald-600">{fmt(lead.estimatedBudget)}</span>}
                                        </div>
                                        {lead.nextFollowUp && (
                                            <div className="mt-1.5 text-[9px] bg-amber-50 dark:bg-amber-900/20 text-amber-700 dark:text-amber-400 px-2 py-0.5 rounded font-bold flex items-center gap-1">
                                                <Calendar className="w-2.5 h-2.5" /> {new Date(lead.nextFollowUp).toLocaleDateString("es-AR")}
                                            </div>
                                        )}
                                    </div>
                                ))}
                            </div>
                        </div>
                    );
                })}
            </div>

            {showModal && <LeadFormModal sources={SOURCES} onSave={handleCreate} onClose={() => setShowModal(false)} />}
        </div>
    );
}

function InfoCard({ icon: Icon, label, value }) {
    return (
        <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 p-4">
            <div className="flex items-center gap-2 mb-1">
                <Icon className="w-3.5 h-3.5 text-slate-400" />
                <span className="text-[10px] font-bold text-slate-400 uppercase">{label}</span>
            </div>
            <span className="text-sm font-bold text-slate-900 dark:text-white">{value}</span>
        </div>
    );
}

function StatusButton({ onClick, color, icon: Icon, label }) {
    const colors = {
        blue: "bg-blue-600 text-white hover:bg-blue-700",
        violet: "bg-violet-600 text-white hover:bg-violet-700",
        emerald: "bg-emerald-600 text-white hover:bg-emerald-700",
        rose: "bg-rose-600 text-white hover:bg-rose-700",
    };
    return (
        <button onClick={onClick} className={`flex items-center gap-2 px-3 py-2 rounded-lg text-xs font-bold ${colors[color]}`}>
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
                <input value={form.fullName} onChange={e => set("fullName", e.target.value)} placeholder="Nombre completo *" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="grid grid-cols-2 gap-3">
                    <input value={form.email} onChange={e => set("email", e.target.value)} placeholder="Email" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                    <input value={form.phone} onChange={e => set("phone", e.target.value)} placeholder="Teléfono" className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                </div>
                <div className="grid grid-cols-2 gap-3">
                    <select value={form.source} onChange={e => set("source", e.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm">
                        <option value="">Fuente</option>
                        {sources.map(s => <option key={s} value={s}>{s}</option>)}
                    </select>
                    <input value={form.interestedIn} onChange={e => set("interestedIn", e.target.value)} placeholder="Interesado en..." className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                </div>
                <div className="grid grid-cols-2 gap-3">
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Presupuesto Estimado</label><input type="number" value={form.estimatedBudget} onChange={e => set("estimatedBudget", parseFloat(e.target.value) || 0)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                    <div><label className="text-[10px] font-bold text-slate-400 uppercase">Próximo Seguimiento</label><input type="date" value={form.nextFollowUp} onChange={e => set("nextFollowUp", e.target.value)} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" /></div>
                </div>
                <textarea value={form.notes} onChange={e => set("notes", e.target.value)} placeholder="Notas" rows={2} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="flex gap-3 justify-end pt-2">
                    <button onClick={onClose} className="px-4 py-2 rounded-lg text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.fullName} className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-bold hover:bg-indigo-700 disabled:opacity-40">Crear Lead</button>
                </div>
            </div>
        </div>
    );
}

function ActivityModal({ types, onSave, onClose }) {
    const [form, setForm] = useState({ type: "Nota", description: "" });
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4" onClick={onClose}>
            <div className="bg-white dark:bg-slate-900 rounded-2xl w-full max-w-md p-6 space-y-4" onClick={e => e.stopPropagation()}>
                <h2 className="text-lg font-black text-slate-900 dark:text-white">Registrar Actividad</h2>
                <select value={form.type} onChange={e => setForm(p => ({ ...p, type: e.target.value }))} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm">
                    {types.map(t => <option key={t} value={t}>{t}</option>)}
                </select>
                <textarea value={form.description} onChange={e => setForm(p => ({ ...p, description: e.target.value }))} placeholder="¿Qué pasó? *" rows={3} className="w-full px-3 py-2 rounded-lg border border-slate-200 dark:border-slate-700 bg-transparent text-sm" />
                <div className="flex gap-3 justify-end">
                    <button onClick={onClose} className="px-4 py-2 rounded-lg text-sm font-bold text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800">Cancelar</button>
                    <button onClick={() => onSave(form)} disabled={!form.description} className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-bold hover:bg-indigo-700 disabled:opacity-40">Guardar</button>
                </div>
            </div>
        </div>
    );
}
