import { useState, useEffect, useCallback } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { X, Plane, Hotel, Bus, Package, Search, Calculator, DollarSign } from "lucide-react";

const SERVICE_TYPES = [
    { value: "Aereo", label: "Aéreo", icon: Plane },
    { value: "Hotel", label: "Hotel", icon: Hotel },
    { value: "Traslado", label: "Traslado", icon: Bus },
    { value: "Paquete", label: "Paquete", icon: Package }
];

// Clases CSS con dark mode
const inputClass = "w-full rounded-xl border border-slate-200 bg-slate-50 p-2.5 text-sm text-slate-900 focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-600 dark:bg-slate-700 dark:text-white dark:focus:border-indigo-400";
const labelClass = "block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1";

// Calcular noches entre dos fechas
const calculateNights = (checkIn, checkOut) => {
    if (!checkIn || !checkOut) return 0;
    const start = new Date(checkIn);
    const end = new Date(checkOut);
    const diff = Math.ceil((end - start) / (1000 * 60 * 60 * 24));
    return diff > 0 ? diff : 0;
};

// Componente de búsqueda de tarifas
function RateSelector({ serviceType, supplierId, onSelect }) {
    const [rates, setRates] = useState([]);
    const [search, setSearch] = useState("");
    const [loading, setLoading] = useState(false);
    const [showDropdown, setShowDropdown] = useState(false);

    const searchRates = useCallback(async () => {
        if (!serviceType) return;
        setLoading(true);
        try {
            const params = new URLSearchParams();
            params.append("serviceType", serviceType);
            if (supplierId) params.append("supplierId", supplierId);
            if (search) params.append("query", search);
            const data = await api.get(`/rates/search?${params}`);
            setRates(data || []);
        } catch {
            setRates([]);
        } finally {
            setLoading(false);
        }
    }, [serviceType, supplierId, search]);

    useEffect(() => {
        const timer = setTimeout(searchRates, 300);
        return () => clearTimeout(timer);
    }, [searchRates]);

    return (
        <div className="relative">
            <label className={labelClass}>
                <Search className="inline h-4 w-4 mr-1" />
                Buscar en Tarifario
            </label>
            <input
                type="text"
                className={inputClass}
                placeholder="Buscar tarifa..."
                value={search}
                onChange={e => setSearch(e.target.value)}
                onFocus={() => setShowDropdown(true)}
            />
            {showDropdown && rates.length > 0 && (
                <div className="absolute z-20 w-full mt-1 bg-white dark:bg-slate-800 border border-slate-200 dark:border-slate-600 rounded-xl shadow-lg max-h-60 overflow-y-auto">
                    {rates.map(rate => (
                        <button
                            key={rate.id}
                            type="button"
                            onClick={() => {
                                onSelect(rate);
                                setShowDropdown(false);
                                setSearch("");
                            }}
                            className="w-full text-left px-4 py-3 hover:bg-slate-50 dark:hover:bg-slate-700 border-b border-slate-100 dark:border-slate-700 last:border-0"
                        >
                            <div className="font-medium text-slate-900 dark:text-white">{rate.productName}</div>
                            <div className="text-xs text-slate-500 dark:text-slate-400 flex items-center gap-2">
                                <span>{rate.supplierName || "Sin proveedor"}</span>
                                <span className="text-emerald-600">Costo: ${rate.netCost}</span>
                                <span className="text-indigo-600">Venta: ${rate.salePrice}</span>
                            </div>
                        </button>
                    ))}
                </div>
            )}
            {loading && <div className="text-xs text-slate-500 mt-1">Buscando...</div>}
        </div>
    );
}

