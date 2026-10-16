using Xunit;
using static BcInventory.Api.Tests.TestData;

namespace BcInventory.Api.Tests;

public class CatalogTests
{
    private static readonly string[] DokumenAsal =
    {
        "Dokumen Asal Barang / Tipe Dokumen", "Dokumen Asal Barang / No Daftar", "Dokumen Asal Barang / Tanggal Daftar",
        "Dokumen Asal Barang / Qty Pemakaian", "Dokumen Asal Barang / Satuan",
    };

    [Fact]
    public void Bc27Out_ends_with_the_opt_in_Dokumen_Asal_Barang_group()
    {
        var fields = Report("bc27-out").Fields;
        Assert.Equal(DokumenAsal, fields.TakeLast(5).Select(f => f.Name));
        Assert.All(fields.TakeLast(5), f => { Assert.True(f.OptIn); Assert.Equal("Dokumen Asal Barang", f.Group); });
        Assert.Equal(FieldType.Date, fields.Single(f => f.Name == "Dokumen Asal Barang / Tanggal Daftar").Type);
        Assert.Equal(FieldType.Number, fields.Single(f => f.Name == "Dokumen Asal Barang / Qty Pemakaian").Type);
    }

    [Theory]
    [InlineData("bc25-out")]
    [InlineData("bc41-out")]
    public void Other_outbound_reports_keep_the_shared_layout(string key)
    {
        Assert.Equal(Catalog.BcOut.Select(f => f.Name), Report(key).Fields.Select(f => f.Name));
        Assert.DoesNotContain(Report(key).Fields, f => f.OptIn);
    }

    [Fact]
    public void Bc27Out_starts_from_the_shared_layout_without_opt_in_columns()
    {
        Assert.Equal(Catalog.BcOut.Select(f => f.Name), SiteReports.Starter(Report("bc27-out")));
    }

    [Fact]
    public void Field_names_are_unique_within_every_report()
    {
        foreach (var r in Catalog.Reports)
            Assert.True(r.Fields.Select(f => f.Name).Distinct().Count() == r.Fields.Length, $"{r.Key} repeats a field name");
    }

    [Fact]
    public void Assignable_reports_are_the_browsable_report_and_movement_ones()
    {
        var keys = SiteReports.Assignable.Select(r => r.Key).ToList();
        Assert.Contains("bc27-out", keys);
        Assert.Contains("sparepart", keys);
        Assert.DoesNotContain("aset-sparepart", keys);      // upload template only, never in a picker
    }
}
