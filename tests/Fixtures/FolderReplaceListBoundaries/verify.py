"""補足native境界の独立reader。製品実行・native再compileは行わない。"""
import sys
sys.dont_write_bytecode = True
import argparse, binascii, hashlib, io, json, pathlib, zipfile
BASE = pathlib.Path(__file__).resolve().parent
COMMIT = '6780924ba35f7f55af0f6f14dd302561175830a6'
PROOF_SHA = '75ea1db305f68b6c6db657d91a365b742b42f7d865e6ee0802da9507a0ed9b0c'
sha = lambda b: hashlib.sha256(b).hexdigest()
def require(ok, message):
    if not ok: raise ValueError(message)
def fixed_files(files, manifest):
    require(manifest['sourceCommit'] == COMMIT, 'fixed source commit')
    require(set(files) == {i['path'] for i in manifest['files']}, 'fixed exact fileset')
    for i in manifest['files']:
        b = files[i['path']]
        require(len(b) == i['size'] and sha(b) == i['sha256'], 'fixed hash: ' + i['path'])
def archive_entries(raw):
    require(sha(raw) == PROOF_SHA, 'fixed native proof SHA')
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        names = z.namelist()
        require(len(names) == len(set(names)) and z.testzip() is None, 'ZIP duplicate/CRC')
        index = json.loads(z.read('entry-index.json'))
        require(len(index) == 270 and set(names) == set(index) | {'entry-index.json'}, 'ZIP indexed exactset')
        entries = {n: z.read(n) for n in index}
        for n, b in entries.items():
            require(z.getinfo(n).CRC == binascii.crc32(b) & 0xffffffff, 'ZIP CRC: ' + n)
        indexed_entries(entries, index)
    return entries, index
def indexed_entries(entries, index):
    require(set(entries) == set(index), 'proof exactset')
    for n, i in index.items():
        b = entries[n]
        require(len(b) == i['size'] and sha(b) == i['sha256'], 'proof indexed hash: ' + n)
def sources(repo, entries):
    base = repo / 'tests/Fixtures/LineTransformReplaceLists'
    smraw = (base / 'source-manifest.json').read_bytes()
    sm = json.loads(smraw)
    require(sm['commit'] == COMMIT and entries['original-source-manifest.json'] == smraw, 'source manifest identity')
    env = json.loads(entries['environment-source-receipt.json'])
    require(env['sourceManifestSha256'] == sha(smraw), 'manifest SHA')
    archive = base.parent / 'LineTransformRegex/reference-source.zip'
    require(sha(archive.read_bytes()) == sm['archiveSha256'] == env['referenceSourceZipSha256'] == env['manifestArchiveSha256'], 'original source ZIP SHA')
    data = {}
    with zipfile.ZipFile(archive) as z:
        require(z.testzip() is None, 'original source CRC')
        for i in sm['sources']:
            p = i['path']
            b = (base / 'additional-original' / p).read_bytes() if i['origin'] == 'additional-original' else z.read('original/' + p)
            require(len(b) == i['size'] and sha(b) == i['sha256'] and hashlib.sha1(b'blob ' + str(len(b)).encode() + b'\0' + b).hexdigest() == i['blob'], 'source blob: ' + p)
            data[p] = b
        require('original/LICENSE.md' in z.namelist() and 'original/Externals/poco/LICENSE' in z.namelist(), 'original license references')
    require(env['adapterCopiesUnchanged'] and env['adapterOriginalSha256'] == sha((base / 'adapter.cpp').read_bytes()), 'adapter origin')
    require(env['captureOriginalSha256'] == sha((base / 'capture.py').read_bytes()), 'capture origin')
    require(env['sdkEnvCmdSha256'] == sha(entries['sdk-env.cmd']) and env['sdkVersion'] == '10.0.26100.0' and env['compilerVersion'] == '14.51.36231', 'SDK environment evidence')
    # ReceiptのD絶対pathは採取履歴。別OS/CIでの実在は要求しない。
    return base, data

