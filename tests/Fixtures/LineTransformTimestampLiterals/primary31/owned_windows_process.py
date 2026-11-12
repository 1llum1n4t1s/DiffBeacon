"""Future Windows-owned process evidence, used only after new reproduction GO."""
import ctypes,time,subprocess,threading,hashlib,json,datetime
from ctypes import wintypes
from pathlib import Path
k=ctypes.WinDLL('kernel32',use_last_error=True)
for n,args,ret in [('GetProcessTimes',[wintypes.HANDLE,*([ctypes.POINTER(wintypes.FILETIME)]*4)],wintypes.BOOL),('GetExitCodeProcess',[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)],wintypes.BOOL),('WaitForSingleObject',[wintypes.HANDLE,wintypes.DWORD],wintypes.DWORD),('CreateJobObjectW',[ctypes.c_void_p,wintypes.LPCWSTR],wintypes.HANDLE),('SetInformationJobObject',[wintypes.HANDLE,ctypes.c_int,ctypes.c_void_p,wintypes.DWORD],wintypes.BOOL),('AssignProcessToJobObject',[wintypes.HANDLE,wintypes.HANDLE],wintypes.BOOL),('TerminateJobObject',[wintypes.HANDLE,wintypes.UINT],wintypes.BOOL),('TerminateProcess',[wintypes.HANDLE,wintypes.UINT],wintypes.BOOL),('CloseHandle',[wintypes.HANDLE],wintypes.BOOL)]:
 fn=getattr(k,n);fn.argtypes=args;fn.restype=ret
class BASIC(ctypes.Structure):_fields_=[('processTime',ctypes.c_longlong),('jobTime',ctypes.c_longlong),('flags',wintypes.DWORD),('minWS',ctypes.c_size_t),('maxWS',ctypes.c_size_t),('active',wintypes.DWORD),('affinity',ctypes.c_size_t),('priority',wintypes.DWORD),('scheduling',wintypes.DWORD)]
class IO(ctypes.Structure):_fields_=[(x,ctypes.c_ulonglong) for x in ['readOps','writeOps','otherOps','readBytes','writeBytes','otherBytes']]
class EXT(ctypes.Structure):_fields_=[('basic',BASIC),('io',IO),('processMemory',ctypes.c_size_t),('jobMemory',ctypes.c_size_t),('peakProcess',ctypes.c_size_t),('peakJob',ctypes.c_size_t)]
def times(h):
 v=[wintypes.FILETIME() for _ in range(4)];assert k.GetProcessTimes(h,*[ctypes.byref(x) for x in v]);return [(x.dwHighDateTime<<32)|x.dwLowDateTime for x in v]
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def bytes_under(root):return sum(x.stat().st_size for x in Path(root).rglob('*') if x.is_file())
def run_owned(item,env,root,budget,overallDeadline):
 assert bytes_under(root)<budget;job=k.CreateJobObjectW(None,None);assert job;limits=EXT();limits.basic.flags=0x2000;assert k.SetInformationJobObject(job,9,ctypes.byref(limits),ctypes.sizeof(limits))
 p=subprocess.Popen(item['command'],cwd=root,env=env,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NEW_PROCESS_GROUP);h=int(p._handle);born=times(h);assigned=bool(k.AssignProcessToJobObject(job,h));channels={};errors=[];tooLarge=threading.Event()
 def drain(stream,key):
  path=Path(root)/'proof'/(item['id']+'.'+key+'.bin');digest=hashlib.sha256();size=0
  try:
   with path.open('xb') as f:
    while True:
     b=stream.read(65536)
     if not b:break
     f.write(b);digest.update(b);size+=len(b)
     if bytes_under(root)>=budget:tooLarge.set()
   channels[key]={'path':str(path),'sha256':digest.hexdigest(),'size':size,'eof':True,'eofUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()}
  except Exception as e:errors.append(str(e));tooLarge.set();channels[key]={'path':str(path),'size':size,'eof':False,'error':str(e)}
 threads=[threading.Thread(target=drain,args=(p.stdout,'stdout'),daemon=True),threading.Thread(target=drain,args=(p.stderr,'stderr'),daemon=True)]
 for t in threads:t.start()
 stop=None;deadline=min(overallDeadline,time.monotonic()+item['timeoutSeconds'])
 if not assigned:
  assert times(h)[0]==born[0];stop='own-job-assignment-failed';k.TerminateProcess(h,125)
 while p.poll() is None:
  if stop is None and (tooLarge.is_set() or bytes_under(root)>=budget or time.monotonic()>=deadline):
   assert times(h)[0]==born[0];stop='byte-budget' if tooLarge.is_set() or bytes_under(root)>=budget else 'timeout'
   if not k.TerminateJobObject(job,124):k.TerminateProcess(h,124)
  time.sleep(.05)
 p.wait()
 for t in threads:t.join(timeout=10)
 if any(t.is_alive() for t in threads):
  assert times(h)[0]==born[0];stop=stop or 'both-EOF-timeout';k.TerminateJobObject(job,126)
  for t in threads:t.join(timeout=5)
 after=times(h);ec=wintypes.DWORD();assert k.GetExitCodeProcess(h,ctypes.byref(ec));wait=k.WaitForSingleObject(h,0);outputs=[]
 for output in item.get('outputs',[]):
  if Path(output).exists():outputs.append({'path':output,'sha256':sha(output),'size':Path(output).stat().st_size})
 receipt={'id':item['id'],'command':item['command'],'pid':p.pid,'birthFileTime':born[0],'osExitFileTime':after[1],'heldProcessHandle':h,'birthMatched':born[0]==after[0],'osExitCode':ec.value,'pythonExitCode':p.returncode,'waitResult':wait,'handleHeldThroughOsExitAndBothEof':wait==0 and all(channels.get(x,{}).get('eof') for x in ['stdout','stderr']),'stdout':channels.get('stdout'),'stderr':channels.get('stderr'),'outputs':outputs,'jobAssigned':assigned,'stopReason':stop,'errors':errors,'bytes':bytes_under(root),'noResubmission':True}
 with (Path(root)/'proof'/(item['id']+'.receipt.json')).open('x',encoding='utf-8') as f:json.dump(receipt,f,indent=2)
 k.CloseHandle(job)
 assert ec.value==p.returncode==0 and wait==0 and born[0]==after[0] and receipt['handleHeldThroughOsExitAndBothEof'] and assigned and not stop and not errors and bytes_under(root)<budget,('command failed; partial retained, no retry',item['id'])
 assert len(outputs)==len(item.get('outputs',[]))
 return receipt
