"""元ソースの関数本体を変更せず抽出し、適合buffer adapterで実行する。"""
from pathlib import Path
import hashlib,json,sys

repository=Path(__file__).resolve().parents[1]
directory=Path(sys.argv[1]).resolve()
directory.mkdir(parents=True,exist_ok=True)
def body(path,marker):
    text=(repository/path).read_text('utf-8-sig')
    start=text.index(marker); opening=text.index('{',start); depth=0; quote=None; comment=None; escaped=False
    index=opening
    while index<len(text):
        c=text[index]; following=text[index+1:index+2]
        if comment=='line':
            if c=='\n':comment=None
        elif comment=='block':
            if c=='*' and following=='/':comment=None;index+=1
        elif quote:
            if escaped:escaped=False
            elif c=='\\':escaped=True
            elif c==quote:quote=None
        elif c=='/' and following=='/':comment='line';index+=1
        elif c=='/' and following=='*':comment='block';index+=1
        elif c in '\"\'':quote=c
        elif c=='{':depth+=1
        elif c=='}':
            depth-=1
            if not depth:
                end=index+1
                if marker.startswith(('struct ','class ')):end+=1
                return text[start:end]
        index+=1
    raise ValueError(marker)
specs={
 'legacy-types.inc': [('Src/MergeDoc.h','struct WordDiff {'),('Src/DiffList.h','class DiffMap\n'),('Src/DiffList.cpp','void DiffMap::InitDiffMap(')],
 'legacy-functions.inc': [('Src/MergeDocLineDiffs.cpp','std::vector<WordDiff>\nCMergeDoc::GetWordDiffArrayInRange('),('Src/MergeDocDiffSync.cpp','int CMergeDoc::GetMatchCost('),('Src/MergeDocDiffSync.cpp','void CMergeDoc::AdjustDiffBlock('),('Src/MergeDocDiffSync.cpp','static void\nValidateDiffMap('),('Src/MergeDocDiffSync.cpp','template <int npanes>\nstatic void\nValidateVirtualLineToRealLineMap('),('Src/MergeDocDiffSync.cpp','static std::vector<std::array<int, 2>>\nCreateVirtualLineToRealLineMap('),('Src/MergeDocDiffSync.cpp','static std::vector<std::array<int, 3>>\nCreateVirtualLineToRealLineMap3way(')]
}
manifest=[]
for output,functions in specs.items():
    chunks=[]
    for path,marker in functions:
        content=body(path,marker); chunks.append(content)
        manifest.append({'source':path,'marker':marker,'sourceSha256':hashlib.sha256((repository/path).read_bytes()).hexdigest(),'bodySha256':hashlib.sha256(content.encode()).hexdigest()})
    (directory/output).write_text('\n\n'.join(chunks)+'\n',encoding='utf-8',newline='\n')
(directory/'extraction.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
original=(directory/'legacy-functions.inc').read_text('utf-8')
before='WordDiff dummyWordDiff(0, 0, dr.begin[0], dr.begin[0], 0, 0, dr.begin[1], dr.begin[1]);'
after='WordDiff dummyWordDiff(0, 0, dr.begin[i0], dr.begin[i0], 0, 0, dr.begin[i1], dr.begin[i1]);'
assert original.count(before)==1
(directory/'corrected-functions.inc').write_text(original.replace(before,after),encoding='utf-8',newline='\n')
print(json.dumps({'originalDefinitions':len(manifest),'outputs':list(specs)}))
