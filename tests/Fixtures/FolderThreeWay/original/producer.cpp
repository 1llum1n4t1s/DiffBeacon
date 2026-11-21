// 原文 wrapper の型・接続だけを供給。採取対象外は到達時に失敗する。
#include <string>
#include <vector>
#include <iostream>
#include <locale.h>
#include <io.h>
#include <fcntl.h>
#include <cstdlib>
#include "../../../../Src/diffutils/src/diff.h"
#undef min
#undef max
#include "pch.h"
#include "DiffList.h"
#include "Diff3.h"
#include "original-diffcode.inc"
extern "C" DECL_TLS int no_discards;
[[noreturn]] static void unsupported(const char* path) { std::cerr << path << '\n'; std::exit(93); }
struct RootLogger { static void Error(const char* e) { unsupported(e); } };
struct MovedLines { enum class SIDE { LEFT, RIGHT }; void Add(SIDE,int,int) { unsupported("moved Add"); } };
struct Filter { bool HasRegExps() { unsupported("filter HasRegExps"); } };
struct PostFilterContext {};
struct CDiffContext { enum { DIFFS_UNKNOWN=-1 }; };
static std::vector<DiffRangeInfo>* pair_observer=nullptr;
// 原文 AddDiffRange からの接続だけを観測。原文 DiffList::AddDiff をそのまま呼ぶ。
struct ObservedDiffList : DiffList {
 void AddDiff(const DIFFRANGE& d) { DiffList::AddDiff(d);if(pair_observer)pair_observer->emplace_back(d); }
};
#define DiffList ObservedDiffList
struct CDiffWrapper {
 struct Options { bool m_filterCommentsLines=false,m_bIgnoreMissingTrailingEol=false,m_bIgnoreLineBreaks=false,m_bIgnoreBlankLines=false; } m_options;
 Filter *m_pFilterList=nullptr,*m_pSubstitutionList=nullptr;
 DiffList *m_pDiffList=nullptr;
 bool GetDetectMovedBlocks() const { return false; }
 MovedLines* GetMovedLines(int) { unsupported("GetMovedLines"); }
 int PostFilter(PostFilterContext&,change*,const file_data*) { unsupported("PostFilter"); }
 std::vector<DiffRangeInfo> InsertMovedBlocks3Way() { unsupported("InsertMovedBlocks3Way"); }
 static bool IsIdenticalOrIgnorable(change*);
 void AddDiffRange(DiffList*,unsigned,unsigned,unsigned,unsigned,OP_TYPE);
 void LoadWinMergeDiffsFromDiffUtilsScript3(change*,change*,change*,const file_data*,const file_data*,const file_data*);
};
#include "original-add.inc"
#include "original-identical.inc"
#include "original-comp02.inc"
#include "original-load3.inc"
#undef DiffList
static void ranges(const std::vector<DiffRangeInfo>& v) {
 std::cout << '['; bool comma=false;
 for(const auto& d:v) { if(comma)std::cout<<',';comma=true;
 std::cout<<"{\"begin\":["<<d.begin[0]<<','<<d.begin[1]<<','<<d.begin[2]<<"],\"end\":["<<d.end[0]<<','<<d.end[1]<<','<<d.end[2]<<"],\"op\":"<<d.op<<'}'; } std::cout<<']';
}
static void raw(change* c) { std::cout<<'[';bool comma=false;for(;c;c=c->link){if(comma)std::cout<<',';comma=true;std::cout<<"{\"line0\":"<<c->line0<<",\"line1\":"<<c->line1<<",\"deleted\":"<<c->deleted<<",\"inserted\":"<<c->inserted<<",\"trivial\":"<<int(c->trivial)<<'}';}std::cout<<']'; }
int main(int argc,char** argv) {
 if(argc!=5 && argc!=6) { std::cerr<<"usage: full34 mask left middle right\n";return 2; }
 bool observe=argc==5;if(!observe && std::string(argv[5])!="--no-observer")return 3;int mask=std::atoi(argv[1]);if(mask<1||mask>7)return 3;
 setlocale(LC_ALL,"C");output_style=OUTPUT_NORMAL;context=0;heuristic=1;no_discards=0;always_text_flag=0;horizon_lines=0;no_details_flag=0;line_end_char='\n';
 ignore_space_change_flag=ignore_all_space_flag=ignore_blank_lines_flag=ignore_case_flag=ignore_numbers_flag=ignore_eol_diff=ignore_some_changes=length_varies=0;
 file_data fd[3][2]={};change* scripts[3]={};int bins[3]={},binfiles[3]={};const int pairs[3][2]={{1,0},{1,2},{0,2}};
 for(int p=0;p<3;++p) {
  for(int j=0;j<2;++j) {int side=pairs[p][j];fd[p][j].name=(mask&(1<<side))?argv[side+2]:"NUL";fd[p][j].desc=_open(fd[p][j].name,_O_RDONLY|_O_BINARY);if(fd[p][j].desc<0||_fstat64(fd[p][j].desc,&fd[p][j].stat)!=0)return 4;if((mask&(1<<side))&&!S_ISREG(fd[p][j].stat.st_mode))return 5;}
  scripts[p]=diff_2_files(fd[p],0,&bins[p],0,&binfiles[p]);
  if(bins[p]||binfiles[p]) {std::cerr<<"binary path outside scope\n";return 94;}
 }
 auto *script10=scripts[0],*script12=scripts[1],*script02=scripts[2];int bin_flag10=bins[0],bin_flag12=bins[1],bin_flag02=bins[2];
  std::vector<DiffRangeInfo> pairRanges[3];
 // 各pairを単独で原文Load3へ渡す観測pass。算法・script・原文textは変えない。
 if(observe)for(int p=0;p<3;++p){ObservedDiffList observed;CDiffWrapper observer;observer.m_pDiffList=&observed;pair_observer=&pairRanges[p];observer.LoadWinMergeDiffsFromDiffUtilsScript3(p==0?script10:nullptr,p==1?script12:nullptr,p==2?script02:nullptr,fd[0],fd[1],fd[2]);pair_observer=nullptr;}
 ObservedDiffList diffList;CDiffWrapper dw;dw.m_pDiffList=&diffList;
 dw.LoadWinMergeDiffsFromDiffUtilsScript3(script10,script12,script02,fd[0],fd[1],fd[2]);
 int m_ndiffs=0,m_ntrivialdiffs=0;unsigned code=DIFFCODE::FILE;
 struct {DIFFCODE diffcode;} di{DIFFCODE((unsigned(mask)<<28)|DIFFCODE::THREEWAY)};
 #include "original-full-flags.inc"
 unsigned before=code;
 // 変換済み全側 UTF-8 の同一 encoding を供給。codepage 無視設定も既定 true。
 struct {bool m_bIgnoreCodepage=true;} ctxt;auto* m_pCtxt=&ctxt;int encoding[3]={65001,65001,65001};int nDirs=3;
 #include "original-correction.inc"
 std::cout<<"{\"presenceMask\":"<<mask<<",\"threewayInputFlags\":"<<di.diffcode.diffcode<<",\"pairs\":[";
 for(int p=0;p<3;++p){if(p)std::cout<<',';std::cout<<"{\"binaryStatus\":"<<bins[p]<<",\"binaryFiles\":"<<binfiles[p]<<",\"rawChanges\":";raw(scripts[p]);std::cout<<",\"ranges\":";ranges(pairRanges[p]);std::cout<<'}';}
 std::cout<<"],\"ranges\":";ranges(diffList.GetDiffRangeInfoVector());
 std::cout<<",\"significantCount\":"<<diffList.GetSignificantDiffs()<<",\"totalCount\":"<<diffList.GetSize()<<",\"reportedSignificant\":"<<m_ndiffs<<",\"reportedTrivial\":"<<m_ntrivialdiffs<<",\"flagsBefore\":"<<before<<",\"flagsAfter\":"<<code<<"}\n";
 for(int p=0;p<3;++p)for(int j=0;j<2;++j)_close(fd[p][j].desc);
 // 一ケース一 process。原本の buffer と script は OS 終了回収。
 return 0;
}



