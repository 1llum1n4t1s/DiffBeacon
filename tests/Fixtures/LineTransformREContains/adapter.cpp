// 検証専用。includeする原典spanは無改変。未使用の外部型は呼ばれたら失敗する。
#define NOMINMAX
#define _UNICODE
#define UNICODE
#include <Windows.h>
#include <tchar.h>
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <functional>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
#define PCRE2_CODE_UNIT_WIDTH 8
#include <pcre2.h>
namespace Poco {
struct Exception:std::runtime_error {using std::runtime_error::runtime_error;};
struct RegularExpressionException:Exception {using Exception::Exception;};
struct AssertionViolationException:Exception {using Exception::Exception;};
class RegularExpression {
public:
#include "options.inc"
RegularExpression(const std::string&,int=0,bool=true);
~RegularExpression();
#include "match-type.inc"
int match(const std::string&,std::string::size_type,Match&,int)const;
int match(const std::string& text,Match& m)const {return match(text,0,m,0);}
bool match(const std::string&,std::string::size_type=0)const;
private:
static int compileOptions(int),matchOptions(int);
void* _pcre;std::map<int,std::string> _groups;
};
struct Timestamp {
auto operator<=>(const Timestamp&)const=default;
int64_t operator-(Timestamp)const {throw Exception("unused timestamp");}
Timestamp operator+(int64_t)const {throw Exception("unused timestamp");}
Timestamp operator-(int64_t)const {throw Exception("unused timestamp");}
};
struct LocalDateTime {explicit LocalDateTime(Timestamp){throw Exception("unused timestamp");}};
struct DateTimeFormatter {static std::string format(LocalDateTime,const char*){throw Exception("unused timestamp");}};
inline int icompare(const std::string&,const std::string&){throw Exception("unused icompare");}
struct Glob {static constexpr int GLOB_CASELESS=1;Glob(const std::string&,int){throw Exception("unused glob");}bool match(const std::string&){throw Exception("unused glob");}};
}
namespace {
#include "match-data.inc"
;
}
namespace Poco {
#define poco_assert(cond) if (!(cond)) throw AssertionViolationException(#cond); else (void)nullptr
#include "poco-ctor.inc"
#include "poco-dtor.inc"
#include "poco-match.inc"
#include "poco-full.inc"
#include "poco-compile-options.inc"
#include "poco-match-options.inc"
}

