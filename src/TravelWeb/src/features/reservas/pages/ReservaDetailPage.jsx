import { useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import {
    FileText, Users, Clock, CreditCard, Paperclip
} from "lucide-react";
import { LucideIcon } from "lucide-react";

import { useReservaDetail } from "../hooks/useReservaDetail";
import ServiceFormModal from "../../../components/ServiceFormModal";
import PassengerFormModal from "../../../components/PassengerFormModal";
import PaymentModal from "../../../components/PaymentModal";
import AuditTimeline from "../../../components/AuditTimeline";
import { ReservaAttachmentsTab } from "../../../components/ReservaAttachmentsTab";

import { ReservaHeader } from "../components/ReservaHeader";
import { ReservaSummaryStrip } from "../components/ReservaSummaryStrip";
import { CapacityWarning } from "../components/CapacityWarning";
import { ServiceList } from "../components/ServiceList";
import { PassengerList } from "../components/PassengerList";

export default function ReservaDetailPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [activeTab, setActiveTab] = useState("services"); // services, passengers, history, account, attachments
    const [showServiceModal, setShowServiceModal] = useState(false);
    const [serviceToEdit, setServiceToEdit] = useState(null);
    const [showPassengerForm, setShowPassengerForm] = useState(false);
    const [editingPassenger, setEditingPassenger] = useState(null);

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
    } = useReservaDetail(id, navigate);

    if (loading) return <div className="p-8 text-center text-slate-500 animate-pulse">Cargando reserva...</div>;

    if (!reserva) return (
        <div className="p-8 text-center bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 m-8">
            <h3 className="text-xl font-bold text-slate-900 dark:text-white">Reserva no encontrada</h3>
            <p className="text-slate-500 dark:text-slate-400 mt-2">No se pudo cargar la información. Verifique que el ID sea correcto.</p>
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
                onStatusChange={handleStatusChange}
                onDelete={() => { if (confirm("¿Eliminar Reserva? Acción irreversible. Solo aplicable a reservas sin pagos.")) handleDeleteReserva(); }}
                onArchive={() => { if (confirm("¿Archivar Reserva? El estado pasará a 'Archivado'.")) handleArchiveReserva(); }}
            />

            <ReservaSummaryStrip reserva={reserva} />

            <CapacityWarning paxCount={reserva.passengers?.length || 0} hotelCapacity={hotelCapacity} />

            {/* TABS NAVIGATION */}
            <div className="bg-white dark:bg-slate-900/50 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                <div className="border-b border-slate-100 dark:border-slate-800 bg-slate-50/30 dark:bg-slate-800/20 px-4 sm:px-6">
                    <nav className="flex gap-8 overflow-x-auto scrollbar-hide">
                        {[
                            { id: 'services', label: 'Servicios', icon: FileText },
                            { id: 'passengers', label: `Pasajeros (${reserva.passengers?.length || 0})`, icon: Users },
                            { id: 'history', label: 'Historial', icon: Clock },
                            { id: 'account', label: 'Estado de Cuenta', icon: CreditCard },
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
                            onDeleteService={(svc) => { if (confirm("¿Eliminar Servicio?")) handleDeleteService(svc); }}
                        />
                    )}

                    {activeTab === 'passengers' && (
                        <PassengerList
                            passengers={reserva.passengers}
                            onAddPassenger={() => { setEditingPassenger(null); setShowPassengerForm(true); }}
                            onEditPassenger={(pax) => { setEditingPassenger(pax); setShowPassengerForm(true); }}
                            onDeletePassenger={(paxId) => { if (confirm("¿Eliminar pasajero?")) handleDeletePassenger(paxId); }}
                        />
                    )}

                    {activeTab === 'history' && <AuditTimeline entityName="Reserva" entityId={id} />}

                    {activeTab === 'attachments' && <ReservaAttachmentsTab reservaId={id} />}

                    {activeTab === 'account' && (
                        <div className="flex flex-col items-center justify-center py-16 text-slate-400 bg-slate-50/50 dark:bg-slate-800/30 rounded-xl border border-dashed border-slate-200 dark:border-slate-800">
                            <CreditCard className="w-12 h-12 mb-4 opacity-20" />
                            <h4 className="text-lg font-semibold text-slate-600 dark:text-slate-300">Estado de Cuenta</h4>
                            <p className="max-w-xs text-center mt-2 text-sm">Este módulo se encuentra en desarrollo integrado con el sistema de Pagos y Facturación.</p>
                        </div>
                    )}
                </div>
            </div>

            {/* MODALS */}
            <ServiceFormModal
                isOpen={showServiceModal}
                onClose={() => setShowServiceModal(false)}
                reservaId={id}
                serviceToEdit={serviceToEdit}
                onSuccess={fetchReserva}
                suppliers={suppliers}
            />

            <PaymentModal
                isOpen={showPaymentModal}
                onClose={() => setShowPaymentModal(false)}
                reservaId={id}
                maxAmount={reserva?.balance}
                onSuccess={fetchReserva}
            />

            <PassengerFormModal
                isOpen={showPassengerForm}
                onClose={() => setShowPassengerForm(false)}
                reservaId={id}
                passengerToEdit={editingPassenger}
                onSuccess={fetchReserva}
            />
        </div>
    );
}
