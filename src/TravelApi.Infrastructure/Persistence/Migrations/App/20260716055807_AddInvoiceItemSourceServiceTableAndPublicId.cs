using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class AddInvoiceItemSourceServiceTableAndPublicId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceServicePublicId",
                table: "InvoiceItem",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceServiceTable",
                table: "InvoiceItem",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItem_SourceServicePublicId",
                table: "InvoiceItem",
                column: "SourceServicePublicId",
                filter: "\"SourceServicePublicId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoiceItem_SourceServicePublicId",
                table: "InvoiceItem");

            migrationBuilder.DropColumn(
                name: "SourceServicePublicId",
                table: "InvoiceItem");

            migrationBuilder.DropColumn(
                name: "SourceServiceTable",
                table: "InvoiceItem");
        }
    }
}
