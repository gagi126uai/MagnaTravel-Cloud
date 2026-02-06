import { useEffect, useState, useCallback } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { Plus, Pencil, Trash2, Search, X, DollarSign, Calculator, Plane, Hotel, Car, Package, Star } from "lucide-react";
import Swal from "sweetalert2";

const serviceTypes = [
    { value: "Aereo", label: "Aéreo", icon: Plane },
    { value: "Hotel", label: "Hotel", icon: Hotel },
    { value: "Traslado", label: "Traslado", icon: Car },
    { value: "Paquete", label: "Paquete", icon: Package },
    { value: "Asistencia", label: "Asistencia" },
    { value: "Excursion", label: "Excursión" },
    { value: "Otro", label: "Otro" },
];

const priceUnits = [
    { value: "servicio", label: "Por servicio" },
    { value: "noche", label: "Por noche" },
    { value: "pasajero", label: "Por pasajero" },
    { value: "trayecto", label: "Por trayecto" },
];

const mealPlans = [
    { value: "RO", label: "Solo habitación (RO)" },
    { value: "BB", label: "Desayuno (BB)" },
    { value: "HB", label: "Media pensión (HB)" },
    { value: "FB", label: "Pensión completa (FB)" },
    { value: "AI", label: "All Inclusive (AI)" },
];

const cabinClasses = [
    { value: "Economy", label: "Economy" },
    { value: "Premium Economy", label: "Premium Economy" },
    { value: "Business", label: "Business" },
    { value: "First", label: "First Class" },
];

const Modal = ({ isOpen, onClose, title, children }) => {
    if (!isOpen) return null;
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm overflow-y-auto">
            <div className="w-full max-w-2xl overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900 my-8">
                <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-700">
                    <h3 className="text-lg font-semibold text-slate-900 dark:text-white">{title}</h3>
                    <button onClick={onClose} className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-white">
                        <X className="h-5 w-5" />
                    </button>
                </div>
                <div className="p-6 max-h-[70vh] overflow-y-auto">{children}</div>
            </div>
        </div>
    );
};

// Clase base para inputs
const inputClass = "mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-900 focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-600 dark:bg-slate-800 dark:text-white dark:focus:border-indigo-400 dark:focus:bg-slate-700";
const labelClass = "block text-sm font-medium text-slate-700 dark:text-slate-300";

