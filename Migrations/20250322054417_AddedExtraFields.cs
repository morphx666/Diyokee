using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diyokee.Migrations
{
    /// <inheritdoc />
    public partial class AddedExtraFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<float>(
                name: "Duration",
                table: "Files",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<float>(
                name: "BPM",
                table: "Files",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Waveform",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BPM",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Waveform",
                table: "Files");

            migrationBuilder.AlterColumn<int>(
                name: "Duration",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(float),
                oldType: "REAL");
        }
    }
}
