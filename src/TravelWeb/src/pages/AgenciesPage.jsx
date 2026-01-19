import { useState, useEffect } from "react";
import { Plus, Search, Edit, Trash2 } from "lucide-react";
import { api } from "../api";
import { Button } from "../components/ui/button";
import Swal from "sweetalert2";

export default function AgenciesPage() {
    const [agencies, setAgencies] = useState([]);
    const [loading, setLoading] = useState(true);
    const [searchTerm, setSearchTerm] = useState("");
    const [isModalOpen, setIsModalOpen] = useState(false);
    const [currentAgency, setCurrentAgency] = useState(null);

    // Form State
    const [formData, setFormData] = useState({
        name: "",
        taxId: "",
        email: "",
        phone: "",
        creditLimit: 0,
        currentBalance: 0,
        isActive: true
    });

    useEffect(() => {
        fetchAgencies();
    }, []);

    const fetchAgencies = async () => {
        try {
            const data = await api.get("/agencies");
            setAgencies(data);
        } catch (error) {
            console.error("Error fetching agencies:", error);
            Swal.fire("Error", "No se pudieron cargar las agencias", "error");
        } finally {
            setLoading(false);
        }
    };

    const handleOpenModal = (agency = null) => {
        if (agency) {
            setCurrentAgency(agency);
            setFormData({
                name: agency.name,
                taxId: agency.taxId || "",
                email: agency.email || "",
                phone: agency.phone || "",
                creditLimit: agency.creditLimit,
                currentBalance: agency.currentBalance,
                isActive: agency.isActive
            });
        } else {
            setCurrentAgency(null);
            setFormData({
                name: "",
                taxId: "",
                email: "",
                phone: "",
                creditLimit: 0,
                currentBalance: 0,
                isActive: true
            });
        }
        setIsModalOpen(true);
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        try {
            if (currentAgency) {
                await api.put(`/agencies/${currentAgency.id}`, { ...formData, id: currentAgency.id });
                Swal.fire("Éxito", "Agencia actualizada", "success");
            } else {
                await api.post("/agencies", formData);
                Swal.fire("Éxito", "Agencia creada", "success");
            }
            setIsModalOpen(false);
            fetchAgencies();
        } catch (error) {
            console.error("Error saving agency:", error);
            Swal.fire("Error", "No se pudo guardar la agencia", "error");
        }
    };

    const filteredAgencies = agencies.filter(a =>
        a.name.toLowerCase().includes(searchTerm.toLowerCase())
    );

    return (
        <div className="space-y-6">
            <div className="flex items-center justify-between">
                <div>
                    <h2 className="text-3xl font-bold tracking-tight">Agencias</h2>
                    <p className="text-muted-foreground">Gestión de entidades comerciales y límites de crédito.</p>
                </div>
                <Button onClick={() => handleOpenModal()}>
                    <Plus className="mr-2 h-4 w-4" /> Nueva Agencia
                </Button>
            </div>

            <div className="flex items-center gap-2 max-w-sm">
                <Search className="h-4 w-4 text-muted-foreground" />
                <input
                    type="text"
                    placeholder="Buscar agencia..."
                    className="flex-1 bg-transparent outline-none text-sm border-b border-border py-1"
                    value={searchTerm}
                    onChange={(e) => setSearchTerm(e.target.value)}
                />
            </div>

            {loading ? (
                <div className="text-center py-10">Cargando...</div>
            ) : (
                <div className="rounded-md border bg-card">
                    <table className="w-full text-sm text-left">
                        <thead className="bg-muted/50 text-muted-foreground">
                            <tr>
                                <th className="p-4 font-medium">Nombre</th>
                                <th className="p-4 font-medium">Email / Tel</th>
                                <th className="p-4 font-medium text-right">Límite Crédito</th>
                                <th className="p-4 font-medium text-right">Saldo Actual</th>
                                <th className="p-4 font-medium text-right">Disponible</th>
                                <th className="p-4 font-medium text-center">Estado</th>
                                <th className="p-4 font-medium text-right">Acciones</th>
                            </tr>
                        </thead>
                        <tbody>
                            {filteredAgencies.map((agency) => {
                                const available = agency.creditLimit - agency.currentBalance;
                                const isNegative = available < 0;
                                return (
                                    <tr key={agency.id} className="border-t hover:bg-muted/50 transition-colors">
                                        <td className="p-4 font-medium">{agency.name}</td>
                                        <td className="p-4">
                                            <div className="flex flex-col text-xs text-muted-foreground">
                                                <span>{agency.email}</span>
                                                <span>{agency.phone}</span>
                                            </div>
                                        </td>
                                        <td className="p-4 text-right font-mono">${agency.creditLimit?.toLocaleString()}</td>
                                        <td className="p-4 text-right font-mono text-red-500">${agency.currentBalance?.toLocaleString()}</td>
                                        <td className={`p-4 text-right font-mono font-bold ${isNegative ? "text-red-600" : "text-green-600"}`}>
                                            ${available?.toLocaleString()}
                                        </td>
                                        <td className="p-4 text-center">
                                            <span className={`px-2 py-1 rounded-full text-xs ${agency.isActive ? "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400" : "bg-red-100 text-red-700"}`}>
                                                {agency.isActive ? "Activo" : "Inactivo"}
                                            </span>
                                        </td>
                                        <td className="p-4 text-right">
                                            <Button variant="ghost" size="icon" onClick={() => handleOpenModal(agency)}>
                                                <Edit className="h-4 w-4" />
                                            </Button>
                                        </td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
            )}

            {/* Modal */}
            {isModalOpen && (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-background/80 backdrop-blur-sm">
                    <div className="bg-card border shadow-lg rounded-lg p-6 w-full max-w-lg">
                        <h3 className="text-lg font-semibold mb-4">{currentAgency ? "Editar Agencia" : "Nueva Agencia"}</h3>
                        <form onSubmit={handleSubmit} className="space-y-4">
                            <div>
                                <label className="text-sm font-medium">Nombre</label>
                                <input
                                    className="w-full p-2 rounded border bg-background"
                                    value={formData.name}
                                    onChange={e => setFormData({ ...formData, name: e.target.value })}
                                    required
                                />
                            </div>
                            <div className="grid grid-cols-2 gap-4">
                                <div>
                                    <label className="text-sm font-medium">Tax ID</label>
                                    <input
                                        className="w-full p-2 rounded border bg-background"
                                        value={formData.taxId}
                                        onChange={e => setFormData({ ...formData, taxId: e.target.value })}
                                    />
                                </div>
                                <div>
                                    <label className="text-sm font-medium">Teléfono</label>
                                    <input
                                        className="w-full p-2 rounded border bg-background"
                                        value={formData.phone}
                                        onChange={e => setFormData({ ...formData, phone: e.target.value })}
                                    />
                                </div>
                            </div>
                            <div>
                                <label className="text-sm font-medium">Email</label>
                                <input
                                    className="w-full p-2 rounded border bg-background"
                                    type="email"
                                    value={formData.email}
                                    onChange={e => setFormData({ ...formData, email: e.target.value })}
                                />
                            </div>
                            <div className="grid grid-cols-2 gap-4">
                                <div>
                                    <label className="text-sm font-medium">Límite Crédito</label>
                                    <input
                                        type="number"
                                        className="w-full p-2 rounded border bg-background"
                                        value={formData.creditLimit}
                                        onChange={e => setFormData({ ...formData, creditLimit: parseFloat(e.target.value) })}
                                    />
                                </div>
                                <div>
                                    <label className="text-sm font-medium">Saldo Actual (Solo lectura)</label>
                                    <input
                                        type="number"
                                        disabled
                                        className="w-full p-2 rounded border bg-muted text-muted-foreground"
                                        value={formData.currentBalance}
                                    />
                                </div>
                            </div>

                            <div className="flex justify-end gap-2 mt-6">
                                <Button type="button" variant="outline" onClick={() => setIsModalOpen(false)}>Cancelar</Button>
                                <Button type="submit">Guardar</Button>
                            </div>
                        </form>
                    </div>
                </div>
            )}
        </div>
    );
}