// Formulario para Vuelos
function FlightForm({ form, setForm, suppliers, onRateSelect }) {
    return (
        <div className="space-y-4">
            <RateSelector serviceType="Aereo" supplierId={form.supplierId} onSelect={onRateSelect} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>PNR/Localizador</label>
                    <input className={inputClass} value={form.pnr || ""} onChange={e => setForm({ ...form, pnr: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-4 gap-4">
                <div>
                    <label className={labelClass}>Aerolínea *</label>
                    <input className={inputClass} placeholder="AA" maxLength={3} value={form.airlineCode || ""} onChange={e => setForm({ ...form, airlineCode: e.target.value.toUpperCase() })} required />
                </div>
                <div>
                    <label className={labelClass}>Vuelo *</label>
                    <input className={inputClass} placeholder="900" value={form.flightNumber || ""} onChange={e => setForm({ ...form, flightNumber: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Origen *</label>
                    <input className={inputClass} placeholder="MIA" maxLength={3} value={form.origin || ""} onChange={e => setForm({ ...form, origin: e.target.value.toUpperCase() })} required />
                </div>
                <div>
                    <label className={labelClass}>Destino *</label>
                    <input className={inputClass} placeholder="EZE" maxLength={3} value={form.destination || ""} onChange={e => setForm({ ...form, destination: e.target.value.toUpperCase() })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Salida *</label>
                    <input type="datetime-local" className={inputClass} value={form.departureTime || ""} onChange={e => setForm({ ...form, departureTime: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Llegada *</label>
                    <input type="datetime-local" className={inputClass} value={form.arrivalTime || ""} onChange={e => setForm({ ...form, arrivalTime: e.target.value })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Clase</label>
                    <select className={inputClass} value={form.cabinClass || "Economy"} onChange={e => setForm({ ...form, cabinClass: e.target.value })}>
                        <option value="Economy">Economy</option>
                        <option value="Premium Economy">Premium Economy</option>
                        <option value="Business">Business</option>
                        <option value="First">First</option>
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Equipaje</label>
                    <input className={inputClass} placeholder="23kg" value={form.baggage || ""} onChange={e => setForm({ ...form, baggage: e.target.value })} />
                </div>
            </div>
        </div>
    );
}

// Formulario para Hoteles con cálculo automático de noches
function HotelForm({ form, setForm, suppliers, onRateSelect }) {
    const nights = calculateNights(form.checkIn, form.checkOut);

    // Recalcular precio total al cambiar noches o precio unitario
    useEffect(() => {
        if (form.unitPrice && nights > 0) {
            const totalCost = form.unitPrice * nights;
            const margin = form.commissionPercent ? totalCost * (form.commissionPercent / 100) : 0;
            setForm(prev => ({
                ...prev,
                netCost: totalCost,
                salePrice: totalCost + margin
            }));
        }
    }, [nights, form.unitPrice, form.commissionPercent]);

    return (
        <div className="space-y-4">
            <RateSelector serviceType="Hotel" supplierId={form.supplierId} onSelect={onRateSelect} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Código Confirmación</label>
                    <input className={inputClass} value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div className="col-span-2">
                    <label className={labelClass}>Hotel *</label>
                    <input className={inputClass} placeholder="Hotel Riu Palace" value={form.hotelName || ""} onChange={e => setForm({ ...form, hotelName: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Estrellas</label>
                    <select className={inputClass} value={form.starRating || ""} onChange={e => setForm({ ...form, starRating: e.target.value })}>
                        <option value="">-</option>
                        <option value="3">⭐⭐⭐</option>
                        <option value="4">⭐⭐⭐⭐</option>
                        <option value="5">⭐⭐⭐⭐⭐</option>
                    </select>
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Ciudad *</label>
                    <input className={inputClass} placeholder="Cancún" value={form.city || ""} onChange={e => setForm({ ...form, city: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>País</label>
                    <input className={inputClass} placeholder="México" value={form.country || ""} onChange={e => setForm({ ...form, country: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Check-In *</label>
                    <input type="date" className={inputClass} value={form.checkIn || ""} onChange={e => setForm({ ...form, checkIn: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Check-Out *</label>
                    <input type="date" className={inputClass} value={form.checkOut || ""} onChange={e => setForm({ ...form, checkOut: e.target.value })} required />
                </div>
                <div className="flex flex-col justify-end">
                    <div className="p-2.5 rounded-xl bg-indigo-50 dark:bg-indigo-900/30 text-center">
                        <span className="text-2xl font-bold text-indigo-600 dark:text-indigo-400">{nights}</span>
                        <span className="text-xs text-indigo-600 dark:text-indigo-400 block">noches</span>
                    </div>
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Tipo Habitación</label>
                    <select className={inputClass} value={form.roomType || "Doble"} onChange={e => setForm({ ...form, roomType: e.target.value })}>
                        <option value="Single">Single</option>
                        <option value="Doble">Doble</option>
                        <option value="Triple">Triple</option>
                        <option value="Suite">Suite</option>
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Régimen</label>
                    <select className={inputClass} value={form.mealPlan || "Desayuno"} onChange={e => setForm({ ...form, mealPlan: e.target.value })}>
                        <option value="Solo Alojamiento">Solo Alojamiento</option>
                        <option value="Desayuno">Desayuno</option>
                        <option value="Media Pensión">Media Pensión</option>
                        <option value="All Inclusive">All Inclusive</option>
                    </select>
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input type="number" min="1" className={inputClass} value={form.adults || 2} onChange={e => setForm({ ...form, adults: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className={labelClass}>Niños</label>
                    <input type="number" min="0" className={inputClass} value={form.children || 0} onChange={e => setForm({ ...form, children: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className={labelClass}>Habitaciones</label>
                    <input type="number" min="1" className={inputClass} value={form.rooms || 1} onChange={e => setForm({ ...form, rooms: parseInt(e.target.value) })} />
                </div>
            </div>
        </div>
    );
}

// Formulario para Traslados
function TransferForm({ form, setForm, suppliers, onRateSelect }) {
    return (
        <div className="space-y-4">
            <RateSelector serviceType="Traslado" supplierId={form.supplierId} onSelect={onRateSelect} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Código Confirmación</label>
                    <input className={inputClass} value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Punto Recogida *</label>
                    <input className={inputClass} placeholder="Aeropuerto Miami (MIA)" value={form.pickupLocation || ""} onChange={e => setForm({ ...form, pickupLocation: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Punto Destino *</label>
                    <input className={inputClass} placeholder="Hotel Riu Palace" value={form.dropoffLocation || ""} onChange={e => setForm({ ...form, dropoffLocation: e.target.value })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Fecha/Hora Recogida *</label>
                    <input type="datetime-local" className={inputClass} value={form.pickupDateTime || ""} onChange={e => setForm({ ...form, pickupDateTime: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Nro. Vuelo (opc)</label>
                    <input className={inputClass} placeholder="AA900" value={form.flightNumber || ""} onChange={e => setForm({ ...form, flightNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Tipo Vehículo</label>
                    <select className={inputClass} value={form.vehicleType || "Sedan"} onChange={e => setForm({ ...form, vehicleType: e.target.value })}>
                        <option value="Sedan">Sedán</option>
                        <option value="Van">Van</option>
                        <option value="Minibus">Minibus</option>
                        <option value="Bus">Bus</option>
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Pasajeros</label>
                    <input type="number" min="1" className={inputClass} value={form.passengers || 1} onChange={e => setForm({ ...form, passengers: parseInt(e.target.value) })} />
                </div>
                <div className="flex items-end pb-1">
                    <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
                        <input type="checkbox" checked={form.isRoundTrip || false} onChange={e => setForm({ ...form, isRoundTrip: e.target.checked })} className="rounded" />
                        Ida y Vuelta
                    </label>
                </div>
            </div>
            {form.isRoundTrip && (
                <div>
                    <label className={labelClass}>Fecha/Hora Regreso *</label>
                    <input type="datetime-local" className={inputClass} value={form.returnDateTime || ""} onChange={e => setForm({ ...form, returnDateTime: e.target.value })} />
                </div>
            )}
        </div>
    );
}

// Formulario para Paquetes
function PackageForm({ form, setForm, suppliers, onRateSelect }) {
    return (
        <div className="space-y-4">
            <RateSelector serviceType="Paquete" supplierId={form.supplierId} onSelect={onRateSelect} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Código Confirmación</label>
                    <input className={inputClass} value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Nombre Paquete *</label>
                    <input className={inputClass} placeholder="Cancún All Inclusive 7 noches" value={form.packageName || ""} onChange={e => setForm({ ...form, packageName: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Destino *</label>
                    <input className={inputClass} placeholder="Cancún, México" value={form.destination || ""} onChange={e => setForm({ ...form, destination: e.target.value })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Fecha Inicio *</label>
                    <input type="date" className={inputClass} value={form.startDate || ""} onChange={e => setForm({ ...form, startDate: e.target.value })} required />
                </div>
                <div>
                    <label className={labelClass}>Fecha Fin *</label>
                    <input type="date" className={inputClass} value={form.endDate || ""} onChange={e => setForm({ ...form, endDate: e.target.value })} required />
                </div>
            </div>
            <div>
                <label className={labelClass}>¿Qué incluye?</label>
                <div className="grid grid-cols-3 gap-2 mt-2">
                    {[
                        { key: "includesHotel", label: "Hotel" },
                        { key: "includesFlight", label: "Vuelo" },
                        { key: "includesTransfer", label: "Traslados" },
                        { key: "includesExcursions", label: "Excursiones" },
                        { key: "includesMeals", label: "Comidas" },
                    ].map(item => (
                        <label key={item.key} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300 cursor-pointer">
                            <input type="checkbox" checked={form[item.key] || false} onChange={e => setForm({ ...form, [item.key]: e.target.checked })} className="rounded" />
                            {item.label}
                        </label>
                    ))}
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input type="number" min="1" className={inputClass} value={form.adults || 2} onChange={e => setForm({ ...form, adults: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className={labelClass}>Niños</label>
                    <input type="number" min="0" className={inputClass} value={form.children || 0} onChange={e => setForm({ ...form, children: parseInt(e.target.value) })} />
                </div>
            </div>
            <div>
                <label className={labelClass}>Itinerario (opcional)</label>
                <textarea rows="3" className={inputClass} placeholder="Día 1: Llegada y check-in..." value={form.itinerary || ""} onChange={e => setForm({ ...form, itinerary: e.target.value })} />
            </div>
        </div>
    );
}

// Formulario de precios mejorado con cálculo automático
function PricingForm({ form, setForm, commissionPercent, onRecalculate, serviceType }) {
    const nights = serviceType === "Hotel" ? calculateNights(form.checkIn, form.checkOut) : 0;
    const showUnitPrice = serviceType === "Hotel" && nights > 0;

    return (
        <div className="rounded-xl bg-slate-50 dark:bg-slate-800 p-4 border border-slate-200 dark:border-slate-700 space-y-4">
            <div className="flex items-center justify-between">
                <h4 className="text-xs font-bold text-slate-500 dark:text-slate-400 uppercase flex items-center gap-2">
                    <DollarSign className="h-4 w-4" /> Valores Económicos
                </h4>
                <button type="button" onClick={onRecalculate}
                    className="text-xs px-2 py-1 rounded-lg bg-indigo-100 text-indigo-700 dark:bg-indigo-900/50 dark:text-indigo-300 hover:bg-indigo-200 flex items-center gap-1">
                    <Calculator className="h-3 w-3" /> Aplicar comisión {commissionPercent}%
                </button>
            </div>

            {showUnitPrice && (
                <div className="p-3 rounded-lg bg-amber-50 dark:bg-amber-900/20 border border-amber-100 dark:border-amber-800">
                    <label className="block text-xs font-medium text-amber-700 dark:text-amber-400 mb-1">
                        Precio por noche (se multiplicará × {nights} noches)
                    </label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01"
                            className={`${inputClass} pl-6`}
                            value={form.unitPrice || 0}
                            onChange={e => setForm({ ...form, unitPrice: parseFloat(e.target.value) || 0 })}
                        />
                    </div>
                </div>
            )}

            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Costo Neto *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.netCost || 0} onChange={e => setForm({ ...form, netCost: parseFloat(e.target.value) || 0 })} required />
                    </div>
                </div>
                <div>
                    <label className={labelClass}>Precio Venta *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.salePrice || 0} onChange={e => setForm({ ...form, salePrice: parseFloat(e.target.value) || 0 })} required />
                    </div>
                </div>
                <div>
                    <label className={labelClass}>Comisión</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01" className={`${inputClass} pl-6 bg-green-50 dark:bg-green-900/20`} value={(form.salePrice || 0) - (form.netCost || 0)} readOnly />
                    </div>
                </div>
            </div>
        </div>
    );
}

export default function ServiceFormModal({ isOpen, onClose, fileId, suppliers, onSuccess }) {
    const [serviceType, setServiceType] = useState("Aereo");
    const [form, setForm] = useState({ supplierId: "", netCost: 0, salePrice: 0, commission: 0 });
    const [loading, setLoading] = useState(false);
    const [commissionPercent, setCommissionPercent] = useState(10);

    // Obtener comisión aplicable al cambiar proveedor/tipo
    const fetchCommission = useCallback(async () => {
        try {
            const params = new URLSearchParams();
            if (form.supplierId) params.append("supplierId", form.supplierId);
            params.append("serviceType", serviceType);
            const result = await api.get(`/commissions/calculate?${params}`);
            setCommissionPercent(result.commissionPercent || 10);
        } catch {
            setCommissionPercent(10);
        }
    }, [form.supplierId, serviceType]);

    useEffect(() => {
        if (isOpen) {
            setForm({ supplierId: "", netCost: 0, salePrice: 0, commission: 0 });
            fetchCommission();
        }
    }, [isOpen, serviceType]);

    useEffect(() => {
        fetchCommission();
    }, [fetchCommission]);

    // Aplicar comisión al costo
    const applyCommission = () => {
        const cost = form.netCost || 0;
        const margin = cost * (commissionPercent / 100);
        setForm(prev => ({ ...prev, salePrice: Math.round((cost + margin) * 100) / 100 }));
    };

    // Al seleccionar una tarifa del buscador
    const handleRateSelect = (rate) => {
        setForm(prev => ({
            ...prev,
            supplierId: rate.supplierId?.toString() || prev.supplierId,
            netCost: rate.netCost,
            salePrice: rate.salePrice,
            unitPrice: rate.netCost, // Para hoteles
            // Datos adicionales según tipo
            ...(serviceType === "Aereo" && {
                origin: rate.origin,
                destination: rate.destination,
                cabinClass: rate.cabinClass,
                airlineCode: rate.airlineCode
            }),
            ...(serviceType === "Hotel" && {
                hotelName: rate.hotelName,
                city: rate.city,
                starRating: rate.starRating?.toString(),
                roomType: rate.roomType,
                mealPlan: rate.mealPlan
            }),
            ...(serviceType === "Traslado" && {
                pickupLocation: rate.pickupLocation,
                dropoffLocation: rate.dropoffLocation,
                vehicleType: rate.vehicleType
            }),
            ...(serviceType === "Paquete" && {
                packageName: rate.productName,
                destination: rate.destination
            })
        }));
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        setLoading(true);
        try {
            const endpoint = {
                "Aereo": `/files/${fileId}/flights`,
                "Hotel": `/files/${fileId}/hotels`,
                "Traslado": `/files/${fileId}/transfers`,
                "Paquete": `/files/${fileId}/packages`
            }[serviceType];

            const payload = { ...form, supplierId: parseInt(form.supplierId) };

            // Ajustar fechas según tipo
            if (serviceType === "Aereo") {
                payload.departureTime = new Date(form.departureTime).toISOString();
                payload.arrivalTime = new Date(form.arrivalTime).toISOString();
            } else if (serviceType === "Hotel") {
                payload.checkIn = new Date(form.checkIn).toISOString();
                payload.checkOut = new Date(form.checkOut).toISOString();
            } else if (serviceType === "Traslado") {
                payload.pickupDateTime = new Date(form.pickupDateTime).toISOString();
                if (form.isRoundTrip && form.returnDateTime) {
                    payload.returnDateTime = new Date(form.returnDateTime).toISOString();
                }
            } else if (serviceType === "Paquete") {
                payload.startDate = new Date(form.startDate).toISOString();
                payload.endDate = new Date(form.endDate).toISOString();
            }

            await api.post(endpoint, payload);
            showSuccess("Servicio agregado correctamente");
            onSuccess();
            onClose();
        } catch (error) {
            showError(error.message || "Error al guardar servicio");
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4 backdrop-blur-sm">
            <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-xl max-w-2xl w-full max-h-[90vh] overflow-hidden">
                <div className="flex items-center justify-between p-4 border-b border-slate-200 dark:border-slate-700">
                    <h2 className="text-lg font-semibold text-slate-900 dark:text-white">Agregar Servicio</h2>
                    <button onClick={onClose} className="p-2 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-lg text-slate-500 dark:text-slate-400">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                {/* Tabs de tipo */}
                <div className="flex border-b border-slate-200 dark:border-slate-700">
                    {SERVICE_TYPES.map(({ value, label, icon: Icon }) => (
                        <button
                            key={value}
                            type="button"
                            onClick={() => setServiceType(value)}
                            className={`flex-1 flex items-center justify-center gap-2 py-3 text-sm font-medium transition-colors ${serviceType === value
                                ? "border-b-2 border-indigo-600 text-indigo-600 dark:text-indigo-400"
                                : "text-slate-500 dark:text-slate-400 hover:text-slate-700 dark:hover:text-slate-300"
                                }`}
                        >
                            <Icon className="h-4 w-4" />
                            {label}
                        </button>
                    ))}
                </div>

                <form onSubmit={handleSubmit} className="p-4 space-y-4 overflow-y-auto max-h-[60vh]">
                    {serviceType === "Aereo" && <FlightForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}
                    {serviceType === "Hotel" && <HotelForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}
                    {serviceType === "Traslado" && <TransferForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}
                    {serviceType === "Paquete" && <PackageForm form={form} setForm={setForm} suppliers={suppliers} onRateSelect={handleRateSelect} />}

                    <PricingForm form={form} setForm={setForm} commissionPercent={commissionPercent} onRecalculate={applyCommission} serviceType={serviceType} />

                    <div className="flex justify-end gap-2 pt-2 border-t border-slate-200 dark:border-slate-700">
                        <button type="button" onClick={onClose} className="px-4 py-2 text-sm rounded-xl hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-600 dark:text-slate-300">Cancelar</button>
                        <button type="submit" disabled={loading} className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-xl hover:bg-indigo-500 disabled:opacity-50">
                            {loading ? "Guardando..." : "Guardar Servicio"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
