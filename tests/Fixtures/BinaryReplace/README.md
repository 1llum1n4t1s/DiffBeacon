# Binary replacement: fixed Frhed reference

固定commit `a0a2f13fe4c37a8f979407e997186680450ad7ca` のFrhed原本から、decoded-byte置換kernelの実出力51件をMSVC x64で採取した。provenance.jsonはraw Git blobのSHA・サイズ、原文抽出行範囲・SHA、コンパイラコマンド・stdout・stderrを記録する。manifest.jsonは入力・goldenのSHA、実プロセスの全引数・raw stdout/stderr・終了コードを保持する。ライセンスは固定treeのGPL.txt（GPL v2）であり、COPYINGはこのtreeに存在しない。製品からgoldenを再生成しない。

## Adapter境界

ReplaceDlg.cpp:71..171と189..236、FindUtil.cpp:30..EOFを原文のまま抽出した。最小adapterはHWindow、String、SimpleArray、UndoRecord、codeconverter、vectorと選択状態を代替する。query/replacementは復号済みbytesとして渡す。codec互換は既存BinaryClipboardの634原本による別検証と分離する。NULも引数hex・長さ付きstringで保持し、同一query判定は実際のdecoded bytesを比較する。ASCII A-Zだけのfoldとhigh bit保持は原文FindUtilそのものである。

初期選択は正規化start、caret=start。未選択の選択境界はhexwnd.cpp:5416..5425どおりcaret。directionはSelection=0、Forward=1、Backward=-1。forward未選択はcaret+1から検索し、empty replacement後にも隣接byteを飛ばす。backwardは変更済みsuffixを跨ぐpatternを拾う場合がある。最初の選択置換はquery一致不要で、queryより長短の選択も置換する。forwardはreplacement内のpatternを再検索しない。未選択の終了状態はselectionStart=0/selectionLength=0へ正規化し、caretは旧値を保持する。

終端を過ぎる空検索窓の参照形成はBufferのsentinelで安全に代替する。MSVC x64でsearch lengthが負となる窓は原文FindUtilが検索せず、sentinelを有効bytesへ混ぜない。empty query、整数overflow、無効な選択等の旧未定義入力は採取しない。

heksedit.rc:715のReplace buttonは初期enabled。EnableDlgItemの戻りFALSEを再現すると、未選択の初回失敗attemptもocc_numに加算される。legacyReportedCountはその旧表示件数、successfulCountはpush_undorecordの成功回数であり、製品は後者と比較する。query==replacementは旧が冒頭で拒否するため、そのfixtureは方向置換だけに限定する。

製品の意図的な安全差異は、allの原子的候補・一つのUndo、不正入力の拒否、actual decoded bytesでの同一判定である。Selection単発はpatternを使用せず、empty patternやquery==replacementでも任意selectionを置換できる。同じbytesへの置換は履歴no-op。この差異は旧kernel互換fixtureへ混ぜず製品E2Eで検証する。

## 独立reader

### 製品の限定実行

リポジトリルートで.NET 11 SDKとPowerShell 7を使う。既存Release buildを対象とし、出力はリポジトリ／Codexユーザーディレクトリ外の新規Tempへ指定する。名前 `artifacts` のフォルダーを作らない。

```powershell
$python = '<Python 3の実行ファイルの絶対パス>'
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('Codex/DiffBeacon/binary-replace-' + [guid]::NewGuid().ToString('N'))
# CLI保存／複製／包装と全readerも実行する限定E2E
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output "$runRoot/e2e" --binary-replace-only --python "$python"
# 実ボタンとAvalonia置換ダイアログだけを独立して確認する場合
dotnet Src/DiffBeacon.App/bin/Release/net11.0/DiffBeacon.dll --self-test "$runRoot/gui" --binary-replace-only
& $python -X utf8 -B tests/Fixtures/BinaryReplace/verify_gui.py --gui "$runRoot/gui" --receipt "$runRoot/gui-reader-receipt.json"
```

