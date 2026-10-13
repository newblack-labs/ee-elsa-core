using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Elsa.Persistence.EFCore.Sqlite.Migrations.Labels
{
    /// <inheritdoc />
    public partial class PerTenantLabelUniqueness : Migration
    {
        private readonly Elsa.Persistence.EFCore.IElsaDbContextSchema _schema;

        /// <inheritdoc />
        public PerTenantLabelUniqueness(Elsa.Persistence.EFCore.IElsaDbContextSchema schema)
        {
            _schema = schema;
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite has no schemas: the provider drops the schema from every table it creates, so raw SQL must name the
            // table unqualified. "Elsa"."Labels" would be read as table Labels of an attached database "Elsa" and fail with
            // "no such table", aborting the tenant's startup tasks.
            // Default tenant is "" (not null). Stamp leftover nulls so the unique index covers them.
            migrationBuilder.Sql($"""
                UPDATE "Labels"
                SET "TenantId" = ''
                WHERE "TenantId" IS NULL;
                """);

            // No silent dedupe: the unique index below refuses leftover duplicate (TenantId, NormalizedName) rows itself, failing
            // with "UNIQUE constraint failed: Labels.TenantId, Labels.NormalizedName", and operators resolve them before
            // upgrading. The other providers list the duplicates first with a RAISE; SQLite allows RAISE() only inside a
            // trigger and rejects the statement when preparing it, so there it would fail every upgrade, duplicates or not.

            migrationBuilder.CreateIndex(
                name: "IX_Label_TenantId_NormalizedName",
                schema: _schema.Schema,
                table: "Labels",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Label_TenantId_NormalizedName",
                schema: _schema.Schema,
                table: "Labels");
        }
    }
}
