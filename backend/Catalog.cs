namespace BcInventory.Api;

public enum FieldType { Text, Number, Date }

/// <summary>
/// A stored field. <see cref="Name"/> is always the upload column header, verbatim — it is the
/// jsonb key, the API key, and what saved views/sorts reference, so ingestion traceability holds
/// (PRD v1.2 App. D & E). <see cref="Label"/> is the customs-terminology name shown to users;
/// when absent the verbatim name is displayed.
/// </summary>
public record Field(string Name, FieldType Type, string Group = "", string? Label = null)
{
    public string Display => Label ?? Name;
}

/// <summary>
/// Report field catalogs. Field names are the exact upload column headers (PRD v1.2 Appendices D & E).
/// Where a template repeats a name, it is qualified by its source group header, e.g. "Amount After Facility / BM".
/// </summary>
public static class Catalog
{
    public static readonly Field[] Pib =
    {
        new("Status", FieldType.Text, "Document"),
        new("No.Pengajuan PIB", FieldType.Text, "Document"),
        new("Tipe PIB", FieldType.Text, "Document", "Jenis BC"),
        new("No. Co", FieldType.Text, "Document"),
        new("No. PIB", FieldType.Text, "Document", "NoPen"),
        new("PibDate", FieldType.Date, "Document", "Tanggal Nopen"),
        new("Vessel Code", FieldType.Text, "Shipping"),
        new("KPBC Bongkar", FieldType.Text, "Shipping"),
        new("KPBC Pengawas", FieldType.Text, "Shipping"),
        new("Loading Port", FieldType.Text, "Shipping"),
        new("Transit Port", FieldType.Text, "Shipping"),
        new("Disc Port", FieldType.Text, "Shipping"),
        new("BL No", FieldType.Text, "Shipping"),
        new("BL Date", FieldType.Date, "Shipping"),
        new("ETA Date", FieldType.Date, "Shipping"),
        new("FC Date", FieldType.Date, "Shipping"),
        new("Vendor Code", FieldType.Text, "Supplier"),
        new("Supplier Name", FieldType.Text, "Supplier", "Pengirim"),
        new("Supplier Country", FieldType.Text, "Supplier"),
        new("Invoice No", FieldType.Text, "Commercial"),
        new("Invoice Date", FieldType.Date, "Commercial"),
        new("Incoterm", FieldType.Text, "Commercial"),
        new("PO No", FieldType.Text, "Commercial", "No PO"),
        new("PO Item", FieldType.Text, "Commercial", "Line"),
        new("STO BM", FieldType.Text, "Commercial"),
        new("STO BM Date", FieldType.Date, "Commercial"),
        new("STO OA", FieldType.Text, "Commercial"),
        new("STO OA Date", FieldType.Date, "Commercial"),
        new("WBS Element", FieldType.Text, "Commercial"),
        new("Approp. Request", FieldType.Text, "Commercial"),
        new("Plant PO", FieldType.Text, "Commercial"),
        new("AFCE No", FieldType.Text, "Commercial"),
        new("NoHS", FieldType.Text, "Item", "HS Code"),
        new("Material Code", FieldType.Text, "Item", "Kode Barang"),
        new("Commodity", FieldType.Text, "Item", "Nama Barang"),
        new("M.Type", FieldType.Text, "Item"),
        new("Negara Asal Barang", FieldType.Text, "Item"),
        new("Qty", FieldType.Number, "Item", "Jumlah"),
        new("Unit", FieldType.Text, "Item", "Satuan"),
        new("Ccy P", FieldType.Text, "Valuation", "Valuta"),
        new("Harga Per Uom", FieldType.Number, "Valuation"),
        new("Inv After Adjustment", FieldType.Number, "Valuation"),
        new("Ccy Inv", FieldType.Text, "Valuation"),
        new("Nilai PIB", FieldType.Number, "Valuation"),
        new("Ccy PIB", FieldType.Text, "Valuation"),
        new("Freight Cost", FieldType.Number, "Valuation", "Nilai Freight"),
        new("Insurance Cost", FieldType.Number, "Valuation", "Nilai Asuransi"),
        new("Peraturan Dirjen", FieldType.Text, "Valuation"),
        new("(F) CIF Amount", FieldType.Number, "Valuation", "Harga"),
        new("Kurs PIB", FieldType.Number, "Valuation"),
        new("(IDR) CIF Amount", FieldType.Number, "Valuation"),
        new("BM Rate", FieldType.Text, "Duty rates"),
        new("PPN Rate", FieldType.Text, "Duty rates"),
        new("PPN BM Rate", FieldType.Text, "Duty rates"),
        new("PPH Rate", FieldType.Text, "Duty rates"),
        new("Amount After Facility / BM", FieldType.Number, "Amount After Facility"),
        new("Amount After Facility / Total Bea Masuk", FieldType.Number, "Amount After Facility"),
        new("Amount After Facility / PPN", FieldType.Number, "Amount After Facility"),
        new("Amount After Facility / Total PPN", FieldType.Number, "Amount After Facility"),
        new("Amount After Facility / PPH", FieldType.Number, "Amount After Facility"),
        new("Amount After Facility / Total PPH", FieldType.Number, "Amount After Facility"),
        new("MFN", FieldType.Text, "Duty rates"),
        new("Amount Before Facility / BM", FieldType.Number, "Amount Before Facility"),
        new("Amount Before Facility / Total Bea Masuk", FieldType.Number, "Amount Before Facility"),
        new("Amount Before Facility / PPN", FieldType.Number, "Amount Before Facility"),
        new("Amount Before Facility / Total PPN", FieldType.Number, "Amount Before Facility"),
        new("Amount Before Facility / PPH", FieldType.Number, "Amount Before Facility"),
        new("Amount Before Facility / Total PPH", FieldType.Number, "Amount Before Facility"),
        new("Save Duty / BM", FieldType.Number, "Save Duty"),
        new("Save Duty / Total Bea Masuk", FieldType.Number, "Save Duty"),
        new("Save Duty / PPN", FieldType.Number, "Save Duty"),
        new("Save Duty / Total PPN", FieldType.Number, "Save Duty"),
        new("Save Duty / PPH", FieldType.Number, "Save Duty"),
        new("Save Duty / Total PPH", FieldType.Number, "Save Duty"),
        new("Nilai SSPCP", FieldType.Number, "Payment"),
        new("No SSPCP", FieldType.Text, "Payment"),
        new("No NPTN", FieldType.Text, "Payment"),
        new("Bank Pembayar", FieldType.Text, "Payment"),
        new("O/N On Behalf", FieldType.Text, "Payment"),
        new("Map No", FieldType.Text, "Payment"),
    };

