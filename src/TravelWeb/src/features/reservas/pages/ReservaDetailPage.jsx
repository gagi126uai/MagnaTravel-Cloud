import { useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import {
    FileText, Users, Clock, CreditCard, Paperclip, History
} from "lucide-react";

import { useReservaDetail } from "../hooks/useReservaDetail";
import ServiceFormModal from "../../../components/ServiceFormModal";
import PassengerFormModal from "../../../components/PassengerFormModal";
import PaymentModal from "../../../components/PaymentModal";
import AuditTimeline from "../../../components/AuditTimeline";
import { ReservaAttachmentsTab } from "../../../components/ReservaAttachmentsTab";
import { getPublicId, getRelatedPublicId } from "../../../lib/publicIds";
import { ReservaVoucherTab } from "../../../components/ReservaVoucherTab";

import { ReservaHeader } from "../components/ReservaHeader";
import { ReservaSummaryStrip } from "../components/ReservaSummaryStrip";
import { CapacityWarning } from "../components/CapacityWarning";
import { ServiceList } from "../components/ServiceList";
import { PassengerList } from "../components/PassengerList";
import ConfirmModal from "../../../components/ConfirmModal";

export default function ReservaDetailPage() {
    const { publicId } = useParams();
    const navigate = useNavigate();
    const [activeTab, setActiveTab] = useState("services"); // services, passengers, history, account, attachments
    const [showServiceModal, setShowServiceModal] = useState(false);
    const [serviceToEdit, setServiceToEdit] = useState(null);
    const [showPassengerForm, setShowPassengerForm] = useState(false);
    const [editingPassenger, setEditingPassenger] = useState(null);
    const [showPaymentModal, setShowPaymentModal] = useState(false);
    const [confirmConfig, setConfirmConfig] = useState({ 
        isOpen: false, 
        title: "", 
        message: "", 
        onConfirm: null, 
        type: "warning" 
    });

    const askConfirmation = (config) => {
        setConfirmConfig({
            isOpen: true,
            title: config.title || "Confirmar acción",
            message: config.message || "¿Estás seguro?",
            type: config.type || "warning",
            onConfirm: () => {
                config.onConfirm();
                setConfirmConfig(prev => ({ ...prev, isOpen: false }));
            }
        });
    };

    const {
        reserva,
        loading,
        suppliers,
        fetchReserva,
        handleArchiveReserva,
        handleDeleteReserva,
        handleStatusChange,
        handleDeleteService,
        handleDeletePassenger,
        allServices,
        hotelCapacity
    } = useReservaDetail(publicId, navigate);

    if (loading) return <div className="p-8 text-center text-slate-500 animate-pulse">Cargando reserva...</div>;

    if (!reserva) return (
        <div className="p-8 text-center bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 m-8">
            <h3 className="text-xl font-bold text-slate-900 dark:text-white">Reserva no encontrada</h3>
            <p className="text-slate-500 dark:text-slate-400 mt-2">No se pudo cargar la información. Verifique que la URL sea correcta.</p>
            <div className="mt-6 flex justify-center">
                <button onClick={() => navigate("/reservas")} className="px-4 py-2 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 transition-colors shadow-sm">
                    Volver a la lista
                </button>
            </div>
        </div>
    );

    return (
        <div className="max-w-7xl mx-auto p-4 sm:p-6 lg:p-8 space-y-6">
            <ReservaHeader
                reserva={reserva}
                onBack={() => navigate("/reservas")}
                onStatusChange={(newStatus) => {
                    if (newStatus === 'Presupuesto' && reserva.status === 'Reservado') {
                        askConfirmation({
                            title: "¿Deshacer Reserva?",
                            message: "¿Seguro que deseas volver el estado a 'Presupuesto'?",
                            type: "warning",
                            onConfirm: () => handleStatusChange(newStatus)
                        });
                    } else {
                        handleStatusChange(newStatus);
                    }
                }}
                onDelete={() => askConfirmation({
                    title: "¿Eliminar Reserva?",
                    message: "Acción irreversible. Solo aplicable a reservas sin pagos.",
                    type: "danger",
                    onConfirm: handleDeleteReserva
                })}
                onArchive={() => askConfirmation({
                    title: "¿Archivar Reserva?",
                    message: "El estado pasará a 'Archivado'.",
                    type: "warning",
                    onConfirm: handleArchiveReserva
                })}
            />

            <ReservaSummaryStrip reserva={reserva} />

            <CapacityWarning paxCount={reserva.passengers?.length || 0} hotelCapacity={hotelCapacity} />

            {(getRelatedPublicId(reserva, "sourceLeadPublicId", "sourceLeadId") || getRelatedPublicId(reserva, "sourceQuotePublicId", "sourceQuoteId")) && (
                <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-5">
                    <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-4">
                        <div>
                            <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Origen comercial</div>
                            <div className="mt-1 text-sm text-slate-600 dark:text-slate-300">
                                Esta reserva conserva la trazabilidad de la gestion comercial que la genero.
                            </div>
                        </div>
                        <div className="flex flex-wrap gap-3">
                            {getRelatedPublicId(reserva, "sourceLeadPublicId", "sourceLeadId") && (
                                <button
                                    onClick={() => navigate('/crm', { state: { openLeadId: getRelatedPublicId(reserva, "sourceLeadPublicId", "sourceLeadId") } })}
                                    className="px-4 py-2.5 rounded-xl bg-slate-100 dark:bg-slate-800 text-slate-700 dark:text-slate-200 text-sm font-bold hover:bg-slate-200 dark:hover:bg-slate-700 transition-colors"
                                >
                                    Abrir posible cliente asociado
                                </button>
                            )}
                            {getRelatedPublicId(reserva, "sourceQuotePublicId", "sourceQuoteId") && (
                                <button
                                    onClick={() => navigate('/quotes', { state: { openQuoteId: getRelatedPublicId(reserva, "sourceQuotePublicId", "sourceQuoteId") } })}
                                    className="px-4 py-2.5 rounded-xl bg-indigo-600 text-white text-sm font-bold hover:bg-indigo-700 transition-colors"
                                >
                                    Abrir cotización origen
                                </button>
                            )}
                        </div>
                    </div>
                </div>
            )}

            {/* TABS NAVIGATION */}
            <div className="bg-white dark:bg-slate-900/50 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                <div className="border-b border-slate-100 dark:border-slate-800 bg-slate-50/30 dark:bg-slate-800/20 px-4 sm:px-6">
                    <nav className="flex gap-8 overflow-x-auto scrollbar-hide">
                        {[
                            { id: 'services', label: 'Servicios', icon: FileText },
                            { id: 'passengers', label: `Pasajeros (${reserva.passengers?.length || 0})`, icon: Users },
                            { id: 'history', label: 'Historial', icon: Clock },
                            { id: 'account', label: 'Estado de Cuenta', icon: CreditCard },
                            { id: 'voucher', label: 'Voucher', icon: FileText },
                            { id: 'attachments', label: 'Documentos', icon: Paperclip },
                        ].map(tab => (
                            <button
                                key={tab.id}
                                onClick={() => setActiveTab(tab.id)}
                                className={`py-4 text-sm font-semibold transition-all relative flex items-center gap-2 whitespace-nowrap ${activeTab === tab.id ? 'text-indigo-600 dark:text-indigo-400' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}`}
                            >
                                <tab.icon className={`w-4 h-4 ${activeTab === tab.id ? 'animate-bounce' : ''}`} /> 
                                {tab.label}
                                {activeTab === tab.id && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-indigo-600 dark:bg-indigo-400 rounded-t-full" />}
                            </button>
                        ))}
                    </nav>
                </div>

                <div className="p-4 sm:p-6 lg:p-8">
                    {activeTab === 'services' && (
                        <ServiceList
                            services={allServices}
                            onAddService={() => { setServiceToEdit(null); setShowServiceModal(true); }}
                            onEditService={(svc) => { setServiceToEdit(svc); setShowServiceModal(true); }}
                            onDeleteService={(svc) => askConfirmation({
                                title: "¿Eliminar Servicio?",
                                message: `¿Estás seguro de eliminar el servicio ${svc.description || ''}?`,
                                type: "danger",
                                onConfirm: () => handleDeleteService(svc)
                            })}
                        />
                    )}

                    {activeTab === 'passengers' && (
                        <PassengerList
                            passengers={reserva.passengers}
                            onAddPassenger={() => { setEditingPassenger(null); setShowPassengerForm(true); }}
                            onEditPassenger={(pax) => { setEditingPassenger(pax); setShowPassengerForm(true); }}
                            onDeletePassenger={(paxId) => askConfirmation({
                                title: "¿Eliminar Pasajero?",
                                message: "¿Estás seguro de eliminar este pasajero de la reserva?",
                                type: "danger",
                                onConfirm: () => handleDeletePassenger(paxId)
                            })}
                        />
                    )}

                    {activeTab === 'history' && <AuditTimeline entityName="Reserva" entityId={publicId} />}

                    {activeTab === 'attachments' && <ReservaAttachmentsTab reservaId={publicId} />}

                    {activeTab === 'voucher' && <ReservaVoucherTab reservaId={publicId} />}

                    {activeTab === 'account' && (
                        <div className="space-y-6 animate-in fade-in duration-500">
                             {/* Financial Summary Cards */}
                             <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                                <div className="bg-white dark:bg-slate-900 p-4 rounded-xl border border-slate-200 dark:border-slate-800">
                                    <div className="text-[10px] font-black uppercase text-slate-400 mb-1">Total Venta</div>
                                    <div className="text-xl font-black text-slate-900 dark:text-white">
                                        {reserva.totalSale?.toLocaleString('es-AR', { style: 'currency', currency: 'ARS' })}
                                    </div>
                                </div>
                                <div className="bg-emerald-50/50 dark:bg-emerald-950/20 p-4 rounded-xl border border-emerald-100 dark:border-emerald-900/30">
                                    <div className="text-[10px] font-black uppercase text-emerald-600/70 dark:text-emerald-400/70 mb-1">Total Cobrado</div>
                                    <div className="text-xl font-black text-emerald-600 dark:text-emerald-400">
                                        {(reserva.totalSale - reserva.balance)?.toLocaleString('es-AR', { style: 'currency', currency: 'ARS' })}
                                    </div>
                                </div>
                                <div className={`p-4 rounded-xl border ${reserva.balance > 0 ? 'bg-rose-50/50 border-rose-100 dark:bg-rose-950/20 dark:border-rose-900/30' : 'bg-slate-50 border-slate-200 dark:bg-slate-800/30 dark:border-slate-800'}`}>
                                    <div className={`text-[10px] font-black uppercase mb-1 ${reserva.balance > 0 ? 'text-rose-600/70' : 'text-slate-400'}`}>Saldo Pendiente</div>
                                    <div className={`text-xl font-black ${reserva.balance > 0 ? 'text-rose-600' : 'text-slate-500'}`}>
                                        {reserva.balance?.toLocaleString('es-AR', { style: 'currency', currency: 'ARS' })}
                                    </div>
                                </div>
                             </div>

                             <div className="flex justify-between items-center bg-slate-50 dark:bg-slate-800/50 p-4 rounded-xl border border-slate-200 dark:border-slate-800">
                                <div className="flex gap-2">
                                    <button 
                                        onClick={() => setShowPaymentModal(true)}
                                        className="px-4 py-2 bg-emerald-600 text-white rounded-lg text-sm font-bold hover:bg-emerald-700 transition-all flex items-center gap-2"
                                    >
                                        <CreditCard className="w-4 h-4" /> Registrar Cobranza
                                    </button>
                                </div>
                                <div className="text-xs text-slate-500 font-medium italic">
                                    * Los pagos recibidos afectan directamente al saldo de la reserva.
                                </div>
                             </div>

                             {/* Payments & Invoices Tables (Estilo Caja y Facturación) */}
                             <div className="grid grid-cols-1 gap-6">
                                {/* Cobranzas */}
                                <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                                    <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/30 dark:bg-slate-800/10 flex items-center gap-2">
                                        <History className="w-4 h-4 text-emerald-500" />
                                        <h4 className="text-sm font-bold text-slate-900 dark:text-white uppercase tracking-wider">Historial de Cobranzas</h4>
                                    </div>
                                    <div className="overflow-x-auto">
                                        <table className="w-full text-left text-sm">
                                            <thead className="bg-slate-50/50 dark:bg-slate-950 text-slate-500 font-bold border-b border-slate-200 dark:border-slate-800">
                                                <tr>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px]">Fecha</th>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px]">Método</th>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px]">Notas</th>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px] text-right">Importe</th>
                                                </tr>
                                            </thead>
                                            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                                {reserva.payments?.length > 0 ? reserva.payments.map(p => (
                                                    <tr key={getPublicId(p)} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30">
                                                        <td className="px-6 py-4">{new Date(p.paidAt).toLocaleDateString()}</td>
                                                        <td className="px-6 py-4">
                                                            <span className="text-[10px] font-black bg-slate-100 dark:bg-slate-800 px-2 py-1 rounded uppercase">
                                                                {p.method}
                                                            </span>
                                                        </td>
                                                        <td className="px-6 py-4 text-slate-500">{p.notes || '-'}</td>
                                                        <td className="px-6 py-4 text-right font-black text-emerald-600">
                                                            {p.amount?.toLocaleString('es-AR', { style: 'currency', currency: 'ARS' })}
                                                        </td>
                                                    </tr>
                                                )) : (
                                                    <tr>
                                                        <td colSpan="4" className="px-6 py-8 text-center text-slate-400 italic">No hay pagos registrados.</td>
                                                    </tr>
                                                )}
                                            </tbody>
                                        </table>
                                    </div>
                                </div>

                                {/* Facturas emitidas */}
                                <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                                    <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/30 dark:bg-slate-800/10 flex items-center gap-2">
                                        <FileText className="w-4 h-4 text-indigo-500" />
                                        <h4 className="text-sm font-bold text-slate-900 dark:text-white uppercase tracking-wider">Documentos Fiscales AFIP</h4>
                                    </div>
                                    <div className="overflow-x-auto">
                                        <table className="w-full text-left text-sm">
                                            <thead className="bg-slate-50/50 dark:bg-slate-950 text-slate-500 font-bold border-b border-slate-200 dark:border-slate-800">
                                                <tr>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px]">Tipo</th>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px]">Número</th>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px]">CAE</th>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px]">Estado</th>
                                                    <th className="px-6 py-3 font-bold uppercase text-[10px] text-right">Importe</th>
                                                </tr>
                                            </thead>
                                            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                                {reserva.invoices?.length > 0 ? reserva.invoices.map(i => (
                                                    <tr key={getPublicId(i)} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30">
                                                        <td className="px-6 py-4 font-bold">Factura {i.tipoComprobante === 1 ? 'A' : i.tipoComprobante === 6 ? 'B' : 'C'}</td>
                                                        <td className="px-6 py-4 font-mono">{String(i.puntoDeVenta).padStart(5, '0')}-{String(i.numeroComprobante).padStart(8, '0')}</td>
                                                        <td className="px-6 py-4 text-xs font-mono text-slate-400">{i.cae || '---'}</td>
                                                        <td className="px-6 py-4 text-[10px]">
                                                            <span className={`px-2 py-0.5 rounded font-black uppercase ${i.resultado === 'A' ? 'bg-emerald-100 text-emerald-700' : 'bg-rose-100 text-rose-700'}`}>
                                                                {i.resultado === 'A' ? 'Aceptada' : 'Error'}
                                                            </span>
                                                        </td>
                                                        <td className="px-6 py-4 text-right font-black">
                                                            {i.importeTotal?.toLocaleString('es-AR', { style: 'currency', currency: 'ARS' })}
                                                        </td>
                                                    </tr>
                                                )) : (
                                                    <tr>
                                                        <td colSpan="5" className="px-6 py-8 text-center text-slate-400 italic">No hay facturas emitidas para esta reserva.</td>
                                                    </tr>
                                                )}
                                            </tbody>
                                        </table>
                                    </div>
                                </div>
                             </div>
                        </div>
                    )}
                </div>
            </div>

            {/* MODALS */}
            <ServiceFormModal
                isOpen={showServiceModal}
                onClose={() => setShowServiceModal(false)}
                reservaId={publicId}
                serviceToEdit={serviceToEdit}
                onSuccess={fetchReserva}
                suppliers={suppliers}
            />

            <PaymentModal
                isOpen={showPaymentModal}
                onClose={() => setShowPaymentModal(false)}
                reservaId={publicId}
                maxAmount={reserva?.balance}
                onSuccess={fetchReserva}
            />

            <PassengerFormModal
                isOpen={showPassengerForm}
                onClose={() => setShowPassengerForm(false)}
                reservaId={publicId}
                passengerToEdit={editingPassenger}
                onSuccess={fetchReserva}
            />

            <ConfirmModal 
                isOpen={confirmConfig.isOpen}
                title={confirmConfig.title}
                message={confirmConfig.message}
                type={confirmConfig.type}
                onConfirm={confirmConfig.onConfirm}
                onClose={() => setConfirmConfig(prev => ({ ...prev, isOpen: false }))}
            />
        </div>
    );
}
