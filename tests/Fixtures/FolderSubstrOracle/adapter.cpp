// 検証専用。原典関数本体・helperは無改変で抽出する。
#include <algorithm>
#include <cstdint>
#include <functional>
#include <iostream>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
using String=std::string;
struct ValueType2;
namespace ucr {std::string toUTF8(const String& s){return s;}}
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

struct SearchExpression {bool caseSensitive;};
struct FilterEvalContext {const SearchExpression* expr;};
struct ExprNode {virtual ~ExprNode()=default; virtual ValueType Evaluate(const FilterEvalContext&) const=0;};
#include "get-string.inc"
#include "lifting.inc"
#include "substr.inc"
static std::vector<int> trace;
struct Literal : ExprNode {
 ValueType value; int tag; bool fail;
 Literal(ValueType v,int t,bool f):value(v),tag(t),fail(f){}
 ValueType Evaluate(const FilterEvalContext&) const override {trace.push_back(tag);if(fail)throw std::runtime_error("probe-"+std::to_string(tag));return value;}
};
static ValueType array(std::initializer_list<ValueType> values){auto a=std::make_shared<std::vector<ValueType2>>();for(auto& v:values)a->push_back({v});return a;}
static std::string bytes(std::initializer_list<unsigned char> b){return std::string(b.begin(),b.end());}
static void emit(const ValueType& v){
 if(std::holds_alternative<std::monostate>(v))std::cout<<"{\"type\":\"none\"}";
 else if(auto s=std::get_if<std::string>(&v)){static const char* h="0123456789abcdef";std::cout<<"{\"type\":\"string\",\"value\":\"";for(unsigned char b:*s)std::cout<<h[b>>4]<<h[b&15];std::cout<<"\"}";}
 else if(auto a=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::cout<<"{\"type\":\"array\",\"items\":[";bool first=true;for(auto& x:**a){if(!first)std::cout<<",";first=false;emit(x.value);}std::cout<<"]}";}
 else throw std::runtime_error("unexpected result type");
}
static void run(const char* id,std::initializer_list<Literal> input){
 trace.clear();std::vector<Literal> nodes(input);std::vector<ExprNode*> args;for(auto& n:nodes)args.push_back(&n);
 SearchExpression search{true};FilterEvalContext c{&search};ValueType result;std::string error;
 try{result=SubstrFunc(c,&args);}catch(const std::exception& e){error=e.what();}
 std::cout<<"{\"id\":\""<<id<<"\",\"status\":\""<<(error.empty()?"ok":"error")<<"\",\"error\":\""<<error<<"\",\"result\":";emit(result);std::cout<<",\"trace\":[";for(size_t i=0;i<trace.size();++i){if(i)std::cout<<",";std::cout<<trace[i];}std::cout<<"]}\n";
}
int main(){
#include "cases.inc"
}
