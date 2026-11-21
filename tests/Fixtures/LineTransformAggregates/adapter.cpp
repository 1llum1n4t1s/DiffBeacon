// 検証専用接続。inc は固定Git原本bytesの無改変抽出。parserは実行しない。
#define NOMINMAX
#define POCO_OS_FAMILY_WINDOWS
#include <Windows.h>
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstdint>
#include <ctime>
#include <functional>
#include <iomanip>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <sstream>
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
#include "timestamp-epoch.inc"
#include "timestamp-default.inc"
#include "timestamp-value.inc"
#include "timestamp-copy.inc"
#include "timestamp-dtor.inc"
#include "timestamp-assign.inc"
#include "timestamp-update.inc"
#include "ascii-class.inc"
#include "ascii-properties.inc"
#include "ascii-isascii.inc"
#include "ascii-hasproperties.inc"
#include "ascii-isupper.inc"
#include "ascii-lower.inc"
#include "ascii-table.inc"
#include "icompare.inc"
struct Exception:std::runtime_error{using std::runtime_error::runtime_error;};
struct RegularExpressionException:Exception{using Exception::Exception;std::string message()const{return what();}};
// typed Timestamp のキー整形は未表現境界。実行しないので代替formatterをgoldenにしない。
struct LocalDateTime{explicit LocalDateTime(Timestamp){throw std::runtime_error("UNSUPPORTED Timestamp formatting reached");}};
struct DateTimeFormatter{static std::string format(LocalDateTime,const char*){throw std::runtime_error("UNSUPPORTED Timestamp formatting reached");}};
struct RegularExpression{};
}
using String=std::string;
namespace ucr{std::string toUTF8(const String& x){return x;}String toTString(const std::string& x){return x;}}
struct FileContentRef{std::string path;};
struct DIFFITEM{};
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2{ValueType value;};
struct FilterExpression;
struct FilterSharedContext;
struct ExprNode;
#include "provider-interface.inc"
#include "context.inc"
#include "statistics-result.inc"
using Range=std::pair<int,int>;
using InsideKey=std::pair<ExprNode*,ExprNode*>;
#include "range-info.inc"
#include "shared-context.inc"
#include "ExprNode.inc"
#include "filter-error-code.inc"
struct FilterExpression{
 bool caseSensitive=true;
 FilterErrorCode errorCode=FILTER_ERROR_NO_ERROR;int errorPosition=-1;
 std::string errorMessage;
 ExprNode* rootNode=nullptr;
 inline static std::function<void(int,const std::string&)> logger;
 std::string TransformLine(const FilterEvalContext&);
};
#include "BoolLiteral.inc"
#include "NoneLiteral.inc"
#include "DoubleLiteral.inc"
#include "IntLiteral.inc"
#include "StringLiteral.inc"
#include "DateTimeLiteral.inc"
#include "ArrayLiteral.inc"
#include "eval-bool.inc"
#include "statistics-process.inc"
#include "statistics-cache.inc"
#include "average.inc"
#include "sum.inc"
#include "maximum.inc"
#include "minimum.inc"
#include "string.inc"
#include "convert-string.inc"
#include "transform-line.inc"
// このhost型は原Vector providerの保管先だけ。encoding/table parserは検証対象外。
struct FileTextEncoding{};
struct TableProps{char quote='"',delimiter=',';bool istable=false;};
#include "vector-provider.inc"
#include "apply-caller.inc"

