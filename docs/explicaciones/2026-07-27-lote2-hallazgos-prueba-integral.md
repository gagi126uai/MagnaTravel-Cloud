# Lote 2 de arreglos de la prueba integral (2026-07-27)

> Explicado en fácil, para leer sin saber programar.

## Qué se arregló y por qué

La prueba integral del 25/07 dejó 20 hallazgos. El Lote 1 (los graves de plata
y facturas) ya salió. Este Lote 2 cierra el resto: pulido de textos, la pantalla
de alta de cliente nueva, la pestaña "Todas", el buscador por servicios y
varios arreglos de Caja y dashboard.

### Alta de cliente con UN casillero de documento (P1, firmado)

Antes había dos campos sueltos y confusos. Ahora hay un solo casillero:
elegís el tipo (CUIT / CUIL / DNI / Pasaporte / Otro), ponés el número, y la
lupita de AFIP aparece solo cuando el tipo lo permite. Detalles importantes:

- **La lupita ahora es honesta**: si buscás por DNI y elegís un resultado del
  padrón, lo que te devuelve AFIP es el CUIT/CUIL de esa persona — entonces el
  casillero pasa solo a "CUIT" y guarda el dato donde corresponde. Antes de
  este arreglo quedaba un "DNI" de 11 dígitos, que era mentira y podía terminar
  en una factura con identidad equivocada.
- **Editar un cliente ya no pisa nada**: si un cliente viejo tiene CUIT **y**
  DNI guardados, y vos le cambiás solo el teléfono, los dos documentos quedan
  intactos. El sistema manda al motor únicamente lo que tocaste.
- **El detector de repetidos revivió**: ahora que la pantalla manda el tipo de
  documento, el sistema avisa si ya existe un cliente con ese mismo documento
  — y lo detecta aunque uno esté escrito con guiones y el otro sin guiones.
- **CUIT inválido se bloquea** (regla firmada del Lote 1, sigue firme).

### Vencimiento de pasaporte siempre visible (P2, firmado)

En "Datos personales" del pasajero ahora está siempre el campo "Vencimiento
pasaporte" (opcional). Si el pasajero ya existía, se autocompleta con lo último
conocido. La alarma de pasaporte vencido ya existía en el motor; ahora hay
dónde cargar el dato.

### Pestaña "Todas" en Reservas (H20, firmado)

Primera pestaña, con el contador que suma todo. Lo que pediste para no tener
que adivinar en qué pestaña quedó una reserva.

### Buscador global también por servicios (H18, firmado)

Ahora podés buscar "Palace" y aparece la reserva que tiene ese hotel adentro,
aunque el nombre de la reserva no diga Palace. Vale para hoteles, vuelos,
traslados, paquetes, asistencias y excursiones. Un vendedor sigue viendo SOLO
sus reservas: el permiso se respeta también en esta búsqueda (verificado con
tests de permisos nuevos).

### Dashboard: plata por moneda de verdad (H16 + regla P-3)

Las tarjetas de Ventas, Cobros, Saldo Pendiente y Margen Bruto mostraban UN
número que mezclaba pesos y dólares. Ahora cada tarjeta muestra una línea por
moneda ($ y US$ por separado), con puntos de miles bien argentinos, y si un
saldo está a favor de los clientes lo dice en esa línea, no en general.
Para eso el motor ahora también calcula el margen bruto por moneda.

### Caja más clara (H14 + arreglo de texto)

- Cuando anulás un movimiento manual, el par (movimiento y contra-asiento)
  aparece con la etiqueta **"Anulado"** y los botones Editar/Anular apagados
  con el motivo a la vista.
- Los retiros de saldo a favor ahora quedan escritos en criollo en el Libro de
  Caja: "en efectivo", "por transferencia", "devuelto al operador". Antes
  quedaba grabada una palabra interna del sistema en inglés.

### Hora real de los cobros (H17, firmado)

En los extractos (cliente y operador) cada cobro muestra "Registrado: hh:mm"
con hora argentina — la hora real en que se cargó, para auditoría. La fecha de
negocio de arriba no cambia.

### Pulido de textos (H10, H11, H12, H19)

- "la fecha de nacimiento" (antes decía "el").
- Ya no aparece "Hotel Hotel Palace" cuando el nombre ya trae la palabra.
- Las validaciones de formularios salen en español (antes el navegador las
  mostraba en inglés).
- El aviso "cargá los nombres para elegir" sale solo en aéreos y traslados,
  que es donde importa.

## El caso del widget "Cobros Pendientes" (H15): probablemente nunca fue un bug

El hallazgo decía: "el widget muestra una reserva saldada como pendiente".
Se investigó en la base de PROD: esa reserva HOY debe plata de verdad (los
cobros de la prueba se anularon en tests posteriores). El día de la prueba
estaba sobre-cobrada y el widget viejo la mostraba igual — foto de un momento
raro. Igual se hizo el arreglo de fondo: ahora el widget y la ficha leen LA
MISMA columna del motor, así que no pueden volver a contradecirse.

## Cómo se trabajó (calidad)

- 2 implementadores (motor y pantallas) + 4 revisores distintos (funcional
  backend, funcional frontend, seguridad de datos, y el cazador de jerga
  técnica). Hubo 3 rondas: las reviews bloquearon 8 cosas en total (incluidas
  2 que habrían perdido datos de clientes al editar) y todas se arreglaron y
  re-revisaron antes de commitear.
- Suites completas verdes: motor 4278/4278, pantallas 2882/2882 + build.
- El test nuevo del buscador contra Postgres real corre en el CI (la única red
  que caza errores de traducción a SQL como el del hotfix anterior).

## Deuda anotada (no bloqueó, queda para próximos lotes)

- "Ventas personales" del dashboard del vendedor en realidad muestra las
  ventas de toda la agencia (viene de antes; el arreglo correcto es en el motor).
- Al editar un movimiento de Caja, el campo Categoría muestra una palabra
  interna ("ClientCreditWithdrawal") — de antes, mismo lugar que se limpió.
- Chips de estado en inglés ("InManagement", "Confirmed") en los dashboards.
- La frase "Retiro de saldo a favor ... devuelto al operador" se entiende pero
  se lee rara (es un ingreso, no un retiro).
- El guard de duplicados no corre al EDITAR un cliente (solo en el alta).
- Margen bruto por moneda: en Cobranzas quedan 2 tarjetas con el patrón viejo.
