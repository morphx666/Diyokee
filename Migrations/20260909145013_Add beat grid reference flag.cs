using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diyokee.Migrations
{
    /// <inheritdoc />
    public partial class Addbeatgridreferenceflag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReference",
                table: "BeatGridMarker",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsReference",
                table: "BeatGridMarker");
        }
    }
}
