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
const screenTimeChildren=document.getElementById('screenTimeChildren');
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

function showView(view){currentView=view;['dashboard','screentime','applications','onboarding'].forEach(v=>{document.getElementById(`view-${v}`).classList.toggle('hidden',v!==view);document.getElementById(`nav-${v}`).classList.toggle('active',v===view)});if(view==='screentime')loadScreenTime();if(view==='applications')loadApplications()}

async function refresh(){try{const next=await api('/api/dashboard');state=next;renderSummary();renderChildren();renderPending();patchDevices();lastRefresh.textContent=`Updated ${new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'})}`;if(currentView==='screentime')loadScreenTime();if(currentView==='applications')loadApplications();}catch(e){if(e.message==='AUTH'){appPanel.classList.add('hidden');loginPanel.classList.remove('hidden');if(refreshTimer){clearInterval(refreshTimer);refreshTimer=null}}}}

function renderSummary(){const appBadge=document.getElementById('appRequestBadge');if(appBadge){const n=state.pending_application_requests||0;appBadge.textContent=n;appBadge.classList.toggle('hidden',!n)}const total=state.devices.length,online=state.devices.filter(d=>d.online).length,pending=state.pending.length;statTotal.textContent=total;statOnline.textContent=online;statOffline.textContent=total-online;statPending.textContent=pending;pendingCount.textContent=pending?`${pending} waiting`:'None waiting';pendingBadge.textContent=pending;pendingBadge.classList.toggle('hidden',pending===0)}
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
function deviceTemplate(d){return `<div class="row"><div><h3 class="device-title"></h3><span class="device-status"></span></div><small class="muted device-agent"></small></div><div class="device-assignment"><label><b>Child:</b> <select class="device-child-select"></select></label><button class="secondary device-child-save" onclick="saveDeviceChild('${escAttr(d.id)}',this)">Save</button></div><p><b>User:</b> <span class="device-user"></span></p><p><b>Current:</b> <span class="device-current"></span></p><p class="muted updater-line"><b>Updater:</b> <span class="device-updater-status"></span></p><div class="activity"></div><div class="message-composer"><div class="message-composer-head"><h4>Message</h4><select class="message-type" aria-label="Message type"><option value="notify">Notify</option><option value="question">Question</option><option value="alert">Alert</option></select></div><div class="message-row"><input class="message-input" placeholder="Type a message…"><button onclick="sendMessage('${escAttr(d.id)}',this)">Send</button></div><small class="muted message-hint">Notify is dismissible. Question requires a reply. Alert requires acknowledgement.</small></div><div class="actions"><button class="secondary" onclick="shot('${escAttr(d.id)}')">Screenshot</button><button class="secondary update-btn" onclick="checkUpdate('${escAttr(d.id)}',this)">Update now</button></div><div class="device-controls"><h4>Device controls</h4><div class="control-actions"><button class="secondary" onclick="deviceCommand('${escAttr(d.id)}','lock')">Lock</button><button class="secondary" onclick="deviceCommand('${escAttr(d.id)}','logoff')">Log off</button><button class="danger" onclick="deviceCommand('${escAttr(d.id)}','restart')">Restart</button><button class="danger" onclick="deviceCommand('${escAttr(d.id)}','shutdown')">Shutdown</button></div><div class="close-app-row"><input class="close-app-input" placeholder="Process name, e.g. wow.exe"><button class="secondary" onclick="closeApp('${escAttr(d.id)}')">Close app</button></div></div><details class="history"><summary>Command & message history</summary><div class="command-history"></div></details><img class="screenshot hidden" alt="Latest screenshot">`}
function updateDeviceCard(card,d){card.querySelector('.device-title').textContent=`${d.child} — ${d.name}`;const status=card.querySelector('.device-status');status.className=`device-status ${d.online?'online':'offline'}`;status.textContent=`● ${d.online?'Online':'Offline'}`;card.querySelector('.device-agent').textContent=`Agent ${d.agent_version||'—'}`;card.querySelector('.device-user').textContent=d.logged_in_user||'Not reported';card.querySelector('.device-current').textContent=d.current_app||'No foreground app reported';card.querySelector('.device-updater-status').textContent=d.update_status||'Not reported by this agent version';card.querySelector('.activity').innerHTML=d.activity.map(a=>`<div><span>${esc(a.process)}</span><b>${fmt(a.seconds)}</b></div>`).join('')||'<span class="muted">No activity today.</span>';const select=card.querySelector('.device-child-select');if(select){const selected=select.value;const ids=state.children.map(c=>String(c.id));const existing=[...select.options].map(o=>o.value);if(JSON.stringify(existing)!==JSON.stringify(ids)){select.replaceChildren(...state.children.map(c=>{const o=document.createElement('option');o.value=c.id;o.textContent=c.name;return o}));}if(document.activeElement!==select||!selected)select.value=String(d.child_id);else if(ids.includes(selected))select.value=selected;}const close=card.querySelector('.close-app-input');if(close&&!close.value&&d.current_app)close.placeholder=`Process name (${d.current_app})`;renderCommandHistory(card,d.commands||[])}

