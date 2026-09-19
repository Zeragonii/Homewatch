let state={children:[],pending:[],devices:[]};
let currentView='dashboard';
let refreshTimer=null;
const loginPanel=document.getElementById('login');
const appPanel=document.getElementById('app');
const usernameInput=document.getElementById('username');
const passwordInput=document.getElementById('password');
const loginErrorEl=document.getElementById('loginError');
const childrenEl=document.getElementById('children');
const pendingEl=document.getElementById('pending');
const devicesEl=document.getElementById('devices');
const childNameInput=document.getElementById('childName');
const statTotal=document.getElementById('statTotal');
const statOnline=document.getElementById('statOnline');
const statOffline=document.getElementById('statOffline');
const statPending=document.getElementById('statPending');
const pendingBadge=document.getElementById('pendingBadge');
const pendingCount=document.getElementById('pendingCount');
const lastRefresh=document.getElementById('lastRefresh');

async function api(url, options={}){const r=await fetch(url,{headers:{'Content-Type':'application/json',...(options.headers||{})},...options});if(r.status===401)throw new Error('AUTH');if(!r.ok){let m='Request failed';try{m=(await r.json()).detail||m}catch{}throw new Error(m)}const ct=r.headers.get('content-type')||'';return ct.includes('json')?r.json():r}
async function login(){try{await api('/api/login',{method:'POST',body:JSON.stringify({username:usernameInput.value,password:passwordInput.value})});await showApp();}catch(e){loginErrorEl.textContent=e.message==='AUTH'?'Invalid credentials':e.message}}
async function logout(){await fetch('/api/logout',{method:'POST'});location.reload()}
async function showApp(){loginPanel.classList.add('hidden');appPanel.classList.remove('hidden');await refresh();if(!refreshTimer)refreshTimer=setInterval(refresh,5000)}

function showView(view){currentView=view;document.getElementById('view-dashboard').classList.toggle('hidden',view!=='dashboard');document.getElementById('view-onboarding').classList.toggle('hidden',view!=='onboarding');document.getElementById('nav-dashboard').classList.toggle('active',view==='dashboard');document.getElementById('nav-onboarding').classList.toggle('active',view==='onboarding')}

async function refresh(){try{const next=await api('/api/dashboard');state=next;renderSummary();renderChildren();renderPending();patchDevices();lastRefresh.textContent=`Updated ${new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'})}`;}catch(e){if(e.message==='AUTH'){appPanel.classList.add('hidden');loginPanel.classList.remove('hidden');if(refreshTimer){clearInterval(refreshTimer);refreshTimer=null}}}}

function renderSummary(){const total=state.devices.length,online=state.devices.filter(d=>d.online).length,pending=state.pending.length;statTotal.textContent=total;statOnline.textContent=online;statOffline.textContent=total-online;statPending.textContent=pending;pendingCount.textContent=pending?`${pending} waiting`:'None waiting';pendingBadge.textContent=pending;pendingBadge.classList.toggle('hidden',pending===0)}
function renderChildren(){
  const wanted=new Set(state.children.map(c=>String(c.id)));
  childrenEl.querySelectorAll('[data-child-id]').forEach(el=>{if(!wanted.has(el.dataset.childId))el.remove()});
  for(const c of state.children){
    let chip=childrenEl.querySelector(`[data-child-id="${cssEscape(c.id)}"]`);
    if(!chip){chip=document.createElement('span');chip.className='chip';chip.dataset.childId=c.id;childrenEl.appendChild(chip)}
    chip.textContent=c.name;
  }
  let empty=childrenEl.querySelector('[data-empty-children]');
  if(!state.children.length){if(!empty){empty=document.createElement('span');empty.className='muted';empty.dataset.emptyChildren='1';empty.textContent='No child profiles yet.';childrenEl.appendChild(empty)}}
  else empty?.remove();
}
function renderPending(){
  const wanted=new Set(state.pending.map(p=>String(p.installation_id)));
  pendingEl.querySelectorAll('[data-installation-id]').forEach(el=>{if(!wanted.has(el.dataset.installationId))el.remove()});
  for(const p of state.pending){
    let row=pendingEl.querySelector(`[data-installation-id="${cssEscape(p.installation_id)}"]`);
    if(!row){
      row=document.createElement('div');row.className='pending';row.dataset.installationId=p.installation_id;
      row.innerHTML=`<div class="row"><div><b class="pending-hostname"></b><br><span class="muted pending-meta"></span></div><div class="row"><select class="pending-child"></select><input class="pending-name" placeholder="Device name"><button class="pending-enrol">Enrol</button></div></div>`;
      row.querySelector('.pending-name').value=p.hostname;
      row.querySelector('.pending-enrol').addEventListener('click',()=>approve(p.installation_id));
      pendingEl.appendChild(row);
    }
    row.querySelector('.pending-hostname').textContent=p.hostname;
    row.querySelector('.pending-meta').textContent=`${p.os_version} · Agent ${p.agent_version}`;
    const select=row.querySelector('.pending-child');
    const selected=select.value;
    const desiredIds=state.children.map(c=>String(c.id));
    const existingIds=[...select.options].map(o=>o.value);
    if(JSON.stringify(existingIds)!==JSON.stringify(desiredIds)){
      select.replaceChildren(...state.children.map(c=>{const o=document.createElement('option');o.value=c.id;o.textContent=c.name;return o}));
      if(desiredIds.includes(selected))select.value=selected;
    }
  }
  let empty=pendingEl.querySelector('[data-empty-pending]');
  if(!state.pending.length){if(!empty){empty=document.createElement('p');empty.className='muted';empty.dataset.emptyPending='1';empty.textContent='No devices waiting for approval.';pendingEl.appendChild(empty)}}
  else empty?.remove();
}

