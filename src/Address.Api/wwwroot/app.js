// This page is a local demonstration client. The HTTP API owns validation and
// matching decisions; the browser only renders the returned result contract.
const $ = id => document.getElementById(id);
const singleTab = $('tab-single');
const bulkTab = $('tab-bulk');
const searchInput = $('address-input');
let suggestTimer;
let suggestController;

/** Switch between synchronous lookup and NDJSON bulk workflows. */
function switchTab(bulk) {
  singleTab.classList.toggle('active', !bulk);
  bulkTab.classList.toggle('active', bulk);
  singleTab.setAttribute('aria-selected', String(!bulk));
  bulkTab.setAttribute('aria-selected', String(bulk));
  $('single-panel').hidden = bulk;
  $('bulk-panel').hidden = !bulk;
}
singleTab.addEventListener('click', () => switchTab(false));
bulkTab.addEventListener('click', () => switchTab(true));
$('nav-bulk').addEventListener('click', event => {
  event.preventDefault();
  switchTab(true);
  $('bulk-panel').scrollIntoView({ behavior: 'smooth', block: 'start' });
});

/** Convert structured API errors into one readable browser message. */
async function api(path, options) {
  const response = await fetch(path, options);
  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    throw new Error(payload.error?.message || `Request failed (${response.status})`);
  }
  return response.json();
}

api('/v1/metadata').then(data => {
  const version = data.dataset?.version || 'No active release';
  $('dataset-label').textContent = `G-NAF ${version}`;
  $('release-fact').textContent = version;
}).catch(() => { $('dataset-label').textContent = 'Dataset unavailable'; });

function message(id, text, error = false) {
  const node = $(id);
  node.textContent = text;
  node.classList.toggle('error', error);
}

function hideSuggestions() { $('suggestions').hidden = true; $('suggestions').replaceChildren(); }

searchInput.addEventListener('input', () => {
  // Debounce and cancel the previous suggestion request so an older response
  // cannot overwrite results for the user's newer text.
  clearTimeout(suggestTimer);
  suggestController?.abort();
  hideSuggestions();
  const query = searchInput.value.trim();
  if (query.length < 3) return;
  suggestTimer = setTimeout(async () => {
    suggestController = new AbortController();
    try {
      const data = await api(`/v1/addresses/suggest?q=${encodeURIComponent(query)}&limit=6`, { signal: suggestController.signal });
      if (query !== searchInput.value.trim()) return;
      const list = $('suggestions');
      list.replaceChildren();
      for (const suggestion of data.suggestions) {
        const item = document.createElement('button');
        item.type = 'button';
        item.className = 'suggestion';
        item.setAttribute('role', 'option');
        item.textContent = suggestion.label;
        item.addEventListener('click', () => {
          searchInput.value = suggestion.label;
          hideSuggestions();
          showId(suggestion.id);
        });
        list.append(item);
      }
      list.hidden = list.childElementCount === 0;
    } catch (error) { if (error.name !== 'AbortError') message('single-message', error.message, true); }
  }, 260);
});

document.addEventListener('click', event => {
  if (!event.target.closest('.input-wrap')) hideSuggestions();
});

/** Render a canonical G-NAF row returned by the ID endpoint. */
function showResult(data, confidence, badge) {
  $('result-card').hidden = false;
  $('result-empty').hidden = true;
  $('result-badge').textContent = badge;
  $('result-confidence').textContent = confidence == null ? 'Found in the national G-NAF dataset' : `${Math.round(confidence * 100)}% match score in the national dataset`;
  $('result-address').textContent = data.address?.formatted || 'Address found';
  $('result-id').textContent = data.id || '—';
  $('result-version').textContent = data.source?.version || '—';
  const lat = data.location?.latitude, lon = data.location?.longitude;
  $('result-location').textContent = lat == null || lon == null ? 'Unavailable' : `${lat.toFixed(5)}, ${lon.toFixed(5)}`;
  const components = data.components || {};
  const fields = [
    ['Building', components.buildingName],
    ['Unit', [components.flatType, components.flatNumber].filter(Boolean).join(' ')],
    ['Floor', [components.levelType, components.levelNumber].filter(Boolean).join(' ')],
    ['Street number', [components.numberFirst, components.numberLast].filter(Boolean).join('–')],
    ['Street name', components.streetName],
    ['Street type', components.streetType],
    ['Locality', components.locality],
    ['State', components.state],
    ['Postcode', components.postcode]
  ];
  const list = $('result-components');
  list.replaceChildren();
  for (const [label, value] of fields.filter(([, value]) => value)) {
    const field = document.createElement('div');
    const name = document.createElement('span');
    const content = document.createElement('strong');
    name.textContent = label;
    content.textContent = value;
    field.append(name, content);
    list.append(field);
  }
}
$('result-copy').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText($('result-address').textContent);
    $('result-copy').textContent = 'Copied';
    setTimeout(() => { $('result-copy').textContent = 'Copy'; }, 1800);
  } catch { message('single-message', 'Copy is unavailable in this browser.', true); }
});

async function showId(id, confidence = null, badge = 'G-NAF address') {
  message('single-message', 'Loading address…');
  try {
    const data = await api(`/v1/addresses/${encodeURIComponent(id)}`);
    showResult(data, confidence, badge);
    message('single-message', '');
  } catch (error) { message('single-message', error.message, true); }
}

