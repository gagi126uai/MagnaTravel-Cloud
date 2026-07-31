# 2026-07-31 — "Cada campo acepta solo lo suyo": la obra de validación de campos

*Explicación en criollo del día, para leer sin saber programar.*

## De dónde salió

De dos lados. Primero, los dos bugs ALTOS que quedaban de la prueba integral del 25/07:
la factura que a veces mostraba el texto genérico "Servicios Turísticos" en vez de sus
renglones, y el CUIT inventado que el sistema aceptaba sin chistar. Segundo, la orden de
Gastón al ver el arreglo del CUIT: *"esto tendrías que hacerlo con todos los campos: que
contengan la información que va con ese campo y no cualquier cosa"*.

## Los dos bugs viejos, cerrados

- **Factura sin renglones**: los renglones SIEMPRE se guardaron bien; el visor de PDF los
  leía mal y caía al texto genérico. Ese punto ya estaba corregido; el barrido de hoy
  confirmó que ningún otro lugar leía mal, y quedó un test de red que explota si alguien
  vuelve a romper esa lectura.
- **CUIT inválido**: el verificador (el "dígito que cierra") existía pero solo se usaba en
  clientes. Ahora frena en TODAS las puertas: operadores, el CUIT propio de la agencia,
  los datos de agencia de los recibos y el titular de las cuentas bancarias. Además, ya no
  se puede borrar por accidente el CUIT de la agencia si había uno cargado (sin él no se
  factura). Y tres pantallas que tapaban el motivo real con "No se pudo guardar" ahora
  muestran el cartel de verdad.

## La obra nueva (inventario primero: 32 campos aceptaban cualquier cosa)

**La receta única** (la del CUIT): una sola regla por tipo de dato, en el motor, aplicada
en todas las puertas de escritura, con mensaje en criollo que la pantalla muestra tal
cual. Datos viejos inválidos NO traban nada: solo se exige corregir si tocás ese campo.

**Tanda 1 — plata y ARCA:** mail con forma de mail; teléfono con forma de teléfono (para
que WhatsApp no falle en silencio); **CBU con los dos dígitos verificadores reales del
BCRA** (antes solo se contaban los 22 números — plata a cuenta equivocada es lo peor que
hay); punto de venta de ARCA en rango; porcentajes de comisión entre 0 y 100; condición
fiscal solo de la lista real. De paso se cazaron dos fugas viejas: el panel de ARCA
mostraba el nombre de una variable interna y errores en inglés.

**Tanda 2 — identidad y viaje:** DNI de 7 u 8 números (según el tipo de documento
elegido); fecha de nacimiento ni futura ni de hace 120 años; **pasaporte vencido = aviso,
no candado** (firmado: a veces cargás el pasajero antes de que renueve); las fechas de
viaje ya tenían su candado desde junio (verificado, no duplicado). Y la decisión de
Gastón sobre el bot de WhatsApp: **un lead jamás se rechaza** (rechazarlo es perder una
venta), pero un mail o teléfono basura ya no entra al campo — queda vacío y anotado en la
ficha para completar.

**Mini-tanda final:** al editar un pasajero, un campo vacío ya **no borra** lo guardado
(la misma regla que clientes) — esto mató un bug real: el formulario rápido borraba el
vencimiento del pasaporte en cada edición sin que nadie lo supiera. El candado fiscal (el
que pide autorización para tocar datos de un pasajero con voucher o factura emitida)
quedó fijado con tests para que ni se dispare de más ni se pueda esquivar. Y la ayuda del
campo documento ahora cambia según el tipo — con la perlita de que el CUIT de EJEMPLO que
mostraban las pantallas tenía el dígito mal y nuestro propio validador lo rechazaba.

## Cómo se trabajó

Cuatro tandas, cada una con su ciclo completo: implementación → tres reviewers (backend,
riesgo de datos, gate de exposición) → fixes de lo bloqueado → re-review → CI con
Postgres real → deploy. Los reviewers bloquearon tres veces y las tres tenían razón:
faltaba auditar cuánto toleraba el gate de resguardos, faltaban los tests del candado
fiscal, y había mensajes que nunca llegaban a la pantalla. Commits del día: `e8d6d992`
(CUIT+PDF) · `6a5b7138` (Tanda 1) · `923623e3` (Tanda 2) · `8014abf3` (mini-tanda).

## Qué puede probar Gastón (todo en producción)

1. Operador nuevo con CUIT `20-11111111-1` → rebota con "Ese CUIT no es válido".
2. Mail "asdasd", CBU inventado, comisión 999% → todos rebotan con su cartel.
3. Pasajero con DNI "12.345.678" → rebota pidiendo sin puntos; con pasaporte vencido →
   guarda y avisa.
4. Editar el teléfono de un operador viejo con datos raros → NO se traba.
5. Editar un pasajero por el formulario rápido → el vencimiento del pasaporte sobrevive.

## Deudas anotadas

- Tests del cuerpo HTTP (que ningún 400/409 traiga datos técnicos) — hoy se verifica a
  nivel motor.
- Clientes: unificar el código de respuesta (409 vs 400) para el mismo tipo de error.
- Aviso de pasaporte vencido: es un cartelito que se va solo; si Gastón lo quiere fijo en
  la ficha, pasa por diseño.
- Desplegable de tipo de documento en blanco al editar un pasajero legacy sin tipo.
- El validador de comisiones no cubre el % por vendedor (ya tenía su propio tope).