export default function RatesPage() {
    const [rates, setRates] = useState([]);
    const [suppliers, setSuppliers] = useState([]);
    const [loading, setLoading] = useState(true);
    const [showModal, setShowModal] = useState(false);
    const [searchTerm, setSearchTerm] = useState("");
    const [filterType, setFilterType] = useState("");

    const emptyForm = {
        id: null, supplierId: "", serviceType: "Aereo", productName: "", description: "",
        priceUnit: "servicio", netCost: 0, tax: 0, salePrice: 0, currency: "USD",
        validFrom: "", validTo: "", internalNotes: "",
        // Aéreo
        airline: "", airlineCode: "", origin: "", destination: "", cabinClass: "", baggageIncluded: "",
        // Hotel
        hotelName: "", city: "", starRating: "", roomType: "", mealPlan: "",
        hotelPriceType: "base_doble", childrenPayPercent: 0, childMaxAge: 12,
        // Traslado
        pickupLocation: "", dropoffLocation: "", vehicleType: "", maxPassengers: "", isRoundTrip: false,
        // Paquete
        includesFlight: false, includesHotel: false, includesTransfer: false, includesExcursions: false, includesInsurance: false,
        durationDays: "", itinerary: ""
    };

    const [form, setForm] = useState(emptyForm);
    const [commissionPercent, setCommissionPercent] = useState(10);
    const [isCalculating, setIsCalculating] = useState(false);

    useEffect(() => {
        loadRates();
        loadSuppliers();
    }, []);

    const loadRates = async () => {
        setLoading(true);
        try {
            const data = await api.get("/rates");
            setRates(Array.isArray(data) ? data : []);
        } catch (error) {
            console.error("Error loading rates:", error);
            setRates([]);
        } finally {
            setLoading(false);
        }
    };

    const loadSuppliers = async () => {
        try {
            const data = await api.get("/suppliers");
            setSuppliers(data || []);
        } catch { }
    };

    const fetchCommission = useCallback(async (supplierId, serviceType) => {
        try {
            setIsCalculating(true);
            const params = new URLSearchParams();
            if (supplierId) params.append("supplierId", supplierId);
            if (serviceType) params.append("serviceType", serviceType);
            const result = await api.get(`/commissions/calculate?${params}`);
            setCommissionPercent(result.commissionPercent || 10);
            return result.commissionPercent || 10;
        } catch {
            return 10;
        } finally {
            setIsCalculating(false);
        }
    }, []);

    const calculateSalePrice = (netCost, tax, commission) => {
        const cost = parseFloat(netCost) || 0;
        const taxVal = parseFloat(tax) || 0;
        const commissionAmount = cost * (commission / 100);
        return Math.round((cost + taxVal + commissionAmount) * 100) / 100;
    };

    const handleSupplierChange = async (e) => {
        const supplierId = e.target.value;
        setForm(prev => ({ ...prev, supplierId }));
        const newCommission = await fetchCommission(supplierId, form.serviceType);
        if (form.netCost > 0) {
            setForm(prev => ({ ...prev, supplierId, salePrice: calculateSalePrice(prev.netCost, prev.tax, newCommission) }));
        }
    };

    const handleServiceTypeChange = async (e) => {
        const serviceType = e.target.value;
        setForm(prev => ({ ...prev, serviceType }));
        const newCommission = await fetchCommission(form.supplierId, serviceType);
        if (form.netCost > 0) {
            setForm(prev => ({ ...prev, serviceType, salePrice: calculateSalePrice(prev.netCost, prev.tax, newCommission) }));
        }
    };

    const handleNetCostChange = (e) => {
        const netCost = e.target.value;
        const suggestedSale = calculateSalePrice(netCost, form.tax, commissionPercent);
        setForm(prev => ({ ...prev, netCost, salePrice: suggestedSale }));
    };

    const handleTaxChange = (e) => {
        const tax = e.target.value;
        const suggestedSale = calculateSalePrice(form.netCost, tax, commissionPercent);
        setForm(prev => ({ ...prev, tax, salePrice: suggestedSale }));
    };

    const applyCommission = () => {
        setForm(prev => ({ ...prev, salePrice: calculateSalePrice(prev.netCost, prev.tax, commissionPercent) }));
    };

    const saveRate = async (e) => {
        e.preventDefault();
        try {
            const payload = {
                ...form,
                supplierId: form.supplierId ? parseInt(form.supplierId) : null,
                netCost: parseFloat(form.netCost) || 0,
                tax: parseFloat(form.tax) || 0,
                salePrice: parseFloat(form.salePrice) || 0,
                starRating: form.starRating ? parseInt(form.starRating) : null,
                maxPassengers: form.maxPassengers ? parseInt(form.maxPassengers) : null,
                durationDays: form.durationDays ? parseInt(form.durationDays) : null,
                validFrom: form.validFrom ? new Date(form.validFrom).toISOString() : null,
                validFrom: form.validFrom ? new Date(form.validFrom).toISOString() : null,
                validTo: form.validTo ? new Date(form.validTo).toISOString() : null,
                hotelPriceType: form.hotelPriceType,
                childrenPayPercent: parseInt(form.childrenPayPercent) || 0,
                childMaxAge: parseInt(form.childMaxAge) || 12
            };

            if (form.id) {
                await api.put(`/rates/${form.id}`, payload);
                showSuccess("Tarifa actualizada");
            } else {
                await api.post("/rates", payload);
                showSuccess("Tarifa creada");
            }

            setShowModal(false);
            setForm(emptyForm);
            loadRates();
        } catch (error) {
            showError(error.message || "Error al guardar tarifa");
        }
    };

    const deleteRate = async (rateId) => {
        const result = await Swal.fire({
            title: "¿Eliminar tarifa?",
            text: "Esta acción no se puede deshacer",
            icon: "warning",
            showCancelButton: true,
            confirmButtonColor: "#ef4444",
            confirmButtonText: "Sí, eliminar",
            cancelButtonText: "Cancelar"
        });
        if (result.isConfirmed) {
            try {
                await api.delete(`/rates/${rateId}`);
                showSuccess("Tarifa eliminada");
                loadRates();
            } catch {
                showError("Error al eliminar tarifa");
            }
        }
    };

    const editRate = (rate) => {
        setForm({
            id: rate.id,
            supplierId: rate.supplierId?.toString() || "",
            serviceType: rate.serviceType || "Aereo",
            productName: rate.productName || "",
            description: rate.description || "",
            priceUnit: rate.priceUnit || "servicio",
            netCost: rate.netCost || 0,
            tax: rate.tax || 0,
            salePrice: rate.salePrice || 0,
            currency: rate.currency || "USD",
            validFrom: rate.validFrom ? rate.validFrom.split("T")[0] : "",
            validTo: rate.validTo ? rate.validTo.split("T")[0] : "",
            internalNotes: rate.internalNotes || "",
            airline: rate.airline || "", airlineCode: rate.airlineCode || "",
            origin: rate.origin || "", destination: rate.destination || "",
            cabinClass: rate.cabinClass || "", baggageIncluded: rate.baggageIncluded || "",
            hotelName: rate.hotelName || "", city: rate.city || "",
            starRating: rate.starRating?.toString() || "", roomType: rate.roomType || "", mealPlan: rate.mealPlan || "",
            hotelPriceType: rate.hotelPriceType || "base_doble",
            childrenPayPercent: rate.childrenPayPercent ?? 0,
            childMaxAge: rate.childMaxAge ?? 12,
            pickupLocation: rate.pickupLocation || "", dropoffLocation: rate.dropoffLocation || "",
            vehicleType: rate.vehicleType || "", maxPassengers: rate.maxPassengers?.toString() || "", isRoundTrip: rate.isRoundTrip || false,
            includesFlight: rate.includesFlight || false, includesHotel: rate.includesHotel || false,
            includesTransfer: rate.includesTransfer || false, includesExcursions: rate.includesExcursions || false,
            includesInsurance: rate.includesInsurance || false,
            durationDays: rate.durationDays?.toString() || "", itinerary: rate.itinerary || ""
        });
        setShowModal(true);
    };

    const openNewModal = async () => {
        setForm(emptyForm);
        setShowModal(true);
        await fetchCommission("", "Aereo");
    };

    const filteredRates = rates.filter(rate => {
        const matchSearch = !searchTerm ||
            rate.productName?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            rate.supplierName?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            rate.hotelName?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            rate.airline?.toLowerCase().includes(searchTerm.toLowerCase());
        const matchType = !filterType || rate.serviceType === filterType;
        return matchSearch && matchType;
    });

    // Renderizar descripción resumida según tipo
    const getTypeDescription = (rate) => {
        switch (rate.serviceType) {
            case "Aereo":
                return `${rate.airline || ""} ${rate.origin ? `${rate.origin} → ${rate.destination}` : ""} ${rate.cabinClass || ""}`.trim() || rate.description;
            case "Hotel":
                return `${rate.hotelName || ""} ${rate.city ? `- ${rate.city}` : ""} ${rate.starRating ? `★${rate.starRating}` : ""} ${rate.mealPlan || ""}`.trim() || rate.description;
            case "Traslado":
                return `${rate.pickupLocation || ""} → ${rate.dropoffLocation || ""} ${rate.vehicleType || ""} ${rate.isRoundTrip ? "(I/V)" : ""}`.trim() || rate.description;
            case "Paquete":
                return `${rate.durationDays ? `${rate.durationDays} días` : ""} ${rate.destination || ""}`.trim() || rate.description;
            default:
                return rate.description;
        }
    };

    return (
        <div className="space-y-6">
            {/* Header */}
            <header className="flex items-center justify-between">
                <div>
                    <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white flex items-center gap-3">
                        <div className="rounded-xl bg-gradient-to-br from-emerald-500 to-teal-600 p-2.5 text-white shadow-lg shadow-emerald-500/20">
                            <DollarSign className="h-6 w-6" />
                        </div>
                        Tarifario Profesional
                    </h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
                        Gestione las tarifas y precios de sus proveedores
                    </p>
                </div>
                <button onClick={openNewModal}
                    className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white shadow-sm shadow-indigo-500/20 hover:bg-indigo-500 transition-colors">
                    <Plus className="h-4 w-4" />
                    Nueva Tarifa
                </button>
            </header>

            {/* Filters */}
            <div className="flex flex-col sm:flex-row gap-4">
                <div className="relative flex-1 max-w-md">
                    <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input type="text" placeholder="Buscar..." className={`${inputClass} pl-10`}
                        value={searchTerm} onChange={(e) => setSearchTerm(e.target.value)} />
                </div>
                <select className={inputClass} style={{ width: 'auto' }} value={filterType} onChange={(e) => setFilterType(e.target.value)}>
                    <option value="">Todos los tipos</option>
                    {serviceTypes.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
                </select>
            </div>

            {/* Stats */}
            <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
                <div className="rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-800">
                    <div className="text-xs text-slate-500 dark:text-slate-400 uppercase font-bold">Total</div>
                    <div className="text-2xl font-bold mt-1 text-slate-900 dark:text-white">{rates.length}</div>
                </div>
                {["Aereo", "Hotel", "Traslado", "Paquete"].map(type => (
                    <div key={type} className="rounded-xl border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-800">
                        <div className="text-xs text-slate-500 dark:text-slate-400 uppercase font-bold">{type}</div>
                        <div className={`text-2xl font-bold mt-1 ${type === "Aereo" ? "text-sky-600" : type === "Hotel" ? "text-amber-600" : type === "Traslado" ? "text-green-600" : "text-violet-600"}`}>
                            {rates.filter(r => r.serviceType === type).length}
                        </div>
                    </div>
                ))}
            </div>

            {/* Table */}
            <div className="rounded-2xl border border-slate-200 bg-white overflow-hidden dark:border-slate-700 dark:bg-slate-800">
                <table className="w-full text-sm">
                    <thead className="bg-slate-50 dark:bg-slate-700">
                        <tr>
                            <th className="px-4 py-3 text-left font-medium text-slate-600 dark:text-slate-300">Producto</th>
                            <th className="px-4 py-3 text-left font-medium text-slate-600 dark:text-slate-300">Tipo</th>
                            <th className="px-4 py-3 text-left font-medium text-slate-600 dark:text-slate-300">Proveedor</th>
                            <th className="px-4 py-3 text-right font-medium text-slate-600 dark:text-slate-300">Costo</th>
                            <th className="px-4 py-3 text-right font-medium text-slate-600 dark:text-slate-300">Venta</th>
                            <th className="px-4 py-3 text-right font-medium text-slate-600 dark:text-slate-300">Margen</th>
                            <th className="px-4 py-3 text-center font-medium text-slate-600 dark:text-slate-300">Acciones</th>
                        </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-slate-700">
                        {loading ? (
                            <tr><td colSpan="7" className="px-4 py-8 text-center text-slate-500 dark:text-slate-400">Cargando...</td></tr>
                        ) : filteredRates.length === 0 ? (
                            <tr><td colSpan="7" className="px-4 py-8 text-center text-slate-500 dark:text-slate-400">
                                {searchTerm || filterType ? "No se encontraron tarifas" : "No hay tarifas. Cree una nueva para comenzar."}
                            </td></tr>
                        ) : filteredRates.map(rate => (
                            <tr key={rate.id} className="hover:bg-slate-50 dark:hover:bg-slate-700/50">
                                <td className="px-4 py-3">
                                    <div className="font-medium text-slate-900 dark:text-white">{rate.productName}</div>
                                    <div className="text-xs text-slate-500 dark:text-slate-400">{getTypeDescription(rate)}</div>
                                </td>
                                <td className="px-4 py-3">
                                    <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${rate.serviceType === "Aereo" ? "bg-sky-100 text-sky-700 dark:bg-sky-900/30 dark:text-sky-400" :
                                        rate.serviceType === "Hotel" ? "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400" :
                                            rate.serviceType === "Traslado" ? "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400" :
                                                rate.serviceType === "Paquete" ? "bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-400" :
                                                    "bg-slate-100 text-slate-700 dark:bg-slate-600 dark:text-slate-300"
                                        }`}>
                                        {rate.serviceType}
                                    </span>
                                </td>
                                <td className="px-4 py-3 text-slate-700 dark:text-slate-300">{rate.supplierName || <span className="text-slate-400">-</span>}</td>
                                <td className="px-4 py-3 text-right font-mono text-slate-700 dark:text-slate-300">
                                    ${rate.netCost?.toLocaleString()}
                                    {rate.tax > 0 && <span className="text-xs text-slate-400 block">+${rate.tax} tax</span>}
                                </td>
                                <td className="px-4 py-3 text-right font-mono font-medium text-emerald-600 dark:text-emerald-400">${rate.salePrice?.toLocaleString()}</td>
                                <td className="px-4 py-3 text-right">
                                    <span className={`font-medium ${(rate.salePrice - rate.netCost - (rate.tax || 0)) >= 0 ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}>
                                        ${(rate.salePrice - rate.netCost - (rate.tax || 0)).toLocaleString()}
                                    </span>
                                </td>
                                <td className="px-4 py-3 text-center">
                                    <button onClick={() => editRate(rate)} className="p-1.5 text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 rounded-lg transition-colors"><Pencil className="h-4 w-4" /></button>
                                    <button onClick={() => deleteRate(rate.id)} className="p-1.5 text-slate-500 hover:text-rose-600 hover:bg-rose-50 dark:hover:bg-rose-900/30 rounded-lg transition-colors"><Trash2 className="h-4 w-4" /></button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>

            {/* Modal */}
            <Modal isOpen={showModal} onClose={() => setShowModal(false)} title={form.id ? "Editar Tarifa" : "Nueva Tarifa"}>
                <form onSubmit={saveRate} className="space-y-5">
                    {/* Tipo de Servicio - Tabs visuales */}
                    <div>
                        <label className={labelClass}>Tipo de Servicio *</label>
                        <div className="grid grid-cols-4 gap-2 mt-2">
                            {serviceTypes.slice(0, 4).map(type => (
                                <button key={type.value} type="button"
                                    onClick={() => handleServiceTypeChange({ target: { value: type.value } })}
                                    className={`flex flex-col items-center gap-1 p-3 rounded-xl border-2 transition-all
                                        ${form.serviceType === type.value
                                            ? "border-indigo-500 bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-400"
                                            : "border-slate-200 hover:border-slate-300 dark:border-slate-600 dark:hover:border-slate-500 text-slate-600 dark:text-slate-400"
                                        }`}>
                                    {type.icon && <type.icon className="h-5 w-5" />}
                                    <span className="text-xs font-medium">{type.label}</span>
                                </button>
                            ))}
                        </div>
                    </div>

                    {/* Información básica */}
                    <div className="grid grid-cols-2 gap-4">
                        <div className="col-span-2">
                            <label className={labelClass}>Nombre del Producto *</label>
                            <input type="text" required className={inputClass} value={form.productName}
                                onChange={e => setForm({ ...form, productName: e.target.value })}
                                placeholder={form.serviceType === "Aereo" ? "Vuelo Buenos Aires - Miami" : form.serviceType === "Hotel" ? "Estadía Hotel Marriott" : "Nombre del servicio"} />
                        </div>
                        <div>
                            <label className={labelClass}>Proveedor</label>
                            <select className={inputClass} value={form.supplierId} onChange={handleSupplierChange}>
                                <option value="">Seleccionar proveedor</option>
                                {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                            </select>
                        </div>
                        <div>
                            <label className={labelClass}>Unidad de Precio</label>
                            <select className={inputClass} value={form.priceUnit} onChange={e => setForm({ ...form, priceUnit: e.target.value })}>
                                {priceUnits.map(u => <option key={u.value} value={u.value}>{u.label}</option>)}
                            </select>
                        </div>
                    </div>

                    {/* Campos dinámicos según tipo */}
                    {form.serviceType === "Aereo" && (
                        <div className="p-4 rounded-xl bg-sky-50 dark:bg-sky-900/20 border border-sky-100 dark:border-sky-800 space-y-4">
                            <div className="flex items-center gap-2 text-sky-700 dark:text-sky-400 font-medium text-sm">
                                <Plane className="h-4 w-4" /> Datos del Vuelo
                            </div>
                            <div className="grid grid-cols-2 gap-4">
                                <div>
                                    <label className={labelClass}>Aerolínea</label>
                                    <input type="text" className={inputClass} value={form.airline}
                                        onChange={e => setForm({ ...form, airline: e.target.value })} placeholder="Aerolíneas Argentinas" />
                                </div>
                                <div>
                                    <label className={labelClass}>Código IATA</label>
                                    <input type="text" className={inputClass} value={form.airlineCode} maxLength={3}
                                        onChange={e => setForm({ ...form, airlineCode: e.target.value.toUpperCase() })} placeholder="AR" />
                                </div>
                                <div>
                                    <label className={labelClass}>Origen</label>
                                    <input type="text" className={inputClass} value={form.origin}
                                        onChange={e => setForm({ ...form, origin: e.target.value })} placeholder="Buenos Aires (EZE)" />
                                </div>
                                <div>
                                    <label className={labelClass}>Destino</label>
                                    <input type="text" className={inputClass} value={form.destination}
                                        onChange={e => setForm({ ...form, destination: e.target.value })} placeholder="Miami (MIA)" />
                                </div>
                                <div>
                                    <label className={labelClass}>Clase</label>
                                    <select className={inputClass} value={form.cabinClass} onChange={e => setForm({ ...form, cabinClass: e.target.value })}>
                                        <option value="">Seleccionar</option>
                                        {cabinClasses.map(c => <option key={c.value} value={c.value}>{c.label}</option>)}
                                    </select>
                                </div>
                                <div>
                                    <label className={labelClass}>Equipaje Incluido</label>
                                    <input type="text" className={inputClass} value={form.baggageIncluded}
                                        onChange={e => setForm({ ...form, baggageIncluded: e.target.value })} placeholder="23kg + carry-on" />
                                </div>
                            </div>
                        </div>
                    )}

                    {form.serviceType === "Hotel" && (
                        <div className="p-4 rounded-xl bg-amber-50 dark:bg-amber-900/20 border border-amber-100 dark:border-amber-800 space-y-4">
                            <div className="flex items-center gap-2 text-amber-700 dark:text-amber-400 font-medium text-sm">
                                <Hotel className="h-4 w-4" /> Datos del Hotel
                            </div>
                            <div className="grid grid-cols-2 gap-4">
                                <div className="col-span-2">
                                    <label className={labelClass}>Nombre del Hotel</label>
                                    <input type="text" className={inputClass} value={form.hotelName}
                                        onChange={e => setForm({ ...form, hotelName: e.target.value })} placeholder="Marriott Resort & Spa" />
                                </div>
                                <div>
                                    <label className={labelClass}>Ciudad</label>
                                    <input type="text" className={inputClass} value={form.city}
                                        onChange={e => setForm({ ...form, city: e.target.value })} placeholder="Cancún" />
                                </div>
                                <div>
                                    <label className={labelClass}>Categoría</label>
                                    <select className={inputClass} value={form.starRating} onChange={e => setForm({ ...form, starRating: e.target.value })}>
                                        <option value="">Seleccionar</option>
                                        {[5, 4, 3, 2, 1].map(n => <option key={n} value={n}>{"★".repeat(n)} {n} estrellas</option>)}
                                    </select>
                                </div>
                                <div>
                                    <label className={labelClass}>Tipo Habitación</label>
                                    <input type="text" className={inputClass} value={form.roomType}
                                        onChange={e => setForm({ ...form, roomType: e.target.value })} placeholder="Doble Superior" />
                                </div>
                                <div>
                                    <label className={labelClass}>Régimen</label>
                                    <select className={inputClass} value={form.mealPlan} onChange={e => setForm({ ...form, mealPlan: e.target.value })}>
                                        <option value="">Seleccionar</option>
                                        {mealPlans.map(m => <option key={m.value} value={m.value}>{m.label}</option>)}
                                    </select>
                                </div>
                                <div>
                                    <label className={labelClass}>Tipo de Precio</label>
                                    <select className={inputClass} value={form.hotelPriceType || "base_doble"} onChange={e => setForm({ ...form, hotelPriceType: e.target.value })}>
                                        <option value="base_doble">Por Habitación (Base Doble)</option>
                                        <option value="por_persona">Por Persona</option>
                                    </select>
                                </div>
                                <div>
                                    <label className={labelClass}>% Pago Niños</label>
                                    <div className="relative">
                                        <input type="number" min="0" max="100" className={inputClass} value={form.childrenPayPercent}
                                            onChange={e => setForm({ ...form, childrenPayPercent: e.target.value })} placeholder="0" />
                                        <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-3">
                                            <span className="text-slate-500 sm:text-sm">%</span>
                                        </div>
                                    </div>
                                </div>
                                <div>
                                    <label className={labelClass}>Edad Máx Niño</label>
                                    <input type="number" min="0" className={inputClass} value={form.childMaxAge}
                                        onChange={e => setForm({ ...form, childMaxAge: e.target.value })} placeholder="12" />
                                </div>
                            </div>
                        </div>
                    )}

                    {form.serviceType === "Traslado" && (
                        <div className="p-4 rounded-xl bg-green-50 dark:bg-green-900/20 border border-green-100 dark:border-green-800 space-y-4">
                            <div className="flex items-center gap-2 text-green-700 dark:text-green-400 font-medium text-sm">
                                <Car className="h-4 w-4" /> Datos del Traslado
                            </div>
                            <div className="grid grid-cols-2 gap-4">
                                <div>
                                    <label className={labelClass}>Punto de Recogida</label>
                                    <input type="text" className={inputClass} value={form.pickupLocation}
                                        onChange={e => setForm({ ...form, pickupLocation: e.target.value })} placeholder="Aeropuerto EZE" />
                                </div>
                                <div>
                                    <label className={labelClass}>Punto de Destino</label>
                                    <input type="text" className={inputClass} value={form.dropoffLocation}
                                        onChange={e => setForm({ ...form, dropoffLocation: e.target.value })} placeholder="Hotel Centro" />
                                </div>
                                <div>
                                    <label className={labelClass}>Tipo de Vehículo</label>
                                    <input type="text" className={inputClass} value={form.vehicleType}
                                        onChange={e => setForm({ ...form, vehicleType: e.target.value })} placeholder="Van, Sedan, Bus" />
                                </div>
                                <div>
                                    <label className={labelClass}>Máx. Pasajeros</label>
                                    <input type="number" className={inputClass} value={form.maxPassengers}
                                        onChange={e => setForm({ ...form, maxPassengers: e.target.value })} placeholder="4" />
                                </div>
                                <div className="col-span-2">
                                    <label className="flex items-center gap-2 cursor-pointer">
                                        <input type="checkbox" checked={form.isRoundTrip}
                                            onChange={e => setForm({ ...form, isRoundTrip: e.target.checked })}
                                            className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500" />
                                        <span className={labelClass}>Incluye ida y vuelta</span>
                                    </label>
                                </div>
                            </div>
                        </div>
                    )}

                    {form.serviceType === "Paquete" && (
                        <div className="p-4 rounded-xl bg-violet-50 dark:bg-violet-900/20 border border-violet-100 dark:border-violet-800 space-y-4">
                            <div className="flex items-center gap-2 text-violet-700 dark:text-violet-400 font-medium text-sm">
                                <Package className="h-4 w-4" /> Datos del Paquete
                            </div>
                            <div className="grid grid-cols-2 gap-4">
                                <div>
                                    <label className={labelClass}>Destino</label>
                                    <input type="text" className={inputClass} value={form.destination}
                                        onChange={e => setForm({ ...form, destination: e.target.value })} placeholder="Punta Cana" />
                                </div>
                                <div>
                                    <label className={labelClass}>Duración (días)</label>
                                    <input type="number" className={inputClass} value={form.durationDays}
                                        onChange={e => setForm({ ...form, durationDays: e.target.value })} placeholder="7" />
                                </div>
                                <div className="col-span-2 grid grid-cols-3 gap-2">
                                    {[
                                        { key: "includesFlight", label: "Vuelo" },
                                        { key: "includesHotel", label: "Hotel" },
                                        { key: "includesTransfer", label: "Traslados" },
                                        { key: "includesExcursions", label: "Excursiones" },
                                        { key: "includesInsurance", label: "Seguro" },
                                    ].map(item => (
                                        <label key={item.key} className="flex items-center gap-2 cursor-pointer">
                                            <input type="checkbox" checked={form[item.key]}
                                                onChange={e => setForm({ ...form, [item.key]: e.target.checked })}
                                                className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500" />
                                            <span className="text-sm text-slate-700 dark:text-slate-300">{item.label}</span>
                                        </label>
                                    ))}
                                </div>
                                <div className="col-span-2">
                                    <label className={labelClass}>Itinerario</label>
                                    <textarea className={inputClass} rows={3} value={form.itinerary}
                                        onChange={e => setForm({ ...form, itinerary: e.target.value })}
                                        placeholder="Día 1: Llegada y traslado al hotel. Día 2: Excursión a..." />
                                </div>
                            </div>
                        </div>
                    )}

                    {/* Descripción */}
                    <div>
                        <label className={labelClass}>Descripción Detallada</label>
                        <textarea className={inputClass} rows={2} value={form.description}
                            onChange={e => setForm({ ...form, description: e.target.value })}
                            placeholder="Incluye información adicional, condiciones, restricciones..." />
                    </div>

                    {/* Comisión Badge */}
                    <div className="flex items-center gap-2 p-3 rounded-xl bg-indigo-50 dark:bg-indigo-900/20 border border-indigo-100 dark:border-indigo-800">
                        <Calculator className="h-4 w-4 text-indigo-600 dark:text-indigo-400" />
                        <span className="text-sm text-indigo-700 dark:text-indigo-300">
                            Comisión aplicable: <strong>{commissionPercent}%</strong>
                            {isCalculating && <span className="ml-2 text-xs opacity-70">(calculando...)</span>}
                        </span>
                        <button type="button" onClick={applyCommission} className="ml-auto text-xs px-2 py-1 rounded-lg bg-indigo-600 text-white hover:bg-indigo-500">
                            Recalcular
                        </button>
                    </div>

                    {/* Precios */}
                    <div className="grid grid-cols-4 gap-4">
                        <div>
                            <label className={labelClass}>Costo Neto *</label>
                            <div className="relative mt-1">
                                <span className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400">$</span>
                                <input type="number" step="0.01" required className={`${inputClass} pl-7`}
                                    value={form.netCost} onChange={handleNetCostChange} />
                            </div>
                        </div>
                        <div>
                            <label className={labelClass}>Impuestos</label>
                            <div className="relative mt-1">
                                <span className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400">$</span>
                                <input type="number" step="0.01" className={`${inputClass} pl-7`}
                                    value={form.tax} onChange={handleTaxChange} />
                            </div>
                        </div>
                        <div>
                            <label className={labelClass}>Precio Venta *</label>
                            <div className="relative mt-1">
                                <span className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400">$</span>
                                <input type="number" step="0.01" required className={`${inputClass} pl-7`}
                                    value={form.salePrice} onChange={e => setForm({ ...form, salePrice: e.target.value })} />
                            </div>
                        </div>
                        <div>
                            <label className={labelClass}>Moneda</label>
                            <select className={inputClass} value={form.currency} onChange={e => setForm({ ...form, currency: e.target.value })}>
                                <option value="USD">USD</option>
                                <option value="ARS">ARS</option>
                                <option value="EUR">EUR</option>
                            </select>
                        </div>
                    </div>

                    {/* Vigencia */}
                    <div className="grid grid-cols-2 gap-4">
                        <div>
                            <label className={labelClass}>Vigencia Desde</label>
                            <input type="date" className={inputClass} value={form.validFrom}
                                onChange={e => setForm({ ...form, validFrom: e.target.value })} />
                        </div>
                        <div>
                            <label className={labelClass}>Vigencia Hasta</label>
                            <input type="date" className={inputClass} value={form.validTo}
                                onChange={e => setForm({ ...form, validTo: e.target.value })} />
                        </div>
                    </div>

                    {/* Notas internas */}
                    <div>
                        <label className={labelClass}>Notas Internas (no visible para clientes)</label>
                        <input type="text" className={inputClass} value={form.internalNotes}
                            onChange={e => setForm({ ...form, internalNotes: e.target.value })}
                            placeholder="Comisión especial, contacto, etc." />
                    </div>

                    {/* Botones */}
                    <div className="flex justify-end gap-3 pt-4 border-t border-slate-200 dark:border-slate-700">
                        <button type="button" onClick={() => setShowModal(false)}
                            className="rounded-xl px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-700">
                            Cancelar
                        </button>
                        <button type="submit"
                            className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500">
                            {form.id ? "Guardar Cambios" : "Crear Tarifa"}
                        </button>
                    </div>
                </form>
            </Modal>
        </div>
    );
}
