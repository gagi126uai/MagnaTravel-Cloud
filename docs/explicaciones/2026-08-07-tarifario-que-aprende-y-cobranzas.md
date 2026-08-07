# 2026-08-06/07 — El día que el Tarifario aprendió a aprender (y Cobranzas mostró a quién cobrarle)

Explicación nivel trainee de lo que se construyó y por qué, sin jerga.

## Los tres frentes que se cerraron

### 1. Las sesiones ya no se caen con cada deploy (cerrado con prueba real)

**El problema:** cada vez que se actualizaba el sistema, todos los usuarios quedaban
deslogueados. Doble causa: (a) todas las visitas compartían UNA sola identidad para el
límite de intentos, y la ráfaga de reconexión tras el reinicio lo agotaba; (b) cuando
varias pestañas renovaban la sesión a la vez, la defensa anti-robo de tokens las
confundía con un ataque y mataba la sesión entera.

**El arreglo:** el servidor ahora ve la dirección real de cada visita (con candado
anti-suplantación), el límite del refresco es más generoso y separado del login, y hay
una "ventana de gracia" de 15 segundos: si dos pestañas renuevan a la vez, ambas
reciben la MISMA sesión nueva — pero solo si vienen de la misma computadora y navegador
(un token robado usado desde otra máquina sigue siendo robo, con registro auditable).

**La prueba de fuego:** sesión abierta con 4 pestañas + dos deploys seguidos → siguió
viva. Antes, un solo deploy la mataba.

### 2. Tarifario: de "formulario de 20 campos" a "memoria de lo que vendiste"

**El problema de concepto:** la pantalla vieja esperaba que alguien cargara tarifas de
antemano (aerolínea, código IATA, clase, equipaje, vigencias...). Gastón cotiza a mano
cada vez — nadie iba a llenar eso jamás. La investigación de mercado confirmó que los
back-offices de agencias minoristas reales NO usan catálogo previo: el estándar es
precio libre por expediente + memoria del último precio (NetSuite, Dynamics, Trams).

**Lo nuevo:** el Tarifario es la lista de los productos que ya vendiste — un renglón
por operador con su último precio y fecha (en ámbar si está viejo), y link a la reserva
de donde salió. Se alimenta SOLO, de las ventas. Extras: alta a mano con fichita simple
(permiso "editar tarifario"), freno de repetidos server-side (te muestra los parecidos
ANTES de dejarte crear; descartar el cartel no crea nada), renombre que corrige el
grupo entero, y el formulario largo quedó como "Carga completa" para casos especiales.
Al cargar un servicio en una reserva, un renglón gris te recuerda: "Último precio: Ola
Mayorista · US$ 48 · 22/05/2026".

**Bug de fondo cazado:** los productos cargados por el formulario viejo nunca escribían
su nombre normalizado de búsqueda → eran invisibles para el buscador (por eso se
duplicaban). Arreglado con relleno retroactivo.

### 3. Cobranzas: las dos preguntas que Gastón se hace para cobrar

"Cobranza y Facturación" pasó a llamarse **Cobranzas** y ahora contesta exactamente lo
que él mira: **"¿Quién viaja pronto y me debe?"** (ordenada por fecha de salida, nada de
30/60/90 contable — con prepago puro el reloj es el viaje) y **"¿Cuánto me debe cada
cliente en total?"** (cruzando reservas). Pesos y dólares SIEMPRE separados. Las listas
son pasivas: la fila abre la ficha, la acción vive ahí.

**Concepto nuevo firmado:** "el saldo tiene que estar completo X días antes de la
salida" (Configuración, default 21, separado del aviso de 7 días). Pasada esa fecha, el
renglón se pinta rojo con el motivo — solo informa, no traba nada (el freno real "debe,
no viaja" ya existía).

**Demolición:** murieron 4 pantallas viejas de la época anterior, 2 ventanas flotantes
(incluida la última de facturar) y 3 hooks huérfanos. Los bookmarks viejos redirigen.

## Cómo se trabajó (el método)

- Spec FIRMADA antes de construir: 19 preguntas + 3 aclaraciones + 4 de permisos + 2 de
  detalle, todas contestadas por Gastón (varias con maquetas HTML interactivas).
- Investigación de mercado con fuentes (erp-systems-expert) ANTES de diseñar: evitó
  rehacer lo que ya estaba bien (el circuito reserva-céntrico ES el estándar) y enfocó
  la obra en lo que faltaba de verdad.
- 4 reviewers × 2 rondas (funcional, seguridad, exposición de datos, pantallas): 7
  bloqueantes cazados ANTES de producción, entre ellos: el descarte del cartel de
  repetidos creaba el repetido · renombrar partía el producto en dos · la FK nueva
  rompía "Empezar de cero" · el permiso de crear productos era de solo-lectura · el
  alta no era atómica.
- Lección de tests: la corrida "todo junto en un proceso" satura la máquina y da rojos
  ALEATORIOS por timeout (víctimas distintas cada vez) — no es señal de rotura; las
  compuertas reales del CI (unit/security/http separadas + integración aparte) son el
  contrato válido.

## Además, ese mismo día

- La "ayuda invisible del TC" quedó en PROD (facturar USD en práctica sin casillero,
  solapa Reportes "Facturas en dólares", techo del TC server-side con rastro).
- GitHub Actions tuvo un incidente mayor (webhooks caídos): los push no disparaban CI
  → se dispara a mano con `gh workflow run ci-cd.yml` mientras dure la resaca.

## Deudas anotadas (NO construidas, a propósito)

M-4 alias que aprende · M-5 solapa "Repetidos" (unir) · M-9 campanita de saldos
vencidos · recordatorios de cobro (P18/P19: ficha + WhatsApp, solo diseñado el hueco) ·
ámbar-por-viejo en el renglón gris de las fichas de servicio (falta el dato en ese
contrato) · casing mixto de tipo en el rename (Func-N10) · tarifas inactivas en la
colisión del rename (Sec-R3) · pantalla de settings: chequeo visual del campo 21 días.
