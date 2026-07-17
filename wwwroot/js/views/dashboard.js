window.Views = window.Views || {};
window.Views.dashboard = (function () {
  // Validated categorical palette (light mode), fixed slot order — color follows
  // the activity category, never its rank in a given chart.
  const CATEGORY_COLORS = {
    'Generated code': '#2a78d6',
    'Wrote tests':    '#1baf7a',
    'Refactored':     '#eda100',
    'Debugged':       '#008300',
    'Reviewed':       '#4a3aa7',
    'Wrote docs':     '#e34948',
    'Investigated':   '#e87ba4',
    'Uncategorised':  '#c3c2b7'
  };
  const BLUE = '#2a78d6';
  const GRID = '#e1e0d9';
  const INK_MUTED = '#898781';
  const SURFACE = '#ffffff';
  // Validated categorical hues in fixed slot order — assigned to models by name so a
  // model keeps the same colour across renders.
  const PALETTE = ['#2a78d6', '#1baf7a', '#eda100', '#008300', '#4a3aa7', '#e34948', '#e87ba4', '#eb6834'];

  let charts = [];
  let topMetric = 'tokens';
  let lastStats = null;

  function destroyCharts() {
    charts.forEach(c => c.destroy());
    charts = [];
  }

  const baseScales = {
    x: { grid: { display: false }, ticks: { color: INK_MUTED } },
    y: { grid: { color: GRID }, border: { display: false }, ticks: { color: INK_MUTED, precision: 0 } }
  };

  function makeChart(id, config) {
    const el = document.getElementById(id);
    if (!el) return;
    charts.push(new Chart(el, config));
  }

  function tile(label, value, sub) {
    return `<div class="panel tile">
      <div class="label">${label}</div>
      <div class="value">${value}</div>
      ${sub ? `<div class="muted" style="font-size:11.5px">${sub}</div>` : ''}
    </div>`;
  }

  async function load(el) {
    destroyCharts();
    let s;
    try {
      s = lastStats = await Bridge.call('stats.dashboard');
    } catch (e) {
      el.innerHTML = `<div class="panel empty">Failed to load stats: ${App.esc(e.message)}</div>`;
      return;
    }

    const hasData = s.weekly.length || s.activity.length;

    el.innerHTML = `<h1>Dashboard</h1>
      <div class="grid tiles">
        ${tile('Sessions this month', App.fmtNum(s.tiles.sessionsThisMonth))}
        ${tile('Tickets touched this month', App.fmtNum(s.tiles.ticketsThisMonth))}
        ${tile('Tokens this month', App.fmtNum(s.tiles.tokensThisMonth), 'input + output, cache reads excluded')}
        ${tile('Sessions needing review', App.fmtNum(s.tiles.pendingReview), '<a href="#sessions">review queue →</a>')}
      </div>
      ${!hasData ? '<div class="panel empty">No data yet — run “Scan now” or add a manual entry.</div>' : `
      <div class="grid charts" style="margin-top:16px">
        <div class="panel"><h2>Token usage per week</h2>
          <div class="chart-box"><canvas id="ch-tokens"></canvas></div>
          <div class="footnote">input + output tokens (cache reads excluded).</div></div>
        <div class="panel"><h2>Claude model usage per week</h2>
          <div class="chart-box"><canvas id="ch-models"></canvas></div>
          <div class="footnote">Sessions per model each week.</div></div>
        <div class="panel"><h2>AI-assisted tickets per week</h2>
          <div class="chart-box"><canvas id="ch-weekly"></canvas></div></div>
        <div class="panel"><h2>What the AI did</h2>
          <div class="chart-box"><canvas id="ch-activity"></canvas></div>
          <div class="footnote">Manual categories + inferred from session tool use (manual wins on overlap).</div></div>
        <div class="panel"><h2>Top tickets
          <span style="float:right">
            <button class="btn btn-small ${topMetric === 'tokens' ? 'active btn-primary' : ''}" onclick="Views.dashboard.setMetric('tokens')">tokens</button>
            <button class="btn btn-small ${topMetric === 'sessions' ? 'active btn-primary' : ''}" onclick="Views.dashboard.setMetric('sessions')">sessions</button>
          </span></h2>
          <div class="chart-box"><canvas id="ch-top"></canvas></div>
          <div class="footnote">Multi-ticket sessions count fully against each linked ticket.</div></div>
        <div class="panel"><h2>Ticket type × AI activity</h2>
          <div class="chart-box"><canvas id="ch-matrix"></canvas></div>
          <div class="footnote">Issue types appear after tickets are synced from JIRA.</div></div>
      </div>`}`;

    if (hasData) renderCharts(s);
  }

  function renderCharts(s) {
    makeChart('ch-tokens', {
      type: 'line',
      data: {
        labels: s.tokensWeekly.map(w => w.week),
        datasets: [{
          data: s.tokensWeekly.map(w => w.tokens),
          borderColor: BLUE,
          backgroundColor: 'rgba(79,109,245,0.12)',
          fill: true,
          tension: 0.3,
          borderWidth: 2,
          pointRadius: 3,
          pointBackgroundColor: BLUE
        }]
      },
      options: {
        maintainAspectRatio: false,
        plugins: {
          legend: { display: false },
          tooltip: { callbacks: { label: ctx => App.fmtNum(ctx.raw) + ' tokens' } }
        },
        scales: {
          x: { grid: { display: false }, ticks: { color: INK_MUTED } },
          y: { grid: { color: GRID }, border: { display: false }, ticks: { color: INK_MUTED, callback: v => App.fmtNum(v) } }
        }
      }
    });

    // Model usage per week — stacked bar, one series per Claude model.
    const modelWeeks = [...new Set(s.modelWeekly.map(r => r.week))];
    const models = [...new Set(s.modelWeekly.map(r => r.model))].sort();
    makeChart('ch-models', {
      type: 'bar',
      data: {
        labels: modelWeeks,
        datasets: models.map((m, i) => ({
          label: (m || 'unknown').replace('claude-', ''),
          data: modelWeeks.map(w => {
            const row = s.modelWeekly.find(r => r.week === w && r.model === m);
            return row ? row.sessions : 0;
          }),
          backgroundColor: PALETTE[i % PALETTE.length],
          borderColor: SURFACE,
          borderWidth: 2,
          borderRadius: 4,
          maxBarThickness: 26
        }))
      },
      options: {
        maintainAspectRatio: false,
        plugins: { legend: { position: 'bottom', labels: { color: '#52514e', boxWidth: 12 } } },
        scales: {
          x: { stacked: true, grid: { display: false }, ticks: { color: INK_MUTED } },
          y: { stacked: true, grid: { color: GRID }, border: { display: false }, ticks: { color: INK_MUTED, precision: 0 } }
        }
      }
    });

    makeChart('ch-weekly', {
      type: 'bar',
      data: {
        labels: s.weekly.map(w => w.week),
        datasets: [{
          data: s.weekly.map(w => w.tickets),
          backgroundColor: BLUE,
          borderRadius: 4,
          maxBarThickness: 26
        }]
      },
      options: {
        maintainAspectRatio: false,
        plugins: { legend: { display: false } },
        scales: baseScales
      }
    });

    makeChart('ch-activity', {
      type: 'doughnut',
      data: {
        labels: s.activity.map(a => a.category),
        datasets: [{
          data: s.activity.map(a => a.count),
          backgroundColor: s.activity.map(a => CATEGORY_COLORS[a.category] || '#c3c2b7'),
          borderColor: SURFACE,
          borderWidth: 2
        }]
      },
      options: {
        maintainAspectRatio: false,
        plugins: { legend: { position: 'right', labels: { color: '#52514e', boxWidth: 12 } } }
      }
    });

    const metric = topMetric;
    makeChart('ch-top', {
      type: 'bar',
      data: {
        labels: s.topTickets.map(t => t.key),
        datasets: [{
          data: s.topTickets.map(t => t[metric]),
          backgroundColor: BLUE,
          borderRadius: 4,
          maxBarThickness: 18
        }]
      },
      options: {
        indexAxis: 'y',
        maintainAspectRatio: false,
        plugins: {
          legend: { display: false },
          tooltip: { callbacks: { label: ctx => `${metric}: ${App.fmtNum(ctx.raw)}` } }
        },
        scales: {
          x: { grid: { color: GRID }, border: { display: false }, ticks: { color: INK_MUTED, callback: v => App.fmtNum(v) } },
          y: { grid: { display: false }, ticks: { color: INK_MUTED } }
        }
      }
    });

    const types = [...new Set(s.typeMatrix.map(r => r.issueType))];
    const cats = [...new Set(s.typeMatrix.map(r => r.category))];
    makeChart('ch-matrix', {
      type: 'bar',
      data: {
        labels: types,
        datasets: cats.map(cat => ({
          label: cat,
          data: types.map(t =>
            (s.typeMatrix.find(r => r.issueType === t && r.category === cat) || {}).count || 0),
          backgroundColor: CATEGORY_COLORS[cat] || '#c3c2b7',
          borderColor: SURFACE,
          borderWidth: 2,
          borderRadius: 4,
          maxBarThickness: 40
        }))
      },
      options: {
        maintainAspectRatio: false,
        plugins: { legend: { position: 'bottom', labels: { color: '#52514e', boxWidth: 12 } } },
        scales: {
          x: { stacked: true, grid: { display: false }, ticks: { color: INK_MUTED } },
          y: { stacked: true, grid: { color: GRID }, border: { display: false }, ticks: { color: INK_MUTED, precision: 0 } }
        }
      }
    });
  }

  return {
    render: load,
    setMetric(m) {
      topMetric = m;
      App.refresh();
    }
  };
})();
