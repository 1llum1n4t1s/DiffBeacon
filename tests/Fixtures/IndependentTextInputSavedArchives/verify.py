"""Independent stdlib oracle: literals, full ZIP contents, routes and PNG scanlines."""
import argparse, binascii, hashlib, io, json, pathlib, struct, zlib, zipfile
ROOT = pathlib.Path(__file__).resolve().parent
ROLES = ('left', 'middle', 'right')
TEXTS = ('shared café\r\nLEFT €\nend-left\r', 'shared café\r\nMIDDLE £\nend-middle\r', 'shared café\r\nRIGHT “quote”\nend-right\r')
EDITS = tuple(t.replace('end-', 'edited-') for t in TEXTS)
CODECS = ('utf-8', 'utf-16-le', 'utf-8'); BOMS = (b'\xef\xbb\xbf', b'\xff\xfe', b'')
count = 0
def require(ok, name):
    global count
    count += 1
    if not ok: raise AssertionError(name)
def encoded(text, side): return BOMS[side] + text.encode(CODECS[side])
def sha(data): return hashlib.sha256(data).hexdigest()
def unzip(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist(); require(len(names) == len(set(names)), 'ZIP duplicate names')
        require(archive.testzip() is None, 'ZIP all entry CRC')
        return {info.filename: archive.read(info) for info in archive.infolist()}
def originals(folder):
    for side, role in enumerate(ROLES):
        expected = encoded(TEXTS[side], side)
        require((folder / (role + '.txt')).read_bytes() == expected, 'literal physical ' + role)
        entries = unzip((folder / (role + '.zip')).read_bytes())
        require(entries == {'docs/leaf.txt': expected, 'docs/other.bin': bytes([side, 0, 255, 13, 10])}, 'all ZIP literal entries ' + role)
def png(path):
    data = path.read_bytes(); require(data[:8] == b'\x89PNG\r\n\x1a\n', 'PNG signature')
    offset = 8; compressed = bytearray(); header = None; ended = False
    while offset < len(data):
        n = struct.unpack_from('>I', data, offset)[0]; kind = data[offset+4:offset+8]; chunk = data[offset+8:offset+8+n]
        require(len(chunk) == n and offset+12+n <= len(data), 'PNG complete chunk')
        require(binascii.crc32(kind+chunk) & 0xffffffff == struct.unpack_from('>I', data, offset+8+n)[0], 'PNG chunk CRC')
        if kind == b'IHDR': header = struct.unpack('>IIBBBBB', chunk)
        if kind == b'IDAT': compressed.extend(chunk)
        offset += n+12
        if kind == b'IEND': ended = True; break
    require(ended and offset == len(data) and header is not None, 'PNG full stream')
    w,h,depth,color,method,filter_method,interlace = header
    require(w > 0 and h > 0 and depth == 8 and color in (2,6) and method == filter_method == interlace == 0, 'PNG supported scanlines')
    bpp = 4 if color == 6 else 3; stride = w*bpp; raw = zlib.decompress(compressed)
    require(len(raw) == h*(stride+1), 'PNG all scanline bytes'); previous = bytearray(stride); pixel_hash = hashlib.sha256()
    for y in range(h):
        start = y*(stride+1); kind = raw[start]; row = bytearray(raw[start+1:start+1+stride]); require(kind <= 4, 'PNG filter')
        for x in range(stride):
            a = row[x-bpp] if x >= bpp else 0; b = previous[x]; c = previous[x-bpp] if x >= bpp else 0
            p = a+b-c; distances = (abs(p-a), abs(p-b), abs(p-c)); paeth = (a,b,c)[distances.index(min(distances))]
            prediction = (0,a,b,(a+b)//2,paeth)[kind]; row[x] = (row[x]+prediction)&255
        pixel_hash.update(row); previous = row
    return {'width':w,'height':h,'pixelSha256':pixel_hash.hexdigest()}
def relative_name(name):
    require(isinstance(name,str) and bool(name) and '\x00' not in name, 'nonempty relative name')
    portable=name.replace('\\','/')
    require(not pathlib.PureWindowsPath(name).is_absolute() and not pathlib.PureWindowsPath(name).drive
            and not pathlib.PurePosixPath(portable).is_absolute() and ':' not in portable,
            'reject foreign absolute/drive/UNC name')
    require(all(part not in ('','..') for part in portable.split('/')), 'reject relative path escape')
    return portable
def workspace_read(root,name):
    root=root.resolve(strict=True); native=pathlib.Path(name)
    if native.is_absolute():
        require('..' not in native.parts, 'reject absolute path traversal')
        candidate=native; require(candidate.is_relative_to(root), 'native absolute reference stays within runtime directory')
    else:
        portable=relative_name(name); candidate=root/pathlib.PurePosixPath(portable)
    current=root
    for part in candidate.relative_to(root).parts:
        current=current/part; require(not current.is_symlink(), 'reject relative reference symlink')
    resolved=candidate.resolve(strict=True); require(resolved.is_relative_to(root), 'reference stays within runtime directory')
    return resolved.read_bytes()
def package_read(entries,name):
    portable=relative_name(name); require(name==portable and portable in entries, 'portable package reference')
    return entries[portable]

def nested_original(raw,shape,side):
    entries=unzip(raw)
    if shape=='nested':
        require(set(entries)=={'inner.zip','outer.bin'},'all outer entries')
        require(entries['outer.bin']==bytes([side,44,0]),'outer literal bytes')
        entries=unzip(entries['inner.zip'])
        require(set(entries)=={'deep.zip','inner.bin'},'all inner entries')
        require(entries['inner.bin']==bytes([side,22,0]),'inner literal bytes')
        entries=unzip(entries['deep.zip'])
    require(entries=={'docs/leaf.txt':encoded(TEXTS[side],side),'docs/other.bin':bytes([side,0,255,13,10])},'all literal leaf entries')
def source_pins():
    provenance=json.loads((ROOT/'provenance.json').read_text(encoding='utf-8-sig')); pins=provenance['sources']
    names={'Src/DiffBeacon.App/HeadlessIndependentTextInputSavedArchiveChecks.cs',
           'tests/Fixtures/IndependentTextInputSavedArchives/generate.py',
           'tests/Fixtures/IndependentTextInputSavedArchives/verify.py',
           'tests/Fixtures/IndependentTextInputSavedArchives/LICENSE'}
    require(len(pins)==4 and {p['path'] for p in pins}==names,'exact nested provenance pin set')
    for pin in pins:
        raw=workspace_read(ROOT.parents[2],pin['path']); require(len(raw)==pin['size'] and sha(raw)==pin['sha256'].lower(),'all nested source size/SHA')
def project(data,read,shape):
    obj=json.loads(data); require(obj['formatVersion']==6,'saved Archive workspace v6')
    entries=[p for p in obj['entries'] if p.get('textInputs',{}).get('semantics')=='Independent']
    require(len(entries)==1,'one independent saved project'); p=entries[0]
    for side,role in enumerate(ROLES):
        require(p['textInputs'][role]['kind']=='Archive','restored Archive kind')
        require(p[('leftReadOnly','baseReadOnly','rightReadOnly')[side]] is True,'original leaf readonly true')
        inp=p[('leftArchiveInput','baseArchiveInput','rightArchiveInput')[side]]
        require(inp['inheritedReadOnly'] is False,'editable inherited false')
        require(inp['entryChain']==(['inner.zip','deep.zip'] if shape=='nested' else []) and inp['leafEntry']=='docs/leaf.txt','exact nested leaf route')
        original=(ROOT/'inputs'/shape/(role+'.zip')).read_bytes()
        require(read(inp['rootPath'])==original and inp['rootSha256'].lower()==sha(original),'original nested root bytes/SHA')
        docs=inp['workingTexts']; require(len(docs)==1,'one saved working document')
        doc=docs[0]; require(doc['entryChain']==inp['entryChain'] and doc['leafEntry']==inp['leafEntry'],'working route matches explicit leaf')
        relative_name(doc['snapshotPath']); raw=read(doc['snapshotPath'])
        require(raw==encoded(EDITS[side],side) and doc['sha256'].lower()==sha(raw),'literal working bytes/SHA')
        require(doc['encodingName']==('utf-8','utf-16','utf-8')[side] and doc['hasBom'] is (side!=2),'literal saved encoding/BOM metadata')
    return p
def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--gui-report',type=pathlib.Path); parser.add_argument('--output',type=pathlib.Path); parser.add_argument('--fixture-only',action='store_true'); parser.add_argument('--full-ui',action='store_true'); args=parser.parse_args()
    # draft artifact validation permits fixture-only; accepted repository/product execution verifies all source pins.
    if not args.fixture_only: source_pins()
    manifest=json.loads((ROOT/'manifest.json').read_text(encoding='utf-8'))
    expected_names={'inputs/%s/%s.zip'%(shape,role) for shape in ('flat','nested') for role in ROLES}
    require(set(manifest['inputs'])==expected_names,'all six original fixture pins')
    for name,pin in manifest['inputs'].items():
        raw=workspace_read(ROOT,name); require(len(raw)==pin['size'] and sha(raw)==pin['sha256'],'immutable fixture bytes/SHA')
        parts=pathlib.PurePosixPath(name).parts; nested_original(raw,parts[1],ROLES.index(pathlib.Path(parts[2]).stem))
    pictures=[]
    if args.gui_report:
        require(not args.fixture_only,'product read cannot bypass source pins')
        report=json.loads(args.gui_report.read_text(encoding='utf-8')); require(report['scope']==('all' if args.full_ui else 'independent-text-input-saved-archives-only'),'exact declared execution scope')
        assertions=[a for a in report['assertions'] if a['name'].startswith('Saved Archive ')]
        require(len(assertions)>0 and all(a['passed'] is True for a in assertions),'actual original Button Task assertions')
        run=pathlib.Path(report['fixtures'])/'independent-text-input-saved-archives'
        observations=json.loads((run/'observations.json').read_text(encoding='utf-8'))
        require(observations['schema']=='input348-saved-archive-v1', 'exact observation schema')
        cases=observations['cases']
        require(len(cases)==2 and {c['shape'] for c in cases}=={'flat','nested'},'exact new flat/nested case set')
        for case in cases:
            shape=case['shape']; work=run/shape
            for side,role in enumerate(ROLES):
                original=(ROOT/'inputs'/shape/(role+'.zip')).read_bytes(); require((work/'inputs'/(role+'.zip')).read_bytes()==original,'original container preserved'); nested_original(original,shape,side)
            project((work/'working.json').read_bytes(),lambda name:workspace_read(work,name),shape)
            entries=unzip((work/'working.zip').read_bytes()); p=project(entries['project.json'],lambda name:package_read(entries,name),shape)
            names={'project.json'}
            for key in ('leftArchiveInput','baseArchiveInput','rightArchiveInput'):
                names.add(p[key]['rootPath']); names.update(d['snapshotPath'] for d in p[key]['workingTexts'])
            require(set(entries)==names,'all package entries no patch/extra')
            expanded=work/'expanded'; actual={str(p.relative_to(expanded)).replace('\\','/') for p in expanded.rglob('*') if p.is_file()}
            require(actual==set(entries),'all product-extracted files')
            for name,raw in entries.items(): require(workspace_read(expanded,name)==raw,'product extraction exact bytes/SHA')
            project((expanded/'project.json').read_bytes(),lambda name:workspace_read(expanded,name),shape)
            actions=case['actions']
            for name in ('entry','compare','左を保存','中央の作業版を保存','右を保存','project-save','package-open','package-save','path-compare','左をすべて展開'): require(name in actions,'real Button action '+name)
            require(actions.count('project-open')==2,'both actual project reloads')
            for side in range(3):
                require(actions.count(str(side)+':pick')==2 and sum(a['name']=='Saved Archive '+shape+' task completed '+str(side)+':pick' for a in assertions)==2, 'both original same-kind picker Tasks')
            if shape=='nested':
                for side in range(3): require(actions.count(str(side)+':open')==2,'two actual nested opens per side')
            snapshots=case['snapshots']; require([s['stage'] for s in snapshots]==['workspace-reload','package-reload'],'both actual restored snapshots')
            for snapshot in snapshots:
                require(snapshot['texts']==list(EDITS),'restored literal bodies')
                require(snapshot['encodings']==['utf-8','utf-16','utf-8'] and snapshot['hasBom']==[True,True,False],'restored encoding/BOM')
                require(snapshot['dirty']==[False]*3 and snapshot['editorReadOnly']==[False]*3 and snapshot['leafReadOnly']==[True]*3,'restored permissions/savepoints')
                pictures.append(png(work/(snapshot['stage']+'.png')))
        containers=observations['containerCases']
        require(len(containers)==2 and [c['depth'] for c in containers]==[1,2], 'exact separate container cases')
        for case in containers:
            depth=case['depth']; work=run/('container-'+str(depth)); chain=['inner.zip'] if depth==1 else ['inner.zip','deep.zip']
            require(case['parentMode']=='Archive' and case['initialLeafNull'] is True and case['initialChain']==chain, 'canonical parent and no implicit leaf')
            parent=json.loads((work/'parent.json').read_bytes()); adopted=json.loads((work/'adopted.json').read_bytes())
            require(parent['mode']=='Archive' and parent.get('baseArchiveInput') is None and parent['basePath']=='', 'valid two-side Archive parent')
            require(adopted['mode']=='Text' and adopted['textInputs']['semantics']=='Independent', 'adopted independent Text')
            actions=case['actions']; require(actions.count('parent-compare')==actions.count('entry')==actions.count('compare')==actions.count('initial-compare')==1, 'original parent and public entry/Compare actions')
            for side,role in enumerate(ROLES):
                original=workspace_read(work,'inputs/'+role+'.zip'); require(original==(ROOT/'inputs'/'nested'/(role+'.zip')).read_bytes(), 'container regression original root preserved'); nested_original(original,'nested',side)
                key=('leftArchiveInput','baseArchiveInput','rightArchiveInput')[side]; readonly=('leftReadOnly','baseReadOnly','rightReadOnly')[side]
                if side!=1:
                    inp=parent[key]; require(inp['entryChain']==chain and inp['leafEntry'] is None and inp['inheritedReadOnly'] is False and parent[readonly] is True, 'real parent route and readonly')
                    require(workspace_read(work,inp['rootPath'])==original and inp['rootSha256'].lower()==sha(original), 'parent root SHA and known absolute path')
                request=json.loads((work/(str(side)+'-load-request-1.json')).read_bytes()); rows=json.loads((work/(str(side)+'-load-rows.json')).read_bytes())['rows']
                expected_chain=[] if side==1 else chain
                expected_rows=['inner.zip','outer.bin'] if side==1 else ['deep.zip','inner.bin'] if depth==1 else ['docs/leaf.txt','docs/other.bin']
                require(request['entryChain']==expected_chain and sorted(row.split(' （')[0] for row in rows)==expected_rows, 'original initial request chain and all literal listing entries')
                require(workspace_read(work,request['rootPath'])==original, 'initial listing original root')
                if side!=1:
                    same=json.loads((work/(str(side)+'-load-request-2.json')).read_bytes()); require(same['entryChain']==chain and same['rootPath']==request['rootPath'], 'same-root picker retains exact container chain')
                    require(any(a['name']=='Saved Archive container-'+str(depth)+' same root retains container route '+str(side) for a in assertions), 'actual same-root route observation')
                if side==0:
                    changed=json.loads((work/'0-load-request-3.json').read_bytes()); restored=json.loads((work/'0-load-request-4.json').read_bytes())
                    require(changed['entryChain']==restored['entryChain']==[] and changed['rootPath']!=restored['rootPath'] and restored['rootPath']==request['rootPath'], 'changed-root picker clears inherited route')
                    alias=workspace_read(work,changed['rootPath']); require(alias==original, 'changed root literal bytes'); nested_original(alias,'nested',side)
                    for name in ('changed root clears selection','changed root clears container route','restored root starts at root'):
                        require(any(a['name']=='Saved Archive container-'+str(depth)+' '+name for a in assertions), 'actual changed-root rejection/reset boundary')
                inp=adopted[key]; require(inp['entryChain']==['inner.zip','deep.zip'] and inp['leafEntry']=='docs/leaf.txt' and inp['inheritedReadOnly'] is False and adopted[readonly] is True, 'explicit leaf route and permissions')
                require(workspace_read(work,inp['rootPath'])==original and inp['rootSha256'].lower()==sha(original), 'adopted original root SHA')
                require(actions.count(str(side)+':pick')==(4 if side==0 else 2) and actions.count(str(side)+':load')==(4 if side==0 else 2 if side==2 else 1) and actions.count(str(side)+':row:docs/leaf.txt')==1, 'repeated same-kind pick and original Load/leaf controls')
                opens=2 if side in (0,1) else 1 if depth==1 else 0
                require(actions.count(str(side)+':open')==opens, 'exact original nested Open Tasks')
                require(sum(a['name']=='Saved Archive container-'+str(depth)+' task completed '+str(side)+':pick' for a in assertions)==(4 if side==0 else 2), 'all original picker Tasks')
                require(sum(a['name']=='Saved Archive container-'+str(depth)+' task completed '+str(side)+':open' for a in assertions)==opens, 'all original container Open Tasks')
                require(sum(a['name']=='Saved Archive container-'+str(depth)+' task completed '+str(side)+':load' for a in assertions)==(4 if side==0 else 2 if side==2 else 1), 'all original listing Load Tasks')
                for name in ('verified container no leaf '+str(side), 'initial container no leaf '+str(side), 'initial kind and readonly '+str(side), 'literal initial listing '+str(side)):
                    require(sum(a['name']=='Saved Archive container-'+str(depth)+' '+name and a['passed'] is True for a in assertions)==1, 'container original Task and initial observation '+name)
            for name in ('task completed parent-compare','parent container routes','public dialog original task','no initial leaf submission','original submission and opening completed','new tab adopted','literal bodies and readonly originals','explicit adopted leaf route'):
                require(sum(a['name']=='Saved Archive container-'+str(depth)+' '+name and a['passed'] is True for a in assertions)==1, 'required container adoption observation '+name)
            require(case['texts']==list(TEXTS) and case['leafReadOnly']==[True]*3 and case['editorReadOnly']==[False]*3, 'literal container bodies and readonly original leaves')
            pictures.append(png(work/'adopted.png'))
    result={'status':'draft-fixture-only' if args.fixture_only else 'new-saved-archives-independent-criteria','cases':4 if args.gui_report else 0,'assertions':count,'pngs':pictures}
    if args.output:
        with args.output.open('x',encoding='utf-8') as file: json.dump(result,file,indent=2)
    print(json.dumps(result))
if __name__=='__main__': main()
