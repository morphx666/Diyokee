using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diyokee.Migrations
{
    /// <inheritdoc />
    public partial class Renamechildtablesandtheirforeignkey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BeatGridMarker_Files_DFileId",
                table: "BeatGridMarker");

            migrationBuilder.DropForeignKey(
                name: "FK_CuePoint_Files_DFileId",
                table: "CuePoint");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CuePoint",
                table: "CuePoint");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BeatGridMarker",
                table: "BeatGridMarker");

            migrationBuilder.RenameTable(
                name: "CuePoint",
                newName: "CuePoints");

            migrationBuilder.RenameTable(
                name: "BeatGridMarker",
                newName: "BeatGridMarkers");

            migrationBuilder.RenameColumn(
                name: "DFileId",
                table: "CuePoints",
                newName: "FileId");

            migrationBuilder.RenameIndex(
                name: "IX_CuePoint_DFileId",
                table: "CuePoints",
                newName: "IX_CuePoints_FileId");

            migrationBuilder.RenameColumn(
                name: "DFileId",
                table: "BeatGridMarkers",
                newName: "FileId");

            migrationBuilder.RenameIndex(
                name: "IX_BeatGridMarker_DFileId",
                table: "BeatGridMarkers",
                newName: "IX_BeatGridMarkers_FileId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CuePoints",
                table: "CuePoints",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BeatGridMarkers",
                table: "BeatGridMarkers",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BeatGridMarkers_Files_FileId",
                table: "BeatGridMarkers",
                column: "FileId",
                principalTable: "Files",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CuePoints_Files_FileId",
                table: "CuePoints",
                column: "FileId",
                principalTable: "Files",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BeatGridMarkers_Files_FileId",
                table: "BeatGridMarkers");

            migrationBuilder.DropForeignKey(
                name: "FK_CuePoints_Files_FileId",
                table: "CuePoints");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CuePoints",
                table: "CuePoints");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BeatGridMarkers",
                table: "BeatGridMarkers");

            migrationBuilder.RenameTable(
                name: "CuePoints",
                newName: "CuePoint");

            migrationBuilder.RenameTable(
                name: "BeatGridMarkers",
                newName: "BeatGridMarker");

            migrationBuilder.RenameColumn(
                name: "FileId",
                table: "CuePoint",
                newName: "DFileId");

            migrationBuilder.RenameIndex(
                name: "IX_CuePoints_FileId",
                table: "CuePoint",
                newName: "IX_CuePoint_DFileId");

            migrationBuilder.RenameColumn(
                name: "FileId",
                table: "BeatGridMarker",
                newName: "DFileId");

            migrationBuilder.RenameIndex(
                name: "IX_BeatGridMarkers_FileId",
                table: "BeatGridMarker",
                newName: "IX_BeatGridMarker_DFileId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CuePoint",
                table: "CuePoint",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BeatGridMarker",
                table: "BeatGridMarker",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BeatGridMarker_Files_DFileId",
                table: "BeatGridMarker",
                column: "DFileId",
                principalTable: "Files",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CuePoint_Files_DFileId",
                table: "CuePoint",
                column: "DFileId",
                principalTable: "Files",
                principalColumn: "Id");
        }
    }
}
