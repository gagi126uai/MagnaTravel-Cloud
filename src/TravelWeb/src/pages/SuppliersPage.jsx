import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search, Pencil, Wallet, Trash2, Building2, Mail, Phone, Power, CheckCircle2, XCircle, Info, RefreshCw } from "lucide-react";
import { api } from "../api";
import { formatCurrency } from "../lib/utils";
import { showError, showSuccess, showConfirm } from "../alerts";
import { Badge } from "../components/ui/badge";

export default function SuppliersPage() {
  const [suppliers, setSuppliers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [showInactive, setShowInactive] = useState(false);
  const navigate = useNavigate();

  // Modal State
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [currentSupplier, setCurrentSupplier] = useState(null);
  const [formData, setFormData] = useState({
    name: "",
    contactName: "",
    taxId: "",
    taxCondition: "",
    address: "",
    email: "",
    phone: "",
    isActive: true,
    currentBalance: 0
  });

  useEffect(() => {
    fetchSuppliers();
  }, []);

  const fetchSuppliers = async () => {
    try {
      setLoading(true);
      const data = await api.get("/suppliers");
      setSuppliers(data);
    } catch (error) {
      console.error("Error fetching suppliers:", error);
      showError("No se pudieron cargar los proveedores");
    } finally {
      setLoading(false);
    }
  };

  const handleOpenModal = (supplier = null) => {
    if (supplier) {
      setCurrentSupplier(supplier);
      setFormData({
        name: supplier.name,
        taxId: supplier.taxId || "",
        taxCondition: supplier.taxCondition || "",
        address: supplier.address || "",
        contactName: supplier.contactName || "",
        email: supplier.email || "",
        phone: supplier.phone || "",
        isActive: supplier.isActive,
        currentBalance: supplier.currentBalance
      });
    } else {
      setCurrentSupplier(null);
      setFormData({
        name: "",
        taxId: "",
        taxCondition: "",
        address: "",
        contactName: "",
        email: "",
        phone: "",
        isActive: true,
        currentBalance: 0
      });
    }
    setIsModalOpen(true);
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    try {
      if (currentSupplier) {
        await api.put(`/suppliers/${currentSupplier.id}`, { ...formData, id: currentSupplier.id });
        showSuccess("Proveedor actualizado");
      } else {
        await api.post("/suppliers", formData);
        showSuccess("Proveedor creado");
      }
      setIsModalOpen(false);
      fetchSuppliers();
    } catch (error) {
      console.error("Error saving supplier:", error);
      showError("No se pudo guardar el proveedor");
    }
  };

  const toggleStatus = async (supplier) => {
    try {
      const newStatus = !supplier.isActive;
      await api.put(`/suppliers/${supplier.id}`, {
        ...supplier,
        isActive: newStatus
      });
      showSuccess(`Proveedor ${newStatus ? 'activado' : 'desactivado'}`);
      fetchSuppliers();
    } catch (error) {
      showError("No se pudo cambiar el estado");
    }
  };

  const filteredSuppliers = suppliers.filter(supplier => {
    const matchesSearch = supplier.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
      supplier.taxId?.toLowerCase().includes(searchTerm.toLowerCase());
    const matchesStatus = showInactive ? true : supplier.isActive;
    return matchesSearch && matchesStatus;
  });

  // Avatar generator
  const getInitials = (name) => {
    return name
      ?.split(" ")
      .map((n) => n[0])
      .join("")
      .slice(0, 2)
      .toUpperCase() || "PV";
  };

  const getRandomColor = (name) => {
    const colors = ["bg-blue-500", "bg-emerald-500", "bg-violet-500", "bg-amber-500", "bg-rose-500", "bg-indigo-500"];
    let hash = 0;
    for (let i = 0; i < name.length; i++) {
      hash = name.charCodeAt(i) + ((hash << 5) - hash);
    }
    return colors[Math.abs(hash) % colors.length];
  };


  return (
    <div className="space-y-6 animate-in fade-in duration-500">
      {/* Header Section */}
      <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
        <div>
          <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Proveedores</h2>
          <p className="text-sm text-muted-foreground">Gestión comercial y cuentas corrientes</p>
        </div>
        <button
          onClick={() => handleOpenModal()}
          className="inline-flex items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 shadow-sm shadow-indigo-500/20 transition-all hover:scale-105"
        >
          <Plus className="h-4 w-4" />
          Nuevo Proveedor
        </button>
      </div>

      {/* Filters & Toolbar */}
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center justify-between rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <div className="relative flex-1 max-w-sm">
          <Search className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
          <input
            type="text"
            placeholder="Buscar por nombre o CUIT..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="w-full rounded-lg border border-slate-200 bg-slate-50 pl-9 pr-4 py-2 text-sm outline-none focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
          />
        </div>

        <div className="flex items-center gap-2">
          <label className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-400 cursor-pointer select-none px-3 py-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors">
            <input
              type="checkbox"
              checked={showInactive}
              onChange={(e) => setShowInactive(e.target.checked)}
              className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
            />
            Mostrar inactivos
          </label>
        </div>
      </div>

      {/* Main Table */}
      <div className="rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="relative w-full overflow-auto">
          <table className="w-full caption-bottom text-sm text-left">
            <thead className="[&_tr]:border-b">
              <tr className="border-b border-slate-100 dark:border-slate-800 transition-colors hover:bg-slate-50/50 dark:hover:bg-slate-800/50">
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400">Proveedor</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400">Contacto</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right">
                  <div className="flex items-center justify-end gap-1 cursor-help group relative">
                    Saldo (Deuda)
                    <Info className="h-3 w-3 text-slate-400" />
                    <div className="absolute bottom-full mb-2 right-0 w-64 p-2 bg-slate-800 text-white text-xs rounded shadow-lg opacity-0 group-hover:opacity-100 transition-opacity pointer-events-none z-10">
                      Solo incluye expedientes Reservados, Operativos o Cerrados. Los Presupuestos no suman deuda.
                    </div>
                  </div>
                </th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-center">Estado</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right">Acciones</th>
              </tr>
            </thead>
            <tbody className="[&_tr:last-child]:border-0">
              {filteredSuppliers.length === 0 ? (
                <tr>
                  <td colSpan={5} className="p-8 text-center text-muted-foreground">
                    No se encontraron proveedores
                  </td>
                </tr>
              ) : (
                filteredSuppliers.map((supplier) => (
                  <tr key={supplier.id} className={`border-b border-slate-100 dark:border-slate-800 transition-colors hover:bg-slate-50 dark:hover:bg-slate-800/50 ${!supplier.isActive ? 'opacity-60 bg-slate-50/50 dark:bg-slate-900/50' : ''}`}>
                    <td className="p-4 align-middle font-medium">
                      <div className="flex items-center gap-3">
                        <div className={`flex h-10 w-10 items-center justify-center rounded-full text-xs font-bold text-white shadow-sm ${getRandomColor(supplier.name)}`}>
                          {getInitials(supplier.name)}
                        </div>
                        <div className="flex flex-col">
                          <span className="text-slate-900 dark:text-white font-semibold">{supplier.name}</span>
                          <span className="text-xs text-slate-500">{supplier.taxId || "Sin CUIT"}</span>
                        </div>
                      </div>
                    </td>
                    <td className="p-4 align-middle">
                      <div className="flex flex-col gap-1">
                        <div className="flex items-center gap-2 text-slate-600 dark:text-slate-300">
                          <Building2 className="h-3 w-3" />
                          <span>{supplier.contactName || "-"}</span>
                        </div>
                        {supplier.email && (
                          <div className="flex items-center gap-2 text-xs text-slate-500">
                            <Mail className="h-3 w-3" />
                            {supplier.email}
                          </div>
                        )}
                      </div>
                    </td>
                    <td className="p-4 align-middle text-right">
                      <div className={`font-mono font-medium ${supplier.currentBalance > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                        {formatCurrency(supplier.currentBalance)}
                      </div>
                    </td>
                    <td className="p-4 align-middle text-center">
                      <Badge variant={supplier.isActive ? "success" : "secondary"}>
                        {supplier.isActive ? "Activo" : "Inactivo"}
                      </Badge>
                    </td>
                    <td className="p-4 align-middle text-right">
                      <div className="flex items-center justify-end gap-1">
                        <button
                          onClick={() => navigate(`/suppliers/${supplier.id}/account`)}
                          title="Ver Cuenta Corriente"
                          className="inline-flex h-8 w-8 items-center justify-center rounded-md text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 transition-colors"
                        >
                          <Wallet className="h-4 w-4" />
                        </button>
                        <button
                          onClick={() => handleOpenModal(supplier)}
                          className="inline-flex h-8 w-8 items-center justify-center rounded-md text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 transition-colors"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          onClick={() => toggleStatus(supplier)}
                          title={supplier.isActive ? "Desactivar" : "Activar"}
                          className={`inline-flex h-8 w-8 items-center justify-center rounded-md transition-colors ${supplier.isActive ? 'text-slate-500 hover:text-rose-600 hover:bg-rose-50 dark:hover:bg-rose-900/30' : 'text-slate-400 hover:text-emerald-600 hover:bg-emerald-50 dark:hover:bg-emerald-900/30'}`}
                        >
                          <Power className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Modal */}
      {isModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4 animate-in fade-in zoom-in duration-200">
          <div className="w-full max-w-lg rounded-2xl bg-white p-6 shadow-2xl dark:bg-slate-900 border border-slate-200 dark:border-slate-800">
            <div className="flex items-center justify-between mb-6">
              <div>
                <h3 className="text-xl font-bold text-slate-900 dark:text-white">
                  {currentSupplier ? "Editar Proveedor" : "Nuevo Proveedor"}
                </h3>
                <p className="text-sm text-muted-foreground mt-1">
                  {currentSupplier ? "Modifique los datos del proveedor" : "Ingrese los datos para dar de alta un nuevo proveedor"}
                </p>
              </div>
              {currentSupplier && (
                <Badge variant={formData.isActive ? "success" : "secondary"}>
                  {formData.isActive ? "Activo" : "Inactivo"}
                </Badge>
              )}
            </div>

            <form onSubmit={handleSubmit} className="space-y-5">
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="space-y-2 sm:col-span-2">
                  <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Razón Social *</label>
                  <input
                    type="text"
                    required
                    placeholder="Ej: Despegar Argentina S.A."
                    value={formData.name}
                    onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800"
                  />
                </div>

                <div className="space-y-2">
                  <label className="text-sm font-medium text-slate-700 dark:text-slate-300">CUIT</label>
                  <input
                    type="text"
                    placeholder="20-12345678-9"
                    value={formData.taxId}
                    onChange={(e) => setFormData({ ...formData, taxId: e.target.value })}
                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800"
                  />
                </div>

                <div className="space-y-2">
                  <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Condición Fiscal</label>
                  <select
                    value={formData.taxCondition}
                    onChange={(e) => setFormData({ ...formData, taxCondition: e.target.value })}
                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800"
                  >
                    <option value="">Seleccionar...</option>
                    <option value="IVA_RESP_INSCRIPTO">Resp. Inscripto</option>
                    <option value="MONOTRIBUTISTA">Monotributista</option>
                    <option value="IVA_EXENTO">Exento</option>
                    <option value="CONSUMIDOR_FINAL">Cons. Final</option>
                  </select>
                </div>

                <div className="space-y-2">
                  <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Contacto</label>
                  <input
                    type="text"
                    placeholder="Nombre contacto"
                    value={formData.contactName}
                    onChange={(e) => setFormData({ ...formData, contactName: e.target.value })}
                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800"
                  />
                </div>

                <div className="space-y-2">
                  <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Teléfono</label>
                  <input
                    type="text"
                    placeholder="+54 11 ..."
                    value={formData.phone}
                    onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800"
                  />
                </div>

                <div className="space-y-2 sm:col-span-2">
                  <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
                  <input
                    type="email"
                    placeholder="contacto@proveedor.com"
                    value={formData.email}
                    onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800"
                  />
                </div>

                {currentSupplier && (
                  <div className="sm:col-span-2 rounded-lg border bg-slate-50 dark:bg-slate-900/50 p-3 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <Wallet className="h-4 w-4 text-slate-500" />
                      <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Saldo Actual</span>
                    </div>
                    <span className={`font-mono font-bold ${currentSupplier.currentBalance > 0 ? "text-rose-600" : "text-emerald-600"}`}>
                      {formatCurrency(currentSupplier.currentBalance)}
                    </span>
                  </div>
                )}

              </div>

              <div className="flex gap-3 pt-4 border-t border-slate-100 dark:border-slate-800">
                <button
                  type="button"
                  onClick={() => setIsModalOpen(false)}
                  className="flex-1 rounded-lg border bg-white px-4 py-2.5 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  className="flex-1 rounded-lg bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 shadow-lg shadow-indigo-500/25"
                >
                  {currentSupplier ? "Guardar Cambios" : "Crear Proveedor"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
