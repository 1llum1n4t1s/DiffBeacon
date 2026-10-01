"""CC0: 小TIFF/BigTIFFと圧縮代表、独立literal BGRA期待値。decoderは使用しない。"""
import argparse,base64,hashlib,json,stat,struct,zlib
from pathlib import Path
SOURCE='https://image-js.github.io/tiff/media/TIFF6.pdf'
RED=(0,0,255,255); GREEN=(0,255,0,255); BLUE=(255,0,0,255); WHITE=(255,255,255,255)
def sha(b):return hashlib.sha256(b).hexdigest().upper()
def page(w,h,pixels,alpha=0,stored=None):return (w,h,pixels,alpha,stored)
def tiff(pages,be=False):
    endian='>' if be else '<';pack=lambda fmt,*x:struct.pack(endian+fmt,*x)
    data=bytearray((b'MM' if be else b'II')+pack('HI',42,8));locations=[]
    for w,h,pixels,alpha,stored in pages:
        if len(data)%2:data+=b'\0'
        start=len(data);locations.append(start);samples=4 if alpha else 3
        tags=[(256,4,w),(257,4,h),(258,3,None),(259,3,1),(262,3,2),(273,4,None),(274,3,1),(277,3,samples),(278,4,h),(279,4,None),(282,5,None),(283,5,None),(284,3,1),(296,3,2)]
        if alpha:tags.append((338,3,alpha))
        tags.sort();size=2+12*len(tags)+4;bits=start+size;resolution=bits+samples*2;strip=resolution+16
        rgba=stored if stored is not None else [(r,g,b,a) for b,g,r,a in pixels]
        raw=bytes(v for pixel in rgba for v in pixel[:samples]);data+=pack('H',len(tags))
        for tag,kind,value in tags:
            count=samples if tag==258 else 1
            if tag==258:value=bits
            if tag==273:value=strip
            if tag==279:value=len(raw)
            if tag==282:value=resolution
            if tag==283:value=resolution+8
            data+=pack('HHI',tag,kind,count)+(pack('H',value)+b'\0\0' if kind==3 and count==1 else pack('I',value))
        data+=pack('I',0)+pack('H',8)*samples+pack('IIII',72,1,72,1)+raw
        if len(locations)>1:
            prior=locations[-2];n=struct.unpack_from(endian+'H',data,prior)[0];struct.pack_into(endian+'I',data,prior+2+n*12,start)
    return bytes(data),locations
def change(data,ifd,tag,value,be=False):
    data=bytearray(data);e='>' if be else '<';count=struct.unpack_from(e+'H',data,ifd)[0]
    for i in range(count):
        pos=ifd+2+12*i
        if struct.unpack_from(e+'H',data,pos)[0]==tag:struct.pack_into(e+'I',data,pos+8,value);return bytes(data)
    raise ValueError(tag)