    public static readonly Field[] Bc40 =
    {
        new("Location", FieldType.Text, "Location"),
        new("Status KB", FieldType.Text, "Location"),
        new("TPB No.", FieldType.Text, "Location"),
        new("Type", FieldType.Text, "Document", "Jenis BC"),
        new("BC \"Aju\" / No", FieldType.Text, "BC \"Aju\""),
        new("BC \"Aju\" / Date", FieldType.Date, "BC \"Aju\""),
        new("Document BC / No", FieldType.Text, "Document BC", "NoPen"),
        new("Document BC / Date", FieldType.Date, "Document BC"),
        new("Vendor / Code", FieldType.Text, "Vendor"),
        new("Vendor Name", FieldType.Text, "Vendor", "Pengirim"),
        new("Vendor Address", FieldType.Text, "Vendor"),
        new("Vendor NPWP", FieldType.Text, "Vendor"),
        new("Customer / Code", FieldType.Text, "Customer"),
        new("Customer Name", FieldType.Text, "Customer"),
        new("Customer Address", FieldType.Text, "Customer"),
        new("Customer NPWP", FieldType.Text, "Customer"),
        new("Material / Code", FieldType.Text, "Material", "Kode Barang"),
        new("Material / Description", FieldType.Text, "Material", "Nama Barang"),
        new("Plant", FieldType.Text, "Material"),
        new("Contract / No", FieldType.Text, "Contract"),
        new("Contract / Date", FieldType.Date, "Contract"),
        new("Contract / Incoterm", FieldType.Text, "Contract"),
        new("Contract / (unlabelled)", FieldType.Text, "Contract"),
        new("Purchase Order / No", FieldType.Text, "Purchase Order", "No PO"),
        new("Purchase Order / Date", FieldType.Date, "Purchase Order"),
        new("Purchase Order / Qty", FieldType.Number, "Purchase Order"),
        new("Purchase Order / UOM", FieldType.Text, "Purchase Order"),
        new("Purchase Order / Ccy", FieldType.Text, "Purchase Order"),
        new("Purchase Order / Price (excl PPN)", FieldType.Number, "Purchase Order"),
        new("Purchase Order / DPP", FieldType.Number, "Purchase Order"),
        new("Purchase Order / PPN", FieldType.Number, "Purchase Order"),
        new("Purchase Order / Total", FieldType.Number, "Purchase Order"),
        new("Purchase Order / Type PPN", FieldType.Text, "Purchase Order"),
        new("TP", FieldType.Text, "Logistics"),
        new("GR No", FieldType.Text, "Logistics"),
        new("GR Date", FieldType.Date, "Logistics"),
        new("DO / No", FieldType.Text, "Logistics"),
        new("BL NO/ DO NO", FieldType.Text, "Logistics"),
        new("STO / Langsir", FieldType.Text, "Logistics"),
        new("Transportation", FieldType.Text, "Logistics"),
        new("BC / Qty", FieldType.Number, "BC", "Jumlah Barang"),
        new("BC / UOM", FieldType.Text, "BC", "Satuan Barang"),
        new("BC / Ccy", FieldType.Text, "BC"),
        new("BC / Amount", FieldType.Number, "BC", "Nilai"),
        new("BC / Tolerance", FieldType.Text, "BC"),
        new("BC / Gross Weight", FieldType.Number, "BC"),
        new("BC / Net Weight", FieldType.Number, "BC"),
        new("Realization of Good Receipts / Start Date", FieldType.Date, "Realization of Good Receipts"),
        new("Realization of Good Receipts / Complete Date", FieldType.Date, "Realization of Good Receipts"),
        new("Realization of Good Receipts / Delivery Qty", FieldType.Number, "Realization of Good Receipts"),
        new("Realization of Good Receipts / Complete Qty", FieldType.Number, "Realization of Good Receipts"),
        new("Realization of Good Receipts / (+/-)", FieldType.Number, "Realization of Good Receipts"),
        new("Remarks", FieldType.Text, "Other"),
        new("FP1 Date", FieldType.Date, "Tax invoice"),
        new("FP1 No", FieldType.Text, "Tax invoice"),
    };

