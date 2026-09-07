using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diyokee.Migrations
{
    /// <inheritdoc />
    public partial class Addbeatgridmarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BeatGridMarker",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Position = table.Column<double>(type: "REAL", nullable: false),
                    BPM = table.Column<double>(type: "REAL", nullable: false),
                    IsDownbeat = table.Column<bool>(type: "INTEGER", nullable: false),
                    DFileId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BeatGridMarker", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BeatGridMarker_Files_DFileId",
                        column: x => x.DFileId,
                        principalTable: "Files",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BeatGridMarker_DFileId",
                table: "BeatGridMarker",
                column: "DFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BeatGridMarker");
        }
    }
}
