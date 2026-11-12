// 検証専用adapter。採取対象の関数本体は原典から無改変でincludeする。
#define NOMINMAX
#define UNICODE
#define _UNICODE
#define Foundation_API
#include <cassert>
#define poco_assert assert
#define poco_check_ptr assert
#include <windows.h>
#include <optional>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <iostream>
#include <iomanip>
#include <limits>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
namespace Poco {
class TextEncoding {public:enum {MAX_SEQUENCE_LENGTH=4};using CharacterMap=int[256];virtual int queryConvert(const unsigned char*,int)const=0;virtual int sequenceLength(const unsigned char*,int)const=0;};
class UTF8Encoding:public TextEncoding {public:UTF8Encoding();~UTF8Encoding();int queryConvert(const unsigned char*,int)const override;int sequenceLength(const unsigned char*,int)const override;static bool isLegal(const unsigned char*,int);private:static const TextEncoding::CharacterMap _charMap;};
#include "text-class.inc"
#include "text-inlines.inc"
#include "text-bodies.inc"
#include "utf-table.inc"
#include "utf-ctor.inc"
#include "utf-dtor.inc"
#include "utf-query.inc"
#include "utf-sequence.inc"
#include "utf-legal.inc"

using UTF16String=std::wstring;using UTF16Char=wchar_t;
struct UnicodeConverter {static void convert(const std::string&,UTF16String&);
#include "converter-template.inc"
};
#include "converter.inc"

struct Timestamp{}; struct RegularExpression{};
struct Exception:std::runtime_error {using std::runtime_error::runtime_error;};
struct LocalDateTime {explicit LocalDateTime(Timestamp){throw std::runtime_error("dead Poco binding");}};
struct DateTimeFormatter {static std::string format(LocalDateTime,const char*){throw std::runtime_error("dead Poco binding");}};
}
using String=std::wstring;
struct FileContentRef {String path;};
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2{ValueType value;};
struct FilterEvalContext{};
struct ExprNode{virtual ~ExprNode()=default; virtual ValueType Evaluate(const FilterEvalContext&)const=0;};
namespace ucr {
using Poco::UnicodeConverter;
#include "ucs4.inc"
#include "to-tstring.inc"
#include "to-utf8-buffer.inc"
#include "to-utf8.inc"
#include "windows.inc"
}
#include "stringification.inc"
#include "get-string.inc"
#include "lifting.inc"
#include "native.inc"
using InvalidArgumentCountError=std::runtime_error;
struct FunctionInfo {const char* name;ValueType(*func)(const FilterEvalContext&,std::vector<ExprNode*>*);int minArgs,maxArgs;};
FunctionInfo table[]={
#include "registration.inc"
};
ValueType invoke(int function,std::vector<ExprNode*>* args){const auto* info=&table[function];std::string functionName=info->name;
#include "arity.inc"
return info->func({},args);}
std::vector<int> trace;
struct Literal:ExprNode {ValueType v;int i;bool fail;Literal(ValueType v,int i,bool f):v(v),i(i),fail(f){}ValueType Evaluate(const FilterEvalContext&)const override{trace.push_back(i);if(fail)throw std::runtime_error("injected-evaluation-error");return v;}};
std::string bytes(std::initializer_list<unsigned char> b){return std::string(b.begin(),b.end());}
ValueType arr(std::initializer_list<ValueType> vs){auto r=std::make_shared<std::vector<ValueType2>>();for(auto& v:vs)r->push_back({v});return r;}
std::string hex(const std::string& s){static const char* h="0123456789abcdef";std::string r;for(unsigned char b:s){r+=h[b>>4];r+=h[b&15];}return r;}
void emit(const ValueType& v){if(std::holds_alternative<std::monostate>(v))std::cout<<"{\"type\":\"none\"}";else if(auto s=std::get_if<std::string>(&v))std::cout<<"{\"type\":\"string\",\"hex\":\""<<hex(*s)<<"\"}";else if(auto a=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::cout<<"{\"type\":\"array\",\"items\":[";bool first=true;for(auto& x:**a){if(!first)std::cout<<",";first=false;emit(x.value);}std::cout<<"]}";}else throw std::runtime_error("unexpected-result");}
void run(const char* id,int f,bool nullArgs,std::initializer_list<Literal> inputs){trace.clear();std::vector<Literal> nodes(inputs);std::vector<ExprNode*> args;for(auto& n:nodes)args.push_back(&n);std::cout<<"{\"id\":\""<<id<<"\"";try{auto v=invoke(f,nullArgs?nullptr:&args);std::cout<<",\"status\":\"ok\",\"result\":";emit(v);std::cout<<",\"stringHex\":\""<<hex(ToStringValue(v))<<"\"";}catch(const std::exception& e){std::cout<<",\"status\":\"error\",\"error\":\""<<e.what()<<"\"";}std::cout<<",\"trace\":[";for(size_t i=0;i<trace.size();i++){if(i)std::cout<<",";std::cout<<trace[i];}std::cout<<"]}\n";}
int main(){
#include "cases.inc"
}
