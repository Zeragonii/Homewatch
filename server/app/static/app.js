let state={children:[],pending:[],devices:[]};
const loginPanel=document.getElementById('login');
const appPanel=document.getElementById('app');
const usernameInput=document.getElementById('username');
const passwordInput=document.getElementById('password');
const loginErrorEl=document.getElementById('loginError');
const childrenEl=document.getElementById('children');
const pendingEl=document.getElementById('pending');
const devicesEl=document.getElementById('devices');
const childNameInput=document.getElementById('childName');
async function api(url, options={}){const r=await fetch(url,{headers:{'Content-Type':'application/json',...(options.headers||{})},...options});if(r.status===401)throw new Error('AUTH');if(!r.ok){let m='Request failed';try{m=(await r.json()).detail||m}catch{}throw new Error(m)}const ct=r.headers.get('content-type')||'';return ct.includes('json')?r.json():r}
async function login(){try{await api('/api/login',{method:'POST',body:JSON.stringify({username:usernameInput.value,password:passwordInput.value})});await showApp();}catch(e){loginErrorEl.textContent=e.message==='AUTH'?'Invalid credentials':e.message}}
async function logout(){await fetch('/api/logout',{method:'POST'});location.reload()}
async function showApp(){loginPanel.classList.add('hidden');appPanel.classList.remove('hidden');await refresh();setInterval(refresh,5000)}
async function refresh(){try{state=await api('/api/dashboard');render()}catch(e){if(e.message==='AUTH'){appPanel.classList.add('hidden');loginPanel.classList.remove('hidden')}}}
function fmt(sec){sec=Number(sec);if(sec<60)return sec+'s';const h=Math.floor(sec/3600),m=Math.floor((sec%3600)/60);return h?`${h}h ${m}m`:`${m}m`}
function render(){childrenEl.innerHTML=state.children.map(c=>`<span class="chip">${esc(c.name)}</span>`).join('')||'<span class="muted">No child profiles yet.</span>';
pendingEl.innerHTML=state.pending.map(p=>`<div class="pending"><div class="row"><div><b>${esc(p.hostname)}</b><br><span class="muted">${esc(p.os_version)} · Agent ${esc(p.agent_version)}</span></div><div class="row"><select id="child-${p.installation_id}">${state.children.map(c=>`<option value="${c.id}">${esc(c.name)}</option>`).join('')}</select><input id="name-${p.installation_id}" placeholder="Device name" value="${esc(p.hostname)}"><button onclick="approve('${p.installation_id}')">Enrol</button></div></div></div>`).join('')||'<p class="muted">No devices waiting for approval.</p>';
devicesEl.innerHTML=state.devices.map(d=>`<article class="device"><div class="row"><div><h3>${esc(d.child)} — ${esc(d.name)}</h3><span class="${d.online?'online':'offline'}">● ${d.online?'Online':'Offline'}</span></div><small class="muted">Agent ${esc(d.agent_version||'—')}</small></div><p><b>Current:</b> ${esc(d.current_app||'No foreground app reported')}</p><div class="activity">${d.activity.map(a=>`<div><span>${esc(a.process)}</span><b>${fmt(a.seconds)}</b></div>`).join('')||'<span class="muted">No activity today.</span>'}</div><div class="actions"><input id="msg-${d.id}" placeholder="Message"><button onclick="sendMessage('${d.id}')">Send</button><button class="secondary" onclick="shot('${d.id}')">Screenshot</button></div><img id="img-${d.id}" class="screenshot hidden"></article>`).join('')||'<p class="muted">No enrolled devices.</p>'}
async function addChild(){if(!childNameInput.value.trim())return;await api('/api/children',{method:'POST',body:JSON.stringify({name:childNameInput.value.trim()})});childNameInput.value='';refresh()}
async function approve(id){const child=Number(document.getElementById('child-'+id).value),name=document.getElementById('name-'+id).value.trim();await api('/api/pending/'+id+'/approve',{method:'POST',body:JSON.stringify({child_id:child,device_name:name})});refresh()}
async function sendMessage(id){const el=document.getElementById('msg-'+id);if(!el.value.trim())return;await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'message',payload:el.value.trim()})});el.value=''}
async function shot(id){await api(`/api/devices/${id}/commands`,{method:'POST',body:JSON.stringify({kind:'screenshot',payload:''})});setTimeout(()=>{const img=document.getElementById('img-'+id);img.src=`/api/devices/${id}/screenshot?t=${Date.now()}`;img.onload=()=>img.classList.remove('hidden')},2500)}
function esc(s){return String(s??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
(async()=>{try{await refresh();loginPanel.classList.add('hidden');appPanel.classList.remove('hidden');setInterval(refresh,5000)}catch{}})();
