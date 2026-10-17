CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923052929_InitialCreate') THEN
    CREATE TABLE "TourPackages" (
        "Id" uuid NOT NULL,
        "Title" character varying(150) NOT NULL,
        "Subtitle" character varying(150) NOT NULL,
        "Schedule" character varying(100) NOT NULL,
        "Description" text NOT NULL,
        "Inclusions" character varying(500) NOT NULL,
        "BasePrice" numeric(18,2) NOT NULL DEFAULT 0.0,
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        CONSTRAINT "PK_TourPackages" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923052929_InitialCreate') THEN
    CREATE TABLE "TourImages" (
        "Id" uuid NOT NULL,
        "TourPackageId" uuid NOT NULL,
        "ImageUrl" text NOT NULL,
        "IsMain" boolean NOT NULL,
        CONSTRAINT "PK_TourImages" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_TourImages_TourPackages_TourPackageId" FOREIGN KEY ("TourPackageId") REFERENCES "TourPackages" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923052929_InitialCreate') THEN
    INSERT INTO "TourPackages" ("Id", "Description", "Inclusions", "IsActive", "Schedule", "Subtitle", "Title")
    VALUES ('11111111-1111-1111-1111-111111111111', 'Ventanillas de Otuzco (Cementerio de la Cultura Cajamarca con más de 3 mil años de antigüedad), Jardín de las Hortensias, Artesanía de Cajamarca, Fundo los Aples o ''Tres Molinos'', fábrica artesanal de quesos, mantequilla, manjar blanco, Rosquitas de manteca etc.', 'Movilidad + Guía de turismo', TRUE, '3:30pm – 6:30pm', 'Cementerio pre-inca', 'Ventanillas de Otuzco');
    INSERT INTO "TourPackages" ("Id", "Description", "Inclusions", "IsActive", "Schedule", "Subtitle", "Title")
    VALUES ('22222222-2222-2222-2222-222222222222', 'Ex - hacienda La Collpa, Laguna Artificial, Casa Hacienda, Capilla de la Virgen del Carmen, Establo Central, Llamado de Vacas por su nombre, degustación de Lácteos; visita a las Cascadas de Llacanora según temporada o talleres de cerámica utilitaria y decorativa.', 'Movilidad + Guía de turismo', TRUE, '3:30pm – 7pm', 'Llamado de Vacas por su nombre', 'Collpa');
    INSERT INTO "TourPackages" ("Id", "Description", "Inclusions", "IsActive", "Schedule", "Subtitle", "Title")
    VALUES ('33333333-3333-3333-3333-333333333333', 'El Castillo de Yanamarca es un moderno atractivo turístico con arquitectura estilo medieval en donde se puede apreciar, pinturas, esculturas de piedra y madera. Este lugar ofrece vistas panorámicas espectaculares del valle circundante y cuenta con un restaurante moderno, cafetería, áreas de juegos y espacios temáticos.', 'Movilidad + Guía de turismo', TRUE, 'Horario a coordinar - Am o Pm', 'Estilo medieval', 'Castillo de Yanamarca');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923052929_InitialCreate') THEN
    CREATE INDEX "IX_TourImages_TourPackageId" ON "TourImages" ("TourPackageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923052929_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260923052929_InitialCreate', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" DROP COLUMN "Inclusions";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" RENAME COLUMN "Schedule" TO "Duration";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD "Category" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD "ChildPrice" numeric(18,2) NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD "DefaultVehicleId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD "Difficulty" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD "MaxCapacity" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD "Modality" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD "RequiredAdvancePercentage" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "AspNetRoles" (
        "Id" text NOT NULL,
        "Name" character varying(256),
        "NormalizedName" character varying(256),
        "ConcurrencyStamp" text,
        CONSTRAINT "PK_AspNetRoles" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "AspNetUsers" (
        "Id" text NOT NULL,
        "FirstName" text NOT NULL,
        "LastName" text NOT NULL,
        "IsActive" boolean NOT NULL,
        "UserName" character varying(256),
        "NormalizedUserName" character varying(256),
        "Email" character varying(256),
        "NormalizedEmail" character varying(256),
        "EmailConfirmed" boolean NOT NULL,
        "PasswordHash" text,
        "SecurityStamp" text,
        "ConcurrencyStamp" text,
        "PhoneNumber" text,
        "PhoneNumberConfirmed" boolean NOT NULL,
        "TwoFactorEnabled" boolean NOT NULL,
        "LockoutEnd" timestamp with time zone,
        "LockoutEnabled" boolean NOT NULL,
        "AccessFailedCount" integer NOT NULL,
        CONSTRAINT "PK_AspNetUsers" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "BoardingPoints" (
        "Id" uuid NOT NULL,
        "TourPackageId" uuid NOT NULL,
        "Name" text NOT NULL,
        "Description" text NOT NULL,
        CONSTRAINT "PK_BoardingPoints" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_BoardingPoints_TourPackages_TourPackageId" FOREIGN KEY ("TourPackageId") REFERENCES "TourPackages" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "ItineraryStops" (
        "Id" uuid NOT NULL,
        "TourPackageId" uuid NOT NULL,
        "OrderIndex" integer NOT NULL,
        "Name" text NOT NULL,
        CONSTRAINT "PK_ItineraryStops" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_ItineraryStops_TourPackages_TourPackageId" FOREIGN KEY ("TourPackageId") REFERENCES "TourPackages" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "TourInclusions" (
        "Id" uuid NOT NULL,
        "TourPackageId" uuid NOT NULL,
        "Description" text NOT NULL,
        CONSTRAINT "PK_TourInclusions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_TourInclusions_TourPackages_TourPackageId" FOREIGN KEY ("TourPackageId") REFERENCES "TourPackages" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "TourShifts" (
        "Id" uuid NOT NULL,
        "TourPackageId" uuid NOT NULL,
        "ShiftName" text NOT NULL,
        "StartTime" interval NOT NULL,
        "EndTime" interval NOT NULL,
        "IsActive" boolean NOT NULL,
        CONSTRAINT "PK_TourShifts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_TourShifts_TourPackages_TourPackageId" FOREIGN KEY ("TourPackageId") REFERENCES "TourPackages" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "Vehicles" (
        "Id" uuid NOT NULL,
        "Model" text NOT NULL,
        "LicensePlate" text NOT NULL,
        "SeatCapacity" integer NOT NULL,
        "IsActive" boolean NOT NULL,
        CONSTRAINT "PK_Vehicles" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "AspNetRoleClaims" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "RoleId" text NOT NULL,
        "ClaimType" text,
        "ClaimValue" text,
        CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "AspNetUserClaims" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "UserId" text NOT NULL,
        "ClaimType" text,
        "ClaimValue" text,
        CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "AspNetUserLogins" (
        "LoginProvider" text NOT NULL,
        "ProviderKey" text NOT NULL,
        "ProviderDisplayName" text,
        "UserId" text NOT NULL,
        CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey"),
        CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "AspNetUserRoles" (
        "UserId" text NOT NULL,
        "RoleId" text NOT NULL,
        CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId"),
        CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE TABLE "AspNetUserTokens" (
        "UserId" text NOT NULL,
        "LoginProvider" text NOT NULL,
        "Name" text NOT NULL,
        "Value" text,
        CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name"),
        CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    UPDATE "TourPackages" SET "BasePrice" = 30.0, "Category" = 1, "ChildPrice" = 20.0, "DefaultVehicleId" = NULL, "Difficulty" = 1, "Duration" = '4 Horas', "MaxCapacity" = 19, "Modality" = 1, "RequiredAdvancePercentage" = 50
    WHERE "Id" = '11111111-1111-1111-1111-111111111111';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    UPDATE "TourPackages" SET "BasePrice" = 35.0, "Category" = 4, "ChildPrice" = 25.0, "DefaultVehicleId" = NULL, "Difficulty" = 1, "Duration" = '4 Horas', "MaxCapacity" = 19, "Modality" = 1, "RequiredAdvancePercentage" = 50
    WHERE "Id" = '22222222-2222-2222-2222-222222222222';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    UPDATE "TourPackages" SET "BasePrice" = 40.0, "Category" = 5, "ChildPrice" = 30.0, "DefaultVehicleId" = NULL, "Description" = 'El Castillo de Yanamarca es un moderno atractivo turístico con arquitectura estilo medieval en donde se puede apreciar, pinturas, esculturas de piedra y madera.', "Difficulty" = 1, "Duration" = '4 Horas', "MaxCapacity" = 15, "Modality" = 1, "RequiredAdvancePercentage" = 50
    WHERE "Id" = '33333333-3333-3333-3333-333333333333';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_TourPackages_DefaultVehicleId" ON "TourPackages" ("DefaultVehicleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE UNIQUE INDEX "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE UNIQUE INDEX "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_BoardingPoints_TourPackageId" ON "BoardingPoints" ("TourPackageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_ItineraryStops_TourPackageId" ON "ItineraryStops" ("TourPackageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_TourInclusions_TourPackageId" ON "TourInclusions" ("TourPackageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    CREATE INDEX "IX_TourShifts_TourPackageId" ON "TourShifts" ("TourPackageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    ALTER TABLE "TourPackages" ADD CONSTRAINT "FK_TourPackages_Vehicles_DefaultVehicleId" FOREIGN KEY ("DefaultVehicleId") REFERENCES "Vehicles" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923055417_AddIdentityAndTourEntities') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260923055417_AddIdentityAndTourEntities', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923073005_AddBookings') THEN
    CREATE TABLE "Bookings" (
        "Id" uuid NOT NULL,
        "ReferenceCode" text NOT NULL,
        "CustomerName" text NOT NULL,
        "CustomerEmail" text NOT NULL,
        "CustomerPhone" text NOT NULL,
        "BookingDate" timestamp with time zone NOT NULL,
        "TravelDate" timestamp with time zone NOT NULL,
        "TotalPassengers" integer NOT NULL,
        "TotalAmount" numeric NOT NULL,
        "Status" integer NOT NULL,
        CONSTRAINT "PK_Bookings" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923073005_AddBookings') THEN
    CREATE TABLE "BookingItems" (
        "Id" uuid NOT NULL,
        "BookingId" uuid NOT NULL,
        "TourPackageId" uuid NOT NULL,
        "Passengers" integer NOT NULL,
        "UnitPrice" numeric NOT NULL,
        "SubTotal" numeric NOT NULL,
        CONSTRAINT "PK_BookingItems" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_BookingItems_Bookings_BookingId" FOREIGN KEY ("BookingId") REFERENCES "Bookings" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_BookingItems_TourPackages_TourPackageId" FOREIGN KEY ("TourPackageId") REFERENCES "TourPackages" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923073005_AddBookings') THEN
    CREATE INDEX "IX_BookingItems_BookingId" ON "BookingItems" ("BookingId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923073005_AddBookings') THEN
    CREATE INDEX "IX_BookingItems_TourPackageId" ON "BookingItems" ("TourPackageId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923073005_AddBookings') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260923073005_AddBookings', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923074011_AddTourSlug') THEN
    ALTER TABLE "TourPackages" ADD "Slug" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923074011_AddTourSlug') THEN
    UPDATE "TourPackages" SET "Slug" = ''
    WHERE "Id" = '11111111-1111-1111-1111-111111111111';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923074011_AddTourSlug') THEN
    UPDATE "TourPackages" SET "Slug" = ''
    WHERE "Id" = '22222222-2222-2222-2222-222222222222';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923074011_AddTourSlug') THEN
    UPDATE "TourPackages" SET "Slug" = ''
    WHERE "Id" = '33333333-3333-3333-3333-333333333333';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923074011_AddTourSlug') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260923074011_AddTourSlug', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923074214_SeedTourSlugs') THEN
    UPDATE "TourPackages" SET "Slug" = 'ventanillas-de-otuzco'
    WHERE "Id" = '11111111-1111-1111-1111-111111111111';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923074214_SeedTourSlugs') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260923074214_SeedTourSlugs', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923081646_SeedRemainingSlugs') THEN
    UPDATE "TourPackages" SET "Slug" = 'collpa'
    WHERE "Id" = '22222222-2222-2222-2222-222222222222';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923081646_SeedRemainingSlugs') THEN
    UPDATE "TourPackages" SET "Slug" = 'castillo-de-yanamarca'
    WHERE "Id" = '33333333-3333-3333-3333-333333333333';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923081646_SeedRemainingSlugs') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260923081646_SeedRemainingSlugs', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923193822_AddPickupToBooking') THEN
    ALTER TABLE "Bookings" ADD "PickupCost" numeric NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923193822_AddPickupToBooking') THEN
    ALTER TABLE "Bookings" ADD "PickupLocation" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260923193822_AddPickupToBooking') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260923193822_AddPickupToBooking', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017024643_CompleteSystemSchema') THEN

                    -- 1. Actualización de columnas en TourPackages
                    ALTER TABLE "TourPackages" DROP COLUMN IF EXISTS "Category";
                    ALTER TABLE "TourPackages" ADD COLUMN IF NOT EXISTS "CategoryId" uuid NULL;

                    -- 2. Actualización de columnas en ItineraryStops
                    ALTER TABLE "ItineraryStops" ADD COLUMN IF NOT EXISTS "EstimatedTime" text NULL;

                    -- 3. Actualización de columnas en Bookings
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "AdditionalPassengersJson" text NOT NULL DEFAULT '';
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "CustomerDni" text NOT NULL DEFAULT '';
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "InvoiceNumber" text NOT NULL DEFAULT '';
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "InvoiceType" text NOT NULL DEFAULT '';
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "OperationNumber" text NOT NULL DEFAULT '';
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "PaidAmount" numeric NOT NULL DEFAULT 0;
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "PaymentMethod" text NOT NULL DEFAULT '';
                    ALTER TABLE "Bookings" ADD COLUMN IF NOT EXISTS "ShiftName" text NOT NULL DEFAULT '';

                    -- 4. Actualización de columnas técnicas en Vehicles
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "Category" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "ChassisNumber" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "CitvExpiryDate" timestamp with time zone NULL;
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "CitvNumber" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "CitvProvider" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "Color" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "EngineNumber" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "EquipmentJson" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "FuelType" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "ManufactureYear" integer NOT NULL DEFAULT 0;
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "ResolutionNumber" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "SoatExpiryDate" timestamp with time zone NULL;
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "SoatIssueDate" timestamp with time zone NULL;
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "SoatNumber" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "SoatProvider" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "Status" text NOT NULL DEFAULT '';
                    ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "TucNumber" text NOT NULL DEFAULT '';

                    -- 5. Tabla de Categorías de Tours
                    CREATE TABLE IF NOT EXISTS "Categories" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "Name" text NOT NULL,
                        "Description" text NULL,
                        "Icon" text NULL,
                        "IsActive" boolean NOT NULL DEFAULT true,
                        "DisplayOrder" integer NOT NULL DEFAULT 1,
                        "CreatedAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );

                    -- 6. Tabla de Paquetes Destacados en Portada
                    CREATE TABLE IF NOT EXISTS "FeaturedPackages" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "TourPackageId" uuid NOT NULL,
                        "DisplayOrder" integer NOT NULL DEFAULT 1,
                        "CommercialTitle" text NOT NULL DEFAULT '',
                        "CommercialSubtitle" text NOT NULL DEFAULT '',
                        "PromoBadge" text NOT NULL DEFAULT '',
                        "PromoPrice" numeric NULL,
                        "SyncInventory" boolean NOT NULL DEFAULT true
                    );

                    -- 7. Tabla de Galería de Imágenes de Vehículos
                    CREATE TABLE IF NOT EXISTS "VehicleImages" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "VehicleId" uuid NOT NULL,
                        "ImageUrl" text NOT NULL DEFAULT '',
                        "IsCover" boolean NOT NULL DEFAULT false
                    );

                    -- 8. Módulo de Caja: Turnos de Caja Registradora
                    CREATE TABLE IF NOT EXISTS "CashRegisterShifts" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "ShiftNumber" text NOT NULL DEFAULT '',
                        "OpenedByUserName" text NOT NULL DEFAULT '',
                        "OpenedAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        "InitialAmount" numeric NOT NULL DEFAULT 0,
                        "ClosedByUserName" text NULL,
                        "ClosedAt" timestamp with time zone NULL,
                        "IsOpen" boolean NOT NULL DEFAULT true,
                        "ActualCash" numeric NULL,
                        "Notes" text NULL
                    );

                    -- 9. Módulo de Caja: Movimientos y Transacciones de Caja
                    CREATE TABLE IF NOT EXISTS "CashTransactions" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "ShiftId" uuid NOT NULL,
                        "TransactionType" text NOT NULL DEFAULT 'Ingreso',
                        "Category" text NOT NULL DEFAULT 'Cobro de Reserva',
                        "Amount" numeric NOT NULL DEFAULT 0,
                        "PaymentMethod" text NOT NULL DEFAULT 'Efectivo',
                        "ReferenceCode" text NULL,
                        "BookingId" uuid NULL,
                        "Description" text NOT NULL DEFAULT '',
                        "RegisteredBy" text NOT NULL DEFAULT '',
                        "CreatedAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );

                    -- 10. Módulo de Caja Chica: Cuentas de Fondo Fijo
                    CREATE TABLE IF NOT EXISTS "PettyCashAccounts" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "Name" text NOT NULL DEFAULT '',
                        "TotalFundAmount" numeric NOT NULL DEFAULT 0,
                        "ResponsiblePerson" text NOT NULL DEFAULT '',
                        "IsActive" boolean NOT NULL DEFAULT true,
                        "LastReplenishedAt" timestamp with time zone NULL
                    );

                    -- 11. Módulo de Caja Chica: Egresos y Rendiciones
                    CREATE TABLE IF NOT EXISTS "PettyCashMovements" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "AccountId" uuid NOT NULL,
                        "MovementType" text NOT NULL DEFAULT 'Egreso',
                        "ExpenseCategory" text NOT NULL DEFAULT 'Combustible',
                        "VoucherType" text NOT NULL DEFAULT 'Boleta',
                        "VoucherNumber" text NULL,
                        "Supplier" text NULL,
                        "Amount" numeric NOT NULL DEFAULT 0,
                        "ResponsiblePerson" text NOT NULL DEFAULT '',
                        "Concept" text NOT NULL DEFAULT '',
                        "RegisteredAt" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );

                    -- 12. Creación segura de Llaves Foráneas (Foreign Keys)
                    DO $$ BEGIN
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_TourPackages_Categories_CategoryId') THEN
                            ALTER TABLE "TourPackages" ADD CONSTRAINT "FK_TourPackages_Categories_CategoryId" FOREIGN KEY ("CategoryId") REFERENCES "Categories" ("Id") ON DELETE SET NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_FeaturedPackages_TourPackages_TourPackageId') THEN
                            ALTER TABLE "FeaturedPackages" ADD CONSTRAINT "FK_FeaturedPackages_TourPackages_TourPackageId" FOREIGN KEY ("TourPackageId") REFERENCES "TourPackages" ("Id") ON DELETE CASCADE;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_VehicleImages_Vehicles_VehicleId') THEN
                            ALTER TABLE "VehicleImages" ADD CONSTRAINT "FK_VehicleImages_Vehicles_VehicleId" FOREIGN KEY ("VehicleId") REFERENCES "Vehicles" ("Id") ON DELETE CASCADE;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_CashTransactions_CashRegisterShifts_ShiftId') THEN
                            ALTER TABLE "CashTransactions" ADD CONSTRAINT "FK_CashTransactions_CashRegisterShifts_ShiftId" FOREIGN KEY ("ShiftId") REFERENCES "CashRegisterShifts" ("Id") ON DELETE CASCADE;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_CashTransactions_Bookings_BookingId') THEN
                            ALTER TABLE "CashTransactions" ADD CONSTRAINT "FK_CashTransactions_Bookings_BookingId" FOREIGN KEY ("BookingId") REFERENCES "Bookings" ("Id") ON DELETE SET NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PettyCashMovements_PettyCashAccounts_AccountId') THEN
                            ALTER TABLE "PettyCashMovements" ADD CONSTRAINT "FK_PettyCashMovements_PettyCashAccounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "PettyCashAccounts" ("Id") ON DELETE CASCADE;
                        END IF;
                    END $$;

                    -- 13. Creación de Índices
                    CREATE INDEX IF NOT EXISTS "IX_TourPackages_CategoryId" ON "TourPackages" ("CategoryId");
                    CREATE INDEX IF NOT EXISTS "IX_CashTransactions_BookingId" ON "CashTransactions" ("BookingId");
                    CREATE INDEX IF NOT EXISTS "IX_CashTransactions_ShiftId" ON "CashTransactions" ("ShiftId");
                    CREATE INDEX IF NOT EXISTS "IX_FeaturedPackages_TourPackageId" ON "FeaturedPackages" ("TourPackageId");
                    CREATE INDEX IF NOT EXISTS "IX_PettyCashMovements_AccountId" ON "PettyCashMovements" ("AccountId");
                    CREATE INDEX IF NOT EXISTS "IX_VehicleImages_VehicleId" ON "VehicleImages" ("VehicleId");
                
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017024643_CompleteSystemSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261017024643_CompleteSystemSchema', '10.0.12');
    END IF;
END $EF$;
COMMIT;

