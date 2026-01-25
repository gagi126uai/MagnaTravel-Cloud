import { useEffect, useState } from "react";
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
    MoreHorizontal,
    CreditCard,
    FileText,
    Trash2,
    Edit2
} from "lucide-react";
import { Button } from "../components/ui/button";

export default function FileDetailPage() {
    const { id } = useParams();
    const navigate = useNavigate();
    const [file, setFile] = useState(null);
    const [loading, setLoading] = useState(true);
    const [activeTab, setActiveTab] = useState("services"); // services, payments, docs

    // Generic Service Modal
    const [isServiceModalOpen, setIsServiceModalOpen] = useState(false);
    const [serviceType, setServiceType] = useState("Aereo"); // Default

    useEffect(() => {
        loadFile();
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

    const openServiceModal = (type) => {
        setServiceType(type);
        setIsServiceModalOpen(true);
        // Implementation of dynamic modal logic pending in next step
        // For now we just show a placeholder log
        console.log("Open modal for", type);
    }

    if (loading) return <div>Cargando expediente...</div>;
    if (!file) return <div>No encontrado</div>;

    return (
        <div className="space-y-6">
            {/* Header / Breadcrumb */}
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
                    <div className="flex items-center gap-4 text-sm text-muted-foreground mt-1">
                        <span className="flex items-center gap-1">
                            <User className="h-3 w-3" /> {file.payer?.fullName || "Sin cliente"}
                        </span>
                        <span className="flex items-center gap-1">
                            <Calendar className="h-3 w-3" /> {file.startDate ? new Date(file.startDate).toLocaleDateString() : "Fecha abierta"}
                        </span>
                        <span className={`px-2 rounded-full text-xs font-medium bg-blue-100 text-blue-700`}>
                            {file.status}
                        </span>
                    </div>
                </div>
                <div className="ml-auto flex gap-2">
                    <Button variant="outline">
                        <FileText className="h-4 w-4 mr-2" /> Voucher
                    </Button>
                    <Button>
                        <Plus className="h-4 w-4 mr-2" /> Agregar Servicio
                    </Button>
                </div>
            </div>

            {/* Financial Summary */}
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
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
                    <div className="text-2xl font-bold mt-1 text-emerald-600">
                        ${(file.totalSale - file.totalCost).toLocaleString()}
                    </div>
                </div>
                <div className="rounded-xl border bg-card p-4 border-l-4 border-l-indigo-500">
                    <div className="text-xs text-muted-foreground uppercase font-bold">Saldo Pendiente</div>
                    <div className="text-2xl font-bold mt-1 text-indigo-600">
                        ${file.balance?.toLocaleString()}
                    </div>
                </div>
            </div>

            {/* Tabs / Content */}
            <div className="flex gap-6 border-b border-slate-200 dark:border-slate-800">
                {['services', 'payments', 'documents', 'notes'].map(tab => (
                    <button
                        key={tab}
                        onClick={() => setActiveTab(tab)}
                        className={`pb-3 text-sm font-medium border-b-2 transition-colors capitalize ${activeTab === tab
                                ? 'border-primary text-primary'
                                : 'border-transparent text-muted-foreground hover:text-foreground'
                            }`}
                    >
                        {tab === 'services' ? 'Itinerario' : tab === 'payments' ? 'Pagos' : tab === 'documents' ? 'Documentos' : 'Notas'}
                    </button>
                ))}
            </div>

            {activeTab === 'services' && (
                <div className="space-y-4">
                    {file.reservations && file.reservations.length > 0 ? (
                        <div className="space-y-3">
                            {/* Render each reservation as a generic service card */}
                            {file.reservations.map(res => (
                                <div key={res.id} className="flex items-center gap-4 rounded-xl border bg-card p-4 hover:shadow-sm transition-shadow">
                                    <div className={`h-10 w-10 flex items-center justify-center rounded-lg bg-slate-100 text-slate-600`}>
                                        {res.serviceType === 'Aereo' ? <Plane className="h-5 w-5" /> :
                                            res.serviceType === 'Hotel' ? <Hotel className="h-5 w-5" /> :
                                                <CreditCard className="h-5 w-5" />}
                                    </div>
                                    <div className="flex-1">
                                        <div className="flex items-center gap-2">
                                            <h4 className="font-semibold">{res.description || res.serviceType}</h4>
                                            <span className="text-xs bg-slate-100 px-2 py-0.5 rounded text-slate-600">
                                                {res.status}
                                            </span>
                                        </div>
                                        <div className="text-sm text-muted-foreground">
                                            {res.supplier?.name} • Confirmación: {res.confirmationNumber || "Pendiente"}
                                        </div>
                                    </div>
                                    <div className="text-right">
                                        <div className="font-bold">${res.salePrice?.toLocaleString()}</div>
                                        <div className="text-xs text-muted-foreground">
                                            {new Date(res.departureDate).toLocaleDateString()}
                                        </div>
                                    </div>
                                    <div className="flex gap-1">
                                        <Button variant="ghost" size="icon">
                                            <Edit2 className="h-4 w-4" />
                                        </Button>
                                        <Button variant="ghost" size="icon" className="text-rose-500 hover:text-rose-600">
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

            {/* Other tabs implementation placeholders */}
            {activeTab === 'payments' && (
                <div className="py-8 text-center text-muted-foreground">
                    Módulo de Cobranzas integrado próximamente.
                </div>
            )}

            {/* Service Modal Placeholder */}
            {isServiceModalOpen && (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
                    <div className="bg-white p-8 rounded-xl">
                        <h2>Cargar {serviceType}</h2>
                        <p>Funcionalidad en construcción en el siguiente paso.</p>
                        <Button onClick={() => setIsServiceModalOpen(false)} className="mt-4">Cerrar</Button>
                    </div>
                </div>
            )}
        </div>
    );
}
