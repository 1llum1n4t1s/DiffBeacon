"""自作CC0 PNG/APNG。期待canvasは手書きで、decoder/compositor出力を使用しない。"""
import argparse, base64, hashlib, json, stat, struct, zlib
from pathlib import Path

BLUE=(255,0,0,255); RED=(0,0,255,255); GREEN=(0,255,0,255); WHITE=(255,255,255,255)
CLEAR=(0,0,0,0); HIDDEN=(13,37,71,0); HALF_RED=(0,0,255,128); OVER_RED_BLUE=(127,0,128,255)
SIG=b'\x89PNG\r\n\x1a\n'
def sha(data):return hashlib.sha256(data).hexdigest().upper()
def chunk(kind,body):return struct.pack('>I',len(body))+kind+body+struct.pack('>I',zlib.crc32(kind+body))
def header(w,h):return chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6,0,0,0))
def compressed(w,h,pixels):
    assert len(pixels)==w*h
    rgba=bytes(v for b,g,r,a in pixels for v in (r,g,b,a))
    return zlib.compress(b''.join(b'\0'+rgba[y*w*4:(y+1)*w*4] for y in range(h)))
def static(w,h,pixels):return SIG+header(w,h)+chunk(b'IDAT',compressed(w,h,pixels))+chunk(b'IEND',b'')
def frame(w,h,pixels,x=0,y=0,dispose=0,blend=0):return (w,h,pixels,x,y,dispose,blend)
def apng(w,h,frames,default=None):
    result=SIG+header(w,h)+chunk(b'acTL',struct.pack('>II',len(frames),0));seq=0
    if default is not None:result+=chunk(b'IDAT',compressed(w,h,default))
    for index,(fw,fh,pixels,x,y,dispose,blend) in enumerate(frames):
        result+=chunk(b'fcTL',struct.pack('>IIIIIHHBB',seq,fw,fh,x,y,1,10,dispose,blend));seq+=1
        data=compressed(fw,fh,pixels)
        if index==0 and default is None:result+=chunk(b'IDAT',data)
        else:result+=chunk(b'fdAT',struct.pack('>I',seq)+data);seq+=1
    return result+chunk(b'IEND',b'')
def mutate(data,kind,change,occurrence=0):
    pos=8;result=data[:8];seen=0
    while pos<len(data):
        size=struct.unpack_from('>I',data,pos)[0];key=data[pos+4:pos+8];body=data[pos+8:pos+8+size]
        if key==kind:
            if seen==occurrence:body=change(body)
            seen+=1
        result+=chunk(key,body);pos+=size+12
    return result
def expected(w,h,rows):
    # rowsは以下の各caseで明示した全canvas。入力frameから合成しない。
    assert len(rows)==w*h
    raw=bytes(v for pixel in rows for v in pixel)
    return {'width':w,'height':h,'bgra':[list(p) for p in rows],'bgraBase64':base64.b64encode(raw).decode(),'pixelSha256':sha(raw)}
