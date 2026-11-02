'use strict';
document.querySelectorAll('button').forEach(b=>b.disabled=false);
const articles=[...document.querySelectorAll('article')], nav=document.querySelector('nav ol');
let step=1, reach=1, decision=null, all=false;
const titles=articles.map(a=>a.querySelector('h3').textContent);
const buttons=titles.map((title,i)=>{const li=document.createElement('li'), b=document.createElement('button');b.className='step';b.textContent=(i===5?'○ ':'□ ')+(i+1)+'. '+title;b.onclick=()=>{all=false;go(i+1)};li.append(b);nav.append(li);return b});
const bs=[...document.querySelectorAll('button')].filter(b=>!b.classList.contains('step'));
const toggle=bs[0], accept=bs[1], reject=bs[2], stale=bs[3], prev=bs[4], next=bs[5];
const msg=document.querySelector('[role=status]'), receipt=document.querySelector('aside ol'), outcome=document.querySelector('#outcome');
const reset=document.createElement('button');reset.textContent='Restart simulation';reset.onclick=()=>{step=reach=1;decision=null;all=false;go(1)};document.querySelector('aside').append(reset);
const events=['Run admitted · service-intake · CR-1042 · snapshot 1','candidate-1 produced · example hash a41c…09e2','candidate-1 checked · Fail · rollbackPlan missing','candidate-2 produced · example hash 7be3…5d10 · candidate-1 retained','candidate-2 checked · Pass · awaiting review · snapshot 2'];
function render(){
 articles.forEach((a,i)=>a.hidden=!all&&i!==step-1);
 buttons.forEach((b,i)=>{if(!all&&i===step-1)b.setAttribute('aria-current','step');else b.removeAttribute('aria-current')});
 toggle.textContent=all?'Show one step at a time':'Show all steps on one page';toggle.setAttribute('aria-pressed',String(all));
 prev.disabled=step===1;next.disabled=step===7;
 const status=decision||(reach>=5?'Awaiting review':'Running');
 outcome.textContent=status;
 msg.textContent=decision?'Recorded: '+decision+' for candidate-2, example hash 7be3…5d10. Restart to explore another outcome.':'No decision yet. The run is awaiting review.';
 accept.disabled=reject.disabled=stale.disabled=!!decision;
 const rows=events.slice(0,reach);
 if(decision)rows.push('Decision recorded: '+decision+' · candidate-2 · example hash 7be3…5d10','Run finished: '+decision+' · snapshot 3');
 receipt.replaceChildren(...rows.map(t=>{const li=document.createElement('li');li.textContent=t;return li}));
 document.querySelector('[aria-live]').textContent='Step '+step+' of 7. Run status: '+status;
}
function go(n){step=Math.max(1,Math.min(7,n));reach=Math.max(reach,Math.min(step,5));render();const h=articles[step-1].querySelector('h3');h.tabIndex=-1;h.focus()}
toggle.onclick=()=>{all=!all;if(all)reach=5;render()};
prev.onclick=()=>go(step-1);next.onclick=()=>go(step+1);
function decide(value){if(decision)return;reach=5;decision=value;render()}
accept.onclick=()=>decide('Accepted');reject.onclick=()=>decide('Rejected');
stale.onclick=()=>{if(!decision)msg.textContent='Simulated HTTP 409: outdated fingerprint refused. No decision recorded; awaiting review.'};
render();
