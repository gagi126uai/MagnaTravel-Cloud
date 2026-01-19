import { useEffect, useState } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import Swal from "sweetalert2";
import { Plane, Calendar, Plus } from "lucide-react";
import { Button } from "../components/ui/button";

export default function ReservationsPage() {
  const [reservations, setReservations] = useState([]);
  const [selectedReservation, setSelectedReservation] = useState(null);
  const [isSegmentModalOpen, setIsSegmentModalOpen] = useState(false);

  // Create Reservation Form
  const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
  const [customers, setCustomers] = useState([]);
  const [createForm, setCreateForm] = useState({
    customerId: "",
    referenceCode: "",
    productType: "Flight",
    supplierName: "",
    departureDate: "",
    returnDate: "",
    basePrice: 0,
    commission: 0,
    totalAmount: 0
  });

  useEffect(() => {
    loadReservations();
    loadCustomers();
  }, []);

  const loadReservations = async () => {
    try {
      const data = await api.get("/reservations");
      setReservations(data);
    } catch {
      showError("No se pudieron cargar las reservas.");
    }
  };

  const loadCustomers = async () => {
    try {
      const data = await api.get("/customers");
      setCustomers(data);
    } catch {
      console.error("Failed to load customers");
    }
  };

  const loadReservationDetail = async (id) => {
    try {
      const data = await api.get(`/reservations/${id}`);
      setSelectedReservation(data);
    } catch {
      showError("No se pudo cargar el detalle de la reserva.");
    }
  };

  const handleCreateSubmit = async (e) => {
    e.preventDefault();
    try {
      const payload = {
        ...createForm,
        customerId: parseInt(createForm.customerId),
        basePrice: parseFloat(createForm.basePrice),
        commission: parseFloat(createForm.commission),
        totalAmount: parseFloat(createForm.totalAmount),
        departureDate: new Date(createForm.departureDate).toISOString(),
        returnDate: createForm.returnDate ? new Date(createForm.returnDate).toISOString() : null
      };

      await api.post("/reservations", payload);
      showSuccess("Reserva creada exitosamente");
      setIsCreateModalOpen(false);
      loadReservations(); // Refresh list

      // Reset form
      setCreateForm({
        customerId: "",
        referenceCode: "",
        productType: "Flight",
        supplierName: "",
        departureDate: "",
        returnDate: "",
        basePrice: 0,
        commission: 0,
        totalAmount: 0
      });
    } catch (error) {
      console.error(error);
      showError("Error al crear la reserva. Verifique los datos.");
    }
  };

  const handleSegmentSubmit = async (e) => {
    e.preventDefault();
    try {
      await api.post(`/reservations/${selectedReservation.id}/segments`, {
        ...segmentForm,
        departureTime: new Date(segmentForm.departureTime).toISOString(),
        arrivalTime: new Date(segmentForm.arrivalTime).toISOString()
      });
      showSuccess("Segmento agregado correctamente");
      setIsSegmentModalOpen(false);
      setSegmentForm({
        airlineCode: "",
        flightNumber: "",
        origin: "",
        destination: "",
        departureTime: "",
        arrivalTime: "",
        status: "HK"
      });
      loadReservationDetail(selectedReservation.id); // Reload details
    } catch (error) {
      console.error(error);
      showError("Error al agregar segmento");
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <div>
          <h2 className="text-2xl font-semibold">Reservas</h2>
          <p className="text-sm text-muted-foreground">Gestión de expedientes y servicios.</p>
        </div>
        <Button onClick={() => setIsCreateModalOpen(true)}>
          <Plus className="h-4 w-4 mr-2" /> Nueva Reserva
        </Button>
      </div>

      <div className="grid gap-6 lg:grid-cols-[1fr_1.5fr]">
        {/* List Column */}
        <div className="space-y-4 rounded-xl border bg-card p-4">
          <h3 className="font-semibold px-2">Listado de Reservas</h3>
          <div className="space-y-2">
            {reservations.map((res) => (
              <div
                key={res.id}
                onClick={() => loadReservationDetail(res.id)}
                className={`p-4 rounded-lg border cursor-pointer transition-colors ${selectedReservation?.id === res.id
                  ? "bg-primary/10 border-primary"
                  : "hover:bg-muted"
                  }`}
              >
                <div className="flex justify-between items-start mb-1">
                  <span className="font-bold text-sm">{res.referenceCode}</span>
                  <span className={`text-xs px-2 py-0.5 rounded-full ${res.status === 'Booked' || res.status === 'Confirmed' ? 'bg-green-100 text-green-700' : 'bg-gray-100 text-gray-700'
                    }`}>
                    {res.status}
                  </span>
                </div>
                <div className="text-sm text-muted-foreground mb-1">
                  {res.customer?.fullName}
                </div>
                <div className="text-xs text-muted-foreground flex justify-between">
                  <span>{new Date(res.createdAt).toLocaleDateString()}</span>
                  <span className="font-mono text-foreground">${res.totalAmount.toLocaleString()}</span>
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Detail Column */}
        <div className="space-y-6">
          {selectedReservation ? (
            <div className="space-y-6">
              {/* Header Panel */}
              <div className="rounded-xl border bg-card p-6">
                <div className="flex justify-between items-start mb-4">
                  <div>
                    <h3 className="text-xl font-bold">{selectedReservation.referenceCode}</h3>
                    <p className="text-muted-foreground">
                      Expediente: {selectedReservation.travelFile ? selectedReservation.travelFile.fileNumber : "N/A"}
                    </p>
                  </div>
                  <div className="text-right">
                    <div className="text-2xl font-bold text-primary">
                      ${selectedReservation.totalAmount.toLocaleString()}
                    </div>
                    <div className="text-sm text-muted-foreground">Total (ARS)</div>
                  </div>
                </div>

                <div className="grid grid-cols-2 gap-4 text-sm">
                  <div className="p-3 bg-muted rounded-lg">
                    <div className="font-medium mb-1">Cliente</div>
                    <div>{selectedReservation.customer?.fullName}</div>
                    <div className="text-muted-foreground text-xs">{selectedReservation.customer?.email}</div>
                  </div>
                  <div className="p-3 bg-muted rounded-lg">
                    <div className="font-medium mb-1">Fechas</div>
                    <div className="flex items-center gap-2">
                      <Calendar className="h-4 w-4" />
                      {new Date(selectedReservation.departureDate).toLocaleDateString()}
                    </div>
                  </div>
                </div>
              </div>

              {/* Itinerary Panel */}
              <div className="rounded-xl border bg-card p-6">
                <div className="flex justify-between items-center mb-4">
                  <h3 className="font-semibold flex items-center gap-2">
                    <Plane className="h-5 w-5" /> Itinerario de Vuelo
                  </h3>
                  <Button size="sm" onClick={() => setIsSegmentModalOpen(true)}>
                    <Plus className="h-4 w-4 mr-2" /> Agregar Tramo
                  </Button>
                </div>

                <div className="space-y-3">
                  {selectedReservation.segments && selectedReservation.segments.length > 0 ? (
                    selectedReservation.segments.map((seg) => (
                      <div key={seg.id} className="flex flex-col md:flex-row gap-4 p-4 border rounded-lg items-center">
                        <div className="flex items-center gap-4 flex-1">
                          <div className="h-10 w-10 flex items-center justify-center bg-blue-100 text-blue-700 rounded-full font-bold">
                            {seg.airlineCode}
                          </div>
                          <div>
                            <div className="font-bold">{seg.airlineCode} {seg.flightNumber}</div>
                            <div className="text-xs text-muted-foreground">Clase {seg.status}</div>
                          </div>
                        </div>

                        <div className="flex items-center gap-8 flex-[2]">
                          <div className="text-center">
                            <div className="text-2xl font-light">{seg.origin}</div>
                            <div className="text-xs text-muted-foreground">
                              {new Date(seg.departureTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                            </div>
                          </div>
                          <div className="flex-1 border-t-2 border-dashed border-gray-300 relative">
                            <Plane className="h-4 w-4 absolute -top-2.5 left-1/2 -translate-x-1/2 text-gray-400 rotate-90" />
                          </div>
                          <div className="text-center">
                            <div className="text-2xl font-light">{seg.destination}</div>
                            <div className="text-xs text-muted-foreground">
                              {new Date(seg.arrivalTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                            </div>
                          </div>
                        </div>

                        <div className="text-xs text-right min-w-[80px]">
                          <div>{new Date(seg.departureTime).toLocaleDateString()}</div>
                        </div>
                      </div>
                    ))
                  ) : (
                    <div className="text-center py-8 text-muted-foreground border-2 border-dashed rounded-lg">
                      No hay vuelos cargados.
                    </div>
                  )}
                </div>
              </div>
            </div>
          ) : (
            <div className="h-full flex items-center justify-center text-muted-foreground bg-card border rounded-xl p-10">
              Selecciona una reserva para ver el detalle.
            </div>
          )}
        </div>
      </div>

      {/* Create Reservation Modal */}
      {isCreateModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-background/80 backdrop-blur-sm">
          <div className="bg-card border shadow-lg rounded-lg p-6 w-full max-w-2xl max-h-[90vh] overflow-y-auto">
            <h3 className="text-lg font-semibold mb-4">Nueva Reserva Manual</h3>
            <form onSubmit={handleCreateSubmit} className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div className="col-span-2">
                  <label className="text-sm font-medium">Cliente</label>
                  <select
                    className="w-full p-2 rounded border bg-background"
                    value={createForm.customerId}
                    onChange={e => setCreateForm({ ...createForm, customerId: e.target.value })}
                    required
                  >
                    <option value="">Seleccione un cliente...</option>
                    {customers.map(c => (
                      <option key={c.id} value={c.id}>{c.fullName}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="text-sm font-medium">Código Referencia</label>
                  <input
                    className="w-full p-2 rounded border bg-background"
                    placeholder="REF-001"
                    value={createForm.referenceCode}
                    onChange={e => setCreateForm({ ...createForm, referenceCode: e.target.value })}
                    required
                  />
                </div>
                <div>
                  <label className="text-sm font-medium">Tipo</label>
                  <select
                    className="w-full p-2 rounded border bg-background"
                    value={createForm.productType}
                    onChange={e => setCreateForm({ ...createForm, productType: e.target.value })}
                  >
                    <option value="Flight">Aéreo</option>
                    <option value="Hotel">Hotel</option>
                    <option value="Package">Paquete</option>
                  </select>
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-medium">Fecha Salida</label>
                  <input
                    type="date"
                    className="w-full p-2 rounded border bg-background"
                    value={createForm.departureDate}
                    onChange={e => setCreateForm({ ...createForm, departureDate: e.target.value })}
                    required
                  />
                </div>
                <div>
                  <label className="text-sm font-medium">Fecha Regreso (Opcional)</label>
                  <input
                    type="date"
                    className="w-full p-2 rounded border bg-background"
                    value={createForm.returnDate}
                    onChange={e => setCreateForm({ ...createForm, returnDate: e.target.value })}
                  />
                </div>
              </div>

              <div className="grid grid-cols-3 gap-4">
                <div>
                  <label className="text-sm font-medium">Precio Base</label>
                  <input
                    type="number"
                    step="0.01"
                    className="w-full p-2 rounded border bg-background"
                    value={createForm.basePrice}
                    onChange={e => setCreateForm({ ...createForm, basePrice: e.target.value })}
                    required
                  />
                </div>
                <div>
                  <label className="text-sm font-medium">Comisión</label>
                  <input
                    type="number"
                    step="0.01"
                    className="w-full p-2 rounded border bg-background"
                    value={createForm.commission}
                    onChange={e => setCreateForm({ ...createForm, commission: e.target.value })}
                    required
                  />
                </div>
                <div>
                  <label className="text-sm font-medium">Total</label>
                  <input
                    type="number"
                    step="0.01"
                    className="w-full p-2 rounded border bg-background"
                    value={createForm.totalAmount}
                    onChange={e => setCreateForm({ ...createForm, totalAmount: e.target.value })}
                    required
                  />
                </div>
              </div>

              <div>
                <label className="text-sm font-medium">Proveedor (Aerolínea/Operador)</label>
                <input
                  className="w-full p-2 rounded border bg-background"
                  placeholder="Ej. Aerolineas Argentinas"
                  value={createForm.supplierName}
                  onChange={e => setCreateForm({ ...createForm, supplierName: e.target.value })}
                />
              </div>

              <div className="flex justify-end gap-2 mt-6">
                <Button type="button" variant="outline" onClick={() => setIsCreateModalOpen(false)}>Cancelar</Button>
                <Button type="submit">Crear Reserva</Button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Add Segment Modal */}
      {isSegmentModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-background/80 backdrop-blur-sm">
          <div className="bg-card border shadow-lg rounded-lg p-6 w-full max-w-lg">
            <h3 className="text-lg font-semibold mb-4">Agregar Tramo Aéreo</h3>
            <form onSubmit={handleSegmentSubmit} className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-medium">Aerolínea</label>
                  <input
                    className="w-full p-2 rounded border bg-background uppercase"
                    maxLength={3}
                    placeholder="AA"
                    value={segmentForm.airlineCode}
                    onChange={e => setSegmentForm({ ...segmentForm, airlineCode: e.target.value.toUpperCase() })}
                    required
                  />
                </div>
                <div>
                  <label className="text-sm font-medium">Número Vuelo</label>
                  <input
                    className="w-full p-2 rounded border bg-background"
                    placeholder="900"
                    value={segmentForm.flightNumber}
                    onChange={e => setSegmentForm({ ...segmentForm, flightNumber: e.target.value })}
                    required
                  />
                </div>
              </div>
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-medium">Origen</label>
                  <input
                    className="w-full p-2 rounded border bg-background uppercase"
                    maxLength={3}
                    placeholder="MIA"
                    value={segmentForm.origin}
                    onChange={e => setSegmentForm({ ...segmentForm, origin: e.target.value.toUpperCase() })}
                    required
                  />
                </div>
                <div>
                  <label className="text-sm font-medium">Destino</label>
                  <input
                    className="w-full p-2 rounded border bg-background uppercase"
                    maxLength={3}
                    placeholder="EZE"
                    value={segmentForm.destination}
                    onChange={e => setSegmentForm({ ...segmentForm, destination: e.target.value.toUpperCase() })}
                    required
                  />
                </div>
              </div>
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-medium">Salida</label>
                  <input
                    type="datetime-local"
                    className="w-full p-2 rounded border bg-background"
                    value={segmentForm.departureTime}
                    onChange={e => setSegmentForm({ ...segmentForm, departureTime: e.target.value })}
                    required
                  />
                </div>
                <div>
                  <label className="text-sm font-medium">Llegada</label>
                  <input
                    type="datetime-local"
                    className="w-full p-2 rounded border bg-background"
                    value={segmentForm.arrivalTime}
                    onChange={e => setSegmentForm({ ...segmentForm, arrivalTime: e.target.value })}
                    required
                  />
                </div>
              </div>
              <div className="flex justify-end gap-2 mt-6">
                <Button type="button" variant="outline" onClick={() => setIsSegmentModalOpen(false)}>Cancelar</Button>
                <Button type="submit">Agregar</Button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
