// Filter the module table.
document.querySelectorAll('[data-filter]').forEach(input =>
  input.addEventListener('input', () => {
    const query = input.value.toLowerCase();
    document.querySelectorAll(input.dataset.filter).forEach(row =>
      row.hidden = !row.textContent.toLowerCase().includes(query));
  }));

// Show every moment in the reader's own zone.
//
// The page is served with UTC in both the attribute and the text, so it is
// already correct before this runs and stays correct with scripting off. What
// this adds is that an operator in CEST reads 17:04 instead of doing the
// arithmetic — and gets the zone spelled out, because a bare local time on a
// page that might be screenshotted for a ticket is worse than an explicit UTC.
//
// The datetime attribute is left alone: it is what a collector reads.
(() => {
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'local';
  const format = new Intl.DateTimeFormat(undefined, {
    dateStyle: 'short', timeStyle: 'medium'
  });

  document.querySelectorAll('time[data-utc]').forEach(element => {
    const instant = new Date(element.getAttribute('datetime'));
    if (isNaN(instant)) return;
    element.textContent = format.format(instant);
    element.title = element.getAttribute('datetime') + ' — shown in ' + zone;
  });

  const note = document.querySelector('[data-zone]');
  if (note) note.textContent = zone;
})();
