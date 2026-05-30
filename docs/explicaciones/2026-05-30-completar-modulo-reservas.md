# Completar el módulo de Reservas — los 5 servicios parejos (2026-05-30)

> Para Gastón, explicado fácil. Esta sesión cerramos el módulo de reservas: que los **5 tipos de servicio** que vende la agencia se carguen completos y parejos. Antes solo el Hotel estaba 100%.

## ¿Qué problema teníamos?

Una agencia vende 5 cosas dentro de una reserva: **Hotel, Vuelo, Paquete, Traslado y Asistencia (el seguro)**. Solo el **Hotel** estaba terminado. Los otros estaban a medias y la Asistencia ni existía. Era como tener una caja registradora linda (la facturación) pero el depósito a medio llenar.

Lo hicimos en **5 bloques**, de lo más urgente/seguro a lo más grande.

## Bloque 1 — Seguridad 🔒 (commit `06d3562`)

Dos cosas:
1. **Un bug**: los vuelos, paquetes y traslados **solo los podía cargar un Admin**. Un vendedor común no. Lo arreglamos: ahora los carga como al hotel.
2. **Una filtración**: el **costo** (lo que te cuesta a vos, no lo que le cobrás al cliente) se le mostraba a cualquiera que estuviera logueado, incluso de reservas que no eran suyas. Lo tapamos en todas las pantallas de los 4 servicios.

## Bloque 2 — Campos completos ✈️📦🚐 (commit `d206a13`)

Le agregamos a Vuelo, Paquete y Traslado todos los datos que faltaban (número de vuelo, localizador, aerolínea, tipo de vehículo, qué incluye el paquete, etc.). De paso aparecieron **2 bugs viejos**:
- El **número de ticket y de confirmación del vuelo no se guardaban** al crear (se cargaban y se perdían).
- La **hora del vuelo se guardaba corrida** por zona horaria (un vuelo a las 14:30 quedaba a otra hora). Eso es importante porque esa hora va en el voucher del pasajero. Ahora se guarda tal cual se carga.

## Bloque 3 — Asistencia (el seguro) 🩺 (commit `56459a6`)

Construimos el servicio de **Asistencia desde cero**: una pantalla propia con compañía, número de póliza, tipo de plan, cobertura, zona, vigencia (desde/hasta), pasajeros, precio. Y lo más importante y delicado: **lo enchufamos en los ~20 lugares del sistema donde se suma plata o se arman cálculos**, para que no descuadre el saldo del cliente.

### El bug que casi se escapa (y por qué hicimos tests primero)

Al revisar, encontramos que en **2 lugares** el saldo se calculaba con su propia copia que se olvidaba de la asistencia:
- **Al registrar un pago** (¡pasa todo el tiempo!): el saldo se recalculaba sin el seguro → el cliente "debía de menos" **en silencio, sin ningún error**.
- **Al facturar o anular**: lo mismo.

Esto es lo peligroso: no rompe nada visible, solo descuadra plata. Lo cazamos porque escribimos los **tests del saldo primero** y porque pasó por una **revisión crítica**. Quedó arreglado y testeado antes de llegar a producción.

## Bloque 4 — La pantalla de Asistencia 🖥️ (commit `7505f95`)

El formulario para cargar la asistencia en la reserva, que aparezca en la lista, y que se puedan **marcar qué pasajeros cubre la póliza**. El revisor verificó campo por campo que la pantalla engancha bien con lo que espera el backend.

## Bloque 5 — Integrar al saldo/voucher/fechas 🧮

Esto se hizo **junto con el bloque 3** (era la parte de "enchufar en todos lados").

## Decisiones tuyas que quedaron grabadas
- La **comisión** (lo que ganás) no se muestra en el formulario (igual que en hoteles).
- La **asistencia suma** a lo que el cliente te tiene que pagar.
- Los **pasajeros cubiertos se marcan** uno por uno (como asignar pasajeros a una habitación).

## Lo que te toca a vos ahora (deploy)

En el servidor: `git pull` + `bash scripts/ops/deploy.sh`. Eso aplica las migraciones nuevas (solo **agregan** tablas/columnas, no borran nada).

**Probá después de deployar:**
- Que un **vendedor (no admin)** pueda cargar un vuelo, un paquete, un traslado y una asistencia.
- Que las pantallas de servicios no te tiren "no autorizado" donde no debería.
- Cargá una **asistencia** de prueba y fijate que el saldo de la reserva la sume bien.

> Nota: si algún vuelo viejo tenía la hora cargada, pudo haber quedado corrida (del bug anterior); los nuevos ya salen bien.

## Deudas anotadas (para más adelante, no urgente)
- Unificar las 3 copias del cálculo de saldo en una sola (para que no vuelva a pasar lo del descuadre).
- Candado de "dueño" en el botón de cambiar estado de un servicio (hoy pide permiso pero no chequea que sea tu reserva).

## Qué sigue
Reservas quedó terminado. Lo próximo es **"el resto"** que mencionaste — lo definimos juntos (las zonas más flojas suelen ser tarifario, operadores/proveedores y clientes).
