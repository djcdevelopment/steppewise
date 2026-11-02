#!/usr/bin/env python3
"""Fixture-only qualification. Managed mode kills only child hosts it launches."""
import argparse, concurrent.futures, hashlib, json, os, pathlib, shutil, socket
import subprocess, sys, tempfile, time, urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
WORKFLOWS = ('service-intake', 'local-ai-work')
class Results:
    def __init__(self): self.checks=[]; self.runs=[]; self.requests=[]
    def check(self,name,ok,detail=None,started=None):
        self.checks.append(dict(name=name,passed=bool(ok),detail=detail,latency_ms=round((time.monotonic()-started)*1000,3) if started is not None else 0.0))
    def scenario(self,name,fn):
        start=time.monotonic()
        try: return fn()
        except Exception as e: self.check(name+'.exception',False,type(e).__name__+': '+str(e),start)
        finally: self.runs.append(dict(name=name,latency_ms=round((time.monotonic()-start)*1000,3)))
R=Results()
def request(url,path,body=None):
    start=time.monotonic(); code=None
    try:
        data=None if body is None else json.dumps(body).encode()
        req=urllib.request.Request(url+path,data=data,headers={'Content-Type':'application/json'})
        try:
            with urllib.request.urlopen(req,timeout=15) as response: code=response.status; raw=response.read()
        except urllib.error.HTTPError as e: code=e.code; raw=e.read()
        try: value=json.loads(raw)
        except ValueError: value={'raw':raw.decode(errors='replace')}
        return code,value
    finally: R.requests.append(dict(path=path,status=code,latency_ms=round((time.monotonic()-start)*1000,3)))
def review(url,run,accept=True):
    body={'accept':accept,'expectedHash':run['artifacts'][-1]['hash']}
    if args.milestone != 'A' and accept: body['reviewer']='Qualification Operator'
    return request(url,'/api/runs/'+run['id']+'/review',body)
def inspect_run(run,prefix):
    R.check(prefix+'.fixture',run['evidenceMode']=='fixture')
    for a in run['artifacts']:
        R.check(prefix+'.artifact-hash.'+a['id'],hashlib.sha256(a['json'].encode()).hexdigest()==a['hash'])
    events=run['events']; seq=[e['sequence'] for e in events]
    R.check(prefix+'.sequence',seq==list(range(1,len(events)+1)))
    domain=[e for e in events if e['action']!='maf']
    R.check(prefix+'.causal-chain',all(e.get('causeSequence')==(domain[i-1]['sequence'] if i else None) for i,e in enumerate(domain)))
def normal(url,workflow,accept):
    code,run=request(url,'/api/runs/'+workflow,{})
    R.check(workflow+'.admitted',code==200)
    if not isinstance(run,dict) or 'id' not in run:
        R.check(workflow+'.dependent-checks',False,{'blocked_by':workflow+'.admitted'}); return
    inspect_run(run,workflow)
    R.check(workflow+'.awaiting-review',run['status']=='AwaitingReview')
    R.check(workflow+'.failed-original',len(run['artifacts'])==2 and not run['artifacts'][0]['passed'])
    R.check(workflow+'.repaired',run['artifacts'][-1]['passed'])
    code,_=request(url,'/api/runs/'+run['id']+'/review',{'accept':True,'expectedHash':'0'*64})
    R.check(workflow+'.stale-hash-rejected',code==409)
    code,result=review(url,run,accept)
    R.check(workflow+'.explicit-review',code==200 and result.get('status')==('Accepted' if accept else 'Rejected'))
    code,_=review(url,run,accept); R.check(workflow+'.duplicate-review-refused',code==409)

