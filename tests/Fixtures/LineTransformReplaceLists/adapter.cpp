// 検証専用。関数本体は固定Git blobから無改変で切り出す。
#define NOMINMAX
#include <Windows.h>
#include <fstream>
#include <iterator>
#include <algorithm>
#include <cstdint>
#include <functional>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <variant>
#include <vector>
#define PCRE2_CODE_UNIT_WIDTH 8
#include <pcre2.h>
namespace Poco {
// Exception型の接続のみ。regex compile/match/substは原典とPCRE2が実行する。
struct Exception: std::runtime_error {using std::runtime_error::runtime_error;};
struct RegularExpressionException:Exception {using Exception::Exception;std::string message()const{return what();}};
struct AssertionViolationException:Exception {using Exception::Exception;};
class RegularExpression {
public:
#include "options.inc"
RegularExpression(const std::string&,int=0,bool=true);
~RegularExpression();
#include "match-type.inc"
using MatchVec=std::vector<Match>;
using GroupMap=std::map<int,std::string>;
int subst(std::string&,std::string::size_type,const std::string&,int)const;
int subst(std::string&,const std::string&,int)const;
int match(const std::string&,std::string::size_type,Match&,int)const;
int match(const std::string&,std::string::size_type,MatchVec&,int)const;
private:

static int compileOptions(int),matchOptions(int);
std::string::size_type substOne(std::string&,std::string::size_type,const std::string&,int)const;
void* _pcre;std::map<int,std::string> _groups;
};
struct Timestamp {};
struct LocalDateTime {explicit LocalDateTime(Timestamp){throw Exception("unsupported Timestamp");}};
struct DateTimeFormatter {static std::string format(LocalDateTime,const char*){throw Exception("unsupported Timestamp");}};
}
namespace {
#include "match-data.inc"
;
}
namespace Poco {
#define poco_assert(cond) if (!(cond)) throw AssertionViolationException(#cond); else (void)nullptr
#include "poco-ctor.inc"
#include "poco-dtor.inc"
#include "poco-subst-inline.inc"
#include "poco-subst.inc"
#include "poco-subst-one.inc"
#include "poco-match.inc"
#include "poco-match-vector.inc"
#include "poco-compile-options.inc"
#include "poco-match-options.inc"
}
using String=std::string;
namespace ucr {std::string toUTF8(const String& x){return x;}String toTString(const std::string& x){return x;}}
struct FileContentRef {std::string path;};
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 {ValueType value;};
struct FilterExpression {bool caseSensitive;inline static std::function<void(int,const std::string&)> logger;};
struct FilterEvalContext {const FilterExpression* expr;};
#include "ExprNode.inc"
#include "BoolLiteral.inc"
#include "NoneLiteral.inc"
#include "DoubleLiteral.inc"
#include "IntLiteral.inc"
#include "StringLiteral.inc"
#include "DateTimeLiteral.inc"
#include "RegularExpressionLiteral.inc"
#include "ArrayLiteral.inc"
#include "string.inc"
#include "get.inc"
#include "escape.inc"
#include "flags.inc"
#include "create.inc"
#include "lifting.inc"
#include "subst-string.inc"
#include "subst-regex.inc"
#include "regex-literal-ctor.inc"

