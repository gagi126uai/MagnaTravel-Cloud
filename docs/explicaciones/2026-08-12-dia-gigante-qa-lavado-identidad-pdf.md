# El día gigante: QA integral, lavado de cara, identidad del documento y el arranque del PDF (11-12/08/2026)

> Explicación nivel trainee de todo lo que pasó en la sesión del 11 y la
> madrugada del 12 de agosto. Ocho commits deployados y una obra en marcha.

## 1. La QA integral con el robot (y los 6 bugs)

Gaston pidió probar TODO el módulo de reservas en producción como un QA de
verdad. Usamos el robot de navegador (Puppeteer headless, arnés fuera del
repo) en rondas: reconocimiento, las 5 solapas de carga, validaciones rotas a
propósito, la pregunta ✨, y verificación final. Salieron 6 bugs que un humano
no veía:

1. **Habitaciones -1 se guardaba** → guard en el motor (las 5 altas, las 5
   ediciones Y la conversión de cotización, que era una puerta trasera).
2. **"250,50" se borraba solo** → los campos de plata eran `input type=number`
   (el navegador descarta la coma). Ahora hay UN componente `MoneyInput` que
   entiende formato argentino.
3. **Crear-nuevo comía palabras cortas** ("Hotel Robot QA" → "Hotel Robot").
4. **La frase precargaba fechas del pasado** (la IA elegía el año a criterio)
   → clamp local SOLO cuando el motor dudó del año.
5. **El chip "Vendido" sumaba presupuestos** → lista positiva: Confirmada,
   En viaje, Finalizada.
6. **El motivo de "Archivar" repetido en cada fila** → globito (con enmienda
   firmada de P-9/P-10: globito solo en listados de escritorio, en táctil
   escrito; y va en un ENVOLTORIO porque un botón deshabilitado no recibe el
   mouse).

Lección de robot: los tests del arnés de la migración fallaban con
`FormatException` porque `ExecuteSqlRaw` interpreta `{4}` de una regex como
placeholder — el SQL con llaves va por `NpgsqlCommand` directo.

## 2. El lavado de cara (piloto de Reservas completo)

Gaston dijo la frase que ordenó todo: "parece un trabajo de novato, no hay
una UX centrada". La auditoría le dio la razón con números: 6 colores
distintos para "el botón principal", 646 botones escritos a mano en 153
archivos, y el famoso "Perdida" más grande que "El cliente aceptó" (causa
raíz: un flex sin alineación vertical).

Se firmó un estándar visual ("mostrador de agencia": azul boleto único
#1D4ED8, listas compactas, secundarios como texto, el SELLO tipo pasaporte
para reservas muertas, "modo prueba" chiquito en vez de la franja naranja) y
una maqueta HTML, y se aplicó en 3 tandas: listado → cabecera de ficha →
ficha de carga (que además ganó modo oscuro completo). "Cancelar" de la ficha
de carga ahora dice "Descartar" para no chocar con el vocabulario del negocio.

## 3. La identidad del documento (chau "F")

"#F-2026-1067" era un resto de cuando la tabla se llamaba TravelFiles. Tras
investigar cómo lo hacen los ERP reales (Odoo y Tourplan: un solo registro,
el estado le pone el nombre), se firmó: **un número para siempre, sin letra**
("2026-1067"), y **la palabra la pone la etapa**: "Cotización / Presupuesto /
Reserva 2026-1067". Se renombraron los ~1067 históricos con una migración
idempotente, y de paso murieron dos bugs invisibles: el año se tomaba con
hora de Londres, y el contador de números no era atómico (dos ventas
simultáneas podían chocar).

## 4. Las fechas del viaje (spec lista, obra en cola)

La QA descubrió que las fechas de la reserva se estiran solas y en silencio
cuando cargás un servicio fuera de rango — y eso mueve vencimientos de plata
y traba estados. Investigación ERP + 3 decisiones firmadas: ventana calculada
de solo lectura desde servicios VIGENTES (anular deshace la estirada —
reemplaza ADR-019 R8), aviso suave cuando se mueve, campo "fecha prometida"
aparte, y recálculo total al deployar con rastro por fila. El ADR-053 pasó
por el desafío del arquitecto revisor (4 bloqueantes aplicados, entre ellos
un 4to escritor oculto en la conversión de cotizaciones y el predicado
canónico de "cancelado").

## 5. El PDF de presupuesto (obra en marcha)

Gaston quiere emitirle al cliente un PDF calcado de su ejemplo (BAYAHIBE):
banda con logo, destino grande, vuelos con dibujitos (directo, valijas, +1),
hoteles con estrellas y tarifa por persona, formas de pago, condiciones.
Decisiones firmadas: opciones A/B/C de hotel (alternativas que el cliente
elige), todo configurable UNA vez en Configuración (logo, colores, datos,
legajo EVT, bloques de condiciones con ayuda de la IA), el PDF es ESPEJO de
lo cargado (lo que no cargaste no se dibuja), y SIN la leyenda fiscal de la
RG 1415 (decisión del dueño con el riesgo avisado; palabra final del contador).

La Tanda 1 (backend) quedó construida y pusheada: el motor de opciones (un
grupo ambiguo no suma a la plata y no se puede aceptar sin resolver; resolver
borra los perdedores con rastro completo), los campos del espejo (horarios,
equipaje, estrellas), y toda la Configuración de agencia. La review de
seguridad cazó dos bloqueantes de oro: una venta que podía esconderse de los
totales y un borrado sin rastro identificable — ambos cerrados y re-aprobados.

## Método que quedó firme

- Maqueta firmada ANTES de programar; verificación visual final la hace
  Gaston en su navegador (sin capturas enviadas — feedback firmado).
- Subagentes con modelo mediano cuando el brief es quirúrgico (orden de
  Gaston, repetida tres veces y probada: rinde igual y cuesta una fracción);
  el modelo grande solo donde el criterio fino manda.
- Cada hallazgo de review se corrige y RE-verifica solo lo bloqueado.
- Todo lo decidido queda en la memoria del proyecto con fecha y firma.