def verify_spans(directory,run,label):
    if args.milestone=='A':return
    spans=[json.loads(line) for line in (directory/'spans.jsonl').read_text().splitlines()]
    lookup={(s.get('traceId'),s.get('spanId')):s for s in spans}
    for event in run['events']:
        if event['action']=='maf':continue
        span=lookup.get((event.get('traceId'),event.get('spanId')))
        tags=(span or {}).get('tags',{})
        R.check('trace.domain-event.'+label+'.'+str(event['sequence']),bool(span) and tags.get('workflow.run.id')==run['id'] and str(tags.get('event.sequence'))==str(event['sequence']))
        if event['action']=='human.review' and event['status'] in ('DecisionRequested','Accepted','Rejected'):
            R.check('review.actor.'+label+'.'+str(event['sequence']),bool(event.get('actor')) and bool(event.get('actorSource')) and bool(event.get('decisionId')))
        if event['action']=='human.review' and event['status'] in ('Accepted','Rejected'):
            requested=[e for e in run['events'] if e['status']=='DecisionRequested' and e.get('decisionId')==event.get('decisionId')]
            R.check('review.actor-continuity.'+label,bool(requested) and requested[-1].get('actor')==event.get('actor'))

class Host:
    def __init__(self,directory,dll,dotnet,extra=None):
        self.directory=directory; directory.mkdir(parents=True,exist_ok=True)
        with socket.socket() as s: s.bind(('127.0.0.1',0)); port=s.getsockname()[1]
        self.url='http://127.0.0.1:'+str(port)
        self.marker=directory/'fault-hit.json'; self.trace=directory/'fault-sites.jsonl'
        for evidence in (self.marker,self.trace):
            if evidence.exists():
                attempt=len(list(directory.glob(evidence.name+'.attempt-*')))+1
                evidence.rename(directory/(evidence.name+'.attempt-'+str(attempt)))
        env=os.environ.copy()
        for k in list(env):
            if k.startswith('STEPPEWISE_'): del env[k]
        env.update(STEPPEWISE_ENV='qa',STEPPEWISE_PORT=str(port),STEPPEWISE_STORAGE=str(directory),STEPPEWISE_DEFINITIONS=str(ROOT/'examples'),STEPPEWISE_FAULT_MARKER=str(self.marker),STEPPEWISE_EMIT_TRACE=str(self.trace))
        env.update(extra or {})
        self.log=open(directory/'host.log','ab'); self.proc=subprocess.Popen([dotnet,str(dll)],cwd=dll.parent,env=env,stdout=self.log,stderr=subprocess.STDOUT)
    def ready(self):
        start=time.monotonic()
        while time.monotonic()-start<15:
            if self.marker.exists(): return False
            if self.proc.poll() is not None: raise RuntimeError('Host exited: '+(self.directory/'host.log').read_text(errors='replace')[-1400:])
            try:
                with urllib.request.urlopen(self.url+'/api/info',timeout=.2) as response: info=json.load(response)
                if args.milestone!='A':
                    rev=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
                    R.check('identity.revision',info.get('gitRevision')==rev,info)
                    R.check('identity.dll-hash',info.get('dllSha256')==args.dll_sha256)
                return True
            except (OSError,urllib.error.URLError): time.sleep(.02)
        raise TimeoutError('Host readiness timeout')
    def stop(self):
        if self.proc.poll() is None: self.proc.kill()
        self.proc.wait(timeout=10); self.log.close()
    def rows(self): return [json.loads(x) for x in self.trace.read_text().splitlines()] if self.trace.exists() else []
def snapshots(directory):
    """Independent durable-prefix oracle; snapshot-only input remains supported."""
    states={json.loads(p.read_text())['id']:json.loads(p.read_text()) for p in directory.glob('run-*.json')}
    for log in directory.glob('run-*.events.jsonl'):
        rows=[json.loads(line) for line in log.read_text().splitlines()]
        if not rows: continue
        first=rows[0]; state=dict(id=first['runId'],workflow=first['workflow'],status='Running',version=0,evidenceMode='fixture',events=[],artifacts=[])
        for row in rows:
            state['events'].append(row['event']); state['version']+=1
            if row.get('nextStatus'):state['status']=row['nextStatus']
            artifact=row.get('artifact')
            if artifact:
                state['artifacts']=[a for a in state['artifacts'] if a['id']!=artifact['id']]+[artifact]
        states[state['id']]=state
    return list(states.values())
def clone_seed(seed,target):
    target.mkdir(parents=True)
    if seed:
        for pattern in ('run-*.json','run-*.events.jsonl','spans.jsonl'):
            for p in seed.glob(pattern): shutil.copy2(p,target/p.name)
