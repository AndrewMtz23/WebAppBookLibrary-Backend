# Organización y progreso de lectura — fase 8

Cada cuenta activa (`user`, `librarian`, `admin`) administra únicamente sus lecturas. El servidor toma la identidad del JWT; el DTO no acepta `userId`. No modifica préstamos, favoritos, inventario ni permisos de archivos.

## Contrato

| Operación | Resultado |
|---|---|
| `GET /api/reading/my?status=reading&page=1&pageSize=20` | `items`, `page`, `pageSize`, `totalItems`, `counts` globales por estado |
| `GET /api/reading/my?bookId={id}` | Seguimiento propio de una ficha |
| `GET /api/reading/my/latest` | `{entry}`; `null` si no hay lectura activa disponible |
| `PUT /api/reading/my/books/{id}` | `ReadingResponse` confirmado |
| `DELETE /api/reading/my/books/{id}?expectedRevision={revision}` | 204; repetición idempotente mientras no exista una entrada nueva |

Crear: `{ "status":"reading", "progressMode":"percent", "progressPercent":37, "currentPage":null, "expectedRevision":null }`.
Actualizar usa la revisión devuelta, con formato `ObjectId:version`. Borrar y recrear genera otro ObjectId, por lo que una revisión antigua no afecta la nueva entrada. Paginación: página positiva, tamaño 1–50; orden `updatedAt,id` descendente. Los conteos y la página comparten snapshot.

Estados: `want_to_read`, `reading`, `finished`. Modos: `percent`, `page`. El modo páginas recibe `currentPage` y `progressPercent:null`; el servidor calcula el porcentaje truncado. El modo porcentaje recibe 0–100 y `currentPage:null`. Quiero leer normaliza a cero y limpia fechas; terminado normaliza a 100; llegar a 100 termina. Reiniciar y reabrir requieren `confirmReset:true`; reabrir requiere progreso menor de 100.

`pageCountSnapshot` conserva el total utilizado. `currentPageCount` y `pageCountChanged` permiten mostrar discrepancias. `adoptCurrentPageCount:true` adopta explícitamente el catálogo actual y valida la página contra él. Una lectura terminada exige declarar la nueva página final o reabrirla. Un cambio de total sin nueva actividad no mueve `lastProgressAt`. Guardar datos iguales tampoco modifica versión ni fechas.

Respuesta pública: identidad del libro, revisión, estado, progreso, páginas, fechas UTC, disponibilidad, título y portada. No incluye usuario ni URL privada. Libros ocultos/eliminados conservan historial con «Libro no disponible» y portada nula; no pueden actualizarse, pero sí quitarse. `latest` filtra estos libros antes de elegir la última actividad.

Errores Problem Details: 400 entrada inválida o confirmación requerida, 401 sesión/cuenta inválida, 404 libro no disponible, 409 revisión obsoleta. El cliente debe recargar explícitamente después de un 409; no reenviar automáticamente el borrador.

## Arranque y operación

Se requiere MongoDB con transacciones (replica set), como en circulación. `CreateIndexesAsync` crea antes de servir peticiones:

- `ux_reading_user_book`: único por usuario/libro.
- `ix_reading_status_updated`: usuario/estado/fecha/id.
- `ix_reading_updated`: usuario/fecha/id.
- `ix_reading_activity`: usuario/estado/actividad/id.

No hay backfill: las cuentas existentes empiezan sin seguimientos. Consultas enriquecidas por lote evitan N+1. Las mutaciones usan transacciones, guardas de referencia de usuario/libro y revisión esperada. Auditoría mínima `reading_saved`/`reading_removed` se escribe en la misma transacción mediante la fábrica existente; no incluye historial completo ni metadatos HTTP.

Rollback: revertir las ramas de aplicación y ocultar la navegación; conservar `ReadingEntries` y sus índices. No borrar ni transformar préstamos/favoritos. No hay despliegue ni migración contra datos reales en esta entrega.

## Verificación local

`BOOK_LIBRARY_TEST_MONGO_URI=mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa`

```text
dotnet test WebAppBookLibrary.sln -c Release
dotnet build WebAppBookLibrary.sln -c Release --no-restore
```

309 pruebas, cero fallos/omisiones; build sin errores/advertencias. `ReadingRulesTests`, `ReadingPersistenceTests` y `ReadingHttpTests` cubren reglas, concurrencia, recreación, aislamiento, libros ocultos, auditoría y tres roles. Revisión independiente: tres hallazgos corregidos y reproducidos antes de la corrección.

## Cierre para uso real (28/09/2026)

La eliminación definitiva de una cuenta limpia sus `ReadingEntries` dentro de la misma transacción que elimina usuario y favoritos. La prueba con guardado concurrente verifica cero entradas huérfanas y conservación del historial de otra cuenta. Suite actual: 310/310 sin omisiones.

`python scripts/measure-reading-qa.py` requiere pymongo y el host QA local. Rechaza bases sin el marcador aleatorio de QA; no admite URL remota. Genera datos sintéticos que se eliminan al limpiar la base del host. Restaura BSON e índices de Users/Books/ReadingEntries en otra base temporal y compara todos los documentos antes de eliminarla.

Medición local Windows/.NET8/Mongo8.0.28: 24 usuarios, 120 guardados sobre el mismo libro, sin errores; p50 35 ms, p95 486 ms, máximo 1023 ms. Con 3000 entradas adicionales y 90 % de libros ocultos: p95 primera página 36 ms, página 150 67 ms, última lectura 37 ms. Explain usa `ix_reading_updated`; página profunda examina 3000 claves y 20 documentos. No se elimina la guarda compartida ni se promete capacidad de producción con esta muestra. El tiempo de las peticiones incluye esperas/reintentos internos; el script no mide su número por separado.

La restauración comprobó 39 usuarios, 3017 libros, 3033 lecturas y los cinco índices de lectura (incluido `_id_`). Es una prueba del conjunto sintético y del procedimiento BSON; falta ensayar el servicio real de backups.

### Puertas de puesta en operación

| Puerta | Evidencia requerida | Responsable |
|---|---|---|
| Staging compatible | SHA API/cliente, índices creados y smoke de lectura/aislamiento | Responsable del despliegue |
| Backup real | Identificador/hora de snapshot, restauración aislada y cotejo de ReadingEntries/Users/Books/índices | Operador de base de datos |
| Rollback | Artefactos anterior/nuevo compatibles; conservar lecturas e inventario; conciliar escrituras posteriores al snapshot | Responsable del despliegue |
| Monitoreo | Collector recibiendo `booklibrary.http.request.duration`, rutas de lectura, 5xx/409/latencia y alerta probada | Operador de aplicación |
| Capacidad | Carga con volumen previsto, p95/errores y reintentos de transacción medidos en staging | Responsable técnico |

Criterio provisional de laboratorio: cero escrituras perdidas y p95 menor de un segundo en este escenario. La prueba lo cumple; el volumen y los umbrales de producción deben acordarse con datos reales de uso. No están configurados ni probados staging, alertas o backups externos por esta entrega.