def native(entries, casesraw, golden, base, data):
    cases = json.loads(casesraw)
    baselinecases = json.loads((base / 'cases.json').read_bytes())
    baseline = (base / 'golden.ndjson').read_bytes()
    provraw = (base / 'provenance.json').read_bytes(); prov = json.loads(provraw)
    require(prov['commit'] == COMMIT and prov['sourceManifestSha256'] == sha((base / 'source-manifest.json').read_bytes()), 'provenance identity')
    require(len(cases) == 10 and len({c['id'] for c in cases}) == 10, '10 unique supplementary cases')
    require(entries['supplementary-cases.json'] == casesraw, 'original supplementary cases bytes')
    rows = [json.loads(l) for l in golden.splitlines()]
    require(len(rows) == 10 and [c['id'] for c in cases] == [g['id'] for g in rows], 'supplementary IDs')
    require(not ({c['id'] for c in cases} & {c['id'] for c in baselinecases}), 'separate case IDs')
    receipts = []; outputs = []
    for run in ['run1-sdk', 'run2-sdk']:
        get = lambda n: entries[run + '/' + n]
        receipt = json.loads(get('receipt.json')); cp = json.loads(get('compile-receipt.json'))
        receipts.append(receipt)
        require(receipt['sourceCommit'] == COMMIT and receipt['caseCount'] == 80 and receipt['baselineCount'] == 70 and receipt['supplementaryCount'] == 10, run + ' counts')
        require(receipt['casesSha256'] == sha((base / 'cases.json').read_bytes()) and receipt['supplementaryCasesSha256'] == sha(casesraw), run + ' cases identity')
        require(receipt['pid'] > 0 and receipt['birthFileTime'] > 0 and receipt['heldProcessHandleSignaled'], run + ' held child')
        for p in [receipt, cp]:
            require(p['exitCode'] == 0 and p['timeout'] is False and p['stdoutEof'] is True and p['stderrEof'] is True, run + ' completion')
        require(0 <= receipt['elapsedSeconds'] < 30, run + ' elapsed')
        output = get('golden.ndjson'); outputs.append(output); lines = output.splitlines(keepends=True)
        require(len(lines) == 80 and b''.join(lines[:70]) == baseline and b''.join(lines[70:]) == golden == get('supplementary.ndjson'), run + ' exact 70+10 stdout')
        require(receipt['stdoutSha256'] == sha(output) and get('reference.stderr') == b'' and receipt['stderrSha256'] == sha(b''), run + ' stdout/stderr receipt')
        require(get('adapter.cpp') == (base / 'adapter.cpp').read_bytes() and receipt['adapterSha256'] == sha(get('adapter.cpp')) and get('provenance.json') == provraw, run + ' unchanged adapter/provenance')
        for n, digest in cp['inputSha256'].items():
            require(sha(get(n)) == digest, run + ' compiler input: ' + n)
        pcre = {p.rsplit('/',1)[1]: sha(b) for p,b in data.items() if p.startswith('Externals/poco/dependencies/pcre2/src/')}
        require(cp['pcreSourceSha256'] == pcre, run + ' original PCRE2 inputs')
        require(len(cp['compilerSha256']) == 64 and b'14.51.36231' in get('compile.cmd') and b'sdk-env.cmd' in get('compile.cmd'), run + ' compiler evidence')
        for span in prov['extractions']:
            source = data[span['path']]; body = source[span['startByte']:span['endByte']]
            require(sha(body) == span['sha256'], 'original native span SHA')
            require(source[:span['startByte']].count(b'\n')+1 == span['startLine'] and source[:span['endByte']].count(b'\n')+1 == span['endLine'], 'native span line range')
            if span['file']: require(get(span['file']) == body + b'\n', 'unchanged compiled span')
        for c in baselinecases + cases:
            if c['fileUtf8Hex'] is not None: require(get('inputs/'+c['id']+'.tsv') == bytes.fromhex(c['fileUtf8Hex']), 'original file-list input')
        generated = get('cases.inc')
        for c in cases:
            line = next(l for l in generated.splitlines() if l.startswith(('run("'+c['id']+'",').encode()))
            if c['args'][0]['type'] == 'raw': require(b'new StringLiteral(bytes({195}))' in line and c['args'][0]['hex'] == 'c3', 'safe raw source binding')
    require(outputs[0] == outputs[1], 'two independent compile stdout bytes')
    require(receipts[0]['pid'] != receipts[1]['pid'] or receipts[0]['birthFileTime'] != receipts[1]['birthFileTime'], 'separate child identity')
    require(json.loads(entries['run1/compile-receipt.json'])['exitCode'] != 0, 'initial SDK failure retained')
    return dict(baselineCases=70,supplementaryCases=10,totalNativeRowsPerRun=80,independentCompiles=2,sourceBlobs=len(data),sourceSpans=len(prov['extractions']))

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--repo-root',type=pathlib.Path,default=BASE.parents[2]);ap.add_argument('--self-check-rejections',action='store_true');a=ap.parse_args()
    files={p.relative_to(BASE).as_posix():p.read_bytes() for p in BASE.rglob('*') if p.is_file() and p.name != 'fixed-manifest.json'}
    manifest=json.loads((BASE/'fixed-manifest.json').read_bytes());fixed_files(files,manifest)
    entries,index=archive_entries(files['native-boundary-proof.zip']);base,data=sources(a.repo_root.resolve(),entries)
    counts=native(entries,files['cases.json'],files['golden.ndjson'],base,data)
    rejected=[]
    if a.self_check_rejections:
        def reject(label, action):
            try: action()
            except (ValueError,KeyError,json.JSONDecodeError): rejected.append(label)
            else: raise ValueError('mutation accepted: '+label)
        altered=dict(files);altered['golden.ndjson'] += b' '
        reject('golden-bytes',lambda:fixed_files(altered,manifest))
        alteredentries=dict(entries);alteredentries['run2-sdk/adapter.cpp'] += b' '
        reject('proof-entry',lambda:indexed_entries(alteredentries,index))
        alteredreceipt=dict(entries);p=json.loads(entries['run2-sdk/receipt.json']);p['exitCode']=1;alteredreceipt['run2-sdk/receipt.json']=json.dumps(p).encode()
        reject('receipt-exit-code',lambda:native(alteredreceipt,files['cases.json'],files['golden.ndjson'],base,data))
        missing=dict(entries);del missing['run2-sdk/reference.stderr']
        reject('missing-entry',lambda:indexed_entries(missing,index))
    print(json.dumps(dict(passed=True,indexedProofEntries=270,**counts,rejectedMutations=rejected,productExecuted=False,nativeRecompiled=False,gitAttributesAndBlobsChecked=False),separators=(',',':')))
if __name__ == '__main__': main()