def drive(host,workflow,scenario):
    if scenario.startswith('recover'): return
    if scenario.startswith('restored'):
        _,runs=request(host.url,'/api/runs'); run=runs[0]
    else:
        _,run=request(host.url,'/api/runs/'+workflow,{})
    if scenario!='repair-fails': review(host.url,run,scenario!='reject')
def dangling(run):
    ev=run['events']; decisions=[e['sequence'] for e in ev if e['action']=='human.review' and e['status']=='DecisionRequested']
    terminal=[e['sequence'] for e in ev if e['action']=='human.review' and e['status'] in ('Accepted','Rejected')]
    return bool(decisions) and (not terminal or max(decisions)>max(terminal))
def expected_state(before):
    if before['status'] in ('Accepted','Rejected','Failed'): return before['status']
    if dangling(before) or before['status']=='Running': return 'Failed'
    return 'AwaitingReview'
def restart_check(directory,dll,dotnet,before,label):
    host=Host(directory,dll,dotnet)
    try:
        host.ready(); _,runs=request(host.url,'/api/runs')
        R.check('fault.restart-state.'+label,len(runs)==len(before))
        for old in before:
            new=next(r for r in runs if r['id']==old['id'])
            R.check('fault.restart-state.'+label+'.'+old['id'],new['status']==expected_state(old))
            R.check('fault.no-dangling-review.'+label,not(new['status']=='AwaitingReview' and dangling(new)))
            if dangling(old) and old['status']=='AwaitingReview':
                R.check('recovery.pending-decision.'+label,new['status']=='Failed' and any(e['status']=='NeedsReconciliation' for e in new['events']))
            if new['status'] in ('Accepted','Rejected','Failed') and new['artifacts']:
                code,_=review(host.url,new); R.check('fault.review-refused.'+label,code==409)
            inspect_run(new,'fault.integrity.'+label)
            verify_spans(directory,new,label)
        return runs
    finally: host.stop()