def assets():
    definitions={}
    def valid(name,data,w,h,canvases,description,defaultIncluded=None):
        definitions[name]=(data,{'valid':True,'description':description,'defaultImageIncluded':defaultIncluded,'frameCount':len(canvases),'frames':[expected(w,h,pixels) for pixels in canvases]})
    first=frame(2,2,[RED]*4)
    left=apng(2,2,[first,frame(2,2,[GREEN]*4)])
    valid('same-first-left.png',left,2,2,[[RED]*4,[GREEN]*4],'same red first frame; green later',True)
    # §5.6 Table 7: acTL/fcTLの相対順序は固定されない。既存chunk bytes/CRCをそのまま交換。
    start=8+len(header(2,2));middle=start+struct.unpack_from('>I',left,start)[0]+12
    end=middle+struct.unpack_from('>I',left,middle)[0]+12
    assert left[start+4:start+8]==b'acTL' and left[middle+4:middle+8]==b'fcTL'
    control_first=left[:start]+left[middle:end]+left[start:middle]+left[end:]
    valid('control-before-actl.png',control_first,2,2,[[RED]*4,[GREEN]*4],'legal fcTL sequence0 before acTL; both before IDAT',True)
    valid('same-first-right.png',apng(2,2,[first,frame(2,2,[BLUE]*4)]),2,2,[[RED]*4,[BLUE]*4],'same red first frame; blue later',True)
    valid('default-excluded.png',apng(2,2,[frame(2,2,[BLUE]*4),frame(1,1,[GREEN],1,1)],default=[WHITE]*4),2,2,[[BLUE]*4,[BLUE,BLUE,BLUE,GREEN]],'white default PNG is not an animation frame',False)
    valid('offset-disposals.png',apng(3,2,[frame(3,2,[BLUE]*6),frame(1,1,[RED],1,0,dispose=1),frame(1,1,[GREEN],2,1,dispose=2),frame(1,1,[WHITE],0,0)]),3,2,
          [[BLUE]*6,[BLUE,RED,BLUE,BLUE,BLUE,BLUE],[BLUE,CLEAR,BLUE,BLUE,BLUE,GREEN],[WHITE,CLEAR,BLUE,BLUE,BLUE,BLUE]],'offsets; NONE then BACKGROUND then PREVIOUS',True)
    valid('source-over.png',apng(2,2,[frame(2,2,[BLUE]*4),frame(1,1,[HALF_RED],blend=1),frame(1,1,[HIDDEN],1,1,blend=0)]),2,2,
          [[BLUE]*4,[OVER_RED_BLUE,BLUE,BLUE,BLUE],[OVER_RED_BLUE,BLUE,BLUE,HIDDEN]],'alpha128 red OVER blue; transparent SOURCE replaces alpha and hidden RGB',True)
    valid('first-previous.png',apng(2,2,[frame(2,2,[RED]*4,dispose=2),frame(1,1,[BLUE],1,1,blend=1)]),2,2,
          [[RED]*4,[CLEAR,CLEAR,CLEAR,BLUE]],'first-frame PREVIOUS treated as BACKGROUND',True)
    valid('hidden-source-first.png',apng(2,2,[frame(2,2,[HIDDEN]*4),frame(1,1,[WHITE])]),2,2,
          [[HIDDEN]*4,[WHITE,HIDDEN,HIDDEN,HIDDEN]],'first SOURCE retains encoded unassociated alpha0 RGB',True)
    valid('static-red.png',static(2,2,[RED]*4),2,2,[[RED]*4],'single-frame repeat-last comparison',None)
    def invalid(name,data,reason):definitions[name]=(data,{'valid':False,'expectedExit':2,'expectedNoSuccessJson':True,'failure':reason})
    invalid('broken-sequence.png',mutate(left,b'fdAT',lambda b:struct.pack('>I',7)+b[4:]),'fcTL/fdAT sequence gap')
    bad=bytearray(left);pos=left.find(b'fdAT');length=struct.unpack_from('>I',left,pos-4)[0];bad[pos+4+length]^=1
    invalid('broken-crc.png',bytes(bad),'fdAT CRC mismatch')
    invalid('broken-rect.png',mutate(left,b'fcTL',lambda b:b[:12]+struct.pack('>I',2)+b[16:],1),'x_offset + frame width exceeds canvas')
    invalid('broken-count.png',mutate(left,b'acTL',lambda b:struct.pack('>I',3)+b[4:]),'declared count differs from fcTL count')
    invalid('truncated-fdat.png',left[:pos+6],'truncated animation chunk; no fallback to static PNG')
    invalid('oversized-canvas.png',mutate(left,b'IHDR',lambda b:struct.pack('>II',16000001,1)+b[8:]),'1600万画素canvas bound before image allocation')
    invalid('oversized-frame-count.png',mutate(left,b'acTL',lambda b:struct.pack('>I',1025)+b[4:]),'1024 animation frame bound before canvas/frame allocation')
    return definitions
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);args=parser.parse_args();out=args.output.absolute()
    for path in [out,*out.parents]:
        if path.is_symlink() or path.exists() and getattr(path.lstat(),'st_file_attributes',0) & getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',0):raise RuntimeError('link/reparse output rejected: '+str(path))
    if out.exists() and any(out.iterdir()):raise RuntimeError('fresh empty output required; no overwrite')
    out.mkdir(parents=True,exist_ok=True);definitions=assets();manifest={'format':1,'license':'CC0-1.0','pixelFormat':'BGRA8888 Unpremul tightly packed','source':'https://www.w3.org/TR/2025/REC-png-3-20250624/','expectationMethod':'literal hand-written full canvases, no decoder/compositor expectation generation','assets':{}}
    for name,(data,entry) in definitions.items():
        (out/name).write_bytes(data);entry['fileSha256']=sha(data);entry['fileBytes']=len(data);manifest['assets'][name]=entry
    manifest['generatorSha256']=sha(Path(__file__).read_bytes());(out/'expectations.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    if len(definitions)>20 or sum(p.stat().st_size for p in out.iterdir())>200000:raise RuntimeError('fixture budget exceeded')
    print(json.dumps({'images':len(definitions),'valid':sum(e[1]['valid'] for e in definitions.values()),'bytes':sum(p.stat().st_size for p in out.iterdir()),'expectationsSha256':sha((out/'expectations.json').read_bytes())}))
if __name__=='__main__':main()
