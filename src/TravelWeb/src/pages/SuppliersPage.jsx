import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search, Pencil, Wallet, Trash2, Building2, Mail, Phone, Power, CheckCircle2, XCircle } from "lucide-react";
import { api } from "../api";
import { formatCurrency } from "../lib/utils";
import Swal from "sweetalert2";

export default function SuppliersPage() {
  const [suppliers, setSuppliers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const navigate = useNavigate();
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [currentSupplier, setCurrentSupplier] = useState(null);
  const [formData, setFormData] = useState({
    name: "",
    contactName: "",
    taxId: "",
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
      const data = await api.get("/suppliers");
      setSuppliers(data);
    } catch (error) {
      console.error("Error fetching suppliers:", error);
      Swal.fire("Error", "No se pudieron cargar los proveedores", "error");
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
        Swal.fire("Éxito", "Proveedor actualizado", "success");
      } else {
        await api.post("/suppliers", formData);
        Swal.fire("Éxito", "Proveedor creado", "success");
      }
      setIsModalOpen(false);
      fetchSuppliers();
    } catch (error) {
      console.error("Error saving supplier:", error);
      Swal.fire("Error", "No se pudo guardar el proveedor", "error");
    }
  };

  const handleDelete = async (supplier) => {
    const result = await Swal.fire({
      title: "¿Eliminar proveedor?",
      text: `¿Estás seguro de eliminar a ${supplier.name}?`,
      icon: "warning",
      showCancelButton: true,
      confirmButtonColor: "#ef4444",
      confirmButtonText: "Sí, eliminar",
      cancelButtonText: "Cancelar"
    });

    if (result.isConfirmed) {
      try {
        await api.delete(`/suppliers/${supplier.id}`);
        Swal.fire("Eliminado", "Proveedor eliminado correctamente", "success");
        fetchSuppliers();
      } catch (error) {
        const errorMsg = error.response?.data || "No se pudo eliminar el proveedor";

        // Ofrecer forzar eliminación si tiene servicios/pagos
        const forceResult = await Swal.fire({
          title: "No se puede eliminar",
          text: `${errorMsg}. ¿Desea FORZAR la eliminación? (desvinculará servicios y eliminará pagos)`,
          icon: "error",
          showCancelButton: true,
          confirmButtonColor: "#dc2626",
          confirmButtonText: "Forzar Eliminación",
          cancelButtonText: "Cancelar"
        });

        if (forceResult.isConfirmed) {
          try {
            await api.delete(`/suppliers/${supplier.id}/force`);
            Swal.fire("Eliminado", "Proveedor eliminado (forzado)", "success");
            fetchSuppliers();
          } catch (forceError) {
            Swal.fire("Error", forceError.response?.data || "No se pudo forzar la eliminación", "error");
          }
        }
      }
    }
  };

  const filteredSuppliers = suppliers.filter(supplier =>
    supplier.name.toLowerCase().includes(searchTerm.toLowerCase())
  );

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
        <div>
          <h2 className="text-3xl font-bold tracking-tight text-white">Proveedores</h2>
          <p className="text-muted-foreground">Gestión de proveedores y acreedores</p>
        </div>
        <button
          onClick={() => handleOpenModal()}
          className="flex items-center gap-2 rounded-lg bg-primary px-4 py-2 font-medium text-primary-foreground hover:bg-primary/90"
        >
          <Plus className="h-4 w-4" />
          Nuevo Proveedor
        </button>
      </div>

      <div className="flex items-center gap-2 rounded-lg border bg-card/50 px-3 py-2 backdrop-blur-sm">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          type="text"
          placeholder="Buscar proveedores..."
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          className="flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
        />
      </div>

      <div className="rounded-xl border bg-card/50 backdrop-blur-sm">
        <div className="relative w-full overflow-auto">
          <table className="w-full caption-bottom text-sm text-left">
            <thead className="[&_tr]:border-b">
              <tr className="border-b transition-colors hover:bg-muted/50 data-[state=selected]:bg-muted">
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground">Nombre</th>
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground">Contacto</th>
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground text-right">Saldo (Deuda)</th>
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground text-center">Estado</th>
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground text-right">Acciones</th>
              </tr>
            </thead>
            <tbody className="[&_tr:last-child]:border-0">
              {filteredSuppliers.map((supplier) => (
                <tr key={supplier.id} className="border-b transition-colors hover:bg-muted/50 data-[state=selected]:bg-muted">
                  <td className="p-4 align-middle font-medium text-white">
                    <div className="flex flex-col">
                      <span>{supplier.name}</span>
                      <span className="text-xs text-muted-foreground">{supplier.taxId || "Sin CUIT"}</span>
                    </div>
                  </td>
                  <td className="p-4 align-middle">
                    <div className="flex flex-col">
                      <span>{supplier.contactName || "-"}</span>
                      <span className="text-xs text-muted-foreground">{supplier.email}</span>
                    </div>
                  </td>
                  <td className={`p-4 align-middle text-right font-medium ${supplier.currentBalance > 0 ? "text-red-400" : "text-green-400"}`}>
                    {formatCurrency(supplier.currentBalance)}
                  </td>
                  <td className="p-4 align-middle text-center">
                    {supplier.isActive ? (
                      <span className="inline-flex items-center rounded-full bg-green-500/10 px-2 py-1 text-xs font-medium text-green-500 ring-1 ring-inset ring-green-500/20">Active</span>
                    ) : (
                      <span className="inline-flex items-center rounded-full bg-red-500/10 px-2 py-1 text-xs font-medium text-red-500 ring-1 ring-inset ring-red-500/20">Inactive</span>
                    )}
                  </td>
                  <td className="p-4 align-middle text-right">
                    <div className="flex items-center justify-end gap-1">
                      <button
                        onClick={() => navigate(`/suppliers/${supplier.id}/account`)}
                        title="Ver Cuenta Corriente"
                        className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-red-500/50 bg-red-500/10 text-red-500 text-sm font-medium shadow-sm hover:bg-red-500/20"
                      >
                        <Wallet className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => handleOpenModal(supplier)}
                        title="Editar Proveedor"
                        className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-input bg-background/50 text-sm font-medium shadow-sm hover:bg-accent hover:text-accent-foreground"
                      >
                        <Pencil className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => handleDelete(supplier)}
                        title="Eliminar Proveedor"
                        className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-red-500/50 bg-red-500/10 text-red-500 text-sm font-medium shadow-sm hover:bg-red-500/20"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {/* Modal */}
      {isModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
          <div className="w-full max-w-lg rounded-xl border bg-card p-6 shadow-xl sm:p-8">
            <h3 className="text-xl font-bold text-white mb-6">
              {currentSupplier ? "Editar Proveedor" : "Nuevo Proveedor"}
            </h3>
            <form onSubmit={handleSubmit} className="space-y-4">
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Nombre</label>
                  <input
                    type="text"
                    required
                    value={formData.name}
                    onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">CUIT</label>
                  <input
                    type="text"
                    required
                    placeholder="20-12345678-9"
                    value={formData.taxId}
                    onChange={(e) => setFormData({ ...formData, taxId: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Contacto</label>
                  <input
                    type="text"
                    value={formData.contactName}
                    onChange={(e) => setFormData({ ...formData, contactName: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Email</label>
                  <input
                    type="email"
                    value={formData.email}
                    onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Teléfono</label>
                  <input
                    type="text"
                    value={formData.phone}
                    onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Current Balance (Deuda)</label>
                  <input
                    type="number"
                    step="0.01"
                    value={formData.currentBalance}
                    onChange={(e) => setFormData({ ...formData, currentBalance: parseFloat(e.target.value) })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="flex items-center space-x-2 pt-8">
                  <input
                    type="checkbox"
                    id="isActive"
                    checked={formData.isActive}
                    onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })}
                    className="rounded border-gray-300 text-primary focus:ring-primary"
                  />
                  <label htmlFor="isActive" className="text-sm font-medium text-white">
                    Activo
                  </label>
                </div>
              </div>
              <div className="flex gap-3 pt-4">
                <button
                  type="button"
                  onClick={() => setIsModalOpen(false)}
                  className="flex-1 rounded-lg border bg-transparent px-4 py-2 font-medium text-muted-foreground hover:bg-accent"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  className="flex-1 rounded-lg bg-primary px-4 py-2 font-medium text-primary-foreground hover:bg-primary/90"
                >
                  Guardar
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
