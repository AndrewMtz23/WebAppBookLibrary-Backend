# Categorías y géneros — fase 6

Implementación local del 22 de septiembre de 2026. La escritura nueva de libros requiere categorías existentes; antes de habilitar el editor nuevo sobre una base anterior, conciliar y migrar su vocabulario.

## Contrato

- `GET /api/categories`: público, categorías activas. `query`, `page=1`, `pageSize=20` (máximo 100).
- `GET /api/admin/categories`: solo admin; admite también `isActive` e incluye `bookCount`, contando libros activos, inactivos y referencias legacy.
- `POST /api/admin/categories`: `{ name, description? }`; devuelve `201` con categoría.
- `PUT /api/admin/categories/{id}`: `{ name, description?, version }`.
- `PATCH /api/admin/categories/{id}/status`: `{ isActive, version }`.
- `DELETE /api/admin/categories/{id}?version=N`: `204` solo sin referencias. Una categoría en uso se puede desactivar.

DTO: `id`, `name`, `slug`, `description`, `isActive`, `version`, `createdAt`, `updatedAt`; `bookCount` solo administrativo. La versión cambia con cada edición/estado. El slug permanece estable al renombrar.

Libros reciben `categoryIds` (1–8 IDs diferentes) y responden `categories: [{ id, name, slug, isActive }]`. `genres` sigue siendo una proyección de nombres para compatibilidad. Se admite `categoryId` como filtro; `genre` continúa resolviendo nombres anteriores mediante alias. Enviar nombres e IDs contradictorios produce rechazo. No se crean categorías desde nombres escritos en un libro.

`400`: formato/validación; `401/403`: sesión/permiso; `404`: categoría ausente; `409`: `category_duplicate`, `category_version_conflict`, `category_in_use`. Las escrituras de libros rechazan categorías inexistentes y producen `409` para inactivas nuevas o categorías que dejaron de estar disponibles. Una categoría inactiva ya asignada puede conservarse al editar ese libro.

## Invariantes

Nombre visible: 1–80 caracteres, espacios colapsados, sin controles. Descripción: hasta 500. La normalización ignora mayúsculas y acentos; conserva `ñ` distinta de `n`, soporta Unicode compuesto/descompuesto y caracteres suplementarios. Índices únicos de `NormalizedName`, `Slug` y `Aliases` impiden duplicados concurrentes. Alias anteriores permanecen reservados.

La asignación de categorías y el borrado usan transacciones MongoDB con escritura de `ReferenceVersion` sobre la categoría, evitando que un borrado deje referencias huérfanas. Requiere replica set, igual que las operaciones referenciales existentes. No se expone `ReferenceVersion` al cliente.

Se auditan creación, edición, cambio de estado y borrado mediante `category.*`, resultado y código de razón; no se registra la descripción escrita.

## Migración revisable

El modo `--categories` no carga `.env`; exige una variable de conexión y una base explícitas. Sin `--apply` solamente informa. Primera versión limitada a 10 000 libros: si se excede, informa anomalía y no trunca silenciosamente. Las búsquedas de compatibilidad y facetas aún leen proyecciones legacy en memoria; revisar esa estrategia antes de escalar el catálogo.

1. Inventariar sobre copia aislada y guardar snapshot de la base de destino. Detener escrituras durante la aplicación.
2. Ejecutar dry-run sin mapping para obtener etiquetas desconocidas. Revisar un JSON de equivalencias; no se adivinan errores ortográficos. Ejemplo de formato, no un mapeo aprobado de datos reales:

```json
{ "Terror": "Terror", "Ficción": "Ficción", "Ficccion": "Ficción" }
```

3. Proporcionar la conexión mediante la variable elegida (fuera del repositorio y de logs). Desde la raíz del backend:

```powershell
dotnet run --project WebAppBookLibrary.Migration --configuration Release -- --categories --database NOMBRE_EXPLICITO --connection-env BOOK_LIBRARY_MIGRATION_URI --mapping equivalencias-revisadas.json
```

4. Revisar `scanned`, `alreadyCurrent`, `transformable`, `equivalences` y `anomalies`. Corregir mapeo/documentos ambiguos antes de aplicar: cualquier anomalía del análisis bloquea todas las escrituras.
5. Con snapshot confirmado y escrituras pausadas, añadir `--apply --snapshot-confirmed --writes-paused`. Estos indicadores declaran condiciones operativas; la herramienta no crea el snapshot ni pausa servidores. Atlas necesita autorización específica; no fue migrado durante esta fase.
6. Repetir el mismo comando: `updated` debe ser cero; conciliar total de libros, IDs, nombres/facetas y clasificaciones de libros inactivos. Después habilitar la escritura por IDs.

Los campos legacy y metadatos ajenos se conservan. IDs deterministas y guardas sobre versión/campos permiten repetir. La aplicación no es una única transacción global: si falla a mitad, puede haber categorías o libros ya conciliados; detenerse, revisar el reporte/estado y repetir dry-run. No borrar categorías parciales sin comprobar referencias.

Códigos de salida: 0 sin anomalías; 1 anomalías; 2 argumentos/condiciones faltantes; 3 variable de conexión ausente; 4 ejecución interrumpida. El error no imprime la cadena de conexión.

Rollback: deshabilitar editor nuevo y volver a lectura/escritura compatible, manteniendo IDs y legacy. Restaurar snapshot únicamente durante un corte controlado y conciliando escrituras posteriores; no eliminar campos para simular reversión.

## Verificación reproducible

`CategoryTests` cubre normalización, validación, permisos, conflictos, alias, filtros, referencias y 16 carreras de asignación contra borrado/estado. `CategoryMigrationTests` cubre dry-run, aplicación repetida, preservación, etiquetas desconocidas y bloqueo de ambigüedades.

```powershell
$env:BOOK_LIBRARY_TEST_MONGO_URI = 'mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa'
dotnet test WebAppBookLibrary.Tests/WebAppBookLibrary.Tests.csproj --configuration Release
dotnet build WebAppBookLibrary.sln --configuration Release
```

Usar exclusivamente MongoDB local aislado para estas pruebas. El host `tools/BookLibrary.QaHost` crea su propia base `booklibrary_ui_test_<guid>`, migra sus datos ficticios y la borra al detenerse de forma normal.
