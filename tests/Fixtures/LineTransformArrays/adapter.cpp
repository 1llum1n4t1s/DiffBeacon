// 検証専用。原典の関数本体は reproduce.py が無改変で切り出して include する。
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <functional>
#include <iostream>
#include <limits>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
#include "ILineDataProvider.h"
using String = std::string;
struct FileTextEncoding {};
struct TableProps { bool istable; char delimiter; char quote; };
namespace ucr {
#include "utf8.inc"
 std::string toUTF8(const String& s) { return s; } String toTString(const std::string& s) { return s; } }
#include "provider.inc"
struct ValueType2;
// 原典文字列化本体をそのままcompileするための未使用Poco型binding。
// fixtureはTimestamp/Regex/FileContentを構築しない。日時処理が呼ばれたら失敗させる。
namespace Poco {
 struct Timestamp {auto operator<=>(const Timestamp&)const=default; int64_t operator-(Timestamp)const {throw std::runtime_error("unsupported timestamp");} Timestamp operator+(int64_t)const {throw std::runtime_error("unsupported timestamp");} Timestamp operator-(int64_t)const {throw std::runtime_error("unsupported timestamp");}};
 struct RegularExpression {bool match(const std::string&)const {throw std::runtime_error("unsupported regex");}};
 inline int icompare(const std::string&,const std::string&){throw std::runtime_error("unsupported icompare");}
 struct Glob {static constexpr int GLOB_CASELESS=1; Glob(const std::string&,int){throw std::runtime_error("unsupported Glob");} bool match(const std::string&){throw std::runtime_error("unsupported Glob");}};
 struct Exception : std::runtime_error {using std::runtime_error::runtime_error;};
 struct LocalDateTime {explicit LocalDateTime(Timestamp){throw std::runtime_error("Poco stub must not execute");}};
 struct DateTimeFormatter {static std::string format(LocalDateTime,const char*){throw std::runtime_error("Poco stub must not execute");}};
}
struct FileContentRef {String path; bool operator==(const FileContentRef&)const{throw std::runtime_error("unsupported FileContent");} bool Contains(const std::string&,bool){throw std::runtime_error("unsupported FileContent");} bool REContains(const Poco::RegularExpression&){throw std::runtime_error("unsupported FileContent");}};
using ValueType = std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 { ValueType value; };
#include "to-string.inc"
#include "convert-string.inc"
struct FilterSharedContext {};
struct FilterEvalContext { const ILineDataProvider* provider = nullptr; int lineIndex = -1; const FilterSharedContext* sharedContext = nullptr; };
struct ExprNode { virtual ~ExprNode() = default; virtual ValueType Evaluate(const FilterEvalContext&) const = 0; };
struct InvalidArgumentError : std::runtime_error { using std::runtime_error::runtime_error; };
#include "bool.inc"
#include "if.inc"
#include "range-parse.inc"
#include "range-evaluate.inc"
#include "fields.inc"
#include "line-at.inc"
// 以下のscalar接続は型adapter。array/Poco/Unicodeの旧実装の代用品ではない。
#include "get-string.inc"
#include "lifting.inc"
#include "array-functions.inc"
#include "len.inc"
#include "to-int.inc"
#include "choices.inc"
#include "logical.inc"
#include "FilterParser.h"
struct DivisionByZeroError : std::runtime_error {using std::runtime_error::runtime_error;};
template<class... T> bool regexMatch(T&&...){throw std::runtime_error("unsupported regex");}
inline std::shared_ptr<Poco::RegularExpression> createRegex(const std::string&,bool){throw std::runtime_error("unsupported regex");}
#include "compute.inc"
#include "trim.inc"
struct Node : ExprNode {
 std::string op, text; int64_t integer=0; std::vector<std::shared_ptr<Node>> children; int side=0;
 Node(std::string o,std::string t,int64_t i,int s,std::vector<std::shared_ptr<Node>> c):op(o),text(t),integer(i),children(std::move(c)),side(s){}
 ValueType Evaluate(const FilterEvalContext& c) const override {
  // 原典MakeColumnRangeFuncのRight(-1)は単一provider比較のdirs-1 = 0。
  const int index=side<0?0:side;
  if(op=="string")return text;
  if(op=="integer")return integer;
  if(op=="bool")return integer!=0;
  if(op=="none")return std::monostate{};
  std::vector<ExprNode*> args;for(auto& a:children)args.push_back(a.get());
  if(op=="array")return ArrayFunc(c,&args);
  if(op=="at")return AtFunc(c,&args);
  if(op=="anyof")return AnyOfFunc(c,&args);
  if(op=="allof")return AllOfFunc(c,&args);
  if(op=="allequal")return AllEqualFunc(c,&args);
  if(op=="andeach")return AndEachFunc(c,&args);
  if(op=="oreach")return OrEachFunc(c,&args);
  if(op=="noteach")return NotEachFunc(c,&args);
  if(op=="ifeach")return IfEachFunc(c,&args);
  if(op=="choose")return ChooseFunc(c,&args);
  if(op=="chooseeach")return ChooseEachFunc(c,&args);
  if(op=="toint")return ToIntFunc(c,&args);
  if(op=="len")return LenFunc(c,&args);
  if(op=="tostring")return ToStringValue(children[0]->Evaluate(c));
  if(op=="add")return compute(TK_PLUS,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="sub")return compute(TK_MINUS,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="mul")return compute(TK_STAR,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="div")return compute(TK_SLASH,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="mod")return compute(TK_MOD,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="eq")return compute(TK_EQ,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="ne")return compute(TK_NE,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="lt")return compute(TK_LT,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="le")return compute(TK_LE,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="gt")return compute(TK_GT,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="ge")return compute(TK_GE,children[0]->Evaluate(c),children[1]->Evaluate(c),true);
  if(op=="if")return IfFunc(c,&args);
  if(op=="range")return columnRangeFunc(index,c,&args);
  if(op=="line")return LineField(index,c);
  if(op=="number")return LineNumberField(index,c);
  if(op=="count")return ColumnCountField(index,c);
  if(op=="column")return ColumnField(index,static_cast<int>(integer)-1,c);
  if(op=="lineat")return lineAt(index,c,&args);
  if(op=="offsetat")return lineOffsetAt(index,c,&args);
  if(op=="trim")return TrimFunc(c,&args);
  // 明示ASTの整数比較接続のみ。旧parser/BinaryOpNodeの検証範囲には含めない。
  if(op=="equal")return children[0]->Evaluate(c)==children[1]->Evaluate(c);
  throw std::runtime_error("unsupported adapter node");
 }
 void Compile()const {for(auto& a:children)a->Compile(); if(op=="range"&&children[0]->op=="string") ParseColumnRanges(children[0]->text);}
};
using N=std::shared_ptr<Node>;
static N node(std::string op,std::string text,int64_t integer,int side,std::initializer_list<N> children) {return std::make_shared<Node>(op,text,integer,side,std::vector<N>(children));}
constexpr int FILTER_ERROR_NO_ERROR=0;
struct FilterExpression {
 N root; int errorCode=0; std::string errorMessage;
 std::string TransformLine(const FilterEvalContext& c) {try {return ConvertString(root->Evaluate(c));}catch(const std::exception& e){errorCode=1;errorMessage=e.what();return {};}}
};
#include "apply.inc"
struct Group { int mask; std::vector<N> expressions; };
static std::string hex(const std::string& s) {static const char* d="0123456789abcdef";std::string r;for(unsigned char c:s){r+=d[c>>4];r+=d[c&15];}return r;}
struct Selection {int row,pane;std::vector<int> columns;std::string key;};
struct RecordingProvider : ILineDataProvider {
 VectorLineDataProvider& p; mutable std::vector<Selection> selections;
 explicit RecordingProvider(VectorLineDataProvider& v):p(v){}
 int GetLineCount()const override{return p.GetLineCount();}
 std::string GetLine(int pane,int row)const override{return p.GetLine(pane,row);}
 int GetColumnCount(int pane,int row)const override{return p.GetColumnCount(pane,row);}
 std::string GetColumn(int pane,int row,int column)const override{return p.GetColumn(pane,row,column);}
 std::string GetColumns(int pane,int row,const std::vector<int>& columns)const override {auto key=p.GetColumns(pane,row,columns);selections.push_back({row,pane,columns,key});return key;}
 int GetRealLineNumber(int pane,int row)const override{return p.GetRealLineNumber(pane,row);}
 unsigned GetLineFlags(int pane,int row)const override{return p.GetLineFlags(pane,row);}
 unsigned GetLineEol(int pane,int row)const override{return p.GetLineEol(pane,row);}
};
static std::string bytes(std::initializer_list<unsigned char> v) {return std::string(v.begin(),v.end());}
static void runCase(const char* id,bool table,char delimiter,char quote,int pane,std::vector<std::string> originals,std::vector<Group> groups) {
 VectorLineDataProvider p(originals,{}, {table,delimiter,quote});
 RecordingProvider observed(p);
 std::string status="ok",phase,error;
 try {for(auto& g:groups)for(auto& n:g.expressions)n->Compile();}catch(const std::exception& e){status="error";phase="compile";error=e.what();}
 if(status=="ok")for(auto& g:groups)if(g.mask&(1<<pane)){
  std::vector<FilterExpression> expressions;std::vector<FilterEvalContext> contexts;
  for(auto& n:g.expressions){expressions.push_back({n});contexts.push_back({&observed});}
  if(!ApplyFilterExpressionsToLines(p,expressions,contexts,error)){status="error";phase="evaluate";break;}
 }
 std::cout<<"{\"id\":\""<<id<<"\",\"status\":\""<<status<<"\",\"errorPhase\":\""<<phase<<"\",\"error\":\""<<error<<"\",\"lines\":[";
 VectorLineDataProvider original(originals,{}, {table,delimiter,quote});
 for(int i=0;i<p.GetLineCount();++i){if(i)std::cout<<",";std::cout<<"{\"originalHex\":\""<<hex(originals[i])<<"\",\"keyHex\":\""<<hex(p.GetFullLine(0,i))<<"\",\"originalEol\":"<<original.GetLineEol(0,i)<<",\"keyEol\":"<<p.GetLineEol(0,i)<<",\"columnCount\":"<<original.GetColumnCount(0,i)<<"}";}
 std::cout<<"],\"selections\":[";
 for(size_t i=0;i<observed.selections.size();++i){if(i)std::cout<<",";auto& s=observed.selections[i];std::cout<<"{\"row\":"<<s.row<<",\"providerPane\":"<<s.pane<<",\"columns\":[";for(size_t j=0;j<s.columns.size();++j){if(j)std::cout<<",";std::cout<<s.columns[j];}std::cout<<"],\"keyHex\":\""<<hex(s.key)<<"\"}";}
 std::cout<<"]}\n";
}
int main(){
#include "cases.inc"
 return 0;
}
