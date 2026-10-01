"""CC0: 公式DLLの観測bytesを要約。copy/変換の期待画素は計算しない。"""
import argparse,base64,hashlib,json,stat,struct,zlib
from pathlib import Path
def sha(b):return hashlib.sha256(b).hexdigest().upper()
def pixel(w,h,b):
    assert len(b)==w*h*4
    return dict(width=w,height=h,bgraBase64=base64.b64encode(b).decode(),sha256=sha(b))
def png_decode(data):
    assert data[:8]==b'\x89PNG\r\n\x1a\n';pos=8;compressed=b''
    while pos<len(data):
        size=struct.unpack_from('>I',data,pos)[0];kind=data[pos+4:pos+8];body=data[pos+8:pos+8+size]
        assert zlib.crc32(kind+body)==struct.unpack_from('>I',data,pos+8+size)[0]
        if kind==b'IHDR':w,h,depth,color,_,_,interlace=struct.unpack('>IIBBBBB',body);assert depth==8 and color in (2,6) and interlace==0
        if kind==b'IDAT':compressed+=body
        pos+=size+12
    samples=4 if color==6 else 3;stride=w*samples;raw=zlib.decompress(compressed);previous=bytearray(stride);rgba=bytearray()
    assert len(raw)==h*(stride+1)
    for y in range(h):
        mode=raw[y*(stride+1)];row=bytearray(raw[y*(stride+1)+1:(y+1)*(stride+1)])
        for x in range(stride):
            a=row[x-samples] if x>=samples else 0;b=previous[x];c=previous[x-samples] if x>=samples else 0
            p=a+b-c;dist=[abs(p-a),abs(p-b),abs(p-c)];paeth=(a,b,c)[dist.index(min(dist))]
            predictor=(0,a,b,(a+b)//2,paeth)[mode];row[x]=(row[x]+predictor)&255
        for x in range(0,stride,samples):
            r,g,b=row[x:x+3];rgba+=bytes([b,g,r,row[x+3] if samples==4 else 255])
        previous=row
    return w,h,bytes(rgba)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--reference',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    root=args.reference;out=args.output.absolute()
    for p in [out,*out.parents]:
        if p.is_symlink() or p.exists() and getattr(p.lstat(),'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',0):raise RuntimeError('link output rejected')
    if out.exists() and any(out.iterdir()):raise RuntimeError('fresh output required')
    out.mkdir(parents=True,exist_ok=True);observed=json.loads((root/'run2/observations.json').read_bytes());copies=json.loads((root/'copy-run1/observations.json').read_bytes())
    inputpng=(root/'run2/case-00/pane0.png').read_bytes();w,h,raw=png_decode(inputpng);assert (w,h)==(3,2)
    (out/'input.png').write_bytes(inputpng);cases=[]
    def append(kind,index,folder,states,readonly):
        lines=(folder/'actions.txt').read_text().splitlines();header=lines[0].split();n=int(header[0]);actions=[]
        for line in lines[1:]:
            if not line.strip():continue
            action,src,dst,value=line.split();item=dict(kind=action)
            if action in ('rotate','flipx','flipy','save','all'):item['dst']=int(dst)
            if action in ('rotate','flipx','flipy'):item['index']=int(value)
            if action=='all':item['src']=int(src)
            actions.append(item)
        expected=[]
        for state in states:
            frames=[];orientations=[];panes=[]
            for pane in state['panes']:
                frames.append(pixel(pane['width'],pane['height'],(folder/pane['rawFile']).read_bytes()))
                orientations.append(dict(rotation=pane['angle'],flipHorizontal=pane['flipx'],flipVertical=pane['flipy']))
                panes.append(dict(modified=pane['modified'],savepoint=pane['savepoint']))
            expected.append(dict(actionResult=state['actionResult'],frames=frames,orientations=orientations,differenceCount=state['differenceCount'],conflictCount=state['conflictCount'],undoable=state['undoable'],redoable=state['redoable'],panes=panes))
        saved=[]
        for pane in range(n):
            png=(folder/f'final-pane{pane}.png').read_bytes();pw,ph,pixels=png_decode(png)
            saved.append(dict(**pixel(pw,ph,pixels),pngBase64=base64.b64encode(png).decode(),pngSha256=sha(png)))
        assert len(expected)==len(actions)+1
        cases.append(dict(name=f'{kind}-{index:03d}',kind=kind,paneCount=n,blockSize=1,threshold=0,readOnly=[readonly]*n,actions=actions,states=expected,finalRaw=saved))
    for index in range(32):
        records=[item for item in observed if item['case']==index];append('display',index,root/'run2'/f'case-{index:02d}',[r['state'] for r in records],records[0]['readOnly'])
    for item in copies:append('copy',item['case'],root/'copy-run1'/f"case-{item['case']:03d}",item['states'],False)
    repo=Path(__file__).resolve().parents[3];helper=repo/'build/WinIMergeDllReference'
    provenance={str(p.relative_to(root)):sha(p.read_bytes()) for p in [root/'run2/observations.json',root/'copy-run1/observations.json',root/'run2/capture-source.py',root/'copy-run1/capture-source.py',root/'run2/transform-probe.cpp',root/'run2/compiler.json',root/'run2/transform-probe.exe']}
    header=(helper/'WinIMergeLib.h').read_bytes();assert hashlib.sha1(b'blob '+str(len(header)).encode()+b'\0'+header).hexdigest()=='d5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa'
    assert json.loads((root/'run2/summary.json').read_bytes())['dllSha256']==json.loads((root/'copy-run1/summary.json').read_bytes())['dllSha256']=='36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6'
    provenance['publicHeaderSha256']=sha(header);provenance['GPLLicenseSha256']=sha((repo/'tests/Fixtures/ImageRegions/LICENSE.txt').read_bytes())
    golden=dict(format=1,sourceRevision='da639cdfaeca87aaad0eaceec509afa11ad61421',dllSha256='36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6',headerBlob='d5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa',sources=provenance,extractorSha256=sha(Path(__file__).read_bytes()),input=dict(**pixel(w,h,raw),pngSha256=sha(inputpng)),cases=cases)
    data=(json.dumps(golden,separators=(',',':'),ensure_ascii=False)+'\n').encode('utf8');(out/'winimerge-transforms-golden.json').write_bytes(data)
    print(json.dumps(dict(cases=len(cases),states=sum(len(c['states']) for c in cases),bytes=len(data),sha256=sha(data))))
if __name__=='__main__':main()
