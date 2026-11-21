using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// Lists an administrator uploads (owner's decision, 2026-11-21; version 2, the Rust reference first): Australia's
// Consolidated List as a fictional workbook, and Ukraine's register, which must come as all three of its files. Every
// upload is held for review; the test confirms one, rejects another, then removes the list again.
[Collection("3 Changes")]
public sealed class UploadsTests
{
    private const string Australia = "au-dfat";

    [SinceVersionFact(2)]
    public async Task AnUpload_IsHeld_ItsChangesShown_ThenConfirmed_AndAListAwaitingItIsNeverScreened()
    {
        await Suite.ReadyAsync();
        var status = await Suite.GetAsync("api/v1/status");
        Assert.Contains("uploads", status.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));

        try
        {
            // A file that isn't the list's, and Ukraine's register without its other files: refused, nothing written.
            using (var wrong = await Suite.AdminUploadAsync($"api/v1/admin/lists/{Australia}/upload", ("list.xlsx", "not a workbook"u8.ToArray())))
            {
                Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            }

            using (var partial = await Suite.AdminUploadAsync("api/v1/admin/lists/ua-nsdc/upload", ("people.csv", Encoding.UTF8.GetBytes(UkrainianPeople))))
            {
                Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
                Assert.Contains("companies, vessels", await partial.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var notUploaded = await Suite.AdminUploadAsync("api/v1/admin/lists/uk/upload", ("uk.xml", "<x/>"u8.ToArray())))
            {
                Assert.Equal(HttpStatusCode.NotFound, notUploaded.StatusCode);
            }

            // The first upload: held, every entry new, and the list isn't screened while it waits.
            var first = await UploadAsync(Workbook(("1", "Quillon Zephyrsmith", "Individual", "Primary Name"), ("1a", "Q. Zephyrsmith", "Individual", "Alias"), ("2", "Northwind Brassworks", "Entity", "Primary Name"), ("3", "Zephyr Star", "Vessel", "Primary Name")));
            Assert.Equal("HeldForReview", first.GetProperty("result").GetString());
            var firstId = first.GetProperty("importId").GetInt32();
            var changes = await ChangesAsync(firstId);
            Assert.Equal((3, 0, 0), (changes.GetProperty("added").GetProperty("count").GetInt32(), changes.GetProperty("removed").GetProperty("count").GetInt32(), changes.GetProperty("changed").GetProperty("count").GetInt32()));
            var screened = await Suite.ScreenAsync(new { reference = "upload-waiting", receiverName = "Quillon Zephyrsmith" });
            Assert.DoesNotContain(screened.GetProperty("lists").EnumerateArray(), list => list.GetProperty("code").GetString() == Australia);
            Assert.NotEqual("Incomplete", screened.GetProperty("outcome").GetString());

            // Confirmed: the list is searched.
            using (var confirm = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/imports/{firstId}/confirm", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            }

            await Suite.WaitForListsAsync(lists => lists.TryGetValue(Australia, out var list) && list.GetProperty("state").GetString() == "Current" && list.GetProperty("recordCount").GetInt32() == 3);
            var found = await Suite.ScreenAsync(new { reference = "upload-confirmed", receiverName = "Quillon Zephyrsmith" });
            Assert.Equal("Match", Suite.Cell(found, Australia)!.Value.GetProperty("outcome").GetString());

            // A second upload: one entry gone, one renamed; held, its changes field by field; rejected.
            var second = await UploadAsync(Workbook(("1", "Quillon Zephyrsmith", "Individual", "Primary Name"), ("1a", "Quill Zephyrsmith", "Individual", "Alias"), ("3", "Zephyr Star", "Vessel", "Primary Name")));
            var secondId = second.GetProperty("importId").GetInt32();
            changes = await ChangesAsync(secondId);
            Assert.Equal((0, 1, 1), (changes.GetProperty("added").GetProperty("count").GetInt32(), changes.GetProperty("removed").GetProperty("count").GetInt32(), changes.GetProperty("changed").GetProperty("count").GetInt32()));
            var field = changes.GetProperty("changed").GetProperty("entries")[0].GetProperty("fields")[0];
            Assert.Equal(("Names", "Quill Zephyrsmith", "Q. Zephyrsmith"), (field.GetProperty("field").GetString(), field.GetProperty("added")[0].GetString(), field.GetProperty("removed")[0].GetString()));
            using (var reject = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/imports/{secondId}/reject", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
            }

            // The list in use again: unchanged, nothing to review.
            var again = await UploadAsync(Workbook(("1", "Quillon Zephyrsmith", "Individual", "Primary Name"), ("1a", "Q. Zephyrsmith", "Individual", "Alias"), ("2", "Northwind Brassworks", "Entity", "Primary Name"), ("3", "Zephyr Star", "Vessel", "Primary Name")));
            Assert.Equal("Unchanged", again.GetProperty("result").GetString());
        }
        finally
        {
            await RemoveAsync(Australia);
        }
    }

    private static async Task<JsonElement> UploadAsync(byte[] workbook)
    {
        using var response = await Suite.AdminUploadAsync($"api/v1/admin/lists/{Australia}/upload", ("Australian_Sanctions_Consolidated_List.xlsx", workbook));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ChangesAsync(int importId)
    {
        using var response = await Suite.AdminAsync(HttpMethod.Get, $"api/v1/admin/imports/{importId}/changes", body: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>The list removed whole (its records, imports and source), so later tests see the lists as they were. In
    /// two steps, each seen before the next: removed at once, the database would be as it was before the test, and a
    /// replica whose change check saw nothing in between (every step here reloads by itself) would see no change.</summary>
    private static async Task RemoveAsync(string code)
    {
        await SqlAsync(
            code,
            """
            DECLARE @id int = (SELECT Id FROM Sources WHERE Code = @code);
            UPDATE Sources SET ActiveImportId = NULL WHERE Id = @id;
            DELETE FROM Subjects WHERE ImportId IN (SELECT Id FROM Imports WHERE SourceId = @id);
            UPDATE Imports SET BasedOnImportId = NULL WHERE SourceId = @id;
            DELETE FROM Imports WHERE SourceId = @id;
            """);
        await Suite.WaitForListsAsync(lists => !lists.TryGetValue(code, out var list) || list.GetProperty("state").GetString() != "Current");
        await SqlAsync(code, "DELETE FROM Sources WHERE Code = @code;");
        await Suite.WaitForListsAsync(lists => !lists.TryGetValue(code, out var list) || list.GetProperty("state").GetString() == "NotImported");
    }

    private static async Task SqlAsync(string code, string sql)
    {
        await using var connection = await Suite.OpenSqlAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@code", code);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A fictional Consolidated List: its one sheet, a header row and a row per name.</summary>
    private static byte[] Workbook(params (string Reference, string Name, string Type, string NameType)[] rows)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static string Cell(int row, int column, string text) =>
            $"<c r=\"{(char)('A' + column)}{row}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(text)}</t></is></c>";
        var header = new[] { "Reference", "Name of Individual or Entity", "Type", "Name Type", "Alias Strength", "Committees" };
        var sheet = new StringBuilder($"<worksheet xmlns=\"{main}\"><sheetData>");
        sheet.Append("<row>").Append(string.Concat(header.Select((text, c) => Cell(1, c, text)))).Append("</row>");
        for (var i = 0; i < rows.Length; i++)
        {
            var (reference, name, type, nameType) = rows[i];
            var cells = new[] { reference, name, type, nameType, "", "Conformance (fictional)" };
            sheet.Append("<row>").Append(string.Concat(cells.Select((text, c) => Cell(i + 2, c, text)))).Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
            {
                ("xl/workbook.xml", $"<workbook xmlns=\"{main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Consolidated List\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
                ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
                ("xl/sharedStrings.xml", $"<sst xmlns=\"{main}\"/>"),
                ("xl/worksheets/sheet1.xml", sheet.ToString()),
            })
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return stream.ToArray();
    }

    private const string UkrainianPeople =
        "sid\tname\ttranslit_name\taliases\tstatus\tbirthdate\tcitizenship\tidentity_docs\treg_id\ttax_id\tadditional_info\tactive_sanctions\tsanctions_term\tsanctions_end_date\tlast_action_type\tlast_action_decree\tdecree_date\tdecree_appendix\tposition_in_appendix\n" +
        "1\tКвіллон Зефірсміт\tKvillon Zefirsmit\t\tactive\t1960\t\t\t\t\t\t\t\t\t\t\t\t\t\n";
}
