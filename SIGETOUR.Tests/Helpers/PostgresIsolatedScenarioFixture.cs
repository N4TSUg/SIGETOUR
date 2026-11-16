using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SIGETOUR.API.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Helpers
{
    /// <summary>
    /// Fixture reutilizable que aprovisiona un espacio de base de datos exclusivo por escenario mediante
    /// un esquema PostgreSQL dedicado (sigetour_iso_{guid}).
    /// Garantiza:
    /// 1. Aislamiento físico de tablas, índices y datos por escenario (el worker solo ve los datos del escenario).
    /// 2. Propiedad demostrable mediante metadata en el catálogo del sistema PostgreSQL (COMMENT ON SCHEMA).
    /// 3. Prevención estricta de eliminación de esquemas compartidos o del sistema (public, information_schema, pg_*).
    /// 4. Destrucción segura en cascada del esquema exclusivo sin requerir borrados por prefijo ni TRUNCATE global.
    /// 5. Propagación fiel de fallos de prueba combinados con eventuales fallos de limpieza.
    /// </summary>
    public sealed class PostgresIsolatedScenarioFixture : IAsyncDisposable
    {
        private readonly string _baseConnectionString;
        private readonly string _schemaName;
        private readonly string _isolatedConnectionString;
        private readonly Guid _instanceId;
        private readonly ITestOutputHelper? _output;
        private bool _isDisposed;
        private bool _isCreatedByThisInstance;

        public string SchemaName => _schemaName;
        public string ConnectionString => _isolatedConnectionString;
        public Guid InstanceId => _instanceId;

        private PostgresIsolatedScenarioFixture(
            string baseConnectionString,
            string schemaName,
            string isolatedConnectionString,
            Guid instanceId,
            ITestOutputHelper? output)
        {
            _baseConnectionString = baseConnectionString;
            _schemaName = schemaName;
            _isolatedConnectionString = isolatedConnectionString;
            _instanceId = instanceId;
            _output = output;
            _isCreatedByThisInstance = true;
        }

        /// <summary>
        /// Crea una nueva instancia de DbContext conectada exclusivamente al esquema aislado del escenario.
        /// </summary>
        public AppDbContext CreateDbContext()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(PostgresIsolatedScenarioFixture), "El espacio de prueba aislado ya fue liberado.");
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_isolatedConnectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .Options;

            return new AppDbContext(options);
        }

        /// <summary>
        /// Aprovisiona el esquema aislado en PostgreSQL, registra la metadata de propiedad y crea las tablas del modelo.
        /// Si no existe configuración de PostgreSQL de pruebas, emite SKIP explícito mediante PostgresTestHelper.
        /// </summary>
        public static async Task<PostgresIsolatedScenarioFixture?> CreateAsync(
            ITestOutputHelper? output = null,
            string? explicitConnectionString = null)
        {
            // 1. Validar entorno mediante PostgresTestHelper (emite Skip.If si no hay conexión configurada)
            var baseConn = explicitConnectionString ?? PostgresTestHelper.GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                output?.WriteLine("[OMITIDA] Prueba PostgreSQL omitida: variable de entorno TEST_POSTGRES_CONNECTION no configurada.");
                Skip.If(true, "Prueba PostgreSQL omitida: variable de entorno TEST_POSTGRES_CONNECTION no configurada.");
                return null;
            }

            // 2. Comprobaciones de seguridad obligatorias contra bases operativas
            PostgresTestHelper.EnsureSafeTestDatabase(baseConn);

            var instanceId = Guid.NewGuid();
            var schemaName = $"sigetour_iso_{instanceId:N}";

            // 3. Cadena de conexión con SearchPath restringido estrictamente al esquema del escenario
            var isolatedBuilder = new NpgsqlConnectionStringBuilder(baseConn)
            {
                SearchPath = schemaName
            };
            var isolatedConn = isolatedBuilder.ConnectionString;

            // 4. Crear el esquema físico y estampar comentario de propiedad en el catálogo pg_namespace
            try
            {
                await using var conn = new NpgsqlConnection(baseConn);
                await conn.OpenAsync();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    CREATE SCHEMA ""{schemaName}"";
                    COMMENT ON SCHEMA ""{schemaName}"" IS 'sigetour_fixture_instance:{instanceId:D}';
                ";
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                output?.WriteLine($"[ERROR INICIALIZACIÓN FIXTURE] No se pudo crear el esquema '{schemaName}': {ex.Message}");
                throw;
            }

            // 5. Inicializar el esquema creando las tablas del modelo EF Core dentro del espacio aislado
            var fixture = new PostgresIsolatedScenarioFixture(baseConn, schemaName, isolatedConn, instanceId, output);
            try
            {
                await using var initDb = fixture.CreateDbContext();
                var creator = (RelationalDatabaseCreator)initDb.Database.GetService<IDatabaseCreator>();
                await creator.CreateTablesAsync();

                // 6. Verificar que la conexión apunta inequívocamente al esquema recién creado
                await using var cmdCheck = initDb.Database.GetDbConnection().CreateCommand();
                await initDb.Database.OpenConnectionAsync();
                cmdCheck.CommandText = "SELECT current_schema();";
                var currentSchema = (string?)await cmdCheck.ExecuteScalarAsync();
                if (!string.Equals(currentSchema, schemaName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"[AISLAMIENTO CRÍTICO] La conexión no apuntó al esquema aislado '{schemaName}', sino a '{currentSchema}'. Se aborta por riesgo de contaminación.");
                }
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }

            return fixture;
        }

        /// <summary>
        /// Libera el espacio aislado verificando la propiedad criptográfica/token en el catálogo de PostgreSQL.
        /// </summary>
        public async Task DisposeAsync(Exception? testException = null)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (!_isCreatedByThisInstance || string.IsNullOrWhiteSpace(_schemaName))
            {
                return;
            }

            var name = _schemaName.Trim();

            // Validación estricta contra nombres protegidos
            if (name.Equals("public", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("information_schema", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"[SEGURIDAD CRÍTICA] Intento de eliminar esquema protegido del sistema: '{name}'.");
            }

            try
            {
                await using var conn = new NpgsqlConnection(_baseConnectionString);
                await conn.OpenAsync();

                // Validar propiedad real consultando el comentario registrado en pg_namespace
                await using (var cmdCheck = conn.CreateCommand())
                {
                    cmdCheck.CommandText = @"
                        SELECT obj_description(oid, 'pg_namespace') 
                        FROM pg_namespace 
                        WHERE nspname = @nspname;
                    ";
                    cmdCheck.Parameters.AddWithValue("@nspname", name);
                    var comment = (string?)await cmdCheck.ExecuteScalarAsync();

                    var expectedComment = $"sigetour_fixture_instance:{_instanceId:D}";
                    if (comment == null || !string.Equals(comment, expectedComment, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"[SEGURIDAD] No se pudo demostrar la propiedad del esquema '{name}'. " +
                            $"Metadata esperada: '{expectedComment}', encontrada: '{comment ?? "(nula)"}'. Se aborta la eliminación.");
                    }
                }

                // Eliminar el esquema exclusivo y todos sus objetos contenidos
                await using (var cmdDrop = conn.CreateCommand())
                {
                    cmdDrop.CommandText = $@"DROP SCHEMA IF EXISTS ""{name}"" CASCADE;";
                    await cmdDrop.ExecuteNonQueryAsync();
                }
                _isCreatedByThisInstance = false;
            }
            catch (Exception cleanupEx)
            {
                _output?.WriteLine($"[ERROR DE LIMPIEZA DE FIXTURE] Error al eliminar esquema '{name}': {cleanupEx}");
                if (testException != null)
                {
                    throw new AggregateException("Fallo en la prueba y posterior fallo en la limpieza del espacio de prueba PostgreSQL.", testException, cleanupEx);
                }
                throw new InvalidOperationException($"Fallo en la limpieza del espacio de prueba '{name}': {cleanupEx.Message}", cleanupEx);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsync(null);
        }
    }
}