def extended(pages,big=False,be=False):
    # 各pageは(w,h,bits,photometric,samples,compression,raw,extra tags,tile)の独立宣言。
    e='>' if be else '<';pack=lambda fmt,*v:struct.pack(e+fmt,*v)
    inline=8 if big else 4;entry=20 if big else 12;countbytes=8 if big else 2;offsetfmt='Q' if big else 'I'
    data=bytearray((b'MM' if be else b'II')+(pack('HHHQ',43,8,0,16) if big else pack('HI',42,8)));previous=None;locations=[]
    for w,h,bits,photo,samples,compression,raw,extra,tile in pages:
        while len(data)%8:data+=b'\0'
        start=len(data);locations.append(start)
        if previous is not None:struct.pack_into(e+offsetfmt,data,previous,start)
        tags={256:(4,pack('I',w)),257:(4,pack('I',h)),258:(3,pack('H'*len(bits),*bits)),259:(3,pack('H',compression)),262:(3,pack('H',photo)),274:(3,pack('H',1)),277:(3,pack('H',samples)),282:(5,pack('II',72,1)),283:(5,pack('II',72,1)),284:(3,pack('H',1)),296:(3,pack('H',2))}
        offsettag=324 if tile else 273;lengthtag=325 if tile else 279
        tags[offsettag]=(4,b'\0'*4);tags[lengthtag]=(4,pack('I',len(raw)))
        if tile:tags[322]=(4,pack('I',tile[0]));tags[323]=(4,pack('I',tile[1]))
        else:tags[278]=(4,pack('I',h))
        tags.update(extra);ordered=sorted(tags.items());directoryend=start+countbytes+entry*len(tags)+inline
        tail=bytearray();entries=[];sizes={3:2,4:4,5:8}
        for tag,(kind,value) in ordered:
            if len(value)<=inline:payload=value.ljust(inline,b'\0')
            else:
                while (directoryend+len(tail))%8:tail+=b'\0'
                payload=pack(offsetfmt,directoryend+len(tail));tail+=value
            entries.append((tag,kind,len(value)//sizes[kind],payload))
        while (directoryend+len(tail))%8:tail+=b'\0'
        pixeloffset=directoryend+len(tail);data+=pack('Q' if big else 'H',len(entries))
        for tag,kind,count,payload in entries:
            if tag==offsettag:payload=pack('I',pixeloffset).ljust(inline,b'\0')
            data+=pack('HHQ' if big else 'HHI',tag,kind,count)+payload
        previous=len(data);data+=pack(offsetfmt,0)+tail+raw
    return bytes(data),locations
def assets():
    result={}
    def valid(name,pages,be=False):
        data,_=tiff(pages,be);frames=[]
        for w,h,pixels,alpha,_ in pages:
            raw=bytes(v for p in pixels for v in p)
            frames.append(dict(width=w,height=h,bgra=[list(p) for p in pixels],bgraBase64=base64.b64encode(raw).decode(),pixelSha256=sha(raw),extraSamples=alpha))
        result[name]=(data,dict(valid=True,byteOrder='MM' if be else 'II',frameCount=len(frames),frames=frames))
    first=page(2,1,[RED,BLUE]);second=page(1,3,[GREEN,WHITE,BLUE])
    valid('two-pages-le.tif',[first,second]);valid('two-pages-be.tiff',[first,second],True)
    valid('same-first-right.tif',[first,page(1,3,[RED,WHITE,BLUE])])
    valid('same-first-middle.tif',[first,page(1,3,[BLUE,WHITE,BLUE])])
    valid('single-page.tif',[first])
    valid('unassociated-alpha.tif',[page(2,1,[(0,0,255,128),(13,37,71,0)],2)])
    # 128/255 redはstored128、straight255。associated alpha0は透明黒。
    valid('associated-alpha.tif',[page(2,1,[(0,0,255,128),(0,0,0,0)],1,[(128,0,0,128),(0,0,0,0)])])
    data,loc=tiff([first,second]);n=struct.unpack_from('<H',data,loc[-1])[0];cycle=bytearray(data);struct.pack_into('<I',cycle,loc[-1]+2+n*12,loc[0])
    def invalid(name,b,reason):result[name]=(b,dict(valid=False,expectedExit=2,expectedNoSuccessJson=True,failure=reason))
    invalid('ifd-cycle.tif',bytes(cycle),'main IFD chain cycle')
    offset=bytearray(data);struct.pack_into('<I',offset,loc[0]+2+struct.unpack_from('<H',data,loc[0])[0]*12,0xfffffff0)
    invalid('ifd-outside.tif',bytes(offset),'next IFD offset outside file')
    invalid('later-strip-truncated.tif',data[:-1],'second-page strip missing final sample')
    invalid('later-strip-outside.tif',change(data,loc[1],273,0xfffffff0),'second-page strip offset outside file')
    invalid('too-many-pages.tif',tiff([page(1,1,[RED])]*1025)[0],'1025 main IFD pages exceeds 1024 bound')
    invalid('later-oversized.tif',change(data,loc[1],256,16000001),'later page exceeds 16M pixels before allocation')
    def additional(name,data,canvases,order='II'):
        frames=[]
        for w,h,pixels in canvases:
            raw=bytes(v for p in pixels for v in p)
            frames.append(dict(width=w,height=h,bgra=[list(p) for p in pixels],bgraBase64=base64.b64encode(raw).decode(),pixelSha256=sha(raw),extraSamples=0))
        result[name]=(data,dict(valid=True,byteOrder=order,frameCount=len(frames),frames=frames))
    rgb=b'\xff\0\0\0\0\xff';base=(2,1,[8,8,8],2,3)
    additional('packbits-rgb.tif',extended([(*base,32773,bytes([5])+rgb,{},None)])[0],[(2,1,[RED,BLUE])])
    additional('deflate-rgb.tif',extended([(*base,8,zlib.compress(rgb),{},None)])[0],[(2,1,[RED,BLUE])])
    # TIFF6 §13: Clear256/EOI257、初期9bitをMSB順。6literalのみでbit幅境界には達しない。
    codes=[256,*rgb,257];lzw=int(''.join(f'{code:09b}' for code in codes),2).to_bytes(9,'big')
    additional('lzw-rgb.tif',extended([(*base,5,lzw,{},None)])[0],[(2,1,[RED,BLUE])])
    palette=[0]*768;palette[0]=65535;palette[512+1]=65535
    additional('palette-8.tif',extended([(2,1,[8],3,1,1,b'\0\1',{320:(3,struct.pack('<768H',*palette))},None)])[0],[(2,1,[RED,BLUE])])
    additional('gray-white-is-zero.tif',extended([(2,1,[1],0,1,1,b'\x40',{},None)])[0],[(2,1,[WHITE,(0,0,0,255)])])
    additional('rgb-16.tif',extended([(1,1,[16,16,16],2,3,1,struct.pack('<3H',65535,0,32896),{},None)])[0],[(1,1,[(128,0,255,255)])])
    tile=rgb+bytes(16*16*3-len(rgb))
    tiled,tileloc=extended([(*base,1,tile,{},(16,16))])
    additional('tile-padding.tif',tiled,[(2,1,[RED,BLUE])])
    for be in (False,True):
        pages=[(*base,1,rgb,{},None),(1,3,[8,8,8],2,3,1,b'\0\xff\0\xff\xff\xff\0\0\xff',{},None)]
        bigdata,bigloc=extended(pages,True,be)
        additional('bigtiff-'+('be' if be else 'le')+'.tif',bigdata,[(2,1,[RED,BLUE]),(1,3,[GREEN,WHITE,BLUE])],'MM' if be else 'II')
        if not be:
            bad=bytearray(bigdata);struct.pack_into('<Q',bad,bigloc[0],2**63)
            invalid('big-entry-count.tif',bytes(bad[:24]),'huge BigTIFF IFD entry count before allocation')
            bad=bytearray(bigdata);struct.pack_into('<Q',bad,bigloc[0]+8+4,2**63)
            invalid('big-tag-count.tif',bytes(bad),'huge BigTIFF tag count before allocation')
    invalid('tile-huge-dimensions.tif',change(tiled,tileloc[0],322,0x7fffffff),'huge tile width before scratch allocation')
    jpeg=b'\xff\xd8\xff\xc0'+struct.pack('>H B H H B',17,8,65535,65535,3)+bytes([1,0x11,0,2,0x11,0,3,0x11,0])+b'\xff\xd9'
    invalid('jpeg-huge-sof.tif',extended([(*base,7,jpeg,{},None)])[0],'JPEG SOF exceeds declared TIFF strip dimensions before native decode')
    invalid('jpeg-interchange-outside.tif',extended([(*base,6,rgb,{513:(4,struct.pack('<I',0xfffffff0)),514:(4,struct.pack('<I',32))},None)])[0],'old JPEGInterchangeFormat offset outside file; reject before library decode')
    invalid('jpeg-interchange-length.tif',extended([(*base,6,rgb,{513:(4,struct.pack('<I',8)),514:(4,struct.pack('<I',0xfffffff0))},None)])[0],'old JPEGInterchangeFormatLength outside file; reject before library decode')
    no_scan=bytes.fromhex('FFD8FFC0000B080001000101011100FFD9')
    invalid('jpeg-without-scan.tif',extended([(1,1,[8],1,1,7,no_scan,{},None)])[0],'JPEG SOI/SOF0/EOI without SOS or scan data must not produce successful zero pixels')
    # T.81 baseline: 全DC/AC係数0でIDCTのlevel shift後128。各Huffman code0をMSB順、pad1。
    segment=lambda marker,body:bytes([255,marker])+struct.pack('>H',len(body)+2)+body
    lengths=bytes([1]+[0]*15)
    dc=bytes([0])+lengths+bytes([0]);ac=bytes([0x10])+lengths+bytes([0])
    grayjpeg=b'\xff\xd8'+segment(0xdb,bytes([0])+bytes([1]*64))+segment(0xc0,bytes.fromhex('080001000101011100'))+segment(0xc4,dc+ac)+segment(0xda,bytes.fromhex('010100003F00'))+b'\x3f\xff\xd9'
    additional('jpeg-gray-128.tif',extended([(1,1,[8],1,1,7,grayjpeg,{},None)])[0],[(1,1,[(128,128,128,255)])])
    # JPEG6 table offset配列を5件宣言、sample1に対して過剰。参照先は実Q/DC/AC table bytes。
    tables={tag:(4,bytes(20)) for tag in (519,520,521)}
    old,locations=extended([(1,1,[8],1,1,6,grayjpeg,tables,None)]);old=bytearray(old);directory=locations[0];values={}
    for i in range(struct.unpack_from('<H',old,directory)[0]):
        at=directory+2+i*12;tag=struct.unpack_from('<H',old,at)[0];values[tag]=struct.unpack_from('<I',old,at+8)[0]
    origin=values[273];huffman=grayjpeg.index(b'\xff\xc4')+4
    for tag,offset in ((519,7),(520,huffman+1),(521,huffman+len(dc)+1)):
        struct.pack_into('<5I',old,values[tag],*([origin+offset]*5))
    invalid('jpeg-old-table-count.tif',bytes(old),'old JPEG table count5 exceeds SamplesPerPixel1 before table allocation')
    return result
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);out=parser.parse_args().output.absolute()
    for p in [out,*out.parents]:
        if p.is_symlink() or p.exists() and getattr(p.lstat(),'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',0):raise RuntimeError('link output rejected')
    if out.exists() and any(out.iterdir()):raise RuntimeError('fresh empty output required')
    out.mkdir(parents=True,exist_ok=True);manifest=dict(format=1,license='CC0-1.0',licenseSha256=sha(Path(__file__).with_name('LICENSE.txt').read_bytes()),source=SOURCE,pixelFormat='BGRA8888 Unpremul tightly packed',expectationMethod='literal hand-written canvases; no decoder',generatorSha256=sha(Path(__file__).read_bytes()),assets={})
    for name,(data,entry) in assets().items():
        (out/name).write_bytes(data);entry.update(fileSha256=sha(data),fileBytes=len(data));manifest['assets'][name]=entry
    (out/'expectations.json').write_bytes((json.dumps(manifest,ensure_ascii=False,indent=2)+'\n').encode('utf8'))
    print(json.dumps(dict(images=len(manifest['assets']),expectationsSha256=sha((out/'expectations.json').read_bytes()),bytes=sum(p.stat().st_size for p in out.iterdir()))))
if __name__=='__main__':main()