std::string bytes(std::initializer_list<unsigned> b){std::string s;for(auto x:b)s+=char(x);return s;}
std::string hex(const std::string& s){static const char* d="0123456789abcdef";std::string o;for(unsigned char c:s){o+=d[c>>4];o+=d[c&15];}return o;}
std::string bits(double x){std::ostringstream s;s<<std::hex<<std::setw(16)<<std::setfill('0')<<std::bit_cast<uint64_t>(x);return s.str();}
std::string valueJson(const ValueType& v){
 if(std::holds_alternative<std::monostate>(v))return "{\"kind\":\"none\"}";
 if(auto p=std::get_if<bool>(&v))return std::string("{\"kind\":\"bool\",\"value\":")+(*p?"true":"false")+"}";
 if(auto p=std::get_if<int64_t>(&v))return "{\"kind\":\"integer\",\"decimal\":\""+std::to_string(*p)+"\"}";
 if(auto p=std::get_if<double>(&v))return "{\"kind\":\"double\",\"bitsHex\":\""+bits(*p)+"\"}";
 if(auto p=std::get_if<std::string>(&v))return "{\"kind\":\"string\",\"utf8Hex\":\""+hex(*p)+"\"}";
 if(auto p=std::get_if<Poco::Timestamp>(&v))return "{\"kind\":\"timestamp\",\"microseconds\":\""+std::to_string(p->epochMicroseconds())+"\"}";
 if(auto p=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::string s="{\"kind\":\"array\",\"items\":[";bool first=true;for(auto& x:**p){if(!first)s+=",";first=false;s+=valueJson(x.value);}return s+"]}";}
 throw std::runtime_error("unexpected output alternative");
}
struct Datum{ValueType value;std::string error;};
Datum integer(int64_t x){return {ValueType(x),{}};}Datum number(double x){return {ValueType(x),{}};}
Datum str(std::string x){return {ValueType(x),{}};}Datum boolDatum(bool x){return {ValueType(x),{}};}
Datum none(){return {ValueType(std::monostate{}),{}};}Datum timestamp(int64_t x){return {ValueType(Poco::Timestamp(x)),{}};}
Datum fail(std::string x){return {ValueType(std::monostate{}),x};}
Datum array(std::initializer_list<Datum> d){auto v=std::make_shared<std::vector<ValueType2>>();for(auto& x:d){if(!x.error.empty())throw std::runtime_error("unsupported array throw datum");v->push_back({x.value});}return {ValueType(v),{}};}
std::vector<std::string> trace;
bool contextValid=true;
bool expectSeed=true;
const DIFFITEM* expectedDi=nullptr;
const FilterExpression* expectedExpr=nullptr;
const ILineDataProvider* expectedProvider=nullptr;
const FilterSharedContext* expectedShared=nullptr;
std::map<std::string,std::pair<const FilterExpression*,const FilterSharedContext*>> callerContexts;
void mark(const std::string& name,const FilterEvalContext& c,const std::string& raw=""){
 bool ok=c.di==expectedDi&&c.provider==expectedProvider&&c.expr&&c.sharedContext;
 if(expectSeed)ok&=c.expr==expectedExpr&&c.sharedContext==expectedShared&&c.sharedContext->matchRanges.size()==1&&c.sharedContext->insideRanges.size()==1;
 else{auto key=name.substr(0,5);auto [it,inserted]=callerContexts.try_emplace(key,c.expr,c.sharedContext);ok&=it->second.first==c.expr&&it->second.second==c.sharedContext;}
 contextValid&=ok;
 trace.push_back("{\"node\":\""+name+"\",\"lineIndex\":"+std::to_string(c.lineIndex)+",\"contextRetained\":"+(ok?"true":"false")+",\"providerLineHex\":\""+hex(raw)+"\"}");
}
struct SequenceNode:ExprNode{
 std::string name;std::vector<Datum> rows;std::unique_ptr<ExprNode> originalLiteral;
 SequenceNode(std::string n,std::vector<Datum> r):name(n),rows(std::move(r)){}
 ValueType Evaluate(const FilterEvalContext& c)const override{mark(name,c);if(originalLiteral)return originalLiteral->Evaluate(c);if(c.lineIndex<0||c.lineIndex>=int(rows.size()))throw std::runtime_error("adapter line-index out of range");auto& d=rows[c.lineIndex];if(!d.error.empty())throw std::runtime_error(d.error);return d.value;}
};
using NativeFunction=ValueType(*)(const FilterEvalContext&,std::vector<ExprNode*>*);
struct AggregateNode:ExprNode{
 NativeFunction fn;std::vector<ExprNode*> args;
 AggregateNode(NativeFunction f,ExprNode* v,ExprNode* cond=nullptr):fn(f),args{v}{if(cond)args.push_back(cond);}
 ValueType Evaluate(const FilterEvalContext& c)const override{return fn(c,const_cast<std::vector<ExprNode*>*>(&args));}
};
struct OuterNode:ExprNode{
 const AggregateNode& inner;bool throwSecond;
 OuterNode(const AggregateNode& n,bool x):inner(n),throwSecond(x){}
 ValueType Evaluate(const FilterEvalContext& c)const override{mark("outer",c);auto v=inner.Evaluate(c);if(throwSecond&&c.lineIndex==1)throw std::runtime_error("outer-error-after-inner-success");return v;}
};
struct ProviderNode:ExprNode{
 std::string name;bool throwSecond=false;
 explicit ProviderNode(std::string n):name(n){}
 ValueType Evaluate(const FilterEvalContext& c)const override{auto s=c.provider->GetLine(0,c.lineIndex);mark(name,c,s);if(throwSecond&&c.lineIndex==1)throw std::runtime_error("caller-rule-error");return double(std::stod(s));}
};
void seed(FilterSharedContext& s,ExprNode* node){s.matchRanges[node]={{0,0,1}};s.insideRanges[{node,node}]={{0,0}};}
struct Environment{
 DIFFITEM di;FilterExpression e;SequenceNode value,condition;FilterSharedContext shared;VectorLineDataProvider provider;FilterEvalContext c;
 Environment(std::vector<Datum> r,std::vector<Datum> cond,bool cs,int outer):value("value",r),condition("condition",cond),provider(std::vector<std::string>(r.size(),"unused"),FileTextEncoding{},TableProps{}){
 e.caseSensitive=cs;seed(shared,&value);c={&e,&di,&provider,&shared,outer};expectedDi=&di;expectedExpr=&e;expectedProvider=&provider;expectedShared=&shared;expectSeed=true;
 }
};
std::string join(const std::vector<std::string>& x){std::string s;for(auto& a:x){if(!s.empty())s+=",";s+=a;}return s;}
std::string statsJson(const FilterSharedContext& shared){
 std::vector<std::string> values;
 // map pointer ordering is unspecified across processes: deterministic insertion ordinal is supplied by snapshots below.
 for(auto& [key,s]:shared.statistics)values.push_back("{\"count\":"+std::to_string(s.count)+",\"valueType\":"+std::to_string(s.valueType)+",\"sumBitsHex\":\""+bits(s.sum)+"\",\"averageBitsHex\":\""+bits(s.average)+"\",\"trueCount\":"+std::to_string(s.trueCount)+",\"falseCount\":"+std::to_string(s.falseCount)+"}");
 std::sort(values.begin(),values.end());return "["+join(values)+"]";
}
std::vector<std::string> steps;
void evaluate(const std::string& label,const AggregateNode& a,Environment& env){
 trace.clear();std::string val="null",err,serialized;bool applied=false,returned=false;int before=int(env.shared.statistics.size());
 bool hit=env.shared.statistics.contains({a.args[0],a.args.size()>1?a.args[1]:nullptr});int outerBefore=env.c.lineIndex;
 try{auto v=a.Evaluate(env.c);val=valueJson(v);returned=true;if(!std::holds_alternative<Poco::Timestamp>(v)){serialized=hex(ToStringValue(v));applied=true;}}
 catch(const std::exception& x){err=x.what();}
 contextValid&=env.c.lineIndex==outerBefore;
 steps.push_back("{\"label\":\""+label+"\",\"value\":"+val+",\"returned\":"+(returned?"true":"false")+",\"errorUtf8Hex\":\""+hex(err)+"\",\"errorPhase\":\""+(err.empty()?"":"evaluate")+"\",\"exceptionType\":\""+(err.empty()?"":"std::runtime_error")+"\",\"serializedUtf8Hex\":"+(applied?"\""+serialized+"\"":"null")+",\"serializationApplied\":"+(applied?"true":"false")+",\"cacheHitAtEntry\":"+(hit?"true":"false")+",\"outerLineIndexBefore\":"+std::to_string(outerBefore)+",\"outerLineIndexAfter\":"+std::to_string(env.c.lineIndex)+",\"cacheBefore\":"+std::to_string(before)+",\"cacheAfter\":"+std::to_string(env.shared.statistics.size())+",\"statistics\":"+statsJson(env.shared)+",\"trace\":["+join(trace)+"]}");
}
void output(const char* id){std::cout<<"{\"id\":\""<<id<<"\",\"contextRetained\":"<<(contextValid?"true":"false")<<",\"steps\":["<<join(steps)<<"]}\n";}
void scan(const char* id,NativeFunction fn,std::vector<Datum> rows,std::vector<Datum> cond,bool hasCondition,bool cs,int outer,bool literal){
 steps.clear();contextValid=true;Environment env(rows,cond,cs,outer);
 if(literal){auto& v=rows[0].value;if(auto p=std::get_if<int64_t>(&v))env.value.originalLiteral=std::make_unique<IntLiteral>(*p);else if(auto p=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v))env.value.originalLiteral=std::make_unique<ArrayLiteral>(*p);else throw std::runtime_error("unsupported original literal leaf");}
 AggregateNode a(fn,&env.value,hasCondition?&env.condition:nullptr);evaluate("first",a,env);evaluate("repeat",a,env);output(id);
}
struct CallerRule:ExprNode{
 ProviderNode& value;NativeFunction fn;std::string name;bool delayed=false;int directBefore=0;
 CallerRule(ProviderNode& v,NativeFunction f,std::string n):value(v),fn(f),name(n){}
 ValueType Evaluate(const FilterEvalContext& c)const override{
  mark(name,c,c.provider->GetLine(0,c.lineIndex));
  if(c.lineIndex<directBefore){return std::to_string(int(std::stod(c.provider->GetLine(0,c.lineIndex))*10));}
  std::vector<ExprNode*> args{&value};return fn(c,&args);
 }
};
void caller(const char* id,const std::string& mode){
 steps.clear();contextValid=true;DIFFITEM di;expectedDi=&di;expectSeed=false;
 VectorLineDataProvider provider({"1\r\n","2\n","3"},FileTextEncoding{},TableProps{});
 expectedProvider=&provider;
 ProviderNode v1("rule0-value"),v2("rule1-value");CallerRule r1(v1,SumFunc,"rule0"),r2(v2,AverageFunc,"rule1");
 r1.directBefore=1;r2.directBefore=1;
 if(mode=="caller-error")v2.throwSecond=true;
 auto invocation=[&](const char* label){
  std::vector<FilterExpression> e(2);e[0].rootNode=&r1;e[1].rootNode=&r2;
  std::vector<FilterEvalContext> ctx{{&e[0],&di,&provider,nullptr,-1},{&e[1],&di,&provider,nullptr,-1}};
  // Original caller creates its own sharedContext. Seed is not part of this native caller path.
  trace.clear();callerContexts.clear();std::string error;
  bool ok=ApplyFilterExpressionsToLines(provider,e,ctx,error);
  bool independent=callerContexts.size()==2&&callerContexts["rule0"].second!=callerContexts["rule1"].second;
  contextValid&=independent;
  std::vector<std::string> lines;for(int i=0;i<provider.GetLineCount();++i)lines.push_back("\""+hex(provider.GetFullLine(0,i))+"\"");
  steps.push_back("{\"label\":\""+std::string(label)+"\",\"callerSucceeded\":"+(ok?"true":"false")+",\"independentRuleCaches\":"+(independent?"true":"false")+",\"errorUtf8Hex\":\""+hex(error)+"\",\"ruleErrors\":["+std::to_string(e[0].errorCode)+","+std::to_string(e[1].errorCode)+"],\"outputFullLineHex\":["+join(lines)+"],\"trace\":["+join(trace)+"]}");
 };
 invocation("epoch1");if(mode=="caller-epoch")invocation("epoch2");output(id);
}
void scenario(const char* id,const std::string& mode){
 if(mode.starts_with("caller")){caller(id,mode);return;}
 steps.clear();contextValid=true;Environment env({integer(1),integer(2),integer(3)},{boolDatum(true),boolDatum(false),boolDatum(true)},true,1);
 AggregateNode a(SumFunc,&env.value);
 if(mode=="cache-mutation"){evaluate("before-mutation",a,env);env.value.rows={integer(10),integer(20),integer(30)};evaluate("after-mutation",a,env);}
 else if(mode=="cross-function-cache"){for(auto pair:{std::pair{"sum",SumFunc},{"average",AverageFunc},{"maximum",MaximumFunc},{"minimum",MinimumFunc}}){AggregateNode b(pair.second,&env.value);evaluate(pair.first,b,env);}}
 else if(mode=="distinct-values"){SequenceNode other("other-value",env.value.rows);AggregateNode b(SumFunc,&other);evaluate("first-node",a,env);evaluate("distinct-node",b,env);}
 else if(mode=="distinct-conditions"||mode=="same-condition-cache"){AggregateNode b(SumFunc,&env.value,&env.condition);evaluate("condition-one",b,env);SequenceNode other("other-condition",env.condition.rows);AggregateNode c(SumFunc,&env.value,mode=="distinct-conditions"?&other:&env.condition);evaluate("condition-two",c,env);}
 else if(mode=="first-call-delayed"){env.value.rows={integer(10),integer(20),integer(30)};evaluate("first-after-mutation",a,env);}
 else if(mode=="retry-value-error"){env.value.rows[1]=fail("value-error");evaluate("failed",a,env);env.value.rows[1]=integer(20);evaluate("retry",a,env);evaluate("cached",a,env);}
 else if(mode=="retry-condition-error"){env.condition.rows[1]=fail("condition-error");AggregateNode b(SumFunc,&env.value,&env.condition);evaluate("failed",b,env);env.condition.rows[1]=boolDatum(true);evaluate("retry",b,env);}
 else if(mode=="nested-error"||mode=="nested-success"){OuterNode outer(a,mode=="nested-error");AggregateNode b(SumFunc,&outer);evaluate("outer-first",b,env);env.value.rows={integer(10),integer(20),integer(30)};outer.throwSecond=false;evaluate("outer-retry",b,env);evaluate("inner-after-retry",a,env);}
 else if(mode=="nested-condition"){SequenceNode bools("inner-bool",{boolDatum(false),boolDatum(true),boolDatum(false)});AggregateNode inner(MaximumFunc,&bools);AggregateNode outer(SumFunc,&env.value,&inner);evaluate("nested-condition",outer,env);evaluate("repeat",outer,env);}
 else if(mode=="empty-cache"){env.value.rows={none(),none(),none()};evaluate("empty",a,env);env.value.rows={integer(10),integer(20),integer(30)};evaluate("after-mutation",a,env);}
 else if(mode=="provider-null"){env.c.provider=nullptr;evaluate("without-provider",a,env);env.c.provider=&env.provider;evaluate("provider-restored",a,env);}
 else if(mode=="case-change-cache"){env.value.rows={str("a"),str("B"),str("c")};AggregateNode b(MinimumFunc,&env.value);evaluate("case-sensitive",b,env);env.e.caseSensitive=false;evaluate("case-insensitive-same-key",b,env);}
 else throw std::runtime_error("unknown scenario "+mode);
 output(id);
}
int main(){
#include "cases.inc"
}
