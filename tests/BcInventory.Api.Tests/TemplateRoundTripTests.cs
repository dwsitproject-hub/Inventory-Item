using ClosedXML.Excel;
using Xunit;
using static BcInventory.Api.Tests.TestData;

namespace BcInventory.Api.Tests;

/// <summary>Download a blank template, fill it, and read it back the way an upload is read.</summary>
public class TemplateRoundTripTests
{
    private static byte[] Filled(Catalog.Report report, Dictionary<string, string> values, string sheetName = "Sheet1")
    {
        var (bytes, _) = TemplateFiles.Build(report);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.First();
        ws.Name = sheetName;                               // no name hint: identification is by headers
        var headers = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
        foreach (var (k, v) in values) ws.Cell(2, headers[k]).Value = v;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static readonly Dictionary<string, string> OutRow = new()
    {
        ["Jenis Dok."] = "27", ["Dok. Pabean / Nomor"] = "000777", ["Dok. Pabean / Tanggal"] = "01.10.2026",
        ["Kode Barang"] = "912.001.006", ["Nama Barang"] = "BLEACHING EARTH", ["Sat"] = "KG", ["Jumlah"] = "100",
    };

    [Fact]
    public void Bc27Out_template_carries_the_Dokumen_Asal_Barang_headers()
    {
        var (bytes, _) = TemplateFiles.Build(Report("bc27-out"));
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var headers = wb.Worksheets.First().Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Equal(Report("bc27-out").Fields.Select(f => f.Name), headers.Skip(1));   // after "No"
        Assert.Contains(wb.Worksheet("Petunjuk").CellsUsed(), c => c.GetString().StartsWith("Dokumen Asal Barang columns are optional"));
    }

    [Fact]
    public void Filled_Bc27Out_is_identified_by_its_columns_alone_and_parsed()
    {
        var row = new Dictionary<string, string>(OutRow)
        {
            ["Dokumen Asal Barang / Tipe Dokumen"] = "BC 2.3",
            ["Dokumen Asal Barang / No Daftar"] = "000123",
            ["Dokumen Asal Barang / Tanggal Daftar"] = "15.09.2026",
            ["Dokumen Asal Barang / Qty Pemakaian"] = "40",
            ["Dokumen Asal Barang / Satuan"] = "KG",
        };
        var bytes = Filled(Report("bc27-out"), row);
        Assert.Equal("BC27OUT", XlsxParser.SniffTemplate(bytes, "upload.xlsx"));

        var line = Assert.Single(XlsxParser.Parse(bytes, "upload.xlsx").Lines);
        Assert.Empty(line.Problems);
        Assert.Equal("000123", line.Data["Dokumen Asal Barang / No Daftar"]?.ToString());
        Assert.Equal("2026-09-15", line.Data["Dokumen Asal Barang / Tanggal Daftar"]?.ToString());
        Assert.Equal(40m, Convert.ToDecimal(line.Data["Dokumen Asal Barang / Qty Pemakaian"]));
    }

    [Theory]
    [InlineData("bc25-out", "Laporan BC 2.5 Out", "BC25")]
    [InlineData("bc41-out", "Laporan BC 4.1 Out", "BC41")]
    public void Shared_outbound_layout_is_still_told_apart_by_name(string key, string sheet, string template)
    {
        var bytes = Filled(Report(key), OutRow, sheet);
        Assert.Equal(template, XlsxParser.SniffTemplate(bytes, "upload.xlsx"));
    }
}