E2EはGUI限定を内部で起動し、固定原本reader・GUI reader・CLI readerをすべて別プロセス実行する。GUI単独の出力には通常／内包保存・複製／包装のCLI実行結果が揃わないため、`verify_cli.py` は限定E2E完了後の出力だけを入力にする。E2Eの `fixtures/<run-id>/binary-replace` がworkで、`--receipt` は既存でないJSONをそのwork直下へ指定する。

```powershell
$work = '<E2E出力先>/fixtures/<run-id>/binary-replace'
& $python -X utf8 -B tests/Fixtures/BinaryReplace/verify_cli.py --work $work --receipt "$work/cli-reader-repeat-receipt.json"
```

`verify_gui.py` は `--gui` が必須、`--receipt` は任意で、全原本51件・GUI状態・保存bytes／SHA・全側の共有履歴と保存点・不正入力／容量／作業量／履歴上限・取消／古い完了／タブABA・PNG証拠を独立照合する。`verify_cli.py` は `--work` と `--receipt` がともに必須で、固定期待から通常／内包2群の保存版・複製・包装・全asset bytes・ZIP全entry／CRC・入力保持と実終了metadataを照合する。readerの内容一致だけでtimeoutや部分出力を成功扱いしない。入力／出力、PNG、proof.json、reference-product.ndjson、assertions.json、標準出力／エラー、reader-process.jsonとreceiptを一組として保持し、不要な一時物は共通清掃手順で処理する。ネイティブ保存ダイアログ・実OSクリップボード・通常デスクトップpointer・AOT／他RIDはこの限定検証とは別である。

### 固定原本だけ／製品NDJSONの照合


```powershell
& <python3.exe> tests/Fixtures/BinaryReplace/verify.py <product-output.ndjson>
```

各caseを一度ずつ含むNDJSONのフィールドはcaseId、bytesBase64、replacementCount、selectionStart、selectionLength、caret。readerは原本・抽出・入力・golden・旧stdout・製品の全bytes/位置/実置換数を照合する。出力引数省略時は固定原本だけ照合し、製品実行済みと報告しない。

## 再採取

既存MSVC x64を使用し依存を追加しない。祖先のreparse pointを確認した外部の作業別Tempをcwdとする。名前artifactsのフォルダーは作成・再利用しない。原本はgit showをraw bytesで取得しprovenance SHAへ照合する。original-functions.incをTempへコピーし、以下の最小adapterをadapter.cppへUTF-8保存する。

```powershell
# MSVC x64環境を有効化済みのPowerShell、cwdは専用外部Temp
& cl.exe /nologo /EHsc /std:c++17 /Od adapter.cpp /Fe:reference.exe
# 各case.process.argumentsをそのままreference.exeの引数へ渡す
# source/query/replacement hex、caret、selection length、direction、match case
```

各caseを別プロセスで起動し、終了0とrawstdout一致を確認する。stdout第1行は全bytesのhex、第2行はsuccessful legacy start length caret。harness・exe・obj・compilerログは外部Tempのみへ出力する。今回の場所は `C:\Users\IMT\AppData\Local\Temp\Codex\DiffBeacon\brp1011\reference`。親の製品照合と必要な証拠保持後に、今回作成分だけ共通ごみ箱手順で清掃する。

## 最小adapter（再現用）