    // Default view: customs-reporting column set, in the order BC users read it.
    public static readonly string[] PibDefaults =
    {
        "Tipe PIB", "No. PIB", "PibDate", "Supplier Name", "Incoterm", "PO No", "PO Item",
        "NoHS", "Material Code", "Commodity", "Qty", "Unit", "Ccy P",
        "Freight Cost", "Insurance Cost", "(F) CIF Amount"
    };

    public static readonly string[] Bc40Defaults =
    {
        "Type", "Document BC / No", "Vendor Name", "Material / Code", "Material / Description",
        "Purchase Order / No", "BC / Qty", "BC / UOM", "BC / Amount"
    };

    public static readonly string[] PibSearch =
        { "No. PIB", "No.Pengajuan PIB", "Supplier Name", "Commodity", "Material Code", "NoHS", "Invoice No", "PO No" };

    public static readonly string[] Bc40Search =
        { "Document BC / No", "BC \"Aju\" / No", "Vendor Name", "Material / Description", "Material / Code", "Purchase Order / No", "GR No" };

    // ---------- Laporan WIP (work in progress stock) ----------
    public static readonly Field[] Wip =
    {
        new("Kode Barang", FieldType.Text, "Item"),
        new("Nama Barang", FieldType.Text, "Item"),
        new("Sat", FieldType.Text, "Item"),
        new("Jumlah", FieldType.Number, "Stock"),
        new("Keterangan", FieldType.Text, "Other"),
    };