def message_probe(root,dll,dotnet):
    probe=root/'message-probe';probe.mkdir()
    (probe/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup></Project>')
    (probe/'Program.cs').write_text(r'''
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
var location = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) => { var p = Path.Combine(Path.GetDirectoryName(location)!, name.Name + ".dll"); return File.Exists(p) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null; };
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(location);
var method = assembly.GetType("Steppewise.WorkflowRuntime")!.GetMethod("MessageContractSamples", BindingFlags.Public | BindingFlags.Static)!;
var values = (object[])method.Invoke(null, null)!;
var results = values.Select(value => {
    var type=value.GetType(); var json=JsonSerializer.Serialize(value,type);
    var restored=JsonSerializer.Deserialize(json,type)!;
    bool coordinates=new[]{"RunId","Sequence","CauseSequence"}.All(name=>type.GetProperty(name)!=null);
    bool preserved=new[]{"RunId","Sequence","CauseSequence"}.All(name=>Equals(type.GetProperty(name)?.GetValue(value),type.GetProperty(name)?.GetValue(restored)));
    return new { name=type.Name, sealedRecord=type.IsSealed && type.GetMethod("<Clone>$")!=null, coordinates, preserved, plainJsonRoundTrip=json==JsonSerializer.Serialize(restored,type), forbiddenReferences=Forbidden(type,new HashSet<Type>()) };
}).ToArray();
Console.WriteLine(JsonSerializer.Serialize(results));
static bool Forbidden(Type type, HashSet<Type> visited) {
    if(!visited.Add(type))return false;
    if(typeof(Delegate).IsAssignableFrom(type)||typeof(Stream).IsAssignableFrom(type)||typeof(System.Runtime.InteropServices.SafeHandle).IsAssignableFrom(type)||type.Name.Contains("Session")||type==typeof(IntPtr)||type==typeof(UIntPtr)||type==typeof(object))return true;
    if(type.IsPrimitive||type.IsEnum||type==typeof(string)||type==typeof(decimal)||type==typeof(DateTime)||type==typeof(DateTimeOffset)||type==typeof(Guid))return false;
    if(type.IsArray)return Forbidden(type.GetElementType()!,visited);
    if(type.IsGenericType && type.Namespace?.StartsWith("System")==true)return type.GetGenericArguments().Any(t=>Forbidden(t,visited));
    return type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Any(f=>Forbidden(f.FieldType,visited));
}
''')
    start=time.monotonic();result=subprocess.run([dotnet,'run','--project',str(probe/'Probe.csproj'),'-c','Release','--',str(dll)],capture_output=True,text=True,timeout=60)
    (probe/'stdout.txt').write_text(result.stdout);(probe/'stderr.txt').write_text(result.stderr)
    R.check('messages.serialization-probe-executed',result.returncode==0,result.stderr,start)
    if result.returncode:return
    records=json.loads(result.stdout.strip().splitlines()[-1])
    R.check('messages.all-executor-contracts',set(r['name'] for r in records)=={'RunInput','Candidate','Verdict','ReviewDecision','RunOutcome'})
    for r in records:
        R.check('messages.sealed-record.'+r['name'],r['sealedRecord'])
        R.check('messages.plain-json-roundtrip.'+r['name'],r['plainJsonRoundTrip'])
        R.check('messages.coordinates-preserved.'+r['name'],r['coordinates'] and r['preserved'])
        R.check('messages.no-runtime-references.'+r['name'],not r['forbiddenReferences'])

def qualification_c(root,dll,dotnet):
    R.scenario('messages.serialization',lambda:message_probe(root,dll,dotnet))
    source=(ROOT/'src/Steppewise/Runtime.cs').read_text()
    R.check('messages.no-direct-artifact-mutation','s.Snapshot.Artifacts.Add(' not in source and 's.Snapshot.Artifacts[^1] =' not in source)
    R.check('definition.no-ordinal-indexing','Take(4)' not in source and 'Steps[4]' not in source)
    def refused_host(directory,label,extra=None):
        h=Host(directory,dll,dotnet,extra)
        try:
            try: ready=h.ready(); R.check(label,not ready,'Host must refuse corrupted/unsupported input')
            except RuntimeError as error:
                message=str(error)
                error_lines=[line for line in (directory/'host.log').read_text(errors='replace').splitlines() if 'InvalidDataException:' in line]
                validation=error_lines[0].split('InvalidDataException:',1)[1].strip().lower() if error_lines else ''
                clear=(validation.startswith('unsupported definition: v1 supports exactly five steps in order:') and 'IndexOutOfRange' not in message) if label=='definition.sixth-step-rejected' else True
                R.check(label,clear,message)
        finally:h.stop()
    for workflow in WORKFLOWS:
        def log_only():
            origin=root/workflow/'accept-baseline'; target=root/(workflow+'-log-only');target.mkdir()
            for pattern in ('run-*.events.jsonl','spans.jsonl'):
                for p in origin.glob(pattern):shutil.copy2(p,target/p.name)
            R.check('storage.log-only.no-snapshot.'+workflow,not list(target.glob('run-*.json')))
            expected=snapshots(origin);h=Host(target,dll,dotnet)
            try:
                h.ready();_,runs=request(h.url,'/api/runs')
                R.check('storage.log-only.'+workflow,runs[0]['status']==expected[0]['status'] and runs[0]['artifacts']==expected[0]['artifacts'] and runs[0]['events']==expected[0]['events'])
            finally:h.stop()
        R.scenario('storage.log-only.'+workflow,log_only)
        def tamper():
            target=root/(workflow+'-tampered');clone_seed(root/workflow/'accept-baseline',target)
            p=next(target.glob('run-*.json'));obj=json.loads(p.read_text());obj['artifacts'][0]['json']='{}';p.write_text(json.dumps(obj));refused_host(target,'storage.tamper-rejected.'+workflow)
        R.scenario('storage.tamper.'+workflow,tamper)
        def corrupt():
            target=root/(workflow+'-corrupt-log');clone_seed(root/workflow/'accept-baseline',target)
            p=next(target.glob('run-*.events.jsonl'));p.write_text(p.read_text()+'{broken\n');before=p.read_bytes();refused_host(target,'storage.corrupt-log-refused.'+workflow)
            R.check('storage.corrupt-log-preserved.'+workflow,p.read_bytes()==before)
        R.scenario('storage.corrupt-log.'+workflow,corrupt)
        def legacy():
            origin=pathlib.Path(args.golden_root)/workflow/'waiting-seed'; target=root/(workflow+'-legacy');target.mkdir()
            for p in origin.glob('run-*.json'):shutil.copy2(p,target/p.name)
            before={p.name:p.read_bytes() for p in target.glob('run-*.json')};h=Host(target,dll,dotnet)
            try:
                h.ready();_,runs=request(h.url,'/api/runs');R.check('storage.legacy-readable.'+workflow,len(runs)==1)
                code,_=review(h.url,runs[0]);R.check('storage.legacy-review-refused.'+workflow,400<=code<500)
            finally:h.stop()
            R.check('storage.legacy-preserved.'+workflow,before=={p.name:p.read_bytes() for p in target.glob('run-*.json')})
        R.scenario('storage.legacy.'+workflow,legacy)
    definitions=root/'invalid-definitions';shutil.copytree(ROOT/'examples',definitions)
    p=definitions/'service-intake.json';d=json.loads(p.read_text());d['steps'].append(dict(id='sixth',action='verify',boundary='software'));p.write_text(json.dumps(d))
    refused_host(root/'sixth-step','definition.sixth-step-rejected',{'STEPPEWISE_DEFINITIONS':str(definitions)})

def managed(args):
    root=pathlib.Path(tempfile.mkdtemp(prefix='steppewise-ma-',dir=args.storage_parent)).resolve(); args.storage_root=str(root)
    original_dll=pathlib.Path(args.dll).resolve(); dotnet=args.dotnet
    shutil.copytree(original_dll.parent,root/'published')
    dll=root/'published'/original_dll.name
    args.dll_sha256=hashlib.sha256(dll.read_bytes()).hexdigest()
    R.check('build.dll-exists',dll.exists())
    if args.milestone!='A':
        def browser_check():
            evidence=json.loads(pathlib.Path(args.browser_evidence).read_text()) if args.browser_evidence else {}
            revision=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
            R.check('review.actor-visible.all-personas',evidence.get('gitRevision')==revision and evidence.get('dllSha256')==args.dll_sha256 and evidence.get('actorsAllPersonas') is True,evidence)
        R.scenario('browser.evidence',browser_check)
    seen=set(); declared=set(); ordinal_count=0
    for workflow in WORKFLOWS:
        try:
            workflow_seen=set(); workflow_ordinals=0; expected_ordinals=0
            base=root/workflow; base.mkdir()
            # Create untouched waiting-review seed using real execution.
            waiting=base/'waiting-seed'; h=Host(waiting,dll,dotnet)
            try: h.ready(); request(h.url,'/api/runs/'+workflow,{})
            finally: h.stop()
            seeds={'restored-accept':waiting}
            scenarios=['accept','reject','repair-fails','restored-accept','review-fails']
            for scenario in scenarios:
                try:
                    scenario_started=time.monotonic()
                    label=workflow+'.'+scenario; seed=seeds.get(scenario)
                    extra={'STEPPEWISE_FAIL_REPAIR':'1'} if scenario=='repair-fails' else {'STEPPEWISE_FAIL_REVIEW':'1'} if scenario=='review-fails' else {}
                    baseline=base/(scenario+'-baseline'); clone_seed(seed,baseline)
                    h=Host(baseline,dll,dotnet,extra)
                    try:
                        h.ready(); drive(h,workflow,scenario); rows=h.rows(); seen.update(r['site'] for r in rows); workflow_seen.update(r['site'] for r in rows); expected_ordinals+=len(rows)
                        runs=snapshots(baseline)
                        for evidence_run in runs: verify_spans(baseline,evidence_run,label)
                        canonical=[dict(workflow=r['workflow'],status=r['status'],artifacts=r['artifacts'],domain=[dict(action=e['action'],boundary=e['boundary'],status=e['status'],detail=e['detail']) for e in r['events'] if e['action']!='maf']) for r in runs]
                        (baseline/'canonical-domain.json').write_text(json.dumps(canonical,sort_keys=True,indent=2))
                        if args.milestone=='C':
                            for evidence_run in runs:
                                _, projected=request(h.url,'/api/runs/'+evidence_run['id'])
                                R.check('messages.projection-equivalence.'+label,projected==evidence_run)
                                envelopes=[json.loads(line) for line in (baseline/('run-'+evidence_run['id']+'.events.jsonl')).read_text().splitlines()]
                                payloads=[e for e in envelopes if e['event']['status'] in ('CandidateProduced','CheckPassed','CheckFailed')]
                                R.check('messages.typed-flow.'+label,len(payloads)==4 and all(e.get('artifact') and e['artifact']['hash']==hashlib.sha256(e['artifact']['json'].encode()).hexdigest() for e in payloads))
                            golden=pathlib.Path(args.golden_root or '')/workflow/(scenario+'-baseline')/'canonical-domain.json'
                            R.check('projection.baseline-equivalence.'+label,golden.exists() and json.loads(golden.read_text())==canonical)
                            writes=[json.loads(line) for line in (baseline/'storage-writes.jsonl').read_text().splitlines()]
                            if not seed:
                                for evidence_run in runs:
                                    own=[w for w in writes if w['runId']==evidence_run['id']]
                                    appends=sum(w['kind']=='event-append' for w in own); snaps=sum(w['kind']=='snapshot' for w in own)
                                    R.check('storage.write-budget.'+label,appends==len(evidence_run['events']) and snaps<=3,{'appends':appends,'snapshots':snaps})
                        if scenario=='repair-fails':
                            R.check('repair-failed.artifacts.'+workflow,len(runs)==1 and runs[0]['status']=='Failed' and len(runs[0]['artifacts'])==2 and not runs[0]['artifacts'][-1]['passed'])
                            code,_=review(h.url,runs[0]);R.check('repair-failed.review-refused.'+workflow,code==409)
                        if scenario=='review-fails': R.check('review-failed.reconciliation.'+workflow,runs[0]['status']=='Failed' and any(e['status']=='NeedsReconciliation' for e in runs[0]['events']))
                    finally: h.stop()
                    for n in range(1,len(rows)+1):
                        trial_started=time.monotonic()
                        trial=base/(scenario+'-fault-'+str(n));clone_seed(seed,trial)
                        h=Host(trial,dll,dotnet,dict(extra,STEPPEWISE_FAULT_AFTER=str(n)))
                        pool=concurrent.futures.ThreadPoolExecutor(max_workers=1); future=None
                        try:
                            if h.ready(): future=pool.submit(drive,h,workflow,scenario)
                            deadline=time.monotonic()+15
                            while not h.marker.exists() and time.monotonic()<deadline and h.proc.poll() is None: time.sleep(.01)
                            hit=json.loads(h.marker.read_text()) if h.marker.exists() else None
                            R.check('fault.ordinal.'+label+'.'+str(n),hit is not None and hit['ordinal']==n)
                            if hit: seen.add(hit['site']);workflow_seen.add(hit['site']);ordinal_count+=1;workflow_ordinals+=1
                        finally:
                            h.stop();pool.shutdown(wait=True)
                        before=snapshots(trial)
                        # Capture real interrupted prefixes to exercise startup Emit sites later.
                        if before and before[0]['status']=='Running' and 'recover-running' not in seeds:
                            target=base/'running-seed';clone_seed(trial,target);seeds['recover-running']=target
                        if before and dangling(before[0]) and before[0]['status']=='AwaitingReview' and 'recover-decision' not in seeds:
                            target=base/'decision-seed';clone_seed(trial,target);seeds['recover-decision']=target
                        restart_check(trial,dll,dotnet,before,label+'.'+str(n))
                        R.runs.append(dict(name=label+'.fault-'+str(n),storage=str(trial),latency_ms=round((time.monotonic()-trial_started)*1000,3)))
                    R.runs.append(dict(name=label,latency_ms=round((time.monotonic()-scenario_started)*1000,3)))
                except Exception as e:
                    R.check('scenario.'+workflow+'.'+scenario,False,type(e).__name__+': '+str(e))
            for scenario in ('recover-running','recover-decision'):
                seed=seeds.get(scenario);R.check('fault.seed.'+workflow+'.'+scenario,seed is not None)
                if not seed: continue
                trial=base/scenario;clone_seed(seed,trial);h=Host(trial,dll,dotnet,{'STEPPEWISE_FAULT_AFTER':'1'})
                try:
                    h.ready();hit=json.loads(h.marker.read_text());seen.add(hit['site']);workflow_seen.add(hit['site']);ordinal_count+=1;workflow_ordinals+=1;expected_ordinals+=1
                    R.check('fault.ordinal.'+workflow+'.'+scenario,hit['ordinal']==1)
                finally:h.stop()
                before=snapshots(trial);after=restart_check(trial,dll,dotnet,before,workflow+'.'+scenario)
                again=restart_check(trial,dll,dotnet,after,workflow+'.'+scenario+'.idempotent')
                R.check('recovery.idempotent.'+workflow+'.'+scenario,[len(x['events']) for x in after]==[len(x['events']) for x in again])
            declared_workflow=set(__import__('re').findall(r'"([^"]+)"', __import__('re').search(r'EmitSites\s*=\s*\[([^]]+)\]', (ROOT/'src/Steppewise/Runtime.cs').read_text()).group(1)))
            R.check('fault.site-coverage.'+workflow,declared_workflow<=workflow_seen,{'missing':sorted(declared_workflow-workflow_seen)})
            R.check('fault.ordinal-coverage.'+workflow,workflow_ordinals==expected_ordinals,{'covered':workflow_ordinals,'expected':expected_ordinals})
        except Exception as e:
            R.check('workflow.'+workflow+'.exception',False,type(e).__name__+': '+str(e))
    if args.milestone=='C': R.scenario('milestone-C',lambda:qualification_c(root,dll,dotnet))
    # Stable inventory emitted by runtime, never infer coverage from just observed rows.
    inventory=ROOT/'src/Steppewise/Runtime.cs'
    import re
    declared=set(re.findall(r'"([^"]+)"', re.search(r'EmitSites\s*=\s*\[([^]]+)\]', inventory.read_text()).group(1)))
    R.check('fault.site-inventory-present',bool(declared))
    R.check('fault.site-coverage.all',bool(declared) and declared<=seen,{'declared':sorted(declared),'observed':sorted(seen),'missing':sorted(declared-seen),'ordinal_trials':ordinal_count})

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--browser-evidence',help='Exact-candidate browser JSON; required for B/C actor visibility proof')
parser.add_argument('--golden-root',help='Baseline preserved evidence root; required for tier C canonical comparison')
parser.add_argument('--milestone',choices=['A','B','C'],default='A')
parser.add_argument('--url',default='http://127.0.0.1:5188')
parser.add_argument('--managed',action='store_true');parser.add_argument('--dll',default=str(ROOT/'artifacts/portable/Steppewise.dll'))
parser.add_argument('--dotnet',default=os.environ.get('DOTNET_BIN','dotnet'))
parser.add_argument('--storage-parent',default=tempfile.gettempdir());parser.add_argument('--output');parser.add_argument('--inject-check-failure',action='store_true')
args=parser.parse_args();start=time.monotonic()
if args.inject_check_failure:R.check('qualification.deliberately-broken',False,'Intentional collector self-check')
R.check('qualification.later-sentinel',True)
probe=Results();probe.check('deliberate-failure',False);probe.check('later-sentinel',True)
R.check('qualification.collects-failures',len(probe.checks)==2 and not probe.checks[0]['passed'] and probe.checks[1]['passed'])
if args.managed:R.scenario('managed',lambda:managed(args))
else:
    for workflow,accept in zip(WORKFLOWS,(True,False)):R.scenario(workflow,lambda w=workflow,a=accept:normal(args.url,w,a))
R.check('qualification.latency-fields',all('latency_ms' in x for x in R.checks+R.requests+R.runs))
report=dict(schema_version=1,evidenceCategory='synthetic-managed-fault-qualification' if args.managed else 'synthetic-http-qualification',liveInference=False,storage_root=getattr(args,'storage_root',None),dll_sha256=getattr(args,'dll_sha256',None),checks=R.checks,runs=R.runs,requests=R.requests,latency_ms=round((time.monotonic()-start)*1000,3),passed=sum(x['passed'] for x in R.checks),failed=sum(not x['passed'] for x in R.checks))
output=json.dumps(report,indent=2)+'\n'
if args.output:pathlib.Path(args.output).write_text(output)
print(output,end='');sys.exit(1 if report['failed'] else 0)
