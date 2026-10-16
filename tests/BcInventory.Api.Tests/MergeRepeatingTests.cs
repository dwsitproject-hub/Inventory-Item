using System.Text.Json;
using ClosedXML.Excel;
using Xunit;
using static BcInventory.Api.Tests.TestData;

namespace BcInventory.Api.Tests;

/// <summary>
/// BC 2.7 Out: one line may draw on several origin documents. The upload repeats the line once
/// per document; those rows load as one line whose Dokumen Asal Barang columns hold a list.
/// </summary>
public class MergeRepeatingTests
{
    private const string Tipe = "Dokumen Asal Barang / Tipe Dokumen";
    private const string NoDaftar = "Dokumen Asal Barang / No Daftar";
    private const string Qty = "Dokumen Asal Barang / Qty Pemakaian";

    private static readonly Field[] Fields = Report("bc27-out").Fields;

    private static ParsedLine Row(int rowNo, string? nopen, string? kode, string? noDaftar, decimal? qty = null, params string[] problems)
    {
        var d = new Dictionary<string, object?>();
        if (nopen is not null) { d["Dok. Pabean / Nomor"] = nopen; d["Jumlah"] = 100m; }
        if (kode is not null) d["Kode Barang"] = kode;
        if (noDaftar is not null) { d[Tipe] = "BC 2.3"; d[NoDaftar] = noDaftar; }
        if (qty is not null) d[Qty] = qty;
        return new ParsedLine(rowNo, d, problems.ToList());
    }

    private static List<object?> List(ParsedLine l, string field) => (List<object?>)l.Data[field]!;

    [Fact]
    public void Rows_differing_only_in_origin_documents_become_one_line()
    {
        var merged = Ingestion.MergeRepeating(new()
        {
            Row(2, "000777", "912.001", "000123", 40m),
            Row(3, "000777", "912.001", "000124", 60m),
        }, Fields);

        var line = Assert.Single(merged);
        Assert.Equal(2, line.RowNo);
        Assert.Equal(new object?[] { "000123", "000124" }, List(line, NoDaftar));
        Assert.Equal(new object?[] { 40m, 60m }, List(line, Qty));
        Assert.Equal("000777", line.Data["Dok. Pabean / Nomor"]);
    }

    [Fact]
    public void Blank_main_columns_continue_the_row_above()
    {
        // Excel merged cells: only the first row of the block carries NoPen / Kode Barang.
        var merged = Ingestion.MergeRepeating(new()
        {
            Row(2, "000777", "912.001", "000123"),
            Row(3, null, null, "000124"),
            Row(4, null, null, "000125"),
        }, Fields);
        Assert.Equal(new object?[] { "000123", "000124", "000125" }, List(Assert.Single(merged), NoDaftar));
    }

    [Fact]
    public void Same_NoPen_with_different_items_stays_separate_lines()
    {
        var merged = Ingestion.MergeRepeating(new()
        {
            Row(2, "000777", "912.001", "000123"),
            Row(3, "000777", "912.002", "000124"),
            Row(4, "000777", "912.001", "000125"),     // back to the first item, out of order
        }, Fields);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new object?[] { "000123", "000125" }, List(merged[0], NoDaftar));
        Assert.Equal(new object?[] { "000124" }, List(merged[1], NoDaftar));
    }

    [Fact]
    public void Entries_stay_aligned_when_one_document_leaves_a_column_blank()
    {
        var merged = Ingestion.MergeRepeating(new()
        {
            Row(2, "000777", "912.001", "000123", 40m),
            Row(3, "000777", "912.001", "000124"),      // no Qty Pemakaian on this document
        }, Fields);
        Assert.Equal(new object?[] { 40m, null }, List(Assert.Single(merged), Qty));
    }

    [Fact]
    public void Rows_without_origin_documents_are_never_merged()
    {
        var merged = Ingestion.MergeRepeating(new()
        {
            Row(2, "000777", "912.001", null),
            Row(3, "000777", "912.001", null),
        }, Fields);
        Assert.Equal(2, merged.Count);
        Assert.All(merged, l => Assert.False(l.Data.ContainsKey(NoDaftar)));
    }

    [Fact]
    public void A_bad_row_quarantines_the_whole_merged_line_and_names_the_row()
    {
        var merged = Ingestion.MergeRepeating(new()
        {
            Row(2, "000777", "912.001", "000123"),
            Row(3, "000777", "912.001", "000124", null, "'Dokumen Asal Barang / Tanggal Daftar': not a recognised date ('x')"),
        }, Fields);
        var problem = Assert.Single(Assert.Single(merged).Problems);
        Assert.StartsWith("row 3:", problem);
    }

    [Fact]
    public void Reports_without_repeating_columns_are_untouched()
    {
        var lines = new List<ParsedLine> { Row(2, "1", "a", null), Row(3, "1", "a", null) };
        Assert.Same(lines, Ingestion.MergeRepeating(lines, Report("bc25-out").Fields));
    }

    [Fact]
    public void Repeating_values_leave_the_api_as_an_array()
    {
        var f = Fields.Single(x => x.Name == NoDaftar);
        var v = Assert.IsType<JsonElement>(Reports.Value(f, "[\"000123\", \"000124\"]"));
        Assert.Equal(JsonValueKind.Array, v.ValueKind);
        Assert.Equal(2, v.GetArrayLength());
        Assert.Equal("x", Reports.Value(Fields.Single(x => x.Name == "Kode Barang"), "x"));
        Assert.Contains("jsonb_build_array", Reports.Proj(f));        // an older scalar reads as one entry
    }

    [Fact]
    public void Template_upload_with_two_documents_and_a_merged_cell_block_loads_two_lines()
    {
        var (bytes, _) = TemplateFiles.Build(Report("bc27-out"));
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.First();
        var col = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
        void Put(int row, string field, string value) => ws.Cell(row, col[field]).Value = value;

        // item 1: two origin documents, main columns repeated
        foreach (var (row, daftar, qty) in new[] { (2, "000123", "40"), (3, "000124", "60") })
        {
            Put(row, "Jenis Dok.", "27"); Put(row, "Dok. Pabean / Nomor", "000777"); Put(row, "Kode Barang", "912.001");
            Put(row, "Jumlah", "100"); Put(row, NoDaftar, daftar); Put(row, Qty, qty);
        }
        // item 2: same NoPen, other item; its second document sits under merged (blank) cells
        Put(4, "Jenis Dok.", "27"); Put(4, "Dok. Pabean / Nomor", "000777"); Put(4, "Kode Barang", "912.002");
        Put(4, "Jumlah", "5"); Put(4, NoDaftar, "000200");
        Put(5, NoDaftar, "000201");

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var parsed = XlsxParser.Parse(ms.ToArray(), "upload.xlsx");
        var merged = Ingestion.MergeRepeating(parsed.Lines, Fields);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new object?[] { "000123", "000124" }, List(merged[0], NoDaftar));
        Assert.Equal(new object?[] { 40m, 60m }, List(merged[0], Qty).Select(Convert.ToDecimal).Cast<object?>());
        Assert.Equal(new object?[] { "000200", "000201" }, List(merged[1], NoDaftar));
        Assert.All(merged, l => Assert.Empty(l.Problems));
    }
}