    // ---------- Laporan Aset dan Sparepart (mutation) ----------
    public static readonly Field[] Aset =
    {
        new("Kode Barang", FieldType.Text, "Item"),
        new("Nama Barang", FieldType.Text, "Item"),
        new("Sat", FieldType.Text, "Item"),
        new("Saldo Awal", FieldType.Number, "Mutasi"),
        new("Pemasukan", FieldType.Number, "Mutasi"),
        new("Pengeluaran", FieldType.Number, "Mutasi"),
        new("Penyesuaian (Adj)", FieldType.Number, "Mutasi"),
        new("Saldo Akhir", FieldType.Number, "Mutasi"),
        new("Stock Opname", FieldType.Number, "Opname"),
        new("Selisih", FieldType.Number, "Opname"),
        new("Keterangan", FieldType.Text, "Other"),
    };

    // ---------- Laporan Bahan Baku / Barang Jadi (identical layout, 14 cols) ----------
    public static readonly Field[] Mutasi =
    {
        new("Kode Barang", FieldType.Text, "Item"),
        new("Nama Barang", FieldType.Text, "Item"),
        new("Sat", FieldType.Text, "Item"),
        new("Saldo Awal", FieldType.Number, "Mutasi"),
        new("Pemasukan", FieldType.Number, "Mutasi"),
        new("Pemasukan WIP", FieldType.Number, "Mutasi"),
        new("Pengeluaran", FieldType.Number, "Mutasi"),
        new("Pengeluaran WIP", FieldType.Number, "Mutasi"),
        new("Penyesuaian (Adj)", FieldType.Number, "Mutasi"),
        new("Saldo Akhir", FieldType.Number, "Mutasi"),
        new("Stock Opname", FieldType.Number, "Opname"),
        new("Selisih", FieldType.Number, "Opname"),
        new("Keterangan", FieldType.Text, "Other"),
    };

    // ---------- Laporan BC 3.0 (Pengeluaran Barang / PEB export) ----------
    public static readonly Field[] Bc30 =
    {
        new("Jenis Dok.", FieldType.Text, "Document", "Jenis BC"),
        new("Dok. Pabean / Nomor", FieldType.Text, "Dok. Pabean", "NoPen"),
        new("Dok. Pabean / Tanggal", FieldType.Date, "Dok. Pabean", "Tanggal Nopen"),
        new("Bukti Penerimaan / Nomor", FieldType.Text, "Bukti Penerimaan"),
        new("Bukti Penerimaan / Tanggal", FieldType.Date, "Bukti Penerimaan"),
        new("Bukti Penerimaan / Tgl buka", FieldType.Date, "Bukti Penerimaan"),
        new("Dokumen / Nomor", FieldType.Text, "Dokumen"),
        new("Dokumen / (unlabelled)", FieldType.Text, "Dokumen"),
        new("Dokumen / Tanggal", FieldType.Date, "Dokumen"),
        new("Penerima / Pembeli", FieldType.Text, "Counterparty", "Penerima"),
        new("Kode Barang", FieldType.Text, "Item"),
        new("Nama Barang", FieldType.Text, "Item"),
        new("Terms", FieldType.Text, "Commercial"),
        new("Sat", FieldType.Text, "Item", "Satuan"),
        new("Jumlah", FieldType.Number, "Item"),
        new("Nilai Barang / Mata Uang", FieldType.Text, "Nilai Barang", "Valuta"),
        new("Nilai Barang / Nilai", FieldType.Number, "Nilai Barang", "Nilai"),
        new("Freight/Delivery Cost", FieldType.Number, "Nilai Barang", "Nilai Freight"),
        new("Insurance", FieldType.Number, "Nilai Barang", "Nilai Asuransi"),
    };

