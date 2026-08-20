using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

public partial class AddRankPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "rank_preferences",
            table: "profile",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "rank_preferences",
            table: "profile");
    }
}
