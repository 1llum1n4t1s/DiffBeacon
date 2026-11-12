// 検証専用adapter。採取対象の関数本体は原典から無改変でincludeする。
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
struct Timestamp{}; struct RegularExpression{};
struct Exception:std::runtime_error {using std::runtime_error::runtime_error;};
struct LocalDateTime {explicit LocalDateTime(Timestamp){throw std::runtime_error("dead Poco binding");}};
struct DateTimeFormatter {static std::string format(LocalDateTime,const char*){throw std::runtime_error("dead Poco binding");}};
}
struct FileContentRef {std::string path;};
namespace ucr {std::string toUTF8(const std::string& s){throw std::runtime_error("dead FileContent binding");}}
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2{ValueType value;};
struct FilterEvalContext{};
struct ExprNode{virtual ~ExprNode()=default; virtual ValueType Evaluate(const FilterEvalContext&)const=0;};
#include "stringification.inc"
#include "lifting.inc"
#include "numeric.inc"
std::vector<int> trace;
struct Literal:ExprNode {
 ValueType value; int index; bool fail;
 Literal(ValueType v,int i,bool f=false):value(v),index(i),fail(f){}
 ValueType Evaluate(const FilterEvalContext&)const override {trace.push_back(index);if(fail)throw std::runtime_error("injected-evaluation-error");return value;}
};
std::string bytes(std::initializer_list<unsigned char> b){return std::string(b.begin(),b.end());}
ValueType arr(std::initializer_list<ValueType> xs){auto r=std::make_shared<std::vector<ValueType2>>();for(auto& x:xs)r->push_back({x});return r;}
std::string hex(const std::string& s){static const char*d="0123456789abcdef";std::string r;for(unsigned char c:s){r+=d[c>>4];r+=d[c&15];}return r;}
void emit(const ValueType& v){
 if(std::holds_alternative<std::monostate>(v)){std::cout<<"{\"type\":\"none\"}";return;}
 if(auto x=std::get_if<int64_t>(&v)){std::cout<<"{\"type\":\"int\",\"decimal\":\""<<*x<<"\"}";return;}
 if(auto x=std::get_if<double>(&v)){uint64_t bits;std::memcpy(&bits,x,8);std::cout<<"{\"type\":\"double\",\"bits\":\""<<std::hex<<std::setw(16)<<std::setfill('0')<<bits<<std::dec<<"\"}";return;}
 if(auto x=std::get_if<bool>(&v)){std::cout<<"{\"type\":\"bool\",\"value\":"<<(*x?"true":"false")<<"}";return;}
 if(auto x=std::get_if<std::string>(&v)){std::cout<<"{\"type\":\"string\",\"utf8Hex\":\""<<hex(*x)<<"\"}";return;}
 if(auto x=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::cout<<"{\"type\":\"array\",\"items\":[";bool first=true;for(auto& a:**x){if(!first)std::cout<<",";first=false;emit(a.value);}std::cout<<"]}";return;}
 throw std::runtime_error("unsupported serialization");
}
using Function=ValueType(*)(const FilterEvalContext&,std::vector<ExprNode*>*);
void run(const char* id,Function fn,std::vector<Literal> values){
 trace.clear();std::vector<ExprNode*> args;for(auto& v:values)args.push_back(&v);
 std::cout<<"{\"id\":\""<<id<<"\",\"input\":[";for(size_t i=0;i<values.size();++i){if(i)std::cout<<",";emit(values[i].value);}std::cout<<"],\"inputStringHex\":[";for(size_t i=0;i<values.size();++i){if(i)std::cout<<",";std::cout<<"\""<<hex(ToStringValue(values[i].value))<<"\"";}std::cout<<"]";
 try{auto result=fn({},&args);std::cout<<",\"status\":\"ok\",\"result\":";emit(result);std::cout<<",\"stringHex\":\""<<hex(ToStringValue(result))<<"\"";}
 catch(const std::exception& e){std::cout<<",\"status\":\"error\",\"error\":\""<<e.what()<<"\"";}
 std::cout<<",\"trace\":[";for(size_t i=0;i<trace.size();++i){if(i)std::cout<<",";std::cout<<trace[i];}std::cout<<"]}\n";
}
int main(){
#include "cases.inc"
return 0;
}
