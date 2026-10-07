(() => {
  // Catalogue entries are read-only until an operator selects an exact version.
  // The server resolves that version from the official catalogue again on POST.
  const check = document.getElementById('check-releases');
  const checkLatest = document.getElementById('check-latest');
  const select = document.getElementById('release-select');
  const result = document.getElementById('latest-result');
  const prepare = document.getElementById('prepare-form');
  const prepareVersion = document.getElementById('prepare-version');
  const formatSize = bytes => `${(bytes / 1024 ** 3).toFixed(2)} GiB`;
  const releaseName = version => {
    const match = /^(\d{4})_(\d{2})$/.exec(version);
    if (!match) return version;
    const month = Number(match[2]);
    return month >= 1 && month <= 12
      ? `${new Date(Date.UTC(Number(match[1]), month - 1, 1)).toLocaleString('en-AU', { month: 'long', timeZone: 'UTC' })} ${match[1]}`
      : version;
  };

  let releases = [];
  let activeVersion = null;
  let latestVersion = null;
  const describeSelection = () => {
    const release = releases.find(item => item.version === select.value);
    prepare.hidden = true;
    prepareVersion.value = '';
    if (!release) {
      result.textContent = 'Choose a release to see its status.';
      return;
    }
    const name = `${release.version === latestVersion ? 'Latest official release: ' : ''}${releaseName(release.version)}`;
    if (release.installedStatus === 'ACTIVE') {
      result.textContent = `${name} is live now.`;
    } else if (release.installedStatus === 'READY') {
      result.textContent = `${name} is already prepared. Review it in step 3.`;
    } else if (release.installedStatus === 'RETIRED') {
      result.textContent = `${name} is retained for rollback and does not need another import.`;
    } else {
      const older = activeVersion && release.version < activeVersion;
      result.textContent = `${name} is available${release.size ? ` (${formatSize(release.size)} download)` : ''}. ` +
        (older ? `This is older than the live release, so preparing it is a historical replay. The live data stays in service until you review and activate it.` :
          'Preparing it will not switch the live service.');
      prepareVersion.value = release.version;
      prepare.hidden = document.body.dataset.jobActive === 'true';
      if (document.body.dataset.jobActive === 'true') result.textContent += ' Wait for the current preparation to finish.';
    }
  };
  const loadReleases = async () => {
    check.disabled = true;
    checkLatest.disabled = true;
    result.textContent = 'Loading current and archived releases from data.gov.au…';
    prepare.hidden = true;
    try {
      const response = await fetch('/api/releases', { cache: 'no-store' });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error || 'Could not load the official releases.');
      releases = data.releases || [];
      activeVersion = data.activeVersion;
      const selected = select.value;
      const placeholder = document.createElement('option');
      placeholder.value = '';
      placeholder.textContent = 'Choose a release…';
      select.replaceChildren(placeholder, ...releases.map(release => {
        const option = document.createElement('option');
        option.value = release.version;
        option.textContent = `${releaseName(release.version)}${release.installedStatus ? ` · ${release.installedStatus.toLowerCase()}` : ''}`;
        return option;
      }));
      select.disabled = releases.length === 0;
      select.value = releases.some(release => release.version === selected) ? selected : '';
      if (releases.length) describeSelection();
      else result.textContent = 'No national GDA2020 ZIP releases were found in the official catalogues.';
    } catch (error) {
      result.textContent = error.message || 'Could not check for an update.';
      select.disabled = true;
    } finally {
      check.disabled = false;
      checkLatest.disabled = false;
    }
  };
  check?.addEventListener('click', loadReleases);
  checkLatest?.addEventListener('click', async () => {
    checkLatest.disabled = true;
    result.textContent = 'Checking the newest official G-NAF release…';
    try {
      const response = await fetch('/api/latest', { cache: 'no-store' });
      const latest = await response.json();
      if (!response.ok) throw new Error(latest.error || 'Could not check the latest release.');
      latestVersion = latest.version;
      if (!releases.some(release => release.version === latestVersion)) await loadReleases();
      if (releases.some(release => release.version === latestVersion)) {
        select.value = latestVersion;
        describeSelection();
      } else {
        result.textContent = `Latest official release: ${releaseName(latestVersion)}. Refresh the release list to select it.`;
      }
    } catch (error) {
      result.textContent = error.message || 'Could not check the latest release.';
    } finally {
      checkLatest.disabled = false;
    }
  });
  select?.addEventListener('change', describeSelection);
  if (check && select) loadReleases();

  document.querySelectorAll('form[data-confirm]').forEach(form => {
    // The confirmation is usability guidance. Server authorization, CSRF and
    // release-state validation remain the actual action safeguards.
    form.addEventListener('submit', event => {
      if (!window.confirm(form.dataset.confirm)) event.preventDefault();
      else form.querySelector('[type="submit"]')?.setAttribute('disabled', '');
    });
  });

  const clusterPanel = document.querySelector('.cluster-panel');
  if (clusterPanel) {
    const badge = document.getElementById('cluster-badge');
    const detail = document.getElementById('cluster-detail');
    const nodes = document.getElementById('cluster-nodes');
    const coordinators = document.getElementById('cluster-coordinators');
    const lag = document.getElementById('cluster-lag');
    const apiDot = document.getElementById('api-status-dot');
    const apiText = document.getElementById('api-status-text');
    const apiDetail = document.getElementById('api-status-detail');
    const replicaPanel = document.querySelector('[data-api-state]');
    const replicaBadge = document.getElementById('api-replicas-badge');
    const replicaDetail = document.getElementById('api-replicas-detail');
    const replicaNodes = document.getElementById('api-replica-nodes');
    const roleText = role => ({ primary: 'Serving addresses', standby: 'Ready to take over', offline: 'Not responding' })[role] || role;
    /** Refresh observed node state without reloading the operator's page. */
    const pollSystem = async () => {
      try {
        const response = await fetch('/api/system', { cache: 'no-store' });
        if (!response.ok) throw new Error('Status unavailable');
        const { cluster, apiReady, apiReplicas } = await response.json();
        clusterPanel.dataset.clusterState = cluster.state;
        badge.className = `cluster-badge ${cluster.state}`;
        badge.textContent = cluster.headline;
        detail.textContent = cluster.detail;
        coordinators.textContent = `Coordinators: ${cluster.coordinatorsOnline}/${cluster.coordinatorsTotal} online`;
        const primary = cluster.databases.find(node => node.role === 'primary');
        lag.textContent = `Replica: ${primary?.replayLagBytes == null ? 'Not available' :
          primary.replayLagBytes === 0 ? 'Caught up' : `${Math.round(primary.replayLagBytes / 1024)} KiB behind`}`;
        // Build nodes with textContent, including host names from service
        // configuration, so status data cannot become executable markup.
        const cards = cluster.databases.map(node => {
          const card = document.createElement('div');
          card.className = 'cluster-node';
          card.dataset.host = node.host;
          card.dataset.role = node.role;
          const name = document.createElement('span');
          name.className = 'cluster-node-name';
          name.textContent = node.name;
          const role = document.createElement('strong');
          role.className = 'cluster-node-role';
          role.textContent = roleText(node.role);
          const nodeDetail = document.createElement('span');
          nodeDetail.className = 'cluster-node-detail';
          nodeDetail.textContent = `${node.host} · ${node.online ? 'Online' : 'Offline'}`;
          card.append(name, role, nodeDetail);
          return card;
        });
        nodes.replaceChildren(...cards);
        apiDot.className = `health-dot ${apiReady ? 'good' : 'bad'}`;
        apiText.textContent = apiReady ? 'API responding' : 'API needs attention';
        apiDetail.textContent = apiReady ? 'Address lookups are available' : 'Check the API container and database';
        const readyCount = apiReplicas.filter(node => node.ready).length;
        const replicaState = readyCount === apiReplicas.length && readyCount > 1 ? 'healthy' :
          readyCount > 0 ? 'degraded' : 'unavailable';
        replicaPanel.dataset.apiState = replicaState;
        replicaBadge.className = `cluster-badge ${replicaState}`;
        replicaBadge.textContent = replicaState === 'healthy' ? 'Both API servers ready' :
          replicaState === 'degraded' ? 'One API server ready' : 'API servers unavailable';
        replicaDetail.textContent = `${readyCount} of ${apiReplicas.length} API servers ready. The gateway directs requests to ready servers.`;
        replicaNodes.replaceChildren(...apiReplicas.map(node => {
          const card = document.createElement('div');
          card.className = 'cluster-node';
          card.dataset.apiHost = node.host;
          card.dataset.role = node.ready ? 'ready' : 'offline';
          const name = document.createElement('span');
          name.className = 'cluster-node-name';
          name.textContent = node.name;
          const role = document.createElement('strong');
          role.className = 'cluster-node-role';
          role.textContent = node.ready ? 'Ready for requests' : 'Not responding';
          const detail = document.createElement('span');
          detail.className = 'cluster-node-detail';
          detail.textContent = `${node.host} · ${node.ready ? 'Online' : 'Offline'}`;
          card.append(name, role, detail);
          return card;
        }));
      } catch {
        // Status polling must not replace or submit release controls when a
        // replica probe fails; the next interval can recover the display.
        badge.className = 'cluster-badge degraded';
        badge.textContent = 'Status temporarily unavailable';
        detail.textContent = 'Refresh the page or check the local containers.';
      }
    };
    window.setInterval(pollSystem, 10000);
  }

  if (document.body.dataset.jobActive === 'true') {
    // Progress is advisory. The worker persists the durable stage and a page
    // reload reads it again; no release switch occurs from this poller.
    const fill = document.getElementById('progress-fill');
    const label = document.getElementById('progress-label');
    const stages = {
      WAITING: 'Waiting to start', DOWNLOADING: 'Downloading official archive',
      IMPORTING: 'Importing and checking national addresses', READY: 'Ready for review'
    };
    const datasetStages = {
      CREATING_SCHEMA: 'Preparing database tables', LOADING: 'Loading national addresses',
      TRANSFORMING: 'Building address search', BUILDING_INDEXES: 'Building search indexes',
      VALIDATING: 'Checking address quality', READY: 'Ready for review'
    };
    const poll = async () => {
      try {
        const response = await fetch('/api/maintenance', { cache: 'no-store' });
        if (!response.ok) throw new Error('Progress unavailable');
        const { job } = await response.json();
        if (!job || job.status === 'COMPLETED' || job.status === 'FAILED') {
          window.location.replace('/');
          return;
        }
        let message = datasetStages[job.datasetStage] || stages[job.stage] || 'Preparing release';
        if (job.stage === 'DOWNLOADING' && job.total && job.downloaded != null) {
          const percent = Math.min(100, Math.round(job.downloaded / job.total * 100));
          fill.style.width = `${percent}%`;
          message += ` · ${percent}%`;
        } else {
          fill.style.width = job.stage === 'WAITING' ? '8%' : '55%';
        }
        label.textContent = message;
      } catch {
        label.textContent = 'Progress temporarily unavailable. The live API is unaffected.';
      }
    };
    poll();
    window.setInterval(poll, 5000);
  }
})();