```cpp

#include <algorithm>
#include <vector>
#include <string>
#include <iostream>
#include <cstring>
#include <cstdio>
using BYTE=unsigned char; using SSIZE_T=long long; using TCHAR=char;
using std::min; using std::max;
const int FALSE=0,TRUE=1,BST_CHECKED=1,BST_UNCHECKED=0;
enum {IDC_MATCHCASE_CHECK,IDC_TO_REPLACE_EDIT,IDC_REPLACEWITH_EDIT,IDC_USETRANSLATION_CHECK,IDC_REPLACE_BUTTON,IDS_REPL_NO_DATA,IDS_REPL_CANT_DELETE,IDS_REPL_FAILED,IDS_REPL_CANNOT_CONVERT,IDS_REPL_SAME_STRS,IDS_REPL_COUNT,MB_ICONERROR,MB_ICONINFORMATION};
int reported=0; std::string query,replacement;
const char* GetLangString(int id){return id==IDS_REPL_COUNT?"%d":"error";}
struct HWindow { bool match=true; int IsDlgButtonChecked(int id){return id==IDC_MATCHCASE_CHECK?(match?1:0):1;} void GetDlgItemTextA(int id,std::string& s){s=id==IDC_TO_REPLACE_EDIT?query:replacement;} static HWindow* GetFocus(){static HWindow w; return &w;} bool IsWindowEnabled(){return true;} void SetFocus(){} };
int EnableDlgItem(HWindow*,int,int){return FALSE;}
void MessageBox(HWindow*,const char* s,int kind){if(kind==MB_ICONINFORMATION) reported=std::stoi(s);}
#define _stprintf std::sprintf
struct WaitCursor {};
template<class T> struct SimpleArray {std::vector<T> v; T* pointer(){return v.data();} size_t size(){return v.size();}};
struct UndoRecord { struct Data{}; static Data* alloc(BYTE*,size_t){return new Data;} static void free(Data* d){delete d;} };
std::string activeDecoded;
struct Text2BinTranslator {std::string s; Text2BinTranslator(const char* p):s(p){} bool bCompareBin(const Text2BinTranslator&,int,int){return query==replacement;} };
size_t create_bc_translation(BYTE** p,const char*,size_t,int,int){*p=new BYTE[query.size()]; memcpy(*p,query.data(),query.size()); return query.size();}
struct Buffer {std::vector<BYTE> v; BYTE sentinel=0; BYTE& operator[](size_t i){return i<v.size()?v[i]:sentinel;} size_t size(){return v.size();} bool Replace(size_t i,size_t n,const BYTE* p,size_t k){v.erase(v.begin()+i,v.begin()+i+n); if(k)v.insert(v.begin()+i,p,p+k);return true;}};
struct ReplaceDlg {Buffer m_dataArray; bool bSelected=false,bPasteAsText=false,bFilestatusChanged=false; size_t iCurByte=0,iStartOfSelection=0,iEndOfSelection=0; int iCharacterSet=0,iBinaryMode=0,successful=0; std::string strToReplaceData,strReplaceWithData; size_t iGetStartOfSelection(){return bSelected?min(iStartOfSelection,iEndOfSelection):iCurByte;} size_t iGetEndOfSelection(){return bSelected?max(iStartOfSelection,iEndOfSelection):iCurByte;} void push_undorecord(size_t,size_t,UndoRecord::Data* d){successful++;delete d;} bool transl_text_to_binary(SimpleArray<BYTE>& a){a.v.assign(replacement.begin(),replacement.end());return true;} void set_wnd_title(){} void adjust_view_for_selection(){} void resize_window(){} void synch_sibling(){} bool find_and_select_data(int,bool); bool replace_selected_data(HWindow*); void replace_directed(HWindow*,int,bool);};
SSIZE_T findutils_FindBytes(BYTE*,SSIZE_T,BYTE*,SSIZE_T,int,bool);
#include "original-functions.inc"
std::string unhex(std::string s){std::string r; for(size_t i=0;i<s.size();i+=2)r.push_back(char(std::stoi(s.substr(i,2),nullptr,16)));return r;}
int main(int argc,char** argv){if(argc!=8)return 2; query=unhex(argv[2]);replacement=unhex(argv[3]); ReplaceDlg d;auto bytes=unhex(argv[1]);d.m_dataArray.v.assign(bytes.begin(),bytes.end());d.iCurByte=std::stoull(argv[4]);int len=std::stoi(argv[5]);if(len){d.bSelected=true;d.iStartOfSelection=d.iCurByte;d.iEndOfSelection=d.iCurByte+len-1;}HWindow w;w.match=std::stoi(argv[7])!=0;int mode=std::stoi(argv[6]);d.replace_directed(&w,mode,true);const char* h="0123456789abcdef";for(BYTE b:d.m_dataArray.v)std::cout<<h[b>>4]<<h[b&15];std::cout<<"\n"<<d.successful<<" "<<reported<<" "<<(d.bSelected?d.iGetStartOfSelection():0)<<" "<<(d.bSelected?d.iGetEndOfSelection()-d.iGetStartOfSelection()+1:0)<<" "<<d.iCurByte<<"\n";}

```