function fmt(sec){sec=Number(sec);if(sec<60)return sec+'s';const h=Math.floor(sec/3600),m=Math.floor((sec%3600)/60);return h?`${h}h ${m}m`:`${m}m`}
async function addChild(){if(!childNameInput.value.trim())return;await api('/api/children',{method:'POST',body:JSON.stringify({name:childNameInput.value.trim()})});childNameInput.value='';await refresh()}
async function approve(id){const row=pendingEl.querySelector(`[data-installation-id="${cssEscape(id)}"]`);const child=Number(row?.querySelector('.pending-child')?.value),name=row?.querySelector('.pending-name')?.value.trim();if(!row||!child||!name)return;await api('/api/pending/'+id+'/approve',{method:'POST',body:JSON.stringify({child_id:child,device_name:name})});await refresh()}

async function saveDeviceChild(id,button){
  const card=devicesEl.querySelector(`[data-device-id="${cssEscape(id)}"]`);
  const select=card?.querySelector('.device-child-select');
  const childId=Number(select?.value);
  if(!childId)return;
  const current=state.devices.find(d=>d.id===id);
  if(current&&Number(current.child_id)===childId)return;
  const child=state.children.find(c=>Number(c.id)===childId);
  if(!confirm(`Move ${current?.name||'this device'} to ${child?.name||'the selected child'}?`))return;
  const original=button?.textContent||'Save';
  if(button){button.disabled=true;button.textContent='Saving…'}
  try{
    await api(`/api/devices/${id}/child`,{method:'PUT',body:JSON.stringify({child_id:childId})});
    await refresh();
    if(currentView==='screentime')await loadScreenTime();
    if(button)button.textContent='Saved';
    setTimeout(()=>{if(button){button.disabled=false;button.textContent=original}},1200);
  }catch(e){
    if(button){button.disabled=false;button.textContent=original}
    alert(`Could not reassign device: ${e.message}`);
  }
}

async function sendMessage(id,button){
  const card=devicesEl.querySelector(`[data-device-id="${cssEscape(id)}"]`),input=card?.querySelector('.message-input'),typeEl=card?.querySelector('.message-type');
  const text=input?.value.trim(),type=typeEl?.value||'notify';if(!text)return;
  const original=button?.textContent||'Send';if(button){button.disabled=true;button.textContent='Sending…'}
  try{
    await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'message',payload:JSON.stringify({type,text})})});
    input.value='';if(button)button.textContent='Sent';setTimeout(()=>{if(button){button.disabled=false;button.textContent=original}},1000);await refresh();
  }catch(e){if(button){button.disabled=false;button.textContent=original}alert(`Could not send message: ${e.message}`)}
}

