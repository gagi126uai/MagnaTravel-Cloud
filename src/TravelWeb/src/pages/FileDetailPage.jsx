import { useState, useEffect, useCallback } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { api } from "../api";
import {
    ArrowLeft, Plus, DollarSign, Calendar, Users,
    FileText, Edit2, Trash2, CheckCircle, AlertTriangle,
    Plane, Hotel, Car, Package, CreditCard, Archive
} from "lucide-react";
import Swal from "sweetalert2";
import ServiceFormModal from "../components/ServiceFormModal";
import { showError, showSuccess } from "../alerts";

export default function FileDetailPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [file, setFile] = useState(null);
    const [loading, setLoading] = useState(true);
    const [activeTab, setActiveTab] = useState("services"); // services, passengers, payments, notes
    const [showServiceModal, setShowServiceModal] = useState(false);
    const [serviceToEdit, setServiceToEdit] = useState(null);

    // States for CRUD forms (Passengers, Payments)
    const [showPassengerForm, setShowPassengerForm] = useState(false);
    const [editingPassenger, setEditingPassenger] = useState(null);
    const [showPaymentForm, setShowPaymentForm] = useState(false);

    // Passenger Form State
    const [passengerForm, setPassengerForm] = useState({
        fullName: "", documentType: "DNI", documentNumber: "",
        birthDate: "", nationality: "", phone: "", email: "", gender: "M", notes: ""
    });

    // Payment Form State
    const [paymentForm, setPaymentForm] = useState({
        amount: "", method: "Transferencia", notes: ""
    });

    const fetchFile = useCallback(async () => {
        try {
            setLoading(true);
            const res = await api.get(`/travelfiles/${id}`);
            setFile(res.data);
        } catch (error) {
            console.error(error);
            showError("Error al cargar el file");
            navigate("/files");
        } finally {
            setLoading(false);
        }
    }, [id, navigate]);

    useEffect(() => {
        fetchFile();
    }, [fetchFile]);

    // --- ACTIONS: FILE ---
    const handleArchiveFile = async () => {
        const result = await Swal.fire({
            title: '¿Archivar File?',
            text: "El file pasará a estado 'Archivado' y no será visible en la lista activa.",
            icon: 'warning',
            showCancelButton: true,
            confirmButtonText: 'Sí, archivar',
            cancelButtonText: 'Cancelar'
        });

        if (result.isConfirmed) {
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
        const result = await Swal.fire({
            title: '¿Eliminar File?',
            text: "Esta acción no se puede deshacer. Solo permitido si es un Presupuesto sin pagos.",
            icon: 'error',
            showCancelButton: true,
            confirmButtonColor: '#d33',
            confirmButtonText: 'Sí, eliminar',
            cancelButtonText: 'Cancelar'
        });

        if (result.isConfirmed) {
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
        const result = await Swal.fire({
            title: '¿Eliminar Servicio?',
            text: "Se revertirá el costo y venta del total del file.",
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#d33',
            confirmButtonText: 'Sí, eliminar'
        });

        if (result.isConfirmed) {
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
    const handlePassengerSubmit = async (e) => {
        e.preventDefault();
        try {
            if (editingPassenger) {
                await api.put(`/travelfiles/passengers/${editingPassenger.id}`, passengerForm);
            } else {
                await api.post(`/travelfiles/${id}/passengers`, passengerForm);
            }
            setShowPassengerForm(false);
            setEditingPassenger(null);
            setPassengerForm({ fullName: "", documentType: "DNI", documentNumber: "", birthDate: "", nationality: "", phone: "", email: "", gender: "M", notes: "" });
            fetchFile();
            showSuccess("Pasajero guardado");
        } catch (error) {
            showError(error.response?.data || "Error al guardarpasajero");
        }
    };

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

    // --- ACTIONS: PAYMENTS ---
    const handlePaymentSubmit = async (e) => {
        e.preventDefault();
        try {
            await api.post(`/travelfiles/${id}/payments`, {
                ...paymentForm,
                amount: parseFloat(paymentForm.amount)
            });
            setShowPaymentForm(false);
            setPaymentForm({ amount: "", method: "Transferencia", notes: "" });
            fetchFile();
            showSuccess("Pago registrado correctamente");
        } catch (error) {
            showError(error.response?.data || "Error al registrar pago");
        }
    };

    const handleDeletePayment = async (paymentId, amount) => {
        const result = await Swal.fire({
            title: '¿Eliminar Pago?',
            text: `Se anulará el pago de $${amount} y aumentará el saldo pendiente.`,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#d33',
            confirmButtonText: 'Sí, eliminar'
        });

        if (result.isConfirmed) {
            try {
                await api.delete(`/travelfiles/${id}/payments/${paymentId}`);
                fetchFile();
                showSuccess("Pago eliminado");
            } catch (error) {
                showError("Error al eliminar pago");
            }
        }
    };

    // --- HELPERS ---
    const confirmAction = async (title) => {
        const result = await Swal.fire({
            title,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonText: 'Sí',
            cancelButtonText: 'No'
        });
        return result.isConfirmed;
    };

    // --- RENDER HELPERS ---
    const getAllServices = () => {
        if (!file) return [];
        const services = [];
        file.flightSegments?.forEach(f => services.push({ ...f, _type: 'Flight', date: f.departureTime, name: `${f.airlineName} ${f.flightNumber}` }));
        file.hotelBookings?.forEach(h => services.push({ ...h, _type: 'Hotel', date: h.checkIn, name: h.hotelName }));
        file.transferBookings?.forEach(t => services.push({ ...t, _type: 'Transfer', date: t.pickupDateTime, name: `${t.pickupLocation} > ${t.dropoffLocation}` }));
        file.packageBookings?.forEach(p => services.push({ ...p, _type: 'Package', date: p.startDate, name: p.packageName }));
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
    if (!file) return <div className="p-8 text-center">File no encontrado</div>;

    const allServices = getAllServices();

    return (
        <div className="max-w-7xl mx-auto p-4 sm:p-6 lg:p-8">

            {/* HEADER */}
            <div className="mb-8 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div>
                    <button
                        onClick={() => navigate("/files")}
                        className="flex items-center text-gray-500 hover:text-gray-700 mb-2 transition-colors"
                    >
                        <ArrowLeft className="w-4 h-4 mr-1" /> Volver a Lista
                    </button>
                    <div className="flex items-center gap-3">
                        <h1 className="text-3xl font-bold text-gray-900">File #{file.fileNumber}</h1>
                        <span className={`px-3 py-1 rounded-full text-sm font-medium 
                ${file.status === 'Budget' ? 'bg-gray-100 text-gray-800' :
                                file.status === 'Reserved' ? 'bg-blue-100 text-blue-800' :
                                    file.status === 'Operational' ? 'bg-purple-100 text-purple-800' :
                                        file.status === 'Closed' ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'}`}>
                            {file.status === 'Budget' ? 'Presupuesto' :
                                file.status === 'Reserved' ? 'Reservado' :
                                    file.status === 'Operational' ? 'Operativo' : file.status}
                        </span>
                    </div>
                    <p className="text-xl text-gray-600 mt-1">{file.name}</p>
                </div>

                <div className="flex flex-wrap gap-2">
                    {/* STATUS ACTIONS */}
                    {file.status === 'Budget' && (
                        <button onClick={() => handleStatusChange('Reserved')} className="btn btn-primary bg-blue-600 hover:bg-blue-700 text-white px-4 py-2 rounded shadow">
                            Confirmar Reserva
                        </button>
                    )}
                    {file.status === 'Reserved' && (
                        <button onClick={() => handleStatusChange('Operational')} className="btn btn-secondary bg-purple-600 hover:bg-purple-700 text-white px-4 py-2 rounded shadow">
                            Pasar a Operativo
                        </button>
                    )}
                    {file.status === 'Operational' && (
                        <button onClick={() => handleStatusChange('Closed')} className="btn btn-success bg-green-600 hover:bg-green-700 text-white px-4 py-2 rounded shadow">
                            Cerrar File
                        </button>
                    )}

                    {/* ADMIN ACTIONS */}
                    <div className="ml-2 pl-2 border-l border-gray-300 flex gap-2">
                        {file.status === 'Budget' && (
                            <button onClick={handleDeleteFile} className="btn bg-red-100 text-red-700 hover:bg-red-200 px-3 py-2 rounded" title="Eliminar File">
                                <Trash2 className="w-5 h-5" />
                            </button>
                        )}
                        <button onClick={handleArchiveFile} className="btn bg-gray-100 text-gray-600 hover:bg-gray-200 px-3 py-2 rounded" title="Archivar">
                            <Archive className="w-5 h-5" />
                        </button>
                    </div>
                </div>
            </div>

            {/* SUMMARY CARDS */}
            <div className="grid grid-cols-1 md:grid-cols-4 gap-6 mb-8">
                <div className="bg-white rounded-xl shadow-sm p-6 border border-gray-100">
                    <p className="text-sm font-medium text-gray-500 mb-1">Total Venta</p>
                    <p className="text-2xl font-bold text-gray-900">${file.totalSale?.toLocaleString()}</p>
                </div>
                <div className="bg-white rounded-xl shadow-sm p-6 border border-gray-100">
                    <p className="text-sm font-medium text-gray-500 mb-1">Total Costo</p>
                    <p className="text-2xl font-bold text-gray-900">${file.totalCost?.toLocaleString()}</p>
                </div>
                <div className="bg-white rounded-xl shadow-sm p-6 border border-gray-100">
                    <p className="text-sm font-medium text-gray-500 mb-1">Cobrado</p>
                    <p className="text-2xl font-bold text-green-600">${(file.totalSale - file.balance)?.toLocaleString()}</p>
                </div>
                <div className={`bg-white rounded-xl shadow-sm p-6 border-l-4 ${file.balance > 0 ? 'border-red-500' : 'border-green-500'}`}>
                    <div className="flex justify-between items-start">
                        <div>
                            <p className="text-sm font-medium text-gray-500 mb-1">Saldo Pendiente</p>
                            <p className={`text-2xl font-bold ${file.balance > 0 ? 'text-red-600' : 'text-green-600'}`}>
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
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden min-h-[500px]">
                <div className="border-b border-gray-200">
                    <nav className="flex -mb-px">
                        <button
                            onClick={() => setActiveTab('services')}
                            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
                ${activeTab === 'services' ? 'border-blue-500 text-blue-600 bg-blue-50' : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'}`}
                        >
                            <FileText className="w-4 h-4" /> Servicios
                        </button>
                        <button
                            onClick={() => setActiveTab('passengers')}
                            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
                ${activeTab === 'passengers' ? 'border-blue-500 text-blue-600 bg-blue-50' : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'}`}
                        >
                            <Users className="w-4 h-4" /> Pasajeros ({file.passengers?.length || 0})
                        </button>
                        <button
                            onClick={() => setActiveTab('payments')}
                            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
                ${activeTab === 'payments' ? 'border-blue-500 text-blue-600 bg-blue-50' : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'}`}
                        >
                            <DollarSign className="w-4 h-4" /> Pagos
                        </button>
                    </nav>
                </div>

                <div className="p-6">
                    {/* --- TAB: SERVICES --- */}
                    {activeTab === 'services' && (
                        <div>
                            <div className="flex justify-between items-center mb-4">
                                <h3 className="text-lg font-medium text-gray-900">Servicios Contratados</h3>
                                <button
                                    onClick={() => { setServiceToEdit(null); setShowServiceModal(true); }}
                                    className="flex items-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors shadow-sm"
                                >
                                    <Plus className="w-4 h-4" /> Agregar Servicio
                                </button>
                            </div>

                            {allServices.length === 0 ? (
                                <div className="text-center py-12 bg-gray-50 rounded-lg border border-dashed border-gray-300">
                                    <Plane className="w-12 h-12 text-gray-300 mx-auto mb-3" />
                                    <p className="text-gray-500">No hay servicios cargados en este file.</p>
                                </div>
                            ) : (
                                <div className="overflow-hidden rounded-lg border border-gray-200">
                                    <table className="min-w-full divide-y divide-gray-200">
                                        <thead className="bg-gray-50">
                                            <tr>
                                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Tipo</th>
                                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Descripción</th>
                                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Fecha</th>
                                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Confirmación</th>
                                                <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Venta</th>
                                                <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Acciones</th>
                                            </tr>
                                        </thead>
                                        <tbody className="bg-white divide-y divide-gray-200">
                                            {allServices.map((svc, idx) => (
                                                <tr key={`${svc._type}-${svc.id}`} className="hover:bg-gray-50">
                                                    <td className="px-6 py-4 whitespace-nowrap">
                                                        <div className="flex items-center">
                                                            {svc._type === 'Flight' && <Plane className="w-5 h-5 text-blue-500 mr-2" />}
                                                            {svc._type === 'Hotel' && <Hotel className="w-5 h-5 text-indigo-500 mr-2" />}
                                                            {svc._type === 'Transfer' && <Car className="w-5 h-5 text-yellow-500 mr-2" />}
                                                            {svc._type === 'Package' && <Package className="w-5 h-5 text-purple-500 mr-2" />}
                                                            <span className="font-medium text-gray-900">{svc._type === 'Flight' ? 'Aéreo' : svc._type}</span>
                                                        </div>
                                                    </td>
                                                    <td className="px-6 py-4">
                                                        <div className="text-sm text-gray-900 font-medium">{svc.name}</div>
                                                        <div className="text-xs text-gray-500">{svc.notes || svc.description}</div>
                                                    </td>
                                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                                                        {new Date(svc.date).toLocaleDateString()}
                                                    </td>
                                                    <td className="px-6 py-4 whitespace-nowrap">
                                                        <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium
                                      {svc.confirmationNumber ? 'bg-green-100 text-green-800' : 'bg-yellow-100 text-yellow-800'}`}>
                                                            {svc.confirmationNumber || 'Pendiente'}
                                                        </span>
                                                    </td>
                                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-gray-900">
                                                        ${svc.salePrice?.toLocaleString()}
                                                    </td>
                                                    <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                                                        <button onClick={() => handleEditService(svc)} className="text-blue-600 hover:text-blue-900 mr-3">
                                                            <Edit2 className="w-4 h-4" />
                                                        </button>
                                                        <button onClick={() => handleDeleteService(svc)} className="text-red-600 hover:text-red-900">
                                                            <Trash2 className="w-4 h-4" />
                                                        </button>
                                                    </td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>
                            )}
                        </div>
                    )}

                    {/* --- TAB: PASSENGERS --- */}
                    {activeTab === 'passengers' && (
                        <div>
                            <div className="flex justify-between items-center mb-4">
                                <h3 className="text-lg font-medium text-gray-900">Lista de Pasajeros</h3>
                                <button
                                    onClick={() => { setEditingPassenger(null); setShowPassengerForm(true); }}
                                    className="flex items-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors shadow-sm"
                                >
                                    <Plus className="w-4 h-4" /> Agregar Pasajero
                                </button>
                            </div>

                            {showPassengerForm && (
                                <div className="bg-gray-50 p-4 rounded-lg border border-gray-200 mb-6">
                                    <h4 className="text-sm font-bold text-gray-700 mb-3">{editingPassenger ? 'Editar Pasajero' : 'Nuevo Pasajero'}</h4>
                                    <form onSubmit={handlePassengerSubmit} className="grid grid-cols-1 md:grid-cols-3 gap-4">
                                        <div>
                                            <label className="block text-xs font-medium text-gray-700">Nombre Completo</label>
                                            <input required type="text" className="mt-1 block w-full rounded-md border-gray-300 shadow-sm sm:text-sm"
                                                value={passengerForm.fullName} onChange={e => setPassengerForm({ ...passengerForm, fullName: e.target.value })} />
                                        </div>
                                        <div>
                                            <label className="block text-xs font-medium text-gray-700">Documento</label>
                                            <div className="flex gap-2">
                                                <select className="mt-1 block w-24 rounded-md border-gray-300 shadow-sm sm:text-sm"
                                                    value={passengerForm.documentType} onChange={e => setPassengerForm({ ...passengerForm, documentType: e.target.value })}>
                                                    <option>DNI</option> <option>Pasaporte</option>
                                                </select>
                                                <input type="text" className="mt-1 block w-full rounded-md border-gray-300 shadow-sm sm:text-sm"
                                                    value={passengerForm.documentNumber} onChange={e => setPassengerForm({ ...passengerForm, documentNumber: e.target.value })} />
                                            </div>
                                        </div>
                                        <div>
                                            <label className="block text-xs font-medium text-gray-700">Fecha Nac.</label>
                                            <input type="date" className="mt-1 block w-full rounded-md border-gray-300 shadow-sm sm:text-sm"
                                                value={passengerForm.birthDate ? passengerForm.birthDate.split('T')[0] : ''}
                                                onChange={e => setPassengerForm({ ...passengerForm, birthDate: e.target.value })} />
                                        </div>
                                        <div className="col-span-full flex justify-end gap-2 mt-2">
                                            <button type="button" onClick={() => setShowPassengerForm(false)} className="px-3 py-2 text-sm text-gray-600 hover:text-gray-800">Cancelar</button>
                                            <button type="submit" className="px-4 py-2 bg-blue-600 text-white text-sm rounded hover:bg-blue-700">Guardar</button>
                                        </div>
                                    </form>
                                </div>
                            )}

                            {/* List of Passengers */}
                            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                                {file.passengers?.map(p => (
                                    <div key={p.id} className="bg-white border border-gray-200 rounded-lg p-4 flex justify-between items-start hover:shadow-md transition-shadow">
                                        <div>
                                            <div className="font-medium text-gray-900">{p.fullName}</div>
                                            <div className="text-sm text-gray-500">{p.documentType}: {p.documentNumber}</div>
                                            {p.birthDate && <div className="text-xs text-gray-400 mt-1">Nac: {new Date(p.birthDate).toLocaleDateString()}</div>}
                                        </div>
                                        <div className="flex gap-1">
                                            <button onClick={() => { setEditingPassenger(p); setPassengerForm(p); setShowPassengerForm(true); }} className="p-1 text-gray-400 hover:text-blue-600">
                                                <Edit2 className="w-4 h-4" />
                                            </button>
                                            <button onClick={() => handleDeletePassenger(p.id)} className="p-1 text-gray-400 hover:text-red-600">
                                                <Trash2 className="w-4 h-4" />
                                            </button>
                                        </div>
                                    </div>
                                ))}
                            </div>
                        </div>
                    )}

                    {/* --- TAB: PAYMENTS --- */}
                    {activeTab === 'payments' && (
                        <div>
                            <div className="flex justify-between items-center mb-4">
                                <h3 className="text-lg font-medium text-gray-900">Registro de Pagos</h3>
                                <button
                                    onClick={() => setShowPaymentForm(true)}
                                    className="flex items-center gap-2 bg-green-600 text-white px-4 py-2 rounded-lg hover:bg-green-700 transition-colors shadow-sm"
                                    disabled={file.balance <= 0}
                                >
                                    <Plus className="w-4 h-4" /> Registrar Pago
                                </button>
                            </div>

                            {showPaymentForm && (
                                <div className="bg-green-50 p-4 rounded-lg border border-green-200 mb-6">
                                    <h4 className="text-sm font-bold text-green-800 mb-3">Nuevo Pago</h4>
                                    <form onSubmit={handlePaymentSubmit} className="flex flex-wrap gap-4 items-end">
                                        <div className="w-40">
                                            <label className="block text-xs font-medium text-gray-700">Monto</label>
                                            <div className="relative mt-1 rounded-md shadow-sm">
                                                <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3">
                                                    <span className="text-gray-500 sm:text-sm">$</span>
                                                </div>
                                                <input type="number" step="0.01" required className="block w-full rounded-md border-gray-300 pl-7 sm:text-sm"
                                                    value={paymentForm.amount} onChange={e => setPaymentForm({ ...paymentForm, amount: e.target.value })}
                                                    max={file.balance} />
                                            </div>
                                        </div>
                                        <div className="w-48">
                                            <label className="block text-xs font-medium text-gray-700">Método</label>
                                            <select className="mt-1 block w-full rounded-md border-gray-300 shadow-sm sm:text-sm"
                                                value={paymentForm.method} onChange={e => setPaymentForm({ ...paymentForm, method: e.target.value })}>
                                                <option>Efectivo</option><option>Transferencia</option><option>Tarjeta Crédito</option><option>Tarjeta Débito</option>
                                            </select>
                                        </div>
                                        <div className="flex-1">
                                            <label className="block text-xs font-medium text-gray-700">Notas</label>
                                            <input type="text" className="mt-1 block w-full rounded-md border-gray-300 shadow-sm sm:text-sm"
                                                value={paymentForm.notes} onChange={e => setPaymentForm({ ...paymentForm, notes: e.target.value })} />
                                        </div>
                                        <button type="submit" className="px-4 py-2 bg-green-600 text-white text-sm rounded hover:bg-green-700">Registrar</button>
                                        <button type="button" onClick={() => setShowPaymentForm(false)} className="px-3 py-2 text-sm text-gray-600 hover:text-gray-800">Cancelar</button>
                                    </form>
                                </div>
                            )}

                            <div className="overflow-hidden rounded-lg border border-gray-200">
                                <table className="min-w-full divide-y divide-gray-200">
                                    <thead className="bg-gray-50">
                                        <tr>
                                            <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Fecha</th>
                                            <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Método</th>
                                            <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Monto</th>
                                            <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Acciones</th>
                                        </tr>
                                    </thead>
                                    <tbody className="bg-white divide-y divide-gray-200">
                                        {file.payments?.map(p => (
                                            <tr key={p.id}>
                                                <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                                                    {new Date(p.paidAt).toLocaleDateString()}
                                                </td>
                                                <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                                                    {p.method}
                                                </td>
                                                <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-green-600">
                                                    ${p.amount.toLocaleString()}
                                                </td>
                                                <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                                                    <button onClick={() => handleDeletePayment(p.id, p.amount)} className="text-red-400 hover:text-red-600 p-1">
                                                        <Trash2 className="w-4 h-4" />
                                                    </button>
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        </div>
                    )}
                </div>
            </div>

            <ServiceFormModal
                isOpen={showServiceModal}
                onClose={() => { setShowServiceModal(false); setServiceToEdit(null); }}
                fileId={parseInt(id)}
                onSuccess={fetchFile}
                serviceToEdit={serviceToEdit}
            />
        </div>
    );
}