using String=std::wstring;
using tchar_t=wchar_t;
std::vector<std::string> searched;
namespace ucr {
enum UNICODESET {NONE,UTF8,UCS2LE,UCS2BE};
std::string toUTF8(const std::string& text){return text;}
std::string toUTF8(const String& text){
int length=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),(int)text.size(),nullptr,0,nullptr,nullptr);
std::string result(length,0);
if(length)WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),(int)text.size(),result.data(),length,nullptr,nullptr);
searched.push_back(result);return result;}
#include "utf8-len.inc"
#include "utf8-char.inc"
#include "make-char.inc"
void maketchar(String& text,unsigned ch,bool& lossy){maketchar(text,ch,lossy,CP_ACP);}
unsigned get_unicode_char(unsigned char* ptr,UNICODESET kind,int page=0){
if(kind==UCS2LE)return ptr[0]+(ptr[1]<<8);if(kind==UCS2BE)return (ptr[0]<<8)+ptr[1];
if(*ptr>=128)throw std::runtime_error("unused non-ASCII lookahead");return *ptr;}
bool maketstring(String& text,const char* bytes,size_t size,int page,bool* lossy){
if(lossy)*lossy=false;if(!size){text.clear();return true;}
int length=MultiByteToWideChar(page,MB_ERR_INVALID_CHARS,bytes,(int)size,nullptr,0);
if(!length)throw std::runtime_error("invalid adapter encoding");text.resize(length);
MultiByteToWideChar(page,MB_ERR_INVALID_CHARS,bytes,(int)size,text.data(),length);return true;}
}
struct Stats {int ncrlfs=0,ncrs=0,nlfs=0,nlosses=0;};
void RecordZero(Stats&,size_t){throw std::runtime_error("NUL outside adapter slice");}
void Append(String& target,const wchar_t* chars,size_t length){target.append(chars,length);}
class UniMemFile {
std::vector<unsigned char> bytes;
public:
unsigned char* m_current=nullptr;unsigned char* m_base=nullptr;
size_t m_filesize=0;int m_unicoding=ucr::UTF8,m_charsize=1,m_codepage=CP_UTF8,m_lineno=0;Stats m_txtstats;
bool OpenReadOnly(const std::string& path){
auto colon=path.find(':');auto encoding=path.substr(0,colon);auto hex=path.substr(colon+1);
for(size_t i=0;i<hex.size();i+=2)bytes.push_back((unsigned char)std::stoul(hex.substr(i,2),nullptr,16));
m_filesize=bytes.size();bytes.resize(bytes.size()+8);m_base=bytes.data();m_current=m_base;
if(encoding=="utf16le"||encoding=="utf16be"){
m_unicoding=encoding=="utf16le"?ucr::UCS2LE:ucr::UCS2BE;m_charsize=2;if(m_filesize>=2)m_current+=2;
}else if(encoding=="cp1252"){m_unicoding=ucr::NONE;m_codepage=1252;}
else if(encoding=="utf8bom"&&m_filesize>=3)m_current+=3;
else if(encoding!="utf8"&&encoding!="utf8bom")throw std::runtime_error("unsupported adapter encoding");
return true;}
void Close(){}
bool ReadString(String&,String&,bool*);
};
// Encoding detection / mmap ownership are injected boundaries; line reading is the actual original.
void GuessEncoding(UniMemFile&,const std::string&){}
#include "read-line.inc"
struct FileContentRef {
std::string path;
bool operator==(const FileContentRef&)const {throw Poco::Exception("unused content equality");}
bool Contains(const std::string&,bool)const {throw Poco::Exception("unused content contains");}
bool REContains(const Poco::RegularExpression&)const;
};
#include "content-search.inc"
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 {ValueType value;};
struct FilterExpression {bool caseSensitive;};
struct FilterEvalContext {};
#include "ExprNode.inc"
#include "BoolLiteral.inc"
#include "NoneLiteral.inc"
#include "DoubleLiteral.inc"
#include "IntLiteral.inc"
#include "StringLiteral.inc"
#include "DateTimeLiteral.inc"
#include "RegularExpressionLiteral.inc"
#include "ArrayLiteral.inc"
#include "BinaryOpNode.inc"
#include "FilterParser.h"
struct DivisionByZeroError:std::runtime_error {using std::runtime_error::runtime_error;};
#include "string.inc"
#include "flags.inc"
#include "create.inc"
#include "regex-string.inc"
#include "regex-prepared.inc"
#include "regex-literal-ctor.inc"
#include "compute.inc"
// matchesはTryFoldConstantsの対象外。別operatorでadapterのstubを通過させない。
ExprNode* TryFoldConstants(ExprNode*,int op,ExprNode*,bool) {
if(op!=TK_RECONTAINS)throw std::runtime_error("unexpected folding operator");return nullptr;
}
#include "binary-optimize.inc"
#include "binary-evaluate.inc"
#include "as-bool.inc"
struct NotNode:ExprNode {
ExprNode* right;explicit NotNode(ExprNode* n):right(n){}~NotNode(){delete right;}
ValueType Evaluate(const FilterEvalContext&)const override;
};
#include "not-evaluate.inc"
struct ContentNode:ExprNode {
std::shared_ptr<FileContentRef> value;explicit ContentNode(std::string text):value(std::make_shared<FileContentRef>()){value->path=text;}
ValueType Evaluate(const FilterEvalContext&)const override{return value;}
};
ExprNode* content(ExprNode* node){FilterEvalContext c;auto value=node->Evaluate(c);delete node;return new ContentNode(std::get<std::string>(value));}
struct DynamicNode:ExprNode {
ExprNode* child;explicit DynamicNode(ExprNode* n):child(n){}~DynamicNode(){delete child;}
ValueType Evaluate(const FilterEvalContext& c)const override{return child->Evaluate(c);}
};
std::vector<int> evaluated;
struct TraceNode:ExprNode {
ExprNode* child;int index;TraceNode(ExprNode* n,int i):child(n),index(i){}~TraceNode(){delete child;}
ExprNode* Optimize()override {child=child->Optimize();return this;}
ValueType Evaluate(const FilterEvalContext& c)const override {evaluated.push_back(index);return child->Evaluate(c);}
};
struct ThrowNode:ExprNode {
std::string message;explicit ThrowNode(std::string s):message(s){}
ValueType Evaluate(const FilterEvalContext&)const override {throw std::runtime_error(message);}
};
std::string bytes(std::initializer_list<int> xs){std::string s;for(int x:xs)s+=char(x);return s;}
ExprNode* array(std::initializer_list<ExprNode*> xs) {
auto values=std::make_shared<std::vector<ValueType2>>();FilterEvalContext c;
for(auto n:xs){values->push_back({n->Evaluate(c)});delete n;}return new ArrayLiteral(values);
}
std::string hex(const std::string& s){std::string out;const char* digits="0123456789abcdef";for(unsigned char c:s){out+=digits[c>>4];out+=digits[c&15];}return out;}
std::string typed(const ValueType& v) {
if(std::holds_alternative<std::monostate>(v))return "{\"kind\":\"none\"}";
if(auto p=std::get_if<bool>(&v))return std::string("{\"kind\":\"bool\",\"value\":")+(*p?"true":"false")+"}";
if(auto p=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){
std::string out="{\"kind\":\"array\",\"items\":[";bool first=true;
for(auto& item:**p){if(!first)out+=",";first=false;out+=typed(item.value);}return out+"]}";}
throw std::runtime_error("unexpected result type");
}
void run(const char* id,bool optimize,bool sensitive,bool negate,ExprNode* left,ExprNode* right) {
FilterExpression expression{sensitive};FilterEvalContext context;evaluated.clear();searched.clear();
// 非最適化時の評価順のみを観測。最適化は右literalを隠さず原典処理へ渡す。
if(!optimize){left=new TraceNode(left,0);right=new TraceNode(right,1);}
ExprNode* binary=new BinaryOpNode(&expression,left,TK_RECONTAINS,right);
std::string result="null",error,phase=optimize?"optimize":"evaluate",serialized;bool prepared=false;
try {
if(optimize){binary=binary->Optimize();prepared=dynamic_cast<RegularExpressionLiteral*>(static_cast<BinaryOpNode*>(binary)->right)!=nullptr;}
phase="evaluate";ValueType value;
if(negate){NotNode node(binary);binary=nullptr;value=node.Evaluate(context);}else value=binary->Evaluate(context);
result=typed(value);serialized=hex(ToStringValue(value));phase.clear();
}catch(const Poco::RegularExpressionException&){error="RegularExpressionException";}
catch(const std::exception&){error="std::exception";}
delete binary;
std::cout<<"{\"id\":\""<<id<<"\",\"value\":"<<result<<",\"error\":\""<<error<<"\",\"phase\":\""<<phase<<"\",\"prepared\":"<<(prepared?"true":"false")<<",\"serializedUtf8Hex\":\""<<serialized<<"\",\"evaluated\":[";
for(size_t i=0;i<evaluated.size();++i){if(i)std::cout<<",";std::cout<<evaluated[i];}std::cout<<"],\"searchedUtf8Hex\":[";for(size_t i=0;i<searched.size();i++){if(i)std::cout<<",";std::cout<<"\""<<hex(searched[i])<<"\"";}std::cout<<"]}\n";
}
int main(){
#include "cases.inc"
}