$('search-form').addEventListener('submit', async event => {
  // Validation decides whether the result is confident; an ID lookup then
  // provides the complete canonical row for the detail card.
  event.preventDefault();
  clearTimeout(suggestTimer);
  suggestController?.abort();
  hideSuggestions();
  const address = searchInput.value.trim();
  if (!address) return;
  $('result-card').hidden = true;
  message('single-message', 'Checking the national dataset…');
  try {
    const data = await api('/v1/addresses/validate', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ address })
    });
    if (data.valid && data.gnaf?.id) {
      await showId(data.gnaf.id, data.validation.confidence, 'G-NAF matched');
    } else {
      message('single-message', 'No confident address match. Try the full street number, locality, and postcode.');
    }
  } catch (error) { message('single-message', error.message, true); }
});

const bulkInput = $('bulk-input');
const bulkFile = $('bulk-file');
// Clear identifiers saved by earlier versions; this page never keeps a job history.
try { localStorage.removeItem('addressDeskRecentBulkJobs'); } catch { /* Storage may be disabled. */ }
$('bulk-operation').addEventListener('change', () => {
  $('bulk-suggest-options').hidden = $('bulk-operation').value !== 'SUGGEST';
});
function lines() { return bulkInput.value.split(/\r?\n/).map(x => x.trim()).filter(Boolean); }
bulkInput.addEventListener('input', () => {
  const count = lines().length;
  $('bulk-count').textContent = `${count.toLocaleString()} ${count === 1 ? 'line' : 'lines'}`;
});
bulkFile.addEventListener('change', () => {
  if (bulkFile.files.length) $('bulk-count').textContent = bulkFile.files[0].name;
  else bulkInput.dispatchEvent(new Event('input'));
});

$('bulk-form').addEventListener('submit', async event => {
  // Each text line becomes one NDJSON object with the field expected by the
  // chosen operation. The server applies its own input and size limits.
  event.preventDefault();
  const file = bulkFile.files[0];
  const entries = file ? [] : lines();
  if (!file && !entries.length) { message('bulk-message', 'Add an NDJSON file or paste an address list.', true); return; }
  if (file && (file.size === 0 || file.size > 104857600)) { message('bulk-message', 'Choose a non-empty NDJSON file up to 100 MB.', true); return; }
  const operation = $('bulk-operation').value;
  const params = new URLSearchParams({ operation });
  if (operation === 'SUGGEST') {
    const minScore = $('bulk-min-score').value;
    const limit = $('bulk-limit').value;
    if (minScore && (Number(minScore) < 0 || Number(minScore) > 1)) { message('bulk-message', 'Minimum score must be from 0 to 1.', true); return; }
    if (!Number.isInteger(Number(limit)) || Number(limit) < 1 || Number(limit) > 20) { message('bulk-message', 'Candidate count must be from 1 to 20.', true); return; }
    if (minScore) params.set('minScore', minScore);
    params.set('limit', limit);
  }
  const field = operation === 'ID' ? 'id' : 'address';
  const ndjson = file || entries.map(value => JSON.stringify({ [field]: value })).join('\n') + '\n';
  const submit = $('bulk-form').querySelector('button[type=submit]');
  submit.disabled = true;
  $('bulk-result').hidden = true;
  message('bulk-message', 'Uploading list…');
  try {
    const data = await api(`/v1/bulk?${params}`, {
      method: 'POST', headers: { 'Content-Type': 'application/x-ndjson' }, body: ndjson
    });
    $('bulk-result').hidden = false;
    $('bulk-download').hidden = true;
    $('bulk-preview').replaceChildren();
    message('bulk-message', 'List queued. Processing will begin shortly.');
    await followJob(data.jobId);
  } catch (error) { message('bulk-message', error.message, true); }
  finally { submit.disabled = false; }
});

/** Poll a durable job until the worker publishes a terminal outcome. */
async function followJob(id) {
  for (;;) {
    const job = await api(`/v1/bulk/${id}`);
    $('bulk-status').textContent = job.status;
    $('bulk-status').classList.toggle('negative', job.status === 'FAILED');
    $('bulk-progress').textContent = `${job.progress.processed.toLocaleString()} processed · ${job.progress.failed.toLocaleString()} input errors`;
    if (job.status === 'COMPLETED') {
      const link = $('bulk-download');
      link.href = job.result.downloadUrl;
      link.hidden = false;
      message('bulk-message', 'Processing complete. Download the full result below.');
      if (job.progress.processed <= 1000) await previewResult(job.result.downloadUrl);
      return;
    }
    if (job.status === 'FAILED' || job.status === 'CANCELLED') {
      message('bulk-message', job.errorMessage || 'The job did not complete.', true);
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 1200));
  }
}

/** Preview only small completed outputs; the download link serves the full file. */
async function previewResult(url) {
  const response = await fetch(url);
  if (!response.ok) return;
  const text = await response.text();
  const preview = $('bulk-preview');
  for (const line of text.split(/\r?\n/).filter(Boolean).slice(0, 5)) {
    const row = document.createElement('div');
    row.className = 'preview-row';
    try {
      const data = JSON.parse(line);
      const topSuggestion = data.suggestions?.[0];
      row.textContent = `Row ${data.row} · ${data.status} · ${topSuggestion ? `${data.suggestions.length} candidates · ${topSuggestion.formattedAddress} (${topSuggestion.score.toFixed(2)})` : data.formattedAddress || data.id || data.error || ''}`;
    } catch { row.textContent = line; }
    preview.append(row);
  }
}
