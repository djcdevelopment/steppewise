const $ = id => document.getElementById(id);
let selected = null, busy = false;
const names = {'service-intake':'Synthetic service change intake','local-ai-work':'Synthetic local AI work'};
const escape = value => String(value ?? '').replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
async function api(path, options={}) {
  const response = await fetch(path, {...options, headers:{'Content-Type':'application/json',...options.headers}});
  const text = await response.text();
  let data; try { data = JSON.parse(text); } catch { data = {}; }
  if (!response.ok) throw new Error(data.error || `Request returned HTTP ${response.status}.`);
  return data;
}
function error(message) { $('error').textContent = message; $('error').hidden = !message; }
function controls(value) { busy = value; for (const id of ['start','accept','reject','refresh']) $(id).disabled = value; }
function actorLabel(event) {
  if(event.action !== 'human.review' || !['DecisionRequested','Accepted','Rejected'].includes(event.status)) return '';
  return `<small class="actor">Reviewer: ${escape(event.actor || 'identity not recorded')} · ${escape(event.actorSource || 'legacy record')}</small>`;
}
function render() {
  const persona = $('persona').value;
  $('viewTitle').textContent = {operator:'Operator view',business:'Business owner view',engineer:'Engineer view'}[persona];
  $('empty').hidden = !!selected; $('detail').hidden = !selected;
  if (!selected) return;
  const run = selected;
  const awaiting = run.status === 'AwaitingReview';
  const next = awaiting ? 'Inspect both artifacts, then accept or reject the repaired result.' : ['Accepted','Rejected'].includes(run.status) ? 'Decision recorded. Start a new run to repeat the workflow.' : run.status === 'Failed' ? 'Inspect the recorded failure before attempting another run.' : 'Execution is in progress; no human acceptance has been recorded.';
  $('summary').innerHTML = `<strong>${escape(run.status.replace(/([a-z])([A-Z])/g,'$1 $2'))}</strong><p>${escape(names[run.workflow] || run.workflow)} · ${escape(run.evidenceMode)} evidence</p><p>${escape(next)}</p>`;
  // Domain progression is synchronous. Framework stream arrival order is diagnostics only.
  const events = run.events.filter(e => e.action !== 'maf' && (persona !== 'business' || e.status !== 'ActionStarted'));
  $('flow').innerHTML = events.map(e => `<div class="step"><span class="number">${escape(e.sequence)}</span><div><b>${escape(e.action)}</b> <span aria-hidden="true">→</span> ${escape(e.boundary)}<small>${escape(e.detail)}</small>${actorLabel(e)}${persona === 'engineer' ? `<span class="status">${escape(e.status)}</span>` : ''}</div><time>${escape(new Date(e.timestamp).toLocaleTimeString())}</time></div>`).join('') || '<p>Execution evidence has not arrived.</p>';
  $('artifacts').innerHTML = '<h3>Preserved results</h3><div class="artifacts">'+run.artifacts.map((a,i) => `<article class="artifact ${a.passed?'':'failed'}"><p class="eyebrow">${i===0?'ORIGINAL CANDIDATE':`CANDIDATE ${i+1}`}</p><h3>${escape(a.id)}</h3><span class="result">${a.passed?'CHECKS PASSED':'CHECK FAILED'}</span><p>${escape(a.detail)}</p><pre>${escape(pretty(a.json))}</pre>${persona==='engineer'?`<small>SHA-256 ${escape(a.hash)}</small>`:''}</article>`).join('')+'</div>';
  $('review').hidden = !awaiting;
  $('diagnostics').textContent = JSON.stringify({id:run.id,domainEvents:run.events.filter(e=>e.action!=='maf'),frameworkArrivalEvents:run.events.filter(e=>e.action==='maf'),frameworkOrdering:'arrival-order-not-causal',artifacts:run.artifacts},null,2);
}
function pretty(value) { try { return JSON.stringify(JSON.parse(value),null,2); } catch { return value; } }
async function refresh() {
  const runs = await api('/api/runs');
  $('runs').replaceChildren();
  for(const run of [...runs].sort((a,b)=>new Date(b.events?.[0]?.timestamp||0)-new Date(a.events?.[0]?.timestamp||0))) {
    const button = document.createElement('button'); button.className='run'+(run.id===selected?.id?' active':'');
    const title = document.createElement('span'); title.textContent=names[run.workflow]||run.workflow;
    const sub=document.createElement('small'); sub.textContent=run.status+' · '+run.id.slice(0,8);
    button.append(title,sub); button.onclick=()=>guard(async()=>{selected=await api('/api/runs/'+encodeURIComponent(run.id));render();await refresh();});
    $('runs').append(button);
  }
  if(!runs.length) $('runs').textContent='No runs yet.';
}
async function guard(action) {
  if(busy) return; controls(true); error('');
  try { await action(); } catch(e) { error(e.message); $('progress').textContent='Command failed. Inspect the message; success has not been claimed.'; }
  finally { controls(false); }
}
$('start').onclick=()=>guard(async()=>{
  $('progress').textContent='Submitting start request… waiting for execution evidence.';
  selected=await api('/api/runs/'+$('workflow').value,{method:'POST'});
  $('progress').textContent=selected.status==='AwaitingReview'?'Execution evidenced. Checks complete; human review is pending.':`Execution returned ${selected.status}.`;
  render();await refresh();
});
async function review(accept) {
  if(!selected) return;
  const candidate=selected.artifacts.at(-1);
  if(!candidate?.passed) throw new Error('No verified candidate is available for review.');
  $('progress').textContent='Submitting your decision… waiting for the recorded receipt.';
  selected=await api('/api/runs/'+encodeURIComponent(selected.id)+'/review',{method:'POST',body:JSON.stringify({accept,expectedHash:candidate.hash,reviewer:$('reviewer').value.trim() || null})});
  $('progress').textContent=`Human decision recorded: ${selected.status}.`;
  render();await refresh();
}
$('accept').onclick=()=>guard(()=>review(true));$('reject').onclick=()=>guard(()=>review(false));
$('refresh').onclick=()=>guard(async()=>{if(selected)selected=await api('/api/runs/'+encodeURIComponent(selected.id));render();await refresh();});
$('persona').onchange=render;
guard(async()=>{const info=await api('/api/info');$('environment').textContent=info.environment+' · loopback only';await refresh();});
