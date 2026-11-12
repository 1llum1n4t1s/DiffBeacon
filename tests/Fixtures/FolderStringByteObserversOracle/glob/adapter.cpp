// 固定native bodyへの型接続のみ。各caseは別processで隔離する。
#include <algorithm>
#include <cassert>
#include <cstdint>
#include <iostream>
#include <set>
#include <string>
#include <stdexcept>
#define Foundation_API
#define poco_assert assert
#define poco_check_ptr assert
#define PCRE2_CODE_UNIT_WIDTH 8
extern "C" {
#include <pcre2_config.h>
#include <pcre2_internal.h>
}
namespace Poco {
class Path;
struct SyntaxException:std::runtime_error {using std::runtime_error::runtime_error;};
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
#include "unicode-class.inc"
#include "unicode-properties.inc"
#include "unicode-isupper.inc"
#include "unicode-lower.inc"
#include "glob-class.inc"
#include "glob-ctor.inc"
#include "glob-dtor.inc"
#include "glob-match.inc"
#include "glob-inner.inc"
#include "glob-star.inc"
#include "glob-set.inc"
}
static std::string bytes(std::initializer_list<unsigned char> v){return std::string(v.begin(),v.end());}
static void run(const char* id,bool sensitive,const std::string& subject,const std::string& pattern){
 bool result=false;std::string error;try{Poco::Glob glob(pattern,sensitive?0:Poco::Glob::GLOB_CASELESS);result=glob.match(subject);}catch(const std::exception& e){error=e.what();}
 std::cout<<"{\"id\":\""<<id<<"\",\"status\":\""<<(error.empty()?"ok":"error")<<"\",\"error\":\""<<error<<"\",\"result\":"<<(result?"true":"false")<<"}\n";
}
int main(int argc,char** argv){if(argc!=2)return 90;int index=std::stoi(argv[1]);
#include "glob-cases.inc"
return 91;}
