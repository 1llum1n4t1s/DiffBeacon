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
def source_pins():
    provenance=json.loads((ROOT/'provenance.json').read_text(encoding='utf-8-sig')); pins=provenance['sources']
    expected={'Src/DiffBeacon.App/HeadlessIndependentTextInputSelectionRouteChecks.cs',
              'tests/Fixtures/IndependentTextInputSelectionRoutes/generate.py',
              'tests/Fixtures/IndependentTextInputSelectionRoutes/verify.py',
              'tests/Fixtures/IndependentTextInputSelectionRoutes/LICENSE'}
    require(len(pins)==len(expected) and {p['path'] for p in pins}==expected, 'exact provenance source pin set')
    for pin in pins:
        raw=workspace_read(ROOT.parents[2],pin['path'])
        require(len(raw)==pin['size'] and sha(raw)==pin['sha256'].lower(), 'provenance all source size/SHA')
def project(data, reader, expected, archive):
    obj = json.loads(data); require(obj['formatVersion'] == 6, 'workspace v6')
    entries = [p for p in obj['entries'] if p.get('textInputs',{}).get('semantics') == 'Independent']
    require(len(entries) == 1, 'one independent workspace entry'); p = entries[0]
    for side, role in enumerate(ROLES):
        require(p['textInputs'][role]['kind'] == ('Archive' if archive else 'Physical'), 'saved descriptor kind')
        require(p[('leftReadOnly','baseReadOnly','rightReadOnly')[side]] is archive, 'explicit original leaf readonly flag')
        if archive:
            inp = p[('leftArchiveInput','baseArchiveInput','rightArchiveInput')[side]]
            require(inp['inheritedReadOnly'] is False, 'explicit false retained')
            require(inp['entryChain'] == [] and inp['leafEntry'] == 'docs/leaf.txt', 'explicit flat route')
            require(reader(inp['rootPath']) == (ROOT/'inputs'/(role+'.zip')).read_bytes(), 'root bytes retained')
            docs = inp['workingTexts']; require(len(docs)==1, 'one working asset')
            raw = reader(docs[0]['snapshotPath']); require(raw == encoded(expected[side],side), 'literal working bytes')
            require(sha(raw) == docs[0]['sha256'].lower(), 'working SHA')
            require(docs[0]['encodingName']==('utf-8','utf-16','utf-8')[side], 'literal working encoding metadata')
            require(docs[0]['hasBom'] is (side!=2), 'literal working BOM metadata')
            relative_name(docs[0]['snapshotPath'])
        else:
            require(reader(p[('leftPath','basePath','rightPath')[side]]) == encoded(expected[side], side), 'literal physical saved bytes')
    return p
def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--gui-report',type=pathlib.Path); parser.add_argument('--output',type=pathlib.Path); parser.add_argument('--full-ui',action='store_true'); args=parser.parse_args()
    source_pins()
    manifest=json.loads((ROOT/'manifest.json').read_text(encoding='utf-8'))
    for name,pin in manifest['inputs'].items():
        raw=(ROOT/name).read_bytes(); require(len(raw)==pin['size'] and sha(raw)==pin['sha256'],'immutable input pin '+name)
    originals(ROOT/'inputs'); result={'status':'fixture-only','assertions':count}
    if args.gui_report:
        report=json.loads(args.gui_report.read_text(encoding='utf-8')); run=pathlib.Path(report['fixtures'])/'independent-text-input-selection-routes'
        require(report.get('scope') == ('all' if args.full_ui else 'independent-text-input-routes-only'), 'exact declared execution scope')
        require(all(a.get('passed',False) for a in report['assertions'] if a.get('name','').startswith('Input322 ')), 'product Input322 assertion outcomes')
        obs=json.loads((run/'observations.json').read_text(encoding='utf-8')); cases=obs['cases']
        required={'working-external','mixed'}|{'copy-%d-%d'%(a,b) for a in range(3) for b in range(3) if a!=b}
        require({c['id'] for c in cases}==required and len(cases)==8,'exact route case set'); pictures=[]
        for case in cases:
            work=run/case['id']; originals(work/'inputs')
            for name,pin in manifest['inputs'].items(): require(sha((work/name).read_bytes())==pin['sha256'],'unchanged source SHA')
            expected=list(EDITS); destination=case['destination']; source=case['source']
            if source is not None: expected[destination]=EDITS[source]
            require(case['texts']==expected,'all three literal bodies'); actions=case['actions']
            require('entry' in actions and 'compare' in actions,'real entry and dialog Compare')
            require('route-compare' in actions,'real pane compare task')
            for side in range(3):
                if source is not None and side!=destination: continue
                require((work/(ROLES[side]+'-external.txt')).read_bytes()==encoded(expected[side],side),'external exact bytes BOM/EOL')
                require(('左を外部保存','中央を外部保存','右を外部保存')[side] in actions,'actual external save action')
            if source is not None: require(any(a=='copy:%d:%d'%(source,destination) for a in actions),'real copy action')
            else:
                require(case['kinds']==['Physical']*3,'external route adoption')
                project((work/'external.json').read_bytes(),lambda name:workspace_read(work,name),expected,False)
                packed=unzip((work/'external.zip').read_bytes()); require(not any(n.endswith('.diff') for n in packed),'no packaged patch')
                p=project(packed['project.json'],lambda name:package_read(packed,name),expected,False)
                require(set(packed)=={'project.json',p['leftPath'],p['basePath'],p['rightPath']},'all external ZIP entries expected')
                require('project-save' in actions and 'project-open' in actions and 'package-open' in actions and 'package-save' in actions,'actual project/package/reload actions')
                for guard in ('input','filter','workspace','asset','link'):
                    for side in range(3):
                        require('guard:%s:%d'%(guard,side) in actions,'complete all-side guard actions')
                        if guard=='workspace': continue
                        name=work/('guard-%s-%d.txt'%(guard,side)); target=name.with_name(name.name+'.target') if guard=='link' else name
                        require(target.read_bytes()==b'input322 keep\r\n','guard sentinel bytes')
                        if guard=='link': require(name.is_symlink(),'guard actual link')
                if case['id']=='working-external':
                    require(all(a in actions for a in ('左を保存','中央の作業版を保存','右を保存')),'all real working saves')
                    project((work/'working.json').read_bytes(),lambda name:workspace_read(work,name),expected,True)
                    packed=unzip((work/'working.zip').read_bytes()); p=project(packed['project.json'],lambda name:package_read(packed,name),expected,True)
                    names={'project.json'}
                    for key in ('leftArchiveInput','baseArchiveInput','rightArchiveInput'):
                        names.add(p[key]['rootPath']); names.update(d['snapshotPath'] for d in p[key]['workingTexts'])
                    require(set(packed)==names,'all working ZIP entries expected')
            pictures.append(png(work/'result.png'))
        result={'status':'managed-headless-route-subset','cases':8,'assertions':count,'pngs':pictures}
    if args.output:
        with args.output.open('x',encoding='utf-8') as file: json.dump(result,file,indent=2)
    print(json.dumps(result))
if __name__=='__main__': main()
