import ReportBrowser from '../components/ReportBrowser'

export default function Movement() {
  return (
    <ReportBrowser
      page="movement"
      title="Inventory Movement"
      crumb="Periodic stock &amp; mutation reports · WIP · Bahan Baku &amp; Penolong · Barang Jadi · Sparepart · Aset · Scraps"
      dateLabels={['Period from', 'Period to']}
      searchHint="e.g. 912.001.006, BLEACHING EARTH, RBDPO"
    />
  )
}