// OS/encoding adapter boundary: native LoadListImpl and callbacks are unchanged.
// This UTF-8 reader is NOT WinMerge UniMemFile nor its encoding detector.
#define _T(x) x
struct UniMemFile{
 std::string raw;size_t pos=0;bool bom=false;
 bool OpenReadOnly(const String& p){std::ifstream f(p,std::ios::binary);if(!f)return false;raw.assign(std::istreambuf_iterator<char>(f),{});pos=0;return true;}
 void ReadBom(){bom=raw.starts_with("\xef\xbb\xbf");if(bom)pos=3;}
 bool HasBom()const{return bom;}int64_t GetFileSize()const{return raw.size();}const char* GetBase()const{return raw.data();}void SetCodepage(int){}
 struct Error{String GetError()const{return "adapter-open-failure";}};Error GetLastUniError()const{return {};}
 bool ReadString(String& line,String& eol,bool* lossy){line.clear();eol.clear();*lossy=false;if(pos>=raw.size())return false;size_t start=pos;while(pos<raw.size()&&raw[pos]!='\r'&&raw[pos]!='\n')++pos;line=raw.substr(start,pos-start);if(pos<raw.size()){char c=raw[pos++];eol+=c;if(c=='\r'&&pos<raw.size()&&raw[pos]=='\n')eol+=raw[pos++];}return true;}
 void Close(){}
};
struct FileTextEncoding{int m_codepage=65001;};
struct Options{int GetInt(int)const{return 0;}};Options* GetOptionsMgr(){static Options o;return &o;}const int OPT_CP_DETECT=0;
namespace paths{String FindExtension(const String& x){return x.substr(x.find_last_of('.')+1);}}
namespace env{String ExpandEnvironmentVariables(const String& x){return x;}}
namespace codepage_detect{const size_t BufSize=65536;FileTextEncoding Guess(const String&,const char*,size_t,int){return {};}}
namespace ReplaceList{
#include "guess-encoding.inc"
#include "load-impl.inc"
#include "load-list.inc"
#include "load-regex-list.inc"
}
#include "replace-list-func.inc"
#include "regex-list-func.inc"
std::string bytes(std::initializer_list<unsigned> b){std::string s;for(unsigned x:b)s+=char(x);return s;}
std::string hex(const std::string& s){static const char*d="0123456789abcdef";std::string r;for(unsigned char c:s){r+=d[c>>4];r+=d[c&15];}return r;}
std::vector<int> evaluated;
struct TraceNode:ExprNode{ExprNode* child;int index;TraceNode(ExprNode*c,int i):child(c),index(i){}~TraceNode(){delete child;}ValueType Evaluate(const FilterEvalContext& c)const override{evaluated.push_back(index);return child->Evaluate(c);}};
struct ThrowNode:ExprNode{ValueType Evaluate(const FilterEvalContext&)const override{throw std::runtime_error("adapter-predicate-throw");}};
ExprNode* array(std::initializer_list<ExprNode*> nodes){auto v=std::make_shared<std::vector<ValueType2>>();FilterExpression e{true};FilterEvalContext c{&e};for(auto*n:nodes){v->push_back({n->Evaluate(c)});delete n;}return new ArrayLiteral(v);}
std::string valueJson(const ValueType&v){
 if(std::holds_alternative<std::monostate>(v))return "{\"kind\":\"none\"}";
 if(auto p=std::get_if<bool>(&v))return std::string("{\"kind\":\"bool\",\"value\":")+(*p?"true":"false")+"}";
 if(auto p=std::get_if<std::string>(&v))return "{\"kind\":\"string\",\"utf8Hex\":\""+hex(*p)+"\"}";
 if(auto p=std::get_if<int64_t>(&v))return "{\"kind\":\"integer\",\"value\":"+std::to_string(*p)+"}";
 if(std::holds_alternative<std::shared_ptr<Poco::RegularExpression>>(v))return "{\"kind\":\"opaque-native-regex\"}";
 if(auto p=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::string r="{\"kind\":\"array\",\"items\":[";bool first=true;for(auto&x:**p){if(!first)r+=",";first=false;r+=valueJson(x.value);}return r+"]}";}
 throw std::runtime_error("unrepresented-type");
}
std::string join(const std::vector<std::string>&xs){std::string r;for(auto&x:xs){if(!r.empty())r+=",";r+=x;}return r;}
void run(const char*id,bool regex,bool cs,const char*path,bool nullArgs,std::initializer_list<ExprNode*> nodes){
 FilterExpression e{cs};FilterEvalContext c{&e};std::vector<std::string> logs,loadLogs;std::string loaded="null",value="null",key="null",error="";
 e.logger=[&](int level,const std::string& text){logs.push_back("{\"level\":"+std::to_string(level)+",\"utf8Hex\":\""+hex(text)+"\"}");};
 if(*path){auto list=regex?ReplaceList::LoadRegexList(e,path):ReplaceList::LoadList(e,path);loaded=valueJson(ValueType(list));loadLogs=logs;logs.clear();}
 std::vector<ExprNode*> args;int i=0;for(auto*n:nodes)args.push_back(new TraceNode(n,i++));evaluated.clear();
 try{auto result=regex?RegexReplaceWithListFunc(c,nullArgs?nullptr:&args):ReplaceWithListFunc(c,nullArgs?nullptr:&args);value=valueJson(result);key="\""+hex(ToStringValue(result))+"\"";}catch(const std::exception&ex){error=hex(ex.what());}
 std::cout<<"{\"id\":\""<<id<<"\",\"value\":"<<value<<",\"serializedUtf8Hex\":"<<key<<",\"errorUtf8Hex\":\""<<error<<"\",\"loadedList\":"<<loaded<<",\"loaderLogs\":["<<join(loadLogs)<<"],\"functionLogs\":["<<join(logs)<<"],\"evaluated\":[";
 for(size_t j=0;j<evaluated.size();++j){if(j)std::cout<<",";std::cout<<evaluated[j];}std::cout<<"]}\n";for(auto*n:args)delete n;e.logger=nullptr;
}
int main(){
#include "cases.inc"
}
