# 2026-08-18 (noche) — Cierre de la obra post-rollout: tandas 5, 6 y 7.2

> Nivel: trainee. Qué se hizo, por qué, y cómo verificarlo, sin jerga.

## De dónde veníamos

Gastón recorrió PROD después del rollout visual y encontró problemas reales.
La obra "arreglos post-rollout" los fue arreglando en tandas: las tandas 1-4 y
7.1 salieron a la mañana; esta sesión cerró las tres que faltaban.

## Tanda 5 — El pasajero se carga y edita EN LA LISTA (murió la ventana)

**Antes**: tocar "Editar" o "Agregar Pasajero" abría una ventana flotante
(`PassengerFormModal`) encima de la pantalla. La regla firmada P-5 dice que en
este sistema nada se abre en ventana — todo pasa en el lugar.

**Ahora**:
- "Editar" transforma la fila del pasajero en el formulario, ahí mismo.
  Los chips de aviso (pasaporte por vencer, DNI vencido, menor) se esconden
  mientras editás y vuelven al guardar o cancelar (decisión firmada P5=A).
- "Agregar Pasajero" abre el formulario al FINAL de la lista (P4=A).
- El formulario en línea ganó lo que solo tenía la ventana: la lupa de AFIP,
  el buscador de pasajeros de viajes anteriores (solo al crear — al editar ya
  sabés quién es), y "+ Más detalles" con nacimiento, vencimientos,
  nacionalidad, género, teléfono, email y notas.
- El archivo `PassengerFormModal.jsx` se borró del proyecto.

**Detalle técnico para el que toque esto después**: la lógica nueva de
armar el payload y decidir si "+ Más detalles" arranca abierto vive en
`pasajeroInlineFormLogic.js` (con tests propios). El formulario compartido
(`PasajeroInlineForm`) tiene una prop `conFuncionesCompletas`: sin ella se
comporta EXACTO como antes — los usos de red de seguridad en `ServiceList`
no cambiaron ni un pixel.

**Verificación**: reviews aprobados (suite completa 3748/3748 verde, payload
comparado campo por campo contra el modal viejo para no pisar datos al
editar), y recorrida REAL en PROD con navegador: alta al final de la lista,
histórico trayendo pasajeros de verdad, lupa y "+ Más detalles" en su lugar,
todo cancelado sin guardar nada. Lo único no ejercitado en PROD fue el flujo
"Editar" sin candado (no había ninguna reserva editable en ese momento) —
queda para el ojo de Gastón.

## Tanda 6 — Configuración: limpieza grande + solapa Agencia al molde

**Commit 1 (limpieza)**: `SettingsPage.jsx` tenía 600 líneas de código
MUERTO: solapas de usuarios/roles/comisiones/programación/auditoría que
ningún botón podía abrir (viven de verdad en el Hub de administración), una
"terminal del bot" con caracteres rotos que nadie usaba, y componentes sin
un solo uso. Se borró todo con grep exhaustivo símbolo por símbolo
(914 → 322 líneas). Cero cambio visible.

**Commit 2 (piel)**: la solapa Agencia era la única que quedaba con el
estilo viejo. Título y campos pasaron a la receta estándar del sistema
(la misma de los formularios ya migrados). Solo clases CSS.

**Enmienda firmada en el camino**: la guía visual decía "títulos peso 800",
pero TODO el sistema se construyó con 700. Gastón eligió dejar 700 y
corregir la guía (enmienda anotada en
`docs/ux/2026-08-16-guia-rollout-estandar-visual.md`).

## Tanda 7.2 — Los pagos deshechos ya no se cuelan por las rendijas

**El problema**: cuando se deshace un cobro (soft-delete: se marca como
borrado pero la fila queda, porque acá NADA importante se borra), sus
"hijas" — el recibo y las aplicaciones a facturas de operador — NO heredaban
ese filtro. Una consulta directa a recibos podía traer recibos de pagos que
ya no existen para el negocio.

**El arreglo**: filtro espejo en las dos hijas (`PaymentReceipt` y
`SupplierInvoicePaymentApplication`). Y dos excepciones deliberadas, con
`IgnoreQueryFilters()` explícito y comentado:

1. **La numeración de recibos**: es una secuencia GLOBAL y física. Si el
   contador dejara de ver los recibos de pagos deshechos, calcularía números
   que ya están ocupados en la base y chocaría para siempre contra el índice
   único. Este bug lo habría metido el plan tal como estaba escrito — lo cazó
   el implementador con el test antes del fix.
2. **La bandeja de reconciliación de NC parciales**: es una herramienta de
   auditoría fiscal; tiene que ver la verdad completa, incluso de pagos
   deshechos (si no, mostraría el estado viejo "Emitido" de un recibo que en
   realidad está anulado).

**Por qué esto no esconde plata**: un pago a operador NO se puede deshacer
mientras tenga aplicaciones vivas (candado preexistente verificado por el
reviewer de seguridad). O sea: el filtro solo esconde cosas que ya estaban
revertidas.

La migración generada tiene Up/Down vacíos (los filtros son metadata de EF,
no cambian el esquema); solo sincroniza el snapshot.

## Decisión nueva firmada hoy (pendiente de construir)

**Campanita**: cuando falla la resolución/emisión de un servicio con el
operador, además de verse en el lugar, va a caer una notificación en la
campanita. Solo errores — sin avisos de éxito. Alcance nuevo, se construye
en una tanda futura.

## Deuda anotada (no urgente)

- El desplegable "Moneda Base" de Configuración tiene color de borde pero le
  falta la clase `border` que lo dibuja (preexistente).
- Los DbSet "nietos" (reversas de aplicaciones, snapshot de recibos de la
  bandeja) tienen comentario de advertencia: consultarlos directo sin
  `IgnoreQueryFilters()` haría desaparecer filas enteras si el pago padre
  está deshecho. Hoy ningún código lo hace.
- Dropdown histórico sin `aria-selected` en las opciones (menor).
- InvoicingTab/FacturacionClienteTab sin chip "Anulada" (gap preexistente,
  necesita diseño).

## Commits de esta sesión

- `9d6b7ba1` tanda 5 (pasajero inline, chau modal) — CI verde, deployado.
- `b962dc26` tanda 6.1 (limpieza SettingsPage) — CI verde, deployado.
- `8380d7e0` tanda 6.2 (piel Agencia) — CI verde, deployado.
- `8fe250fd` tanda 7.2 (filtros EF espejo) — CI en curso al cierre de este doc.
