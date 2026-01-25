import { useEffect, useState, useRef } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    ArrowLeft,
    Calendar,
    User,
    Plus,
    Plane,
    Hotel,
    Bus,
    CreditCard,
    FileText,
    Trash2,
    Edit2,
    X,
    ChevronDown
} from "lucide-react";
import { Button } from "../components/ui/button";

// SERVICE TYPES CONSTANTS
const SERVICE_ICONS = {
    Aereo: <Plane className="h-5 w-5" />,
    Hotel: <Hotel className="h-5 w-5" />,
    Traslado: <Bus className="h-5 w-5" />,
    Otro: <CreditCard className="h-5 w-5" />
};

export default function FileDetailPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [file, setFile] = useState(null);
    const [loading, setLoading] = useState(true);
    const [activeTab, setActiveTab] = useState("services");
    const [suppliers, setSuppliers] = useState([]);
    const dropdownRef = useRef(null);

    // UI States
    const [isDropdownOpen, setIsDropdownOpen] = useState(false);
    const [isServiceModalOpen, setIsServiceModalOpen] = useState(false);
    const [payments, setPayments] = useState([]);

    // Form State
    const [serviceType, setServiceType] = useState("Aereo");
    const [serviceForm, setServiceForm] = useState({
        supplierId: "",
        description: "",
        confirmationNumber: "",
        departureDate: "",
        returnDate: "",
        salePrice: 0,
        netCost: 0
    });

    // Payment Form State
    const [paymentForm, setPaymentForm] = useState({
        amount: "",
        method: "Transfer",
        notes: ""
    });

    useEffect(() => {
        loadFile();
        loadSuppliers();
        loadPayments();

        // Click outside to close dropdown
        function handleClickOutside(event) {
            if (dropdownRef.current && !dropdownRef.current.contains(event.target)) {
                setIsDropdownOpen(false);
            }
        }
        document.addEventListener("mousedown", handleClickOutside);
        return () => document.removeEventListener("mousedown", handleClickOutside);
    }, [id]);

    const loadFile = async () => {
        setLoading(true);
        try {
            const data = await api.get(`/travelfiles/${id}`);
            setFile(data);
        } catch (error) {
            showError("No se pudo cargar el expediente.");
        } finally {
            setLoading(false);
        }
    };

    const loadSuppliers = async () => {
        try {
            const data = await api.get("/suppliers");
            setSuppliers(data);
        } catch {
            console.log("Error loading suppliers");
        }
    };

    const loadPayments = async () => {
        try {
            // Assuming we need to get payments related to this file's reservations
            // For now, let's just try to fetch all payments and filter on frontend
            const allPayments = await api.get("/payments");
            // Filter payments that belong to reservations of this file
            const fileReservationIds = file?.reservations?.map(r => r.id) || [];
            const filteredPayments = allPayments.filter(p => fileReservationIds.includes(p.reservationId));
            setPayments(filteredPayments);
        } catch {
            console.log("Error loading payments");
        }
    };

    const handleAddPayment = async (e) => {
        e.preventDefault();
        if (!file?.reservations?.length) {
            showError("Debe tener al menos un servicio cargado para registrar un pago.");
            return;
        }

        try {
            // Associate payment with the first reservation (or we could let user choose)
            const firstReservation = file.reservations[0];
            await api.post("/payments", {
                reservationId: firstReservation.id,
                amount: parseFloat(paymentForm.amount),
                method: paymentForm.method,
                status: "Paid",
                paidAt: new Date().toISOString(),
                notes: paymentForm.notes
            });

            showSuccess("Pago registrado correctamente");
            setPaymentForm({ amount: "", method: "Transfer", notes: "" });
            loadPayments();
            loadFile(); // Reload to update balance
        } catch (error) {
            showError("Error al registrar el pago");
        }
    };

    const handleAddService = async (e) => {
        e.preventDefault();
        try {
            await api.post(`/travelfiles/${id}/services`, {
                ...serviceForm,
                serviceType: serviceType,
                supplierId: serviceForm.supplierId ? parseInt(serviceForm.supplierId) : null,
                departureDate: new Date(serviceForm.departureDate).toISOString(),
                returnDate: serviceForm.returnDate ? new Date(serviceForm.returnDate).toISOString() : null,
                salePrice: parseFloat(serviceForm.salePrice),
                netCost: parseFloat(serviceForm.netCost)
            });
            showSuccess("Servicio agregado correctamente");
            setIsServiceModalOpen(false);
            loadFile();
            setServiceForm({
                supplierId: "",
                description: "",
                confirmationNumber: "",
                departureDate: "",
                returnDate: "",
                salePrice: 0,
                netCost: 0
            });
        } catch (error) {
            showError(error.response?.data || "Error al agregar servicio");
        }
    };

    const handleDeleteService = async (serviceId) => {
        if (!confirm("¿Eliminar este servicio del expediente?")) return;
        try {
            await api.delete(`/travelfiles/services/${serviceId}`);
            showSuccess("Servicio eliminado");
            loadFile();
        } catch (error) {
            showError("Error al eliminar servicio");
        }
    };

    const openServiceModal = (type) => {
        setServiceType(type);
        setServiceForm(prev => ({ ...prev, description: type }));
        setIsDropdownOpen(false); // Close dropdown if open
        setIsServiceModalOpen(true);
    }

    const toggleDropdown = () => setIsDropdownOpen(!isDropdownOpen);

    const EmptyState = () => (
        <div className="flex flex-col items-center justify-center rounded-2xl border border-dashed border-slate-300 bg-slate-50 p-12 text-center dark:border-slate-700 dark:bg-slate-900/50">
            <div className="mb-4 rounded-full bg-indigo-50 p-4 text-indigo-500 dark:bg-indigo-900/20">
                <Plus className="h-8 w-8" />
            </div>
            <h3 className="text-lg font-semibold">Sin servicios cargados</h3>
            <p className="text-sm text-muted-foreground mt-1 max-w-sm">
                Comienza armando el itinerario agregando vuelos, hoteles o traslados.
            </p>
            <div className="mt-6 flex gap-3">
                <Button variant="outline" onClick={() => openServiceModal("Aereo")}>
                    <Plane className="h-4 w-4 mr-2" /> Aéreo
                </Button>
                <Button variant="outline" onClick={() => openServiceModal("Hotel")}>
                    <Hotel className="h-4 w-4 mr-2" /> Hotel
                </Button>
                <Button variant="outline" onClick={() => openServiceModal("Traslado")}>
                    <Bus className="h-4 w-4 mr-2" /> Traslado
                </Button>
            </div>
        </div>
    );

    if (loading) return <div>Cargando expediente...</div>;
    if (!file) return <div>No encontrado</div>;

    return (
        <div className="space-y-6">
            {/* Header */}
            <div className="flex flex-col md:flex-row md:items-center gap-4 justify-between">
                <div className="flex items-center gap-4">
                    <Button variant="ghost" size="icon" onClick={() => navigate("/files")}>
                        <ArrowLeft className="h-5 w-5" />
                    </Button>
                    <div>
                        <h1 className="text-2xl font-bold flex items-center gap-2">
                            {file.name}
                            <span className="text-sm font-normal text-muted-foreground bg-slate-100 px-2 py-1 rounded dark:bg-slate-800">
                                {file.fileNumber}
                            </span>
                        </h1>
                        <div className="flex flex-wrap items-center gap-4 text-sm text-muted-foreground mt-1">
                            <span className="flex items-center gap-1">
                                <User className="h-3 w-3" /> {file.payer?.fullName || "Sin cliente"}
                            </span>
                            <span className="flex items-center gap-1">
                                <Calendar className="h-3 w-3" />
                                {file.startDate ? new Date(file.startDate).toLocaleDateString() : "Fecha abierta"}
                            </span>
                            <span className={`px-2 rounded-full text-xs font-medium bg-blue-100 text-blue-700`}>
                                {file.status}
                            </span>
                        </div>
                    </div>
                </div>
                <div className="flex gap-2 relative">
                    <Button variant="outline">
                        <FileText className="h-4 w-4 mr-2" /> Voucher
                    </Button>

                    <div className="relative" ref={dropdownRef}>
                        <Button onClick={toggleDropdown} className={isDropdownOpen ? "ring-2 ring-indigo-500 ring-offset-2" : ""}>
                            <Plus className="h-4 w-4 mr-2" /> Agregar Servicio <ChevronDown className="h-4 w-4 ml-1 opacity-70" />
                        </Button>

                        {isDropdownOpen && (
                            <div className="absolute right-0 top-full mt-2 w-56 rounded-xl border border-slate-200 bg-white p-1 shadow-xl z-30 dark:border-slate-700 dark:bg-slate-800 animate-in fade-in zoom-in-95 duration-100">
                                <button onClick={() => openServiceModal("Aereo")} className="flex w-full items-center rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-700 gap-3 text-left transition-colors">
                                    <div className="rounded-md bg-blue-50 p-1.5 text-blue-600 dark:bg-blue-900/30 dark:text-blue-400"><Plane className="h-4 w-4" /></div>
                                    <span className="font-medium">Aéreo</span>
                                </button>
                                <button onClick={() => openServiceModal("Hotel")} className="flex w-full items-center rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-700 gap-3 text-left transition-colors">
                                    <div className="rounded-md bg-amber-50 p-1.5 text-amber-600 dark:bg-amber-900/30 dark:text-amber-400"><Hotel className="h-4 w-4" /></div>
                                    <span className="font-medium">Hotel</span>
                                </button>
                                <button onClick={() => openServiceModal("Traslado")} className="flex w-full items-center rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-700 gap-3 text-left transition-colors">
                                    <div className="rounded-md bg-emerald-50 p-1.5 text-emerald-600 dark:bg-emerald-900/30 dark:text-emerald-400"><Bus className="h-4 w-4" /></div>
                                    <span className="font-medium">Traslado</span>
                                </button>
                                <div className="my-1 h-px bg-slate-100 dark:bg-slate-700"></div>
                                <button onClick={() => openServiceModal("Otro")} className="flex w-full items-center rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-700 gap-3 text-left transition-colors">
                                    <div className="rounded-md bg-slate-50 p-1.5 text-slate-600 dark:bg-slate-700 dark:text-slate-400"><CreditCard className="h-4 w-4" /></div>
                                    <span className="font-medium">Otro Servicio</span>
                                </button>
                            </div>
                        )}
                    </div>
                </div>
            </div>

            {/* Financial Summary */}
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                <div className="rounded-xl border bg-card p-4">
                    <div className="text-xs text-muted-foreground uppercase font-bold">Total Venta</div>
                    <div className="text-2xl font-bold mt-1">${file.totalSale?.toLocaleString()}</div>
                </div>
                <div className="rounded-xl border bg-card p-4">
                    <div className="text-xs text-muted-foreground uppercase font-bold">Costo Neto</div>
                    <div className="text-2xl font-bold mt-1 text-slate-600 dark:text-slate-400">${file.totalCost?.toLocaleString()}</div>
                </div>
                <div className="rounded-xl border bg-card p-4">
                    <div className="text-xs text-muted-foreground uppercase font-bold">Rentabilidad</div>
                    <div className={`text-2xl font-bold mt-1 ${(file.totalSale - file.totalCost) >= 0 ? 'text-emerald-600' : 'text-rose-600'}`}>
                        ${(file.totalSale - file.totalCost).toLocaleString()}
                    </div>
                </div>
                <div className="rounded-xl border bg-card p-4 border-l-4 border-l-indigo-500 bg-indigo-50/20">
                    <div className="text-xs text-muted-foreground uppercase font-bold">Saldo Pendiente</div>
                    <div className="text-2xl font-bold mt-1 text-indigo-600">
                        ${file.balance?.toLocaleString()}
                    </div>
                </div>
            </div>

            {/* Tabs */}
            <div className="flex gap-6 border-b border-slate-200 dark:border-slate-800 overflow-x-auto">
                {['services', 'payments', 'documents', 'notes'].map(tab => (
                    <button
                        key={tab}
                        onClick={() => setActiveTab(tab)}
                        className={`pb-3 text-sm font-medium border-b-2 transition-colors capitalize whitespace-nowrap ${activeTab === tab
                            ? 'border-primary text-primary'
                            : 'border-transparent text-muted-foreground hover:text-foreground'
                            }`}
                    >
                        {tab === 'services' ? 'Itinerario' : tab === 'payments' ? 'Pagos / Cobros' : tab === 'documents' ? 'Documentos' : 'Notas'}
                    </button>
                ))}
            </div>

            {/* Services Tab */}
            {activeTab === 'services' && (
                <div className="space-y-4">
                    {file.reservations && file.reservations.length > 0 ? (
                        <div className="space-y-3">
                            {file.reservations.map(res => (
                                <div key={res.id} className="flex flex-col sm:flex-row sm:items-center gap-4 rounded-xl border bg-card p-4 hover:shadow-sm transition-shadow">
                                    <div className={`h-10 w-10 flex items-center justify-center rounded-lg bg-slate-100 text-slate-600 shrink-0`}>
                                        {SERVICE_ICONS[res.serviceType] || <CreditCard className="h-5 w-5" />}
                                    </div>
                                    <div className="flex-1">
                                        <div className="flex items-center gap-2">
                                            <h4 className="font-semibold">{res.description || res.serviceType}</h4>
                                            <span className="text-xs bg-slate-100 px-2 py-0.5 rounded text-slate-600 uppercase">
                                                {res.status}
                                            </span>
                                        </div>
                                        <div className="text-sm text-muted-foreground mt-1">
                                            {res.supplier?.name} • Conf: {res.confirmationNumber || "Pendiente"}
                                        </div>
                                    </div>
                                    <div className="flex justify-between sm:block sm:text-right gap-4 mt-2 sm:mt-0">
                                        <div>
                                            <div className="font-bold text-lg">${res.salePrice?.toLocaleString()}</div>
                                            <div className="text-xs text-muted-foreground">
                                                {new Date(res.departureDate).toLocaleDateString()}
                                            </div>
                                        </div>
                                        <div className="flex gap-1 sm:hidden">
                                            <Button variant="ghost" size="icon" className="text-rose-500" onClick={() => handleDeleteService(res.id)}>
                                                <Trash2 className="h-4 w-4" />
                                            </Button>
                                        </div>
                                    </div>
                                    <div className="hidden sm:flex gap-1">
                                        <Button variant="ghost" size="icon" onClick={() => handleDeleteService(res.id)} className="text-rose-500 hover:text-rose-600 hover:bg-rose-50">
                                            <Trash2 className="h-4 w-4" />
                                        </Button>
                                    </div>
                                </div>
                            ))}
                        </div>
                    ) : (
                        <EmptyState />
                    )}
                </div>
            )}

            {/* Payments Tab */}
            {activeTab === 'payments' && (
                <div className="space-y-6">
                    {/* Payment Form */}
                    <div className="rounded-xl border bg-card p-6">
                        <h3 className="text-lg font-semibold mb-4 flex items-center gap-2">
                            <CreditCard className="h-5 w-5 text-primary" />
                            Registrar Cobro
                        </h3>
                        <form onSubmit={handleAddPayment} className="grid grid-cols-1 md:grid-cols-3 gap-4">
                            <div>
                                <label className="text-sm font-medium text-muted-foreground mb-1.5 block">Monto</label>
                                <input
                                    type="number"
                                    step="0.01"
                                    required
                                    className="w-full rounded-lg border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                                    placeholder="0.00"
                                    value={paymentForm.amount}
                                    onChange={(e) => setPaymentForm({ ...paymentForm, amount: e.target.value })}
                                />
                            </div>
                            <div>
                                <label className="text-sm font-medium text-muted-foreground mb-1.5 block">Método</label>
                                <select
                                    className="w-full rounded-lg border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                                    value={paymentForm.method}
                                    onChange={(e) => setPaymentForm({ ...paymentForm, method: e.target.value })}
                                >
                                    <option value="Cash">Efectivo</option>
                                    <option value="Transfer">Transferencia</option>
                                    <option value="Card">Tarjeta</option>
                                </select>
                            </div>
                            <div className="md:col-span-1 flex items-end">
                                <Button type="submit" className="w-full">
                                    <Plus className="h-4 w-4 mr-2" />
                                    Registrar
                                </Button>
                            </div>
                            <div className="md:col-span-3">
                                <label className="text-sm font-medium text-muted-foreground mb-1.5 block">Notas (opcional)</label>
                                <input
                                    type="text"
                                    className="w-full rounded-lg border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                                    placeholder="Ej: Transferencia Banco Nación"
                                    value={paymentForm.notes}
                                    onChange={(e) => setPaymentForm({ ...paymentForm, notes: e.target.value })}
                                />
                            </div>
                        </form>
                    </div>

                    {/* Payments List */}
                    <div className="rounded-xl border overflow-hidden">
                        <div className="bg-muted/50 px-6 py-3 border-b">
                            <h4 className="font-medium text-sm">Historial de Cobros</h4>
                        </div>
                        {payments.length > 0 ? (
                            <div className="divide-y">
                                {payments.map((payment) => (
                                    <div key={payment.id} className="flex items-center justify-between px-6 py-4 hover:bg-muted/30 transition-colors">
                                        <div>
                                            <div className="font-medium">${payment.amount?.toLocaleString()}</div>
                                            <div className="text-sm text-muted-foreground">{new Date(payment.paidAt).toLocaleDateString()} · {payment.method}</div>
                                            {payment.notes && <div className="text-xs text-muted-foreground mt-1">{payment.notes}</div>}
                                        </div>
                                        <span className="px-2.5 py-0.5 rounded-full text-xs font-medium bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400">
                                            Cobrado
                                        </span>
                                    </div>
                                ))}
                            </div>
                        ) : (
                            <div className="px-6 py-12 text-center text-muted-foreground text-sm">
                                No hay pagos registrados aún.
                            </div>
                        )}
                    </div>
                </div>
            )}

            {/* Service Modal */}
            {isServiceModalOpen && (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 overflow-y-auto">
                    <div className="w-full max-w-lg rounded-2xl bg-white shadow-2xl dark:bg-slate-900 border dark:border-slate-800">
                        <div className="flex items-center justify-between border-b p-4 dark:border-slate-800">
                            <h3 className="text-lg font-semibold flex items-center gap-2 text-slate-900 dark:text-white">
                                {SERVICE_ICONS[serviceType]}
                                Cargar {serviceType}
                            </h3>
                            <button onClick={() => setIsServiceModalOpen(false)} className="text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200">
                                <X className="h-5 w-5" />
                            </button>
                        </div>
                        <form onSubmit={handleAddService} className="p-6 space-y-4">
                            {/* Provider & Desc */}
                            <div className="grid grid-cols-2 gap-4">
                                <div className="col-span-2 sm:col-span-1">
                                    <label className="block text-sm font-medium mb-1 text-slate-700 dark:text-slate-300">Proveedor</label>
                                    <select
                                        className="w-full rounded-lg border border-slate-300 bg-white p-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                        value={serviceForm.supplierId}
                                        onChange={e => setServiceForm({ ...serviceForm, supplierId: e.target.value })}
                                        required
                                    >
                                        <option value="">Seleccionar...</option>
                                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                                    </select>
                                </div>
                                <div className="col-span-2 sm:col-span-1">
                                    <label className="block text-sm font-medium mb-1 text-slate-700 dark:text-slate-300">Código Confirmación</label>
                                    <input
                                        className="w-full rounded-lg border border-slate-300 bg-white p-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                        placeholder="PNR / Voucher"
                                        value={serviceForm.confirmationNumber}
                                        onChange={e => setServiceForm({ ...serviceForm, confirmationNumber: e.target.value })}
                                    />
                                </div>
                            </div>

                            <div>
                                <label className="block text-sm font-medium mb-1 text-slate-700 dark:text-slate-300">Descripción corta</label>
                                <input
                                    className="w-full rounded-lg border border-slate-300 bg-white p-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                    placeholder={serviceType === 'Aereo' ? 'Vuelo AA900 MIA-EZE' : serviceType === 'Hotel' ? 'Hotel Riu Palace - Standard Room' : 'Traslado Privado'}
                                    value={serviceForm.description}
                                    onChange={e => setServiceForm({ ...serviceForm, description: e.target.value })}
                                    required
                                />
                            </div>

                            {/* Dates */}
                            <div className="grid grid-cols-2 gap-4">
                                <div>
                                    <label className="block text-sm font-medium mb-1 text-slate-700 dark:text-slate-300">Fecha {serviceType === 'Hotel' ? 'Check-In' : 'Salida'}</label>
                                    <input
                                        type="datetime-local"
                                        className="w-full rounded-lg border border-slate-300 bg-white p-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                        value={serviceForm.departureDate}
                                        onChange={e => setServiceForm({ ...serviceForm, departureDate: e.target.value })}
                                        required
                                    />
                                </div>
                                <div>
                                    <label className="block text-sm font-medium mb-1 text-slate-700 dark:text-slate-300">Fecha {serviceType === 'Hotel' ? 'Check-Out' : serviceType === 'Aereo' ? 'Regreso (Opc)' : 'Fin'}</label>
                                    <input
                                        type="datetime-local"
                                        className="w-full rounded-lg border border-slate-300 bg-white p-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                        value={serviceForm.returnDate}
                                        onChange={e => setServiceForm({ ...serviceForm, returnDate: e.target.value })}
                                    />
                                </div>
                            </div>

                            {/* Financials */}
                            <div className="rounded-xl bg-slate-50 p-4 border border-slate-200 dark:bg-slate-800/50 dark:border-slate-700">
                                <h4 className="text-xs font-bold text-slate-500 uppercase mb-3">Valores Económicos</h4>
                                <div className="grid grid-cols-2 gap-4">
                                    <div>
                                        <label className="block text-sm font-medium mb-1 text-slate-700 dark:text-slate-300">Costo Neto</label>
                                        <div className="relative">
                                            <span className="absolute left-3 top-2 text-slate-500">$</span>
                                            <input
                                                type="number" step="0.01"
                                                className="w-full rounded-lg border border-slate-300 bg-white pl-6 p-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                                value={serviceForm.netCost}
                                                onChange={e => setServiceForm({ ...serviceForm, netCost: e.target.value })}
                                                required
                                            />
                                        </div>
                                    </div>
                                    <div>
                                        <label className="block text-sm font-medium mb-1 text-slate-700 dark:text-slate-300">Precio Venta</label>
                                        <div className="relative">
                                            <span className="absolute left-3 top-2 text-slate-500">$</span>
                                            <input
                                                type="number" step="0.01"
                                                className="w-full rounded-lg border border-slate-300 bg-white pl-6 p-2 text-sm text-slate-900 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                                value={serviceForm.salePrice}
                                                onChange={e => setServiceForm({ ...serviceForm, salePrice: e.target.value })}
                                                required
                                            />
                                        </div>
                                    </div>
                                </div>
                            </div>

                            <div className="flex justify-end gap-2 pt-2">
                                <Button type="button" variant="ghost" onClick={() => setIsServiceModalOpen(false)}>Cancelar</Button>
                                <Button type="submit">Guardar Servicio</Button>
                            </div>
                        </form>
                    </div>
                </div>
            )}
        </div>
    );
}
