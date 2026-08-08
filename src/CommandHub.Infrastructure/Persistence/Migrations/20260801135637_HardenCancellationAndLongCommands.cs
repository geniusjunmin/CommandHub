using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenCancellationAndLongCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CommandExecutions_NormalizedCommand",
                table: "CommandExecutions");

            migrationBuilder.AlterColumn<string>(
                name: "CommandText",
                table: "CommandTemplates",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedCommand",
                table: "CommandExecutions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192);

            migrationBuilder.AlterColumn<string>(
                name: "MaskedCommandText",
                table: "CommandExecutions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "CommandExecutions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAt",
                table: "CommandExecutions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestedByUserId",
                table: "CommandExecutions",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedCommandHash",
                table: "CommandExecutions",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedCommandPrefix",
                table: "CommandExecutions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
            migrationBuilder.Sql("""
                UPDATE "CommandExecutions"
                SET "NormalizedCommandHash" = encode(digest(convert_to("NormalizedCommand", 'UTF8'), 'sha256'), 'hex'),
                    "NormalizedCommandPrefix" = left("NormalizedCommand", 512);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CommandExecutions_NormalizedCommandHash",
                table: "CommandExecutions",
                column: "NormalizedCommandHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CommandExecutions_NormalizedCommandHash",
                table: "CommandExecutions");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "CommandExecutions");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAt",
                table: "CommandExecutions");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedByUserId",
                table: "CommandExecutions");

            migrationBuilder.DropColumn(
                name: "NormalizedCommandHash",
                table: "CommandExecutions");

            migrationBuilder.DropColumn(
                name: "NormalizedCommandPrefix",
                table: "CommandExecutions");

            migrationBuilder.AlterColumn<string>(
                name: "CommandText",
                table: "CommandTemplates",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedCommand",
                table: "CommandExecutions",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "MaskedCommandText",
                table: "CommandExecutions",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_CommandExecutions_NormalizedCommand",
                table: "CommandExecutions",
                column: "NormalizedCommand");
        }
    }
}
