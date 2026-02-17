import { useState, useEffect, useCallback } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { api } from "../api";
import {
    ArrowLeft, Plus, Calendar, Users,
    FileText, Edit2, Trash2, CheckCircle, AlertTriangle, X,
    Plane, Hotel, Car, Package, CreditCard, Archive, Clock, Paperclip
} from "lucide-react";
import ServiceFormModal from "../components/ServiceFormModal";
import PassengerFormModal from "../components/PassengerFormModal";

import { showError, showSuccess, showConfirm } from "../alerts";
import AuditTimeline from "../components/AuditTimeline";
import { FileAttachmentsTab } from "../components/FileAttachmentsTab";
import InvoicesTab from "../components/InvoicesTab";

export default function FileDetailPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [file, setFile] = useState(null);
    const [loading, setLoading] = useState(true);
    const [activeTab, setActiveTab] = useState("services"); // services, passengers, payments, notes, history
    const [showServiceModal, setShowServiceModal] = useState(false);
    const [serviceToEdit, setServiceToEdit] = useState(null);

    // States for CRUD forms (Passengers, Payments)
    const [showPassengerForm, setShowPassengerForm] = useState(false);
    const [editingPassenger, setEditingPassenger] = useState(null);


    const [suppliers, setSuppliers] = useState([]);

    const fetchFile = useCallback(async () => {
        try {
            setLoading(true);
            const res = await api.get(`/travelfiles/${id}`);
            setFile(res);
        } catch (error) {
            console.error(error);
            console.error(error);
            showError("Error al cargar el expediente: " + (error.response?.data?.Error || error.message || "Error desconocido"));
            // Do NOT navigate away automatically, let the user see the error
            setFile(null);
        } finally {
            setLoading(false);
        }
    }, [id, navigate]);

    const fetchSuppliers = useCallback(async () => {
        try {
            const res = await api.get("/suppliers");
            setSuppliers(res || []);
        } catch (error) {
            console.error("Error fetching suppliers:", error);
        }
    }, []);

    useEffect(() => {
        fetchFile();
        fetchSuppliers();
    }, [fetchFile, fetchSuppliers]);

    // --- ACTIONS: FILE ---
    const handleArchiveFile = async () => {
        const confirmed = await showConfirm(
            '¿Archivar File?',
            "El file pasará a estado 'Archivado' y no será visible en la lista activa.",
            'Sí, archivar'
        );

        if (confirmed) {
            try {
                await api.put(`/travelfiles/${id}/archive`);
                showSuccess("File archivado correctamente");
                navigate("/files");
            } catch (error) {
                showError("Error al archivar");
            }
        }
    };

    const handleDeleteFile = async () => {
        const confirmed = await showConfirm(
            '¿Eliminar File?',
            "Esta acción no se puede deshacer. Solo permitido si es un Presupuesto sin pagos.",
            'Sí, eliminar',
            'red'
        );

        if (confirmed) {
            try {
                await api.delete(`/travelfiles/${id}`);
                showSuccess("File eliminado correctamente");
                navigate("/files");
            } catch (error) {
                showError(error.response?.data || "Error al eliminar");
            }
        }
    };

    const handleStatusChange = async (newStatus) => {
        if (file.status === 'Reservado' && newStatus === 'Presupuesto') {
            if (file.payments?.length > 0) {
                showError("No se puede volver a Presupuesto: hay pagos registrados. Elimínalos desde Facturación y Caja.");
                return;
            }
            if (file.invoices?.length > 0) {
                showError("No se puede volver a Presupuesto: hay facturas emitidas. Deben ser anuladas primero.");
                return;
            }
        }

        try {
            await api.put(`/travelfiles/${id}/status`, { status: newStatus });
            fetchFile();
            showSuccess(`Estado actualizado a ${newStatus}`);
        } catch (error) {
            showError("Error al cambiar estado");
        }
    };

    // --- ACTIONS: SERVICES ---
    const handleEditService = (service) => {
        setServiceToEdit(service);
        setShowServiceModal(true);
    };

    const handleDeleteService = async (service) => {
        const confirmed = await showConfirm(
            '¿Eliminar Servicio?',
            "Se revertirá el costo y venta del total del file.",
            'Sí, eliminar',
            'red'
        );

        if (confirmed) {
            try {
                // Build endpoint based on type (Logic matches backend controllers)
                // flightSegments, hotelBookings, transferBookings, packageBookings
                // Using generic mapping or distinct endpoints
                let endpoint = "";

                // Determine endpoint based on service object structure or type
                // The backend returns separate lists, we consolidate them. 
                // We need to know the TYPE to hit the right controller.
                // We can inject a 'type' field when consolidating.
                if (service._type === 'Flight') endpoint = `/files/${id}/flights/${service.id}`;
                else if (service._type === 'Hotel') endpoint = `/files/${id}/hotels/${service.id}`;
                else if (service._type === 'Transfer') endpoint = `/files/${id}/transfers/${service.id}`;
                else if (service._type === 'Package') endpoint = `/files/${id}/packages/${service.id}`;

                await api.delete(endpoint);
                fetchFile();
                showSuccess("Servicio eliminado");
            } catch (error) {
                showError("Error al eliminar servicio");
            }
        }
    };

    // --- ACTIONS: PASSENGERS ---
    // --- ACTIONS: PASSENGERS ---
    // Moved to PassengerFormModal

    const handleDeletePassenger = async (passengerId) => {
        if (!await confirmAction("¿Eliminar pasajero?")) return;
        try {
            await api.delete(`/travelfiles/passengers/${passengerId}`);
            fetchFile();
            showSuccess("Pasajero eliminado");
        } catch (error) {
            showError("Error al eliminar");
        }
    };



    // --- HELPERS ---
    // --- HELPERS ---
    const confirmAction = async (title) => {
        return await showConfirm(title, "¿Está seguro?", "Sí", "indigo");
    };

    // --- RENDER HELPERS ---
    const getAllServices = () => {
        if (!file) return [];
        const services = [];
        file.flightSegments?.forEach(f => services.push({ ...f, _type: 'Flight', date: f.departureTime, name: `${f.airlineName} ${f.flightNumber}` }));
        file.hotelBookings?.forEach(h => services.push({ ...h, _type: 'Hotel', date: h.checkIn, name: h.hotelName }));
        file.transferBookings?.forEach(t => services.push({ ...t, _type: 'Transfer', date: t.pickupDateTime, name: `${t.pickupLocation} > ${t.dropoffLocation}` }));
        file.packageBookings?.forEach(p => services.push({ ...p, _type: 'Package', date: p.startDate, name: p.packageName }));
        file.reservations?.forEach(r => services.push({ ...r, _type: r.serviceType || 'Generic', date: r.departureDate, name: r.description }));
        // Legacy support if needed, but assuming new structure
        return services.sort((a, b) => new Date(a.date) - new Date(b.date));
    };

    const getCapacityWarning = () => {
        if (!file) return null;
        const paxCount = file.passengers?.length || 0;
        if (paxCount === 0) return null;

        let totalCapacity = 0;
        // Simple heuristic
        file.hotelBookings?.forEach(h => totalCapacity += (h.rooms * 2)); // Assume double occupancy avg
        file.flightSegments?.forEach(f => totalCapacity += 1); // Flights usually 1 per person logic, but this is per segment. Hard to map.
        // Better heuristic: "If any service has passengers count distinct from file pax count?"
        // User asked: "sihay 1 habitacion doble pero cargado hay 3 pasajeros que avise"
        // Let's check Hotels specially
        let hotelCapacity = 0;
        let hasHotels = false;
        file.hotelBookings?.forEach(h => {
            hasHotels = true;
            // Check room type for better accuracy?
            if (h.roomType.toLowerCase().includes('sing')) hotelCapacity += (1 * h.rooms);
            else if (h.roomType.toLowerCase().includes('trip')) hotelCapacity += (3 * h.rooms);
            else if (h.roomType.toLowerCase().includes('quad')) hotelCapacity += (4 * h.rooms);
            else hotelCapacity += (2 * h.rooms); // Default Double
        });

        if (hasHotels && paxCount > hotelCapacity) {
            return (
                <div className="bg-yellow-50 border-l-4 border-yellow-400 p-4 mb-4">
                    <div className="flex">
                        <div className="flex-shrink-0">
                            <AlertTriangle className="h-5 w-5 text-yellow-400" aria-hidden="true" />
                        </div>
                        <div className="ml-3">
                            <p className="text-sm text-yellow-700">
                                Atención: Hay <strong>{paxCount}</strong> pasajeros cargados pero la capacidad hotelera estimada es de <strong>{hotelCapacity}</strong> plazas.
                                <br /><span className="text-xs opacity-75">Verifique la distribución de habitaciones.</span>
                            </p>
                        </div>
                    </div>
                </div>
            );
        }
        return null;
    };

    if (loading) return <div className="p-8 text-center">Cargando file...</div>;
    const handleDebug = async () => {
        try {
            const res = await api.get(`/travelfiles/debug/${id}`);
            Swal.fire({
                title: 'Reporte de Diagnóstico',
                html: `<pre style="text-align: left; font-size: 12px; max-height: 400px; overflow: auto; background: #f0f0f0; padding: 10px;">${res.report || res.Report}</pre>`,
                width: '600px'
            });
        } catch (err) {
            showError("No se pudo ejecutar el diagnóstico: " + err.message);
        }
    };

    if (!file) return (
        <div className="p-8 text-center">
            <h3 className="text-xl font-medium text-gray-900 dark:text-white">Expediente no encontrado</h3>
            <p className="text-gray-500 dark:text-slate-400 mt-2">No se pudo cargar la información. Verifique que el expediente exista.</p>
            <div className="mt-4 flex gap-4 justify-center">
                <button onClick={() => navigate("/files")} className="text-blue-600 hover:text-blue-800 dark:text-blue-400 dark:hover:text-blue-300 underline">Volver a la lista</button>
                <button onClick={handleDebug} className="bg-gray-800 text-white px-4 py-2 rounded text-sm hover:bg-gray-700 dark:bg-slate-700 dark:hover:bg-slate-600">
                    🛠️ Ver Diagnóstico
                </button>
            </div>
        </div>
    );

    const allServices = getAllServices();

    return (
        <div className="max-w-7xl mx-auto p-4 sm:p-6 lg:p-8">

            {/* HEADER */}
            <div className="mb-8 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div>
                    <button
                        onClick={() => navigate("/files")}
                        className="flex items-center text-gray-500 hover:text-gray-700 dark:text-slate-400 dark:hover:text-slate-200 mb-2 transition-colors"
                    >
                        <ArrowLeft className="w-4 h-4 mr-1" /> Volver a Lista
                    </button>
                    <div className="flex items-center gap-3">
                        <h1 className="text-3xl font-bold text-gray-900 dark:text-white">File #{file.fileNumber}</h1>
                        <span className={`px-3 py-1 rounded-full text-sm font-medium 
                ${file.status === 'Presupuesto' ? 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-200' :
                                file.status === 'Reservado' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200' :
                                    file.status === 'Operativo' ? 'bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200' :
                                        file.status === 'Cerrado' ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200' : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'}`}>
                            {file.status}
                        </span>
                    </div>
                    <p className="text-xl text-gray-900 dark:text-white mt-1 font-semibold">{file.customerName}</p>
                    <p className="text-lg text-gray-600 dark:text-slate-400">{file.name}</p>
                </div>

                <div className="flex flex-wrap gap-2">
                    {/* STATUS ACTIONS */}
                    {file.status === 'Presupuesto' && (
                        <button onClick={() => handleStatusChange('Reservado')} className="btn btn-primary bg-blue-600 hover:bg-blue-700 text-white px-4 py-2 rounded shadow">
                            Confirmar Reserva
                        </button>
                    )}
                    {file.status === 'Reservado' && (
                        <>
                            <button onClick={() => handleStatusChange('Operativo')} className="btn btn-secondary bg-purple-600 hover:bg-purple-700 text-white px-4 py-2 rounded shadow">
                                Pasar a Operativo
                            </button>
                            <button onClick={async () => { if (await confirmAction("¿Volver a Presupuesto?")) handleStatusChange('Presupuesto'); }} className="btn bg-amber-100 text-amber-800 hover:bg-amber-200 px-4 py-2 rounded shadow ml-2">
                                Deshacer Reserva
                            </button>
                        </>
                    )}
                    {file.status === 'Operativo' && (
                        <button onClick={() => handleStatusChange('Cerrado')} className="btn btn-success bg-green-600 hover:bg-green-700 text-white px-4 py-2 rounded shadow">
                            Cerrar File
                        </button>
                    )}

                    {/* ADMIN ACTIONS */}
                    <div className="ml-2 pl-2 border-l border-gray-300 flex gap-2">
                        {file.status === 'Presupuesto' && (
                            <button onClick={handleDeleteFile} className="btn bg-red-100 text-red-700 hover:bg-red-200 px-3 py-2 rounded" title="Eliminar File">
                                <Trash2 className="w-5 h-5" />
                            </button>
                        )}
                        <button onClick={handleArchiveFile} className="btn bg-gray-100 text-gray-600 hover:bg-gray-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700 px-3 py-2 rounded" title="Archivar">
                            <Archive className="w-5 h-5" />
                        </button>
                    </div>
                </div>
            </div>

            {/* SUMMARY CARDS */}
            <div className="grid grid-cols-1 md:grid-cols-4 gap-6 mb-8">
                <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm p-6 border border-gray-100 dark:border-slate-700">
                    <p className="text-sm font-medium text-gray-500 dark:text-slate-400 mb-1">Total Venta</p>
                    <p className="text-2xl font-bold text-gray-900 dark:text-white">${file.totalSale?.toLocaleString()}</p>
                </div>
                <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm p-6 border border-gray-100 dark:border-slate-700">
                    <p className="text-sm font-medium text-gray-500 dark:text-slate-400 mb-1">Total Costo</p>
                    <p className="text-2xl font-bold text-gray-900 dark:text-white">${file.totalCost?.toLocaleString()}</p>
                </div>
                <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm p-6 border border-gray-100 dark:border-slate-700">
                    <p className="text-sm font-medium text-gray-500 dark:text-slate-400 mb-1">Cobrado</p>
                    <p className="text-2xl font-bold text-green-600 dark:text-emerald-400">${(file.totalSale - file.balance)?.toLocaleString()}</p>
                </div>
                <div className={`bg-white dark:bg-slate-800 rounded-xl shadow-sm p-6 border-l-4 ${file.balance > 0 ? 'border-red-500' : 'border-green-500'}`}>
                    <div className="flex justify-between items-start">
                        <div>
                            <p className="text-sm font-medium text-gray-500 dark:text-slate-400 mb-1">Saldo Pendiente</p>
                            <p className={`text-2xl font-bold ${file.balance > 0 ? 'text-red-600 dark:text-rose-400' : 'text-green-600 dark:text-emerald-400'}`}>
                                ${file.balance?.toLocaleString()}
                            </p>
                        </div>
                        {file.balance > 0 && <AlertTriangle className="w-6 h-6 text-red-500" />}
                    </div>
                </div>
            </div>

            {/* CAPACITY WARNING */}
            {getCapacityWarning()}

            {/* TABS */}
            <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden min-h-[500px]">
                <div className="border-b border-gray-200 dark:border-slate-700 overflow-x-auto scrollbar-hide">
                    <nav className="flex -mb-px min-w-max sm:min-w-0">
                        <button
                            onClick={() => setActiveTab('services')}
                            className={`flex-1 min-w-[120px] py-4 px-4 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2 whitespace-nowrap
                ${activeTab === 'services' ? 'border-blue-500 text-blue-600 dark:text-blue-400 bg-blue-50 dark:bg-slate-800' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
                        >
                            <FileText className="w-4 h-4" /> Servicios
                        </button>
                        <button
                            onClick={() => setActiveTab('passengers')}
                            className={`flex-1 min-w-[120px] py-4 px-4 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2 whitespace-nowrap
                ${activeTab === 'passengers' ? 'border-blue-500 text-blue-600 dark:text-blue-400 bg-blue-50 dark:bg-slate-800' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
                        >
                            <Users className="w-4 h-4" /> Pasajeros ({file.passengers?.length || 0})
                        </button>
                        <button
                            onClick={() => setActiveTab('history')}
                            className={`flex-1 min-w-[120px] py-4 px-4 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2 whitespace-nowrap
                ${activeTab === 'history' ? 'border-blue-500 text-blue-600 dark:text-blue-400 bg-blue-50 dark:bg-slate-800' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
                        >
                            <Clock className="w-4 h-4" /> Historial
                        </button>
                        <button
                            onClick={() => setActiveTab('invoices')}
                            className={`flex-1 min-w-[120px] py-4 px-4 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2 whitespace-nowrap
                ${activeTab === 'invoices' ? 'border-blue-500 text-blue-600 dark:text-blue-400 bg-blue-50 dark:bg-slate-800' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
                        >
                            <CreditCard className="w-4 h-4" /> Facturación
                        </button>
                        <button
                            onClick={() => setActiveTab('attachments')}
                            className={`flex-1 min-w-[120px] py-4 px-4 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2 whitespace-nowrap
                ${activeTab === 'attachments' ? 'border-blue-500 text-blue-600 dark:text-blue-400 bg-blue-50 dark:bg-slate-800' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
                        >
                            <Paperclip className="w-4 h-4" /> Documentos
                        </button>
                    </nav>
                </div>

                <div className="p-4 sm:p-6">
                    {/* --- TAB: SERVICES --- */}
                    {activeTab === 'services' && (
                        <div>
                            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 mb-4">
                                <h3 className="text-lg font-medium text-gray-900 dark:text-white">Servicios Contratados</h3>
                                <button
                                    onClick={() => { setServiceToEdit(null); setShowServiceModal(true); }}
                                    className="w-full sm:w-auto flex items-center justify-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors shadow-sm"
                                >
                                    <Plus className="w-4 h-4" /> Agregar Servicio
                                </button>
                            </div>

                            {allServices.length === 0 ? (
                                <div className="text-center py-12 bg-gray-50 dark:bg-slate-800 rounded-lg border border-dashed border-gray-300 dark:border-slate-700">
                                    <Plane className="w-12 h-12 text-gray-300 dark:text-slate-600 mx-auto mb-3" />
                                    <p className="text-gray-500 dark:text-slate-400">No hay servicios cargados en este file.</p>
                                </div>
                            ) : (
                                <>
                                    {/* Desktop Table View */}
                                    <div className="hidden md:block overflow-hidden rounded-lg border border-gray-200 dark:border-slate-700">
                                        <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
                                            <thead className="bg-gray-50 dark:bg-slate-900 text-gray-500 dark:text-slate-400">
                                                <tr>
                                                    <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider">Tipo</th>
                                                    <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider">Descripción</th>
                                                    <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider">Fecha</th>
                                                    <th className="px-6 py-3 text-left text-xs font-medium uppercase tracking-wider">Estado</th>
                                                    <th className="px-6 py-3 text-right text-xs font-medium uppercase tracking-wider">Venta</th>
                                                    <th className="px-6 py-3 text-right text-xs font-medium uppercase tracking-wider">Acciones</th>
                                                </tr>
                                            </thead>
                                            <tbody className="bg-white dark:bg-slate-800 divide-y divide-gray-200 dark:divide-slate-700">
                                                {allServices.map((svc, idx) => (
                                                    <tr key={`${svc._type}-${svc.id}`} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                                                        <td className="px-6 py-4 whitespace-nowrap">
                                                            <div className="flex items-center">
                                                                {svc._type === 'Flight' && <Plane className="w-5 h-5 text-blue-500 mr-2" />}
                                                                {svc._type === 'Hotel' && <Hotel className="w-5 h-5 text-indigo-500 mr-2" />}
                                                                {svc._type === 'Transfer' && <Car className="w-5 h-5 text-yellow-500 mr-2" />}
                                                                {svc._type === 'Package' && <Package className="w-5 h-5 text-purple-500 mr-2" />}
                                                                <span className="font-medium text-gray-900 dark:text-white">{svc._type === 'Flight' ? 'Aéreo' : svc._type}</span>
                                                            </div>
                                                        </td>
                                                        <td className="px-6 py-4">
                                                            <div className="text-sm text-gray-900 dark:text-white font-medium">{svc.name}</div>
                                                            <div className="text-xs text-gray-500 dark:text-slate-400">{svc.notes || svc.description}</div>
                                                            {svc.confirmationNumber && (
                                                                <div className="text-xs text-indigo-500 mt-1 font-mono">Ref: {svc.confirmationNumber}</div>
                                                            )}
                                                        </td>
                                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                                                            {new Date(svc.date).toLocaleDateString()}
                                                        </td>
                                                        <td className="px-6 py-4 whitespace-nowrap">
                                                            {(() => {
                                                                const isConfirmed = svc.status === 'Confirmado' || svc.status === 'HK';
                                                                return (
                                                                    <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium
                                                                        ${isConfirmed
                                                                            ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400'
                                                                            : 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400'
                                                                        }`}>
                                                                        {isConfirmed ? 'Confirmado' : 'Pendiente'}
                                                                    </span>
                                                                );
                                                            })()}
                                                        </td>
                                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-gray-900 dark:text-white">
                                                            ${svc.salePrice?.toLocaleString()}
                                                        </td>
                                                        <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                                                            <button onClick={() => handleEditService(svc)} className="text-blue-600 hover:text-blue-900 dark:text-blue-400 dark:hover:text-blue-300 mr-3">
                                                                <Edit2 className="w-4 h-4" />
                                                            </button>
                                                            <button onClick={() => handleDeleteService(svc)} className="text-red-600 hover:text-red-900 dark:text-red-400 dark:hover:text-red-300">
                                                                <Trash2 className="w-4 h-4" />
                                                            </button>
                                                        </td>
                                                    </tr>
                                                ))}
                                            </tbody>
                                        </table>
                                    </div>

                                    {/* Mobile Card View */}
                                    <div className="md:hidden space-y-3">
                                        {allServices.map((svc) => (
                                            <div key={`${svc._type}-${svc.id}`} className="bg-white dark:bg-slate-800 rounded-lg p-4 border border-gray-200 dark:border-slate-700 shadow-sm">
                                                <div className="flex justify-between items-start mb-2">
                                                    <div className="flex items-center gap-2">
                                                        {svc._type === 'Flight' && <Plane className="w-4 h-4 text-blue-500" />}
                                                        {svc._type === 'Hotel' && <Hotel className="w-4 h-4 text-indigo-500" />}
                                                        {svc._type === 'Transfer' && <Car className="w-4 h-4 text-yellow-500" />}
                                                        {svc._type === 'Package' && <Package className="w-4 h-4 text-purple-500" />}
                                                        <span className="text-sm font-semibold text-slate-700 dark:text-slate-200">
                                                            {svc._type === 'Flight' ? 'Aéreo' : svc._type}
                                                        </span>
                                                    </div>
                                                    <span className={`text-[10px] uppercase font-bold px-2 py-0.5 rounded-full
                                                        ${(svc.status === 'Confirmado' || svc.status === 'HK')
                                                            ? 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400'
                                                            : 'bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400'
                                                        }`}>
                                                        {svc.status === 'HK' ? 'Confirmado' : svc.status}
                                                    </span>
                                                </div>

                                                <h4 className="font-medium text-slate-900 dark:text-white mb-0.5">{svc.name}</h4>
                                                <p className="text-xs text-slate-500 dark:text-slate-400 mb-2 line-clamp-2">{svc.notes || svc.description}</p>

                                                {svc.confirmationNumber && (
                                                    <div className="text-xs bg-slate-100 dark:bg-slate-900 inline-block px-2 py-1 rounded mb-3 font-mono text-slate-600 dark:text-slate-300">
                                                        REF: {svc.confirmationNumber}
                                                    </div>
                                                )}

                                                <div className="flex justify-between items-end border-t border-slate-100 dark:border-slate-700 pt-3 mt-1">
                                                    <div className="text-xs text-slate-500 dark:text-slate-400 flex items-center gap-1">
                                                        <Calendar className="w-3 h-3" />
                                                        {new Date(svc.date).toLocaleDateString()}
                                                    </div>
                                                    <div className="flex items-center gap-3">
                                                        <div className="text-right">
                                                            <div className="text-xs text-slate-400">Venta</div>
                                                            <div className="font-bold text-slate-900 dark:text-white">${svc.salePrice?.toLocaleString()}</div>
                                                        </div>
                                                        <div className="flex gap-1 pl-3 border-l border-slate-200 dark:border-slate-700">
                                                            <button onClick={() => handleEditService(svc)} className="p-1.5 bg-blue-50 text-blue-600 rounded-md hover:bg-blue-100 dark:bg-slate-700 dark:text-blue-400">
                                                                <Edit2 className="w-4 h-4" />
                                                            </button>
                                                            <button onClick={() => handleDeleteService(svc)} className="p-1.5 bg-red-50 text-red-600 rounded-md hover:bg-red-100 dark:bg-slate-700 dark:text-red-400">
                                                                <Trash2 className="w-4 h-4" />
                                                            </button>
                                                        </div>
                                                    </div>
                                                </div>
                                            </div>
                                        ))}
                                    </div>
                                </>
                            )}
                        </div>
                    )}

                    {/* --- TAB: PASSENGERS --- */}
                    {activeTab === 'passengers' && (
                        <div>
                            <div className="flex justify-between items-center mb-4">
                                <h3 className="text-lg font-medium text-gray-900 dark:text-white">Lista de Pasajeros</h3>
                                <button
                                    onClick={() => { setEditingPassenger(null); setShowPassengerForm(true); }}
                                    className="flex items-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors shadow-sm"
                                >
                                    <Plus className="w-4 h-4" /> Agregar Pasajero
                                </button>
                            </div>

                            {/* Modal is rendered at bottom */}

                            {/* List of Passengers */}
                            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                                {file.passengers?.map(p => (
                                    <div key={p.id} className="bg-white dark:bg-slate-800 border border-gray-200 dark:border-slate-700 rounded-lg p-4 flex justify-between items-start hover:shadow-md transition-shadow">
                                        <div className="min-w-0 pr-2">
                                            <div className="font-medium text-gray-900 dark:text-white truncate" title={p.fullName}>{p.fullName}</div>
                                            <div className="text-sm text-gray-500 dark:text-slate-400 truncate">{p.documentType}: {p.documentNumber}</div>
                                            {p.birthDate && <div className="text-xs text-gray-400 mt-1">Nac: {new Date(p.birthDate).toLocaleDateString()}</div>}
                                        </div>
                                        <div className="flex gap-1">
                                            <button onClick={() => { setEditingPassenger(p); setShowPassengerForm(true); }} className="p-1 text-gray-400 hover:text-blue-600 dark:hover:text-blue-400">
                                                <Edit2 className="w-4 h-4" />
                                            </button>
                                            <button onClick={() => handleDeletePassenger(p.id)} className="p-1 text-gray-400 hover:text-red-600 dark:hover:text-red-400">
                                                <Trash2 className="w-4 h-4" />
                                            </button>
                                        </div>
                                    </div>
                                ))}
                            </div>
                        </div>
                    )}


                    {/* --- TAB: PAYMENTS REMOVED --- */}
                    {/* --- TAB: INVOICES REMOVED --- */}

                    {/* --- TAB: HISTORY --- */}
                    {activeTab === 'history' && (
                        <div>
                            <h3 className="text-lg font-medium text-gray-900 dark:text-white mb-4">Historial de Cambios</h3>
                            <AuditTimeline entityName="TravelFile" entityId={file.id} />
                        </div>
                    )}

                    {/* --- TAB: ATTACHMENTS --- */}
                    {activeTab === 'attachments' && (
                        <FileAttachmentsTab travelFileId={file.id} />
                    )}
                </div>
            </div>



            <ServiceFormModal
                isOpen={showServiceModal}
                onClose={() => { setShowServiceModal(false); setServiceToEdit(null); }}
                fileId={parseInt(id)}
                onSuccess={fetchFile}
                serviceToEdit={serviceToEdit}
                suppliers={suppliers}
            />

            <PassengerFormModal
                isOpen={showPassengerForm}
                onClose={() => { setShowPassengerForm(false); setEditingPassenger(null); }}
                fileId={parseInt(id)}
                onSuccess={fetchFile}
                passengerToEdit={editingPassenger}
            />
        </div >
    );
}
