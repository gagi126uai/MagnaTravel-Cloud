# Informes por moneda y TC real en la devolución del operador (2026-08-20)

> Explicación nivel trainee de la tanda `ee97898c`. Qué estaba mal, qué se hizo
> y por qué, sin jerga innecesaria.

## El contexto: la obra "multimoneda" ya estaba casi toda hecha

El plan viejo (memoria del 01/07) decía que faltaba construir el tipo de cambio
real (ADR-011), el nodo fiscal `CanMisMonExt` y la anulación de reservas con
varias facturas (ADR-042). Al contrastar con el código real, todo eso ya estaba
construido en obras de julio y agosto. Lo que quedaba VIVO eran tres restos, y
esta tanda cierra los tres.

## Resto 1 — Informes que sumaban pesos con dólares

### El problema (ejemplo pelotudo)

Si un vendedor vendió un paquete de $ 1.000.000 y un hotel de US$ 500, el
ranking de vendedores mostraba "vendió 1.000.500". Eso es sumar peras con
manzanas: un número que no existe en ninguna moneda. La regla firmada del
producto (P-3 y la guía UX: "las monedas van SIEMPRE separadas, NUNCA se
suman") lo prohíbe, y la solapa de caja de Informes ya se había arreglado el
19/08 — pero Vendedores, Destinos e Interanual seguían mezclando.

### La solución

- **Backend** (`ReportService.cs`): los tres reportes ahora devuelven, además
  de los números viejos (que se conservan para no romper nada), listas nuevas
  "por moneda": venta, costo y margen, cada uno con su moneda. Se leen de la
  tabla `ReservaMoneyByCurrency`, la misma fuente que ya usa el tablero.
- **Frontend** (`AnalyticsPage.jsx` + `analyticsByCurrency.js`): si los datos
  traen UNA sola moneda, la pantalla se ve exactamente igual que siempre. Si
  hay dos, cada solapa muestra un bloque por moneda (con el cartelito $ / US$),
  cada ranking ordenado y escalado contra su propia moneda.

### El detalle fino: los conteos

"Cantidad de reservas" o "pasajeros" NO se pueden partir por moneda (una
reserva puede tener plata en las dos — contaría doble). Repetir el total en
cada bloque tampoco: quien suma los bloques leería el doble. Decisión: en los
bloques por moneda esos conteos no se muestran; con una sola moneda se ven
como siempre. El reviewer de frontend bloqueó justamente por esto y el fix
quedó blindado con tests.

## Resto 2 — Tres reportes que eran "solo Admin" a lo bruto

Vendedores, Destinos e Interanual exigían rol Admin duro, cuando la decisión
firmada R3 (spec del dashboard, 18/08) dice que Informes se maneja con el
permiso `reportes.view`. Al abrirlos hubo que agregar dos candados que Admin
tapaba:

1. **Costos y margen tapados sin `cobranzas.see_cost`** (F-14): los números de
   costo van en 0 y las listas por moneda van vacías. Ojo con la trampa del
   margen: si el margen se calculara como "venta menos costo tapado en 0",
   el margen filtraría la venta — se calcula solo con permiso.
2. **Recorte de cartera** (bloqueo del reviewer de backend): sin
   `reservas.view_all`, un vendedor ve SOLO su fila del ranking y SUS reservas
   en destinos/interanual. Es la invariante ya firmada del tablero: "el
   vendedor no ve los números de toda la agencia SIN EXCEPCIONES". Sin este
   filtro, cualquier vendedor con `reportes.view` suelto veía el ranking de
   sus compañeros por HTTP directo.

## Resto 3 — La devolución del operador con tipo de cambio "1" clavado

Cuando un operador devuelve plata (por una anulación), el sistema grababa
`ExchangeRateAtReceipt = 1` SIEMPRE, aunque la devolución fuera en dólares.
Ese número no es decorativo: `TreasuryFxAdjustmentEngine` (ADR-044) lo usa
para calcular la diferencia de cambio de tesorería. Con TC=1, ese cálculo
daba cualquier cosa para las devoluciones en dólares.

Ahora: pesos sigue siendo 1 (correcto, no hay conversión); moneda extranjera
busca el TC del día en la libreta histórica de ADR-011 (`ExchangeRateQuotes`,
la que llena el job diario — este camino NUNCA llama a internet en vivo). Si
la libreta no tiene dato, degrada a 1 SIN frenar la carga de la caja (falla
abierta al comportamiento de siempre) pero deja un `LogWarning` para que la
degradación sea auditable — recomendación del reviewer de seguridad.

## Reviews

4 reviews: backend, frontend, seguridad y data-exposure. Data-exposure y
seguridad aprobaron de entrada; backend y frontend bloquearon (recorte de
cartera y conteos duplicados), se corrigió y se re-aprobó solo lo bloqueado.

## Qué NO cambió

- La solapa de caja de Informes (ya estaba bien desde el 19/08).
- Con datos de una sola moneda, las tres solapas se ven idénticas a antes.
- Los campos viejos de la API siguen existiendo (compatibilidad).

## Deuda anotada (chica)

- Labels en inglés preexistentes en Informes ("files", "mrg", "Revenue",
  "Bookings") — para una limpieza de textos futura.
- `OperatorRefundReceived` todavía no guarda la FUENTE del TC (solo el
  número); queda para cuando el campo gane trazabilidad.
- En la ventana rara sin cotización cargada, el ajuste FX de un refund puntual
  sigue saliendo con TC degradado (igual que siempre, pero ahora con aviso en
  el log) — limitación conocida para comentar con el contador.
