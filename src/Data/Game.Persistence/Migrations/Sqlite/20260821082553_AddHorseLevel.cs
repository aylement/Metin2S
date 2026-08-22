using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantumCore.Game.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddHorseLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "HorseLevel",
                table: "Players",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.UpdateData(
                table: "Players",
                keyColumn: "Id",
                keyValue: 1u,
                column: "HorseLevel",
                value: (byte)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HorseLevel",
                table: "Players");
        }
    }
}
