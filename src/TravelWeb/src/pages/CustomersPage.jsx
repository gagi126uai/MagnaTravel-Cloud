import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search, Pencil, Wallet, User, Mail, Phone, FileText, CheckCircle2, XCircle, Trash2, Power, AlertTriangle } from "lucide-react";
import { api } from "../api";
import { formatCurrency } from "../lib/utils";
import Swal from "sweetalert2";
import { Badge } from "../components/ui/badge";

export default function CustomersPage() {
  const [customers, setCustomers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [showInactive, setShowInactive] = useState(false);
  const navigate = useNavigate();
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [currentCustomer, setCurrentCustomer] = useState(null);
  const [formData, setFormData] = useState({
    fullName: "",
    taxId: "",
    email: "",
    phone: "",
    documentNumber: "",
    address: "",
    notes: "",
    creditLimit: 0,
    isActive: true
  });

  useEffect(() => {
    fetchCustomers();
  }, [showInactive]);

  const fetchCustomers = async () => {
    try {
      // Backend now supports filtering by IsActive
      const data = await api.get(`/customers?includeInactive=${showInactive}`);
      setCustomers(data);
    } catch (error) {
      console.error("Error fetching customers:", error);
      Swal.fire("Error", "No se pudieron cargar los clientes", "error");
    } finally {
      setLoading(false);
    }
  };

  const handleOpenModal = (customer = null) => {
    if (customer) {
      setCurrentCustomer(customer);
      setFormData({
        fullName: customer.fullName,
        taxId: customer.taxId || "",
        email: customer.email || "",
        phone: customer.phone || "",
        documentNumber: customer.documentNumber || "",
        address: customer.address || "",
        notes: customer.notes || "",
        creditLimit: customer.creditLimit || 0,
        isActive: customer.isActive,
        // Current balance is read-only and passed separately
      });
    } else {
      setCurrentCustomer(null);
      setFormData({
        fullName: "",
        taxId: "",
        email: "",
        phone: "",
        documentNumber: "",
        address: "",
        notes: "",
        creditLimit: 0,
        isActive: true
      });
    }
    setIsModalOpen(true);
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    try {
      if (currentCustomer) {
        await api.put(`/customers/${currentCustomer.id}`, { ...formData, id: currentCustomer.id });
        Swal.fire({
          title: "Actualizado",
          text: "Cliente actualizado correctamente",
          icon: "success",
          timer: 1500,
          showConfirmButton: false
        });
      } else {
        await api.post("/customers", formData);
        Swal.fire({
          title: "Creado",
          text: "Cliente creado exitosamente",
          icon: "success",
          timer: 1500,
          showConfirmButton: false
        });
      }
      setIsModalOpen(false);
      fetchCustomers();
    } catch (error) {
      console.error("Error saving customer:", error);
      Swal.fire("Error", "No se pudo guardar el cliente", "error");
    }
  };

  const handleToggleStatus = async (customer) => {
    const action = customer.isActive ? "desactivar" : "activar";
    const result = await Swal.fire({
      title: `¿${action.charAt(0).toUpperCase() + action.slice(1)} cliente?`,
      text: customer.isActive
        ? "El cliente no aparecerá en las búsquedas de nuevos expedientes."
        : "El cliente volverá a estar disponible.",
      icon: "warning",
      showCancelButton: true,
      confirmButtonColor: customer.isActive ? "#ef4444" : "#10b981",
      cancelButtonColor: "#3085d6",
      confirmButtonText: `Sí, ${action}`,
      cancelButtonText: "Cancelar"
    });

    if (result.isConfirmed) {
      try {
        await api.put(`/customers/${customer.id}`, {
          ...customer,
          isActive: !customer.isActive,
          // Ensure required fields are present if backend needs them full
          id: customer.id
        });
        fetchCustomers();
        Swal.fire({
          title: "Hecho",
          text: `Cliente ${action === "activar" ? "activado" : "desactivado"}.`,
          icon: "success",
          timer: 1000,
          showConfirmButton: false
        });
      } catch (error) {
        Swal.fire("Error", "No se pudo cambiar el estado", "error");
      }
    }
  };

  const filteredCustomers = customers.filter(c =>
    c.fullName.toLowerCase().includes(searchTerm.toLowerCase()) ||
    c.documentNumber?.includes(searchTerm)
  );

  const getInitials = (name) => {
    return name
      .split(" ")
      .map((n) => n[0])
      .join("")
      .toUpperCase()
      .slice(0, 2);
  };

  return (
    <div className="space-y-4 md:space-y-6 animate-in fade-in duration-500">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-xl md:text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Gestión de Clientes</h2>
          <p className="text-sm text-muted-foreground">Administra pasajeros y cuentas corporativas.</p>
        </div>
        <div className="flex gap-2">
          <button
            onClick={() => setShowInactive(!showInactive)}
            className={`text-xs px-3 py-2 rounded-md transition-colors border ${showInactive ? 'bg-slate-100 dark:bg-slate-800 border-slate-300 dark:border-slate-700' : 'bg-transparent border-transparent hover:bg-slate-50 dark:hover:bg-slate-900'}`}
          >
            {showInactive ? "Ocultar Inactivos" : "Mostrar Inactivos"}
          </button>
          <button
            onClick={() => handleOpenModal()}
            className="flex items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 shadow-md transition-all hover:shadow-lg w-full sm:w-auto"
          >
            <Plus className="h-4 w-4" />
            Nuevo Cliente
          </button>
        </div>
      </div>

      <div className="flex items-center gap-2 rounded-lg border bg-card/50 px-3 py-2 backdrop-blur-sm shadow-sm focus-within:ring-2 focus-within:ring-indigo-500/20 transition-all">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          type="text"
          placeholder="Buscar por nombre, documento o CUIT..."
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          className="flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
        />
      </div>

      <div className="rounded-xl border bg-card shadow-sm overflow-hidden">
        <div className="relative w-full overflow-auto">
          <table className="w-full caption-bottom text-sm text-left">
            <thead className="bg-slate-50 dark:bg-slate-900/50">
              <tr className="border-b transition-colors">
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400">Cliente</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400">Contacto</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right">Límite Crédito</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right">Saldo Actual</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-center">Estado</th>
                <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right">Acciones</th>
              </tr>
            </thead>
            <tbody className="[&_tr:last-child]:border-0">
              {filteredCustomers.map((customer) => (
                <tr key={customer.id} className={`border-b transition-colors hover:bg-slate-50/50 dark:hover:bg-slate-900/50 ${!customer.isActive ? 'opacity-60 bg-slate-50/30' : ''}`}>
                  <td className="p-4 align-middle">
                    <div className="flex items-center gap-3">
                      <div className={`h-10 w-10 rounded-full flex items-center justify-center text-sm font-bold shadow-sm ${customer.isActive ? 'bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300' : 'bg-slate-200 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}`}>
                        {getInitials(customer.fullName)}
                      </div>
                      <div className="flex flex-col">
                        <span className="font-semibold text-slate-900 dark:text-slate-100">{customer.fullName}</span>
                        <span className="text-xs text-muted-foreground">{customer.taxId || customer.documentNumber || "S/D"}</span>
                      </div>
                    </div>
                  </td>
                  <td className="p-4 align-middle text-muted-foreground">
                    <div className="flex flex-col gap-0.5">
                      <div className="flex items-center gap-1.5 text-xs">
                        <Mail className="h-3 w-3" />
                        {customer.email || "-"}
                      </div>
                      <div className="flex items-center gap-1.5 text-xs">
                        <Phone className="h-3 w-3" />
                        {customer.phone || "-"}
                      </div>
                    </div>
                  </td>
                  <td className="p-4 align-middle text-right text-muted-foreground font-mono">
                    {formatCurrency(customer.creditLimit)}
                  </td>
                  <td className="p-4 align-middle text-right">
                    <div className={`font-mono font-medium ${(customer.currentBalance || 0) > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                      {formatCurrency(customer.currentBalance || 0)}
                    </div>
                    {(customer.currentBalance || 0) > 0 && (
                      <span className="text-[10px] text-rose-500 font-semibold uppercase">Deuda</span>
                    )}
                  </td>
                  <td className="p-4 align-middle text-center">
                    <Badge variant={customer.isActive ? "success" : "secondary"} className={customer.isActive ? "bg-emerald-100 text-emerald-700 border-transparent" : "bg-slate-100 text-slate-500"}>
                      {customer.isActive ? "Activo" : "Inactivo"}
                    </Badge>
                  </td>
                  <td className="p-4 align-middle text-right">
                    <div className="flex items-center justify-end gap-1">
                      <button
                        onClick={() => navigate(`/customers/${customer.id}/account`)}
                        title="Ver Cuenta Corriente"
                        className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-slate-50 hover:text-indigo-600 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400 dark:hover:bg-slate-900 transition-colors shadow-sm"
                      >
                        <Wallet className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => handleOpenModal(customer)}
                        title="Editar Cliente"
                        className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-slate-50 hover:text-indigo-600 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400 dark:hover:bg-slate-900 transition-colors shadow-sm"
                      >
                        <Pencil className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => handleToggleStatus(customer)}
                        title={customer.isActive ? "Desactivar" : "Activar"}
                        className={`inline-flex h-8 w-8 items-center justify-center rounded-md border shadow-sm transition-colors ${customer.isActive ? 'border-slate-200 bg-white text-slate-400 hover:bg-rose-50 hover:text-rose-600 hover:border-rose-200 dark:border-slate-800 dark:bg-slate-950' : 'border-emerald-200 bg-emerald-50 text-emerald-600 hover:bg-emerald-100'}`}
                      >
                        <Power className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {filteredCustomers.length === 0 && !loading && (
            <div className="p-8 text-center text-muted-foreground flex flex-col items-center">
              <div className="bg-slate-50 dark:bg-slate-800/50 p-4 rounded-full mb-3">
                <Search className="h-8 w-8 opacity-20" />
              </div>
              <p>No se encontraron clientes</p>
            </div>
          )}
        </div>
      </div>

      {isModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
          <div className="w-full max-w-lg rounded-xl border bg-card p-0 shadow-2xl max-h-[90vh] overflow-y-auto scale-100 animate-in zoom-in-95 duration-200">
            {/* Modal Header */}
            <div className="px-6 py-4 border-b bg-slate-50/50 dark:bg-slate-900/50 flex items-center justify-between">
              <div>
                <h3 className="text-lg font-bold text-slate-900 dark:text-white">
                  {currentCustomer ? "Editar Cliente" : "Nuevo Cliente"}
                </h3>
                <p className="text-sm text-muted-foreground">
                  {currentCustomer ? "Modificar datos del cliente" : "Registrar un nuevo cliente en el sistema"}
                </p>
              </div>
              <button
                onClick={() => setIsModalOpen(false)}
                className="text-slate-400 hover:text-slate-500 transition-colors"
              >
                <XCircle className="h-5 w-5" />
              </button>
            </div>

            <form onSubmit={handleSubmit}>
              <div className="p-6 space-y-4">
                {/* Basic Info */}
                <div className="grid gap-4 sm:grid-cols-2">
                  <div className="col-span-2 space-y-1.5">
                    <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Nombre Completo <span className="text-red-500">*</span></label>
                    <div className="relative">
                      <User className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                      <input
                        type="text"
                        required
                        value={formData.fullName}
                        onChange={(e) => setFormData({ ...formData, fullName: e.target.value })}
                        className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                        placeholder="Ej. Juan Pérez"
                      />
                    </div>
                  </div>

                  <div className="space-y-1.5">
                    <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Documento / Pasaporte</label>
                    <input
                      type="text"
                      value={formData.documentNumber}
                      onChange={(e) => setFormData({ ...formData, documentNumber: e.target.value })}
                      className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                    />
                  </div>
                  <div className="space-y-1.5">
                    <label className="text-sm font-medium text-slate-700 dark:text-slate-300">CUIT / ID Fiscal</label>
                    <input
                      type="text"
                      value={formData.taxId}
                      placeholder="Ej. 20-12345678-9"
                      onChange={(e) => setFormData({ ...formData, taxId: e.target.value })}
                      className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                    />
                  </div>

                  <div className="col-span-2 grid sm:grid-cols-2 gap-4">
                    <div className="space-y-1.5">
                      <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
                      <div className="relative">
                        <Mail className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                        <input
                          type="email"
                          value={formData.email}
                          onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                          className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                        />
                      </div>
                    </div>
                    <div className="space-y-1.5">
                      <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Teléfono</label>
                      <div className="relative">
                        <Phone className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                        <input
                          type="text"
                          value={formData.phone}
                          onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                          className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                        />
                      </div>
                    </div>
                  </div>

                  <div className="col-span-2 space-y-1.5">
                    <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Dirección</label>
                    <input
                      type="text"
                      value={formData.address}
                      onChange={(e) => setFormData({ ...formData, address: e.target.value })}
                      className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                    />
                  </div>
                </div>

                {/* Financial Info - ONLY SHOW ON EDIT */}
                {currentCustomer && (
                  <div className="rounded-lg border bg-slate-50 dark:bg-slate-900/40 p-4 space-y-4">
                    <div className="flex items-center gap-2 border-b border-slate-200 dark:border-slate-800 pb-2">
                      <Wallet className="h-4 w-4 text-indigo-500" />
                      <h4 className="text-sm font-semibold text-slate-900 dark:text-white">Información Financiera</h4>
                    </div>

                    <div className="grid gap-4 sm:grid-cols-2">
                      <div className="space-y-1.5">
                        <label className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Límite de Crédito</label>
                        <div className="relative">
                          <span className="absolute left-3 top-2 text-muted-foreground">$</span>
                          <input
                            type="number"
                            step="0.01"
                            value={formData.creditLimit}
                            onChange={(e) => setFormData({ ...formData, creditLimit: parseFloat(e.target.value) })}
                            className="w-full rounded-md border border-slate-200 bg-white pl-6 pr-3 py-1.5 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800"
                          />
                        </div>
                      </div>
                      <div className="space-y-1.5">
                        <label className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Saldo Actual (Automático)</label>
                        <div className="relative">
                          <span className="absolute left-3 top-2 text-muted-foreground">$</span>
                          <input
                            type="text"
                            disabled
                            value={currentCustomer.currentBalance?.toLocaleString()}
                            className={`w-full rounded-md border border-transparent bg-transparent pl-6 pr-3 py-1.5 text-sm font-bold ${currentCustomer.currentBalance > 0 ? 'text-rose-600' : 'text-emerald-600'}`}
                          />
                        </div>
                      </div>
                    </div>
                  </div>
                )}
              </div>

              <div className="flex gap-3 px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50">
                <button
                  type="button"
                  onClick={() => setIsModalOpen(false)}
                  className="flex-1 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  className="flex-1 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 shadow-sm transition-colors"
                >
                  {currentCustomer ? "Guardar Cambios" : "Crear Cliente"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
