import { useEffect, useState } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { Plus, Search, FolderOpen, Calendar, DollarSign, User } from "lucide-react";
import { Button } from "../components/ui/button";
import { useNavigate } from "react-router-dom";

export default function FilesPage() {
    const [files, setFiles] = useState([]);
    const [loading, setLoading] = useState(false);
    const [searchTerm, setSearchTerm] = useState("");
    const navigate = useNavigate();

    // Create Modal State
    const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
    const [newFile, setNewFile] = useState({
        name: "",
        payerId: "",
        startDate: "",
        description: ""
    });
    const [customers, setCustomers] = useState([]);

    useEffect(() => {
        loadFiles();
        loadCustomers();
    }, []);

    const loadFiles = async () => {
        setLoading(true);
        try {
            // Assuming GET /reservations/files or similar endpoint exists, 
            // otherwise defaulting to /reservations generic for now until backend controller update
            // Ideally we need a specific /travelfiles endpoint. 
            // For this step I will assume /travelfiles will be created or I'll use a placeholder.
            // Let's use /travelfiles and if it fails we know we need the controller.
            const data = await api.get("/travelfiles");
            setFiles(data);
        } catch (error) {
            console.warn("Endpoint /travelfiles not ready yet, showing empty list or error");
            // Fallback or empty if backend isn't deployed yet
            setFiles([]);
        } finally {
            setLoading(false);
        }
    };

    const loadCustomers = async () => {
        try {
            const data = await api.get("/customers");
            setCustomers(data);
        } catch (error) {
            console.error("Error loading customers", error);
        }
    };

    const handleCreateFile = async (e) => {
        e.preventDefault();
        try {
            await api.post("/travelfiles", {
                ...newFile,
                payerId: newFile.payerId ? parseInt(newFile.payerId) : null,
                startDate: newFile.startDate ? new Date(newFile.startDate).toISOString() : null
            });
            showSuccess("Expediente creado exitosamente");
            setIsCreateModalOpen(false);
            loadFiles();
            setNewFile({ name: "", payerId: "", startDate: "", description: "" });
        } catch (error) {
            showError("Error al crear el expediente");
        }
    };

    // Status Badge Helper
    const getStatusColor = (status) => {
        switch (status) {
            case 'Presupuesto': return 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300';
            case 'Reservado': return 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300';
            case 'Operativo': return 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300';
            case 'Cerrado': return 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300';
            case 'Cancelado': return 'bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300';
            default: return 'bg-slate-100 text-slate-700';
        }
    };

    const filteredFiles = files.filter(f =>
        f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        f.fileNumber?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        f.payer?.fullName?.toLowerCase().includes(searchTerm.toLowerCase())
    );

    return (
        <div className="space-y-6">
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
                <div>
                    <h2 className="text-2xl font-bold tracking-tight">Expedientes</h2>
                    <p className="text-sm text-muted-foreground">Gestión integral de viajes y servicios.</p>
                </div>
                <Button onClick={() => setIsCreateModalOpen(true)} className="w-full sm:w-auto">
                    <Plus className="h-4 w-4 mr-2" /> Nuevo Expediente
                </Button>
            </div>

            {/* Filters */}
            <div className="flex items-center space-x-2 bg-white dark:bg-slate-900 p-2 rounded-xl border">
                <Search className="h-4 w-4 text-muted-foreground ml-2" />
                <input
                    className="flex-1 bg-transparent border-none text-sm focus:outline-none"
                    placeholder="Buscar por nombre, número de file o cliente..."
                    value={searchTerm}
                    onChange={(e) => setSearchTerm(e.target.value)}
                />
            </div>

            {/* Grid */}
            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
                {filteredFiles.map((file) => (
                    <div
                        key={file.id}
                        onClick={() => navigate(`/files/${file.id}`)}
                        className="group relative cursor-pointer overflow-hidden rounded-xl border bg-card p-5 transition-all hover:shadow-md hover:border-primary/50"
                    >
                        <div className="flex justify-between items-start mb-4">
                            <div className="flex items-center gap-2">
                                <div className="h-10 w-10 rounded-full bg-primary/10 flex items-center justify-center text-primary">
                                    <FolderOpen className="h-5 w-5" />
                                </div>
                                <div>
                                    <h3 className="font-semibold leading-none group-hover:text-primary transition-colors">
                                        {file.name}
                                    </h3>
                                    <span className="text-xs text-muted-foreground font-mono mt-1 block">
                                        {file.fileNumber}
                                    </span>
                                </div>
                            </div>
                            <span className={`text-xs px-2.5 py-1 rounded-full font-medium ${getStatusColor(file.status)}`}>
                                {file.status}
                            </span>
                        </div>

                        <div className="space-y-3 text-sm">
                            <div className="flex items-center gap-2 text-muted-foreground">
                                <User className="h-4 w-4 opacity-70" />
                                <span>{file.payer?.fullName || "Sin Cliente Asignado"}</span>
                            </div>
                            <div className="flex items-center gap-2 text-muted-foreground">
                                <Calendar className="h-4 w-4 opacity-70" />
                                <span>
                                    {file.startDate ? new Date(file.startDate).toLocaleDateString() : "Fecha a definir"}
                                </span>
                            </div>
                        </div>

                        <div className="mt-4 pt-4 border-t flex justify-between items-center">
                            <div className="text-xs text-muted-foreground">
                                Total Venta
                            </div>
                            <div className="font-bold text-lg font-mono">
                                ${file.totalSale?.toLocaleString() || "0"}
                            </div>
                        </div>

                        {/* Hover Balance Indicator */}
                        <div className="absolute bottom-0 left-0 h-1 w-full bg-slate-100">
                            <div
                                className={`h-full ${file.balance > 0 ? 'bg-rose-500' : 'bg-emerald-500'}`}
                                style={{ width: '100%' }} // Simplified for now, could be proportional
                            />
                        </div>
                    </div>
                ))}

                {filteredFiles.length === 0 && !loading && (
                    <div className="col-span-full py-12 text-center text-muted-foreground">
                        <div className="mx-auto h-12 w-12 rounded-full bg-slate-100 flex items-center justify-center mb-4">
                            <FolderOpen className="h-6 w-6 text-slate-400" />
                        </div>
                        <p>No se encontraron expedientes.</p>
                        <Button variant="link" onClick={() => setIsCreateModalOpen(true)}>
                            Crear el primero
                        </Button>
                    </div>
                )}
            </div>

            {/* Create Modal */}
            {isCreateModalOpen && (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
                    <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-2xl dark:bg-slate-900">
                        <h3 className="text-lg font-semibold mb-4">Nuevo Expediente</h3>
                        <form onSubmit={handleCreateFile} className="space-y-4">
                            <div>
                                <label className="text-sm font-medium">Nombre del Viaje</label>
                                <input
                                    autoFocus
                                    required
                                    placeholder="Ej. Familia Perez - Caribe 2025"
                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-800 dark:bg-slate-800"
                                    value={newFile.name}
                                    onChange={e => setNewFile({ ...newFile, name: e.target.value })}
                                />
                            </div>

                            <div>
                                <label className="text-sm font-medium">Cliente Principal (Pagador)</label>
                                <select
                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-800 dark:bg-slate-800"
                                    value={newFile.payerId}
                                    onChange={e => setNewFile({ ...newFile, payerId: e.target.value })}
                                >
                                    <option value="">Seleccionar cliente...</option>
                                    {customers.map(c => (
                                        <option key={c.id} value={c.id}>{c.fullName}</option>
                                    ))}
                                </select>
                            </div>

                            <div>
                                <label className="text-sm font-medium">Fecha de Inicio</label>
                                <input
                                    type="date"
                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-800 dark:bg-slate-800"
                                    value={newFile.startDate}
                                    onChange={e => setNewFile({ ...newFile, startDate: e.target.value })}
                                />
                            </div>

                            <div>
                                <label className="text-sm font-medium">Descripción (Opcional)</label>
                                <textarea
                                    rows={3}
                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-800 dark:bg-slate-800"
                                    value={newFile.description}
                                    onChange={e => setNewFile({ ...newFile, description: e.target.value })}
                                />
                            </div>

                            <div className="flex justify-end gap-2 pt-2">
                                <Button type="button" variant="ghost" onClick={() => setIsCreateModalOpen(false)}>
                                    Cancelar
                                </Button>
                                <Button type="submit">
                                    Crear
                                </Button>
                            </div>
                        </form>
                    </div>
                </div>
            )}
        </div>
    );
}
