import { useState, useEffect } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { X, Plane, Hotel, Bus, Package } from "lucide-react";

const SERVICE_TYPES = [
    { value: "Aereo", label: "Aéreo", icon: Plane },
    { value: "Hotel", label: "Hotel", icon: Hotel },
    { value: "Traslado", label: "Traslado", icon: Bus },
    { value: "Paquete", label: "Paquete", icon: Package }
];

// Formulario para Vuelos
function FlightForm({ form, setForm, suppliers }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Proveedor *</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">PNR/Localizador</label>
                    <input className="w-full rounded-lg border p-2 text-sm" value={form.pnr || ""} onChange={e => setForm({ ...form, pnr: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-4 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Aerolínea *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="AA" maxLength={3} value={form.airlineCode || ""} onChange={e => setForm({ ...form, airlineCode: e.target.value.toUpperCase() })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Vuelo *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="900" value={form.flightNumber || ""} onChange={e => setForm({ ...form, flightNumber: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Origen *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="MIA" maxLength={3} value={form.origin || ""} onChange={e => setForm({ ...form, origin: e.target.value.toUpperCase() })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Destino *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="EZE" maxLength={3} value={form.destination || ""} onChange={e => setForm({ ...form, destination: e.target.value.toUpperCase() })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Salida *</label>
                    <input type="datetime-local" className="w-full rounded-lg border p-2 text-sm" value={form.departureTime || ""} onChange={e => setForm({ ...form, departureTime: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Llegada *</label>
                    <input type="datetime-local" className="w-full rounded-lg border p-2 text-sm" value={form.arrivalTime || ""} onChange={e => setForm({ ...form, arrivalTime: e.target.value })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Clase</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.cabinClass || "Economy"} onChange={e => setForm({ ...form, cabinClass: e.target.value })}>
                        <option value="Economy">Economy</option>
                        <option value="Premium Economy">Premium Economy</option>
                        <option value="Business">Business</option>
                        <option value="First">First</option>
                    </select>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Equipaje</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="23kg" value={form.baggage || ""} onChange={e => setForm({ ...form, baggage: e.target.value })} />
                </div>
            </div>
        </div>
    );
}

// Formulario para Hoteles
function HotelForm({ form, setForm, suppliers }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Proveedor *</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Código Confirmación</label>
                    <input className="w-full rounded-lg border p-2 text-sm" value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div className="col-span-2">
                    <label className="block text-sm font-medium mb-1">Hotel *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="Hotel Riu Palace" value={form.hotelName || ""} onChange={e => setForm({ ...form, hotelName: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Estrellas</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.starRating || ""} onChange={e => setForm({ ...form, starRating: e.target.value })}>
                        <option value="">-</option>
                        <option value="3">⭐⭐⭐</option>
                        <option value="4">⭐⭐⭐⭐</option>
                        <option value="5">⭐⭐⭐⭐⭐</option>
                    </select>
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Ciudad *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="Cancún" value={form.city || ""} onChange={e => setForm({ ...form, city: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">País</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="México" value={form.country || ""} onChange={e => setForm({ ...form, country: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Check-In *</label>
                    <input type="date" className="w-full rounded-lg border p-2 text-sm" value={form.checkIn || ""} onChange={e => setForm({ ...form, checkIn: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Check-Out *</label>
                    <input type="date" className="w-full rounded-lg border p-2 text-sm" value={form.checkOut || ""} onChange={e => setForm({ ...form, checkOut: e.target.value })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Tipo Habitación</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.roomType || "Doble"} onChange={e => setForm({ ...form, roomType: e.target.value })}>
                        <option value="Single">Single</option>
                        <option value="Doble">Doble</option>
                        <option value="Triple">Triple</option>
                        <option value="Suite">Suite</option>
                    </select>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Régimen</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.mealPlan || "Desayuno"} onChange={e => setForm({ ...form, mealPlan: e.target.value })}>
                        <option value="Solo Alojamiento">Solo Alojamiento</option>
                        <option value="Desayuno">Desayuno</option>
                        <option value="Media Pensión">Media Pensión</option>
                        <option value="All Inclusive">All Inclusive</option>
                    </select>
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Adultos</label>
                    <input type="number" min="1" className="w-full rounded-lg border p-2 text-sm" value={form.adults || 2} onChange={e => setForm({ ...form, adults: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Niños</label>
                    <input type="number" min="0" className="w-full rounded-lg border p-2 text-sm" value={form.children || 0} onChange={e => setForm({ ...form, children: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Habitaciones</label>
                    <input type="number" min="1" className="w-full rounded-lg border p-2 text-sm" value={form.rooms || 1} onChange={e => setForm({ ...form, rooms: parseInt(e.target.value) })} />
                </div>
            </div>
        </div>
    );
}

// Formulario para Traslados
function TransferForm({ form, setForm, suppliers }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Proveedor *</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Código Confirmación</label>
                    <input className="w-full rounded-lg border p-2 text-sm" value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Punto Recogida *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="Aeropuerto Miami (MIA)" value={form.pickupLocation || ""} onChange={e => setForm({ ...form, pickupLocation: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Punto Destino *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="Hotel Riu Palace" value={form.dropoffLocation || ""} onChange={e => setForm({ ...form, dropoffLocation: e.target.value })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Fecha/Hora Recogida *</label>
                    <input type="datetime-local" className="w-full rounded-lg border p-2 text-sm" value={form.pickupDateTime || ""} onChange={e => setForm({ ...form, pickupDateTime: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Nro. Vuelo (opc)</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="AA900" value={form.flightNumber || ""} onChange={e => setForm({ ...form, flightNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Tipo Vehículo</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.vehicleType || "Sedan"} onChange={e => setForm({ ...form, vehicleType: e.target.value })}>
                        <option value="Sedan">Sedán</option>
                        <option value="Van">Van</option>
                        <option value="Minibus">Minibus</option>
                        <option value="Bus">Bus</option>
                    </select>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Pasajeros</label>
                    <input type="number" min="1" className="w-full rounded-lg border p-2 text-sm" value={form.passengers || 1} onChange={e => setForm({ ...form, passengers: parseInt(e.target.value) })} />
                </div>
                <div className="flex items-end pb-1">
                    <label className="flex items-center gap-2 text-sm">
                        <input type="checkbox" checked={form.isRoundTrip || false} onChange={e => setForm({ ...form, isRoundTrip: e.target.checked })} />
                        Ida y Vuelta
                    </label>
                </div>
            </div>
            {form.isRoundTrip && (
                <div>
                    <label className="block text-sm font-medium mb-1">Fecha/Hora Regreso *</label>
                    <input type="datetime-local" className="w-full rounded-lg border p-2 text-sm" value={form.returnDateTime || ""} onChange={e => setForm({ ...form, returnDateTime: e.target.value })} />
                </div>
            )}
        </div>
    );
}

// Formulario para Paquetes
function PackageForm({ form, setForm, suppliers }) {
    return (
        <div className="space-y-4">
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Proveedor *</label>
                    <select className="w-full rounded-lg border p-2 text-sm" value={form.supplierId} onChange={e => setForm({ ...form, supplierId: e.target.value })} required>
                        <option value="">Seleccionar...</option>
                        {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                    </select>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Código Confirmación</label>
                    <input className="w-full rounded-lg border p-2 text-sm" value={form.confirmationNumber || ""} onChange={e => setForm({ ...form, confirmationNumber: e.target.value })} />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Nombre Paquete *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="Cancún All Inclusive 7 noches" value={form.packageName || ""} onChange={e => setForm({ ...form, packageName: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Destino *</label>
                    <input className="w-full rounded-lg border p-2 text-sm" placeholder="Cancún, México" value={form.destination || ""} onChange={e => setForm({ ...form, destination: e.target.value })} required />
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Fecha Inicio *</label>
                    <input type="date" className="w-full rounded-lg border p-2 text-sm" value={form.startDate || ""} onChange={e => setForm({ ...form, startDate: e.target.value })} required />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Fecha Fin *</label>
                    <input type="date" className="w-full rounded-lg border p-2 text-sm" value={form.endDate || ""} onChange={e => setForm({ ...form, endDate: e.target.value })} required />
                </div>
            </div>
            <div>
                <label className="block text-sm font-medium mb-2">¿Qué incluye?</label>
                <div className="grid grid-cols-3 gap-2">
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.includesHotel !== false} onChange={e => setForm({ ...form, includesHotel: e.target.checked })} /> Hotel</label>
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.includesFlight !== false} onChange={e => setForm({ ...form, includesFlight: e.target.checked })} /> Vuelo</label>
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.includesTransfer || false} onChange={e => setForm({ ...form, includesTransfer: e.target.checked })} /> Traslados</label>
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.includesExcursions || false} onChange={e => setForm({ ...form, includesExcursions: e.target.checked })} /> Excursiones</label>
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.includesMeals || false} onChange={e => setForm({ ...form, includesMeals: e.target.checked })} /> Comidas</label>
                </div>
            </div>
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Adultos</label>
                    <input type="number" min="1" className="w-full rounded-lg border p-2 text-sm" value={form.adults || 2} onChange={e => setForm({ ...form, adults: parseInt(e.target.value) })} />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Niños</label>
                    <input type="number" min="0" className="w-full rounded-lg border p-2 text-sm" value={form.children || 0} onChange={e => setForm({ ...form, children: parseInt(e.target.value) })} />
                </div>
            </div>
            <div>
                <label className="block text-sm font-medium mb-1">Itinerario (opcional)</label>
                <textarea rows="3" className="w-full rounded-lg border p-2 text-sm" placeholder="Día 1: Llegada y check-in..." value={form.itinerary || ""} onChange={e => setForm({ ...form, itinerary: e.target.value })} />
            </div>
        </div>
    );
}

// Formulario de precios (compartido)
function PricingForm({ form, setForm }) {
    return (
        <div className="rounded-xl bg-slate-50 dark:bg-slate-800/50 p-4 border space-y-4">
            <h4 className="text-xs font-bold text-slate-500 uppercase">Valores Económicos</h4>
            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className="block text-sm font-medium mb-1">Costo Neto *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2 text-slate-500">$</span>
                        <input type="number" step="0.01" className="w-full rounded-lg border pl-6 p-2 text-sm" value={form.netCost || 0} onChange={e => setForm({ ...form, netCost: parseFloat(e.target.value) })} required />
                    </div>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Precio Venta *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2 text-slate-500">$</span>
                        <input type="number" step="0.01" className="w-full rounded-lg border pl-6 p-2 text-sm" value={form.salePrice || 0} onChange={e => setForm({ ...form, salePrice: parseFloat(e.target.value) })} required />
                    </div>
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Comisión</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2 text-slate-500">$</span>
                        <input type="number" step="0.01" className="w-full rounded-lg border pl-6 p-2 text-sm" value={form.commission || 0} onChange={e => setForm({ ...form, commission: parseFloat(e.target.value) })} />
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

    useEffect(() => {
        if (isOpen) {
            setForm({ supplierId: "", netCost: 0, salePrice: 0, commission: 0 });
        }
    }, [isOpen, serviceType]);

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
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
            <div className="bg-white dark:bg-slate-900 rounded-2xl shadow-xl max-w-2xl w-full max-h-[90vh] overflow-hidden">
                <div className="flex items-center justify-between p-4 border-b">
                    <h2 className="text-lg font-semibold">Agregar Servicio</h2>
                    <button onClick={onClose} className="p-2 hover:bg-slate-100 rounded-lg"><X className="h-5 w-5" /></button>
                </div>

                {/* Tabs de tipo */}
                <div className="flex border-b">
                    {SERVICE_TYPES.map(({ value, label, icon: Icon }) => (
                        <button
                            key={value}
                            type="button"
                            onClick={() => setServiceType(value)}
                            className={`flex-1 flex items-center justify-center gap-2 py-3 text-sm font-medium transition-colors ${serviceType === value ? "border-b-2 border-indigo-600 text-indigo-600" : "text-slate-500 hover:text-slate-700"}`}
                        >
                            <Icon className="h-4 w-4" />
                            {label}
                        </button>
                    ))}
                </div>

                <form onSubmit={handleSubmit} className="p-4 space-y-4 overflow-y-auto max-h-[60vh]">
                    {serviceType === "Aereo" && <FlightForm form={form} setForm={setForm} suppliers={suppliers} />}
                    {serviceType === "Hotel" && <HotelForm form={form} setForm={setForm} suppliers={suppliers} />}
                    {serviceType === "Traslado" && <TransferForm form={form} setForm={setForm} suppliers={suppliers} />}
                    {serviceType === "Paquete" && <PackageForm form={form} setForm={setForm} suppliers={suppliers} />}

                    <PricingForm form={form} setForm={setForm} />

                    <div className="flex justify-end gap-2 pt-2">
                        <button type="button" onClick={onClose} className="px-4 py-2 text-sm rounded-lg hover:bg-slate-100">Cancelar</button>
                        <button type="submit" disabled={loading} className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-500 disabled:opacity-50">
                            {loading ? "Guardando..." : "Guardar Servicio"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
