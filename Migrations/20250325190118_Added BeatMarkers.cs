using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diyokee.Migrations
{
    /// <inheritdoc />
    public partial class AddedBeatMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DownBeatAt",
                table: "Files");

            migrationBuilder.AddColumn<string>(
                name: "BeatMarkers",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BeatMarkers",
                table: "Files");

            migrationBuilder.AddColumn<double>(
                name: "DownBeatAt",
                table: "Files",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
