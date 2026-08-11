import ReportBrowser from '../components/ReportBrowser'

export default function Reports() {
  return (
    <ReportBrowser
      page="reports"
      title="Reports — Customs Documents"
      crumb="Reports · Pemasukan (BC 2.3 / BC 4.0) &amp; Pengeluaran (BC 3.0) · hover a column header for its upload field"
      dateLabels={['From', 'To']}
      searchHint="e.g. olein, 434951, 8481.20.20"
    />
  )
}
