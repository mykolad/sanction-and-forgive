using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanctionAndForgive.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportChangeCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AddedCount",
                table: "Imports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChangedCount",
                table: "Imports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemovedCount",
                table: "Imports",
                type: "int",
                nullable: true);

            // The history so far, where it can be counted honestly (a copy of ImportStore.ChangeCountSql's rules, frozen
            // here): a source's first import added everything; a later one whose records and whose predecessor's records
            // are both still there (the active list, the one before it, a held one) is compared with it; the rest stay
            // null, "not recorded".
            migrationBuilder.Sql(BackfillSql);
        }

        private const string BackfillSql = @"
UPDATE i SET AddedCount = i.RecordCount, RemovedCount = 0, ChangedCount = 0
FROM Imports i
WHERE i.Status = 'Completed' AND i.BasedOnImportId IS NULL
    AND NOT EXISTS (SELECT 1 FROM Imports e WHERE e.SourceId = i.SourceId AND e.Id < i.Id AND e.Status = 'Completed');

SELECT i.Id AS NewId, p.Id AS OldId
INTO #Pairs
FROM Imports i
CROSS APPLY (
    SELECT TOP 1 c.Id, c.HasData FROM Imports c
    WHERE (i.BasedOnImportId IS NOT NULL AND c.Id = i.BasedOnImportId)
       OR (i.BasedOnImportId IS NULL AND c.SourceId = i.SourceId AND c.Id < i.Id AND c.Status = 'Completed')
    ORDER BY c.Id DESC) p
WHERE i.HasData = 1 AND i.AddedCount IS NULL AND i.Status IN ('Completed', 'PendingReview') AND p.HasData = 1;

SELECT s.ImportId, s.ExternalId,
    HASHBYTES('SHA2_256', CONCAT(
        CONVERT(nvarchar(3), s.Type), NCHAR(31),
        s.PrimaryName, NCHAR(31),
        ISNULL(N'+' + s.Programmes, N'-'), NCHAR(31),
        ISNULL(N'+' + CONVERT(nvarchar(10), s.ListedOn, 23), N'-'), NCHAR(31),
        ISNULL(N'+' + s.Remarks, N'-'), NCHAR(31),
        ISNULL(N'+' + s.SourceUrl, N'-'), NCHAR(30),
        (SELECT STRING_AGG(CONVERT(nvarchar(max), CONCAT(n.FullName, NCHAR(31), CONVERT(nvarchar(1), n.IsWeak), NCHAR(31), CONVERT(nvarchar(3), n.Script))), NCHAR(30))
            WITHIN GROUP (ORDER BY n.FullName COLLATE Latin1_General_BIN2, n.IsWeak, n.Script)
         FROM SubjectNames n WHERE n.SubjectId = s.Id), NCHAR(29),
        (SELECT STRING_AGG(CONVERT(nvarchar(max), CONCAT(a.Raw, NCHAR(31), ISNULL(N'+' + a.CountryCode, N'-'))), NCHAR(29))
            WITHIN GROUP (ORDER BY a.Raw COLLATE Latin1_General_BIN2, a.CountryCode COLLATE Latin1_General_BIN2)
         FROM SubjectAddresses a WHERE a.SubjectId = s.Id))) AS Signature
INTO #Signatures
FROM Subjects s
WHERE s.ImportId IN (SELECT NewId FROM #Pairs UNION SELECT OldId FROM #Pairs);

UPDATE i SET AddedCount = c.Added, RemovedCount = c.Removed, ChangedCount = c.Changed
FROM Imports i
JOIN (
    SELECT pr.NewId,
        SUM(CASE WHEN j.OldExternalId IS NULL THEN 1 ELSE 0 END) AS Added,
        SUM(CASE WHEN j.NewExternalId IS NULL THEN 1 ELSE 0 END) AS Removed,
        SUM(CASE WHEN j.NewExternalId IS NOT NULL AND j.OldExternalId IS NOT NULL AND j.NewSignature <> j.OldSignature THEN 1 ELSE 0 END) AS Changed
    FROM #Pairs pr
    CROSS APPLY (
        SELECT n.ExternalId AS NewExternalId, o.ExternalId AS OldExternalId, n.Signature AS NewSignature, o.Signature AS OldSignature
        FROM (SELECT ExternalId, Signature FROM #Signatures WHERE ImportId = pr.NewId) n
        FULL OUTER JOIN (SELECT ExternalId, Signature FROM #Signatures WHERE ImportId = pr.OldId) o
            ON o.ExternalId COLLATE Latin1_General_BIN2 = n.ExternalId COLLATE Latin1_General_BIN2 AND DATALENGTH(o.ExternalId) = DATALENGTH(n.ExternalId)) j
    GROUP BY pr.NewId) c ON c.NewId = i.Id;

DROP TABLE #Signatures;
DROP TABLE #Pairs;
";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedCount",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "ChangedCount",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "RemovedCount",
                table: "Imports");
        }
    }
}
