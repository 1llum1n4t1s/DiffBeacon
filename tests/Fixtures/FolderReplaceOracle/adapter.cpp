// 検証専用。関数本体は固定Git blobから無改変で切り出す。
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
struct RegularExpressionException:Exception {using Exception::Exception;};
class RegularExpression {
public:
#include "options.inc"
RegularExpression(const std::string&,int=0,bool=true);
~RegularExpression();
int subst(std::string&,const std::string&,int=0)const;
int subst(std::string&,std::string::size_type,const std::string&,int)const;
private:
std::string::size_type substOne(std::string&,std::string::size_type,const std::string&,int)const;
static int compileOptions(int),matchOptions(int);
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
#include "poco-subst-forward.inc"
#include "poco-ctor.inc"
#include "poco-dtor.inc"
#include "poco-subst.inc"
#include "poco-one.inc"
#include "poco-compile-options.inc"
#include "poco-match-options.inc"
}
namespace ucr {std::string toUTF8(const std::string& x){return x;}}
struct FileContentRef {std::string path;};
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 {ValueType value;};
struct FilterExpression {bool caseSensitive;};
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
#include "subst-string.inc"
#include "subst-regex.inc"
#include "lifting.inc"
#include "replace.inc"
#include "regex-literal-ctor.inc"

static std::vector<int> trace;
struct RegexSpec {std::string pattern;bool sensitive;bool utf8;bool lazy;};
struct Node:ExprNode {
 mutable ValueType value;std::optional<RegexSpec> regex;int tag;bool fail;
 Node(ValueType v,int t,bool f):value(v),tag(t),fail(f){}
 Node(RegexSpec r,int t,bool f):regex(r),tag(t),fail(f){}
 ValueType compile()const{if(regex->utf8){RegularExpressionLiteral original(regex->pattern,regex->sensitive);return original.value;}return std::make_shared<Poco::RegularExpression>(regex->pattern,regex->sensitive?0:Poco::RegularExpression::RE_CASELESS);}
 void bind(){if(regex&&!regex->lazy)value=compile();}
 ValueType Evaluate(const FilterEvalContext&)const override{trace.push_back(tag);if(fail)throw std::runtime_error("probe-"+std::to_string(tag));return regex&&regex->lazy?compile():value;}
};
static ValueType array(std::initializer_list<ValueType> values){auto out=std::make_shared<std::vector<ValueType2>>();for(auto& value:values)out->push_back({value});return out;}
static std::string bytes(std::initializer_list<unsigned char> b){return std::string(b.begin(),b.end());}
static std::string hex(const std::string& s){static const char* d="0123456789abcdef";std::string out;for(unsigned char b:s){out+=d[b>>4];out+=d[b&15];}return out;}
static void emit(const ValueType& value){
 if(std::holds_alternative<std::monostate>(value))std::cout<<"{\"type\":\"none\"}";
 else if(auto s=std::get_if<std::string>(&value))std::cout<<"{\"type\":\"string\",\"value\":\""<<hex(*s)<<"\"}";
 else if(auto a=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&value)){std::cout<<"{\"type\":\"array\",\"items\":[";bool first=true;for(auto& item:**a){if(!first)std::cout<<",";first=false;emit(item.value);}std::cout<<"]}";}
 else throw std::runtime_error("unexpected result type");
}
static void run(const char* id,bool sensitive,bool nullArgs,std::initializer_list<Node> input){
 trace.clear();std::vector<Node> nodes(input);std::vector<ExprNode*> args;for(auto& n:nodes)args.push_back(&n);FilterExpression e{sensitive};FilterEvalContext c{&e};ValueType result;std::string error,errorType,phase="binding";
 try{for(auto& n:nodes)n.bind();phase="evaluate";result=ReplaceFunc(c,nullArgs?nullptr:&args);phase="";}
 catch(const Poco::RegularExpressionException& ex){error=ex.what();errorType="Poco.RegularExpressionException";}
 catch(const std::out_of_range& ex){error=ex.what();errorType="std.out_of_range";}
 catch(const std::exception& ex){error=ex.what();errorType="std.exception";}
 std::cout<<"{\"id\":\""<<id<<"\",\"status\":\""<<(error.empty()?"ok":"error")<<"\",\"errorType\":\""<<errorType<<"\",\"errorPhase\":\""<<phase<<"\",\"errorHex\":\""<<hex(error)<<"\",\"result\":";emit(result);std::cout<<",\"trace\":[";for(size_t i=0;i<trace.size();++i){if(i)std::cout<<",";std::cout<<trace[i];}std::cout<<"]}\n";
}
int main(int argc,char** argv){if(argc!=2)return 90;int index=std::stoi(argv[1]);
#include "cases.inc"
return 91;}