    public static readonly string[] WipDefaults = { "Kode Barang", "Nama Barang", "Sat", "Jumlah", "Keterangan" };
    public static readonly string[] AsetDefaults =
        { "Kode Barang", "Nama Barang", "Sat", "Saldo Awal", "Pemasukan", "Pengeluaran", "Penyesuaian (Adj)", "Saldo Akhir", "Stock Opname", "Selisih" };
    public static readonly string[] MutasiDefaults =
        { "Kode Barang", "Nama Barang", "Sat", "Saldo Awal", "Pemasukan", "Pemasukan WIP", "Pengeluaran", "Pengeluaran WIP", "Penyesuaian (Adj)", "Saldo Akhir", "Stock Opname", "Selisih" };
    public static readonly string[] Bc30Defaults =
    {
        "Jenis Dok.", "Dok. Pabean / Nomor", "Dok. Pabean / Tanggal", "Penerima / Pembeli",
        "Kode Barang", "Nama Barang", "Terms", "Sat", "Jumlah",
        "Nilai Barang / Mata Uang", "Nilai Barang / Nilai", "Freight/Delivery Cost", "Insurance"
    };

    private static readonly string[] StockSearch = { "Kode Barang", "Nama Barang" };
    private static readonly string[] Bc30Search =
        { "Dok. Pabean / Nomor", "Bukti Penerimaan / Nomor", "Dokumen / Nomor", "Penerima / Pembeli", "Kode Barang", "Nama Barang" };

    /// <summary>
    /// Page = which screen hosts the report: "reports" (customs documents) or "movement".
    /// NameHints disambiguate templates whose column headers are identical — the only signal
    /// left is the sheet or file name (Bahan Baku vs Barang Jadi; Aset dan Sparepart vs Scraps).
    /// </summary>
    public record Report(string Key, string Title, string Template, Field[] Fields, string[] Defaults,
                         string[] SearchFields, string Page = "reports", string[]? NameHints = null);

    public static readonly Report[] Reports =
    {
        new("pib-import", "Pemasukan Barang — PIB Import (BC 2.3)", "BC23", Pib, PibDefaults, PibSearch),
        new("bc40-receipt", "Pemasukan Barang — BC 4.0", "BC40", Bc40, Bc40Defaults, Bc40Search),
        new("bc30-export", "Pengeluaran Barang — BC 3.0 (PEB)", "BC30", Bc30, Bc30Defaults, Bc30Search),
        new("wip", "Laporan WIP", "WIP", Wip, WipDefaults, StockSearch, "movement",
            new[] { "WIP" }),
        new("bahan-baku", "Laporan Bahan Baku", "BAHANBAKU", Mutasi, MutasiDefaults, StockSearch, "movement",
            new[] { "BAHAN BAKU" }),
        new("barang-jadi", "Laporan Barang Jadi", "BARANGJADI", Mutasi, MutasiDefaults, StockSearch, "movement",
            new[] { "BARANG JADI" }),
        new("aset-sparepart", "Laporan Aset dan Sparepart", "ASET", Aset, AsetDefaults, StockSearch, "movement",
            new[] { "ASET", "SPAREPART" }),
        // same 12-column layout as Aset dan Sparepart — told apart only by name
        new("scraps", "Laporan Scraps", "SCRAP", Aset, AsetDefaults, StockSearch, "movement",
            new[] { "SCRAP" }),
    };

    public static Report? Get(string key) => Reports.FirstOrDefault(r => r.Key == key);
    public static Report? ByTemplate(string template) => Reports.FirstOrDefault(r => r.Template == template);
}
