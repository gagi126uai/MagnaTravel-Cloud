import { useEffect, useState } from "react";
import { Plus, Search, Pencil, User, Mail, Phone, FileText, CheckCircle2, XCircle } from "lucide-react";
import { api } from "../api";
import { formatCurrency } from "../lib/utils";
import Swal from "sweetalert2";

export default function CustomersPage() {
  const [customers, setCustomers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
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
    currentBalance: 0
  });

  useEffect(() => {
    fetchCustomers();
  }, []);

  const fetchCustomers = async () => {
    try {
      const data = await api.get("/customers");
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
        currentBalance: customer.currentBalance || 0
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
        currentBalance: 0
      });
    }
    setIsModalOpen(true);
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    try {
      if (currentCustomer) {
        await api.put(`/customers/${currentCustomer.id}`, { ...formData, id: currentCustomer.id });
        Swal.fire("Éxito", "Cliente actualizado", "success");
      } else {
        await api.post("/customers", formData);
        Swal.fire("Éxito", "Cliente creado", "success");
      }
      setIsModalOpen(false);
      fetchCustomers();
    } catch (error) {
      console.error("Error saving customer:", error);
      Swal.fire("Error", "No se pudo guardar el cliente", "error");
    }
  };

  const filteredCustomers = customers.filter(c =>
    c.fullName.toLowerCase().includes(searchTerm.toLowerCase()) ||
    c.documentNumber?.includes(searchTerm)
  );

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
        <div>
          <h2 className="text-3xl font-bold tracking-tight text-white">Clientes</h2>
          <p className="text-muted-foreground">Pasajeros y Cuentas Corporativas</p>
        </div>
        <button
          onClick={() => handleOpenModal()}
          className="flex items-center gap-2 rounded-lg bg-primary px-4 py-2 font-medium text-primary-foreground hover:bg-primary/90"
        >
          <Plus className="h-4 w-4" />
          Nuevo Cliente
        </button>
      </div>

      <div className="flex items-center gap-2 rounded-lg border bg-card/50 px-3 py-2 backdrop-blur-sm">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          type="text"
          placeholder="Buscar por nombre o documento..."
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
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground text-right">Límite Crédito</th>
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground text-right">Saldo (Deuda)</th>
                <th className="h-12 px-4 align-middle font-medium text-muted-foreground text-right">Acciones</th>
              </tr>
            </thead>
            <tbody className="[&_tr:last-child]:border-0">
              {filteredCustomers.map((customer) => (
                <tr key={customer.id} className="border-b transition-colors hover:bg-muted/50 data-[state=selected]:bg-muted">
                  <td className="p-4 align-middle font-medium text-white">
                    <div className="flex flex-col">
                      <span>{customer.fullName}</span>
                      <span className="text-xs text-muted-foreground">{customer.taxId || customer.documentNumber}</span>
                    </div>
                  </td>
                  <td className="p-4 align-middle text-muted-foreground">
                    <div className="flex flex-col">
                      <span>{customer.email}</span>
                      <span className="text-xs">{customer.phone}</span>
                    </div>
                  </td>
                  <td className="p-4 align-middle text-right text-muted-foreground">
                    {formatCurrency(customer.creditLimit)}
                  </td>
                  <td className={`p-4 align-middle text-right font-medium ${customer.currentBalance > 0 ? "text-red-400" : "text-green-400"}`}>
                    {formatCurrency(customer.currentBalance)}
                  </td>
                  <td className="p-4 align-middle text-right">
                    <button
                      onClick={() => handleOpenModal(customer)}
                      className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-input bg-background/50 text-sm font-medium shadow-sm hover:bg-accent hover:text-accent-foreground"
                    >
                      <Pencil className="h-4 w-4" />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {isModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
          <div className="w-full max-w-lg rounded-xl border bg-card p-6 shadow-xl sm:p-8 max-h-[90vh] overflow-y-auto">
            <h3 className="text-xl font-bold text-white mb-6">
              {currentCustomer ? "Editar Cliente" : "Nuevo Cliente"}
            </h3>
            <form onSubmit={handleSubmit} className="space-y-4">
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="col-span-2 space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Nombre Completo</label>
                  <input
                    type="text"
                    required
                    value={formData.fullName}
                    onChange={(e) => setFormData({ ...formData, fullName: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">CUIT / CUIL</label>
                  <input
                    type="text"
                    value={formData.taxId}
                    placeholder="20-12345678-9"
                    onChange={(e) => setFormData({ ...formData, taxId: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Documento (DNI/Pasaporte)</label>
                  <input
                    type="text"
                    value={formData.documentNumber}
                    onChange={(e) => setFormData({ ...formData, documentNumber: e.target.value })}
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
                <div className="col-span-2 space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Email</label>
                  <input
                    type="email"
                    value={formData.email}
                    onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>
                <div className="col-span-2 space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Dirección</label>
                  <input
                    type="text"
                    value={formData.address}
                    onChange={(e) => setFormData({ ...formData, address: e.target.value })}
                    className="w-full rounded-md border bg-background px-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                  />
                </div>

                <div className="col-span-2 border-t pt-4 mt-2">
                  <h4 className="text-sm font-bold text-white mb-3">Información Financiera</h4>
                </div>

                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Límite de Crédito</label>
                  <div className="relative">
                    <span className="absolute left-3 top-2 text-muted-foreground">$</span>
                    <input
                      type="number"
                      step="0.01"
                      value={formData.creditLimit}
                      onChange={(e) => setFormData({ ...formData, creditLimit: parseFloat(e.target.value) })}
                      className="w-full rounded-md border bg-background pl-6 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                    />
                  </div>
                </div>
                <div className="space-y-2">
                  <label className="text-sm font-medium text-muted-foreground">Saldo Actual</label>
                  <div className="relative">
                    <span className="absolute left-3 top-2 text-muted-foreground">$</span>
                    <input
                      type="number"
                      step="0.01"
                      value={formData.currentBalance}
                      onChange={(e) => setFormData({ ...formData, currentBalance: parseFloat(e.target.value) })}
                      className="w-full rounded-md border bg-background pl-6 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-primary"
                    />
                  </div>
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
