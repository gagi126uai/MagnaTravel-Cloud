import { useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import {
    FileText, Users, Clock, CreditCard, Paperclip
} from "lucide-react";

import { useTravelFileDetail } from "../hooks/useTravelFileDetail";
import ServiceFormModal from "../../../components/ServiceFormModal";
import PassengerFormModal from "../../../components/PassengerFormModal";
import AuditTimeline from "../../../components/AuditTimeline";
import { FileAttachmentsTab } from "../../../components/FileAttachmentsTab";

import { FileHeader } from "../components/FileHeader";
import { FileSummaryStrip } from "../components/FileSummaryStrip";
import { CapacityWarning } from "../components/CapacityWarning";
import { ServiceList } from "../components/ServiceList";
import { PassengerList } from "../components/PassengerList";

export default function FileDetailPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [activeTab, setActiveTab] = useState("services"); // services, passengers, history, account, attachments
    const [showServiceModal, setShowServiceModal] = useState(false);
    const [serviceToEdit, setServiceToEdit] = useState(null);
    const [showPassengerForm, setShowPassengerForm] = useState(false);
    const [editingPassenger, setEditingPassenger] = useState(null);

    const {
        file,
        loading,
        suppliers,
        fetchFile,
        handleArchiveFile,
        handleDeleteFile,
        handleStatusChange,
        handleDeleteService,
        handleDeletePassenger,
        allServices,
        hotelCapacity
    } = useTravelFileDetail(id, navigate);

    if (loading) return <div className="p-8 text-center">Cargando file...</div>;

    if (!file) return (
        <div className="p-8 text-center">
            <h3 className="text-xl font-medium text-gray-900 dark:text-white">Expediente no encontrado</h3>
            <p className="text-gray-500 dark:text-slate-400 mt-2">No se pudo cargar la información. Verifique que el expediente exista.</p>
            <div className="mt-4 flex gap-4 justify-center">
                <button onClick={() => navigate("/files")} className="text-blue-600 hover:text-blue-800 dark:text-blue-400 dark:hover:text-blue-300 underline">Volver a la lista</button>
            </div>
        </div>
    );

    return (
        <div className="max-w-7xl mx-auto p-4 sm:p-6 lg:p-8">
            <FileHeader
                file={file}
                onBack={() => navigate("/files")}
                onStatusChange={handleStatusChange}
                onDelete={() => { if (confirm("¿Eliminar Expediente? Acción irreversible. Solo aplicable a expedientes sin pagos.")) handleDeleteFile(); }}
                onArchive={() => { if (confirm("¿Archivar File? El file pasará a estado 'Archivado'.")) handleArchiveFile(); }}
            />

            <FileSummaryStrip file={file} />

            <CapacityWarning paxCount={file.passengers?.length || 0} hotelCapacity={hotelCapacity} />

            {/* TABS NAVIGATION */}
            <div className="mb-8">
                <div className="border-b border-slate-100 dark:border-slate-800 overflow-x-auto scrollbar-hide">
                    <nav className="flex gap-6 min-w-max">
                        {[
                            { id: 'services', label: 'Servicios', icon: FileText },
                            { id: 'passengers', label: `Pasajeros (${file.passengers?.length || 0})`, icon: Users },
                            { id: 'history', label: 'Historial', icon: Clock },
                            { id: 'account', label: 'Estado de Cuenta', icon: CreditCard },
                            { id: 'attachments', label: 'Documentos', icon: Paperclip },
                        ].map(tab => (
                            <button
                                key={tab.id}
                                onClick={() => setActiveTab(tab.id)}
                                className={`pb-3 text-sm font-medium transition-colors relative ${activeTab === tab.id ? 'text-slate-900 dark:text-white' : 'text-slate-400 hover:text-slate-600'}`}
                            >
                                <div className="flex items-center gap-2"><tab.icon className="w-4 h-4" /> {tab.label}</div>
                                {activeTab === tab.id && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-slate-900 dark:bg-white rounded-t-full" />}
                            </button>
                        ))}
                    </nav>
                </div>

                <div className="p-4 sm:p-6">
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
                            passengers={file.passengers}
                            onAddPassenger={() => { setEditingPassenger(null); setShowPassengerForm(true); }}
                            onEditPassenger={(pax) => { setEditingPassenger(pax); setShowPassengerForm(true); }}
                            onDeletePassenger={(paxId) => { if (confirm("¿Eliminar pasajero?")) handleDeletePassenger(paxId); }}
                        />
                    )}

                    {activeTab === 'history' && <AuditTimeline fileId={id} />}

                    {activeTab === 'attachments' && <FileAttachmentsTab fileId={id} />}

                    {activeTab === 'account' && (
                        <div className="text-center py-12 text-slate-500">
                            Módulo de Estado de Cuenta (En desarrollo con el feature de Pagos)
                        </div>
                    )}
                </div>
            </div>

            {/* MODALS */}
            <ServiceFormModal
                isOpen={showServiceModal}
                onClose={() => setShowServiceModal(false)}
                fileId={id}
                serviceToEdit={serviceToEdit}
                onSuccess={fetchFile}
                suppliers={suppliers}
            />

            <PassengerFormModal
                isOpen={showPassengerForm}
                onClose={() => setShowPassengerForm(false)}
                fileId={id}
                passengerToEdit={editingPassenger}
                onSuccess={fetchFile}
            />
        </div>
    );
}
