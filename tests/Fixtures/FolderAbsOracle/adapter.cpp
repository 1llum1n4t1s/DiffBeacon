// 検証専用。関数とarity検査は固定原典から無改変抽出する。
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
struct ValueType2;
namespace Poco {struct Timestamp {};struct RegularExpression {};}
struct FileContentRef {};
using ValueType = std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 {ValueType value;};
struct FilterEvalContext {};
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
 else if(auto n=std::get_if<int64_t>(&v))std::cout<<"{\"type\":\"int64\",\"value\":"<<*n<<"}";
 else if(auto d=std::get_if<double>(&v))std::cout<<"{\"type\":\"double\",\"value\":\""<<std::hex<<std::setw(16)<<std::setfill('0')<<std::bit_cast<uint64_t>(*d)<<std::dec<<"\"}";
 else if(auto a=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::cout<<"{\"type\":\"array\",\"items\":[";bool first=true;for(auto& x:**a){if(!first)std::cout<<",";first=false;emit(x.value);}std::cout<<"]}";}
 else throw std::runtime_error("unexpected result type");
}
static void run(const char* id,bool nullArgs,std::initializer_list<Literal> input){trace.clear();std::vector<Literal> nodes(input);std::vector<ExprNode*> args;for(auto& n:nodes)args.push_back(&n);FilterEvalContext c;ValueType result;std::string error;try{result=invoke(c,nullArgs?nullptr:&args);}catch(const std::exception& e){error=e.what();}std::cout<<"{\"id\":\""<<id<<"\",\"status\":\""<<(error.empty()?"ok":"error")<<"\",\"error\":\""<<error<<"\",\"result\":";emit(result);std::cout<<",\"trace\":[";for(size_t i=0;i<trace.size();++i){if(i)std::cout<<",";std::cout<<trace[i];}std::cout<<"]}\n";}
int main(){
#include "cases.inc"
}
