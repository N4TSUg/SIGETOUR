using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Audit
{
    [Collection("PostgresDatabaseCollection")]
    public class HardeningMigrationAndCleanupTests
    {
        private readonly ITestOutputHelper _output;

        public HardeningMigrationAndCleanupTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string PriorMigrationName = "20261116203628_VerifyAndHardenElectronicReceiptCashTransactionForeignKey";
        private const string HardenedMigrationShortName = "HardenForeignKeyResolutionAndValidation";
        private const string FkConstraintName = "FK_ElectronicReceipts_CashTransactions_CashTransactionId";

        // =========================================================================
        // GRUPO 1: RESOLUCIÓN CORRECTA DE TABLAS Y VALIDACIÓN DE CLAVE FORÁNEA (PUNTOS 1 Y 2)
        // Ejecutan migraciones REALES mediante IMigrator / MigrateAsync()
        // =========================================================================

        [SkippableFact]
        public async Task Point1_TableResolution_WhenBothTablesInUsualSchema_MigratesAndValidatesForeignKey()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                // 1. Ejecutar la migración real completa
                await db.Database.MigrateAsync();

                // 2. Comprobar que la clave foránea existe en la tabla y esquema objetivo
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT c.convalidated, c.contype::text, c.confdeltype::text, ref_t.relname
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    JOIN pg_class ref_t ON ref_t.oid = c.confrelid
                    WHERE t.relname = 'ElectronicReceipts'
                      AND n.nspname = @schema
                      AND c.conname = @fkName;
                ";
                cmd.Parameters.AddWithValue("@schema", fixture.SchemaName);
                cmd.Parameters.AddWithValue("@fkName", FkConstraintName);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync(), "La clave foránea no fue creada en la tabla objetivo.");
                Assert.True(reader.GetBoolean(0), "La clave foránea debe tener convalidated = true.");
                Assert.Equal("f", reader.GetString(1));
                Assert.Equal("n", reader.GetString(2)); // ON DELETE SET NULL
                Assert.Equal("CashTransactions", reader.GetString(3));
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        [SkippableFact]
        public async Task Point1_TableResolution_WhenSearchPathHasEmptyFirstSchema_FindsTablesInNextSchemaAndMigrates()
        {
            PostgresIsolatedScenarioFixture? emptyPrefixFixture = null;
            PostgresIsolatedScenarioFixture? mainFixture = null;

            try
            {
                emptyPrefixFixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
                mainFixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
                if (emptyPrefixFixture == null || mainFixture == null) return;

                // 1. Migrar mainFixture hasta la migración previa para que las tablas existan físicamente en mainFixture
                await using (var dbInit = mainFixture.CreateDbContext())
                {
                    var migratorInit = dbInit.GetInfrastructure().GetRequiredService<IMigrator>();
                    await migratorInit.MigrateAsync(PriorMigrationName);

                    // Eliminar la FK temporalmente en mainFixture para probar su resolución y creación
                    await dbInit.Database.ExecuteSqlRawAsync($@"
                        ALTER TABLE ""ElectronicReceipts"" DROP CONSTRAINT IF EXISTS ""{FkConstraintName}"";
                    ");
                }

                // Sincronizar el historial de migraciones en emptyPrefixFixture para que EF Core evalúe
                // únicamente la migración correctiva bajo current_schema() = emptyPrefixFixture
                await using (var emptyConn = await PostgresTestHelper.OpenSafeConnectionAsync(emptyPrefixFixture.ConnectionString))
                {
                    await using var emptyCmd = emptyConn.CreateCommand();
                    emptyCmd.CommandText = $@"
                        CREATE TABLE ""{emptyPrefixFixture.SchemaName}"".""__EFMigrationsHistory"" (
                            ""MigrationId"" character varying(150) NOT NULL PRIMARY KEY,
                            ""ProductVersion"" character varying(32) NOT NULL
                        );
                        INSERT INTO ""{emptyPrefixFixture.SchemaName}"".""__EFMigrationsHistory""
                        SELECT * FROM ""{mainFixture.SchemaName}"".""__EFMigrationsHistory"";
                    ";
                    await emptyCmd.ExecuteNonQueryAsync();
                }

                // 2. Configurar search_path con un primer esquema vacío seguido del esquema con las tablas
                var dualSearchPath = $"{emptyPrefixFixture.SchemaName},{mainFixture.SchemaName}";
                var dualConnBuilder = new NpgsqlConnectionStringBuilder(mainFixture.ConnectionString)
                {
                    SearchPath = dualSearchPath
                };

                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(dualConnBuilder.ConnectionString)
                    .Options;

                await using var dbDual = new AppDbContext(options);
                var migrator = dbDual.GetInfrastructure().GetRequiredService<IMigrator>();

                // 3. Migrar usando search_path multi-esquema donde current_schema() es el esquema vacío
                await migrator.MigrateAsync();

                // 4. Verificar que to_regclass resolvió ElectronicReceipts en mainFixture y creó la FK validada
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(mainFixture.ConnectionString);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT c.convalidated, n.nspname
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE t.relname = 'ElectronicReceipts'
                      AND n.nspname = @schema
                      AND c.conname = @fkName;
                ";
                cmd.Parameters.AddWithValue("@schema", mainFixture.SchemaName);
                cmd.Parameters.AddWithValue("@fkName", FkConstraintName);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync(), "to_regclass debió encontrar la tabla en el segundo esquema de search_path.");
                Assert.True(reader.GetBoolean(0), "La restricción debe estar validada.");
                Assert.Equal(mainFixture.SchemaName, reader.GetString(1));
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, emptyPrefixFixture, mainFixture);
            }
        }

        [SkippableFact]
        public async Task Point1_TableResolution_WhenHomonymousConstraintExistsInAnotherSchema_CreatesInTargetSchemaWithoutInterference()
        {
            PostgresIsolatedScenarioFixture? otherSchemaFixture = null;
            PostgresIsolatedScenarioFixture? targetFixture = null;

            try
            {
                otherSchemaFixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
                targetFixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
                if (otherSchemaFixture == null || targetFixture == null) return;

                // En otherSchema, crear una tabla ficticia con una restricción del MISMO nombre
                await using (var otherConn = await PostgresTestHelper.OpenSafeConnectionAsync(otherSchemaFixture.ConnectionString))
                {
                    await using var otherCmd = otherConn.CreateCommand();
                    otherCmd.CommandText = $@"
                        CREATE TABLE ""{otherSchemaFixture.SchemaName}"".""DummyTable"" (id uuid PRIMARY KEY, note text);
                        ALTER TABLE ""{otherSchemaFixture.SchemaName}"".""DummyTable""
                        ADD CONSTRAINT ""{FkConstraintName}""
                        CHECK (note IS NOT NULL);
                    ";
                    await otherCmd.ExecuteNonQueryAsync();
                }

                // En targetFixture, ejecutar la migración real
                await using var db = targetFixture.CreateDbContext();
                await db.Database.MigrateAsync();

                // Verificar que la restricción en targetFixture es FK y no colisionó con la de otherSchema
                await using var targetConn = await PostgresTestHelper.OpenSafeConnectionAsync(targetFixture.ConnectionString);
                await using var cmd = targetConn.CreateCommand();
                cmd.CommandText = @"
                    SELECT c.contype::text, c.convalidated
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE t.relname = 'ElectronicReceipts'
                      AND n.nspname = @schema
                      AND c.conname = @fkName;
                ";
                cmd.Parameters.AddWithValue("@schema", targetFixture.SchemaName);
                cmd.Parameters.AddWithValue("@fkName", FkConstraintName);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync(), "La FK debió crearse en el esquema objetivo a pesar del nombre homónimo en otro esquema.");
                Assert.Equal("f", reader.GetString(0));
                Assert.True(reader.GetBoolean(1));
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, otherSchemaFixture, targetFixture);
            }
        }

        [SkippableFact]
        public async Task Point1_TableResolution_WhenRequiredTableMissing_FailsWithClearDiagnostic_AndDoesNotRecordMigration()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

                // 1. Llevar la base hasta la migración anterior a la clave foránea
                await migrator.MigrateAsync(PriorMigrationName);

                // 2. Eliminar deliberadamente la tabla referenciada CashTransactions para simular ausencia en search_path
                await db.Database.ExecuteSqlRawAsync(@"DROP TABLE ""CashTransactions"" CASCADE;");

                // 3. Ejecutar la migración real mediante IMigrator
                var ex = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
                Assert.Contains("CashTransactions", ex.Message);
                Assert.Contains("No se encontró la tabla", ex.Message);

                // 4. Comprobar que la migración fallida NO quedó registrada en __EFMigrationsHistory
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT count(*) FROM ""__EFMigrationsHistory"" WHERE ""MigrationId"" LIKE @name;";
                cmd.Parameters.AddWithValue("@name", $"%{HardenedMigrationShortName}%");
                var recordedCount = (long?)await cmd.ExecuteScalarAsync();
                Assert.Equal(0, recordedCount);
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        [SkippableFact]
        public async Task Point2_ForeignKey_WhenCorrectAndValidated_PreservesOriginalOidWithoutRecreating()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                // Migración completa
                await db.Database.MigrateAsync();

                // Obtener el OID de la restricción creada y validada
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString);
                await using var cmdOid = conn.CreateCommand();
                cmdOid.CommandText = @"
                    SELECT c.oid
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE t.relname = 'ElectronicReceipts'
                      AND n.nspname = @schema
                      AND c.conname = @fkName;
                ";
                cmdOid.Parameters.AddWithValue("@schema", fixture.SchemaName);
                cmdOid.Parameters.AddWithValue("@fkName", FkConstraintName);
                var originalOid = (uint?)await cmdOid.ExecuteScalarAsync();
                Assert.NotNull(originalOid);

                // Volver a aplicar db.Database.MigrateAsync()
                await db.Database.MigrateAsync();

                // Comprobar que el OID de la restricción sigue siendo exactamente el mismo
                var newOid = (uint?)await cmdOid.ExecuteScalarAsync();
                Assert.Equal(originalOid, newOid);
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        [SkippableFact]
        public async Task Point2_ForeignKey_WhenCorrectNotValidWithoutOrphans_ValidatesConstraintWithoutRecreating()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

                // 1. Migrar hasta la migración anterior
                await migrator.MigrateAsync(PriorMigrationName);

                // 2. Modificar la clave foránea para que sea NOT VALID
                await db.Database.ExecuteSqlRawAsync($@"
                    ALTER TABLE ""ElectronicReceipts"" DROP CONSTRAINT IF EXISTS ""{FkConstraintName}"";
                    ALTER TABLE ""ElectronicReceipts""
                    ADD CONSTRAINT ""{FkConstraintName}""
                    FOREIGN KEY (""CashTransactionId"")
                    REFERENCES ""CashTransactions"" (""Id"")
                    ON DELETE SET NULL NOT VALID;
                ");

                // Verificar que inicialmente convalidated es false y capturar OID
                uint initialOid;
                await using (var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT c.oid, c.convalidated
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE t.relname = 'ElectronicReceipts'
                          AND n.nspname = @schema
                          AND c.conname = @fkName;
                    ";
                    cmd.Parameters.AddWithValue("@schema", fixture.SchemaName);
                    cmd.Parameters.AddWithValue("@fkName", FkConstraintName);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());
                    initialOid = reader.GetFieldValue<uint>(0);
                    Assert.False(reader.GetBoolean(1), "La restricción debe comenzar como NOT VALID.");
                }

                // 3. Ejecutar la migración real correctiva
                await migrator.MigrateAsync();

                // 4. Verificar que convalidated pasó a true y el OID fue preservado
                await using (var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT c.oid, c.convalidated
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE t.relname = 'ElectronicReceipts'
                          AND n.nspname = @schema
                          AND c.conname = @fkName;
                    ";
                    cmd.Parameters.AddWithValue("@schema", fixture.SchemaName);
                    cmd.Parameters.AddWithValue("@fkName", FkConstraintName);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());
                    var postOid = reader.GetFieldValue<uint>(0);
                    Assert.Equal(initialOid, postOid);
                    Assert.True(reader.GetBoolean(1), "La migración debió validar la restricción (convalidated = true).");
                }
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        [SkippableFact]
        public async Task Point2_ForeignKey_WhenCorrectNotValidWithOrphans_Aborts_PreservesData_AndDoesNotRecordMigration()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

                // 1. Migrar hasta la migración previa
                await migrator.MigrateAsync(PriorMigrationName);

                // 2. Eliminar la restricción para poder insertar un registro huérfano histórico
                await db.Database.ExecuteSqlRawAsync($@"
                    ALTER TABLE ""ElectronicReceipts"" DROP CONSTRAINT IF EXISTS ""{FkConstraintName}"";
                ");

                // 3. Insertar el registro huérfano histórico (CashTransactionId no existe)
                var orphanReceiptId = Guid.NewGuid();
                var nonExistentCashTxId = Guid.NewGuid();

                var booking = new Booking
                {
                    Id = Guid.NewGuid(),
                    ReferenceCode = "BK-ORPH-NOTVALID-01",
                    CustomerName = "Orphan User",
                    CustomerDni = "12345678",
                    CustomerEmail = "orphan@example.com",
                    CustomerPhone = "999888777",
                    BookingDate = DateTime.UtcNow,
                    TravelDate = DateTime.UtcNow.AddDays(1),
                    ShiftName = "Mañana",
                    TotalPassengers = 1,
                    TotalAmount = 100m,
                    PaidAmount = 100m,
                    InvoiceType = "boleta",
                    Status = BookingStatus.Confirmed
                };
                db.Bookings.Add(booking);
                await db.SaveChangesAsync();

                var receipt = new ElectronicReceipt
                {
                    Id = orphanReceiptId,
                    BookingId = booking.Id,
                    ReceiptType = "Boleta",
                    Series = "ORP2",
                    Number = "00000002",
                    CustomerDocType = "1",
                    CustomerDocNumber = "12345678",
                    CustomerName = "Orphan User",
                    CustomerEmail = "orphan@example.com",
                    TotalAmount = 100m,
                    SubtotalAmount = 84.75m,
                    IgvAmount = 15.25m,
                    Status = "Pending",
                    CreatedAtUtc = DateTime.UtcNow,
                    CashTransactionId = nonExistentCashTxId
                };
                db.ElectronicReceipts.Add(receipt);
                await db.SaveChangesAsync();

                // 4. Agregar la restricción como NOT VALID (PostgreSQL lo permite en datos existentes con NOT VALID)
                await db.Database.ExecuteSqlRawAsync($@"
                    ALTER TABLE ""ElectronicReceipts""
                    ADD CONSTRAINT ""{FkConstraintName}""
                    FOREIGN KEY (""CashTransactionId"")
                    REFERENCES ""CashTransactions"" (""Id"")
                    ON DELETE SET NULL NOT VALID;
                ");

                // 5. Ejecutar la migración real: debe abortar por presencia de huérfanos antes de validar
                var ex = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
                Assert.Contains("huérfano", ex.Message);
                Assert.Contains("NOT VALID", ex.Message);

                // 5. Verificar que el comprobante NO fue eliminado ni su referencia anulada
                await using (var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString))
                {
                    await using var verifyCmd = conn.CreateCommand();
                    verifyCmd.CommandText = $@"SELECT ""CashTransactionId"" FROM ""ElectronicReceipts"" WHERE ""Id"" = '{orphanReceiptId}';";
                    var remainingCashTxId = (Guid?)await verifyCmd.ExecuteScalarAsync();
                    Assert.Equal(nonExistentCashTxId, remainingCashTxId);

                    // 6. Verificar que la migración NO fue registrada en el historial
                    await using var histCmd = conn.CreateCommand();
                    histCmd.CommandText = @"SELECT count(*) FROM ""__EFMigrationsHistory"" WHERE ""MigrationId"" LIKE @name;";
                    histCmd.Parameters.AddWithValue("@name", $"%{HardenedMigrationShortName}%");
                    var recordedCount = (long?)await histCmd.ExecuteScalarAsync();
                    Assert.Equal(0, recordedCount);
                }
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        [SkippableFact]
        public async Task Point2_ForeignKey_WhenIncompatibleConstraintExists_Aborts_AndDoesNotRecordMigration()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

                // 1. Migrar hasta la migración previa
                await migrator.MigrateAsync(PriorMigrationName);

                // 2. Colocar una restricción incompatible con ON DELETE CASCADE en vez de SET NULL
                await db.Database.ExecuteSqlRawAsync($@"
                    ALTER TABLE ""ElectronicReceipts"" DROP CONSTRAINT IF EXISTS ""{FkConstraintName}"";
                    ALTER TABLE ""ElectronicReceipts""
                    ADD CONSTRAINT ""{FkConstraintName}""
                    FOREIGN KEY (""CashTransactionId"")
                    REFERENCES ""CashTransactions"" (""Id"")
                    ON DELETE CASCADE;
                ");

                // 3. Ejecutar la migración real mediante IMigrator
                var ex = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
                Assert.Contains("definición incompatible", ex.Message);

                // 4. Verificar que la migración NO fue registrada
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT count(*) FROM ""__EFMigrationsHistory"" WHERE ""MigrationId"" LIKE @name;";
                cmd.Parameters.AddWithValue("@name", $"%{HardenedMigrationShortName}%");
                var recordedCount = (long?)await cmd.ExecuteScalarAsync();
                Assert.Equal(0, recordedCount);
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        [SkippableFact]
        public async Task Point2_ForeignKey_WhenConstraintMissingWithoutOrphans_CreatesSuccessfully()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

                // 1. Migrar hasta la migración previa
                await migrator.MigrateAsync(PriorMigrationName);

                // 2. Eliminar la clave foránea
                await db.Database.ExecuteSqlRawAsync($@"
                    ALTER TABLE ""ElectronicReceipts"" DROP CONSTRAINT IF EXISTS ""{FkConstraintName}"";
                ");

                // 3. Ejecutar la migración real
                await migrator.MigrateAsync();

                // 4. Comprobar que fue creada y validada
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT c.convalidated, c.contype::text, c.confdeltype::text
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE t.relname = 'ElectronicReceipts'
                      AND n.nspname = @schema
                      AND c.conname = @fkName;
                ";
                cmd.Parameters.AddWithValue("@schema", fixture.SchemaName);
                cmd.Parameters.AddWithValue("@fkName", FkConstraintName);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0), "La clave foránea debe crearse como validada.");
                Assert.Equal("f", reader.GetString(1));
                Assert.Equal("n", reader.GetString(2));
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        // =========================================================================
        // GRUPO 2: COMPATIBILIDAD CON RENDER Y EJECUCIÓN REPETIDA (PUNTO 3)
        // =========================================================================

        [SkippableFact]
        public async Task Point3_Migration_WhenUpgradingFromExistingDatabaseWithPriorMigration_CompletesCleanly()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

                // 1. Simular base de datos existente en Render migrada hasta 20261116203628
                await migrator.MigrateAsync(PriorMigrationName);

                // Verificar que la migración previa está registrada y la nueva está pendiente
                var pendingBefore = (await db.Database.GetPendingMigrationsAsync()).ToList();
                Assert.Contains(pendingBefore, m => m.Contains(HardenedMigrationShortName));

                // 2. Aplicar la actualización completa
                await migrator.MigrateAsync();

                // 3. Verificar que no quedan migraciones pendientes
                var pendingAfter = (await db.Database.GetPendingMigrationsAsync()).ToList();
                Assert.Empty(pendingAfter);

                // 4. Verificar que la clave foránea existe y está validada
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(fixture.ConnectionString);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT c.convalidated FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE t.relname = 'ElectronicReceipts' AND n.nspname = @schema AND c.conname = @fkName;
                ";
                cmd.Parameters.AddWithValue("@schema", fixture.SchemaName);
                cmd.Parameters.AddWithValue("@fkName", FkConstraintName);
                var isValid = (bool?)await cmd.ExecuteScalarAsync();
                Assert.True(isValid);
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        [SkippableFact]
        public async Task Point3_Migration_RepeatedExecution_ProducesZeroChangesAndZeroErrors()
        {
            var fixture = await PostgresIsolatedScenarioFixture.CreateEmptyAsync(_output);
            if (fixture == null) return;

            try
            {
                await using var db = fixture.CreateDbContext();

                // Primera ejecución
                await db.Database.MigrateAsync();

                // Segunda ejecución repetida
                await db.Database.MigrateAsync();

                // Tercera ejecución repetida
                await db.Database.MigrateAsync();

                var pendingCount = (await db.Database.GetPendingMigrationsAsync()).Count();
                Assert.Equal(0, pendingCount);
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture);
            }
        }

        // =========================================================================
        // GRUPO 3: PROTECCIÓN CENTRALIZADA DE LA BASE DE PRUEBAS (PUNTO 4)
        // Verificaciones unitarias de seguridad sin conectar a base operativa
        // =========================================================================

        [Fact]
        public void Point4_Safety_OperativeDatabaseName_IsRejectedBeforeConnection()
        {
            var forbiddenStrings = new[]
            {
                "Host=localhost;Database=sigetour_db;Username=postgres;Password=test",
                "Host=render.com;Database=production_db;Username=admin;Password=secret",
                "Host=remote.host;Database=sigetour_prod;Username=admin;Password=secret",
                "Host=localhost;Database=live_tour_db;Username=postgres;Password=secret",
                "Host=localhost;Database=operativa_db;Username=postgres;Password=secret"
            };

            foreach (var connStr in forbiddenStrings)
            {
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                    PostgresTestHelper.EnsureSafeTestDatabase(connStr));
            }
        }

        [Fact]
        public void Point4_Safety_ExplicitTestDatabaseName_PassesValidation()
        {
            var safeStrings = new[]
            {
                "Host=localhost;Database=tests_db_sigetour;Username=postgres;Password=test",
                "Host=localhost;Database=sigetour_test_db;Username=postgres;Password=test",
                "Host=127.0.0.1;Port=5432;Database=ci_test;Username=postgres;Password=test"
            };

            foreach (var connStr in safeStrings)
            {
                PostgresTestHelper.EnsureSafeTestDatabase(connStr);
            }
        }

        [Fact]
        public async Task Point4_Safety_AbsenceOfConfiguration_ProducesRecognizableSkip()
        {
            await Assert.ThrowsAsync<Xunit.SkipException>(async () =>
            {
                await PostgresTestHelper.RequirePostgresAsync(_output, explicitConnectionString: "");
            });
        }

        [Fact]
        public void Point4_Safety_SanitizeConnectionString_NeverExposesPassword()
        {
            var connStr = "Host=myhost.com;Port=5432;Database=tests_db_sigetour;Username=myuser;Password=SuperSecretPassword123#";
            var sanitized = PostgresTestHelper.SanitizeConnectionString(connStr);

            Assert.DoesNotContain("SuperSecretPassword123#", sanitized);
            Assert.DoesNotContain("myuser", sanitized);
            Assert.Equal("myhost.com:5432/tests_db_sigetour", sanitized);
        }

        // =========================================================================
        // GRUPO 4: LIMPIEZA SEGURA Y METADATA DE ESQUEMAS DE PRUEBA (PUNTO 5)
        // =========================================================================

        [SkippableFact]
        public async Task Point5_FixtureCleanup_WhenSecondFixtureCreationFails_FirstFixtureIsCleanedUpSafely()
        {
            var baseConn = PostgresTestHelper.GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            PostgresIsolatedScenarioFixture? fixture1 = null;
            PostgresIsolatedScenarioFixture? fixture2 = null;
            string? schema1 = null;

            try
            {
                fixture1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(fixture1);
                schema1 = fixture1.SchemaName;

                // Forzar fallo en la creación de fixture2 pasando cadena de conexión no alcanzable pero con nombre test
                var invalidConn = "Host=localhost;Port=5432;Database=non_existent_test_db_999;Username=postgres;Password=invalid";
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PostgresIsolatedScenarioFixture.CreateAsync(_output, explicitConnectionString: invalidConn));
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture1, fixture2);
            }

            // Verificar que el esquema 1 fue completamente eliminado
            await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @name;";
            cmd.Parameters.AddWithValue("@name", schema1!);
            var count = (long?)await cmd.ExecuteScalarAsync();
            Assert.Equal(0, count);
        }

        [SkippableFact]
        public async Task Point5_FixtureCleanup_WhenTestThrowsDuringSeedingOrAssertions_AllFixturesAreCleanedUpSafely()
        {
            var baseConn = PostgresTestHelper.GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            PostgresIsolatedScenarioFixture? f1 = null;
            PostgresIsolatedScenarioFixture? f2 = null;
            string? schema1 = null;
            string? schema2 = null;
            Exception? thrownTestEx = null;

            try
            {
                f1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(f1);
                schema1 = f1.SchemaName;

                f2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(f2);
                schema2 = f2.SchemaName;

                throw new InvalidOperationException("Fallo intencional durante aserción de negocio");
            }
            catch (Exception ex)
            {
                thrownTestEx = ex;
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(thrownTestEx, f1, f2);
            }

            Assert.NotNull(thrownTestEx);
            Assert.Equal("Fallo intencional durante aserción de negocio", thrownTestEx.Message);

            // Comprobar que AMBOS esquemas concretos fueron eliminados de PostgreSQL
            await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname IN (@s1, @s2);";
            cmd.Parameters.AddWithValue("@s1", schema1!);
            cmd.Parameters.AddWithValue("@s2", schema2!);
            var count = (long?)await cmd.ExecuteScalarAsync();
            Assert.Equal(0, count);
        }

        [SkippableFact]
        public async Task Point5_FixtureCleanup_WhenOneFixtureFailsToDispose_OtherFixtureIsStillCleanedUp_AndControlledRestorationCleansAll()
        {
            var baseConn = PostgresTestHelper.GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            PostgresIsolatedScenarioFixture? f1 = null;
            PostgresIsolatedScenarioFixture? f2 = null;
            string? schema1 = null;
            string? schema2 = null;

            f1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            Assert.NotNull(f1);
            schema1 = f1.SchemaName;

            f2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            Assert.NotNull(f2);
            schema2 = f2.SchemaName;

            try
            {
                // Alterar el comentario en f1 para forzar fallo por verificación de seguridad
                await f1.SetOwnershipCommentAsync("tampered_metadata");

                // DisposeAllAsync: f1 fallará pero f2 debe ser limpiado
                var cleanupAggEx = await Assert.ThrowsAsync<AggregateException>(() =>
                    PostgresIsolatedScenarioFixture.DisposeAllAsync(null, f1, f2));

                Assert.Contains(cleanupAggEx.InnerExceptions, e => e.Message.Contains("No se pudo demostrar la propiedad"));

                // Verificar que f2 fue eliminado a pesar del fallo en f1
                await using (var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @s2;";
                    cmd.Parameters.AddWithValue("@s2", schema2!);
                    var count2 = (long?)await cmd.ExecuteScalarAsync();
                    Assert.Equal(0, count2);
                }
            }
            finally
            {
                // Restauración controlada de la marca de propiedad para limpiar verificadamente sin DROP desprotegido
                if (f1 != null)
                {
                    await f1.RestoreOwnershipCommentAsync();
                    await f1.DisposeAsync();
                }

                // Verificar que f1 fue finalmente eliminado tras la restauración legítima
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @s1;";
                cmd.Parameters.AddWithValue("@s1", schema1!);
                var count1 = (long?)await cmd.ExecuteScalarAsync();
                Assert.Equal(0, count1);
            }
        }

        [SkippableFact]
        public async Task Point5_FixtureCleanup_PreservesOriginalTestException_AndCleanupExceptionTogether()
        {
            var baseConn = PostgresTestHelper.GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var f = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            Assert.NotNull(f);
            var schema = f.SchemaName;

            try
            {
                // Alterar metadata para forzar fallo de limpieza
                await f.SetOwnershipCommentAsync("tampered_comment");

                var originalTestEx = new InvalidOperationException("Excepción original de negocio en prueba");

                var aggEx = await Assert.ThrowsAsync<AggregateException>(() =>
                    PostgresIsolatedScenarioFixture.DisposeAllAsync(originalTestEx, f));

                Assert.Contains(aggEx.InnerExceptions, e => e == originalTestEx);
                Assert.Contains(aggEx.InnerExceptions, e => e.Message.Contains("No se pudo demostrar la propiedad"));
            }
            finally
            {
                await f.RestoreOwnershipCommentAsync();
                await f.DisposeAsync();

                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @s;";
                cmd.Parameters.AddWithValue("@s", schema);
                var count = (long?)await cmd.ExecuteScalarAsync();
                Assert.Equal(0, count);
            }
        }

        [SkippableFact]
        public async Task Point5_FixtureCleanup_RejectsDroppingForeignOrUnrelatedSchema()
        {
            var baseConn = PostgresTestHelper.GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            // Crear un esquema ajeno con tabla propia
            var foreignSchema = $"foreign_schema_{Guid.NewGuid():N}";
            await using (var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    CREATE SCHEMA ""{foreignSchema}"";
                    COMMENT ON SCHEMA ""{foreignSchema}"" IS 'unrelated_owner';
                    CREATE TABLE ""{foreignSchema}"".""ForeignTable"" (id int);
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            try
            {
                var f = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(f);
                await f.DisposeAsync();

                // Comprobar que foreignSchema sigue 100% intacto
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_tables WHERE schemaname = @fs;";
                cmd.Parameters.AddWithValue("@fs", foreignSchema);
                var tableCount = (long?)await cmd.ExecuteScalarAsync();
                Assert.Equal(1, tableCount);
            }
            finally
            {
                // Limpieza del esquema ajeno
                await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"DROP SCHEMA IF EXISTS ""{foreignSchema}"" CASCADE;";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        [SkippableFact]
        public async Task Point5_FixtureCleanup_LeavesZeroResidualSchemas_ForExactCreatedNames()
        {
            var baseConn = PostgresTestHelper.GetConfiguredConnectionString();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var f1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            var f2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            var f3 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            Assert.NotNull(f1);
            Assert.NotNull(f2);
            Assert.NotNull(f3);

            var s1 = f1.SchemaName;
            var s2 = f2.SchemaName;
            var s3 = f3.SchemaName;

            await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, f1, f2, f3);

            // Verificar específicamente que los 3 nombres exactos creados ya no existen
            await using var conn = await PostgresTestHelper.OpenSafeConnectionAsync(baseConn);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname IN (@s1, @s2, @s3);";
            cmd.Parameters.AddWithValue("@s1", s1);
            cmd.Parameters.AddWithValue("@s2", s2);
            cmd.Parameters.AddWithValue("@s3", s3);
            var count = (long?)await cmd.ExecuteScalarAsync();
            Assert.Equal(0, count);
        }
    }
}