async function deviceCommand(id,kind){
  const labels={lock:'Lock this PC?',logoff:'Log off the current Windows user?',restart:'Restart this PC in 10 seconds?',shutdown:'Shut down this PC in 10 seconds?'};
  if(!confirm(labels[kind]||`Send ${kind}?`))return;
  try{await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind,payload:''})});await refresh()}catch(e){alert(`Could not queue command: ${e.message}`)}
}
async function closeApp(id){
  const card=devicesEl.querySelector(`[data-device-id="${cssEscape(id)}"]`),input=card?.querySelector('.close-app-input');
  const d=state.devices.find(x=>x.id===id);
  const target=(input?.value.trim()||d?.current_app||'').trim();
  if(!target)return alert('Enter a process name or wait for the current app to be reported.');
  if(!confirm(`Close ${target} on this PC?`))return;
  try{await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'close_app',payload:target})});if(input)input.value='';await refresh()}catch(e){alert(`Could not queue close-app command: ${e.message}`)}
}
function commandLabel(kind){return({message:'Message',screenshot:'Screenshot',check_update:'Update check',lock:'Lock',logoff:'Log off',restart:'Restart',shutdown:'Shutdown',close_app:'Close app'})[kind]||kind}
function parseMessagePayload(payload){try{const p=JSON.parse(payload);if(p&&['notify','question','alert'].includes(p.type)&&typeof p.text==='string')return p}catch{}return{type:'notify',text:String(payload||'')}}
function messageTypeLabel(type){return({notify:'Notify',question:'Question',alert:'Alert'})[type]||'Notify'}
function renderCommandHistory(card,commands){
  const el=card.querySelector('.command-history');if(!el)return;
  el.innerHTML=commands.length?commands.map(c=>{
    if(c.kind==='message'){
      const m=parseMessagePayload(c.payload);return `<div class="command-row message-history-row message-${escAttr(m.type)}"><div><div><span class="message-type-badge">${esc(messageTypeLabel(m.type))}</span></div><div class="message-history-text">${esc(m.text)}</div></div><div><span class="command-status status-${escAttr(c.status)}">${esc(c.status)}</span>${c.result?`<span class="command-result message-response">${esc(c.result)}</span>`:''}</div></div>`;
    }
    return `<div class="command-row"><div><b>${esc(commandLabel(c.kind))}</b>${c.kind==='close_app'&&c.payload?` <span class="muted">${esc(c.payload)}</span>`:''}</div><div><span class="command-status status-${escAttr(c.status)}">${esc(c.status)}</span>${c.result?`<span class="muted command-result">${esc(c.result)}</span>`:''}</div></div>`;
  }).join(''):'<span class="muted">No commands yet.</span>';
}

