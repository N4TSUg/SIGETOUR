using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TourPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Subtitle = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Schedule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Inclusions = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BasePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false, defaultValue: 0m),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TourPackages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TourImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TourPackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false),
                    IsMain = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TourImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TourImages_TourPackages_TourPackageId",
                        column: x => x.TourPackageId,
                        principalTable: "TourPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "TourPackages",
                columns: new[] { "Id", "Description", "Inclusions", "IsActive", "Schedule", "Subtitle", "Title" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), "Ventanillas de Otuzco (Cementerio de la Cultura Cajamarca con más de 3 mil años de antigüedad), Jardín de las Hortensias, Artesanía de Cajamarca, Fundo los Aples o 'Tres Molinos', fábrica artesanal de quesos, mantequilla, manjar blanco, Rosquitas de manteca etc.", "Movilidad + Guía de turismo", true, "3:30pm – 6:30pm", "Cementerio pre-inca", "Ventanillas de Otuzco" },
                    { new Guid("22222222-2222-2222-2222-222222222222"), "Ex - hacienda La Collpa, Laguna Artificial, Casa Hacienda, Capilla de la Virgen del Carmen, Establo Central, Llamado de Vacas por su nombre, degustación de Lácteos; visita a las Cascadas de Llacanora según temporada o talleres de cerámica utilitaria y decorativa.", "Movilidad + Guía de turismo", true, "3:30pm – 7pm", "Llamado de Vacas por su nombre", "Collpa" },
                    { new Guid("33333333-3333-3333-3333-333333333333"), "El Castillo de Yanamarca es un moderno atractivo turístico con arquitectura estilo medieval en donde se puede apreciar, pinturas, esculturas de piedra y madera. Este lugar ofrece vistas panorámicas espectaculares del valle circundante y cuenta con un restaurante moderno, cafetería, áreas de juegos y espacios temáticos.", "Movilidad + Guía de turismo", true, "Horario a coordinar - Am o Pm", "Estilo medieval", "Castillo de Yanamarca" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_TourImages_TourPackageId",
                table: "TourImages",
                column: "TourPackageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TourImages");

            migrationBuilder.DropTable(
                name: "TourPackages");
        }
    }
}
