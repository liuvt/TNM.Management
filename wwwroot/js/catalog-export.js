// Download on direct click only. UTF-8 BOM enables Vietnamese CSV in Excel for Windows.
window.tnmDownloadCsv = function (fileName, content) {
    const blob = new Blob(['\uFEFF', content], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = String(fileName).replace(/[^a-zA-Z0-9_.-]/g, '_');
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
};

window.tnmScrollCatalogEditor = function () {
    document.getElementById('catalog-editor')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
};