function patchDevices(){const wanted=new Set(state.devices.map(d=>d.id));devicesEl.querySelectorAll('[data-device-id]').forEach(el=>{if(!wanted.has(el.dataset.deviceId))el.remove()});for(const d of state.devices){let card=devicesEl.querySelector(`[data-device-id="${cssEscape(d.id)}"]`);if(!card){card=document.createElement('article');card.className='device';card.dataset.deviceId=d.id;card.innerHTML=deviceTemplate(d);devicesEl.appendChild(card)}updateDeviceCard(card,d)}if(!state.devices.length){if(!document.getElementById('noDevices'))devicesEl.insertAdjacentHTML('beforeend','<p id="noDevices" class="muted empty-state">No enrolled devices yet. Open Onboarding to add one.</p>')}else document.getElementById('noDevices')?.remove()}
function deviceTemplate(d){return `<div class="row"><div><h3 class="device-title"></h3><span class="device-status"></span></div><small class="muted device-agent"></small></div><p><b>Current:</b> <span class="device-current"></span></p><div class="activity"></div><div class="actions"><input class="message-input" placeholder="Message"><button onclick="sendMessage('${escAttr(d.id)}')">Send</button><button class="secondary" onclick="shot('${escAttr(d.id)}')">Screenshot</button></div><img class="screenshot hidden" alt="Latest screenshot">`}
function updateDeviceCard(card,d){card.querySelector('.device-title').textContent=`${d.child} — ${d.name}`;const status=card.querySelector('.device-status');status.className=`device-status ${d.online?'online':'offline'}`;status.textContent=`● ${d.online?'Online':'Offline'}`;card.querySelector('.device-agent').textContent=`Agent ${d.agent_version||'—'}`;card.querySelector('.device-current').textContent=d.current_app||'No foreground app reported';card.querySelector('.activity').innerHTML=d.activity.map(a=>`<div><span>${esc(a.process)}</span><b>${fmt(a.seconds)}</b></div>`).join('')||'<span class="muted">No activity today.</span>'}

function fmt(sec){sec=Number(sec);if(sec<60)return sec+'s';const h=Math.floor(sec/3600),m=Math.floor((sec%3600)/60);return h?`${h}h ${m}m`:`${m}m`}
async function addChild(){if(!childNameInput.value.trim())return;await api('/api/children',{method:'POST',body:JSON.stringify({name:childNameInput.value.trim()})});childNameInput.value='';await refresh()}
async function approve(id){const row=pendingEl.querySelector(`[data-installation-id="${cssEscape(id)}"]`);const child=Number(row?.querySelector('.pending-child')?.value),name=row?.querySelector('.pending-name')?.value.trim();if(!row||!child||!name)return;await api('/api/pending/'+id+'/approve',{method:'POST',body:JSON.stringify({child_id:child,device_name:name})});await refresh()}
async function sendMessage(id){const card=devicesEl.querySelector(`[data-device-id="${cssEscape(id)}"]`),el=card?.querySelector('.message-input');if(!el?.value.trim())return;await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'message',payload:el.value.trim()})});el.value=''}
async function shot(id){const card=devicesEl.querySelector(`[data-device-id="${cssEscape(id)}"]`);await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'screenshot',payload:''})});setTimeout(()=>{const img=card?.querySelector('.screenshot');if(!img)return;img.src=`/api/devices/${id}/screenshot?t=${Date.now()}`;img.onload=()=>img.classList.remove('hidden')},2500)}
function esc(s){return String(s??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function escAttr(s){return esc(s).replace(/`/g,'&#96;')}
function cssEscape(s){return window.CSS?.escape?CSS.escape(String(s)):String(s).replace(/[^a-zA-Z0-9_-]/g,'\\$&')}

(async()=>{try{await refresh();loginPanel.classList.add('hidden');appPanel.classList.remove('hidden');showView(currentView);if(!refreshTimer)refreshTimer=setInterval(refresh,5000)}catch{}})();
