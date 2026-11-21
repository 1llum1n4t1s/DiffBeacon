// 関数本体は元ソースから無変更抽出。buffer/DIFFOPTIONSは正常経路のadapter。
#define NOMINMAX
#include <windows.h>
#include "pch.h"
#include "stringdiffs.h"
#include <cassert>
#include <iostream>
#include <iomanip>
#include <clocale>
#define ASSERT assert
#include "legacy-types.inc"
struct DIFFRANGE {int begin[3]{},end[3]{};};
struct DIFFOPTIONS {bool bIgnoreCase{},bIgnoreEol{},bIgnoreLineBreaks{},bIgnoreNumbers{}; int nIgnoreWhitespace{};};
enum class CRLFSTYLE {AUTOMATIC};
struct Line {String content,ending;};
struct Buffer {
    std::vector<Line> lines;
    int sourceRows{};
    int GetLineCount()const{return static_cast<int>(lines.size());}
    int GetLineLength(int n)const{return static_cast<int>(lines.at(n).content.size());}
    int GetFullLineLength(int n)const{return static_cast<int>(lines.at(n).content.size()+lines.at(n).ending.size());}
    const wchar_t* GetLineEol(int n)const{return lines.at(n).ending.c_str();}
    void GetTextWithoutEmptys(int first,int,int last,int length,String& out,CRLFSTYLE,bool)const {
        out.clear(); for(int n=first;n<=last;n++){out+=lines.at(n).content.substr(0,n==last?length:lines.at(n).content.size());if(n<last)out+=lines.at(n).ending;}
    }
};
struct Wrapper {DIFFOPTIONS value;void GetOptions(DIFFOPTIONS* target){*target=value;}};
struct CMergeDoc {
    int m_nBuffers{}; Buffer* m_ptBuf[3]{}; Wrapper m_diffWrapper; bool character=true;int breaks=1;
    int GetBreakType()const{return breaks;} bool GetByteColoringOption()const{return character;}
    std::vector<WordDiff> GetWordDiffArrayInRange(const int[3],const int[3],bool=false,int=-1,int=-1);
    int GetMatchCost(const DIFFRANGE&,int,int,int,int,const std::vector<WordDiff>&);
    void AdjustDiffBlock(DiffMap&,const DIFFRANGE&,const std::vector<WordDiff>&,int,int,int,int,int,int);
};
#ifdef CORRECT_PAIR_DUMMY
#include "corrected-functions.inc"
#else
#include "legacy-functions.inc"
#endif
static Buffer load(const String& text){
    Buffer result;int start=0;bool quoted=false;
    for(int i=0;i<static_cast<int>(text.size());i++){
        if(text[i]==L'"')quoted=!quoted;
        if(!quoted&&(text[i]==L'\r'||text[i]==L'\n')){
            int end=i;if(text[i]==L'\r'&&i+1<static_cast<int>(text.size())&&text[i+1]==L'\n')i++;
            result.lines.push_back({text.substr(start,end-start),text.substr(end,i-end+1)});start=i+1;
        }
    }
    if(start<static_cast<int>(text.size()))result.lines.push_back({text.substr(start),L""});
    result.sourceRows=static_cast<int>(result.lines.size());
    // 実ロードの追加EOF空行。DIFFRANGEはその表示専用行を含めない。
    if(text.empty()||(!result.lines.empty()&&!result.lines.back().ending.empty()))result.lines.push_back({L"",L""});
    return result;
}
static void json(const String& value){std::cout<<'"';for(wchar_t c:value){if(c==L'"'||c==L'\\')std::cout<<'\\'<<static_cast<char>(c);else if(c<32||c>126)std::cout<<"\\u"<<std::hex<<std::setw(4)<<std::setfill('0')<<static_cast<unsigned>(c)<<std::dec;else std::cout<<static_cast<char>(c);}std::cout<<'"';}
struct Case {String name;std::array<String,3> text;int panes=2;std::array<int,3> start{};};
int wmain(int argc,wchar_t** argv){
    strdiff::Init();strdiff::SetBreakChars(L",.;:");
    const String central=L"A one two three four B\n",split=L"A xxxxxxxxxxxxxxxxxx B\nC one two three four D\n";
    const String capped=L"A "+String(4093,L'x')+L" B\n";
    std::vector<Case> cases={
        {L"zero-tie",{L"a",L"b\nc"}}, {L"zero-tie-reverse",{L"b\nc",L"a"}},
        {L"central",{central,split}}, {L"central-reverse",{split,central}},
        {L"4096-edge",{L"A "+String(4092,L'x')+L" B\n",L"C\nD "+String(4092,L'x')+L" E\n"}},
        {L"4096-both-edge",{L"A "+String(4092,L'x')+L" B\n",L"\nD "+String(4092,L'x')+L" E\n"}},
        {L"4097-cap",{capped,L"C\nD "+String(4093,L'x')+L" E\n"}},
        {L"4097-one-pair",{capped,L"B "+String(4093,L'x')+L" A\n"}},
        {L"mixed-eol",{L"A one two three four B\r\n",L"A xxxxxxxxxxxxxxxxxx B\nC one two three four D\r\n"}},
        {L"eof-no-eol",{L"A one two three four B",L"A xxxxxxxxxxxxxxxxxx B\nC one two three four D"}},
        {L"eol-only",{L"a\r\nb\r\n",L"a\nb\n"}},
        {L"wrapped",{L"one two\nthree",L"one\ntwo three"}},
        {L"empty-left",{L"",L"a\nb\n"}}, {L"empty-right",{L"a\nb\n",L""}},
        {L"quoted",{L"1,\"A\r\ncommon B\"\r\n",L"0,Z\r\n1,\"C\r\ncommon D\"\r\n"}},
        {L"central-three",{central,central,split},3},
        {L"20-adopt",{L"H\nX\nB\nT\n",L"H\nB\nT\n",L"H\nY\nX\nB\nT\n"},3},
        {L"20-ghost",{L"H\nX\nT\nZ\n",L"H\nB\nT\n",L"H\nY\nX\nB\nT\nQ\nZ\n"},3},
        {L"20-inconsistent",{L"H\nX\nB\nY\nT\n",L"H\nB\nT\n",L"H\nY\nB\nX\nT\n"},3},
        {L"offset-three",{L"head0\nA one two three four B\n",L"head1\nhead2\nA one two three four B\n",L"head3\nhead4\nhead5\nA xxxxxxxxxxxxxxxxxx B\nC one two three four D\n"},3,{1,2,3}},
        {L"offset-prefix-three",{L"head0\nprefixAAAAAAAAAAAA old end\n",L"head1\nhead2\nprefixAAAAAAAAAAAA old end\n",L"head3\nhead4\nhead5\nprefixAAAAAAAAAAAA new xxxx\nsomething old end\n"},3,{1,2,3}},
        {L"empty-base",{L"a\nb\n",L"",L"z\na\nb\n"},3}
    };
    std::cout<<"{\"locale\":\""<<setlocale(LC_ALL,nullptr)<<"\",\"cases\":[";bool first=true;
    for(const auto& c:cases)for(int config=0;config<4;config++){
        if(argc>1&&c.name!=argv[1])continue;
        std::wcerr<<c.name<<L" config="<<config<<L"\n";
        if(!first)std::cout<<',';first=false;
        std::array<Buffer,3> buffers;CMergeDoc doc;doc.m_nBuffers=c.panes;doc.character=config!=1;
        doc.m_diffWrapper.value.bIgnoreEol=config!=2;doc.m_diffWrapper.value.bIgnoreCase=config==3;doc.m_diffWrapper.value.nIgnoreWhitespace=config==3?2:0;
        DIFFRANGE range;for(int pane=0;pane<c.panes;pane++){buffers[pane]=load(c.text[pane]);doc.m_ptBuf[pane]=&buffers[pane];range.begin[pane]=c.start[pane];range.end[pane]=buffers[pane].sourceRows-1;}
        std::array<DiffMap,3> maps;std::array<std::vector<WordDiff>,3> diffs;
        std::cout<<"{\"name\":";json(c.name);std::cout<<",\"panes\":"<<c.panes<<",\"config\":"<<config<<",\"text\":[";
        for(int p=0;p<c.panes;p++){if(p)std::cout<<',';json(c.text[p]);}std::cout<<"],\"starts\":[";
        for(int p=0;p<c.panes;p++){if(p)std::cout<<',';std::cout<<range.begin[p];}std::cout<<"],\"ends\":[";
        for(int p=0;p<c.panes;p++){if(p)std::cout<<',';std::cout<<range.end[p]+1;}std::cout<<"],\"pairs\":[";
        int pairCount=c.panes==2?1:3;
        for(int pair=0;pair<pairCount;pair++){
            int a=pair,b=(pair+1)%c.panes;int ac=range.end[a]-range.begin[a]+1,bc=range.end[b]-range.begin[b]+1;
            // 旧投影のゼロ側配列アクセスは実行せず、原本Adjustのone-sided分岐だけ実行。
            if(ac>0&&bc>0)diffs[pair]=doc.GetWordDiffArrayInRange(range.begin,range.end,false,a,b);
            maps[pair].InitDiffMap(ac);doc.AdjustDiffBlock(maps[pair],range,diffs[pair],a,b,0,ac-1,0,bc-1);
            ValidateDiffMap(maps[pair]);if(pair)std::cout<<',';
            std::cout<<"{\"sides\":["<<a<<','<<b<<"],\"projected\":[";
            for(size_t d=0;d<diffs[pair].size();d++){if(d)std::cout<<',';auto&w=diffs[pair][d];std::cout<<'['<<w.beginline[0]<<','<<w.begin[0]<<','<<w.endline[0]<<','<<w.end[0]<<','<<w.beginline[1]<<','<<w.begin[1]<<','<<w.endline[1]<<','<<w.end[1]<<']';}
            std::cout<<"],\"scores\":[";for(int i=0;i<ac;i++){if(i)std::cout<<',';std::cout<<'[';for(int j=0;j<bc;j++){if(j)std::cout<<',';std::cout<<doc.GetMatchCost(range,a,b,range.begin[a]+i,range.begin[b]+j,diffs[pair]);}std::cout<<']';}
            std::cout<<"],\"map\":[";for(size_t i=0;i<maps[pair].m_map.size();i++){if(i)std::cout<<',';int v=maps[pair].m_map[i];if(v==DiffMap::GHOST_MAP_ENTRY)std::cout<<"null";else std::cout<<v;}std::cout<<"]}";
        }
        std::cout<<"],\"mapping\":[";
        if(c.panes==2){auto rows=CreateVirtualLineToRealLineMap(maps[0],static_cast<int>(maps[0].m_map.size()),buffers[1].sourceRows-range.begin[1]);for(size_t i=0;i<rows.size();i++){if(i)std::cout<<',';std::cout<<'[';for(int p=0;p<2;p++){if(p)std::cout<<',';int v=rows[i][p];if(v==DiffMap::GHOST_MAP_ENTRY)std::cout<<"null";else std::cout<<v+range.begin[p]+1;}std::cout<<']';}}
        else{auto rows=CreateVirtualLineToRealLineMap3way(maps[0],maps[1],maps[2],static_cast<int>(maps[0].m_map.size()),static_cast<int>(maps[1].m_map.size()),static_cast<int>(maps[2].m_map.size()));for(size_t i=0;i<rows.size();i++){if(i)std::cout<<',';std::cout<<'[';for(int p=0;p<3;p++){if(p)std::cout<<',';int v=rows[i][p];if(v==DiffMap::GHOST_MAP_ENTRY)std::cout<<"null";else std::cout<<v+range.begin[p]+1;}std::cout<<']';}}
        std::cout<<"]}";
    }
    std::cout<<"]}\n";strdiff::Close();return 0;
}