async function checkUpdate(id,button){
  const original=button?.textContent||'Update now';
  if(button){button.disabled=true;button.textContent='Queued…'}
  try{
    await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'check_update',payload:''})});
    if(button)button.textContent='Check queued';
    setTimeout(()=>{if(button){button.disabled=false;button.textContent=original}},2500);
  }catch(e){
    if(button){button.disabled=false;button.textContent=original}
    alert(`Could not queue update check: ${e.message}`);
  }
}
async function checkAllUpdates(){
  const button=document.getElementById('checkAllUpdates');
  const devices=[...state.devices];
  if(!devices.length)return;
  const original=button.textContent;button.disabled=true;button.textContent='Queuing…';
  const results=await Promise.allSettled(devices.map(d=>api(`/api/devices/${d.id}/commands`,{method:'POST',body:JSON.stringify({kind:'check_update',payload:''})})));
  const ok=results.filter(r=>r.status==='fulfilled').length;
  button.textContent=`Queued ${ok}/${devices.length}`;
  setTimeout(()=>{button.disabled=false;button.textContent=original},3000);
}
async function shot(id){const card=devicesEl.querySelector(`[data-device-id="${cssEscape(id)}"]`);await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'screenshot',payload:''})});setTimeout(()=>{const img=card?.querySelector('.screenshot');if(!img)return;img.src=`/api/devices/${id}/screenshot?t=${Date.now()}`;img.onload=()=>img.classList.remove('hidden')},2500)}
function esc(s){return String(s??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function escAttr(s){return esc(s).replace(/`/g,'&#96;')}
function cssEscape(s){return window.CSS?.escape?CSS.escape(String(s)):String(s).replace(/[^a-zA-Z0-9_-]/g,'\\$&')}


async function loadScreenTime(){
  if(!screenTimeChildren)return;
  try{
    const data=await api('/api/screentime');
    document.getElementById('screenTimeZone').textContent=`Schedule timezone: ${data.timezone}`;
    patchScreenTime(data.children||[]);
  }catch(e){screenTimeChildren.innerHTML=`<div class="card error">${esc(e.message)}</div>`}
}
function patchScreenTime(children){
  const keep=new Set(children.map(c=>String(c.id)));
  [...screenTimeChildren.querySelectorAll('[data-st-child]')].forEach(el=>{if(!keep.has(el.dataset.stChild))el.remove()});
  for(const c of children){
    let card=screenTimeChildren.querySelector(`[data-st-child="${cssEscape(c.id)}"]`);
    if(!card){card=document.createElement('section');card.className='card screen-time-card';card.dataset.stChild=c.id;screenTimeChildren.appendChild(card);renderScreenTimeCard(card,c,true)}
    else renderScreenTimeCard(card,c,false);
  }
  if(!children.length)screenTimeChildren.innerHTML='<div class="card muted">Create a child profile first.</div>'
}
function renderScreenTimeCard(card,c,initial){
  const p=c.policy,t=c.today;
  if(initial){
    card.innerHTML=`<div class="section-heading"><div><h3>${esc(c.name)}</h3><div class="muted st-summary"></div></div><label class="toggle"><input class="st-enabled" type="checkbox"> Enforce limits</label></div>
    <div class="policy-grid">
      <div><h4>Weekdays</h4><label>Allowed from <input class="st-wd-start" type="time"></label><label>Until <input class="st-wd-end" type="time"></label><label>Daily allowance <input class="st-wd-min" type="number" min="0" max="1440"> min</label></div>
      <div><h4>Weekends</h4><label>Allowed from <input class="st-we-start" type="time"></label><label>Until <input class="st-we-end" type="time"></label><label>Daily allowance <input class="st-we-min" type="number" min="0" max="1440"> min</label></div>
      <div><h4>Warnings</h4><label>Warn with <input class="st-warning" type="number" min="0" max="120"> min left</label><label>Grace after limit <input class="st-grace" type="number" min="0" max="120"> min</label></div>
    </div>
    <div class="row"><button onclick="savePolicy(${c.id},this)">Save policy</button><div class="extension-actions"><span class="muted">Add today:</span><button class="secondary" onclick="extendTime(${c.id},15)">+15m</button><button class="secondary" onclick="extendTime(${c.id},30)">+30m</button><button class="secondary" onclick="extendTime(${c.id},60)">+60m</button></div></div>
    <div class="app-limits"><h4>Application limits</h4><div class="row app-limit-add"><input class="al-process" placeholder="e.g. wow.exe"><input class="al-wd" type="number" min="0" placeholder="Weekday min"><input class="al-we" type="number" min="0" placeholder="Weekend min"><button onclick="addAppLimit(${c.id},this)">Add / update</button></div><div class="app-limit-list"></div></div>`;
  }
  const active=document.activeElement;
  const set=(sel,val)=>{const el=card.querySelector(sel);if(el&&el!==active)el.value=val};
  const chk=card.querySelector('.st-enabled');if(chk!==active)chk.checked=p.enabled;
  set('.st-wd-start',p.weekday_start);set('.st-wd-end',p.weekday_end);set('.st-we-start',p.weekend_start);set('.st-we-end',p.weekend_end);set('.st-wd-min',p.weekday_minutes);set('.st-we-min',p.weekend_minutes);set('.st-warning',p.warning_minutes);set('.st-grace',p.grace_minutes);
  const used=Math.round(t.used_seconds/60);card.querySelector('.st-summary').textContent=`Today: ${used} min used · ${t.extension_minutes} min extra · ${t.status}${t.reason?` · ${t.reason}`:''}`;
  card.querySelector('.app-limit-list').innerHTML=(c.app_limits||[]).length?(c.app_limits||[]).map(x=>`<div class="app-limit-row"><span><strong>${esc(x.process_name)}</strong> <span class="muted">Weekday ${x.weekday_minutes||'∞'}m · Weekend ${x.weekend_minutes||'∞'}m</span></span><button class="secondary" onclick="deleteAppLimit(${x.id})">Remove</button></div>`).join(''):'<span class="muted">No application-specific limits.</span>';
}
async function savePolicy(id,button){
  const card=button.closest('[data-st-child]');
  const val=s=>card.querySelector(s).value;
  const body={enabled:card.querySelector('.st-enabled').checked,weekday_start:val('.st-wd-start'),weekday_end:val('.st-wd-end'),weekend_start:val('.st-we-start'),weekend_end:val('.st-we-end'),weekday_minutes:+val('.st-wd-min')||0,weekend_minutes:+val('.st-we-min')||0,warning_minutes:+val('.st-warning')||0,grace_minutes:+val('.st-grace')||0};
  button.disabled=true;try{await api(`/api/children/${id}/policy`,{method:'PUT',body:JSON.stringify(body)});button.textContent='Saved';setTimeout(()=>button.textContent='Save policy',1500);await loadScreenTime()}catch(e){alert(e.message)}finally{button.disabled=false}
}
async function extendTime(id,minutes){await api(`/api/children/${id}/extension`,{method:'POST',body:JSON.stringify({minutes})});await loadScreenTime()}
async function addAppLimit(id,button){const card=button.closest('[data-st-child]');const process=card.querySelector('.al-process').value.trim();if(!process)return;await api(`/api/children/${id}/app-limits`,{method:'POST',body:JSON.stringify({process_name:process,weekday_minutes:+card.querySelector('.al-wd').value||0,weekend_minutes:+card.querySelector('.al-we').value||0,enabled:true})});card.querySelector('.al-process').value='';card.querySelector('.al-wd').value='';card.querySelector('.al-we').value='';await loadScreenTime()}
async function deleteAppLimit(id){await api(`/api/app-limits/${id}`,{method:'DELETE'});await loadScreenTime()}


// Application approvals are not Windows elevation grants.
let lastApplicationSnapshot='';
async function loadApplications(force=false){
  if(!document.getElementById('appRequests'))return;
  try{
    const data=await api('/api/applications');
    const pending=data.requests.filter(r=>r.status==='pending');
    document.getElementById('appPendingCount').textContent=pending.length;
    const badge=document.getElementById('appRequestBadge');badge.textContent=pending.length;badge.classList.toggle('hidden',!pending.length);
    lastApplicationsData=data;
    const snapshot=JSON.stringify(data);
    if(!force && snapshot===lastApplicationSnapshot)return;
    // Keep any scope dropdown currently being edited when the 5-second heartbeat fires.
    if(!force && document.activeElement?.closest?.('#appRequests'))return;
    lastApplicationSnapshot=snapshot;
    const requests=document.getElementById('appRequests');
    requests.innerHTML=pending.length?`<div class="app-wrap"><table class="app-table"><thead><tr><th>Application</th><th>Requested by</th><th>Signature</th><th>Decision</th></tr></thead><tbody>${pending.map(r=>`<tr>
      <td><b>${esc(r.display_name)}</b><button class="subtle-action" onclick="openAppEditor('${r.sha256}','${r.id}')">Edit tile</button><div class="meta">${esc(r.file_name)} · ${esc(r.file_path)}</div><details><summary class="muted small">Fingerprint & signer details</summary><div class="fingerprint">SHA-256: ${esc(r.sha256)}</div><div class="meta">Certificate: ${esc(r.signer_thumbprint||'None')}<br>Subject: ${esc(r.signer_subject||'—')}<br>Issuer: ${esc(r.signer_issuer||'—')}</div></details></td>
      <td>${esc(r.child_name)}<div class="meta">${esc(r.device_name)}</div>${r.reason?`<div class="meta">“${esc(r.reason)}”</div>`:''}</td>
      <td><span class="signature sig-${escAttr(r.signature_status)}">${esc(r.signature_status)}</span><div class="meta">${esc(r.publisher||'Unknown publisher')}</div></td>
      <td><div class="row-actions"><select id="app-scope-${r.id}" aria-label="Approval scope"><option value="device">This device</option><option value="child">Child's devices</option>${r.signature_status==='valid'&&r.signer_thumbprint?'<option value="global">Entire family</option>':''}</select><button class="accept" onclick="decideApplication(${r.id},'approved')">Approve</button><button class="warn" onclick="decideApplication(${r.id},'denied')">Deny</button></div></td>
    </tr>`).join('')}</tbody></table></div>`:'<div class="app-empty">All caught up — no pending application requests.</div>';
    const approvals=document.getElementById('appApprovals');
    approvals.innerHTML=data.approvals.length?`<div class="app-wrap"><table class="app-table"><thead><tr><th>Application</th><th>Scope</th><th>Fingerprint</th><th></th></tr></thead><tbody>${data.approvals.map(a=>`<tr><td><b>${esc(a.display_name)}</b><button class="subtle-action" onclick="openAppEditor('${a.sha256}')">Edit tile</button><div class="meta">${esc(a.file_name)} · ${esc(a.publisher||'Unsigned / unknown')}</div></td><td>${esc(a.scope==='global'?'Entire family':a.scope==='child'?'Child devices':'One device')}</td><td><span class="fingerprint">${esc(a.sha256.substring(0,20))}…</span></td><td><button class="secondary" onclick="revokeApplication(${a.id})">Revoke</button></td></tr>`).join('')}</tbody></table></div>`:'<div class="app-empty">No active application approvals.</div>';
    document.getElementById('appHistory').innerHTML=`<div class="app-wrap"><table class="app-table"><tbody>${data.requests.filter(r=>r.status!=='pending').slice(0,30).map(r=>`<tr><td>${esc(r.file_name)}<div class="meta">${esc(r.child_name)} · ${esc(r.device_name)}</div></td><td>${esc(r.status)}</td><td class="muted">${esc(new Date(r.created_at).toLocaleString())}</td></tr>`).join('')}${data.launches.slice(0,15).map(x=>`<tr><td>Launch #${x.request_id}<div class="meta">${esc(x.detail)}</div></td><td>${esc(x.status)}</td><td class="muted">${esc(new Date(x.created_at).toLocaleString())}</td></tr>`).join('')}</tbody></table></div>`;
  }catch(e){document.getElementById('appRequests').textContent=`Unable to load applications: ${e.message}`}
}
async function decideApplication(id,decision){
  const scope=document.getElementById(`app-scope-${id}`)?.value||'device';
  if(decision==='denied'&&!confirm('Deny this request?'))return;
  if(decision==='approved'&&scope==='global'&&!confirm('Approve this exact signed binary for ALL family devices?'))return;
  try{await api(`/api/applications/requests/${id}/decision`,{method:'POST',body:JSON.stringify({decision,scope})});lastApplicationSnapshot='';await loadApplications(true)}catch(e){alert(e.message)}
}
async function revokeApplication(id){if(!confirm('Revoke this approval?'))return;try{await api(`/api/applications/approvals/${id}`,{method:'DELETE'});lastApplicationSnapshot='';await loadApplications(true)}catch(e){alert(e.message)}}

(async()=>{try{await refresh();loginPanel.classList.add('hidden');appPanel.classList.remove('hidden');showView(currentView);if(!refreshTimer)refreshTimer=setInterval(refresh,5000)}catch{}})();

// Centrally managed display names and SteamGridDB poster selection.
let lastApplicationsData={requests:[],approvals:[]};
let editingDigest='';
let artworkGameId=0;
function artPosterURL(a){return a?.artwork_url||''}
function closeAppEditor(){document.getElementById('appEditor').close()}
function editorMessage(s){document.getElementById('appEditorFeedback').textContent=s}
function openAppEditor(digest){
  const a=[...(lastApplicationsData.requests||[]),...(lastApplicationsData.approvals||[])].find(r=>r.sha256===digest);
  if(!a)return;
  editingDigest=digest;artworkGameId=0;
  document.getElementById('appFriendlyName').value=a.display_name||a.file_name;
  document.getElementById('appArtworkSearch').value=a.display_name||'';
  document.getElementById('appArtworkGames').replaceChildren();
  document.getElementById('appArtworkGrids').replaceChildren();
  const poster=document.getElementById('appEditorPoster');
  poster.src=a.artwork_url||'';poster.classList.toggle('hidden',!a.artwork_url);
  document.getElementById('appEditorPlaceholder').classList.toggle('hidden',!!a.artwork_url);
  editorMessage('Search SteamGridDB to choose a portrait poster, then save your name.');
  document.getElementById('appEditor').showModal();
}
async function saveAppFriendlyName(){
  if(!editingDigest)return;
  try{
    await api(`/api/applications/${editingDigest}/presentation`,{method:'PUT',body:JSON.stringify({display_name:document.getElementById('appFriendlyName').value})});
    editorMessage('Friendly name saved. Clients will see it on their next refresh.');lastApplicationSnapshot='';await loadApplications(true);
  }catch(e){editorMessage(e.message)}
}
async function searchAppArtwork(){
  const q=document.getElementById('appArtworkSearch').value.trim();if(q.length<2){editorMessage('Enter at least two characters.');return}
  editorMessage('Searching SteamGridDB…');
  document.getElementById('appArtworkGames').replaceChildren();document.getElementById('appArtworkGrids').replaceChildren();
  try{
    const data=await api('/api/applications/artwork/search?q='+encodeURIComponent(q));
    const host=document.getElementById('appArtworkGames');
    data.games.forEach(g=>{
      const btn=document.createElement('button');btn.className='secondary';btn.textContent=g.name;
      btn.onclick=()=>loadArtworkGrids(g.id);host.appendChild(btn);
    });editorMessage(data.games.length?'Select the game to view its posters.':'No matching games found.');
  }catch(e){editorMessage(e.message)}
}
async function loadArtworkGrids(gameId){
  artworkGameId=gameId;editorMessage('Loading poster choices…');
  const host=document.getElementById('appArtworkGrids');host.replaceChildren();
  try{
    const data=await api(`/api/applications/artwork/games/${gameId}`);
    data.artwork.forEach(a=>{
      const button=document.createElement('button');button.className='artwork-grid-choice';button.title='Use this poster';
      const img=document.createElement('img');img.src=a.thumb;img.loading='lazy';img.alt='SteamGridDB poster';
      button.appendChild(img);button.onclick=()=>chooseArtwork(a.id);host.appendChild(button);
    });editorMessage(data.artwork.length?'Click the poster you want to use.':'No portrait posters found for this game.');
  }catch(e){editorMessage(e.message)}
}
async function chooseArtwork(artworkId){
  if(!editingDigest||!artworkGameId)return;editorMessage('Downloading and caching selected poster on the server…');
  try{
    const data=await api(`/api/applications/${editingDigest}/artwork`,{method:'POST',body:JSON.stringify({game_id:artworkGameId,artwork_id:artworkId})});
    const img=document.getElementById('appEditorPoster');img.src=data.artwork_url;img.classList.remove('hidden');
    document.getElementById('appEditorPlaceholder').classList.add('hidden');
    editorMessage('Poster saved centrally. Windows clients will fetch the new revision once.');
    lastApplicationSnapshot='';await loadApplications(true);
  }catch(e){editorMessage(e.message)}
}
