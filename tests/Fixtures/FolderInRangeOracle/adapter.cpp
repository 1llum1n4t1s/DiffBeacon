// 外部検証専用。inc は固定原典bytesの無改変抽出。parserと全optimizerは対象外。
#include <algorithm>
#include <bit>
#include <iomanip>
#include <cstdint>
#include <ctime>
#include <iostream>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
#define Foundation_API
namespace Poco {
using Int64=int64_t;using UInt32=uint32_t;
class Timespan;
#include "timestamp-class.inc"
#include "timestamp-opeq.inc"
#include "timestamp-opne.inc"
#include "timestamp-opgt.inc"
#include "timestamp-opge.inc"
#include "timestamp-oplt.inc"
#include "timestamp-ople.inc"
#include "timestamp-value.inc"
#include "timestamp-copy.inc"
#include "timestamp-dtor.inc"
#include "timestamp-assign.inc"
#include "ascii-class.inc"
#include "ascii-properties.inc"
#include "ascii-isascii.inc"
#include "ascii-hasproperties.inc"
#include "ascii-isupper.inc"
#include "ascii-lower.inc"
#include "ascii-table.inc"
#include "icompare.inc"
struct RegularExpression{};
}
struct FileContentRef {};
struct ValueType2;
using ValueType = std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 {ValueType value;};
struct FilterExpression {bool caseSensitive;};
struct FilterEvalContext {const FilterExpression* expr;};
struct ExprNode {virtual ~ExprNode()=default;virtual ValueType Evaluate(const FilterEvalContext&)const=0;};
#include "native.inc"
using InvalidArgumentCountError=std::runtime_error;
struct FunctionInfo {const char* name;ValueType(*func)(const FilterEvalContext&,std::vector<ExprNode*>*);int minArgs,maxArgs;};
static FunctionInfo table[]={
#include "registration.inc"
};
static ValueType invoke(const FilterEvalContext& c,std::vector<ExprNode*>* args){
 const FunctionInfo* info=&table[0];std::string functionName=info->name;
#include "arity.inc"
 return info->func(c,args);
}
static std::vector<int> trace;
struct Literal:ExprNode {ValueType value;int tag;bool fail;Literal(ValueType v,int t,bool f):value(v),tag(t),fail(f){} ValueType Evaluate(const FilterEvalContext&)const override{trace.push_back(tag);if(fail)throw std::runtime_error("probe-"+std::to_string(tag));return value;}};
static ValueType array(std::initializer_list<ValueType> values){auto a=std::make_shared<std::vector<ValueType2>>();for(auto& v:values)a->push_back({v});return a;}
static double bits(uint64_t n){return std::bit_cast<double>(n);}
static void emit(const ValueType& v){
 if(std::holds_alternative<std::monostate>(v))std::cout<<"{\"type\":\"none\"}";
 else if(auto b=std::get_if<bool>(&v))std::cout<<"{\"type\":\"bool\",\"value\":"<<(*b?"true":"false")<<"}";
 else if(auto n=std::get_if<int64_t>(&v))std::cout<<"{\"type\":\"int64\",\"value\":"<<*n<<"}";
 else if(auto d=std::get_if<double>(&v))std::cout<<"{\"type\":\"double\",\"value\":\""<<std::hex<<std::setw(16)<<std::setfill('0')<<std::bit_cast<uint64_t>(*d)<<std::dec<<"\"}";
 else if(auto a=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::cout<<"{\"type\":\"array\",\"items\":[";bool first=true;for(auto& x:**a){if(!first)std::cout<<",";first=false;emit(x.value);}std::cout<<"]}";}
 else throw std::runtime_error("unexpected result type");
}
static void run(const char* id,bool caseSensitive,bool nullArgs,std::initializer_list<Literal> input){trace.clear();std::vector<Literal> nodes(input);std::vector<ExprNode*> args;for(auto& n:nodes)args.push_back(&n);FilterExpression expression{caseSensitive};FilterEvalContext c{&expression};ValueType result;std::string error;try{result=invoke(c,nullArgs?nullptr:&args);}catch(const std::exception& e){error=e.what();}std::cout<<"{\"id\":\""<<id<<"\",\"status\":\""<<(error.empty()?"ok":"error")<<"\",\"error\":\""<<error<<"\",\"result\":";emit(result);std::cout<<",\"trace\":[";for(size_t i=0;i<trace.size();++i){if(i)std::cout<<",";std::cout<<trace[i];}std::cout<<"]}\n";}
static std::string bytes(std::initializer_list<unsigned char> b){return std::string(b.begin(),b.end());}
int main(){
#ifdef _M_X64
 constexpr bool x64=true;
#else
 constexpr bool x64=false;
#endif
 std::cerr<<"{\"pointerBytes\":"<<sizeof(void*)<<",\"charSigned\":"<<(char(-1)<0?"true":"false")<<",\"msvcVersion\":"<<_MSC_VER<<",\"x64\":"<<(x64?"true":"false")<<"}\n";
#include "cases.inc"
}
